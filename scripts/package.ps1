# Builds everything a release ships into dist\release:
#
#   OpenDownloadManager-<version>-setup-x64.exe        installer (needs Inno Setup 6's iscc)
#   OpenDownloadManager-<version>-portable-x64.zip     the same app as a folder
#   OpenDownloadManager-<version>-extension-edge.zip   browser extension, Edge build (YouTube included)
#   OpenDownloadManager-<version>-extension-chrome.zip browser extension, Chrome build
#   SHA256SUMS.txt
#
# Usage: powershell -ExecutionPolicy Bypass -File scripts\package.ps1 [-SkipInstaller]
param([switch]$SkipInstaller)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$version = (node scripts/version.mjs).Trim()
if ($LASTEXITCODE -ne 0) { throw 'The version files disagree (see above).' }
Write-Host "Packaging Open Download Manager $version"

$dist = Join-Path $root 'dist'
$app = Join-Path $dist 'app'
$out = Join-Path $dist 'release'
foreach ($dir in $app, $out) { if (Test-Path $dir) { Remove-Item $dir -Recurse -Force } }
New-Item -ItemType Directory -Force $out | Out-Null

# yt-dlp, ffmpeg and deno, checked against their published hashes.
& powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'fetch-tools.ps1')
if ($LASTEXITCODE -ne 0) { throw 'fetch-tools failed' }

# The app, self-contained: the Rust core and host are built by the project file.
dotnet publish windows\OpenDownloadManager -c Release -o $app
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
foreach ($needed in 'OpenDownloadManager.exe', 'odm.dll', 'odm-host.exe', 'tools\yt-dlp\yt-dlp.exe', 'tools\ffmpeg.exe', 'tools\deno.exe') {
    if (-not (Test-Path (Join-Path $app $needed))) { throw "The published app is missing $needed" }
}
Copy-Item LICENSE (Join-Path $app 'LICENSE.txt')

Compress-Archive -Path (Join-Path $app '*') -DestinationPath (Join-Path $out "OpenDownloadManager-$version-portable-x64.zip") -CompressionLevel Optimal

if (-not $SkipInstaller) {
    $iscc = (Get-Command iscc -ErrorAction SilentlyContinue).Source
    if (-not $iscc) {
        $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
                  "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    }
    if (-not $iscc) { throw 'Inno Setup 6 (iscc) not found. Install it, or pass -SkipInstaller.' }
    & $iscc /Qp "/DAppVersion=$version" "/DSourceDir=$app" "/DOutputDir=$out" installer\OpenDownloadManager.iss
    if ($LASTEXITCODE -ne 0) { throw 'iscc failed' }
}

node scripts/build.mjs
if ($LASTEXITCODE -ne 0) { throw 'extension build failed' }
foreach ($target in 'edge', 'chrome') {
    Copy-Item (Join-Path $dist "open-download-manager-$target.zip") (Join-Path $out "OpenDownloadManager-$version-extension-$target.zip")
}

Push-Location $out
Get-ChildItem -File | Where-Object Name -ne 'SHA256SUMS.txt' | ForEach-Object {
    "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower())  $($_.Name)"
} | Set-Content SHA256SUMS.txt -Encoding ascii
Pop-Location

Get-ChildItem $out | ForEach-Object { '{0,8:N1} MB  {1}' -f ($_.Length / 1MB), $_.Name }
