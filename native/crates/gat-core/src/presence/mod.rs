//! Game presence: process matching, Minecraft identity, related executables and Steam library reading.

pub mod matcher;
pub mod minecraft;
pub mod paths;
pub mod related;
pub mod steam;

pub use matcher::{GameMatcher, ProcessSnapshot};
pub use minecraft::MinecraftProcessIdentity;
pub use related::RelatedProcessRules;
pub use steam::{InstalledSteamGame, SteamError, SteamLibraryReader, SteamLibraryScan};
