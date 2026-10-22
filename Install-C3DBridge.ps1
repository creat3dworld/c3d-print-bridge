# C3D Print Bridge v1.1.0 - installer for the shop PC (Windows).
# If PowerShell says running scripts is disabled, first run:
#   Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser -Force
# (or double-click Enable-Scripts.bat, which does that for you).
#
# Run AFTER the code is on the PC (see SETUP.md for the one-paste
# command that fetches it). This script:
#   1. Makes sure the .NET 8 SDK is available. If it is missing,
#      installs it with winget and restarts this installer in a
#      fresh PowerShell window so the new .NET is picked up
#      (no manual close/reopen). Falls back to a user-local
#      install if winget is missing or fails. The SDK includes
#      the Windows Desktop Runtime the tray icon needs.
#   2. Stops any running bridge (tray or console) so its exe is
#      not locked, then builds the bridge from source and
#      publishes it to $InstallDir\app (default C:\C3D_Bridge\app, framework-dependent,
#      win-x64). Updating from v1.0.0 is safe: the existing
#      bridge-config.json and bridge-key.txt are never touched.
#   3. Creates bridge-config.json from the example file, if none
#      exists yet (or open the tray Configure window instead of
#      editing it by hand).
#   4. Registers a Scheduled Task that starts the bridge at logon.
#   5. Runs the bridge self-test and prints the report.
#
# This script contains NO secrets and stores NO secrets. Printer
# access codes go in bridge-config.json (your PC only). The
# dashboard bridge API key is stored later with
# "C3DBridge.exe setup" (masked prompt) - never here.

param(
    [string]$InstallDir = "C:\C3D_Bridge",
    [string]$SourceDir = $PSScriptRoot,
    [switch]$DotnetRestarted,
    [switch]$FromUpdate
)

# Self-elevate: installing to C:\C3D_Bridge and registering the logon
# task need administrator rights, and the script must run with
# ExecutionPolicy Bypass (no permanent policy change).
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $elevArgs = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -InstallDir `"$InstallDir`" -SourceDir `"$SourceDir`""
    if ($DotnetRestarted) { $elevArgs += " -DotnetRestarted" }
    if ($FromUpdate) { $elevArgs += " -FromUpdate" }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $elevArgs
    exit
}

$ErrorActionPreference = 'Stop'

Write-Host '=== C3D Print Bridge installer ===' -ForegroundColor Cyan
Write-Host "Source: $SourceDir"
Write-Host "Install folder: $InstallDir"

# --- 1. .NET 8 SDK ---------------------------------------------------
function Test-Dotnet8 {
    param([string]$Exe)
    if (-not $Exe) { return $false }
    try {
        $out = & $Exe --list-sdks 2>$null
        return ($out -match '8\.')
    } catch {
        return $false
    }
}

# Check the PATH copy first, then the standard install folders
# directly, so a fresh install is found even if PATH is stale.
$dotnet = $null
$candidates = @()
if (Get-Command dotnet -ErrorAction SilentlyContinue) { $candidates += 'dotnet' }
foreach ($path in @("$env:ProgramFiles\dotnet\dotnet.exe", "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe")) {
    if (Test-Path $path) { $candidates += $path }
}
foreach ($candidate in $candidates) {
    if (Test-Dotnet8 $candidate) { $dotnet = $candidate; break }
}

# Missing .NET: install it with winget, then restart this installer
# in a fresh window so the new .NET is on the path. The
# -DotnetRestarted flag stops this from looping: after one restart,
# a still-missing .NET falls through to the user-local install below.
if (-not $dotnet -and -not $DotnetRestarted -and (Get-Command winget -ErrorAction SilentlyContinue)) {
    Write-Host '.NET 8 SDK not found - installing it with winget, then restarting this installer...' -ForegroundColor Yellow
    winget install --id Microsoft.DotNet.SDK.8 -e --accept-source-agreements --accept-package-agreements
    if ($LASTEXITCODE -eq 0) {
        Write-Host 'Restarting the installer so the new .NET is picked up...' -ForegroundColor Cyan
        $restartArgs = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -InstallDir `"$InstallDir`" -SourceDir `"$SourceDir`" -DotnetRestarted"
        Start-Process -FilePath 'powershell.exe' -ArgumentList $restartArgs
        return
    }
    Write-Host 'winget install did not succeed - falling back to the user-local install...' -ForegroundColor Yellow
}

if (-not $dotnet) {
    Write-Host '.NET 8 SDK not found - installing it user-locally (no admin needed)...' -ForegroundColor Yellow
    $dotnetDir = "$env:LOCALAPPDATA\Microsoft\dotnet"
    $installer = "$env:TEMP\dotnet-install.ps1"
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing
    & $installer -Channel 8.0 -InstallDir $dotnetDir | Out-Null
    $env:PATH = "$dotnetDir;$env:PATH"
    $dotnet = "$dotnetDir\dotnet.exe"
    if (-not (Test-Path $dotnet)) { throw '.NET SDK install failed. See https://dotnet.microsoft.com/download/dotnet/8.0 and install the SDK, then re-run this installer.' }
    Write-Host '.NET 8 SDK installed.' -ForegroundColor Green
}

# --- 2. Build + publish ----------------------------------------------
$appDir = Join-Path $InstallDir 'app'
# Project check FIRST, before stopping the old bridge or clearing its
# files. A failed check here leaves the previous install intact.
$projCheck = Join-Path $SourceDir 'src\C3DBridge\C3DBridge.csproj'
if (-not (Test-Path $projCheck)) {
    $projFound = Get-ChildItem -Path $SourceDir -Recurse -Filter 'C3DBridge.csproj' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $projFound) {
        throw "Build failed - project file not found under $SourceDir. Extract ALL files (including src) and run Setup-Bridge.bat from inside that extracted folder."
    }
}
New-Item -ItemType Directory -Force -Path $appDir | Out-Null
# Stop a running bridge first (v1.0.0 console or v1.1.0 tray),
# otherwise Windows locks the exe and publish cannot replace it.
# Best effort only: a missing task/process is not a failure.
Stop-ScheduledTask -TaskName 'C3D Print Bridge' -ErrorAction SilentlyContinue
Get-Process C3DBridge -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
Write-Host 'Building the bridge (this takes a minute or two the first time)...'
# Overwrite an existing install cleanly: keep config/key, clear the old
# app files (so stale files never linger), publish, then restore.
$preserveDir = Join-Path $env:TEMP 'C3DBridge-preserve'
$preserveApp = Join-Path $env:TEMP 'C3DBridge-preserve-app'
if (Test-Path $appDir) {
    if (Test-Path $preserveApp) { Remove-Item $preserveApp -Recurse -Force -ErrorAction SilentlyContinue }
    Copy-Item $appDir $preserveApp -Recurse -Force -ErrorAction SilentlyContinue

    New-Item -ItemType Directory -Force -Path $preserveDir | Out-Null
    Copy-Item (Join-Path $appDir 'bridge-config.json') $preserveDir -Force -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $appDir 'bridge-key.txt') $preserveDir -Force -ErrorAction SilentlyContinue
    Get-ChildItem $appDir -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}
$projectPath = Join-Path $SourceDir 'src\C3DBridge'
$projectFile = Join-Path $projectPath 'C3DBridge.csproj'
if (-not (Test-Path $projectFile)) {
    # Fallback: the user may have extracted the zip into a nested folder,
    # or Drive may have synced src at a different depth. Search for it.
    $found = Get-ChildItem -Path $SourceDir -Recurse -Filter 'C3DBridge.csproj' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) { $projectPath = $found.Directory.FullName; $projectFile = $found.FullName; Write-Host "Found project at: $projectFile" -ForegroundColor Green }
}
if (-not (Test-Path $projectFile)) {
    Write-Host 'Checked:' -ForegroundColor Yellow
    Write-Host "  $projectFile" -ForegroundColor Yellow
    Write-Host "  (recursive search under $SourceDir found nothing)" -ForegroundColor Yellow
    Write-Host 'Fix: extract ALL files from the version folder / zip, including the src subfolder, and run Setup-Bridge.bat from inside that extracted folder - not from inside the zip, and not from a folder with only the .ps1/.bat files.' -ForegroundColor Yellow
    throw "Build failed - project file not found. See the fix above."
}
$dotnetVersion = $null
try { $dotnetVersion = & $dotnet --version } catch { $dotnetVersion = 'unknown' }
Write-Host "Using .NET: $dotnetVersion"
Write-Host "Project: $projectFile"
$buildLog = Join-Path $env:TEMP 'C3DBridge-build.log'
Write-Host "Build log: $buildLog"
& $dotnet publish $projectPath -c Release -r win-x64 --self-contained false -o $appDir > $buildLog 2>&1
if (Test-Path $buildLog) { Get-Content $buildLog | ForEach-Object { Write-Host $_ } }
$publishExit = $LASTEXITCODE
if (Test-Path $preserveDir) {
    Copy-Item (Join-Path $preserveDir 'bridge-config.json') $appDir -Force -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $preserveDir 'bridge-key.txt') $appDir -Force -ErrorAction SilentlyContinue
    Remove-Item $preserveDir -Recurse -Force -ErrorAction SilentlyContinue
}
if ($publishExit -ne 0 -or -not (Test-Path (Join-Path $appDir 'C3DBridge.exe'))) {
    if (Test-Path $preserveApp) {
        Write-Host 'Build failed - restoring the previous app files so you are not left broken.' -ForegroundColor Yellow
        if (Test-Path $appDir) { Remove-Item $appDir -Recurse -Force -ErrorAction SilentlyContinue }
        Copy-Item $preserveApp $appDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    Write-Host '--- Last 30 lines of the build log ---' -ForegroundColor Yellow
    if (Test-Path $buildLog) { Get-Content $buildLog -Tail 30 | ForEach-Object { Write-Host $_ } }
    throw "Build failed (dotnet publish exit $publishExit) - C3DBridge.exe was not produced. Full log: $buildLog"
}
if (Test-Path $preserveApp) { Remove-Item $preserveApp -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host 'Build OK.' -ForegroundColor Green

# Reference docs next to the app, for later reading.
Copy-Item (Join-Path $SourceDir 'SETUP.md') $InstallDir -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $SourceDir 'README.md') $InstallDir -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $SourceDir 'Uninstall-C3DBridge.ps1') $InstallDir -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $SourceDir 'Uninstall-Repair-Reinstall.ps1') $InstallDir -Force -ErrorAction SilentlyContinue

# --- 3. Config (example only - never overwrites a real config) ------
# Migration to C:\C3D_Bridge: if this install has no config/key yet,
# pull them from a previous install location instead of starting blank.
foreach ($oldApp in @("$env:USERPROFILE\C3DBridge\app", "$env:LOCALAPPDATA\C3DBridge\app")) {
    if ($oldApp -ne $appDir -and (Test-Path $oldApp)) {
        Copy-Item (Join-Path $oldApp 'bridge-config.json') $appDir -Force -ErrorAction SilentlyContinue
        Copy-Item (Join-Path $oldApp 'bridge-key.txt') $appDir -Force -ErrorAction SilentlyContinue
        # Shield surviving copies: once migrated into the encrypted
        # config/key files, delete the old location credential files
        # so no extra plain/old copy survives the reinstall.
        if (Test-Path (Join-Path $appDir 'bridge-config.json')) { Remove-Item (Join-Path $oldApp 'bridge-config.json') -Force -ErrorAction SilentlyContinue }
        if (Test-Path (Join-Path $appDir 'bridge-key.txt')) { Remove-Item (Join-Path $oldApp 'bridge-key.txt') -Force -ErrorAction SilentlyContinue }
    }
}
$configPath = Join-Path $appDir 'bridge-config.json'
if (-not (Test-Path $configPath)) {
    Copy-Item (Join-Path $SourceDir 'config\bridge-config.example.json') $configPath
    Write-Host "Created $configPath from the example." -ForegroundColor Yellow
    Write-Host 'EDIT IT NOW with your printers: names, IPs, Bambu serials + LAN access codes. See SETUP.md.' -ForegroundColor Yellow
} else {
    Write-Host 'Existing bridge-config.json kept (not overwritten).' -ForegroundColor Green
}

# --- 4. Start at logon (Scheduled Task, current user) ------------------
$taskName = 'C3D Print Bridge'
try {
    $action = New-ScheduledTaskAction -Execute (Join-Path $appDir 'C3DBridge.exe') -WorkingDirectory $appDir
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -StartWhenAvailable
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings -Force | Out-Null
    Write-Host 'Scheduled Task "C3D Print Bridge" registered: the bridge starts when you sign in.' -ForegroundColor Green
    Write-Host 'Note: it runs while you are signed in. If the PC restarts and nobody signs in, it does not run until sign-in.'
} catch {
    Write-Host "Could not register the Scheduled Task ($($_.Exception.Message))." -ForegroundColor Yellow
    Write-Host "Fallback: double-click $appDir\C3DBridge.exe whenever you want the bridge running, or re-run this installer."
}

# --- 5. Self-test ------------------------------------------------------
Write-Host ''
Write-Host '--- Self-test ---' -ForegroundColor Cyan
& (Join-Path $appDir 'C3DBridge.exe') --self-test
Write-Host ''
Write-Host '=== Installer finished ===' -ForegroundColor Cyan
Write-Host "App folder: $appDir"
if (-not (Test-Path (Join-Path $appDir 'bridge-key.txt'))) {
    Write-Host 'Bridge API key: NOT stored yet. After the dashboard plugin update ships the "Bridge key" page, run:' -ForegroundColor Yellow
    Write-Host "  $appDir\C3DBridge.exe setup" -ForegroundColor Yellow
    Write-Host 'and paste the key when asked (input is hidden). Until then the bridge reports local printer status only.'
}
# Start after install: a git update was already checked for on purpose,
# so it starts without asking. A fresh install asks first (safety check).
# Always start the bridge after install: that is the point of the
# software. No prompt, fresh or update. Exit from the tray if needed.
Start-ScheduledTask -TaskName 'C3D Print Bridge' -ErrorAction SilentlyContinue
Write-Host 'Bridge started.' -ForegroundColor Green
Write-Host '  - or just double-click C3DBridge.exe in the app folder. Either way, it shows as a tray icon by the clock.'
Write-Host 'Right-click the tray icon for Configure (printer list, Scan, access codes, dashboard key), Self-test, Get updates, and Exit.'
