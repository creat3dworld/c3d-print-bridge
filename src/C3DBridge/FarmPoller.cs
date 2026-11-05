// C3D Print Bridge — farm job poller (AUTO-START BUILD,
// 2026.1029 farm-auto).
//
// SECOND STAGE: this poller IS the single print starter for
// farm jobs. The dashboard Print Farm tab serves farm/jobs
// with a dispatch map (start / pause / stop) and a file
// reference per job, and the dashboard Start already gates
// on bed-confirmed + AMS-confirmed + printer connected
// before it dispatches. This poller is the bridge side:
//
//  - Poll GET /wp-json/c3d-shipping/v1/farm/jobs.
//  - For dispatch=start: resolve the printer by name, then
//    by IP (never a substitute), require it idle right now,
//    require both dashboard safety flags, require a live AMS
//    contradiction check (only when the printer actually
//    reports AMS data), require a sliced G-code file whose
//    recorded model matches the target printer (known
//    mismatch = hold), download it over the authenticated
//    farm/file route with a SHA-256 check, upload it to
//    the printer, and forward the start — then report
//    'started' (the dashboard flips to 'printing' only on
//    that report) or 'failed' with the reason. A started
//    job is then monitored: printing → idle means finished,
//    error means failed, 10 minutes offline means failed,
//    never printing within 2 minutes means failed.
//  - For dispatch=pause/stop: execute via the brand
//    adapter (Moonraker pause/cancel; Bambu MQTT pause/
//    stop) and report 'paused' / 'finished', or
//    'control_failed' with the command named so the
//    dashboard keeps the dispatch pending for retry.
//  - For controls (lights/home/power): lights execute on
//    Snapmaker (SET_LED) and Bambu (ledctrl); home/power
//    execution is GATED on the bridge's home_power_allowed
//    flag (owner A7 decision, default off) — a gated
//    intent is reported as gated, never executed, never
//    silently dropped. Power has no verified execution
//    path on any brand even when the flag is on.
//
// SINGLE STARTER RULE: this poller is the ONLY farm-job
// starter. GET bridge/jobs stays a read-only mirror whose
// safety_verdict is forced 'held' on purpose, and the
// Worker path never sees a 'ready' farm job — so no job
// can ever start twice. Nothing here emits 'ready'.
//
// What this stage NEVER does:
//  - Never uploads a 3MF, an unsliced file, or a file
//    sliced for another printer model.
//  - Never starts on an unconfirmed gate, a busy printer,
//    or a file that failed its SHA-256 check.
//  - Never substitutes another printer, never guesses a
//    file from a title, never slices anything.

using C3DBridge.Printers;

namespace C3DBridge;

public sealed class FarmPoller
{
    private readonly DashboardClient _dashboard;
    private readonly PrinterRegistry _printers;
    private readonly BridgeConfig _config;
    private readonly HashSet<string> _loggedHold = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loggedDispatch = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _startedFarm = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FarmActive> _activeFarm = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lightStates = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>BRIDGE-READS BUILD: last streaming G-code parse
    /// per printer name, for the Worker status overlay (same
    /// pattern as LightStateFor). Last-parse semantics: it
    /// describes the most recent file parsed for a job on
    /// that printer, not a live printer state.</summary>
    private readonly Dictionary<string, GcodeDeclaration> _lastParse = new(StringComparer.OrdinalIgnoreCase);

    public GcodeDeclaration? ParseFor(string printerName) =>
        _lastParse.TryGetValue(printerName, out var d) ? d : null;

    /// <summary>SPEED FIX (2026.1103): true while a farm
    /// job this bridge started has not yet been seen
    /// printing by the monitor. The Worker forces a fresh
    /// status poll on every loop tick while this is true,
    /// so "printing" is confirmed within ~one tick of the
    /// printer actually starting instead of on the next
    /// slow status tick. It goes false as soon as the
    /// monitor sees printing (or the job ends), so normal
    /// status cadence resumes on its own; the 2-minute
    /// no-start failure still bounds it, so a printer
    /// that never starts cannot hold fast polling open.
    /// Read-only: it never starts, stops, or changes a
    /// job.</summary>
    public bool HasUnconfirmedStart() =>
        _activeFarm.Values.Any(a => !a.PrintConfirmed);

    private sealed record FarmActive(
        FarmJob Job,
        IPrinterAdapter Adapter,
        DateTime StartedAt,
        bool PrintConfirmed,
        DateTime LastSeenOnline);

    public FarmPoller(DashboardClient dashboard, PrinterRegistry printers, BridgeConfig config)
    {
        _dashboard = dashboard;
        _printers = printers;
        _config = config;
    }

    /// <summary>Bridge-known light state for a printer
    /// ("on" / "off" / "unknown"), set only from a lights
    /// control this bridge actually executed. The Worker
    /// overlays this onto status pushes so the dashboard
    /// card stops reading "Not reported" after a proven
    /// lights execution.</summary>
    public string LightStateFor(string printerName) =>
        _lightStates.TryGetValue(printerName, out var s) ? s : "unknown";

    public async Task PollAsync(IReadOnlyDictionary<string, PrinterStatus> statuses, CancellationToken ct)
    {
        if (!_dashboard.HasKey)
            return;
        await PollControlsAsync(ct);
        var (jobs, problem) = await _dashboard.PollFarmJobsAsync(ct);
        if (jobs is null)
        {
            if (!string.IsNullOrWhiteSpace(problem))
                Program.Log($"[farm] Poll failed: {problem}");
            return;
        }
        foreach (var job in jobs)
        {
            var dispatch = (job.Dispatch ?? "").Trim().ToLowerInvariant();
            if (dispatch == "start")
            {
                await TryStartFarmJobAsync(job, statuses, ct);
            }
            else if (dispatch == "pause")
            {
                await TryPauseFarmJobAsync(job, statuses, ct);
            }
            else if (dispatch == "stop")
            {
                await TryStopFarmJobAsync(job, statuses, ct);
            }
            else if (!string.IsNullOrWhiteSpace(dispatch))
            {
                // Unknown or informational dispatch: log once
                // per job+dispatch, never every poll.
                if (_loggedDispatch.Add(job.JobId + ":" + dispatch))
                    Program.Log($"[farm] Dispatch '{dispatch}' for job {job.JobId} ('{job.Title}') is not executed by this stage; job left untouched.");
            }
        }
        await MonitorFarmActiveAsync(statuses, ct);
    }

    private async Task TryStartFarmJobAsync(FarmJob job, IReadOnlyDictionary<string, PrinterStatus> statuses, CancellationToken ct)
    {
        // A job already handed to a printer (this run) is
        // monitored, never re-started — even if its
        // 'started' report POST failed on the last poll.
        if (_startedFarm.Contains(job.JobId))
            return;
        // Only queued / pending jobs start. A printing job
        // still carries its start dispatch until its
        // 'finished' report clears it, so the status guard
        // is what keeps a running print from re-starting.
        var status = (job.Status ?? "").Trim().ToLowerInvariant();
        if (status != "queued" && status != "pending_bridge_confirmation")
            return;

        var adapter = _printers.Find(job.Printer);
        adapter ??= _printers.All.FirstOrDefault(a =>
            string.Equals(a.Ip, job.PrinterIp, StringComparison.OrdinalIgnoreCase));
        if (adapter is null)
        {
            await ReportHoldAsync(job.JobId, "no-printer",
                $"[farm] Job {job.JobId} dispatch=start, but no printer '{job.Printer}' / {job.PrinterIp} is configured on this bridge. No substitution, job left queued.", ct);
            return;
        }
        ClearHold(job.JobId, "no-printer");
        if (!statuses.TryGetValue(adapter.Name, out var live) ||
            !string.Equals(live.State, "idle", StringComparison.OrdinalIgnoreCase))
        {
            // Busy, offline, or unknown: leave queued, retry later.
            return;
        }
        if (!job.BedConfirmed || !job.AmsConfirmed)
        {
            await ReportHoldAsync(job.JobId, "gates",
                $"[farm] Job {job.JobId} dispatch=start, but dashboard safety flags are not both confirmed (bed={job.BedConfirmed}, ams={job.AmsConfirmed}). Job left queued; bridge never starts on an unconfirmed gate.", ct);
            return;
        }
        ClearHold(job.JobId, "gates");
        // Live AMS contradiction check (mirror of Safety):
        // only when the printer actually reports AMS data.
        // A name-vs-hex color pair is incomparable, not a
        // contradiction — the dashboard's verdict stands.
        if (live.Ams.Known)
        {
            if (!string.IsNullOrWhiteSpace(job.Material) && !string.IsNullOrWhiteSpace(live.Ams.Material) &&
                !string.Equals(job.Material.Trim(), live.Ams.Material.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                await ReportHoldAsync(job.JobId, "ams-material",
                    $"[farm] Job {job.JobId}: live AMS material changed since dispatch (printer reports '{live.Ams.Material}', job needs '{job.Material}'). Job left queued.", ct);
                return;
            }
            if (!string.IsNullOrWhiteSpace(job.Color) && !string.IsNullOrWhiteSpace(live.Ams.Color) &&
                Safety.ColorsContradict(job.Color, live.Ams.Color))
            {
                await ReportHoldAsync(job.JobId, "ams-color",
                    $"[farm] Job {job.JobId}: live AMS color changed since dispatch (printer reports '{live.Ams.Color}', job needs '{job.Color}'). Job left queued.", ct);
                return;
            }
        }
        ClearHold(job.JobId, "ams-material");
        ClearHold(job.JobId, "ams-color");
        // File gates: a missing, unsliced, or wrong-kind
        // file is a hold with a reason, never a failed
        // print — the operator fixes the file and the next
        // poll picks the job up.
        if (job.FileMissing || string.IsNullOrWhiteSpace(job.FileUrl))
        {
            await ReportHoldAsync(job.JobId, "no-file",
                $"[farm] Job {job.JobId} ('{job.Title}') is dispatched and its printer is idle, but the farm job carries no print file yet. Job left queued; never guessed.", ct);
            return;
        }
        ClearHold(job.JobId, "no-file");
        if (job.FileNeedsSlicing || !string.Equals(job.FileKind, "sliced", StringComparison.OrdinalIgnoreCase))
        {
            await ReportHoldAsync(job.JobId, "unsliced",
                $"[farm] Job {job.JobId} ('{job.Title}') carries an unsliced file (kind '{job.FileKind}'). Slice it for the target printer first; job left queued.", ct);
            return;
        }
        ClearHold(job.JobId, "unsliced");
        var fileName = SanitizeFileName(job.FileName ?? "");
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = job.JobId + ".gcode";
        // WordPress/Media dedup renames a stored Bambu plate to
        // ".gcode-<digits>.3mf" (and defensively ".gcode_<digits>.3mf");
        // repair it to the canonical ".gcode.3mf" before the plate
        // gate, so the job is not refused as "not a G-code file". The
        // repaired name is then used for the temp file, the FTPS
        // destination, the StartPrint URL, and file/subtask_name.
        if (adapter is BambuAdapter)
        {
            var repairedFileName = BambuAdapter.RepairPlateFileName(fileName);
            if (!string.Equals(repairedFileName, fileName, StringComparison.Ordinal))
            {
                Program.Log($"[farm] Plate filename repaired '{fileName}' -> '{repairedFileName}' for job {job.JobId}.");
                fileName = repairedFileName;
            }
        }
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        // 2026.1102.2214-send-fallback — Bambu plates: a
        // .gcode.3mf plate is the Bambu-native sliced file
        // (FTPS upload to /cache + project_file), so it
        // passes this gate for Bambu adapters only.
        // Moonraker print/start cannot print a 3MF, so the
        // refusal below stands for every other adapter.
        var isBambuPlate = adapter is BambuAdapter &&
            (fileName.EndsWith(".gcode.3mf", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".gcode_.3mf", StringComparison.OrdinalIgnoreCase));
        if (!isBambuPlate && ext != ".gcode" && ext != ".gco")
        {
            // Defense in depth: even a "sliced" record must
            // name a G-code file. A 3MF here is refused —
            // Moonraker print/start cannot print it.
            await ReportHoldAsync(job.JobId, "not-gcode",
                $"[farm] Job {job.JobId} ('{job.Title}') resolves to '{fileName}', not a G-code file. A 3MF is never uploaded as a print file; job left queued.", ct);
            return;
        }
        ClearHold(job.JobId, "not-gcode");
        // Sliced-for gate (mirror of the dashboard rule):
        // normalize both sides the same way (lowercase,
        // strip brand words, strip non-alphanumerics); a
        // known mismatch on both sides holds the job.
        var sfModel = NormPrinterModel(job.SlicedFor);
        var printerModel = NormPrinterModel(adapter.Name);
        if (sfModel != "" && printerModel != "" &&
            !printerModel.Contains(sfModel) && !sfModel.Contains(printerModel))
        {
            await ReportHoldAsync(job.JobId, "sliced-for",
                $"[farm] Job {job.JobId} ('{job.Title}') was sliced for '{job.SlicedFor}', but the target printer is '{adapter.Name}'. A sliced file is only sent to a matching printer; job left queued.", ct);
            return;
        }
        ClearHold(job.JobId, "sliced-for");

        // Carry the job out: download (verified) → upload → start.
        var tempPath = Path.Combine(Config.TempDir, job.JobId + "_" + fileName);
        var (dlOk, dlReason) = await _dashboard.DownloadFileAsync(job.FileUrl, tempPath, job.FileSha256, ct);
        if (!dlOk)
        {
            // Transient (network/dashboard): leave queued
            // and retry on a later poll, never report a
            // failed print for a download problem.
            await ReportHoldAsync(job.JobId, "download",
                $"[farm] Job {job.JobId} file download failed ({dlReason}). Job left queued for retry.", ct);
            Cleanup(tempPath);
            return;
        }
        ClearHold(job.JobId, "download");
        // BRIDGE-READS BUILD: streaming parse of the verified
        // download, BEFORE upload. The parse never alters the
        // file and never starts a print. Parse failure is a
        // HOLD with reason ("file unreadable"), never a failed
        // print and never a guessed declaration. The parse is
        // reported to the dashboard best-effort first, so the
        // dashboard stores the declaration even when a gate
        // below holds the job; a failed report never holds
        // or fails the job (an older dashboard simply rejects
        // the unknown status and stores nothing).
        // 2026.1102.2214-send-fallback — a Bambu plate is a
        // ZIP, not G-code text, so there is no G-code
        // declaration to parse from it; its tool/nozzle
        // gates do not apply. The parse runs for
        // .gcode/.gco only, and a plate keeps an empty
        // declaration (FirstTool "" maps to no tool, so
        // the tool gate below cannot hold it).
        var decl = new GcodeDeclaration { ParseOk = true };
        if (!isBambuPlate)
        {
            decl = await GcodeFileParser.ParseAsync(tempPath, ct);
            if (decl.ParseOk)
            {
                _lastParse[adapter.Name] = decl;
                await _dashboard.ReportFileParseAsync(job.JobId, decl, ct);
            }
            if (!decl.ParseOk)
            {
                await ReportHoldAsync(job.JobId, "file-unreadable",
                    $"[farm] Job {job.JobId} ('{job.Title}') file downloaded and SHA-verified, but the G-code could not be read ({decl.ParseError}). Job left queued; never guessed.", ct);
                Cleanup(tempPath);
                return;
            }
        }
        ClearHold(job.JobId, "file-unreadable");
        // GATE MIRROR (same hold pattern as the live AMS-color
        // hold): parsed declared tool known AND printer-side
        // tool known AND they differ -> hold with reason.
        // Printer-side tool comes from the job's ams_slot via
        // SlotToToolIndex; unmappable slot labels ("AMS A2",
        // blank) and tool-less files mean unknown -> no hold.
        // The bare-integer -> 1-based mapping is ASSUMED (see
        // SlotToToolIndex): a wrong assumption can only hold
        // a job, never mis-start one, but confirm the
        // numbering on the first live run.
        var declaredIdx = GcodeFileParser.ToolToIndex(decl.FirstTool);
        var slotIdx = GcodeFileParser.SlotToToolIndex(job.AmsSlot);
        if (declaredIdx is not null && slotIdx is not null && declaredIdx.Value != slotIdx.Value)
        {
            await ReportHoldAsync(job.JobId, "file-tool",
                $"[farm] Job {job.JobId} ('{job.Title}') file declares tool T{declaredIdx.Value}, but the job's slot maps to tool T{slotIdx.Value}. A file is never sent to a mismatched tool; re-slice for the loaded tool or correct the slot. Job left queued.", ct);
            Cleanup(tempPath);
            return;
        }
        ClearHold(job.JobId, "file-tool");
        var (upOk, upReason) = await adapter.UploadFileAsync(tempPath, fileName, ct);
        if (!upOk)
        {
            Cleanup(tempPath);
            Program.Log($"[farm] Job {job.JobId} upload to '{adapter.Name}' failed: {upReason}");
            await _dashboard.ReportFarmJobAsync(job.JobId, "failed", upReason, null, ct);
            return;
        }
        var (startOk, startReason) = await adapter.StartPrintAsync(fileName, job.JobId, ct);
        if (!startOk)
        {
            Cleanup(tempPath);
            Program.Log($"[farm] Job {job.JobId} start on '{adapter.Name}' failed: {startReason}");
            await _dashboard.ReportFarmJobAsync(job.JobId, "failed", startReason, null, ct);
            return;
        }
        Cleanup(tempPath);
        _startedFarm.Add(job.JobId);
        _activeFarm[adapter.Name] = new FarmActive(job, adapter, DateTime.UtcNow, false, DateTime.UtcNow);
        var reported = await _dashboard.ReportFarmJobAsync(job.JobId, "started",
            $"Start command forwarded to '{adapter.Name}'. Printing is confirmed by later status polls.", null, ct);
        Program.Log(reported
            ? $"[farm] Job {job.JobId} ('{job.Title}') started on '{adapter.Name}'."
            : $"[farm] Job {job.JobId} ('{job.Title}') start command sent to '{adapter.Name}', but the 'started' report did not reach the dashboard — the job is monitored here and its finish will still be reported.");
    }

    private async Task TryPauseFarmJobAsync(FarmJob job, IReadOnlyDictionary<string, PrinterStatus> statuses, CancellationToken ct)
    {
        // The dashboard dispatches pause only for printing
        // jobs; anything else means the state already moved
        // on, so there is nothing to pause.
        if (!string.Equals((job.Status ?? "").Trim(), "printing", StringComparison.OrdinalIgnoreCase))
            return;
        var adapter = ResolveAdapter(job);
        if (adapter is null)
        {
            Program.Log($"[farm] Pause for job {job.JobId}: no printer '{job.Printer}' / {job.PrinterIp} on this bridge. No substitution.");
            return;
        }
        if (!statuses.TryGetValue(adapter.Name, out var live) ||
            !string.Equals(live.State, "printing", StringComparison.OrdinalIgnoreCase))
        {
            // Not printing right now: leave the dispatch for
            // a later poll (or for the monitor to resolve).
            return;
        }
        var (ok, reason) = adapter switch
        {
            SnapmakerAdapter snap => await snap.PausePrintAsync(ct),
            BambuAdapter bambu => await bambu.PausePrintAsync(ct),
            _ => (false, $"Pause is not supported for printer type '{adapter.Type}'."),
        };
        if (ok)
        {
            Program.Log($"[farm] Job {job.JobId} paused on '{adapter.Name}'.");
            await _dashboard.ReportFarmJobAsync(job.JobId, "paused", "Bridge confirmed the printer paused.", null, ct);
        }
        else
        {
            Program.Log($"[farm] Job {job.JobId} pause on '{adapter.Name}' failed: {reason}");
            await _dashboard.ReportFarmControlAsync(job.JobId, "control_failed", reason, "pause", ct);
        }
    }

    private async Task TryStopFarmJobAsync(FarmJob job, IReadOnlyDictionary<string, PrinterStatus> statuses, CancellationToken ct)
    {
        // The dashboard dispatches stop only for printing or
        // paused jobs; anything else means the state already
        // moved on.
        var st = (job.Status ?? "").Trim().ToLowerInvariant();
        if (st != "printing" && st != "paused")
            return;
        var adapter = ResolveAdapter(job);
        if (adapter is null)
        {
            Program.Log($"[farm] Stop for job {job.JobId}: no printer '{job.Printer}' / {job.PrinterIp} on this bridge. No substitution.");
            return;
        }
        if (!statuses.TryGetValue(adapter.Name, out var live) ||
            (!string.Equals(live.State, "printing", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(live.State, "paused", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        var (ok, reason) = adapter switch
        {
            SnapmakerAdapter snap => await snap.CancelPrintAsync(ct),
            BambuAdapter bambu => await bambu.CancelPrintAsync(ct),
            _ => (false, $"Stop is not supported for printer type '{adapter.Type}'."),
        };
        if (ok)
        {
            // The print is stopped by bridge command: drop
            // the monitor entry now, so the monitor does not
            // report a second 'finished' when the printer
            // goes idle. A re-dispatched start for this job
            // id is also unblocked (the status guard still
            // blocks any start while the job reads printing).
            _activeFarm.Remove(adapter.Name);
            _startedFarm.Remove(job.JobId);
            Program.Log($"[farm] Job {job.JobId} stopped on '{adapter.Name}'.");
            await _dashboard.ReportFarmJobAsync(job.JobId, "finished", "Bridge confirmed the print stopped.", null, ct);
        }
        else
        {
            Program.Log($"[farm] Job {job.JobId} stop on '{adapter.Name}' failed: {reason}");
            await _dashboard.ReportFarmControlAsync(job.JobId, "control_failed", reason, "stop", ct);
        }
    }

    private async Task MonitorFarmActiveAsync(IReadOnlyDictionary<string, PrinterStatus> statuses, CancellationToken ct)
    {
        foreach (var printerName in _activeFarm.Keys.ToList())
        {
            var active = _activeFarm[printerName];
            if (!statuses.TryGetValue(printerName, out var live))
                continue;
            if (live.State == "printing")
            {
                _activeFarm[printerName] = active with { PrintConfirmed = true, LastSeenOnline = DateTime.UtcNow };
                continue;
            }
            if (live.State == "idle" && active.PrintConfirmed)
            {
                _activeFarm.Remove(printerName);
                _startedFarm.Remove(active.Job.JobId);
                Program.Log($"[farm] Job {active.Job.JobId} on '{printerName}': finished — printer idle after printing.");
                await _dashboard.ReportFarmJobAsync(active.Job.JobId, "finished", "Print finished (printer idle after printing).", null, ct);
                continue;
            }
            if (live.State == "error")
            {
                _activeFarm.Remove(printerName);
                _startedFarm.Remove(active.Job.JobId);
                var reason = $"Printer '{printerName}' reports an error" +
                    (string.IsNullOrWhiteSpace(live.Detail) ? "." : $": {live.Detail}");
                Program.Log($"[farm] Job {active.Job.JobId} on '{printerName}': failed — {reason}");
                await _dashboard.ReportFarmJobAsync(active.Job.JobId, "failed", reason, null, ct);
                continue;
            }
            if (live.State == "offline")
            {
                if (DateTime.UtcNow - active.LastSeenOnline >= TimeSpan.FromMinutes(10))
                {
                    _activeFarm.Remove(printerName);
                    _startedFarm.Remove(active.Job.JobId);
                    Program.Log($"[farm] Job {active.Job.JobId} on '{printerName}': failed — lost contact for over 10 minutes during a print.");
                    await _dashboard.ReportFarmJobAsync(active.Job.JobId, "failed", $"Lost contact with printer '{printerName}' for over 10 minutes during a print.", null, ct);
                }
                continue;
            }
            if (live.State == "idle" && !active.PrintConfirmed &&
                DateTime.UtcNow - active.StartedAt >= TimeSpan.FromMinutes(2))
            {
                _activeFarm.Remove(printerName);
                _startedFarm.Remove(active.Job.JobId);
                Program.Log($"[farm] Job {active.Job.JobId} on '{printerName}': failed — printer did not start printing within 2 minutes of the start command.");
                await _dashboard.ReportFarmJobAsync(active.Job.JobId, "failed", $"Printer '{printerName}' did not start printing within 2 minutes of the start command.", null, ct);
                continue;
            }
            if (live.State != "offline")
                _activeFarm[printerName] = active with { LastSeenOnline = DateTime.UtcNow };
        }
    }

    /// <summary>Farm controls: lights execute (Snapmaker
    /// SET_LED, Bambu ledctrl); home/power are GATED on
    /// the bridge's home_power_allowed flag (owner A7
    /// decision, default off) and reported as gated, never
    /// executed, until that decision is made. Power has no
    /// verified execution path on any brand even when the
    /// flag is on. Every outcome is reported back so the
    /// dashboard can clear the intent and show the result
    /// — nothing is silently dropped, nothing is executed
    /// on a guess.</summary>
    private async Task PollControlsAsync(CancellationToken ct)
    {
        var (controls, problem) = await _dashboard.PollFarmControlsAsync(ct);
        if (controls is null)
        {
            if (!string.IsNullOrWhiteSpace(problem))
                Program.Log($"[farm] Control poll failed: {problem}");
            return;
        }
        foreach (var control in controls)
        {
            var adapter = _printers.Find(control.PrinterName);
            adapter ??= _printers.All.FirstOrDefault(a =>
                string.Equals(a.Ip, control.PrinterIp, StringComparison.OrdinalIgnoreCase));
            if (adapter is null)
            {
                Program.Log($"[farm] Control '{control.Command}' for '{control.PrinterName}' / {control.PrinterIp} has no matching printer on this bridge. No substitution; reported as failed.");
                await _dashboard.ReportControlAsync(control.PrinterIp, control.Command, false, "No matching printer configured on this bridge.", ct);
                continue;
            }
            var command = control.Command.Trim().ToLowerInvariant();
            if (command == "lights")
            {
                var (ledOk, ledReason, ledDetail) = adapter switch
                {
                    SnapmakerAdapter snap => await ExecLightsAsync(snap, ct),
                    BambuAdapter bambu => await ExecBambuLightsAsync(bambu, ct),
                    _ => (false, $"Light control is not supported for printer type '{adapter.Type}'.", ""),
                };
                if (ledOk)
                    _lightStates[adapter.Name] = "on";
                Program.Log(ledOk
                    ? $"[farm] Lights ON sent to {adapter.Name}."
                    : $"[farm] Lights ON to {adapter.Name} failed: {ledReason}");
                await _dashboard.ReportControlAsync(control.PrinterIp, control.Command, ledOk,
                    ledOk ? ledDetail : ledReason, ct);
                continue;
            }
            if (command == "home" || command == "power")
            {
                if (!_config.HomePowerAllowed)
                {
                    var gated = $"{char.ToUpper(command[0]) + command[1..]} execution is gated on the owner's undecided safety decision (A7): the bridge will not {command} a printer until the owner decides when {command}ing is allowed. This is a gate, not a missing feature.";
                    Program.Log($"[farm] Control '{command}' for {adapter.Name} not executed: {gated}");
                    await _dashboard.ReportControlAsync(control.PrinterIp, control.Command, false, gated, ct);
                    continue;
                }
                if (command == "power")
                {
                    const string noPower = "Power has no verified execution path on any brand: powering a printer needs a smart-plug decision that has not been made. Not executed.";
                    Program.Log($"[farm] Control 'power' for {adapter.Name} not executed: {noPower}");
                    await _dashboard.ReportControlAsync(control.PrinterIp, control.Command, false, noPower, ct);
                    continue;
                }
                // Home, flag on: verified paths only.
                var (homeOk, homeReason) = adapter switch
                {
                    SnapmakerAdapter snap => await snap.HomeAsync(ct),
                    BambuAdapter bambu => await bambu.HomeAsync(ct),
                    _ => (false, $"Home is not supported for printer type '{adapter.Type}'."),
                };
                Program.Log(homeOk
                    ? $"[farm] Home sent to {adapter.Name}."
                    : $"[farm] Home to {adapter.Name} failed: {homeReason}");
                await _dashboard.ReportControlAsync(control.PrinterIp, control.Command, homeOk,
                    homeOk ? $"Home command sent to {adapter.Name}." : homeReason, ct);
                continue;
            }
            var detail = $"Unknown control command '{control.Command}' — never executed.";
            Program.Log($"[farm] Control '{control.Command}' for {adapter.Name} not executed: {detail}");
            await _dashboard.ReportControlAsync(control.PrinterIp, control.Command, false, detail, ct);
        }
    }

    private static async Task<(bool Ok, string Reason, string Detail)> ExecLightsAsync(SnapmakerAdapter snap, CancellationToken ct)
    {
        var (ok, reason) = await snap.SetCavityLedAsync(true, ct);
        return ok
            ? (true, "", "U1 cavity LED turned on.")
            : (false, $"U1 cavity LED call failed: {reason}", "");
    }

    private static async Task<(bool Ok, string Reason, string Detail)> ExecBambuLightsAsync(BambuAdapter bambu, CancellationToken ct)
    {
        var (ok, reason) = await bambu.SetChamberLedAsync(true, ct);
        return ok
            ? (true, "", "Chamber light turned on (Bambu ledctrl). First-use verification on this model is still owed if this is its first lights execution.")
            : (false, $"Chamber light call failed: {reason}", "");
    }

    private IPrinterAdapter? ResolveAdapter(FarmJob job)
    {
        var adapter = _printers.Find(job.Printer);
        adapter ??= _printers.All.FirstOrDefault(a =>
            string.Equals(a.Ip, job.PrinterIp, StringComparison.OrdinalIgnoreCase));
        return adapter;
    }

    /// <summary>Log a start refusal locally, once per hold key
    /// per bridge run. Returns true only when the key is newly
    /// added (i.e. this is the first log for it).</summary>
    private bool LogHold(string jobId, string key, string message)
    {
        if (_loggedHold.Add(jobId + ":" + key))
        {
            Program.Log(message);
            return true;
        }
        return false;
    }

    /// <summary>Log a start refusal locally AND report it to
    /// the dashboard as status "held" with the same reason,
    /// once per hold key per bridge run (mirrors LogHold's
    /// dedup). The dashboard shows "Held: &lt;reason&gt;" on
    /// the job and leaves it queued; the bridge retries on its
    /// next poll once the problem is cleared. Returns true
    /// when this is the first report for the key.</summary>
    private async Task<bool> ReportHoldAsync(string jobId, string key, string message, CancellationToken ct)
    {
        if (!LogHold(jobId, key, message))
            return false;
        await _dashboard.ReportFarmJobAsync(jobId, "held", message, null, ct);
        return true;
    }

    /// <summary>Clear one hold key for a job, so a re-occurring
    /// hold reports again. Called as each gate passes: reaching
    /// a gate means the hold condition behind it has cleared.</summary>
    private void ClearHold(string jobId, string key) => _loggedHold.Remove(jobId + ":" + key);

    /// <summary>Dashboard norm_printer_model, mirrored
    /// exactly: lowercase, strip brand words, strip
    /// non-alphanumerics; a bare IP (or blank) is unknown
    /// (""). Unknown on either side means no gate.</summary>
    private static string NormPrinterModel(string name)
    {
        var s = (name ?? "").Trim().ToLowerInvariant();
        if (s == "") return "";
        var parts = s.Split('.');
        if (parts.Length == 4 && parts.All(p => p.Length > 0 && p.All(char.IsDigit))) return "";
        s = s.Replace("bambu lab", "").Replace("bambu", "").Replace("snapmaker", "");
        var kept = new System.Text.StringBuilder();
        foreach (var c in s)
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                kept.Append(c);
        return kept.ToString();
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
