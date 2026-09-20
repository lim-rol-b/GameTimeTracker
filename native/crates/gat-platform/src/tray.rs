//! Tray-only background mode. On Windows this owns a hidden top-level window that
//! receives the tray callback, timer, power and session messages. On other platforms it
//! degrades to a headless loop so the engine can still be exercised in CI.

use crate::error::PlatformError;

/// Callbacks invoked by the background message loop.
pub trait TrayHost {
    fn on_tick(&mut self);
    fn on_open(&mut self);
    fn on_exit(&mut self);
    fn on_suspend(&mut self);
    fn on_resume(&mut self);
    fn on_lock(&mut self, locked: bool);
    fn should_exit(&self) -> bool;
}

#[cfg(windows)]
pub fn run_tray(host: Box<dyn TrayHost>, tick_ms: u32) -> Result<(), PlatformError> {
    windows_tray(host, tick_ms)
}

#[cfg(not(windows))]
pub fn run_tray(mut host: Box<dyn TrayHost>, tick_ms: u32) -> Result<(), PlatformError> {
    loop {
        host.on_tick();
        if host.should_exit() {
            break;
        }
        std::thread::sleep(std::time::Duration::from_millis(tick_ms as u64));
    }
    Ok(())
}

#[cfg(windows)]
fn windows_tray(host: Box<dyn TrayHost>, tick_ms: u32) -> Result<(), PlatformError> {
    use windows::core::{w, PCWSTR};
    use windows::Win32::Foundation::{HWND, LPARAM, LRESULT, POINT, WPARAM};
    use windows::Win32::System::LibraryLoader::GetModuleHandleW;
    use windows::Win32::System::RemoteDesktop::{
        WTSRegisterSessionNotification, WTSUnRegisterSessionNotification, NOTIFY_FOR_THIS_SESSION,
    };
    use windows::Win32::UI::Shell::{
        ExtractIconExW, Shell_NotifyIconW, NIF_ICON, NIF_MESSAGE, NIF_TIP, NIM_ADD, NIM_DELETE,
        NOTIFYICONDATAW,
    };
    // PBT_* and WTS_* notification codes all live in WindowsAndMessaging.
    use windows::Win32::UI::WindowsAndMessaging::*;

    const WM_TRAY: u32 = WM_APP + 1;
    const ID_OPEN: usize = 1;
    const ID_EXIT: usize = 2;
    const TIMER_ID: usize = 1;

    struct Context {
        host: Box<dyn TrayHost>,
        hwnd: HWND,
        tray: NOTIFYICONDATAW,
        last_open: Option<std::time::Instant>,
        own_icon: bool,
        user_exit: bool,
    }

    unsafe fn show_menu(context: &mut Context) {
        if let Ok(menu) = CreatePopupMenu() {
            let _ = AppendMenuW(menu, MF_STRING, ID_OPEN, w!("打开"));
            let _ = AppendMenuW(menu, MF_STRING, ID_EXIT, w!("退出"));
            let mut point = POINT::default();
            let _ = GetCursorPos(&mut point);
            let _ = SetForegroundWindow(context.hwnd);
            let _ = TrackPopupMenu(
                menu,
                TPM_BOTTOMALIGN | TPM_LEFTALIGN,
                point.x,
                point.y,
                Some(0),
                context.hwnd,
                None,
            );
            let _ = DestroyMenu(menu);
        }
    }

    unsafe fn load_tray_icon() -> (HICON, bool) {
        if let Ok(configured) = std::env::var("GAT_ICON_PATH") {
            let path = std::path::Path::new(&configured);
            if path.is_file() {
                if let Some(icon) = load_icon_file(path) {
                    return (icon, true);
                }
            }
        }
        let directory = std::env::current_exe()
            .ok()
            .and_then(|path| path.parent().map(|parent| parent.to_path_buf()));
        if let Some(directory) = &directory {
            // A loose App.ico can be swapped without rebuilding the engine.
            let ico = directory.join("App.ico");
            if ico.is_file() {
                if let Some(icon) = load_icon_file(&ico) {
                    return (icon, true);
                }
            }
            // Otherwise reuse the icon embedded in the WPF viewer beside the engine.
            let viewer = directory.join("GameActivityTracker.exe");
            if viewer.is_file() {
                if let Some(icon) = extract_icon(&viewer) {
                    return (icon, true);
                }
            }
        }
        (LoadIconW(None, IDI_APPLICATION).unwrap_or_default(), false)
    }

    unsafe fn load_icon_file(path: &std::path::Path) -> Option<HICON> {
        use std::os::windows::ffi::OsStrExt as _;
        let wide: Vec<u16> = path
            .as_os_str()
            .encode_wide()
            .chain(std::iter::once(0))
            .collect();
        let width = GetSystemMetrics(SM_CXSMICON);
        let height = GetSystemMetrics(SM_CYSMICON);
        let handle = LoadImageW(
            None,
            PCWSTR(wide.as_ptr()),
            IMAGE_ICON,
            width,
            height,
            LR_LOADFROMFILE,
        )
        .ok()?;
        if handle.0.is_null() {
            None
        } else {
            Some(HICON(handle.0))
        }
    }

    unsafe fn extract_icon(path: &std::path::Path) -> Option<HICON> {
        use std::os::windows::ffi::OsStrExt as _;
        let wide: Vec<u16> = path
            .as_os_str()
            .encode_wide()
            .chain(std::iter::once(0))
            .collect();
        let mut small = HICON(std::ptr::null_mut());
        let count = ExtractIconExW(PCWSTR(wide.as_ptr()), 0, None, Some(&mut small), 1);
        if count == 0 || small.0.is_null() {
            None
        } else {
            Some(small)
        }
    }

    unsafe extern "system" fn window_proc(
        hwnd: HWND,
        message: u32,
        wparam: WPARAM,
        lparam: LPARAM,
    ) -> LRESULT {
        let pointer = GetWindowLongPtrW(hwnd, GWLP_USERDATA) as *mut Context;
        if pointer.is_null() {
            return DefWindowProcW(hwnd, message, wparam, lparam);
        }
        let context = &mut *pointer;
        match message {
            WM_TRAY => {
                let event = lparam.0 as u32;
                if event == WM_LBUTTONUP {
                    // Single left click wakes the viewer, debounced against double clicks.
                    let now = std::time::Instant::now();
                    let ready = context
                        .last_open
                        .map(|last| {
                            now.duration_since(last) >= std::time::Duration::from_millis(800)
                        })
                        .unwrap_or(true);
                    if ready {
                        context.last_open = Some(now);
                        context.host.on_open();
                    }
                } else if event == WM_RBUTTONUP {
                    show_menu(context);
                }
                LRESULT(0)
            }
            WM_COMMAND => {
                let id = wparam.0 & 0xffff;
                match id {
                    ID_OPEN => context.host.on_open(),
                    ID_EXIT => {
                        context.user_exit = true;
                        context.host.on_exit();
                        PostQuitMessage(0);
                    }
                    _ => {}
                }
                LRESULT(0)
            }
            WM_TIMER => {
                if wparam.0 == TIMER_ID {
                    context.host.on_tick();
                }
                LRESULT(0)
            }
            WM_POWERBROADCAST => {
                match wparam.0 as u32 {
                    PBT_APMSUSPEND => context.host.on_suspend(),
                    PBT_APMRESUMEAUTOMATIC | PBT_APMRESUMESUSPEND => context.host.on_resume(),
                    _ => {}
                }
                LRESULT(1)
            }
            WM_WTSSESSION_CHANGE => {
                match wparam.0 as u32 {
                    WTS_SESSION_LOCK | WTS_REMOTE_DISCONNECT | WTS_CONSOLE_DISCONNECT => {
                        context.host.on_lock(true)
                    }
                    WTS_SESSION_UNLOCK | WTS_REMOTE_CONNECT | WTS_CONSOLE_CONNECT => {
                        context.host.on_lock(false)
                    }
                    _ => {}
                }
                LRESULT(0)
            }
            WM_DESTROY => {
                PostQuitMessage(0);
                LRESULT(0)
            }
            _ => DefWindowProcW(hwnd, message, wparam, lparam),
        }
    }

    fn set_tip(data: &mut NOTIFYICONDATAW, text: &str) {
        let wide: Vec<u16> = text.encode_utf16().take(127).collect();
        for (index, unit) in wide.iter().enumerate() {
            data.szTip[index] = *unit;
        }
    }

    unsafe {
        let module =
            GetModuleHandleW(None).map_err(|error| PlatformError::message(error.to_string()))?;
        let instance = windows::Win32::Foundation::HINSTANCE(module.0);
        let class_name = w!("GameActivityTrackerTrayWindow");
        let window_class = WNDCLASSW {
            lpfnWndProc: Some(window_proc),
            hInstance: instance,
            lpszClassName: class_name,
            ..Default::default()
        };
        RegisterClassW(&window_class);
        let hwnd = CreateWindowExW(
            WINDOW_EX_STYLE::default(),
            class_name,
            w!("GameActivityTracker"),
            WS_OVERLAPPED,
            0,
            0,
            0,
            0,
            None,
            None,
            Some(instance),
            None,
        )
        .map_err(|error| PlatformError::message(error.to_string()))?;

        let (icon, own_icon) = load_tray_icon();
        let mut tray = NOTIFYICONDATAW::default();
        tray.cbSize = std::mem::size_of::<NOTIFYICONDATAW>() as u32;
        tray.hWnd = hwnd;
        tray.uID = 1;
        tray.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        tray.uCallbackMessage = WM_TRAY;
        tray.hIcon = icon;
        set_tip(&mut tray, "游戏时长记录器");
        if !Shell_NotifyIconW(NIM_ADD, &tray).as_bool() {
            let _ = DestroyWindow(hwnd);
            return Err(PlatformError::message(
                "Shell_NotifyIcon NIM_ADD failed; the tray icon was not created",
            ));
        }

        let context = Box::into_raw(Box::new(Context {
            host,
            hwnd,
            tray,
            last_open: None,
            own_icon,
            user_exit: false,
        }));
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, context as isize);
        let _ = WTSRegisterSessionNotification(hwnd, NOTIFY_FOR_THIS_SESSION);
        SetTimer(Some(hwnd), TIMER_ID, tick_ms, None);

        let mut message = MSG::default();
        loop {
            let result = GetMessageW(&mut message, None, 0, 0);
            if result.0 == 0 || result.0 == -1 {
                break;
            }
            let _ = TranslateMessage(&message);
            DispatchMessageW(&message);
        }

        let mut context = Box::from_raw(context);
        if !context.user_exit {
            // The loop ended without the tray "退出" action (for example a message-loop
            // error): finalize sessions and surface the reason.
            context.host.on_exit();
        }
        let _ = Shell_NotifyIconW(NIM_DELETE, &context.tray);
        if context.own_icon {
            let _ = DestroyIcon(context.tray.hIcon);
        }
        let _ = WTSUnRegisterSessionNotification(hwnd);
        let _ = KillTimer(Some(hwnd), TIMER_ID);
        let _ = DestroyWindow(hwnd);
        if !context.user_exit {
            return Err(PlatformError::message(
                "tray message loop ended unexpectedly (no user exit)",
            ));
        }
    }
    Ok(())
}
