//! Windows platform integration for the Game Activity Tracker lightweight engine.
//!
//! Every provider has a portable fallback so the workspace builds and its logic stays
//! testable on Linux; only the Windows target performs real observation.

pub mod controller;
pub mod error;
pub mod foreground;
pub mod input;
pub mod paths;
pub mod process;
pub mod registry;
pub mod single_instance;
pub mod tray;

pub use controller::ControllerProvider;
pub use error::PlatformError;
pub use process::ProcessScanner;
pub use single_instance::SingleInstance;
pub use tray::{run_tray, TrayHost};
