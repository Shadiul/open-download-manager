# Registers the native messaging host for Chrome and Edge for the current user.
# Usage:  powershell -ExecutionPolicy Bypass -File install-windows.ps1 [-HostPath C:\path\to\host.exe]
param(
  [string]$HostPath = (Join-Path $PSScriptRoot 'test_host.bat'),
  [string]$ExtensionId = 'ohammiojomdnbnfhigkofgahnghdmolg'
)
$ErrorActionPreference = 'Stop'
$name = 'org.opendownloadmanager.host'
$manifestPath = Join-Path $PSScriptRoot "$name.json"

$manifest = [ordered]@{
  name            = $name
  description     = 'Open Download Manager'
  path            = (Resolve-Path $HostPath).Path
  type            = 'stdio'
  allowed_origins = @("chrome-extension://$ExtensionId/")
}
# Written without a byte order mark, which Windows PowerShell 5 would otherwise add.
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))

foreach ($browser in 'Google\Chrome', 'Microsoft\Edge') {
  $key = "HKCU:\Software\$browser\NativeMessagingHosts\$name"
  New-Item -Path $key -Value $manifestPath -Force | Out-Null
  Write-Host "Registered $name for $browser"
}
