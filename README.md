# Open Download Manager (browser extension)

A Chrome and Edge extension (Manifest V3, two builds, see below) that hands downloads and videos to the
Open Download Manager desktop app on Windows 11 through native messaging.

What it does:

- **Catches browser downloads.** When a download starts, the extension pauses it, sends it to the
  download manager with the page's cookies, referrer and user agent, and cancels the browser's
  copy once the app accepts it. If the app is not installed, not running, or refuses, the browser
  carries on with the download as normal.
- **Finds video and audio on the page.** It watches network traffic for HLS (`.m3u8`), DASH
  (`.mpd`) and progressive video or audio files, and reads `<video>`/`<audio>` elements. Stream
  segments (`.ts`, `.m4s`) and files under 512 KB are ignored. The toolbar badge shows how many
  were found, and the popup lists them with a Download button.
- **Shows a "Download this video" button** at the top right of a video when you hover over it.
  For videos that play from a `blob:` URL it sends the best stream it saw on the page instead.
- **Downloads YouTube videos (Edge build).** On YouTube (watch pages, Shorts, embeds, `youtu.be` links) the
  button opens a quality menu (1080p, 720p, audio only, …) filled in by the desktop app, which
  extracts the video with yt-dlp. The extension sends the page link, never YouTube's split
  `googlevideo.com` streams, which are useless on their own.
- **Adds "Download with Open Download Manager"** to the right-click menu for links, videos, audio and images.
- **Settings:** turn catching on or off, catch everything or only listed file types, a minimum
  size, and sites to leave alone.

## Layout

```
extension/          the unpacked extension (load this folder in the browser)
  background.js     service worker: catching downloads, detecting media, context menu
  content.js        finds media elements and draws the video button
  lib/media.js      decides which responses are media (unit tested)
  lib/rules.js      decides which downloads to catch (unit tested)
  lib/native.js     native messaging client
  lib/sites.js      recognises YouTube links (unit tested)
  popup/, options/  toolbar popup and settings page
native-host/
  PROTOCOL.md       the messages the desktop app must answer
  test_host.py      a stand-in host that accepts everything and logs to received.jsonl
  install-windows.ps1  registers the host for Chrome and Edge (current user)
tests/              unit tests and a Chromium end-to-end test
```

## Try it on Windows

1. Open `chrome://extensions` (or `edge://extensions`), turn on Developer mode, choose
   **Load unpacked** and pick the `extension` folder. The extension ID will be
   `ohammiojomdnbnfhigkofgahnghdmolg`, fixed by the `key` in `manifest.json`.
2. With Python installed, run
   `powershell -ExecutionPolicy Bypass -File native-host\install-windows.ps1`.
   This registers the test host. Once the real app exists, pass `-HostPath` with its host exe.
3. Download any file. It should disappear from the browser's download list and show up in
   `native-host\received.jsonl`.

## Tests

```
npm install         # once, for the end-to-end test
npm test            # unit tests, no browser needed
npm run build       # dist/edge and dist/chrome, plus a zip of each
npm run test:e2e    # builds, then loads the extension in Playwright's Chromium with the test host
```

## Two builds: Edge and Chrome

The Chrome Web Store does not accept extensions that download YouTube videos, so there are two
builds made from the same code:

| Build | YouTube | Where it goes |
|---|---|---|
| Edge | Download button and quality menu on YouTube | Microsoft Edge Add-ons |
| Chrome | Off. The page script doesn't run on YouTube, and the popup tells people to paste the link into the desktop app | Chrome Web Store |

`npm run build` writes `dist/edge/`, `dist/chrome/` and a store-ready zip for each. The only
differences are `lib/build-flags.js` and the Chrome manifest's `exclude_matches` for YouTube.
Loading the `extension/` folder unpacked gives you the Edge behaviour.

## Contributing

Issues and pull requests are welcome. Run `npm test` before sending a change, and
`npm run test:e2e` if you touched `background.js`, `content.js`, the build or the protocol.
Changes to the messages in `native-host/PROTOCOL.md` need a matching change in the desktop app.

## License

GNU General Public License v3.0. See [LICENSE](LICENSE).
