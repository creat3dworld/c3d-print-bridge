// C3D Print Bridge — worker loop (the thin relay itself).
//
// Every tick the bridge does exactly three things, all outbound:
//   1. Ask each configured printer for its live state.
//   2. Push that state (and the queue length) to the dashboard.
//   3. Ask the dashboard for jobs it assigned to this bridge, and
//      carry out the ones the dashboard marked "ready".
//
// What it NEVER does (thin-relay scope, owner-confirmed):
// - It never picks a printer for a job: the dashboard assigns by
//   name, and an unknown name is an error, not a substitution.
// - It never orders, prioritizes, or reschedules jobs: per printer
//   it runs at most one job, in dashboard poll order, because a
//   printer can physically print only one thing at a time.
// - It never slices, modifies, or inspects model geometry: the
//   file the dashboard provides is forwarded byte-for-byte.
// - It keeps no database and serves no UI: state is in-memory
//   only, plus a plain log file. A restart forgets in-flight jobs
//   (the printer keeps printing; the dashboard still shows the
//   job until it is re-reported — see README limits).
// - It never logs secrets: no API keys, no access codes, no full
//   URLs (file names and job keys only).

using C3DBridge.Printers;

namespace C3DBridge;

public sealed class Worker
{
    private readonly BridgeConfig _cfg;
    private readonly PrinterRegistry _printers;
    private readonly DashboardClient _dashboard;
    private readonly Dictionary<string, ActiveJob> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _terminalReported = new(StringComparer.OrdinalIgnoreCase);
    private List<DashboardJob> _lastPollJobs = new();
    private string _lastPollProblem = "";
    private DateTime _lastPollProblemLog = DateTime.MinValue;
    private DateTime _lastKeyWarning = DateTime.MinValue;

    private sealed record ActiveJob(
        DashboardJob Job,
        IPrinterAdapter Adapter,
        string TempPath,
        DateTime StartedAt,
        bool PrintConfirmed,
        double LastReportedPercent,
        DateTime LastProgressReport,
        DateTime LastSeenOnline);

    public Worker(BridgeConfig cfg, PrinterRegistry printers, DashboardClient dashboard)
    {
        _cfg = cfg;
        _printers = printers;
        _dashboard = dashboard;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Program.Log($"Bridge '{_cfg.BridgeName}' running: {_printers.All.Count} printer(s) configured, dashboard {_cfg.DashboardBaseUrl}.");
        if (!_dashboard.HasKey)
            Program.Log("WARNING: no bridge API key stored. Dashboard reporting is disabled until setup stores a key (see SETUP.md). Local printer polling continues.");

        var lastStatus = DateTime.MinValue;
        var lastHeartbeat = DateTime.MinValue;
        var lastPoll = DateTime.MinValue;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;

                // 1. Live printer state (each printer isolated: one
                //    printer's failure never stops the others).
                Dictionary<string, PrinterStatus> statuses = new(StringComparer.OrdinalIgnoreCase);
                if (now - lastStatus >= TimeSpan.FromSeconds(_cfg.StatusIntervalSeconds))
                {
                    lastStatus = now;
                    var tasks = _printers.All.Select(async a =>
                    {
                        try { return await a.GetStatusAsync(ct); }
                        catch (Exception ex) { return new PrinterStatus { Name = a.Name, State = "offline", Detail = ex.Message }; }
                    });
                    foreach (var st in await Task.WhenAll(tasks))
                        statuses[st.Name] = st;

                    var queueLength = _lastPollJobs.Count(j =>
                        string.Equals(j.SafetyVerdict, "ready", StringComparison.OrdinalIgnoreCase) &&
                        !_active.ContainsKey(j.PrinterName) &&
                        !_terminalReported.Contains(j.JobKey)) + _active.Count;
                    if (_dashboard.HasKey)
                        await _dashboard.PushStatusAsync(statuses.Values.ToList(), queueLength, ct);
                    else
                        foreach (var st in statuses.Values)
                            Program.Log($"[local] {st.Name}: {st.State} {st.Detail}");
                }
                else
                {
                    // Reuse the last known state between status ticks.
                    statuses = _lastStatuses;
                }

                // 2. Heartbeat (dashboard treats >10 min old as stale).
                if (_dashboard.HasKey && now - lastHeartbeat >= TimeSpan.FromSeconds(_cfg.HeartbeatIntervalSeconds))
                {
                    lastHeartbeat = now;
                    await _dashboard.HeartbeatAsync(ct);
                }

                // 3. Job poll + execution + monitoring.
                if (_dashboard.HasKey && !_dashboard.KeyRejected &&
                    now - lastPoll >= TimeSpan.FromSeconds(_cfg.JobPollIntervalSeconds))
                {
                    lastPoll = now;
                    var (jobs, problem) = await _dashboard.PollJobsAsync(ct);
                    if (jobs is not null)
                    {
                        _lastPollJobs = jobs;
                        if (_lastPollProblem != "")
                        {
                            _lastPollProblem = "";
                            Program.Log("Dashboard job poll recovered.");
                        }
                        await ExecuteReadyJobsAsync(statuses, ct);
                    }
                    else if (problem is not null && ShouldLogProblem(problem))
                    {
                        Program.Log($"Job poll problem: {problem}");
                    }
                }
                else if (_dashboard.KeyRejected && now - _lastKeyWarning >= TimeSpan.FromMinutes(10))
                {
                    _lastKeyWarning = now;
                    Program.Log("WARNING: dashboard rejected the bridge API key (revoked or rotated). Job polling paused. Re-copy the key from the dashboard settings and run setup again (see SETUP.md).");
                }

                await MonitorActiveJobsAsync(statuses, ct);
                _lastStatuses = statuses;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Program.Log($"Worker loop error (continuing): {ex.Message}");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private Dictionary<string, PrinterStatus> _lastStatuses = new(StringComparer.OrdinalIgnoreCase);

    private bool ShouldLogProblem(string problem)
    {
        if (problem != _lastPollProblem || DateTime.UtcNow - _lastPollProblemLog >= TimeSpan.FromMinutes(30))
        {
            _lastPollProblem = problem;
            _lastPollProblemLog = DateTime.UtcNow;
            return true;
        }
        return false;
    }

    private async Task ExecuteReadyJobsAsync(
        IReadOnlyDictionary<string, PrinterStatus> statuses, CancellationToken ct)
    {
        foreach (var job in _lastPollJobs)
        {
            if (!string.Equals(job.SafetyVerdict, "ready", StringComparison.OrdinalIgnoreCase)) continue;
            if (_terminalReported.Contains(job.JobKey)) continue;
            if (_active.ContainsKey(job.PrinterName)) continue;

            var adapter = _printers.Find(job.PrinterName);
            if (adapter is null)
            {
                await ReportTerminalAsync(new JobReport
                {
                    JobKey = job.JobKey, OrderId = job.OrderId, PrinterName = job.PrinterName,
                    Status = "failed",
                    Reason = $"Unknown printer '{job.PrinterName}': no printer by that name is configured on this bridge. The bridge never substitutes another printer.",
                }, ct);
                continue;
            }

            if (string.IsNullOrWhiteSpace(job.FileUrl))
            {
                await ReportTerminalAsync(new JobReport
                {
                    JobKey = job.JobKey, OrderId = job.OrderId, PrinterName = job.PrinterName,
                    Status = "failed",
                    Reason = "No print file supplied with this job. Digital products are never printed.",
                }, ct);
                continue;
            }

            if (!statuses.TryGetValue(job.PrinterName, out var live) ||
                !string.Equals(live.State, "idle", StringComparison.OrdinalIgnoreCase))
            {
                // Printer busy, offline, or state unknown: leave the
                // job queued. It stays "ready" on the dashboard and
                // is picked up on a later poll. No report, no spam.
                continue;
            }

            var refusal = Safety.InterlockRefusal(job, live);
            if (refusal is not null)
            {
                await ReportTerminalAsync(new JobReport
                {
                    JobKey = job.JobKey, OrderId = job.OrderId, PrinterName = job.PrinterName,
                    Status = "refused_unsafe", Reason = refusal,
                }, ct);
                continue;
            }

            // Carry the job out: download (verified) → upload → start.
            var tempPath = Path.Combine(Config.TempDir,
                job.JobKey + "_" + SanitizeFileName(Path.GetFileName(job.FileName)));
            var (dlOk, dlReason) = await _dashboard.DownloadFileAsync(job.FileUrl, tempPath, job.FileSha256, ct);
            if (!dlOk)
            {
                await ReportTerminalAsync(new JobReport
                {
                    JobKey = job.JobKey, OrderId = job.OrderId, PrinterName = job.PrinterName,
                    Status = "failed", Reason = dlReason,
                }, ct);
                continue;
            }

            var fileName = Path.GetFileName(tempPath);
            var (upOk, upReason) = await adapter.UploadFileAsync(tempPath, fileName, ct);
            if (!upOk)
            {
                Cleanup(tempPath);
                await ReportTerminalAsync(new JobReport
                {
                    JobKey = job.JobKey, OrderId = job.OrderId, PrinterName = job.PrinterName,
                    Status = "failed", Reason = upReason,
                }, ct);
                continue;
            }

            var (startOk, startReason) = await adapter.StartPrintAsync(fileName, job.JobKey, ct);
            if (!startOk)
            {
                Cleanup(tempPath);
                await ReportTerminalAsync(new JobReport
                {
                    JobKey = job.JobKey, OrderId = job.OrderId, PrinterName = job.PrinterName,
                    Status = "failed", Reason = startReason,
                }, ct);
                continue;
            }

            _active[job.PrinterName] = new ActiveJob(job, adapter, tempPath,
                DateTime.UtcNow, false, -1, DateTime.MinValue, DateTime.UtcNow);
            await _dashboard.ReportJobAsync(new JobReport
            {
                JobKey = job.JobKey, OrderId = job.OrderId, PrinterName = job.PrinterName,
                Status = "started", Reason = $"Start command forwarded to '{job.PrinterName}'. Printing is confirmed by later status polls.",
            }, ct);
            Program.Log($"Job {job.JobKey} (order {job.OrderId}) started on '{job.PrinterName}'.");
        }
    }

    private async Task MonitorActiveJobsAsync(
        IReadOnlyDictionary<string, PrinterStatus> statuses, CancellationToken ct)
    {
        foreach (var printerName in _active.Keys.ToList())
        {
            var active = _active[printerName];
            if (!statuses.TryGetValue(printerName, out var live))
                continue;

            if (live.State == "printing")
            {
                active = active with { PrintConfirmed = true, LastSeenOnline = DateTime.UtcNow };
                _active[printerName] = active;
                var pct = live.ProgressPercent ?? -1;
                if (pct >= 0 && (Math.Abs(pct - active.LastReportedPercent) >= 5 ||
                    DateTime.UtcNow - active.LastProgressReport >= TimeSpan.FromMinutes(5)))
                {
                    _active[printerName] = active with { LastReportedPercent = pct, LastProgressReport = DateTime.UtcNow };
                    await _dashboard.ReportJobAsync(new JobReport
                    {
                        JobKey = active.Job.JobKey, OrderId = active.Job.OrderId, PrinterName = printerName,
                        Status = "progress", ProgressPercent = pct,
                    }, ct);
                }
                continue;
            }

            if (live.State == "idle" && active.PrintConfirmed)
            {
                // Printer went idle after printing: the print finished.
                await FinishActiveAsync(printerName, active, "completed", "Print finished (printer idle after printing).", null, ct);
                continue;
            }

            if (live.State == "error")
            {
                var snapshot = await TrySnapshotAsync(active.Adapter, ct);
                await FinishActiveAsync(printerName, active, "failed",
                    $"Printer '{printerName}' reports an error: {live.Detail}", snapshot, ct);
                continue;
            }

            if (live.State == "offline")
            {
                if (DateTime.UtcNow - active.LastSeenOnline >= TimeSpan.FromMinutes(10))
                {
                    await FinishActiveAsync(printerName, active, "failed",
                        $"Lost contact with printer '{printerName}' for over 10 minutes during a print.", null, ct);
                }
                continue;
            }

            if (live.State == "idle" && !active.PrintConfirmed &&
                DateTime.UtcNow - active.StartedAt >= TimeSpan.FromMinutes(2))
            {
                await FinishActiveAsync(printerName, active, "failed",
                    $"Printer '{printerName}' did not start printing within 2 minutes of the start command.", null, ct);
                continue;
            }

            if (live.State != "offline")
            {
                _active[printerName] = active with { LastSeenOnline = DateTime.UtcNow };
            }
        }
    }

    private async Task<byte[]?> TrySnapshotAsync(IPrinterAdapter adapter, CancellationToken ct)
    {
        try
        {
            var bytes = await adapter.TryGetSnapshotAsync(ct);
            if (bytes is null || bytes.Length > 2_000_000) return null;
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    private async Task FinishActiveAsync(string printerName, ActiveJob active,
        string status, string reason, byte[]? snapshot, CancellationToken ct)
    {
        _active.Remove(printerName);
        Cleanup(active.TempPath);
        await ReportTerminalAsync(new JobReport
        {
            JobKey = active.Job.JobKey, OrderId = active.Job.OrderId, PrinterName = printerName,
            Status = status, Reason = reason,
            SnapshotBase64 = snapshot is null ? null : Convert.ToBase64String(snapshot),
        }, ct);
        Program.Log($"Job {active.Job.JobKey} on '{printerName}': {status} — {reason}");
    }

    private async Task ReportTerminalAsync(JobReport report, CancellationToken ct)
    {
        _terminalReported.Add(report.JobKey);
        if (_dashboard.HasKey)
            await _dashboard.ReportJobAsync(report, ct);
        else
            Program.Log($"[local] Job {report.JobKey}: {report.Status} — {report.Reason}");
    }

    private static void Cleanup(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "print-file" : name;
    }
}
