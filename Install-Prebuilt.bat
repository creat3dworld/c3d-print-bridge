@echo off
echo Installing the prebuilt bridge app - no build needed.
set "APP=%~dp0app"
if not exist "%APP%\C3DBridge.exe" (
  echo C3DBridge.exe not found in %APP%
  echo Extract ALL files from this version folder, including the app subfolder, and run this file from inside that extracted folder.
  pause
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -Command "$app='C:\C3D_Bridge\app'; $src='%~dp0app'; New-Item -ItemType Directory -Force -Path $app | Out-Null; if (Test-Path \"$app\bridge-config.json\") { Copy-Item \"$app\bridge-config.json\" \"$env:TEMP\C3DBridge-preserve-config.json\" -Force }; if (Test-Path \"$app\bridge-key.txt\") { Copy-Item \"$app\bridge-key.txt\" \"$env:TEMP\C3DBridge-preserve-key.txt\" -Force }; Get-Process C3DBridge -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2; Get-ChildItem $app -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue; Copy-Item \"$src\*\" $app -Recurse -Force; if (Test-Path \"$env:TEMP\C3DBridge-preserve-config.json\") { Copy-Item \"$env:TEMP\C3DBridge-preserve-config.json\" \"$app\bridge-config.json\" -Force }; if (Test-Path \"$env:TEMP\C3DBridge-preserve-key.txt\") { Copy-Item \"$env:TEMP\C3DBridge-preserve-key.txt\" \"$app\bridge-key.txt\" -Force }; $action = New-ScheduledTaskAction -Execute \"$app\C3DBridge.exe\" -WorkingDirectory $app; $trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME; $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -StartWhenAvailable; Register-ScheduledTask -TaskName 'C3D Print Bridge' -Action $action -Trigger $trigger -Settings $settings -Force | Out-Null; Write-Host 'Prebuilt bridge installed to C:\C3D_Bridge\app and task registered.'"
choice /C YN /M "Start bridge now"
if errorlevel 2 goto done
schtasks /run /tn "C3D Print Bridge"
:done
pause
