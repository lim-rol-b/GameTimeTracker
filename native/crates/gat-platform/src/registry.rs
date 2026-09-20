//! Per-user autostart through the Run key.

use crate::error::PlatformError;

#[cfg(windows)]
const VALUE_NAME: &str = "GameActivityTracker";

#[cfg(windows)]
pub fn set_autostart(command: &str) -> Result<(), PlatformError> {
    use winreg::enums::HKEY_CURRENT_USER;
    use winreg::RegKey;
    let hkcu = RegKey::predef(HKEY_CURRENT_USER);
    let (key, _) = hkcu
        .create_subkey(r"Software\Microsoft\Windows\CurrentVersion\Run")
        .map_err(|error| PlatformError::message(error.to_string()))?;
    key.set_value(VALUE_NAME, &command.to_string())
        .map_err(|error| PlatformError::message(error.to_string()))?;
    Ok(())
}

#[cfg(windows)]
pub fn remove_autostart() -> Result<(), PlatformError> {
    use winreg::enums::{HKEY_CURRENT_USER, KEY_SET_VALUE};
    use winreg::RegKey;
    let hkcu = RegKey::predef(HKEY_CURRENT_USER);
    if let Ok(key) = hkcu.open_subkey_with_flags(
        r"Software\Microsoft\Windows\CurrentVersion\Run",
        KEY_SET_VALUE,
    ) {
        let _ = key.delete_value(VALUE_NAME);
    }
    Ok(())
}

#[cfg(windows)]
pub fn autostart_command() -> Result<Option<String>, PlatformError> {
    use winreg::enums::HKEY_CURRENT_USER;
    use winreg::RegKey;
    let hkcu = RegKey::predef(HKEY_CURRENT_USER);
    match hkcu.open_subkey(r"Software\Microsoft\Windows\CurrentVersion\Run") {
        Ok(key) => Ok(key.get_value(VALUE_NAME).ok()),
        Err(_) => Ok(None),
    }
}

#[cfg(not(windows))]
pub fn set_autostart(_command: &str) -> Result<(), PlatformError> {
    Err(PlatformError::message("仅 Windows 支持开机启动。"))
}

#[cfg(not(windows))]
pub fn remove_autostart() -> Result<(), PlatformError> {
    Ok(())
}

#[cfg(not(windows))]
pub fn autostart_command() -> Result<Option<String>, PlatformError> {
    Ok(None)
}
