# Renders the app icon in every size the project uses.
#   Windows app:  windows/OpenDownloadManager/Assets/app.ico (16 to 256) and app.png
#   Extension:    extension/icons/16, 32, 48, 128 .png
#   macOS (later): scripts/icon/out/macos-1024.png, the tile on Apple's 1024 grid with shadow
# Usage: powershell -ExecutionPolicy Bypass -File scripts\icon\render-icon.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Add-Type -AssemblyName System.Drawing
Add-Type -Path (Join-Path $PSScriptRoot 'IconRenderer.cs') -ReferencedAssemblies System.Drawing

$out = Join-Path $PSScriptRoot 'out'
New-Item -ItemType Directory -Force $out | Out-Null

function Save-Png([int]$size, [string]$path, [double]$plate = 0.92, [bool]$shadow = $false) {
    $bold = $size -le 32
    $bmp = [IconRenderer]::Render($size, $plate, $shadow, $bold)
    $bmp.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# Windows: the tile fills most of the square, like Fluent app icons.
$icoSizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
foreach ($s in $icoSizes) { Save-Png $s (Join-Path $out "win-$s.png") }
Copy-Item (Join-Path $out 'win-256.png') (Join-Path $root 'windows\OpenDownloadManager\Assets\app.png') -Force

# An .ico with PNG frames (supported since Windows Vista).
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
foreach ($s in 16, 32, 48, 128) { Save-Png $s (Join-Path $root "extension\icons\$s.png") }

# macOS: Apple's grid puts an 824 px tile with a shadow on a 1024 canvas.
Save-Png 1024 (Join-Path $out 'macos-1024.png') (824 / 1024) $true
Save-Png 1024 (Join-Path $out 'preview-1024.png')
Write-Host "Icons written: $ico, extension\icons, $out"
