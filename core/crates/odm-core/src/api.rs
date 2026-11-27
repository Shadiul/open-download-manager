//! The core as one object: a Tokio runtime, the manager, and the browser endpoint.
//!
//! The app talks to it with JSON strings only: commands in through [`Core::call`], events out
//! through the sink given to [`Core::start`]. That keeps the C interface down to four functions.

use std::path::PathBuf;
use std::sync::Arc;
use std::time::Duration;

use serde::Deserialize;
use tokio::runtime::Runtime;
use tokio_util::sync::CancellationToken;

use crate::manager::{Event, EventSink, Manager, ManagerConfig};
use crate::store::Store;
use crate::{endpoint, ipc};

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CoreConfig {
    /// Holds `state.db`.
    pub data_dir: PathBuf,
    pub default_download_dir: PathBuf,
    /// Holds yt-dlp and ffmpeg.
    pub tools_dir: PathBuf,
    /// Listen for the native host. Off for the command-line runner and tests.
    #[serde(default = "yes")]
    pub listen: bool,
    /// Overrides the per-user endpoint name (for tests).
    #[serde(default)]
    pub endpoint: Option<String>,
}

fn yes() -> bool {
    true
}

/// Receives each event as a JSON string. Called from the core's threads: it must not block,
/// and must not call back into [`Core::call`].
pub type JsonSink = Arc<dyn Fn(String) + Send + Sync>;

pub struct Core {
    runtime: Option<Runtime>,
    manager: Manager,
    stop: CancellationToken,
}

impl Core {
    pub fn start(config: CoreConfig, sink: JsonSink) -> Result<Self, String> {
        std::fs::create_dir_all(&config.data_dir)
            .map_err(|e| format!("Cannot create {}: {e}", config.data_dir.display()))?;
        let runtime = tokio::runtime::Builder::new_multi_thread()
            .worker_threads(4)
            .thread_name("odm-core")
            .enable_all()
            .build()
            .map_err(|e| format!("Cannot start the engine: {e}"))?;
        let store = Store::open(&config.data_dir.join("state.db"))
            .map_err(|e| format!("Cannot open the download database: {e}"))?;

        let _guard = runtime.enter();
        let event_sink: EventSink = {
            let sink = sink.clone();
            Arc::new(move |event: Event| sink(serde_json::to_string(&event).expect("event serializes")))
        };
        let manager = Manager::start(
            store,
            ManagerConfig { default_download_dir: config.default_download_dir, tools_dir: config.tools_dir },
            event_sink,
        );
        let stop = CancellationToken::new();
        if config.listen {
            let name = config.endpoint.clone().unwrap_or_else(endpoint::path);
            match ipc::bind(&name) {
                Ok(listener) => {
                    let (manager, stop) = (manager.clone(), stop.clone());
                    runtime.spawn(async move {
                        let _ = ipc::serve(listener, manager, stop).await;
                    });
                }
                Err(e) => {
                    let message = format!("The browser extension cannot reach the app: {e}");
                    sink(serde_json::to_string(&Event::Error { message }).expect("event serializes"));
                }
            }
        }
        Ok(Self { runtime: Some(runtime), manager, stop })
    }

    /// Runs one JSON command and returns the JSON reply. Blocks; call from an app thread.
    pub fn call(&self, command: &str) -> String {
        self.runtime().block_on(self.manager.command_json(command)).to_string()
    }

    pub fn manager(&self) -> &Manager {
        &self.manager
    }

    pub fn runtime(&self) -> &Runtime {
        self.runtime.as_ref().expect("runtime lives until stop")
    }

    /// Saves progress, stops downloads and the endpoint, and shuts the runtime down.
    pub fn stop(mut self) {
        self.stop.cancel();
        if let Some(rt) = self.runtime.take() {
            rt.block_on(self.manager.shutdown());
            rt.shutdown_timeout(Duration::from_secs(2));
        }
    }
}
