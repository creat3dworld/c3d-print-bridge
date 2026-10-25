// C3D Print Bridge — Facebook label OCR poller (bridge OCR stage).
//
// This is the bridge side of the dashboard's Facebook label OCR
// fallback (dashboard 2026.1024.2046-fb-ocr). The dashboard
// already extracts tracking from a label PDF's text layer; when
// that yields nothing (a scanned/image-only label), it queues
// the PDF for the bridge, and this poller does the OCR locally
// on the shop PC.
//
// OUTBOUND ONLY, like every other bridge route: the bridge polls
// GET /wp-json/c3d-shipping/v1/bridge/ocr-jobs with the same
// Bearer bridge key as all other dashboard requests, and posts
// ONLY extracted text to POST
// /wp-json/c3d-shipping/v1/bridge/ocr-report. It never opens a
// listening port, never invents an auth scheme, and never calls
// a new dashboard route.
//
// PRIVACY, hard rule: a shipping label carries a buyer address.
// The PDF and its rendered image NEVER leave this PC except as
// extracted text back to the dashboard — never to an external
// OCR API, never to an external rendering service, never to
// any third party. Extracted OCR text is NEVER written to the
// bridge log either (only the job id, the outcome, and a
// failure reason are logged), because that text can contain
// the buyer address and the tracking number.
//
// WHAT THIS POLLER NEVER DOES: it never starts a print, never
// buys a label, never pushes tracking to Facebook, never edits
// a host order, and never touches a printer. OCR jobs are
// paperwork only. A job that fails to render or OCR is reported
// back as failed with its reason (see ReportOcrAsync), not
// silently dropped — and it is never retried in a loop by this
// poller: a job reported once in this run is never reported
// again, and the dashboard marks a reported job done.
//
// TOOLCHAIN, local only (both are free, both are install
// prerequisites on the shop PC — see the changes note):
//   - Renderer: Ghostscript (gswin64c.exe), on PATH or under
//     C:\Program Files\gs\gs*\bin. Page 1 only, 300 dpi PNG.
//     No .NET PDF renderer is referenced by this project, so
//     no bundled renderer is invented here; Ghostscript is
//     the one remaining install prerequisite if it is absent.
//   - OCR: local Tesseract (tesseract.exe on PATH), invoked as
//     `tesseract <png> stdout --psm 6`, the same invocation
//     the dashboard uses server-side. No external OCR API.

using System.Diagnostics;

namespace C3DBridge;

public sealed class OcrPoller
{
    private readonly DashboardClient _dashboard;
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);
    private bool _ghostscriptMissingLogged;
    private bool _tesseractMissingLogged;
    private bool _endpointMissingLogged;

    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(120);

    public OcrPoller(DashboardClient dashboard)
    {
        _dashboard = dashboard;
    }

    public async Task PollAsync(CancellationToken ct)
    {
        if (!_dashboard.HasKey)
            return;
        var (jobs, problem) = await _dashboard.PollOcrJobsAsync(ct);
        if (jobs is null)
        {
            // 404 = the dashboard OCR routes are not installed
            // yet: log once, then stay quiet until restart,
            // exactly like the bed-snapshot missing-route rule.
            if (problem is not null && problem.Contains("not installed", StringComparison.OrdinalIgnoreCase))
            {
                if (!_endpointMissingLogged)
                {
                    _endpointMissingLogged = true;
                    Program.Log($"[ocr] Poll failed: {problem}");
                }
                return;
            }
            if (!string.IsNullOrWhiteSpace(problem))
                Program.Log($"[ocr] Poll failed: {problem}");
            return;
        }
        foreach (var job in jobs)
        {
            if (_reported.Contains(job.JobId))
                continue; // Never report the same job twice in a run.
            await ProcessJobAsync(job, ct);
        }
    }

    private async Task ProcessJobAsync(OcrJob job, CancellationToken ct)
    {
        var pdfPath = Path.Combine(Config.TempDir, "ocr_" + Sanitize(job.JobId) + ".pdf");
        var pngPath = Path.ChangeExtension(pdfPath, ".png");
        try
        {
            byte[] pdf;
            try
            {
                pdf = Convert.FromBase64String(job.PdfB64);
            }
            catch
            {
                await ReportFailedAsync(job, "Label PDF was not valid base64; nothing was rendered or OCR'd.", ct);
                return;
            }
            if (pdf.Length < 5 || pdf[0] != (byte)'%' || pdf[1] != (byte)'P' ||
                pdf[2] != (byte)'D' || pdf[3] != (byte)'F' || pdf[4] != (byte)'-')
            {
                await ReportFailedAsync(job, "Downloaded label data was not a PDF; nothing was rendered or OCR'd.", ct);
                return;
            }
            Directory.CreateDirectory(Config.TempDir);
            await File.WriteAllBytesAsync(pdfPath, pdf, ct);

            var gs = FindGhostscript();
            if (gs is null)
            {
                if (!_ghostscriptMissingLogged)
                {
                    _ghostscriptMissingLogged = true;
                    Program.Log("[ocr] Ghostscript (gswin64c) was not found on this PC, so label PDFs cannot be rendered for OCR. Install Ghostscript for Windows (free) and make gswin64c.exe available on PATH or under C:\\Program Files\\gs — see the bridge OCR changes note.");
                }
                await ReportFailedAsync(job, "Ghostscript (gswin64c) is not installed on the shop PC, so the label PDF could not be rendered for OCR.", ct);
                return;
            }
            var (renderOk, renderReason) = await RunToolAsync(
                gs,
                $"-dBATCH -dNOPAUSE -sDEVICE=png16m -r300 -dFirstPage=1 -dLastPage=1 -sOutputFile=\"{pngPath}\" \"{pdfPath}\"",
                ct);
            if (!renderOk || !File.Exists(pngPath) || new FileInfo(pngPath).Length == 0)
            {
                await ReportFailedAsync(job, $"Ghostscript could not render page 1 of the label PDF{(string.IsNullOrWhiteSpace(renderReason) ? "." : ": " + renderReason)}", ct);
                return;
            }

            var tess = FindOnPath("tesseract.exe");
            if (tess is null)
            {
                if (!_tesseractMissingLogged)
                {
                    _tesseractMissingLogged = true;
                    Program.Log("[ocr] tesseract.exe was not found on PATH, so rendered label images cannot be OCR'd. Install Tesseract for Windows (free) and put tesseract.exe on PATH — verify with `tesseract --version`.");
                }
                await ReportFailedAsync(job, "tesseract.exe is not on PATH on the shop PC, so the rendered label image could not be OCR'd.", ct);
                return;
            }
            var (ocrOk, ocrText, ocrReason) = await RunToolWithOutputAsync(
                tess, $"\"{pngPath}\" stdout --psm 6", ct);
            if (!ocrOk && string.IsNullOrWhiteSpace(ocrText))
            {
                await ReportFailedAsync(job, $"Tesseract could not read the rendered label image{(string.IsNullOrWhiteSpace(ocrReason) ? "." : ": " + ocrReason)}", ct);
                return;
            }
            // OCR ran. Empty text is a legitimate "no text
            // found" result, not a failure: it still marks the
            // job done on the dashboard (which stores nothing
            // for a non-match), so the job is never looped.
            var (ok, _) = await _dashboard.ReportOcrAsync(
                job.JobId, ocrText, "tesseract",
                string.IsNullOrWhiteSpace(ocrText) ? "no_text" : "ok",
                string.IsNullOrWhiteSpace(ocrText) ? "Tesseract ran but extracted no text from the label image." : "",
                ct);
            if (ok)
            {
                _reported.Add(job.JobId);
                Program.Log($"[ocr] Job {job.JobId}: reported extracted text to the dashboard (text itself is never logged).");
            }
            else
            {
                Program.Log($"[ocr] Job {job.JobId}: report to the dashboard failed; the job stays queued and will be re-sent by the dashboard after 7 days without a report.");
            }
        }
        finally
        {
            // Temp files hold a buyer-addressed label: always
            // removed, best effort, never left on disk.
            try { if (File.Exists(pdfPath)) File.Delete(pdfPath); } catch { /* best effort */ }
            try { if (File.Exists(pngPath)) File.Delete(pngPath); } catch { /* best effort */ }
        }
    }

    private async Task ReportFailedAsync(OcrJob job, string reason, CancellationToken ct)
    {
        // NOTE: the dashboard's ocr-report route today reads
        // only job_id and text; the status/reason fields below
        // are carried for a future dashboard change that stores
        // failure reasons. Sending this report still marks the
        // job done on the dashboard, which is what stops it
        // being re-sent in a loop. The reason itself is logged
        // locally here, never silently dropped.
        var (ok, _) = await _dashboard.ReportOcrAsync(job.JobId, "", "tesseract", "failed", reason, ct);
        if (ok)
            _reported.Add(job.JobId);
        Program.Log($"[ocr] Job {job.JobId}: failed — {reason}");
    }

    /// <summary>Ghostscript: PATH first, then the standard
    /// install folders. Returns null when not found anywhere
    /// (the caller reports that as a failed job, never a crash).</summary>
    private static string? FindGhostscript()
    {
        var onPath = FindOnPath("gswin64c.exe") ?? FindOnPath("gswin32c.exe");
        if (onPath is not null) return onPath;
        foreach (var root in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "gs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "gs"),
        })
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                var candidates = Directory.GetFiles(root, "gswin64c.exe", SearchOption.AllDirectories)
                    .Concat(Directory.GetFiles(root, "gswin32c.exe", SearchOption.AllDirectories))
                    .OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase);
                var first = candidates.FirstOrDefault();
                if (first is not null) return first;
            }
            catch { /* a folder we cannot read is not a renderer */ }
        }
        return null;
    }

    private static string? FindOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var candidate = Path.Combine(dir.Trim(), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* ignore an unusable PATH entry */ }
        }
        return null;
    }

    private static async Task<(bool Ok, string Reason)> RunToolAsync(
        string exe, string args, CancellationToken ct)
    {
        // Tuple order from RunToolCoreAsync is
        // (Ok, Text, Error, TimedOut) — do not swap Ok and
        // TimedOut here, or a timed-out tool would read as
        // a success.
        var (ok, _, err, _) = await RunToolCoreAsync(exe, args, ct);
        return (ok, TrimReason(err));
    }

    private static async Task<(bool Ok, string Text, string Reason)> RunToolWithOutputAsync(
        string exe, string args, CancellationToken ct)
    {
        var (ok, text, err, _) = await RunToolCoreAsync(exe, args, ct);
        return (ok, text, TrimReason(err));
    }

    private static async Task<(bool Ok, string Text, string Error, bool TimedOut)> RunToolCoreAsync(
        string exe, string args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return (false, "", "", false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ToolTimeout);
            var outTask = proc.StandardOutput.ReadToEndAsync(timeout.Token);
            var errTask = proc.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await proc.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return (false, "", "Tool timed out after 120 seconds.", true);
            }
            var text = await outTask;
            var err = await errTask;
            return (proc.ExitCode == 0, text, err, false);
        }
        catch (Exception ex)
        {
            return (false, "", ex.Message, false);
        }
    }

    private static string TrimReason(string s)
    {
        s = (s ?? "").Trim();
        return s.Length <= 200 ? s : s[..200] + "…";
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "ocr-job" : name;
    }
}
