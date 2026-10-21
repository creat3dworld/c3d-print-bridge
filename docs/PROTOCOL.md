# C3D Print Bridge — dashboard protocol (v1)

This document is the contract between the bridge (this repo) and the
C3D Shipping dashboard plugin on creat3dworld.com. The dashboard-side
implementation lives in `dashboard-addon/c3d-bridge-endpoints.php`
(a drop-in for a LATER plugin update — it is NOT part of plugin
v0.2.0, and the endpoints below return 404 until that update ships).

## Direction of travel (read this first)

The dashboard is hosted remotely and CANNOT reach the home LAN (NAT).
The bridge therefore initiates EVERY contact, outbound, over HTTPS.
The bridge never opens a listening port and never accepts inbound
connections. If the bridge is off, the dashboard simply sees stale
status — it never dials into the house.

## Authentication — bridge API key (required)

Every bridge request carries:

```
Authorization: Bearer <bridge-api-key>
```

- The key is generated on the dashboard settings page ("Bridge key"
  section in the later plugin update). It is a 48-byte random value,
  shown ONCE at generation time. The dashboard stores only a hash
  (HMAC-SHA256 with the site salt); the plaintext is never stored.
- The bridge stores the key in ONE restricted local file on the PC
  (`bridge-key.txt` next to the executable, inside the owner's
  user-profile folder). It is never in this repo, never in code,
  never in `bridge-config.json`.
- A missing, revoked, or rotated key gets HTTP 401 on every
  endpoint. The bridge then pauses job polling, logs a plain
  warning, and keeps sending slow heartbeats so the dashboard can
  show the problem. It never retries a rejected key aggressively
  and never falls back to unauthenticated requests.
- Rotate: generate a new key on the dashboard (the old key stops
  working immediately), then run `C3DBridge.exe setup` on the PC and
  paste the new key. Revoke: delete the key on the dashboard (all
  bridge traffic stops being accepted at once).

## Base and versioning

Base: `{dashboard_base_url}` from the bridge config
(default `https://creat3dworld.com`). All paths below are under:

```
/wp-json/c3d-shipping/v1/bridge/
```

All requests and responses are JSON. The bridge sends
`User-Agent: C3DBridge/1.0`. A 404 on any endpoint means the
dashboard-side bridge code is not installed yet (later plugin
update) — the bridge reports that plainly and does NOT treat it
as a printer problem.

## Polling intervals (bridge-initiated)

| Call | Interval | Notes |
|---|---|---|
| Heartbeat | 60 s | Dashboard treats >10 min old as stale |
| Status push | 30 s | Printers running/idle, AMS, queue length |
| Job poll | 30 s | Only when the key is accepted |
| Job progress | On ≥5% change, or every 5 min while printing | Per active job |
| Failure backoff | 30 s → 60 s → 120 s → max 300 s | On network/dashboard errors; 401 is NOT retried this way (see above) |

## Endpoints

### POST /bridge/heartbeat

Body: `{ "bridge_name": "shop-pc", "version": "1.0.0",
"timestamp": 1790000000 }` (Unix seconds, UTC).
Effect: dashboard updates its bridge heartbeat timestamp.
Response: `200 { "ok": true }`.

### POST /bridge/status

Body:
```json
{
  "bridge_name": "shop-pc",
  "version": "1.0.0",
  "queue_length": 2,
  "printers": [
    {
      "name": "P2S",
      "state": "idle",
      "progress_percent": null,
      "remaining_seconds": null,
      "current_job_key": null,
      "detail": "",
      "ams": { "known": true, "material": "PLA", "color": "000000", "detail": "AMS tray 0." },
      "camera_available": false
    }
  ]
}
```
`state` is one of: `idle`, `printing`, `paused`, `error`, `offline`.
`ams.known=false` means the printer did not report usable AMS data —
the dashboard must treat material/color as unverifiable, never guess.
`camera_available` means the bridge can actually supply a still image
(false in v1 for both printer types — see README limits).
Effect: dashboard updates the shop status board from this report
only. Nothing is shown as running without a bridge report.
Response: `200 { "ok": true }`.

### GET /bridge/jobs?bridge=shop-pc

Response:
```json
{
  "jobs": [
    {
      "job_key": "job_0",
      "order_id": 1234,
      "printer_name": "P2S",
      "items": [ { "name": "Ryobi Vacuum Mount", "qty": 2 } ],
      "color": "Black",
      "material": "",
      "safety_verdict": "ready",
      "file_url": "https://creat3dworld.com/wp-content/uploads/c3d-print-files/ryobi-mount.3mf",
      "file_name": "ryobi-mount.3mf",
      "file_sha256": ""
    }
  ]
}
```
Rules the dashboard MUST follow (the bridge enforces its half too):
- Only physical-line jobs. Digital lines are never sent (the
  dashboard already skips them when queueing).
- Only jobs for printers, assigned BY NAME. The bridge executes a
  job only on the printer with that exact name; an unknown name is
  reported as failed, never substituted.
- `safety_verdict` is the dashboard's own evaluation: `"ready"` or
  `"held"`. The bridge executes only `"ready"` jobs.
- `material` is empty today (the dashboard does not record material
  per line item yet); the bridge treats empty as "unknown", and its
  start-time interlock skips comparisons it cannot make.
- `file_url` is the already-sliced print file. **OPEN ITEM (see
  below):** the dashboard does not yet map products to print files,
  so `file_url` is empty for most products today. An empty file_url
  makes the bridge report the job failed with reason "No print file
  supplied" — it never prints without a file, and never slices one.

### POST /bridge/job-report

Body:
```json
{
  "job_key": "job_0",
  "order_id": 1234,
  "printer_name": "P2S",
  "status": "completed",
  "progress_percent": 100,
  "reason": "Print finished (printer idle after printing).",
  "snapshot_base64": null
}
```
`status` is one of: `started`, `progress`, `completed`, `failed`,
`refused_unsafe`, `cancelled`. A partial send is never reported as
complete: each job reports its own terminal state.
`snapshot_base64` is a JPEG (≤2 MB) ONLY when a still was actually
captured — on failure, or on dashboard request. Null means no image
was available. The dashboard stores it with the failed-print log.
Effect: dashboard updates the job's status and adds an order note.
Response: `200 { "ok": true }`.

## File downloads

The bridge downloads `file_url` with the same Bearer key, verifies
SHA-256 when `file_sha256` is supplied (a mismatch deletes the file
and fails the job), uploads it to the printer, and deletes the local
copy when the job reaches a terminal state. Files are forwarded
byte-for-byte; the bridge never slices, repairs, or modifies them.

## Error semantics

| HTTP | Meaning | Bridge behavior |
|---|---|---|
| 200 | OK | Normal |
| 401 | Key missing/revoked/rotated | Pause job polling; warn; slow heartbeats |
| 404 | Endpoints not installed yet | Report plainly; keep local polling |
| 5xx / timeout | Dashboard unreachable | Backoff (see intervals); never fake status |

## Single-bridge assumption (v1)

There is one bridge: the shop PC. The jobs endpoint returns all
queued jobs; each bridge executes only jobs naming printers it
knows. If a second bridge ever appears, the dashboard must scope
jobs per bridge before this protocol can support it.
