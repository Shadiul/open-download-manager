//! Native messaging host. The browser starts it and talks to it over stdin and stdout; it relays
//! each message to the Open Download Manager app over a per-user named pipe (Windows) or Unix
//! domain socket, and the app's reply back to the browser.
//!
//! If the app is not running, a download or probe starts it in the tray (`--background`) and
//! waits for it to listen. A ping never starts the app: it answers `"running": false` instead.
//! Downloads never run in this process, because the browser may close it after each reply.

use std::io::{self, BufReader, BufWriter, Read, Write};
use std::process::ExitCode;
use std::time::{Duration, Instant};

use odm_core::endpoint;
use odm_core::frame::{self, FrameError, MAX_FROM_BROWSER, MAX_TO_BROWSER};
use odm_core::protocol::Reply;

/// The extension gives up on a download after 8 seconds; leave room for the reply.
const LAUNCH_WAIT: Duration = Duration::from_secs(7);

fn main() -> ExitCode {
    let input = BufReader::new(io::stdin().lock());
    let output = BufWriter::new(io::stdout().lock());
    match run(input, output, &mut SystemApp) {
        Ok(()) => ExitCode::SUCCESS,
        Err(e) => {
            // stderr goes to the browser's log, never to the extension.
            eprintln!("odm-host: {e}");
            ExitCode::FAILURE
        }
    }
}

/// The connection to the app, as a byte stream.
trait Link: Read + Write {}
impl<T: Read + Write> Link for T {}

/// How the host reaches the app; replaced in tests.
trait App {
    fn connect(&mut self) -> io::Result<Box<dyn Link>>;
    fn launch(&mut self) -> io::Result<()>;
}

/// Answers every message until the browser closes stdin.
fn run(mut input: impl Read, mut output: impl Write, app: &mut dyn App) -> Result<(), FrameError> {
    let mut link: Option<Box<dyn Link>> = None;
    while let Some(body) = frame::read_frame(&mut input, MAX_FROM_BROWSER)? {
        let reply = match relay(app, &mut link, &body) {
            Ok(reply) => reply,
            Err(e) => serde_json::to_vec(&offline_reply(&body, &e)).expect("reply serializes"),
        };
        frame::write_frame(&mut output, &reply, MAX_TO_BROWSER)?;
    }
    Ok(())
}

fn message_type(body: &[u8]) -> Option<String> {
    let v: serde_json::Value = serde_json::from_slice(body).ok()?;
    v.get("type")?.as_str().map(String::from)
}

/// What the host says on its own when the app cannot be reached.
fn offline_reply(body: &[u8], e: &io::Error) -> serde_json::Value {
    if message_type(body).as_deref() == Some("ping") {
        let mut v = serde_json::to_value(Reply::pong(odm_core::VERSION)).expect("reply serializes");
        v["running"] = false.into();
        return v;
    }
    serde_json::to_value(Reply::error(format!("Open Download Manager is not running ({e})")))
        .expect("serializes")
}

fn relay(app: &mut dyn App, link: &mut Option<Box<dyn Link>>, body: &[u8]) -> io::Result<Vec<u8>> {
    if let Some(l) = link.as_mut()
        && let Ok(reply) = exchange(l.as_mut(), body)
    {
        return Ok(reply);
    }
    // No link yet, or the app went away since the last message.
    *link = None;
    let mut fresh = match app.connect() {
        Ok(l) => l,
        Err(_) if message_type(body).as_deref() != Some("ping") => launch_and_connect(app)?,
        Err(e) => return Err(e),
    };
    let reply = exchange(fresh.as_mut(), body)?;
    *link = Some(fresh);
    Ok(reply)
}

fn exchange(link: &mut dyn Link, body: &[u8]) -> io::Result<Vec<u8>> {
    let to_io = |e: FrameError| match e {
        FrameError::Io(e) => e,
        other => io::Error::new(io::ErrorKind::InvalidData, other.to_string()),
    };
    frame::write_frame(link, body, MAX_FROM_BROWSER).map_err(to_io)?;
    frame::read_frame(link, MAX_TO_BROWSER)
        .map_err(to_io)?
        .ok_or_else(|| io::Error::new(io::ErrorKind::UnexpectedEof, "the app closed the connection"))
}

fn launch_and_connect(app: &mut dyn App) -> io::Result<Box<dyn Link>> {
    app.launch()?;
    let deadline = Instant::now() + LAUNCH_WAIT;
    loop {
        std::thread::sleep(Duration::from_millis(150));
        match app.connect() {
            Ok(l) => return Ok(l),
            Err(e) if Instant::now() >= deadline => {
                return Err(io::Error::new(e.kind(), format!("the app did not start in time: {e}")));
            }
            Err(_) => {}
        }
    }
}

struct SystemApp;

impl App for SystemApp {
    #[cfg(windows)]
    fn connect(&mut self) -> io::Result<Box<dyn Link>> {
        // A named pipe opens like a file. A busy pipe (all instances taken) is retried by the caller.
        let pipe = std::fs::OpenOptions::new().read(true).write(true).open(endpoint::path())?;
        Ok(Box::new(pipe))
    }

    #[cfg(unix)]
    fn connect(&mut self) -> io::Result<Box<dyn Link>> {
        Ok(Box::new(std::os::unix::net::UnixStream::connect(endpoint::path())?))
    }

    fn launch(&mut self) -> io::Result<()> {
        #[cfg(windows)]
        stop_std_handle_inheritance();
        let exe = std::env::current_exe()?;
        let dir = exe.parent().ok_or_else(|| io::Error::other("host has no folder"))?;
        let app = dir.join(endpoint::APP_EXE);
        let mut cmd = std::process::Command::new(&app);
        cmd.arg(endpoint::BACKGROUND_ARG)
            .current_dir(dir)
            .stdin(std::process::Stdio::null())
            .stdout(std::process::Stdio::null())
            .stderr(std::process::Stdio::null());
        #[cfg(windows)]
        {
            use std::os::windows::process::CommandExt;
            const DETACHED_PROCESS: u32 = 0x0000_0008;
            const CREATE_NEW_PROCESS_GROUP: u32 = 0x0000_0200;
            const CREATE_BREAKAWAY_FROM_JOB: u32 = 0x0100_0000;
            // The browser puts the host in a job that is killed with it; the app must break away.
            cmd.creation_flags(DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP | CREATE_BREAKAWAY_FROM_JOB);
            if cmd.spawn().is_ok() {
                return Ok(());
            }
            cmd.creation_flags(DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP);
        }
        cmd.spawn()
            .map(|_| ())
            .map_err(|e| io::Error::new(e.kind(), format!("cannot start {}: {e}", app.display())))
    }
}

/// Our stdin and stdout are the browser's pipes. Windows hands inheritable handles to every child
/// process, so without this the app would hold the browser's pipes open for as long as it runs.
#[cfg(windows)]
#[allow(unsafe_code)]
fn stop_std_handle_inheritance() {
    use std::ffi::c_void;
    unsafe extern "system" {
        fn GetStdHandle(which: u32) -> *mut c_void;
        fn SetHandleInformation(handle: *mut c_void, mask: u32, flags: u32) -> i32;
    }
    const HANDLE_FLAG_INHERIT: u32 = 1;
    const STD_HANDLES: [u32; 3] = [-10i32 as u32, -11i32 as u32, -12i32 as u32];
    for which in STD_HANDLES {
        // SAFETY: GetStdHandle has no preconditions; SetHandleInformation only changes a flag on
        // a handle we own, and fails harmlessly on null or invalid handles.
        unsafe {
            let handle = GetStdHandle(which);
            if !handle.is_null() && handle as isize != -1 {
                SetHandleInformation(handle, HANDLE_FLAG_INHERIT, 0);
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::{Value, json};
    use std::collections::VecDeque;
    use std::io::Cursor;
    use std::sync::{Arc, Mutex};

    /// A fake app that answers each message by echoing its type.
    #[derive(Default)]
    struct FakeApp {
        running: bool,
        start_on_launch: bool,
        launches: usize,
        seen: Arc<Mutex<Vec<Value>>>,
    }

    struct FakeLink {
        seen: Arc<Mutex<Vec<Value>>>,
        written: Vec<u8>,
        replies: VecDeque<u8>,
    }

    impl Read for FakeLink {
        fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
            let n = buf.len().min(self.replies.len());
            for b in buf.iter_mut().take(n) {
                *b = self.replies.pop_front().unwrap();
            }
            Ok(n)
        }
    }

    impl Write for FakeLink {
        fn write(&mut self, buf: &[u8]) -> io::Result<usize> {
            self.written.extend_from_slice(buf);
            let mut cursor = Cursor::new(self.written.clone());
            if let Ok(Some(body)) = frame::read_frame(&mut cursor, usize::MAX) {
                self.written.drain(..cursor.position() as usize);
                let msg: Value = serde_json::from_slice(&body).unwrap();
                let reply = json!({ "ok": true, "echo": msg["type"] });
                self.seen.lock().unwrap().push(msg);
                let mut out = Vec::new();
                frame::write_json(&mut out, &reply, usize::MAX).unwrap();
                self.replies.extend(out);
            }
            Ok(buf.len())
        }
        fn flush(&mut self) -> io::Result<()> {
            Ok(())
        }
    }

    impl App for FakeApp {
        fn connect(&mut self) -> io::Result<Box<dyn Link>> {
            if !self.running {
                return Err(io::ErrorKind::NotFound.into());
            }
            Ok(Box::new(FakeLink { seen: self.seen.clone(), written: Vec::new(), replies: VecDeque::new() }))
        }
        fn launch(&mut self) -> io::Result<()> {
            self.launches += 1;
            self.running = self.start_on_launch;
            Ok(())
        }
    }

    fn exchange_all(app: &mut FakeApp, messages: &[Value]) -> Vec<Value> {
        let mut input = Vec::new();
        for m in messages {
            frame::write_json(&mut input, m, usize::MAX).unwrap();
        }
        let mut output = Vec::new();
        run(Cursor::new(input), &mut output, app).unwrap();
        let mut replies = Vec::new();
        let mut r = Cursor::new(output);
        while let Some(reply) = frame::read_json::<Value>(&mut r, MAX_TO_BROWSER).unwrap() {
            replies.push(reply);
        }
        replies
    }

    #[test]
    fn relays_to_a_running_app() {
        let mut app = FakeApp { running: true, ..Default::default() };
        let replies = exchange_all(
            &mut app,
            &[json!({ "v": 1, "type": "ping" }), json!({ "v": 1, "type": "download" })],
        );
        assert_eq!(
            replies,
            [json!({ "ok": true, "echo": "ping" }), json!({ "ok": true, "echo": "download" })]
        );
        assert_eq!(app.seen.lock().unwrap().len(), 2);
        assert_eq!(app.launches, 0);
    }

    #[test]
    fn ping_answers_offline_without_starting_the_app() {
        let mut app = FakeApp::default();
        let replies = exchange_all(&mut app, &[json!({ "v": 1, "type": "ping" })]);
        assert_eq!(replies[0]["ok"], true);
        assert_eq!(replies[0]["running"], false);
        assert_eq!(app.launches, 0);
    }

    #[test]
    fn download_starts_the_app() {
        let mut app = FakeApp { start_on_launch: true, ..Default::default() };
        let replies = exchange_all(&mut app, &[json!({ "v": 1, "type": "download" })]);
        assert_eq!(replies[0], json!({ "ok": true, "echo": "download" }));
        assert_eq!(app.launches, 1);
    }

    #[test]
    fn download_fails_cleanly_when_the_app_cannot_start() {
        let mut app = FakeApp::default();
        let replies = exchange_all(&mut app, &[json!({ "v": 1, "type": "download" })]);
        assert_eq!(replies[0]["ok"], false);
        assert!(replies[0]["error"].as_str().unwrap().contains("not running"));
    }
}
