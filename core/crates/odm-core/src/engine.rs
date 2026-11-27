//! The segmented HTTP engine for one download.
//!
//! The first request asks for `bytes=0-`. A 206 reply means ranges work: the file is split into
//! one range per connection and the first reply's body becomes the first range. A 200 reply means
//! one connection and no resume. Each connection writes at its own offset in `name.ext.part`;
//! when one finishes, it takes the second half of the largest range still in progress. On resume
//! every request carries `If-Range`, so a file that changed on the server restarts from zero
//! instead of being stitched together from two versions.

use std::collections::HashMap;
use std::fs::{File, OpenOptions};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU32, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use futures_util::StreamExt;
use reqwest::{Client, Response, StatusCode, header};
use tokio::task::JoinSet;
use tokio_util::sync::CancellationToken;

use crate::fileio::write_all_at;
use crate::job::RequestContext;
use crate::limiter::RateLimiter;
use crate::segments::{self, ByteRange, DEFAULT_MIN_SPLIT, SegmentRow};
use crate::{lock, naming};

/// Tries per connection before the download fails; waits are 2, 4, 8 and 16 seconds.
pub const MAX_ATTEMPTS: u32 = 5;
/// The end of a range whose size the server did not say.
pub const UNKNOWN_END: u64 = u64::MAX;

/// Builds the client every download shares. No automatic decompression: it breaks byte ranges.
pub fn client() -> Client {
    Client::builder()
        .connect_timeout(Duration::from_secs(20))
        .read_timeout(Duration::from_secs(45))
        .user_agent(concat!("OpenDownloadManager/", env!("CARGO_PKG_VERSION")))
        .build()
        .expect("HTTP client builds")
}

/// What the engine needs to start or resume a download.
#[derive(Debug, Clone)]
pub struct HttpJob {
    pub url: String,
    pub request: RequestContext,
    /// The name so far: a hint before the first response, the settled name after it.
    pub file_name: String,
    /// The base folder before the first response; the final folder after it.
    pub save_dir: PathBuf,
    pub category_folders: bool,
    /// The user named the file: use `file_name` as it is, not the server's name.
    pub fixed_name: bool,
    pub total: Option<u64>,
    pub resumable: bool,
    pub segments: Vec<SegmentRow>,
    pub connections: usize,
}

/// Shared between connections, limiters and per-host connection limits.
#[derive(Clone)]
pub struct Env {
    pub client: Client,
    pub global_limit: Arc<RateLimiter>,
    /// Hosts that answered 429 or 503, with the most connections they put up with.
    pub host_limits: Arc<Mutex<HashMap<String, usize>>>,
    pub min_split: u64,
}

impl Env {
    pub fn new(client: Client, global_limit: Arc<RateLimiter>) -> Self {
        Self { client, global_limit, host_limits: Arc::default(), min_split: DEFAULT_MIN_SPLIT }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Outcome {
    Completed { path: PathBuf },
    Cancelled,
    Failed { error: String, link_expired: bool },
}

/// What the first response settled, for the manager to store.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct Settled {
    pub file_name: String,
    pub save_dir: PathBuf,
    pub total: Option<u64>,
    pub resumable: bool,
    pub request: RequestContext,
}

/// Live progress, read by the manager twice a second.
#[derive(Default)]
pub struct Transfer {
    segs: Mutex<Vec<Arc<Seg>>>,
    /// Connections streaming right now.
    pub active: AtomicU32,
    /// Bytes counted by a downloader that does not use segments (yt-dlp).
    pub external_downloaded: AtomicU64,
    pub external_total: AtomicU64,
    settled: Mutex<Option<Settled>>,
    /// Set when a server answered 429 or 503.
    pub throttled: AtomicU32,
    /// This download's own speed limit; the manager can change it while it runs.
    pub job_limit: RateLimiter,
}

impl Transfer {
    pub fn with_segments(rows: &[SegmentRow]) -> Arc<Self> {
        let t = Self::default();
        *lock(&t.segs) = rows.iter().map(|r| Seg::new(r.start, r.cursor, r.end)).collect();
        Arc::new(t)
    }

    pub fn rows(&self) -> Vec<SegmentRow> {
        lock(&self.segs).iter().map(|s| s.row()).collect()
    }

    pub fn downloaded(&self) -> u64 {
        let segs = lock(&self.segs);
        if segs.is_empty() {
            return self.external_downloaded.load(Ordering::Relaxed);
        }
        segs.iter().map(|s| s.row()).map(|r| r.cursor.saturating_sub(r.start)).sum()
    }

    /// What the first response settled, once it has arrived.
    pub fn settled(&self) -> Option<Settled> {
        lock(&self.settled).clone()
    }

    fn set_segments(&self, segs: Vec<Arc<Seg>>) {
        *lock(&self.segs) = segs;
    }
}

/// One connection's byte range. `pending` bytes past the cursor are being written right now, so
/// a split never hands them to another connection.
struct Seg {
    start: u64,
    pos: Mutex<Pos>,
    busy: AtomicBool,
}

#[derive(Clone, Copy)]
struct Pos {
    cursor: u64,
    pending: u64,
    end: u64,
}

impl Seg {
    fn new(start: u64, cursor: u64, end: u64) -> Arc<Self> {
        Arc::new(Self {
            start,
            pos: Mutex::new(Pos { cursor, pending: 0, end }),
            busy: AtomicBool::new(false),
        })
    }

    fn row(&self) -> SegmentRow {
        let p = *lock(&self.pos);
        SegmentRow { start: self.start, cursor: p.cursor, end: p.end }
    }

    fn is_done(&self) -> bool {
        let p = lock(&self.pos);
        p.cursor >= p.end
    }

    /// Claims up to `n` bytes at the cursor: `(offset, len)`, with `len` 0 once the range is done.
    fn reserve(&self, n: u64) -> (u64, u64) {
        let mut p = lock(&self.pos);
        let len = n.min(p.end.saturating_sub(p.cursor));
        p.pending = len;
        (p.cursor, len)
    }

    fn commit(&self) {
        let mut p = lock(&self.pos);
        p.cursor += p.pending;
        p.pending = 0;
    }

    /// The body ended and the size was never known: the range ends here.
    fn close_at_cursor(&self) {
        let mut p = lock(&self.pos);
        p.end = p.cursor;
    }

    fn end_unknown(&self) -> bool {
        lock(&self.pos).end == UNKNOWN_END
    }

    /// Bytes nobody has claimed yet.
    fn unclaimed(&self) -> ByteRange {
        let p = lock(&self.pos);
        let from = p.cursor + p.pending;
        ByteRange::new(from.min(p.end), p.end)
    }

    /// Moves the end down to `new_end` if no claimed byte lies past it.
    fn shrink_end(&self, new_end: u64) -> bool {
        let mut p = lock(&self.pos);
        if new_end >= p.cursor + p.pending && new_end < p.end {
            p.end = new_end;
            true
        } else {
            false
        }
    }
}

enum Step {
    Done(Outcome),
    /// The file changed on the server: start again from zero.
    Restart,
}

/// Runs a download until it completes, fails or `cancel` fires.
pub async fn run(mut job: HttpJob, env: Env, transfer: Arc<Transfer>, cancel: CancellationToken) -> Outcome {
    let mut restarts = 0;
    // A restart settles the name and category again, from the folder the download started in.
    let base_dir = job.save_dir.clone();
    loop {
        match run_once(&mut job, &env, &transfer, &cancel).await {
            Step::Done(outcome) => return outcome,
            Step::Restart if restarts < 2 => {
                restarts += 1;
                if job.request.name_decided {
                    let _ = std::fs::remove_file(naming::part_path(&job.save_dir.join(&job.file_name)));
                }
                job.segments.clear();
                job.save_dir = base_dir.clone();
                job.request.name_decided = false;
                job.request.etag = None;
                job.request.last_modified = None;
                transfer.set_segments(Vec::new());
            }
            Step::Restart => {
                return Outcome::Failed {
                    error: "The file keeps changing on the server".into(),
                    link_expired: false,
                };
            }
        }
    }
}

async fn run_once(
    job: &mut HttpJob,
    env: &Env,
    transfer: &Arc<Transfer>,
    cancel: &CancellationToken,
) -> Step {
    let mut first: Option<Response> = None;
    // Where this run's connections go: the mirror the first answer came from, when there was one,
    // so every connection reads the same copy of the file.
    let mut url = job.url.clone();
    let part;

    let can_resume = job.request.name_decided
        && !job.segments.is_empty()
        && (job.resumable || job.segments.iter().all(|s| s.cursor == s.start))
        && naming::part_path(&job.save_dir.join(&job.file_name)).exists();

    if can_resume {
        part = naming::part_path(&job.save_dir.join(&job.file_name));
    } else {
        // Fresh start: the first request settles the name, size and whether ranges work.
        let resp = match first_response(job, env, cancel).await {
            Ok(r) => r,
            Err(step) => return step,
        };
        let status = resp.status();
        let headers = resp.headers().clone();
        let content_range_total = headers
            .get(header::CONTENT_RANGE)
            .and_then(|v| v.to_str().ok())
            .and_then(|v| v.rsplit('/').next())
            .and_then(|v| v.trim().parse::<u64>().ok());
        let (total, resumable) = if status == StatusCode::PARTIAL_CONTENT {
            (content_range_total, content_range_total.is_some())
        } else {
            (resp.content_length(), false)
        };

        let disposition = headers.get(header::CONTENT_DISPOSITION).and_then(|v| v.to_str().ok());
        let mime = headers
            .get(header::CONTENT_TYPE)
            .and_then(|v| v.to_str().ok())
            .map(String::from)
            .or(job.request.mime.clone());
        let final_url = resp.url().to_string();
        let host_of = |u: &str| reqwest::Url::parse(u).ok().and_then(|u| u.host_str().map(String::from));
        job.request.mirrored = host_of(&final_url) != host_of(&job.url);
        url = final_url.clone();
        let name = if job.fixed_name {
            naming::sanitize(&job.file_name)
        } else {
            naming::choose(disposition, Some(&job.file_name), &final_url, mime.as_deref())
        };
        let dir = if job.category_folders {
            job.save_dir.join(naming::category(&name))
        } else {
            job.save_dir.clone()
        };
        if let Err(e) = std::fs::create_dir_all(&dir) {
            return fail(format!("Cannot create folder {}: {e}", dir.display()));
        }
        let final_path = naming::unique_path(&dir, &name);
        part = naming::part_path(&final_path);

        job.file_name = final_path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or(name);
        job.save_dir = dir;
        job.total = total;
        job.resumable = resumable;
        job.request.mime = mime;
        job.request.etag = headers.get(header::ETAG).and_then(|v| v.to_str().ok()).map(String::from);
        job.request.last_modified =
            headers.get(header::LAST_MODIFIED).and_then(|v| v.to_str().ok()).map(String::from);
        job.request.name_decided = true;

        let created = File::create(&part).and_then(|f| match total {
            Some(t) => f.set_len(t),
            None => Ok(()),
        });
        if let Err(e) = created {
            return fail(format!("Cannot create {}: {e}", part.display()));
        }

        job.segments = match total {
            Some(0) => Vec::new(),
            Some(t) if resumable => segments::initial_plan(t, job.connections, env.min_split)
                .into_iter()
                .map(SegmentRow::fresh)
                .collect(),
            Some(t) => vec![SegmentRow::fresh(ByteRange::new(0, t))],
            None => vec![SegmentRow { start: 0, cursor: 0, end: UNKNOWN_END }],
        };
        *lock(&transfer.settled) = Some(Settled {
            file_name: job.file_name.clone(),
            save_dir: job.save_dir.clone(),
            total,
            resumable,
            request: job.request.clone(),
        });
        first = Some(resp);
    }

    if !job.resumable {
        // Without ranges the only way to continue is from the start.
        for s in &mut job.segments {
            s.cursor = s.start;
        }
    }

    let file = match OpenOptions::new().write(true).open(&part) {
        Ok(f) => Arc::new(f),
        Err(e) => return fail(format!("Cannot open {}: {e}", part.display())),
    };
    let segs: Vec<Arc<Seg>> = job.segments.iter().map(|r| Seg::new(r.start, r.cursor, r.end)).collect();
    transfer.set_segments(segs.clone());

    let host =
        reqwest::Url::parse(&job.url).ok().and_then(|u| u.host_str().map(String::from)).unwrap_or_default();
    let host_cap = lock(&env.host_limits).get(&host).copied().unwrap_or(usize::MAX);
    let mut max_conns = job.connections.clamp(1, segments::MAX_CONNECTIONS).min(host_cap).max(1);

    let worker_env = Arc::new(WorkerEnv {
        client: env.client.clone(),
        url,
        request: job.request.clone(),
        resumable: job.resumable,
        file: file.clone(),
        global_limit: env.global_limit.clone(),
        cancel: cancel.child_token(),
        transfer: transfer.clone(),
    });

    let mut set = JoinSet::new();
    let spawn = |set: &mut JoinSet<_>, seg: Arc<Seg>, resp: Option<Response>| {
        let env = worker_env.clone();
        set.spawn(async move {
            let r = worker(&seg, resp, &env).await;
            seg.busy.store(false, Ordering::Relaxed);
            r
        });
    };

    for seg in segs.iter().filter(|s| !s.is_done()).take(max_conns) {
        seg.busy.store(true, Ordering::Relaxed);
        // The first reply covers the file from byte 0, so it feeds the first range.
        let resp = if seg.start == 0 && seg.row().cursor == 0 { first.take() } else { None };
        spawn(&mut set, seg.clone(), resp);
    }
    drop(first);
    transfer.active.store(set.len() as u32, Ordering::Relaxed);

    let mut failure: Option<Step> = None;
    let mut seen_throttles = transfer.throttled.load(Ordering::Relaxed);
    while let Some(joined) = set.join_next().await {
        let result =
            joined.unwrap_or_else(|e| Err(WorkerError::Fatal(format!("Connection task failed: {e}"))));
        match result {
            Ok(()) => {}
            Err(WorkerError::Cancelled) => {}
            Err(e) if failure.is_none() => {
                worker_env.cancel.cancel();
                failure = Some(match e {
                    WorkerError::Changed => Step::Restart,
                    WorkerError::Expired(msg) => {
                        Step::Done(Outcome::Failed { error: msg, link_expired: true })
                    }
                    WorkerError::Fatal(msg) => {
                        Step::Done(Outcome::Failed { error: msg, link_expired: false })
                    }
                    WorkerError::Cancelled => unreachable!(),
                });
            }
            Err(_) => {}
        }

        // A server that pushed back gets one connection fewer, remembered for the host.
        let throttles = transfer.throttled.load(Ordering::Relaxed);
        if throttles > seen_throttles {
            seen_throttles = throttles;
            max_conns = (set.len()).max(1);
            lock(&env.host_limits).insert(host.clone(), max_conns);
        }

        if failure.is_none() && !worker_env.cancel.is_cancelled() {
            while set.len() < max_conns {
                match next_segment(transfer, job.resumable, env.min_split) {
                    Some(seg) => spawn(&mut set, seg, None),
                    None => break,
                }
            }
        }
        transfer.active.store(set.len() as u32, Ordering::Relaxed);
    }
    transfer.active.store(0, Ordering::Relaxed);
    job.segments = transfer.rows();

    if let Some(step) = failure {
        return step;
    }
    if cancel.is_cancelled() {
        let _ = file.sync_all();
        return Step::Done(Outcome::Cancelled);
    }
    if !job.segments.iter().all(SegmentRow::is_done) {
        return fail("The download stopped before the file was complete".into());
    }
    if let Err(e) = file.sync_all() {
        return fail(format!("Cannot save the file: {e}"));
    }
    drop(worker_env);
    drop(file);

    // Rename name.ext.part to name.ext, avoiding a file that appeared meanwhile.
    let mut final_path = job.save_dir.join(&job.file_name);
    if final_path.exists() {
        final_path = naming::unique_path(&job.save_dir, &job.file_name);
    }
    match std::fs::rename(&part, &final_path) {
        Ok(()) => Step::Done(Outcome::Completed { path: final_path }),
        Err(e) => fail(format!("Cannot rename {}: {e}", part.display())),
    }
}

fn fail(error: String) -> Step {
    Step::Done(Outcome::Failed { error, link_expired: false })
}

/// The first request, retried on network errors and server errors.
async fn first_response(job: &HttpJob, env: &Env, cancel: &CancellationToken) -> Result<Response, Step> {
    let mut attempt = 0;
    loop {
        let sent = tokio::select! {
            r = send(&env.client, &job.url, &job.request, 0, None, None) => r,
            _ = cancel.cancelled() => return Err(Step::Done(Outcome::Cancelled)),
        };
        let retry_reason = match sent {
            Ok(resp) => {
                let status = resp.status();
                if status.is_success() {
                    return Ok(resp);
                }
                if status == StatusCode::FORBIDDEN
                    || status == StatusCode::GONE
                    || status == StatusCode::UNAUTHORIZED
                {
                    return Err(Step::Done(Outcome::Failed {
                        error: format!("Link expired or needs a login (server answered {})", status.as_u16()),
                        link_expired: true,
                    }));
                }
                if status.is_server_error()
                    || status == StatusCode::TOO_MANY_REQUESTS
                    || status == StatusCode::REQUEST_TIMEOUT
                {
                    format!("Server answered {}", status.as_u16())
                } else {
                    return Err(fail(format!("Server answered {}", status.as_u16())));
                }
            }
            Err(e) => describe(&e),
        };
        attempt += 1;
        if attempt >= MAX_ATTEMPTS {
            return Err(fail(retry_reason));
        }
        tokio::select! {
            _ = tokio::time::sleep(backoff(attempt)) => {}
            _ = cancel.cancelled() => return Err(Step::Done(Outcome::Cancelled)),
        }
    }
}

/// An unfinished range nobody is on, else the second half of the largest range in progress.
fn next_segment(transfer: &Transfer, resumable: bool, min_split: u64) -> Option<Arc<Seg>> {
    let mut segs = lock(&transfer.segs);
    if let Some(seg) = segs.iter().find(|s| !s.is_done() && !s.busy.load(Ordering::Relaxed)) {
        seg.busy.store(true, Ordering::Relaxed);
        return Some(seg.clone());
    }
    if !resumable {
        return None;
    }
    let remaining: Vec<ByteRange> = segs
        .iter()
        .map(|s| {
            if s.busy.load(Ordering::Relaxed) && !s.end_unknown() {
                s.unclaimed()
            } else {
                ByteRange::new(0, 0)
            }
        })
        .collect();
    let (victim, new) = segments::split_largest(&remaining, min_split)?;
    if !segs[victim].shrink_end(new.start) {
        return None;
    }
    let seg = Seg::new(new.start, new.start, new.end);
    seg.busy.store(true, Ordering::Relaxed);
    segs.push(seg.clone());
    Some(seg)
}

struct WorkerEnv {
    client: Client,
    url: String,
    request: RequestContext,
    resumable: bool,
    file: Arc<File>,
    global_limit: Arc<RateLimiter>,
    cancel: CancellationToken,
    transfer: Arc<Transfer>,
}

#[derive(Debug)]
enum WorkerError {
    Cancelled,
    /// The server sent the whole file instead of the range: it changed since we started.
    Changed,
    Expired(String),
    Fatal(String),
}

enum Attempt {
    Retry(String),
    Stop(WorkerError),
}

/// Fetches one range, retrying with backoff. `first` is a response already open for it.
async fn worker(seg: &Seg, mut first: Option<Response>, env: &WorkerEnv) -> Result<(), WorkerError> {
    let mut attempt = 0;
    loop {
        if seg.is_done() {
            return Ok(());
        }
        let before = seg.row().cursor;
        let result = match first.take() {
            Some(resp) => stream_into(seg, resp, env, true).await,
            None => fetch_range(seg, env).await,
        };
        match result {
            Ok(()) => return Ok(()),
            Err(Attempt::Stop(e)) => return Err(e),
            Err(Attempt::Retry(reason)) => {
                if seg.row().cursor > before {
                    attempt = 0; // data was flowing: a fresh set of tries
                }
                attempt += 1;
                if attempt >= MAX_ATTEMPTS {
                    return Err(WorkerError::Fatal(reason));
                }
                tokio::select! {
                    _ = tokio::time::sleep(backoff(attempt)) => {}
                    _ = env.cancel.cancelled() => return Err(WorkerError::Cancelled),
                }
            }
        }
    }
}

async fn fetch_range(seg: &Seg, env: &WorkerEnv) -> Result<(), Attempt> {
    let row = seg.row();
    let to = (row.end != UNKNOWN_END).then(|| row.end - 1);
    let if_range = if env.resumable { env.request.if_range() } else { None };
    let sent = tokio::select! {
        r = send(&env.client, &env.url, &env.request, row.cursor, to, if_range) => r,
        _ = env.cancel.cancelled() => return Err(Attempt::Stop(WorkerError::Cancelled)),
    };
    let resp = sent.map_err(|e| Attempt::Retry(describe(&e)))?;
    let status = resp.status();
    match status {
        StatusCode::PARTIAL_CONTENT => stream_into(seg, resp, env, false).await,
        StatusCode::OK if !env.resumable && row.cursor == 0 => stream_into(seg, resp, env, true).await,
        StatusCode::OK | StatusCode::RANGE_NOT_SATISFIABLE => Err(Attempt::Stop(WorkerError::Changed)),
        StatusCode::FORBIDDEN | StatusCode::GONE | StatusCode::UNAUTHORIZED => Err(Attempt::Stop(
            WorkerError::Expired(format!("Link expired (server answered {})", status.as_u16())),
        )),
        StatusCode::TOO_MANY_REQUESTS | StatusCode::SERVICE_UNAVAILABLE => {
            env.transfer.throttled.fetch_add(1, Ordering::Relaxed);
            Err(Attempt::Retry(format!("Server is busy (answered {})", status.as_u16())))
        }
        s if s.is_server_error() || s == StatusCode::REQUEST_TIMEOUT => {
            Err(Attempt::Retry(format!("Server answered {}", s.as_u16())))
        }
        s => Err(Attempt::Stop(WorkerError::Fatal(format!("Server answered {}", s.as_u16())))),
    }
}

/// Writes the body into the range until the range is done or the body ends.
/// `from_zero` bodies start at byte 0 of the file rather than at the range's cursor.
async fn stream_into(seg: &Seg, resp: Response, env: &WorkerEnv, from_zero: bool) -> Result<(), Attempt> {
    // A body that starts at byte 0 for a range further in (the first reply) is not ours to use.
    if from_zero && seg.row().cursor != 0 {
        return Err(Attempt::Retry("restarting range".into()));
    }
    let mut stream = resp.bytes_stream();
    loop {
        let next = tokio::select! {
            c = stream.next() => c,
            _ = env.cancel.cancelled() => return Err(Attempt::Stop(WorkerError::Cancelled)),
        };
        let chunk = match next {
            None => {
                if seg.end_unknown() {
                    seg.close_at_cursor();
                    return Ok(());
                }
                return if seg.is_done() {
                    Ok(())
                } else {
                    Err(Attempt::Retry("Connection closed early".into()))
                };
            }
            Some(Err(e)) => return Err(Attempt::Retry(describe(&e))),
            Some(Ok(chunk)) => chunk,
        };
        let mut data = &chunk[..];
        while !data.is_empty() {
            let (offset, len) = seg.reserve(data.len() as u64);
            if len == 0 {
                return Ok(()); // the range was split or is done
            }
            tokio::select! {
                _ = async { env.global_limit.acquire(len).await; env.transfer.job_limit.acquire(len).await } => {}
                _ = env.cancel.cancelled() => {
                    seg.reserve(0);
                    return Err(Attempt::Stop(WorkerError::Cancelled));
                }
            }
            if let Err(e) = write_all_at(&env.file, &data[..len as usize], offset) {
                seg.reserve(0);
                return Err(Attempt::Stop(WorkerError::Fatal(format!("Cannot write the file: {e}"))));
            }
            seg.commit();
            data = &data[len as usize..];
        }
    }
}

async fn send(
    client: &Client,
    url: &str,
    ctx: &RequestContext,
    from: u64,
    to: Option<u64>,
    if_range: Option<&str>,
) -> reqwest::Result<Response> {
    let range = match to {
        Some(to) => format!("bytes={from}-{to}"),
        None => format!("bytes={from}-"),
    };
    let mut req = client.get(url).header(header::RANGE, range).header(header::ACCEPT_ENCODING, "identity");
    if let Some(ua) = ctx.user_agent.as_deref().filter(|s| !s.is_empty()) {
        req = req.header(header::USER_AGENT, ua);
    }
    if let Some(r) = ctx.referrer.as_deref().filter(|s| !s.is_empty()) {
        req = req.header(header::REFERER, r);
    }
    if let Some(c) = ctx.cookies.as_deref().filter(|s| !s.is_empty()) {
        req = req.header(header::COOKIE, c);
    }
    if let Some(v) = if_range {
        req = req.header(header::IF_RANGE, v);
    }
    req.send().await
}

fn backoff(attempt: u32) -> Duration {
    Duration::from_secs(1u64 << attempt.min(5))
}

fn describe(e: &reqwest::Error) -> String {
    if e.is_timeout() {
        "The server stopped responding".into()
    } else if e.is_connect() {
        "Cannot connect to the server".into()
    } else {
        format!("Network error: {e}")
    }
}

/// The `.part` file for a job that has settled its name.
pub fn part_file(save_dir: &Path, file_name: &str) -> PathBuf {
    naming::part_path(&save_dir.join(file_name))
}
