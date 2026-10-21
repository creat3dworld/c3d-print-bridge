# C3D Print Bridge — installer for the shop PC (Windows).
#
# Run AFTER the code is on the PC (see SETUP.md for the one-paste
# command that fetches it). This script:
#   1. Makes sure the .NET 8 SDK is available (installs it
#      user-locally if missing — no admin rights needed).
#   2. Builds the bridge from source and publishes it to
#      $InstallDir\app (framework-dependent, win-x64).
#   3. Creates bridge-config.json from the example file, if none
#      exists yet (you still must edit it with your printers).
#   4. Registers a Scheduled Task that starts the bridge at logon.
#   5. Runs the bridge self-test and prints the report.
#
# This script contains NO secrets and stores NO secrets. Printer
# access codes go in bridge-config.json (your PC only). The
# dashboard bridge API key is stored later with
# "C3DBridge.exe setup" (masked prompt) — never here.

param(
    [string]$InstallDir = "$env:USERPROFILE\C3DBridge",
    [string]$SourceDir = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'

Write-Host '=== C3D Print Bridge installer ===' -ForegroundColor Cyan
Write-Host "Source: $SourceDir"
Write-Host "Install folder: $InstallDir"

# --- 1. .NET 8 SDK ---------------------------------------------------
$dotnet = $null
if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    $sdks = dotnet --list-sdks 2>$null
    if ($sdks -match '8\.') { $dotnet = 'dotnet' }
}
if (-not $dotnet) {
    Write-Host '.NET 8 SDK not found — installing it user-locally (no admin needed)...' -ForegroundColor Yellow
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
New-Item -ItemType Directory -Force -Path $appDir | Out-Null
Write-Host 'Building the bridge (this takes a minute or two the first time)...'
& $dotnet publish (Join-Path $SourceDir 'src\C3DBridge') -c Release -r win-x64 --self-contained false -o $appDir | Out-Null
if (-not (Test-Path (Join-Path $appDir 'C3DBridge.exe'))) { throw 'Build failed — C3DBridge.exe was not produced. Read the build output above.' }
Write-Host 'Build OK.' -ForegroundColor Green

# Reference docs next to the app, for later reading.
Copy-Item (Join-Path $SourceDir 'SETUP.md') $InstallDir -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $SourceDir 'README.md') $InstallDir -Force -ErrorAction SilentlyContinue

# --- 3. Config (example only — never overwrites a real config) ------
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
Write-Host 'To start the bridge right now (without signing out and back in):'
Write-Host "  Start-ScheduledTask -TaskName 'C3D Print Bridge'"
Write-Host '  — or just double-click C3DBridge.exe in the app folder.'
