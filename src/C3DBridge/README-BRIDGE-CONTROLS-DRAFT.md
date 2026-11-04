# Bridge farm-controls draft — DRAFT, NOT compile-verified, NOT ready to deploy

Built overnight Oct 29, 2026 from the live bridge source (the copy in
`~/workspace/goals/shipping-dashboard-with-auto-rate-shopping/bridge/src/C3DBridge/`).
No .NET SDK exists on the Muse VM, so this package has NOT been compiled
and has NOT been tested against a live printer. It is a review draft for
the morning: compile it on a Windows machine with the .NET SDK, then
live-test it on the shop PC before any deploy.

## What changed (3 files only)

- `DashboardClient.cs` — new `PollFarmControlsAsync()` (GET farm/controls)
  and `ReportControlAsync()` (POST farm/control-report), following the
  existing `PollFarmJobsAsync` / `ReportFarmJobAsync` patterns exactly.
- `Models.cs` — new `FarmControl` record (printer_ip, printer_name,
  command, set_at, set_unix), matching the dashboard's controls shape.
- `FarmPoller.cs` — new `PollControlsAsync()`, called at the top of
  every farm poll (the worker already calls `PollAsync` on its loop, so
  no worker change was needed).

## What it does

For each pending control intent the dashboard recorded (Lights / Home /
Power buttons), the bridge resolves the printer by name, then by IP —
never a substitute printer — and:

- **Lights on a Snapmaker (U1): executed.** Wired to the bridge's
  existing `SetCavityLedAsync(true)` (Moonraker `SET_LED
  LED=cavity_led`). Lights ON only: the dashboard control carries no
  on/off value, so a toggle would be a guess, and lights-on is the
  documented purpose (making the bed visible for the next snapshot).
- **Lights on a Bambu: reported as not executed.** No verified MQTT
  light command exists in the bridge source. (An Oct 27 note claiming a
  Bambu `ledctrl` command was checked against the code on disk and is
  NOT verified — do not treat it as proven.)
- **Home and Power on every printer: reported as not executed.**
  Homing a printer that may hold a print or an object, and powering a
  printer off, both need a safety decision that has not been made.
- Every outcome — executed or not — is reported back to the dashboard,
  which clears the pending intent and shows the result. Unsupported
  intents are never silently dropped and never executed on a guess.

## What it does NOT do

- No print sends, starts, pauses, or stops are added or changed.
- No Bambu light command, no home, no power execution.
- No change to job dispatch, safety gates, or file delivery.
- The bridge's `FarmJob` model still lacks the file-delivery fields
  (file_url / file_sha256 / file_kind) that the dashboard's
  `rest_jobs` now serves — that gap is flagged in the overnight report
  and is NOT fixed in this draft, because fixing the actual start path
  is safety-critical and needs a live test, not an overnight edit.

## Morning approval must cover, in order

1. `dotnet build` on a Windows machine — must pass before anything else.
2. Live bridge test on the shop PC: click Lights on the U1 in the
   dashboard, confirm the bridge log shows the control, the U1 light
   actually turns on, and the dashboard shows the confirmation.
3. Bambu lights: verify a real MQTT light command from Bambu's own
   documentation before any Bambu light code is written.
4. Home / Power: Marlena's safety decision (when, if ever, the bridge
   may home or power a printer) before any code for those commands.
5. Only then deploy, via the standing bridge deploy route (scheduled
   task "C3D Print Bridge" — never launch the exe directly).
