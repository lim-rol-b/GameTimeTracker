use std::path::Path;

use anyhow::{bail, Context, Result};
use chrono::Datelike;

use crate::cli::Command;
use crate::control;
use gat_core::statistics::{StatisticsService, Zone};
use gat_data::TrackerDatabase;

pub fn run(command: &Command, data_directory: &Path) -> Result<()> {
    match command {
        Command::Status => simple(request("status", data_directory)?),
        Command::Snapshot => simple(request("snapshot", data_directory)?),
        Command::Reload => simple(request("reload", data_directory)?),
        Command::Suspend => simple(request("suspend", data_directory)?),
        Command::Resume => simple(request("resume", data_directory)?),
        Command::Shutdown => simple(request("shutdown", data_directory)?),
        Command::Games => list_games(data_directory),
        Command::Stats => print_stats(data_directory),
    }
}

fn request(command: &str, data_directory: &Path) -> Result<serde_json::Value> {
    control::request(data_directory, command, serde_json::json!({}))
        .map_err(|error| anyhow::anyhow!(error))
}

fn simple(value: serde_json::Value) -> Result<()> {
    if value.get("ok").and_then(|ok| ok.as_bool()) == Some(true) {
        let data = value
            .get("data")
            .cloned()
            .unwrap_or(serde_json::Value::Null);
        println!("{}", serde_json::to_string_pretty(&data)?);
        Ok(())
    } else {
        bail!(
            "{}",
            value
                .get("error")
                .and_then(|error| error.as_str())
                .unwrap_or("未知错误")
        )
    }
}

fn list_games(data_directory: &Path) -> Result<()> {
    let database = open(data_directory)?;
    let games = database.get_games()?;
    if games.is_empty() {
        println!("尚未登记任何游戏。");
        return Ok(());
    }
    for game in games {
        let executable = if game.executable_path.is_empty() {
            game.executable.clone()
        } else {
            game.executable_path.clone()
        };
        println!("{}	{}	{}", game.id, game.name, executable);
    }
    Ok(())
}

fn print_stats(data_directory: &Path) -> Result<()> {
    let database = open(data_directory)?;
    let sessions = database.get_sessions()?;
    let zone = Zone::Fixed(*chrono::Local::now().offset());
    let year = chrono::Local::now().year();
    let today = chrono::Local::now().date_naive();
    let statistics = StatisticsService.year(&sessions, &zone, year, today);
    println!(
        "{} 年：活跃 {:.1} 小时 / 运行 {:.1} 小时，活跃天数 {}，会话 {}，当前连续 {} 天，最长连续 {} 天",
        year,
        statistics.active_seconds.max(0.0) / 3600.0,
        statistics.running_seconds.max(0.0) / 3600.0,
        statistics.active_days,
        statistics.session_count,
        statistics.current_streak,
        statistics.longest_streak
    );
    Ok(())
}

fn open(data_directory: &Path) -> Result<TrackerDatabase> {
    TrackerDatabase::open(data_directory.join("activity.db")).context("无法打开数据库")
}
