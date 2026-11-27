//! The manager through its JSON commands and events, as the app drives it, and the browser
//! endpoint as the native host reaches it.

mod common;

use std::sync::atomic::Ordering;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use common::{Behaviour, Server, data};
use odm_core::api::{Core, CoreConfig};
use serde_json::{Value, json};

const KB: usize = 1024;

struct Harness {
    core: Option<Core>,
    events: Arc<Mutex<Vec<Value>>>,
    _dir: tempfile::TempDir,
    downloads: std::path::PathBuf,
    data_dir: std::path::PathBuf,
}

impl Harness {
    fn start(dir: tempfile::TempDir, listen: bool) -> Self {
        Self::start_on(dir, listen, None)
    }

    fn start_on(dir: tempfile::TempDir, listen: bool, endpoint: Option<String>) -> Self {
        let downloads = dir.path().join("Downloads");
        let data_dir = dir.path().join("data");
        let events = Arc::new(Mutex::new(Vec::new()));
        let sink = {
            let events = events.clone();
            Arc::new(move |e: String| events.lock().unwrap().push(serde_json::from_str(&e).unwrap()))
        };
        let core = Core::start(
            CoreConfig {
                data_dir: data_dir.clone(),
                default_download_dir: downloads.clone(),
                tools_dir: dir.path().join("tools"),
                listen,
                endpoint,
            },
            sink,
        )
        .unwrap();
        Self { core: Some(core), events, _dir: dir, downloads, data_dir }
    }

    fn call(&self, cmd: Value) -> Value {
        serde_json::from_str(&self.core.as_ref().unwrap().call(&cmd.to_string())).unwrap()
    }

    fn job(&self, id: u64) -> Value {
        let list = self.call(json!({ "type": "list" }));
        list["jobs"].as_array().unwrap().iter().find(|j| j["id"] == id).cloned().unwrap_or(Value::Null)
    }

    fn wait_for(&self, id: u64, what: &str, check: impl Fn(&Value) -> bool) -> Value {
        for _ in 0..300 {
            let job = self.job(id);
            if check(&job) {
                return job;
            }
            std::thread::sleep(Duration::from_millis(50));
        }
        panic!("timed out waiting for {what}: {}", self.job(id));
    }

    fn stop(&mut self) {
        if let Some(core) = self.core.take() {
            core.stop();
        }
    }
}

fn server(content: Vec<u8>) -> (tokio::runtime::Runtime, Server) {
    let rt = tokio::runtime::Builder::new_multi_thread().enable_all().build().unwrap();
    let server = rt.block_on(Server::start(
        content,
        Behaviour { ranges: true, content_length: true, ..Default::default() },
    ));
    (rt, server)
}

#[test]
fn add_download_complete_and_clear() {
    let content = data(KB * KB, 1);
    let (_rt, server) = server(content.clone());
    let mut h = Harness::start(tempfile::tempdir().unwrap(), false);

    let reply = h.call(json!({ "type": "add", "url": server.url }));
    assert_eq!(reply["ok"], true, "{reply}");
    let id = reply["id"].as_u64().unwrap();

    let job = h.wait_for(id, "completion", |j| j["state"] == "completed");
    let path = job["filePath"].as_str().unwrap();
    assert_eq!(std::fs::read(path).unwrap(), content);
    assert!(path.contains("Other"), "category folder: {path}");
    assert_eq!(job["downloaded"], content.len());

    let events = h.events.lock().unwrap().clone();
    assert!(events.iter().any(|e| e["type"] == "jobAdded"));
    assert!(events.iter().any(|e| e["type"] == "activity" && e["active"] == 1));

    assert_eq!(h.call(json!({ "type": "clearCompleted" }))["ok"], true);
    assert_eq!(h.call(json!({ "type": "list" }))["jobs"], json!([]));
    h.stop();
}

#[test]
fn pause_survives_a_restart_and_resumes() {
    let content = data(3 * KB * KB, 2);
    let (_rt, server) = server(content.clone());
    server.behaviour.rate.store(800 * KB as u64, Ordering::SeqCst);
    let dir = tempfile::tempdir().unwrap();
    let dir_path = dir.path().to_path_buf();
    let mut h = Harness::start(dir, false);

    let id = h.call(json!({ "type": "add", "url": server.url }))["id"].as_u64().unwrap();
    h.wait_for(id, "progress", |j| j["downloaded"].as_u64().unwrap_or(0) > 400 * KB as u64);
    assert_eq!(h.call(json!({ "type": "pause", "id": id }))["ok"], true);
    let paused = h.wait_for(id, "pause", |j| j["state"] == "paused" && j["connections"] == 0);
    let fetched = paused["downloaded"].as_u64().unwrap();
    let details = h.call(json!({ "type": "details", "id": id }));
    let segments = details["segments"].as_array().unwrap();
    assert!(segments.len() > 1, "{details}");
    let mapped: u64 =
        segments.iter().map(|s| s["cursor"].as_u64().unwrap() - s["start"].as_u64().unwrap()).sum();
    assert_eq!(mapped, fetched, "the map adds up to what was fetched");

    // Restart the core on the same state file: the paused job and its progress are still there.
    let data_dir = h.data_dir.clone();
    h.stop();
    let events = Arc::new(Mutex::new(Vec::new()));
    let sink = {
        let events = events.clone();
        Arc::new(move |e: String| events.lock().unwrap().push(e))
    };
    let core = Core::start(
        CoreConfig {
            data_dir,
            default_download_dir: h.downloads.clone(),
            tools_dir: dir_path.join("tools"),
            listen: false,
            endpoint: None,
        },
        sink,
    )
    .unwrap();
    h.core = Some(core);
    let job = h.job(id);
    assert_eq!(job["state"], "paused");
    assert!(job["downloaded"].as_u64().unwrap() >= fetched.saturating_sub(64 * KB as u64), "{job}");

    server.behaviour.rate.store(0, Ordering::SeqCst);
    assert_eq!(h.call(json!({ "type": "resume", "id": id }))["ok"], true);
    let done = h.wait_for(id, "completion", |j| j["state"] == "completed");
    assert_eq!(std::fs::read(done["filePath"].as_str().unwrap()).unwrap(), content);
    h.stop();
}

#[test]
fn bad_commands_and_addresses_are_refused() {
    let mut h = Harness::start(tempfile::tempdir().unwrap(), false);
    assert_eq!(h.call(json!({ "type": "launchRockets" }))["ok"], false);
    assert_eq!(h.call(json!({ "type": "add", "url": "file:///etc/passwd" }))["ok"], false);
    assert_eq!(h.call(json!({ "type": "add", "url": "not a url" }))["ok"], false);
    let s = h.call(json!({ "type": "getSettings" }));
    assert_eq!(s["settings"]["connections"], 8);
    let mut settings = s["settings"].clone();
    settings["maxParallel"] = json!(5);
    assert_eq!(h.call(json!({ "type": "setSettings", "settings": settings }))["settings"]["maxParallel"], 5);
    h.stop();
}

#[cfg(windows)]
#[test]
fn browser_downloads_arrive_over_the_pipe() {
    use odm_core::frame;
    let content = data(200 * KB, 3);
    let (_rt, server) = server(content.clone());
    // A pipe of its own, so a running app is not disturbed.
    let name = format!(r"\\.\pipe\odm-test-{}", std::process::id());
    let mut h = Harness::start_on(tempfile::tempdir().unwrap(), true, Some(name.clone()));

    let mut pipe = std::fs::OpenOptions::new().read(true).write(true).open(&name).unwrap();
    let mut ask = |msg: Value| -> Value {
        frame::write_json(&mut pipe, &msg, frame::MAX_FROM_BROWSER).unwrap();
        frame::read_json(&mut pipe, frame::MAX_TO_BROWSER).unwrap().unwrap()
    };
    let pong = ask(json!({ "v": 1, "type": "ping" }));
    assert_eq!(pong["ok"], true);
    assert_eq!(pong["version"], odm_core::VERSION);

    let reply = ask(json!({ "v": 1, "type": "download", "url": server.url, "filename": "from-browser.bin",
                            "fileSize": content.len(), "source": "browser-download", "cookies": "s=1" }));
    assert_eq!(reply, json!({ "ok": true }));
    let reply = ask(json!({ "v": 1, "type": "probe", "url": "https://www.youtube.com/watch?v=x" }));
    assert_eq!(reply["ok"], false, "no yt-dlp in the test tools folder");

    let list = h.call(json!({ "type": "list" }));
    let job = &list["jobs"][0];
    assert_eq!(job["fromBrowser"], true);
    let id = job["id"].as_u64().unwrap();
    assert_eq!(job["awaitingConfirmation"], true);
    assert_eq!(h.call(json!({ "type": "confirm", "id": id, "start": true }))["ok"], true);
    let done = h.wait_for(id, "completion", |j| j["state"] == "completed");
    assert_eq!(std::fs::read(done["filePath"].as_str().unwrap()).unwrap(), content);
    assert!(server.requests().iter().all(|r| !r.is_empty()));
    h.stop();
}

#[test]
fn browser_downloads_wait_for_the_new_download_window() {
    let content = data(300 * KB, 4);
    let (_rt, server) = server(content.clone());
    let mut h = Harness::start(tempfile::tempdir().unwrap(), false);
    let core = h.core.as_ref().unwrap();
    let request: odm_core::protocol::DownloadRequest = serde_json::from_value(json!({
        "url": server.url, "filename": "from-browser.bin", "fileSize": content.len(), "source": "browser-download"
    }))
    .unwrap();
    core.runtime().block_on(core.manager().browser(vec![request])).unwrap();

    let job = h.call(json!({ "type": "list" }))["jobs"][0].clone();
    assert_eq!(job["state"], "paused");
    assert_eq!(job["awaitingConfirmation"], true);
    let id = job["id"].as_u64().unwrap();

    let chosen = h.downloads.join("Chosen");
    let reply = h.call(json!({ "type": "edit", "id": id, "fileName": "my name.bin", "saveDir": chosen }));
    assert_eq!(reply["ok"], true, "{reply}");
    assert_eq!(h.call(json!({ "type": "confirm", "id": id, "start": true }))["ok"], true);

    let done = h.wait_for(id, "completion", |j| j["state"] == "completed");
    assert_eq!(done["awaitingConfirmation"], false);
    assert_eq!(std::path::Path::new(done["filePath"].as_str().unwrap()), chosen.join("my name.bin"));
    assert_eq!(std::fs::read(chosen.join("my name.bin")).unwrap(), content);
    assert_eq!(h.call(json!({ "type": "edit", "id": id, "fileName": "late.bin" }))["ok"], false);
    h.stop();
}
