# Renders the installer's wizard images from the app icon (scripts/icon/icon-master.png):
#   wizard-<scale>.png       the side panel on the Finish page, light (and Windows in light mode)
#   wizard-dark-<scale>.png  the same for dark mode
#   small-<scale>.png        the icon at the top right of the other pages (transparent, both modes)
# at every scale Inno Setup picks from (100, 125, 150, 175, 200, 225 and 250 %), so they stay sharp.
# The PNGs are committed; run this again after the icon changes.
#
# Rendered by headless Microsoft Edge (or Chrome), as scripts/icon/render-icon.ps1 does.
# Usage: powershell -ExecutionPolicy Bypass -File installer\art\render.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent

$browser = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
             "$env:ProgramFiles\Google\Chrome\Application\chrome.exe", "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $browser) { throw 'Microsoft Edge or Google Chrome is needed to render the artwork.' }

$work = Join-Path ([IO.Path]::GetTempPath()) 'odm-installer-art'
New-Item -ItemType Directory -Force $work | Out-Null
$icon = 'file:///' + ((Join-Path $root 'scripts\icon\icon-master.png') -replace '\\', '/')

# Image areas from Inno Setup's help (WizardImageFile, WizardSmallImageFile), per scale.
#           scale, side panel w x h, small image
$scales = @(@(100, 202, 386, 58), @(125, 269, 515, 77), @(150, 336, 643, 97), @(175, 403, 772, 116),
            @(200, 430, 824, 124), @(225, 498, 953, 143), @(250, 534, 1022, 159))

# The side panel: the icon on a soft field in its own colours, with rings spreading from it.
# Sized in viewport units, so one page renders every scale.
function Panel([bool]$dark) {
    $bg = if ($dark) { 'linear-gradient(170deg,#12323C 0%,#0B222A 55%,#071519 100%)' } else { 'linear-gradient(170deg,#F4FBFD 0%,#DEF2F7 50%,#BEE1EC 100%)' }
    $glow = if ($dark) { 'rgba(45,212,191,.30)' } else { 'rgba(255,255,255,.95)' }
    $ring = if ($dark) { 'rgba(118,239,227,.16)' } else { 'rgba(74,143,168,.16)' }
    $shadow = if ($dark) { 'rgba(0,0,0,.45)' } else { 'rgba(42,110,135,.28)' }
    @"
<!doctype html><html><head><style>
html,body{margin:0;width:100vw;height:100vh;overflow:hidden}
body{background:$bg;position:relative}
.glow{position:absolute;left:50%;top:44%;width:150vw;height:150vw;transform:translate(-50%,-50%);border-radius:50%;
  background:radial-gradient(circle,$glow 0%,transparent 62%)}
.ring{position:absolute;left:50%;top:44%;transform:translate(-50%,-50%);border-radius:50%;border:.6vw solid $ring}
img{position:absolute;left:50%;top:44%;width:64vw;height:64vw;transform:translate(-50%,-50%);
  filter:drop-shadow(0 3vw 5vw $shadow)}
</style></head><body><div class="glow"></div>
<div class="ring" style="width:92vw;height:92vw"></div>
<div class="ring" style="width:128vw;height:128vw;opacity:.7"></div>
<div class="ring" style="width:168vw;height:168vw;opacity:.45"></div>
<div class="ring" style="width:212vw;height:212vw;opacity:.25"></div>
<img src="$icon"></body></html>
"@
}

$small = "<!doctype html><html><body style=`"margin:0;background:transparent`"><img src=`"$icon`" style=`"display:block;width:100vw;height:100vh`"></body></html>"

function Render([string]$html, [int]$w, [int]$h, [string]$png) {
    $page = Join-Path $work 'page.html'
    Set-Content $page $html -Encoding utf8
    if (Test-Path $png) { Remove-Item $png }
    $bargs = @('--headless=new', '--disable-gpu', '--hide-scrollbars', '--force-device-scale-factor=1', "--window-size=$w,$h",
              '--default-background-color=00000000', '--virtual-time-budget=2000', "--user-data-dir=`"$(Join-Path $work 'profile')`"",
              "--screenshot=`"$png`"", ('file:///' + ($page -replace '\\', '/')))
    Start-Process $browser -ArgumentList $bargs -Wait -WindowStyle Hidden
    for ($i = 0; $i -lt 50 -and -not (Test-Path $png); $i++) { Start-Sleep -Milliseconds 100 }
    if (-not (Test-Path $png)) { throw "Could not render $png" }
}

# Headless browsers keep a minimum window width, so each image is rendered once, large (twice the
# 250 % size; every scale has nearly the same proportions), then scaled down to each size.
$light = Join-Path $work 'wizard.png'; $dark = Join-Path $work 'wizard-dark.png'; $mark = Join-Path $work 'small.png'
Render (Panel $false) 1068 2044 $light
Render (Panel $true) 1068 2044 $dark
Render $small 640 640 $mark

Add-Type -AssemblyName System.Drawing
function Save-Scaled([string]$from, [int]$w, [int]$h, [string]$to) {
    $source = [Drawing.Image]::FromFile($from)
    $bmp = New-Object Drawing.Bitmap $w, $h, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.DrawImage($source, 0, 0, $w, $h)
    $g.Dispose(); $source.Dispose()
    $bmp.Save($to, [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}
foreach ($row in $scales) {
    $s, $w, $h, $sq = $row
    Save-Scaled $light $w $h (Join-Path $PSScriptRoot "wizard-$s.png")
    Save-Scaled $dark $w $h (Join-Path $PSScriptRoot "wizard-dark-$s.png")
    Save-Scaled $mark $sq $sq (Join-Path $PSScriptRoot "small-$s.png")
    "saved $s %"
}
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
