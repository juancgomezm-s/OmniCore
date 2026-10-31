use crate::{input, native};
use base64::Engine;
use serde_json::{Value, json};
use std::{
    collections::HashMap,
    io::{Read, Write},
    os::windows::process::CommandExt,
    path::PathBuf,
    process::{Command, Stdio},
    time::{Duration, Instant},
};
#[derive(Debug)]
pub struct Error {
    code: String,
    message: String,
}
pub type Result<T> = std::result::Result<T, Error>;
impl std::fmt::Display for Error {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}: {}", self.code, self.message)
    }
}
impl std::error::Error for Error {}
impl Error {
    pub fn new(code: &str, message: &str) -> Self {
        Self {
            code: code.into(),
            message: message.into(),
        }
    }
    pub fn value(&self) -> Value {
        json!({"code":self.code,"message":self.message})
    }
}
pub fn integer(p: &Value, key: &str, default: i64, min: i64, max: i64) -> Result<i64> {
    p.get(key)
        .map_or(Some(default), Value::as_i64)
        .filter(|n| *n >= min && *n <= max)
        .ok_or_else(|| {
            Error::new(
                "INVALID_PARAMS",
                &format!("{key} must be an integer in {min}..{max}."),
            )
        })
}
fn boolean(p: &Value, key: &str, default: bool) -> Result<bool> {
    p.get(key)
        .map_or(Some(default), Value::as_bool)
        .ok_or_else(|| Error::new("INVALID_PARAMS", &format!("{key} must be boolean.")))
}
fn uid() -> String {
    uuid::Uuid::new_v4().simple().to_string()
}
fn worker(kind: &str, data: Value) -> Result<Value> {
    let mut child = Command::new(
        std::env::current_exe()
            .map_err(|_| Error::new("WORKER_FAILED", "Cannot locate native worker."))?,
    )
    .args(["--worker", kind])
    .creation_flags(0x08000000)
    .stdin(Stdio::piped())
    .stdout(Stdio::piped())
    .stderr(Stdio::null())
    .spawn()
    .map_err(|_| Error::new("WORKER_FAILED", "Cannot launch native worker."))?;
    let sent = child
        .stdin
        .take()
        .unwrap()
        .write_all(data.to_string().as_bytes());
    if sent.is_err() {
        let _ = child.kill();
        let _ = child.wait();
        return Err(Error::new(
            "WORKER_FAILED",
            "Cannot write native worker request.",
        ));
    }
    let stdout = child.stdout.take().unwrap();
    let reader = std::thread::spawn(move || {
        let mut data = Vec::new();
        stdout
            .take(4 * 1024 * 1024 + 1)
            .read_to_end(&mut data)
            .map(|_| data)
    });
    let started = Instant::now();
    loop {
        if crate::indicator::cancelled() {
            let _ = child.kill();
            let _ = child.wait();
            let _ = reader.join();
            return crate::indicator::check().map(|_| Value::Null);
        }
        match child.try_wait() {
            Ok(Some(_)) => break,
            Ok(None) if started.elapsed() < Duration::from_secs(8) => {
                std::thread::sleep(Duration::from_millis(10))
            }
            _ => {
                let _ = child.kill();
                let _ = child.wait();
                let _ = reader.join();
                return Err(Error::new(
                    &format!("{}_TIMEOUT", kind.to_uppercase()),
                    "Isolated native worker timed out and was terminated.",
                ));
            }
        }
    }
    let raw = reader
        .join()
        .ok()
        .and_then(|v| v.ok())
        .filter(|v| v.len() <= 4 * 1024 * 1024)
        .ok_or_else(|| Error::new("WORKER_FAILED", "Invalid worker output."))?;
    let answer: Value = serde_json::from_slice(&raw)
        .map_err(|_| Error::new("WORKER_FAILED", "Malformed native worker output."))?;
    if answer["ok"] != true {
        return Err(Error::new(
            answer["error"]["code"].as_str().unwrap_or("WORKER_FAILED"),
            answer["error"]["message"]
                .as_str()
                .unwrap_or("Native worker failed."),
        ));
    }
    Ok(answer["result"].clone())
}
#[derive(Clone)]
struct Observation {
    window: Value,
    created: Instant,
    accessibility: Value,
    image: Value,
}
pub struct Server {
    output: PathBuf,
    ttl: f64,
    allow_input: bool,
    visible_pixels_allowed: bool,
    bindings: HashMap<String, (String, String)>,
    observations: HashMap<String, Observation>,
}
impl Server {
    pub fn new(output: PathBuf, ttl: f64, allow_input: bool) -> Self {
        Self {
            output,
            ttl,
            allow_input,
            visible_pixels_allowed: false,
            bindings: HashMap::new(),
            observations: HashMap::new(),
        }
    }
    /// Adapter policy, never supplied by tool arguments. OmniCore leaves this disabled.
    pub fn allow_visible_pixel_capture(&mut self) {
        self.visible_pixels_allowed = true;
    }
    fn invalidate(&mut self, id: &str) {
        self.observations.retain(|_, o| o.window["id"] != id);
    }
    fn bind(&mut self, h: native::Handle) -> Result<Value> {
        native::validate_target(h)?;
        let mut w = native::describe(h)?;
        let key = native::identity(&w)?;
        let id = w["id"].as_str().unwrap().to_string();
        if self.bindings.get(&id).is_none_or(|a| a.0 != key) {
            self.bindings.insert(id.clone(), (key, uid()));
            self.invalidate(&id)
        }
        w["identity"] = json!(self.bindings[&id].1);
        Ok(w)
    }
    fn resolve(&mut self, p: &Value) -> Result<(native::Handle, Value)> {
        let supplied = p.get("window").unwrap_or(&p["id"]);
        let h = native::hwnd(supplied)?;
        let w = self.bind(h)?;
        crate::indicator::target(h);
        if supplied.is_object()
            && (supplied["pid"] != w["pid"] || supplied["identity"] != w["identity"])
        {
            return Err(Error::new(
                "WINDOW_CHANGED",
                "Window identity no longer matches discovery.",
            ));
        }
        Ok((h, w))
    }
    fn observed(&mut self, p: &Value) -> Result<(native::Handle, Value, Observation)> {
        if !self.allow_input {
            return Err(Error::new(
                "INPUT_DISABLED",
                "Start with --allow-input to enable input.",
            ));
        }
        let o = p["observation_id"]
            .as_str()
            .and_then(|id| self.observations.get(id))
            .cloned()
            .ok_or_else(|| {
                Error::new(
                    "STALE_OBSERVATION",
                    "Call get_window_state for a current observation.",
                )
            })?;
        let (h, w) = self.resolve(p)?;
        if w["id"] != o.window["id"] || w["identity"] != o.window["identity"] {
            return Err(Error::new(
                "WINDOW_CHANGED",
                "Observation belongs to another target.",
            ));
        }
        if o.created.elapsed().as_secs_f64() > self.ttl {
            self.invalidate(w["id"].as_str().unwrap());
            return Err(Error::new("STALE_OBSERVATION", "Observation expired."));
        }
        if w["bounds"] != o.window["bounds"] {
            self.invalidate(w["id"].as_str().unwrap());
            return Err(Error::new("WINDOW_CHANGED", "Target geometry changed."));
        }
        if w["visible"] != true || w["minimized"] == true {
            return Err(Error::new("WINDOW_NOT_VISIBLE", "Target must be visible."));
        }
        if unsafe { native::GetForegroundWindow() } != h {
            return Err(Error::new(
                "FOCUS_CHANGED",
                "Activate target and refresh state.",
            ));
        }
        if p.get("screenshot_id")
            .is_some_and(|id| id != &o.image["id"])
        {
            return Err(Error::new(
                "STALE_SCREENSHOT",
                "Screenshot does not match observation.",
            ));
        }
        Ok((h, w, o))
    }
    fn point(&self, p: &Value, w: &Value, o: &Observation, prefix: &str) -> Result<(i32, i32)> {
        let b = &w["bounds"];
        let x = b["x"].as_i64().unwrap();
        let y = b["y"].as_i64().unwrap();
        let (point_x, point_y) = if prefix.is_empty() && p.get("element_index").is_some() {
            let index = integer(p, "element_index", 0, 0, 499)?;
            let element = o.accessibility["elements"]
                .as_array()
                .and_then(|a| a.iter().find(|e| e["index"] == index))
                .ok_or_else(|| {
                    Error::new("ELEMENT_NOT_FOUND", "Element absent from observation.")
                })?;
            if element["enabled"] == false || element["offscreen"] == true {
                return Err(Error::new(
                    "ELEMENT_UNAVAILABLE",
                    "Element is disabled or offscreen.",
                ));
            }
            if element["runtime_id"]
                .as_array()
                .is_none_or(|a| a.is_empty())
            {
                return Err(Error::new(
                    "ELEMENT_IDENTITY_UNAVAILABLE",
                    "Use observed coordinates instead.",
                ));
            }
            let fresh = worker(
                "accessibility",
                json!({"hwnd":native::hwnd(w)?,"max_elements":500}),
            )?;
            let found = fresh["elements"]
                .as_array()
                .and_then(|a| a.iter().find(|e| e["runtime_id"] == element["runtime_id"]));
            if found.is_none_or(|e| {
                e["bounds"] != element["bounds"] || e["enabled"] == false || e["offscreen"] == true
            }) {
                return Err(Error::new(
                    "ELEMENT_CHANGED",
                    "Element changed; refresh state.",
                ));
            }
            let eb = &element["bounds"];
            let width = eb["width"].as_f64().unwrap_or(0.0);
            let height = eb["height"].as_f64().unwrap_or(0.0);
            if width <= 0.0 || height <= 0.0 {
                return Err(Error::new(
                    "ELEMENT_UNAVAILABLE",
                    "No usable element bounds.",
                ));
            }
            (
                (eb["x"].as_f64().unwrap_or(0.0) + width / 2.0).round() as i64,
                (eb["y"].as_f64().unwrap_or(0.0) + height / 2.0).round() as i64,
            )
        } else {
            let coordinate = |name: &str| -> Result<i64> {
                p[name]
                    .as_f64()
                    .filter(|v| v.is_finite() && v.abs() < 1000000.0)
                    .map(|v| v.round() as i64)
                    .ok_or_else(|| {
                        Error::new("INVALID_PARAMS", "Coordinates must be finite numbers.")
                    })
            };
            (
                if p.get("screenshot_id").is_some() {
                    crate::capture::map_coordinate(
                        &o.image,
                        coordinate(&format!("{prefix}x"))?,
                        true,
                    )?
                } else {
                    x + coordinate(&format!("{prefix}x"))?
                },
                if p.get("screenshot_id").is_some() {
                    crate::capture::map_coordinate(
                        &o.image,
                        coordinate(&format!("{prefix}y"))?,
                        false,
                    )?
                } else {
                    y + coordinate(&format!("{prefix}y"))?
                },
            )
        };
        if point_x < x
            || point_y < y
            || point_x >= x + b["width"].as_i64().unwrap()
            || point_y >= y + b["height"].as_i64().unwrap()
        {
            return Err(Error::new(
                "OUTSIDE_TARGET",
                "Point lies outside target bounds.",
            ));
        }
        Ok((point_x as i32, point_y as i32))
    }
    fn state(&mut self, p: &Value) -> Result<Value> {
        let (h, w) = self.resolve(p)?;
        let image = boolean(p, "include_screenshot", true)?;
        let text = boolean(p, "include_text", false)?;
        if !image && !text {
            return Err(Error::new("INVALID_PARAMS", "Request screenshot or text."));
        }
        if w["visible"] != true || w["minimized"] == true {
            return Err(Error::new(
                "WINDOW_NOT_VISIBLE",
                "Observe a visible non-minimized window.",
            ));
        }
        let oid = uid();
        let path = self.output.join(format!("{oid}.png"));
        std::fs::create_dir_all(&self.output)
            .map_err(|_| Error::new("CAPTURE_FAILED", "Cannot create capture directory."))?;
        let result = (|| {
            let mut shots = Vec::new();
            if image {
                let mut metadata = worker(
                    "capture",
                    json!({"hwnd":h,"bounds":w["bounds"],"output_path":path,"visible_pixels_allowed":self.visible_pixels_allowed,"capture_backend":p.get("capture_backend").cloned().unwrap_or(json!("wgc")),"image_scale":p.get("image_scale").cloned().unwrap_or(json!(1.0))}),
                )?;
                let bytes = std::fs::read(&path)
                    .map_err(|_| Error::new("CAPTURE_FAILED", "Cannot read capture."))?;
                if bytes.len() > 8 * 1024 * 1024 {
                    return Err(Error::new("IMAGE_TOO_LARGE", "PNG exceeds 8 MiB."));
                }
                metadata["id"] = json!(format!("{oid}:image"));
                metadata["url"] = json!(format!(
                    "data:image/png;base64,{}",
                    base64::engine::general_purpose::STANDARD.encode(bytes)
                ));
                metadata["originX"] = metadata["bounds"]["x"].clone();
                metadata["originY"] = metadata["bounds"]["y"].clone();
                metadata["coordinate_space"] = json!("physical-pixels");
                shots.push(metadata);
            }
            let a = if text {
                worker(
                    "accessibility",
                    json!({"hwnd":h,"max_elements":integer(p,"max_elements",200,1,500)?}),
                )?
            } else {
                Value::Null
            };
            let now = self.bind(h)?;
            if now["identity"] != w["identity"] || now["bounds"] != w["bounds"] {
                return Err(Error::new(
                    "WINDOW_CHANGED",
                    "Target changed while acquiring state.",
                ));
            }
            self.invalidate(w["id"].as_str().unwrap());
            self.observations
                .retain(|_, o| o.created.elapsed().as_secs_f64() <= self.ttl);
            if self.observations.len() >= 64 {
                self.observations.clear()
            }
            self.observations.insert(
                oid.clone(),
                Observation {
                    window: w.clone(),
                    created: Instant::now(),
                    accessibility: a.clone(),
                    image: shots
                        .first()
                        .cloned()
                        .map(|mut s| {
                            s.as_object_mut().unwrap().remove("url");
                            s.as_object_mut().unwrap().remove("path");
                            s
                        })
                        .unwrap_or(Value::Null),
                },
            );
            Ok(
                json!({"window":w,"observation_id":oid,"expires_in_seconds":self.ttl,"screenshots":shots,"accessibility":a,"limitations":["Sequential snapshots; content changes may remain undetected. Refresh after every input."]}),
            )
        })();
        let _ = std::fs::remove_file(path);
        result
    }
    fn call(&mut self, method: &str, p: &Value) -> Result<Value> {
        crate::indicator::check()?;
        match method {
            "capabilities" => Ok(
                json!({"protocol":"ndjson-v2-independent","implementation":"rust","input_enabled":self.allow_input,"observation_ttl_seconds":self.ttl,"capture_backend":"wgc","capture_backends":["wgc","printwindow"],"engine_version":env!("CARGO_PKG_VERSION"),"capture_scope":"window-only","activity_indicator":"purple-curtain","cancellation":["button","Escape"],"accessibility_backend":"Microsoft UI Automation","input_backend":"SendInput","methods":["list_windows","get_window","activate_window","get_window_state","click","scroll","drag","type_text","press_key","invoke","set_value"]}),
            ),
            "list_windows" => {
                let hidden = boolean(p, "include_hidden", false)?;
                let list: Vec<_> = native::windows(hidden)
                    .into_iter()
                    .filter_map(|h| self.bind(h).ok())
                    .collect();
                Ok(json!(list))
            }
            "get_window" => Ok(self.resolve(p)?.1),
            "get_window_state" => self.state(p),
            "activate_window" => {
                if !self.allow_input {
                    return Err(Error::new("INPUT_DISABLED", "Input is disabled."));
                }
                let (h, w) = self.resolve(p)?;
                unsafe {
                    if native::IsIconic(h) != 0 {
                        native::ShowWindow(h, 9);
                    }
                    native::SetForegroundWindow(h);
                }
                for _ in 0..10 {
                    if unsafe { native::GetForegroundWindow() } == h {
                        self.invalidate(w["id"].as_str().unwrap());
                        return Ok(
                            json!({"window":self.bind(h)?,"activated":true,"requires_fresh_observation":true}),
                        );
                    }
                    std::thread::sleep(Duration::from_millis(50));
                }
                Err(Error::new(
                    "FOCUS_DENIED",
                    "Windows denied foreground focus. Focus manually and retry.",
                ))
            }
            "invoke" | "set_value" => {
                let (h, w, o) = self.observed(p)?;
                let index = integer(p, "element_index", -1, 0, 499)?;
                let element = o.accessibility["elements"]
                    .as_array()
                    .and_then(|rows| rows.iter().find(|e| e["index"] == index))
                    .ok_or_else(|| {
                        Error::new(
                            "ELEMENT_NOT_FOUND",
                            "Refresh accessibility before an element action.",
                        )
                    })?;
                let result = worker(
                    "uia_action",
                    json!({"hwnd":h,"identity":native::identity(&w)?,"element":element,"operation":method,"value":p.get("value")}),
                );
                self.invalidate(w["id"].as_str().unwrap());
                Ok(
                    json!({"performed":method,"window_id":w["id"],"details":result?,"requires_fresh_observation":true}),
                )
            }
            "click" | "scroll" | "drag" | "type_text" | "press_key" => {
                let (h, w, o) = self.observed(p)?;
                let mut points = Vec::new();
                if matches!(method, "click" | "scroll") {
                    points.push(self.point(p, &w, &o, "")?)
                }
                if method == "drag" {
                    points.push(self.point(p, &w, &o, "start_")?);
                    points.push(self.point(p, &w, &o, "end_")?)
                }
                let result = (|| {
                    self.observed(p)?;
                    input::perform(h, method, p, &points)
                })();
                self.invalidate(w["id"].as_str().unwrap());
                Ok(
                    json!({"performed":method,"window_id":w["id"],"details":result?,"requires_fresh_observation":true}),
                )
            }
            _ => Err(Error::new(
                "METHOD_NOT_IMPLEMENTED",
                "Unknown method. Query capabilities.",
            )),
        }
    }
    pub fn dispatch(&mut self, r: Value) -> Value {
        let id = r["id"].clone();
        let result = if !r.is_object() || !(id.is_string() || id.is_i64() || id.is_u64()) {
            Err(Error::new(
                "INVALID_REQUEST",
                "Integer/string request id is required.",
            ))
        } else if r.get("params").is_some_and(|v| !v.is_object()) {
            Err(Error::new("INVALID_PARAMS", "params must be an object."))
        } else {
            self.call(
                r["method"].as_str().unwrap_or(""),
                r.get("params").unwrap_or(&json!({})),
            )
        };
        match result {
            Ok(v) => json!({"id":id,"ok":true,"result":v}),
            Err(e) => json!({"id":id,"ok":false,"error":e.value()}),
        }
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn input_disabled_before_target() {
        let mut s = Server::new(std::env::temp_dir(), 30.0, false);
        let r = s.dispatch(json!({"id":1,"method":"click","params":{}}));
        assert_eq!(r["error"]["code"], "INPUT_DISABLED")
    }
    #[test]
    fn validation() {
        assert!(integer(&json!({"n":true}), "n", 0, 0, 5).is_err());
        let mut s = Server::new(std::env::temp_dir(), 30.0, false);
        assert_eq!(
            s.dispatch(json!({"id":false}))["error"]["code"],
            "INVALID_REQUEST"
        );
    }
}
