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
//   only. Without an owner-set or discovered URL, no snapshot
//   is reported — never a fake image.
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
    private bool _autoDiscoveryAttempted;
    public string SnapshotSource => _discoveredSnapshotUrl is not null || _cfg.SnapshotAutoDiscovered
        ? "auto_discovered_moonraker" : "owner_snapshot_url";
    public string Name => _cfg.Name;
    public string Type => _cfg.Type;
    public string Ip => _cfg.Ip;

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
            var detail = stateRaw is "" or "printing" or "paused" or "standby" or "complete" or "cancelled" or "error"
                ? ""
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
                State = state,
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

    public async Task<(bool Ok, string Reason)> UploadFileAsync(string localPath, string fileName, CancellationToken ct)
    {
        try
        {
            using var form = new MultipartFormDataContent();
            var bytes = await File.ReadAllBytesAsync(localPath, ct);
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(content, "file", fileName);
            form.Add(new StringContent("false"), "print");

            var req = new HttpRequestMessage(HttpMethod.Post, Base + "/server/files/upload") { Content = form };
            if (!await AddAuthAsync(req, ct))
                return (false, $"Paxx login failed for '{Name}': check the Paxx username/password in Configure.");
            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
                return (false, $"Moonraker upload to '{Name}' answered {(int)res.StatusCode}: {Trim(await res.Content.ReadAsStringAsync(ct))}");
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, $"Upload to '{Name}' failed: {ex.Message}");
        }
    }

    public async Task<(bool Ok, string Reason)> StartPrintAsync(string fileName, string jobKey, CancellationToken ct)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { filename = fileName });
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

    public async Task<byte[]?> TryGetSnapshotAsync(CancellationToken ct)
    {
        var url = !string.IsNullOrWhiteSpace(_cfg.SnapshotUrl) ? _cfg.SnapshotUrl : _discoveredSnapshotUrl;
        if (string.IsNullOrWhiteSpace(url)) return null;
        return await FetchJpegAsync(url, ct);
    }

    /// <summary>Camera auto-discovery (2275). Never overwrites
    /// an owner-set URL (returns null when one is set). Tries,
    /// in order: Moonraker GET /server/webcams/list — first
    /// webcam's snapshot_url, resolved against the printer
    /// base URL — then, only when the list is empty or
    /// unavailable, /webcam/?action=snapshot and
    /// /webcam/snapshot. Every candidate is fetched with the
    /// printer's existing auth and must validate as a JPEG
    /// (100 bytes – 2 MB, SOI marker) before it is returned.
    /// Each miss is logged once (this method runs at most
    /// once per adapter instance); no URL is logged.</summary>
    public async Task<string?> TryAutoDiscoverSnapshotUrlAsync(CancellationToken ct)
    {
        if (_autoDiscoveryAttempted) return null;
        _autoDiscoveryAttempted = true;
        if (!string.IsNullOrWhiteSpace(_cfg.SnapshotUrl)) return null;
        if (!string.IsNullOrWhiteSpace(_discoveredSnapshotUrl)) return _discoveredSnapshotUrl;

        // 1. Moonraker webcam list.
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, Base + "/server/webcams/list");
            if (!await AddAuthAsync(req, ct))
            {
                Program.Log($"Camera auto-discovery for '{Name}': Moonraker login failed; no snapshot URL discovered.");
            }
            else
            {
                using var res = await _http.SendAsync(req, ct);
                if (res.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                    foreach (var cam in EnumerateWebcams(doc.RootElement))
                    {
                        var raw = WebcamSnapshotField(cam);
                        if (string.IsNullOrWhiteSpace(raw)) continue;
                        var resolved = ResolveSnapshotUrl(raw);
                        if (resolved is null) continue;
                        if (await FetchJpegAsync(resolved, ct) is not null)
                        {
                            _discoveredSnapshotUrl = resolved;
                            Program.Log($"Camera auto-discovery for '{Name}': snapshot URL auto-discovered from the Moonraker webcam list.");
                            return resolved;
                        }
                    }
                    Program.Log($"Camera auto-discovery for '{Name}': Moonraker webcam list had no usable snapshot URL.");
                }
                else
                {
                    Program.Log($"Camera auto-discovery for '{Name}': Moonraker webcam list unavailable (HTTP {(int)res.StatusCode}).");
                }
            }
        }
        catch (Exception ex)
        {
            Program.Log($"Camera auto-discovery for '{Name}': Moonraker webcam list failed ({ex.Message}).");
        }

        // 2. Common Fluidd/Moonraker still paths, only as fallback.
        foreach (var path in new[] { "/webcam/?action=snapshot", "/webcam/snapshot" })
        {
            var url = Base + path;
            if (await FetchJpegAsync(url, ct) is not null)
            {
                _discoveredSnapshotUrl = url;
                Program.Log($"Camera auto-discovery for '{Name}': snapshot URL auto-discovered from a common Moonraker still path.");
                return url;
            }
            Program.Log($"Camera auto-discovery for '{Name}': fallback still path missed.");
        }

        Program.Log($"Camera auto-discovery for '{Name}': nothing found; no snapshot will be sent until a snapshot URL is set or discovered.");
        return null;
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

    private string? ResolveSnapshotUrl(string raw)
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
        State = "offline",
        Detail = detail,
        Ams = new AmsState { Known = false, Detail = "Printer unreachable; AMS unknown." },
        CameraAvailable = false,
    };

    private static string Trim(string s) => s.Length <= 200 ? s : s[..200] + "…";
}
