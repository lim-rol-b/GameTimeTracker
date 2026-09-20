//! Bounded file logging that never grows without limit.

use std::fs;
use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::Mutex;

pub trait Log: Send + Sync {
    fn write(&self, message: &str);
}

pub struct NullLog;

impl Log for NullLog {
    fn write(&self, _message: &str) {}
}

pub struct FileLog {
    directory: PathBuf,
    gate: Mutex<()>,
}

impl FileLog {
    pub fn new(directory: impl AsRef<Path>) -> Self {
        Self {
            directory: directory.as_ref().to_path_buf(),
            gate: Mutex::new(()),
        }
    }
}

impl Log for FileLog {
    fn write(&self, message: &str) {
        let _guard = self.gate.lock().unwrap_or_else(|error| error.into_inner());
        let _ = fs::create_dir_all(&self.directory);
        let now = chrono::Local::now();
        let path = self
            .directory
            .join(format!("gat-{}.log", now.format("%Y-%m-%d")));
        if let Ok(metadata) = fs::metadata(&path) {
            if metadata.len() > 5_000_000 {
                return;
            }
        }
        if let Ok(mut file) = fs::OpenOptions::new().create(true).append(true).open(&path) {
            let _ = writeln!(file, "[{}] {}", now.to_rfc3339(), message);
        }
        if let Ok(entries) = fs::read_dir(&self.directory) {
            let cutoff = std::time::Duration::from_secs(30 * 24 * 3600);
            for entry in entries.flatten() {
                let path = entry.path();
                if path.extension().and_then(|value| value.to_str()) != Some("log") {
                    continue;
                }
                if let Ok(metadata) = entry.metadata() {
                    if let Ok(modified) = metadata.modified() {
                        if let Ok(age) = std::time::SystemTime::now().duration_since(modified) {
                            if age > cutoff {
                                let _ = fs::remove_file(&path);
                            }
                        }
                    }
                }
            }
        }
    }
}
