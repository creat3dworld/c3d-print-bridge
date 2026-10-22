# C3D Print Bridge — uninstaller (Windows).
#
# Removes the Scheduled Task, stops the bridge, and deletes the install
# folder (default C:\C3D_Bridge). With -KeepConfig, bridge-config.json
# and bridge-key.txt are first copied to Desktop\C3DBridge-backup.
# Run from PowerShell (as administrator if C:\C3D_Bridge needs it):
#   powershell -NoProfile -ExecutionPolicy Bypass -File "C:\C3D_Bridge\Uninstall-C3DBridge.ps1"
# Or right-click the tray icon and choose Uninstall...

param(
    [string]$InstallDir = "C:\C3D_Bridge",
    [switch]$KeepConfig
)

# Self-elevate: removing C:\C3D_Bridge and the Scheduled Task need
# administrator rights; run with ExecutionPolicy Bypass (no permanent change).
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $elevArgs = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -InstallDir `"$InstallDir`""
    if ($KeepConfig) { $elevArgs += " -KeepConfig" }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $elevArgs
    exit
}

$ErrorActionPreference = 'Stop'
$appDir = Join-Path $InstallDir 'app'
Write-Host '=== C3D Print Bridge uninstaller ===' -ForegroundColor Cyan
Write-Host "Install folder: $InstallDir"

Stop-ScheduledTask -TaskName 'C3D Print Bridge' -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName 'C3D Print Bridge' -Confirm:$false -ErrorAction SilentlyContinue
Get-Process C3DBridge -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

if ($KeepConfig) {
    $backup = Join-Path ([Environment]::GetFolderPath('Desktop')) 'C3DBridge-backup'
    New-Item -ItemType Directory -Force -Path $backup | Out-Null
    Copy-Item (Join-Path $appDir 'bridge-config.json') $backup -Force -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $appDir 'bridge-key.txt') $backup -Force -ErrorAction SilentlyContinue
    Write-Host "Config/key backed up to: $backup" -ForegroundColor Green
}

if (Test-Path $InstallDir) {
    Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host 'Uninstalled: task removed, bridge stopped, install folder deleted.' -ForegroundColor Green
