# Renders the app icon from its artwork into every size the project uses:
#   Windows app:  windows/OpenDownloadManager/Assets/app.ico (16 to 256) and app.png (256)
#                 windows/OpenDownloadManager/Assets/tray-light.ico and tray-dark.ico (16 to 48)
#   Extension:    extension/icons/16, 32, 48, 128 .png (the extension's own icon)
#                 extension/icons/toolbar-light-16/32.png and toolbar-dark-16/32.png (toolbar button)
#   Master:       scripts/icon/icon-master.png (1024, transparent; made once by cutout-master.py)
#
# icon-master.png is the full artwork (40 px and up, Lanczos-downscaled); icon-small.svg is drawn for 32 px and below.
# icon-mono.svg is the one-colour glyph for the tray and the browser toolbar, rendered twice: a dark
# glyph for light taskbars and toolbars ("light"), a white one for dark ones ("dark").
# The SVGs are rasterised by headless Microsoft Edge (or Chrome) on a transparent background, large,
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

# Lanczos-3 downscaling of the PNG master, in premultiplied alpha so the transparent surround
# cannot darken the edge, with an optional light unsharp mask for the small sizes.
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System; using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices;
public static class OdmLanczos {
    static double K(double x) { x = Math.Abs(x); if (x < 1e-9) return 1; if (x >= 3) return 0; double p = Math.PI * x; return 3 * Math.Sin(p) * Math.Sin(p / 3) / (p * p); }
    static double[] Pass(double[] src, int sw, int sh, int dw, bool horiz) {
        int n = horiz ? sw : sh, m = dw, ow = horiz ? dw : sw, oh = horiz ? sh : dw;
        double scale = (double)n / m, sup = 3 * scale; var dst = new double[ow * oh * 4];
        for (int o = 0; o < m; o++) {
            double c = (o + 0.5) * scale; int lo = (int)Math.Floor(c - sup), hi = (int)Math.Ceiling(c + sup);
            var w = new double[hi - lo + 1]; double sum = 0;
            for (int i = lo; i <= hi; i++) { w[i - lo] = K((i + 0.5 - c) / scale); sum += w[i - lo]; }
            int lines = horiz ? sh : sw;
            for (int l = 0; l < lines; l++) for (int ch = 0; ch < 4; ch++) {
                double acc = 0;
                for (int i = lo; i <= hi; i++) { int s = Math.Min(n - 1, Math.Max(0, i)); acc += w[i - lo] * (horiz ? src[(l * sw + s) * 4 + ch] : src[(s * sw + l) * 4 + ch]); }
                if (horiz) dst[(l * ow + o) * 4 + ch] = acc / sum; else dst[(o * ow + l) * 4 + ch] = acc / sum;
            }
        }
        return dst;
    }
    public static void Resize(string from, int size, double sharpen, string to) {
        double[] px; int w, h;
        using (var src = new Bitmap(from)) {
            w = src.Width; h = src.Height; px = new double[w * h * 4];
            var d = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var b = new byte[w * h * 4]; Marshal.Copy(d.Scan0, b, 0, b.Length); src.UnlockBits(d);
            for (int i = 0; i < w * h; i++) { double a = b[i * 4 + 3] / 255.0; px[i * 4] = b[i * 4] * a; px[i * 4 + 1] = b[i * 4 + 1] * a; px[i * 4 + 2] = b[i * 4 + 2] * a; px[i * 4 + 3] = b[i * 4 + 3]; }
        }
        var r = Pass(Pass(px, w, h, size, true), size, h, size, false);
        if (sharpen > 0) {   // unsharp mask: original + amount * (original - 3x3 blur)
            var s = (double[])r.Clone();
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) for (int ch = 0; ch < 4; ch++) {
                double blur = 0; int cnt = 0;
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) { int yy = Math.Min(size - 1, Math.Max(0, y + dy)), xx = Math.Min(size - 1, Math.Max(0, x + dx)); blur += r[(yy * size + xx) * 4 + ch]; cnt++; }
                s[(y * size + x) * 4 + ch] = r[(y * size + x) * 4 + ch] + sharpen * (r[(y * size + x) * 4 + ch] - blur / cnt);
            }
            r = s;
        }
        var outB = new byte[size * size * 4];
        for (int i = 0; i < size * size; i++) {
            double a = Math.Min(255, Math.Max(0, r[i * 4 + 3]));
            for (int ch = 0; ch < 3; ch++) { double v = a > 0.5 ? r[i * 4 + ch] * 255 / a : 0; v = Math.Min(255, Math.Max(0, v)); outB[i * 4 + ch] = (byte)Math.Round(v); }
            outB[i * 4 + 3] = (byte)Math.Round(a);
        }
        using (var dst = new Bitmap(size, size, PixelFormat.Format32bppArgb)) {
            var d = dst.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            Marshal.Copy(outB, 0, d.Scan0, outB.Length); dst.UnlockBits(d); dst.Save(to, ImageFormat.Png);
        }
    }
}
'@

# ---- the colour icon: the app, its windows and taskbar button, the extension's own icon ----
# 40 px and up come from icon-master.png (the artwork cut out of source/icon-master.jpg by
# cutout-master.py); 32 px and below from icon-small.svg, drawn to read at that size.
$master = Join-Path $PSScriptRoot 'icon-master.png'
$small = Join-Path $out 'icon-small-512.png'
Render-Svg (Join-Path $PSScriptRoot 'icon-small.svg') 512 $small
function Save-Colour([int]$size, [string]$path) {
    if ($size -le 32) { Save-Size $small $size $path }
    else { [OdmLanczos]::Resize($master, $size, $(if ($size -le 64) { 0.35 } else { 0 }), $path) }
}

$icoSizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
foreach ($s in $icoSizes) { Save-Colour $s (Join-Path $out "win-$s.png") }
Copy-Item (Join-Path $out 'win-256.png') (Join-Path $assets 'app.png') -Force
Write-Ico (Join-Path $assets 'app.ico') $icoSizes 'win'
foreach ($s in 16, 32, 48, 128) { Save-Colour $s (Join-Path $extIcons "$s.png") }

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
Write-Host "Icons written: app.ico, app.png, tray-light.ico, tray-dark.ico, extension\icons from $master"
