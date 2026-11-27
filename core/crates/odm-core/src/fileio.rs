//! Positional file writes, so each connection writes at its own offset in the shared `.part` file.

use std::fs::File;
use std::io;

#[cfg(windows)]
pub fn write_all_at(file: &File, mut buf: &[u8], mut offset: u64) -> io::Result<()> {
    use std::os::windows::fs::FileExt;
    while !buf.is_empty() {
        let n = file.seek_write(buf, offset)?;
        if n == 0 {
            return Err(io::ErrorKind::WriteZero.into());
        }
        buf = &buf[n..];
        offset += n as u64;
    }
    Ok(())
}

#[cfg(unix)]
pub fn write_all_at(file: &File, buf: &[u8], offset: u64) -> io::Result<()> {
    use std::os::unix::fs::FileExt;
    file.write_all_at(buf, offset)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Read;

    #[test]
    fn writes_out_of_order() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("f");
        let file = File::options().read(true).write(true).create(true).truncate(true).open(&path).unwrap();
        file.set_len(6).unwrap();
        write_all_at(&file, b"def", 3).unwrap();
        write_all_at(&file, b"abc", 0).unwrap();
        drop(file);
        let mut s = String::new();
        File::open(&path).unwrap().read_to_string(&mut s).unwrap();
        assert_eq!(s, "abcdef");
    }
}
