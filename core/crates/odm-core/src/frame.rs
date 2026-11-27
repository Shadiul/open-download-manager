//! Native messaging framing: each message is a 4-byte little-endian length followed by UTF-8 JSON.
//!
//! The browser uses it on the host's stdin and stdout, and the host reuses it on the pipe to the app.

use std::io::{self, Read, Write};

use serde::{Serialize, de::DeserializeOwned};

/// Chrome's limit for one message from the host to the browser.
pub const MAX_TO_BROWSER: usize = 1024 * 1024;
/// Chrome's limit for one message from the browser to the host.
pub const MAX_FROM_BROWSER: usize = 64 * 1024 * 1024;

#[derive(Debug, thiserror::Error)]
pub enum FrameError {
    #[error(transparent)]
    Io(#[from] io::Error),
    #[error("message of {size} bytes is over the {limit}-byte limit")]
    TooLarge { size: usize, limit: usize },
    #[error("message is not valid JSON: {0}")]
    Json(#[from] serde_json::Error),
}

/// Reads one frame. Returns `Ok(None)` when the stream ends before a new frame starts.
pub fn read_frame(reader: &mut impl Read, limit: usize) -> Result<Option<Vec<u8>>, FrameError> {
    let mut header = [0u8; 4];
    match reader.read_exact(&mut header) {
        Ok(()) => {}
        Err(e) if e.kind() == io::ErrorKind::UnexpectedEof => return Ok(None),
        Err(e) => return Err(e.into()),
    }
    let size = u32::from_le_bytes(header) as usize;
    if size > limit {
        return Err(FrameError::TooLarge { size, limit });
    }
    let mut body = vec![0u8; size];
    reader.read_exact(&mut body)?;
    Ok(Some(body))
}

/// Writes one frame and flushes, so the reader sees it at once.
pub fn write_frame(writer: &mut impl Write, body: &[u8], limit: usize) -> Result<(), FrameError> {
    if body.len() > limit {
        return Err(FrameError::TooLarge { size: body.len(), limit });
    }
    let size = u32::try_from(body.len()).map_err(|_| FrameError::TooLarge { size: body.len(), limit })?;
    writer.write_all(&size.to_le_bytes())?;
    writer.write_all(body)?;
    writer.flush()?;
    Ok(())
}

pub fn read_json<T: DeserializeOwned>(reader: &mut impl Read, limit: usize) -> Result<Option<T>, FrameError> {
    match read_frame(reader, limit)? {
        Some(body) => Ok(Some(serde_json::from_slice(&body)?)),
        None => Ok(None),
    }
}

pub fn write_json<T: Serialize>(writer: &mut impl Write, value: &T, limit: usize) -> Result<(), FrameError> {
    write_frame(writer, &serde_json::to_vec(value)?, limit)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Cursor;

    #[test]
    fn round_trips_several_frames() {
        let mut buf = Vec::new();
        write_frame(&mut buf, b"{\"a\":1}", MAX_TO_BROWSER).unwrap();
        write_frame(&mut buf, b"[]", MAX_TO_BROWSER).unwrap();
        assert_eq!(&buf[..4], &7u32.to_le_bytes());

        let mut r = Cursor::new(buf);
        assert_eq!(read_frame(&mut r, MAX_FROM_BROWSER).unwrap().unwrap(), b"{\"a\":1}");
        assert_eq!(read_frame(&mut r, MAX_FROM_BROWSER).unwrap().unwrap(), b"[]");
        assert!(read_frame(&mut r, MAX_FROM_BROWSER).unwrap().is_none());
    }

    #[test]
    fn rejects_oversized_frames_both_ways() {
        let mut header = Cursor::new(1_000u32.to_le_bytes().to_vec());
        assert!(matches!(read_frame(&mut header, 999), Err(FrameError::TooLarge { size: 1000, limit: 999 })));

        let mut out = Vec::new();
        assert!(matches!(write_frame(&mut out, &[0; 10], 9), Err(FrameError::TooLarge { .. })));
        assert!(out.is_empty());
    }

    #[test]
    fn truncated_body_is_an_error() {
        let mut data = 10u32.to_le_bytes().to_vec();
        data.extend_from_slice(b"short");
        assert!(matches!(read_frame(&mut Cursor::new(data), 100), Err(FrameError::Io(_))));
    }
}
