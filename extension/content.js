// Finds <video> and <audio> elements on the page and reports each one (poster, title, length and,
// on sites like X or Reddit, the post it belongs to) so the popup can show it as a card. Shows a
// "Download this video" button over videos. The button always targets the video it sits on:
// on YouTube and post sites it sends that video's own page link and opens a quality menu; on
// other sites it sends the stream the extension saw that video load.
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

  let showButton = true;
  const siteSkipped = (s) => (s?.excludedHosts || []).some((p) => {
    p = p.trim().toLowerCase().replace(/^\*\./, '').replace(/^www\./, '');
    const h = location.hostname.toLowerCase().replace(/^www\./, '');
    return p && (h === p || h.endsWith('.' + p));
  });
  const applySettings = (s) => { showButton = s?.showVideoButton !== false && !siteSkipped(s); };
  send({ type: 'get-settings' }).then(applySettings);

  // YouTube pages (and youtube embeds in other sites' iframes) are sent as page links.
  const isYouTube = /(^|\.)youtube(-nocookie)?\.com$/i.test(location.hostname);

  // lib/sites.js holds the post-link rules; content scripts load modules dynamically.
  let sites = null;
  import(chrome.runtime.getURL('lib/sites.js')).then((m) => { sites = m; scanSoon(); }).catch(() => {});

  // ----- identifying videos -----

  function idOf(el) {
    if (!el.dataset.odmId) el.dataset.odmId = Math.random().toString(36).slice(2, 10);
    return el.dataset.odmId;
  }

  /** The link of the post this video belongs to, on sites where yt-dlp extracts posts. */
  function permalinkOf(el) {
    if (!sites) return null;
    if (isYouTube) return sites.pageDownloadUrl(location.href);
    if (!sites.isPostSite(location.hostname)) return null;
    const container = el.closest('article, shreddit-post, [data-testid="post-container"], [data-e2e="recommend-list-item-container"]');
    if (container) {
      // A post's timestamp links to the post itself on X; Reddit keeps it in an attribute.
      const stamp = container.querySelector('a[href*="/status/"] time')?.closest('a');
      const candidates = [
        stamp?.href,
        container.getAttribute?.('permalink') && new URL(container.getAttribute('permalink'), location.origin).href,
        ...[...container.querySelectorAll('a[href*="/status/"], a[href*="/comments/"], a[href*="/p/"], a[href*="/reel/"], a[href*="/video/"]')].map((a) => a.href)
      ];
      for (const c of candidates) {
        const p = c && sites.postUrl(c);
        if (p) return p;
      }
    }
    return sites.postUrl(location.href);
  }

  function titleOf(el) {
    const container = el.closest('article, shreddit-post');
    const text = container?.getAttribute?.('post-title')
      || container?.querySelector('[data-testid="tweetText"]')?.textContent
      || el.getAttribute('aria-label') || el.title || document.title;
    return (text || '').replace(/\s+/g, ' ').trim().slice(0, 140);
  }

  function posterOf(el, onlyVideo) {
    if (el.poster) return el.poster;
    // A page with one video usually advertises its thumbnail.
    if (onlyVideo) return document.querySelector('meta[property="og:image"]')?.content || '';
    return '';
  }

  const lastSent = new Map();

  /** Tells the extension about every visible-sized video on the page. */
  function scan() {
    const els = [...document.querySelectorAll('video, audio')];
    const videos = els.filter((el) => el.tagName === 'AUDIO' || el.clientWidth >= 160 || !el.paused);
    const items = [];
    for (const el of videos) {
      const src = [el.currentSrc, el.src, ...[...el.querySelectorAll('source')].map((s) => s.src)].find((u) => /^https?:/i.test(u || '')) || '';
      const info = {
        videoId: idOf(el),
        kind: el.tagName === 'VIDEO' ? 'video' : 'audio',
        src,
        poster: posterOf(el, videos.length === 1),
        title: titleOf(el),
        duration: Number.isFinite(el.duration) ? el.duration : 0,
        width: el.videoWidth || 0,
        height: el.videoHeight || 0,
        pageUrl: permalinkOf(el) || ''
      };
      const key = JSON.stringify(info);
      if (lastSent.get(info.videoId) === key) continue;
      lastSent.set(info.videoId, key);
      items.push(info);
    }
    if (items.length) send({ type: 'page-videos', items });
  }

  // Streams a page loads right after a video starts belong to that video.
  document.addEventListener('play', (e) => {
    if (e.target instanceof HTMLMediaElement) {
      send({ type: 'video-playing', videoId: idOf(e.target) });
      scanSoon();
    }
  }, true);

  // ----- the floating button and menu -----
  const LABEL = 'Download this video';
  let root = null, button = null, menu = null;
  let target = null;
  let hideTimer = null;
  let menuOpen = false;
  // The x hides the button until the next video.
  let dismissed = false;

  function ensureUi() {
    if (root) return;
    root = document.createElement('div');
    root.className = 'odm-root';
    button = document.createElement('button');
    button.type = 'button';
    button.className = 'odm-btn';
    button.textContent = LABEL;
    const close = document.createElement('button');
    close.type = 'button';
    close.className = 'odm-close';
    close.textContent = '×';
    close.title = "Hide for this video (turn the button off in the extension's settings)";
    close.setAttribute('aria-label', 'Hide the download button');
    close.addEventListener('click', () => {
      dismissed = true;
      closeMenu();
      root.classList.remove('odm-visible');
    });
    const bar = document.createElement('div');
    bar.className = 'odm-bar';
    bar.append(button, close);
    menu = document.createElement('div');
    menu.className = 'odm-menu';
    menu.hidden = true;
    root.append(bar, menu);
    button.addEventListener('click', onButton);
    root.addEventListener('mouseenter', () => clearTimeout(hideTimer));
    root.addEventListener('mouseleave', scheduleHide);
    // Keep the page's player from treating our clicks as play/pause.
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

  async function sendItem(item, videoId) {
    closeMenu();
    button.textContent = 'Sending…';
    const r = await send({ type: 'send-media', item, videoId, pageUrl: location.href, source: 'video-button' });
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

  const note = (text) => Object.assign(document.createElement('div'), { className: 'odm-note', textContent: text, title: text });

  async function onButton() {
    if (menuOpen) return closeMenu();
    if (!target) return;
    const videoId = idOf(target);
    const page = permalinkOf(target);
    if (!page) {
      // A plain file plays straight from its address; anything else uses the stream seen for it.
      const src = target.currentSrc || target.src;
      const item = src && /^https?:/i.test(src) ? { kind: 'video', url: src, mime: '', size: -1, referrer: location.href } : null;
      return sendItem(item, videoId);
    }
    // A page link: ask the desktop app for the qualities, then show them under the button.
    menuOpen = true;
    menu.replaceChildren(note('Getting qualities…'));
    menu.hidden = false;
    const r = await send({ type: 'probe-page', url: page });
    if (!menuOpen) return;
    if (!r?.ok || !r.formats?.length) {
      if (!r?.url) { closeMenu(); return flash('Could not send', r?.error || ''); }
      const why = r?.error ? `Could not list the qualities: ${r.error}` : 'Could not list the qualities';
      menu.replaceChildren(note(why), menuRow('Best quality', '', () => sendItem({ kind: 'page', url: r.url }, videoId)));
      return;
    }
    const rows = r.formats.map((f) => menuRow(f.label || f.id, [f.ext, formatSize(f.size)].filter(Boolean).join(' · '),
      () => sendItem({ kind: 'page', url: r.url, formatId: f.id, size: f.size > 0 ? f.size : -1 }, videoId)));
    if (r.title) rows.unshift(note(r.title));
    menu.replaceChildren(...rows);
  }

  function place(video) {
    const r = video.getBoundingClientRect();
    if (dismissed || !showButton) return;
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
  window.addEventListener('yt-navigate-finish', () => { dismissed = false; closeMenu(); root?.classList.remove('odm-visible'); lastSent.clear(); scanSoon(); });
  // Settings changed in the popup take effect without reloading the page.
  chrome.storage.onChanged.addListener((changes) => {
    if (!changes.settings) return;
    applySettings(changes.settings.newValue);
    if (!showButton) { closeMenu(); root?.classList.remove('odm-visible'); }
  });

  let scanTimer = null;
  function scanSoon() { clearTimeout(scanTimer); scanTimer = setTimeout(scan, 400); }
  scanSoon();
  new MutationObserver(scanSoon).observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['src', 'poster'] });
  document.addEventListener('loadedmetadata', scanSoon, true);

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
