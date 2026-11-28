# Renders the app icon from its SVG artwork into every size the project uses:
#   Windows app:  windows/OpenDownloadManager/Assets/app.ico (16 to 256) and app.png (256)
#                 windows/OpenDownloadManager/Assets/tray-light.ico and tray-dark.ico (16 to 48)
#                 and the development copy's purple versions: app-dev.ico/.png, tray-dev-light/-dark.ico
#   Extension:    extension/icons/16, 32, 48, 128 .png (the extension's own icon)
#                 extension/icons/toolbar-light-16/32.png and toolbar-dark-16/32.png (toolbar button)
#   README/macOS: scripts/icon/out/icon-1024.png
#
# icon.svg is the full artwork, used from 40 px up. The sizes of 32 px and below use the same artwork
# without its tile, framed tight (see Small-Svg), so the globe and rocket fill the small sizes.
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

# The development copy's colours: every saturated colour of the artwork turned 70 degrees round the
# colour wheel, so the blue globe turns purple and stands apart from the installed app in the taskbar.
function Dev-Colours([string]$svgText) {
    [regex]::Replace($svgText, '#([0-9A-Fa-f]{6})(?![0-9A-Fa-f])', {
        param($m)
        $c = [Drawing.ColorTranslator]::FromHtml('#' + $m.Groups[1].Value)
        $h = $c.GetHue(); $s = $c.GetSaturation(); $l = $c.GetBrightness()
        if ($s -lt 0.15) { return $m.Value }
        $h = ($h + 70) % 360
        $q = if ($l -lt 0.5) { $l * (1 + $s) } else { $l + $s - $l * $s }; $p = 2 * $l - $q
        $rgb = foreach ($t in ($h / 360 + 1 / 3), ($h / 360), ($h / 360 - 1 / 3)) {
            if ($t -lt 0) { $t += 1 }; if ($t -gt 1) { $t -= 1 }
            $v = if ($t -lt 1 / 6) { $p + ($q - $p) * 6 * $t } elseif ($t -lt 1 / 2) { $q } elseif ($t -lt 2 / 3) { $p + ($q - $p) * (2 / 3 - $t) * 6 } else { $p }
            [int][Math]::Round($v * 255)
        }
        '#{0:X2}{1:X2}{2:X2}' -f $rgb[0], $rgb[1], $rgb[2]
    })
}

# The tile-less artwork for 32 px and below: icon.svg without its group id="tile" or the tile's clip,
# framed square and tight around what is drawn (nothing cut off, no padding), found from a wide rendering.
function Small-Svg([string]$svgText, [string]$path) {
    $art = [regex]::Replace($svgText, '(?s)<g id="tile".*?\n  </g>\r?\n', '')
    $art = $art.Replace('<g id="art" clip-path="url(#v3_sqClip)">', '<g id="art">')
    # Render a 1536-unit square around the 1024 canvas at 512 px (3 units a pixel) and find the drawn pixels.
    Set-Content $path (([regex]'viewBox="[^"]*"').Replace($art, 'viewBox="-256 -256 1536 1536"', 1)) -Encoding utf8
    $probe = Join-Path $out 'small-probe.png'
    Remove-Item $probe -ErrorAction SilentlyContinue
    Render-Svg $path 512 $probe
    $bmp = [Drawing.Bitmap]::FromFile($probe)
    $data = $bmp.LockBits((New-Object Drawing.Rectangle 0, 0, 512, 512), [Drawing.Imaging.ImageLockMode]::ReadOnly, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bytes = New-Object byte[] (512 * 512 * 4)
    [Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    $bmp.UnlockBits($data); $bmp.Dispose(); Remove-Item $probe
    $x0 = 512; $y0 = 512; $x1 = -1; $y1 = -1
    for ($i = 3; $i -lt $bytes.Length; $i += 4) {
        if ($bytes[$i] -gt 8) {   # ignore the last faint edge of the shadows
            $p = ($i - 3) / 4; $x = $p % 512; $y = [Math]::Floor($p / 512)
            if ($x -lt $x0) { $x0 = $x }; if ($x -gt $x1) { $x1 = $x }; if ($y -lt $y0) { $y0 = $y }; if ($y -gt $y1) { $y1 = $y }
        }
    }
    $side = 3 * [Math]::Max($x1 - $x0 + 1, $y1 - $y0 + 1)
    $cx = -256 + 3 * ($x0 + $x1 + 1) / 2; $cy = -256 + 3 * ($y0 + $y1 + 1) / 2
    $box = '{0:0.#} {1:0.#} {2:0.#} {2:0.#}' -f ($cx - $side / 2), ($cy - $side / 2), $side
    Set-Content $path (([regex]'viewBox="[^"]*"').Replace($art, "viewBox=`"$box`"", 1)) -Encoding utf8
}

# ---- the colour icon: the app, its windows and taskbar button, the extension's own icon ----
# Rendered twice: the everyday icon (app.ico, app.png) and the development copy's (app-dev.ico, app-dev.png).
$icoSizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
foreach ($variant in '', '-dev') {
    $text = Get-Content (Join-Path $PSScriptRoot 'icon.svg') -Raw
    if ($variant) { $text = Dev-Colours $text }
    $fullSvg = Join-Path $out "icon$variant.svg"
    $smallSvg = Join-Path $out "icon-small$variant.svg"
    Set-Content $fullSvg $text -Encoding utf8
    Small-Svg $text $smallSvg
    $full = Join-Path $out "icon$variant-1024.png"
    $small = Join-Path $out "icon-small$variant-512.png"
    Render-Svg $fullSvg 1024 $full
    Render-Svg $smallSvg 512 $small
    # The tile-less artwork up to 32 px, the full icon above.
    foreach ($s in $icoSizes) { Save-Size $(if ($s -le 32) { $small } else { $full }) $s (Join-Path $out "win$variant-$s.png") }
    Copy-Item (Join-Path $out "win$variant-256.png") (Join-Path $assets "app$variant.png") -Force
    Write-Ico (Join-Path $assets "app$variant.ico") $icoSizes "win$variant"
    if (-not $variant) { foreach ($s in 16, 32, 48, 128) { Save-Size $(if ($s -le 32) { $small } else { $full }) $s (Join-Path $extIcons "$s.png") } }
}
$full = Join-Path $out 'icon-1024.png'

# ---- the one-colour icon: the tray and the browser toolbar, one set per theme ----
# The development copy's tray glyph is purple instead (tray-dev-light.ico, tray-dev-dark.ico).
$mono = Get-Content (Join-Path $PSScriptRoot 'icon-mono.svg') -Raw
$traySizes = 16, 20, 24, 32, 40, 48
foreach ($theme in @(@('light', '#1A1A1A'), @('dark', '#FFFFFF'), @('dev-light', '#7A2BD9'), @('dev-dark', '#B98CFF'))) {
    $name = $theme[0]
    $svg = Join-Path $out "icon-mono-$name.svg"
    Set-Content $svg ($mono -replace '#000000', $theme[1]) -Encoding utf8
    $big = Join-Path $out "icon-mono-$name-512.png"
    Render-Svg $svg 512 $big
    # Tray: 16 px at 100% scale up to 32 at 200%, and 40/48 for 250/300%.
    foreach ($s in $traySizes) { Save-Size $big $s (Join-Path $out "tray-$name-$s.png") }
    Write-Ico (Join-Path $assets "tray-$name.ico") $traySizes "tray-$name"
    if ($name -notlike 'dev-*') { foreach ($s in 16, 32) { Save-Size $big $s (Join-Path $extIcons "toolbar-$name-$s.png") } }
}

Remove-Item $profile -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Icons written: app.ico, app.png, tray-light.ico, tray-dark.ico, their -dev copies, extension\icons, $full"
