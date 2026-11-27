//! One download: what the UI sees ([`Job`]) and what is stored with it ([`JobRecord`]).

use serde::{Deserialize, Serialize};

use crate::protocol::MediaKind;

/// A download is in exactly one of these states at a time.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum JobState {
    Queued,
    Connecting,
    Downloading,
    Paused,
    WaitingToRetry,
    Completed,
    Failed,
}

impl JobState {
    /// Whether the job is using a connection slot in its queue.
    pub fn is_active(self) -> bool {
        matches!(self, Self::Connecting | Self::Downloading | Self::WaitingToRetry)
    }

    /// Whether the job has stopped for good, until the user retries it.
    pub fn is_finished(self) -> bool {
        matches!(self, Self::Completed | Self::Failed)
    }
}

/// How the file is fetched.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum JobKind {
    /// A direct file, fetched by the segmented engine.
    #[default]
    Http,
    /// A video page, HLS or DASH stream, fetched by yt-dlp and merged by ffmpeg.
    Video,
}

/// What the UI shows for one download.
#[derive(Debug, Clone, PartialEq, Eq, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Job {
    pub id: u64,
    pub kind: JobKind,
    pub url: String,
    /// The page the download came from.
    pub page_url: Option<String>,
    pub title: Option<String>,
    pub file_name: String,
    /// The folder the finished file goes to.
    pub save_dir: String,
    /// The finished file, once completed.
    pub file_path: Option<String>,
    pub total_size: Option<u64>,
    pub downloaded: u64,
    /// Bytes per second, averaged over the last few seconds.
    pub speed: u64,
    pub state: JobState,
    pub error: Option<String>,
    /// The server refused the saved address; the user can refresh it from the browser.
    pub link_expired: bool,
    /// The server accepts range requests, so pause and resume keep the bytes already fetched.
    pub resumable: bool,
    /// Connections open right now.
    pub connections: u32,
    /// Bytes per second for this download alone; 0 means no limit.
    pub speed_limit: u64,
    pub media_kind: Option<MediaKind>,
    /// For video pages: the format picked in the quality menu, or `None` for the best.
    pub format_id: Option<String>,
    pub from_browser: bool,
    /// Unix time in milliseconds.
    pub created_at: i64,
    pub finished_at: Option<i64>,
    /// Order in the queue; lower starts first.
    pub position: i64,
    /// Came from the browser and waits for the user to confirm it in the new-download window.
    pub awaiting_confirmation: bool,
    /// The user chose the file name; it wins over the server's.
    pub custom_name: bool,
    /// The user chose the folder; category folders do not apply.
    pub custom_dir: bool,
}

impl Default for Job {
    fn default() -> Self {
        Self {
            id: 0,
            kind: JobKind::Http,
            url: String::new(),
            page_url: None,
            title: None,
            file_name: String::new(),
            save_dir: String::new(),
            file_path: None,
            total_size: None,
            downloaded: 0,
            speed: 0,
            state: JobState::Queued,
            error: None,
            link_expired: false,
            resumable: false,
            connections: 0,
            speed_limit: 0,
            media_kind: None,
            format_id: None,
            from_browser: false,
            created_at: 0,
            finished_at: None,
            position: 0,
            awaiting_confirmation: false,
            custom_name: false,
            custom_dir: false,
        }
    }
}

/// What the request needs to look like the browser's own: never sent to the UI.
#[derive(Debug, Clone, PartialEq, Eq, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
pub struct RequestContext {
    pub referrer: Option<String>,
    pub cookies: Option<String>,
    pub user_agent: Option<String>,
    pub mime: Option<String>,
    /// Validators from the first response, sent back in `If-Range` on resume.
    pub etag: Option<String>,
    pub last_modified: Option<String>,
    /// The file name was settled by the first response (and the `.part` file exists).
    pub name_decided: bool,
}

impl RequestContext {
    /// The `If-Range` value: a strong ETag, else the Last-Modified date.
    pub fn if_range(&self) -> Option<&str> {
        self.etag.as_deref().filter(|e| !e.starts_with("W/")).or(self.last_modified.as_deref())
    }
}

/// A job as stored in the state database.
#[derive(Debug, Clone, PartialEq, Eq, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
pub struct JobRecord {
    pub job: Job,
    pub request: RequestContext,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn active_and_finished_do_not_overlap() {
        use JobState::*;
        for state in [Queued, Connecting, Downloading, Paused, WaitingToRetry, Completed, Failed] {
            assert!(!(state.is_active() && state.is_finished()), "{state:?}");
        }
        assert!(Downloading.is_active());
        assert!(!Paused.is_active());
        assert!(Failed.is_finished());
    }

    #[test]
    fn serializes_in_camel_case() {
        assert_eq!(serde_json::to_string(&JobState::WaitingToRetry).unwrap(), "\"waitingToRetry\"");
        let json = serde_json::to_value(Job::default()).unwrap();
        assert!(json.get("linkExpired").is_some());
    }

    #[test]
    fn if_range_skips_weak_etags() {
        let mut ctx = RequestContext { etag: Some("W/\"abc\"".into()), ..Default::default() };
        assert_eq!(ctx.if_range(), None);
        ctx.last_modified = Some("Tue, 01 Dec 2026 10:00:00 GMT".into());
        assert_eq!(ctx.if_range(), ctx.last_modified.as_deref());
        ctx.etag = Some("\"abc\"".into());
        assert_eq!(ctx.if_range(), Some("\"abc\""));
    }
}
