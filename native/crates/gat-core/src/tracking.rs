//! Single-writer session state machine. All boundaries are UTC observations and
//! never infer pre-launch activity.

use std::collections::HashMap;
use std::time::Duration;

use chrono::{DateTime, Utc};

use crate::models::{new_id, ActivitySegment, ActivityState, GameSession};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ActivityObservation {
    pub is_foreground: Option<bool>,
    pub last_input: Option<DateTime<Utc>>,
}

impl ActivityObservation {
    pub fn new(is_foreground: Option<bool>, last_input: Option<DateTime<Utc>>) -> Self {
        Self {
            is_foreground,
            last_input,
        }
    }
}

pub struct ActivityDetector;

impl ActivityDetector {
    pub fn detect(
        now: DateTime<Utc>,
        input: ActivityObservation,
        threshold: Duration,
    ) -> ActivityState {
        match input.is_foreground {
            None => ActivityState::Unknown,
            Some(false) => ActivityState::Background,
            Some(true) => match input.last_input {
                None => ActivityState::Unknown,
                Some(last) if last > now => ActivityState::Unknown,
                Some(last) => {
                    let elapsed = (now - last).to_std().unwrap_or(Duration::ZERO);
                    if elapsed < threshold {
                        ActivityState::Active
                    } else {
                        ActivityState::Idle
                    }
                }
            },
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct ControllerSample {
    pub buttons: u16,
    pub lx: i16,
    pub ly: i16,
    pub rx: i16,
    pub ry: i16,
    pub lt: u8,
    pub rt: u8,
}

pub struct ControllerFilter;

impl ControllerFilter {
    pub fn normalize(sample: ControllerSample, dead_zone: f64) -> ControllerSample {
        ControllerSample {
            buttons: sample.buttons,
            lx: axis(sample.lx, dead_zone),
            ly: axis(sample.ly, dead_zone),
            rx: axis(sample.rx, dead_zone),
            ry: axis(sample.ry, dead_zone),
            lt: if sample.lt < 30 { 0 } else { sample.lt },
            rt: if sample.rt < 30 { 0 } else { sample.rt },
        }
    }

    pub fn has_meaningful_change(
        previous: ControllerSample,
        current: ControllerSample,
        dead_zone: f64,
    ) -> bool {
        Self::normalize(previous, dead_zone) != Self::normalize(current, dead_zone)
    }
}

fn axis(value: i16, dead_zone: f64) -> i16 {
    if (value as i32).unsigned_abs() as f64 / 32768.0 < dead_zone {
        0
    } else {
        value
    }
}

struct Tracked {
    session: GameSession,
    observation: ActivityObservation,
    time: DateTime<Utc>,
}

pub struct SessionManager {
    running: HashMap<String, Tracked>,
    idle_threshold: Duration,
}

impl Default for SessionManager {
    fn default() -> Self {
        Self {
            running: HashMap::new(),
            idle_threshold: Duration::from_secs(60),
        }
    }
}

impl SessionManager {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn idle_threshold(&self) -> Duration {
        self.idle_threshold
    }

    pub fn set_idle_threshold(&mut self, threshold: Duration) {
        self.idle_threshold = threshold;
    }

    pub fn sessions(&self) -> Vec<&GameSession> {
        self.running
            .values()
            .map(|tracked| &tracked.session)
            .collect()
    }

    pub fn is_running(&self, game_id: &str) -> bool {
        self.running.contains_key(game_id)
    }

    pub fn start(
        &mut self,
        game_id: &str,
        now: DateTime<Utc>,
        observation: ActivityObservation,
    ) -> &GameSession {
        self.start_with_source(game_id, now, observation, "Observed")
    }

    pub fn start_with_source(
        &mut self,
        game_id: &str,
        now: DateTime<Utc>,
        observation: ActivityObservation,
        source: &str,
    ) -> &GameSession {
        if !self.running.contains_key(game_id) {
            let session = GameSession {
                id: new_id(),
                game_id: game_id.to_string(),
                start_time: now,
                end_time: None,
                last_checkpoint: now,
                source: source.to_string(),
                end_reason: None,
                segments: Vec::new(),
            };
            self.running.insert(
                game_id.to_string(),
                Tracked {
                    session,
                    observation,
                    time: now,
                },
            );
        }
        &self
            .running
            .get(game_id)
            .expect("session was just inserted")
            .session
    }

    pub fn state(&self, game_id: &str) -> ActivityState {
        self.running
            .get(game_id)
            .map(|tracked| {
                ActivityDetector::detect(tracked.time, tracked.observation, self.idle_threshold)
            })
            .unwrap_or(ActivityState::Unknown)
    }

    pub fn advance(&mut self, game_id: &str, now: DateTime<Utc>, observation: ActivityObservation) {
        let threshold = self.idle_threshold;
        let threshold_chrono = chrono::Duration::from_std(threshold).unwrap_or_default();
        let Some(tracked) = self.running.get_mut(game_id) else {
            return;
        };
        if now < tracked.time {
            // Wall-clock correction must not create negative or overlapping data.
            return;
        }

        let mut state = ActivityDetector::detect(tracked.time, tracked.observation, threshold);
        let mut cursor = tracked.time;
        let input_at = observation.last_input;
        let same_foreground = tracked.observation.is_foreground == Some(true)
            && observation.is_foreground == Some(true);

        if state == ActivityState::Active {
            if let Some(last) = tracked.observation.last_input {
                let mut expiry = last + threshold_chrono;
                if same_foreground {
                    if let Some(input_time) = input_at {
                        if input_time > last && input_time <= expiry && input_time <= now {
                            expiry = input_time + threshold_chrono;
                        }
                    }
                }
                let active_end = if expiry < now { expiry } else { now };
                append(
                    &mut tracked.session,
                    cursor,
                    active_end,
                    ActivityState::Active,
                );
                cursor = active_end;
                state = ActivityState::Idle;
            }
        }

        if cursor < now && state == ActivityState::Idle && same_foreground {
            if let Some(input_time) = input_at {
                if input_time > cursor && input_time <= now {
                    append(
                        &mut tracked.session,
                        cursor,
                        input_time,
                        ActivityState::Idle,
                    );
                    let expiry = input_time + threshold_chrono;
                    let end = if expiry < now { expiry } else { now };
                    append(&mut tracked.session, input_time, end, ActivityState::Active);
                    cursor = end;
                }
            }
        }

        append(&mut tracked.session, cursor, now, state);
        tracked.time = now;
        tracked.observation = observation;
        tracked.session.last_checkpoint = now;
    }

    pub fn stop(&mut self, game_id: &str, now: DateTime<Utc>, reason: &str) -> Option<GameSession> {
        let observation = self.running.get(game_id)?.observation;
        self.advance(game_id, now, observation);
        let mut tracked = self.running.remove(game_id)?;
        tracked.session.end_time = Some(tracked.time);
        tracked.session.end_reason = Some(reason.to_string());
        Some(tracked.session)
    }

    pub fn stop_all(&mut self, now: DateTime<Utc>, reason: &str) -> Vec<GameSession> {
        let keys: Vec<String> = self.running.keys().cloned().collect();
        keys.into_iter()
            .filter_map(|key| self.stop(&key, now, reason))
            .collect()
    }
}

fn append(
    session: &mut GameSession,
    start: DateTime<Utc>,
    end: DateTime<Utc>,
    state: ActivityState,
) {
    if end <= start {
        return;
    }
    if let Some(previous) = session.segments.last_mut() {
        if previous.state == state && previous.end_time == start {
            previous.end_time = end;
            return;
        }
    }
    session.segments.push(ActivitySegment {
        id: new_id(),
        session_id: session.id.clone(),
        start_time: start,
        end_time: end,
        state,
    });
}
