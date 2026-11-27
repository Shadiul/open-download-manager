//! The C interface the Windows app calls through P/Invoke. Everything crosses as UTF-8 JSON:
//!
//! ```c
//! typedef void (*odm_event_cb)(const char *event_json, void *user);
//! void *odm_start(const char *config_json, odm_event_cb cb, void *user, char **error_out);
//! char *odm_call(void *core, const char *command_json);   // free with odm_free_string
//! void  odm_free_string(char *s);
//! void  odm_stop(void *core);
//! ```
//!
//! The callback runs on the core's threads. It must return quickly and must not call `odm_call`.
//! No panic crosses the boundary: each function catches them and reports an error instead.

use std::ffi::{CStr, CString, c_char, c_void};
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::sync::Arc;

use odm_core::api::{Core, CoreConfig};

pub type EventCallback = extern "C" fn(event_json: *const c_char, user: *mut c_void);

/// The app's context pointer, handed back with every event.
struct User(*mut c_void);
// SAFETY: the pointer is opaque to us and only passed back to the callback; the app promises
// its callback can be called from any thread.
unsafe impl Send for User {}
unsafe impl Sync for User {}

fn to_c(s: String) -> *mut c_char {
    // JSON never contains a NUL byte; strip one defensively instead of failing.
    CString::new(s.replace('\0', "")).expect("no NUL bytes").into_raw()
}

/// # Safety
/// `config_json` must be a valid NUL-terminated UTF-8 string. `error_out`, if not null, must be
/// writable; on failure it receives a string to free with [`odm_free_string`].
#[unsafe(no_mangle)]
pub unsafe extern "C" fn odm_start(
    config_json: *const c_char,
    callback: Option<EventCallback>,
    user: *mut c_void,
    error_out: *mut *mut c_char,
) -> *mut Core {
    let user = Arc::new(User(user));
    let result = catch_unwind(AssertUnwindSafe(|| -> Result<Core, String> {
        if config_json.is_null() {
            return Err("config is null".into());
        }
        // SAFETY: checked for null; the caller guarantees a NUL-terminated string.
        let text = unsafe { CStr::from_ptr(config_json) }.to_str().map_err(|e| e.to_string())?;
        let config: CoreConfig = serde_json::from_str(text).map_err(|e| format!("bad config: {e}"))?;
        let sink = Arc::new(move |json: String| {
            if let Some(cb) = callback
                && let Ok(c) = CString::new(json)
            {
                cb(c.as_ptr(), user.0);
            }
        });
        Core::start(config, sink)
    }));
    let error = match result {
        Ok(Ok(core)) => return Box::into_raw(Box::new(core)),
        Ok(Err(e)) => e,
        Err(_) => "the engine panicked while starting".to_string(),
    };
    if !error_out.is_null() {
        // SAFETY: the caller guarantees `error_out` is writable when not null.
        unsafe { *error_out = to_c(error) };
    }
    std::ptr::null_mut()
}

/// # Safety
/// `core` must come from [`odm_start`] and not yet be stopped; `command_json` must be a valid
/// NUL-terminated UTF-8 string. Do not call from inside the event callback.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn odm_call(core: *const Core, command_json: *const c_char) -> *mut c_char {
    let reply = catch_unwind(AssertUnwindSafe(|| {
        if core.is_null() || command_json.is_null() {
            return r#"{"ok":false,"error":"null argument"}"#.to_string();
        }
        // SAFETY: checked for null; the caller guarantees both pointers are valid.
        let (core, text) = unsafe { (&*core, CStr::from_ptr(command_json)) };
        match text.to_str() {
            Ok(text) => core.call(text),
            Err(_) => r#"{"ok":false,"error":"command is not UTF-8"}"#.to_string(),
        }
    }))
    .unwrap_or_else(|_| r#"{"ok":false,"error":"the engine panicked"}"#.to_string());
    to_c(reply)
}

/// # Safety
/// `s` must be null or a string returned by this library, freed only once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn odm_free_string(s: *mut c_char) {
    if !s.is_null() {
        // SAFETY: the caller guarantees `s` came from `CString::into_raw` here.
        drop(unsafe { CString::from_raw(s) });
    }
}

/// Saves progress and stops the engine. Blocks for up to a few seconds.
///
/// # Safety
/// `core` must be null or come from [`odm_start`], and is invalid afterwards.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn odm_stop(core: *mut Core) {
    if core.is_null() {
        return;
    }
    // SAFETY: the caller guarantees `core` came from `Box::into_raw` in `odm_start`.
    let core = unsafe { Box::from_raw(core) };
    let _ = catch_unwind(AssertUnwindSafe(move || core.stop()));
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Mutex;
    use std::sync::atomic::{AtomicUsize, Ordering};

    static EVENTS: AtomicUsize = AtomicUsize::new(0);
    static LAST: Mutex<String> = Mutex::new(String::new());

    extern "C" fn on_event(json: *const c_char, _user: *mut c_void) {
        let s = unsafe { CStr::from_ptr(json) }.to_string_lossy().into_owned();
        *LAST.lock().unwrap() = s;
        EVENTS.fetch_add(1, Ordering::SeqCst);
    }

    fn call(core: *mut Core, cmd: &str) -> serde_json::Value {
        let c = CString::new(cmd).unwrap();
        let raw = unsafe { odm_call(core, c.as_ptr()) };
        let s = unsafe { CStr::from_ptr(raw) }.to_str().unwrap().to_string();
        unsafe { odm_free_string(raw) };
        serde_json::from_str(&s).unwrap()
    }

    #[test]
    fn start_call_and_stop() {
        let dir = tempfile_dir();
        let config = serde_json::json!({
            "dataDir": dir.join("data"), "defaultDownloadDir": dir.join("dl"), "toolsDir": dir.join("tools"), "listen": false
        });
        let c = CString::new(config.to_string()).unwrap();
        let mut err = std::ptr::null_mut();
        let core = unsafe { odm_start(c.as_ptr(), Some(on_event), std::ptr::null_mut(), &mut err) };
        assert!(!core.is_null());

        assert_eq!(call(core, r#"{"type":"list"}"#)["jobs"], serde_json::json!([]));
        assert_eq!(call(core, "not json")["ok"], false);
        let added = call(core, r#"{"type":"add","url":"http://127.0.0.1:9/x.bin","start":false}"#);
        assert_eq!(added["ok"], true, "{added}");
        assert!(EVENTS.load(Ordering::SeqCst) >= 1);
        assert!(LAST.lock().unwrap().contains("jobAdded"));
        unsafe { odm_stop(core) };
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn bad_config_reports_an_error() {
        let c = CString::new("{}").unwrap();
        let mut err = std::ptr::null_mut();
        let core = unsafe { odm_start(c.as_ptr(), None, std::ptr::null_mut(), &mut err) };
        assert!(core.is_null());
        let msg = unsafe { CStr::from_ptr(err) }.to_string_lossy().into_owned();
        unsafe { odm_free_string(err) };
        assert!(msg.contains("bad config"), "{msg}");
    }

    fn tempfile_dir() -> std::path::PathBuf {
        let d = std::env::temp_dir().join(format!("odm-ffi-test-{}", std::process::id()));
        std::fs::create_dir_all(&d).unwrap();
        d
    }
}
