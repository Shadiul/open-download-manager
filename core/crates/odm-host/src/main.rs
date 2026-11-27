//! Native messaging host. The browser starts it and talks to it over stdin and stdout.
//!
//! For now it answers on its own: `ping` succeeds, and `download` and `probe` reply that the app
//! is not running, so the browser keeps its own download. Relaying to the app over a per-user
//! named pipe (Windows) or Unix domain socket (macOS) comes with the desktop app.

use std::io::{self, BufReader, BufWriter, Read, Write};
use std::process::ExitCode;

use odm_core::frame::{self, FrameError};
use odm_core::protocol::{Envelope, PROTOCOL_VERSION, Reply, Request};

const APP_NOT_RUNNING: &str = "Open Download Manager is not running";

fn main() -> ExitCode {
    let input = BufReader::new(io::stdin().lock());
    let output = BufWriter::new(io::stdout().lock());
    match run(input, output) {
        Ok(()) => ExitCode::SUCCESS,
        Err(e) => {
            // stderr goes to the browser's log, never to the extension.
            eprintln!("odm-host: {e}");
            ExitCode::FAILURE
        }
    }
}

/// Answers every message until the browser closes stdin.
fn run(mut input: impl Read, mut output: impl Write) -> Result<(), FrameError> {
    while let Some(body) = frame::read_frame(&mut input, frame::MAX_FROM_BROWSER)? {
        frame::write_json(&mut output, &handle(&body), frame::MAX_TO_BROWSER)?;
    }
    Ok(())
}

fn handle(body: &[u8]) -> Reply {
    let envelope: Envelope = match serde_json::from_slice(body) {
        Ok(envelope) => envelope,
        Err(e) => return Reply::error(format!("bad message: {e}")),
    };
    if envelope.v != PROTOCOL_VERSION {
        return Reply::error(format!("unsupported protocol version {}", envelope.v));
    }
    match envelope.request {
        Request::Ping => Reply::pong(env!("CARGO_PKG_VERSION")),
        Request::Download(_) | Request::Probe(_) => Reply::error(APP_NOT_RUNNING),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::{Value, json};
    use std::io::Cursor;

    fn exchange(messages: &[Value]) -> Vec<Value> {
        let mut input = Vec::new();
        for m in messages {
            frame::write_json(&mut input, m, usize::MAX).unwrap();
        }
        let mut output = Vec::new();
        run(Cursor::new(input), &mut output).unwrap();

        let mut replies = Vec::new();
        let mut r = Cursor::new(output);
        while let Some(reply) = frame::read_json::<Value>(&mut r, frame::MAX_TO_BROWSER).unwrap() {
            replies.push(reply);
        }
        replies
    }

    #[test]
    fn answers_each_message_in_order() {
        let replies = exchange(&[
            json!({ "v": 1, "type": "ping" }),
            json!({ "v": 1, "type": "download", "url": "https://example.com/a.zip" }),
            json!({ "v": 2, "type": "ping" }),
            json!({ "v": 1, "type": "nope" }),
        ]);
        assert_eq!(replies[0], json!({ "ok": true, "version": env!("CARGO_PKG_VERSION") }));
        assert_eq!(replies[1], json!({ "ok": false, "error": APP_NOT_RUNNING }));
        assert_eq!(replies[2]["error"], "unsupported protocol version 2");
        assert_eq!(replies[3]["ok"], false);
        assert_eq!(replies.len(), 4);
    }

    #[test]
    fn empty_input_is_a_clean_exit() {
        assert!(exchange(&[]).is_empty());
    }
}
