$name = 'org.opendownloadmanager.host'
foreach ($browser in 'Google\Chrome', 'Microsoft\Edge') {
  Remove-Item -Path "HKCU:\Software\$browser\NativeMessagingHosts\$name" -Force -ErrorAction SilentlyContinue
}
Write-Host "Removed $name"
