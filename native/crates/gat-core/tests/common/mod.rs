#![allow(dead_code)]

use std::path::PathBuf;

use chrono::{DateTime, Utc};
use gat_core::tracking::ActivityObservation;

pub fn parse(value: &str) -> DateTime<Utc> {
    DateTime::parse_from_rfc3339(value)
        .unwrap()
        .with_timezone(&Utc)
}

pub fn obs(is_foreground: Option<bool>, last_input: Option<DateTime<Utc>>) -> ActivityObservation {
    ActivityObservation::new(is_foreground, last_input)
}

pub struct TempDir {
    pub path: PathBuf,
}

impl TempDir {
    pub fn new(label: &str) -> Self {
        let path =
            std::env::temp_dir().join(format!("gat-test-{}-{}", label, gat_core::models::new_id()));
        std::fs::create_dir_all(&path).unwrap();
        Self { path }
    }
}

impl Drop for TempDir {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.path);
    }
}

pub fn temp_path(label: &str) -> PathBuf {
    std::env::temp_dir().join(format!("gat-path-{}-{}", label, gat_core::models::new_id()))
}
