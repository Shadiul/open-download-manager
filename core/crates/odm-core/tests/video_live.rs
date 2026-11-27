//! Downloads a short Creative Commons video with the real yt-dlp. Needs the network and the tools
//! from scripts/fetch-tools.ps1, so it only runs when asked:
//!
//!     cargo test -p odm-core --test video_live -- --ignored

use std::path::PathBuf;
use std::sync::Arc;
use std::sync::atomic::Ordering;
use std::time::Duration;

use odm_core::engine::{Outcome, Transfer};
use odm_core::job::RequestContext;
use odm_core::protocol::MediaKind;
use odm_core::video::{self, VideoJob};
use tokio_util::sync::CancellationToken;

#[tokio::test(flavor = "multi_thread")]
#[ignore = "needs the network and tools/"]
async fn video_downloads_report_size_and_progress_while_running() {
    let tools = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../../tools");
    assert!(video::tool_path(&tools, "yt-dlp").exists(), "run scripts/fetch-tools.ps1 first");
    let out = tempfile::tempdir().unwrap();
    let job = VideoJob {
        // Big Buck Bunny, Blender Foundation, CC BY 3.0.
        url: "https://www.youtube.com/watch?v=aqz-KE-bpKQ".into(),
        request: RequestContext::default(),
        media_kind: Some(MediaKind::Page),
        format_id: Some("134+140".into()),
        save_dir: out.path().to_path_buf(),
        tools_dir: tools,
        // Slow enough to watch it run.
        speed_limit: 3_000_000,
        file_name: Some("bunny".into()),
    };
    let transfer = Arc::new(Transfer::default());
    let run = tokio::spawn(video::run(job, transfer.clone(), CancellationToken::new()));

    let mut seen_mid_download = None;
    while !run.is_finished() {
        let (done, total) = (
            transfer.external_downloaded.load(Ordering::Relaxed),
            transfer.external_total.load(Ordering::Relaxed),
        );
        if done > 0 && total > done {
            seen_mid_download.get_or_insert((done, total));
        }
        tokio::time::sleep(Duration::from_millis(100)).await;
    }
    let outcome = run.await.unwrap();
    let Outcome::Completed { path } = outcome else { panic!("{outcome:?}") };
    assert_eq!(path.file_name().unwrap(), "bunny.mp4", "the chosen name is used");
    let (done, total) = seen_mid_download.expect("progress and a total were reported while downloading");
    println!(
        "mid-download: {done} of {total} bytes; final file {} bytes",
        std::fs::metadata(&path).unwrap().len()
    );
}
