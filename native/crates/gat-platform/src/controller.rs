//! XInput controller activity. Connecting a controller alone is not activity, and held
//! steady controls do not refresh the clock.

use chrono::{DateTime, Utc};
#[cfg(windows)]
use gat_core::tracking::ControllerFilter;
use gat_core::tracking::ControllerSample;

pub struct ControllerProvider {
    previous: [Option<ControllerSample>; 4],
    last_input: Option<DateTime<Utc>>,
    #[cfg_attr(not(windows), allow(dead_code))]
    unavailable: bool,
}

impl Default for ControllerProvider {
    fn default() -> Self {
        Self::new()
    }
}

impl ControllerProvider {
    pub fn new() -> Self {
        Self {
            previous: [None; 4],
            last_input: None,
            unavailable: false,
        }
    }

    pub fn poll(&mut self, now: DateTime<Utc>, dead_zone: f64) -> Option<DateTime<Utc>> {
        #[cfg(windows)]
        {
            self.poll_windows(now, dead_zone)
        }
        #[cfg(not(windows))]
        {
            let _ = (now, dead_zone);
            self.last_input
        }
    }

    pub fn reset(&mut self) {
        self.previous = [None; 4];
        self.last_input = None;
    }

    #[cfg(windows)]
    fn poll_windows(&mut self, now: DateTime<Utc>, dead_zone: f64) -> Option<DateTime<Utc>> {
        use windows::Win32::UI::Input::XboxController::{XInputGetState, XINPUT_STATE};
        if self.unavailable {
            return None;
        }
        for index in 0..4usize {
            let mut state = XINPUT_STATE::default();
            let result = unsafe { XInputGetState(index as u32, &mut state) };
            if result != 0 {
                self.previous[index] = None;
                continue;
            }
            let gamepad = state.Gamepad;
            let sample = ControllerSample {
                buttons: gamepad.wButtons.0,
                lx: gamepad.sThumbLX,
                ly: gamepad.sThumbLY,
                rx: gamepad.sThumbRX,
                ry: gamepad.sThumbRY,
                lt: gamepad.bLeftTrigger,
                rt: gamepad.bRightTrigger,
            };
            if let Some(old) = self.previous[index] {
                if ControllerFilter::has_meaningful_change(old, sample, dead_zone) {
                    self.last_input = Some(now);
                }
            }
            self.previous[index] = Some(sample);
        }
        self.last_input
    }
}
