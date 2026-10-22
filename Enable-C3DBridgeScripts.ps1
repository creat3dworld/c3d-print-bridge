# C3D Print Bridge — enable PowerShell scripts on this PC.
#
# Use this ONLY if PowerShell says "running scripts is disabled on this
# system". It sets the execution policy to RemoteSigned (local scripts
# run; downloaded scripts must be signed) — not Unrestricted.
# Run it by double-clicking Enable-Scripts.bat, which already passes
# -ExecutionPolicy Bypass for this one run, so it works even while
# scripts are currently disabled. Or, in PowerShell:
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\Enable-C3DBridgeScripts.ps1
#
# The command this script runs, in plain form:
#   Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser -Force
# With administrator rights it also sets the same for LocalMachine.

param()

$ErrorActionPreference = 'Stop'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    # Try to elevate so LocalMachine can be set too; if the user
    # declines, fall through to CurrentUser only below.
    try {
        Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`"" -Wait
        Write-Host 'Elevated run finished. Checking the resulting policy below.' -ForegroundColor Green
    } catch {
        Write-Host 'Elevation declined; setting CurrentUser policy only.' -ForegroundColor Yellow
    }
}
Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser -Force
if (([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope LocalMachine -Force
}
Write-Host ''
Write-Host 'Execution policy is now:' -ForegroundColor Green
Get-ExecutionPolicy -List | Format-Table -AutoSize
Write-Host 'You can now run the bridge installer and uninstaller normally, or use the .bat launchers.'
