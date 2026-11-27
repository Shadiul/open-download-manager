# Open Download Manager

An open source download manager for Windows 11, with a Chrome and Edge extension that hands it
your downloads and videos. Fast segmented downloads, pause and resume that survive a restart, a
queue with a schedule, and video downloads from YouTube and other sites through yt-dlp.

It is for files and videos you have the right to download. It does not bypass DRM, paywalls or
logins you do not have.

## What it does

**The app** (WinUI 3, in the notification area)

- Splits each download over up to 32 connections, and when one connection finishes it takes
  over half of the slowest remaining range, so no connection sits idle at the end.
- Saves progress every second in a SQLite file. Pause, close the app or restart Windows, and the
  download continues where it stopped. If the file changed on the server it starts over rather
  than mixing two versions (`If-Range`).
- Retries dropped connections, backs off from servers that answer "too busy" and remembers how
  many connections each server accepts.
- Queue with a limit on parallel downloads, a start/stop time window, and sleep or shut down
  when everything is done. Speed limits for all downloads and per download.
- Sorts files into Video, Music, Documents, Programs, Archives and Other.
- Video pages (YouTube, Vimeo, X, Reddit, TikTok and the other sites yt-dlp supports) come with a
  quality menu: 2160p to 360p, audio only (M4A) or MP3. Video and audio are merged into one MP4.
- "Refresh address" for links that expired: click the link again in the browser and the
  download continues with the new address.
- Marks finished files with Mark of the Web, like a browser does, so Windows and your antivirus
  check downloaded programs before they run.
- Notifications when a download finishes or fails, keeps the PC awake while downloading, starts
  with Windows, and offers to download links you copy (this is how Chrome users get YouTube
  videos, see below).

**The extension** (Manifest V3, Chrome and Edge)

- Catches browser downloads and sends them to the app with the page's cookies, referrer and user
  agent, so downloads behind a login work. If the app is not installed or refuses, the browser
  keeps the download. Hold Alt while clicking a link to leave that one to the browser.
- Finds video and audio on pages (HLS, DASH, MP4 and others), shows a count on its icon, and a
  "Download this video" button on videos.
- On YouTube (Edge build) the button opens the quality menu next to the player.
- Right-click: "Download with Open Download Manager" on links and media, and "Download all
  links" on a page.

## Layout

```
core/                         the Rust core, shared with a later macOS app
  crates/odm-core             engine, queue, SQLite state, yt-dlp, browser pipe, protocol
  crates/odm-host             native messaging host: relays the extension to the app
  crates/odm-ffi              odm.dll, the C interface the Windows app calls
windows/OpenDownloadManager/  the WinUI 3 app (C#)
extension/                    the browser extension (load this folder unpacked)
native-host/PROTOCOL.md       the messages between the extension and the app
scripts/fetch-tools.ps1       downloads yt-dlp, an LGPL ffmpeg and deno into tools/, checking hashes
scripts/build.mjs             builds the Edge and Chrome extension packages
tests/                        extension unit tests and a Chromium end-to-end test
```

## Build and run on Windows

You need the Rust toolchain (1.88 or later, MSVC target), the Visual Studio C++ build tools,
the .NET 9 SDK and Node.js.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\fetch-tools.ps1
dotnet build windows\OpenDownloadManager -c Release
windows\OpenDownloadManager\bin\Release\net9.0-windows10.0.26100.0\win-x64\OpenDownloadManager.exe
```

The app build runs `cargo build --release` for the core and the host first (skip it with
`-p:SkipRustBuild=true`), and copies `odm.dll`, `odm-host.exe` and `tools\` next to the app.
On every start the app registers the native messaging host for Chrome and Edge under
`HKCU` (no admin rights needed).

Then load the extension: open `edge://extensions` or `chrome://extensions`, turn on Developer
mode, choose **Load unpacked** and pick the `extension` folder. Its ID is
`ohammiojomdnbnfhigkofgahnghdmolg`, fixed by the `key` in `manifest.json`, which is the ID the app
allows. The popup shows "App connected" when everything is in place.

## Tests

```powershell
cd core; cargo test --workspace     # engine against a misbehaving local server, manager, pipe, host
npm test                            # extension unit tests
npm install; npm run test:e2e       # loads the extension in Playwright's Chromium with a test host
```

## Two extension builds: Edge and Chrome

The Chrome Web Store does not accept extensions that download YouTube videos, so there are two
builds made from the same code:

| Build | YouTube | Where it goes |
|---|---|---|
| Edge | Download button and quality menu on YouTube | Microsoft Edge Add-ons |
| Chrome | Off on YouTube. Copy the video's link instead: the app offers the quality menu | Chrome Web Store |

`npm run build` writes `dist/edge/`, `dist/chrome/` and a store-ready zip for each. Loading the
`extension/` folder unpacked gives you the Edge behaviour.

## Contributing

Issues and pull requests are welcome. Run `cargo test` in `core` and `npm test` before sending a
change. Changes to the messages in `native-host/PROTOCOL.md` need matching changes in
`odm-core/src/protocol.rs` and the extension. Tests use a local test server and files we
generate; please do not add links to copyrighted videos.

## License

GNU General Public License v3.0. See [LICENSE](LICENSE). yt-dlp is public domain (Unlicense);
the bundled ffmpeg is an LGPL build and runs as a separate program; deno (MIT) is the
JavaScript runtime yt-dlp uses for YouTube.
