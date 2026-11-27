// Sites where the browser only ever sees split, scrambled stream pieces. For these the extension
// sends the page link and the desktop app extracts the video itself (with yt-dlp).

const YT_HOSTS = ['youtube.com', 'www.youtube.com', 'm.youtube.com', 'music.youtube.com', 'youtube-nocookie.com', 'www.youtube-nocookie.com'];
const ID_RE = /^[A-Za-z0-9_-]{11}$/;

/** Returns the canonical video page link for a supported site, or null. */
export function pageDownloadUrl(url) {
  let u;
  try { u = new URL(url); } catch { return null; }
  const host = u.hostname.toLowerCase();

  if (host === 'youtu.be') {
    const id = u.pathname.slice(1).split('/')[0];
    return ID_RE.test(id) ? `https://www.youtube.com/watch?v=${id}` : null;
  }
  if (!YT_HOSTS.includes(host)) return null;

  const base = host === 'music.youtube.com' ? 'https://music.youtube.com' : 'https://www.youtube.com';
  const v = u.searchParams.get('v');
  if (u.pathname === '/watch' && v && ID_RE.test(v)) return `${base}/watch?v=${v}`;
  const m = /^\/(shorts|embed|live|v)\/([A-Za-z0-9_-]{11})/.exec(u.pathname);
  if (m) return m[1] === 'shorts' ? `https://www.youtube.com/shorts/${m[2]}` : `https://www.youtube.com/watch?v=${m[2]}`;
  return null;
}

/** Stream pieces from these hosts are useless on their own, so they are not listed. */
export function isSplitStream(url) {
  try {
    const host = new URL(url).hostname.toLowerCase();
    return host === 'googlevideo.com' || host.endsWith('.googlevideo.com');
  } catch {
    return false;
  }
}
