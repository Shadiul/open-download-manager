import test from 'node:test';
import assert from 'node:assert/strict';
import { classifyMedia, totalFromContentRange, mediaKey, extensionOf, parseHls } from '../extension/lib/media.js';
import { shouldCatch, DEFAULT_SETTINGS } from '../extension/lib/rules.js';
import { pickFileLinks } from '../extension/lib/links.js';
import { buildCards, formatDuration } from '../extension/lib/cards.js';

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

test('postUrl gives one canonical link per post', async () => {
  const { postUrl, isPostSite } = await import('../extension/lib/sites.js');
  assert.equal(postUrl('https://x.com/nasa/status/1861234567890/video/1?s=20'), 'https://x.com/nasa/status/1861234567890');
  assert.equal(postUrl('https://twitter.com/nasa/status/42'), 'https://x.com/nasa/status/42');
  assert.equal(postUrl('https://x.com/home'), null);
  assert.equal(postUrl('https://www.reddit.com/r/videos/comments/abc123/some_title/'), 'https://www.reddit.com/r/videos/comments/abc123/');
  assert.equal(postUrl('https://www.instagram.com/someone/reel/Cx1_ab/'), 'https://www.instagram.com/reel/Cx1_ab/');
  assert.equal(postUrl('https://www.tiktok.com/@me/video/7300000000000000000?lang=en'), 'https://www.tiktok.com/@me/video/7300000000000000000');
  assert.equal(postUrl('https://example.com/status/1'), null);
  assert.equal(isPostSite('x.com'), true);
  assert.equal(isPostSite('example.com'), false);
});

test('parseHls tells master playlists from media playlists', () => {
  const master = '#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360\nlow.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=2500000,RESOLUTION=1280x720\nhigh.m3u8\n';
  assert.deepEqual(parseHls(master), { master: true, height: 720 });
  assert.deepEqual(parseHls('#EXTM3U\n#EXTINF:4.0,\nseg1.ts\n'), { master: false, height: null });
  assert.deepEqual(parseHls('#EXT-X-STREAM-INF:RESOLUTION=720x1280\nv.m3u8'), { master: true, height: 720 }, 'portrait counts its short side');
});

test('buildCards gives one card per video and hides covered variants', () => {
  const videos = [
    { videoId: 'a', title: 'Post with a link', poster: 'p.jpg', duration: 83, pageUrl: 'https://x.com/u/status/1' },
    { videoId: 'b', title: 'Blob player', poster: '', duration: 10 }
  ];
  const streams = [
    { kind: 'hls', url: 'https://cdn/b/master.m3u8', videoId: 'b', hlsMaster: true, height: 720, seenAt: 1 },
    { kind: 'hls', url: 'https://cdn/b/360.m3u8', videoId: 'b', hlsMaster: false, seenAt: 2 },
    { kind: 'hls', url: 'https://cdn/b/720.m3u8', videoId: 'b', hlsMaster: false, seenAt: 3 },
    { kind: 'video', url: 'https://cdn/other.mp4', size: 5000000, seenAt: 4 }
  ];
  const cards = buildCards(videos, streams, 'Page');
  assert.equal(cards.length, 3);
  assert.deepEqual(cards[0].item, { kind: 'page', url: 'https://x.com/u/status/1' }, 'post link wins');
  assert.equal(cards[1].item.url, 'https://cdn/b/master.m3u8', 'the master, not a variant');
  assert.equal(cards[1].quality, 720);
  assert.equal(cards[2].item.url, 'https://cdn/other.mp4');
  assert.equal(cards[2].title, 'other.mp4');
  assert.equal(formatDuration(83), '1:23');
  assert.equal(formatDuration(3725), '1:02:05');
  assert.equal(formatDuration(NaN), '');
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
