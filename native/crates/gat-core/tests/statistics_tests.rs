mod common;

use chrono::NaiveDate;
use common::parse;
use gat_core::models::{ActivitySegment, ActivityState, GameSession};
use gat_core::statistics::{HourlyStatisticsService, StatisticsService, TrackingYears, Zone};

fn session(start: &str, end: &str, state: ActivityState) -> GameSession {
    let start_time = parse(start);
    let end_time = parse(end);
    let mut value = GameSession {
        game_id: "game".to_string(),
        start_time,
        ..Default::default()
    };
    value.end_time = Some(end_time);
    value.segments.push(ActivitySegment {
        session_id: value.id.clone(),
        start_time,
        end_time,
        state,
        ..Default::default()
    });
    value
}

fn date(year: i32, month: u32, day: u32) -> NaiveDate {
    NaiveDate::from_ymd_opt(year, month, day).unwrap()
}

#[test]
fn cross_midnight_uses_local_dates() {
    let zone = Zone::fixed(8 * 3600);
    let days = StatisticsService.daily(
        &[session(
            "2026-09-15T15:30:00Z",
            "2026-09-15T17:30:00Z",
            ActivityState::Active,
        )],
        &zone,
    );
    assert_eq!(days.len(), 2);
    assert_eq!(days[&date(2026, 9, 15)].active_seconds, 1800.0);
    assert_eq!(days[&date(2026, 9, 16)].active_seconds, 5400.0);
}

#[test]
fn negative_offset_uses_previous_calendar_day() {
    let zone = Zone::fixed(-7 * 3600);
    let days = StatisticsService.daily(
        &[session(
            "2026-01-01T01:00:00Z",
            "2026-01-01T02:00:00Z",
            ActivityState::Active,
        )],
        &zone,
    );
    assert_eq!(days.len(), 1);
    assert_eq!(days.keys().next(), Some(&date(2025, 12, 31)));
}

#[test]
fn daylight_saving_day_has_23_hours() {
    let zone = Zone::named(chrono_tz::America::New_York);
    let days = StatisticsService.daily(
        &[session(
            "2026-03-08T05:00:00Z",
            "2026-03-09T04:00:00Z",
            ActivityState::Active,
        )],
        &zone,
    );
    assert_eq!(days.len(), 1);
    assert_eq!(days.values().next().unwrap().active_seconds, 23.0 * 3600.0);
}

#[test]
fn fall_back_day_has_25_hours() {
    let zone = Zone::named(chrono_tz::America::New_York);
    let days = StatisticsService.daily(
        &[session(
            "2026-11-01T04:00:00Z",
            "2026-11-02T05:00:00Z",
            ActivityState::Active,
        )],
        &zone,
    );
    assert_eq!(days.len(), 1);
    assert_eq!(days.values().next().unwrap().active_seconds, 25.0 * 3600.0);
}

#[test]
fn heatmap_aggregates_only_active_but_running_includes_all_states() {
    let active = session(
        "2026-09-15T12:00:00Z",
        "2026-09-15T13:00:00Z",
        ActivityState::Active,
    );
    let idle = session(
        "2026-09-15T13:00:00Z",
        "2026-09-15T13:30:00Z",
        ActivityState::Idle,
    );
    let background = session(
        "2026-09-15T13:30:00Z",
        "2026-09-15T14:00:00Z",
        ActivityState::Background,
    );
    let days = StatisticsService.daily(&[active, idle, background], &Zone::utc());
    let day = days.values().next().unwrap();
    assert_eq!(day.active_seconds, 3600.0);
    assert_eq!(day.running_seconds, 7200.0);
    assert_eq!(day.game_active_seconds["game"], 3600.0);
}

#[test]
fn heatmap_levels() {
    let cases = [
        (0.0, 0),
        (1.0, 1),
        (1800.0, 1),
        (1801.0, 2),
        (3600.0, 2),
        (3601.0, 3),
        (7201.0, 4),
        (14401.0, 5),
    ];
    for (seconds, level) in cases {
        assert_eq!(StatisticsService::heat_level(seconds), level);
    }
    let hourly = [
        (0.0, 0),
        (1.0, 1),
        (300.0, 1),
        (301.0, 2),
        (900.0, 2),
        (901.0, 3),
        (2701.0, 5),
    ];
    for (seconds, level) in hourly {
        assert_eq!(HourlyStatisticsService::heat_level(seconds), level);
    }
}

#[test]
fn streak_allows_yesterday_when_today_not_played() {
    let sessions = [
        session(
            "2026-09-12T12:00:00Z",
            "2026-09-12T13:00:00Z",
            ActivityState::Active,
        ),
        session(
            "2026-09-13T12:00:00Z",
            "2026-09-13T13:00:00Z",
            ActivityState::Active,
        ),
        session(
            "2026-09-14T12:00:00Z",
            "2026-09-14T13:00:00Z",
            ActivityState::Active,
        ),
    ];
    let year = StatisticsService.year(&sessions, &Zone::utc(), 2026, date(2026, 9, 15));
    assert_eq!(year.current_streak, 3);
    assert_eq!(year.longest_streak, 3);
    assert_eq!(
        StatisticsService
            .year(&sessions, &Zone::utc(), 2026, date(2026, 9, 16))
            .current_streak,
        0
    );
}

#[test]
fn year_boundary_clips_annual_totals() {
    let value = session(
        "2025-12-31T23:30:00Z",
        "2026-01-01T00:30:00Z",
        ActivityState::Active,
    );
    let year = StatisticsService.year(&[value], &Zone::utc(), 2026, date(2026, 1, 1));
    assert_eq!(year.active_seconds, 1800.0);
    assert_eq!(year.session_count, 1);
    assert_eq!(year.active_days, 1);
}

#[test]
fn empty_data_is_valid() {
    let year = StatisticsService.year(&[], &Zone::utc(), 2026, date(2026, 9, 15));
    assert_eq!(year.active_days, 0);
    assert_eq!(year.longest_session, 0.0);
    assert!(StatisticsService
        .game(&[], &Zone::utc())
        .first_tracked
        .is_none());
}

#[test]
fn hourly_projection_bins_by_local_hour() {
    let value = session(
        "2026-09-15T12:00:00Z",
        "2026-09-15T12:30:00Z",
        ActivityState::Active,
    );
    let hours = HourlyStatisticsService.for_day(date(2026, 9, 15), &[value], &Zone::utc());
    assert_eq!(hours.len(), 24);
    assert_eq!(hours[12].active_seconds(), 1800.0);
    assert_eq!(hours[12].available_seconds, 3600.0);
}

#[test]
fn available_years_start_now_and_grow_without_future_years() {
    assert_eq!(TrackingYears::available(2026, 2026, []), vec![2026]);
    assert_eq!(
        TrackingYears::available(2026, 2028, []),
        vec![2028, 2027, 2026]
    );
    assert_eq!(
        TrackingYears::available(2026, 2026, [2024, 2030]),
        vec![2026, 2025, 2024]
    );
    assert_eq!(TrackingYears::available(2026, 2025, []), vec![2025]);
}
