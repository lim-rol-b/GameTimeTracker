use std::path::{Path, PathBuf};

use gat_core::models::{Game, GameSession, TrackerSettings};
use gat_core::tracking::{ActivityObservation, SessionManager};
use gat_data::TrackerDatabase;

struct TempDir {
    path: PathBuf,
}

impl TempDir {
    fn new(label: &str) -> Self {
        let path =
            std::env::temp_dir().join(format!("gat-db-{}-{}", label, gat_core::models::new_id()));
        std::fs::create_dir_all(&path).unwrap();
        Self { path }
    }

    fn db(&self) -> PathBuf {
        self.path.join("activity.db")
    }
}

impl Drop for TempDir {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.path);
    }
}

fn parse(value: &str) -> chrono::DateTime<chrono::Utc> {
    chrono::DateTime::parse_from_rfc3339(value)
        .unwrap()
        .with_timezone(&chrono::Utc)
}

#[test]
fn initialize_save_reopen_and_recover_at_last_checkpoint() {
    let temp = TempDir::new("recover");
    let path = temp.db();
    let db = TrackerDatabase::open(&path).unwrap();
    let game = Game {
        name: "Test".to_string(),
        executable: "game.exe".to_string(),
        ..Default::default()
    };
    db.save_game(
        &game,
        &[gat_core::models::GameProcessRule {
            game_id: game.id.clone(),
            executable_name: "game.exe".to_string(),
            ..Default::default()
        }],
    )
    .unwrap();

    let start = parse("2026-09-15T12:00:00Z");
    let mut manager = SessionManager::new();
    manager.start(
        &game.id,
        start,
        ActivityObservation::new(Some(true), Some(start)),
    );
    manager.advance(
        &game.id,
        start + chrono::Duration::seconds(70),
        ActivityObservation::new(Some(true), Some(start)),
    );
    db.save_sessions(&manager.sessions().into_iter().cloned().collect::<Vec<_>>())
        .unwrap();

    let reopened = TrackerDatabase::open(&path).unwrap();
    assert_eq!(reopened.recover_open_sessions().unwrap(), 1);
    assert_eq!(reopened.recover_open_sessions().unwrap(), 0);

    let sessions = reopened.get_sessions().unwrap();
    assert_eq!(sessions.len(), 1);
    let stored = &sessions[0];
    assert_eq!(stored.end_time, Some(start + chrono::Duration::seconds(70)));
    assert_eq!(stored.end_reason.as_deref(), Some("RecoveredAtCheckpoint"));
    assert!((stored.active_duration() - 60.0).abs() < 1e-6);
    assert!((stored.idle_duration() - 10.0).abs() < 1e-6);
    assert!((stored.running_duration() - 70.0).abs() < 1e-6);
    assert_eq!(stored.segments.len(), 2);

    assert_eq!(reopened.get_games().unwrap().len(), 1);
    assert_eq!(reopened.get_games().unwrap()[0].name, "Test");
    assert_eq!(reopened.get_rules().unwrap().len(), 1);
}

#[test]
fn checkpoints_upsert_without_double_counting() {
    let temp = TempDir::new("checkpoint");
    let db = TrackerDatabase::open(temp.db()).unwrap();
    let game = Game {
        name: "Test".to_string(),
        ..Default::default()
    };
    db.save_game(
        &game,
        &[gat_core::models::GameProcessRule {
            game_id: game.id.clone(),
            executable_name: "game.exe".to_string(),
            ..Default::default()
        }],
    )
    .unwrap();

    let t = chrono::Utc::now();
    let mut manager = SessionManager::new();
    manager.start(&game.id, t, ActivityObservation::new(Some(true), Some(t)));
    manager.advance(
        &game.id,
        t + chrono::Duration::seconds(10),
        ActivityObservation::new(Some(true), Some(t)),
    );
    db.save_sessions(&manager.sessions().into_iter().cloned().collect::<Vec<_>>())
        .unwrap();
    manager.advance(
        &game.id,
        t + chrono::Duration::seconds(20),
        ActivityObservation::new(Some(true), Some(t)),
    );
    db.save_sessions(&manager.sessions().into_iter().cloned().collect::<Vec<_>>())
        .unwrap();

    let ended = manager
        .stop(&game.id, t + chrono::Duration::seconds(30), "Exit")
        .unwrap();
    db.save_sessions(std::slice::from_ref(&ended)).unwrap();
    db.save_sessions(&[ended]).unwrap();

    let sessions = db.get_sessions().unwrap();
    assert_eq!(sessions.len(), 1);
    assert_eq!(sessions[0].segments.len(), 1);
    assert!((sessions[0].running_duration() - 30.0).abs() < 1e-6);
    assert_eq!(db.recover_open_sessions().unwrap(), 0);
}

#[test]
fn delete_game_cascades_and_settings_persist() {
    let temp = TempDir::new("cascade");
    let path = temp.db();
    let db = TrackerDatabase::open(&path).unwrap();
    let game = Game {
        name: "Test".to_string(),
        ..Default::default()
    };
    db.save_game(
        &game,
        &[gat_core::models::GameProcessRule {
            game_id: game.id.clone(),
            executable_name: "game.exe".to_string(),
            ..Default::default()
        }],
    )
    .unwrap();

    let t = chrono::Utc::now();
    let mut manager = SessionManager::new();
    manager.start(&game.id, t, ActivityObservation::new(Some(true), Some(t)));
    let ended = manager
        .stop(&game.id, t + chrono::Duration::minutes(1), "Exit")
        .unwrap();
    db.save_sessions(&[ended]).unwrap();

    db.save_settings(&TrackerSettings {
        idle_threshold_seconds: 120,
        controller_dead_zone: 0.3,
        ..Default::default()
    })
    .unwrap();
    db.delete_game(&game.id).unwrap();

    let reopened = TrackerDatabase::open(&path).unwrap();
    assert!(reopened.get_games().unwrap().is_empty());
    assert!(reopened.get_rules().unwrap().is_empty());
    assert!(reopened.get_sessions().unwrap().is_empty());
    assert_eq!(reopened.get_settings().unwrap().idle_threshold_seconds, 120);
}

#[test]
fn failed_checkpoint_rolls_back_entire_transaction() {
    let temp = TempDir::new("rollback");
    let db = TrackerDatabase::open(temp.db()).unwrap();
    let game = Game {
        name: "Test".to_string(),
        ..Default::default()
    };
    db.save_game(
        &game,
        &[gat_core::models::GameProcessRule {
            game_id: game.id.clone(),
            executable_name: "game.exe".to_string(),
            ..Default::default()
        }],
    )
    .unwrap();

    let t = chrono::Utc::now();
    let mut manager = SessionManager::new();
    manager.start(&game.id, t, ActivityObservation::new(Some(true), Some(t)));
    let valid = manager.sessions()[0].clone();
    let invalid = GameSession {
        game_id: "missing".to_string(),
        start_time: t,
        last_checkpoint: t,
        ..Default::default()
    };
    assert!(db.save_sessions(&[valid, invalid]).is_err());
    assert!(db.get_sessions().unwrap().is_empty());
}

#[test]
fn old_settings_default_to_system_and_current_year() {
    let settings: TrackerSettings = serde_json::from_str(r#"{"IdleThresholdSeconds":90}"#).unwrap();
    assert_eq!(settings.theme_mode, "System");
    assert_eq!(
        settings.first_tracking_year,
        chrono::Datelike::year(&chrono::Local::now())
    );
    assert_eq!(settings.idle_threshold_seconds, 90);
    settings.validate().unwrap();
}

#[test]
fn theme_and_first_year_survive_database_reopen() {
    let temp = TempDir::new("theme");
    let path = temp.db();
    let db = TrackerDatabase::open(&path).unwrap();
    db.save_settings(&TrackerSettings {
        theme_mode: "Dark".to_string(),
        first_tracking_year: 2026,
        idle_threshold_seconds: 90,
        ..Default::default()
    })
    .unwrap();
    let reopened = TrackerDatabase::open(&path)
        .unwrap()
        .get_settings()
        .unwrap();
    assert_eq!(reopened.theme_mode, "Dark");
    assert_eq!(reopened.first_tracking_year, 2026);
    assert_eq!(reopened.idle_threshold_seconds, 90);
}

#[test]
fn daemon_settings_round_trip_and_default() {
    let temp = TempDir::new("daemon");
    let path = temp.db();
    let db = TrackerDatabase::open(&path).unwrap();
    assert!(db.get_daemon_settings().unwrap().lightweight_mode);
    let mut settings = db.get_daemon_settings().unwrap();
    settings.lightweight_mode = false;
    settings.process_scan_seconds = 9;
    db.save_daemon_settings(&settings).unwrap();
    let reopened = TrackerDatabase::open(&path)
        .unwrap()
        .get_daemon_settings()
        .unwrap();
    assert_eq!(reopened, settings);
}

#[test]
fn first_creation_of_c_style_database_is_readable() {
    // The Rust layer must understand a database created from scratch by the Rust schema,
    // whose JSON and timestamp shape mirror the C# writer.
    let temp = TempDir::new("schema");
    let path = temp.db();
    let db = TrackerDatabase::open(&path).unwrap();
    let connection = rusqlite::Connection::open(&path).unwrap();
    let version: i32 = connection
        .query_row("PRAGMA user_version", [], |row| row.get(0))
        .unwrap();
    assert_eq!(version, 1);
    let _ = db;
    let _ = Path::new(&path);
}

#[test]
fn reads_csharp_written_rows() {
    let temp = TempDir::new("csharp");
    let path = temp.db();
    let db = TrackerDatabase::open(&path).unwrap();
    let game_json = serde_json::json!({
        "Id": "g1",
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
    })
    .to_string();
    let connection = rusqlite::Connection::open(&path).unwrap();
    connection
        .execute("INSERT INTO Games VALUES('g1','Apex',?1)", [&game_json])
        .unwrap();
    connection
        .execute(
            "INSERT INTO Sessions VALUES('s1','g1',\
             '2026-09-15T12:00:00.0000000+00:00','2026-09-15T12:01:00.0000000+00:00',\
             '2026-09-15T12:01:00.0000000+00:00','Observed',NULL)",
            [],
        )
        .unwrap();
    connection
        .execute(
            "INSERT INTO Segments VALUES('seg1','s1',\
             '2026-09-15T12:00:00.0000000+00:00','2026-09-15T12:00:30.0000000+00:00','ACTIVE')",
            [],
        )
        .unwrap();
    drop(connection);

    let games = db.get_games().unwrap();
    assert_eq!(games.len(), 1);
    assert_eq!(games[0].name, "Apex");
    assert_eq!(games[0].executable_path, r"C:\\Games\\r5apex.exe");

    let sessions = db.get_sessions().unwrap();
    assert_eq!(sessions.len(), 1);
    assert_eq!(sessions[0].segments.len(), 1);
    assert!((sessions[0].active_duration() - 30.0).abs() < 1e-6);
}
