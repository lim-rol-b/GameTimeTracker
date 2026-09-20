mod cli;
mod commands;
mod control;
mod daemon;
mod engine;
mod logging;

use std::path::Path;
use std::sync::{Arc, Mutex};

use clap::Parser;
use cli::Cli;
use daemon::{run_headless, Daemon};
use gat_core::models::TrackerSettings;
use gat_data::TrackerDatabase;
use gat_platform::paths;
use logging::{FileLog, Log};

fn main() -> std::process::ExitCode {
    let cli = Cli::parse();
    if let Some(directory) = &cli.data_dir {
        std::env::set_var(paths::DATA_DIR_ENV, directory);
    }
    let data_directory = paths::data_directory();
    match &cli.command {
        Some(command) => match commands::run(command, &data_directory) {
            Ok(()) => std::process::ExitCode::SUCCESS,
            Err(error) => {
                eprintln!("{:#}", error);
                std::process::ExitCode::from(1)
            }
        },
        None => match run_daemon(&cli, &data_directory) {
            Ok(code) => code,
            Err(error) => {
                let message = format!("{:#}", error);
                // The viewer launches the engine without a console, so persist the reason.
                let _ = std::fs::create_dir_all(paths::log_directory());
                let _ = std::fs::write(
                    paths::log_directory().join("startup-error.log"),
                    format!("{}\n", message),
                );
                eprintln!("启动失败：{}", message);
                std::process::ExitCode::from(1)
            }
        },
    }
}

fn run_daemon(cli: &Cli, data_directory: &Path) -> anyhow::Result<std::process::ExitCode> {
    std::fs::create_dir_all(data_directory)?;
    let log: Arc<dyn Log> = if cli.smoke_test {
        Arc::new(logging::NullLog)
    } else {
        Arc::new(FileLog::new(paths::log_directory()))
    };

    let database = TrackerDatabase::open(data_directory.join("activity.db"))?;
    let recovered = database.recover_open_sessions()?;
    log.write(&format!(
        "Startup: recovered {} session(s) at persisted checkpoint",
        recovered
    ));

    let settings = database.get_settings()?;
    database.save_settings(&settings)?;
    let mut daemon_settings = database.get_daemon_settings()?;
    if cli.lightweight {
        daemon_settings.lightweight_mode = true;
    }

    // The engine uses its own mutex; the WPF viewer keeps its separate
    // Local\GameActivityTracker mutex and reads live state over the control channel.
    let Some(_instance) =
        gat_platform::SingleInstance::acquire("Local\\GameActivityTracker.Engine")
    else {
        eprintln!("游戏时长记录器已在运行。");
        return Ok(std::process::ExitCode::SUCCESS);
    };

    apply_startup(&settings);

    let bridge = Arc::new(Mutex::new(control::ControlBridge::new()));
    let _server = control::ControlServer::start(bridge.clone(), database.clone(), log.clone()).ok();

    let mut daemon = Daemon::new(
        database,
        log.clone(),
        settings,
        daemon_settings,
        Some(bridge),
    )?;

    if cli.smoke_test {
        let seconds = cli.smoke_seconds.unwrap_or(5);
        run_headless(&mut daemon, 250, Some(seconds))?;
        if let Some(error) = daemon.error() {
            eprintln!("冒烟测试失败：{}", error);
            return Ok(std::process::ExitCode::from(2));
        }
        println!("冒烟测试通过：数据库、记录循环与正常退出均可用。");
        return Ok(std::process::ExitCode::SUCCESS);
    }

    if cli.headless {
        run_headless(&mut daemon, 250, None)?;
        return Ok(std::process::ExitCode::SUCCESS);
    }

    log.write("Starting native tray background mode");
    gat_platform::run_tray(Box::new(daemon), 250)?;
    log.write("Native tray background mode exited");
    Ok(std::process::ExitCode::SUCCESS)
}

fn apply_startup(settings: &TrackerSettings) {
    if settings.start_with_windows {
        if let Ok(executable) = std::env::current_exe() {
            let command = format!("\"{}\" --background", executable.display());
            let _ = gat_platform::registry::set_autostart(&command);
        }
    } else {
        let _ = gat_platform::registry::remove_autostart();
    }
}
