//! Shared Windows input argument translation.
//!
//! These functions only translate arguments; they do not inject input.
use anyhow::{Result, bail};

/// Public positive Y means down; Win32 positive wheel delta means up.
/// Match the original helper's signed-16-bit saturation after reversing Y.
pub fn wheel_deltas(sx: i32, sy: i32) -> (i32, i32) {
    let clamp = |value: i64| value.clamp(i16::MIN as i64, i16::MAX as i64) as i32;
    (clamp(sx as i64), clamp(-(sy as i64)))
}

/// Return the Win32 down/up flags for the original helper's mouse aliases.
pub fn mouse_button_flags(name: &str) -> Result<(u32, u32)> {
    Ok(match name.trim().to_ascii_lowercase().as_str() {
        "left" | "l" => (0x0002, 0x0004),
        "right" | "r" => (0x0008, 0x0010),
        "middle" | "m" => (0x0020, 0x0040),
        _ => bail!("mouse_button must be left/l, right/r, or middle/m"),
    })
}

/// The caller may impose an additional click-count budget.
/// The original rejects multi-clicks of the right mouse button before input.
pub fn validate_click(button: &str, count: u64) -> Result<()> {
    let (down, _) = mouse_button_flags(button)?;
    if count == 0 {
        bail!("click_count must be >= 1");
    }
    if down == 0x0008 && count > 1 {
        bail!("right double click is not supported");
    }
    Ok(())
}

/// Resolve common X keysym names and Windows aliases to a virtual key.
/// Shifted punctuation retains the original physical OEM key: request Shift
/// explicitly, e.g. Control_L+Shift_L+period, or use type_text for literal text.
/// The original's measured KP_Enter emits the main Enter VK without EXTENDEDKEY.
pub fn virtual_key(name: &str) -> Result<u16> {
    let upper = name.trim().to_ascii_uppercase();
    let key = match upper.as_str() {
        "CONTROL" | "CTRL" => 0x11,
        "CTRL_L" | "CONTROL_L" => 0xA2,
        "CTRL_R" | "CONTROL_R" => 0xA3,
        "SHIFT" => 0x10,
        "SHIFT_L" => 0xA0,
        "SHIFT_R" => 0xA1,
        "ALT" | "OPTION" => 0x12,
        "ALT_L" | "OPTION_L" => 0xA4,
        "ALT_R" | "OPTION_R" => 0xA5,
        "WIN" | "WINDOWS" | "META" | "COMMAND" | "CMD" | "SUPER" | "OS" | "WIN_L" | "WINDOWS_L"
        | "META_L" | "COMMAND_L" | "CMD_L" | "SUPER_L" | "OS_L" => 0x5B,
        "WIN_R" | "WINDOWS_R" | "META_R" | "COMMAND_R" | "CMD_R" | "SUPER_R" | "OS_R" => 0x5C,
        "ENTER" | "RETURN" | "LINEFEED" => 0x0D,
        "KP_ENTER" | "NUMPAD_ENTER" | "NUMPADENTER" | "NUM_ENTER" | "NUMENTER" => 0x0D,
        "TAB" => 0x09,
        "CLEAR" => 0x0C,
        "SPACE" | " " => 0x20,
        "ESC" | "ESCAPE" => 0x1B,
        "BACKSPACE" => 0x08,
        "DELETE" | "DEL" => 0x2E,
        "INSERT" => 0x2D,
        "HOME" | "BEGIN" => 0x24,
        "END" => 0x23,
        "PRIOR" | "PAGEUP" | "PAGE_UP" | "PGUP" => 0x21,
        "NEXT" | "PAGEDOWN" | "PAGE_DOWN" | "PGDN" => 0x22,
        "LEFT" | "ARROWLEFT" => 0x25,
        "UP" | "ARROWUP" => 0x26,
        "RIGHT" | "ARROWRIGHT" => 0x27,
        "DOWN" | "ARROWDOWN" => 0x28,
        "MENU" | "APPS" => 0x5D,
        "HELP" => 0x2F,
        "SELECT" => 0x29,
        "PRINT" => 0x2A,
        "PRINTSCREEN" | "PRINT_SCREEN" | "SYS_REQ" => 0x2C,
        "EXECUTE" => 0x2B,
        "CANCEL" | "BREAK" => 0x03,
        "MODE_SWITCH" | "SCRIPT_SWITCH" => 0x1F,
        "PAUSE" => 0x13,
        "SCROLL_LOCK" | "SCROLLLOCK" => 0x91,
        "NUM_LOCK" | "NUMLOCK" => 0x90,
        "CAPS_LOCK" | "CAPSLOCK" | "SHIFT_LOCK" => 0x14,
        "KP_DELETE" | "NUMPAD_DELETE" | "NUMPADDELETE" | "NUM_DELETE" | "NUMDELETE" => 0x2E,
        "PLUS" | "EQUAL" | "EQUALS" | "+" | "=" => 0xBB,
        "MINUS" | "HYPHEN" | "UNDERSCORE" | "-" | "_" => 0xBD,
        "COMMA" | "LESS" | "," | "<" => 0xBC,
        "PERIOD" | "DOT" | "FULL_STOP" | "GREATER" | "." | ">" => 0xBE,
        "SLASH" | "FORWARD_SLASH" | "QUESTION" | "/" | "?" => 0xBF,
        "SEMICOLON" | "COLON" | ";" | ":" => 0xBA,
        "APOSTROPHE" | "QUOTESINGLE" | "QUOTE_SINGLE" | "SINGLE_QUOTE" | "DOUBLE_QUOTE"
        | "QUOTEDBL" | "'" | "\"" => 0xDE,
        "GRAVE" | "BACKTICK" | "TILDE" | "`" | "~" => 0xC0,
        "BRACKETLEFT" | "BRACKET_LEFT" | "LEFTBRACKET" | "LEFT_BRACKET" | "BRACELEFT"
        | "BRACE_LEFT" | "LEFTBRACE" | "LEFT_BRACE" | "[" | "{" => 0xDB,
        "BRACKETRIGHT" | "BRACKET_RIGHT" | "RIGHTBRACKET" | "RIGHT_BRACKET" | "BRACERIGHT"
        | "BRACE_RIGHT" | "RIGHTBRACE" | "RIGHT_BRACE" | "]" | "}" => 0xDD,
        "BACKSLASH" | "BACK_SLASH" | "BAR" | "PIPE" | "\\" | "|" => 0xDC,
        "KP_EQUAL" | "NUMPAD_EQUAL" | "NUMPADEQUAL" | "NUM_EQUAL" | "NUMEQUAL" => 0xBB,
        "KP_MULTIPLY" | "KP_STAR" | "NUMPAD_MULTIPLY" | "NUMPADMULTIPLY" | "NUMPAD_STAR"
        | "NUMPADSTAR" | "NUM_MULTIPLY" | "NUMMULTIPLY" => 0x6A,
        "KP_ADD" | "KP_PLUS" | "NUMPAD_ADD" | "NUMPADADD" | "NUMPAD_PLUS" | "NUMPADPLUS"
        | "NUM_ADD" | "NUMADD" => 0x6B,
        "KP_SEPARATOR" | "NUMPAD_SEPARATOR" | "NUMPADSEPARATOR" => 0x6C,
        "KP_SUBTRACT" | "KP_MINUS" | "NUMPAD_SUBTRACT" | "NUMPADSUBTRACT" | "NUMPAD_MINUS"
        | "NUMPADMINUS" | "NUM_SUBTRACT" | "NUMSUBTRACT" => 0x6D,
        "KP_DECIMAL" | "KP_PERIOD" | "KP_DOT" | "NUMPAD_DECIMAL" | "NUMPADDECIMAL"
        | "NUMPAD_PERIOD" | "NUMPADPERIOD" | "NUMPAD_DOT" | "NUMPADDOT" => 0x6E,
        "KP_DIVIDE" | "KP_SLASH" | "NUMPAD_DIVIDE" | "NUMPADDIVIDE" | "NUMPAD_SLASH"
        | "NUMPADSLASH" | "NUM_DIVIDE" | "NUMDIVIDE" => 0x6F,
        _ if upper.len() == 1 && upper.as_bytes()[0].is_ascii_alphanumeric() => {
            upper.as_bytes()[0] as u16
        }
        _ => {
            for prefix in ["KP_", "KP", "NUMPAD_", "NUMPAD", "NUM_", "NUM"] {
                if let Some(digit) = upper.strip_prefix(prefix)
                    && digit.len() == 1
                    && digit.as_bytes()[0].is_ascii_digit()
                {
                    return Ok(0x60 + (digit.as_bytes()[0] - b'0') as u16);
                }
            }
            if let Some(function) = upper.strip_prefix('F')
                && let Ok(number) = function.parse::<u16>()
                && (1..=24).contains(&number)
            {
                return Ok(0x6F + number);
            }
            bail!("unsupported key {name}; use type_text for literal characters")
        }
    };
    Ok(key)
}

/// Win32 virtual keys that use KEYEVENTF_EXTENDEDKEY.
pub fn extended_key(vk: u16) -> u32 {
    u32::from(matches!(
        vk,
        0xA3 | 0xA5
            | 0x2D
            | 0x2E
            | 0x24
            | 0x23
            | 0x21
            | 0x22
            | 0x25
            | 0x26
            | 0x27
            | 0x28
            | 0x90
            | 0x03
            | 0x2C
            | 0x6F
            | 0x5B
            | 0x5C
            | 0x5D
    ))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn wheel_matches_measured_directions_and_saturation() {
        assert_eq!(wheel_deltas(120, 120), (120, -120));
        assert_eq!(wheel_deltas(-120, -120), (-120, 120));
        assert_eq!(wheel_deltas(40000, 40000), (32767, -32768));
        assert_eq!(wheel_deltas(-40000, -40000), (-32768, 32767));
        assert_eq!(wheel_deltas(i32::MIN, i32::MIN), (-32768, 32767));
        assert_eq!(wheel_deltas(i32::MAX, i32::MAX), (32767, -32768));
        assert_eq!(wheel_deltas(0, 0), (0, 0));
    }

    #[test]
    fn aliases_preserve_key_identity_and_extended_flags() {
        assert_eq!(virtual_key("Control_L").unwrap(), 0xA2);
        assert_eq!(
            virtual_key("ctrl_l").unwrap(),
            virtual_key("Control_L").unwrap()
        );
        assert_eq!(virtual_key("Control_R").unwrap(), 0xA3);
        assert_eq!(extended_key(virtual_key("Control_R").unwrap()), 1);
        assert_eq!(extended_key(virtual_key("Control_L").unwrap()), 0);
        assert_eq!(
            virtual_key("Return").unwrap(),
            virtual_key("Enter").unwrap()
        );
        for name in ["period", "dot", "full_stop", "greater", "."] {
            assert_eq!(virtual_key(name).unwrap(), 0xBE, "{name}");
        }
        for name in ["KP_0", "KP0", "Numpad_0", "NUMPAD0", "NUM_0", "NUM0"] {
            assert_eq!(virtual_key(name).unwrap(), 0x60, "{name}");
        }
        assert_eq!(virtual_key("0").unwrap(), 0x30);
        assert_eq!(extended_key(virtual_key("KP_Divide").unwrap()), 1);
        assert_eq!(extended_key(virtual_key("KP_0").unwrap()), 0);
        assert_eq!(
            virtual_key("KP_Enter").unwrap(),
            virtual_key("Return").unwrap()
        );
        assert_eq!(extended_key(virtual_key("KP_Enter").unwrap()), 0);
        assert!(virtual_key("F25").is_err());
        assert!(virtual_key("KP_10").is_err());
    }

    #[test]
    fn button_aliases_and_unsupported_right_multiclick() {
        for (short, long) in [("l", "left"), ("r", "right"), ("m", "middle")] {
            assert_eq!(
                mouse_button_flags(short).unwrap(),
                mouse_button_flags(long).unwrap()
            );
            assert!(validate_click(short, 1).is_ok());
        }
        assert!(validate_click("r", 2).is_err());
        assert!(validate_click("right", 3).is_err());
        assert!(validate_click("left", 2).is_ok());
        assert!(validate_click("m", 2).is_ok());
        assert!(validate_click("left", 0).is_err());
        assert!(mouse_button_flags("unknown").is_err());
    }
}
