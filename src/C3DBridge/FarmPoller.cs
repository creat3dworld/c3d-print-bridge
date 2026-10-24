// C3D Print Bridge — farm job poller (farm build, first stage).
//
// The dashboard Print Farm tab already serves farm/jobs with a
// dispatch map (start / pause / stop), and the dashboard Start
// already gates on bed-confirmed + AMS-confirmed + printer
// connected before it dispatches. This poller is the bridge
// side of that route. It is deliberately NOT a print starter
// yet, for one honest reason: a farm job carries title,
// order, printer, material, color, and qty — but NO print
// file. The bridge never invents a file, never guesses one
// from a title, and never starts a printer without a verified
// file supplied by the dashboard. So this stage polls, logs
// what the dashboard dispatched, re-checks that the named
// printer is idle, and leaves the job untouched when the
// file is missing. It reports nothing that would corrupt
// the dashboard state (a missing-file dispatch is NOT a
// failed print, and it is NOT a started print).
//
// What this stage DOES do, safely:
//  - Poll GET /wp-json/c3d-shipping/v1/farm/jobs.
//  - For dispatch=start: require a printer by that name/IP
//    in the local registry (never substitute another one),
//    and require that printer to be idle right now. If it
//    is busy/offline/unknown, leave the job queued — no
//    report, no spam, picked up on a later poll.
//  - For dispatch=pause/stop: log only for now. Pause/stop
//    control per brand (Moonraker pause/cancel, Bambu
//    MQTT pause/stop) is the next stage, after the file
//    delivery route lands, because a control command sent
//    to the wrong printer state would be worse than none.
//  - Never send a safety pass the bridge did not verify.
//    A snapshot never auto-passes a bed check here either.
//
// Next stage (dashboard + bridge together): farm jobs get
// a confirmed file reference (SKU/product -> model file,
// confirmed only, never guessed), delivered over an
// authenticated bridge file route with a SHA-256 check,
// and only then does this poller download, upload, and
// forward a start — still only when the printer is idle
// and the dashboard safety flags are confirmed, with the
// same interlock refusal as the existing bridge/jobs path.

using C3DBridge.Printers;

namespace C3DBridge;

public sealed class FarmPoller
{
    private readonly DashboardClient _dashboard;
    private readonly PrinterRegistry _printers;
    private readonly HashSet<string> _loggedMissingFile = new(StringComparer.OrdinalIgnoreCase);

    public FarmPoller(DashboardClient dashboard, PrinterRegistry printers)
    {
        _dashboard = dashboard;
        _printers = printers;
    }

    public async Task PollAsync(IReadOnlyDictionary<string, PrinterStatus> statuses, CancellationToken ct)
    {
        if (!_dashboard.HasKey)
            return;
        var (jobs, problem) = await _dashboard.PollFarmJobsAsync(ct);
        if (jobs is null)
        {
            if (!string.IsNullOrWhiteSpace(problem))
                Program.Log($"[farm] Poll failed: {problem}");
            return;
        }
        foreach (var job in jobs)
        {
            if (!string.Equals(job.Dispatch, "start", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(job.Dispatch))
                    Program.Log($"[farm] Dispatch '{job.Dispatch}' for job {job.JobId} ('{job.Title}') is logged only in this stage — brand pause/stop control is not yet built.");
                continue;
            }
            var adapter = _printers.Find(job.Printer);
            adapter ??= _printers.All.FirstOrDefault(a =>
                string.Equals(a.Ip, job.PrinterIp, StringComparison.OrdinalIgnoreCase));
            if (adapter is null)
            {
                Program.Log($"[farm] Job {job.JobId} dispatch=start, but no printer '{job.Printer}' / {job.PrinterIp} is configured on this bridge. No substitution, job left queued.");
                continue;
            }
            if (!statuses.TryGetValue(adapter.Name, out var live) ||
                !string.Equals(live.State, "idle", StringComparison.OrdinalIgnoreCase))
            {
                // Busy, offline, or unknown: leave queued, retry later.
                continue;
            }
            if (!job.BedConfirmed || !job.AmsConfirmed)
            {
                Program.Log($"[farm] Job {job.JobId} dispatch=start, but dashboard safety flags are not both confirmed (bed={job.BedConfirmed}, ams={job.AmsConfirmed}). Job left queued; bridge never starts on an unconfirmed gate.");
                continue;
            }
            // The file gap: farm jobs carry no file yet. Log once
            // per job, then leave it untouched — never guess a file.
            if (_loggedMissingFile.Add(job.JobId))
                Program.Log($"[farm] Job {job.JobId} ('{job.Title}') is dispatched and its printer is idle, but the farm job carries no print file yet, so the bridge cannot start it. Execution waits on the dashboard file-delivery route (confirmed file only).");
        }
    }
}
