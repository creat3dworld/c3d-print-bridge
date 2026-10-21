# =====================================================================
# C3D Print Bridge — public anonymous bootstrap.
#
# The bridge repo is PUBLIC (creat3dworld/C3d-print-bridge), so this
# needs NO GitHub login, NO GitHub CLI, and NO token. It downloads
# the public main-branch zip anonymously, expands it, and runs the
# installer from the extracted folder. The installer preserves an
# existing bridge-config.json and bridge-key.txt, handles .NET 8,
# and registers the bridge to start at sign-in.
#
# Typed install command (one line, PowerShell on the shop PC):
#   curl.exe https://creat3dworld.com/bridge|iex
# That command fetches a ONE-LINE compressed equivalent of this
# script from creat3dworld.com/bridge (Code Snippets ID 12).
# Piping curl.exe output to iex evaluates line by line, so the
# /bridge version must stay on a single physical line — do NOT
# expand it back into this multi-line form on the website.
# This readable version and the /bridge one-liner do the same
# steps in the same order; keep them in sync.
# =====================================================================

$ErrorActionPreference = 'Stop'
# Process scope only: lets this session run the installer FILE even on
# machines set to Restricted. Gone when this window closes; it changes
# no permanent setting.
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force -ErrorAction SilentlyContinue

$zip = Join-Path $env:TEMP 'c3d-bridge.zip'
curl.exe -L 'https://github.com/creat3dworld/C3d-print-bridge/archive/refs/heads/main.zip' -o $zip
if (-not (Test-Path $zip)) {
    throw 'Bridge download failed.'
}

$extractRoot = Join-Path $env:TEMP 'c3d-bridge-src'
Remove-Item $extractRoot -Recurse -Force -ErrorAction SilentlyContinue
Expand-Archive $zip $extractRoot -Force

$installerFile = Get-ChildItem $extractRoot -Recurse -Filter 'Install-C3DBridge.ps1' | Select-Object -First 1
if (-not $installerFile) {
    throw 'Install-C3DBridge.ps1 not found in download.'
}
& $installerFile.FullName -SourceDir $installerFile.DirectoryName
