//! Open Download Manager core: everything that is not UI.
//!
//! Shared by the Windows app (WinUI 3, through a C interface), the macOS app (SwiftUI, through
//! UniFFI) and the native messaging host. OS-specific code lives only behind small adapters.
//!
//! Still to come, in the order of the plan's milestone 1: the HTTP engine (tokio and reqwest),
//! SQLite state for crash-safe resume, and the queue and scheduler. HLS, DASH and yt-dlp
//! control come in the video milestone.

pub mod frame;
pub mod job;
pub mod protocol;
pub mod segments;
