//! The state of one download.

use serde::{Deserialize, Serialize};

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
    }
}
