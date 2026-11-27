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

// Sites where each post has its own page that yt-dlp can extract. On these the video button
// sends the post's link, which names exactly one video, instead of a guessed stream.
const POST_PATTERNS = [
  // x.com and twitter.com: /<user>/status/<id>, without /photo/1, /video/1 or a query
  { hosts: ['x.com', 'twitter.com', 'mobile.twitter.com'], re: /^\/([A-Za-z0-9_]{1,15})\/status\/(\d+)/, url: (m) => `https://x.com/${m[1]}/status/${m[2]}` },
  { hosts: ['reddit.com', 'www.reddit.com', 'old.reddit.com', 'new.reddit.com'], re: /^\/r\/([^/]+)\/comments\/([a-z0-9]+)/i, url: (m) => `https://www.reddit.com/r/${m[1]}/comments/${m[2]}/` },
  { hosts: ['instagram.com', 'www.instagram.com'], re: /^\/(?:[^/]+\/)?(p|reel|reels)\/([A-Za-z0-9_-]+)/, url: (m) => `https://www.instagram.com/${m[1] === 'p' ? 'p' : 'reel'}/${m[2]}/` },
  { hosts: ['tiktok.com', 'www.tiktok.com'], re: /^\/@([^/]+)\/video\/(\d+)/, url: (m) => `https://www.tiktok.com/@${m[1]}/video/${m[2]}` },
  { hosts: ['vimeo.com', 'www.vimeo.com'], re: /^\/(\d+)(?:\/|$)/, url: (m) => `https://vimeo.com/${m[1]}` }
];

/** The canonical link of one post on a site yt-dlp extracts by post, or null. */
export function postUrl(url) {
  let u;
  try { u = new URL(url); } catch { return null; }
  const host = u.hostname.toLowerCase();
  for (const p of POST_PATTERNS) {
    if (!p.hosts.includes(host)) continue;
    const m = p.re.exec(u.pathname);
    return m ? p.url(m) : null;
  }
  return null;
}

/** Whether the site is one where videos are fetched by their post's link. */
export function isPostSite(hostname) {
  const host = (hostname || '').toLowerCase();
  return POST_PATTERNS.some((p) => p.hosts.includes(host));
}
