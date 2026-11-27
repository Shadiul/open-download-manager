//! A small HTTP/1.1 test server with the behaviours the engine must survive: range requests,
//! `If-Range`, dropped connections, slow connections, 403 and 429 replies, and a file that
//! changes on the server.

#![allow(dead_code)]

use std::sync::atomic::{AtomicU64, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::{TcpListener, TcpStream};

pub fn data(len: usize, seed: u8) -> Vec<u8> {
    (0..len).map(|i| ((i * 7 + seed as usize * 13) % 251) as u8).collect()
}

#[derive(Default)]
pub struct Behaviour {
    pub ranges: bool,
    pub content_length: bool,
    /// Close the next N responses after this many body bytes.
    pub drop_responses: AtomicUsize,
    pub drop_after: usize,
    /// Body bytes per second per connection; 0 is unlimited.
    pub rate: AtomicU64,
    /// Answer every request with 403.
    pub forbid: bool,
    /// Answer the next N range requests (not the first request) with 429.
    pub busy_responses: AtomicUsize,
    pub disposition: Option<String>,
}

pub struct Server {
    pub url: String,
    pub content: Arc<Mutex<(Vec<u8>, String)>>,
    pub behaviour: Arc<Behaviour>,
    pub requests: Arc<Mutex<Vec<String>>>,
    pub max_concurrent: Arc<AtomicUsize>,
}

impl Server {
    pub async fn start(content: Vec<u8>, behaviour: Behaviour) -> Self {
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let url = format!("http://{}/files/test.bin", listener.local_addr().unwrap());
        let content = Arc::new(Mutex::new((content, "\"v1\"".to_string())));
        let behaviour = Arc::new(behaviour);
        let requests = Arc::new(Mutex::new(Vec::new()));
        let max_concurrent = Arc::new(AtomicUsize::new(0));
        let current = Arc::new(AtomicUsize::new(0));
        {
            let (content, behaviour, requests, max_concurrent) =
                (content.clone(), behaviour.clone(), requests.clone(), max_concurrent.clone());
            tokio::spawn(async move {
                loop {
                    let Ok((sock, _)) = listener.accept().await else { break };
                    let (content, behaviour, requests, max_c, current) = (
                        content.clone(),
                        behaviour.clone(),
                        requests.clone(),
                        max_concurrent.clone(),
                        current.clone(),
                    );
                    tokio::spawn(async move {
                        let now = current.fetch_add(1, Ordering::SeqCst) + 1;
                        max_c.fetch_max(now, Ordering::SeqCst);
                        let _ = serve(sock, &content, &behaviour, &requests).await;
                        current.fetch_sub(1, Ordering::SeqCst);
                    });
                }
            });
        }
        Self { url, content, behaviour, requests, max_concurrent }
    }

    /// Replaces the file and its ETag, as if it were updated on the server.
    pub fn change(&self, content: Vec<u8>) {
        *self.content.lock().unwrap() = (content, "\"v2\"".into());
    }

    pub fn requests(&self) -> Vec<String> {
        self.requests.lock().unwrap().clone()
    }
}

async fn serve(
    mut sock: TcpStream,
    content: &Mutex<(Vec<u8>, String)>,
    b: &Behaviour,
    requests: &Mutex<Vec<String>>,
) -> std::io::Result<()> {
    let mut buf = Vec::new();
    let mut tmp = [0u8; 1024];
    while !buf.windows(4).any(|w| w == b"\r\n\r\n") {
        let n = sock.read(&mut tmp).await?;
        if n == 0 {
            return Ok(());
        }
        buf.extend_from_slice(&tmp[..n]);
    }
    let head = String::from_utf8_lossy(&buf).to_string();
    let header = |name: &str| {
        head.lines()
            .find(|l| l.to_ascii_lowercase().starts_with(&format!("{}:", name.to_ascii_lowercase())))
            .map(|l| l.split_once(':').unwrap().1.trim().to_string())
    };
    let range = header("range");
    let if_range = header("if-range");
    let first_request = requests.lock().unwrap().is_empty();
    requests.lock().unwrap().push(format!(
        "range={} if-range={}",
        range.clone().unwrap_or_default(),
        if_range.clone().unwrap_or_default()
    ));

    if b.forbid {
        sock.write_all(b"HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n").await?;
        return Ok(());
    }
    if !first_request
        && b.busy_responses.load(Ordering::SeqCst) > 0
        && b.busy_responses.fetch_sub(1, Ordering::SeqCst) > 0
    {
        sock.write_all(b"HTTP/1.1 429 Too Many Requests\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
            .await?;
        return Ok(());
    }

    let (data, etag) = content.lock().unwrap().clone();
    let total = data.len();
    let mut status = "200 OK";
    let (mut from, mut to) = (0usize, total.saturating_sub(1));
    if b.ranges
        && let Some(r) = range.as_deref().and_then(|r| r.strip_prefix("bytes="))
        && if_range.as_deref().is_none_or(|v| v == etag)
    {
        let (a, z) = r.split_once('-').unwrap();
        from = a.parse().unwrap();
        if !z.is_empty() {
            to = z.parse::<usize>().unwrap().min(total - 1);
        }
        if from >= total {
            sock.write_all(
                b"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            )
            .await?;
            return Ok(());
        }
        status = "206 Partial Content";
    }
    let body = &data[from..=to];
    let mut head = format!(
        "HTTP/1.1 {status}\r\nConnection: close\r\nETag: {etag}\r\nContent-Type: application/octet-stream\r\n"
    );
    if b.ranges {
        head.push_str("Accept-Ranges: bytes\r\n");
    }
    if status.starts_with("206") {
        head.push_str(&format!("Content-Range: bytes {from}-{to}/{total}\r\n"));
    }
    if b.content_length || status.starts_with("206") {
        head.push_str(&format!("Content-Length: {}\r\n", body.len()));
    }
    if let Some(d) = &b.disposition {
        head.push_str(&format!("Content-Disposition: {d}\r\n"));
    }
    head.push_str("\r\n");
    sock.write_all(head.as_bytes()).await?;

    let drop_this =
        b.drop_responses.load(Ordering::SeqCst) > 0 && b.drop_responses.fetch_sub(1, Ordering::SeqCst) > 0;
    let mut sent = 0;
    for chunk in body.chunks(16 * 1024) {
        if drop_this && sent >= b.drop_after {
            return Ok(()); // connection closed mid-body
        }
        sock.write_all(chunk).await?;
        sent += chunk.len();
        let rate = b.rate.load(Ordering::SeqCst);
        if rate > 0 {
            tokio::time::sleep(Duration::from_secs_f64(chunk.len() as f64 / rate as f64)).await;
        }
    }
    sock.shutdown().await
}
