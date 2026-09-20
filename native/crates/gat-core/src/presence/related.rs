//! Expansion of a game's primary executable into related executables found in its
//! install directory, excluding launchers and shared runtimes.

use std::collections::BTreeSet;
use std::path::Path;

use crate::models::{Game, GameProcessRule};
use crate::presence::paths::{file_name, file_stem, full_path, trim_end_dir_sep_path};
use crate::text::eq_ignore_case;

const LAUNCHER_NAMES: &[&str] = &[
    "steam",
    "EADesktop",
    "EALauncher",
    "Origin",
    "EpicGamesLauncher",
    "Battle.net",
    "UbisoftConnect",
    "upc",
    "start_protected_game",
    "start_game",
    "start",
];

const SHARED_HOST_NAMES: &[&str] = &[
    "steam",
    "EADesktop",
    "EALauncher",
    "Origin",
    "EpicGamesLauncher",
    "Battle.net",
    "UbisoftConnect",
    "upc",
    "java",
    "javaw",
    "dotnet",
    "python",
    "pythonw",
];

pub struct RelatedProcessRules;

impl RelatedProcessRules {
    pub fn is_launcher(path: &str) -> bool {
        let name = file_stem(Path::new(path));
        let lower = name.to_lowercase();
        lower.starts_with("pcl")
            || lower.contains("launcher")
            || LAUNCHER_NAMES
                .iter()
                .any(|known| eq_ignore_case(&name, known))
    }

    pub fn is_shared_host(path: &str) -> bool {
        let name = file_stem(Path::new(path));
        SHARED_HOST_NAMES
            .iter()
            .any(|known| eq_ignore_case(&name, known))
    }

    pub fn is_simple(rule: &GameProcessRule) -> bool {
        rule.enabled
            && !is_blank(rule.executable_path.as_deref())
            && is_blank(rule.minecraft_root_directory.as_deref())
            && is_blank(rule.path_contains.as_deref())
            && is_blank(rule.command_line_contains.as_deref())
            && is_blank(rule.steam_app_id.as_deref())
    }

    /// Only simple generated rules are expanded. Advanced conjunctions retain their meaning.
    pub fn expand(
        game: &Game,
        rules: &[GameProcessRule],
        root: &str,
        executables: &[String],
    ) -> Vec<GameProcessRule> {
        if !game.detect_related_executables || !rules.iter().any(Self::is_simple) {
            return rules.to_vec();
        }
        let root_full = trim_end_dir_sep_path(&full_path(root));
        let prefix = format!(
            "{}{}",
            root_full.to_string_lossy(),
            std::path::MAIN_SEPARATOR
        );
        let prefix_lower = prefix.to_lowercase();

        let mut seen: BTreeSet<String> = BTreeSet::new();
        let mut candidates: Vec<String> = Vec::new();
        for path in executables {
            let full = full_path(path).to_string_lossy().into_owned();
            if !full.to_lowercase().starts_with(&prefix_lower) {
                continue;
            }
            if Self::is_launcher(&full) || Self::is_shared_host(&full) {
                continue;
            }
            if seen.insert(full.to_lowercase()) {
                candidates.push(full);
            }
        }
        candidates.sort_by_key(|candidate| candidate.to_lowercase());

        let mut result: Vec<GameProcessRule> = rules
            .iter()
            .filter(|rule| !Self::is_simple(rule) || !Self::is_launcher(&rule.executable_name))
            .cloned()
            .collect();
        for path in candidates {
            let already_known = rules.iter().any(|rule| {
                rule.executable_path
                    .as_deref()
                    .map(|known| eq_ignore_case(known, &path))
                    .unwrap_or(false)
            });
            if already_known {
                continue;
            }
            result.push(GameProcessRule {
                id: format!("related:{}:{}", game.id, path),
                game_id: game.id.clone(),
                executable_name: file_name(Path::new(&path)),
                executable_path: Some(path),
                priority: i32::MIN,
                ..Default::default()
            });
        }
        result
    }
}

fn is_blank(value: Option<&str>) -> bool {
    value.map(|value| value.trim().is_empty()).unwrap_or(true)
}
