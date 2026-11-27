import test from 'node:test';
import assert from 'node:assert/strict';
import { classifyMedia, totalFromContentRange, mediaKey, extensionOf } from '../extension/lib/media.js';
import { shouldCatch, DEFAULT_SETTINGS } from '../extension/lib/rules.js';
import { pickFileLinks } from '../extension/lib/links.js';

const MB = 1024 * 1024;

test('classifyMedia finds HLS and DASH by type or extension', () => {
  assert.equal(classifyMedia({ url: 'https://a.com/x/master.m3u8?t=1', contentType: 'text/plain' }).kind, 'hls');
  assert.equal(classifyMedia({ url: 'https://a.com/play', contentType: 'application/vnd.apple.mpegurl' }).kind, 'hls');
  assert.equal(classifyMedia({ url: 'https://a.com/m.mpd' }).kind, 'dash');
  assert.equal(classifyMedia({ url: 'https://a.com/m', contentType: 'application/dash+xml; charset=utf-8' }).kind, 'dash');
});

test('classifyMedia ignores stream segments and tiny files', () => {
  assert.equal(classifyMedia({ url: 'https://a.com/seg-001.ts', contentType: 'video/mp2t', contentLength: 2 * MB }), null);
  assert.equal(classifyMedia({ url: 'https://a.com/chunk.m4s', contentType: 'video/mp4', contentLength: 2 * MB }), null);
  assert.equal(classifyMedia({ url: 'https://a.com/preview.mp4', contentType: 'video/mp4', contentLength: 20000 }), null);
});

test('classifyMedia accepts progressive video and audio', () => {
  assert.equal(classifyMedia({ url: 'https://a.com/movie.mp4', contentType: 'video/mp4', contentLength: 40 * MB }).kind, 'video');
  assert.equal(classifyMedia({ url: 'https://a.com/stream', contentType: 'audio/mpeg', contentLength: 5 * MB }).kind, 'audio');
  // 206 range response: size is from Content-Range, small range is fine
  assert.equal(classifyMedia({ url: 'https://a.com/v.webm', contentType: 'video/webm', contentLength: 40 * MB, statusCode: 206 }).kind, 'video');
});

test('classifyMedia rejects non-http and errors', () => {
  assert.equal(classifyMedia({ url: 'blob:https://a.com/123', contentType: 'video/mp4' }), null);
  assert.equal(classifyMedia({ url: 'https://a.com/v.mp4', contentType: 'video/mp4', statusCode: 404 }), null);
  assert.equal(classifyMedia({ url: 'https://a.com/page.html', contentType: 'text/html' }), null);
});

test('helpers', () => {
  assert.equal(totalFromContentRange('bytes 0-1023/52428800'), 52428800);
  assert.equal(totalFromContentRange('bytes 0-1023/*'), -1);
  assert.equal(mediaKey('https://a.com/v.mp4?id=1&range=0-100#t'), 'https://a.com/v.mp4?id=1');
  assert.equal(extensionOf('https://a.com/a/B.MP4?x=1'), 'mp4');
});

const item = (over = {}) => ({ id: 1, url: 'https://a.com/f.zip', filename: 'C:\\Users\\me\\Downloads\\f.zip', mime: 'application/zip', totalBytes: 10 * MB, state: 'in_progress', ...over });

test('shouldCatch catches normal downloads by default', () => {
  assert.deepEqual(shouldCatch(item(), DEFAULT_SETTINGS, 'me'), { catch: true, reason: 'ok' });
});

test('shouldCatch leaves some downloads to the browser', () => {
  assert.equal(shouldCatch(item(), { enabled: false }, 'me').reason, 'disabled');
  assert.equal(shouldCatch(item({ url: 'blob:https://a.com/1' }), {}, 'me').reason, 'scheme');
  assert.equal(shouldCatch(item({ url: 'data:text/plain,hi' }), {}, 'me').reason, 'scheme');
  assert.equal(shouldCatch(item({ byExtensionId: 'me' }), {}, 'me').reason, 'own-download');
  assert.equal(shouldCatch(item({ mime: 'text/html' }), {}, 'me').reason, 'html');
  assert.equal(shouldCatch(item({ url: 'https://dl.corp.example.com/f.zip' }), { excludedHosts: ['example.com'] }, 'me').reason, 'excluded-host');
  assert.equal(shouldCatch(item({ totalBytes: 1000 }), { minSizeKB: 100 }, 'me').reason, 'too-small');
});

test('shouldCatch in listed mode checks the file type', () => {
  assert.equal(shouldCatch(item(), { mode: 'listed' }, 'me').catch, true);
  assert.equal(shouldCatch(item({ filename: 'x.txt', url: 'https://a.com/x.txt' }), { mode: 'listed' }, 'me').reason, 'extension-not-listed');
  // filename not decided yet: fall back to the URL
  assert.equal(shouldCatch(item({ filename: '', url: 'https://a.com/setup.exe' }), { mode: 'listed' }, 'me').catch, true);
});

import { pageDownloadUrl, isSplitStream } from '../extension/lib/sites.js';

test('pageDownloadUrl turns any YouTube link into the canonical page', () => {
  const W = 'https://www.youtube.com/watch?v=dQw4w9WgXcQ';
  assert.equal(pageDownloadUrl('https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PL1&t=30s'), W);
  assert.equal(pageDownloadUrl('https://m.youtube.com/watch?v=dQw4w9WgXcQ'), W);
  assert.equal(pageDownloadUrl('https://youtu.be/dQw4w9WgXcQ?si=abc'), W);
  assert.equal(pageDownloadUrl('https://www.youtube.com/embed/dQw4w9WgXcQ?autoplay=1'), W);
  assert.equal(pageDownloadUrl('https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ'), W);
  assert.equal(pageDownloadUrl('https://www.youtube.com/live/dQw4w9WgXcQ'), W);
  assert.equal(pageDownloadUrl('https://www.youtube.com/shorts/dQw4w9WgXcQ'), 'https://www.youtube.com/shorts/dQw4w9WgXcQ');
  assert.equal(pageDownloadUrl('https://music.youtube.com/watch?v=dQw4w9WgXcQ'), 'https://music.youtube.com/watch?v=dQw4w9WgXcQ');
});

test('pageDownloadUrl ignores non-video YouTube pages and other sites', () => {
  assert.equal(pageDownloadUrl('https://www.youtube.com/'), null);
  assert.equal(pageDownloadUrl('https://www.youtube.com/@channel'), null);
  assert.equal(pageDownloadUrl('https://www.youtube.com/watch?v=short'), null);
  assert.equal(pageDownloadUrl('https://notyoutube.com/watch?v=dQw4w9WgXcQ'), null);
  assert.equal(pageDownloadUrl('not a url'), null);
});

test('isSplitStream flags googlevideo pieces', () => {
  assert.equal(isSplitStream('https://rr3---sn-abc.googlevideo.com/videoplayback?itag=137'), true);
  assert.equal(isSplitStream('https://example.com/v.mp4'), false);
});

test('pickFileLinks keeps file links once, in page order', () => {
  const links = [
    { url: 'https://e.com/a.zip', text: ' A ' },
    { url: 'https://e.com/about.html', text: 'About' },
    { url: 'https://e.com/a.zip#top', text: 'again' },
    { url: 'javascript:void(0)', text: 'x' },
    { url: 'mailto:me@example.com', text: 'mail' },
    { url: 'https://e.com/My%20Doc.PDF?dl=1', text: 'doc' },
    { url: 'not a url' }
  ];
  assert.deepEqual(pickFileLinks(links, DEFAULT_SETTINGS.extensions), [
    { url: 'https://e.com/a.zip', text: 'A' },
    { url: 'https://e.com/My%20Doc.PDF?dl=1', text: 'doc' }
  ]);
});

import fs from 'node:fs';
import { build } from '../scripts/build.mjs';

test('build makes an Edge version with YouTube and a Chrome version without', () => {
  const edge = build('edge');
  const chrome = build('chrome');
  assert.match(fs.readFileSync(`${edge.dir}/lib/build-flags.js`, 'utf8'), /YOUTUBE_ENABLED = true/);
  assert.match(fs.readFileSync(`${chrome.dir}/lib/build-flags.js`, 'utf8'), /YOUTUBE_ENABLED = false/);
  const cm = JSON.parse(fs.readFileSync(`${chrome.dir}/manifest.json`, 'utf8'));
  assert.ok(cm.content_scripts[0].exclude_matches.includes('*://*.youtube.com/*'));
  const em = JSON.parse(fs.readFileSync(`${edge.dir}/manifest.json`, 'utf8'));
  assert.equal(em.content_scripts[0].exclude_matches, undefined);
  assert.ok(fs.statSync(chrome.zip).size > 1000);
});
