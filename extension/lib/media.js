// Decides whether a network response is a video or audio stream worth offering.
// Pure functions only, so they can be unit tested in Node.

const HLS_TYPES = ['application/vnd.apple.mpegurl', 'application/x-mpegurl', 'audio/mpegurl', 'audio/x-mpegurl'];
const DASH_TYPES = ['application/dash+xml'];
// Pieces of a stream. They are not useful on their own, so they are ignored.
const SEGMENT_TYPES = ['video/mp2t', 'video/iso.segment', 'audio/iso.segment'];
const SEGMENT_EXT = ['ts', 'm4s', 'm4f', 'cmfv', 'cmfa', 'aac'];

const VIDEO_EXT = ['mp4', 'webm', 'mkv', 'mov', 'avi', 'flv', 'm4v', 'wmv', '3gp', 'ogv'];
const AUDIO_EXT = ['mp3', 'm4a', 'ogg', 'oga', 'opus', 'wav', 'flac', 'weba'];

// Files under this size are almost always previews, ads or thumbnails.
export const MIN_PROGRESSIVE_BYTES = 512 * 1024;

export function extensionOf(url) {
  try {
    const path = new URL(url).pathname;
    const last = path.split('/').pop() || '';
    const dot = last.lastIndexOf('.');
    return dot > 0 ? last.slice(dot + 1).toLowerCase() : '';
  } catch {
    return '';
  }
}

export function filenameFromUrl(url) {
  try {
    const last = new URL(url).pathname.split('/').pop() || '';
    return decodeURIComponent(last);
  } catch {
    return '';
  }
}

function baseType(contentType) {
  return (contentType || '').split(';')[0].trim().toLowerCase();
}

/**
 * @param {{url: string, contentType?: string, contentLength?: number, statusCode?: number}} res
 * @returns {null | {kind: 'hls'|'dash'|'video'|'audio', url: string, mime: string, size: number}}
 */
export function classifyMedia(res) {
  const { url } = res;
  if (!/^https?:/i.test(url)) return null;
  if (res.statusCode && (res.statusCode < 200 || res.statusCode >= 300) && res.statusCode !== 206) return null;

  const mime = baseType(res.contentType);
  const ext = extensionOf(url);
  const size = Number.isFinite(res.contentLength) ? res.contentLength : -1;
  const out = (kind) => ({ kind, url, mime, size });

  if (HLS_TYPES.includes(mime) || ext === 'm3u8') return out('hls');
  if (DASH_TYPES.includes(mime) || ext === 'mpd') return out('dash');
  if (SEGMENT_TYPES.includes(mime) || SEGMENT_EXT.includes(ext)) return null;

  let kind = null;
  if (mime.startsWith('video/') || VIDEO_EXT.includes(ext)) kind = 'video';
  else if (mime.startsWith('audio/') || AUDIO_EXT.includes(ext)) kind = 'audio';
  if (!kind) return null;

  // A 206 response is a range of a bigger file; its Content-Length is the range, not the file.
  if (res.statusCode !== 206 && size >= 0 && size < MIN_PROGRESSIVE_BYTES) return null;
  return out(kind);
}

/** Reads the total file size from a Content-Range header such as "bytes 0-1023/52428800". */
export function totalFromContentRange(value) {
  const m = /\/(\d+)\s*$/.exec(value || '');
  return m ? Number(m[1]) : -1;
}

/**
 * Some sites (for example ones that stream with ?range= or &bytestart=) request one file in many
 * pieces. Strip those query parameters so the pieces collapse into one entry.
 */
export function mediaKey(url) {
  try {
    const u = new URL(url);
    for (const p of ['range', 'rn', 'rbuf', 'bytestart', 'byteend', 'start', 'end']) u.searchParams.delete(p);
    u.hash = '';
    return u.toString();
  } catch {
    return url;
  }
}

/**
 * Reads an HLS playlist. A master playlist lists the qualities of one video (#EXT-X-STREAM-INF);
 * a media playlist is one quality's segments. Only masters are worth offering.
 * @returns {{ master: boolean, height: number | null }}
 */
export function parseHls(text) {
  const master = /#EXT-X-STREAM-INF/i.test(text || '');
  let height = null;
  for (const m of (text || '').matchAll(/RESOLUTION=(\d+)x(\d+)/gi)) {
    const h = Math.min(Number(m[1]), Number(m[2]));
    if (!height || h > height) height = h;
  }
  return { master, height };
}
