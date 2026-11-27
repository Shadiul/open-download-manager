# Renders the app icon from its SVG artwork into every size the project uses:
#   Windows app:  windows/OpenDownloadManager/Assets/app.ico (16 to 256) and app.png (256)
#   Extension:    extension/icons/16, 32, 48, 128 .png
#   README/macOS: scripts/icon/out/icon-1024.png
#
# icon.svg is the full artwork (40 px and up); icon-small.svg is drawn for 32 px and below.
# Both are rasterised by headless Microsoft Edge (or Chrome) on a transparent background, large,
# then scaled down with high-quality resampling.
#
# Usage: powershell -ExecutionPolicy Bypass -File scripts\icon\render-icon.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Add-Type -AssemblyName System.Drawing

$browser = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
             "$env:ProgramFiles\Google\Chrome\Application\chrome.exe", "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $browser) { throw 'Microsoft Edge or Google Chrome is needed to render the SVG artwork.' }

$out = Join-Path $PSScriptRoot 'out'
New-Item -ItemType Directory -Force $out | Out-Null
$profile = Join-Path $out 'browser-profile'

# One large, transparent rendering of an SVG.
function Render-Svg([string]$svg, [int]$size, [string]$png) {
    $page = Join-Path $out "render-$size.html"
    $src = 'file:///' + ((Join-Path $PSScriptRoot $svg) -replace '\\', '/')
    Set-Content $page "<!doctype html><html><body style=`"margin:0;background:transparent`"><img src=`"$src`" width=`"$size`" height=`"$size`" style=`"display:block`"></body></html>" -Encoding utf8
    # Start-Process: the browser writes progress to stderr, which a direct call would turn into an error.
    $browserArgs = @('--headless=new', '--disable-gpu', '--hide-scrollbars', '--force-device-scale-factor=1', "--window-size=$size,$size",
              '--default-background-color=00000000', '--virtual-time-budget=2000', "--user-data-dir=`"$profile`"", "--screenshot=`"$png`"",
              ('file:///' + ($page -replace '\\', '/')))
    Start-Process $browser -ArgumentList $browserArgs -Wait -WindowStyle Hidden
    for ($i = 0; $i -lt 50 -and -not (Test-Path $png); $i++) { Start-Sleep -Milliseconds 100 }
    if (-not (Test-Path $png)) { throw "Could not render $svg" }
    Remove-Item $page
}

$full = Join-Path $out 'icon-1024.png'
$small = Join-Path $out 'icon-small-512.png'
Render-Svg 'icon.svg' 1024 $full
Render-Svg 'icon-small.svg' 512 $small

# A scaled copy: the small artwork up to 32 px, the full artwork above.
function Save-Size([int]$size, [string]$path) {
    $source = [Drawing.Image]::FromFile($(if ($size -le 32) { $small } else { $full }))
    $bmp = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.DrawImage($source, 0, 0, $size, $size)
    $g.Dispose(); $source.Dispose()
    $bmp.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# Windows: an .ico with PNG frames (supported since Windows Vista).
$icoSizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
foreach ($s in $icoSizes) { Save-Size $s (Join-Path $out "win-$s.png") }
Copy-Item (Join-Path $out 'win-256.png') (Join-Path $root 'windows\OpenDownloadManager\Assets\app.png') -Force
$ico = Join-Path $root 'windows\OpenDownloadManager\Assets\app.ico'
$frames = $icoSizes | ForEach-Object { , [IO.File]::ReadAllBytes((Join-Path $out "win-$_.png")) }
$ms = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $ms
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$icoSizes.Count)
$offset = 6 + 16 * $icoSizes.Count
for ($i = 0; $i -lt $icoSizes.Count; $i++) {
    $s = $icoSizes[$i]; $len = $frames[$i].Length
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($f in $frames) { $w.Write($f) }
[IO.File]::WriteAllBytes($ico, $ms.ToArray())

# Extension toolbar and store icons.
foreach ($s in 16, 32, 48, 128) { Save-Size $s (Join-Path $root "extension\icons\$s.png") }

Remove-Item $profile -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Icons written: $ico, app.png, extension\icons, $full"
