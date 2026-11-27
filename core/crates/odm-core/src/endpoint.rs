//! Where the app listens for the native host: a per-user named pipe on Windows, a per-user Unix
//! domain socket elsewhere. The app and the host compute the same name independently.

/// The app's executable name, which the host starts when the app is not running.
#[cfg(windows)]
pub const APP_EXE: &str = "OpenDownloadManager.exe";
#[cfg(not(windows))]
pub const APP_EXE: &str = "OpenDownloadManager";

/// The argument that starts the app in the tray without showing its window.
pub const BACKGROUND_ARG: &str = "--background";

fn user_name() -> String {
    let raw = std::env::var("USERNAME").or_else(|_| std::env::var("USER")).unwrap_or_else(|_| "user".into());
    raw.chars().map(|c| if c.is_ascii_alphanumeric() || c == '-' || c == '_' { c } else { '_' }).collect()
}

/// `\\.\pipe\OpenDownloadManager-<user>` on Windows.
#[cfg(windows)]
pub fn path() -> String {
    format!(r"\\.\pipe\OpenDownloadManager-{}", user_name())
}

/// `<temp dir>/OpenDownloadManager-<user>.sock` elsewhere.
#[cfg(not(windows))]
pub fn path() -> String {
    std::env::temp_dir()
        .join(format!("OpenDownloadManager-{}.sock", user_name()))
        .to_string_lossy()
        .into_owned()
}

#[cfg(test)]
mod tests {
    #[test]
    fn name_is_per_user_and_safe() {
        let p = super::path();
        assert!(p.contains("OpenDownloadManager-"));
        assert!(!p.ends_with('-'));
    }
}
