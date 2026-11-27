// Turns what was seen on a page (its <video> elements and the streams the page loaded) into one
// card per video for the popup: thumbnail, title, and the best thing to download.

const KIND_RANK = { page: 5, hls: 4, dash: 4, video: 3, audio: 1 };

/** True for HLS media playlists (one quality) when a master playlist covers them. */
function isCoveredVariant(stream, all) {
  return stream.kind === 'hls' && stream.hlsMaster === false
    && all.some((s) => s !== stream && s.kind === 'hls' && s.hlsMaster && s.videoId === stream.videoId);
}

function bestOf(streams) {
  return [...streams].sort((a, b) =>
    (KIND_RANK[b.kind] - KIND_RANK[a.kind]) || ((b.height || 0) - (a.height || 0)) || ((b.size || 0) - (a.size || 0)) || (b.seenAt - a.seenAt))[0] || null;
}

/**
 * @param {Array<{videoId, title?, poster?, duration?, pageUrl?, src?, width?, height?}>} videos
 * @param {Array<{kind, url, size?, mime?, videoId?, hlsMaster?, height?, seenAt?, title?}>} streams
 * @param {string} pageTitle
 * @returns {Array<{id, title, poster, duration, quality, kind, size, host, pageUrl, item}>}
 */
export function buildCards(videos, streams, pageTitle = '') {
  const offered = streams.filter((s) => !isCoveredVariant(s, streams));
  const cards = [];
  const used = new Set();

  for (const v of videos) {
    const mine = offered.filter((s) => s.videoId === v.videoId);
    mine.forEach((s) => used.add(s));
    if (v.src && /^https?:/i.test(v.src) && !mine.some((s) => s.url === v.src)) {
      mine.push({ kind: 'video', url: v.src, size: -1, seenAt: 0 });
    }
    // A post link wins: yt-dlp gets exactly this video and can offer every quality.
    const item = v.pageUrl ? { kind: 'page', url: v.pageUrl } : bestOf(mine);
    if (!item) continue;
    cards.push(card(v.videoId, {
      title: v.title || pageTitle,
      poster: v.poster || '',
      duration: v.duration || 0,
      quality: item.height || (v.height ? Math.min(v.height, v.width || v.height) : 0),
      item,
      pageUrl: v.pageUrl || ''
    }));
  }

  for (const s of offered) {
    if (used.has(s) || cards.some((c) => c.item.url === s.url)) continue;
    // Variant playlists without a master are still offered, but tiny audio-only ones are noise.
    cards.push(card(s.url, { title: s.title || nameFromUrl(s.url) || pageTitle, poster: '', duration: 0, quality: s.height || 0, item: s, pageUrl: '' }));
  }
  return cards;
}

function card(id, { title, poster, duration, quality, item, pageUrl }) {
  let host = '';
  try { host = new URL(pageUrl || item.url).hostname.replace(/^www\./, ''); } catch { /* keep empty */ }
  return { id, title, poster, duration, quality, kind: item.kind, size: item.size > 0 ? item.size : -1, host, pageUrl, item };
}

function nameFromUrl(url) {
  try { return decodeURIComponent(new URL(url).pathname.split('/').pop() || ''); } catch { return ''; }
}

export function formatDuration(seconds) {
  if (!(seconds > 0) || !Number.isFinite(seconds)) return '';
  const s = Math.round(seconds), h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), r = s % 60;
  const pad = (n) => String(n).padStart(2, '0');
  return h ? `${h}:${pad(m)}:${pad(r)}` : `${m}:${pad(r)}`;
}
