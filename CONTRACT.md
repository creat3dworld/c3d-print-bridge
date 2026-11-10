# Naming Contract — Dashboard ↔ Bridge

**Canonical copy: this file** (`~/workspace/goals/shipping-dashboard-with-auto-rate-shopping/build/CONTRACT.md`).
The bridge repo holds an exact copy (`~/workspace/bridge-machine-update/CONTRACT.md`); the dashboard copy rules, and the bridge copy must match it exactly at bridge build time. Machine-readable twin: `CONTRACT.json` beside this file. Check script: `tools/check-contract.py` (stdlib only).

**Audited against:** dashboard `2026.1109.0159-bed-check-field` (staged tree) and bridge `2026.1109.2109-ams-safety-bed-watch` (Nov 9, 2026).

## The rule (Marlena, Nov 9, 2026)

**The dashboard owns all names. The bridge never invents a name.**

- Every field name, value word, file name, and folder name that crosses the wire is defined here, by the dashboard, first. The bridge uses only names on this list.
- A new name is defined by the dashboard in this contract first, then served, then read by the bridge. Never the other way round.
- The dashboard never renames a field without updating this contract and the bridge in the same change. The bridge never renames a field on its own.
- Extra fields are allowed: a side may send a field the other side ignores, and may serve a field the other side does not read — but only if the field is listed here as sent/ignored or served/unread. A field that is neither side's, or spelled differently on the two sides, is a bug, not a tolerance.
- Run `tools/check-contract.py` before any bridge or dashboard build ships. A FAIL blocks the ship.
- Why this exists: on Nov 9, 2026 the bridge invented `bed_check` while the dashboard used `bed_check_mode`, and the two builds would not have worked together. The audit that wrote this contract found a second live case of the same bug (printer-moved, §N) — proof the check is needed.

## How to read the tables

- **Bridge sends / dashboard reads** — the bridge's request body keys; the dashboard handler must read every key that is not listed as ignored.
- **Dashboard serves / bridge reads** — the dashboard's response keys; the bridge's model must find every key it reads.
- **Values** — the only allowed words for a field. Anything else is never sent, never read as valid.
- Keys are case-sensitive and spelled exactly as shown. `snake_case` everywhere on the wire, except the device-config blob (§K), which is camelCase for legacy reasons and must stay that way.

## A. Farm jobs — GET /wp-json/c3d-shipping/v1/farm/jobs

Dashboard serves (one object per job, envelope `{ jobs, dispatch }`); the bridge reads the subset marked ✓.

| Field | Bridge reads | Values / notes |
|---|---|---|
| job_id | ✓ | Dashboard job id, e.g. `farm_…` |
| title | ✓ | Free text |
| order_id | ✓ | Number |
| printer | ✓ | Printer name as the dashboard spells it |
| printer_ip | ✓ | |
| printer_type | ✓ | `bambu` / `snapmaker` / other brand word |
| material | ✓ | Free text, from the dashboard's material list |
| material_options | — | Served; bridge does not read |
| material_label | — | Served; bridge does not read |
| color | ✓ | Free text |
| qty | ✓ | Number, ≥ 1 |
| status | ✓ | `queued` / `pending_bridge_confirmation` / `printing` / `paused` / `done` / `failed` |
| bed_confirmed | ✓ | Boolean |
| ams_confirmed | ✓ | Boolean |
| safety_verdict | — | `ready` / `held`. Served for the record. The farm poller does NOT read it: the farm execution gate reads `bed_check` + `ams_confirmed` + `dispatch`. (The legacy `bridge/jobs` path always serves `held`; see §H.) |
| bed_check_mode | — | Stored audit trail, `printer` / `''`. Served; the bridge does not read it. The wire field is `bed_check`, not this one. |
| bed_delegated | — | Boolean, live computation. Served; the bridge does not read it. |
| bed_check | ✓ | `printer` / `''`. **The** bed-delegation wire field (0159). `printer` = the dashboard delegated the bed check to the printer's own start-time detection (Bambu only); anything else, including absent, means manual bed confirmation. |
| copies_done | — | Served; bridge does not read |
| plate | ✓ | Plate number as text |
| ams_slot | ✓ | Free text (e.g. `1`, `AMS A2`); unmappable stays unknown, never guessed |
| library_file_id | ✓ | e.g. `lib_…` |
| sliced_for | ✓ | Printer model the file was sliced for; `''` = not recorded, never guessed |
| sliced_for_models | — | Served; bridge does not read |
| sliced_for_target | — | Served; bridge does not read |
| file_sliced_for_other | — | Served; bridge does not read |
| file_sliced_models | — | Served; bridge does not read |
| plates | — | Served; bridge does not read |
| combo_group | — | Served; bridge does not read |
| combo_index | — | Served; bridge does not read |
| combo_total | — | Served; bridge does not read |
| file | — | Served; bridge does not read |
| file_name | ✓ | Dashboard's file name, used verbatim (§L) |
| file_url | ✓ | Dashboard file route; empty when the file is missing, never a broken URL |
| file_sha256 | ✓ | Lowercase hex; a mismatch discards the file, never prints it |
| file_kind | ✓ | `sliced` = verified G-code, anything else (including `unsliced`) means do not start |
| file_missing | ✓ | Boolean |
| file_needs_slicing | ✓ | Boolean |
| bridge_confirmation | — | Served; bridge does not read |
| bridge_started | — | Served; bridge does not read |
| dispatch_age_seconds | — | Served; bridge does not read |
| dispatch_stale | — | Served; bridge does not read |
| dispatch_stale_reason | — | Served; bridge does not read |
| dispatch_payload | — | Served; bridge does not read |
| dispatch | ✓ | `start` / `pause` / `stop` / `fail` / `''`. The bridge acts on `start`, `pause`, `stop`; any other non-empty value is logged and left untouched. |
| (envelope) jobs | ✓ | Array of the objects above |
| (envelope) dispatch | — | Map; the bridge reads the flattened per-job `dispatch`, not the map |

## B. Safety report — POST /wp-json/c3d-shipping/v1/farm/safety-report

Bridge sends; dashboard reads. Response: `{ ok, job_id, bed_confirmed, ams_confirmed }` (+ `bed_ignored` when a bed report was ignored for an already-active job).

| Field | Values / notes |
|---|---|
| job_id | Dashboard job id |
| ams_match | Boolean. `true` = proven AMS match, `false` = proven contradiction. The bridge sends only a proven verdict; an unprovable verdict sends nothing. |
| bed_vacant | **Dashboard-accepted, bridge-never-sends (by design).** The bridge cannot see the bed, so it never claims a bed is empty. The dashboard accepts it from no current sender; a future sender must follow the U1 two-stage rule. |

## C. Farm job report — POST /wp-json/c3d-shipping/v1/farm/job-report

Bridge sends; dashboard reads. Response: `{ ok, job_id, status }` (+ `stale`, `bed_check_failed`, `control_ack`, `command` where applicable).

| Field | Values / notes |
|---|---|
| job_id | Dashboard job id |
| status | `started` / `paused` / `finished` / `failed` / `held` / `control_failed` / `file_parsed` |
| reason | Free text. A reason naming HMS 0300-8017 is a terminal bed failure, never a retry. |
| filament_used_g | Number (grams) or null |
| progress_percent | Number 0–100 or null. **Dashboard-read, bridge-does-not-send on farm job-report** (the bridge sends it only on the legacy `bridge/job-report`, where the dashboard ignores it). |
| control_ack | `pause` / `stop` / `fail`. **Dashboard-read, bridge-does-not-send.** Absence means "not acknowledged". |
| command | `pause` / `stop` / `fail`. Required with `control_failed`. |
| declared_tool | e.g. `T0` (only with `file_parsed`) |
| tool_sequence | Comma-joined, e.g. `T0,T0` (only with `file_parsed`) |
| file_profile | Slicer profile name (only with `file_parsed`) |
| file_nozzle_mm | Number or null (only with `file_parsed`) |
| slicer | `orcaslicer` / `snapmaker-orca` / `bambu-studio` (only with `file_parsed`) |
| layers | Number or null (only with `file_parsed`) |
| file_total_g | Number > 0 (only with `file_parsed`). **Dashboard-read, bridge-not-yet-built.** |
| file_total_g_basis | `header` / `e_sum` / `''` (only with `file_parsed`). **Dashboard-read, bridge-not-yet-built.** |
| file_total_minutes | Number > 0 (only with `file_parsed`). **Dashboard-read, bridge-not-yet-built.** |
| file_total_minutes_basis | `header` / `e_sum` / `''` (only with `file_parsed`). **Dashboard-read, bridge-not-yet-built.** |

## D. Heartbeat & status — POST /wp-json/c3d-shipping/v1/bridge/heartbeat, /bridge/status

Bridge sends; dashboard reads. Both routes run the same handler. Response: `{ ok, bridge, printers_received, printers_kept }` (+ `new_key`, only when the caller authenticated with the previous key — see §D.3).

### D.1 Top level

| Field | Values / notes |
|---|---|
| bridge_name | This bridge's name. Legacy alias `bridge` is also accepted. |
| version | Bridge version string, e.g. `2026.1109.2109-ams-safety-bed-watch` |
| timestamp | Unix seconds. Sent; dashboard does not read it. |
| queue_length | Number. Sent on status; dashboard does not read it. |
| printers | Array of printer objects (§D.2). A heartbeat sends none; only a status push carries it. |
| slice_machine | Block (§D.4). Omitted entirely in legacy mode (no `machine_id`). |

### D.2 One printer object

| Field | Values / notes |
|---|---|
| name | Printer name |
| type | `bambu` / `snapmaker` / other brand word |
| ip | |
| status | Printer state word; the dashboard reads `status` first, then `state`. Bridge sends both with the same value. |
| state | Same as `status`. |
| serial | Bambu factory serial, or the learned Moonraker hostname for Snapmaker. Empty = unknown, never guessed. **Dashboard-read, bridge-does-not-send on status** — the dashboard backfills it from the stored device config when absent. |
| native_state | The printer's own raw state word, verbatim (Bambu `gcode_state`, Moonraker `print_stats.state`). Never mapped. |
| progress_percent | Number or null. Dashboard also accepts the alias `progress`. |
| remaining_seconds | Number or null |
| current_job_key | Dashboard also accepts the alias `current_job`. |
| detail | Free text |
| ams | Object: `known` (boolean), `material`, `color`, `detail`, `slots` (array of `{ slot, material, color }`), `observed_unix` (unix time of the reading). **The dashboard does NOT read `ams.observed_unix`** — sent, ignored. `slot` is the printer's own tray id. |
| light_state | `on` / `off` / `unknown`. Bridge-executed light state. |
| light_verified | `on` / `off` / `unknown`. Printer-confirmed light mode. **Sent; dashboard does not read it.** |
| light_verified_unix | Unix seconds. **Sent; dashboard does not read it.** |
| camera_available | Boolean |
| nozzle_diameters | Map, tool index (`"0"` = first toolhead) → mm. Only positive numbers kept. |
| declared_file_tool | e.g. `T0`, or `''` |
| declared_tool_sequence | Comma-joined, e.g. `T0,T0`, or `''` |
| file_profile | Slicer profile name, or `''` |
| file_nozzle_mm | Number or null |
| thermals | Array of `{ name, actual, target }` (Moonraker, U1 only). |
| printer_files | Array of `{ filename, size, modified }`, or null = not fetched. |
| job_history | Array of `{ filename, status, duration, started }`, or null = not fetched. `status` is Moonraker's own word (`completed` / `cancelled` / `error`), verbatim. |
| feed_type | `none` / `mjpeg` / `webrtc` / `hls` / `http` / `rtsp` / `other`. **Dashboard-read, bridge-does-not-send on status** (the bridge sends feed fields only with a bed snapshot, §G). |
| feed_url | HTTP(S) URL, credentials stripped, or `''`. **Dashboard-read, bridge-does-not-send on status.** |
| nozzle_temp_c / nozzle_temp / nozzle_temperature / extruder_temp | Number or null. **Dashboard-read, bridge-does-not-send** (the bridge sends no temperatures). First present alias wins. |
| bed_temp_c / bed_temp / bed_temperature | Same. **Dashboard-read, bridge-does-not-send.** |
| chamber_temp_c / chamber_temp / chamber_temperature | Same. **Dashboard-read, bridge-does-not-send.** |
| hms_codes | **Bridge-internal only. Never sent.** The bridge reads the printer's live HMS codes for its own bed watch; the status push does not carry them and the dashboard does not read them. |

Printer state values the bridge sends (`status`/`state`): `idle` / `printing` / `paused` / `error` / `offline`. The dashboard maps `standby`/`complete`/`completed`/`cancelled`/`canceled` → `idle` when it sees them, and anything else unmapped is stored verbatim (never `unknown` unless empty).

### D.3 Key rotation

When the bridge authenticates with the previous key, the heartbeat/status response carries `new_key` (the current key). The bridge adopts it and stores it. Key format: exactly 64 lowercase hex chars (`bin2hex(random_bytes(32))`). The bridge never adopts any other shape.

### D.4 Slice-machine block (`slice_machine`)

| Field | Values / notes |
|---|---|
| machine_id | Lowercase slug, `[a-z0-9_-]` only, e.g. `3d1pc`. Empty = legacy mode, block omitted. |
| name | Display name, e.g. `3d1PC`. Falls back to `machine_id`. |
| cpu | Free text (hardware probe) |
| gpu | Free text |
| ram | Free text |
| storage | Free text |
| os | Free text |
| slicers | Array, subset of `bambu-studio` / `snapmaker-orca` / `orcaslicer` |
| slice_status | `idle` / `busy` / absent |

## E. Slice jobs — GET /wp-json/c3d-shipping/v1/farm/slice-jobs, POST /farm/slice-report

### E.1 Poll (GET, query `machine_id`)

Dashboard serves envelope `{ jobs }`; each job is the stored slice record plus `input_url` and `input_sha256`. The bridge reads the subset marked ✓.

| Field | Bridge reads | Values / notes |
|---|---|---|
| slice_job_id | ✓ | |
| library_file_id | ✓ | |
| input_url | ✓ | Dashboard route serving the library file bytes (`farm/slice-file?slice_job_id=…`) |
| input_sha256 | ✓ | Lowercase hex; a mismatch discards the file |
| slicer | ✓ | `orcaslicer` / `snapmaker-orca` / `bambu-studio` |
| target_model | ✓ | Printer model the slice is for |
| plate | ✓ | Number, 1-based; 0 = all plates |
| preset_printer | ✓ | Preset file name under the bridge's slicer presets folder |
| preset_process | ✓ | Same |
| preset_filament | ✓ | Same |
| status | — | `pending` / `slicing` / `done` / `failed`. Served; the bridge filters on `pending` only. |
| machine_id | — | Served; the bridge asks with `?machine_id=` and is served only its own jobs |
| claimed_at | — | Served; bridge does not read |
| reason | — | Served; bridge does not read |

### E.2 Report (POST)

Bridge sends; dashboard reads. Response: `{ ok: true }`.

| Field | Values / notes |
|---|---|
| slice_job_id | |
| status | `started` / `done` / `failed`. `started` is the claim (first claim wins). |
| reason | Free text. `done` carries a human reason too (e.g. `Sliced for <model>, plate <n>.`). |
| gcode_base64 | Base64 G-code, 64 MB cap. Required with `done`. |
| file_name | Reported slice file name, `plate<N>.gcode` (§L). Required with `done`. |
| sliced_for | Target model. `done` falls back to the job's `target_model` when empty. |
| machine_id | Sent when the bridge has one; omitted in legacy mode. |

## F. Controls & printer moves

### F.1 Controls — GET /wp-json/c3d-shipping/v1/farm/controls, POST /farm/control-report

Controls served (envelope `{ controls }`); the bridge reads all three fields.

| Field | Values / notes |
|---|---|
| printer_ip | |
| printer_name | |
| command | `home` / `lights` / `power` / `reboot` |
| set_at | MySQL time string |
| set_unix | Unix seconds |

Control report sends: `printer_ip`, `command`, `ok` (boolean), `detail` (free text). Response: `{ ok: true }`.

### F.2 Printer moved — POST /wp-json/c3d-shipping/v1/farm/printer-moved

**NON-CONFORMING — see §N.** Canonical (dashboard) field names:

| Field | Values / notes |
|---|---|
| printer_name | |
| printer_type | |
| old_ip | |
| new_ip | |
| evidence | Free text, how the move was matched |
| serial | Bambu serial or Moonraker hostname; empty = unknown |

Response: `{ ok: true, manual_record_updated: <bool> }`.

## G. Bed snapshot & camera refresh

### G.1 Bed snapshot — POST /wp-json/c3d-shipping/v1/farm/bed-snapshot

Bridge sends; dashboard reads. A snapshot NEVER sets `bed_confirmed`. Response: `{ ok, printer_name, printer_ip, captured_unix }`.

| Field | Values / notes |
|---|---|
| printer_name | |
| printer_ip | At least one of `printer_ip` / `printer_name` is required |
| printer_type | |
| captured_unix | Unix seconds, within the last 24 hours |
| snapshot_base64 | Base64 JPEG, 100 bytes–2 MB, SOI marker required |
| snapshot_source | `owner_snapshot_url` / `auto_discovered_*` |
| feed_type | `none` / `mjpeg` / `webrtc` / `hls` / `http` / `rtsp` / `other` |
| feed_url | HTTP(S) only, credentials stripped, or `''` |
| camera_available | Boolean |

### G.2 Camera refresh — GET /wp-json/c3d-shipping/v1/farm/camera-refresh

Dashboard serves `{ requests }`: a map of printer IP → unix time. The bridge reads `requests`. Requests older than 10 minutes are pruned by the dashboard.

## H. Legacy bridge jobs — GET /wp-json/c3d-shipping/v1/bridge/jobs, POST /bridge/job-report

### H.1 Jobs (GET, query `bridge=<name>`)

Dashboard serves envelope `{ jobs, source }`; the bridge reads all fields.

| Field | Values / notes |
|---|---|
| job_key | = the farm job id |
| order_id | Number |
| printer_name | |
| items | Array of `{ name, qty }` (one item: the job title) |
| color | Free text |
| material | Free text |
| safety_verdict | Always `held` here, on purpose — farm execution is owned by FarmPoller via farm/jobs, never by this mirror. |
| file_url | |
| file_name | |
| file_sha256 | |

### H.2 Job report (POST, legacy)

Bridge sends; dashboard stores `job_key` + `status` only — the rest are accepted-but-ignored.

| Field | Values / notes |
|---|---|
| job_key | |
| status | `started` / `progress` / `completed` / `failed` / `refused_unsafe` / `cancelled` |
| order_id | Sent; dashboard ignores |
| printer_name | Sent; dashboard ignores |
| progress_percent | Sent; dashboard ignores |
| reason | Sent; dashboard ignores |
| snapshot_base64 | Sent; dashboard ignores |

## I. OCR — GET /wp-json/c3d-shipping/v1/bridge/ocr-jobs, POST /bridge/ocr-report

### I.1 Jobs (GET)

Dashboard serves envelope `{ jobs }`; each job: `job_id`, `gmail_id`, `pdf_b64`, `file_name`. The bridge reads all four.

### I.2 Report (POST)

Bridge sends; dashboard reads `job_id` + `text` only. Response: `{ ok, stored }` (+ `tracking` when stored).

| Field | Values / notes |
|---|---|
| job_id | |
| text | Extracted text only — never a PDF, never an image |
| engine | `tesseract`. **Sent; dashboard ignores.** |
| status | **Sent; dashboard ignores.** |
| reason | **Sent; dashboard ignores.** |

## J. Lithophane — GET /wp-json/c3d-lithophane/v1/generation-jobs, POST /generation-report

Separate plugin (`c3d-lithophane`), same rule.

### J.1 Jobs (GET)

Dashboard serves envelope `{ jobs }`; the bridge reads all fields: `job_id`, `order_id`, `photo_name`, `photo_size`, `drive_file_id`, `drive_folder_id`, `target_model`, `slicer` (`orcaslicer` / `snapmaker-orca` / `bambu-studio`), `preset_printer`, `preset_process`, `preset_filament`.

### J.2 Report (POST)

Bridge sends; dashboard reads all fields. Response: `{ ok, drive_verified }` on `done`, `{ ok }` on `failed`.

| Field | Values / notes |
|---|---|
| job_id | |
| status | `done` / `failed` |
| reason | Free text |
| model_3mf_name | Dashboard file name, used verbatim (§L) |
| gcode_name | Same |
| plate_3mf_name | Same |
| model_bytes | Number |
| gcode_bytes | Number |
| layers | Number |

## K. Install, pairing & device config

### K.1 Pairing exchange — POST /wp-json/c3d-shipping/v1/bridge/pairing/exchange

No Bearer key; the code is the credential (single-use, 30-minute expiry).

| Direction | Fields |
|---|---|
| Bridge sends | `code`, `machine_id`, `machine_name` |
| Dashboard returns | `bridge_key`, `machine_id` (no `machine_name` — the bridge keeps its own) |

### K.2 Prepackaged setup file — bridge-setup.json

Written by the dashboard installer into the install zip; read once by the bridge on first start, then deleted. Fields: `dashboard_url`, `machine_id`, `machine_name`, `bridge_key`. All optional except `bridge_key`; a missing `dashboard_url` means `https://creat3dworld.com`.

### K.3 Device config — POST/GET /wp-json/c3d-shipping/v1/bridge/device-config

**The one camelCase exception.** The device-config blob predates the snake_case wire and keeps its camelCase keys on both sides. Do not "fix" it to snake_case — the pull side reads camelCase.

Push (bridge sends `{ bridge, printers[] }`; `bridge` is legacy and ignored):

| Field (per printer) | Notes |
|---|---|
| name | |
| type | |
| ip | |
| deviceId | = serial (Bambu) or Moonraker hostname (Snapmaker) |
| username | |
| password | |
| apiKey | Sent; dashboard does not read it |
| serial | |
| accessCode | |
| paxxUser | |
| paxxPassword | |
| loginUser | |
| loginPassword | |
| camera | Boolean |
| mac | |

Fetch (dashboard returns `{ printers[], stored, stored_at }`); the bridge pull reads `ip`, `type`, `name`, `deviceId`/`serial`, `username`/`paxxUser`/`loginUser`, `password`/`paxxPassword`/`accessCode`/`loginPassword`, `mac`. The dashboard stores the blob encrypted and returns it verbatim; it reads `name`/`ip`/`type`/`serial`/`deviceId` from it only to fill heartbeat rows.

## L. File naming

- **The dashboard names all files and plates.** The bridge uses `file_name`, `file_url`, and `file_sha256` verbatim. It never renames a file, never invents a file name, and never serves a file under a different name than the dashboard gave it.
- **Printer file names never contain `_`** (Marlena's rule, Nov 6, 2026). Slice outputs reported to the dashboard are named `plate<N>.gcode` — the slicer's on-disk file is `plate_<N>.gcode` and the bridge looks it up by that name, but only the stripped name is ever reported or stored. Uploads with `_` are refused by the dashboard.
- **Drive folders are dashboard-named:** `3MF files`, `Sliced G-code files`, `Lithophane Orders / order-<number>/`. The bridge reads them from its Drive sync copy using the configured folder names; it never invents a folder name.
- **`machine_id`** is a lowercase slug, `[a-z0-9_-]` only (e.g. `3d1pc`, `3d2pc`).
- **Library file ids** are dashboard-issued (`lib_…`).

## M. Out of scope — not dashboard names

These cross the wire or the LAN but are owned by someone else, and this contract does not define them:

- **Bambu HMS codes** (e.g. `0300-8017` "Foreign Objects on Build Plate") — Bambu's names. The bridge reads them from the printer and repeats the code verbatim in reasons; the dashboard matches on the code and the full phrase.
- **Bambu MQTT `print` object** (`gcode_state`, `mc_percent`, `mc_remaining_time`, `ams`/`tray`/`tray_type`, `hms`/`code`/`attr`, `print_error`, `subtask_id`) — Bambu's names.
- **Moonraker objects** (`print_stats.state`, `server/files/list`, `server/history/list`, configfile, webcam list) — Moonraker's names.
- **Bridge-local `bridge-config.json` keys** (slicer paths, poll intervals, `drive_sync_root`, `home_power_allowed`, `auto_update`, `non3d_printers`, `action1_*`, lithophane paths) — bridge-local. Only the keys that cross the wire are in this contract: `dashboard_base_url`, `bridge_name`, `machine_id`, `machine_name`.
- **WordPress error codes** (`c3d_farm_no_job`, `c3d_slice_no_job`, …) — dashboard-internal.
- **Printer `native_state`** — the printer's own raw word, verbatim, never a dashboard name.

## N. Known non-conformances (open — fix is a separate build)

### N.1 Printer-moved report — fixed in bridge 2026.1109.2136-printer-moved-names

- **Bridge sends** (`DashboardClient.ReportPrinterMovedAsync`, called from `IpReconciler`): `printer_name`, `printer_type`, `old_ip`, `new_ip`, `evidence`, `serial`.
- **Dashboard reads** (`C3D_Watchtower::rest_printer_moved`): `printer_name`, `printer_type`, `old_ip`, `new_ip`, `evidence`, `serial`.
- **Was broken until 2136:** the bridge sent `name`, `type`, `match_evidence` instead, so `printer_name` was absent and the handler returned 400 "A printer move report must name the printer and both addresses." **Every printer-move report from the bridge failed.** Fixed by renaming the three keys on the bridge side only — the dashboard names were canonical throughout.

## O. Changing this contract

1. The dashboard defines the new or renamed name here (this file + `CONTRACT.json`) first.
2. The dashboard serves it.
3. The bridge reads it.
4. Run `tools/check-contract.py` — it must PASS — before either build ships.
5. Update the "Audited against" line at the top with the new dashboard and bridge versions.
6. A rename is never one-sided: the contract, the dashboard, and the bridge change together, or not at all.
