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
                    // AUTO-START BUILD: the printer's own raw
                    // state word, verbatim, so a finish word
                    // survives beside the mapped status.
                    native_state = p.NativeState,
                    progress_percent = p.ProgressPercent,
                    remaining_seconds = p.RemainingSeconds,
                    current_job_key = p.CurrentJobKey,
                    detail = p.Detail,
                    // AUTO-START BUILD: per-tray slots ride
                    // inside ams (the dashboard already reads
                    // ams.slots); light_state rides top-level
                    // (the dashboard already reads it).
                    ams = new
                    {
                        known = p.Ams.Known,
                        material = p.Ams.Material,
                        color = p.Ams.Color,
                        detail = p.Ams.Detail,
                        slots = p.Ams.Slots.Select(s => new
                        {
                            slot = s.Slot,
                            material = s.Material,
                            color = s.Color,
                        }),
                        // GATE-AMS BUILD (2026.1105): when this
                        // AMS reading was observed, so the
                        // dashboard can show its age.
                        observed_unix = p.Ams.AmsObservedUnix,
                    },
                    light_state = p.LightState,
                    // GATE-LIGHT BUILD (2026.1105): printer-
                    // confirmed light mode + observation time,
                    // beside the bridge-executed light_state:
                    // "on" here means the printer itself
                    // reported chamber_light on, not merely
                    // that this bridge once sent ledctrl.
                    light_verified = p.LightVerified,
                    light_verified_unix = p.LightVerifiedUnix,
                    camera_available = p.CameraAvailable,
                    // FLUIDD SLICE 1 FIX: the Worker overlays
                    // nozzle diameters, last-parse fields, and
                    // now thermals / printer files / job history
                    // onto PrinterStatus, but this projection
                    // previously re-listed only a subset — so
                    // every overlaid field was silently dropped
                    // before sending, and the dashboard panels
                    // could never populate. The projection must
                    // carry every field the dashboard reads;
                    // anything not listed here never arrives.
                    nozzle_diameters = p.NozzleDiameters,
                    declared_file_tool = p.DeclaredFileTool,
                    declared_tool_sequence = p.DeclaredToolSequence,
                    file_profile = p.FileProfile,
                    file_nozzle_mm = p.FileNozzleMm,
                    thermals = p.Thermals.Select(t => new
                    {
                        name = t.Name,
                        actual = t.Actual,
                        target = t.Target,
                    }),
                    printer_files = p.PrinterFiles == null ? null : p.PrinterFiles.Select(f => new
                    {
                        filename = f.Filename,
                        size = f.Size,
                        modified = f.Modified,
                    }),
                    job_history = p.JobHistory == null ? null : p.JobHistory.Select(h => new
                    {
                        filename = h.Filename,
                        status = h.Status,
                        duration = h.Duration,
                        started = h.Started,
                    }),
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
    /// JPEG bytes, the source label, and (2276) the feed
    /// type plus, for HTTP feeds only, a
    /// credential-stripped feed URL.</summary>
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
                feed_type = snap.FeedType,
                feed_url = snap.FeedUrl,
                camera_available = snap.CameraAvailable,
            }, ct);
        return (ok, status);
    }

    /// <summary>0.9.6: poll the dashboard for pending camera
    /// refresh requests (farm/camera-refresh, bridge-key
    /// auth). Returns IP (lower-cased) -> request unix time,
    /// or null on any failure (the caller then falls back to
    /// its normal once-a-minute snapshot cadence). No
    /// credentials, URLs, or snapshots are in this reply.</summary>
    public async Task<Dictionary<string, long>?> GetCameraRefreshRequestsAsync(CancellationToken ct)
    {
        var (ok, _, body) = await SendAsync(HttpMethod.Get, "/wp-json/c3d-shipping/v1/farm/camera-refresh", null, ct);
        if (!ok || string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("requests", out var reqs) || reqs.ValueKind != JsonValueKind.Object) return null;
            var out_ = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in reqs.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt64(out var v))
                    out_[prop.Name.ToLowerInvariant()] = v;
            return out_;
        }
        catch { return null; }
    }

    /// <summary>Farm build, first stage: poll GET farm/jobs.
    /// Returns null on any failure (caller logs the reason).
    /// The dispatch field is the per-job command from the
    /// dashboard dispatch map, flattened into each job.</summary>
    public async Task<(List<FarmJob>? Jobs, string? Problem)> PollFarmJobsAsync(CancellationToken ct)
    {
        var (ok, status, body) = await SendAsync(HttpMethod.Get, "/wp-json/c3d-shipping/v1/farm/jobs", null, ct);
        if (!ok)
        {
            if (status == System.Net.HttpStatusCode.NotFound)
                return (null, "Dashboard farm endpoints are not installed yet.");
            if (status == System.Net.HttpStatusCode.Unauthorized)
                return (null, "Bridge API key was rejected by the dashboard.");
            return (null, $"Farm job poll failed ({(int)status}): {Trim(body)}");
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            var jobs = new List<FarmJob>();
            if (doc.RootElement.TryGetProperty("jobs", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    var job = JsonSerializer.Deserialize<FarmJob>(el.GetRawText(), JsonOptions);
                    if (job is not null) jobs.Add(job);
                }
            }
            // The dashboard also returns a dispatch map; the
            // per-job dispatch field above already carries it,
            // so no second parse is needed here.
            return (jobs, null);
        }
        catch (Exception ex)
        {
            return (null, $"Dashboard returned a farm job list the bridge could not read: {ex.Message}");
        }
    }

    /// <summary>Farm build: report a farm job outcome to POST
    /// farm/job-report. Status is started / paused / finished /
    /// failed / held, per the dashboard route. Started / failed
    /// are used only once a farm job actually starts or fails
    /// on a printer. Held is used when the bridge refuses a
    /// start dispatch (see FarmPoller.ReportHoldAsync): the
    /// dashboard shows "Held: &lt;reason&gt;" on the job and
    /// leaves it queued, so the bridge retries on its next
    /// poll once the problem is cleared.</summary>
    public async Task<bool> ReportFarmJobAsync(string jobId, string status, string reason, double? filamentUsedG, CancellationToken ct)
    {
        var (ok, _, _) = await SendAsync(HttpMethod.Post, "/wp-json/c3d-shipping/v1/farm/job-report",
            new { job_id = jobId, status, reason, filament_used_g = filamentUsedG }, ct);
        return ok;
    }

    /// <summary>BRIDGE-READS BUILD: report a streaming G-code
    /// parse for a farm job to the dashboard's farm/job-report
    /// route with status "file_parsed". The dashboard stores
    /// the declaration on the job record WITHOUT changing the
    /// job's status or clearing its dispatch (see the
    /// dashboard's file_parsed branch). A failed report is
    /// informational only: the caller must NOT hold or fail
    /// the job for it, and an older dashboard that rejects
    /// the unknown status simply never stores the parse.</summary>
    public async Task<bool> ReportFileParseAsync(string jobId, GcodeDeclaration decl, CancellationToken ct)
    {
        var (ok, _, _) = await SendAsync(HttpMethod.Post, "/wp-json/c3d-shipping/v1/farm/job-report",
            new
            {
                job_id = jobId,
                status = "file_parsed",
                declared_tool = decl.FirstTool,
                tool_sequence = string.Join(",", decl.ToolsInOrder),
                file_profile = decl.Profile,
                file_nozzle_mm = decl.SlicedNozzleMm,
                slicer = decl.Slicer,
                layers = decl.Layers,
            }, ct);
        return ok;
    }

    /// <summary>AUTO-START BUILD: report a farm job control
    /// outcome that needs its command named — the dashboard's
    /// control_failed branch requires it (a pause/stop/fail
    /// command that the bridge could not execute keeps the
    /// dispatch pending so the bridge retries on its next
    /// poll, instead of silently dropping the operator's
    /// pause/stop). Status is "control_failed" here; the
    /// command is "pause", "stop", or "fail".</summary>
    public async Task<bool> ReportFarmControlAsync(string jobId, string status, string reason, string command, CancellationToken ct)
    {
        var (ok, _, _) = await SendAsync(HttpMethod.Post, "/wp-json/c3d-shipping/v1/farm/job-report",
            new { job_id = jobId, status, reason, command }, ct);
        return ok;
    }

    /// <summary>AUTO-START BUILD: poll GET farm/slice-jobs for
    /// pending in-dashboard slice requests. Returns null on
    /// any failure (caller logs the reason).</summary>
    public async Task<(List<SliceJob>? Jobs, string? Problem)> PollSliceJobsAsync(CancellationToken ct)
    {
        var (ok, status, body) = await SendAsync(HttpMethod.Get, "/wp-json/c3d-shipping/v1/farm/slice-jobs", null, ct);
        if (!ok)
        {
            if (status == System.Net.HttpStatusCode.NotFound)
                return (null, "Dashboard slice endpoints are not installed yet.");
            if (status == System.Net.HttpStatusCode.Unauthorized)
                return (null, "Bridge API key was rejected by the dashboard.");
            return (null, $"Slice job poll failed ({(int)status}): {Trim(body)}");
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            var jobs = new List<SliceJob>();
            if (doc.RootElement.TryGetProperty("jobs", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    var job = JsonSerializer.Deserialize<SliceJob>(el.GetRawText(), JsonOptions);
                    if (job is not null) jobs.Add(job);
                }
            }
            return (jobs, null);
        }
        catch (Exception ex)
        {
            return (null, $"Dashboard returned a slice job list the bridge could not read: {ex.Message}");
        }
    }

    /// <summary>AUTO-START BUILD: report a slice outcome to
    /// POST farm/slice-report. Status is "done" (gcode_base64
    /// + file_name + sliced_for carried) or "failed" (reason
    /// carried, no file). The dashboard saves a done file
    /// as the library entry's sliced attachment; a failed
    /// slice never attaches anything.</summary>
    public async Task<bool> ReportSliceAsync(string sliceJobId, string status, string reason,
        string? gcodeBase64, string? fileName, string? slicedFor, CancellationToken ct)
    {
        var (ok, _, _) = await SendAsync(HttpMethod.Post, "/wp-json/c3d-shipping/v1/farm/slice-report",
            new
            {
                slice_job_id = sliceJobId,
                status,
                reason,
                gcode_base64 = gcodeBase64,
                file_name = fileName,
                sliced_for = slicedFor,
            }, ct);
        return ok;
    }

    /// <summary>AUTO-START BUILD: report a detected printer
    /// move to POST farm/printer-moved. The dashboard
    /// updates its manual printer record for that name to
    /// the new IP and posts a Farm tab notice. A move is
    /// reported only when the bridge matched the printer by
    /// serial (Bambu) or learned Moonraker hostname
    /// (Snapmaker) — never by "an open port looks close".
    /// 2026.1030.1511-farm-dedupe: the report also carries
    /// the serial identity key, so the dashboard matches by
    /// serial first, then name, then old IP.</summary>
    public async Task<bool> ReportPrinterMovedAsync(string name, string type,
        string oldIp, string newIp, string matchEvidence, string serial, CancellationToken ct)
    {
        var (ok, _, _) = await SendAsync(HttpMethod.Post, "/wp-json/c3d-shipping/v1/farm/printer-moved",
            new
            {
                name,
                type,
                old_ip = oldIp,
                new_ip = newIp,
                match_evidence = matchEvidence,
                serial,
            }, ct);
        return ok;
    }

    /// <summary>Farm controls (DRAFT 2026-10-29, not yet
    /// compile-verified): poll GET farm/controls. The
    /// dashboard serves pending control intents recorded by
    /// the Lights / Home / Power buttons; each entry names a
    /// printer (IP + name) and a command. Returns null on any
    /// failure (caller logs the reason). The dashboard clears
    /// a control from pending when its report arrives, so a
    /// failed report simply retries on the next poll.</summary>
    public async Task<(List<FarmControl>? Controls, string? Problem)> PollFarmControlsAsync(CancellationToken ct)
    {
        var (ok, status, body) = await SendAsync(HttpMethod.Get, "/wp-json/c3d-shipping/v1/farm/controls", null, ct);
        if (!ok)
        {
            if (status == System.Net.HttpStatusCode.NotFound)
                return (null, "Dashboard farm controls endpoint is not installed yet.");
            if (status == System.Net.HttpStatusCode.Unauthorized)
                return (null, "Bridge API key was rejected by the dashboard.");
            return (null, $"Farm control poll failed ({(int)status}): {Trim(body)}");
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            var controls = new List<FarmControl>();
            if (doc.RootElement.TryGetProperty("controls", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    var control = JsonSerializer.Deserialize<FarmControl>(el.GetRawText(), JsonOptions);
                    if (control is not null) controls.Add(control);
                }
            }
            return (controls, null);
        }
        catch (Exception ex)
        {
            return (null, $"Dashboard returned a farm control list the bridge could not read: {ex.Message}");
        }
    }

    /// <summary>Farm controls (DRAFT 2026-10-29): report one
    /// control outcome to POST farm/control-report. The
    /// dashboard records it as the control's confirmation and
    /// clears the pending intent. ok=false never claims the
    /// control worked — the detail says why it did not.</summary>
    public async Task<bool> ReportControlAsync(string printerIp, string command, bool ok, string detail, CancellationToken ct)
    {
        var (sent, _, _) = await SendAsync(HttpMethod.Post, "/wp-json/c3d-shipping/v1/farm/control-report",
            new { printer_ip = printerIp, command, ok, detail }, ct);
        return sent;
    }

    /// <summary>Bridge OCR stage: poll GET bridge/ocr-jobs.
    /// Returns null on any failure (caller logs the reason).
    /// Response shape, per the dashboard 2046 spec:
    /// { "jobs": [ { "job_id", "gmail_id", "pdf_b64",
    /// "file_name" } ] }. No new auth: the same Bearer key
    /// as every other request, via Request().</summary>
    public async Task<(List<OcrJob>? Jobs, string? Problem)> PollOcrJobsAsync(CancellationToken ct)
    {
        var (ok, status, body) = await SendAsync(HttpMethod.Get, "/wp-json/c3d-shipping/v1/bridge/ocr-jobs", null, ct);
        if (!ok)
        {
            if (status == System.Net.HttpStatusCode.NotFound)
                return (null, "Dashboard OCR endpoints are not installed yet.");
            if (status == System.Net.HttpStatusCode.Unauthorized)
                return (null, "Bridge API key was rejected by the dashboard.");
            return (null, $"OCR job poll failed ({(int)status}): {Trim(body)}");
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            var jobs = new List<OcrJob>();
            if (doc.RootElement.TryGetProperty("jobs", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    var job = JsonSerializer.Deserialize<OcrJob>(el.GetRawText(), JsonOptions);
                    if (job is not null) jobs.Add(job);
                }
            }
            return (jobs, null);
        }
        catch (Exception ex)
        {
            return (null, $"Dashboard returned an OCR job list the bridge could not read: {ex.Message}");
        }
    }

    /// <summary>Bridge OCR stage: report one OCR job to POST
    /// bridge/ocr-report. Request shape, per the dashboard
    /// 2046 spec: { "job_id", "text", "engine": "tesseract" }.
    /// ONLY extracted text is ever sent — never the PDF, never
    /// the rendered image, never any other PDF content.
    /// NOTE (honest limit): the dashboard route today reads
    /// only job_id and text and marks the job done on any
    /// report, so the status/reason fields below are ignored
    /// server-side for now — but sending the report is still
    /// what stops a failed job being re-sent in a loop, and
    /// the reason is logged locally by the caller. A future
    /// dashboard change may store the reason.</summary>
    public async Task<(bool Ok, string Reason)> ReportOcrAsync(
        string jobId, string text, string engine, string status, string reason, CancellationToken ct)
    {
        var (ok, code, body) = await SendAsync(HttpMethod.Post, "/wp-json/c3d-shipping/v1/bridge/ocr-report",
            new { job_id = jobId, text, engine, status, reason }, ct);
        return ok ? (true, "") : (false, $"OCR report answered {(int)code}: {Trim(body)}");
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
