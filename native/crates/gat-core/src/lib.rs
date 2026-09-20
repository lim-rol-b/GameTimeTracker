//! Platform-agnostic domain logic for Game Activity Tracker.
//!
//! This crate is a faithful Rust port of the original GameActivityTracker.Core
//! assembly. It contains no Windows dependencies so the behaviour can be unit
//! tested on any platform (including the Linux CI/agent host).

pub mod models;
pub mod presence;
pub mod statistics;
pub mod text;
pub mod tracking;

pub use models::{
    ActivitySegment, ActivityState, Game, GameProcessRule, GameSession, SettingsError,
    TrackerSettings,
};
pub use tracking::{
    ActivityDetector, ActivityObservation, ControllerFilter, ControllerSample, SessionManager,
};
