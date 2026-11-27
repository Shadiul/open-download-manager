import { YOUTUBE_ENABLED } from '../lib/build-flags.js';
import { pageDownloadUrl } from '../lib/sites.js';
import { formatDuration } from '../lib/cards.js';

const $ = (id) => document.getElementById(id);
const send = (msg) => chrome.runtime.sendMessage(msg);
const el = (tag, cls, text) => {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text != null) e.textContent = text;
  return e;
};

// Material Symbols, as Chrome uses.
const ICONS = {
  download: 'M480-320 280-520l56-58 104 104v-326h80v326l104-104 56 58-200 200ZM240-160q-33 0-56.5-23.5T160-240v-120h80v120h480v-120h80v120q0 33-23.5 56.5T720-160H240Z',
  more: 'M480-345 240-585l56-56 184 184 184-184 56 56-240 240Z',
  check: 'M382-240 154-468l57-57 171 171 367-367 57 57-424 424Z',
  play: 'M320-200v-560l440 280-440 280Z'
};
function icon(name, size = 20) {
  const ns = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(ns, 'svg');
  svg.setAttribute('viewBox', '0 -960 960 960');
  svg.setAttribute('width', size);
  svg.setAttribute('height', size);
  svg.setAttribute('aria-hidden', 'true');
  const p = document.createElementNS(ns, 'path');
  p.setAttribute('d', ICONS[name]);
  p.setAttribute('fill', 'currentColor');
  svg.append(p);
  return svg;
}
function iconButton(name, label, tonal = false) {
  const b = el('button', `icon-button${tonal ? ' tonal' : ''}`);
  b.title = label;
  b.setAttribute('aria-label', label);
  b.append(icon(name));
  return b;
}

function formatSize(bytes) {
  if (!(bytes > 0)) return '';
  const units = ['B', 'KB', 'MB', 'GB'];
  let i = 0;
  while (bytes >= 1024 && i < units.length - 1) { bytes /= 1024; i++; }
  return `${bytes.toFixed(i > 1 ? 1 : 0)} ${units[i]}`;
}

const KIND = { page: 'Video post', hls: 'HLS stream', dash: 'DASH stream', video: 'Video file', audio: 'Audio' };

function matches(host, pattern) {
  const p = pattern.trim().toLowerCase().replace(/^\*\./, '').replace(/^www\./, '');
  return !!p && (host === p || host.endsWith('.' + p));
}

let openMenu = null;
function closeMenu() {
  openMenu?.remove();
  openMenu = null;
}
document.addEventListener('click', (e) => { if (openMenu && !openMenu.contains(e.target)) closeMenu(); });
document.addEventListener('keydown', (e) => { if (e.key === 'Escape') closeMenu(); });

function row(card, tab) {
  const li = el('li', 'item');

  const thumb = el('div', 'thumb');
  if (card.poster) {
    const img = el('img');
    img.src = card.poster;
    img.alt = '';
    img.referrerPolicy = 'no-referrer';
    img.onerror = () => { img.remove(); thumb.prepend(icon('play', 24)); };
    thumb.append(img);
  } else {
    thumb.append(icon('play', 24));
  }
  const d = formatDuration(card.duration);
  if (d) thumb.append(el('span', 'duration', d));

  const text = el('div', 'text');
  const title = el('div', 'title', card.title || 'Untitled video');
  title.title = card.title || card.item.url;
  const details = [card.quality ? `${card.quality}p` : '', KIND[card.kind] || card.kind, formatSize(card.size), card.host].filter(Boolean).join(' · ');
  const meta = el('div', 'meta', details);
  text.append(title, meta);

  const actions = el('div', 'actions');
  const download = iconButton('download', card.kind === 'page' ? 'Download (best quality)' : 'Download', true);
  actions.append(download);

  async function sendItem(item) {
    closeMenu();
    download.disabled = true;
    const r = await send({ type: 'send-media', tabId: tab.id, item, videoId: card.id, title: card.title, source: 'media' });
    if (r?.ok) {
      download.replaceChildren(icon('check'));
      download.title = 'Sent';
      meta.textContent = 'Sent to Open Download Manager';
      meta.className = 'meta done';
    } else {
      download.disabled = false;
      meta.textContent = r?.error || 'Could not reach Open Download Manager';
      meta.className = 'meta failed';
      meta.title = meta.textContent;
    }
  }
  download.addEventListener('click', () => sendItem(card.item));

  if (card.kind === 'page') {
    const more = iconButton('more', 'Choose a quality');
    actions.append(more);
    more.addEventListener('click', async (e) => {
      e.stopPropagation();
      if (openMenu?.dataset.for === card.id) { closeMenu(); return; }
      closeMenu();
      const menu = el('div', 'menu');
      menu.dataset.for = card.id;
      menu.append(el('div', 'menu-title', 'Getting qualities…'));
      document.body.append(menu);
      openMenu = menu;
      // Below the button, or above it when the popup has no room underneath.
      const place = () => {
        const r = more.getBoundingClientRect();
        menu.style.right = `${Math.max(8, innerWidth - r.right)}px`;
        const below = r.bottom + 4;
        menu.style.top = below + menu.offsetHeight <= innerHeight - 4 ? `${below}px` : `${Math.max(4, r.top - 4 - menu.offsetHeight)}px`;
      };
      place();
      const r = await send({ type: 'probe-page', url: card.item.url });
      if (openMenu !== menu) return;
      if (!r?.ok || !r.formats?.length) {
        menu.replaceChildren(el('div', 'menu-title', r?.error ? `Could not list the qualities: ${r.error}` : 'Could not list the qualities'));
        return;
      }
      menu.replaceChildren(el('div', 'menu-title', 'Quality'));
      queueMicrotask(place);
      for (const f of r.formats) {
        const b = el('button');
        b.append(el('span', '', f.label || f.id), el('span', '', [f.ext, formatSize(f.size)].filter(Boolean).join(' · ')));
        b.addEventListener('click', () => sendItem({ kind: 'page', url: r.url, formatId: f.id, size: f.size > 0 ? f.size : -1 }));
        menu.append(b);
      }
      place();
    });
  }

  li.append(thumb, text, actions);
  return li;
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
    $('siteName').textContent = `Use on ${host}`;
    $('siteOn').checked = !(settings.excludedHosts || []).some((p) => matches(host, p));
    $('siteOn').addEventListener('change', (e) => {
      const others = (settings.excludedHosts || []).filter((h) => !matches(host, h));
      save({ excludedHosts: e.target.checked ? others : [...others, host] });
    });
  }

  // ----- app status -----
  send({ type: 'ping-host' }).then((r) => {
    const s = $('host');
    s.textContent = !r.ok ? 'App not found' : r.running ? 'Connected' : 'Ready';
    s.className = `status ${!r.ok ? 'bad' : r.running ? 'ok' : ''}`;
    s.title = !r.ok ? r.error || '' : r.running ? 'Open Download Manager is running' : 'The app starts with the first download';
  });

  // ----- videos on this page -----
  const cards = tab ? await send({ type: 'get-tab-cards', tabId: tab.id }) : [];
  $('count').textContent = cards.length ? String(cards.length) : '';
  for (const card of cards) $('cards').append(row(card, tab));
  $('empty').hidden = cards.length > 0;
  if (!YOUTUBE_ENABLED && tab && pageDownloadUrl(tab.url)) {
    $('empty').hidden = false;
    $('emptyTitle').textContent = 'YouTube is not available in this version';
    $('emptyText').textContent = 'Copy the page link: Open Download Manager offers to download it.';
  }
}

init();
