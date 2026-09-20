//! Data directory resolution. An explicit override keeps smoke tests isolated.

use std::path::PathBuf;

pub const DATA_DIR_ENV: &str = "GAT_DATA_DIR";

pub fn data_directory() -> PathBuf {
    if let Some(value) = std::env::var_os(DATA_DIR_ENV) {
        if !value.is_empty() {
            return PathBuf::from(value);
        }
    }
    if let Some(local) = std::env::var_os("LOCALAPPDATA") {
        return PathBuf::from(local).join("GameActivityTracker");
    }
    if let Some(home) = std::env::var_os("HOME") {
        return PathBuf::from(home).join(".local/share/GameActivityTracker");
    }
    std::env::temp_dir().join("GameActivityTracker")
}

pub fn database_path() -> PathBuf {
    data_directory().join("activity.db")
}

pub fn log_directory() -> PathBuf {
    data_directory().join("logs")
}
