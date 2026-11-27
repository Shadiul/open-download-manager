//! The engine against a local server that misbehaves in the ways real servers do.

mod common;

use std::path::Path;
use std::sync::Arc;
use std::sync::atomic::Ordering;
use std::time::Duration;

use common::{Behaviour, Server, data};
use odm_core::engine::{self, Env, HttpJob, Outcome, Transfer};
use odm_core::job::RequestContext;
use odm_core::limiter::RateLimiter;
use tokio_util::sync::CancellationToken;

const KB: usize = 1024;

fn env() -> Env {
    let mut env = Env::new(engine::client(), Arc::new(RateLimiter::new(0)));
    env.min_split = 64 * KB as u64;
    env
}

fn job(url: &str, dir: &Path, connections: usize) -> HttpJob {
    HttpJob {
        url: url.into(),
        request: RequestContext::default(),
        file_name: String::new(),
        save_dir: dir.to_path_buf(),
        category_folders: false,
        fixed_name: false,
        total: None,
        resumable: false,
        segments: Vec::new(),
        connections,
    }
}

/// The job to resume with, built from what the stopped run settled.
fn resumed(mut job: HttpJob, transfer: &Transfer) -> HttpJob {
    let s = transfer.settled().expect("first response settled the job");
    job.file_name = s.file_name;
    job.save_dir = s.save_dir;
    job.total = s.total;
    job.resumable = s.resumable;
    job.request = s.request;
    job.segments = transfer.rows();
    job
}

async fn run(job: HttpJob, transfer: Arc<Transfer>) -> Outcome {
    tokio::time::timeout(Duration::from_secs(60), engine::run(job, env(), transfer, CancellationToken::new()))
        .await
        .expect("download finished in time")
}

fn completed(outcome: &Outcome) -> &Path {
    match outcome {
        Outcome::Completed { path } => path,
        other => panic!("expected completion, got {other:?}"),
    }
}

#[tokio::test(flavor = "multi_thread")]
async fn segmented_download_is_byte_exact() {
    let content = data(3 * KB * KB + 123, 1);
    let server = Server::start(
        content.clone(),
        Behaviour { ranges: true, content_length: true, ..Default::default() },
    )
    .await;
    let dir = tempfile::tempdir().unwrap();

    let transfer = Arc::new(Transfer::default());
    let outcome = run(job(&server.url, dir.path(), 4), transfer.clone()).await;
    let path = completed(&outcome);

    assert_eq!(path.file_name().unwrap(), "test.bin");
    assert_eq!(std::fs::read(path).unwrap(), content);
    assert!(!dir.path().join("test.bin.part").exists());
    assert!(server.max_concurrent.load(Ordering::SeqCst) > 1, "used several connections");
    assert!(transfer.settled().unwrap().resumable);
}

#[tokio::test(flavor = "multi_thread")]
async fn connections_keep_busy_by_splitting() {
    // 1 MB over 2 connections with a 64 KB minimum split: finished ranges take over others.
    let content = data(KB * KB, 2);
    let server = Server::start(
        content.clone(),
        Behaviour { ranges: true, content_length: true, ..Default::default() },
    )
    .await;
    server.behaviour.rate.store(2 * KB as u64 * KB as u64, Ordering::SeqCst);
    let dir = tempfile::tempdir().unwrap();
    let transfer = Arc::new(Transfer::default());
    let outcome = run(job(&server.url, dir.path(), 2), transfer.clone()).await;
    assert_eq!(std::fs::read(completed(&outcome)).unwrap(), content);
    assert!(transfer.rows().len() >= 2);
}

#[tokio::test(flavor = "multi_thread")]
async fn server_without_ranges_uses_one_connection() {
    let content = data(500 * KB, 3);
    let server = Server::start(
        content.clone(),
        Behaviour { ranges: false, content_length: true, ..Default::default() },
    )
    .await;
    let dir = tempfile::tempdir().unwrap();
    let transfer = Arc::new(Transfer::default());
    let outcome = run(job(&server.url, dir.path(), 8), transfer.clone()).await;
    assert_eq!(std::fs::read(completed(&outcome)).unwrap(), content);
    assert!(!transfer.settled().unwrap().resumable);
    assert_eq!(server.requests().len(), 1);
}

#[tokio::test(flavor = "multi_thread")]
async fn unknown_size_reads_to_the_end() {
    let content = data(300 * KB, 4);
    let server = Server::start(
        content.clone(),
        Behaviour { ranges: false, content_length: false, ..Default::default() },
    )
    .await;
    let dir = tempfile::tempdir().unwrap();
    let outcome = run(job(&server.url, dir.path(), 4), Arc::new(Transfer::default())).await;
    assert_eq!(std::fs::read(completed(&outcome)).unwrap(), content);
}

#[tokio::test(flavor = "multi_thread")]
async fn dropped_connections_are_retried() {
    let content = data(2 * KB * KB, 5);
    let behaviour =
        Behaviour { ranges: true, content_length: true, drop_after: 100 * KB, ..Default::default() };
    behaviour.drop_responses.store(3, Ordering::SeqCst);
    let server = Server::start(content.clone(), behaviour).await;
    let dir = tempfile::tempdir().unwrap();
    let outcome = run(job(&server.url, dir.path(), 4), Arc::new(Transfer::default())).await;
    assert_eq!(std::fs::read(completed(&outcome)).unwrap(), content);
}

#[tokio::test(flavor = "multi_thread")]
async fn busy_server_is_retried_and_remembered() {
    let content = data(KB * KB, 6);
    let behaviour = Behaviour { ranges: true, content_length: true, ..Default::default() };
    behaviour.busy_responses.store(2, Ordering::SeqCst);
    let server = Server::start(content.clone(), behaviour).await;
    let dir = tempfile::tempdir().unwrap();
    let env = env();
    let outcome = engine::run(
        job(&server.url, dir.path(), 4),
        env.clone(),
        Arc::new(Transfer::default()),
        CancellationToken::new(),
    )
    .await;
    assert_eq!(std::fs::read(completed(&outcome)).unwrap(), content);
    assert!(env.host_limits.lock().unwrap().contains_key("127.0.0.1"), "host limit learned");
}

#[tokio::test(flavor = "multi_thread")]
async fn pause_and_resume_keep_the_bytes_already_fetched() {
    let content = data(2 * KB * KB, 7);
    let server = Server::start(
        content.clone(),
        Behaviour { ranges: true, content_length: true, ..Default::default() },
    )
    .await;
    server.behaviour.rate.store(1500 * KB as u64, Ordering::SeqCst);
    let dir = tempfile::tempdir().unwrap();

    let first = job(&server.url, dir.path(), 4);
    let transfer = Arc::new(Transfer::default());
    let cancel = CancellationToken::new();
    let handle = tokio::spawn(engine::run(first.clone(), env(), transfer.clone(), cancel.clone()));
    while transfer.downloaded() < 500 * KB as u64 {
        tokio::time::sleep(Duration::from_millis(20)).await;
    }
    cancel.cancel();
    assert_eq!(handle.await.unwrap(), Outcome::Cancelled);
    let fetched = transfer.downloaded();
    assert!(dir.path().join("test.bin.part").exists());

    server.behaviour.rate.store(0, Ordering::SeqCst);
    let before = server.requests().len();
    let job2 = resumed(first, &transfer);
    let transfer2 = Transfer::with_segments(&job2.segments);
    assert_eq!(transfer2.downloaded(), fetched);
    let outcome = run(job2, transfer2).await;
    assert_eq!(std::fs::read(completed(&outcome)).unwrap(), content);
    let resumes = &server.requests()[before..];
    assert!(resumes.iter().all(|r| r.contains("if-range=\"v1\"")), "{resumes:?}");
    assert!(
        !resumes.iter().any(|r| r.starts_with("range=bytes=0-")),
        "nothing refetched from zero: {resumes:?}"
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn a_changed_file_restarts_from_zero() {
    let old = data(KB * KB, 8);
    let server =
        Server::start(old, Behaviour { ranges: true, content_length: true, ..Default::default() }).await;
    server.behaviour.rate.store(1000 * KB as u64, Ordering::SeqCst);
    let dir = tempfile::tempdir().unwrap();

    let first = job(&server.url, dir.path(), 2);
    let transfer = Arc::new(Transfer::default());
    let cancel = CancellationToken::new();
    let handle = tokio::spawn(engine::run(first.clone(), env(), transfer.clone(), cancel.clone()));
    while transfer.downloaded() < 200 * KB as u64 {
        tokio::time::sleep(Duration::from_millis(20)).await;
    }
    cancel.cancel();
    handle.await.unwrap();

    let new = data(1300 * KB, 9);
    server.change(new.clone());
    server.behaviour.rate.store(0, Ordering::SeqCst);
    let job2 = resumed(first, &transfer);
    let outcome = run(job2.clone(), Transfer::with_segments(&job2.segments)).await;
    assert_eq!(std::fs::read(completed(&outcome)).unwrap(), new, "no mix of old and new bytes");
}

#[tokio::test(flavor = "multi_thread")]
async fn forbidden_links_are_reported_as_expired() {
    let server = Server::start(data(KB, 1), Behaviour { forbid: true, ..Default::default() }).await;
    let dir = tempfile::tempdir().unwrap();
    let outcome = run(job(&server.url, dir.path(), 4), Arc::new(Transfer::default())).await;
    assert!(matches!(outcome, Outcome::Failed { link_expired: true, .. }), "{outcome:?}");
}

#[tokio::test(flavor = "multi_thread")]
async fn content_disposition_and_categories_pick_the_folder() {
    let content = data(10 * KB, 1);
    let behaviour = Behaviour {
        ranges: true,
        content_length: true,
        disposition: Some("attachment; filename=\"Report 2026.pdf\"".into()),
        ..Default::default()
    };
    let server = Server::start(content.clone(), behaviour).await;
    let dir = tempfile::tempdir().unwrap();
    let mut j = job(&server.url, dir.path(), 4);
    j.category_folders = true;
    let outcome = run(j, Arc::new(Transfer::default())).await;
    assert_eq!(completed(&outcome), dir.path().join("Documents").join("Report 2026.pdf"));
}

#[tokio::test(flavor = "multi_thread")]
async fn speed_limit_holds() {
    let content = data(400 * KB, 2);
    let server = Server::start(
        content.clone(),
        Behaviour { ranges: true, content_length: true, ..Default::default() },
    )
    .await;
    let dir = tempfile::tempdir().unwrap();
    let env = Env { global_limit: Arc::new(RateLimiter::new(400 * KB as u64)), ..env() };
    let start = std::time::Instant::now();
    let outcome = engine::run(
        job(&server.url, dir.path(), 4),
        env,
        Arc::new(Transfer::default()),
        CancellationToken::new(),
    )
    .await;
    completed(&outcome);
    let took = start.elapsed().as_secs_f64();
    assert!(took > 0.7, "400 KB at 400 KB/s took only {took} s");
}

#[tokio::test(flavor = "multi_thread")]
async fn a_restart_keeps_the_category_folder_single() {
    let behaviour = Behaviour {
        ranges: true,
        content_length: true,
        disposition: Some("attachment; filename=\"setup.exe\"".into()),
        ..Default::default()
    };
    let server = Server::start(data(KB * KB, 3), behaviour).await;
    server.behaviour.rate.store(800 * KB as u64, Ordering::SeqCst);
    let dir = tempfile::tempdir().unwrap();
    let mut j = job(&server.url, dir.path(), 2);
    j.category_folders = true;
    let transfer = Arc::new(Transfer::default());
    let handle = tokio::spawn(engine::run(j, env(), transfer.clone(), CancellationToken::new()));
    while transfer.downloaded() < 100 * KB as u64 {
        tokio::time::sleep(Duration::from_millis(20)).await;
    }
    // The file changes while it downloads: the engine starts over, in the same folder.
    server.change(data(1100 * KB, 4));
    server.behaviour.rate.store(0, Ordering::SeqCst);
    let outcome = handle.await.unwrap();
    assert_eq!(completed(&outcome), dir.path().join("Programs").join("setup.exe"));
}
