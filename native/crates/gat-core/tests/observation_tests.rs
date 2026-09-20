mod common;

use chrono::Duration;
use common::{obs, parse};
use gat_core::models::ActivityState;
use gat_core::statistics::{StatisticsService, Zone};
use gat_core::tracking::SessionManager;

#[test]
fn sleep_retains_running_but_never_creates_active_time() {
    let t = parse("2026-09-15T12:00:00Z");
    let mut manager = SessionManager::new();
    manager.start("game", t, obs(Some(true), Some(t)));
    manager.advance("game", t + Duration::seconds(10), obs(None, None));
    manager.advance(
        "game",
        t + Duration::hours(2),
        obs(Some(true), Some(t + Duration::hours(2))),
    );
    let session = manager
        .stop(
            "game",
            t + Duration::hours(2) + Duration::seconds(10),
            "Exit",
        )
        .unwrap();
    assert!((session.active_duration() - 20.0).abs() < 1e-6);
    assert!((session.unknown_duration() - 7190.0).abs() < 1e-6);
    assert!((session.running_duration() - 7210.0).abs() < 1e-6);
    assert_eq!(session.segments.len(), 3);
}

#[test]
fn current_streak_continues_across_new_year() {
    let mut manager = SessionManager::new();
    let mut sessions = Vec::new();
    for day in ["2025-12-30", "2025-12-31", "2026-01-01"] {
        let t = parse(&format!("{}T12:00:00Z", day));
        manager.start("game", t, obs(Some(true), Some(t)));
        sessions.push(
            manager
                .stop("game", t + Duration::seconds(30), "Exit")
                .unwrap(),
        );
    }
    let stats = StatisticsService;
    let year = stats.year(&sessions, &Zone::utc(), 2026, parse_date(2026, 1, 1));
    assert_eq!(year.current_streak, 3);
    assert_eq!(year.active_days, 1);
    assert_eq!(year.longest_streak, 1);
}

fn parse_date(year: i32, month: u32, day: u32) -> chrono::NaiveDate {
    chrono::NaiveDate::from_ymd_opt(year, month, day).unwrap()
}

struct Rng(u64);
impl Rng {
    fn next(&mut self) -> u64 {
        let mut x = self.0;
        x ^= x << 13;
        x ^= x >> 7;
        x ^= x << 17;
        self.0 = x;
        x
    }
    fn range(&mut self, low: u64, high: u64) -> u64 {
        low + self.next() % (high - low)
    }
}

#[test]
fn many_transitions_preserve_contiguous_nonnegative_intervals() {
    let mut rng = Rng(871);
    let mut manager = SessionManager::new();
    let start = parse("2026-09-15T12:00:00Z");
    manager.start("game", start, obs(Some(true), Some(start)));
    let mut now = start;
    let mut input = start;
    for _ in 0..2000 {
        now += Duration::milliseconds(rng.range(100, 2000) as i64);
        if rng.range(0, 10) == 0 {
            input = now - Duration::milliseconds(50);
        }
        let foreground = match rng.range(0, 5) {
            0 => Some(false),
            1 => None,
            _ => Some(true),
        };
        manager.advance("game", now, obs(foreground, Some(input)));
    }
    let session = manager.stop("game", now, "Exit").unwrap();
    let expected = (now - start).num_microseconds().unwrap() as f64 / 1_000_000.0;
    assert!((session.running_duration() - expected).abs() < 1e-6);
    let sum = session.active_duration()
        + session.idle_duration()
        + session.background_duration()
        + session.unknown_duration();
    assert!((session.running_duration() - sum).abs() < 1e-6);
    for index in 0..session.segments.len() {
        assert!(session.segments[index].duration() > 0.0);
        if index > 0 {
            assert_eq!(
                session.segments[index - 1].end_time,
                session.segments[index].start_time
            );
        }
    }
    let _ = ActivityState::Unknown;
}
