//! The download manager: one task that owns every job, the queue and the scheduler.
//!
//! The app sends [`Command`]s and gets JSON replies; the browser's downloads arrive through
//! [`Manager::browser`]. Changes go out as [`Event`]s. Running downloads report progress through
//! their [`Transfer`], which this task reads twice a second to update speeds, save segment
//! progress and start queued jobs.

use std::collections::BTreeMap;
use std::path::PathBuf;
use std::sync::Arc;
use std::sync::atomic::Ordering;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use chrono::Timelike;
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use tokio::sync::{mpsc, oneshot};
use tokio_util::sync::CancellationToken;

use crate::engine::{self, Env, HttpJob, Outcome, Transfer};
use crate::job::{Job, JobKind, JobRecord, JobState, RequestContext};
use crate::limiter::RateLimiter;
use crate::protocol::{DownloadRequest, MediaKind};
use crate::settings::{Settings, WhenDone};
use crate::store::Store;
use crate::{naming, video};

const SETTINGS_KEY: &str = "settings";
const TOOLS_CHECKED_KEY: &str = "toolsCheckedAt";
/// How long "Refresh address" waits for the user to click the link again in the browser.
const REFRESH_WINDOW: Duration = Duration::from_secs(10 * 60);

pub struct ManagerConfig {
    /// Used when the download folder setting is empty.
    pub default_download_dir: PathBuf,
    /// Holds yt-dlp and ffmpeg.
    pub tools_dir: PathBuf,
}

/// Something the UI should show.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(tag = "type", rename_all = "camelCase", rename_all_fields = "camelCase")]
pub enum Event {
    JobAdded {
        job: Job,
    },
    JobUpdated {
        job: Job,
    },
    JobRemoved {
        id: u64,
    },
    /// The queue ran empty; the app carries out `when_done`.
    QueueFinished {
        when_done: WhenDone,
    },
    /// Downloads running right now; the app keeps the computer awake while this is above zero.
    Activity {
        active: usize,
    },
    Error {
        message: String,
    },
}

pub type EventSink = Arc<dyn Fn(Event) + Send + Sync>;

/// A request from the app. Replies are JSON objects with `ok` and, on failure, `error`.
#[derive(Debug, Clone, Deserialize)]
#[serde(tag = "type", rename_all = "camelCase", rename_all_fields = "camelCase")]
pub enum Command {
    List,
    GetSettings,
    SetSettings {
        settings: Settings,
    },
    Add(AddSpec),
    /// The quality menu for a video page: `{ ok, title, formats }`.
    Probe {
        url: String,
        #[serde(default)]
        cookies: Option<String>,
        #[serde(default)]
        user_agent: Option<String>,
    },
    Pause {
        id: u64,
    },
    /// Also retries a failed download.
    Resume {
        id: u64,
    },
    Remove {
        id: u64,
        #[serde(default)]
        delete_file: bool,
    },
    PauseAll,
    ResumeAll,
    /// Moves a job up (negative) or down the queue.
    Move {
        id: u64,
        delta: i64,
    },
    /// The next download from the browser replaces this job's address.
    RefreshAddress {
        id: u64,
    },
    ClearCompleted,
    SetJobSpeedLimit {
        id: u64,
        limit: u64,
    },
}

/// A new download, from the app's add dialog or the browser.
#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct AddSpec {
    pub url: String,
    pub file_name: Option<String>,
    pub save_dir: Option<String>,
    pub referrer: Option<String>,
    pub cookies: Option<String>,
    pub user_agent: Option<String>,
    pub mime: Option<String>,
    pub size: Option<u64>,
    pub media_kind: Option<MediaKind>,
    pub format_id: Option<String>,
    pub title: Option<String>,
    /// `false` adds the job paused.
    pub start: Option<bool>,
}

impl From<DownloadRequest> for AddSpec {
    fn from(r: DownloadRequest) -> Self {
        let size = r.known_size();
        Self {
            url: r.url,
            file_name: Some(r.filename).filter(|f| !f.is_empty()),
            save_dir: None,
            referrer: r.referrer.filter(|s| !s.is_empty()),
            cookies: r.cookies.filter(|s| !s.is_empty()),
            user_agent: r.user_agent.filter(|s| !s.is_empty()),
            mime: r.mime.filter(|s| !s.is_empty()),
            size,
            media_kind: r.media_kind,
            format_id: r.format_id.filter(|s| !s.is_empty()),
            title: r.page_title.filter(|s| !s.is_empty()),
            start: Some(true),
        }
    }
}

enum Msg {
    Command(Command, oneshot::Sender<Value>),
    Browser(Vec<DownloadRequest>, oneshot::Sender<Result<(), String>>),
    Finished { id: u64, run_id: u64, outcome: Outcome },
    Shutdown(oneshot::Sender<()>),
}

/// A handle to the manager task. Cheap to clone.
#[derive(Clone)]
pub struct Manager {
    tx: mpsc::UnboundedSender<Msg>,
    tools_dir: PathBuf,
}

impl Manager {
    /// Loads saved jobs and settings and starts the manager task. Call inside a Tokio runtime.
    pub fn start(store: Store, config: ManagerConfig, sink: EventSink) -> Self {
        let (tx, rx) = mpsc::unbounded_channel();
        let tools_dir = config.tools_dir.clone();
        let store = Arc::new(store);
        let actor = Actor::new(store.clone(), config, sink, tx.clone());
        let update_tools = actor.settings.auto_update_tools;
        tokio::spawn(actor.run(rx));
        if update_tools {
            tokio::spawn(update_tools_daily(store, tools_dir.clone()));
        }
        Self { tx, tools_dir }
    }

    pub async fn command(&self, cmd: Command) -> Value {
        if let Command::Probe { url, cookies, user_agent } = cmd {
            let ctx = RequestContext { cookies, user_agent, ..Default::default() };
            return match self.probe(&url, &ctx).await {
                Ok(p) => json!({ "ok": true, "title": p.title, "formats": p.formats }),
                Err(e) => json!({ "ok": false, "error": e }),
            };
        }
        let (reply, rx) = oneshot::channel();
        if self.tx.send(Msg::Command(cmd, reply)).is_err() {
            return stopped();
        }
        rx.await.unwrap_or_else(|_| stopped())
    }

    /// Parses and runs a JSON command, as the app sends them.
    pub async fn command_json(&self, text: &str) -> Value {
        match serde_json::from_str::<Command>(text) {
            Ok(cmd) => self.command(cmd).await,
            Err(e) => json!({ "ok": false, "error": format!("Bad command: {e}") }),
        }
    }

    /// Downloads handed over by the browser extension.
    pub async fn browser(&self, items: Vec<DownloadRequest>) -> Result<(), String> {
        let (reply, rx) = oneshot::channel();
        self.tx
            .send(Msg::Browser(items, reply))
            .map_err(|_| "Open Download Manager is closing".to_string())?;
        rx.await.unwrap_or_else(|_| Err("Open Download Manager is closing".into()))
    }

    pub async fn probe(&self, url: &str, ctx: &RequestContext) -> Result<video::ProbeResult, String> {
        video::probe(&self.tools_dir, url, ctx).await
    }

    /// Stops every download, saving its progress, within a few seconds.
    pub async fn shutdown(&self) {
        let (reply, rx) = oneshot::channel();
        if self.tx.send(Msg::Shutdown(reply)).is_ok() {
            let _ = rx.await;
        }
    }
}

fn stopped() -> Value {
    json!({ "ok": false, "error": "The download engine has stopped" })
}

fn now_ms() -> i64 {
    SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_millis() as i64).unwrap_or(0)
}

fn minute_of_day() -> u32 {
    let now = chrono::Local::now();
    now.hour() * 60 + now.minute()
}

async fn update_tools_daily(store: Arc<Store>, tools_dir: PathBuf) {
    let last: i64 = store.get(TOOLS_CHECKED_KEY).ok().flatten().and_then(|v| v.parse().ok()).unwrap_or(0);
    if now_ms() - last < 24 * 3600 * 1000 {
        return;
    }
    let _ = store.set(TOOLS_CHECKED_KEY, &now_ms().to_string());
    let _ = video::self_update(&tools_dir).await;
}

struct Running {
    run_id: u64,
    cancel: CancellationToken,
    transfer: Arc<Transfer>,
    /// Go back to Queued (not Paused) once stopped: a resume during the stop, the schedule, or exit.
    requeue: bool,
}

struct Entry {
    rec: JobRecord,
    run: Option<Running>,
    last_bytes: u64,
    last_at: Instant,
    /// Removed while running: delete once it has stopped (`true` also deletes the file).
    removing: Option<bool>,
}

struct Actor {
    store: Arc<Store>,
    settings: Settings,
    config: ManagerConfig,
    sink: EventSink,
    env: Env,
    tx: mpsc::UnboundedSender<Msg>,
    jobs: BTreeMap<u64, Entry>,
    next_id: u64,
    next_run: u64,
    awaiting_refresh: Option<(u64, Instant)>,
    had_work: bool,
    last_active: usize,
    ticks: u64,
    shutdown: Option<(oneshot::Sender<()>, Instant)>,
}

impl Actor {
    fn new(
        store: Arc<Store>,
        config: ManagerConfig,
        sink: EventSink,
        tx: mpsc::UnboundedSender<Msg>,
    ) -> Self {
        let settings: Settings = store
            .get(SETTINGS_KEY)
            .ok()
            .flatten()
            .and_then(|s| serde_json::from_str(&s).ok())
            .unwrap_or_default();
        let settings = settings.normalized();
        let env = Env::new(engine::client(), Arc::new(RateLimiter::new(settings.speed_limit)));

        let mut jobs = BTreeMap::new();
        for mut rec in store.load_jobs().unwrap_or_default() {
            // Downloads that were running when the app closed carry on.
            if rec.job.state.is_active() {
                rec.job.state = JobState::Queued;
            }
            rec.job.speed = 0;
            rec.job.connections = 0;
            jobs.insert(
                rec.job.id,
                Entry { rec, run: None, last_bytes: 0, last_at: Instant::now(), removing: None },
            );
        }
        let next_id = jobs.keys().next_back().map_or(1, |id| id + 1);
        Self {
            store,
            settings,
            config,
            sink,
            env,
            tx,
            jobs,
            next_id,
            next_run: 1,
            awaiting_refresh: None,
            had_work: false,
            last_active: 0,
            ticks: 0,
            shutdown: None,
        }
    }

    async fn run(mut self, mut rx: mpsc::UnboundedReceiver<Msg>) {
        self.schedule();
        let mut interval = tokio::time::interval(Duration::from_millis(500));
        interval.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);
        loop {
            tokio::select! {
                msg = rx.recv() => match msg {
                    Some(msg) => self.handle(msg),
                    None => break,
                },
                _ = interval.tick() => self.tick(),
            }
            if self.shutdown_done() {
                break;
            }
        }
    }

    fn handle(&mut self, msg: Msg) {
        match msg {
            Msg::Command(cmd, reply) => {
                let value = self.command(cmd);
                let _ = reply.send(value);
            }
            Msg::Browser(items, reply) => {
                let _ = reply.send(self.browser(items));
            }
            Msg::Finished { id, run_id, outcome } => self.finished(id, run_id, outcome),
            Msg::Shutdown(reply) => {
                for e in self.jobs.values_mut() {
                    if let Some(run) = &mut e.run {
                        run.requeue = true;
                        run.cancel.cancel();
                    }
                }
                self.shutdown = Some((reply, Instant::now() + Duration::from_secs(4)));
            }
        }
    }

    fn shutdown_done(&mut self) -> bool {
        let Some((_, deadline)) = &self.shutdown else { return false };
        let running = self.jobs.values().any(|e| e.run.is_some());
        if running && Instant::now() < *deadline {
            return false;
        }
        // Save what the stragglers have, then let the app exit.
        for (id, e) in &self.jobs {
            if let Some(run) = &e.run
                && e.rec.job.kind == JobKind::Http
            {
                let _ = self.store.save_segments(*id, &run.transfer.rows());
            }
        }
        if let Some((reply, _)) = self.shutdown.take() {
            let _ = reply.send(());
        }
        true
    }

    fn emit(&self, event: Event) {
        (self.sink)(event);
    }

    fn emit_job(&self, id: u64) {
        if let Some(e) = self.jobs.get(&id)
            && e.removing.is_none()
        {
            self.emit(Event::JobUpdated { job: e.rec.job.clone() });
        }
    }

    fn save(&self, id: u64) {
        if let Some(e) = self.jobs.get(&id)
            && let Err(err) = self.store.save_job(&e.rec)
        {
            self.emit(Event::Error { message: format!("Cannot save download state: {err}") });
        }
    }

    fn download_dir(&self) -> PathBuf {
        if self.settings.download_dir.trim().is_empty() {
            self.config.default_download_dir.clone()
        } else {
            PathBuf::from(&self.settings.download_dir)
        }
    }

    fn command(&mut self, cmd: Command) -> Value {
        match cmd {
            Command::List => {
                let mut jobs: Vec<&Job> =
                    self.jobs.values().filter(|e| e.removing.is_none()).map(|e| &e.rec.job).collect();
                jobs.sort_by_key(|j| j.position);
                json!({ "ok": true, "jobs": jobs })
            }
            Command::GetSettings => json!({
                "ok": true,
                "settings": self.settings,
                "defaultDownloadDir": self.config.default_download_dir,
                "toolsDir": self.config.tools_dir,
            }),
            Command::SetSettings { settings } => {
                self.settings = settings.normalized();
                self.env.global_limit.set_rate(self.settings.speed_limit);
                let text = serde_json::to_string(&self.settings).expect("settings serialize");
                if let Err(e) = self.store.set(SETTINGS_KEY, &text) {
                    return json!({ "ok": false, "error": e.to_string() });
                }
                self.schedule();
                json!({ "ok": true, "settings": self.settings })
            }
            Command::Add(spec) => match self.add(spec, false) {
                Ok(id) => json!({ "ok": true, "id": id }),
                Err(e) => json!({ "ok": false, "error": e }),
            },
            Command::Probe { .. } => json!({ "ok": false, "error": "probe is handled outside the queue" }),
            Command::Pause { id } => self.pause(id),
            Command::Resume { id } => self.resume(id),
            Command::PauseAll => {
                let ids: Vec<u64> = self.jobs.keys().copied().collect();
                for id in ids {
                    self.pause(id);
                }
                json!({ "ok": true })
            }
            Command::ResumeAll => {
                let ids: Vec<u64> = self
                    .jobs
                    .iter()
                    .filter(|(_, e)| matches!(e.rec.job.state, JobState::Paused | JobState::Failed))
                    .map(|(id, _)| *id)
                    .collect();
                for id in ids {
                    self.resume(id);
                }
                json!({ "ok": true })
            }
            Command::Remove { id, delete_file } => self.remove(id, delete_file),
            Command::Move { id, delta } => self.move_job(id, delta),
            Command::RefreshAddress { id } => {
                if !self.jobs.contains_key(&id) {
                    return json!({ "ok": false, "error": "No such download" });
                }
                self.awaiting_refresh = Some((id, Instant::now()));
                json!({ "ok": true })
            }
            Command::ClearCompleted => {
                let done: Vec<u64> = self
                    .jobs
                    .iter()
                    .filter(|(_, e)| e.rec.job.state == JobState::Completed)
                    .map(|(id, _)| *id)
                    .collect();
                for id in done {
                    self.jobs.remove(&id);
                    let _ = self.store.delete_job(id);
                    self.emit(Event::JobRemoved { id });
                }
                json!({ "ok": true })
            }
            Command::SetJobSpeedLimit { id, limit } => {
                let Some(e) = self.jobs.get_mut(&id) else {
                    return json!({ "ok": false, "error": "No such download" });
                };
                e.rec.job.speed_limit = limit;
                if let Some(run) = &e.run {
                    run.transfer.job_limit.set_rate(limit);
                }
                self.save(id);
                self.emit_job(id);
                json!({ "ok": true })
            }
        }
    }

    fn add(&mut self, spec: AddSpec, from_browser: bool) -> Result<u64, String> {
        let url = spec.url.trim().to_string();
        let parsed = reqwest::Url::parse(&url).map_err(|_| format!("Not a valid address: {url}"))?;
        if !matches!(parsed.scheme(), "http" | "https") {
            return Err("Only http and https addresses can be downloaded".into());
        }
        let kind = match spec.media_kind {
            Some(MediaKind::Hls | MediaKind::Dash | MediaKind::Page) => JobKind::Video,
            _ => JobKind::Http,
        };
        let base = spec.save_dir.map(PathBuf::from).unwrap_or_else(|| self.download_dir());
        let (file_name, save_dir) = match kind {
            JobKind::Video => {
                let folder =
                    if spec.format_id.as_deref() == Some(video::MP3_FORMAT) { "Music" } else { "Video" };
                let dir = if self.settings.category_folders { base.join(folder) } else { base };
                (spec.title.clone().unwrap_or_else(|| "Video".into()), dir)
            }
            JobKind::Http => {
                let name = spec
                    .file_name
                    .as_deref()
                    .map(naming::sanitize)
                    .or_else(|| naming::from_url(&url).map(|n| naming::sanitize(&n)))
                    .unwrap_or_else(|| "download".into());
                (name, base)
            }
        };

        let id = self.next_id;
        self.next_id += 1;
        let position = self.jobs.values().map(|e| e.rec.job.position).max().unwrap_or(0) + 1;
        let job = Job {
            id,
            kind,
            url,
            page_url: spec.referrer.clone(),
            title: spec.title,
            file_name,
            save_dir: save_dir.to_string_lossy().into_owned(),
            total_size: spec.size,
            state: if spec.start.unwrap_or(true) { JobState::Queued } else { JobState::Paused },
            media_kind: spec.media_kind,
            format_id: spec.format_id,
            from_browser,
            created_at: now_ms(),
            position,
            ..Default::default()
        };
        let request = RequestContext {
            referrer: spec.referrer,
            cookies: spec.cookies,
            user_agent: spec.user_agent,
            mime: spec.mime,
            ..Default::default()
        };
        let rec = JobRecord { job, request };
        self.store.save_job(&rec).map_err(|e| format!("Cannot save the download: {e}"))?;
        self.emit(Event::JobAdded { job: rec.job.clone() });
        self.jobs
            .insert(id, Entry { rec, run: None, last_bytes: 0, last_at: Instant::now(), removing: None });
        self.schedule();
        Ok(id)
    }

    fn browser(&mut self, items: Vec<DownloadRequest>) -> Result<(), String> {
        if items.is_empty() {
            return Err("Nothing to download".into());
        }
        // A link clicked again after "Refresh address" gives the waiting download its new address.
        if let Some((id, at)) = self.awaiting_refresh.take()
            && at.elapsed() < REFRESH_WINDOW
            && items.len() == 1
            && let Some(e) = self.jobs.get_mut(&id)
        {
            let item = &items[0];
            e.rec.job.url = item.url.clone();
            e.rec.job.link_expired = false;
            e.rec.job.error = None;
            e.rec.request.cookies = item.cookies.clone().filter(|s| !s.is_empty());
            e.rec.request.user_agent = item.user_agent.clone().filter(|s| !s.is_empty());
            if let Some(r) = item.referrer.clone().filter(|s| !s.is_empty()) {
                e.rec.request.referrer = Some(r);
            }
            if e.run.is_none() && !matches!(e.rec.job.state, JobState::Completed) {
                e.rec.job.state = JobState::Queued;
            }
            self.save(id);
            self.emit_job(id);
            self.schedule();
            return Ok(());
        }
        let mut first_error = None;
        for item in items {
            if let Err(e) = self.add(AddSpec::from(item), true) {
                first_error.get_or_insert(e);
            }
        }
        match first_error {
            Some(e) if self.jobs.is_empty() => Err(e),
            _ => Ok(()),
        }
    }

    fn pause(&mut self, id: u64) -> Value {
        let Some(e) = self.jobs.get_mut(&id) else {
            return json!({ "ok": false, "error": "No such download" });
        };
        if e.rec.job.state.is_finished() {
            return json!({ "ok": true });
        }
        if let Some(run) = &mut e.run {
            run.requeue = false;
            run.cancel.cancel();
        }
        e.rec.job.state = JobState::Paused;
        e.rec.job.speed = 0;
        self.save(id);
        self.emit_job(id);
        json!({ "ok": true })
    }

    fn resume(&mut self, id: u64) -> Value {
        let Some(e) = self.jobs.get_mut(&id) else {
            return json!({ "ok": false, "error": "No such download" });
        };
        if e.rec.job.state == JobState::Completed {
            return json!({ "ok": true });
        }
        if let Some(run) = &mut e.run {
            if !run.cancel.is_cancelled() {
                return json!({ "ok": true }); // already running
            }
            run.requeue = true;
        }
        e.rec.job.state = JobState::Queued;
        e.rec.job.error = None;
        self.save(id);
        self.emit_job(id);
        self.schedule();
        json!({ "ok": true })
    }

    fn remove(&mut self, id: u64, delete_file: bool) -> Value {
        let Some(e) = self.jobs.get_mut(&id) else {
            return json!({ "ok": false, "error": "No such download" });
        };
        if let Some(run) = &e.run {
            run.cancel.cancel();
            e.removing = Some(delete_file);
            self.emit(Event::JobRemoved { id });
            return json!({ "ok": true });
        }
        let rec = self.jobs.remove(&id).map(|e| e.rec);
        if let Some(rec) = rec {
            delete_files(&rec, delete_file);
        }
        let _ = self.store.delete_job(id);
        self.emit(Event::JobRemoved { id });
        self.schedule();
        json!({ "ok": true })
    }

    fn move_job(&mut self, id: u64, delta: i64) -> Value {
        let mut order: Vec<u64> =
            self.jobs.iter().filter(|(_, e)| e.removing.is_none()).map(|(id, _)| *id).collect();
        order.sort_by_key(|id| self.jobs[id].rec.job.position);
        let Some(from) = order.iter().position(|x| *x == id) else {
            return json!({ "ok": false, "error": "No such download" });
        };
        let to = (from as i64 + delta).clamp(0, order.len() as i64 - 1) as usize;
        let moved = order.remove(from);
        order.insert(to, moved);
        for (i, job_id) in order.iter().enumerate() {
            let e = self.jobs.get_mut(job_id).expect("listed job exists");
            if e.rec.job.position != i as i64 + 1 {
                e.rec.job.position = i as i64 + 1;
                self.save(*job_id);
                self.emit_job(*job_id);
            }
        }
        json!({ "ok": true })
    }

    /// Starts queued jobs while there are free slots and the schedule allows it.
    fn schedule(&mut self) {
        if self.shutdown.is_none() && self.settings.schedule.is_open(minute_of_day()) {
            let running = self.jobs.values().filter(|e| e.run.is_some()).count();
            let free = self.settings.max_parallel.saturating_sub(running);
            let mut queued: Vec<(i64, u64)> = self
                .jobs
                .values()
                .filter(|e| e.rec.job.state == JobState::Queued && e.run.is_none() && e.removing.is_none())
                .map(|e| (e.rec.job.position, e.rec.job.id))
                .collect();
            queued.sort();
            for (_, id) in queued.into_iter().take(free) {
                self.start(id);
            }
        }
        let active = self.jobs.values().filter(|e| e.run.is_some()).count();
        if active != self.last_active {
            self.last_active = active;
            self.emit(Event::Activity { active });
        }
    }

    fn start(&mut self, id: u64) {
        let segments = self.store.load_segments(id).unwrap_or_default();
        let tools_dir = self.config.tools_dir.clone();
        let connections = self.settings.connections;
        let category_folders = self.settings.category_folders;
        let env = self.env.clone();
        let tx = self.tx.clone();
        let run_id = self.next_run;
        self.next_run += 1;

        let Some(e) = self.jobs.get_mut(&id) else { return };
        let job = &mut e.rec.job;
        job.state = JobState::Connecting;
        job.error = None;
        job.link_expired = false;
        job.connections = 0;
        job.speed = 0;

        let transfer = Transfer::with_segments(&segments);
        transfer.job_limit.set_rate(job.speed_limit);
        let cancel = CancellationToken::new();
        match job.kind {
            JobKind::Http => {
                let http = HttpJob {
                    url: job.url.clone(),
                    request: e.rec.request.clone(),
                    file_name: job.file_name.clone(),
                    save_dir: PathBuf::from(&job.save_dir),
                    category_folders: category_folders && !e.rec.request.name_decided,
                    total: job.total_size,
                    resumable: job.resumable,
                    segments,
                    connections,
                };
                let (t, c) = (transfer.clone(), cancel.clone());
                tokio::spawn(async move {
                    let outcome = engine::run(http, env, t, c).await;
                    let _ = tx.send(Msg::Finished { id, run_id, outcome });
                });
            }
            JobKind::Video => {
                let v = video::VideoJob {
                    url: job.url.clone(),
                    request: e.rec.request.clone(),
                    media_kind: job.media_kind,
                    format_id: job.format_id.clone(),
                    save_dir: PathBuf::from(&job.save_dir),
                    tools_dir,
                    speed_limit: job.speed_limit.max(self.settings.speed_limit),
                };
                let (t, c) = (transfer.clone(), cancel.clone());
                tokio::spawn(async move {
                    let outcome = video::run(v, t, c).await;
                    let _ = tx.send(Msg::Finished { id, run_id, outcome });
                });
            }
        }
        e.last_bytes = e.rec.job.downloaded;
        e.last_at = Instant::now();
        e.run = Some(Running { run_id, cancel, transfer, requeue: false });
        self.had_work = true;
        self.save(id);
        self.emit_job(id);
    }

    fn tick(&mut self) {
        self.ticks += 1;
        let persist = self.ticks.is_multiple_of(2);
        let now = Instant::now();

        // Outside the schedule window running downloads go back to the queue.
        if !self.settings.schedule.is_open(minute_of_day()) {
            for e in self.jobs.values_mut() {
                if let Some(run) = &mut e.run
                    && !run.cancel.is_cancelled()
                {
                    run.requeue = true;
                    run.cancel.cancel();
                    e.rec.job.state = JobState::Queued;
                }
            }
        }

        let mut updated = Vec::new();
        for (id, e) in self.jobs.iter_mut() {
            let Some(run) = &e.run else { continue };
            let t = &run.transfer;
            let job = &mut e.rec.job;
            if let Some(s) = t.settled() {
                job.file_name = s.file_name;
                job.save_dir = s.save_dir.to_string_lossy().into_owned();
                job.total_size = s.total;
                job.resumable = s.resumable;
                e.rec.request = s.request;
            }
            let downloaded = t.downloaded();
            if job.kind == JobKind::Video {
                let total = t.external_total.load(Ordering::Relaxed);
                if total > 0 {
                    job.total_size = Some(total);
                }
            }
            let dt = now.duration_since(e.last_at).as_secs_f64();
            if dt >= 0.4 {
                let instant = downloaded.saturating_sub(e.last_bytes) as f64 / dt;
                job.speed = if job.speed == 0 {
                    instant as u64
                } else {
                    (0.6 * job.speed as f64 + 0.4 * instant) as u64
                };
                e.last_bytes = downloaded;
                e.last_at = now;
            }
            job.downloaded = downloaded;
            job.connections = t.active.load(Ordering::Relaxed);
            if !run.cancel.is_cancelled() {
                if job.connections > 0 {
                    job.state = JobState::Downloading;
                } else if job.state == JobState::Downloading {
                    job.state = JobState::WaitingToRetry;
                }
            }
            if persist && job.kind == JobKind::Http {
                let _ = self.store.save_segments(*id, &t.rows());
            }
            updated.push(*id);
        }
        for id in updated {
            if persist {
                self.save(id);
            }
            self.emit_job(id);
        }
        self.schedule();
    }

    fn finished(&mut self, id: u64, run_id: u64, outcome: Outcome) {
        let Some(e) = self.jobs.get_mut(&id) else { return };
        if e.run.as_ref().map(|r| r.run_id) != Some(run_id) {
            return;
        }
        let run = e.run.take().expect("checked above");
        if let Some(s) = run.transfer.settled() {
            e.rec.job.file_name = s.file_name;
            e.rec.job.save_dir = s.save_dir.to_string_lossy().into_owned();
            e.rec.job.total_size = s.total;
            e.rec.job.resumable = s.resumable;
            e.rec.request = s.request;
        }
        e.rec.job.downloaded = run.transfer.downloaded();
        e.rec.job.speed = 0;
        e.rec.job.connections = 0;

        if let Some(delete_file) = e.removing {
            let rec = self.jobs.remove(&id).expect("present").rec;
            delete_files(&rec, delete_file);
            let _ = self.store.delete_job(id);
            self.schedule();
            self.check_queue_finished();
            return;
        }

        let rows = run.transfer.rows();
        match outcome {
            Outcome::Completed { path } => {
                let size = std::fs::metadata(&path).map(|m| m.len()).ok();
                let job = &mut e.rec.job;
                job.state = JobState::Completed;
                job.file_name =
                    path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
                if let Some(dir) = path.parent() {
                    job.save_dir = dir.to_string_lossy().into_owned();
                }
                job.file_path = Some(path.to_string_lossy().into_owned());
                job.finished_at = Some(now_ms());
                if let Some(size) = size {
                    job.downloaded = size;
                    job.total_size = Some(size);
                }
                let _ = self.store.save_segments(id, &[]);
            }
            Outcome::Cancelled => {
                e.rec.job.state = if run.requeue { JobState::Queued } else { JobState::Paused };
                if e.rec.job.kind == JobKind::Http {
                    let _ = self.store.save_segments(id, &rows);
                }
            }
            Outcome::Failed { error, link_expired } => {
                e.rec.job.state = JobState::Failed;
                e.rec.job.error = Some(error);
                e.rec.job.link_expired = link_expired;
                if e.rec.job.kind == JobKind::Http {
                    let _ = self.store.save_segments(id, &rows);
                }
            }
        }
        self.save(id);
        self.emit_job(id);
        self.schedule();
        self.check_queue_finished();
    }

    fn check_queue_finished(&mut self) {
        if self.shutdown.is_some() || !self.had_work {
            return;
        }
        let busy = self.jobs.values().any(|e| e.run.is_some() || e.rec.job.state == JobState::Queued);
        if !busy {
            self.had_work = false;
            if self.settings.when_done != WhenDone::Nothing {
                self.emit(Event::QueueFinished { when_done: self.settings.when_done });
            }
        }
    }
}

/// Removes the `.part` file, and the finished file when asked.
fn delete_files(rec: &JobRecord, delete_file: bool) {
    let job = &rec.job;
    if rec.request.name_decided {
        let _ = std::fs::remove_file(engine::part_file(std::path::Path::new(&job.save_dir), &job.file_name));
    }
    if delete_file && let Some(path) = &job.file_path {
        let _ = std::fs::remove_file(path);
    }
}
