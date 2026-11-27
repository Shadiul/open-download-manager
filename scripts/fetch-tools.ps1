# Downloads the tools the app bundles into tools\ (git-ignored), checking each file against the
# SHA-256 list its project publishes with the release.
#
#   yt-dlp.exe              https://github.com/yt-dlp/yt-dlp (Unlicense)
#   ffmpeg.exe, ffprobe.exe https://github.com/BtbN/FFmpeg-Builds, LGPL build
#   deno.exe                https://github.com/denoland/deno (MIT), the JavaScript runtime
#                           yt-dlp needs for full YouTube support
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

$ytdlp = Join-Path $tools 'yt-dlp.exe'
if ($Force -or -not (Test-Path $ytdlp)) {
    $base = 'https://github.com/yt-dlp/yt-dlp/releases/latest/download'
    Get-Checked "$base/yt-dlp.exe" "$base/SHA2-256SUMS" 'yt-dlp.exe' $ytdlp
} else { Write-Host "yt-dlp.exe already present (use -Force to refresh)" }

$ffmpeg = Join-Path $tools 'ffmpeg.exe'
if ($Force -or -not (Test-Path $ffmpeg)) {
    $base = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest'
    $zipName = 'ffmpeg-master-latest-win64-lgpl.zip'
    $zip = Join-Path $work $zipName
    Get-Checked "$base/$zipName" "$base/checksums.sha256" $zipName $zip
    $unpacked = Join-Path $work 'ffmpeg'
    if (Test-Path $unpacked) { Remove-Item $unpacked -Recurse -Force }
    Expand-Archive $zip $unpacked
    $bin = Get-ChildItem $unpacked -Recurse -Filter 'ffmpeg.exe' | Select-Object -First 1
    Copy-Item $bin.FullName $tools -Force
    Copy-Item (Join-Path $bin.DirectoryName 'ffprobe.exe') $tools -Force
    $license = Get-ChildItem $unpacked -Recurse -Filter 'LICENSE*' | Select-Object -First 1
    if ($license) { Copy-Item $license.FullName (Join-Path $tools 'ffmpeg-LICENSE.txt') -Force }
} else { Write-Host "ffmpeg.exe already present (use -Force to refresh)" }

$deno = Join-Path $tools 'deno.exe'
if ($Force -or -not (Test-Path $deno)) {
    $base = 'https://github.com/denoland/deno/releases/latest/download'
    $zipName = 'deno-x86_64-pc-windows-msvc.zip'
    $zip = Join-Path $work $zipName
    Get-Checked "$base/$zipName" "$base/$zipName.sha256sum" $zipName $zip
    Expand-Archive $zip $work -Force
    Copy-Item (Join-Path $work 'deno.exe') $tools -Force
} else { Write-Host "deno.exe already present (use -Force to refresh)" }

Remove-Item $work -Recurse -Force
& $ytdlp --version | ForEach-Object { Write-Host "yt-dlp $_" }
& $deno --version | Select-Object -First 1 | ForEach-Object { Write-Host $_ }
& $ffmpeg -hide_banner -version | Select-Object -First 1 | ForEach-Object { Write-Host $_ }
