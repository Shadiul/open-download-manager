import { YOUTUBE_ENABLED } from '../lib/build-flags.js';
import { pageDownloadUrl } from '../lib/sites.js';

const $ = (id) => document.getElementById(id);
const send = (msg) => chrome.runtime.sendMessage(msg);

function formatSize(bytes) {
  if (!(bytes > 0)) return '';
  const units = ['B', 'KB', 'MB', 'GB'];
  let i = 0;
  while (bytes >= 1024 && i < units.length - 1) { bytes /= 1024; i++; }
  return `${bytes.toFixed(i ? 1 : 0)} ${units[i]}`;
}

function displayName(url) {
  try {
    const u = new URL(url);
    return decodeURIComponent(u.pathname.split('/').pop() || u.hostname);
  } catch { return url; }
}

async function init() {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });

  const settings = await send({ type: 'get-settings' });
  $('enabled').checked = settings.enabled;
  $('enabled').addEventListener('change', (e) => send({ type: 'set-settings', settings: { enabled: e.target.checked } }));
  $('options').addEventListener('click', (e) => { e.preventDefault(); chrome.runtime.openOptionsPage(); });

  send({ type: 'ping-host' }).then((r) => {
    const el = $('host');
    el.textContent = !r.ok ? 'App not found' : r.running ? 'App connected' : 'App ready (starts with the first download)';
    el.className = `status ${r.ok ? 'ok' : 'bad'}`;
    if (!r.ok) el.title = r.error || '';
  });

  const media = tab ? await send({ type: 'get-tab-media', tabId: tab.id }) : [];
  $('empty').hidden = media.length > 0;
  if (!YOUTUBE_ENABLED && tab && pageDownloadUrl(tab.url)) {
    $('empty').hidden = false;
    $('empty').textContent = 'YouTube downloads are not available in the Chrome version. Copy this page\'s link and paste it into Open Download Manager.';
  }
  const list = $('media');
  for (const item of [...media].reverse()) {
    const li = document.createElement('li');
    li.className = 'item';
    li.innerHTML = '<span class="kind"></span><div class="meta"><div class="name"></div><div class="sub"></div></div><button>Download</button>';
    li.querySelector('.kind').textContent = item.kind;
    li.querySelector('.name').textContent = item.kind === 'page' ? (item.title || 'This video') : displayName(item.url);
    li.querySelector('.name').title = item.url;
    li.querySelector('.sub').textContent = item.kind === 'page' ? 'Best quality, the app picks the streams' : [formatSize(item.size), new URL(item.url).hostname].filter(Boolean).join(' · ');
    const btn = li.querySelector('button');
    btn.addEventListener('click', async () => {
      btn.disabled = true;
      btn.textContent = 'Sending…';
      const r = await send({ type: 'send-media', tabId: tab.id, item, source: 'media' });
      btn.textContent = r.ok ? 'Sent' : 'Retry';
      btn.disabled = r.ok;
      $('error').hidden = r.ok;
      if (!r.ok) $('error').textContent = r.error;
    });
    list.appendChild(li);
  }
}

init();
