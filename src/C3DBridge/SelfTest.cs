// C3D Print Bridge — self-test (BRIDGE-CONFIG HEALTH,
// 2026.1109.2139, CONTRACT.md §Q.3).
//
// Marlena, Nov 10, 2026: the bridge self-tests on connect
// (slicers present, printers reachable, /cache exists, MQTT
// push alive) and reports to the dashboard. Runs on startup
// (after the first heartbeat), on reconnect (heartbeat
// re-established after a failure), and when the machine
// config version changes. Each failed check also produces
// an error report (error_code self_test_failed) so the
// dashboard notifies, not just stores.
//
// Checks (contract words):
//   slicer_bambu_studio / slicer_snapmaker_orca /
//   slicer_orcaslicer — exe present and runnable: exists +
//   file version readable + the process starts within a
//   short timeout (hidden window, killed afterwards — GUI
//   apps may not exit; "started and still running at
//   timeout" counts as runnable).
//   printer_reachable — TCP connect to a configured
//   printer (3s timeout; Bambu 8883, Moonraker 7125, or
//   the configured port when set).
//   ftp_cache — FTP /cache exists on a Bambu printer
//   (FTP LIST the root, look for `cache`; fail when
//   missing — this check NEVER creates it).
//   mqtt_push — a Bambu push arrived within the last 10
//   minutes; `skip` with a detail when there has been no
//   push window since connect.
//
// result is "pass" / "fail" / "skip". overall is "pass"
// only when no check failed (skips do not fail it).

using System.Diagnostics;
using System.Net.Sockets;
using C3DBridge.Printers;

namespace C3DBridge;

/// <summary>One self-test check result, exactly the
/// contract's check object: { check, printer_ip,
/// printer_name, result, detail }.</summary>
public sealed record SelfTestCheck(string Check, string PrinterIp, string PrinterName, string Result, string Detail);

public sealed class SelfTestRunner
{
    private readonly DashboardClient _dashboard;
    private readonly ErrorReporter _errors;

    public SelfTestRunner(DashboardClient dashboard, ErrorReporter errors)
    {
        _dashboard = dashboard;
        _errors = errors;
    }

    /// <summary>Run the full self-test and report it. Never
    /// throws: a check that cannot run reports "skip" or
    /// "fail" with the reason, and a report that cannot
    /// reach the dashboard is logged, never fatal.</summary>
    public async Task RunAsync(BridgeConfig cfg, IReadOnlyList<IPrinterAdapter> adapters,
        DateTime startedUtc, CancellationToken ct)
    {
        var checks = new List<SelfTestCheck>();
        foreach (var slicer in MachineInfo.KnownSlicers)
            checks.Add(await CheckSlicerAsync(cfg, slicer, ct));
        foreach (var adapter in adapters)
        {
            checks.Add(await CheckPrinterReachableAsync(cfg, adapter, ct));
            if (adapter is BambuAdapter bambu)
            {
                checks.Add(await CheckFtpCacheAsync(bambu, ct));
                checks.Add(CheckMqttPush(bambu, startedUtc));
            }
        }
        var overall = checks.Any(c => c.Result == "fail") ? "fail" : "pass";
        Program.Log($"[self-test] Ran {checks.Count} checks: overall {overall} ({checks.Count(c => c.Result == "pass")} pass, {checks.Count(c => c.Result == "fail")} fail, {checks.Count(c => c.Result == "skip")} skip).");
        foreach (var failed in checks.Where(c => c.Result == "fail"))
            Program.Log($"[self-test] FAIL {failed.Check} ({failed.PrinterName}): {failed.Detail}");
        try
        {
            var sent = await _dashboard.ReportSelfTestAsync(checks, overall, ct);
            if (!sent) Program.Log("[self-test] The self-test report did not reach the dashboard.");
        }
        catch (Exception ex)
        {
            Program.Log($"[self-test] The self-test report failed to send: {ex.Message}");
        }
        foreach (var failed in checks.Where(c => c.Result == "fail"))
        {
            await _errors.ReportAsync("self_test_failed", "error", "self_test",
                $"Self-test failed: {failed.Check}" + (string.IsNullOrWhiteSpace(failed.PrinterName) ? "." : $" on '{failed.PrinterName}'."),
                failed.Detail, "", "", ct);
        }
    }

    private static async Task<SelfTestCheck> CheckSlicerAsync(BridgeConfig cfg, string slicer, CancellationToken ct)
    {
        var check = slicer switch
        {
            "bambu-studio" => "slicer_bambu_studio",
            "snapmaker-orca" => "slicer_snapmaker_orca",
            "orcaslicer" => "slicer_orcaslicer",
            _ => "slicer_" + slicer.Replace("-", "_", StringComparison.Ordinal),
        };
        var path = (MachineInfo.ConfiguredPathFor(cfg, slicer) ?? "").Trim();
        if (path.Length == 0)
            return new SelfTestCheck(check, "", "", "fail", $"Slicer '{slicer}' is not configured (no executable path in bridge-config.json).");
        if (!File.Exists(path))
            return new SelfTestCheck(check, "", "", "fail", $"Slicer '{slicer}' is configured at '{path}' but the executable was not found.");
        string version;
        try { version = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion ?? ""; }
        catch (Exception ex) { return new SelfTestCheck(check, "", "", "fail", $"Slicer '{slicer}' exists at '{path}' but its file version could not be read ({ex.Message})."); }
        if (string.IsNullOrWhiteSpace(version))
            return new SelfTestCheck(check, "", "", "fail", $"Slicer '{slicer}' exists at '{path}' but its file version could not be read.");
        // Runnable: start it hidden and watch briefly. GUI
        // slicers stay running (that counts as runnable);
        // one that exits at once must exit 0. Killed
        // afterwards either way — the test never leaves a
        // slicer window open.
        Process? proc;
        try
        {
            proc = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Exception ex)
        {
            return new SelfTestCheck(check, "", "", "fail", $"Slicer '{slicer}' at '{path}' could not be started ({ex.Message}).");
        }
        if (proc is null)
            return new SelfTestCheck(check, "", "", "fail", $"Slicer '{slicer}' at '{path}' could not be started (no process).");
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Still running at the 5s timeout: runnable.
                return new SelfTestCheck(check, "", "", "pass", $"Slicer '{slicer}' version {version} started and was still running at the 5s timeout.");
            }
            if (proc.ExitCode == 0)
                return new SelfTestCheck(check, "", "", "pass", $"Slicer '{slicer}' version {version} started and exited 0.");
            return new SelfTestCheck(check, "", "", "fail", $"Slicer '{slicer}' version {version} started but exited with code {proc.ExitCode}.");
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            proc.Dispose();
        }
    }

    private static async Task<SelfTestCheck> CheckPrinterReachableAsync(BridgeConfig cfg, IPrinterAdapter adapter, CancellationToken ct)
    {
        var ip = adapter.Ip;
        var port = PortFor(cfg, adapter);
        if (port is null)
            return new SelfTestCheck("printer_reachable", ip, adapter.Name, "skip",
                $"No known probe port for printer type '{adapter.Type}' (no port configured); reachability was not tested.");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(ip, port.Value, timeoutCts.Token);
            return new SelfTestCheck("printer_reachable", ip, adapter.Name, "pass",
                $"TCP connect to {ip}:{port.Value} succeeded.");
        }
        catch (Exception ex)
        {
            return new SelfTestCheck("printer_reachable", ip, adapter.Name, "fail",
                $"TCP connect to {ip}:{port.Value} failed ({ex.Message}).");
        }
    }

    /// <summary>Probe port: the configured port when set
    /// (PrinterConfig.Port, matched by printer name),
    /// else the adapter port defaults — Bambu 8883,
    /// Snapmaker Moonraker 7125. Any other brand with no
    /// configured port has no known default: null, and
    /// the check skips instead of guessing a port.</summary>
    private static int? PortFor(BridgeConfig cfg, IPrinterAdapter adapter)
    {
        var printer = cfg.Printers.FirstOrDefault(p =>
            string.Equals(p.Name, adapter.Name, StringComparison.OrdinalIgnoreCase));
        if (printer?.Port is int configured) return configured;
        return adapter.Type switch
        {
            "bambu" => 8883,
            "snapmaker" => 7125,
            _ => null,
        };
    }

    private static async Task<SelfTestCheck> CheckFtpCacheAsync(BambuAdapter bambu, CancellationToken ct)
    {
        var (exists, detail) = await bambu.FtpCacheExistsAsync(ct);
        return new SelfTestCheck("ftp_cache", bambu.Ip, bambu.Name,
            exists ? "pass" : "fail", detail);
    }

    private static SelfTestCheck CheckMqttPush(BambuAdapter bambu, DateTime startedUtc)
    {
        var last = bambu.LastPushUtc;
        if (last == DateTime.MinValue)
        {
            if (DateTime.UtcNow - startedUtc < TimeSpan.FromMinutes(10))
                return new SelfTestCheck("mqtt_push", bambu.Ip, bambu.Name, "skip",
                    "No Bambu push received yet since connect (no push window yet).");
            return new SelfTestCheck("mqtt_push", bambu.Ip, bambu.Name, "fail",
                "No Bambu push has arrived in the last 10 minutes.");
        }
        var age = DateTime.UtcNow - last;
        return age <= TimeSpan.FromMinutes(10)
            ? new SelfTestCheck("mqtt_push", bambu.Ip, bambu.Name, "pass",
                $"Last Bambu push arrived {age.TotalSeconds:0}s ago.")
            : new SelfTestCheck("mqtt_push", bambu.Ip, bambu.Name, "fail",
                $"Last Bambu push arrived {age.TotalMinutes:0} minutes ago (over 10 minutes).");
    }
}
