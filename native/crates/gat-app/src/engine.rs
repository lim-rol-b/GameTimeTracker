//! Game presence engine: rule fingerprinting, related-executable expansion and
//! process matching. A port of the original Windows ProcessProvider.

use std::collections::{HashMap, HashSet};
use std::path::Path;
use std::sync::mpsc::{self, Receiver, TryRecvError};
use std::time::Duration;

use chrono::{DateTime, Utc};
use gat_core::models::{Game, GameProcessRule};
use gat_core::presence::{GameMatcher, RelatedProcessRules, SteamLibraryReader};
use gat_platform::ProcessScanner;

use crate::logging::Log;

#[derive(Debug)]
pub enum PresenceEvent {
    Started {
        game_id: String,
        pids: HashSet<i32>,
        at: DateTime<Utc>,
    },
    Stopped {
        game_id: String,
        at: DateTime<Utc>,
    },
}

struct ExpansionJob {
    receiver: Receiver<(Vec<GameProcessRule>, Vec<String>)>,
    fingerprint: String,
}

pub struct PresenceEngine {
    matcher: GameMatcher,
    scanner: ProcessScanner,
    running: HashMap<String, HashSet<i32>>,
    known: HashMap<i32, (DateTime<Utc>, String)>,
    rule_fingerprint: String,
    effective_rules: Vec<GameProcessRule>,
    unreadable: HashSet<i32>,
    expansion: Option<ExpansionJob>,
    last_expansion: Option<DateTime<Utc>>,
}

impl Default for PresenceEngine {
    fn default() -> Self {
        Self::new()
    }
}

impl PresenceEngine {
    pub fn new() -> Self {
        Self {
            matcher: GameMatcher,
            scanner: ProcessScanner::new(),
            running: HashMap::new(),
            known: HashMap::new(),
            rule_fingerprint: String::new(),
            effective_rules: Vec::new(),
            unreadable: HashSet::new(),
            expansion: None,
            last_expansion: None,
        }
    }

    pub fn running_pids(&self, game_id: &str) -> Option<&HashSet<i32>> {
        self.running.get(game_id)
    }

    pub fn reset(&mut self) {
        self.running.clear();
        self.known.clear();
    }

    pub fn scan(
        &mut self,
        now: DateTime<Utc>,
        rules: &[GameProcessRule],
        games: &[Game],
        log: &dyn Log,
        allow_expansion: bool,
        expansion_interval: Duration,
    ) -> Vec<PresenceEvent> {
        let fingerprint = fingerprint(rules, games);
        if fingerprint != self.rule_fingerprint {
            self.known.clear();
            self.unreadable.clear();
            self.rule_fingerprint = fingerprint.clone();
            self.last_expansion = None;
            self.expansion = None;
            self.effective_rules = explicit_rules(rules, games);
        }

        if let Some(job) = self.expansion.take() {
            match job.receiver.try_recv() {
                Ok((expanded, warnings)) => {
                    if job.fingerprint == fingerprint {
                        self.effective_rules = expanded;
                    }
                    for warning in warnings {
                        log.write(&format!("Related executables: {}", warning));
                    }
                }
                Err(TryRecvError::Empty) => self.expansion = Some(job),
                Err(TryRecvError::Disconnected) => {}
            }
        }

        if allow_expansion
            && self.expansion.is_none()
            && self
                .last_expansion
                .map(|last| {
                    now - last >= chrono::Duration::from_std(expansion_interval).unwrap_or_default()
                })
                .unwrap_or(true)
        {
            self.last_expansion = Some(now);
            let configured = rules.to_vec();
            let current_games = games.to_vec();
            let (sender, receiver) = mpsc::channel();
            std::thread::spawn(move || {
                let _ = sender.send(expand_rules(&configured, &current_games));
            });
            self.expansion = Some(ExpansionJob {
                receiver,
                fingerprint,
            });
        }

        let effective = self.effective_rules.clone();
        let names: HashSet<String> = effective
            .iter()
            .map(|rule| executable_stem(&rule.executable_name).to_lowercase())
            .collect();

        let mut next: HashMap<String, HashSet<i32>> = HashMap::new();
        let mut seen: HashSet<i32> = HashSet::new();
        for snapshot in self.scanner.scan() {
            let stem = executable_stem(&snapshot.executable_name).to_lowercase();
            if !names.contains(&stem) {
                continue;
            }
            let pid = snapshot.pid;
            seen.insert(pid);
            let candidates: Vec<GameProcessRule> = effective
                .iter()
                .filter(|rule| executable_stem(&rule.executable_name).eq_ignore_ascii_case(&stem))
                .cloned()
                .collect();
            let mut game = self.matcher.match_process(&snapshot, &candidates);

            // A previously verified process may temporarily become inaccessible. Never reuse a recycled PID.
            if game.is_none() && snapshot.full_path.is_none() {
                if let (Some(started), Some((known_start, known_game))) =
                    (snapshot.started_at, self.known.get(&pid))
                {
                    if *known_start == started
                        && candidates.iter().any(|rule| &rule.game_id == known_game)
                    {
                        game = Some(known_game.clone());
                    }
                }
            }

            let Some(game) = game else {
                if snapshot.full_path.is_none() && self.unreadable.insert(pid) {
                    log.write(&format!(
                        "Unable to verify process path: {} ({}); bind the real game process or check process permissions.",
                        snapshot.executable_name, pid
                    ));
                }
                continue;
            };

            if let Some(started) = snapshot.started_at {
                self.known.insert(pid, (started, game.clone()));
            }
            next.entry(game).or_default().insert(pid);
        }

        self.unreadable.retain(|pid| seen.contains(pid));
        self.known.retain(|pid, _| seen.contains(pid));

        let previous = std::mem::take(&mut self.running);
        let mut events = Vec::new();
        for game in previous.keys() {
            if !next.contains_key(game) {
                events.push(PresenceEvent::Stopped {
                    game_id: game.clone(),
                    at: now,
                });
            }
        }
        for game in next.keys() {
            if !previous.contains_key(game) {
                let pids = next.get(game).cloned().unwrap_or_default();
                log.write(&format!("Game detected: {}", game));
                events.push(PresenceEvent::Started {
                    game_id: game.clone(),
                    pids,
                    at: now,
                });
            }
        }
        for game in previous.keys() {
            if !next.contains_key(game) {
                log.write(&format!("Game stopped: {}", game));
            }
        }
        self.running = next;
        events
    }
}

fn fingerprint(rules: &[GameProcessRule], games: &[Game]) -> String {
    serde_json::to_string(&(rules, games)).unwrap_or_default()
}

fn explicit_rules(rules: &[GameProcessRule], games: &[Game]) -> Vec<GameProcessRule> {
    rules
        .iter()
        .filter(|rule| rule.enabled)
        .filter(|rule| {
            !RelatedProcessRules::is_simple(rule)
                || !RelatedProcessRules::is_launcher(&rule.executable_name)
                || games
                    .iter()
                    .find(|game| game.id == rule.game_id)
                    .map(|game| !game.detect_related_executables)
                    .unwrap_or(true)
        })
        .cloned()
        .collect()
}

fn expand_rules(
    configured: &[GameProcessRule],
    games: &[Game],
) -> (Vec<GameProcessRule>, Vec<String>) {
    let mut result = Vec::new();
    let mut warnings = Vec::new();
    let mut by_game: HashMap<String, Vec<GameProcessRule>> = HashMap::new();
    for rule in configured {
        by_game
            .entry(rule.game_id.clone())
            .or_default()
            .push(rule.clone());
    }

    for (game_id, own) in by_game {
        let game = games.iter().find(|game| game.id == game_id);
        let mut minecraft = game.and_then(|game| game.minecraft_directory.clone());
        if let Some(game) = game {
            if game.detect_related_executables && blank(&minecraft) {
                let launcher = own.iter().find(|rule| {
                    RelatedProcessRules::is_simple(rule)
                        && RelatedProcessRules::is_launcher(&rule.executable_name)
                });
                if let Some(path) = launcher.and_then(|rule| rule.executable_path.as_deref()) {
                    if let Some(parent) = Path::new(path).parent() {
                        if parent.join(".minecraft").is_dir() {
                            minecraft =
                                Some(parent.join(".minecraft").to_string_lossy().into_owned());
                        }
                    }
                }
            }
        }

        if let Some(game) = game {
            if game.detect_related_executables {
                if let Some(root) = minecraft.as_deref().filter(|root| !root.trim().is_empty()) {
                    result.extend(
                        own.iter()
                            .filter(|rule| {
                                !RelatedProcessRules::is_simple(rule)
                                    || !RelatedProcessRules::is_launcher(&rule.executable_name)
                            })
                            .cloned(),
                    );
                    for name in ["javaw.exe", "java.exe"] {
                        result.push(GameProcessRule {
                            id: format!("minecraft:{}:{}", game.id, name),
                            game_id: game.id.clone(),
                            executable_name: name.to_string(),
                            minecraft_root_directory: Some(root.to_string()),
                            priority: i32::MIN,
                            ..Default::default()
                        });
                    }
                    continue;
                }
            }
        }

        let game_ref = game;
        if game_ref.is_none()
            || !game_ref
                .map(|game| game.detect_related_executables)
                .unwrap_or(false)
            || !own.iter().any(RelatedProcessRules::is_simple)
        {
            result.extend(own);
            continue;
        }

        let game = game_ref.expect("checked above");
        let primary = own
            .iter()
            .find(|rule| RelatedProcessRules::is_simple(rule))
            .and_then(|rule| rule.executable_path.clone());
        let mut root = game.install_directory.clone().or_else(|| {
            primary
                .as_deref()
                .and_then(|path| Path::new(path).parent())
                .map(|parent| parent.to_string_lossy().into_owned())
        });

        if root.is_none()
            && primary
                .as_deref()
                .map(RelatedProcessRules::is_shared_host)
                .unwrap_or(false)
        {
            result.extend(
                own.iter()
                    .filter(|rule| {
                        !RelatedProcessRules::is_simple(rule)
                            || !RelatedProcessRules::is_launcher(&rule.executable_name)
                    })
                    .cloned(),
            );
            warnings.push(format!(
                "{}: shared launcher requires a game EXE or Minecraft directory; launcher runtime is excluded.",
                game.name
            ));
            continue;
        }

        let Some(root) = root.take() else {
            result.extend(own);
            continue;
        };
        if !Path::new(&root).is_dir() {
            result.extend(own);
            continue;
        }
        let mut warning = None;
        let paths = SteamLibraryReader.find_executables(Path::new(&root), &mut warning);
        result.extend(RelatedProcessRules::expand(game, &own, &root, &paths));
        if let Some(warning) = warning {
            warnings.push(format!("{}: {}", game.name, warning));
        }
    }

    (result, warnings)
}

fn blank(value: &Option<String>) -> bool {
    value
        .as_deref()
        .map(|value| value.trim().is_empty())
        .unwrap_or(true)
}

/// Mirror of Path.GetFileNameWithoutExtension for both separators.
pub fn executable_stem(name: &str) -> String {
    let file = name.rsplit(['/', '\\']).next().unwrap_or(name);
    match file.rfind('.') {
        Some(index) => file[..index].to_string(),
        None => file.to_string(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn stem_matches_get_file_name_without_extension() {
        assert_eq!(executable_stem("game.exe"), "game");
        assert_eq!(executable_stem(r"C:\Games\App.EXE"), "App");
        assert_eq!(executable_stem("noext"), "noext");
    }
}
