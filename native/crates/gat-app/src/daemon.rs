//! The lightweight tracking daemon. Single-writer, single-threaded while ticking, with
//! a control bridge serviced from the loopback control channel.

use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use anyhow::Result;
use chrono::{DateTime, Utc};
use gat_core::models::{Game, GameProcessRule, GameSession, TrackerSettings};
use gat_core::tracking::{ActivityObservation, SessionManager};
use gat_data::{DaemonSettings, TrackerDatabase};
use gat_platform::{controller::ControllerProvider, foreground, input, TrayHost};

use crate::control::{CommandKind, ControlBridge, ControlStatus, LiveGameStatus};
use crate::engine::{PresenceEngine, PresenceEvent};
use crate::logging::Log;

pub struct Daemon {
    database: TrackerDatabase,
    log: Arc<dyn Log>,
    settings: TrackerSettings,
    daemon_settings: DaemonSettings,
    rules: Vec<GameProcessRule>,
    games: Vec<Game>,
    sessions: SessionManager,
    presence: PresenceEngine,
    controller: ControllerProvider,
    pending: Vec<GameSession>,
    bridge: Option<Arc<Mutex<ControlBridge>>>,
    ui_command: Option<String>,
    last_tick: DateTime<Utc>,
    last_mono: Instant,
    last_scan: Option<DateTime<Utc>>,
    last_save: Option<DateTime<Utc>>,
    suspended: bool,
    locked: bool,
    last_input: Option<DateTime<Utc>>,
    error: Option<String>,
    exit_requested: bool,
}

impl Daemon {
    pub fn new(
        database: TrackerDatabase,
        log: Arc<dyn Log>,
        settings: TrackerSettings,
        daemon_settings: DaemonSettings,
        bridge: Option<Arc<Mutex<ControlBridge>>>,
    ) -> Result<Self> {
        let rules = database.get_rules()?;
        let games = database.get_games()?;
        let mut sessions = SessionManager::new();
        sessions.set_idle_threshold(settings.idle_threshold());
        let now = Utc::now();
        Ok(Self {
            database,
            log,
            settings,
            daemon_settings,
            rules,
            games,
            sessions,
            presence: PresenceEngine::new(),
            controller: ControllerProvider::new(),
            pending: Vec::new(),
            bridge,
            ui_command: resolve_ui_command(),
            last_tick: now,
            last_mono: Instant::now(),
            last_scan: None,
            last_save: None,
            suspended: false,
            locked: false,
            last_input: None,
            error: None,
            exit_requested: false,
        })
    }

    pub fn error(&self) -> Option<&str> {
        self.error.as_deref()
    }

    fn scan_interval_seconds(&self) -> i64 {
        if self.daemon_settings.lightweight_mode {
            self.settings
                .process_scan_interval_seconds
                .max(self.daemon_settings.process_scan_seconds)
        } else {
            self.settings.process_scan_interval_seconds
        }
    }

    fn tick(&mut self) -> Result<()> {
        let now = Utc::now();
        let elapsed = self.last_mono.elapsed().as_secs_f64();
        let wall = (now - self.last_tick).num_microseconds().unwrap_or(0) as f64 / 1_000_000.0;
        let wall_out_of_range = !(0.0..=10.0).contains(&wall);
        if elapsed > 10.0 || wall_out_of_range || (wall - elapsed).abs() > 2.0 {
            if wall < 0.0 {
                let last_tick = self.last_tick;
                self.end_all(last_tick, "ClockMovedBackwards");
            } else {
                let ids: Vec<String> = self
                    .sessions
                    .sessions()
                    .iter()
                    .map(|session| session.game_id.clone())
                    .collect();
                let last_tick = self.last_tick;
                for id in &ids {
                    self.sessions
                        .advance(id, last_tick, ActivityObservation::new(None, None));
                    self.sessions
                        .advance(id, now, ActivityObservation::new(None, None));
                }
                self.controller.reset();
            }
            self.last_scan = None;
        }

        self.last_tick = now;
        self.last_mono = Instant::now();

        let keyboard = input::last_input(now);
        let controller = if self.settings.enable_controller_detection {
            self.controller
                .poll(now, self.settings.controller_dead_zone)
        } else {
            None
        };
        self.last_input = match (keyboard, controller) {
            (Some(a), Some(b)) => Some(a.max(b)),
            (Some(a), None) => Some(a),
            (None, Some(b)) => Some(b),
            (None, None) => None,
        };

        if self
            .last_scan
            .map(|last| (now - last).num_seconds() >= self.scan_interval_seconds())
            .unwrap_or(true)
        {
            self.scan(now);
            self.last_scan = Some(now);
        }

        let ids: Vec<String> = self
            .sessions
            .sessions()
            .iter()
            .map(|session| session.game_id.clone())
            .collect();
        for id in ids {
            let old = self.sessions.state(&id);
            let observation = self.observe(&id, now);
            self.sessions.advance(&id, now, observation);
            let new = self.sessions.state(&id);
            if old != new {
                self.log
                    .write(&format!("{}: {} -> {}", id, old.as_str(), new.as_str()));
            }
        }

        if self
            .last_save
            .map(|last| (now - last).num_seconds() >= 5)
            .unwrap_or(true)
            || !self.pending.is_empty()
        {
            self.save(now);
        }

        self.process_commands();
        self.publish_status();
        Ok(())
    }

    fn scan(&mut self, now: DateTime<Utc>) {
        let allow_expansion = !self.daemon_settings.lightweight_mode;
        let events = self.presence.scan(
            now,
            &self.rules,
            &self.games,
            self.log.as_ref(),
            allow_expansion,
            Duration::from_secs(120),
        );
        for event in events {
            match event {
                PresenceEvent::Started { game_id, pids, at } => {
                    let observation = self.observe(&game_id, at);
                    self.sessions.start_with_source(
                        &game_id,
                        at,
                        observation,
                        "Observed; no pre-detection activity inferred",
                    );
                    self.log.write(&format!(
                        "Session started: {} ({} process(es))",
                        game_id,
                        pids.len()
                    ));
                }
                PresenceEvent::Stopped { game_id, at } => {
                    if let Some(session) = self.sessions.stop(&game_id, at, "ProcessExited") {
                        self.pending.push(session);
                    }
                }
            }
        }
    }

    fn observe(&self, game_id: &str, _now: DateTime<Utc>) -> ActivityObservation {
        if self.locked {
            return ActivityObservation::new(Some(false), None);
        }
        if self.suspended {
            return ActivityObservation::new(None, None);
        }
        let foreground = foreground::foreground_process_id();
        let is_foreground = foreground.map(|pid| {
            self.presence
                .running_pids(game_id)
                .map(|pids| pids.contains(&pid))
                .unwrap_or(false)
        });
        ActivityObservation::new(is_foreground, self.last_input)
    }

    fn save(&mut self, now: DateTime<Utc>) {
        let mut all: Vec<GameSession> = self.pending.clone();
        all.extend(self.sessions.sessions().into_iter().cloned());
        if let Err(error) = self.database.save_sessions(&all) {
            self.log
                .write(&format!("Checkpoint save failed: {}", error));
        }
        self.pending.clear();
        self.last_save = Some(now);
    }

    fn end_all(&mut self, now: DateTime<Utc>, reason: &str) {
        self.pending.extend(self.sessions.stop_all(now, reason));
        self.presence.reset();
        self.controller.reset();
        self.save(now);
    }

    fn suspend(&mut self, reason: &str) {
        self.suspended = true;
        let now = Utc::now();
        let ids: Vec<String> = self
            .sessions
            .sessions()
            .iter()
            .map(|session| session.game_id.clone())
            .collect();
        for id in &ids {
            self.sessions
                .advance(id, now, ActivityObservation::new(None, None));
        }
        self.save(now);
        self.log.write(&format!(
            "{}: observation paused; subsequent time is UNKNOWN",
            reason
        ));
    }

    fn resume(&mut self) {
        let now = Utc::now();
        let ids: Vec<String> = self
            .sessions
            .sessions()
            .iter()
            .map(|session| session.game_id.clone())
            .collect();
        for id in &ids {
            self.sessions
                .advance(id, now, ActivityObservation::new(None, None));
        }
        self.suspended = false;
        self.last_tick = now;
        self.last_mono = Instant::now();
        self.last_scan = None;
        self.controller.reset();
    }

    fn set_locked(&mut self, locked: bool) {
        self.locked = locked;
        let now = Utc::now();
        let ids: Vec<String> = self
            .sessions
            .sessions()
            .iter()
            .map(|session| session.game_id.clone())
            .collect();
        for id in &ids {
            let observation =
                ActivityObservation::new(if locked { Some(false) } else { None }, None);
            self.sessions.advance(id, now, observation);
        }
        self.controller.reset();
    }

    pub fn reload(&mut self) -> Result<()> {
        let now = Utc::now();
        let ids: Vec<String> = self
            .sessions
            .sessions()
            .iter()
            .map(|session| session.game_id.clone())
            .collect();
        for id in &ids {
            let observation = self.observe(id, now);
            self.sessions.advance(id, now, observation);
        }
        self.save(now);
        self.settings = self.database.get_settings()?;
        self.rules = self.database.get_rules()?;
        self.games = self.database.get_games()?;
        // Keep the Run key pointed at the native engine, which owns the only tray icon.
        crate::apply_startup(&self.settings);
        self.sessions
            .set_idle_threshold(self.settings.idle_threshold());
        self.controller.reset();
        self.last_scan = None;
        Ok(())
    }

    #[allow(dead_code)]
    pub fn delete_game(&mut self, game_id: &str) -> Result<()> {
        let now = Utc::now();
        if let Some(session) = self.sessions.stop(game_id, now, "GameDeleted") {
            self.pending.push(session);
        }
        self.save(now);
        self.database.delete_game(game_id)?;
        self.rules = self.database.get_rules()?;
        self.games = self.database.get_games()?;
        self.last_scan = None;
        Ok(())
    }

    fn process_commands(&mut self) {
        let Some(bridge) = &self.bridge else {
            return;
        };
        let commands = {
            let mut guard = bridge.lock().unwrap_or_else(|error| error.into_inner());
            guard.take_commands()
        };
        for command in commands {
            match command {
                CommandKind::Reload => {
                    if let Err(error) = self.reload() {
                        self.log.write(&format!("Reload failed: {}", error));
                    }
                }
                CommandKind::Suspend => self.suspend("ControlSuspend"),
                CommandKind::Resume => self.resume(),
                CommandKind::Shutdown => self.exit_requested = true,
            }
        }
    }

    fn publish_status(&mut self) {
        let Some(bridge) = &self.bridge else {
            return;
        };
        let mut games = Vec::new();
        for session in self.sessions.sessions() {
            let name = self
                .games
                .iter()
                .find(|game| game.id == session.game_id)
                .map(|game| game.name.clone())
                .unwrap_or_else(|| session.game_id.clone());
            games.push(LiveGameStatus {
                game_id: session.game_id.clone(),
                name,
                state: self.sessions.state(&session.game_id).as_str().to_string(),
                active_seconds: session.active_duration(),
                running_seconds: session.running_duration(),
            });
        }
        let status = ControlStatus {
            running: true,
            suspended: self.suspended,
            locked: self.locked,
            error: self.error.clone(),
            idle_threshold_seconds: self.settings.idle_threshold_seconds,
            games,
        };
        let mut guard = bridge.lock().unwrap_or_else(|error| error.into_inner());
        guard.status = status;
    }

    fn open_ui(&mut self) {
        if let Some(command) = &self.ui_command {
            match std::process::Command::new(command).spawn() {
                Ok(_) => self.log.write("Opened WPF interface"),
                Err(error) => self
                    .log
                    .write(&format!("Failed to open interface: {}", error)),
            }
        } else {
            self.log
                .write("No GameActivityTracker.exe found next to gat; skipping interface launch");
        }
    }
}

impl TrayHost for Daemon {
    fn on_tick(&mut self) {
        match self.tick() {
            Ok(()) => self.error = None,
            Err(error) => {
                self.error = Some(error.to_string());
                self.log
                    .write(&format!("Tracking tick failed; will retry: {}", error));
            }
        }
    }

    fn on_open(&mut self) {
        self.open_ui();
    }

    fn on_exit(&mut self) {
        self.log
            .write("Application exit requested; finalizing sessions");
        self.end_all(Utc::now(), "TrackerExit");
        self.exit_requested = true;
    }

    fn on_suspend(&mut self) {
        self.suspend("SystemSuspend");
    }

    fn on_resume(&mut self) {
        self.resume();
    }

    fn on_lock(&mut self, locked: bool) {
        self.set_locked(locked);
    }

    fn should_exit(&self) -> bool {
        self.exit_requested
    }
}

pub fn run_headless(daemon: &mut Daemon, tick_ms: u64, max_seconds: Option<u64>) -> Result<()> {
    let start = Instant::now();
    loop {
        std::thread::sleep(Duration::from_millis(tick_ms));
        daemon.on_tick();
        if daemon.should_exit() {
            break;
        }
        if let Some(max) = max_seconds {
            if start.elapsed().as_secs() >= max {
                break;
            }
        }
    }
    daemon.on_exit();
    Ok(())
}

fn resolve_ui_command() -> Option<String> {
    if let Ok(command) = std::env::var("GAT_UI_COMMAND") {
        if !command.trim().is_empty() {
            return Some(command);
        }
    }
    let exe = std::env::current_exe().ok()?;
    let candidate = exe.parent()?.join("GameActivityTracker.exe");
    if candidate.exists() {
        Some(candidate.to_string_lossy().into_owned())
    } else {
        None
    }
}
