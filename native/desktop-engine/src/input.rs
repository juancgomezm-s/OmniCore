use crate::{
    native::*,
    server::{Error, Result},
};
use serde_json::{Value, json};
use std::{mem::size_of, thread::sleep, time::Duration};
#[repr(C)]
#[derive(Clone, Copy)]
struct Mouse {
    x: i32,
    y: i32,
    data: u32,
    flags: u32,
    time: u32,
    extra: usize,
}
#[repr(C)]
#[derive(Clone, Copy)]
struct Key {
    vk: u16,
    scan: u16,
    flags: u32,
    time: u32,
    extra: usize,
}
#[repr(C)]
#[derive(Clone, Copy)]
union Data {
    mouse: Mouse,
    key: Key,
}
#[repr(C)]
#[derive(Clone, Copy)]
struct Input {
    kind: u32,
    data: Data,
}
#[link(name = "user32")]
unsafe extern "system" {
    fn SendInput(n: u32, inputs: *const Input, size: i32) -> u32;
    fn GetSystemMetrics(n: i32) -> i32;
}
fn key(vk: u16, scan: u16, flags: u32) -> Input {
    Input {
        kind: 1,
        data: Data {
            key: Key {
                vk,
                scan,
                flags,
                time: 0,
                extra: 0,
            },
        },
    }
}
fn mouse(flags: u32, data: u32) -> Input {
    Input {
        kind: 0,
        data: Data {
            mouse: Mouse {
                x: 0,
                y: 0,
                data,
                flags,
                time: 0,
                extra: 0,
            },
        },
    }
}
fn movement(p: (i32, i32)) -> Result<Input> {
    unsafe {
        let (l, t, w, h) = (
            GetSystemMetrics(76),
            GetSystemMetrics(77),
            GetSystemMetrics(78),
            GetSystemMetrics(79),
        );
        if w <= 0 || h <= 0 || p.0 < l || p.1 < t || p.0 >= l + w || p.1 >= t + h {
            return Err(Error::new(
                "POINT_OUTSIDE_DESKTOP",
                "Point is outside the virtual desktop.",
            ));
        }
        Ok(Input {
            kind: 0,
            data: Data {
                mouse: Mouse {
                    x: (((p.0 - l) as f64) * 65535.0 / (w - 1).max(1) as f64).round() as i32,
                    y: (((p.1 - t) as f64) * 65535.0 / (h - 1).max(1) as f64).round() as i32,
                    data: 0,
                    flags: 0xc001,
                    time: 0,
                    extra: 0,
                },
            },
        })
    }
}
struct Session {
    hwnd: Handle,
    held: Vec<(u32, u16, u16, u32, Input)>,
    sent: u32,
    mouse_guard: bool,
}
impl Session {
    fn check(&self, p: Option<(i32, i32)>) -> Result<()> {
        crate::indicator::check()?;
        unsafe {
            if GetAsyncKeyState(27) < 0 {
                crate::indicator::cancel();
                return Err(Error::new("CANCELLED", "Escape is held; input cancelled."));
            }
            if IsWindow(self.hwnd) == 0 || GetForegroundWindow() != self.hwnd {
                return Err(Error::new(
                    "FOCUS_LOST",
                    "Target must remain the foreground window.",
                ));
            }
            if let Some((x, y)) = p
                && GetAncestor(WindowFromPoint(Point { x, y }), 2) != self.hwnd
            {
                return Err(Error::new(
                    "POINT_NOT_TARGET",
                    "Point belongs to another window.",
                ));
            }
            for vk in [16, 17, 18, 160, 161, 162, 163, 164, 165, 91, 92] {
                let owned = self.held.iter().any(|a| {
                    a.0 == 1
                        && (a.1 as i32 == vk
                            || match a.1 {
                                16 => [160, 161].contains(&vk),
                                17 => [162, 163].contains(&vk),
                                18 => [164, 165].contains(&vk),
                                _ => false,
                            })
                });
                if !owned && GetAsyncKeyState(vk) < 0 {
                    return Err(Error::new("INPUT_BUSY", "Release physical modifier keys."));
                }
            }
            if self.mouse_guard {
                for (vk, down) in [(1, 2), (2, 8), (4, 32), (5, 0), (6, 0)] {
                    if GetAsyncKeyState(vk) < 0
                        && !self.held.iter().any(|a| a.0 == 0 && a.3 == down)
                    {
                        return Err(Error::new("INPUT_BUSY", "Release physical mouse buttons."));
                    }
                }
            }
            Ok(())
        }
    }
    fn record(&mut self, i: Input) {
        unsafe {
            if i.kind == 1 {
                let k = i.data.key;
                let token = (1, k.vk, k.scan, k.flags & !2);
                self.held.retain(|a| (a.0, a.1, a.2, a.3) != token);
                if k.flags & 2 == 0 {
                    self.held
                        .push((1, k.vk, k.scan, k.flags, key(k.vk, k.scan, k.flags | 2)))
                }
            } else {
                let m = i.data.mouse;
                for (d, u) in [(2, 4), (8, 16), (32, 64)] {
                    if m.flags & u != 0 {
                        self.held.retain(|a| !(a.0 == 0 && a.3 == d))
                    }
                    if m.flags & d != 0 {
                        self.held.push((0, 0, 0, d, mouse(u, 0)))
                    }
                }
            }
        }
    }
    fn send(&mut self, items: &[Input], p: Option<(i32, i32)>) -> Result<()> {
        self.check(p)?;
        let n = unsafe {
            SendInput(
                items.len() as u32,
                items.as_ptr(),
                size_of::<Input>() as i32,
            )
        };
        for i in &items[..n as usize] {
            self.record(*i)
        }
        self.sent += n;
        if n as usize != items.len() {
            return Err(Error::new(
                "INPUT_BLOCKED",
                "Windows did not accept all input events; no elevation attempted.",
            ));
        }
        Ok(())
    }
    fn cleanup(&mut self) -> Result<()> {
        let items: Vec<_> = self.held.iter().rev().map(|a| a.4).collect();
        if items.is_empty() {
            return Ok(());
        }
        let n = unsafe {
            SendInput(
                items.len() as u32,
                items.as_ptr(),
                size_of::<Input>() as i32,
            )
        };
        for i in &items[..n as usize] {
            self.record(*i)
        }
        if n as usize != items.len() {
            return Err(Error::new(
                "INPUT_CLEANUP_FAILED",
                "Manually release keys/buttons; Windows rejected synthetic releases.",
            ));
        }
        Ok(())
    }
}
impl Drop for Session {
    fn drop(&mut self) {
        let _ = self.cleanup();
    }
}
fn chord(s: &str) -> Result<Vec<Input>> {
    let mut mods = Vec::new();
    let mut base = None;
    for token in s.split('+').map(|x| x.trim().to_ascii_lowercase()) {
        let m = match token.as_str() {
            "ctrl" | "control" | "control_l" | "control_r" => Some(17),
            "shift" | "shift_l" | "shift_r" => Some(16),
            "alt" | "alt_l" | "alt_r" => Some(18),
            _ => None,
        };
        if let Some(m) = m {
            if mods.contains(&m) {
                return Err(Error::new("INVALID_PARAMS", "Duplicate modifier."));
            }
            mods.push(m)
        } else if base.replace(token).is_some() {
            return Err(Error::new(
                "INVALID_PARAMS",
                "Exactly one base key is required.",
            ));
        }
    }
    let b = base.ok_or_else(|| Error::new("INVALID_PARAMS", "Missing base key."))?;
    if ["escape", "esc"].contains(&b.as_str()) {
        return Err(Error::new(
            "RESERVED_CANCEL_KEY",
            "Escape is reserved for user cancellation.",
        ));
    }
    let vk = crate::input_compat::virtual_key(&b)
        .map_err(|e| Error::new("KEY_NOT_SUPPORTED", &e.to_string()))?;
    if [0x5b, 0x5c].contains(&vk) {
        return Err(Error::new(
            "SYSTEM_CHORD_NOT_SUPPORTED",
            "Windows keys are unsupported.",
        ));
    }
    let f = crate::input_compat::extended_key(vk);
    if mods.contains(&18) && [9, 27, 32, 0x73].contains(&vk)
        || mods.contains(&17) && vk == 27
        || mods.contains(&17) && mods.contains(&18) && vk == 46
    {
        return Err(Error::new(
            "SYSTEM_CHORD_NOT_SUPPORTED",
            "System/window management chord is unsupported.",
        ));
    }
    let mut items: Vec<_> = mods.iter().map(|m| key(*m, 0, 0)).collect();
    items.extend([key(vk, 0, f), key(vk, 0, f | 2)]);
    items.extend(mods.iter().rev().map(|m| key(*m, 0, 2)));
    Ok(items)
}
pub fn perform(hwnd: Handle, method: &str, p: &Value, points: &[(i32, i32)]) -> Result<Value> {
    let mut s = Session {
        hwnd,
        held: Vec::new(),
        sent: 0,
        mouse_guard: matches!(method, "click" | "drag" | "scroll"),
    };
    let result = (|| {
        s.check(None)?;
        match method {
            "type_text" => {
                let text = p["text"]
                    .as_str()
                    .filter(|t| t.chars().count() <= 4096 && !t.chars().any(|c| (c as u32) < 32))
                    .ok_or_else(|| {
                        Error::new(
                            "INVALID_PARAMS",
                            "text must be at most 4096 printable characters.",
                        )
                    })?;
                let units: Vec<_> = text.encode_utf16().collect();
                for batch in units.chunks(16) {
                    let items: Vec<_> = batch
                        .iter()
                        .flat_map(|u| [key(0, *u, 4), key(0, *u, 6)])
                        .collect();
                    s.send(&items, None)?;
                }
            }
            "press_key" => {
                let text = p["key"]
                    .as_str()
                    .filter(|t| !t.is_empty() && t.len() <= 80)
                    .ok_or_else(|| Error::new("INVALID_PARAMS", "key must be a short chord."))?;
                s.send(&chord(text)?, None)?;
            }
            "click" => {
                let button = p["button"].as_str().unwrap_or("left");
                let (d, u) = crate::input_compat::mouse_button_flags(button)
                    .map_err(|e| Error::new("INVALID_PARAMS", &e.to_string()))?;
                let n = crate::server::integer(p, "click_count", 1, 1, 3)?;
                crate::input_compat::validate_click(button, n as u64)
                    .map_err(|e| Error::new("INVALID_PARAMS", &e.to_string()))?;
                for i in 0..n {
                    if i > 0 {
                        sleep(Duration::from_millis(50))
                    }
                    s.send(
                        &[movement(points[0])?, mouse(d, 0), mouse(u, 0)],
                        Some(points[0]),
                    )?;
                }
            }
            "scroll" => {
                let (sx, sy) = if p.get("delta").is_some() {
                    if p.get("scroll_x").is_some() || p.get("scroll_y").is_some() {
                        return Err(Error::new(
                            "INVALID_PARAMS",
                            "Use legacy delta OR scroll_x/scroll_y.",
                        ));
                    }
                    (
                        0,
                        crate::server::integer(p, "delta", 0, -12000, 12000)? as i32,
                    )
                } else {
                    let x =
                        crate::server::integer(p, "scroll_x", 0, i32::MIN as i64, i32::MAX as i64)?
                            as i32;
                    let y =
                        crate::server::integer(p, "scroll_y", 0, i32::MIN as i64, i32::MAX as i64)?
                            as i32;
                    crate::input_compat::wheel_deltas(x, y)
                };
                if sx == 0 && sy == 0 {
                    return Err(Error::new("INVALID_PARAMS", "Scroll must be nonzero."));
                }
                s.send(&[movement(points[0])?], Some(points[0]))?;
                if sy != 0 {
                    s.send(&[mouse(0x800, sy as u32)], Some(points[0]))?;
                }
                if sx != 0 {
                    s.send(&[mouse(0x1000, sx as u32)], Some(points[0]))?;
                }
            }
            "drag" => {
                let duration = p
                    .get("duration")
                    .map_or(Some(0.5), Value::as_f64)
                    .filter(|d| d.is_finite() && (0.05..=3.0).contains(d))
                    .ok_or_else(|| {
                        Error::new("INVALID_PARAMS", "duration must be 0.05..3 seconds.")
                    })?;
                let (a, b) = (points[0], points[1]);
                s.check(Some(b))?;
                s.send(&[movement(a)?, mouse(2, 0)], Some(a))?;
                let steps = (duration * 60.0).ceil() as i32;
                for i in 1..=steps {
                    sleep(Duration::from_secs_f64(duration / steps as f64));
                    let at = (
                        a.0 + ((b.0 - a.0) as f64 * i as f64 / steps as f64).round() as i32,
                        a.1 + ((b.1 - a.1) as f64 * i as f64 / steps as f64).round() as i32,
                    );
                    s.send(&[movement(at)?], Some(at))?;
                }
                s.send(&[mouse(4, 0)], Some(b))?;
            }
            _ => {
                return Err(Error::new(
                    "METHOD_NOT_IMPLEMENTED",
                    "Unknown input method.",
                ));
            }
        }
        Ok(())
    })();
    let cleanup = s.cleanup();
    cleanup?;
    result?;
    Ok(json!({"events_sent":s.sent,"cleanup_events_sent":0}))
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn abi() {
        assert_eq!(size_of::<Input>(), 40)
    }
    #[test]
    fn chords() {
        assert!(chord("CTRL+A").is_ok());
        assert!(chord("Alt+F4").is_err());
        assert!(chord("Win+R").is_err());
        assert!(chord("ESC").is_err());
        assert!(chord("Ctrl+Ctrl+A").is_err());
        assert!(chord("Ctrl+A+B").is_err());
    }
}
