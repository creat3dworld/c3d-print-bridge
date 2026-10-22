# C3D Print Bridge — setup guide for the shop PC

If PowerShell says running scripts is disabled on this system, double-click `Enable-Scripts.bat` first (it runs `Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser -Force` for you), or run the installer via `Run-Installer.bat`, which already uses `-ExecutionPolicy Bypass`.

Plain language, start to finish. The bridge is a small program that
runs on the shop PC and relays between the home printers and the
C3D Shipping dashboard on creat3dworld.com. It does NOT decide
anything — no picking printers, no scheduling, no slicing, no
prices. The dashboard decides; the bridge just carries messages and
reports real printer status. It never accepts connections from the
internet: it only calls OUT to the dashboard and to printers on the
home network.

## Before you start

- The shop PC is on and on the same home network as the printers.
- Bambu P2S: Developer Mode / LAN mode is ON (it already is).
  Bambu X2D: Developer Mode still needs turning on before it can
  join — the bridge will report it offline until then.
- Snapmaker U1: the Paxx firmware USB install still needs doing
  before the U1 can join — the bridge will report it offline until
  then.
- You need your GitHub sign-in (browser login) OR a one-time
  read-only token (explained in Step 1).

## Step 1 — Install with ONE pasted command

Copy the whole block below, paste it into PowerShell on the shop PC
(right-click in the PowerShell window to paste), and press Enter.
That is the whole install: it fetches the bridge code, builds it,
registers it to start when you sign in, and runs a self-test.

Short version of the same thing, served from the website:

```
iex (curl.exe https://creat3dworld.com/bridge|Out-String)
```

.NET note: if the .NET 8 SDK is not on the PC, the installer
installs it with winget and then restarts itself in a fresh
PowerShell window so the new .NET is picked up — no manual
close/reopen needed. If winget is missing or its install fails,
the installer falls back to a user-local .NET install instead.

**One honest note first:** the bridge code lives in a PRIVATE
GitHub repo, so a bare download link cannot work — GitHub requires
a sign-in for private code. A truly token-free one-liner is
impossible while the repo stays private. This is the closest thing:
ONE paste, plus at most ONE login/token step. If GitHub CLI is
already signed in on the PC, there is no extra step at all. If it
asks for a token, create a fine-grained token at
https://github.com/settings/tokens?type=beta with repository access
set to ONLY `creat3dworld/c3d-print-bridge` and the single
permission Contents = Read-only. The token is used for that one
download and then forgotten — it is never saved anywhere.

```powershell
$ErrorActionPreference = 'Stop'
$repo = 'creat3dworld/c3d-print-bridge'
$installRoot = "$env:USERPROFILE\C3DBridge"
$sourceDir = "$installRoot\source"
New-Item -ItemType Directory -Force -Path $installRoot | Out-Null
function Test-GhAuth {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { return $false }
    gh auth status 2>$null | Out-Null
    return ($LASTEXITCODE -eq 0)
}
$haveSource = $false
if (Get-Command gh -ErrorAction SilentlyContinue) {
    if (-not (Test-GhAuth)) {
        Write-Host 'Signing into GitHub (a browser code login — follow the prompts)...' -ForegroundColor Cyan
        gh auth login
    }
} elseif (Get-Command winget -ErrorAction SilentlyContinue) {
    Write-Host 'Installing GitHub CLI (needed to fetch the private repo)...' -ForegroundColor Cyan
    try {
        winget install --id GitHub.cli -e --accept-source-agreements --accept-package-agreements | Out-Null
        if (-not (Test-GhAuth)) {
            Write-Host 'Signing into GitHub (a browser code login — follow the prompts)...' -ForegroundColor Cyan
            gh auth login
        }
    } catch {
        Write-Host "GitHub CLI install did not work ($($_.Exception.Message)) — falling back to a token download." -ForegroundColor Yellow
    }
}
if ((Get-Command gh -ErrorAction SilentlyContinue) -and (Test-GhAuth)) {
    try {
        if (Test-Path (Join-Path $sourceDir '.git')) {
            Write-Host 'Updating the existing bridge source...'
            git -C $sourceDir pull | Out-Null
        } else {
            Write-Host 'Downloading the bridge source...'
            if (Test-Path $sourceDir) { Remove-Item $sourceDir -Recurse -Force }
            gh repo clone $repo $sourceDir | Out-Null
        }
        $haveSource = $true
    } catch {
        Write-Host "GitHub CLI download did not work ($($_.Exception.Message)) — falling back to a token download." -ForegroundColor Yellow
    }
}
if (-not $haveSource) {
    Write-Host ''
    Write-Host 'A read-only GitHub token is needed for this one download.' -ForegroundColor Cyan
    Write-Host 'Create it at: https://github.com/settings/tokens?type=beta'
    Write-Host '  - Fine-grained token, repository access: ONLY creat3dworld/c3d-print-bridge'
    Write-Host '  - Permissions: Contents = Read-only (nothing else).'
    Write-Host 'Paste it below (input is hidden). It is used once and then forgotten.'
    $secure = Read-Host 'Token' -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try {
        $plain = [Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr)
        $zip = Join-Path $env:TEMP 'c3d-bridge.zip'
        Invoke-RestMethod -Headers @{ Authorization = "Bearer $plain" } `
            -Uri "https://api.github.com/repos/$repo/zipball" -OutFile $zip
    } finally {
        $plain = $null
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
    $tmp = Join-Path $env:TEMP 'c3d-bridge-src'
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    Expand-Archive $zip $tmp
    $inner = Get-ChildItem $tmp -Directory | Select-Object -First 1
    if (Test-Path $sourceDir) { Remove-Item $sourceDir -Recurse -Force }
    Move-Item $inner.FullName $sourceDir
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host 'Download complete. The token was used once and is now gone from memory.' -ForegroundColor Green
    $haveSource = $true
}
if (-not (Test-Path (Join-Path $sourceDir 'Install-C3DBridge.ps1'))) {
    throw 'Bridge source was not downloaded correctly — Install-C3DBridge.ps1 is missing.'
}
& (Join-Path $sourceDir 'Install-C3DBridge.ps1') -SourceDir $sourceDir
```

(The same block is saved in the repo as `Bootstrap-Install.ps1`
for reference. The installer itself is `Install-C3DBridge.ps1`.)

## Step 2 — Tell the bridge about your printers (tray Configure window, v1.1.0)

After the install, the bridge shows as a small icon by the
clock. Right-click it and choose **Configure**. That window is
the easy way to do this step: a printer list with Add, Edit,
and Delete, a **Scan** button, and plain fields — no JSON
editing by hand.

- Click **Scan WiFi for printers**: it probes this PC's own
  network for Bambu (port 8883) and Snapmaker (port 7125) and
  adds each candidate it finds to the list with its IP and type
  filled in. A found port is a candidate, not a confirmed
  printer, and Scan only sees printers on the same network as
  the PC.
- For each printer, fill in a name, and for a Bambu printer the
  serial and the access code from the printer's own screen
  (the access-code box hides what you type). Click **Apply to
  list** for each one, then **Save**. Saving restarts the
  bridge with the new config. Deleting a printer asks first.
- The dashboard API key box at the bottom is optional here:
  leave it blank to keep the key already stored, or paste a new
  one to replace it (see Step 3).

You can still edit the file by hand instead, if you prefer:

```
C:\C3D_Bridge\app\bridge-config.json
```

The installer created it from the example. Fill in each printer:

- `name` — any name you like, but it MUST match the printer name
  in the dashboard exactly (the dashboard assigns jobs by name).
- `type` — `bambu` or `snapmaker`.
- `ip` — the printer's address on the home network (find it on the
  printer's screen, under network settings).
- Bambu only — `serial`: the printer's serial number (on the
  printer's screen / About page), and `access_code`: the LAN access
  code shown on the printer's screen under LAN / Developer mode.
- `camera` — `true` if the printer has a camera, else `false`.
  (Today this only records that a camera exists; still-image
  capture needs an optional `snapshot_url` — see README.)

Save the file, then run the self-test (Step 4) to check it.

Tip: if you do not know a printer's IP, run this in PowerShell
from the app folder — it probes your own home network and suggests
config lines (it never edits the file itself):

```
.\C3DBridge.exe --discover
```

## Updating the bridge (tray: Get updates, v1.1.0)

Right-click the tray icon and choose **Get updates**. It checks
the bridge repo for a newer version and reports one of three
things, plainly: already up to date (with the version), updated
(with the new version), or failed (with the reason). It never
claims an update happened without checking the new version
afterwards.

How the check works: it first tries an unauthenticated check
against the public repo (no sign-in, no token stored on the
PC). Only if that returns "not found" — meaning the repo is
still private — does it fall back to the GitHub CLI sign-in
already on the PC. If GitHub is not signed in there, it says so
plainly instead of faking a check: run `gh auth login` once in
PowerShell, then try Get updates again, or reinstall with the
normal install command, which handles sign-in for you.

When a newer version is confirmed and you approve, the bridge
closes so the installer can replace it, then the installer runs,
your config and key are preserved, the new version is verified
with `--version`, and the bridge starts again. If the new
version does not verify, it says so and your old config and key
are still intact.

Updating from v1.0.0: re-running the same one-command install
does the same thing (new tray version, config and key kept,
Scheduled Task re-registered). The v1.0.0 console window is
replaced by the tray icon at the next sign-in.

## Step 3 — Connect the bridge to the dashboard (the API key)

**Timing note — read this:** the dashboard side of this connection
(the "Bridge key" page and the bridge endpoints) ships in a LATER
plugin update. It is NOT in dashboard v0.2.0. Until that update
ships, the self-test will say "Bridge endpoints: NOT INSTALLED
YET" and the bridge will only do local printer checks. That is
expected — the PC side is ready and waiting.

When the update ships, do this once:

1. On the website: WordPress admin → C3D Shipping → Bridge key →
   **Generate new key**. A long key appears ONCE — copy it right
   then. If you lose it, generate a new one (the old one stops
   working immediately).
2. On the shop PC, in PowerShell from the app folder:
   ```
   .\C3DBridge.exe setup
   ```
   Paste the key when asked. What you type is hidden (stars), and
   it is stored only in a restricted file on this PC
   (`bridge-key.txt` in the app folder). It is never in the repo,
   never in the config file, and never printed back.
3. Run the self-test again — it should now say the dashboard
   accepted the key.

**Rotate:** dashboard → Generate new key → PC → `C3DBridge.exe
setup` → paste the new key. The old key stops working the moment
the new one is generated.
**Revoke:** dashboard → Revoke key. Every bridge request is then
refused until a new key is generated and stored on the PC.

## Step 4 — Start it and verify it phones home

The installer registered a Scheduled Task named **C3D Print
Bridge** that starts the bridge when you sign in. To start it
right now without signing out:

```
Start-ScheduledTask -TaskName 'C3D Print Bridge'
```

Then run the self-test from the app folder:

```
.\C3DBridge.exe --self-test
```

What the lines mean:

- `Dashboard host: PASS` — the PC can reach creat3dworld.com.
- `Bridge endpoints: NOT INSTALLED YET` — expected until the
  later plugin update ships. Local printer checks still apply.
- `Bridge endpoints: PASS` — the dashboard accepted the key.
  (After the update.)
- `Bridge endpoints: INSTALLED, key rejected` — the key was
  revoked or rotated. Re-do Step 3.
- `Printer 'P2S': PRINTING / IDLE / ...` — live state, as observed.
- `Printer 'X': OFFLINE — ...` — the reason tells you what to
  check: IP, Developer/LAN mode, or access code.

To see it running day to day: open Task Scheduler → Task Scheduler
Library → "C3D Print Bridge" (Running), or read `bridge.log` in
the app folder. After the plugin update, the dashboard's shop
status board shows the printers from bridge reports only — nothing
is shown as running without a real report.

## Step 5 — First-print check (do this once, before relying on it)

After the plugin update ships AND the dashboard knows which print
file belongs to a product (that mapping is still being built on
the dashboard side — until then jobs are refused with "No print
file supplied", which is the safe behavior):

1. Send one small test job through the dashboard to one printer.
2. Confirm the printer actually starts printing.
3. Confirm the dashboard shows the job progressing and then
   completing.
4. If the printer refuses the file upload or the start command,
   the job fails WITH the printer's reason shown on the dashboard
   — that is the bridge working correctly, not silently. Tell Muse
   the exact reason and the fix goes into the next bridge update.

Two first-use verifications live here on purpose: the Bambu file
upload (FTPS) and the Bambu start command shape. Both follow
public community documentation, but neither has been run against
a real P2S yet. The Snapmaker path (Moonraker HTTP) is the same:
verified against docs, not yet against the real U1.

## If something goes wrong

| Symptom | What it means / what to do |
|---|---|
| Self-test: dashboard host FAIL | The PC cannot reach the website. Check internet / Wi-Fi. |
| Endpoints NOT INSTALLED YET | Expected until the later plugin update. Nothing is broken. |
| Key rejected / 401 | Key revoked or rotated. Re-do Step 3. |
| Printer OFFLINE (Bambu) | Wrong IP, Developer/LAN mode off, or wrong access code / serial. |
| Printer OFFLINE (U1) | Paxx not installed yet, wrong IP, or Moonraker port changed. |
| Job failed: "No print file supplied" | The dashboard does not yet map that product to a print file. Safe refusal, not a bridge bug. |
| Job refused_unsafe | The printer was busy, or its loaded filament no longer matched the job. Fix the filament / wait, and the dashboard re-queues. |
| FTPS upload refused (Bambu) | Known Bambu quirk (data-channel session). Report the exact reason; a retry or a Bambu Studio upload is the fallback. |
| Token paste fails | The token must be fine-grained, scoped to ONLY this repo, Contents = Read-only. Tokens also expire — create a fresh one. |
| .NET 8 SDK missing | The installer installs it with winget and restarts itself — just let the new window finish. If winget is missing or fails, it falls back to a user-local install; only if both fail does it ask you to install the SDK by hand from dotnet.microsoft.com/download/dotnet/8.0 and re-run the installer. |

| Tray icon missing after sign-in | The Scheduled Task may not have started it. Open Task Scheduler → Task Scheduler Library → "C3D Print Bridge" → Run, or double-click C3DBridge.exe in the app folder. The tray icon only shows while you are signed in. |
| Get updates says sign-in required | The repo still looks private and GitHub is not signed in on this PC. Run `gh auth login` in PowerShell once, then try Get updates again. No token is stored by Get updates. |
| Get updates failed | The message gives the reason (no network, repo moving, or the new version did not verify). Your config and key are preserved. Try again, or reinstall with the normal install command. |
| Scan finds nothing | Printers must be on, on the same network as the PC, and in Developer/LAN mode (Bambu) or with Paxx installed (U1). Scan cannot see printers on a different network. |

## Stopping or removing the bridge

- Stop it for now: `Stop-ScheduledTask -TaskName 'C3D Print Bridge'`
- Stop it starting at sign-in: `Disable-ScheduledTask -TaskName 'C3D Print Bridge'`
- Remove it completely: delete the Scheduled Task, then delete
  the `C:\C3D_Bridge` folder. That also deletes the
  stored API key and the printer access codes from the PC.

## Where secrets live (and where they never do)

- Dashboard API key: `bridge-key.txt` in the app folder, on this
  PC only. Never in the repo, never in code, never emailed.
- Bambu access codes: `bridge-config.json` in the app folder, on
  this PC only. Same rule.
- GitHub token (install only): memory only, for one download,
  then gone. Never saved.
- The bridge log (`bridge.log`) never contains keys, codes, or
  full download URLs — file names and job numbers only.
