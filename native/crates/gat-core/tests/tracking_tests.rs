mod common;

use chrono::{Duration, Utc};
use common::{obs, parse};
use gat_core::models::{ActivityState, GameProcessRule, TrackerSettings};
use gat_core::presence::{GameMatcher, ProcessSnapshot};
use gat_core::tracking::{ActivityDetector, ControllerFilter, ControllerSample, SessionManager};
use std::time::Duration as StdDuration;

fn base() -> chrono::DateTime<Utc> {
    parse("2026-09-15T12:00:00Z")
}

#[test]
fn idle_threshold() {
    let threshold = StdDuration::from_secs(60);
    let cases: [(f64, ActivityState); 4] = [
        (0.0, ActivityState::Active),
        (59.999, ActivityState::Active),
        (60.0, ActivityState::Idle),
        (120.0, ActivityState::Idle),
    ];
    for (elapsed, expected) in cases {
        let now = base() + Duration::milliseconds((elapsed * 1000.0).round() as i64);
        assert_eq!(
            ActivityDetector::detect(now, obs(Some(true), Some(base())), threshold),
            expected
        );
    }
}

#[test]
fn foreground_is_required_even_when_other_apps_receive_input() {
    let threshold = StdDuration::from_secs(60);
    assert_eq!(
        ActivityDetector::detect(base(), obs(Some(false), Some(base())), threshold),
        ActivityState::Background
    );
    assert_eq!(
        ActivityDetector::detect(base(), obs(None, Some(base())), threshold),
        ActivityState::Unknown
    );
    assert_eq!(
        ActivityDetector::detect(base(), obs(Some(true), None), threshold),
        ActivityState::Unknown
    );
}

#[test]
fn idle_returns_active_immediately_after_input() {
    let t = base();
    let mut manager = SessionManager::new();
    manager.start("a", t, obs(Some(true), Some(t)));
    manager.advance("a", t + Duration::seconds(80), obs(Some(true), Some(t)));
    assert_eq!(manager.state("a"), ActivityState::Idle);
    manager.advance(
        "a",
        t + Duration::seconds(81),
        obs(Some(true), Some(t + Duration::milliseconds(80500))),
    );
    assert_eq!(manager.state("a"), ActivityState::Active);
    let session = manager
        .stop("a", t + Duration::seconds(90), "Exit")
        .unwrap();
    assert!((session.active_duration() - 69.5).abs() < 1e-5);
    assert!((session.idle_duration() - 20.5).abs() < 1e-5);
    assert!((session.running_duration() - 90.0).abs() < 1e-5);
}

#[test]
fn threshold_splits_between_sampling_instants() {
    let t = base();
    let mut manager = SessionManager::new();
    manager.start("a", t, obs(Some(true), Some(t)));
    manager.advance(
        "a",
        t + Duration::milliseconds(59700),
        obs(Some(true), Some(t)),
    );
    manager.advance(
        "a",
        t + Duration::milliseconds(60200),
        obs(Some(true), Some(t)),
    );
    let session = manager
        .stop("a", t + Duration::seconds(70), "Exit")
        .unwrap();
    assert!((session.active_duration() - 60.0).abs() < 1e-6);
    assert!((session.idle_duration() - 10.0).abs() < 1e-6);
    assert_eq!(session.segments.len(), 2);
}

#[test]
fn input_before_old_deadline_extends_active_window() {
    let t = base();
    let mut manager = SessionManager::new();
    manager.start("a", t, obs(Some(true), Some(t)));
    manager.advance("a", t + Duration::seconds(59), obs(Some(true), Some(t)));
    manager.advance(
        "a",
        t + Duration::seconds(61),
        obs(Some(true), Some(t + Duration::milliseconds(59500))),
    );
    let session = manager
        .stop("a", t + Duration::seconds(65), "Exit")
        .unwrap();
    assert!((session.active_duration() - 65.0).abs() < 1e-6);
    assert!((session.idle_duration()).abs() < 1e-6);
}

#[test]
fn foreground_background_foreground_transitions() {
    let t = base();
    let mut manager = SessionManager::new();
    manager.start("a", t, obs(Some(true), Some(t)));
    manager.advance("a", t + Duration::seconds(10), obs(Some(false), Some(t)));
    assert_eq!(manager.state("a"), ActivityState::Background);
    manager.advance(
        "a",
        t + Duration::seconds(30),
        obs(Some(true), Some(t + Duration::seconds(29))),
    );
    assert_eq!(manager.state("a"), ActivityState::Active);
    let session = manager
        .stop("a", t + Duration::seconds(40), "Exit")
        .unwrap();
    assert!((session.active_duration() - 20.0).abs() < 1e-6);
    assert!((session.background_duration() - 20.0).abs() < 1e-6);
}

#[test]
fn returning_to_foreground_without_recent_input_is_idle() {
    let t = base();
    let mut manager = SessionManager::new();
    manager.start("a", t, obs(Some(false), Some(t)));
    manager.advance("a", t + Duration::seconds(90), obs(Some(true), Some(t)));
    assert_eq!(manager.state("a"), ActivityState::Idle);
    let session = manager
        .stop("a", t + Duration::seconds(100), "Exit")
        .unwrap();
    assert!((session.background_duration() - 90.0).abs() < 1e-6);
    assert!((session.idle_duration() - 10.0).abs() < 1e-6);
}

#[test]
fn session_start_is_idempotent_and_end_removes_it() {
    let t = base();
    let mut manager = SessionManager::new();
    let first_id = manager.start("a", t, obs(Some(true), Some(t))).id.clone();
    let second_id = manager
        .start("a", t + Duration::seconds(2), obs(Some(true), Some(t)))
        .id
        .clone();
    assert_eq!(first_id, second_id);
    assert_eq!(manager.sessions().len(), 1);
    let session = manager
        .stop("a", t + Duration::seconds(10), "Exit")
        .unwrap();
    assert_eq!(session.end_time, Some(t + Duration::seconds(10)));
    assert!(manager.sessions().is_empty());
    assert!(manager
        .stop("a", t + Duration::seconds(20), "Exit")
        .is_none());
}

#[test]
fn suspend_and_resume_have_no_unobserved_gap() {
    let t = base();
    let mut manager = SessionManager::new();
    manager.start("a", t, obs(Some(true), Some(t)));
    let first = manager.stop_all(t + Duration::seconds(10), "Suspend");
    manager.start(
        "a",
        t + Duration::hours(2),
        obs(Some(true), Some(t + Duration::hours(2))),
    );
    let second = manager
        .stop("a", t + Duration::hours(2) + Duration::seconds(10), "Exit")
        .unwrap();
    assert_eq!(first.len(), 1);
    assert!((first[0].running_duration() + second.running_duration() - 20.0).abs() < 1e-6);
}

#[test]
fn backwards_clock_does_not_make_negative_intervals() {
    let t = base();
    let mut manager = SessionManager::new();
    manager.start("a", t, obs(Some(true), Some(t)));
    manager.advance("a", t + Duration::seconds(10), obs(Some(true), Some(t)));
    let session = manager
        .stop("a", t - Duration::seconds(10), "Exit")
        .unwrap();
    assert_eq!(session.end_time, Some(t + Duration::seconds(10)));
    assert!((session.running_duration() - 10.0).abs() < 1e-6);
}

#[test]
fn multiple_games_cannot_share_foreground_activity() {
    let t = base();
    let mut manager = SessionManager::new();
    manager.start("a", t, obs(Some(true), Some(t)));
    manager.start("b", t, obs(Some(false), Some(t)));
    let sessions = manager.stop_all(t + Duration::seconds(20), "Exit");
    let active: f64 = sessions.iter().map(|s| s.active_duration()).sum();
    let running: f64 = sessions.iter().map(|s| s.running_duration()).sum();
    assert!((active - 20.0).abs() < 1e-6);
    assert!((running - 40.0).abs() < 1e-6);
}

#[test]
fn dead_zone_suppresses_stick_and_trigger_noise() {
    let a = ControllerSample {
        buttons: 0,
        lx: 1,
        ly: -2,
        rx: 4,
        ry: -3,
        lt: 1,
        rt: 2,
    };
    let b = ControllerSample {
        buttons: 0,
        lx: 150,
        ly: -120,
        rx: 50,
        ry: 80,
        lt: 15,
        rt: 10,
    };
    assert!(!ControllerFilter::has_meaningful_change(a, b, 0.2));
    assert!(ControllerFilter::has_meaningful_change(
        a,
        ControllerSample { lx: 18000, ..b },
        0.2
    ));
    assert!(ControllerFilter::has_meaningful_change(
        a,
        ControllerSample {
            buttons: 0x1000,
            ..b
        },
        0.2
    ));
    assert!(ControllerFilter::has_meaningful_change(
        a,
        ControllerSample { rt: 100, ..b },
        0.2
    ));
    let c = ControllerSample { lx: 20000, ..b };
    assert!(!ControllerFilter::has_meaningful_change(c, c, 0.2));
}

#[test]
fn stick_release_counts_as_change_and_negative_axis_does_not_overflow() {
    let extreme = ControllerSample {
        buttons: 0,
        lx: i16::MIN,
        ly: 0,
        rx: 0,
        ry: 0,
        lt: 0,
        rt: 0,
    };
    assert!(ControllerFilter::has_meaningful_change(
        extreme,
        ControllerSample::default(),
        0.2
    ));
    assert_eq!(ControllerFilter::normalize(extreme, 0.2).lx, i16::MIN);
}

#[test]
fn matcher_uses_conjunction_and_never_guesses_missing_metadata() {
    let t = base();
    let rule = GameProcessRule {
        game_id: "minecraft".to_string(),
        executable_name: "javaw.exe".to_string(),
        command_line_contains: Some("minecraft".to_string()),
        path_contains: Some("java".to_string()),
        ..Default::default()
    };
    let process = ProcessSnapshot::new(
        1,
        "javaw.exe",
        Some(r"C:\Java\bin\javaw.exe".to_string()),
        Some("-cp minecraft".to_string()),
        Some(t),
    );
    let matcher = GameMatcher;
    assert_eq!(
        matcher.match_process(&process, std::slice::from_ref(&rule)),
        Some("minecraft".to_string())
    );
    let mut other = process.clone();
    other.command_line = Some("other.jar".to_string());
    assert_eq!(
        matcher.match_process(&other, std::slice::from_ref(&rule)),
        None
    );
    let mut missing_command = process.clone();
    missing_command.command_line = None;
    assert_eq!(
        matcher.match_process(&missing_command, std::slice::from_ref(&rule)),
        None
    );
    let mut missing_path = process.clone();
    missing_path.full_path = None;
    assert_eq!(matcher.match_process(&missing_path, &[rule]), None);
}

#[test]
fn exact_path_priority_and_disabled_rules() {
    let process = ProcessSnapshot::new(
        1,
        "GAME.EXE",
        Some(r"C:\Games\game.exe".to_string()),
        None,
        None,
    );
    let low = GameProcessRule {
        game_id: "low".to_string(),
        executable_name: "game.exe".to_string(),
        ..Default::default()
    };
    let high = GameProcessRule {
        game_id: "high".to_string(),
        executable_name: "game.exe".to_string(),
        executable_path: Some(r"c:\games\GAME.EXE".to_string()),
        priority: 10,
        ..Default::default()
    };
    let matcher = GameMatcher;
    assert_eq!(
        matcher.match_process(&process, &[low.clone(), high.clone()]),
        Some("high".to_string())
    );
    let disabled = GameProcessRule {
        enabled: false,
        ..high.clone()
    };
    assert_eq!(
        matcher.match_process(&process, &[low.clone(), disabled]),
        Some("low".to_string())
    );
    let mut elsewhere = process.clone();
    elsewhere.full_path = Some(r"C:\Other\game.exe".to_string());
    assert_eq!(
        matcher.match_process(&elsewhere, &[low, high]),
        Some("low".to_string())
    );
}

#[test]
fn settings_reject_unsafe_values() {
    assert!(TrackerSettings {
        idle_threshold_seconds: 0,
        ..Default::default()
    }
    .validate()
    .is_err());
    assert!(TrackerSettings {
        controller_dead_zone: f64::NAN,
        ..Default::default()
    }
    .validate()
    .is_err());
    assert!(TrackerSettings {
        process_scan_interval_seconds: 0,
        ..Default::default()
    }
    .validate()
    .is_err());
}
