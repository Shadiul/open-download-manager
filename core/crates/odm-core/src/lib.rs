//! Open Download Manager core: everything that is not UI.
//!
//! Shared by the Windows app (WinUI 3, through the C interface in `odm-ffi`), a later macOS app,
//! and the native messaging host. OS-specific code lives only behind small adapters
//! ([`fileio`] and [`endpoint`]).
//!
//! Without the default `runtime` feature only the protocol pieces are built, which is all the
//! native host needs.

pub mod endpoint;
pub mod frame;
pub mod job;
pub mod protocol;
pub mod segments;
pub mod settings;

#[cfg(feature = "runtime")]
pub mod api;
#[cfg(feature = "runtime")]
pub mod engine;
#[cfg(feature = "runtime")]
pub mod fileio;
#[cfg(feature = "runtime")]
pub mod ipc;
#[cfg(feature = "runtime")]
pub mod limiter;
#[cfg(feature = "runtime")]
pub mod manager;
#[cfg(feature = "runtime")]
pub mod naming;
#[cfg(feature = "runtime")]
pub mod store;
#[cfg(feature = "runtime")]
pub mod video;

/// The core's version, reported to the extension in ping replies.
pub const VERSION: &str = env!("CARGO_PKG_VERSION");

/// Locks a mutex, carrying on with the data if another thread panicked while holding it.
#[cfg(feature = "runtime")]
pub(crate) fn lock<T>(m: &std::sync::Mutex<T>) -> std::sync::MutexGuard<'_, T> {
    m.lock().unwrap_or_else(|e| e.into_inner())
}
