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
    private string _discoveredFeedType = "none";
    private bool _cameraDiscoveryAttempted;
    public string SnapshotSource => _discoveredSnapshotUrl is not null || _cfg.SnapshotAutoDiscovered
        ? "auto_discovered_moonraker" : "owner_snapshot_url";
    /// <summary>Feed type (2276). Owner-set stream URL wins
    /// over a discovered one; a discovered RTSP/other feed
    /// still reports its type even though its URL is never
    /// sent to the dashboard.</summary>
    public string FeedType => !string.IsNullOrWhiteSpace(_cfg.StreamUrl)
        ? ClassifyFeedType(_cfg.StreamUrl, null) : _discoveredFeedType;
    /// <summary>Dashboard-facing feed URL (2276): HTTP(S)
    /// only, credential-stripped. Null for RTSP/other
    /// feeds and when no feed is known.</summary>
    public string? FeedUrl
    {
        get
        {
            var raw = !string.IsNullOrWhiteSpace(_cfg.StreamUrl) ? _cfg.StreamUrl : _discoveredStreamUrl;
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
        State = "offline",
        Detail = detail,
        Ams = new AmsState { Known = false, Detail = "Printer unreachable; AMS unknown." },
        CameraAvailable = false,
    };

    private static string Trim(string s) => s.Length <= 200 ? s : s[..200] + "…";
}
