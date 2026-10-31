use crate::{
    native,
    server::{Error, Result},
};
use serde_json::{Value, json};

pub fn map_coordinate(frame: &Value, value: i64, horizontal: bool) -> Result<i64> {
    let (dimension, physical, origin) = if horizontal {
        ("width", "physicalWidth", "originX")
    } else {
        ("height", "physicalHeight", "originY")
    };
    let size = frame[dimension].as_i64().unwrap_or(0);
    let physical = frame[physical].as_i64().unwrap_or(0);
    if size <= 0 || physical <= 0 || value < 0 || value >= size {
        return Err(Error::new(
            "OUTSIDE_TARGET",
            "Point lies outside the observed image.",
        ));
    }
    Ok(frame[origin].as_i64().unwrap_or(0) + value * physical / size)
}

pub fn capture(data: &Value) -> Result<Value> {
    let hwnd = data["hwnd"]
        .as_i64()
        .ok_or_else(|| Error::new("INVALID_PARAMS", "Missing HWND."))? as isize;
    native::validate_target(hwnd)?;
    if native::bounds(native::rect(hwnd)?) != data["bounds"] {
        return Err(Error::new("WINDOW_CHANGED", "Target geometry changed."));
    }
    let scale = data
        .get("image_scale")
        .map_or(Some(1.0), Value::as_f64)
        .filter(|v| v.is_finite() && (0.1..=1.0).contains(v))
        .ok_or_else(|| Error::new("INVALID_PARAMS", "image_scale must be 0.1..1."))?;
    let backend = data["capture_backend"].as_str().unwrap_or("wgc");
    let path = data["output_path"]
        .as_str()
        .ok_or_else(|| Error::new("INVALID_PARAMS", "Missing output path."))?;
    let mut metadata = match backend {
        "printwindow" => native::capture(data)?,
        "screen_gdi" if data["visible_pixels_allowed"] == true => native::capture(data)?,
        "wgc" => {
            let frame = crate::capture_wgc::capture(hwnd as usize)
                .map_err(|e| Error::new("WGC_FAILED", &format!("{e:#}")))?;
            if frame.png.len() > 8 * 1024 * 1024 {
                return Err(Error::new("IMAGE_TOO_LARGE", "PNG exceeds 8 MiB."));
            }
            std::fs::write(path, frame.png)
                .map_err(|_| Error::new("CAPTURE_FAILED", "Cannot write PNG."))?;
            json!({"width":frame.width,"height":frame.height,"bounds":{"x":frame.origin_x,"y":frame.origin_y,"width":frame.width,"height":frame.height},"capture_scope":"window","window_id":format!("0x{hwnd:x}"),"path":path,"backend":"wgc","occlusion_sensitive":false})
        }
        _ => {
            return Err(Error::new(
                "INVALID_PARAMS",
                "capture_backend must be wgc or printwindow; desktop pixel capture is excluded.",
            ));
        }
    };
    metadata["physicalWidth"] = metadata["width"].clone();
    metadata["physicalHeight"] = metadata["height"].clone();
    metadata["image_scale"] = json!(scale);
    if scale < 1.0 {
        let bytes =
            std::fs::read(path).map_err(|_| Error::new("CAPTURE_FAILED", "Cannot read PNG."))?;
        let (bytes, w, h) = reduce_png(&bytes, scale)?;
        std::fs::write(path, bytes)
            .map_err(|_| Error::new("CAPTURE_FAILED", "Cannot write reduced PNG."))?;
        metadata["width"] = json!(w);
        metadata["height"] = json!(h);
    }
    Ok(metadata)
}

fn reduce_png(bytes: &[u8], scale: f64) -> Result<(Vec<u8>, u32, u32)> {
    let image = image::load_from_memory_with_format(bytes, image::ImageFormat::Png)
        .map_err(|_| Error::new("CAPTURE_FAILED", "Invalid PNG."))?;
    let w = ((image.width() as f64 * scale).round() as u32).max(1);
    let h = ((image.height() as f64 * scale).round() as u32).max(1);
    let scaled = image.resize_exact(w, h, image::imageops::FilterType::Triangle);
    let mut output = std::io::Cursor::new(Vec::new());
    scaled
        .write_to(&mut output, image::ImageFormat::Png)
        .map_err(|_| Error::new("CAPTURE_FAILED", "Cannot encode reduced PNG."))?;
    Ok((output.into_inner(), w, h))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn rgba_png_reduction_matches_retained_dimensions() {
        let image = image::RgbaImage::from_pixel(624, 392, image::Rgba([173, 216, 230, 255]));
        let mut png = std::io::Cursor::new(Vec::new());
        image.write_to(&mut png, image::ImageFormat::Png).unwrap();
        let (bytes, w, h) = reduce_png(png.get_ref(), 0.8).unwrap();
        assert_eq!((w, h), (499, 314));
        let decoded = image::load_from_memory(&bytes).unwrap();
        assert_eq!((decoded.width(), decoded.height()), (499, 314));
    }
    #[test]
    fn scaled_coordinates_use_retained_origin_and_dimensions() {
        let frame = json!({"width":499,"height":314,"physicalWidth":624,"physicalHeight":392,"originX":100,"originY":200});
        assert_eq!(map_coordinate(&frame, 70, true).unwrap(), 187);
        assert_eq!(map_coordinate(&frame, 109, false).unwrap(), 336);
        assert!(map_coordinate(&frame, 499, true).is_err());
        assert!(map_coordinate(&frame, -1, false).is_err());
    }
}
