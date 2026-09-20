//! System-wide last input time (keyboard and mouse).

use chrono::{DateTime, Utc};

#[cfg(windows)]
pub fn last_input(now: DateTime<Utc>) -> Option<DateTime<Utc>> {
    use windows::Win32::System::SystemInformation::GetTickCount64;
    use windows::Win32::UI::Input::KeyboardAndMouse::{GetLastInputInfo, LASTINPUTINFO};
    unsafe {
        let mut info = LASTINPUTINFO {
            cbSize: std::mem::size_of::<LASTINPUTINFO>() as u32,
            dwTime: 0,
        };
        if !GetLastInputInfo(&mut info).as_bool() {
            return None;
        }
        let elapsed = (GetTickCount64() as u32).wrapping_sub(info.dwTime);
        Some(now - chrono::Duration::milliseconds(elapsed as i64))
    }
}

#[cfg(not(windows))]
pub fn last_input(_now: DateTime<Utc>) -> Option<DateTime<Utc>> {
    None
}
