// Finds <video> and <audio> elements on the page, reports their sources, and shows a
// "Download video" button over videos. On YouTube the button opens a quality menu that the
// desktop app fills in.
(() => {
  if (window.__odmLoaded) return;
  window.__odmLoaded = true;

  const send = (msg) => new Promise((resolve) => {
    try {
      chrome.runtime.sendMessage(msg, (reply) => {
        if (chrome.runtime.lastError) resolve({ ok: false, error: chrome.runtime.lastError.message });
        else resolve(reply);
      });
    } catch (e) {
      resolve({ ok: false, error: e.message }); // extension was reloaded; this page has an orphaned script
    }
  });

  const reported = new Set();
  let showButton = true;
  send({ type: 'get-settings' }).then((s) => { showButton = s?.showVideoButton !== false; });

  // YouTube pages (and youtube embeds in other sites' iframes) are sent as page links.
  const isYouTube = /(^|\.)youtube(-nocookie)?\.com$/i.test(location.hostname);

  // ----- reporting media elements -----
  function sourcesOf(el) {
    const urls = [el.currentSrc, el.src, ...[...el.querySelectorAll('source')].map((s) => s.src)];
    return [...new Set(urls.filter(Boolean))];
  }

  function scan() {
    if (isYouTube) return; // its <video> plays from blob: and its streams are split
    const items = [];
    for (const el of document.querySelectorAll('video, audio')) {
      const kind = el.tagName === 'VIDEO' ? 'video' : 'audio';
      for (const url of sourcesOf(el)) {
        if (!/^https?:/i.test(url) || reported.has(url)) continue; // blob: sources are found from network traffic instead
        reported.add(url);
        items.push({ kind, url });
      }
    }
    if (items.length) send({ type: 'page-media', items });
  }

  // ----- the floating button and menu -----
  const LABEL = 'Download this video';
  let root = null, button = null, menu = null;
  let target = null;
  let hideTimer = null;
  let menuOpen = false;

  function ensureUi() {
    if (root) return;
    root = document.createElement('div');
    root.className = 'odm-root';
    button = document.createElement('button');
    button.type = 'button';
    button.className = 'odm-btn';
    button.textContent = LABEL;
    menu = document.createElement('div');
    menu.className = 'odm-menu';
    menu.hidden = true;
    root.append(button, menu);
    button.addEventListener('click', onButton);
    root.addEventListener('mouseenter', () => clearTimeout(hideTimer));
    root.addEventListener('mouseleave', scheduleHide);
    // Keep YouTube's player from treating our clicks as play/pause.
    for (const ev of ['mousedown', 'mouseup', 'click', 'dblclick', 'pointerdown', 'pointerup']) {
      root.addEventListener(ev, (e) => e.stopPropagation());
    }
    (document.body || document.documentElement).appendChild(root);
  }

  function flash(text, title = '') {
    button.textContent = text;
    button.title = title;
    setTimeout(() => { if (button) { button.textContent = LABEL; button.title = ''; } }, 3000);
  }

  function closeMenu() {
    menuOpen = false;
    if (menu) { menu.hidden = true; menu.replaceChildren(); }
  }

  async function sendItem(item) {
    closeMenu();
    button.textContent = 'Sending…';
    const r = await send({ type: 'send-media', item, pageUrl: location.href, source: 'video-button' });
    flash(r?.ok ? 'Sent to Open Download Manager' : 'Could not send', r?.ok ? '' : r?.error || '');
  }

  function formatSize(b) {
    if (!(b > 0)) return '';
    const u = ['B', 'KB', 'MB', 'GB']; let i = 0;
    while (b >= 1024 && i < 3) { b /= 1024; i++; }
    return `${b.toFixed(i > 1 ? 1 : 0)} ${u[i]}`;
  }

  function menuRow(text, sub, onClick) {
    const row = document.createElement('button');
    row.type = 'button';
    row.className = 'odm-row';
    const a = document.createElement('span'); a.textContent = text;
    const b = document.createElement('span'); b.className = 'odm-sub'; b.textContent = sub || '';
    row.append(a, b);
    row.addEventListener('click', onClick);
    return row;
  }

  async function onButton() {
    if (menuOpen) return closeMenu();
    if (!isYouTube) {
      const src = target && (target.currentSrc || target.src);
      const item = src && /^https?:/i.test(src) ? { kind: 'video', url: src, mime: '', size: -1, referrer: location.href } : null;
      return sendItem(item);
    }
    // YouTube: ask the desktop app for the qualities, then show them under the button.
    menuOpen = true;
    menu.replaceChildren(Object.assign(document.createElement('div'), { className: 'odm-note', textContent: 'Getting qualities…' }));
    menu.hidden = false;
    const r = await send({ type: 'probe-page', url: location.href });
    if (!menuOpen) return;
    if (!r?.ok || !r.formats?.length) {
      // Older app or probe failed: offer the best quality only.
      if (!r?.url) { closeMenu(); return flash('Could not send', r?.error || ''); }
      menu.replaceChildren(menuRow('Best quality', '', () => sendItem({ kind: 'page', url: r.url })));
      return;
    }
    const rows = r.formats.map((f) => menuRow(f.label || f.id, [f.ext, formatSize(f.size)].filter(Boolean).join(' · '),
      () => sendItem({ kind: 'page', url: r.url, formatId: f.id })));
    if (r.title) rows.unshift(Object.assign(document.createElement('div'), { className: 'odm-note', textContent: r.title }));
    menu.replaceChildren(...rows);
  }

  function place(video) {
    const r = video.getBoundingClientRect();
    if (r.width < 160 || r.height < 90) return; // skip tiny previews
    ensureUi();
    root.style.top = `${Math.max(r.top + 10, 8)}px`;
    // Anchor by the right edge so the menu grows leftwards over the video.
    root.style.right = `${Math.max(document.documentElement.clientWidth - Math.min(r.right, innerWidth) + 10, 8)}px`;
    root.classList.add('odm-visible');
  }

  function scheduleHide() {
    clearTimeout(hideTimer);
    hideTimer = setTimeout(() => {
      if (!root || menuOpen) return;
      root.classList.remove('odm-visible');
    }, 1500);
  }

  function videoAt(x, y) {
    return [...document.querySelectorAll('video')].find((el) => {
      const r = el.getBoundingClientRect();
      return r.width > 0 && x >= r.left && x <= r.right && y >= r.top && y <= r.bottom;
    });
  }

  document.addEventListener('mousemove', (e) => {
    if (!showButton) return;
    if (root && root.contains(e.target)) return;
    const v = (e.target instanceof Element && e.target.closest('video')) || videoAt(e.clientX, e.clientY);
    if (v) {
      if (v !== target) closeMenu();
      target = v;
      clearTimeout(hideTimer);
      place(v);
    } else {
      scheduleHide();
    }
  }, { passive: true });

  // Follow the player when the page scrolls or resizes.
  const follow = () => { if (target && root?.classList.contains('odm-visible')) place(target); };
  window.addEventListener('scroll', follow, { passive: true, capture: true });
  window.addEventListener('resize', follow, { passive: true });
  document.addEventListener('fullscreenchange', () => setTimeout(follow, 100));
  // YouTube changes videos without reloading the page.
  window.addEventListener('yt-navigate-finish', () => { closeMenu(); root?.classList.remove('odm-visible'); });

  scan();
  let scanTimer = null;
  const scanSoon = () => { clearTimeout(scanTimer); scanTimer = setTimeout(scan, 300); };
  new MutationObserver(scanSoon).observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['src'] });
  document.addEventListener('loadedmetadata', scan, true);

  // ----- "Download all links" and Alt+click -----
  chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
    if (msg?.type !== 'collect-links') return false;
    sendResponse([...document.links].map((a) => ({ url: a.href, text: a.textContent || '' })));
    return false;
  });

  // Alt+click downloads a link in the browser itself; leave that download to the browser.
  document.addEventListener('click', (e) => {
    if (e.altKey && e.target instanceof Element && e.target.closest('a[href]')) send({ type: 'skip-next-download' });
  }, true);
})();
