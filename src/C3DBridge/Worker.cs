// C3D Print Bridge — worker loop (the thin relay itself).
//
// Every tick the bridge does, all outbound:
//   1. Ask each configured printer for its live state.
//   2. Push that state (and the queue length) to the dashboard.
//   3. Ask the dashboard for jobs it assigned to this bridge, and
//      carry out the ones the dashboard marked "ready".
//   4. On the job-poll tick, also poll slice jobs
//      (dashboard-requested slicing, run locally) and
//      reconcile offline printers against LAN candidates
//      for IP-change detection. Farm jobs are NOT on
//      this tick: SPEED FIX (2026.1103) polls them on
//      EVERY loop tick (~5 s), so a Send/Start dispatch
//      is picked up within ~one tick instead of up to a
//      full job-poll interval later; while a farm start
//      is unconfirmed, the status poll is likewise
//      forced every tick until the printer shows
//      printing, then normal cadence resumes.
//
// What it NEVER does (thin-relay scope, owner-confirmed):
// - It never picks a printer for a job: the dashboard assigns by
//   name, and an unknown name is an error, not a substitution.
// - It never orders, prioritizes, or reschedules jobs: per printer
//   it runs at most one job, in dashboard poll order, because a
//   printer can physically print only one thing at a time.
// - It never inspects or modifies model geometry: a slice job
//   hands a file to the slicer CLI and forwards the verified
//   G-code byte-for-byte; a print job forwards the dashboard's
//   file byte-for-byte.
// - It keeps no database: state is in-memory only, plus a plain
//   log file. A restart forgets in-flight jobs (the printer keeps
//   printing; the dashboard still shows the job until it is
//   re-reported — see README limits). The only local UI is the
//   owner-approved tray icon + Configure window (v1.1.0), which
//   edits config and checks for updates; it makes no relay
//   decisions and opens no port.
// - It never logs secrets: no API keys, no access codes, no full
//   URLs (file names and job keys only).

using C3DBridge.Printers;

namespace C3DBridge;

public sealed class Worker
{
    /// <summary>Auto-update guard (2273): true while the bridge
    /// itself knows a print is running — either a printer it
    /// polled reported "printing", or it is executing/monitoring
    /// a dashboard job. Updated every worker tick from the last
    /// known statuses. It does NOT cover prints started outside
    /// the bridge (e.g. directly from a printer app) that the
    /// bridge has not polled yet — that limit is stated in the
    /// 2273 changes file, not hidden.</summary>
    public static bool AnyPrinting { get; private set; }

    private readonly BridgeConfig _cfg;
    private readonly PrinterRegistry _printers;
    private readonly DashboardClient _dashboard;
    private readonly FarmPoller _farmPoller;
    private readonly IpReconciler _ipReconciler;
    private readonly SlicePoller _slicePoller;
    private readonly LithophanePoller _lithophanePoller;
    private readonly OcrPoller _ocrPoller;
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
        // AUTO-START BUILD: FarmPoller is now the single
        // starter for farm jobs (the bridge/jobs mirror stays
        // forced 'held' and emits no 'ready' — see
        // ExecuteReadyJobsAsync, which is deliberately NOT
        // the farm path). IpReconciler and SlicePoller are
        // the IP-change detection and in-dashboard slicing
        // stages from the same build.
        _farmPoller = new FarmPoller(dashboard, printers, cfg);
        _ipReconciler = new IpReconciler(dashboard, printers);
        _slicePoller = new SlicePoller(dashboard, cfg);
        _lithophanePoller = new LithophanePoller(dashboard, cfg);
        _ocrPoller = new OcrPoller(dashboard);
    }

    /// <summary>MULTI-BRIDGE (2026.1108): the live slice state
    /// the heartbeat/status slice_machine block reports —
    /// "busy" while SlicePoller has a slice running, else
    /// "idle" — so the dashboard knows this machine is free
    /// to take the next slice job.</summary>
    private string SliceStatus() => _slicePoller.IsSlicing ? "busy" : "idle";

    public async Task RunAsync(CancellationToken ct)
    {
        Program.Log($"Bridge '{_cfg.BridgeName}' running: {_printers.All.Count} printer(s) configured, dashboard {_cfg.DashboardBaseUrl}.");
        // Non-3D printers (Oct 24, 2026): logged only. They are
        // not in _printers, so no 3D job path can ever pick one.
        if (_cfg.Non3DPrinters.Count > 0)
            Program.Log($"Non-3D printers configured (label/document only, never 3D jobs): {string.Join(", ", _cfg.Non3DPrinters)}.");
        // Camera auto-discovery (2275): once per Worker startup.
        // This is also the "when a printer is added" trigger:
        // Configure saves restart the Worker (TrayApp
        // RestartWorker), so a newly added printer is discovered
        // on its first run. It never runs on every status tick,
        // never overwrites a set URL, and never sends config or
        // credentials anywhere — see DiscoverCameraUrlsAsync.
        await DiscoverCameraUrlsAsync(ct);
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

                // SPEED FIX (2026.1103): while a farm job
                // this bridge started has not yet been seen
                // printing, force a fresh status poll on
                // this iteration instead of waiting for the
                // next status-interval tick, so the
                // dashboard learns "printing" within ~one
                // loop tick of the printer actually
                // starting. Once printing is confirmed (or
                // the job ends), HasUnconfirmedStart goes
                // false and normal cadence resumes on its
                // own; the monitor's 2-minute no-start
                // failure still bounds it, so a printer
                // that never starts cannot hold fast
                // polling open.
                if (_farmPoller.HasUnconfirmedStart())
                    lastStatus = DateTime.MinValue;

                // 1. Live printer state (each printer isolated: one
                //    printer's failure never stops the others).
                Dictionary<string, PrinterStatus> statuses = new(StringComparer.OrdinalIgnoreCase);
                if (now - lastStatus >= TimeSpan.FromSeconds(_cfg.StatusIntervalSeconds))
                {
                    lastStatus = now;
                    var tasks = _printers.All.Select(async a =>
                    {
                        try { return await a.GetStatusAsync(ct); }
                        catch (Exception ex) { return new PrinterStatus { Name = a.Name, Type = a.Type, Ip = a.Ip, Serial = a.SerialKey, State = "offline", Detail = ex.Message }; }
                    });
                    foreach (var st in await Task.WhenAll(tasks))
                        statuses[st.Name] = st;

                    var queueLength = _lastPollJobs.Count(j =>
                        string.Equals(j.SafetyVerdict, "ready", StringComparison.OrdinalIgnoreCase) &&
                        !_active.ContainsKey(j.PrinterName) &&
                        !_terminalReported.Contains(j.JobKey)) + _active.Count;
                    // AUTO-START BUILD: overlay the bridge-known
                    // light state (recorded when a lights control
                    // executes) onto the status push, so the Farm
                    // card's Light field reflects a proven
                    // execution instead of staying "Not reported".
                    // A printer with no executed lights control
                    // keeps whatever its adapter reported.
                    foreach (var name in statuses.Keys.ToList())
                    {
                        var light = _farmPoller.LightStateFor(name);
                        if (!string.IsNullOrWhiteSpace(light))
                            statuses[name] = statuses[name] with { LightState = light };
                    }
                    // BRIDGE-READS BUILD: overlay the last
                    // G-code parse per printer (same pattern as
                    // the light overlay) and the Snapmaker
                    // nozzle diameters (10-minute cached inside
                    // the adapter) onto the status push, so the
                    // dashboard card can show them with no
                    // endpoint change: PushStatusAsync already
                    // serializes the whole printer list.
                    // A printer with no parse yet keeps "" /
                    // null (unknown); a printer with no nozzle
                    // data keeps an empty map (unknown).
                    foreach (var name in statuses.Keys.ToList())
                    {
                        var parse = _farmPoller.ParseFor(name);
                        if (parse is not null && parse.ParseOk)
                        {
                            statuses[name] = statuses[name] with
                            {
                                DeclaredFileTool = parse.FirstTool,
                                DeclaredToolSequence = string.Join(",", parse.ToolsInOrder),
                                FileProfile = parse.Profile,
                                FileNozzleMm = parse.SlicedNozzleMm,
                            };
                        }
                    }
                    foreach (var adapter in _printers.All)
                    {
                        if (adapter is C3DBridge.Printers.SnapmakerAdapter snap &&
                            statuses.TryGetValue(adapter.Name, out var st))
                        {
                            var diameters = await snap.QueryNozzleDiametersAsync(ct);
                            if (diameters.Count > 0)
                                statuses[adapter.Name] = st with { NozzleDiameters = diameters };
                        }
                    }
                    // FLUIDD SLICE 1: overlay Moonraker thermals
                    // (every status tick), printer files + job
                    // history (5-minute cached inside the
                    // adapter) onto the status push, same
                    // pattern as the nozzle overlay. A failed
                    // fetch keeps the last good files/history
                    // (or null = never fetched) and an empty
                    // thermals list; the dashboard renders
                    // those as Not reported / Not available,
                    // never a guessed value.
                    foreach (var adapter in _printers.All)
                    {
                        if (adapter is C3DBridge.Printers.SnapmakerAdapter snap &&
                            statuses.TryGetValue(adapter.Name, out var st))
                        {
                            var thermals = await snap.QueryThermalsAsync(ct);
                            var files = await snap.QueryPrinterFilesAsync(ct);
                            var history = await snap.QueryJobHistoryAsync(ct);
                            statuses[adapter.Name] = st with { Thermals = thermals, PrinterFiles = files, JobHistory = history };
                        }
                    }
                    if (_dashboard.HasKey)
                        await _dashboard.PushStatusAsync(statuses.Values.ToList(), queueLength, ct, SliceStatus());
                    else
                        foreach (var st in statuses.Values)
                            Program.Log($"[local] {st.Name}: {st.State} {st.Detail}");
                    // AUTO-START BUILD: learn Moonraker
                    // hostnames for IP-change matching (local
                    // config only; once per run per printer).
                    await _ipReconciler.LearnHostnamesAsync(statuses, ct);
                }
                else
                {
                    // Reuse the last known state between status ticks.
                    statuses = _lastStatuses;
                }

                // Auto-update guard: refresh from whatever state
                // this tick knows, so the tray never has to guess.
                AnyPrinting = _active.Count > 0 ||
                    statuses.Values.Any(s => string.Equals(s.State, "printing", StringComparison.OrdinalIgnoreCase));

                // 2. Heartbeat (dashboard treats >10 min old as stale).
                if (_dashboard.HasKey && now - lastHeartbeat >= TimeSpan.FromSeconds(_cfg.HeartbeatIntervalSeconds))
                {
                    lastHeartbeat = now;
                    await _dashboard.HeartbeatAsync(ct, SliceStatus());
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
                        // AUTO-START BUILD: FarmPoller is still
                        // the SINGLE starter for farm jobs
                        // (same gates as before: dispatch=
                        // 'start' AND queued or pending_bridge_
                        // confirmation AND live idle AND bed +
                        // AMS confirmed AND verified sliced
                        // file for that printer model), but it
                        // no longer runs here: SPEED FIX
                        // (2026.1103) runs it on EVERY loop
                        // tick below, so a dispatch is picked
                        // up within ~one tick. The bridge/jobs
                        // mirror stays forced 'held' and
                        // nothing here emits 'ready' for a
                        // farm job, so a farm job can never
                        // be started twice by two paths.
                        // AUTO-START BUILD: IP-change
                        // reconciliation (throttled inside) and
                        // pending move-report retries, on the
                        // same job-poll tick.
                        await _ipReconciler.ReconcileAsync(statuses, ct);
                        await _ipReconciler.FlushMoveReportsAsync(ct);
                        // AUTO-START BUILD: in-dashboard slicing
                        // (dashboard Slice action → bridge slicer
                        // CLI → verified G-code back). One slice
                        // at a time, in the background; it never
                        // blocks status polls or heartbeats.
                        await _slicePoller.PollAsync(ct);
                        // LITHOPHANE (2026.1107): print-file
                        // generation for lithophane orders, on
                        // the same job-poll tick. One generation
                        // at a time, in the background; it never
                        // blocks status polls or heartbeats, and
                        // never starts a print.
                        await _lithophanePoller.PollAsync(ct);
                        // Bridge OCR stage: poll bridge/ocr-jobs
                        // too, on the same job-poll tick as the
                        // farm poller. OcrPoller never starts a
                        // print, never touches a printer, and
                        // only posts extracted text back — OCR
                        // jobs are paperwork only.
                        await _ocrPoller.PollAsync(ct);
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

                // SPEED FIX (2026.1103): farm jobs poll on
                // EVERY loop tick (~5 s + work time), not
                // only on the job-poll tick (default 30 s):
                // a Send/Start dispatch is picked up within
                // ~one tick instead of up to a full job-poll
                // interval later. Every start gate inside
                // FarmPoller is unchanged; only the cadence
                // moved. Farm controls ride in the same
                // PollAsync call, so a lights/home/power
                // intent is likewise picked up within ~one
                // tick. The rejected-key guard matches the
                // job-poll gate: with a rejected key, farm
                // polling stays paused (and the 10-minute
                // warning above still fires). This also
                // decouples farm starts from the bridge/jobs
                // mirror poll: a mirror failure no longer
                // blocks a farm start, since farm/jobs has
                // its own failure handling inside PollAsync.
                if (_dashboard.HasKey && !_dashboard.KeyRejected)
                    await _farmPoller.PollAsync(statuses, ct);

                await MonitorActiveJobsAsync(statuses, ct);
                // Snapshot-route latch expires after 10 minutes
                // (Oct 30, 2026: a deploy window's 404 latched it
                // forever until a bridge restart). After cooldown,
                // retry once; a fresh 404 re-latches from now.
                if (_bedSnapshotEndpointMissing && DateTime.UtcNow - _bedSnapshotEndpointMissingSince >= TimeSpan.FromMinutes(10))
                    _bedSnapshotEndpointMissing = false;
                if (_dashboard.HasKey && !_dashboard.KeyRejected && !_bedSnapshotEndpointMissing)
                    await CaptureBedSnapshotsAsync(statuses, ct);
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
    // Bed View (2274): last bed-snapshot attempt per printer, and
    // whether the dashboard Bed View route is missing (404).
    // Snapshots are idle-printers only, at most once a minute
    // per printer: a bed picture is only useful as bed-vacant
    // evidence when the printer is idle, and a printing
    // printer's bed is occupied by definition.
    private readonly Dictionary<string, DateTime> _lastBedSnapshotAttempt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _cameraRefreshServed = new(StringComparer.OrdinalIgnoreCase);
    private bool _bedSnapshotEndpointMissing;
    private DateTime _bedSnapshotEndpointMissingSince = DateTime.MinValue;

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

            var (startOk, startReason) = await adapter.StartPrintAsync(fileName, job.JobKey, ct, tempPath);
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

    /// <summary>Camera auto-discovery (2275 snapshot,
    /// 2276 feed). For each adapter, once per Worker
    /// startup, ask it for a discovered snapshot URL and
    /// a discovered feed URL. A returned URL is persisted ONLY to the
    /// local bridge-config.json on this PC (as that printer's
    /// snapshot_url, with snapshot_auto_discovered=true), and
    /// only when the freshly loaded config still has no URL
    /// for that printer — an owner-set URL is never
    /// overwritten, including by a race with a Configure save.
    /// No config, credential, or URL is sent to the dashboard
    /// or logged: only the snapshot JPEG itself (with its
    /// source label) goes to the dashboard, as in 2274, and
    /// only the printer name and source label are logged.
    /// A discovery miss leaves the URL blank, so no snapshot
    /// is sent — never a fake image.</summary>
    private async Task DiscoverCameraUrlsAsync(CancellationToken ct)
    {
        foreach (var adapter in _printers.All)
        {
            // Snapshot (2275). A miss here must NOT skip
            // feed discovery below: a printer can have a
            // feed even when its snapshot check missed, and
            // an owner-set snapshot must not block feed
            // discovery either.
            string? url = null;
            try { url = await adapter.TryAutoDiscoverSnapshotUrlAsync(ct); }
            catch { url = null; }
            if (!string.IsNullOrWhiteSpace(url))
            {
                var (loaded, error) = Config.Load();
                if (loaded is null)
                {
                    Program.Log($"Camera auto-discovery for '{adapter.Name}': discovered a snapshot URL but could not reload the local config to save it ({error}). It will be used for this run only.");
                }
                else
                {
                    var idx = loaded.Printers.FindIndex(p => string.Equals(p.Name, adapter.Name, StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0 && string.IsNullOrWhiteSpace(loaded.Printers[idx].SnapshotUrl))
                    {
                        var printers = loaded.Printers.ToList();
                        printers[idx] = printers[idx] with { SnapshotUrl = url, SnapshotAutoDiscovered = true };
                        try
                        {
                            Config.Save(loaded with { Printers = printers });
                            Program.Log($"Camera auto-discovery for '{adapter.Name}': snapshot URL saved to the local bridge config only (source: {adapter.SnapshotSource}).");
                        }
                        catch (Exception ex)
                        {
                            Program.Log($"Camera auto-discovery for '{adapter.Name}': discovered a snapshot URL but could not save it locally ({ex.Message}). It will be used for this run only.");
                        }
                    }
                }
            }

            // Feed (2276): same once-per-startup rule, same
            // local-only persistence, same never-overwrite
            // rule. Only the feed TYPE is ever logged —
            // never a URL, never a credential. An RTSP/other
            // feed returns no URL (FeedUrl is null) but still
            // reports its type, so Bed View knows to keep
            // showing snapshots instead of a broken feed.
            // For Snapmaker, snapshot discovery above already
            // ran the shared webcam-list pass, so this call
            // simply returns the feed found there.
            string? feedUrl = null;
            try { feedUrl = await adapter.TryAutoDiscoverFeedAsync(ct); }
            catch { feedUrl = null; }
            if (!string.IsNullOrWhiteSpace(feedUrl))
            {
                var (loaded2, error2) = Config.Load();
                if (loaded2 is null)
                {
                    Program.Log($"Camera feed discovery for '{adapter.Name}': discovered a feed but could not reload the local config to save it ({error2}). It will be used for this run only.");
                }
                else
                {
                    var idx2 = loaded2.Printers.FindIndex(p => string.Equals(p.Name, adapter.Name, StringComparison.OrdinalIgnoreCase));
                    if (idx2 >= 0 && string.IsNullOrWhiteSpace(loaded2.Printers[idx2].StreamUrl))
                    {
                        var printers2 = loaded2.Printers.ToList();
                        printers2[idx2] = printers2[idx2] with { StreamUrl = feedUrl, StreamAutoDiscovered = true };
                        try
                        {
                            Config.Save(loaded2 with { Printers = printers2 });
                            Program.Log($"Camera feed discovery for '{adapter.Name}': feed saved to the local bridge config only (type: {adapter.FeedType}).");
                        }
                        catch (Exception ex)
                        {
                            Program.Log($"Camera feed discovery for '{adapter.Name}': discovered a feed but could not save it locally ({ex.Message}). It will be used for this run only.");
                        }
                    }
                }
            }
            else if (adapter.FeedUrl is null && adapter.FeedType != "none")
            {
                Program.Log($"Camera feed discovery for '{adapter.Name}': feed type is {adapter.FeedType}; no embeddable feed URL, snapshots remain the fallback.");
            }
        }
    }

    /// <summary>Bed View capture (2274, source labels 2275). For each adapter
    /// whose printer is currently idle, at most once a minute,
    /// try the adapter's snapshot URL (owner-set, or
    /// auto-discovered in 2275) and push the
    /// JPEG to the dashboard bed-snapshot route. A null return
    /// (no snapshot URL, unreachable URL, non-JPEG, too large)
    /// means no snapshot is sent — never a placeholder. A
    /// snapshot arriving at the dashboard NEVER confirms a bed
    /// on its own; a human must confirm from the image, and
    /// only a fresh snapshot on an idle printer counts there.
    /// No credentials are in the payload.</summary>
    private async Task CaptureBedSnapshotsAsync(
        IReadOnlyDictionary<string, PrinterStatus> statuses, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        // 0.9.6: pending admin Refresh requests (best effort;
        // null on any failure means normal cadence only).
        Dictionary<string, long>? refreshRequests = null;
        try { refreshRequests = await _dashboard.GetCameraRefreshRequestsAsync(ct); } catch { refreshRequests = null; }
        foreach (var adapter in _printers.All)
        {
            if (!statuses.TryGetValue(adapter.Name, out var live) ||
                !string.Equals(live.State, "idle", StringComparison.OrdinalIgnoreCase))
                continue;
            // A fresh (unserved) refresh request for this
            // printer bypasses the once-a-minute throttle
            // exactly once; normal cadence applies otherwise.
            var refreshRequested = false;
            if (refreshRequests is not null &&
                refreshRequests.TryGetValue(adapter.Ip.ToLowerInvariant(), out var reqUnix) &&
                (!_cameraRefreshServed.TryGetValue(adapter.Ip.ToLowerInvariant(), out var servedUnix) || reqUnix > servedUnix))
            {
                refreshRequested = true;
                _cameraRefreshServed[adapter.Ip.ToLowerInvariant()] = reqUnix;
            }
            if (!refreshRequested &&
                _lastBedSnapshotAttempt.TryGetValue(adapter.Name, out var last) &&
                now - last < TimeSpan.FromMinutes(1))
                continue;
            _lastBedSnapshotAttempt[adapter.Name] = now;
            // GATE-LIGHT BUILD (2026.1105): for Bambu printers,
            // make sure the chamber light is verified on before
            // grabbing the frame — a dark frame is not bed
            // evidence. The ensure is throttled inside the
            // adapter (at most once per 10 minutes while not
            // verified on). A failed ensure logs inside the
            // adapter and the grab still runs, so a dark frame
            // is visible in the log instead of no frame at all.
            if (adapter is BambuAdapter bambu)
            {
                var lit = await bambu.EnsureChamberLightOnAsync(ct);
                if (!lit)
                    Program.Log($"Bed View: chamber light on '{adapter.Name}' is not verified on; grabbing the snapshot anyway (frame may be dark).");
            }
            byte[]? bytes;
            try { bytes = await adapter.TryGetSnapshotAsync(ct); }
            catch { continue; }
            if (bytes is null) continue;
            var (ok, status) = await _dashboard.PushBedSnapshotAsync(new CameraSnapshot
            {
                PrinterName = adapter.Name,
                PrinterIp = adapter.Ip,
                PrinterType = adapter.Type,
                CapturedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                SnapshotBase64 = Convert.ToBase64String(bytes),
                SnapshotSource = adapter.SnapshotSource,
                FeedType = adapter.FeedType,
                FeedUrl = adapter.FeedUrl,
                CameraAvailable = live.CameraAvailable,
            }, ct);
            if (!ok && status == System.Net.HttpStatusCode.NotFound)
            {
                _bedSnapshotEndpointMissing = true;
                _bedSnapshotEndpointMissingSince = DateTime.UtcNow;
                Program.Log("Bed View: dashboard bed-snapshot route missing (usually a deploy window). Snapshots paused; retry in 10 minutes.");
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
