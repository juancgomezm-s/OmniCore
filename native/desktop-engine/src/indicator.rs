//! User-visible, non-activating desktop indicator owned by the native driver.
//! Cancellation is latched; notify Host only after owned input has been released.
#![allow(non_snake_case)]
use crate::{
    native::{Handle, Point, Rect},
    server::{Error, Result},
};
use std::{
    cell::RefCell,
    mem::{size_of, zeroed},
    sync::atomic::{AtomicBool, AtomicIsize, Ordering},
    thread::JoinHandle,
};

static CANCELLED: AtomicBool = AtomicBool::new(false);
static ACTIVE: AtomicBool = AtomicBool::new(false);
static NOTIFIED: AtomicBool = AtomicBool::new(false);
static TARGET: AtomicIsize = AtomicIsize::new(0);
static CURTAIN: AtomicIsize = AtomicIsize::new(0);
static BANNER: AtomicIsize = AtomicIsize::new(0);
pub const CANCEL_SIGNAL: &str = "OMNICORE_DESKTOP_CANCELLED";
const WIDTH: i32 = 650;
const HEIGHT: i32 = 82;
#[repr(C)]
struct WindowClass {
    size: u32,
    style: u32,
    proc: unsafe extern "system" fn(Handle, u32, usize, isize) -> isize,
    extra: i32,
    window_extra: i32,
    instance: Handle,
    icon: Handle,
    cursor: Handle,
    brush: Handle,
    menu: *const u16,
    name: *const u16,
    small_icon: Handle,
}
#[repr(C)]
struct Message {
    window: Handle,
    message: u32,
    wparam: usize,
    lparam: isize,
    time: u32,
    point: Point,
    private: u32,
}
#[repr(C)]
struct Paint {
    dc: Handle,
    erase: i32,
    rect: Rect,
    restore: i32,
    update: i32,
    reserved: [u8; 32],
}
#[link(name = "user32")]
unsafe extern "system" {
    fn RegisterClassExW(class: *const WindowClass) -> u16;
    fn UnregisterClassW(name: *const u16, instance: Handle) -> i32;
    fn CreateWindowExW(
        ex: u32,
        class: *const u16,
        title: *const u16,
        style: u32,
        x: i32,
        y: i32,
        width: i32,
        height: i32,
        parent: Handle,
        menu: Handle,
        instance: Handle,
        param: *const std::ffi::c_void,
    ) -> Handle;
    fn DestroyWindow(window: Handle) -> i32;
    fn DefWindowProcW(window: Handle, message: u32, wparam: usize, lparam: isize) -> isize;
    fn GetMessageW(message: *mut Message, window: Handle, min: u32, max: u32) -> i32;
    fn TranslateMessage(message: *const Message) -> i32;
    fn DispatchMessageW(message: *const Message) -> isize;
    fn PostMessageW(window: Handle, message: u32, wparam: usize, lparam: isize) -> i32;
    fn PostQuitMessage(code: i32);
    fn SetLayeredWindowAttributes(window: Handle, color: u32, alpha: u8, flags: u32) -> i32;
    fn SetWindowDisplayAffinity(window: Handle, affinity: u32) -> i32;
    fn SetWindowPos(
        window: Handle,
        after: Handle,
        x: i32,
        y: i32,
        width: i32,
        height: i32,
        flags: u32,
    ) -> i32;
    fn ShowWindow(window: Handle, cmd: i32) -> i32;
    fn SetTimer(window: Handle, id: usize, millis: u32, callback: usize) -> usize;
    fn KillTimer(window: Handle, id: usize) -> i32;
    fn RegisterHotKey(window: Handle, id: i32, mods: u32, key: u32) -> i32;
    fn UnregisterHotKey(window: Handle, id: i32) -> i32;
    fn GetAsyncKeyState(key: i32) -> i16;
    fn GetSystemMetrics(index: i32) -> i32;
    fn BeginPaint(window: Handle, paint: *mut Paint) -> Handle;
    fn EndPaint(window: Handle, paint: *const Paint) -> i32;
    fn GetClientRect(window: Handle, rect: *mut Rect) -> i32;
    fn FillRect(dc: Handle, rect: *const Rect, brush: Handle) -> i32;
    fn DrawTextW(dc: Handle, text: *const u16, len: i32, rect: *mut Rect, format: u32) -> i32;
    fn InvalidateRect(window: Handle, rect: *const Rect, erase: i32) -> i32;
    fn SendMessageW(window: Handle, message: u32, wparam: usize, lparam: isize) -> isize;
    fn LoadCursorW(instance: Handle, name: *const u16) -> Handle;
    fn SetWindowRgn(window: Handle, region: Handle, redraw: i32) -> i32;
}
#[link(name = "gdi32")]
unsafe extern "system" {
    fn CreateSolidBrush(color: u32) -> Handle;
    fn DeleteObject(object: Handle) -> i32;
    fn SelectObject(dc: Handle, object: Handle) -> Handle;
    fn CreateFontW(
        height: i32,
        width: i32,
        escapement: i32,
        orientation: i32,
        weight: i32,
        italic: u32,
        underline: u32,
        strike: u32,
        charset: u32,
        out: u32,
        clip: u32,
        quality: u32,
        pitch: u32,
        face: *const u16,
    ) -> Handle;
    fn SetTextColor(dc: Handle, color: u32) -> u32;
    fn SetBkMode(dc: Handle, mode: i32) -> i32;
    fn CreateRoundRectRgn(
        left: i32,
        top: i32,
        right: i32,
        bottom: i32,
        ellipse_width: i32,
        ellipse_height: i32,
    ) -> Handle;
}
#[link(name = "kernel32")]
unsafe extern "system" {
    fn GetModuleHandleW(name: *const u16) -> Handle;
}
fn wide(text: &str) -> Vec<u16> {
    text.encode_utf16().chain(Some(0)).collect()
}
fn rgb(r: u8, g: u8, b: u8) -> u32 {
    r as u32 | ((g as u32) << 8) | ((b as u32) << 16)
}
pub fn cancelled() -> bool {
    CANCELLED.load(Ordering::SeqCst)
}
pub fn check() -> Result<()> {
    if cancelled() {
        Err(Error::new(
            "CANCELLED",
            "Desktop automation cancelled by the user.",
        ))
    } else {
        Ok(())
    }
}
pub fn target(window: Handle) {
    TARGET.store(window, Ordering::SeqCst);
}
pub fn is_owned(window: Handle) -> bool {
    window != 0
        && (window == CURTAIN.load(Ordering::SeqCst) || window == BANNER.load(Ordering::SeqCst))
}
fn notify_if_safe() {
    if cancelled() && !ACTIVE.load(Ordering::SeqCst) && !NOTIFIED.swap(true, Ordering::SeqCst) {
        eprintln!("{CANCEL_SIGNAL}");
    }
}
pub struct Request;
impl Request {
    pub fn begin() -> Self {
        ACTIVE.store(true, Ordering::SeqCst);
        Self
    }
}
impl Drop for Request {
    fn drop(&mut self) {
        ACTIVE.store(false, Ordering::SeqCst);
        notify_if_safe();
    }
}
pub fn cancel() {
    CANCELLED.store(true, Ordering::SeqCst);
    notify_if_safe();
}

struct Texts {
    active: &'static str,
    cancel: &'static str,
    select: &'static str,
    window: &'static str,
}
fn texts(locale: &str) -> Texts {
    match locale.split(['-', '_']).next().unwrap_or("es") {
        "en" => Texts {
            active: "OmniCore is using your computer",
            cancel: "Cancel (Esc)",
            select: "Capture is limited to the selected window",
            window: "Window only",
        },
        "fr" => Texts {
            active: "OmniCore utilise votre ordinateur",
            cancel: "Annuler (Échap)",
            select: "Capture limitée à la fenêtre sélectionnée",
            window: "Fenêtre uniquement",
        },
        "it" => Texts {
            active: "OmniCore sta usando il computer",
            cancel: "Annulla (Esc)",
            select: "Acquisizione limitata alla finestra selezionata",
            window: "Solo finestra",
        },
        _ => Texts {
            active: "OmniCore está usando el equipo",
            cancel: "Cancelar (Esc)",
            select: "Captura limitada a la ventana seleccionada",
            window: "Sólo ventana",
        },
    }
}
struct Ui {
    curtain: Handle,
    banner: Handle,
    font: Handle,
    small_font: Handle,
    labels: Texts,
    last_target: Handle,
    detail: String,
    ticks: u32,
}
thread_local! {static UI:RefCell<Option<Ui>>=const{RefCell::new(None)};}
unsafe fn fill(dc: Handle, rect: Rect, color: u32) {
    unsafe {
        let brush = CreateSolidBrush(color);
        if brush != 0 {
            FillRect(dc, &rect, brush);
            DeleteObject(brush);
        }
    }
}
unsafe fn draw(dc: Handle, text: &str, rect: Rect, font: Handle) {
    unsafe {
        let old = SelectObject(dc, font);
        let value = wide(text);
        let mut rect = rect;
        DrawTextW(dc, value.as_ptr(), -1, &mut rect, 0x20 | 0x4 | 0x8000);
        SelectObject(dc, old);
    }
}
unsafe fn paint(window: Handle) {
    unsafe {
        let mut ps: Paint = zeroed();
        let dc = BeginPaint(window, &mut ps);
        if dc == 0 {
            return;
        }
        UI.with(|state| {
            if let Some(ui) = state.borrow().as_ref() {
                let mut rect = Rect::default();
                GetClientRect(window, &mut rect);
                if window == ui.curtain {
                    // At 6% opacity, broad, graduated edge bands provide a soft purple wash.
                    fill(dc, rect, rgb(155, 112, 245));
                    for band in 0..18 {
                        let inset = band * 3;
                        let strength = (band * 3) as u8;
                        let color = rgb(92 + strength, 49 + strength, 191 + strength);
                        fill(
                            dc,
                            Rect {
                                left: inset,
                                top: inset,
                                right: rect.right - inset,
                                bottom: inset + 3,
                            },
                            color,
                        );
                        fill(
                            dc,
                            Rect {
                                left: inset,
                                top: rect.bottom - inset - 3,
                                right: rect.right - inset,
                                bottom: rect.bottom - inset,
                            },
                            color,
                        );
                        fill(
                            dc,
                            Rect {
                                left: inset,
                                top: inset,
                                right: inset + 3,
                                bottom: rect.bottom - inset,
                            },
                            color,
                        );
                        fill(
                            dc,
                            Rect {
                                left: rect.right - inset - 3,
                                top: inset,
                                right: rect.right - inset,
                                bottom: rect.bottom - inset,
                            },
                            color,
                        );
                    }
                } else {
                    fill(dc, rect, rgb(83, 43, 137));
                    SetBkMode(dc, 1);
                    SetTextColor(dc, rgb(255, 255, 255));
                    draw(
                        dc,
                        ui.labels.active,
                        Rect {
                            left: 18,
                            top: 12,
                            right: 480,
                            bottom: 38,
                        },
                        ui.font,
                    );
                    SetTextColor(dc, rgb(229, 217, 255));
                    draw(
                        dc,
                        &ui.detail,
                        Rect {
                            left: 18,
                            top: 44,
                            right: 480,
                            bottom: 67,
                        },
                        ui.small_font,
                    );
                }
            }
        });
        EndPaint(window, &ps);
    }
}
unsafe extern "system" fn procedure(
    window: Handle,
    message: u32,
    wparam: usize,
    lparam: isize,
) -> isize {
    unsafe {
        match message {
            0xf => {
                paint(window);
                0
            }
            0x14 => 1,
            0x21 => 3, // MA_NOACTIVATE: the indicator never takes the target's keyboard focus.
            0x84 if window == CURTAIN.load(Ordering::SeqCst) => -1, // HTTRANSPARENT
            0x111 if wparam & 0xffff == 1001 => {
                cancel();
                ShowWindow(CURTAIN.load(Ordering::SeqCst), 0);
                ShowWindow(window, 0);
                0
            }
            0x312 => {
                cancel();
                ShowWindow(CURTAIN.load(Ordering::SeqCst), 0);
                ShowWindow(window, 0);
                0
            }
            0x113 => {
                if GetAsyncKeyState(27) < 0 {
                    cancel();
                }
                if cancelled() {
                    ShowWindow(CURTAIN.load(Ordering::SeqCst), 0);
                    ShowWindow(window, 0);
                    return 0;
                }
                UI.with(|state| {
                    if let Some(ui) = state.borrow_mut().as_mut() {
                        ui.ticks += 1;
                        let target = TARGET.load(Ordering::SeqCst);
                        if target != ui.last_target {
                            ui.last_target = target;
                            ui.detail = if target == 0 {
                                ui.labels.select.to_string()
                            } else {
                                let title = crate::native::describe(target)
                                    .ok()
                                    .and_then(|w| w["title"].as_str().map(str::to_owned))
                                    .unwrap_or_default();
                                format!(
                                    "{}: {}",
                                    ui.labels.window,
                                    title.chars().take(70).collect::<String>()
                                )
                            };
                            InvalidateRect(ui.banner, std::ptr::null(), 0);
                        }
                        if ui.ticks.is_multiple_of(50) {
                            let x = GetSystemMetrics(76);
                            let y = GetSystemMetrics(77);
                            let width = GetSystemMetrics(78);
                            let height = GetSystemMetrics(79);
                            SetWindowPos(ui.curtain, -1, x, y, width, height, 0x10);
                            SetWindowPos(
                                ui.banner,
                                -1,
                                (GetSystemMetrics(0) - WIDTH) / 2,
                                24,
                                WIDTH,
                                HEIGHT,
                                0x10,
                            );
                        }
                    }
                });
                0
            }
            0x10 => {
                KillTimer(window, 1);
                UnregisterHotKey(window, 1);
                UI.with(|state| {
                    if let Some(ui) = state.borrow_mut().take() {
                        DestroyWindow(ui.banner);
                        DestroyWindow(ui.curtain);
                        DeleteObject(ui.font);
                        DeleteObject(ui.small_font);
                    }
                });
                CURTAIN.store(0, Ordering::SeqCst);
                BANNER.store(0, Ordering::SeqCst);
                PostQuitMessage(0);
                0
            }
            _ => DefWindowProcW(window, message, wparam, lparam),
        }
    }
}
pub struct Indicator {
    window: Handle,
    thread: Option<JoinHandle<()>>,
}
impl Indicator {
    pub fn start(locale: &str) -> Result<Self> {
        let locale = locale.to_string();
        let (tx, rx) = std::sync::mpsc::sync_channel(1);
        let thread = std::thread::spawn(move || unsafe {
            let instance = GetModuleHandleW(std::ptr::null());
            let name = wide("OmniCoreDesktopIndicator");
            let class = WindowClass {
                size: size_of::<WindowClass>() as u32,
                style: 0,
                proc: procedure,
                extra: 0,
                window_extra: 0,
                instance,
                icon: 0,
                cursor: LoadCursorW(0, 32512usize as *const u16),
                brush: 0,
                menu: std::ptr::null(),
                name: name.as_ptr(),
                small_icon: 0,
            };
            if RegisterClassExW(&class) == 0 {
                let _ = tx.send(Err("Cannot register desktop indicator."));
                return;
            }
            let curtain = CreateWindowExW(
                0x80000 | 0x20 | 0x8000000 | 0x80 | 0x8,
                name.as_ptr(),
                wide("OmniCore · Purple activity curtain").as_ptr(),
                0x80000000,
                GetSystemMetrics(76),
                GetSystemMetrics(77),
                GetSystemMetrics(78),
                GetSystemMetrics(79),
                0,
                0,
                instance,
                std::ptr::null(),
            );
            let banner = CreateWindowExW(
                0x8000000 | 0x80 | 0x8,
                name.as_ptr(),
                wide("OmniCore · Desktop activity").as_ptr(),
                0x80000000,
                (GetSystemMetrics(0) - WIDTH) / 2,
                24,
                WIDTH,
                HEIGHT,
                0,
                0,
                instance,
                std::ptr::null(),
            );
            let font = CreateFontW(
                -20,
                0,
                0,
                0,
                600,
                0,
                0,
                0,
                1,
                0,
                0,
                5,
                0,
                wide("Segoe UI").as_ptr(),
            );
            let small = CreateFontW(
                -15,
                0,
                0,
                0,
                400,
                0,
                0,
                0,
                1,
                0,
                0,
                5,
                0,
                wide("Segoe UI").as_ptr(),
            );
            if curtain == 0
                || banner == 0
                || font == 0
                || small == 0
                || SetLayeredWindowAttributes(curtain, 0, 16, 2) == 0
            {
                if banner != 0 {
                    DestroyWindow(banner);
                }
                if curtain != 0 {
                    DestroyWindow(curtain);
                }
                if font != 0 {
                    DeleteObject(font);
                }
                if small != 0 {
                    DeleteObject(small);
                }
                UnregisterClassW(name.as_ptr(), instance);
                let _ = tx.send(Err("Cannot create visible desktop indicator."));
                return;
            }
            // Target-window PrintWindow excludes these windows independently of display affinity.
            SetWindowDisplayAffinity(curtain, 0x11);
            SetWindowDisplayAffinity(banner, 0x11);
            let region = CreateRoundRectRgn(0, 0, WIDTH, HEIGHT, 18, 18);
            if region != 0 && SetWindowRgn(banner, region, 0) == 0 {
                DeleteObject(region);
            }
            let labels = texts(&locale);
            let button = CreateWindowExW(
                0,
                wide("BUTTON").as_ptr(),
                wide(labels.cancel).as_ptr(),
                0x50000000,
                490,
                21,
                145,
                40,
                banner,
                1001,
                instance,
                std::ptr::null(),
            );
            if button == 0 {
                DestroyWindow(banner);
                DestroyWindow(curtain);
                DeleteObject(font);
                DeleteObject(small);
                UnregisterClassW(name.as_ptr(), instance);
                let _ = tx.send(Err("Cannot create cancellation control."));
                return;
            }
            SendMessageW(button, 0x30, small as usize, 1);
            let detail = labels.select.to_string();
            UI.with(|state| {
                *state.borrow_mut() = Some(Ui {
                    curtain,
                    banner,
                    font,
                    small_font: small,
                    labels,
                    last_target: 0,
                    detail,
                    ticks: 0,
                })
            });
            CURTAIN.store(curtain, Ordering::SeqCst);
            BANNER.store(banner, Ordering::SeqCst);
            SetTimer(banner, 1, 20, 0);
            RegisterHotKey(banner, 1, 0x4000, 27);
            ShowWindow(curtain, 4);
            ShowWindow(banner, 4); // SW_SHOWNOACTIVATE
            SetWindowPos(curtain, -1, 0, 0, 0, 0, 0x13);
            SetWindowPos(banner, -1, 0, 0, 0, 0, 0x13);
            let _ = tx.send(Ok(banner));
            let mut message: Message = zeroed();
            while GetMessageW(&mut message, 0, 0, 0) > 0 {
                TranslateMessage(&message);
                DispatchMessageW(&message);
            }
            UnregisterClassW(name.as_ptr(), instance);
        });
        match rx.recv_timeout(std::time::Duration::from_secs(5)) {
            Ok(Ok(window)) => Ok(Self {
                window,
                thread: Some(thread),
            }),
            Ok(Err(message)) => {
                let _ = thread.join();
                Err(Error::new("INDICATOR_UNAVAILABLE", message))
            }
            Err(_) => Err(Error::new(
                "INDICATOR_UNAVAILABLE",
                "Desktop indicator startup timed out.",
            )),
        }
    }
}
impl Drop for Indicator {
    fn drop(&mut self) {
        unsafe {
            PostMessageW(self.window, 0x10, 0, 0);
        }
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn labels_available_in_supported_languages() {
        for locale in ["es", "en", "fr", "it"] {
            let labels = texts(locale);
            assert!(!labels.active.is_empty());
            assert!(!labels.cancel.is_empty());
        }
    }
    #[test]
    fn color_encoding() {
        assert_eq!(rgb(139, 92, 246), 0xf65c8b);
        assert_eq!(size_of::<WindowClass>(), 80);
    }
}
