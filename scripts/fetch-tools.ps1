# Downloads the tools the app bundles into tools\ (git-ignored), checking each file against the
# SHA-256 list its project publishes with the release.
#
#   yt-dlp\yt-dlp.exe      https://github.com/yt-dlp/yt-dlp (Unlicense), the folder build
#   ffmpeg.exe, ffprobe.exe https://github.com/BtbN/FFmpeg-Builds, LGPL shared build (+ its DLLs)
#
# deno, the JavaScript runtime yt-dlp needs for YouTube, is not bundled: the app fetches it the
# first time a YouTube video needs it (see ensure_js_runtime in core/crates/odm-core/src/video.rs).
#
# Usage: powershell -ExecutionPolicy Bypass -File scripts\fetch-tools.ps1 [-Force]
param([switch]$Force)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is far slower with the progress bar
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$tools = Join-Path (Split-Path $PSScriptRoot -Parent) 'tools'
$work = Join-Path $tools '.download'
New-Item -ItemType Directory -Force $tools, $work | Out-Null

function Get-Checked([string]$Url, [string]$SumsUrl, [string]$Name, [string]$OutFile) {
    Write-Host "Downloading $Name"
    Invoke-WebRequest -Uri $Url -OutFile $OutFile -UseBasicParsing
    $sums = (Invoke-WebRequest -Uri $SumsUrl -UseBasicParsing).Content
    if ($sums -is [byte[]]) { $sums = [Text.Encoding]::UTF8.GetString($sums) }
    # Either "hash  name" lines (sha256sum) or a single "Hash : ..." block (Get-FileHash).
    $line = $sums -split "`n" | Where-Object { $_ -match "\s\*?$([regex]::Escape($Name))\s*$" } | Select-Object -First 1
    if ($line) { $expected = ($line -split '\s+')[0].ToLower() }
    elseif ($sums -match 'Hash\s*:\s*([0-9A-Fa-f]{64})') { $expected = $Matches[1].ToLower() }
    else { throw "No checksum listed for $Name" }
    $actual = (Get-FileHash $OutFile -Algorithm SHA256).Hash.ToLower()
    if ($expected -ne $actual) {
        Remove-Item $OutFile -Force
        throw "Checksum mismatch for $Name (expected $expected, got $actual)"
    }
    Write-Host "  SHA-256 ok: $actual"
}

# The folder build (yt-dlp_win.zip) rather than the single yt-dlp.exe: the single file unpacks
# itself on every run, which adds about a second before each video starts.
$ytdlp = Join-Path $tools 'yt-dlp\yt-dlp.exe'
if ($Force -or -not (Test-Path $ytdlp)) {
    $base = 'https://github.com/yt-dlp/yt-dlp/releases/latest/download'
    $zip = Join-Path $work 'yt-dlp_win.zip'
    Get-Checked "$base/yt-dlp_win.zip" "$base/SHA2-256SUMS" 'yt-dlp_win.zip' $zip
    $dir = Join-Path $tools 'yt-dlp'
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    Expand-Archive $zip $dir
    $old = Join-Path $tools 'yt-dlp.exe'
    if (Test-Path $old) { Remove-Item $old -Force }
} else { Write-Host "yt-dlp already present (use -Force to refresh)" }

# The shared build: ffmpeg.exe and ffprobe.exe use one set of codec DLLs instead of each carrying
# its own copy (about 150 MB less than the static build).
$ffmpeg = Join-Path $tools 'ffmpeg.exe'
$shared = Get-ChildItem $tools -Filter 'avcodec-*.dll' -ErrorAction SilentlyContinue
if ($Force -or -not (Test-Path $ffmpeg) -or -not $shared) {
    $base = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest'
    $zipName = 'ffmpeg-master-latest-win64-lgpl-shared.zip'
    $zip = Join-Path $work $zipName
    Get-Checked "$base/$zipName" "$base/checksums.sha256" $zipName $zip
    $unpacked = Join-Path $work 'ffmpeg'
    if (Test-Path $unpacked) { Remove-Item $unpacked -Recurse -Force }
    Expand-Archive $zip $unpacked
    $bin = (Get-ChildItem $unpacked -Recurse -Filter 'ffmpeg.exe' | Select-Object -First 1).DirectoryName
    Get-ChildItem $tools -Filter '*.dll' | Remove-Item -Force
    foreach ($exe in 'ffmpeg.exe', 'ffprobe.exe') { Copy-Item (Join-Path $bin $exe) $tools -Force }
    Get-ChildItem $bin -Filter '*.dll' | Copy-Item -Destination $tools -Force
    $license = Get-ChildItem $unpacked -Recurse -Filter 'LICENSE*' | Select-Object -First 1
    if ($license) { Copy-Item $license.FullName (Join-Path $tools 'ffmpeg-LICENSE.txt') -Force }
} else { Write-Host "ffmpeg already present (use -Force to refresh)" }

Remove-Item $work -Recurse -Force
& $ytdlp --version | ForEach-Object { Write-Host "yt-dlp $_" }
& $ffmpeg -hide_banner -version | Select-Object -First 1 | ForEach-Object { Write-Host $_ }
