//! Process enumeration through sysinfo. The command line is reconstructed with quotes so
//! the Minecraft gameDir parser sees the same shape as the original WMI provider.

use chrono::{DateTime, Utc};
use gat_core::presence::ProcessSnapshot;
use sysinfo::{ProcessesToUpdate, System};

pub struct ProcessScanner {
    system: System,
}

impl Default for ProcessScanner {
    fn default() -> Self {
        Self::new()
    }
}

impl ProcessScanner {
    pub fn new() -> Self {
        Self {
            system: System::new(),
        }
    }

    pub fn scan(&mut self) -> Vec<ProcessSnapshot> {
        self.system.refresh_processes(ProcessesToUpdate::All, true);
        self.system
            .processes()
            .iter()
            .map(|(pid, process)| ProcessSnapshot {
                pid: pid.as_u32() as i32,
                executable_name: executable_name(process),
                full_path: process
                    .exe()
                    .map(|path| path.to_string_lossy().into_owned()),
                command_line: command_line(process),
                started_at: started_at(process),
                steam_app_id: None,
            })
            .collect()
    }
}

fn executable_name(process: &sysinfo::Process) -> String {
    if let Some(path) = process.exe() {
        if let Some(name) = path.file_name() {
            return name.to_string_lossy().into_owned();
        }
    }
    let name = process.name().to_string_lossy().into_owned();
    if name.to_lowercase().ends_with(".exe") {
        name
    } else {
        format!("{}.exe", name)
    }
}

fn command_line(process: &sysinfo::Process) -> Option<String> {
    if process.cmd().is_empty() {
        return None;
    }
    let parts: Vec<String> = process
        .cmd()
        .iter()
        .map(|part| {
            let text = part.to_string_lossy().into_owned();
            if text.contains(' ') {
                format!("\"{}\"", text)
            } else {
                text
            }
        })
        .collect();
    Some(parts.join(" "))
}

fn started_at(process: &sysinfo::Process) -> Option<DateTime<Utc>> {
    let seconds = process.start_time();
    if seconds == 0 {
        None
    } else {
        DateTime::from_timestamp(seconds as i64, 0)
    }
}
