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
//   avoids, so snapshots come only from an owner-set snapshot_url.
//   Without one, no snapshot is reported — never a fake image.
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
    public string Name => _cfg.Name;

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
                State = state,
                ProgressPercent = percent,
                Detail = detail,
                Ams = new AmsState
                {
                    Known = false,
                    Detail = "U1 filament data is not exposed by a verified Moonraker endpoint; AMS is unverifiable.",
                },
                CameraAvailable = _cfg.Camera && !string.IsNullOrWhiteSpace(_cfg.SnapshotUrl),
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
        if (string.IsNullOrWhiteSpace(_cfg.SnapshotUrl)) return null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var bytes = await http.GetByteArrayAsync(_cfg.SnapshotUrl, ct);
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
        State = "offline",
        Detail = detail,
        Ams = new AmsState { Known = false, Detail = "Printer unreachable; AMS unknown." },
        CameraAvailable = false,
    };

    private static string Trim(string s) => s.Length <= 200 ? s : s[..200] + "…";
}
