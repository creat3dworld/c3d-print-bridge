// C3D Print Bridge — printer IP-change auto-detection
// (AUTO-START BUILD, owner requirement Oct 29, 2026:
// "there needs to be something the bridge or dashboard
// can detect" when a printer moves to a new DHCP address).
//
// HOW IT WORKS, WITHOUT GUESSING:
//  - The bridge LEARNS each Snapmaker printer's Moonraker
//    hostname (GET /printer/info, a documented Moonraker
//    endpoint) and saves it to the LOCAL config only, the
//    same way camera auto-discovery persists URLs locally.
//    A Bambu printer's serial is already in its config.
//  - When a configured printer is offline, the reconciler
//    (throttled: at most every 10 minutes, only while a
//    printer is offline) probes the LAN with the existing
//    Discovery TCP probe (ports 8883/7125) and IDENTIFIES
//    each candidate: a Moonraker candidate matches only
//    when its hostname equals the learned hostname; a
//    Bambu candidate matches only when it answers a pushall
//    report on the known printer's serial topic.
//  - On a match at a new IP: the local config is updated
//    (IP, plus any snapshot/stream URL whose host is
//    exactly the old IP — the Oct 29 manual move had to
//    rewrite those too), the registry is reloaded in
//    place (no process restart), the move is logged, and
//    the dashboard is told via farm/printer-moved so it
//    can update its manual record and post a notice.
//  - An open port that cannot be identified is NEVER
//    treated as the moved printer. A printer whose
//    hostname was never learned cannot be matched by
//    hostname — the reconciler logs that learning is
//    pending and moves on, rather than guessing.
//
// What this does NOT do: it never changes a printer's
// serial, access code, or credentials; it never touches
// a printer that is currently online; it never runs the
// full-subnet probe on every tick (throttle above).

using C3DBridge.Printers;

namespace C3DBridge;

public sealed class IpReconciler
{
    private readonly DashboardClient _dashboard;
    private readonly PrinterRegistry _printers;
    private DateTime _lastRun = DateTime.MinValue;
    private readonly HashSet<string> _hostnameLearnAttempted = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Detected moves whose dashboard report has
    /// not been confirmed yet. Retried on every flush
    /// until the dashboard answers — a move is never
    /// forgotten just because one report POST failed.</summary>
    private readonly List<(string Name, string Type, string OldIp, string NewIp, string Evidence, string Serial)> _pendingMoveReports = new();

    public IpReconciler(DashboardClient dashboard, PrinterRegistry printers)
    {
        _dashboard = dashboard;
        _printers = printers;
    }

    /// <summary>Learn Moonraker hostnames for online
    /// Snapmaker printers that do not have one saved yet.
    /// Called on status ticks; each printer is attempted
    /// at most once per bridge run, and only while it is
    /// online — a failed attempt is simply retried on the
    /// next bridge run, never spun on.</summary>
    public async Task LearnHostnamesAsync(IReadOnlyDictionary<string, PrinterStatus> statuses, CancellationToken ct)
    {
        var (loaded, _) = Config.Load();
        if (loaded is null) return;
        var changed = false;
        var printers = loaded.Printers.ToList();
        foreach (var adapter in _printers.All)
        {
            if (adapter is not SnapmakerAdapter snap) continue;
            if (!statuses.TryGetValue(adapter.Name, out var live) ||
                string.Equals(live.State, "offline", StringComparison.OrdinalIgnoreCase))
                continue;
            var idx = printers.FindIndex(p => string.Equals(p.Name, adapter.Name, StringComparison.OrdinalIgnoreCase));
            if (idx < 0 || !string.IsNullOrWhiteSpace(printers[idx].MoonrakerHostname)) continue;
            if (!_hostnameLearnAttempted.Add(adapter.Name)) continue;
            var hostname = await snap.TryGetHostnameAsync(ct);
            if (string.IsNullOrWhiteSpace(hostname)) continue;
            printers[idx] = printers[idx] with { MoonrakerHostname = hostname };
            changed = true;
            Program.Log($"[discovery] Learned Moonraker hostname for '{adapter.Name}' for future IP-change matching.");
        }
        if (changed)
        {
            try { Config.Save(loaded with { Printers = printers }); }
            catch (Exception ex)
            {
                Program.Log($"[discovery] Could not save learned Moonraker hostnames locally ({ex.Message}). Learning retries on the next bridge run.");
            }
        }
    }

    /// <summary>Reconcile offline printers against LAN
    /// candidates. Throttled to every 10 minutes and only
    /// while at least one configured printer is offline.</summary>
    public async Task ReconcileAsync(IReadOnlyDictionary<string, PrinterStatus> statuses, CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastRun < TimeSpan.FromMinutes(10)) return;
        var offline = _printers.All.Where(a =>
            !statuses.TryGetValue(a.Name, out var live) ||
            string.Equals(live.State, "offline", StringComparison.OrdinalIgnoreCase)).ToList();
        if (offline.Count == 0) return;
        _lastRun = DateTime.UtcNow;

        var (loaded, loadError) = Config.Load();
        if (loaded is null)
        {
            Program.Log($"[discovery] IP reconciliation skipped: could not reload the local config ({loadError}).");
            return;
        }
        var candidates = await Discovery.ProbeCandidatesAsync();
        if (candidates.Count == 0)
        {
            Program.Log("[discovery] IP reconciliation: no LAN printer candidates found for the offline printers.");
            return;
        }
        var printers = loaded.Printers.ToList();
        var configChanged = false;
        var moves = new List<(string Name, string Type, string OldIp, string NewIp, string Evidence, string Serial)>();
        foreach (var adapter in offline)
        {
            var idx = printers.FindIndex(p => string.Equals(p.Name, adapter.Name, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) continue;
            var cfg = printers[idx];
            if (string.Equals(adapter.Type, "snapmaker", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(cfg.MoonrakerHostname))
                {
                    Program.Log($"[discovery] Cannot match '{adapter.Name}' by hostname yet: no Moonraker hostname was ever learned for it (it must be online once for learning). Not guessing.");
                    continue;
                }
                foreach (var cand in candidates.Where(c => c.Port == 7125))
                {
                    if (string.Equals(cand.Ip, cfg.Ip, StringComparison.OrdinalIgnoreCase)) continue;
                    var host = await SnapmakerAdapter.TryGetMoonrakerHostnameAsync(
                        cand.Ip, cfg.PaxxUser, cfg.PaxxPassword, cfg.AccessCode, ct);
                    if (string.Equals(host, cfg.MoonrakerHostname, StringComparison.OrdinalIgnoreCase))
                    {
                        moves.Add(ApplyMove(printers, idx, cand.Ip, "Moonraker hostname '" + host + "'"));
                        configChanged = true;
                        break;
                    }
                }
            }
            else if (string.Equals(adapter.Type, "bambu", StringComparison.OrdinalIgnoreCase))
            {
                // BRIDGE PRIORITY (2026.1111.2142): a
                // deferred printer is not probed. It shows
                // offline here precisely because this bridge
                // does not own it — but identifying it on a
                // new IP would still open an MQTT session to
                // its broker and fight the owning bridge.
                // The owning bridge reconciles its own
                // printers; this one stands aside.
                if (adapter is BambuAdapter deferredBambu && deferredBambu.Deferred)
                {
                    Program.Log($"[discovery] Skipping IP reconciliation for '{adapter.Name}': deferred to the dashboard-assigned owner (this bridge does not own it).");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(cfg.Serial) || string.IsNullOrWhiteSpace(cfg.AccessCode))
                {
                    Program.Log($"[discovery] Cannot match '{adapter.Name}' by serial yet: no serial/access code is configured for it. Not guessing.");
                    continue;
                }
                var port = cfg.Port ?? 8883;
                foreach (var cand in candidates.Where(c => c.Port == 8883))
                {
                    if (string.Equals(cand.Ip, cfg.Ip, StringComparison.OrdinalIgnoreCase)) continue;
                    if (await BambuAdapter.TryIdentifyAsync(cand.Ip, port, cfg.Serial, cfg.AccessCode, ct))
                    {
                        moves.Add(ApplyMove(printers, idx, cand.Ip, "Bambu serial match"));
                        configChanged = true;
                        break;
                    }
                }
            }
            else
            {
                Program.Log($"[discovery] IP reconciliation does not cover printer type '{adapter.Type}' for '{adapter.Name}': no verified identification path exists for it.");
            }
        }
        if (configChanged)
        {
            var updated = loaded with { Printers = printers };
            try
            {
                Config.Save(updated);
                _printers.Reload(printers);
                Program.Log("[discovery] Local bridge config updated and registry reloaded in place (no restart needed).");
            }
            catch (Exception ex)
            {
                Program.Log($"[discovery] Detected printer move(s) but could not save the local config ({ex.Message}). The dashboard is still notified; fix the config and restart the bridge.");
            }
        }
        foreach (var move in moves)
            _pendingMoveReports.Add(move);
        await FlushMoveReportsAsync(ct);
    }

    /// <summary>Send every pending move report, keeping
    /// any that fail for the next flush. Called on every
    /// job-poll tick so a failed report is retried within
    /// ~30 seconds, not on the next 10-minute
    /// reconciliation run.</summary>
    public async Task FlushMoveReportsAsync(CancellationToken ct)
    {
        foreach (var move in _pendingMoveReports.ToList())
        {
            var sent = await _dashboard.ReportPrinterMovedAsync(move.Name, move.Type, move.OldIp, move.NewIp, move.Evidence, move.Serial, ct);
            if (sent)
            {
                _pendingMoveReports.Remove(move);
                Program.Log($"[discovery] Dashboard notified: '{move.Name}' moved {move.OldIp} -> {move.NewIp}.");
            }
            else
            {
                Program.Log($"[discovery] Move report for '{move.Name}' ({move.OldIp} -> {move.NewIp}) did not reach the dashboard; it stays pending and retries on the next poll.");
            }
        }
    }

    /// <summary>Apply one detected move to the in-memory
    /// config list and return its report tuple. The caller
    /// saves the config and sends the dashboard report.</summary>
    private static (string Name, string Type, string OldIp, string NewIp, string Evidence, string Serial) ApplyMove(
        List<PrinterConfig> printers, int idx, string newIp, string evidence)
    {
        var old = printers[idx];
        var oldIp = old.Ip;
        // Rewrite snapshot/stream URL hosts ONLY when the
        // host is exactly the old IP — a URL pointing
        // elsewhere (or a hostname) is left alone.
        string? RewriteUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return url;
            try
            {
                var uri = new Uri(url);
                if (string.Equals(uri.Host, oldIp, StringComparison.OrdinalIgnoreCase))
                {
                    var b = new UriBuilder(uri) { Host = newIp };
                    return b.Uri.ToString();
                }
            }
            catch { /* not a URL: leave it */ }
            return url;
        }
        printers[idx] = old with
        {
            Ip = newIp,
            SnapshotUrl = RewriteUrl(old.SnapshotUrl),
            StreamUrl = RewriteUrl(old.StreamUrl),
            SnapmakerSnapshotUrl = RewriteUrl(old.SnapmakerSnapshotUrl),
            SnapmakerStreamUrl = RewriteUrl(old.SnapmakerStreamUrl),
        };
        Program.Log($"[discovery] Printer '{old.Name}' moved {oldIp} -> {newIp} (matched by {evidence}).");
        // 2026.1030.1511-farm-dedupe — the report carries the
        // printer's identity key so the dashboard updates the
        // existing card by serial instead of by name guess:
        // Bambu = factory serial; Snapmaker = learned
        // Moonraker hostname, else the configured serial.
        var serial = string.Equals(old.Type, "snapmaker", StringComparison.OrdinalIgnoreCase)
            ? (!string.IsNullOrWhiteSpace(old.MoonrakerHostname) ? old.MoonrakerHostname : (old.Serial ?? ""))
            : (old.Serial ?? "");
        return (old.Name, old.Type, oldIp, newIp, evidence, serial);
    }
}
