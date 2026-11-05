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
    /// <summary>AUTO-START BUILD — Moonraker hostname learned
    /// from the printer itself (GET /printer/info) and saved
    /// to the LOCAL config only, so IP-change auto-detection
    /// can recognize this printer at a new address. Learned,
    /// never typed by hand; empty until the bridge has seen
    /// the printer answer /printer/info. 2026.1030.1511-farm-dedupe:
    /// Moonraker exposes no factory serial, so this hostname
    /// IS now the printer's dashboard identity key — sent as
    /// the serial on status reports and move reports
    /// (SnapmakerAdapter.SerialKey). It is sent ONLY in that
    /// identity role, never as a separate hostname claim.</summary>
    [JsonPropertyName("moonraker_hostname")] public string? MoonrakerHostname { get; init; }
    /// <summary>Still-image URL for Bed View. Owner-set from the
    /// printer's own docs (for example a Paxx webcam still
    /// endpoint), or auto-discovered by the bridge (2275) from
    /// the printer's own authenticated local endpoints and
    /// saved here in the LOCAL config only. When unset and
    /// auto-discovery finds nothing, the bridge captures no
    /// snapshot and reports none — it never fakes an image,
    /// and auto-discovery never overwrites a URL already set
    /// here.</summary>
    [JsonPropertyName("snapshot_url")] public string? SnapshotUrl { get; init; }
    /// <summary>True when SnapshotUrl was filled by bridge
    /// auto-discovery (2275) rather than set by the owner, so
    /// the snapshot_source label stays distinct across
    /// restarts. Never sent to the dashboard on its own — only
    /// the snapshot JPEG and its source label are sent.</summary>
    [JsonPropertyName("snapshot_auto_discovered")] public bool SnapshotAutoDiscovered { get; init; }
    /// <summary>Live-feed URL for Bed View (2276, Snapmaker
    /// only so far). Owner-set, or auto-discovered by the
    /// bridge from the printer's own Moonraker webcam list
    /// and saved here in the LOCAL config only. The bridge
    /// never sends this URL to the dashboard when it is an
    /// RTSP or other non-HTTP URL — the dashboard then gets
    /// only the feed-type label, and snapshots remain the
    /// fallback. Auto-discovery never overwrites a URL
    /// already set here.</summary>
    [JsonPropertyName("stream_url")] public string? StreamUrl { get; init; }
    /// <summary>True when StreamUrl was filled by bridge
    /// auto-discovery (2276) rather than set by the owner.
    /// Never sent to the dashboard on its own — only the
    /// feed-type label and, for HTTP feeds, a
    /// credential-stripped feed URL are sent.</summary>
    [JsonPropertyName("stream_auto_discovered")] public bool StreamAutoDiscovered { get; init; }
    /// <summary>0.9.6 Snapmaker-first camera fields (local
    /// config only, never sent to the dashboard except as
    /// the snapshot JPEG itself and its source/feed labels).
    /// When set, these win over the generic snapshot_url /
    /// stream_url for a Snapmaker printer only, so a U1 can
    /// have its own webcam URLs without touching Bambu
    /// printers. When blank, the generic fields apply.
    /// Paxx IS installed on Marlena's U1 (Oct 23, 2026), so
    /// the Klipper/Moonraker webcam path is available; the
    /// actual endpoint on her printer is still unverified
    /// until a live probe runs from the shop PC via
    /// Action1 — no endpoint is claimed verified here.</summary>
    [JsonPropertyName("snapmaker_snapshot_url")] public string? SnapmakerSnapshotUrl { get; init; }
    [JsonPropertyName("snapmaker_stream_url")] public string? SnapmakerStreamUrl { get; init; }
    /// <summary>Bambu only: printer serial number (for MQTT topics).
    /// This is also the dashboard identity key for Bambu printers:
    /// status reports carry it so the dashboard updates the
    /// existing card in place on an IP move (2026.1030.1511-farm-dedupe).
    /// Snapmaker printers use the learned Moonraker hostname as
    /// their runtime identity instead (SnapmakerAdapter.SerialKey),
    /// because Moonraker exposes no factory serial.</summary>
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
    /// <summary>AUTO-START BUILD — Home/Power safety gate (A7).
    /// Default FALSE: Home and Power control intents are
    /// reported as gated, never executed, until the owner
    /// decides when (if ever) the bridge may home or power
    /// a printer. Setting this true WITHOUT that decision is
    /// a misconfiguration, not an approval: the flag only
    /// takes effect after the owner states the A7 decision.
    /// Power has no verified execution path on any brand
    /// even when this is true (it needs a smart-plug
    /// decision), so power stays reported-unsupported.</summary>
    [JsonPropertyName("home_power_allowed")] public bool HomePowerAllowed { get; init; }
    /// <summary>AUTO-START BUILD — slicer executables for
    /// in-dashboard slicing. Full paths to the slicer CLIs
    /// on this PC, or empty = not configured (slice jobs
    /// then fail honestly instead of running a guessed
    /// binary). Discovered by the slice pilot script, never
    /// guessed here.</summary>
    [JsonPropertyName("slicer_orcaslicer_path")] public string? SlicerOrcaslicerPath { get; init; }
    [JsonPropertyName("slicer_snapmaker_orca_path")] public string? SlicerSnapmakerOrcaPath { get; init; }
    [JsonPropertyName("slicer_bambu_studio_path")] public string? SlicerBambuStudioPath { get; init; }
    /// <summary>AUTO-START BUILD — folder holding the full
    /// exported slicer preset JSONs (printer, process,
    /// filament) referenced by slice jobs. Presets must be
    /// full exports: the slicer CLI does not resolve
    /// inherits/include, and an unresolved preset slices
    /// "successfully" onto a generic bed with no printer
    /// start G-code.</summary>
    [JsonPropertyName("slicer_presets_dir")] public string? SlicerPresetsDir { get; init; }
    /// <summary>AUTO-START BUILD — minutes a slice run may
    /// take before the bridge kills it and reports failure.
    /// Default 15.</summary>
    [JsonPropertyName("slice_timeout_minutes")] public int SliceTimeoutMinutes { get; init; } = 15;
    /// <summary>Non-3D printer queues the owner picked by Windows
    /// queue name (Oct 24, 2026). These are NOT 3D printers and
    /// are never in PrinterRegistry: no 3D job, farm job, or
    /// safety check ever touches them. Label/document output
    /// only, once a dashboard route exists for it.</summary>
    [JsonPropertyName("non3d_printers")] public List<string> Non3DPrinters { get; init; } = new();
    /// <summary>Action1 agent installer (2277). LOCAL PATH ONLY, set
    /// when the operator picks the account-tied installer they
    /// downloaded from their own Action1 console. Never a URL,
    /// never a token, never committed with a real value: the
    /// example config leaves this blank, and a real path lives
    /// only in bridge-config.json on the PC.</summary>
    [JsonPropertyName("action1_installer_path")] public string? Action1InstallerPath { get; init; }
    /// <summary>Action1 agent download link (2282). LOCAL ONLY, set
    /// when the operator pastes the account-tied download link
    /// from their own Action1 console (Other Options → Direct
    /// download) into the tray menu prompt. Never a token on
    /// its own, never committed with a real value, never
    /// logged, never sent to the dashboard: the example config
    /// leaves this blank, and a real link lives only in
    /// bridge-config.json on the PC. The tray menu downloads
    /// from this link (Action1 host only, .msi only) and then
    /// installs from the downloaded file, so the operator no
    /// longer has to download and pick a file by hand.</summary>
    [JsonPropertyName("action1_download_url")] public string? Action1DownloadUrl { get; init; }
    /// <summary>Google Drive for Desktop fallback read
    /// (2026.1105). Local root of the Drive for Desktop sync
    /// (the "My Drive" folder on the mounted virtual drive).
    /// Blank = auto-detect from the mounted virtual drive's
    /// volume label (DriveDesktopClient.VirtualDriveLetter)
    /// + "My Drive". Set this only when the virtual drive
    /// letter or the sync folder was moved. Never a URL,
    /// never a credential — a local path only, and it lives
    /// only in bridge-config.json on the PC.</summary>
    [JsonPropertyName("drive_sync_root")] public string? DriveSyncRoot { get; init; }
    /// <summary>Folder under the Drive sync root holding model
    /// (.3mf) files, as created on the dashboard's Drive
    /// files tab. Default "3MF files".</summary>
    [JsonPropertyName("drive_3mf_folder")] public string Drive3mfFolder { get; init; } = "3MF files";
    /// <summary>Folder under the Drive sync root holding sliced
    /// G-code files (.gcode/.gco/.gcode.3mf plates), as
    /// created on the dashboard's Drive files tab. Default
    /// "Sliced G-code files". Farm jobs fall back to this
    /// folder when the dashboard file download fails.</summary>
    [JsonPropertyName("drive_sliced_folder")] public string DriveSlicedFolder { get; init; } = "Sliced G-code files";
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

/// <summary>A Print Farm job from GET farm/jobs, plus its
/// dispatch command. AUTO-START BUILD (2026.1029 farm-auto):
/// farm jobs now carry their file-delivery fields (the
/// dashboard has served them since the farm build), so the
/// FarmPoller execution stage can download, upload, and
/// start them. A job with FileMissing, FileNeedsSlicing, or
/// a non-sliced FileKind is never started — it is held and
/// logged, never guessed into a print.</summary>
public sealed record FarmJob
{
    [JsonPropertyName("job_id")] public string JobId { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("order_id")] public long OrderId { get; init; }
    [JsonPropertyName("printer")] public string Printer { get; init; } = "";
    [JsonPropertyName("printer_ip")] public string PrinterIp { get; init; } = "";
    [JsonPropertyName("printer_type")] public string PrinterType { get; init; } = "";
    [JsonPropertyName("material")] public string Material { get; init; } = "";
    [JsonPropertyName("color")] public string Color { get; init; } = "";
    [JsonPropertyName("qty")] public int Qty { get; init; } = 1;
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("bed_confirmed")] public bool BedConfirmed { get; init; }
    [JsonPropertyName("ams_confirmed")] public bool AmsConfirmed { get; init; }
    [JsonPropertyName("dispatch")] public string Dispatch { get; init; } = "";
    /// <summary>Authenticated dashboard file route for this
    /// job (GET farm/file?job_id=...). Empty when the file
    /// is missing — never a broken or public URL.</summary>
    [JsonPropertyName("file_url")] public string FileUrl { get; init; } = "";
    [JsonPropertyName("file_name")] public string FileName { get; init; } = "";
    [JsonPropertyName("file_sha256")] public string FileSha256 { get; init; } = "";
    /// <summary>"sliced" for a verified G-code file, anything
    /// else (including "unsliced") means do not start.</summary>
    [JsonPropertyName("file_kind")] public string FileKind { get; init; } = "";
    [JsonPropertyName("file_missing")] public bool FileMissing { get; init; }
    [JsonPropertyName("file_needs_slicing")] public bool FileNeedsSlicing { get; init; }
    [JsonPropertyName("plate")] public string Plate { get; init; } = "";
    /// <summary>Dashboard job's AMS/slot label as entered by the
    /// operator (free text, e.g. "1" or "AMS A2"). The bridge
    /// maps it to a tool index only via
    /// GcodeFileParser.SlotToToolIndex; unmappable values
    /// stay unknown, never guessed.</summary>
    [JsonPropertyName("ams_slot")] public string AmsSlot { get; init; } = "";
    [JsonPropertyName("library_file_id")] public string LibraryFileId { get; init; } = "";
    /// <summary>Printer model the sliced file was recorded
    /// for ("" = not recorded, never guessed). A known
    /// mismatch against the target printer holds the job.</summary>
    [JsonPropertyName("sliced_for")] public string SlicedFor { get; init; } = "";
}

/// <summary>One in-dashboard slice request from GET
/// farm/slice-jobs (AUTO-START BUILD). The dashboard
/// records these when the Slice action is used on a File
/// Library entry; the bridge downloads the library file,
/// runs the slicer CLI on the shop PC, verifies the
/// G-code, and reports it back. The bridge never slices
/// on the web server and never invents settings: preset
/// files come from the bridge's local slicer presets
/// folder, named in the job. Slicer values: "orcaslicer",
/// "snapmaker-orca", "bambu-studio".</summary>
public sealed record SliceJob
{
    [JsonPropertyName("slice_job_id")] public string SliceJobId { get; init; } = "";
    [JsonPropertyName("library_file_id")] public string LibraryFileId { get; init; } = "";
    /// <summary>Authenticated dashboard route serving the
    /// library file bytes for this slice job.</summary>
    [JsonPropertyName("input_url")] public string InputUrl { get; init; } = "";
    [JsonPropertyName("input_sha256")] public string InputSha256 { get; init; } = "";
    [JsonPropertyName("slicer")] public string Slicer { get; init; } = "";
    /// <summary>Printer model the slice is for; becomes the
    /// library entry's "Sliced for" value on success.</summary>
    [JsonPropertyName("target_model")] public string TargetModel { get; init; } = "";
    /// <summary>Plate number to slice (1-based; 0 = all
    /// plates, in which case plate_1.gcode is reported).</summary>
    [JsonPropertyName("plate")] public int Plate { get; init; } = 1;
    /// <summary>Preset JSON file names under the bridge's
    /// slicer presets folder (full exported presets, never
    /// fragments — the slicer CLI does not resolve
    /// inherits/include, and an unresolved preset slices
    /// "successfully" onto a generic bed).</summary>
    [JsonPropertyName("preset_printer")] public string PresetPrinter { get; init; } = "";
    [JsonPropertyName("preset_process")] public string PresetProcess { get; init; } = "";
    [JsonPropertyName("preset_filament")] public string PresetFilament { get; init; } = "";
}

/// <summary>One queued Facebook label-PDF OCR job from GET
/// bridge/ocr-jobs (dashboard 2026.1024.2046-fb-ocr). The PDF
/// arrives base64-encoded over the authenticated channel; the
/// bridge renders it locally, OCRs it locally, and posts ONLY
/// extracted text back. The PDF and its image never leave the
/// shop PC except as that text.</summary>
public sealed record OcrJob
{
    [JsonPropertyName("job_id")] public string JobId { get; init; } = "";
    [JsonPropertyName("gmail_id")] public string GmailId { get; init; } = "";
    [JsonPropertyName("pdf_b64")] public string PdfB64 { get; init; } = "";
    [JsonPropertyName("file_name")] public string FileName { get; init; } = "";
}

/// <summary>One pending farm control intent from GET
/// farm/controls (DRAFT 2026-10-29). The dashboard records
/// these when the Lights / Home / Power buttons are clicked;
/// the bridge executes what it safely can and reports back.
/// Command is "home", "lights", or "power" (dashboard-side
/// validation); anything else is reported as unsupported,
/// never executed.</summary>
public sealed record FarmControl
{
    [JsonPropertyName("printer_ip")] public string PrinterIp { get; init; } = "";
    [JsonPropertyName("printer_name")] public string PrinterName { get; init; } = "";
    [JsonPropertyName("command")] public string Command { get; init; } = "";
    [JsonPropertyName("set_at")] public string SetAt { get; init; } = "";
    [JsonPropertyName("set_unix")] public long SetUnix { get; init; }
}

public sealed record DashboardJobItem
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("qty")] public int Qty { get; init; } = 1;
}

/// <summary>Live printer state, reported exactly as observed.
/// "offline" means the bridge could not reach the printer at all.
/// Nothing here is ever guessed.
/// AUTO-START BUILD additions: NativeState carries the
/// printer's own raw state word verbatim (Bambu gcode_state,
/// Moonraker print_stats.state) beside the mapped State, so
/// a finish word (FINISH / complete) survives instead of
/// being flattened into "idle". LightState carries the
/// bridge-known chamber/cavity light state ("on" / "off" /
/// "unknown") — set only from a light control this bridge
/// actually executed, never guessed from a snapshot.</summary>
public sealed record PrinterStatus
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    // BUGFIX 2272: dashboard expects type/ip/status; older bridge
    // statuses carried neither type nor ip, so dashboard rows
    // rendered blank with status "unknown". Filled by adapters
    // from their own config; empty stays empty (never guessed).
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("ip")] public string Ip { get; init; } = "";
    // 2026.1030.1511-farm-dedupe — the dashboard identity key,
    // sent on every status report so the dashboard updates the
    // existing card in place on an IP move instead of adding a
    // second card. Bambu: the factory serial. Snapmaker: the
    // learned Moonraker hostname (SnapmakerAdapter.SerialKey).
    // Empty when unknown — never a guessed value.
    [JsonPropertyName("serial")] public string Serial { get; init; } = "";
    [JsonPropertyName("state")] public string State { get; init; } = "offline";
    // idle | printing | paused | error | offline
    [JsonPropertyName("native_state")] public string NativeState { get; init; } = "";
    [JsonPropertyName("progress_percent")] public double? ProgressPercent { get; init; }
    [JsonPropertyName("remaining_seconds")] public long? RemainingSeconds { get; init; }
    [JsonPropertyName("current_job_key")] public string? CurrentJobKey { get; init; }
    [JsonPropertyName("detail")] public string Detail { get; init; } = "";
    [JsonPropertyName("ams")] public AmsState Ams { get; init; } = new();
    [JsonPropertyName("camera_available")] public bool CameraAvailable { get; init; }
    [JsonPropertyName("light_state")] public string LightState { get; init; } = "unknown";
    /// <summary>GATE-LIGHT BUILD (2026.1105): the chamber/cavity
    /// light mode the PRINTER itself last reported
    /// (lights_report), "on" / "off" / "unknown", with the unix
    /// time it was observed. Unlike LightState — which records
    /// only that this bridge once executed a lights control —
    /// this is printer-confirmed, and its age shows when the
    /// confirmation is stale. 0 = never observed. Older
    /// dashboards ignore unknown JSON fields.</summary>
    [JsonPropertyName("light_verified")] public string LightVerified { get; init; } = "unknown";
    [JsonPropertyName("light_verified_unix")] public long LightVerifiedUnix { get; init; }
    /// <summary>BRIDGE-READS BUILD: per-toolhead nozzle diameters
    /// in mm, keyed by tool index ("0" = first toolhead,
    /// "1" = second, …), from the printer's own config
    /// (Snapmaker Moonraker configfile query). Empty =
    /// unknown, never invented. Older dashboards ignore
    /// unknown JSON fields, so this is backward compatible.</summary>
    [JsonPropertyName("nozzle_diameters")] public Dictionary<string, double> NozzleDiameters { get; init; } = new();
    /// <summary>BRIDGE-READS BUILD: the file tool the bridge
    /// last parsed for a job dispatched to this printer
    /// (e.g. "T0"), from FarmPoller's streaming G-code parse.
    /// "" = no file parsed yet (unknown). Last-parse
    /// semantics: it describes the most recent parsed file
    /// for this printer, not a live printer state.</summary>
    [JsonPropertyName("declared_file_tool")] public string DeclaredFileTool { get; init; } = "";
    /// <summary>BRIDGE-READS BUILD: full declared tool sequence
    /// from that last parse, comma-joined (e.g. "T0,T0").</summary>
    [JsonPropertyName("declared_tool_sequence")] public string DeclaredToolSequence { get; init; } = "";
    /// <summary>BRIDGE-READS BUILD: slicer profile name from
    /// that last parse (filament_settings_id), or "".</summary>
    [JsonPropertyName("file_profile")] public string FileProfile { get; init; } = "";
    /// <summary>BRIDGE-READS BUILD: nozzle size in mm derived
    /// ONLY from the profile label ("<number> nozzle"), or
    /// null when the label names none. Never measured,
    /// never taken from extrusion widths.</summary>
    [JsonPropertyName("file_nozzle_mm")] public double? FileNozzleMm { get; init; }
    /// <summary>FLUIDD SLICE 1: live heater/sensor readings
    /// from the printer's Moonraker objects query (U1 only;
    /// Bambu adapters leave this empty). Empty = unknown,
    /// never invented. Older dashboards ignore unknown
    /// JSON fields, so this is backward compatible.</summary>
    [JsonPropertyName("thermals")] public List<ThermalReading> Thermals { get; init; } = new();
    /// <summary>FLUIDD SLICE 1: files on the printer, newest
    /// first, from Moonraker server/files/list (U1 only).
    /// Null = the bridge has not successfully fetched the
    /// list (dashboard renders "Not available"); an empty
    /// list = fetched, but no files. Never invented.</summary>
    [JsonPropertyName("printer_files")] public List<PrinterFile>? PrinterFiles { get; init; }
    /// <summary>FLUIDD SLICE 1: recent print jobs, newest
    /// first, from Moonraker server/history/list (U1 only).
    /// Null = not fetched (dashboard renders "Not
    /// available"); empty = fetched, but no jobs.</summary>
    [JsonPropertyName("job_history")] public List<JobHistoryEntry>? JobHistory { get; init; }
}

/// <summary>One heater or temperature sensor reading, as
/// reported by Moonraker. Target is null for sensors
/// (which have no target) and for any reading Moonraker
/// reported without one.</summary>
public sealed record ThermalReading
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("actual")] public double? Actual { get; init; }
    [JsonPropertyName("target")] public double? Target { get; init; }
}

/// <summary>One file on the printer, from Moonraker
/// server/files/list. Modified is unix seconds.</summary>
public sealed record PrinterFile
{
    [JsonPropertyName("filename")] public string Filename { get; init; } = "";
    [JsonPropertyName("size")] public long? Size { get; init; }
    [JsonPropertyName("modified")] public long? Modified { get; init; }
}

/// <summary>One finished print job, from Moonraker
/// server/history/list. Duration is seconds; started is
/// unix seconds. Status is Moonraker's own word
/// (completed / cancelled / error), verbatim.</summary>
public sealed record JobHistoryEntry
{
    [JsonPropertyName("filename")] public string Filename { get; init; } = "";
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("duration")] public long? Duration { get; init; }
    [JsonPropertyName("started")] public long? Started { get; init; }
}

/// <summary>What the printer reports as loaded, if anything.
/// Known=false means the printer did not report usable AMS data, so
/// the dashboard must treat material/color as unverifiable.
/// AUTO-START BUILD: Slots carries every populated tray the
/// printer reported (Bambu AMS), so the dashboard can show
/// per-slot material/color even when no single loaded
/// filament can be picked (several trays populated).</summary>
public sealed record AmsState
{
    [JsonPropertyName("known")] public bool Known { get; init; }
    [JsonPropertyName("material")] public string Material { get; init; } = "";
    [JsonPropertyName("color")] public string Color { get; init; } = "";
    [JsonPropertyName("detail")] public string Detail { get; init; } = "";
    [JsonPropertyName("slots")] public List<AmsSlot> Slots { get; init; } = new();
}

/// <summary>One populated AMS tray, as reported by the
/// printer. Slot is the printer's own tray id.</summary>
public sealed record AmsSlot
{
    [JsonPropertyName("slot")] public string Slot { get; init; } = "";
    [JsonPropertyName("material")] public string Material { get; init; } = "";
    [JsonPropertyName("color")] public string Color { get; init; } = "";
}

/// <summary>One bed-view snapshot, captured by the bridge on the
/// LAN and pushed to the dashboard. The dashboard stores only
/// the latest snapshot per printer and NEVER auto-passes a bed
/// check from it — a human must confirm from the image.
/// snapshot_source is "owner_snapshot_url" for an owner-set
/// URL, or a distinct "auto_discovered_*" value for a URL the
/// bridge auto-discovered (2275) from the printer's own
/// authenticated local endpoints and validated as a real
/// JPEG. A printer with no snapshot URL set and nothing
/// discovered sends no snapshot at all — never a fake image.</summary>
public sealed record CameraSnapshot
{
    [JsonPropertyName("printer_name")] public string PrinterName { get; init; } = "";
    [JsonPropertyName("printer_ip")] public string PrinterIp { get; init; } = "";
    [JsonPropertyName("printer_type")] public string PrinterType { get; init; } = "";
    [JsonPropertyName("captured_unix")] public long CapturedUnix { get; init; }
    [JsonPropertyName("snapshot_base64")] public string SnapshotBase64 { get; init; } = "";
    [JsonPropertyName("snapshot_source")] public string SnapshotSource { get; init; } = "owner_snapshot_url";
    /// <summary>Feed type for Bed View (2276): "none" when
    /// no feed is known, "mjpeg" / "webrtc" / "hls" / "http"
    /// for HTTP feeds a browser can embed, "rtsp" or "other"
    /// for feeds a browser cannot show directly. This label
    /// is sanitized — it is never a URL and never carries
    /// credentials. A snapshot never auto-passes a bed check,
    /// and a feed label never does either.</summary>
    [JsonPropertyName("feed_type")] public string FeedType { get; init; } = "none";
    /// <summary>LAN feed URL for Bed View (2276), HTTP(S)
    /// only, with any userinfo/credentials stripped. Null
    /// for RTSP/other feeds and when no feed is known, so
    /// the dashboard never receives a non-embeddable URL
    /// and never receives a credential.</summary>
    [JsonPropertyName("feed_url")] public string? FeedUrl { get; init; }
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
