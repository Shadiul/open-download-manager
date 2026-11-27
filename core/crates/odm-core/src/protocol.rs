//! Messages between the browser extension and the app, as described in `native-host/PROTOCOL.md`.
//!
//! The same shapes are used between the native host and the app, and between the app's UI and
//! the core, so there is one vocabulary everywhere.

use serde::{Deserialize, Serialize};

/// The protocol version every message carries in its `v` field.
pub const PROTOCOL_VERSION: u32 = 1;

/// One incoming message: the version plus the request itself.
#[derive(Debug, Clone, PartialEq, Deserialize)]
pub struct Envelope {
    pub v: u32,
    #[serde(flatten)]
    pub request: Request,
}

#[derive(Debug, Clone, PartialEq, Deserialize)]
#[serde(tag = "type", rename_all = "camelCase")]
pub enum Request {
    Ping,
    Download(DownloadRequest),
    Probe(ProbeRequest),
}

#[derive(Debug, Clone, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DownloadRequest {
    pub url: String,
    /// May be empty; the app then takes the name from the response or the URL.
    #[serde(default)]
    pub filename: String,
    #[serde(default)]
    pub referrer: Option<String>,
    #[serde(default)]
    pub mime: Option<String>,
    /// `-1` when unknown; use [`DownloadRequest::known_size`].
    #[serde(default = "unknown_size")]
    pub file_size: i64,
    #[serde(default)]
    pub cookies: Option<String>,
    #[serde(default)]
    pub user_agent: Option<String>,
    #[serde(default)]
    pub source: Option<Source>,
    #[serde(default)]
    pub media_kind: Option<MediaKind>,
    #[serde(default)]
    pub page_title: Option<String>,
    #[serde(default)]
    pub tab_id: Option<i64>,
    /// Only for `media_kind == Page`: an id from a probe reply, or `None` for the best quality.
    #[serde(default)]
    pub format_id: Option<String>,
}

fn unknown_size() -> i64 {
    -1
}

impl DownloadRequest {
    pub fn known_size(&self) -> Option<u64> {
        u64::try_from(self.file_size).ok()
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize, Serialize)]
#[serde(rename_all = "kebab-case")]
pub enum Source {
    BrowserDownload,
    ContextMenu,
    Media,
    VideoButton,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum MediaKind {
    /// An `.m3u8` playlist.
    Hls,
    /// An `.mpd` manifest.
    Dash,
    Video,
    Audio,
    /// A video page such as a YouTube watch link; the app extracts it with yt-dlp.
    Page,
}

#[derive(Debug, Clone, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProbeRequest {
    pub url: String,
    #[serde(default)]
    pub cookies: Option<String>,
    #[serde(default)]
    pub user_agent: Option<String>,
}

/// The reply to any request. Fields that do not apply are left out of the JSON.
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct Reply {
    pub ok: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub version: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub title: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub formats: Option<Vec<Format>>,
}

impl Reply {
    pub fn accepted() -> Self {
        Self { ok: true, error: None, version: None, title: None, formats: None }
    }

    pub fn error(message: impl Into<String>) -> Self {
        Self { ok: false, error: Some(message.into()), ..Self::accepted() }
    }

    pub fn pong(version: impl Into<String>) -> Self {
        Self { version: Some(version.into()), ..Self::accepted() }
    }

    pub fn formats(title: impl Into<String>, formats: Vec<Format>) -> Self {
        Self { title: Some(title.into()), formats: Some(formats), ..Self::accepted() }
    }
}

/// One entry in the quality menu, best first.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct Format {
    pub id: String,
    pub label: String,
    pub ext: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub size: Option<u64>,
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn parse(value: serde_json::Value) -> Envelope {
        serde_json::from_value(value).expect("valid message")
    }

    #[test]
    fn parses_ping() {
        let env = parse(json!({ "v": 1, "type": "ping" }));
        assert_eq!(env.v, 1);
        assert_eq!(env.request, Request::Ping);
    }

    #[test]
    fn parses_the_documented_download_example() {
        let env = parse(json!({
            "v": 1,
            "type": "download",
            "url": "https://example.com/file.zip",
            "filename": "file.zip",
            "referrer": "https://example.com/downloads",
            "mime": "application/zip",
            "fileSize": 52428800,
            "cookies": "session=abc; theme=dark",
            "userAgent": "Mozilla/5.0 ...",
            "source": "browser-download",
            "mediaKind": null,
            "pageTitle": "Downloads - Example",
            "tabId": 12
        }));
        let Request::Download(d) = env.request else { panic!("expected download") };
        assert_eq!(d.url, "https://example.com/file.zip");
        assert_eq!(d.known_size(), Some(52_428_800));
        assert_eq!(d.source, Some(Source::BrowserDownload));
        assert_eq!(d.media_kind, None);
        assert_eq!(d.tab_id, Some(12));
    }

    #[test]
    fn unknown_size_and_page_downloads() {
        let env = parse(json!({
            "v": 1, "type": "download", "url": "https://www.youtube.com/watch?v=ID",
            "fileSize": -1, "source": "video-button", "mediaKind": "page", "formatId": "137+140"
        }));
        let Request::Download(d) = env.request else { panic!("expected download") };
        assert_eq!(d.known_size(), None);
        assert_eq!(d.media_kind, Some(MediaKind::Page));
        assert_eq!(d.format_id.as_deref(), Some("137+140"));
        assert_eq!(d.filename, "");
    }

    #[test]
    fn parses_probe() {
        let env = parse(json!({ "v": 1, "type": "probe", "url": "https://www.youtube.com/watch?v=ID" }));
        let Request::Probe(p) = env.request else { panic!("expected probe") };
        assert_eq!(p.url, "https://www.youtube.com/watch?v=ID");
    }

    #[test]
    fn rejects_unknown_types_and_missing_urls() {
        assert!(serde_json::from_value::<Envelope>(json!({ "v": 1, "type": "format-disk" })).is_err());
        assert!(serde_json::from_value::<Envelope>(json!({ "v": 1, "type": "download" })).is_err());
    }

    #[test]
    fn replies_leave_out_unused_fields() {
        assert_eq!(serde_json::to_value(Reply::accepted()).unwrap(), json!({ "ok": true }));
        assert_eq!(
            serde_json::to_value(Reply::error("busy")).unwrap(),
            json!({ "ok": false, "error": "busy" })
        );
        let formats = Reply::formats(
            "Video title",
            vec![Format { id: "140".into(), label: "Audio only".into(), ext: "m4a".into(), size: None }],
        );
        assert_eq!(
            serde_json::to_value(formats).unwrap(),
            json!({ "ok": true, "title": "Video title",
                    "formats": [{ "id": "140", "label": "Audio only", "ext": "m4a" }] })
        );
    }
}
