//! Short-lived connections, WAL, foreign keys and atomic checkpoints. Durations remain
//! derived from segments. The schema and value formats match the C# implementation so
//! the WPF client can keep reading and writing the same activity.db.

use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::time::Duration;

use chrono::{DateTime, Utc};
use rusqlite::{params, Connection, OptionalExtension};
use serde::de::DeserializeOwned;
use serde::{Deserialize, Serialize};

use gat_core::models::{
    ActivitySegment, ActivityState, Game, GameProcessRule, GameSession, TrackerSettings,
};

const SCHEMA: &str = r#"
CREATE TABLE IF NOT EXISTS Games(Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS Rules(Id TEXT PRIMARY KEY, GameId TEXT NOT NULL REFERENCES Games(Id) ON DELETE CASCADE, Json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS Sessions(Id TEXT PRIMARY KEY, GameId TEXT NOT NULL REFERENCES Games(Id) ON DELETE CASCADE,
    StartTime TEXT NOT NULL, EndTime TEXT, LastCheckpoint TEXT NOT NULL, Source TEXT NOT NULL, EndReason TEXT);
CREATE TABLE IF NOT EXISTS Segments(Id TEXT PRIMARY KEY, SessionId TEXT NOT NULL REFERENCES Sessions(Id) ON DELETE CASCADE,
    StartTime TEXT NOT NULL, EndTime TEXT NOT NULL, State TEXT NOT NULL CHECK(State IN ('ACTIVE','IDLE','BACKGROUND','UNKNOWN')));
CREATE INDEX IF NOT EXISTS IX_Sessions_Game ON Sessions(GameId, StartTime);
CREATE INDEX IF NOT EXISTS IX_Segments_Session ON Segments(SessionId, StartTime);
CREATE TABLE IF NOT EXISTS Settings(Id INTEGER PRIMARY KEY CHECK(Id=1), Json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS DaemonSettings(Id INTEGER PRIMARY KEY CHECK(Id=1), Json TEXT NOT NULL);
PRAGMA user_version=1;
"#;

#[derive(Debug, thiserror::Error)]
pub enum DataError {
    #[error(transparent)]
    Sqlite(#[from] rusqlite::Error),
    #[error(transparent)]
    Json(#[from] serde_json::Error),
    #[error(transparent)]
    Io(#[from] std::io::Error),
    #[error(transparent)]
    Time(#[from] chrono::ParseError),
    #[error(transparent)]
    Settings(#[from] gat_core::models::SettingsError),
    #[error("{0}")]
    InvalidArgument(String),
}

/// Settings owned by the native background mode. Stored in its own table so the WPF
/// client's Settings row stays byte-compatible.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct DaemonSettings {
    /// Lightweight background mode: no window, reduced polling and discovery.
    pub lightweight_mode: bool,
    pub show_tray_icon: bool,
    pub process_scan_seconds: i64,
    pub smoke_test_seconds: u64,
}

impl Default for DaemonSettings {
    fn default() -> Self {
        Self {
            lightweight_mode: true,
            show_tray_icon: true,
            process_scan_seconds: 5,
            smoke_test_seconds: 5,
        }
    }
}

#[derive(Debug, Clone)]
pub struct TrackerDatabase {
    path: PathBuf,
}

impl TrackerDatabase {
    pub fn open(path: impl AsRef<Path>) -> Result<Self, DataError> {
        let path = path.as_ref().to_path_buf();
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        let database = Self { path };
        let connection = database.connect()?;
        connection.pragma_update(None, "journal_mode", "WAL")?;
        connection.pragma_update(None, "synchronous", "FULL")?;
        connection.execute_batch(SCHEMA)?;
        Ok(database)
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    fn connect(&self) -> Result<Connection, DataError> {
        let connection = Connection::open(&self.path)?;
        connection.busy_timeout(Duration::from_secs(10))?;
        connection.execute_batch("PRAGMA foreign_keys=ON;")?;
        Ok(connection)
    }

    pub fn get_games(&self) -> Result<Vec<Game>, DataError> {
        self.read_json("SELECT Json FROM Games ORDER BY Name")
    }

    pub fn get_rules(&self) -> Result<Vec<GameProcessRule>, DataError> {
        self.read_json("SELECT Json FROM Rules")
    }

    fn read_json<T: DeserializeOwned>(&self, sql: &str) -> Result<Vec<T>, DataError> {
        let connection = self.connect()?;
        let mut statement = connection.prepare(sql)?;
        let rows = statement.query_map([], |row| row.get::<_, String>(0))?;
        let mut result = Vec::new();
        for row in rows {
            result.push(serde_json::from_str(&row?)?);
        }
        Ok(result)
    }

    pub fn save_game(&self, game: &Game, rules: &[GameProcessRule]) -> Result<(), DataError> {
        self.save_games(&[(game.clone(), rules.to_vec())])
    }

    pub fn save_games(&self, games: &[(Game, Vec<GameProcessRule>)]) -> Result<(), DataError> {
        for (game, rules) in games {
            if game.name.trim().is_empty() {
                return Err(DataError::InvalidArgument("游戏名称不能为空。".to_string()));
            }
            if rules.is_empty()
                || rules
                    .iter()
                    .any(|rule| rule.game_id != game.id || rule.executable_name.trim().is_empty())
            {
                return Err(DataError::InvalidArgument(
                    "至少需要一条包含可执行文件名的识别规则。".to_string(),
                ));
            }
        }

        let mut connection = self.connect()?;
        let transaction = connection.transaction()?;
        for (game, rules) in games {
            let mut stored = game.clone();
            stored.updated_at = Utc::now();
            transaction.execute(
                "INSERT INTO Games VALUES(?1,?2,?3) ON CONFLICT(Id) DO UPDATE SET Name=?2,Json=?3",
                params![stored.id, stored.name, serde_json::to_string(&stored)?],
            )?;
            transaction.execute("DELETE FROM Rules WHERE GameId=?1", params![stored.id])?;
            for rule in rules {
                transaction.execute(
                    "INSERT INTO Rules VALUES(?1,?2,?3)",
                    params![rule.id, rule.game_id, serde_json::to_string(rule)?],
                )?;
            }
        }
        transaction.commit()?;
        Ok(())
    }

    pub fn delete_game(&self, id: &str) -> Result<(), DataError> {
        let connection = self.connect()?;
        connection.execute("DELETE FROM Games WHERE Id=?1", params![id])?;
        Ok(())
    }

    pub fn save_sessions(&self, sessions: &[GameSession]) -> Result<(), DataError> {
        let mut connection = self.connect()?;
        let transaction = connection.transaction()?;
        for session in sessions {
            let start = format_time(session.start_time);
            let end = session.end_time.map(format_time);
            let checkpoint = format_time(session.last_checkpoint);
            transaction.execute(
                "INSERT INTO Sessions VALUES(?1,?2,?3,?4,?5,?6,?7)                  ON CONFLICT(Id) DO UPDATE SET EndTime=?4,LastCheckpoint=?5,EndReason=?7",
                params![
                    session.id,
                    session.game_id,
                    start,
                    end,
                    checkpoint,
                    session.source,
                    session.end_reason
                ],
            )?;
            for segment in &session.segments {
                transaction.execute(
                    "INSERT INTO Segments VALUES(?1,?2,?3,?4,?5)                      ON CONFLICT(Id) DO UPDATE SET EndTime=?4",
                    params![
                        segment.id,
                        session.id,
                        format_time(segment.start_time),
                        format_time(segment.end_time),
                        segment.state.as_str()
                    ],
                )?;
            }
        }
        transaction.commit()?;
        Ok(())
    }

    pub fn get_sessions(&self) -> Result<Vec<GameSession>, DataError> {
        let connection = self.connect()?;
        let mut sessions: Vec<GameSession> = Vec::new();
        let mut index_by_id: HashMap<String, usize> = HashMap::new();

        {
            let mut statement = connection.prepare(
                "SELECT Id,GameId,StartTime,EndTime,LastCheckpoint,Source,EndReason                  FROM Sessions ORDER BY StartTime DESC",
            )?;
            let rows = statement.query_map([], |row| {
                Ok((
                    row.get::<_, String>(0)?,
                    row.get::<_, String>(1)?,
                    row.get::<_, String>(2)?,
                    row.get::<_, Option<String>>(3)?,
                    row.get::<_, String>(4)?,
                    row.get::<_, String>(5)?,
                    row.get::<_, Option<String>>(6)?,
                ))
            })?;
            for row in rows {
                let (id, game_id, start, end, checkpoint, source, reason) = row?;
                let end_time = match end {
                    Some(value) => Some(parse_time(&value)?),
                    None => None,
                };
                index_by_id.insert(id.clone(), sessions.len());
                sessions.push(GameSession {
                    id,
                    game_id,
                    start_time: parse_time(&start)?,
                    end_time,
                    last_checkpoint: parse_time(&checkpoint)?,
                    source,
                    end_reason: reason,
                    segments: Vec::new(),
                });
            }
        }

        {
            let mut statement = connection.prepare(
                "SELECT Id,SessionId,StartTime,EndTime,State FROM Segments ORDER BY StartTime",
            )?;
            let rows = statement.query_map([], |row| {
                Ok((
                    row.get::<_, String>(0)?,
                    row.get::<_, String>(1)?,
                    row.get::<_, String>(2)?,
                    row.get::<_, String>(3)?,
                    row.get::<_, String>(4)?,
                ))
            })?;
            for row in rows {
                let (id, session_id, start, end, state) = row?;
                let Some(&index) = index_by_id.get(&session_id) else {
                    continue;
                };
                let state = ActivityState::from_db_str(&state)
                    .ok_or_else(|| DataError::InvalidArgument(format!("未知状态：{}", state)))?;
                sessions[index].segments.push(ActivitySegment {
                    id,
                    session_id,
                    start_time: parse_time(&start)?,
                    end_time: parse_time(&end)?,
                    state,
                });
            }
        }

        Ok(sessions)
    }

    pub fn recover_open_sessions(&self) -> Result<i32, DataError> {
        let connection = self.connect()?;
        let changed = connection.execute(
            "UPDATE Sessions SET EndTime=LastCheckpoint,EndReason='RecoveredAtCheckpoint'              WHERE EndTime IS NULL",
            [],
        )?;
        Ok(changed as i32)
    }

    pub fn get_settings(&self) -> Result<TrackerSettings, DataError> {
        let connection = self.connect()?;
        let text: Option<String> = connection
            .query_row("SELECT Json FROM Settings WHERE Id=1", [], |row| row.get(0))
            .optional()?;
        let settings = match text {
            Some(value) => serde_json::from_str(&value)?,
            None => TrackerSettings::default(),
        };
        settings.validate()?;
        Ok(settings)
    }

    pub fn save_settings(&self, settings: &TrackerSettings) -> Result<(), DataError> {
        settings.validate()?;
        let connection = self.connect()?;
        connection.execute(
            "INSERT INTO Settings VALUES(1,?1) ON CONFLICT(Id) DO UPDATE SET Json=?1",
            params![serde_json::to_string(settings)?],
        )?;
        Ok(())
    }

    pub fn get_daemon_settings(&self) -> Result<DaemonSettings, DataError> {
        let connection = self.connect()?;
        let text: Option<String> = connection
            .query_row("SELECT Json FROM DaemonSettings WHERE Id=1", [], |row| {
                row.get(0)
            })
            .optional()?;
        Ok(match text {
            Some(value) => serde_json::from_str(&value)?,
            None => DaemonSettings::default(),
        })
    }

    pub fn save_daemon_settings(&self, settings: &DaemonSettings) -> Result<(), DataError> {
        let connection = self.connect()?;
        connection.execute(
            "INSERT INTO DaemonSettings VALUES(1,?1) ON CONFLICT(Id) DO UPDATE SET Json=?1",
            params![serde_json::to_string(settings)?],
        )?;
        Ok(())
    }
}

fn format_time(time: DateTime<Utc>) -> String {
    // Numeric offset and nanosecond fraction; C# DateTimeOffset.Parse reads this shape.
    time.to_rfc3339_opts(chrono::SecondsFormat::Nanos, false)
}

fn parse_time(value: &str) -> Result<DateTime<Utc>, DataError> {
    Ok(DateTime::parse_from_rfc3339(value)?.with_timezone(&Utc))
}
