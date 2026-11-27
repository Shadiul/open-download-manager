import { YOUTUBE_ENABLED } from '../lib/build-flags.js';
import { pageDownloadUrl } from '../lib/sites.js';
import { formatDuration } from '../lib/cards.js';

const $ = (id) => document.getElementById(id);
const send = (msg) => chrome.runtime.sendMessage(msg);
const el = (tag, cls, text) => Object.assign(document.createElement(tag), cls ? { className: cls } : {}, text != null ? { textContent: text } : {});

function formatSize(bytes) {
  if (!(bytes > 0)) return '';
  const units = ['B', 'KB', 'MB', 'GB'];
  let i = 0;
  while (bytes >= 1024 && i < units.length - 1) { bytes /= 1024; i++; }
  return `${bytes.toFixed(i > 1 ? 1 : 0)} ${units[i]}`;
}

const KIND_LABEL = { page: 'Video post', hls: 'HLS stream', dash: 'DASH stream', video: 'Video file', audio: 'Audio' };

function matches(host, pattern) {
  const p = pattern.trim().toLowerCase().replace(/^\*\./, '').replace(/^www\./, '');
  return !!p && (host === p || host.endsWith('.' + p));
}

const playIcon = () => {
  const ns = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(ns, 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('width', '26');
  svg.setAttribute('height', '26');
  const path = document.createElementNS(ns, 'path');
  path.setAttribute('d', 'M8 5.5v13l11-6.5z');
  path.setAttribute('fill', 'currentColor');
  svg.append(path);
  return svg;
};

function cardView(card, tab) {
  const li = el('li', 'card');

  const thumb = el('div', 'thumb');
  if (card.poster) {
    const img = el('img');
    img.src = card.poster;
    img.alt = '';
    img.referrerPolicy = 'no-referrer';
    img.onerror = () => { img.remove(); thumb.prepend(playIcon()); };
    thumb.append(img);
  } else {
    thumb.append(playIcon());
  }
  const d = formatDuration(card.duration);
  if (d) thumb.append(el('span', 'duration', d));

  const info = el('div', 'info');
  const title = el('div', 'title', card.title || 'Untitled video');
  title.title = card.title || card.item.url;
  const meta = el('div', 'meta');
  if (card.quality) meta.append(el('span', 'chip strong', `${card.quality}p`));
  meta.append(el('span', 'chip', KIND_LABEL[card.kind] || card.kind));
  if (card.size > 0) meta.append(el('span', 'chip', formatSize(card.size)));
  if (card.host) meta.append(el('span', 'host', card.host));

  const actions = el('div', 'actions');
  const download = el('button', 'primary', 'Download');
  download.title = card.kind === 'page' ? 'Best quality' : card.item.url;
  actions.append(download);

  const status = (text, ok) => {
    download.textContent = text;
    download.disabled = ok;
    if (!ok) setTimeout(() => { download.textContent = 'Download'; download.disabled = false; }, 2500);
  };

  async function sendItem(item) {
    download.disabled = true;
    download.textContent = 'Sending…';
    const r = await send({ type: 'send-media', tabId: tab.id, item, videoId: card.id, title: card.title, source: 'media' });
    if (r?.ok) status('Sent ✓', true);
    else { status('Retry', false); showError(r?.error || 'Could not reach Open Download Manager'); }
  }
  download.addEventListener('click', () => sendItem(card.item));

  if (card.kind === 'page') {
    const pick = el('button', 'secondary', 'Quality');
    pick.title = 'Choose a quality';
    actions.append(pick);
    let list = null;
    pick.addEventListener('click', async () => {
      if (list) { list.remove(); list = null; return; }
      pick.disabled = true;
      pick.textContent = 'Loading…';
      const r = await send({ type: 'probe-page', url: card.item.url });
      pick.disabled = false;
      pick.textContent = 'Quality';
      if (!r?.ok || !r.formats?.length) { showError(r?.error ? `Could not list the qualities: ${r.error}` : 'Could not list the qualities'); return; }
      list = el('div', 'qualities');
      for (const f of r.formats) {
        const row = el('button', 'quality');
        row.append(el('span', '', f.label || f.id), el('span', '', [f.ext, formatSize(f.size)].filter(Boolean).join(' · ')));
        row.addEventListener('click', () => sendItem({ kind: 'page', url: r.url, formatId: f.id, size: f.size > 0 ? f.size : -1 }));
        list.append(row);
      }
      li.append(list);
    });
  }

  info.append(title, meta, actions);
  li.append(thumb, info);
  return li;
}

function showError(text) {
  $('error').hidden = false;
  $('error').textContent = text;
}

async function init() {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });

  // ----- quick settings -----
  let settings = await send({ type: 'get-settings' });
  const save = async (patch) => { settings = await send({ type: 'set-settings', settings: patch }); };
  $('enabled').checked = settings.enabled;
  $('enabled').addEventListener('change', (e) => save({ enabled: e.target.checked }));
  $('showVideoButton').checked = settings.showVideoButton !== false;
  $('showVideoButton').addEventListener('change', (e) => save({ showVideoButton: e.target.checked }));
  $('options').addEventListener('click', () => chrome.runtime.openOptionsPage());

  const host = (() => {
    try { const u = new URL(tab?.url || ''); return /^https?:$/.test(u.protocol) ? u.hostname.replace(/^www\./, '') : ''; } catch { return ''; }
  })();
  if (host) {
    $('siteRow').hidden = false;
    $('siteName').textContent = `On ${host}`;
    $('siteOn').checked = !(settings.excludedHosts || []).some((p) => matches(host, p));
    $('siteOn').addEventListener('change', (e) => {
      const others = (settings.excludedHosts || []).filter((h) => !matches(host, h));
      save({ excludedHosts: e.target.checked ? others : [...others, host] });
    });
  }

  // ----- app status -----
  send({ type: 'ping-host' }).then((r) => {
    const pill = $('host');
    pill.textContent = !r.ok ? 'App not found' : r.running ? 'Connected' : 'Ready';
    pill.className = `pill ${!r.ok ? 'bad' : r.running ? 'ok' : ''}`;
    pill.title = !r.ok ? r.error || '' : r.running ? 'Open Download Manager is running' : 'The app starts with the first download';
  });

  // ----- the videos on this page -----
  const cards = tab ? await send({ type: 'get-tab-cards', tabId: tab.id }) : [];
  $('count').textContent = cards.length ? String(cards.length) : '';
  const list = $('cards');
  for (const card of cards) list.append(cardView(card, tab));
  $('empty').hidden = cards.length > 0;
  if (!YOUTUBE_ENABLED && tab && pageDownloadUrl(tab.url)) {
    $('empty').hidden = false;
    $('emptyTitle').textContent = 'YouTube is not available in this version';
    $('emptyText').textContent = 'Copy this page\'s link: Open Download Manager offers to download it.';
  }
}

init();
