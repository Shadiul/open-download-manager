//! Choosing a safe file name and the folder it goes in.

use std::path::{Path, PathBuf};

use percent_encoding::percent_decode_str;

const MAX_NAME_CHARS: usize = 180;
const RESERVED: [&str; 22] = [
    "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
    "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
];

/// The name from a `Content-Disposition` header. `filename*` (RFC 5987) wins over `filename`.
pub fn from_content_disposition(value: &str) -> Option<String> {
    let mut plain = None;
    for part in value.split(';') {
        let Some((key, val)) = part.trim().split_once('=') else { continue };
        let key = key.trim().to_ascii_lowercase();
        let val = val.trim();
        if key == "filename*" {
            // charset'language'percent-encoded-value
            let encoded = val.splitn(3, '\'').nth(2).unwrap_or(val).trim_matches('"');
            let decoded = percent_decode_str(encoded).decode_utf8_lossy().trim().to_string();
            if !decoded.is_empty() {
                return Some(decoded);
            }
        } else if key == "filename" {
            plain = Some(val.trim_matches('"').trim().to_string());
        }
    }
    plain.filter(|s| !s.is_empty())
}

/// The last path segment of a URL, percent-decoded.
pub fn from_url(url: &str) -> Option<String> {
    let without_query = url.split(['?', '#']).next()?;
    let after_scheme = without_query.split_once("://").map_or(without_query, |(_, rest)| rest);
    let (_, path) = after_scheme.split_once('/')?;
    let last = path.rsplit('/').next()?;
    let decoded = percent_decode_str(last).decode_utf8_lossy().trim().to_string();
    (!decoded.is_empty()).then_some(decoded)
}

/// An extension for a MIME type, for names that have none.
pub fn ext_for_mime(mime: &str) -> Option<&'static str> {
    let mime = mime.split(';').next()?.trim().to_ascii_lowercase();
    Some(match mime.as_str() {
        "application/pdf" => "pdf",
        "application/zip" | "application/x-zip-compressed" => "zip",
        "application/x-7z-compressed" => "7z",
        "application/vnd.rar" | "application/x-rar-compressed" => "rar",
        "application/gzip" => "gz",
        "application/x-msdownload" | "application/vnd.microsoft.portable-executable" => "exe",
        "application/x-msi" => "msi",
        "application/json" => "json",
        "text/plain" => "txt",
        "text/csv" => "csv",
        "image/jpeg" => "jpg",
        "image/png" => "png",
        "image/gif" => "gif",
        "image/webp" => "webp",
        "video/mp4" => "mp4",
        "video/webm" => "webm",
        "video/x-matroska" => "mkv",
        "audio/mpeg" => "mp3",
        "audio/mp4" => "m4a",
        "audio/ogg" => "ogg",
        _ => return None,
    })
}

/// Makes a name safe for Windows and macOS file systems.
pub fn sanitize(name: &str) -> String {
    let cleaned: String = name
        .chars()
        .map(|c| {
            if c.is_control() || matches!(c, '<' | '>' | ':' | '"' | '/' | '\\' | '|' | '?' | '*') {
                '_'
            } else {
                c
            }
        })
        .collect();
    let mut out = cleaned.trim().trim_end_matches(['.', ' ']).trim_start_matches('.').to_string();
    if out.is_empty() {
        return "download".into();
    }
    let stem_upper = out.split('.').next().unwrap_or_default().trim().to_ascii_uppercase();
    if RESERVED.contains(&stem_upper.as_str()) {
        out.insert(0, '_');
    }
    if out.chars().count() > MAX_NAME_CHARS {
        let (stem, ext) = split_ext(&out);
        let ext = ext.map(|e| format!(".{e}")).unwrap_or_default();
        let keep = MAX_NAME_CHARS.saturating_sub(ext.chars().count()).max(1);
        out = stem.chars().take(keep).collect::<String>() + &ext;
    }
    out
}

/// Picks the name: header, then the browser's hint, then the URL; adds an extension from the
/// MIME type when none is present.
pub fn choose(
    content_disposition: Option<&str>,
    hint: Option<&str>,
    url: &str,
    mime: Option<&str>,
) -> String {
    let raw = content_disposition
        .and_then(from_content_disposition)
        .or_else(|| hint.map(str::trim).filter(|h| !h.is_empty()).map(String::from))
        .or_else(|| from_url(url))
        .unwrap_or_else(|| "download".into());
    let mut name = sanitize(&raw);
    if split_ext(&name).1.is_none()
        && let Some(ext) = mime.and_then(ext_for_mime)
    {
        name = format!("{name}.{ext}");
    }
    name
}

/// `("archive.tar", Some("gz"))` for `archive.tar.gz`; no extension for `.hidden` or `name`.
pub fn split_ext(name: &str) -> (&str, Option<&str>) {
    match name.rfind('.') {
        Some(i) if i > 0 && i + 1 < name.len() => (&name[..i], Some(&name[i + 1..])),
        _ => (name, None),
    }
}

/// The category folder for a file, from its extension.
pub fn category(name: &str) -> &'static str {
    let ext = split_ext(name).1.unwrap_or_default().to_ascii_lowercase();
    match ext.as_str() {
        "mp4" | "mkv" | "webm" | "avi" | "mov" | "wmv" | "flv" | "m4v" | "ts" | "mpg" | "mpeg" | "3gp" => {
            "Video"
        }
        "mp3" | "m4a" | "aac" | "flac" | "wav" | "ogg" | "opus" | "wma" => "Music",
        "pdf" | "doc" | "docx" | "xls" | "xlsx" | "ppt" | "pptx" | "txt" | "rtf" | "odt" | "ods" | "odp"
        | "epub" | "csv" | "md" => "Documents",
        "exe" | "msi" | "msix" | "appx" | "apk" | "dmg" | "pkg" | "deb" | "rpm" | "appimage" => "Programs",
        "zip" | "rar" | "7z" | "tar" | "gz" | "bz2" | "xz" | "zst" | "iso" | "tgz" => "Archives",
        _ => "Other",
    }
}

/// The in-progress file for a final path: `name.ext.part`.
pub fn part_path(final_path: &Path) -> PathBuf {
    let mut s = final_path.as_os_str().to_owned();
    s.push(".part");
    PathBuf::from(s)
}

/// `dir/name`, or `dir/name (1).ext`, `(2)` and so on when the file or its `.part` exists.
pub fn unique_path(dir: &Path, name: &str) -> PathBuf {
    let first = dir.join(name);
    if !first.exists() && !part_path(&first).exists() {
        return first;
    }
    let (stem, ext) = split_ext(name);
    for i in 1.. {
        let candidate = match ext {
            Some(ext) => dir.join(format!("{stem} ({i}).{ext}")),
            None => dir.join(format!("{stem} ({i})")),
        };
        if !candidate.exists() && !part_path(&candidate).exists() {
            return candidate;
        }
    }
    unreachable!()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn content_disposition_forms() {
        assert_eq!(
            from_content_disposition("attachment; filename=\"report.pdf\"").as_deref(),
            Some("report.pdf")
        );
        assert_eq!(
            from_content_disposition(
                "attachment; filename=\"a.pdf\"; filename*=UTF-8''%E2%82%AC%20rates.pdf"
            )
            .as_deref(),
            Some("€ rates.pdf")
        );
        assert_eq!(from_content_disposition("inline").as_deref(), None);
    }

    #[test]
    fn names_from_urls() {
        assert_eq!(from_url("https://e.com/files/My%20File.zip?x=1").as_deref(), Some("My File.zip"));
        assert_eq!(from_url("https://e.com/").as_deref(), None);
        assert_eq!(from_url("https://e.com").as_deref(), None);
    }

    #[test]
    fn sanitizing() {
        assert_eq!(sanitize("a/b:c?.txt"), "a_b_c_.txt");
        assert_eq!(sanitize("CON.txt"), "_CON.txt");
        assert_eq!(sanitize(" name. "), "name");
        assert_eq!(sanitize(""), "download");
        let long = format!("{}.zip", "x".repeat(300));
        let s = sanitize(&long);
        assert_eq!(s.chars().count(), MAX_NAME_CHARS);
        assert!(s.ends_with(".zip"));
    }

    #[test]
    fn choosing_adds_an_extension_from_the_mime_type() {
        assert_eq!(choose(None, None, "https://e.com/get?id=4", Some("application/pdf")), "get.pdf");
        assert_eq!(
            choose(Some("attachment; filename=x.bin"), Some("hint.zip"), "https://e.com/u", None),
            "x.bin"
        );
        assert_eq!(choose(None, Some("hint.zip"), "https://e.com/u", None), "hint.zip");
    }

    #[test]
    fn categories_and_unique_names() {
        assert_eq!(category("movie.MKV"), "Video");
        assert_eq!(category("setup.exe"), "Programs");
        assert_eq!(category("noext"), "Other");

        let dir = tempfile::tempdir().unwrap();
        assert_eq!(unique_path(dir.path(), "a.zip"), dir.path().join("a.zip"));
        std::fs::write(dir.path().join("a.zip"), b"").unwrap();
        std::fs::write(dir.path().join("a (1).zip.part"), b"").unwrap();
        assert_eq!(unique_path(dir.path(), "a.zip"), dir.path().join("a (2).zip"));
    }
}
