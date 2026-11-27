//! Video pages and streams, through the bundled yt-dlp and ffmpeg.
//!
//! The quality menu comes from `yt-dlp -J`. Downloads of video pages, HLS playlists and DASH
//! manifests run yt-dlp with concurrent fragments and stream copy (no re-encoding); progress is
//! read from a fixed progress template on its output. yt-dlp and ffmpeg are always started with
//! argument lists, never through a shell, and the URL always follows `--`.

use std::path::{Path, PathBuf};
use std::process::Stdio;
use std::sync::Arc;
use std::sync::atomic::Ordering;
use std::time::Duration;

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
const MENU_HEIGHTS: usize = 6;

pub fn tool_path(tools_dir: &Path, name: &str) -> PathBuf {
    if cfg!(windows) { tools_dir.join(format!("{name}.exe")) } else { tools_dir.join(name) }
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

fn request_args(ctx: &RequestContext) -> Vec<String> {
    let mut args = Vec::new();
    if let Some(ua) = ctx.user_agent.as_deref().filter(|s| !s.is_empty()) {
        args.extend(["--user-agent".into(), ua.into()]);
    }
    if let Some(r) = ctx.referrer.as_deref().filter(|s| !s.is_empty()) {
        args.extend(["--referer".into(), r.into()]);
    }
    if let Some(c) = ctx.cookies.as_deref().filter(|s| !s.is_empty()) {
        args.extend(["--add-headers".into(), format!("Cookie:{c}")]);
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
    let ytdlp = tool_path(tools_dir, "yt-dlp");
    if !ytdlp.exists() {
        return Err(format!("yt-dlp is missing from {}", tools_dir.display()));
    }
    let mut cmd = command(&ytdlp);
    cmd.args(["-J", "--no-playlist", "--no-warnings", "--encoding", "utf-8"])
        .args(request_args(ctx))
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
        let h = f["height"].as_u64().unwrap_or(0);
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
}

/// The arguments for a download; the URL goes last, after `--`.
pub fn download_args(job: &VideoJob) -> Vec<String> {
    let mut args: Vec<String> = [
        "--newline",
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
        "-o",
        "%(title).150B [%(id)s].%(ext)s",
    ]
    .map(String::from)
    .into();
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
    args.extend(request_args(&job.request));
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
    cmd.args(download_args(&job)).stdout(Stdio::piped()).stderr(Stdio::piped());
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
        };
        let args = download_args(&job);
        assert_eq!(&args[args.len() - 2..], ["--", "--exec=calc"]);
        assert!(args.windows(2).any(|w| w == ["--audio-format", "mp3"]));
        assert!(args.windows(2).any(|w| w == ["--add-headers", "Cookie:a=b"]));
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
