//! Video pages and streams, through the bundled yt-dlp and ffmpeg.
//!
//! The quality menu comes from `yt-dlp -J`. Downloads of video pages, HLS playlists and DASH
//! manifests run yt-dlp with concurrent fragments and stream copy (no re-encoding); progress is
//! read from a fixed progress template on its output. yt-dlp and ffmpeg are always started with
//! argument lists, never through a shell, and the URL always follows `--`.

use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::process::Stdio;
use std::sync::atomic::Ordering;
use std::sync::{Arc, LazyLock, Mutex};
use std::time::{Duration, Instant};

use serde_json::Value;
use tokio::io::{AsyncBufReadExt, BufReader};
use tokio::process::Command;
use tokio_util::sync::CancellationToken;

use crate::engine::{Outcome, Transfer};
use crate::job::RequestContext;
use crate::protocol::{Format, MediaKind};

/// The quality-menu id that downloads the best audio and converts it to MP3.
pub const MP3_FORMAT: &str = "audio-mp3";
const PROBE_TIMEOUT: Duration = Duration::from_secs(30);

/// How long a quality menu's page details stay good for starting the download from them. Stream
/// addresses inside last hours; this stays well short of that.
const INFO_TTL: Duration = Duration::from_secs(20 * 60);

/// The page details (yt-dlp's info JSON) each quality menu fetched, by page address. Picking a
/// quality then starts the download from them with `--load-info-json`, skipping the seconds yt-dlp
/// spends reading the page again.
type InfoCache = HashMap<String, (Instant, Identity, PathBuf)>;
static INFO: LazyLock<Mutex<InfoCache>> = LazyLock::new(Default::default);

fn info_cache() -> std::sync::MutexGuard<'static, InfoCache> {
    INFO.lock().unwrap_or_else(|e| e.into_inner())
}

fn remember_info(url: &str, identity: Identity, json: &[u8]) {
    use std::hash::{Hash, Hasher};
    let dir = std::env::temp_dir().join("OpenDownloadManager-video-info");
    if std::fs::create_dir_all(&dir).is_err() {
        return;
    }
    let mut h = std::collections::hash_map::DefaultHasher::new();
    url.hash(&mut h);
    let path = dir.join(format!("{:016x}.json", h.finish()));
    if std::fs::write(&path, json).is_ok() {
        let mut cache = info_cache();
        cache.retain(|_, (at, _, _)| at.elapsed() < INFO_TTL);
        cache.insert(url.to_string(), (Instant::now(), identity, path));
    }
}

/// Fresh page details for this address, fetched the same way (with or without cookies).
fn cached_info(url: &str, identity: Identity) -> Option<PathBuf> {
    let cache = info_cache();
    let (at, id, path) = cache.get(url)?;
    (at.elapsed() < INFO_TTL && *id == identity && path.exists()).then(|| path.clone())
}

fn forget_info(url: &str) {
    if let Some((_, _, path)) = info_cache().remove(url) {
        let _ = std::fs::remove_file(path);
    }
}
const MENU_HEIGHTS: usize = 6;

pub fn tool_path(tools_dir: &Path, name: &str) -> PathBuf {
    let file = if cfg!(windows) { format!("{name}.exe") } else { name.to_string() };
    // A tool shipped as a folder build (yt-dlp: it starts faster than the one-file build).
    let in_folder = tools_dir.join(name).join(&file);
    if in_folder.exists() { in_folder } else { tools_dir.join(file) }
}

fn command(program: &Path) -> Command {
    let mut cmd = Command::new(program);
    cmd.stdin(Stdio::null()).kill_on_drop(true);
    #[cfg(windows)]
    {
        const CREATE_NO_WINDOW: u32 = 0x0800_0000;
        cmd.creation_flags(CREATE_NO_WINDOW);
    }
    cmd
}

/// Which of the browser's request details yt-dlp is given.
///
/// Video pages go without the browser's cookies and user agent first: yt-dlp picks its own
/// clients per site, and on YouTube a signed-in cookie switches it to clients that return no
/// formats ("Requested format is not available"). Only when the site asks for a login does the
/// page get a second try with the cookies. Stream addresses (HLS, DASH) always get them, since
/// their servers may check them.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Identity {
    Anonymous,
    Browser,
}

impl Identity {
    fn first_for(kind: Option<MediaKind>) -> Self {
        match kind {
            Some(MediaKind::Hls | MediaKind::Dash) => Self::Browser,
            _ => Self::Anonymous,
        }
    }
}

/// Whether yt-dlp's error says the page needs the user's login.
pub fn needs_login(error: &str) -> bool {
    let e = error.to_lowercase();
    [
        "sign in",
        "log in",
        "login",
        "private video",
        "members",
        "age-restricted",
        "confirm your age",
        "not a bot",
        "cookies",
        "authentication",
    ]
    .iter()
    .any(|k| e.contains(k))
}

/// Points yt-dlp at the bundled JavaScript runtime (deno), which YouTube extraction needs for
/// all formats and for signed-in requests. Without it yt-dlp falls back to fewer formats.
fn runtime_args(tools_dir: &Path) -> Vec<String> {
    let deno = tool_path(tools_dir, "deno");
    if deno.exists() { vec!["--js-runtimes".into(), format!("deno:{}", deno.display())] } else { Vec::new() }
}

fn has_cookies(ctx: &RequestContext) -> bool {
    ctx.cookies.as_deref().is_some_and(|c| !c.is_empty())
}

fn request_args(ctx: &RequestContext, identity: Identity) -> Vec<String> {
    let mut args = Vec::new();
    if let Some(r) = ctx.referrer.as_deref().filter(|s| !s.is_empty()) {
        args.extend(["--referer".into(), r.into()]);
    }
    if identity == Identity::Browser {
        if let Some(ua) = ctx.user_agent.as_deref().filter(|s| !s.is_empty()) {
            args.extend(["--user-agent".into(), ua.into()]);
        }
        if let Some(c) = ctx.cookies.as_deref().filter(|s| !s.is_empty()) {
            args.extend(["--add-headers".into(), format!("Cookie:{c}")]);
        }
    }
    args
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProbeResult {
    pub title: String,
    pub formats: Vec<Format>,
}

/// Lists the qualities for a video page, best first.
pub async fn probe(tools_dir: &Path, url: &str, ctx: &RequestContext) -> Result<ProbeResult, String> {
    match probe_as(tools_dir, url, ctx, Identity::Anonymous).await {
        Err(e) if needs_login(&e) && has_cookies(ctx) => {
            probe_as(tools_dir, url, ctx, Identity::Browser).await
        }
        other => other,
    }
}

async fn probe_as(
    tools_dir: &Path,
    url: &str,
    ctx: &RequestContext,
    identity: Identity,
) -> Result<ProbeResult, String> {
    let ytdlp = tool_path(tools_dir, "yt-dlp");
    if !ytdlp.exists() {
        return Err(format!("yt-dlp is missing from {}", tools_dir.display()));
    }
    let mut cmd = command(&ytdlp);
    cmd.args(["-J", "--no-playlist", "--no-warnings", "--encoding", "utf-8"])
        .args(runtime_args(tools_dir))
        .args(request_args(ctx, identity))
        .arg("--")
        .arg(url)
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    let child = cmd.spawn().map_err(|e| format!("Cannot start yt-dlp: {e}"))?;
    let output = tokio::time::timeout(PROBE_TIMEOUT, child.wait_with_output())
        .await
        .map_err(|_| "yt-dlp took too long to list the formats".to_string())?
        .map_err(|e| format!("yt-dlp failed: {e}"))?;
    if !output.status.success() {
        return Err(error_line(&String::from_utf8_lossy(&output.stderr)));
    }
    let info: Value =
        serde_json::from_slice(&output.stdout).map_err(|e| format!("yt-dlp gave bad JSON: {e}"))?;
    remember_info(url, identity, &output.stdout);
    Ok(ProbeResult {
        title: info["title"].as_str().unwrap_or_default().to_string(),
        formats: build_menu(&info),
    })
}

/// The last `ERROR:` line yt-dlp printed, without its prefix.
fn error_line(stderr: &str) -> String {
    stderr
        .lines()
        .rev()
        .find(|l| l.starts_with("ERROR:"))
        .or_else(|| stderr.lines().rev().find(|l| !l.trim().is_empty()))
        .map(|l| l.trim_start_matches("ERROR:").trim().to_string())
        .unwrap_or_else(|| "yt-dlp failed".into())
}

fn size_of(f: &Value) -> Option<u64> {
    f["filesize"].as_u64().or_else(|| f["filesize_approx"].as_f64().map(|v| v as u64))
}

/// Plain HTTP(S) formats download as one file. HLS fragments (YouTube's ids in the 600s, among
/// others) are slower and often refused partway through, so they rank lower.
fn direct(f: &Value) -> bool {
    f["protocol"].as_str().is_none_or(|p| p == "https" || p == "http")
}

/// The number in "1080p", named the way video sites name it: the frame a 16:9 video of that
/// quality would fill. 1920x1080 and 1080x1920 are 1080p, 3840x1920 is 2160p, 1440x1080 is 1080p.
fn quality(f: &Value) -> u64 {
    let h = f["height"].as_u64().unwrap_or(0);
    match f["width"].as_u64() {
        Some(w) if w > 0 => w.min(h).max(w.max(h) * 9 / 16),
        _ => h,
    }
}

fn has(f: &Value, codec: &str) -> bool {
    f[codec].as_str().is_some_and(|c| c != "none")
}

/// Builds the quality menu from yt-dlp's JSON: one entry per height (best stream for it, paired
/// with the best audio), then audio only and MP3.
pub fn build_menu(info: &Value) -> Vec<Format> {
    let Some(formats) = info["formats"].as_array() else { return Vec::new() };
    let tbr = |f: &Value| f["tbr"].as_f64().unwrap_or(0.0);

    let best_audio = formats.iter().filter(|f| has(f, "acodec") && !has(f, "vcodec")).max_by(|a, b| {
        let key = |f: &Value| (direct(f), f["ext"] == "m4a", f["abr"].as_f64().unwrap_or_else(|| tbr(f)));
        key(a).partial_cmp(&key(b)).unwrap_or(std::cmp::Ordering::Equal)
    });

    let mut by_height: Vec<(u64, &Value)> = Vec::new();
    for f in formats.iter().filter(|f| has(f, "vcodec") && f["height"].as_u64().is_some_and(|h| h > 0)) {
        let h = quality(f);
        let better = |old: &Value| {
            let key = |f: &Value| (direct(f), f["fps"].as_f64().unwrap_or(0.0), f["ext"] == "mp4", tbr(f));
            key(f).partial_cmp(&key(old)) == Some(std::cmp::Ordering::Greater)
        };
        match by_height.iter_mut().find(|(height, _)| *height == h) {
            Some(slot) if better(slot.1) => slot.1 = f,
            Some(_) => {}
            None => by_height.push((h, f)),
        }
    }
    by_height.sort_by_key(|(h, _)| std::cmp::Reverse(*h));

    let mut menu: Vec<Format> = by_height
        .into_iter()
        .take(MENU_HEIGHTS)
        .map(|(h, v)| {
            let fps = v["fps"].as_f64().unwrap_or(0.0);
            let label = if fps >= 50.0 { format!("{h}p{}", fps.round() as u64) } else { format!("{h}p") };
            let vid = v["format_id"].as_str().unwrap_or_default();
            match best_audio.filter(|_| !has(v, "acodec")) {
                Some(a) => Format {
                    id: format!("{vid}+{}", a["format_id"].as_str().unwrap_or_default()),
                    label,
                    ext: "mp4".into(),
                    size: size_of(v).zip(size_of(a)).map(|(x, y)| x + y),
                },
                None => Format {
                    id: vid.into(),
                    label,
                    ext: v["ext"].as_str().unwrap_or("mp4").into(),
                    size: size_of(v),
                },
            }
        })
        .collect();

    if let Some(a) = best_audio {
        let ext = a["ext"].as_str().unwrap_or("m4a").to_string();
        menu.push(Format {
            id: a["format_id"].as_str().unwrap_or_default().into(),
            label: format!("Audio only ({})", ext.to_uppercase()),
            ext,
            size: size_of(a),
        });
        menu.push(Format {
            id: MP3_FORMAT.into(),
            label: "Audio only (MP3)".into(),
            ext: "mp3".into(),
            size: None,
        });
    }
    menu
}

/// What a yt-dlp download needs.
#[derive(Debug, Clone)]
pub struct VideoJob {
    pub url: String,
    pub request: RequestContext,
    pub media_kind: Option<MediaKind>,
    pub format_id: Option<String>,
    pub save_dir: PathBuf,
    pub tools_dir: PathBuf,
    pub speed_limit: u64,
    /// A name the user chose, without extension; `None` uses the video's title and id.
    pub file_name: Option<String>,
}

/// The arguments for a download; the URL goes last, after `--`.
pub fn download_args(job: &VideoJob, identity: Identity) -> Vec<String> {
    let mut args: Vec<String> = [
        "--newline",
        // --print makes yt-dlp quiet, which also hides progress; this brings it back.
        "--progress",
        "--no-playlist",
        "--no-colors",
        "--encoding",
        "utf-8",
        "--windows-filenames",
        "--no-mtime",
        "-N",
        "4",
        "--progress-template",
        "download:ODMP %(progress.downloaded_bytes)s %(progress.total_bytes)s %(progress.total_bytes_estimate)s",
        "--print",
        "after_move:ODMF %(filepath)s",
        "--no-simulate",
    ]
    .map(String::from)
    .into();
    let template = match job.file_name.as_deref().map(str::trim).filter(|n| !n.is_empty()) {
        // % starts a template field in yt-dlp; a literal one is written %%.
        Some(name) => format!("{}.%(ext)s", crate::naming::sanitize(name).replace('%', "%%")),
        None => "%(title).150B [%(id)s].%(ext)s".into(),
    };
    args.extend(["-o".into(), template]);
    args.extend(["-P".into(), job.save_dir.to_string_lossy().into_owned()]);
    args.extend(["--ffmpeg-location".into(), job.tools_dir.to_string_lossy().into_owned()]);
    match job.format_id.as_deref() {
        Some(MP3_FORMAT) => {
            args.extend(["-f", "bestaudio/best", "-x", "--audio-format", "mp3"].map(String::from))
        }
        Some(id) if !id.is_empty() => {
            args.extend(["-f".into(), id.into()]);
            if id.contains('+') {
                args.extend(["--merge-output-format".into(), "mp4".into()]);
            }
        }
        _ => args.extend(["-f", "bv*+ba/b", "--merge-output-format", "mp4"].map(String::from)),
    }
    if job.speed_limit > 0 {
        args.extend(["--limit-rate".into(), job.speed_limit.to_string()]);
    }
    args.extend(runtime_args(&job.tools_dir));
    args.extend(request_args(&job.request, identity));
    args.push("--".into());
    args.push(job.url.clone());
    args
}

/// Progress from one `ODMP downloaded total estimate` line.
fn parse_progress(line: &str) -> Option<(u64, Option<u64>)> {
    let mut parts = line.strip_prefix("ODMP ")?.split_whitespace();
    let num = |s: Option<&str>| s.and_then(|s| s.parse::<f64>().ok()).map(|v| v as u64);
    let downloaded = num(parts.next())?;
    let total = num(parts.next());
    let estimate = num(parts.next());
    Some((downloaded, total.or(estimate)))
}

/// Runs yt-dlp until the file is saved, it fails, or `cancel` fires.
pub async fn run(job: VideoJob, transfer: Arc<Transfer>, cancel: CancellationToken) -> Outcome {
    let first = Identity::first_for(job.media_kind);
    // Straight from the quality menu's page details when they are fresh; if those are stale after
    // all (an address that expired), read the page again.
    let outcome = match cached_info(&job.url, first) {
        Some(info) => match run_as(&job, &transfer, &cancel, first, Some(&info)).await {
            Outcome::Failed { .. } if !cancel.is_cancelled() => {
                forget_info(&job.url);
                transfer.external_downloaded.store(0, Ordering::Relaxed);
                transfer.external_total.store(0, Ordering::Relaxed);
                run_as(&job, &transfer, &cancel, first, None).await
            }
            other => other,
        },
        None => run_as(&job, &transfer, &cancel, first, None).await,
    };
    match outcome {
        Outcome::Failed { error, .. }
            if first == Identity::Anonymous && needs_login(&error) && has_cookies(&job.request) =>
        {
            transfer.external_downloaded.store(0, Ordering::Relaxed);
            transfer.external_total.store(0, Ordering::Relaxed);
            run_as(&job, &transfer, &cancel, Identity::Browser, None).await
        }
        other => other,
    }
}

async fn run_as(
    job: &VideoJob,
    transfer: &Transfer,
    cancel: &CancellationToken,
    identity: Identity,
    info: Option<&Path>,
) -> Outcome {
    let ytdlp = tool_path(&job.tools_dir, "yt-dlp");
    if !ytdlp.exists() {
        return Outcome::Failed {
            error: format!("yt-dlp is missing from {}", job.tools_dir.display()),
            link_expired: false,
        };
    }
    if let Err(e) = std::fs::create_dir_all(&job.save_dir) {
        return Outcome::Failed { error: format!("Cannot create folder: {e}"), link_expired: false };
    }
    let mut cmd = command(&ytdlp);
    let mut args = download_args(job, identity);
    if let Some(info) = info {
        // In place of "-- <page>": the details already fetched.
        args.truncate(args.len() - 2);
        args.extend(["--load-info-json".into(), info.to_string_lossy().into_owned()]);
    }
    cmd.args(args).stdout(Stdio::piped()).stderr(Stdio::piped());
    let mut child = match cmd.spawn() {
        Ok(c) => c,
        Err(e) => return Outcome::Failed { error: format!("Cannot start yt-dlp: {e}"), link_expired: false },
    };
    let stdout = child.stdout.take().expect("stdout is piped");
    let stderr = child.stderr.take().expect("stderr is piped");
    let stderr_task = tokio::spawn(async move {
        let mut lines = BufReader::new(stderr).lines();
        let mut tail: Vec<String> = Vec::new();
        while let Ok(Some(line)) = lines.next_line().await {
            tail.push(line);
            if tail.len() > 30 {
                tail.remove(0);
            }
        }
        tail.join("\n")
    });

    transfer.active.store(1, Ordering::Relaxed);
    let mut lines = BufReader::new(stdout).lines();
    let mut final_path: Option<PathBuf> = None;
    // yt-dlp fetches video and audio one after the other; add the finished streams together.
    let (mut base, mut last_done, mut last_total) = (0u64, 0u64, 0u64);
    loop {
        let line = tokio::select! {
            l = lines.next_line() => l,
            _ = cancel.cancelled() => {
                let _ = child.kill().await;
                transfer.active.store(0, Ordering::Relaxed);
                return Outcome::Cancelled;
            }
        };
        match line {
            Ok(Some(line)) => {
                if let Some((done, total)) = parse_progress(&line) {
                    if done < last_done {
                        base += last_total.max(last_done);
                    }
                    last_done = done;
                    last_total = total.unwrap_or(0);
                    transfer.external_downloaded.store(base + done, Ordering::Relaxed);
                    if let Some(t) = total {
                        transfer.external_total.store(base + t, Ordering::Relaxed);
                    }
                } else if let Some(path) = line.strip_prefix("ODMF ") {
                    final_path = Some(PathBuf::from(path.trim()));
                }
            }
            Ok(None) | Err(_) => break,
        }
    }
    let status = child.wait().await;
    transfer.active.store(0, Ordering::Relaxed);
    let stderr = stderr_task.await.unwrap_or_default();
    match (status, final_path) {
        (Ok(s), Some(path)) if s.success() => {
            if let Ok(meta) = std::fs::metadata(&path) {
                transfer.external_downloaded.store(meta.len(), Ordering::Relaxed);
                transfer.external_total.store(meta.len(), Ordering::Relaxed);
            }
            Outcome::Completed { path }
        }
        _ => {
            let error = error_line(&stderr);
            let link_expired = error.contains("HTTP Error 403") || error.contains("HTTP Error 410");
            Outcome::Failed { error, link_expired }
        }
    }
}

/// Updates the bundled yt-dlp in place (`yt-dlp -U`). Errors are returned, not fatal.
pub async fn self_update(tools_dir: &Path) -> Result<String, String> {
    let ytdlp = tool_path(tools_dir, "yt-dlp");
    if !ytdlp.exists() {
        return Err("yt-dlp is not installed".into());
    }
    let out = command(&ytdlp)
        .args(["-U", "--no-colors"])
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .output()
        .await
        .map_err(|e| e.to_string())?;
    let text = String::from_utf8_lossy(&out.stdout).trim().to_string();
    if out.status.success() { Ok(text) } else { Err(error_line(&String::from_utf8_lossy(&out.stderr))) }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn menu_pairs_video_with_the_best_audio() {
        let info = json!({ "title": "T", "formats": [
            { "format_id": "140", "ext": "m4a", "vcodec": "none", "acodec": "mp4a", "abr": 128.0, "filesize": 4 },
            { "format_id": "251", "ext": "webm", "vcodec": "none", "acodec": "opus", "abr": 160.0, "filesize": 5 },
            { "format_id": "137", "ext": "mp4", "vcodec": "avc1", "acodec": "none", "height": 1080, "fps": 30, "tbr": 4000.0, "filesize": 100 },
            { "format_id": "248", "ext": "webm", "vcodec": "vp9", "acodec": "none", "height": 1080, "fps": 30, "tbr": 3000.0 },
            { "format_id": "18", "ext": "mp4", "vcodec": "avc1", "acodec": "mp4a", "height": 360, "fps": 30, "tbr": 500.0, "filesize_approx": 20.5 },
            { "format_id": "sb0", "ext": "mhtml", "vcodec": "none", "acodec": "none" }
        ]});
        let menu = build_menu(&info);
        let ids: Vec<&str> = menu.iter().map(|f| f.id.as_str()).collect();
        assert_eq!(ids, ["137+140", "18", "140", MP3_FORMAT]);
        assert_eq!(menu[0].label, "1080p");
        assert_eq!(menu[0].size, Some(104));
        assert_eq!(menu[1].size, Some(20), "muxed format keeps its own size");
        assert_eq!(menu[2].label, "Audio only (M4A)");
    }

    #[test]
    fn direct_formats_beat_hls_at_the_same_height() {
        let info = json!({ "formats": [
            { "format_id": "230", "ext": "mp4", "protocol": "m3u8_native", "vcodec": "avc1", "acodec": "none", "height": 360, "fps": 30, "tbr": 900.0 },
            { "format_id": "134", "ext": "mp4", "protocol": "https", "vcodec": "avc1", "acodec": "none", "height": 360, "fps": 30, "tbr": 300.0 },
            { "format_id": "140", "ext": "m4a", "protocol": "https", "vcodec": "none", "acodec": "mp4a", "abr": 128.0 },
            { "format_id": "233", "ext": "mp4", "protocol": "m3u8_native", "vcodec": "none", "acodec": "mp4a", "abr": 192.0 }
        ]});
        let ids: Vec<String> = build_menu(&info).into_iter().map(|f| f.id).collect();
        assert_eq!(ids[0], "134+140");
        assert_eq!(ids[1], "140");
    }

    #[test]
    fn qualities_are_named_like_sites_name_them() {
        let info = json!({ "formats": [
            { "format_id": "137", "ext": "mp4", "vcodec": "avc1", "acodec": "none", "width": 1080, "height": 1920, "fps": 30 },
            { "format_id": "136", "ext": "mp4", "vcodec": "avc1", "acodec": "none", "width": 720, "height": 1280, "fps": 30 },
            { "format_id": "401", "ext": "mp4", "vcodec": "av01", "acodec": "none", "width": 3840, "height": 1920, "fps": 30 },
            { "format_id": "135", "ext": "mp4", "vcodec": "avc1", "acodec": "none", "width": 640, "height": 480, "fps": 30 }
        ]});
        let labels: Vec<String> = build_menu(&info).into_iter().map(|f| f.label).collect();
        assert_eq!(labels, ["2160p", "1080p", "720p", "480p"]);
    }

    #[test]
    fn high_frame_rates_show_in_the_label() {
        let info = json!({ "formats": [
            { "format_id": "299", "ext": "mp4", "vcodec": "avc1", "acodec": "none", "height": 1080, "fps": 60 }
        ]});
        assert_eq!(build_menu(&info)[0].label, "1080p60");
    }

    #[test]
    fn progress_lines() {
        assert_eq!(parse_progress("ODMP 100 1000 NA"), Some((100, Some(1000))));
        assert_eq!(parse_progress("ODMP 100 NA 2000.5"), Some((100, Some(2000))));
        assert_eq!(parse_progress("ODMP NA NA NA"), None);
        assert_eq!(parse_progress("[download] 5%"), None);
    }

    #[test]
    fn url_always_follows_double_dash() {
        let job = VideoJob {
            url: "--exec=calc".into(),
            request: RequestContext { cookies: Some("a=b".into()), ..Default::default() },
            media_kind: Some(MediaKind::Page),
            format_id: Some(MP3_FORMAT.into()),
            save_dir: "out".into(),
            tools_dir: "tools".into(),
            speed_limit: 0,
            file_name: None,
        };
        let args = download_args(&job, Identity::Browser);
        assert_eq!(&args[args.len() - 2..], ["--", "--exec=calc"]);
        assert!(args.windows(2).any(|w| w == ["--audio-format", "mp3"]));
        assert!(args.windows(2).any(|w| w == ["--add-headers", "Cookie:a=b"]));

        assert!(!args.iter().any(|a| a == "--js-runtimes"), "no deno in this tools folder");
        let dir = tempfile::tempdir().unwrap();
        std::fs::write(tool_path(dir.path(), "deno"), b"").unwrap();
        let with_deno = download_args(
            &VideoJob { tools_dir: dir.path().to_path_buf(), ..job.clone() },
            Identity::Browser,
        );
        assert!(with_deno.windows(2).any(|w| w[0] == "--js-runtimes" && w[1].starts_with("deno:")));

        let anonymous = download_args(&job, Identity::Anonymous);
        assert!(!anonymous.iter().any(|a| a.starts_with("Cookie:") || a == "--user-agent"));
    }

    #[test]
    fn pages_go_without_cookies_first_and_streams_with_them() {
        assert_eq!(Identity::first_for(Some(MediaKind::Page)), Identity::Anonymous);
        assert_eq!(Identity::first_for(Some(MediaKind::Hls)), Identity::Browser);
        assert!(needs_login("[youtube] x: Sign in to confirm you're not a bot. Use --cookies-from-browser"));
        assert!(needs_login("[youtube] x: Private video. Sign in if you've been granted access"));
        assert!(!needs_login("[youtube] x: Requested format is not available"));
    }

    #[test]
    fn error_lines() {
        assert_eq!(
            error_line("WARNING: x\nERROR: [youtube] abc: Video unavailable\n"),
            "[youtube] abc: Video unavailable"
        );
        assert_eq!(error_line(""), "yt-dlp failed");
    }
}
