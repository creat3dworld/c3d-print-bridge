// C3D Print Bridge — data models for the dashboard protocol (see docs/PROTOCOL.md).
//
// THIN RELAY RULE: these models carry messages only. The bridge never
// decides which printer runs which job, never orders a queue, never
// slices, and never prices anything. All decisions live in the
// dashboard on creat3dworld.com; this code moves messages faithfully
// and reports real status, or reports that something is unknown.

using System.Text.Json.Serialization;

namespace C3DBridge;

/// <summary>A printer the bridge knows how to reach. Routing only:
/// the dashboard assigns jobs to a printer BY NAME, and the bridge
/// looks the name up here. An unknown name is an error, never a
/// reason to pick a different printer.</summary>
public sealed record PrinterConfig
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    /// <summary>Printer brand/firmware, e.g. "bambu", "snapmaker", or any other brand typed in Configure. Unknown types use the generic adapter.</summary>
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("ip")] public string Ip { get; init; } = "";
    /// <summary>MAC address, when known, used to follow a DHCP printer
    /// to a new IP. Never a substitute for serial/access code.</summary>
    [JsonPropertyName("mac")] public string? Mac { get; init; }
    /// <summary>Optional still-image URL, owner-set from the printer's
    /// own docs (for example a Paxx webcam still endpoint). When
    /// unset, the bridge captures no snapshot and reports none —
    /// it never guesses a camera URL and never fakes an image.</summary>
    [JsonPropertyName("snapshot_url")] public string? SnapshotUrl { get; init; }
    /// <summary>Bambu only: printer serial number (for MQTT topics).</summary>
    [JsonPropertyName("serial")] public string? Serial { get; init; }
    /// <summary>Bambu only: LAN access code from the printer's screen.
    /// Snapmaker only: Moonraker/API key if the printer requires one.
    /// Stored ONLY in the local config file on the PC. Never committed.</summary>
    [JsonPropertyName("access_code")] public string? AccessCode { get; init; }
    /// <summary>Snapmaker Paxx API username, when created on the printer.
    /// Stored ONLY in the local config file on the PC. Never committed.</summary>
    [JsonPropertyName("paxx_user")] public string? PaxxUser { get; init; }
    /// <summary>Snapmaker Paxx API password, when created on the printer.
    /// Stored ONLY in the local config file on the PC. Never committed.</summary>
    [JsonPropertyName("paxx_password")] public string? PaxxPassword { get; init; }
    /// <summary>Generic login username, for brands outside Bambu/Snapmaker.
    /// For Bambu/Snapmaker this mirrors the brand login (Paxx username for Snapmaker).</summary>
    [JsonPropertyName("login_user")] public string? LoginUser { get; init; }
    /// <summary>Generic login password / API key, for brands outside Bambu/Snapmaker.
    /// Stored ONLY in the local config file on the PC. Never committed.</summary>
    [JsonPropertyName("login_password")] public string? LoginPassword { get; init; }
    /// <summary>True when this printer has a camera the bridge could
    /// report a feed from. False/missing means no camera recorded.</summary>
    [JsonPropertyName("camera")] public bool Camera { get; init; }
    /// <summary>Optional override: Bambu MQTT port (default 8883),
    /// Snapmaker Moonraker port (default 7125).</summary>
    [JsonPropertyName("port")] public int? Port { get; init; }
}

/// <summary>Local bridge configuration. Loaded from bridge-config.json
/// next to the executable. The bridge API key lives in a SEPARATE
/// restricted file (see KeyStore) and is never part of this file.</summary>
public sealed record BridgeConfig
{
    [JsonPropertyName("dashboard_base_url")] public string DashboardBaseUrl { get; init; } = "https://creat3dworld.com";
    [JsonPropertyName("bridge_name")] public string BridgeName { get; init; } = "shop-pc";
    /// <summary>Seconds between status pushes. Default 30.</summary>
    [JsonPropertyName("status_interval_seconds")] public int StatusIntervalSeconds { get; init; } = 30;
    /// <summary>Seconds between job polls. Default 30.</summary>
    [JsonPropertyName("job_poll_interval_seconds")] public int JobPollIntervalSeconds { get; init; } = 30;
    /// <summary>Seconds between heartbeats. Default 60. The dashboard
    /// treats a heartbeat older than 10 minutes as stale.</summary>
    [JsonPropertyName("heartbeat_interval_seconds")] public int HeartbeatIntervalSeconds { get; init; } = 60;
    /// <summary>Auto-update option (2273). Default OFF: nothing
    /// changes unless the user opts in. When ON, the tray app
    /// checks for a newer bridge version on startup and every
    /// 24 hours, and runs the normal update path when one is
    /// found. Stored in bridge-config.json, preserved by the
    /// installer like every other local setting.</summary>
    [JsonPropertyName("auto_update")] public bool AutoUpdate { get; init; }
    [JsonPropertyName("printers")] public List<PrinterConfig> Printers { get; init; } = new();
}

/// <summary>One queued physical-line job, assigned by the dashboard.
/// Digital lines never appear here: the dashboard never sends them.</summary>
public sealed record DashboardJob
{
    [JsonPropertyName("job_key")] public string JobKey { get; init; } = "";
    [JsonPropertyName("order_id")] public long OrderId { get; init; }
    [JsonPropertyName("printer_name")] public string PrinterName { get; init; } = "";
    [JsonPropertyName("items")] public List<DashboardJobItem> Items { get; init; } = new();
    [JsonPropertyName("color")] public string Color { get; init; } = "";
    [JsonPropertyName("material")] public string Material { get; init; } = "";
    /// <summary>Dashboard's own safety verdict for this job: "ready" or
    /// "held". The bridge executes ONLY "ready" jobs, and still runs
    /// its start-time interlock (see Safety) before forwarding start.</summary>
    [JsonPropertyName("safety_verdict")] public string SafetyVerdict { get; init; } = "held";
    /// <summary>Dashboard-hosted URL of the already-sliced print file
    /// (3MF/G-code). The bridge downloads and forwards it; it never
    /// slices, meshes, or modifies it.</summary>
    [JsonPropertyName("file_url")] public string FileUrl { get; init; } = "";
    [JsonPropertyName("file_name")] public string FileName { get; init; } = "";
    [JsonPropertyName("file_sha256")] public string FileSha256 { get; init; } = "";
}

public sealed record DashboardJobItem
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("qty")] public int Qty { get; init; } = 1;
}

/// <summary>Live printer state, reported exactly as observed.
/// "offline" means the bridge could not reach the printer at all.
/// Nothing here is ever guessed.</summary>
public sealed record PrinterStatus
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    // BUGFIX 2272: dashboard expects type/ip/status; older bridge
    // statuses carried neither type nor ip, so dashboard rows
    // rendered blank with status "unknown". Filled by adapters
    // from their own config; empty stays empty (never guessed).
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("ip")] public string Ip { get; init; } = "";
    [JsonPropertyName("state")] public string State { get; init; } = "offline";
    // idle | printing | paused | error | offline
    [JsonPropertyName("progress_percent")] public double? ProgressPercent { get; init; }
    [JsonPropertyName("remaining_seconds")] public long? RemainingSeconds { get; init; }
    [JsonPropertyName("current_job_key")] public string? CurrentJobKey { get; init; }
    [JsonPropertyName("detail")] public string Detail { get; init; } = "";
    [JsonPropertyName("ams")] public AmsState Ams { get; init; } = new();
    [JsonPropertyName("camera_available")] public bool CameraAvailable { get; init; }
}

/// <summary>What the printer reports as loaded, if anything.
/// Known=false means the printer did not report usable AMS data, so
/// the dashboard must treat material/color as unverifiable.</summary>
public sealed record AmsState
{
    [JsonPropertyName("known")] public bool Known { get; init; }
    [JsonPropertyName("material")] public string Material { get; init; } = "";
    [JsonPropertyName("color")] public string Color { get; init; } = "";
    [JsonPropertyName("detail")] public string Detail { get; init; } = "";
}

/// <summary>One bed-view snapshot, captured by the bridge on the
/// LAN and pushed to the dashboard. The dashboard stores only
/// the latest snapshot per printer and NEVER auto-passes a bed
/// check from it — a human must confirm from the image. The
/// snapshot_source is always "owner_snapshot_url": the bridge
/// never guesses a camera URL, and a printer with no snapshot
/// URL set sends no snapshot at all.</summary>
public sealed record CameraSnapshot
{
    [JsonPropertyName("printer_name")] public string PrinterName { get; init; } = "";
    [JsonPropertyName("printer_ip")] public string PrinterIp { get; init; } = "";
    [JsonPropertyName("printer_type")] public string PrinterType { get; init; } = "";
    [JsonPropertyName("captured_unix")] public long CapturedUnix { get; init; }
    [JsonPropertyName("snapshot_base64")] public string SnapshotBase64 { get; init; } = "";
    [JsonPropertyName("snapshot_source")] public string SnapshotSource { get; init; } = "owner_snapshot_url";
    [JsonPropertyName("camera_available")] public bool CameraAvailable { get; init; }
}

/// <summary>Per-job progress report back to the dashboard.
/// A partial send is never reported as complete: each job reports
/// its own terminal state.</summary>
public sealed record JobReport
{
    [JsonPropertyName("job_key")] public string JobKey { get; init; } = "";
    [JsonPropertyName("order_id")] public long OrderId { get; init; }
    [JsonPropertyName("printer_name")] public string PrinterName { get; init; } = "";
    // started | progress | completed | failed | refused_unsafe | cancelled
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("progress_percent")] public double? ProgressPercent { get; init; }
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";
    /// <summary>Base64 JPEG snapshot, ONLY when a still image was
    /// actually captured, and only on failure (or on dashboard
    /// request). Null means no image was available — never a
    /// placeholder, never a stock image.</summary>
    [JsonPropertyName("snapshot_base64")] public string? SnapshotBase64 { get; init; }
}
