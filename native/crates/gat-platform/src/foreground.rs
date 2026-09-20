//! Foreground window owner.

#[cfg(windows)]
pub fn foreground_process_id() -> Option<i32> {
    use windows::Win32::UI::WindowsAndMessaging::{GetForegroundWindow, GetWindowThreadProcessId};
    unsafe {
        let window = GetForegroundWindow();
        if window.0.is_null() {
            return None;
        }
        let mut pid = 0u32;
        if GetWindowThreadProcessId(window, Some(&mut pid)) == 0 || pid == 0 {
            None
        } else {
            Some(pid as i32)
        }
    }
}

#[cfg(not(windows))]
pub fn foreground_process_id() -> Option<i32> {
    None
}
