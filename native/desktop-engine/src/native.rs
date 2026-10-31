#![allow(non_snake_case)]
use crate::server::{Error, Result};
use serde_json::{Value, json};
use std::ffi::c_void;
use std::mem::size_of;
use std::ptr::null_mut;
pub type Handle = isize;
#[repr(C)]
#[derive(Clone, Copy, Default, Debug, PartialEq)]
pub struct Rect {
    pub left: i32,
    pub top: i32,
    pub right: i32,
    pub bottom: i32,
}
#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct Point {
    pub x: i32,
    pub y: i32,
}
#[repr(C)]
#[derive(Default)]
struct FileTime {
    low: u32,
    high: u32,
}
#[repr(C)]
struct BitmapHeader {
    size: u32,
    width: i32,
    height: i32,
    planes: u16,
    bits: u16,
    compression: u32,
    image_size: u32,
    x: i32,
    y: i32,
    used: u32,
    important: u32,
}
#[repr(C)]
struct BitmapInfo {
    header: BitmapHeader,
    colors: [u32; 1],
}
#[link(name = "user32")]
unsafe extern "system" {
    pub fn IsWindow(h: Handle) -> i32;
    pub fn IsWindowVisible(h: Handle) -> i32;
    pub fn IsIconic(h: Handle) -> i32;
    pub fn GetForegroundWindow() -> Handle;
    pub fn SetForegroundWindow(h: Handle) -> i32;
    pub fn ShowWindow(h: Handle, cmd: i32) -> i32;
    pub fn GetWindowRect(h: Handle, r: *mut Rect) -> i32;
    pub fn GetWindowThreadProcessId(h: Handle, p: *mut u32) -> u32;
    fn EnumWindows(callback: unsafe extern "system" fn(Handle, isize) -> i32, data: isize) -> i32;
    fn GetWindowTextW(h: Handle, text: *mut u16, len: i32) -> i32;
    fn GetClassNameW(h: Handle, text: *mut u16, len: i32) -> i32;
    fn SetProcessDpiAwarenessContext(ctx: isize) -> i32;
    fn SetThreadDpiAwarenessContext(ctx: isize) -> isize;
    fn GetWindowDpiAwarenessContext(h: Handle) -> isize;
    fn GetAwarenessFromDpiAwarenessContext(ctx: isize) -> i32;
    fn GetWindowDC(h: Handle) -> Handle;
    fn GetDC(h: Handle) -> Handle;
    fn ReleaseDC(h: Handle, dc: Handle) -> i32;
    fn PrintWindow(h: Handle, dc: Handle, flags: u32) -> i32;
    pub fn WindowFromPoint(p: Point) -> Handle;
    pub fn GetAncestor(h: Handle, flags: u32) -> Handle;
    pub fn GetAsyncKeyState(key: i32) -> i16;
}
#[link(name = "gdi32")]
unsafe extern "system" {
    fn CreateCompatibleDC(h: Handle) -> Handle;
    fn DeleteDC(h: Handle) -> i32;
    fn DeleteObject(h: Handle) -> i32;
    fn SelectObject(dc: Handle, obj: Handle) -> Handle;
    fn CreateDIBSection(
        dc: Handle,
        info: *const BitmapInfo,
        usage: u32,
        bits: *mut *mut c_void,
        section: Handle,
        offset: u32,
    ) -> Handle;
    fn GdiFlush() -> i32;
    fn BitBlt(
        dst: Handle,
        x: i32,
        y: i32,
        w: i32,
        h: i32,
        src: Handle,
        sx: i32,
        sy: i32,
        rop: u32,
    ) -> i32;
}
#[link(name = "kernel32")]
unsafe extern "system" {
    fn OpenProcess(access: u32, inherit: i32, pid: u32) -> Handle;
    fn CloseHandle(h: Handle) -> i32;
    fn QueryFullProcessImageNameW(h: Handle, flags: u32, path: *mut u16, size: *mut u32) -> i32;
    fn GetProcessTimes(
        h: Handle,
        c: *mut FileTime,
        e: *mut FileTime,
        k: *mut FileTime,
        u: *mut FileTime,
    ) -> i32;
}
pub fn physical_dpi() {
    unsafe {
        SetProcessDpiAwarenessContext(-4);
    }
}
#[link(name = "user32")]
unsafe extern "system" {
    fn GetDesktopWindow() -> Handle;
    fn GetShellWindow() -> Handle;
}
pub fn validate_target(h: Handle) -> Result<()> {
    let w = describe(h)?;
    if unsafe { h == GetDesktopWindow() || h == GetShellWindow() || GetAncestor(h, 2) != h }
        || pid(h) == std::process::id()
        || crate::indicator::is_owned(h)
        || matches!(
            w["class_name"].as_str(),
            Some(
                "Progman"
                    | "WorkerW"
                    | "Shell_TrayWnd"
                    | "Shell_SecondaryTrayWnd"
                    | "OmniCoreDesktopIndicator"
            )
        )
    {
        return Err(Error::new(
            "WINDOW_REQUIRED",
            "Select an application window; desktop, taskbar and indicator capture are unavailable.",
        ));
    }
    Ok(())
}
pub fn rect(h: Handle) -> Result<Rect> {
    unsafe {
        let mut r = Rect::default();
        if GetWindowRect(h, &mut r) == 0 {
            return Err(Error::new(
                "WINDOW_UNAVAILABLE",
                "Cannot read target bounds.",
            ));
        }
        Ok(r)
    }
}
pub fn bounds(r: Rect) -> Value {
    json!({"x":r.left,"y":r.top,"width":r.right-r.left,"height":r.bottom-r.top})
}
pub fn pid(h: Handle) -> u32 {
    unsafe {
        let mut p = 0;
        GetWindowThreadProcessId(h, &mut p);
        p
    }
}
pub fn describe(h: Handle) -> Result<Value> {
    unsafe {
        if IsWindow(h) == 0 {
            return Err(Error::new(
                "WINDOW_NOT_FOUND",
                "The target window no longer exists.",
            ));
        }
        let mut title = [0u16; 32768];
        let len = GetWindowTextW(h, title.as_mut_ptr(), title.len() as i32);
        let mut class = [0u16; 256];
        let n = GetClassNameW(h, class.as_mut_ptr(), class.len() as i32);
        Ok(
            json!({"id":format!("0x{h:x}"),"pid":pid(h),"title":String::from_utf16_lossy(&title[..len.max(0) as usize]),
        "app":process_path(pid(h)),"class_name":String::from_utf16_lossy(&class[..n.max(0) as usize]),"visible":IsWindowVisible(h)!=0,"minimized":IsIconic(h)!=0,"bounds":bounds(rect(h)?)}),
        )
    }
}
pub fn identity(window: &Value) -> Result<String> {
    unsafe {
        let p = window["pid"]
            .as_u64()
            .ok_or_else(|| Error::new("IDENTITY_UNAVAILABLE", "Missing target process."))?;
        let process = OpenProcess(0x1000, 0, p as u32);
        if process == 0 {
            return Err(Error::new(
                "IDENTITY_UNAVAILABLE",
                "Cannot verify process identity.",
            ));
        }
        let (mut c, mut e, mut k, mut u) = (
            FileTime::default(),
            FileTime::default(),
            FileTime::default(),
            FileTime::default(),
        );
        let ok = GetProcessTimes(process, &mut c, &mut e, &mut k, &mut u);
        CloseHandle(process);
        if ok == 0 {
            return Err(Error::new(
                "IDENTITY_UNAVAILABLE",
                "Cannot read process creation time.",
            ));
        }
        Ok(format!(
            "{}:{}:{}:{}",
            window["id"],
            p,
            ((c.high as u64) << 32) | c.low as u64,
            window["class_name"]
        ))
    }
}
pub fn windows(hidden: bool) -> Vec<Handle> {
    unsafe extern "system" fn collect(h: Handle, data: isize) -> i32 {
        unsafe {
            let list = &mut *(data as *mut Vec<Handle>);
            list.push(h);
            1
        }
    }
    let mut list = Vec::new();
    unsafe {
        EnumWindows(collect, &mut list as *mut _ as isize);
    }
    list.into_iter()
        .filter(|h| hidden || unsafe { IsWindowVisible(*h) != 0 })
        .collect()
}
pub fn hwnd(value: &Value) -> Result<Handle> {
    let id = if value.is_object() {
        &value["id"]
    } else {
        value
    };
    let text = id
        .as_str()
        .and_then(|s| s.strip_prefix("0x"))
        .ok_or_else(|| Error::new("INVALID_PARAMS", "Use a window id returned by discovery."))?;
    isize::from_str_radix(text, 16)
        .ok()
        .filter(|h| *h > 0)
        .ok_or_else(|| Error::new("INVALID_PARAMS", "Invalid window handle."))
}
struct CaptureResources {
    window: Handle,
    dc: Handle,
    memory: Handle,
    bitmap: Handle,
    old: Handle,
    dpi: isize,
}
impl Drop for CaptureResources {
    fn drop(&mut self) {
        unsafe {
            if self.old != 0 {
                SelectObject(self.memory, self.old);
            }
            if self.bitmap != 0 {
                DeleteObject(self.bitmap);
            }
            if self.memory != 0 {
                DeleteDC(self.memory);
            }
            if self.dc != 0 {
                ReleaseDC(self.window, self.dc);
            }
            if self.dpi != 0 {
                SetThreadDpiAwarenessContext(self.dpi);
            }
        }
    }
}
pub fn capture(data: &Value) -> Result<Value> {
    unsafe {
        let h = data["hwnd"]
            .as_i64()
            .ok_or_else(|| Error::new("INVALID_PARAMS", "Missing HWND."))?
            as Handle;
        validate_target(h)?;
        let requested = &data["bounds"];
        if IsWindow(h) == 0 || IsIconic(h) != 0 {
            return Err(Error::new(
                "WINDOW_NOT_VISIBLE",
                "Observe a visible non-minimized window.",
            ));
        }
        let physical = rect(h)?;
        if &bounds(physical) != requested {
            return Err(Error::new("WINDOW_CHANGED", "Target geometry changed."));
        }
        let (width, height) = (
            physical.right - physical.left,
            physical.bottom - physical.top,
        );
        if width <= 0
            || height <= 0
            || width > 16384
            || height > 16384
            || (width as u64 * height as u64) > 40_000_000
        {
            return Err(Error::new(
                "CAPTURE_TOO_LARGE",
                "Capture exceeds pixel budget.",
            ));
        }
        let mut resources = CaptureResources {
            window: h,
            dc: 0,
            memory: 0,
            bitmap: 0,
            old: 0,
            dpi: SetThreadDpiAwarenessContext(-4),
        };
        let visible =
            data["visible_pixels_allowed"] == true && data["capture_backend"] == "screen_gdi";
        let target = if visible {
            -4
        } else {
            GetWindowDpiAwarenessContext(h)
        };
        if target == 0 || SetThreadDpiAwarenessContext(target) == 0 {
            return Err(Error::new("DPI_ERROR", "Cannot select target DPI context."));
        }
        let awareness = GetAwarenessFromDpiAwarenessContext(target);
        let logical = rect(h)?;
        let (w, ht) = (logical.right - logical.left, logical.bottom - logical.top);
        if w <= 0 || ht <= 0 || (w as u64 * ht as u64) > 40_000_000 {
            return Err(Error::new(
                "CAPTURE_TOO_LARGE",
                "Native capture exceeds pixel budget.",
            ));
        }
        resources.dc = if visible {
            resources.window = 0;
            GetDC(0)
        } else {
            GetWindowDC(h)
        };
        resources.memory = CreateCompatibleDC(resources.dc);
        if resources.dc == 0 || resources.memory == 0 {
            return Err(Error::new(
                "CAPTURE_FAILED",
                "Cannot allocate capture context.",
            ));
        }
        let info = BitmapInfo {
            header: BitmapHeader {
                size: size_of::<BitmapHeader>() as u32,
                width: w,
                height: -ht,
                planes: 1,
                bits: 32,
                compression: 0,
                image_size: (w * ht * 4) as u32,
                x: 0,
                y: 0,
                used: 0,
                important: 0,
            },
            colors: [0],
        };
        let mut pixels = null_mut();
        resources.bitmap = CreateDIBSection(resources.memory, &info, 0, &mut pixels, 0, 0);
        if resources.bitmap == 0 || pixels.is_null() {
            return Err(Error::new("CAPTURE_FAILED", "Cannot allocate bitmap."));
        }
        resources.old = SelectObject(resources.memory, resources.bitmap);
        if resources.old == 0 || resources.old == -1 {
            resources.old = 0;
            return Err(Error::new("CAPTURE_FAILED", "Cannot select bitmap."));
        }
        std::ptr::write_bytes(pixels as *mut u8, 0, (w * ht * 4) as usize);
        let rendered = if visible {
            BitBlt(
                resources.memory,
                0,
                0,
                w,
                ht,
                resources.dc,
                physical.left,
                physical.top,
                0x40cc0020,
            )
        } else {
            PrintWindow(h, resources.memory, 2)
        };
        if rendered == 0 || GdiFlush() == 0 {
            return Err(Error::new(
                "CAPTURE_FAILED",
                "PrintWindow did not render the target.",
            ));
        }
        SetThreadDpiAwarenessContext(-4);
        if IsWindow(h) == 0 || IsIconic(h) != 0 || rect(h)? != physical {
            return Err(Error::new(
                "WINDOW_CHANGED",
                "Target changed during capture.",
            ));
        }
        let raw = std::slice::from_raw_parts(pixels as *const u8, (w * ht * 4) as usize);
        let mut rgb = Vec::with_capacity((w * ht * 3) as usize);
        for pixel in raw.as_chunks::<4>().0 {
            rgb.extend_from_slice(&[pixel[2], pixel[1], pixel[0]]);
        }
        let image = image::RgbImage::from_raw(w as u32, ht as u32, rgb)
            .ok_or_else(|| Error::new("CAPTURE_FAILED", "Invalid bitmap."))?;
        let scaled = (w, ht) != (width, height);
        let image = if scaled {
            image::imageops::resize(
                &image,
                width as u32,
                height as u32,
                image::imageops::FilterType::Lanczos3,
            )
        } else {
            image
        };
        let black = image.as_raw().iter().all(|b| *b == 0);
        let mut encoded = std::io::Cursor::new(Vec::new());
        image
            .write_to(&mut encoded, image::ImageFormat::Png)
            .map_err(|_| Error::new("CAPTURE_FAILED", "Cannot encode PNG."))?;
        if encoded.get_ref().len() > 8 * 1024 * 1024 {
            return Err(Error::new(
                "IMAGE_TOO_LARGE",
                "PNG exceeds inline image budget.",
            ));
        }
        let path = data["output_path"]
            .as_str()
            .ok_or_else(|| Error::new("INVALID_PARAMS", "Missing output path."))?;
        std::fs::write(path, encoded.get_ref())
            .map_err(|_| Error::new("CAPTURE_FAILED", "Cannot save PNG."))?;
        Ok(
            json!({"width":width,"height":height,"bounds":bounds(physical),"capture_scope":"window","window_id":format!("0x{h:x}"),"path":path,"backend":if visible{"screen_gdi"}else{"printwindow"},"occlusion_sensitive":visible,"native_capture_size":{"width":w,"height":ht},"scaled_for_dpi":scaled,"target_dpi_awareness":awareness,"all_black":black,"warnings":if black{vec!["Capture is black; rendering may be unsupported or protected."]}else{vec![]}}),
        )
    }
}

fn process_path(pid: u32) -> String {
    unsafe {
        let process = OpenProcess(0x1000, 0, pid);
        if process == 0 {
            return format!("pid:{pid}");
        }
        let mut path = vec![0u16; 32768];
        let mut length = path.len() as u32;
        let ok = QueryFullProcessImageNameW(process, 0, path.as_mut_ptr(), &mut length);
        CloseHandle(process);
        if ok == 0 {
            format!("pid:{pid}")
        } else {
            String::from_utf16_lossy(&path[..length as usize])
        }
    }
}
