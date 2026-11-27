import { classifyMedia, totalFromContentRange, mediaKey, filenameFromUrl, parseHls } from './lib/media.js';
import { shouldCatch, DEFAULT_SETTINGS } from './lib/rules.js';
import { sendToHost, pingHost } from './lib/native.js';
import { pickFileLinks } from './lib/links.js';
import { pageDownloadUrl as anyPageUrl, isSplitStream, postUrl } from './lib/sites.js';
import { buildCards } from './lib/cards.js';
import { YOUTUBE_ENABLED } from './lib/build-flags.js';

// In the Chrome build YouTube links are not handled; users paste them into the desktop app.
const pageDownloadUrl = (url) => (YOUTUBE_ENABLED ? anyPageUrl(url) : null);
const YOUTUBE_OFF_MESSAGE = 'YouTube downloads are not available in the Chrome version. Copy the video link and paste it into Open Download Manager.';

const MAX_MEDIA_PER_TAB = 50;

// ---------- settings ----------

async function getSettings() {
  const { settings } = await chrome.storage.sync.get('settings');
  return { ...DEFAULT_SETTINGS, ...(settings || {}) };
}

// ---------- building a job for the download manager ----------

async function cookieHeader(url) {
  try {
    const cookies = await chrome.cookies.getAll({ url });
    return cookies.map((c) => `${c.name}=${c.value}`).join('; ');
  } catch {
    return '';
  }
}

/** Everything the download manager needs to fetch the file the same way the browser would. */
async function buildJob({ url, filename, referrer, mime, size, source, mediaKind, tabId, pageTitle, formatId }) {
  return {
    type: 'download',
    url,
    filename: filename || filenameFromUrl(url) || '',
    referrer: referrer || '',
    mime: mime || '',
    fileSize: Number.isFinite(size) ? size : -1,
    cookies: await cookieHeader(url),
    userAgent: navigator.userAgent,
    source, // 'browser-download' | 'context-menu' | 'media' | 'video-button'
    mediaKind: mediaKind || null, // 'hls' | 'dash' | 'video' | 'audio' | 'page' | null
    formatId: formatId || null, // for 'page': a format id from the probe reply, null for best
    pageTitle: pageTitle || '',
    tabId: tabId ?? -1
  };
}

// Matches how long the app keeps a video's page details for starting its download quickly.
const PROBE_CACHE_MS = 20 * 60 * 1000;
const probing = new Map();

async function handOff(jobInput) {
  // A YouTube link (page, short, embed, youtu.be) goes over as a page for the app to extract.
  if (!YOUTUBE_ENABLED && anyPageUrl(jobInput.url)) throw new Error(YOUTUBE_OFF_MESSAGE);
  const page = pageDownloadUrl(jobInput.url);
  // The size stays when the quality menu gave one for the picked format.
  if (page) jobInput = { ...jobInput, url: page, mediaKind: 'page', filename: '', mime: '', size: jobInput.formatId ? jobInput.size : -1 };
  const job = await buildJob(jobInput);
  const reply = await sendToHost(job);
  if (!reply.ok) throw new Error(reply.error || 'Open Download Manager refused the download');
  return reply;
}

// ---------- catching browser downloads ----------

// Downloads we let go back to the browser, so we don't catch them twice.
const released = new Set();
// Alt+click on a link leaves that download to the browser.
let skipUntil = 0;

chrome.downloads.onCreated.addListener(async (item) => {
  if (released.has(item.id)) return;
  if (Date.now() < skipUntil) {
    skipUntil = 0;
    return;
  }
  const settings = await getSettings();
  const decision = shouldCatch(item, settings, chrome.runtime.id);
  if (!decision.catch) return;

  // Hold the browser's copy while we ask the download manager.
  try { await chrome.downloads.pause(item.id); } catch { /* may already be finished */ }

  try {
    await handOff({
      url: item.finalUrl || item.url,
      filename: item.filename ? item.filename.split(/[\\/]/).pop() : '',
      referrer: item.referrer,
      mime: item.mime,
      size: item.totalBytes > 0 ? item.totalBytes : item.fileSize,
      source: 'browser-download'
    });
    await chrome.downloads.cancel(item.id).catch(() => {});
    await chrome.downloads.erase({ id: item.id }).catch(() => {});
    flashBadge('✓', '#1f8a3b');
  } catch (e) {
    // The download manager is not installed or not running: let the browser finish the download.
    console.warn('[odm] handing off failed, browser keeps the download:', e.message);
    released.add(item.id);
    try { await chrome.downloads.resume(item.id); } catch { /* ignore */ }
    flashBadge('!', '#c62828');
  }
});

// ---------- detecting video and audio streams ----------

const mediaStoreKey = (tabId) => `media:${tabId}`;
const videoStoreKey = (tabId) => `videos:${tabId}`;
const playingKey = (tabId) => `playing:${tabId}`;
// How long after a video starts playing the streams the page loads still count as its own.
const PLAYING_WINDOW_MS = 20000;

async function getTabVideos(tabId) {
  const key = videoStoreKey(tabId);
  return (await chrome.storage.session.get(key))[key] || [];
}

/** Stores the videos the page reported, newest details winning. */
function setTabVideos(tabId, items) {
  return withTabLock(tabId, async () => {
    const list = await getTabVideos(tabId);
    for (const v of items) {
      const i = list.findIndex((x) => x.videoId === v.videoId);
      if (i >= 0) list[i] = { ...list[i], ...v };
      else list.push(v);
    }
    while (list.length > 40) list.shift();
    await chrome.storage.session.set({ [videoStoreKey(tabId)]: list });
    await refreshBadge(tabId);
  });
}

async function playingVideo(tabId) {
  const key = playingKey(tabId);
  const p = (await chrome.storage.session.get(key))[key];
  return p && Date.now() - p.at < PLAYING_WINDOW_MS ? p.videoId : null;
}

/** The popup's cards for a tab: one per video, with its thumbnail and the best thing to fetch. */
async function tabCards(tabId) {
  const [videos, media] = await Promise.all([getTabVideos(tabId), getTabMedia(tabId)]);
  const tab = await chrome.tabs.get(tabId).catch(() => null);
  return buildCards(videos, media, tab?.title || '');
}

async function refreshBadge(tabId) {
  await updateBadge(tabId, (await tabCards(tabId)).length);
}

// HLS playlists are small; reading one tells a master (the whole video) from one quality's piece.
const hlsInfo = new Map();
async function inspectHls(url) {
  if (hlsInfo.has(url)) return hlsInfo.get(url);
  const info = fetch(url, { credentials: 'include' })
    .then((r) => (r.ok ? r.text() : ''))
    .then((t) => (t.startsWith('#EXTM3U') ? parseHls(t) : { master: null, height: null }))
    .catch(() => ({ master: null, height: null }));
  hlsInfo.set(url, info);
  if (hlsInfo.size > 500) hlsInfo.delete(hlsInfo.keys().next().value);
  return info;
}

async function getTabMedia(tabId) {
  const key = mediaStoreKey(tabId);
  const data = await chrome.storage.session.get(key);
  return data[key] || [];
}

// Serialise writes per tab so parallel responses don't overwrite each other.
const writeQueues = new Map();
function withTabLock(tabId, fn) {
  const prev = writeQueues.get(tabId) || Promise.resolve();
  const next = prev.then(fn, fn);
  writeQueues.set(tabId, next.catch(() => {}));
  return next;
}

function addTabMedia(tabId, entry) {
  return withTabLock(tabId, async () => {
    const list = await getTabMedia(tabId);
    const key = mediaKey(entry.url);
    const existing = list.find((m) => m.key === key);
    if (existing) {
      if (entry.size > existing.size) existing.size = entry.size;
      existing.videoId ??= entry.videoId;
      existing.seenAt = Date.now();
    } else {
      list.push({ ...entry, key, seenAt: Date.now() });
      if (list.length > MAX_MEDIA_PER_TAB) list.shift();
    }
    await chrome.storage.session.set({ [mediaStoreKey(tabId)]: list });
    await refreshBadge(tabId);
  });
}

async function clearTabMedia(tabId) {
  await chrome.storage.session.remove([mediaStoreKey(tabId), videoStoreKey(tabId), playingKey(tabId)]);
  await updateBadge(tabId, 0);
}

async function updateBadge(tabId, count) {
  try {
    await chrome.action.setBadgeBackgroundColor({ tabId, color: '#1f6feb' });
    await chrome.action.setBadgeText({ tabId, text: count ? String(count) : '' });
  } catch { /* tab may be gone */ }
}

function flashBadge(text, color) {
  chrome.action.setBadgeBackgroundColor({ color });
  chrome.action.setBadgeText({ text });
  setTimeout(() => chrome.action.setBadgeText({ text: '' }), 2500);
}

function header(headers, name) {
  const h = (headers || []).find((x) => x.name.toLowerCase() === name);
  return h ? h.value : undefined;
}

chrome.webRequest.onHeadersReceived.addListener(
  (details) => {
    if (details.tabId < 0 || isSplitStream(details.url)) return;
    let size = Number(header(details.responseHeaders, 'content-length'));
    if (details.statusCode === 206) size = totalFromContentRange(header(details.responseHeaders, 'content-range'));
    const media = classifyMedia({
      url: details.url,
      contentType: header(details.responseHeaders, 'content-type'),
      contentLength: Number.isFinite(size) ? size : -1,
      statusCode: details.statusCode
    });
    if (!media) return;
    const tabId = details.tabId;
    (async () => {
      const entry = { ...media, origin: 'network', referrer: details.initiator || '', videoId: (await playingVideo(tabId)) || undefined };
      if (media.kind === 'hls') {
        const info = await inspectHls(details.url);
        entry.hlsMaster = info.master;
        entry.height = info.height;
      }
      await addTabMedia(tabId, entry);
    })();
  },
  { urls: ['http://*/*', 'https://*/*'], types: ['media', 'xmlhttprequest', 'other', 'object'] },
  ['responseHeaders']
);

chrome.tabs.onUpdated.addListener((tabId, changeInfo) => {
  // A new page in the tab: forget the old page's streams.
  if (changeInfo.status === 'loading' && changeInfo.url) clearTabMedia(tabId);
});
chrome.tabs.onRemoved.addListener((tabId) => {
  chrome.storage.session.remove([mediaStoreKey(tabId), videoStoreKey(tabId), playingKey(tabId)]);
});

// ---------- right-click menu ----------

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.create({
    id: 'dl-link',
    title: 'Download with Open Download Manager',
    contexts: ['link', 'video', 'audio', 'image']
  });
  chrome.contextMenus.create({
    id: 'dl-all',
    title: 'Download all links with Open Download Manager',
    contexts: ['page']
  });
});

/** Sends every file link on the page to the app in one batch. */
async function downloadAllLinks(tab) {
  let links = [];
  try {
    links = await chrome.tabs.sendMessage(tab.id, { type: 'collect-links' }, { frameId: 0 });
  } catch {
    flashBadge('!', '#c62828'); // no content script on this page (browser pages, the Web Store)
    return;
  }
  const settings = await getSettings();
  const files = pickFileLinks(links || [], settings.extensions);
  if (files.length === 0) {
    flashBadge('0', '#757575');
    return;
  }
  try {
    const items = [];
    for (const f of files) {
      const job = await buildJob({ url: f.url, referrer: tab.url, source: 'context-menu', tabId: tab.id, pageTitle: tab.title });
      delete job.type;
      items.push(job);
    }
    const reply = await sendToHost({ type: 'batch', items });
    if (!reply.ok) throw new Error(reply.error || 'refused');
    flashBadge(String(files.length), '#1f8a3b');
  } catch (e) {
    console.warn('[odm] download all links failed:', e.message);
    flashBadge('!', '#c62828');
  }
}

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  if (info.menuItemId === 'dl-all') return downloadAllLinks(tab);
  const url = info.srcUrl && !info.srcUrl.startsWith('blob:') ? info.srcUrl : info.linkUrl;
  if (!url || !/^https?:/i.test(url)) {
    flashBadge('!', '#c62828');
    return;
  }
  try {
    await handOff({ url, referrer: info.pageUrl, source: 'context-menu', tabId: tab?.id, pageTitle: tab?.title });
    flashBadge('✓', '#1f8a3b');
  } catch (e) {
    console.warn('[odm] context menu hand-off failed:', e.message);
    flashBadge('!', '#c62828');
  }
});

// ---------- messages from the popup and the page ----------

/**
 * The stream to send for one <video> that plays from a blob: address: the best one the page
 * loaded for that video. Never another video's stream: guessing picks the wrong video.
 */
function bestStreamFor(list, videoId) {
  const rank = { hls: 3, dash: 3, video: 2, audio: 1 };
  const mine = list.filter((m) => m.videoId === videoId && !(m.kind === 'hls' && m.hlsMaster === false
    && list.some((o) => o.videoId === videoId && o.kind === 'hls' && o.hlsMaster)));
  return mine.sort((a, b) => (rank[b.kind] - rank[a.kind]) || ((b.height || 0) - (a.height || 0)) || (b.seenAt - a.seenAt))[0] || null;
}

async function handleMessage(msg, sender) {
  switch (msg.type) {
    case 'ping-host':
      return pingHost();

    case 'skip-next-download':
      skipUntil = Date.now() + 5000;
      return { ok: true };

    case 'get-settings':
      return getSettings();

    case 'set-settings': {
      const settings = { ...(await getSettings()), ...msg.settings };
      await chrome.storage.sync.set({ settings });
      return settings;
    }

    case 'get-tab-cards': {
      const cards = await tabCards(msg.tabId);
      const tab = await chrome.tabs.get(msg.tabId).catch(() => null);
      // A YouTube page is one video even before its player reports in.
      const page = tab && pageDownloadUrl(tab.url);
      if (page && !cards.some((c) => c.item.url === page)) {
        cards.unshift({ id: page, title: tab.title, poster: '', duration: 0, quality: 0, kind: 'page', size: -1, host: 'youtube.com', pageUrl: page, item: { kind: 'page', url: page } });
      }
      return cards;
    }

    case 'page-videos': {
      const tabId = sender.tab?.id;
      if (tabId == null) return { ok: false };
      await setTabVideos(tabId, msg.items || []);
      return { ok: true };
    }

    case 'video-playing': {
      const tabId = sender.tab?.id;
      if (tabId == null) return { ok: false };
      await chrome.storage.session.set({ [playingKey(tabId)]: { videoId: msg.videoId, at: Date.now() } });
      return { ok: true };
    }

    case 'probe-page': {
      // Asks the desktop app which qualities a YouTube video has, for the menu near the player.
      const url = pageDownloadUrl(msg.url) || postUrl(msg.url);
      if (!url) return { ok: false, error: anyPageUrl(msg.url) && !YOUTUBE_ENABLED ? YOUTUBE_OFF_MESSAGE : 'Not a supported video page' };
      // A video's qualities rarely change: keep them for a while so the menu opens at once.
      const key = `probe:${url}`;
      const cached = (await chrome.storage.session.get(key))[key];
      if (cached && Date.now() - cached.at < PROBE_CACHE_MS) return cached.reply;
      // One request per video at a time: an early fetch (popup opened, pointer on the button) and
      // the click share it.
      if (!probing.has(url)) {
        probing.set(url, (async () => {
          try {
            const reply = await sendToHost({ type: 'probe', url, cookies: await cookieHeader(url), userAgent: navigator.userAgent }, 30000);
            if (!reply.ok) return { ok: false, url, error: reply.error };
            const result = { ok: true, url, title: reply.title || '', formats: reply.formats || [] };
            await chrome.storage.session.set({ [key]: { at: Date.now(), reply: result } });
            return result;
          } catch (e) {
            return { ok: false, url, error: e.message };
          } finally {
            probing.delete(url);
          }
        })());
      }
      return probing.get(url);
    }

    case 'send-media': {
      // From the popup list or the video button on the page.
      const tabId = msg.tabId ?? sender.tab?.id;
      const tab = tabId != null ? await chrome.tabs.get(tabId).catch(() => null) : null;
      let item = msg.item;
      const page = pageDownloadUrl(msg.pageUrl || '') || pageDownloadUrl(tab?.url || '');
      if ((!item || !/^https?:/i.test(item.url)) && page) item = { kind: 'page', url: page };
      if ((!item || !/^https?:/i.test(item.url)) && msg.videoId) item = bestStreamFor(await getTabMedia(tabId), msg.videoId);
      if (!item) return { ok: false, error: 'This video\'s stream has not loaded yet. Play it for a few seconds and try again.' };
      try {
        await handOff({
          url: item.url,
          referrer: item.referrer || tab?.url || '',
          mime: item.mime,
          size: item.size,
          source: msg.source || 'media',
          mediaKind: item.kind,
          formatId: item.formatId,
          tabId,
          pageTitle: msg.title || tab?.title
        });
        return { ok: true };
      } catch (e) {
        return { ok: false, error: e.message };
      }
    }

    default:
      return { ok: false, error: `Unknown message ${msg.type}` };
  }
}

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  handleMessage(msg, sender).then(sendResponse, (e) => sendResponse({ ok: false, error: e.message }));
  return true; // keep the channel open for the async reply
});
