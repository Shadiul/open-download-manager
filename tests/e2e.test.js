// Loads the extension into Chromium with the Python test host registered, then checks that
// downloads are handed off, media is detected, and the browser keeps downloads the host refuses.
import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { chromium } = require('playwright');

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const extDir = path.join(root, 'extension');
const hostScript = path.join(root, 'native-host', 'test_host.py');
const EXT_ID = 'ohammiojomdnbnfhigkofgahnghdmolg';

function startServer() {
  const video = Buffer.alloc(1024 * 1024, 1);
  const server = http.createServer((req, res) => {
    const u = new URL(req.url, 'http://x');
    if (u.pathname === '/') {
      res.setHeader('content-type', 'text/html');
      res.end(`<!doctype html><title>Test page</title>
        <a id="dl" href="/file.zip">file</a>
        <video id="v" src="/movie.mp4" width="640" height="360" muted></video>
        <script>fetch('/live/master.m3u8')</script>`);
    } else if (u.pathname === '/file.zip') {
      res.setHeader('content-type', 'application/zip');
      res.setHeader('content-disposition', 'attachment; filename="file.zip"');
      res.setHeader('content-length', String(5 * 1024 * 1024));
      // Send slowly so the download is still running when the extension acts on it.
      let sent = 0;
      const timer = setInterval(() => {
        if (sent >= 5 * 1024 * 1024 || res.destroyed) { clearInterval(timer); return res.end(); }
        res.write(Buffer.alloc(64 * 1024)); sent += 64 * 1024;
      }, 50);
      res.on('close', () => clearInterval(timer));
    } else if (u.pathname === '/movie.mp4') {
      res.setHeader('content-type', 'video/mp4');
      res.setHeader('content-length', String(video.length));
      res.end(video);
    } else if (u.pathname === '/live/master.m3u8') {
      res.setHeader('content-type', 'application/vnd.apple.mpegurl');
      res.end('#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=800000\nlow.m3u8\n');
    } else {
      res.statusCode = 404; res.end();
    }
  });
  return new Promise((r) => server.listen(0, '127.0.0.1', () => r(server)));
}

async function launch({ refuse = false, dir = extDir } = {}) {
  const userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'odm-profile-'));
  const log = path.join(userDataDir, 'received.jsonl');
  const hostDir = path.join(userDataDir, 'NativeMessagingHosts');
  fs.mkdirSync(hostDir);
  fs.writeFileSync(path.join(hostDir, 'org.opendownloadmanager.host.json'), JSON.stringify({
    name: 'org.opendownloadmanager.host', description: 'test', path: hostScript, type: 'stdio',
    allowed_origins: [`chrome-extension://${EXT_ID}/`]
  }));
  process.env.ODM_LOG = log;
  if (refuse) process.env.ODM_REFUSE = '1'; else delete process.env.ODM_REFUSE;
  const context = await chromium.launchPersistentContext(userDataDir, {
    headless: true,
    channel: 'chromium', // the headless shell cannot load extensions
    acceptDownloads: true,
    args: [`--disable-extensions-except=${dir}`, `--load-extension=${dir}`]
  });
  let [sw] = context.serviceWorkers();
  if (!sw) sw = await context.waitForEvent('serviceworker');
  assert.ok(sw.url().startsWith(`chrome-extension://${EXT_ID}/`), `unexpected extension id: ${sw.url()}`);
  const received = () => (fs.existsSync(log) ? fs.readFileSync(log, 'utf8').trim().split('\n').filter(Boolean).map(JSON.parse) : []);
  return { context, sw, received };
}

async function waitFor(fn, ms = 10000) {
  const end = Date.now() + ms;
  while (Date.now() < end) {
    const v = await fn();
    if (v) return v;
    await new Promise((r) => setTimeout(r, 200));
  }
  return fn();
}

test('extension end to end', async (t) => {
  const server = await startServer();
  const base = `http://127.0.0.1:${server.address().port}`;
  t.after(() => server.close());

  await t.test('hands downloads, page video and HLS to the host', async () => {
    const { context, sw, received } = await launch();
    try {
      const ext = await context.newPage();
      await ext.goto(`chrome-extension://${EXT_ID}/popup/popup.html`);
      const ping = await ext.evaluate(() => chrome.runtime.sendMessage({ type: 'ping-host' }));
      assert.equal(ping.ok, true, `ping failed: ${ping.error}`);

      const page = await context.newPage();
      await page.goto(base + '/');
      await page.click('#dl');
      const got = await waitFor(() => received().find((j) => j.url.endsWith('/file.zip')));
      assert.ok(got, 'download was not handed to the host');
      assert.equal(got.source, 'browser-download');
      assert.equal(got.filename, 'file.zip');
      assert.equal(got.referrer, base + '/');

      const left = await waitFor(async () => {
        const items = await sw.evaluate(() => chrome.downloads.search({}));
        return items.length === 0 ? items : null;
      });
      assert.deepEqual(left, [], 'browser copy of the download should be gone');

      const tabId = await sw.evaluate(async (url) => (await chrome.tabs.query({ url: url + '/*' }))[0].id, base);
      const media = await waitFor(async () => {
        const m = await ext.evaluate((id) => chrome.runtime.sendMessage({ type: 'get-tab-media', tabId: id }), tabId);
        return m.some((x) => x.kind === 'hls') && m.some((x) => x.url.endsWith('/movie.mp4')) ? m : null;
      });
      assert.ok(media, 'video and HLS playlist should be detected');

      const hls = media.find((x) => x.kind === 'hls');
      const sent = await ext.evaluate(({ id, item }) => chrome.runtime.sendMessage({ type: 'send-media', tabId: id, item }), { id: tabId, item: hls });
      assert.equal(sent.ok, true, sent.error);
      const job = await waitFor(() => received().find((j) => j.mediaKind === 'hls'));
      assert.equal(job.url, base + '/live/master.m3u8');
      assert.equal(job.pageTitle, 'Test page');
    } finally {
      await context.close();
    }
  });

  await t.test('YouTube: quality menu near the player sends the page link', async () => {
    const { context, received } = await launch();
    try {
      // Stand-in for a YouTube watch page; nothing goes to the real site.
      await context.route('https://www.youtube.com/**', (route) => route.fulfill({
        contentType: 'text/html',
        body: `<!doctype html><title>Test video - YouTube</title>
          <body style="margin:0;background:#0f0f0f"><div id="movie_player" style="margin:40px">
          <video style="width:854px;height:480px;background:#333"></video></div></body>`
      }));
      const page = await context.newPage();
      await page.setViewportSize({ width: 960, height: 600 });
      await page.goto('https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=42s');
      await page.mouse.move(400, 300);
      await page.mouse.move(420, 310);
      await page.click('.odm-btn');
      await page.waitForSelector('.odm-row >> text=720p');
      if (process.env.SCREENSHOT) await page.screenshot({ path: process.env.SCREENSHOT });
      await page.click('.odm-row >> text=720p');
      const job = await waitFor(() => received().find((j) => j.mediaKind === 'page'));
      assert.ok(job, 'no job reached the host');
      assert.equal(job.url, 'https://www.youtube.com/watch?v=dQw4w9WgXcQ');
      assert.equal(job.formatId, '136+140');
      assert.equal(job.source, 'video-button');
    } finally {
      await context.close();
    }
  });

  await t.test('Chrome build: no YouTube button and YouTube links are refused', async () => {
    const chromeDir = path.join(root, 'dist', 'chrome');
    assert.ok(fs.existsSync(chromeDir), 'run `npm run build` first');
    const { context, received } = await launch({ dir: chromeDir });
    try {
      await context.route('https://www.youtube.com/**', (route) => route.fulfill({
        contentType: 'text/html',
        body: '<!doctype html><title>YT</title><video style="width:854px;height:480px"></video>'
      }));
      const page = await context.newPage();
      await page.goto('https://www.youtube.com/watch?v=dQw4w9WgXcQ');
      await page.mouse.move(300, 200);
      await page.mouse.move(320, 210);
      await page.waitForTimeout(500);
      assert.equal(await page.locator('.odm-btn').count(), 0, 'no button on YouTube in the Chrome build');

      const ext = await context.newPage();
      await ext.goto(`chrome-extension://${EXT_ID}/popup/popup.html`);
      const r = await ext.evaluate(() => chrome.runtime.sendMessage({ type: 'send-media', item: { kind: 'video', url: 'https://youtu.be/dQw4w9WgXcQ' } }));
      assert.equal(r.ok, false);
      assert.match(r.error, /not available in the Chrome version/);
      assert.equal(received().length, 0);

      // Other sites still work.
      const other = await context.newPage();
      await other.goto(base + '/');
      await other.click('#dl');
      assert.ok(await waitFor(() => received().find((j) => j.url.endsWith('/file.zip'))), 'normal downloads still caught');
    } finally {
      await context.close();
    }
  });

  await t.test('browser keeps the download when the host refuses it', async () => {
    const { context, sw, received } = await launch({ refuse: true });
    try {
      const page = await context.newPage();
      await page.goto(base + '/');
      const download = page.waitForEvent('download');
      await page.click('#dl');
      await download;
      const done = await waitFor(async () => {
        const items = await sw.evaluate(() => chrome.downloads.search({}));
        return items.find((d) => d.state === 'complete');
      }, 20000);
      assert.ok(done, 'browser should finish the download itself');
      assert.equal(received().length, 0);
    } finally {
      await context.close();
    }
  });
});
