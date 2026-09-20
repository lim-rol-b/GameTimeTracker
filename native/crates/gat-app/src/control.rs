//! Local control channel for the lightweight background process.
//!
//! A loopback TCP listener plus a random token written to control.json keeps the channel
//! cross-platform and easy for the WPF client (TcpClient) to speak. The port is ephemeral
//! and bound to 127.0.0.1, and every request must present the token.

use std::collections::VecDeque;
use std::io::{BufRead, BufReader, Write};
use std::net::{SocketAddr, TcpListener, TcpStream};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::JoinHandle;
use std::time::Duration;

use serde::{Deserialize, Serialize};
use serde_json::{json, Value};

use crate::logging::Log;
use gat_data::TrackerDatabase;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CommandKind {
    Reload,
    Suspend,
    Resume,
    Shutdown,
}

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ControlStatus {
    pub running: bool,
    pub suspended: bool,
    pub locked: bool,
    pub error: Option<String>,
    pub idle_threshold_seconds: i64,
    pub games: Vec<LiveGameStatus>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LiveGameStatus {
    pub game_id: String,
    pub name: String,
    pub state: String,
    pub active_seconds: f64,
    pub running_seconds: f64,
}

#[derive(Default)]
pub struct ControlBridge {
    pub status: ControlStatus,
    pub commands: VecDeque<CommandKind>,
}

impl ControlBridge {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn push(&mut self, command: CommandKind) {
        self.commands.push_back(command);
    }

    pub fn take_commands(&mut self) -> Vec<CommandKind> {
        self.commands.drain(..).collect()
    }
}

pub const CONTROL_FILE: &str = "control.json";

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ControlHandshake {
    pub port: u16,
    pub token: String,
    pub pid: u32,
    pub started_at: String,
}

pub fn control_file_path(data_directory: &Path) -> PathBuf {
    data_directory.join(CONTROL_FILE)
}

pub struct ControlServer {
    stop: Arc<AtomicBool>,
    port: u16,
    thread: Option<JoinHandle<()>>,
    file: PathBuf,
}

impl ControlServer {
    pub fn start(
        bridge: Arc<Mutex<ControlBridge>>,
        database: TrackerDatabase,
        log: Arc<dyn Log>,
    ) -> std::io::Result<Self> {
        let listener = TcpListener::bind(("127.0.0.1", 0))?;
        let port = listener.local_addr()?.port();
        let token = gat_core::models::new_id();
        let data_directory = database
            .path()
            .parent()
            .map(|path| path.to_path_buf())
            .unwrap_or_else(|| PathBuf::from("."));
        let file = control_file_path(&data_directory);
        let handshake = ControlHandshake {
            port,
            token: token.clone(),
            pid: std::process::id(),
            started_at: chrono::Utc::now().to_rfc3339(),
        };
        std::fs::write(&file, serde_json::to_vec_pretty(&handshake)?)?;

        let stop = Arc::new(AtomicBool::new(false));
        let thread_stop = stop.clone();
        let thread = std::thread::spawn(move || {
            for stream in listener.incoming() {
                if thread_stop.load(Ordering::SeqCst) {
                    break;
                }
                if let Ok(stream) = stream {
                    let _ = handle_client(stream, &bridge, &database, &token, log.as_ref());
                }
            }
        });

        Ok(Self {
            stop,
            port,
            thread: Some(thread),
            file,
        })
    }
}

impl Drop for ControlServer {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::SeqCst);
        let address = SocketAddr::from(([127, 0, 0, 1], self.port));
        let _ = TcpStream::connect_timeout(&address, Duration::from_millis(200));
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
        let _ = std::fs::remove_file(&self.file);
    }
}

fn handle_client(
    mut stream: TcpStream,
    bridge: &Arc<Mutex<ControlBridge>>,
    database: &TrackerDatabase,
    token: &str,
    log: &dyn Log,
) -> std::io::Result<()> {
    stream.set_read_timeout(Some(Duration::from_secs(5)))?;
    let reader_stream = stream.try_clone()?;
    let mut reader = BufReader::new(reader_stream);
    let mut line = String::new();
    reader.read_line(&mut line)?;
    let request: Value = serde_json::from_str(line.trim()).unwrap_or(Value::Null);
    let provided = request.get("token").and_then(Value::as_str).unwrap_or("");
    if provided != token {
        return write_response(&mut stream, &json!({"ok": false, "error": "unauthorized"}));
    }
    let command = request
        .get("command")
        .and_then(Value::as_str)
        .unwrap_or("ping");
    let response = dispatch(command, bridge, database, log);
    write_response(&mut stream, &response)
}

fn dispatch(
    command: &str,
    bridge: &Arc<Mutex<ControlBridge>>,
    database: &TrackerDatabase,
    log: &dyn Log,
) -> Value {
    match command {
        "ping" => json!({"ok": true, "data": "pong"}),
        "status" => {
            let guard = bridge.lock().unwrap_or_else(|error| error.into_inner());
            json!({"ok": true, "data": guard.status})
        }
        "snapshot" => {
            let guard = bridge.lock().unwrap_or_else(|error| error.into_inner());
            json!({"ok": true, "data": guard.status.games})
        }
        "games" => match database.get_games() {
            Ok(games) => json!({"ok": true, "data": games}),
            Err(error) => json!({"ok": false, "error": error.to_string()}),
        },
        "stats" => match year_summary(database) {
            Ok(value) => json!({"ok": true, "data": value}),
            Err(error) => json!({"ok": false, "error": error.to_string()}),
        },
        "reload" => enqueue(bridge, CommandKind::Reload),
        "suspend" => enqueue(bridge, CommandKind::Suspend),
        "resume" => enqueue(bridge, CommandKind::Resume),
        "shutdown" => enqueue(bridge, CommandKind::Shutdown),
        other => {
            log.write(&format!("Control: unknown command {}", other));
            json!({"ok": false, "error": format!("unknown command: {}", other)})
        }
    }
}

fn enqueue(bridge: &Arc<Mutex<ControlBridge>>, command: CommandKind) -> Value {
    let mut guard = bridge.lock().unwrap_or_else(|error| error.into_inner());
    guard.push(command);
    json!({"ok": true, "data": "accepted"})
}

fn year_summary(database: &TrackerDatabase) -> Result<Value, gat_data::DataError> {
    let sessions = database.get_sessions()?;
    let zone = gat_core::statistics::Zone::Fixed(*chrono::Local::now().offset());
    let year = chrono::Datelike::year(&chrono::Local::now());
    let today = chrono::Local::now().date_naive();
    let statistics = gat_core::statistics::StatisticsService.year(&sessions, &zone, year, today);
    Ok(json!({
        "year": year,
        "activeSeconds": statistics.active_seconds,
        "runningSeconds": statistics.running_seconds,
        "activeDays": statistics.active_days,
        "sessions": statistics.session_count,
        "currentStreak": statistics.current_streak,
        "longestStreak": statistics.longest_streak,
    }))
}

fn write_response(stream: &mut TcpStream, value: &Value) -> std::io::Result<()> {
    let mut text = serde_json::to_string(value).unwrap_or_else(|_| "{\"ok\":false}".to_string());
    text.push('\n');
    stream.write_all(text.as_bytes())?;
    stream.flush()
}

/// Client helper used by the CLI (and documented for the WPF client).
pub fn request(data_directory: &Path, command: &str, payload: Value) -> Result<Value, String> {
    let file = control_file_path(data_directory);
    let text = std::fs::read_to_string(&file).map_err(|_| "后台轻量模式未在运行。".to_string())?;
    let handshake: ControlHandshake =
        serde_json::from_str(&text).map_err(|error| error.to_string())?;
    let address = SocketAddr::from(([127, 0, 0, 1], handshake.port));
    let mut stream = TcpStream::connect_timeout(&address, Duration::from_secs(2))
        .map_err(|error| error.to_string())?;
    stream.set_read_timeout(Some(Duration::from_secs(5))).ok();

    let mut request = json!({"token": handshake.token, "command": command});
    if let Some(object) = payload.as_object() {
        for (key, value) in object {
            request[key] = value.clone();
        }
    }
    let mut text = serde_json::to_string(&request).map_err(|error| error.to_string())?;
    text.push('\n');
    stream
        .write_all(text.as_bytes())
        .map_err(|error| error.to_string())?;
    stream.flush().map_err(|error| error.to_string())?;

    let mut reader = BufReader::new(stream);
    let mut line = String::new();
    reader
        .read_line(&mut line)
        .map_err(|error| error.to_string())?;
    serde_json::from_str(line.trim()).map_err(|error| error.to_string())
}
