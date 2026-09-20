//! Daily, yearly, per-game and hourly projections of persisted UTC segments.

use std::collections::{BTreeMap, BTreeSet};

use chrono::{
    DateTime, Datelike, FixedOffset, LocalResult, NaiveDate, NaiveDateTime, Offset, TimeZone,
    Timelike, Utc,
};
use chrono_tz::Tz;

use crate::models::{ActivityState, GameSession};

/// A time zone used for calendar projection: either a fixed offset or a named IANA zone.
#[derive(Debug, Clone, Copy)]
pub enum Zone {
    Fixed(FixedOffset),
    Named(Tz),
}

impl Zone {
    pub fn fixed(seconds: i32) -> Self {
        Zone::Fixed(FixedOffset::east_opt(seconds).expect("valid UTC offset"))
    }

    pub fn utc() -> Self {
        Zone::Named(chrono_tz::UTC)
    }

    pub fn named(tz: Tz) -> Self {
        Zone::Named(tz)
    }

    fn offset_at(&self, utc: DateTime<Utc>) -> FixedOffset {
        match self {
            Zone::Fixed(offset) => *offset,
            Zone::Named(tz) => tz.offset_from_utc_datetime(&utc.naive_utc()).fix(),
        }
    }

    fn offsets_for_local(&self, local: NaiveDateTime) -> LocalResult<FixedOffset> {
        match self {
            Zone::Fixed(offset) => LocalResult::Single(*offset),
            Zone::Named(tz) => tz
                .offset_from_local_datetime(&local)
                .map(|offset| offset.fix()),
        }
    }

    fn local_naive(&self, utc: DateTime<Utc>) -> NaiveDateTime {
        utc.with_timezone(&self.offset_at(utc)).naive_local()
    }
}

#[derive(Debug, Clone)]
pub struct DailyStatistics {
    pub date: NaiveDate,
    pub active_seconds: f64,
    pub running_seconds: f64,
    pub game_active_seconds: BTreeMap<String, f64>,
    pub game_running_seconds: BTreeMap<String, f64>,
}

impl DailyStatistics {
    pub fn new(date: NaiveDate) -> Self {
        Self {
            date,
            active_seconds: 0.0,
            running_seconds: 0.0,
            game_active_seconds: BTreeMap::new(),
            game_running_seconds: BTreeMap::new(),
        }
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct YearlyStatistics {
    pub active_seconds: f64,
    pub running_seconds: f64,
    pub active_days: i32,
    pub session_count: i32,
    pub longest_session: f64,
    pub current_streak: i32,
    pub longest_streak: i32,
}

#[derive(Debug, Clone, PartialEq)]
pub struct GameStatistics {
    pub active_seconds: f64,
    pub running_seconds: f64,
    pub sessions: i32,
    pub active_days: i32,
    pub longest_session: f64,
    pub first_tracked: Option<DateTime<Utc>>,
}

#[derive(Debug, Default, Clone, Copy)]
pub struct StatisticsService;

impl StatisticsService {
    pub fn daily(
        &self,
        sessions: &[GameSession],
        zone: &Zone,
    ) -> BTreeMap<NaiveDate, DailyStatistics> {
        let mut days: BTreeMap<NaiveDate, DailyStatistics> = BTreeMap::new();
        for session in sessions {
            for segment in &session.segments {
                for (day, seconds) in self.split(segment.start_time, segment.end_time, zone) {
                    let item = days.entry(day).or_insert_with(|| DailyStatistics::new(day));
                    item.running_seconds += seconds;
                    *item
                        .game_running_seconds
                        .entry(session.game_id.clone())
                        .or_insert(0.0) += seconds;
                    if segment.state != ActivityState::Active {
                        continue;
                    }
                    item.active_seconds += seconds;
                    *item
                        .game_active_seconds
                        .entry(session.game_id.clone())
                        .or_insert(0.0) += seconds;
                }
            }
        }
        days
    }

    /// Split a UTC interval into local calendar-day slices, honouring DST gaps and folds.
    pub fn split(
        &self,
        start: DateTime<Utc>,
        end: DateTime<Utc>,
        zone: &Zone,
    ) -> Vec<(NaiveDate, f64)> {
        let mut start = start;
        let mut out = Vec::new();
        while start < end {
            let day = zone.local_naive(start).date();
            let mut next_local = day.succ_opt().unwrap_or(day).and_hms_opt(0, 0, 0).unwrap();
            while matches!(zone.offsets_for_local(next_local), LocalResult::None) {
                next_local += chrono::Duration::minutes(1);
            }
            let offsets: Vec<FixedOffset> = match zone.offsets_for_local(next_local) {
                LocalResult::Single(offset) => vec![offset],
                LocalResult::Ambiguous(earliest, latest) => vec![earliest, latest],
                LocalResult::None => Vec::new(),
            };
            let next = offsets
                .iter()
                .filter_map(|offset| offset.from_local_datetime(&next_local).single())
                .map(|value| value.with_timezone(&Utc))
                .filter(|value| *value > start)
                .min();
            let stop = match next {
                Some(value) if value < end => value,
                _ => end,
            };
            let seconds = (stop - start).num_microseconds().unwrap_or(0) as f64 / 1_000_000.0;
            out.push((day, seconds));
            start = stop;
        }
        out
    }

    pub fn year(
        &self,
        source: &[GameSession],
        zone: &Zone,
        year: i32,
        today: NaiveDate,
    ) -> YearlyStatistics {
        let all_days = self.daily(source, zone);
        let days: Vec<&DailyStatistics> = all_days
            .values()
            .filter(|day| day.date.year() == year)
            .collect();
        let active_days: BTreeSet<NaiveDate> = days
            .iter()
            .filter(|day| day.active_seconds > 0.0)
            .map(|day| day.date)
            .collect();

        let mut longest = 0;
        for day in &active_days {
            let mut length = 1;
            let mut prior = day.pred_opt();
            while let Some(previous) = prior {
                if active_days.contains(&previous) {
                    length += 1;
                    prior = previous.pred_opt();
                } else {
                    break;
                }
            }
            longest = longest.max(length);
        }

        let mut current = 0;
        if today.year() == year {
            let mut cursor = if all_days
                .get(&today)
                .map(|day| day.active_seconds > 0.0)
                .unwrap_or(false)
            {
                today
            } else {
                today.pred_opt().unwrap_or(today)
            };
            while all_days
                .get(&cursor)
                .map(|day| day.active_seconds > 0.0)
                .unwrap_or(false)
            {
                current += 1;
                match cursor.pred_opt() {
                    Some(previous) => cursor = previous,
                    None => break,
                }
            }
        }

        let intersecting: Vec<&GameSession> = source
            .iter()
            .filter(|session| {
                session.segments.iter().any(|segment| {
                    self.split(segment.start_time, segment.end_time, zone)
                        .iter()
                        .any(|(day, _)| day.year() == year)
                })
            })
            .collect();

        YearlyStatistics {
            active_seconds: days.iter().map(|day| day.active_seconds).sum(),
            running_seconds: days.iter().map(|day| day.running_seconds).sum(),
            active_days: active_days.len() as i32,
            session_count: intersecting.len() as i32,
            longest_session: intersecting
                .iter()
                .map(|session| session.running_duration())
                .fold(0.0_f64, f64::max),
            current_streak: current,
            longest_streak: longest,
        }
    }

    pub fn game(&self, source: &[GameSession], zone: &Zone) -> GameStatistics {
        let daily = self.daily(source, zone);
        GameStatistics {
            active_seconds: source.iter().map(|session| session.active_duration()).sum(),
            running_seconds: source
                .iter()
                .map(|session| session.running_duration())
                .sum(),
            sessions: source.len() as i32,
            active_days: daily
                .values()
                .filter(|day| day.active_seconds > 0.0)
                .count() as i32,
            longest_session: source
                .iter()
                .map(|session| session.running_duration())
                .fold(0.0_f64, f64::max),
            first_tracked: source.iter().map(|session| session.start_time).min(),
        }
    }

    pub fn heat_level(seconds: f64) -> i32 {
        if seconds <= 0.0 {
            0
        } else if seconds <= 1800.0 {
            1
        } else if seconds <= 3600.0 {
            2
        } else if seconds <= 7200.0 {
            3
        } else if seconds <= 14400.0 {
            4
        } else {
            5
        }
    }
}

#[derive(Debug, Clone)]
pub struct HourlyActivityRecord {
    pub session_id: String,
    pub game_id: String,
    pub start_time: DateTime<Utc>,
    pub end_time: DateTime<Utc>,
    pub state: ActivityState,
}

impl HourlyActivityRecord {
    pub fn duration(&self) -> f64 {
        (self.end_time - self.start_time)
            .num_microseconds()
            .unwrap_or(0) as f64
            / 1_000_000.0
    }
}

#[derive(Debug, Clone)]
pub struct HourlyStatistics {
    pub hour: usize,
    pub available_seconds: f64,
    pub records: Vec<HourlyActivityRecord>,
}

impl HourlyStatistics {
    pub fn running_seconds(&self) -> f64 {
        self.records.iter().map(|record| record.duration()).sum()
    }

    pub fn active_seconds(&self) -> f64 {
        self.duration(ActivityState::Active)
    }

    pub fn idle_seconds(&self) -> f64 {
        self.duration(ActivityState::Idle)
    }

    pub fn background_seconds(&self) -> f64 {
        self.duration(ActivityState::Background)
    }

    pub fn unknown_seconds(&self) -> f64 {
        self.duration(ActivityState::Unknown)
    }

    fn duration(&self, state: ActivityState) -> f64 {
        self.records
            .iter()
            .filter(|record| record.state == state)
            .map(|record| record.duration())
            .sum()
    }
}

#[derive(Debug, Default, Clone, Copy)]
pub struct HourlyStatisticsService;

impl HourlyStatisticsService {
    /// Project UTC segments into 24 local clock-hour bins without changing persisted data.
    pub fn for_day(
        &self,
        date: NaiveDate,
        sessions: &[GameSession],
        zone: &Zone,
    ) -> Vec<HourlyStatistics> {
        let mut hours: Vec<HourlyStatistics> = (0..24)
            .map(|hour| HourlyStatistics {
                hour,
                available_seconds: 0.0,
                records: Vec::new(),
            })
            .collect();
        let midnight = date.and_hms_opt(0, 0, 0).unwrap().and_utc();
        let limit = midnight + chrono::Duration::days(1) + chrono::Duration::hours(14);
        let mut windows: Vec<(usize, DateTime<Utc>, DateTime<Utc>)> = Vec::new();
        let mut cursor = midnight - chrono::Duration::hours(14);
        while cursor < limit {
            let local = zone.local_naive(cursor);
            if local.date() == date {
                let end = cursor + chrono::Duration::minutes(1);
                hours[local.hour() as usize].available_seconds += 60.0;
                match windows.last_mut() {
                    Some(last) if last.0 == local.hour() as usize && last.2 == cursor => {
                        last.2 = end;
                    }
                    _ => windows.push((local.hour() as usize, cursor, end)),
                }
            }
            cursor += chrono::Duration::minutes(1);
        }

        for session in sessions {
            for segment in &session.segments {
                for (hour, window_start, window_end) in &windows {
                    let start = segment.start_time.max(*window_start);
                    let end = segment.end_time.min(*window_end);
                    if end <= start {
                        continue;
                    }
                    hours[*hour].records.push(HourlyActivityRecord {
                        session_id: session.id.clone(),
                        game_id: session.game_id.clone(),
                        start_time: start,
                        end_time: end,
                        state: segment.state,
                    });
                }
            }
        }
        for hour in &mut hours {
            hour.records.sort_by_key(|record| record.start_time);
        }
        hours
    }

    pub fn heat_level(seconds: f64) -> i32 {
        if seconds <= 0.0 {
            0
        } else if seconds <= 300.0 {
            1
        } else if seconds <= 900.0 {
            2
        } else if seconds <= 1800.0 {
            3
        } else if seconds <= 2700.0 {
            4
        } else {
            5
        }
    }
}

pub struct TrackingYears;

impl TrackingYears {
    pub fn available(
        first_year: i32,
        current_year: i32,
        recorded_years: impl IntoIterator<Item = i32>,
    ) -> Vec<i32> {
        let mut start = first_year.clamp(1, 9999).min(current_year);
        for year in recorded_years {
            if (1..=current_year).contains(&year) {
                start = start.min(year);
            }
        }
        (start..=current_year).rev().collect()
    }
}
