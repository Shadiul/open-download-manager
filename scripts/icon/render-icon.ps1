# Renders the app icon from its SVG artwork into every size the project uses:
#   Windows app:  windows/OpenDownloadManager/Assets/app.ico (16 to 256) and app.png (256)
#                 windows/OpenDownloadManager/Assets/tray-light.ico and tray-dark.ico (16 to 48)
#   Extension:    extension/icons/16, 32, 48, 128 .png (the extension's own icon)
#                 extension/icons/toolbar-light-16/32.png and toolbar-dark-16/32.png (toolbar button)
#   README/macOS: scripts/icon/icon-master.png
#   Smoke icon:   windows/OpenDownloadManager/Assets/app-smoke.ico and app-smoke.png (the second variant)
#
# icon-master.png ("Orbit") and icon-smoke-master.png ("Smoke") are the colour artwork at every size: the
# artwork renders cut out by cutout-master.py, scaled down with Lanczos (Lanczos.cs).
# icon-mono.svg is the one-colour glyph for the tray and the browser toolbar, rendered twice: a dark
# glyph for light taskbars and toolbars ("light"), a white one for dark ones ("dark").
# All are rasterised by headless Microsoft Edge (or Chrome) on a transparent background, large,
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
$assets = Join-Path $root 'windows\OpenDownloadManager\Assets'
$extIcons = Join-Path $root 'extension\icons'

# One large, transparent rendering of an SVG file.
function Render-Svg([string]$svg, [int]$size, [string]$png) {
    $page = Join-Path $out "render-$size.html"
    $src = 'file:///' + ($svg -replace '\\', '/')
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

# A scaled copy of a large rendering.
function Save-Size([string]$source, [int]$size, [string]$path) {
    $img = [Drawing.Image]::FromFile($source)
    $bmp = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.DrawImage($img, 0, 0, $size, $size)
    $g.Dispose(); $img.Dispose()
    $bmp.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# An .ico with PNG frames (supported since Windows Vista).
function Write-Ico([string]$ico, [int[]]$sizes, [string]$prefix) {
    $frames = $sizes | ForEach-Object { , [IO.File]::ReadAllBytes((Join-Path $out "$prefix-$_.png")) }
    $ms = New-Object IO.MemoryStream
    $w = New-Object IO.BinaryWriter $ms
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]; $len = $frames[$i].Length
        $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
        $offset += $len
    }
    foreach ($f in $frames) { $w.Write($f) }
    [IO.File]::WriteAllBytes($ico, $ms.ToArray())
}

# ---- the colour icon: the app, its windows and taskbar button, the extension's own icon ----
$full = Join-Path $PSScriptRoot 'icon-master.png'   # the Orbit cut-out, 1024 px
# Every size, the taskbar's 16 to 32 px included, is the artwork scaled down (the user's choice over a
# separate small drawing, so the taskbar matches the big icon).
function Colour-Source([int]$size) { $full }
# A PNG master is scaled with Lanczos, lightly sharpened up to 64 px; other sources with Save-Size.
Add-Type -ReferencedAssemblies System.Drawing -Path (Join-Path $PSScriptRoot 'Lanczos.cs')
function Save-Colour([string]$source, [int]$size, [string]$path) {
    if ($source -like '*-master.png') { [OdmLanczos]::Resize($source, $size, $(if ($size -le 64) { 0.35 } else { 0 }), $path) }
    else { Save-Size $source $size $path }
}

$icoSizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
foreach ($s in $icoSizes) { Save-Colour (Colour-Source $s) $s (Join-Path $out "win-$s.png") }
Copy-Item (Join-Path $out 'win-256.png') (Join-Path $assets 'app.png') -Force
Write-Ico (Join-Path $assets 'app.ico') $icoSizes 'win'
foreach ($s in 16, 32, 48, 128) { Save-Colour (Colour-Source $s) $s (Join-Path $extIcons "$s.png") }

# The Smoke variant (app-smoke.ico, app-smoke.png), from its own artwork at every size.
$smoke = Join-Path $PSScriptRoot 'icon-smoke-master.png'
foreach ($s in $icoSizes) { Save-Colour $smoke $s (Join-Path $out "win-smoke-$s.png") }
Copy-Item (Join-Path $out 'win-smoke-256.png') (Join-Path $assets 'app-smoke.png') -Force
Write-Ico (Join-Path $assets 'app-smoke.ico') $icoSizes 'win-smoke'

# ---- the one-colour icon: the tray and the browser toolbar, one set per theme ----
$mono = Get-Content (Join-Path $PSScriptRoot 'icon-mono.svg') -Raw
foreach ($theme in @(@('light', '#1A1A1A'), @('dark', '#FFFFFF'))) {
    $name = $theme[0]
    $svg = Join-Path $out "icon-mono-$name.svg"
    Set-Content $svg ($mono -replace '#000000', $theme[1]) -Encoding utf8
    $big = Join-Path $out "icon-mono-$name-512.png"
    Render-Svg $svg 512 $big
    # Tray: 16 px at 100% scale up to 32 at 200%, and 40/48 for 250/300%.
    $traySizes = 16, 20, 24, 32, 40, 48
    foreach ($s in $traySizes) { Save-Size $big $s (Join-Path $out "tray-$name-$s.png") }
    Write-Ico (Join-Path $assets "tray-$name.ico") $traySizes "tray-$name"
    foreach ($s in 16, 32) { Save-Size $big $s (Join-Path $extIcons "toolbar-$name-$s.png") }
}

Remove-Item $profile -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Icons written: app.ico, app.png, app-smoke.ico, app-smoke.png, tray-light.ico, tray-dark.ico, extension\icons"
