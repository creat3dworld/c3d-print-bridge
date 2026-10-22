# C3D Print Bridge — repair and reinstall (Windows).
#
# REPAIR / REINSTALL: keeps config files and updates the files.
# Uninstalls the bridge, then reinstalls it fresh, KEEPING your printers
# and key: bridge-config.json and bridge-key.txt are backed up to
# Desktop\C3DBridge-backup first, the install is removed, the installer
# runs again, and the backup is restored into the new app folder.
# This is the "repair" uninstaller — use it when the app files are
# broken but you want to keep your setup.
# The "fresh start" uninstaller is Uninstall-C3DBridge.ps1, which
# removes everything and does NOT reinstall or restore your config.
#
# Run from the bridge source folder (where Install-C3DBridge.ps1 lives),
# or from the install folder if the installer copied this script there.
# Run as administrator if C:\C3D_Bridge needs it; this script
# self-elevates like the installer does.

param(
    [string]$InstallDir = "C:\C3D_Bridge",
    [string]$SourceDir = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -InstallDir `"$InstallDir`" -SourceDir `"$SourceDir`""
    exit
}

$appDir = Join-Path $InstallDir 'app'
$backup = Join-Path ([Environment]::GetFolderPath('Desktop')) 'C3DBridge-backup'
Write-Host '=== C3D Print Bridge repair and reinstall ===' -ForegroundColor Cyan

# Find the installer: same folder as this script, or a source folder next to the install.
$installer = Join-Path $SourceDir 'Install-C3DBridge.ps1'
if (-not (Test-Path $installer)) {
    $alt = Join-Path $InstallDir 'source\Install-C3DBridge.ps1'
    if (Test-Path $alt) { $installer = $alt; $SourceDir = Join-Path $InstallDir 'source' }
    $altRoot = Join-Path $InstallDir 'Install-C3DBridge.ps1'
    if (-not (Test-Path $installer) -and (Test-Path $altRoot)) { $installer = $altRoot; $SourceDir = $InstallDir }
}
if (-not (Test-Path $installer)) {
    throw "Installer not found. Run this script from the bridge source folder (where Install-C3DBridge.ps1 lives)."
}

# 1. Back up config/key.
New-Item -ItemType Directory -Force -Path $backup | Out-Null
Copy-Item (Join-Path $appDir 'bridge-config.json') $backup -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $appDir 'bridge-key.txt') $backup -Force -ErrorAction SilentlyContinue
Write-Host "Config/key backed up to: $backup" -ForegroundColor Green

# 2. Uninstall (task, process, install folder).
Stop-ScheduledTask -TaskName 'C3D Print Bridge' -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName 'C3D Print Bridge' -Confirm:$false -ErrorAction SilentlyContinue
Get-Process C3DBridge -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue }

# 3. Reinstall fresh.
Write-Host 'Reinstalling...' -ForegroundColor Cyan
powershell -NoProfile -ExecutionPolicy Bypass -File $installer -InstallDir $InstallDir -SourceDir $SourceDir

# 4. Restore the backup into the new app folder (installer creates an
#    example config if none exists; the backup wins).
$newApp = Join-Path $InstallDir 'app'
Copy-Item (Join-Path $backup 'bridge-config.json') $newApp -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $backup 'bridge-key.txt') $newApp -Force -ErrorAction SilentlyContinue
# Shield surviving copies: once restored into the encrypted
# config/key files, delete the Desktop backup so no extra copy
# of credentials survives the reinstall.
if ((Test-Path (Join-Path $newApp 'bridge-config.json')) -or (Test-Path (Join-Path $newApp 'bridge-key.txt'))) { Remove-Item $backup -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host 'Repair complete: fresh app files, your printers and key restored.' -ForegroundColor Green
Write-Host "Starting the bridge task..." -ForegroundColor Cyan
Start-ScheduledTask -TaskName 'C3D Print Bridge' -ErrorAction SilentlyContinue
