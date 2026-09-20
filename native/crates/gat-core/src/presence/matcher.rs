//! A rule is a conjunction, never a collection of independent guesses.

use chrono::{DateTime, Utc};

use crate::models::GameProcessRule;
use crate::presence::minecraft::MinecraftProcessIdentity;
use crate::text::{contains_ignore_case, eq_ignore_case};

#[derive(Debug, Clone, PartialEq)]
pub struct ProcessSnapshot {
    pub pid: i32,
    pub executable_name: String,
    pub full_path: Option<String>,
    pub command_line: Option<String>,
    pub started_at: Option<DateTime<Utc>>,
    pub steam_app_id: Option<String>,
}

impl ProcessSnapshot {
    pub fn new(
        pid: i32,
        executable_name: impl Into<String>,
        full_path: Option<String>,
        command_line: Option<String>,
        started_at: Option<DateTime<Utc>>,
    ) -> Self {
        Self {
            pid,
            executable_name: executable_name.into(),
            full_path,
            command_line,
            started_at,
            steam_app_id: None,
        }
    }
}

#[derive(Debug, Default, Clone, Copy)]
pub struct GameMatcher;

impl GameMatcher {
    pub fn match_process(
        &self,
        process: &ProcessSnapshot,
        rules: &[GameProcessRule],
    ) -> Option<String> {
        let mut candidates: Vec<&GameProcessRule> =
            rules.iter().filter(|rule| rule.enabled).collect();
        candidates.sort_by(|a, b| {
            b.priority
                .cmp(&a.priority)
                .then_with(|| minecraft_len(b).cmp(&minecraft_len(a)))
                .then_with(|| a.id.cmp(&b.id))
        });
        candidates
            .into_iter()
            .find(|rule| matches(process, rule))
            .map(|rule| rule.game_id.clone())
    }
}

fn minecraft_len(rule: &GameProcessRule) -> usize {
    rule.minecraft_root_directory
        .as_deref()
        .map(str::len)
        .unwrap_or(0)
}

fn matches(process: &ProcessSnapshot, rule: &GameProcessRule) -> bool {
    if rule.executable_name.trim().is_empty() {
        return false;
    }
    if !eq_ignore_case(&process.executable_name, &rule.executable_name) {
        return false;
    }
    if !equals_if_set(
        process.full_path.as_deref(),
        rule.executable_path.as_deref(),
    ) {
        return false;
    }
    if !contains_if_set(process.full_path.as_deref(), rule.path_contains.as_deref()) {
        return false;
    }
    if !contains_if_set(
        process.command_line.as_deref(),
        rule.command_line_contains.as_deref(),
    ) {
        return false;
    }
    if !equals_if_set(
        process.steam_app_id.as_deref(),
        rule.steam_app_id.as_deref(),
    ) {
        return false;
    }
    if let Some(root) = rule.minecraft_root_directory.as_deref() {
        if !root.trim().is_empty()
            && !MinecraftProcessIdentity::matches(process.command_line.as_deref(), root)
        {
            return false;
        }
    }
    true
}

fn equals_if_set(value: Option<&str>, filter: Option<&str>) -> bool {
    match filter {
        None => true,
        Some(filter) if filter.trim().is_empty() => true,
        Some(filter) => value
            .map(|value| eq_ignore_case(value, filter))
            .unwrap_or(false),
    }
}

fn contains_if_set(value: Option<&str>, filter: Option<&str>) -> bool {
    match filter {
        None => true,
        Some(filter) if filter.trim().is_empty() => true,
        Some(filter) => value
            .map(|value| contains_ignore_case(value, filter))
            .unwrap_or(false),
    }
}
