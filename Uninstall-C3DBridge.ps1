# C3D Print Bridge — fresh-start uninstaller (Windows).
#
# Removes everything and does NOT reinstall or restore your config:
# task, process, install folder, and previous install locations are
# deleted. Config/key are still copied to Desktop\C3DBridge-backup
# first as a safety net, but a fresh install will NOT pick them up
# automatically — start blank, or copy them back by hand if you
# change your mind. For a repair that keeps your setup, use
# Uninstall-Repair-Reinstall.ps1 instead.
# If PowerShell says running scripts is disabled, first run:
#   Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser -Force
# (or double-click Enable-Scripts.bat, which does that for you).
#
# FRESH START: removes ALL previous bridge registry entries and files
# (install folders, Scheduled Task, registry keys, temp files, MSI
# registration, config, key, and Desktop backup). Nothing is restored.
# Use -Force to skip the YES confirmation (the tray menu already asks).
# Run from PowerShell (as administrator if C:\C3D_Bridge needs it):
#   powershell -NoProfile -ExecutionPolicy Bypass -File "C:\C3D_Bridge\Uninstall-C3DBridge.ps1"
# Or right-click the tray icon and choose Uninstall...

param(
    [string]$InstallDir = "C:\C3D_Bridge",
    [switch]$KeepConfig,
    [switch]$Force
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

# FRESH START: remove ALL previous bridge registry entries and files.
# This deliberately does NOT keep config/key and does NOT restore
# them. For a repair that keeps your setup, use
# Uninstall-Repair-Reinstall.ps1 instead.
if (-not $Force) {
    Write-Host 'FRESH START will delete ALL of the following:' -ForegroundColor Red
    Write-Host '  - Install folders: C:\C3D_Bridge, %USERPROFILE%\C3DBridge, %LOCALAPPDATA%\C3DBridge'
    Write-Host '  - Scheduled Task "C3D Print Bridge"'
    Write-Host '  - Registry keys: HKCU/HKLM\Software\C3D Print Bridge, and any C3DBridge Run entries'
    Write-Host '  - Temp bridge files and Desktop\C3DBridge-backup'
    Write-Host '  - The MSI product registration, if present'
    Write-Host '  - bridge-config.json and bridge-key.txt (your printers and key)'
    $answer = Read-Host 'Type YES to wipe everything, or anything else to stop'
    if ($answer -ne 'YES') { Write-Host 'Fresh-start uninstall cancelled. Nothing was deleted.' -ForegroundColor Yellow; exit 0 }
}

# Scheduled Task + process first, so files are not locked.
Stop-ScheduledTask -TaskName 'C3D Print Bridge' -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName 'C3D Print Bridge' -Confirm:$false -ErrorAction SilentlyContinue
Get-Process C3DBridge -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

# Registry entries related to the bridge.
Remove-Item 'HKCU:\Software\C3D Print Bridge' -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item 'HKLM:\Software\C3D Print Bridge' -Recurse -Force -ErrorAction SilentlyContinue
foreach ($runKey in @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Run', 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run')) {
    try {
        $props = Get-ItemProperty -Path $runKey -ErrorAction SilentlyContinue
        foreach ($prop in $props.PSObject.Properties) {
            if ($prop.Value -is [string] -and $prop.Value -match 'C3DBridge') {
                Remove-ItemProperty -Path $runKey -Name $prop.Name -Force -ErrorAction SilentlyContinue
            }
        }
    } catch { }
}

# MSI product registration, if the old MSI was ever installed.
try { Start-Process msiexec.exe -ArgumentList '/x {91B2520E-A236-4BEC-A5AF-7951812E208A} /qn' -Wait -NoNewWindow -ErrorAction SilentlyContinue } catch { }

# Install folders (current + previous locations).
foreach ($dir in @($InstallDir, "$env:USERPROFILE\C3DBridge", "$env:LOCALAPPDATA\C3DBridge")) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }
}

# Temp bridge files.
foreach ($pattern in @('C3DBridge-*.ps1', 'c3d-bridge*.zip', 'C3DBridge-preserve', 'C3DBridge-update-src', 'c3d-bridge-src', 'c3d-bridge-update-src')) {
    Get-ChildItem -Path $env:TEMP -Filter $pattern -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

# Desktop backup is also a previous bridge file — fresh start removes it.
$backup = Join-Path ([Environment]::GetFolderPath('Desktop')) 'C3DBridge-backup'
if (Test-Path $backup) { Remove-Item $backup -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host 'Uninstalled: task removed, bridge stopped, install folder deleted.' -ForegroundColor Green
