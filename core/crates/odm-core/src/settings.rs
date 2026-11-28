//! User settings, stored in the state database and edited from the app's settings page.

use serde::{Deserialize, Serialize};

use crate::segments::{DEFAULT_CONNECTIONS, MAX_CONNECTIONS};

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Settings {
    /// Base folder for finished files. Empty means the app's default (the user's Downloads).
    pub download_dir: String,
    /// Put files in Video, Music, Documents, Programs, Archives or Other under the base folder.
    pub category_folders: bool,
    /// Connections per file, 1 to 32.
    pub connections: usize,
    /// Downloads running at the same time.
    pub max_parallel: usize,
    /// Bytes per second across all downloads; 0 means no limit.
    pub speed_limit: u64,
    pub schedule: Schedule,
    pub when_done: WhenDone,
    /// Check for a new yt-dlp once a day.
    pub auto_update_tools: bool,
    /// Show the new-download window (name, size, folder) before a browser download starts.
    pub confirm_browser_downloads: bool,
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            download_dir: String::new(),
            category_folders: true,
            connections: DEFAULT_CONNECTIONS,
            max_parallel: 3,
            speed_limit: 0,
            schedule: Schedule::default(),
            when_done: WhenDone::Nothing,
            auto_update_tools: true,
            confirm_browser_downloads: true,
        }
    }
}

impl Settings {
    /// Brings out-of-range values back into range.
    pub fn normalized(mut self) -> Self {
        self.connections = self.connections.clamp(1, MAX_CONNECTIONS);
        // A folder that is not a full path (a stray edit) falls back to the default folder.
        if !std::path::Path::new(self.download_dir.trim()).is_absolute() {
            self.download_dir.clear();
        }
        self.max_parallel = self.max_parallel.clamp(1, 20);
        self
    }
}

/// Downloads start only between `start` and `stop` (local time, `HH:MM`), when enabled.
/// A window may run past midnight, such as 23:00 to 07:00.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Schedule {
    pub enabled: bool,
    pub start: String,
    pub stop: String,
}

impl Default for Schedule {
    fn default() -> Self {
        Self { enabled: false, start: "23:00".into(), stop: "07:00".into() }
    }
}

impl Schedule {
    /// Whether downloads may run at `minute_of_day` (0 to 1439).
    pub fn is_open(&self, minute_of_day: u32) -> bool {
        if !self.enabled {
            return true;
        }
        let (Some(start), Some(stop)) = (parse_hhmm(&self.start), parse_hhmm(&self.stop)) else {
            return true;
        };
        match start.cmp(&stop) {
            std::cmp::Ordering::Less => (start..stop).contains(&minute_of_day),
            std::cmp::Ordering::Greater => minute_of_day >= start || minute_of_day < stop,
            std::cmp::Ordering::Equal => true,
        }
    }
}

fn parse_hhmm(s: &str) -> Option<u32> {
    let (h, m) = s.trim().split_once(':')?;
    let (h, m): (u32, u32) = (h.parse().ok()?, m.parse().ok()?);
    (h < 24 && m < 60).then_some(h * 60 + m)
}

/// What happens when the queue runs empty. The app carries it out.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum WhenDone {
    #[default]
    Nothing,
    Sleep,
    Hibernate,
    Shutdown,
    Restart,
    SignOut,
    Lock,
    /// Close Open Download Manager.
    Quit,
}

#[cfg(test)]
mod tests {
    use super::*;

    fn window(start: &str, stop: &str) -> Schedule {
        Schedule { enabled: true, start: start.into(), stop: stop.into() }
    }

    #[test]
    fn schedule_windows() {
        let day = window("09:00", "17:30");
        assert!(day.is_open(9 * 60));
        assert!(!day.is_open(17 * 60 + 30));
        assert!(!day.is_open(3 * 60));

        let night = window("23:00", "07:00");
        assert!(night.is_open(23 * 60 + 5));
        assert!(night.is_open(2 * 60));
        assert!(!night.is_open(12 * 60));

        assert!(Schedule::default().is_open(12 * 60), "disabled means always open");
        assert!(window("bad", "07:00").is_open(12 * 60));
    }

    #[test]
    fn missing_fields_take_defaults() {
        let s: Settings = serde_json::from_str(r#"{ "connections": 99 }"#).unwrap();
        assert_eq!(s.clone().normalized().connections, MAX_CONNECTIONS);
        assert_eq!(s.max_parallel, 3);
        let s: Settings = serde_json::from_str(r#"{ "downloadDir": "s tC:/Users/me/Downloads" }"#).unwrap();
        assert_eq!(s.normalized().download_dir, "", "a path that is not absolute is dropped");
    }

    #[test]
    fn when_done_names() {
        for (name, value) in [
            ("nothing", WhenDone::Nothing),
            ("sleep", WhenDone::Sleep),
            ("hibernate", WhenDone::Hibernate),
            ("shutdown", WhenDone::Shutdown),
            ("restart", WhenDone::Restart),
            ("signOut", WhenDone::SignOut),
            ("lock", WhenDone::Lock),
            ("quit", WhenDone::Quit),
        ] {
            let s: Settings = serde_json::from_str(&format!(r#"{{ "whenDone": "{name}" }}"#)).unwrap();
            assert_eq!(s.when_done, value);
            assert_eq!(serde_json::to_value(value).unwrap(), name);
        }
    }
}
