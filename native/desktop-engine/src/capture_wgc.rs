//! One-shot Windows.Graphics.Capture. No implicit GDI fallback.
//!
//! A dedicated MTA owns WinRT and D3D objects. The caller has a three-second
//! deadline; blocked OS/driver calls cannot be forcefully interrupted in-process.
//! A timed-out worker cooperatively closes its resources and prevents new WGC
//! workers until it exits, avoiding an unbounded queue of background captures.
use anyhow::{Context, Result, anyhow, bail};
use std::{
    io::Cursor,
    mem::size_of,
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
        mpsc,
    },
    thread,
    time::{Duration, Instant},
};
use windows::{
    Graphics::{
        Capture::{
            Direct3D11CaptureFrame, Direct3D11CaptureFramePool, GraphicsCaptureItem,
            GraphicsCaptureSession,
        },
        DirectX::{Direct3D11::IDirect3DDevice, DirectXPixelFormat},
    },
    Win32::{
        Foundation::{HMODULE, HWND, RECT},
        Graphics::{
            Direct3D::{D3D_DRIVER_TYPE, D3D_DRIVER_TYPE_HARDWARE, D3D_DRIVER_TYPE_WARP},
            Direct3D11::{
                D3D11_CPU_ACCESS_READ, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                D3D11_MAP_FLAG_DO_NOT_WAIT, D3D11_MAP_READ, D3D11_MAPPED_SUBRESOURCE,
                D3D11_SDK_VERSION, D3D11_TEXTURE2D_DESC, D3D11_USAGE_STAGING, D3D11CreateDevice,
                ID3D11Device, ID3D11DeviceContext, ID3D11Texture2D,
            },
            Dwm::{DWMWA_EXTENDED_FRAME_BOUNDS, DwmGetWindowAttribute},
            Dxgi::{Common::DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_ERROR_WAS_STILL_DRAWING, IDXGIDevice},
        },
        System::WinRT::{
            Direct3D11::{CreateDirect3D11DeviceFromDXGIDevice, IDirect3DDxgiInterfaceAccess},
            Graphics::Capture::IGraphicsCaptureItemInterop,
            RO_INIT_MULTITHREADED, RoInitialize, RoUninitialize,
        },
        UI::{
            HiDpi::{DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, SetThreadDpiAwarenessContext},
            WindowsAndMessaging::{GetWindowThreadProcessId, IsIconic, IsWindow},
        },
    },
    core::{HRESULT, Interface},
};

/// Pixel geometry is checked against physical DWM extended frame bounds.
pub struct Capture {
    pub png: Vec<u8>,
    pub origin_x: i32,
    pub origin_y: i32,
    pub width: u32,
    pub height: u32,
}

/// WGC's free-threaded callbacks can outlive Close/RoUninitialize briefly.
/// Keep the system capture implementation mapped until this isolated process
/// exits, so delayed callbacks cannot execute inside an unloaded DLL.
fn pin_capture_runtime() -> Result<()> {
    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn GetSystemDirectoryW(buffer: *mut u16, size: u32) -> u32;
        fn LoadLibraryExW(name: *const u16, file: isize, flags: u32) -> isize;
        fn GetModuleHandleExW(flags: u32, name: *const u16, module: *mut isize) -> i32;
        fn FreeLibrary(module: isize) -> i32;
    }
    unsafe {
        let mut system = [0u16; 32768];
        let length = GetSystemDirectoryW(system.as_mut_ptr(), system.len() as u32) as usize;
        if length == 0 || length >= system.len() {
            bail!("WGC system directory unavailable");
        }
        let mut path = system[..length].to_vec();
        path.extend("\\GraphicsCapture.dll".encode_utf16());
        path.push(0);
        let loaded = LoadLibraryExW(path.as_ptr(), 0, 0x800); // LOAD_LIBRARY_SEARCH_SYSTEM32
        if loaded == 0 {
            bail!("WGC system runtime could not be loaded");
        }
        let mut pinned = 0;
        let ok = GetModuleHandleExW(1, path.as_ptr(), &mut pinned); // GET_MODULE_HANDLE_EX_FLAG_PIN
        FreeLibrary(loaded);
        if ok == 0 || pinned == 0 {
            bail!("WGC runtime lifetime could not be secured");
        }
    }
    Ok(())
}

static IN_FLIGHT: AtomicBool = AtomicBool::new(false);
struct InFlight;
impl Drop for InFlight {
    fn drop(&mut self) {
        IN_FLIGHT.store(false, Ordering::Release);
    }
}
struct Apartment;
impl Drop for Apartment {
    fn drop(&mut self) {
        unsafe { RoUninitialize() };
    }
}
struct SessionResources {
    pool: Direct3D11CaptureFramePool,
    session: GraphicsCaptureSession,
}
impl Drop for SessionResources {
    fn drop(&mut self) {
        let _ = self.session.Close();
        let _ = self.pool.Close();
    }
}
struct FrameGuard(Direct3D11CaptureFrame);
impl Drop for FrameGuard {
    fn drop(&mut self) {
        let _ = self.0.Close();
    }
}
struct Mapped<'a> {
    context: &'a ID3D11DeviceContext,
    texture: &'a ID3D11Texture2D,
}
impl Drop for Mapped<'_> {
    fn drop(&mut self) {
        unsafe { self.context.Unmap(self.texture, 0) };
    }
}

/// Captures an existing non-minimized window without activating it.
/// The system's normal capture border remains enabled; it is not suppressed.
pub fn capture(hwnd: usize) -> Result<Capture> {
    if hwnd == 0 || hwnd > isize::MAX as usize {
        bail!("invalid HWND");
    }
    IN_FLIGHT
        .compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire)
        .map_err(|_| anyhow!("wgc_busy: previous capture is still running or cleaning up"))?;
    let busy = InFlight;
    let cancelled = Arc::new(AtomicBool::new(false));
    let worker_cancelled = Arc::clone(&cancelled);
    let deadline = Instant::now() + Duration::from_secs(3);
    let (tx, rx) = mpsc::sync_channel(1);
    thread::Builder::new()
        .name("desktop-wgc-mta".into())
        .spawn(move || {
            let _busy = busy;
            let result = capture_worker(hwnd, deadline, &worker_cancelled);
            let _ = tx.send(result);
        })
        .context("could not spawn WGC worker")?;
    match rx.recv_timeout(deadline.saturating_duration_since(Instant::now())) {
        Ok(result) => result,
        Err(error) => {
            cancelled.store(true, Ordering::Release);
            bail!(
                "wgc_timeout: {error}; cleanup is cooperative if an OS/driver call remains blocked"
            )
        }
    }
}

fn check_deadline(deadline: Instant, cancelled: &AtomicBool) -> Result<()> {
    if cancelled.load(Ordering::Acquire) || Instant::now() >= deadline {
        bail!("wgc_timeout: capture exceeded its three-second deadline");
    }
    Ok(())
}

fn capture_worker(hwnd: usize, deadline: Instant, cancelled: &AtomicBool) -> Result<Capture> {
    pin_capture_runtime()?;
    unsafe { RoInitialize(RO_INIT_MULTITHREADED) }.context("WGC WinRT initialization failed")?;
    let _apartment = Apartment;
    let previous =
        unsafe { SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) };
    if previous.0.is_null() {
        bail!("could not configure physical coordinates for WGC");
    }
    check_deadline(deadline, cancelled)?;
    if !GraphicsCaptureSession::IsSupported().context("WGC capability query failed")? {
        bail!("wgc_unsupported: Windows.Graphics.Capture is not supported");
    }
    // The D3D/WinRT capture objects below are all released before PNG encoding.
    let (pixels, bounds, width, height) = read_pixels(hwnd, deadline, cancelled)?;
    check_deadline(deadline, cancelled)?;
    let image =
        image::RgbaImage::from_raw(width, height, pixels).context("invalid WGC image buffer")?;
    let mut png = Cursor::new(Vec::new());
    image::DynamicImage::ImageRgba8(image).write_to(&mut png, image::ImageFormat::Png)?;
    check_deadline(deadline, cancelled)?;
    Ok(Capture {
        png: png.into_inner(),
        origin_x: bounds.left,
        origin_y: bounds.top,
        width,
        height,
    })
}

fn window_bounds(hwnd: HWND) -> Result<(RECT, u32)> {
    unsafe {
        if !IsWindow(Some(hwnd)).as_bool() {
            bail!("invalid_window");
        }
        if IsIconic(hwnd).as_bool() {
            bail!("window_minimized: WGC requires a non-minimized window");
        }
        let mut process_id = 0;
        if GetWindowThreadProcessId(hwnd, Some(&mut process_id)) == 0 || process_id == 0 {
            bail!("could not identify WGC target process");
        }
        let mut bounds = RECT::default();
        DwmGetWindowAttribute(
            hwnd,
            DWMWA_EXTENDED_FRAME_BOUNDS,
            (&mut bounds as *mut RECT).cast(),
            size_of::<RECT>() as u32,
        )
        .context("DWM extended frame bounds unavailable; WGC geometry cannot be established")?;
        validate_dimensions(bounds.right - bounds.left, bounds.bottom - bounds.top)?;
        Ok((bounds, process_id))
    }
}

fn validate_dimensions(width: i32, height: i32) -> Result<(u32, u32)> {
    if width <= 0 || height <= 0 || width as u64 * height as u64 > 20_000_000 {
        bail!("invalid or oversized WGC dimensions: {width}x{height}");
    }
    Ok((width as u32, height as u32))
}

fn device(driver: D3D_DRIVER_TYPE) -> Result<(ID3D11Device, ID3D11DeviceContext)> {
    unsafe {
        let (mut device, mut context) = (None, None);
        D3D11CreateDevice(
            None,
            driver,
            HMODULE::default(),
            D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            None,
            D3D11_SDK_VERSION,
            Some(&mut device),
            None,
            Some(&mut context),
        )?;
        Ok((
            device.context("D3D11 device missing")?,
            context.context("D3D11 context missing")?,
        ))
    }
}

fn read_pixels(
    hwnd: usize,
    deadline: Instant,
    cancelled: &AtomicBool,
) -> Result<(Vec<u8>, RECT, u32, u32)> {
    let hwnd = HWND(hwnd as *mut _);
    let (bounds, process_id) = window_bounds(hwnd)?;
    let (expected_width, expected_height) =
        validate_dimensions(bounds.right - bounds.left, bounds.bottom - bounds.top)?;
    let interop: IGraphicsCaptureItemInterop = windows::core::factory::<GraphicsCaptureItem, _>()?;
    let item: GraphicsCaptureItem =
        unsafe { interop.CreateForWindow(hwnd) }.context("WGC CreateForWindow failed")?;
    let item_size = item.Size()?;
    let (item_width, item_height) = validate_dimensions(item_size.Width, item_size.Height)?;
    if (item_width, item_height) != (expected_width, expected_height) {
        bail!(
            "wgc_geometry_mismatch: capture item {}x{} differs from DWM bounds {}x{}; no scale is inferred",
            item_width,
            item_height,
            expected_width,
            expected_height
        );
    }
    let (native_device, context) = device(D3D_DRIVER_TYPE_HARDWARE).or_else(|hardware_error| {
        device(D3D_DRIVER_TYPE_WARP).with_context(|| {
            format!("D3D hardware failed ({hardware_error}); WARP fallback also failed")
        })
    })?;
    check_deadline(deadline, cancelled)?;
    let dxgi: IDXGIDevice = native_device.cast()?;
    let winrt: IDirect3DDevice = unsafe { CreateDirect3D11DeviceFromDXGIDevice(&dxgi) }?.cast()?;
    let pool = Direct3D11CaptureFramePool::CreateFreeThreaded(
        &winrt,
        DirectXPixelFormat::B8G8R8A8UIntNormalized,
        2,
        item_size,
    )?;
    let session = match pool.CreateCaptureSession(&item) {
        Ok(session) => session,
        Err(error) => {
            let _ = pool.Close();
            return Err(error.into());
        }
    };
    let resources = SessionResources { pool, session };
    resources
        .session
        .StartCapture()
        .context("WGC StartCapture failed")?;
    let frame = loop {
        check_deadline(deadline, cancelled)?;
        match resources.pool.TryGetNextFrame() {
            Ok(frame) => break FrameGuard(frame),
            // WinRT returns a null frame when the free-threaded pool is empty.
            Err(error)
                if error.code() == HRESULT(0) || error.code() == HRESULT(0x80004003_u32 as i32) =>
            {
                thread::sleep(Duration::from_millis(8));
            }
            Err(error) => return Err(error).context("WGC frame acquisition failed"),
        }
    };
    let size = frame.0.ContentSize()?;
    let (width, height) = validate_dimensions(size.Width, size.Height)?;
    if (width, height) != (expected_width, expected_height) {
        bail!(
            "wgc_geometry_mismatch: frame {width}x{height}, DWM {expected_width}x{expected_height}; no scale is inferred"
        );
    }
    let access: IDirect3DDxgiInterfaceAccess = frame.0.Surface()?.cast()?;
    let texture: ID3D11Texture2D = unsafe { access.GetInterface() }?;
    let mut description = D3D11_TEXTURE2D_DESC::default();
    unsafe { texture.GetDesc(&mut description) };
    if description.Format != DXGI_FORMAT_B8G8R8A8_UNORM
        || description.Width < width
        || description.Height < height
        || description.SampleDesc.Count != 1
    {
        bail!("unsupported WGC texture format, dimensions, or multisampling");
    }
    description.Usage = D3D11_USAGE_STAGING;
    description.BindFlags = 0;
    description.MiscFlags = 0;
    description.CPUAccessFlags = D3D11_CPU_ACCESS_READ.0 as u32;
    let mut staging = None;
    unsafe { native_device.CreateTexture2D(&description, None, Some(&mut staging)) }?;
    let staging = staging.context("D3D11 staging texture missing")?;
    unsafe {
        context.CopyResource(&staging, &texture);
        context.Flush();
    }
    let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
    loop {
        check_deadline(deadline, cancelled)?;
        match unsafe {
            context.Map(
                &staging,
                0,
                D3D11_MAP_READ,
                D3D11_MAP_FLAG_DO_NOT_WAIT.0 as u32,
                Some(&mut mapped),
            )
        } {
            Ok(()) => break,
            Err(error) if error.code() == DXGI_ERROR_WAS_STILL_DRAWING => {
                thread::sleep(Duration::from_millis(3));
            }
            Err(error) => return Err(error).context("D3D11 staging readback failed"),
        }
    }
    let mapped_guard = Mapped {
        context: &context,
        texture: &staging,
    };
    if mapped.pData.is_null() || mapped.RowPitch < width * 4 {
        bail!("invalid WGC mapped texture pitch or pointer");
    }
    let mut rgba = vec![0_u8; width as usize * height as usize * 4];
    for row in 0..height as usize {
        check_deadline(deadline, cancelled)?;
        let source = unsafe {
            std::slice::from_raw_parts(
                (mapped.pData as *const u8).add(row * mapped.RowPitch as usize),
                width as usize * 4,
            )
        };
        let destination = &mut rgba[row * width as usize * 4..(row + 1) * width as usize * 4];
        bgra_to_rgba(source, destination)?;
    }
    drop(mapped_guard);
    let (after, after_pid) = window_bounds(hwnd)?;
    if process_id != after_pid || bounds != after {
        bail!("window_changed_during_capture: target identity or DWM bounds changed");
    }
    Ok((rgba, bounds, width, height))
}

fn bgra_to_rgba(source: &[u8], destination: &mut [u8]) -> Result<()> {
    if source.len() != destination.len() || !source.len().is_multiple_of(4) {
        bail!("invalid BGRA row length");
    }
    for (pixel, output) in source
        .as_chunks::<4>()
        .0
        .iter()
        .zip(destination.as_chunks_mut::<4>().0.iter_mut())
    {
        // Window captures are composited; expose an opaque screenshot rather
        // than accidentally treating undefined alpha as full transparency.
        output.copy_from_slice(&[pixel[2], pixel[1], pixel[0], 255]);
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn validates_geometry_without_allocating() {
        assert!(validate_dimensions(0, 400).is_err());
        assert!(validate_dimensions(-1, 400).is_err());
        assert!(validate_dimensions(10000, 10000).is_err());
        assert_eq!(validate_dimensions(1920, 1080).unwrap(), (1920, 1080));
    }

    #[test]
    fn swaps_bgra_channels_and_sets_opaque_alpha() {
        let mut result = [0_u8; 8];
        bgra_to_rgba(&[1, 2, 3, 0, 40, 50, 60, 200], &mut result).unwrap();
        assert_eq!(result, [3, 2, 1, 255, 60, 50, 40, 255]);
        assert!(bgra_to_rgba(&[1, 2, 3], &mut [0; 3]).is_err());
    }
}
