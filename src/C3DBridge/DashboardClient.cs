// C3D Print Bridge — dashboard HTTP client.
//
// OUTBOUND ONLY. The dashboard lives on a remote web host and the
// bridge lives on the home LAN behind NAT, so the dashboard can
// never connect to the bridge. Every contact is initiated here:
// heartbeat, status pushes, job polls, progress reports, snapshot
// uploads. This client never opens a listening socket.
//
// AUTH: every request carries the per-bridge API key in an
// Authorization: Bearer header. The key is generated on the
// dashboard settings page (shown once) and stored ONLY in the
// restricted local key file on the PC. A 401 means the key is
// missing, revoked, or rotated — the bridge stops polling jobs,
// logs a plain-language warning, and keeps sending heartbeats at a
// slow rate so the dashboard can show the problem. It never
// retries a rejected key aggressively, and never falls back to
// unauthenticated requests.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace C3DBridge;

public sealed class DashboardClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _bridgeName;
    private string? _apiKey;
    private bool _keyRejected;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public bool KeyRejected => _keyRejected;
    public bool HasKey => !string.IsNullOrEmpty(_apiKey);

    public DashboardClient(string baseUrl, string bridgeName, string? apiKey)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _bridgeName = bridgeName;
        _apiKey = apiKey;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("C3DBridge/1.0");
    }

    public void UpdateKey(string? apiKey)
    {
        _apiKey = apiKey;
        _keyRejected = false;
    }

    private HttpRequestMessage Request(HttpMethod method, string path, object? body = null)
    {
        var req = new HttpRequestMessage(method, _baseUrl + path);
        if (!string.IsNullOrEmpty(_apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, JsonOptions);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        return req;
    }

    /// <summary>Send a request. Returns (ok, status, body). A 401 sets
    /// KeyRejected and is reported, never retried in a tight loop.
    /// A 404 means the dashboard-side bridge endpoints are not
    /// installed yet (they ship in a later plugin update) — the
    /// caller reports that plainly instead of treating it as a
    /// printer problem.</summary>
    private async Task<(bool Ok, HttpStatusCode Status, string Body)> SendAsync(
        HttpMethod method, string path, object? body, CancellationToken ct)
    {
        try
        {
            using var res = await _http.SendAsync(Request(method, path, body), ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (res.StatusCode == HttpStatusCode.Unauthorized) _keyRejected = true;
            return (res.IsSuccessStatusCode, res.StatusCode, text);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, 0, "Request timed out.");
        }
        catch (HttpRequestException ex)
        {
            return (false, 0, $"Could not reach the dashboard: {ex.Message}");
        }
    }

    public async Task<bool> HeartbeatAsync(CancellationToken ct)
    {
        var (ok, _, _) = await SendAsync(HttpMethod.Post, "/wp-json/c3d-shipping/v1/bridge/heartbeat",
            new { bridge_name = _bridgeName, version = Program.Version, timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }, ct);
        return ok;
    }

    public async Task<bool> PushStatusAsync(IReadOnlyList<PrinterStatus> printers, int queueLength, CancellationToken ct)
    {
        var (ok, _, _) = await SendAsync(HttpMethod.Post, "/wp-json/c3d-shipping/v1/bridge/status",
            new
            {
                bridge_name = _bridgeName,
                version = Program.Version,
                queue_length = queueLength,
                printers = printers.Select(p => new
                {
                    name = p.Name,
                    type = p.Type,
                    ip = p.Ip,
                    status = p.State,
                    state = p.State,
                    progress_percent = p.ProgressPercent,
                    remaining_seconds = p.RemainingSeconds,
                    current_job_key = p.CurrentJobKey,
                    detail = p.Detail,
                    ams = new { known = p.Ams.Known, material = p.Ams.Material, color = p.Ams.Color, detail = p.Ams.Detail },
                    camera_available = p.CameraAvailable,
                }),
            }, ct);
        return ok;
    }

    /// <summary>Fetch jobs the dashboard assigned to this bridge.
    /// Returns null when the dashboard could not be reached or the
    /// endpoints are not installed yet; the caller logs the reason.</summary>
    public async Task<(List<DashboardJob>? Jobs, string? Problem)> PollJobsAsync(CancellationToken ct)
    {
        var (ok, status, body) = await SendAsync(HttpMethod.Get,
            $"/wp-json/c3d-shipping/v1/bridge/jobs?bridge={Uri.EscapeDataString(_bridgeName)}", null, ct);
        if (!ok)
        {
            if (status == HttpStatusCode.NotFound)
                return (null, "Dashboard bridge endpoints are not installed yet (they ship in a later plugin update).");
            if (status == HttpStatusCode.Unauthorized)
                return (null, "Bridge API key was rejected by the dashboard — it may be revoked or rotated. Re-copy the key from the dashboard settings (see SETUP.md).");
            return (null, $"Job poll failed ({(int)status}): {Trim(body)}");
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            var jobs = new List<DashboardJob>();
            if (doc.RootElement.TryGetProperty("jobs", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    var job = JsonSerializer.Deserialize<DashboardJob>(el.GetRawText(), JsonOptions);
                    if (job is not null) jobs.Add(job);
                }
            }
            return (jobs, null);
        }
        catch (Exception ex)
        {
            return (null, $"Dashboard returned a job list the bridge could not read: {ex.Message}");
        }
    }

    /// <summary>Push one bed-view snapshot to the dashboard
    /// (farm/bed-snapshot). Returns (ok, status). A 404 means
    /// the dashboard plugin does not have the Bed View route
    /// yet — the caller logs that once and stops trying until
    /// restart, instead of spamming. The payload carries no
    /// credentials: printer name/ip/type, capture time, the
    /// JPEG bytes, and the source label only.</summary>
    public async Task<(bool Ok, System.Net.HttpStatusCode Status)> PushBedSnapshotAsync(CameraSnapshot snap, CancellationToken ct)
    {
        var (ok, status, _) = await SendAsync(HttpMethod.Post, "/wp-json/c3d-shipping/v1/farm/bed-snapshot",
            new
            {
                printer_name = snap.PrinterName,
                printer_ip = snap.PrinterIp,
                printer_type = snap.PrinterType,
                captured_unix = snap.CapturedUnix,
                snapshot_base64 = snap.SnapshotBase64,
                snapshot_source = snap.SnapshotSource,
                camera_available = snap.CameraAvailable,
            }, ct);
        return (ok, status);
    }

    public async Task<bool> ReportJobAsync(JobReport report, CancellationToken ct)
    {
        var (ok, _, _) = await SendAsync(HttpMethod.Post, "/wp-json/c3d-shipping/v1/bridge/job-report",
            new
            {
                job_key = report.JobKey,
                order_id = report.OrderId,
                printer_name = report.PrinterName,
                status = report.Status,
                progress_percent = report.ProgressPercent,
                reason = report.Reason,
                snapshot_base64 = report.SnapshotBase64,
            }, ct);
        return ok;
    }

    /// <summary>Download a dashboard-hosted print file to a local
    /// path, with the same Bearer auth as every other request.
    /// A relative file_url is resolved against the dashboard base
    /// URL. When file_sha256 is supplied, the download is verified
    /// against it; a mismatch deletes the file and fails — a
    /// corrupted file is never sent to a printer.</summary>
    public async Task<(bool Ok, string Reason)> DownloadFileAsync(
        string fileUrl, string destPath, string expectedSha256, CancellationToken ct)
    {
        var url = fileUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? fileUrl
            : _baseUrl + (fileUrl.StartsWith("/") ? fileUrl : "/" + fileUrl);
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(_apiKey))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode)
                return (false, $"File download answered {(int)res.StatusCode}.");
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            await using (var fs = File.Create(destPath))
            {
                await res.Content.CopyToAsync(fs, ct);
            }
            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                using var sha = System.Security.Cryptography.SHA256.Create();
                await using var fs = File.OpenRead(destPath);
                var hash = Convert.ToHexString(await sha.ComputeHashAsync(fs, ct)).ToLowerInvariant();
                if (!string.Equals(hash, expectedSha256.Trim().ToLowerInvariant(), StringComparison.Ordinal))
                {
                    try { File.Delete(destPath); } catch { /* best effort */ }
                    return (false, "Downloaded file failed its SHA-256 check; the file was discarded, not printed.");
                }
            }
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, $"File download failed: {ex.Message}");
        }
    }

    private static string Trim(string s) => s.Length <= 200 ? s : s[..200] + "…";

    public void Dispose() => _http.Dispose();
}
