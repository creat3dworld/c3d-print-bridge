# C3D Print Bridge

A thin relay between Creat3d World's home printers and the C3D
Shipping dashboard on creat3dworld.com. It runs on the shop PC
(Windows first; the same C# codebase builds for Mac/Linux later).

## The one rule: the bridge is a relay, not a brain

The dashboard decides. The bridge carries. Concretely:

- The dashboard assigns every job to a printer BY NAME. The bridge
  looks the name up and runs it there — an unknown name is an
  error, never a reason to pick a different printer.
- The dashboard evaluates the safety chain and marks a job
  `"ready"` or `"held"`. The bridge runs only `"ready"` jobs, and
  still refuses at start time if the live facts changed (printer
  no longer idle, or loaded filament now contradicts the job).
- The dashboard provides the already-sliced file. The bridge
  downloads it, verifies it when a hash is supplied, forwards it
  byte-for-byte, and deletes the local copy when the job ends.
- Status (idle/printing/paused/error/offline), AMS material/color,
  and queue length are reported exactly as observed — or as
  unknown. Nothing is ever guessed, and nothing is shown as
  running without a bridge report.

## Direction of travel

The dashboard is hosted remotely and cannot reach the home LAN
(NAT). So the bridge initiates ALL contact, outbound, over HTTPS:
heartbeat, status pushes, job polls, progress reports. It never
opens a listening port and never accepts inbound connections.

## What is deliberately LEFT OUT (thin scope)

- No slicing, no mesh work, no model inspection. Files are opaque
  bytes to this program.
- No printer-picking, no queue ordering, no prioritization, no
  rescheduling. One job per printer, in dashboard poll order,
  because a printer can physically print one thing at a time.
- No local dashboard, no web server, no database. State is
  in-memory plus a plain log file. The one local UI exception,
  owner-approved in v1.1.0, is the Windows tray icon with its
  Configure window (printer fields, Scan, dashboard key) and
  Get updates — it edits local config only and makes no relay
  decisions.
- No automatic bed-empty vision. Bed-empty cannot be verified from
  printer telemetry; it needs image analysis (heavy dependencies
  this relay refuses). The bridge reports camera availability and
  forwards a still image ONLY when an owner-configured still URL
  provides one — otherwise the bed check stays manual/unverifiable
  on the dashboard side, exactly as the dashboard already handles
  it. The bridge never claims a bed is empty on its own.
- No still-image capture from video streams. Bambu and Paxx expose
  video (RTSP/webcam pages), not stills; decoding video would need
  ffmpeg/OpenCV-class dependencies. An owner-set `snapshot_url`
  (a real still endpoint, if one exists for the printer) is the
  supported path, and snapshots are attached only to failure
  reports.
- No Bambu UDP discovery packet (its exact bytes could not be
  verified from public docs). `--discover` probes the PC's own
  subnet with short TCP connects to ports 8883 (Bambu) and 7125
  (Moonraker) and prints suggestions only — it never edits config.
  The tray Configure window's Scan button uses the same probe
  and DOES save found printers (IP + type filled in; name and,
  for Bambu, serial + access code still come from the printer
  screen). Scan only sees the PC's own subnet, and a reachable
  port is a candidate, not a confirmed printer.
- No stored credentials in the repo. See "Security" below.

## Layout

```
Install-C3DBridge.ps1   Installer (runs from extracted source).
Bootstrap-Install.ps1   The one-paste bootstrap (same block as in
                        SETUP.md Step 1).
SETUP.md                Plain-language setup guide (start here).
src/C3DBridge/          The bridge (.NET 8 console app).
  Program.cs            CLI: tray (default) / --configure /
                        setup / --self-test / --discover.
  TrayApp.cs            Tray icon: Configure / Self-test /
                        Get updates / Exit; restarts the relay
                        loop after a Configure save.
  ConfigureForm.cs      Configure window: printer list, Scan,
                        masked access-code + dashboard-key fields.
  Updater.cs            Get updates: public unauthenticated
                        version check first, GitHub CLI sign-in
                        fallback only if the repo is still
                        private; installer update with version
                        verification. No token is stored.
  Config.cs             Config load + restricted local key file.
  Models.cs             Protocol payloads (see docs/PROTOCOL.md).
  DashboardClient.cs    Outbound-only HTTPS client + Bearer auth.
  Safety.cs             Start-time interlock (refuse on changed
                        facts only — not a decision engine).
  Worker.cs             Poll → carry out → report loop.
  Discovery.cs          Opt-in own-subnet TCP probe.
  Printers/             One adapter per printer TYPE.
    BambuAdapter.cs     Local MQTT (8883, bblp + access code) +
                        implicit FTPS upload (990, /cache).
    SnapmakerAdapter.cs Moonraker HTTP API (7125).
config/bridge-config.example.json   Placeholders ONLY.
docs/PROTOCOL.md        Exact endpoints, payloads, intervals, auth.
dashboard-addon/        Dashboard-side code for a LATER plugin
  c3d-bridge-endpoints.php  update. NOT part of plugin v0.2.0.
```

## Dependencies (deliberately few)

- .NET 8 SDK on Windows (free, Microsoft). The SDK includes
  the Windows Desktop Runtime the v1.1.0 tray icon needs; the
  installer already installs the SDK if it is missing. (A
  runtime-only install needs the Desktop Runtime, not just the
  base runtime, for the tray window to open.)
- MQTTnet (Bambu local MQTT client) — the only NuGet package.
  Everything else is base-class-library: HttpClient, JSON, TCP,
  TLS. No web server, no database, no image libraries, no
  slicer libraries.

## Build from source

```
dotnet publish src/C3DBridge -c Release -r win-x64 --self-contained false -o ./app
```

(The installer does this for you. Mac/Linux: replace `win-x64`
with `osx-arm64` / `linux-x64`; on non-Windows the bridge runs
as a console relay (no tray icon), and the key file falls back
to chmod 600 off Windows.)

## Printer protocols (sources)

- Bambu local MQTT: printer IP port 8883, TLS, username `bblp`,
  password = LAN access code from the printer screen, self-signed
  certificate (LAN-only). Subscribe `device/{serial}/report`,
  publish `device/{serial}/request` (`pushall` for a full report).
  Report fields used: `print.gcode_state`, `print.mc_percent`,
  `print.mc_remaining_time`, `print.ams.ams[].tray[]`
  (`tray_type`, `tray_color`). FTPS: implicit TLS port 990, same
  credentials, uploads land in `/cache`.
- Snapmaker U1 (Paxx): Klipper + modified Moonraker, HTTP port
  7125. `GET /printer/objects/query?print_stats&virtual_sdcard&
  display_status`, `POST /server/files/upload` (print=false),
  `POST /printer/print/start`. Moonraker does not expose U1
  RFID filament data through any verified endpoint, so U1 AMS is
  always reported unknown in v1.

## Security

- Bridge API key: generated on the dashboard settings page (shown
  once, hash stored), kept in `bridge-key.txt` on the PC only,
  sent as `Authorization: Bearer` on every request. Rotate and
  revoke are dashboard-side; see `docs/PROTOCOL.md`.
- Printer access codes: `bridge-config.json` on the PC only.
- Install-time GitHub token (when the repo was private): memory
  only, one download, then gone. Never in the repo, never in any
  script. Get updates stores no token at all: it checks the
  public repo without sign-in, and only falls back to the GitHub
  CLI sign-in if the repo is still private.
- Logs never contain keys, access codes, or full URLs.

## Status: what is verified vs. what is not

Verified in this build: the code compiles cleanly (0 warnings),
publishes for win-x64, the CLI paths work (`--version`,
`--self-test` with and without config), the self-test correctly
reports the real dashboard host reachable and the bridge endpoints
not installed yet, and the safety interlock was unit-checked on
seven cases (allow/refuse behavior, including the name-vs-hex
incomparability rule).

NOT verified (honest): nothing has run against a real P2S, X2D,
or U1 yet. In particular, the Bambu FTPS upload and the
`project_file` start command, and the Snapmaker upload/start
calls, follow public documentation but await the first-print
check in SETUP.md Step 5. The dashboard-side endpoints and key
page (dashboard-addon/) are untested drop-in code for a later
plugin update, and the dashboard does not yet map products to
print files — jobs without a file are refused safely until then.

Private code — Creat3d World only. Not licensed for public use.
