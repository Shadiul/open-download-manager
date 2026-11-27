# Extension ↔ download manager protocol

The extension talks to the desktop app through Chrome native messaging. The host name is
`org.opendownloadmanager.host`. Chrome starts the host program, writes one message to its stdin, and reads
one reply from its stdout. Each message is a 4-byte little-endian length followed by UTF-8 JSON.
Replies must be under 1 MB.

The real desktop app should ship a small host program that forwards each message to the running
app (for example over a named pipe) and answers right away. Downloads must not run inside the host
process, because Chrome closes it once it has the reply.

Every message carries `"v": 1`.

## ping

```json
{ "v": 1, "type": "ping" }
```

Reply: `{ "ok": true, "version": "1.2.0" }`

## download

```json
{
  "v": 1,
  "type": "download",
  "url": "https://example.com/file.zip",
  "filename": "file.zip",
  "referrer": "https://example.com/downloads",
  "mime": "application/zip",
  "fileSize": 52428800,
  "cookies": "session=abc; theme=dark",
  "userAgent": "Mozilla/5.0 ...",
  "source": "browser-download",
  "mediaKind": null,
  "pageTitle": "Downloads - Example",
  "tabId": 12
}
```

- `source` is one of `browser-download`, `context-menu`, `media` (picked in the popup), `video-button`.
- `mediaKind` is `hls` (an `.m3u8` playlist), `dash` (an `.mpd` manifest), `video`, `audio`,
  `page`, or `null`. For `hls` and `dash` the app has to fetch the playlist, pick a quality and
  join the segments. For `page`, `url` is a video page such as `https://www.youtube.com/watch?v=ID`
  and the app extracts the video itself (yt-dlp), merging video and audio.
- `formatId` is only set for `page`: an `id` from the `probe` reply, or `null` for the best quality.
- YouTube links in any form (`youtu.be`, `/shorts/`, `/embed/`, `/live/`) arrive as `page` with
  the canonical link. The extension never sends YouTube's split `googlevideo.com` streams.
- `fileSize` is `-1` when unknown. `filename` may be empty.
- Send `cookies`, `referrer` and `userAgent` with the request so sites that need a login still work.

Reply: `{ "ok": true }` once the app has accepted the job, or `{ "ok": false, "error": "..." }`.
If the reply is not `ok`, or no reply comes within 8 seconds, the browser keeps the download.

## probe

Sent when the user opens the quality menu over a YouTube video. The app should look up the
formats (for example `yt-dlp -J <url>`) and list the useful ones, best first. The extension waits
up to 30 seconds.

```json
{ "v": 1, "type": "probe", "url": "https://www.youtube.com/watch?v=ID", "cookies": "...", "userAgent": "..." }
```

Reply:

```json
{
  "ok": true,
  "title": "Video title",
  "formats": [
    { "id": "137+140", "label": "1080p", "ext": "mp4", "size": 104857600 },
    { "id": "140", "label": "Audio only", "ext": "m4a", "size": 4194304 }
  ]
}
```

`size` may be left out. If the reply is not `ok`, the menu offers "Best quality" only.
