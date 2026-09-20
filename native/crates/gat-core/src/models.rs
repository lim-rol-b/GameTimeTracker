//! Domain models. JSON field names and shapes match the original C# records so the
//! existing SQLite database and WPF client stay interoperable.

use chrono::{DateTime, Datelike, Utc};
use serde::{Deserialize, Serialize};
use uuid::Uuid;

/// Generate a 32-character lowercase hex id, matching Guid.NewGuid().ToString("N").
pub fn new_id() -> String {
    Uuid::new_v4().simple().to_string()
}

fn default_true() -> bool {
    true
}

fn current_year() -> i32 {
    chrono::Local::now().year()
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "UPPERCASE")]
pub enum ActivityState {
    Active,
    Idle,
    Background,
    Unknown,
}

impl ActivityState {
    pub fn as_str(self) -> &'static str {
        match self {
            ActivityState::Active => "ACTIVE",
            ActivityState::Idle => "IDLE",
            ActivityState::Background => "BACKGROUND",
            ActivityState::Unknown => "UNKNOWN",
        }
    }

    pub fn from_db_str(value: &str) -> Option<Self> {
        match value {
            "ACTIVE" => Some(ActivityState::Active),
            "IDLE" => Some(ActivityState::Idle),
            "BACKGROUND" => Some(ActivityState::Background),
            "UNKNOWN" => Some(ActivityState::Unknown),
            _ => None,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct Game {
    #[serde(default = "new_id")]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub executable: String,
    #[serde(default)]
    pub executable_path: String,
    #[serde(default)]
    pub steam_app_id: Option<String>,
    #[serde(default = "default_true")]
    pub detect_related_executables: bool,
    #[serde(default)]
    pub minecraft_directory: Option<String>,
    #[serde(default)]
    pub install_directory: Option<String>,
    #[serde(default)]
    pub icon_path: Option<String>,
    #[serde(default)]
    pub cover_path: Option<String>,
    #[serde(default = "Utc::now")]
    pub created_at: DateTime<Utc>,
    #[serde(default = "Utc::now")]
    pub updated_at: DateTime<Utc>,
}

impl Default for Game {
    fn default() -> Self {
        let now = Utc::now();
        Self {
            id: new_id(),
            name: String::new(),
            executable: String::new(),
            executable_path: String::new(),
            steam_app_id: None,
            detect_related_executables: true,
            minecraft_directory: None,
            install_directory: None,
            icon_path: None,
            cover_path: None,
            created_at: now,
            updated_at: now,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct GameProcessRule {
    #[serde(default = "new_id")]
    pub id: String,
    #[serde(default)]
    pub game_id: String,
    #[serde(default)]
    pub executable_name: String,
    #[serde(default)]
    pub executable_path: Option<String>,
    #[serde(default)]
    pub path_contains: Option<String>,
    #[serde(default)]
    pub minecraft_root_directory: Option<String>,
    #[serde(default)]
    pub command_line_contains: Option<String>,
    #[serde(default)]
    pub steam_app_id: Option<String>,
    #[serde(default)]
    pub priority: i32,
    #[serde(default = "default_true")]
    pub enabled: bool,
}

impl Default for GameProcessRule {
    fn default() -> Self {
        Self {
            id: new_id(),
            game_id: String::new(),
            executable_name: String::new(),
            executable_path: None,
            path_contains: None,
            minecraft_root_directory: None,
            command_line_contains: None,
            steam_app_id: None,
            priority: 0,
            enabled: true,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct ActivitySegment {
    #[serde(default = "new_id")]
    pub id: String,
    #[serde(default)]
    pub session_id: String,
    #[serde(default = "Utc::now")]
    pub start_time: DateTime<Utc>,
    #[serde(default = "Utc::now")]
    pub end_time: DateTime<Utc>,
    #[serde(default = "default_state")]
    pub state: ActivityState,
}

fn default_state() -> ActivityState {
    ActivityState::Unknown
}

impl Default for ActivitySegment {
    fn default() -> Self {
        let now = Utc::now();
        Self {
            id: new_id(),
            session_id: String::new(),
            start_time: now,
            end_time: now,
            state: ActivityState::Unknown,
        }
    }
}

impl ActivitySegment {
    /// Duration in seconds, never negative.
    pub fn duration(&self) -> f64 {
        let micros = (self.end_time - self.start_time)
            .num_microseconds()
            .unwrap_or(0);
        (micros as f64 / 1_000_000.0).max(0.0)
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct GameSession {
    #[serde(default = "new_id")]
    pub id: String,
    #[serde(default)]
    pub game_id: String,
    #[serde(default = "Utc::now")]
    pub start_time: DateTime<Utc>,
    #[serde(default)]
    pub end_time: Option<DateTime<Utc>>,
    #[serde(default = "Utc::now")]
    pub last_checkpoint: DateTime<Utc>,
    #[serde(default = "default_source")]
    pub source: String,
    #[serde(default)]
    pub end_reason: Option<String>,
    #[serde(skip)]
    pub segments: Vec<ActivitySegment>,
}

fn default_source() -> String {
    "Observed".to_string()
}

impl Default for GameSession {
    fn default() -> Self {
        let now = Utc::now();
        Self {
            id: new_id(),
            game_id: String::new(),
            start_time: now,
            end_time: None,
            last_checkpoint: now,
            source: "Observed".to_string(),
            end_reason: None,
            segments: Vec::new(),
        }
    }
}

impl GameSession {
    pub fn running_duration(&self) -> f64 {
        self.segments.iter().map(|segment| segment.duration()).sum()
    }

    pub fn active_duration(&self) -> f64 {
        self.duration(ActivityState::Active)
    }

    pub fn idle_duration(&self) -> f64 {
        self.duration(ActivityState::Idle)
    }

    pub fn background_duration(&self) -> f64 {
        self.duration(ActivityState::Background)
    }

    pub fn unknown_duration(&self) -> f64 {
        self.duration(ActivityState::Unknown)
    }

    fn duration(&self, state: ActivityState) -> f64 {
        self.segments
            .iter()
            .filter(|segment| segment.state == state)
            .map(|segment| segment.duration())
            .sum()
    }
}

#[derive(Debug, thiserror::Error, PartialEq, Eq)]
pub enum SettingsError {
    #[error("无效的主题模式。")]
    Theme,
    #[error("无效的起始年份。")]
    FirstYear,
    #[error("空闲阈值必须为 1–3600 秒。")]
    Idle,
    #[error("扫描间隔必须为 1–60 秒。")]
    Scan,
    #[error("手柄死区必须为 0.05–0.95。")]
    DeadZone,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct TrackerSettings {
    #[serde(default = "default_theme")]
    pub theme_mode: String,
    #[serde(default = "current_year")]
    pub first_tracking_year: i32,
    #[serde(default = "default_idle")]
    pub idle_threshold_seconds: i64,
    #[serde(default = "default_scan")]
    pub process_scan_interval_seconds: i64,
    #[serde(default)]
    pub start_with_windows: bool,
    #[serde(default = "default_true")]
    pub enable_controller_detection: bool,
    #[serde(default = "default_dead_zone")]
    pub controller_dead_zone: f64,
}

fn default_theme() -> String {
    "System".to_string()
}

fn default_idle() -> i64 {
    60
}

fn default_scan() -> i64 {
    2
}

fn default_dead_zone() -> f64 {
    0.20
}

impl Default for TrackerSettings {
    fn default() -> Self {
        Self {
            theme_mode: default_theme(),
            first_tracking_year: current_year(),
            idle_threshold_seconds: 60,
            process_scan_interval_seconds: 2,
            start_with_windows: false,
            enable_controller_detection: true,
            controller_dead_zone: 0.20,
        }
    }
}

impl TrackerSettings {
    pub fn validate(&self) -> Result<(), SettingsError> {
        if !matches!(self.theme_mode.as_str(), "System" | "Light" | "Dark") {
            return Err(SettingsError::Theme);
        }
        if !(1..=9999).contains(&self.first_tracking_year) {
            return Err(SettingsError::FirstYear);
        }
        if !(1..=3600).contains(&self.idle_threshold_seconds) {
            return Err(SettingsError::Idle);
        }
        if !(1..=60).contains(&self.process_scan_interval_seconds) {
            return Err(SettingsError::Scan);
        }
        if !self.controller_dead_zone.is_finite()
            || self.controller_dead_zone < 0.05
            || self.controller_dead_zone > 0.95
        {
            return Err(SettingsError::DeadZone);
        }
        Ok(())
    }

    pub fn idle_threshold(&self) -> std::time::Duration {
        std::time::Duration::from_secs(self.idle_threshold_seconds.max(1) as u64)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn defaults_match_legacy_settings() {
        let settings = TrackerSettings::default();
        assert_eq!(settings.theme_mode, "System");
        assert_eq!(settings.idle_threshold_seconds, 60);
        assert_eq!(settings.process_scan_interval_seconds, 2);
        assert!((settings.controller_dead_zone - 0.20).abs() < f64::EPSILON);
        settings.validate().unwrap();
    }

    #[test]
    fn new_id_is_32_hex_chars() {
        let id = new_id();
        assert_eq!(id.len(), 32);
        assert!(id.chars().all(|c| c.is_ascii_hexdigit()));
        assert_ne!(id, new_id());
    }

    #[test]
    fn activity_state_round_trips() {
        assert_eq!(
            ActivityState::from_db_str("ACTIVE"),
            Some(ActivityState::Active)
        );
        assert_eq!(ActivityState::Background.as_str(), "BACKGROUND");
        assert_eq!(ActivityState::from_db_str("bogus"), None);
    }

    #[test]
    fn deserializes_legacy_csharp_game_json() {
        let json = serde_json::json!({
            "Id": "abc",
            "Name": "Apex",
            "Executable": "r5apex.exe",
            "ExecutablePath": r"C:\\Games\\r5apex.exe",
            "SteamAppId": null,
            "DetectRelatedExecutables": true,
            "MinecraftDirectory": null,
            "InstallDirectory": null,
            "IconPath": null,
            "CoverPath": null,
            "CreatedAt": "2026-09-15T12:00:00.1234567+00:00",
            "UpdatedAt": "2026-09-15T12:00:00.1234567+00:00"
        });
        let game: Game = serde_json::from_value(json).unwrap();
        assert_eq!(game.name, "Apex");
        assert_eq!(game.executable_path, r"C:\\Games\\r5apex.exe");
        assert!(game.detect_related_executables);
        assert_eq!(game.created_at.format("%Y-%m-%d").to_string(), "2026-09-15");
    }
}
