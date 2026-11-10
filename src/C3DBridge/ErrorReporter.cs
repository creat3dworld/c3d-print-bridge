// C3D Print Bridge — error reporter (BRIDGE-CONFIG
// HEALTH, 2026.1109.2139, CONTRACT.md §Q.4).
//
// Marlena, Nov 10, 2026: the bridge must report ALL errors
// to the dashboard, and the dashboard must NOTIFY of errors
// (visible alert, not a silent list). No error may sit
// unreported: slice failures, upload failures, MQTT push
// silence, printer faults, missing slicer config, anything.
//
// This is the single funnel every error-report call site
// goes through. It throttles repeats of the same
// (error_code, source, job_id/slice_job_id) to one report
// per 15 minutes so one stuck failure does not flood the
// dashboard — the first report still goes immediately.
// A report that cannot reach the dashboard is logged
// locally, never thrown: error reporting must never crash
// the worker loop or mask the error it reports.

namespace C3DBridge;

public sealed class ErrorReporter
{
    private readonly DashboardClient _dashboard;
    private readonly Dictionary<string, DateTime> _lastSent = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public ErrorReporter(DashboardClient dashboard)
    {
        _dashboard = dashboard;
    }

    /// <summary>Report one error (error_code is a contract
    /// word, severity "error" / "warning", source one of
    /// "slice" / "upload" / "mqtt" / "printer" /
    /// "slicer_config" / "self_test" / "install" /
    /// "bridge"). jobId / sliceJobId are "" when the error
    /// is not tied to a job. Repeats of the same
    /// (error_code, source, job) within 15 minutes are
    /// dropped silently after the first.</summary>
    public async Task ReportAsync(string errorCode, string severity, string source,
        string message, string detail, string jobId = "", string sliceJobId = "",
        CancellationToken ct = default)
    {
        var key = errorCode + "|" + source + "|" + (jobId ?? "") + "|" + (sliceJobId ?? "");
        lock (_gate)
        {
            if (_lastSent.TryGetValue(key, out var last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(15))
                return;
            _lastSent[key] = DateTime.UtcNow;
        }
        try
        {
            var sent = await _dashboard.ReportErrorAsync(errorCode, severity, source,
                message, detail, jobId ?? "", sliceJobId ?? "", ct);
            if (!sent)
                Program.Log($"[error-report] {errorCode} ({source}) did not reach the dashboard: {message}");
        }
        catch (Exception ex)
        {
            Program.Log($"[error-report] {errorCode} ({source}) failed to send: {ex.Message}");
        }
    }
}
