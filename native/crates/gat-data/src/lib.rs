//! SQLite persistence compatible with the original C# database schema.

pub mod database;

pub use database::{DaemonSettings, DataError, TrackerDatabase};
