mod accessibility;
mod capture;
pub mod capture_wgc;
mod indicator;
mod input;
pub mod input_compat;
mod native;
pub mod server;

use serde_json::{Value, json};
use std::io::{self, BufRead, Read, Write};

pub fn run_driver() {
    native::physical_dpi();
    let args: Vec<String> = std::env::args().collect();
    if run_worker() {
        return;
    }
    let output = args
        .iter()
        .position(|x| x == "--output-dir")
        .and_then(|i| args.get(i + 1))
        .map(std::path::PathBuf::from)
        .unwrap_or_else(|| std::env::temp_dir().join("OmniCore-Desktop"));
    let ttl = args
        .iter()
        .position(|x| x == "--observation-ttl")
        .and_then(|i| args.get(i + 1))
        .and_then(|s| s.parse::<f64>().ok())
        .unwrap_or(30.0);
    if !ttl.is_finite() || !(0.1..=60.0).contains(&ttl) {
        eprintln!("Invalid observation TTL");
        std::process::exit(2)
    }
    let locale = args
        .iter()
        .position(|a| a == "--locale")
        .and_then(|i| args.get(i + 1))
        .map_or("es", String::as_str);
    let _indicator = match indicator::Indicator::start(locale) {
        Ok(indicator) => indicator,
        Err(error) => {
            eprintln!("{}", error.value());
            return;
        }
    };
    let mut server = server::Server::new(output, ttl, args.iter().any(|a| a == "--allow-input"));
    let mut reader = io::BufReader::new(io::stdin().lock());
    loop {
        let mut line = Vec::new();
        match reader.by_ref().take(65537).read_until(b'\n', &mut line) {
            Ok(0) => break,
            Ok(_) => {}
            Err(_) => break,
        }
        if line.len() > 65536 {
            while line.last() != Some(&b'\n') {
                line.clear();
                if reader
                    .by_ref()
                    .take(65537)
                    .read_until(b'\n', &mut line)
                    .unwrap_or(0)
                    == 0
                {
                    break;
                }
            }
            println!(
                "{}",
                json!({"id":null,"ok":false,"error":{"code":"REQUEST_TOO_LARGE","message":"Request exceeds 64 KiB."}})
            );
            continue;
        }
        if line.iter().all(u8::is_ascii_whitespace) {
            continue;
        }
        let answer = {
            let _request = indicator::Request::begin();
            match serde_json::from_slice::<Value>(&line) {
                Ok(request) => server.dispatch(request),
                Err(_) => {
                    json!({"id":null,"ok":false,"error":{"code":"INVALID_JSON","message":"Malformed JSON."}})
                }
            }
        };
        if writeln!(io::stdout().lock(), "{answer}")
            .and_then(|_| io::stdout().flush())
            .is_err()
        {
            break;
        }
    }
}

pub fn run_worker() -> bool {
    native::physical_dpi();
    let args: Vec<String> = std::env::args().collect();
    if let Some(at) = args.iter().position(|x| x == "--worker") {
        let mut raw = String::new();
        let result = io::stdin()
            .take(65537)
            .read_to_string(&mut raw)
            .map_err(|_| server::Error::new("INVALID_JSON", "Cannot read worker request."))
            .and_then(|_| {
                serde_json::from_str::<Value>(&raw)
                    .map_err(|_| server::Error::new("INVALID_JSON", "Malformed worker JSON."))
            })
            .and_then(|data| match args.get(at + 1).map(String::as_str) {
                Some("capture") => capture::capture(&data),
                Some("uia_action") => accessibility::action(&data),
                Some("accessibility") => accessibility::snapshot(&data),
                _ => Err(server::Error::new("INVALID_PARAMS", "Unknown worker.")),
            });
        println!(
            "{}",
            match result {
                Ok(v) => json!({"ok":true,"result":v}),
                Err(e) => json!({"ok":false,"error":e.value()}),
            }
        );
        return true;
    }
    false
}
