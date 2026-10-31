use crate::{
    native,
    server::{Error, Result},
};
use serde_json::{Value, json};
use windows::Win32::{
    Foundation::HWND,
    System::{
        Com::{
            CLSCTX_INPROC_SERVER, COINIT_MULTITHREADED, CoCreateInstance, CoInitializeEx,
            CoUninitialize,
        },
        Ole::{
            SafeArrayDestroy, SafeArrayGetDim, SafeArrayGetElement, SafeArrayGetLBound,
            SafeArrayGetUBound,
        },
    },
    UI::Accessibility::*,
};

struct Apartment;
impl Drop for Apartment {
    fn drop(&mut self) {
        unsafe {
            CoUninitialize();
        }
    }
}
fn limit(text: String, n: usize) -> String {
    text.chars().take(n).collect()
}
unsafe fn runtime_id(element: &IUIAutomationElement) -> Option<Vec<i32>> {
    unsafe {
        let array = element.GetRuntimeId().ok()?;
        if array.is_null() {
            return None;
        }
        let result = (|| {
            if SafeArrayGetDim(array) != 1 {
                return None;
            }
            let lo = SafeArrayGetLBound(array, 1).ok()?;
            let hi = SafeArrayGetUBound(array, 1).ok()?;
            if hi < lo || hi - lo > 128 {
                return None;
            }
            let mut ids = Vec::new();
            for index in lo..=hi {
                let mut value = 0i32;
                SafeArrayGetElement(array, &index, &mut value as *mut _ as *mut _).ok()?;
                ids.push(value);
            }
            Some(ids)
        })();
        let _ = SafeArrayDestroy(array);
        result
    }
}
pub fn snapshot(data: &Value) -> Result<Value> {
    unsafe {
        let h = data["hwnd"]
            .as_i64()
            .ok_or_else(|| Error::new("INVALID_PARAMS", "Missing HWND."))? as isize;
        let maximum = data["max_elements"].as_u64().unwrap_or(200).clamp(1, 500) as usize;
        CoInitializeEx(None, COINIT_MULTITHREADED)
            .ok()
            .map_err(|_| Error::new("UIA_FAILED", "Cannot initialize COM."))?;
        let _apartment = Apartment;
        let automation: IUIAutomation =
            CoCreateInstance(&CUIAutomation, None, CLSCTX_INPROC_SERVER)
                .map_err(|_| Error::new("UIA_FAILED", "Cannot initialize UI Automation."))?;
        let root = automation
            .ElementFromHandle(HWND(h as *mut _))
            .map_err(|_| Error::new("UIA_FAILED", "Cannot read selected window."))?;
        let walker = automation
            .ControlViewWalker()
            .map_err(|_| Error::new("UIA_FAILED", "Cannot create UIA walker."))?;
        let pid = native::pid(h) as i32;
        let mut stack = vec![(root, 0usize, None::<usize>)];
        let mut elements = Vec::new();
        let mut lines = Vec::new();
        let mut document = String::new();
        let mut selected_text = String::new();
        let mut focused = None;
        let mut truncated = false;
        let mut redacted = 0;
        let mut examined = 0;
        while let Some((element, depth, parent)) = stack.pop() {
            examined += 1;
            if examined > 2000 || elements.len() >= maximum {
                truncated = true;
                break;
            }
            if element.CurrentProcessId().ok() != Some(pid) {
                continue;
            }
            let password = element.CurrentIsPassword().ok().map(|v| v.as_bool());
            let sensitive = password != Some(false);
            let index = elements.len();
            let name = if sensitive {
                "[redacted]".to_string()
            } else {
                limit(
                    element
                        .CurrentName()
                        .map(|v| v.to_string())
                        .unwrap_or_default(),
                    512,
                )
            };
            let control = element.CurrentControlType().map(|id| id.0).unwrap_or(0);
            let bounds = element.CurrentBoundingRectangle().ok().map(
                |r| json!({"x":r.left,"y":r.top,"width":r.right-r.left,"height":r.bottom-r.top}),
            );
            let focus = element
                .CurrentHasKeyboardFocus()
                .map(|b| b.as_bool())
                .unwrap_or(false);
            let mut record = json!({"index":index,"parent_index":parent,"depth":depth,"name":name,"control_type_id":control,"bounds":bounds,"runtime_id":runtime_id(&element),"is_password":password,"process_id":pid,"has_keyboard_focus":focus,"enabled":element.CurrentIsEnabled().map(|b|b.as_bool()).unwrap_or(false),"offscreen":element.CurrentIsOffscreen().map(|b|b.as_bool()).unwrap_or(true)});
            if sensitive {
                record["value"] = json!("[redacted]");
                redacted += 1;
            } else if document.len() < 16000 {
                let mut text = String::new();
                if let Ok(pattern) =
                    element.GetCurrentPatternAs::<IUIAutomationTextPattern>(UIA_TextPatternId)
                    && let Ok(range) = pattern.DocumentRange()
                {
                    text = range
                        .GetText(2048)
                        .map(|v| v.to_string())
                        .unwrap_or_default();
                }
                if selected_text.len() < 16000
                    && let Ok(pattern) =
                        element.GetCurrentPatternAs::<IUIAutomationTextPattern>(UIA_TextPatternId)
                    && let Ok(ranges) = pattern.GetSelection()
                    && let Ok(count) = ranges.Length()
                {
                    let mut selected = String::new();
                    for i in 0..count.clamp(0, 4) {
                        if let Ok(range) = ranges.GetElement(i)
                            && let Ok(value) = range.GetText(512)
                        {
                            let value =
                                limit(value.to_string(), (16000 - selected_text.len()).min(512));
                            selected.push_str(&value);
                            selected_text.push_str(&value);
                        }
                    }
                    if !selected.is_empty() {
                        record["selected_text"] = json!(selected);
                    }
                }
                if text.is_empty()
                    && let Ok(pattern) =
                        element.GetCurrentPatternAs::<IUIAutomationValuePattern>(UIA_ValuePatternId)
                {
                    text = pattern
                        .CurrentValue()
                        .map(|v| limit(v.to_string(), 2048))
                        .unwrap_or_default();
                }
                if !text.is_empty() {
                    text = limit(text, (16000 - document.len()).min(2048));
                    record["value"] = json!(text);
                    document.push_str(&text);
                    document.push('\n');
                }
            }
            lines.push(format!("{}[{}] {}", "  ".repeat(depth), index, name));
            if focus {
                focused = Some(record.clone());
            }
            elements.push(record);
            if sensitive {
                continue;
            }
            if depth >= 16 {
                truncated = true;
                continue;
            }
            let mut children = Vec::new();
            if let Ok(mut child) = walker.GetFirstChildElement(&element) {
                loop {
                    children.push(child.clone());
                    if children.len() > maximum {
                        truncated = true;
                        break;
                    }
                    match walker.GetNextSiblingElement(&child) {
                        Ok(next) => child = next,
                        Err(_) => break,
                    }
                }
            }
            for child in children.into_iter().rev() {
                stack.push((child, depth + 1, Some(index)));
            }
        }
        if native::pid(h) as i32 != pid {
            return Err(Error::new(
                "WINDOW_CHANGED",
                "Target process changed during UIA.",
            ));
        }
        Ok(
            json!({"tree":lines.join("\n"),"elements":elements,"focused_element":focused,"document_text":document,"selected_text":selected_text,"truncated":truncated,"text_truncated":document.len()>=16000,"process_id":pid,"scope":"target-window-control-view-same-process","coordinate_space":"uia-screen-coordinates","redacted_subtrees":redacted,"nodes_examined":examined}),
        )
    }
}

/// Semantic mutations run in the same killable worker boundary as UIA reads.
pub fn action(data: &Value) -> Result<Value> {
    use std::time::{Duration, Instant};
    use windows::core::BSTR;
    unsafe {
        let h = data["hwnd"]
            .as_i64()
            .ok_or_else(|| Error::new("INVALID_PARAMS", "Missing HWND."))? as isize;
        native::validate_target(h)?;
        let validate = || -> Result<()> {
            if native::identity(&native::describe(h)?)? != data["identity"].as_str().unwrap_or("") {
                return Err(Error::new("WINDOW_CHANGED", "Process identity changed."));
            }
            if native::GetForegroundWindow() != h {
                return Err(Error::new(
                    "FOCUS_CHANGED",
                    "Target must remain foreground.",
                ));
            }
            Ok(())
        };
        validate()?;
        let expected = &data["element"];
        if expected["runtime_id"]
            .as_array()
            .is_none_or(|a| a.is_empty())
        {
            return Err(Error::new(
                "ELEMENT_IDENTITY_UNAVAILABLE",
                "Refresh accessibility.",
            ));
        }
        CoInitializeEx(None, COINIT_MULTITHREADED)
            .ok()
            .map_err(|_| Error::new("UIA_FAILED", "COM initialization failed."))?;
        let _apartment = Apartment;
        let a: IUIAutomation = CoCreateInstance(&CUIAutomation, None, CLSCTX_INPROC_SERVER)
            .map_err(|_| Error::new("UIA_FAILED", "Cannot initialize UIA."))?;
        let root = a
            .ElementFromHandle(HWND(h as *mut _))
            .map_err(|_| Error::new("UIA_FAILED", "Cannot read target."))?;
        let walker = a
            .ControlViewWalker()
            .map_err(|_| Error::new("UIA_FAILED", "Cannot walk target."))?;
        let deadline = Instant::now() + Duration::from_secs(7);
        let mut stack = vec![(root, 0usize)];
        let mut count = 0;
        while let Some((element, depth)) = stack.pop() {
            count += 1;
            if count > 2000 || Instant::now() >= deadline {
                return Err(Error::new(
                    "ELEMENT_CHANGED",
                    "Element search exceeded its budget.",
                ));
            }
            if element.CurrentProcessId().ok() != Some(native::pid(h) as i32) {
                continue;
            }
            let ids = runtime_id(&element);
            if json!(ids) == expected["runtime_id"] {
                let r = element
                    .CurrentBoundingRectangle()
                    .map_err(|_| Error::new("ELEMENT_CHANGED", "Missing bounds."))?;
                let bounds =
                    json!({"x":r.left,"y":r.top,"width":r.right-r.left,"height":r.bottom-r.top});
                if bounds != expected["bounds"]
                    || !element
                        .CurrentIsEnabled()
                        .map(|v| v.as_bool())
                        .unwrap_or(false)
                    || element
                        .CurrentIsOffscreen()
                        .map(|v| v.as_bool())
                        .unwrap_or(true)
                {
                    return Err(Error::new(
                        "ELEMENT_CHANGED",
                        "Element geometry or availability changed.",
                    ));
                }
                validate()?;
                if Instant::now() >= deadline {
                    return Err(Error::new("UIA_TIMEOUT", "Action expired before mutation."));
                }
                match data["operation"].as_str() {
                    Some("set_value") => {
                        if element.CurrentIsPassword().map(|v| v.as_bool()) != Ok(false) {
                            return Err(Error::new(
                                "PASSWORD_EXCLUDED",
                                "Password or unknown password state is excluded.",
                            ));
                        }
                        let value = data["value"]
                            .as_str()
                            .filter(|v| v.chars().count() <= 4096)
                            .ok_or_else(|| {
                                Error::new(
                                    "INVALID_PARAMS",
                                    "value must be at most 4096 characters.",
                                )
                            })?;
                        let pattern = element
                            .GetCurrentPatternAs::<IUIAutomationValuePattern>(UIA_ValuePatternId)
                            .map_err(|_| Error::new("PATTERN_UNSUPPORTED", "No ValuePattern."))?;
                        if pattern
                            .CurrentIsReadOnly()
                            .map(|v| v.as_bool())
                            .unwrap_or(true)
                        {
                            return Err(Error::new("READ_ONLY", "Element value is read-only."));
                        }
                        validate()?;
                        if Instant::now() >= deadline {
                            return Err(Error::new(
                                "UIA_TIMEOUT",
                                "Action expired before mutation.",
                            ));
                        }
                        pattern.SetValue(&BSTR::from(value)).map_err(|_| {
                            Error::new(
                                "UIA_ACTION_FAILED",
                                "SetValue failed; inspect state before retry.",
                            )
                        })?;
                    }
                    Some("invoke") => {
                        let pattern = element
                            .GetCurrentPatternAs::<IUIAutomationInvokePattern>(UIA_InvokePatternId)
                            .map_err(|_| Error::new("PATTERN_UNSUPPORTED", "No InvokePattern."))?;
                        validate()?;
                        if Instant::now() >= deadline {
                            return Err(Error::new(
                                "UIA_TIMEOUT",
                                "Action expired before mutation.",
                            ));
                        }
                        pattern.Invoke().map_err(|_| {
                            Error::new(
                                "UIA_ACTION_FAILED",
                                "Invoke failed; inspect state before retry.",
                            )
                        })?;
                    }
                    _ => return Err(Error::new("INVALID_PARAMS", "Unknown semantic action.")),
                }
                return Ok(json!({"performed":true}));
            }
            if depth < 16
                && let Ok(mut child) = walker.GetFirstChildElement(&element)
            {
                let mut siblings = 0;
                loop {
                    stack.push((child.clone(), depth + 1));
                    siblings += 1;
                    if siblings >= 500 {
                        break;
                    }
                    match walker.GetNextSiblingElement(&child) {
                        Ok(next) => child = next,
                        Err(_) => break,
                    }
                }
            }
        }
        Err(Error::new(
            "ELEMENT_CHANGED",
            "Element identity no longer exists.",
        ))
    }
}
