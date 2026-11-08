// C3D Print Bridge — Snapmaker U1 adapter (Moonraker HTTP API).
//
// The U1 under Paxx firmware runs Klipper + a modified Moonraker,
// so this adapter speaks Moonraker's HTTP API on port 7125
// (configurable per printer). Endpoints used:
//   GET  /printer/objects/query?print_stats&virtual_sdcard&display_status
//        → print_stats.state (standby/printing/paused/complete/
//          cancelled/error), virtual_sdcard.progress, filename.
//   POST /server/files/upload (multipart, print=false)
//        → uploads a file to the printer's storage.
//   POST /printer/print/start {"filename": "<name>"}
//        → starts the uploaded file.
//   POST /printer/print/cancel → stops a print (not used by the
//        v1 worker; present for future dashboard cancel support).
//   POST /machine/reboot → reboots the printer's host (remote
//        reboot, FarmPoller idle/error gate only; unproven on
//        the live U1 — a non-2xx answer is reported as a
//        failure, never claimed as a reboot).
//   GET  /access/api_key → returns the Moonraker API key as JSON
//        when the caller is a trusted client (no login). On a U1
//        whose Fluidd/Paxx UI opens without a password, the bridge
//        on the same LAN can therefore pull the key on Scan with
//        no user typing. If Moonraker answers 401, the caller is
//        not trusted and the key must come from a Paxx login or
//        manual entry instead — never guessed.
//
// HONEST LIMITS:
// - Filament material/color: Moonraker does not expose the U1's
//   RFID filament data through any endpoint verified for this
//   build, so AMS is always reported as unknown for the U1. The
//   dashboard then treats material/color as unverifiable — the
//   same as its no-bridge behavior — rather than the bridge
//   inventing a filament. If Paxx later documents a filament
//   endpoint, it plugs in here and nowhere else.
// - Camera: Paxx streams video (RTSP / webcam pages), not stills.
//   Decoding video would need heavy dependencies this thin relay
//   avoids. Snapshots come from an owner-set snapshot_url, or
//   (2275) from auto-discovery: the printer's own Moonraker
//   webcam list (GET /server/webcams/list) and, only if that
//   is empty/unavailable, the common Fluidd/Moonraker still
//   paths /webcam/?action=snapshot and /webcam/snapshot —
//   authenticated with the printer's existing key/login,
//   validated as a real JPEG, and saved to the LOCAL config
//   only. (2276) The same webcam-list fetch also captures
//   the first webcam's stream_url and service: an HTTP
//   MJPEG/WebRTC/HLS feed is reported to the dashboard as a
//   feed type plus a credential-stripped LAN feed URL, while
//   an RTSP/other feed is reported only as its type label
//   and snapshots remain the fallback. The bridge never
//   fetches a stream to validate it (a stream never ends),
//   never decodes video, and never fakes a feed. Without an
//   owner-set or discovered URL, no snapshot is reported —
//   never a fake image.
// - Any unexpected response (unknown JSON shape, non-2xx) is
//   reported as offline/error with the reason, never papered over.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace C3DBridge.Printers;

public sealed class SnapmakerAdapter : IPrinterAdapter
{
    private readonly PrinterConfig _cfg;
    private readonly int _port;
    private readonly HttpClient _http;
    private string? _moonrakerToken;
    // Camera auto-discovery (2275): at most one attempt per
    // adapter instance. A discovered URL is kept here so this
    // run can snapshot immediately, and the Worker persists it
    // to the local config (with SnapshotAutoDiscovered=true).
    private string? _discoveredSnapshotUrl;
    private string? _discoveredStreamUrl; // HTTP(S) only; RTSP/other feeds keep a null URL here
    // 0.9.6: a Moonraker webcam snapshot URL that has
    // actually returned a valid JPEG in this run. A verified
    // Klipper/Moonraker endpoint is preferred over an
    // owner-set snapshot_url; the owner URL is the fallback
    // when no endpoint has verified. Nothing here claims an
    // endpoint is verified on Marlena's U1 until a live probe
    // from the shop PC via Action1 confirms it.
    private string _discoveredFeedType = "none";
    private bool _cameraDiscoveryAttempted;
    public string SnapshotSource => _discoveredSnapshotUrl is not null || _cfg.SnapshotAutoDiscovered
        ? "auto_discovered_moonraker" : "owner_snapshot_url";
    /// <summary>Feed type (2276). Owner-set stream URL wins
    /// over a discovered one; a discovered RTSP/other feed
    /// still reports its type even though its URL is never
    /// sent to the dashboard.</summary>
    public string FeedType => !string.IsNullOrWhiteSpace(_cfg.SnapmakerStreamUrl)
        ? ClassifyFeedType(_cfg.SnapmakerStreamUrl, null)
        : !string.IsNullOrWhiteSpace(_cfg.StreamUrl)
        ? ClassifyFeedType(_cfg.StreamUrl, null) : _discoveredFeedType;
    /// <summary>Dashboard-facing feed URL (2276): HTTP(S)
    /// only, credential-stripped. Null for RTSP/other
    /// feeds and when no feed is known.</summary>
    public string? FeedUrl
    {
        get
        {
            var raw = !string.IsNullOrWhiteSpace(_cfg.SnapmakerStreamUrl) ? _cfg.SnapmakerStreamUrl : !string.IsNullOrWhiteSpace(_cfg.StreamUrl) ? _cfg.StreamUrl : _discoveredStreamUrl;
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return null;
            return StripCredentials(raw);
        }
    }
    public string Name => _cfg.Name;
    public string Type => _cfg.Type;
    public string Ip => _cfg.Ip;
    // 2026.1030.1511-farm-dedupe: Moonraker exposes no factory
    // serial, so the learned Moonraker hostname is this printer's
    // stable identity key; the configured serial is the fallback.
    public string SerialKey => !string.IsNullOrWhiteSpace(_cfg.MoonrakerHostname) ? _cfg.MoonrakerHostname : (_cfg.Serial ?? "");

    public SnapmakerAdapter(PrinterConfig cfg)
    {
        _cfg = cfg;
        _port = cfg.Port ?? 7125;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    private string Base => $"http://{_cfg.Ip}:{_port}";

    /// <summary>Try to pull a Moonraker API key from a printer that
    /// answers /access/api_key without a login (trusted LAN client).
    /// Returns null on any failure, including 401 — the caller must
    /// fall back to Paxx login or manual entry, never fabricate a key.</summary>
    public static async Task<string?> TryFetchApiKeyAsync(string ip, int port = 7125)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var res = await http.GetAsync($"http://{ip}:{port}/access/api_key");
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (root.TryGetProperty("result", out var r))
            {
                if (r.ValueKind == JsonValueKind.String) return string.IsNullOrWhiteSpace(r.GetString()) ? null : r.GetString();
                if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("api_key", out var k) && k.ValueKind == JsonValueKind.String)
                    return string.IsNullOrWhiteSpace(k.GetString()) ? null : k.GetString();
            }
            if (root.TryGetProperty("api_key", out var k2) && k2.ValueKind == JsonValueKind.String)
                return string.IsNullOrWhiteSpace(k2.GetString()) ? null : k2.GetString();
            return null;
        }
        catch { return null; }
    }

    private async Task<bool> AddAuthAsync(HttpRequestMessage req, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_cfg.PaxxUser) && !string.IsNullOrWhiteSpace(_cfg.PaxxPassword))
        {
            if (_moonrakerToken is null)
            {
                try
                {
                    var body = JsonSerializer.Serialize(new { username = _cfg.PaxxUser, password = _cfg.PaxxPassword });
                    using var login = new HttpRequestMessage(HttpMethod.Post, Base + "/access/login") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                    using var res = await _http.SendAsync(login, ct);
                    if (!res.IsSuccessStatusCode) return false;
                    using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                    string? token = null;
                    if (doc.RootElement.TryGetProperty("result", out var r) && r.TryGetProperty("token", out var tk)) token = tk.GetString();
                    else if (doc.RootElement.TryGetProperty("token", out var tk2)) token = tk2.GetString();
                    if (string.IsNullOrWhiteSpace(token)) return false;
                    _moonrakerToken = token;
                }
                catch { return false; }
            }
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _moonrakerToken);
            return true;
        }
        if (!string.IsNullOrWhiteSpace(_cfg.AccessCode))
            req.Headers.Add("X-Api-Key", _cfg.AccessCode);
        return true;
    }

    public async Task<PrinterStatus> GetStatusAsync(CancellationToken ct)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get,
                Base + "/printer/objects/query?print_stats&virtual_sdcard&display_status");
            if (!await AddAuthAsync(req, ct))
                return Offline($"Paxx login failed for '{Name}': check the Paxx username/password in Configure.");
            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
                return Offline($"Moonraker on '{Name}' answered {(int)res.StatusCode}; check the IP and port.");
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("status", out var status))
                return Offline($"Moonraker on '{Name}' returned a status reply without a status object.");

            var ps = status.TryGetProperty("print_stats", out var p) ? p : (JsonElement?)null;
            var stateRaw = ps?.TryGetProperty("state", out var s) == true ? s.GetString() ?? "" : "";
            var state = stateRaw switch
            {
                "printing" => "printing",
                "paused" => "paused",
                "standby" or "complete" or "cancelled" => "idle",
                "error" => "error",
                _ => "error",
            };
            // AUTO-START BUILD: when print_stats.state is
            // "error", print_stats.message carries the related
            // error message (Moonraker docs: "Basic Print
            // Status"). It is reported verbatim — never
            // mapped to a meaning the bridge does not know.
            var psMessage = ps?.TryGetProperty("message", out var m) == true ? m.GetString() ?? "" : "";
            var detail = stateRaw is "" or "printing" or "paused" or "standby" or "complete" or "cancelled" or "error"
                ? (stateRaw == "error" && !string.IsNullOrWhiteSpace(psMessage) ? psMessage : "")
                : $"Unrecognized Moonraker print state '{stateRaw}'.";

            double? percent = null;
            if (status.TryGetProperty("virtual_sdcard", out var vs) &&
                vs.TryGetProperty("progress", out var prog) && prog.ValueKind == JsonValueKind.Number)
                percent = prog.GetDouble() * 100.0;
            else if (status.TryGetProperty("display_status", out var ds) &&
                     ds.TryGetProperty("progress", out var dp) && dp.ValueKind == JsonValueKind.Number)
                percent = dp.GetDouble() * 100.0;

            return new PrinterStatus
            {
                Name = Name,
                Type = Type,
                Ip = Ip,
                Serial = SerialKey,
                State = state,
                // AUTO-START BUILD: the raw print_stats.state
                // word survives beside the mapped state, so
                // "complete" is visible as a finish instead of
                // flat "idle".
                NativeState = stateRaw,
                ProgressPercent = percent,
                Detail = detail,
                Ams = new AmsState
                {
                    Known = false,
                    Detail = "U1 filament data is not exposed by a verified Moonraker endpoint; AMS is unverifiable.",
                },
                CameraAvailable = _cfg.Camera, // camera_available means the printer HAS a camera, not that a still URL is configured; snapshot fetching still needs SnapshotUrl separately
            };
        }
        catch (Exception ex)
        {
            return Offline($"Could not reach Moonraker on '{Name}' at {Base}: {ex.Message}");
        }
    }

    // BRIDGE-READS BUILD — per-toolhead nozzle query. UNCONFIRMED on
    // the Paxx-modified U1 until a live probe proves it: the query
    // is GET /printer/objects/query?configfile, and diameters are
    // read from result.status.configfile.config.<section>.nozzle_diameter
    // (falling back to result.status.configfile.<section>.nozzle_diameter
    // for Moonraker shape variants). Klipper convention: "extruder"
    // is toolhead 0, "extruder1" toolhead 1, and so on. Any failure,
    // missing section, or non-numeric value yields an EMPTY map
    // (unknown) — never an invented diameter. Results are cached
    // 10 minutes: nozzle hardware changes are rare and manual.
    // FILAMENT PROBE (same response): configfile section names are
    // scanned for filament-related names. Moonraker exposes no
    // verified U1 RFID filament endpoint, so the expected result is
    // nothing found and AMS stays unknown. If a filament section
    // ever appears it is logged once for a human to verify — it is
    // NOT treated as filament state here.
    private Dictionary<string, double> _nozzleCache = new();
    private DateTime _nozzleCacheAt = DateTime.MinValue;
    private bool _filamentProbeLogged;

    public async Task<Dictionary<string, double>> QueryNozzleDiametersAsync(CancellationToken ct)
    {
        if (_nozzleCache.Count > 0 && DateTime.UtcNow - _nozzleCacheAt < TimeSpan.FromMinutes(10))
            return new Dictionary<string, double>(_nozzleCache);
        var found = new Dictionary<string, double>();
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get,
                Base + "/printer/objects/query?configfile");
            if (!await AddAuthAsync(req, ct))
                return found;
            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
                return found;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("status", out var status) ||
                !status.TryGetProperty("configfile", out var configfile))
                return found;
            JsonElement sections = configfile;
            if (configfile.TryGetProperty("config", out var cfg) && cfg.ValueKind == JsonValueKind.Object)
                sections = cfg;
            var sectionIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["extruder"] = "0",
                ["extruder1"] = "1",
                ["extruder2"] = "2",
                ["extruder3"] = "3",
            };
            foreach (var section in sections.EnumerateObject())
            {
                if (!_filamentProbeLogged && section.Name.Contains("filament", StringComparison.OrdinalIgnoreCase))
                {
                    _filamentProbeLogged = true;
                    Program.Log($"[nozzle] Configfile on '{Name}' has a filament-related section '{section.Name}': not treated as filament state — needs a human to verify what it means before any AMS use.");
                }
                if (!sectionIndex.TryGetValue(section.Name, out var toolIndex))
                    continue;
                if (section.Value.ValueKind != JsonValueKind.Object)
                    continue;
                if (section.Value.TryGetProperty("nozzle_diameter", out var dia) &&
                    dia.ValueKind == JsonValueKind.Number &&
                    dia.TryGetDouble(out var mm) && mm > 0)
                    found[toolIndex] = mm;
            }
        }
        catch
        {
            return found;
        }
        if (found.Count > 0)
        {
            _nozzleCache = new Dictionary<string, double>(found);
            _nozzleCacheAt = DateTime.UtcNow;
        }
        return found;
    }

    // FLUIDD SLICE 1 — Moonraker thermals / files / history.
    // Object names are DISCOVERED, not assumed: objects/list
    // is queried first and only temperature-bearing objects
    // actually present are queried (extruder*, heater_bed,
    // temperature_sensor*). The screenshot's "Cavity" sensor
    // has no assumed object name — if discovery does not
    // find it, it is simply absent (unknown), never invented.
    // Display names are derived from the object name
    // (heater_bed -> "Heater Bed", temperature_sensor X -> X,
    // extruder -> "Extruder", extruder1 -> "Extruder1").
    // Any failure yields an empty list (thermals) — never a
    // guessed temperature. Files/history are 5-minute cached
    // and keep their last good values on a transient failure;
    // null means never successfully fetched (dashboard
    // renders "Not available").
    private List<PrinterFile>? _filesCache;
    private DateTime _filesCacheAt = DateTime.MinValue;
    private List<JobHistoryEntry>? _historyCache;
    private DateTime _historyCacheAt = DateTime.MinValue;

    private static string ThermalDisplayName(string obj)
    {
        if (obj == "heater_bed") return "Heater Bed";
        if (obj.StartsWith("temperature_sensor ", StringComparison.Ordinal))
            return obj["temperature_sensor ".Length..];
        if (obj.Length > 0)
            return char.ToUpperInvariant(obj[0]) + obj[1..];
        return obj;
    }

    public async Task<List<ThermalReading>> QueryThermalsAsync(CancellationToken ct)
    {
        var found = new List<ThermalReading>();
        try
        {
            var listReq = new HttpRequestMessage(HttpMethod.Get, Base + "/printer/objects/list");
            if (!await AddAuthAsync(listReq, ct))
                return found;
            using var listRes = await _http.SendAsync(listReq, ct);
            if (!listRes.IsSuccessStatusCode)
                return found;
            using var listDoc = JsonDocument.Parse(await listRes.Content.ReadAsStringAsync(ct));
            if (!listDoc.RootElement.TryGetProperty("result", out var listResult) ||
                !listResult.TryGetProperty("objects", out var objects) ||
                objects.ValueKind != JsonValueKind.Array)
                return found;
            var wanted = new List<string>();
            foreach (var o in objects.EnumerateArray())
            {
                var name = o.GetString() ?? "";
                if (name == "heater_bed" || name.StartsWith("extruder", StringComparison.Ordinal) ||
                    name.StartsWith("temperature_sensor", StringComparison.Ordinal))
                    wanted.Add(name);
            }
            if (wanted.Count == 0)
                return found;
            var query = string.Join("&", wanted.Select(w => Uri.EscapeDataString(w)));
            var req = new HttpRequestMessage(HttpMethod.Get, Base + "/printer/objects/query?" + query);
            if (!await AddAuthAsync(req, ct))
                return found;
            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
                return found;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("status", out var status))
                return found;
            foreach (var obj in status.EnumerateObject())
            {
                if (obj.Value.ValueKind != JsonValueKind.Object)
                    continue;
                double? actual = null, target = null;
                if (obj.Value.TryGetProperty("temperature", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetDouble(out var td))
                    actual = td;
                if (obj.Value.TryGetProperty("target", out var tg) && tg.ValueKind == JsonValueKind.Number && tg.TryGetDouble(out var tgd))
                    target = tgd;
                found.Add(new ThermalReading { Name = ThermalDisplayName(obj.Name), Actual = actual, Target = target });
            }
        }
        catch
        {
            return found;
        }
        return found;
    }

    public async Task<List<PrinterFile>?> QueryPrinterFilesAsync(CancellationToken ct)
    {
        if (_filesCache is not null && DateTime.UtcNow - _filesCacheAt < TimeSpan.FromMinutes(5))
            return _filesCache;
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, Base + "/server/files/list?root=gcodes");
            if (!await AddAuthAsync(req, ct))
                return _filesCache;
            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
                return _filesCache;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("result", out var result) ||
                result.ValueKind != JsonValueKind.Array)
                return _filesCache;
            var files = new List<PrinterFile>();
            foreach (var f in result.EnumerateArray())
            {
                string name = "";
                if (f.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String)
                    name = path.GetString() ?? "";
                else if (f.TryGetProperty("filename", out var fn) && fn.ValueKind == JsonValueKind.String)
                    name = fn.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                long? size = null, modified = null;
                if (f.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number && sz.TryGetInt64(out var sv))
                    size = sv;
                if (f.TryGetProperty("modified", out var mod) && mod.ValueKind == JsonValueKind.Number && mod.TryGetDouble(out var mv))
                    modified = (long)mv;
                files.Add(new PrinterFile { Filename = name, Size = size, Modified = modified });
            }
            files.Sort((a, b) => (b.Modified ?? 0).CompareTo(a.Modified ?? 0));
            if (files.Count > 50)
                files = files.GetRange(0, 50);
            _filesCache = files;
            _filesCacheAt = DateTime.UtcNow;
            return _filesCache;
        }
        catch
        {
            return _filesCache;
        }
    }

    public async Task<List<JobHistoryEntry>?> QueryJobHistoryAsync(CancellationToken ct)
    {
        if (_historyCache is not null && DateTime.UtcNow - _historyCacheAt < TimeSpan.FromMinutes(5))
            return _historyCache;
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, Base + "/server/history/list?limit=20");
            if (!await AddAuthAsync(req, ct))
                return _historyCache;
            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
                return _historyCache;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("jobs", out var jobs) ||
                jobs.ValueKind != JsonValueKind.Array)
                return _historyCache;
            var entries = new List<JobHistoryEntry>();
            foreach (var j in jobs.EnumerateArray())
            {
                string name = "";
                if (j.TryGetProperty("filename", out var fn) && fn.ValueKind == JsonValueKind.String)
                    name = fn.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                string status = "";
                if (j.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String)
                    status = st.GetString() ?? "";
                long? duration = null, started = null;
                if (j.TryGetProperty("print_duration", out var pd) && pd.ValueKind == JsonValueKind.Number && pd.TryGetDouble(out var pdv))
                    duration = (long)pdv;
                if (j.TryGetProperty("start_time", out var stt) && stt.ValueKind == JsonValueKind.Number && stt.TryGetDouble(out var stv))
                    started = (long)stv;
                entries.Add(new JobHistoryEntry { Filename = name, Status = status, Duration = duration, Started = started });
            }
            if (entries.Count > 20)
                entries = entries.GetRange(0, 20);
            _historyCache = entries;
            _historyCacheAt = DateTime.UtcNow;
            return _historyCache;
        }
        catch
        {
            return _historyCache;
        }
    }

    public async Task<(bool Ok, string Reason)> UploadFileAsync(string localPath, string fileName, CancellationToken ct)
    {
        // No-underscore rule (Nov 7, 2026): printer file names must
        // not contain "_". Sanitize the name the printer receives;
        // the local file bytes are unchanged.
        var uploadFileName = BambuAdapter.SanitizePrinterFileName(fileName);
        if (!string.Equals(uploadFileName, fileName, StringComparison.Ordinal))
            Program.Log($"[Snapmaker] Sanitized printer filename '{fileName}' -> '{uploadFileName}' for upload");
        try
        {
            using var form = new MultipartFormDataContent();
            var bytes = await File.ReadAllBytesAsync(localPath, ct);
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(content, "file", uploadFileName);
            form.Add(new StringContent("false"), "print");

            var req = new HttpRequestMessage(HttpMethod.Post, Base + "/server/files/upload") { Content = form };
            if (!await AddAuthAsync(req, ct))
                return (false, $"Paxx login failed for '{Name}': check the Paxx username/password in Configure.");
            // Upload timeout scales with file size (Oct 30, 2026:
            // the shared 15s client timed out uploading the 41MB
            // collar G-code, failing the first live print). Floor
            // 180s, +5s per MB, cap 600s; status polls keep 15s.
            var uploadSeconds = Math.Min(600.0, Math.Max(180.0, 60.0 + bytes.Length / 1024.0 / 1024.0 * 5.0));
            using var uploadHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(uploadSeconds) };
            using var res = await uploadHttp.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
                return (false, $"Moonraker upload to '{Name}' answered {(int)res.StatusCode}: {Trim(await res.Content.ReadAsStringAsync(ct))}");
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, $"Upload to '{Name}' failed: {ex.Message}");
        }
    }

    public async Task<(bool Ok, string Reason)> StartPrintAsync(string fileName, string jobKey, CancellationToken ct, string? localPath = null)
    {
        // No-underscore rule (Nov 7, 2026): same sanitization as
        // the upload, so the start command names exactly the file
        // that was uploaded.
        var startFileName = BambuAdapter.SanitizePrinterFileName(fileName);
        if (!string.Equals(startFileName, fileName, StringComparison.Ordinal))
            Program.Log($"[Snapmaker] Sanitized printer filename '{fileName}' -> '{startFileName}' for start");
        try
        {
            var body = JsonSerializer.Serialize(new { filename = startFileName });
            var req = new HttpRequestMessage(HttpMethod.Post, Base + "/printer/print/start")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (!await AddAuthAsync(req, ct))
                return (false, $"Paxx login failed for '{Name}': check the Paxx username/password in Configure.");
            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
                return (false, $"Moonraker start on '{Name}' answered {(int)res.StatusCode}: {Trim(await res.Content.ReadAsStringAsync(ct))}");
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, $"Start command to '{Name}' failed: {ex.Message}");
        }
    }

    /// <summary>Farm build: chamber/cavity light control, via
    /// Moonraker gcode SET_LED on the verified U1 light object
    /// "led cavity_led" (confirmed on the live U1, Oct 23,
    /// 2026: color_data white channel at 1.0 when on). This
    /// is a light switch only — it never passes a bed check.
    /// A dark snapshot still never auto-passes; turning the
    /// light on just makes the next snapshot actually visible
    /// so a human can confirm the bed from it. Returns
    /// (ok, reason); a failure never claims the light changed.</summary>
    public async Task<(bool Ok, string Reason)> SetCavityLedAsync(bool on, CancellationToken ct)
    {
        try
        {
            var script = on
                ? "SET_LED LED=cavity_led WHITE=1"
                : "SET_LED LED=cavity_led WHITE=0";
            var body = JsonSerializer.Serialize(new { script });
            var req = new HttpRequestMessage(HttpMethod.Post, Base + "/printer/gcode/script")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (!await AddAuthAsync(req, ct))
                return (false, $"Paxx login failed for '{Name}': check the Paxx username/password in Configure.");
            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
                return (false, $"Moonraker light command on '{Name}' answered {(int)res.StatusCode}: {Trim(await res.Content.ReadAsStringAsync(ct))}");
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, $"Light command to '{Name}' failed: {ex.Message}");
        }
    }

    /// <summary>AUTO-START BUILD — pause / cancel / home,
    /// all via documented Moonraker endpoints (Moonraker
    /// docs: POST /printer/print/pause, /printer/print/
    /// cancel; home via the same /printer/gcode/script
    /// endpoint the verified SET_LED call already uses,
    /// with script "G28"). Home is executed ONLY behind
    /// the bridge's home_power_allowed gate in FarmPoller
    /// (owner A7 decision) — calling HomeAsync directly
    /// bypasses that gate, so only FarmPoller may call it.
    /// A failure never claims the printer changed state.</summary>
    public async Task<(bool Ok, string Reason)> PausePrintAsync(CancellationToken ct)
        => await PostMoonrakerAsync("/printer/print/pause", null, ct);

    public async Task<(bool Ok, string Reason)> CancelPrintAsync(CancellationToken ct)
        => await PostMoonrakerAsync("/printer/print/cancel", null, ct);

    public async Task<(bool Ok, string Reason)> HomeAsync(CancellationToken ct)
        => await PostMoonrakerAsync("/printer/gcode/script",
            JsonSerializer.Serialize(new { script = "G28" }), ct);

    /// <summary>Reboot the printer's host via Moonraker
    /// POST /machine/reboot (Moonraker docs: reboots the
    /// machine running Moonraker). This is the Snapmaker
    /// equivalent of the Bambu MQTT system reboot: the
    /// whole host restarts, so an errored U1 clears without
    /// walking to it. UNPROVEN on the live U1 — the U1 runs
    /// a Paxx-modified Moonraker, so the endpoint may be
    /// absent or behave differently; a non-2xx answer is
    /// reported as a failure, never claimed as a reboot.
    /// The host takes ~60-90 s to come back; Moonraker is
    /// unreachable until then. Executed ONLY behind
    /// FarmPoller's live-state gate (idle/error only) —
    /// calling this method directly bypasses that gate, so
    /// only FarmPoller may call it.</summary>
    public async Task<(bool Ok, string Reason)> RebootAsync(CancellationToken ct)
        => await PostMoonrakerAsync("/machine/reboot", null, ct);

    private async Task<(bool Ok, string Reason)> PostMoonrakerAsync(string path, string? jsonBody, CancellationToken ct)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Post, Base + path);
            if (jsonBody is not null)
                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            if (!await AddAuthAsync(req, ct))
                return (false, $"Paxx login failed for '{Name}': check the Paxx username/password in Configure.");
            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
                return (false, $"Moonraker {path} on '{Name}' answered {(int)res.StatusCode}: {Trim(await res.Content.ReadAsStringAsync(ct))}");
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, $"Moonraker {path} on '{Name}' failed: {ex.Message}");
        }
    }

    /// <summary>AUTO-START BUILD — identify a Moonraker
    /// candidate by hostname (GET /printer/info, Moonraker
    /// docs: result.hostname). Tries unauthenticated first
    /// (the U1 answered /printer/info that way on Oct 29,
    /// 2026), then with the known printer's Paxx login or
    /// API key. Returns the hostname, or null when the
    /// candidate is not a Moonraker host or cannot be
    /// identified. Used ONLY by IP-change auto-detection:
    /// a hostname match against the learned hostname is
    /// what proves a candidate IS the moved printer.</summary>
    public static async Task<string?> TryGetMoonrakerHostnameAsync(
        string ip, string? paxxUser, string? paxxPassword, string? accessCode, CancellationToken ct)
    {
        var url = $"http://{ip}:7125/printer/info";
        string? TryHostname(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                JsonElement result = default;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("result", out var r))
                    result = r;
                else
                    result = root;
                if (result.ValueKind == JsonValueKind.Object &&
                    result.TryGetProperty("hostname", out var h) && h.ValueKind == JsonValueKind.String)
                {
                    var v = h.GetString();
                    return string.IsNullOrWhiteSpace(v) ? null : v;
                }
                return null;
            }
            catch { return null; }
        }
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var res = await http.GetAsync(url, ct);
            if (res.IsSuccessStatusCode)
            {
                var host = TryHostname(await res.Content.ReadAsStringAsync(ct));
                if (host is not null) return host;
            }
            // Authenticated retry with the known printer's
            // credentials (Paxx login first, then API key).
            if (!string.IsNullOrWhiteSpace(paxxUser) && !string.IsNullOrWhiteSpace(paxxPassword))
            {
                try
                {
                    var loginBody = JsonSerializer.Serialize(new { username = paxxUser, password = paxxPassword });
                    using var login = new HttpRequestMessage(HttpMethod.Post, $"http://{ip}:7125/access/login")
                    {
                        Content = new StringContent(loginBody, Encoding.UTF8, "application/json"),
                    };
                    using var loginRes = await http.SendAsync(login, ct);
                    if (loginRes.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(await loginRes.Content.ReadAsStringAsync(ct));
                        string? token = null;
                        if (doc.RootElement.TryGetProperty("result", out var r) && r.TryGetProperty("token", out var tk))
                            token = tk.GetString();
                        else if (doc.RootElement.TryGetProperty("token", out var tk2))
                            token = tk2.GetString();
                        if (!string.IsNullOrWhiteSpace(token))
                        {
                            using var authed = new HttpRequestMessage(HttpMethod.Get, url);
                            authed.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                            using var authedRes = await http.SendAsync(authed, ct);
                            if (authedRes.IsSuccessStatusCode)
                                return TryHostname(await authedRes.Content.ReadAsStringAsync(ct));
                        }
                    }
                }
                catch { /* fall through to API-key attempt */ }
            }
            if (!string.IsNullOrWhiteSpace(accessCode))
            {
                try
                {
                    using var keyed = new HttpRequestMessage(HttpMethod.Get, url);
                    keyed.Headers.Add("X-Api-Key", accessCode);
                    using var keyedRes = await http.SendAsync(keyed, ct);
                    if (keyedRes.IsSuccessStatusCode)
                        return TryHostname(await keyedRes.Content.ReadAsStringAsync(ct));
                }
                catch { /* no identification */ }
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>Instance wrapper: this printer's own
    /// Moonraker hostname, for learning (see
    /// IpReconciler). Null when the printer cannot be
    /// reached or does not answer /printer/info.</summary>
    public Task<string?> TryGetHostnameAsync(CancellationToken ct)
        => TryGetMoonrakerHostnameAsync(_cfg.Ip, _cfg.PaxxUser, _cfg.PaxxPassword, _cfg.AccessCode, ct);

    /// <summary>0.9.6 Snapmaker camera URL preference, in
    /// order: (1) a Moonraker webcam snapshot URL verified
    /// in this run (see TryGetSnapshotAsync, which records
    /// it only after a real JPEG returns); (2) the
    /// Snapmaker-specific snapmaker_snapshot_url, when set;
    /// (3) the generic snapshot_url; (4) a discovered URL.
    /// Paxx IS installed on Marlena's U1, so the Klipper/
    /// Moonraker path is available, but no endpoint is
    /// treated as verified until it actually answers.</summary>
    private string? EffectiveSnapshotUrl =>
        !string.IsNullOrWhiteSpace(_verifiedMoonrakerSnapshotUrl) ? _verifiedMoonrakerSnapshotUrl
        : !string.IsNullOrWhiteSpace(_cfg.SnapmakerSnapshotUrl) ? _cfg.SnapmakerSnapshotUrl
        : !string.IsNullOrWhiteSpace(_cfg.SnapshotUrl) ? _cfg.SnapshotUrl
        : _discoveredSnapshotUrl;

    private string? _verifiedMoonrakerSnapshotUrl;

    public async Task<byte[]?> TryGetSnapshotAsync(CancellationToken ct)
    {
        // Try the verified Moonraker endpoint first, then
        // each fallback in EffectiveSnapshotUrl order, then
        // the common Moonraker still paths as a last resort.
        // A candidate counts as verified only when it
        // returns a real JPEG (FetchJpegAsync enforces the
        // 100B-2MB JPEG check). Idle-only and once-a-minute
        // throttling live in the Worker, not here.
        var candidates = new List<string?>();
        if (!string.IsNullOrWhiteSpace(EffectiveSnapshotUrl)) candidates.Add(EffectiveSnapshotUrl);
        if (!string.IsNullOrWhiteSpace(_cfg.SnapmakerSnapshotUrl) && !candidates.Contains(_cfg.SnapmakerSnapshotUrl)) candidates.Add(_cfg.SnapmakerSnapshotUrl);
        if (!string.IsNullOrWhiteSpace(_cfg.SnapshotUrl) && !candidates.Contains(_cfg.SnapshotUrl)) candidates.Add(_cfg.SnapshotUrl);
        if (!string.IsNullOrWhiteSpace(_discoveredSnapshotUrl) && !candidates.Contains(_discoveredSnapshotUrl)) candidates.Add(_discoveredSnapshotUrl);
        foreach (var url in candidates)
        {
            var bytes = await FetchJpegAsync(url!, ct);
            if (bytes is not null)
            {
                // A Moonraker-origin URL that answered is now
                // the verified endpoint for the rest of this run.
                if (url!.StartsWith(Base, StringComparison.OrdinalIgnoreCase))
                    _verifiedMoonrakerSnapshotUrl = url;
                return bytes;
            }
        }
        return null;
    }

    /// <summary>Camera auto-discovery (2275 snapshot,
    /// 2276 feed). Never overwrites an owner-set snapshot
    /// URL (returns null when one is set). Tries, in
    /// order: Moonraker GET /server/webcams/list — first
    /// webcam's snapshot_url, resolved against the printer
    /// base URL — then, only when the list is empty or
    /// unavailable, /webcam/?action=snapshot and
    /// /webcam/snapshot. Every snapshot candidate is fetched
    /// with the printer's existing auth and must validate
    /// as a JPEG (100 bytes – 2 MB, SOI marker) before it
    /// is returned. The same webcam-list fetch (2276)
    /// also captures the first webcam's stream_url and
    /// service for feed discovery — see
    /// TryAutoDiscoverFeedAsync. Each miss is logged once
    /// (this method runs at most once per adapter
    /// instance); no URL is logged.</summary>
    public async Task<string?> TryAutoDiscoverSnapshotUrlAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_cfg.SnapshotUrl)) return null;
        if (!string.IsNullOrWhiteSpace(_discoveredSnapshotUrl)) return _discoveredSnapshotUrl;
        await DiscoverCameraAsync(ct);
        return _discoveredSnapshotUrl;
    }

    /// <summary>Feed discovery (2276). Shares the single
    /// webcam-list fetch with snapshot discovery, so it
    /// runs at most once per adapter instance and never
    /// re-probes on every status tick. Returns the
    /// discovered HTTP feed URL, or null when the printer
    /// already has a feed URL set, when no feed was found,
    /// or when the feed is RTSP/other (FeedType still
    /// reports that type, but no URL is returned for it).
    /// The stream URL is NEVER fetched to validate it —
    /// a live stream has no end, so fetching it would hang
    /// the bridge; it is classified only from its scheme,
    /// its Moonraker service name, and its URL pattern.
    /// Never overwrites an owner-set feed URL; no URL is
    /// logged.</summary>
    public async Task<string?> TryAutoDiscoverFeedAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_cfg.StreamUrl)) return null;
        if (!string.IsNullOrWhiteSpace(_discoveredStreamUrl)) return _discoveredStreamUrl;
        await DiscoverCameraAsync(ct);
        return _discoveredStreamUrl;
    }

    /// <summary>One shared camera discovery pass (2276).
    /// Fetches Moonraker GET /server/webcams/list once,
    /// parses the FIRST webcam's snapshot_url, stream_url
    /// and service fields (Moonraker's own webcam API:
    /// name, location, service, target_fps, stream_url,
    /// snapshot_url, flip/rotation), resolves relative
    /// URLs against the printer base URL, validates only
    /// the snapshot as a real JPEG, and classifies the
    /// feed without fetching it. Only if the list is empty
    /// or unavailable does it fall back to the common
    /// still paths, for the snapshot only — there is no
    /// defined stream fallback. Paxx's stock-camera monitor
    /// session (a local MQTT camera.start_monitor request
    /// in third-party Fluidd bridges) is deliberately NOT
    /// implemented here: it is not a Moonraker HTTP call,
    /// and copying that session logic is out of scope for
    /// this thin relay. If Moonraker reports no webcam,
    /// Bed View keeps showing snapshots only — never a
    /// fake feed.</summary>
    private async Task DiscoverCameraAsync(CancellationToken ct)
    {
        if (_cameraDiscoveryAttempted) return;
        _cameraDiscoveryAttempted = true;

        // 1. Moonraker webcam list.
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, Base + "/server/webcams/list");
            if (!await AddAuthAsync(req, ct))
            {
                Program.Log($"Camera auto-discovery for '{Name}': Moonraker login failed; no camera URL discovered.");
            }
            else
            {
                using var res = await _http.SendAsync(req, ct);
                if (res.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                    var foundAny = false;
                    foreach (var cam in EnumerateWebcams(doc.RootElement))
                    {
                        foundAny = true;
                        // Feed first: classification needs no
                        // network fetch, so it is safe even
                        // when the snapshot check fails.
                        var streamRaw = WebcamStreamField(cam);
                        var service = WebcamServiceField(cam);
                        if (!string.IsNullOrWhiteSpace(streamRaw))
                        {
                            var resolvedStream = ResolveCameraUrl(streamRaw);
                            if (resolvedStream is not null)
                            {
                                _discoveredFeedType = ClassifyFeedType(resolvedStream, service);
                                if (resolvedStream.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                    resolvedStream.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                                    _discoveredStreamUrl = resolvedStream;
                                Program.Log($"Camera auto-discovery for '{Name}': feed discovered from the Moonraker webcam list (type: {_discoveredFeedType}).");
                            }
                        }
                        if (string.IsNullOrWhiteSpace(_cfg.SnapshotUrl))
                        {
                            var raw = WebcamSnapshotField(cam);
                            if (!string.IsNullOrWhiteSpace(raw))
                            {
                                var resolved = ResolveCameraUrl(raw);
                                if (resolved is not null && await FetchJpegAsync(resolved, ct) is not null)
                                {
                                    _discoveredSnapshotUrl = resolved;
                                    Program.Log($"Camera auto-discovery for '{Name}': snapshot URL auto-discovered from the Moonraker webcam list.");
                                }
                            }
                        }
                        break; // first webcam only, as in 2275
                    }
                    if (!foundAny)
                        Program.Log($"Camera auto-discovery for '{Name}': Moonraker webcam list had no webcams.");
                    else if (string.IsNullOrWhiteSpace(_cfg.SnapshotUrl) && _discoveredSnapshotUrl is null)
                        Program.Log($"Camera auto-discovery for '{Name}': Moonraker webcam list had no usable snapshot URL.");
                    return; // list answered: no still-path fallback, even on a miss
                }
                Program.Log($"Camera auto-discovery for '{Name}': Moonraker webcam list unavailable (HTTP {(int)res.StatusCode}).");
            }
        }
        catch (Exception ex)
        {
            Program.Log($"Camera auto-discovery for '{Name}': Moonraker webcam list failed ({ex.Message}).");
        }

        // 2. Common Fluidd/Moonraker still paths, snapshot only, only as fallback.
        if (string.IsNullOrWhiteSpace(_cfg.SnapshotUrl))
        {
            foreach (var path in new[] { "/webcam/?action=snapshot", "/webcam/snapshot" })
            {
                var url = Base + path;
                if (await FetchJpegAsync(url, ct) is not null)
                {
                    _discoveredSnapshotUrl = url;
                    Program.Log($"Camera auto-discovery for '{Name}': snapshot URL auto-discovered from a common Moonraker still path.");
                    return;
                }
                Program.Log($"Camera auto-discovery for '{Name}': fallback still path missed.");
            }
        }

        Program.Log($"Camera auto-discovery for '{Name}': nothing found; no snapshot will be sent until a snapshot URL is set or discovered.");
    }

    /// <summary>Classify a feed from its scheme, its
    /// Moonraker service name, and its URL pattern only —
    /// never by fetching the stream. "mjpeg" covers
    /// mjpegstreamer / uv4l-mjpeg services and mjpg/mjpeg/
    /// action=stream URLs (embeddable as an &lt;img&gt; in a
    /// browser); "webrtc" covers webrtc/go2rtc services and
    /// URLs (needs a WebRTC player, a future dashboard
    /// task); "hls" covers .m3u8 URLs (needs an HLS
    /// player, a future dashboard task); "http" is any
    /// other HTTP(S) URL a dashboard may try to embed;
    /// "rtsp" and "other" are browser-unembeddable and
    /// must fall back to snapshots.</summary>
    internal static string ClassifyFeedType(string url, string? service)
    {
        var u = url.ToLowerInvariant();
        var s = (service ?? "").ToLowerInvariant();
        if (u.StartsWith("rtsp://") || u.StartsWith("rtsps://")) return "rtsp";
        if (s.Contains("webrtc") || s.Contains("go2rtc") || u.Contains("webrtc")) return "webrtc";
        if (u.Contains(".m3u8") || s.Contains("hls")) return "hls";
        if (s.Contains("mjpeg") || s.Contains("uv4l") || u.Contains("mjpg") || u.Contains("mjpeg") ||
            u.Contains("action=stream") || u.Contains("/stream")) return "mjpeg";
        if (u.StartsWith("http://") || u.StartsWith("https://")) return "http";
        return "other";
    }

    /// <summary>Strip any userinfo (username/password)
    /// from a URL before it is ever sent to the dashboard.
    /// Returns the original string when it cannot be
    /// parsed as a URI.</summary>
    internal static string StripCredentials(string url)
    {
        try
        {
            var b = new UriBuilder(url) { UserName = "", Password = "" };
            return b.Uri.ToString();
        }
        catch
        {
            return url;
        }
    }

    private static IEnumerable<JsonElement> EnumerateWebcams(JsonElement root)
    {
        // Moonraker shapes: {"result":{"webcams":[...]}},
        // {"result":[...]}, or {"webcams":[...]}.
        JsonElement list = default;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("result", out var result))
        {
            if (result.ValueKind == JsonValueKind.Array) list = result;
            else if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("webcams", out var w)) list = w;
        }
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("webcams", out var w2)) list = w2;
        if (list.ValueKind != JsonValueKind.Array) yield break;
        foreach (var cam in list.EnumerateArray())
            if (cam.ValueKind == JsonValueKind.Object) yield return cam;
    }

    private static string? WebcamSnapshotField(JsonElement cam)
    {
        foreach (var field in new[] { "snapshot_url", "snapshotUrl", "snapshot" })
            if (cam.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(v.GetString()))
                return v.GetString();
        return null;
    }

    private static string? WebcamStreamField(JsonElement cam)
    {
        foreach (var field in new[] { "stream_url", "streamUrl", "stream" })
            if (cam.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(v.GetString()))
                return v.GetString();
        return null;
    }

    private static string? WebcamServiceField(JsonElement cam)
    {
        if (cam.TryGetProperty("service", out var v) && v.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(v.GetString()))
            return v.GetString();
        return null;
    }

    private string? ResolveCameraUrl(string raw)
    {
        var v = raw.Trim();
        if (v.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            v.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return v;
        if (v.StartsWith('/')) return Base + v;
        return Base + "/" + v;
    }

    private async Task<byte[]?> FetchJpegAsync(string url, CancellationToken ct)
    {
        try
        {
            byte[] bytes;
            if (url.StartsWith(Base, StringComparison.OrdinalIgnoreCase))
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (!await AddAuthAsync(req, ct)) return null;
                using var res = await _http.SendAsync(req, ct);
                if (!res.IsSuccessStatusCode) return null;
                bytes = await res.Content.ReadAsByteArrayAsync(ct);
            }
            else
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                bytes = await http.GetByteArrayAsync(url, ct);
            }
            if (bytes.Length < 100 || bytes.Length > 2_000_000) return null;
            if (bytes[0] != 0xFF || bytes[1] != 0xD8) return null;
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    private PrinterStatus Offline(string detail) => new()
    {
        Name = Name,
        Type = Type,
        Ip = Ip,
        Serial = SerialKey,
        State = "offline",
        Detail = detail,
        Ams = new AmsState { Known = false, Detail = "Printer unreachable; AMS unknown." },
        CameraAvailable = false,
    };

    private static string Trim(string s) => s.Length <= 200 ? s : s[..200] + "…";
}
