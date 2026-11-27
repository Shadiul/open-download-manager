//! The app side of the browser link: the native host connects here and relays the extension's
//! messages, framed the same way as on its stdin and stdout.
//!
//! On Windows the pipe refuses remote clients and its name includes the user name; the default
//! pipe security lets only the same user (and administrators) write to it. A web page can never
//! reach it: only our extension, listed in the host manifest's `allowed_origins`, starts the host.

use std::io;

use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt};
use tokio_util::sync::CancellationToken;

use crate::frame::{MAX_FROM_BROWSER, MAX_TO_BROWSER};
use crate::job::RequestContext;
use crate::manager::Manager;
use crate::protocol::{Envelope, PROTOCOL_VERSION, Reply, Request};

/// Answers one message from the extension.
pub async fn handle(manager: &Manager, body: &[u8]) -> Reply {
    let envelope: Envelope = match serde_json::from_slice(body) {
        Ok(e) => e,
        Err(e) => return Reply::error(format!("bad message: {e}")),
    };
    if envelope.v != PROTOCOL_VERSION {
        return Reply::error(format!("unsupported protocol version {}", envelope.v));
    }
    match envelope.request {
        Request::Ping => Reply::pong(crate::VERSION),
        Request::Download(d) => match manager.browser(vec![d]).await {
            Ok(()) => Reply::accepted(),
            Err(e) => Reply::error(e),
        },
        Request::Batch(b) => match manager.browser(b.items).await {
            Ok(()) => Reply::accepted(),
            Err(e) => Reply::error(e),
        },
        Request::Probe(p) => {
            let ctx = RequestContext { cookies: p.cookies, user_agent: p.user_agent, ..Default::default() };
            match manager.probe(&p.url, &ctx).await {
                Ok(result) => Reply::formats(result.title, result.formats),
                Err(e) => Reply::error(e),
            }
        }
    }
}

pub async fn read_frame<R: AsyncRead + Unpin>(r: &mut R) -> io::Result<Option<Vec<u8>>> {
    let mut header = [0u8; 4];
    match r.read_exact(&mut header).await {
        Ok(_) => {}
        Err(e) if e.kind() == io::ErrorKind::UnexpectedEof => return Ok(None),
        Err(e) => return Err(e),
    }
    let size = u32::from_le_bytes(header) as usize;
    if size > MAX_FROM_BROWSER {
        return Err(io::Error::new(io::ErrorKind::InvalidData, "message too large"));
    }
    let mut body = vec![0u8; size];
    r.read_exact(&mut body).await?;
    Ok(Some(body))
}

pub async fn write_frame<W: AsyncWrite + Unpin>(w: &mut W, body: &[u8]) -> io::Result<()> {
    if body.len() > MAX_TO_BROWSER {
        return Err(io::Error::new(io::ErrorKind::InvalidData, "reply too large"));
    }
    w.write_all(&(body.len() as u32).to_le_bytes()).await?;
    w.write_all(body).await?;
    w.flush().await
}

async fn serve_connection<S: AsyncRead + AsyncWrite + Unpin>(mut stream: S, manager: Manager) {
    while let Ok(Some(body)) = read_frame(&mut stream).await {
        let reply = handle(&manager, &body).await;
        let bytes = serde_json::to_vec(&reply).expect("reply serializes");
        if write_frame(&mut stream, &bytes).await.is_err() {
            break;
        }
    }
}

#[cfg(windows)]
pub struct Listener {
    name: String,
    server: tokio::net::windows::named_pipe::NamedPipeServer,
}

/// Claims the endpoint. Fails if another instance of the app already listens on it.
/// Call inside a Tokio runtime.
#[cfg(windows)]
pub fn bind(name: &str) -> io::Result<Listener> {
    use tokio::net::windows::named_pipe::ServerOptions;
    let server = ServerOptions::new().first_pipe_instance(true).reject_remote_clients(true).create(name)?;
    Ok(Listener { name: name.to_string(), server })
}

#[cfg(windows)]
pub async fn serve(listener: Listener, manager: Manager, stop: CancellationToken) -> io::Result<()> {
    use tokio::net::windows::named_pipe::ServerOptions;
    let Listener { name, mut server } = listener;
    loop {
        tokio::select! {
            r = server.connect() => r?,
            _ = stop.cancelled() => return Ok(()),
        }
        let next = ServerOptions::new().reject_remote_clients(true).create(&name)?;
        let connected = std::mem::replace(&mut server, next);
        tokio::spawn(serve_connection(connected, manager.clone()));
    }
}

#[cfg(unix)]
pub struct Listener {
    listener: tokio::net::UnixListener,
}

#[cfg(unix)]
pub fn bind(path: &str) -> io::Result<Listener> {
    use std::os::unix::fs::PermissionsExt;
    // A socket file left behind by a crash: remove it unless someone answers on it.
    if std::os::unix::net::UnixStream::connect(path).is_ok() {
        return Err(io::Error::new(io::ErrorKind::AddrInUse, "another instance is running"));
    }
    let _ = std::fs::remove_file(path);
    let listener = tokio::net::UnixListener::bind(path)?;
    std::fs::set_permissions(path, std::fs::Permissions::from_mode(0o600))?;
    Ok(Listener { listener })
}

#[cfg(unix)]
pub async fn serve(listener: Listener, manager: Manager, stop: CancellationToken) -> io::Result<()> {
    loop {
        let (stream, _) = tokio::select! {
            r = listener.listener.accept() => r?,
            _ = stop.cancelled() => return Ok(()),
        };
        tokio::spawn(serve_connection(stream, manager.clone()));
    }
}
