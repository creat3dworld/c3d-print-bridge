// C3D Print Bridge — machine-config sync (BRIDGE-CONFIG
// HEALTH, 2026.1109.2139, CONTRACT.md §Q.1).
//
// The dashboard holds each bridge machine's config
// (slicer_paths + printer list) and the slicer default
// install locations, versioned by machine_config_version.
// This class fetches that config (on startup after the
// first successful heartbeat, and whenever a
// heartbeat/status response carries a higher version) and
// applies it to the local bridge-config.json:
//
//   slicer_paths — applied ONLY for slicers whose local
//     path is blank or points at a missing exe. A working
//     local path is never clobbered. A rebuilt bridge has
//     blank paths, so it pulls the whole config down
//     instead of starting blank.
//   slicer_default_locations — kept in the local config
//     for SlicerSetup discovery.
//   printers — applied ONLY when the local printer list
//     is empty (fresh/rebuilt bridge); otherwise the
//     local bridge-config.json printers stay authoritative.
//   bridge_version_latest — logged when this bridge is
//     behind it; the dashboard shows the behind-latest
//     flag from the heartbeat version.
//
// Returns the updated config when anything was applied
// (fresh-loaded, applied, saved), so the Worker can swap
// it in without a restart; null when the fetch failed or
// nothing needed changing. LastAppliedVersion only moves
// on a successful fetch, so a failed fetch retries on the
// next version sighting instead of being forgotten.

namespace C3DBridge;

public sealed class MachineConfigSync
{
    private readonly DashboardClient _dashboard;

    /// <summary>machine_config_version last successfully
    /// applied (0 = none applied yet).</summary>
    public int LastAppliedVersion { get; private set; }

    public MachineConfigSync(DashboardClient dashboard)
    {
        _dashboard = dashboard;
    }

    public async Task<BridgeConfig?> FetchAndApplyAsync(CancellationToken ct)
    {
        var (remote, problem) = await _dashboard.GetMachineConfigAsync(ct);
        if (remote is null)
        {
            Program.Log($"[machine-config] Fetch failed: {problem}");
            return null;
        }
        var (loaded, error) = Config.Load();
        if (loaded is null)
        {
            Program.Log($"[machine-config] Could not reload the local config to apply the fetched config ({error}).");
            return null;
        }
        var cfg = loaded;
        var changed = false;

        foreach (var slicer in MachineInfo.KnownSlicers)
        {
            if (!remote.SlicerPaths.TryGetValue(slicer, out var remotePath) || string.IsNullOrWhiteSpace(remotePath))
                continue;
            var local = MachineInfo.ConfiguredPathFor(cfg, slicer);
            var localWorks = !string.IsNullOrWhiteSpace(local) && File.Exists(local.Trim());
            if (localWorks) continue; // never clobber a working local path
            cfg = MachineInfo.WithSlicerPath(cfg, slicer, remotePath.Trim());
            changed = true;
            Program.Log($"[machine-config] {slicer}: took the dashboard-held path (local path was blank or missing).");
        }

        foreach (var (slicer, location) in remote.SlicerDefaultLocations)
        {
            if (string.IsNullOrWhiteSpace(location)) continue;
            if (cfg.SlicerDefaultLocations.TryGetValue(slicer, out var existing) &&
                string.Equals(existing.Trim(), location.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;
            var defaults = new Dictionary<string, string>(cfg.SlicerDefaultLocations, StringComparer.OrdinalIgnoreCase)
            {
                [slicer] = location.Trim(),
            };
            cfg = cfg with { SlicerDefaultLocations = defaults };
            changed = true;
        }

        if (cfg.Printers.Count == 0 && remote.Printers.Count > 0)
        {
            var printers = remote.Printers
                .Where(p => !string.IsNullOrWhiteSpace(p.PrinterName) && !string.IsNullOrWhiteSpace(p.PrinterIp) && !string.IsNullOrWhiteSpace(p.PrinterType))
                .Select(p => new PrinterConfig
                {
                    Name = p.PrinterName.Trim(),
                    Type = p.PrinterType.Trim(),
                    Ip = p.PrinterIp.Trim(),
                    Serial = string.IsNullOrWhiteSpace(p.Serial) ? null : p.Serial.Trim(),
                })
                .ToList();
            if (printers.Count > 0)
            {
                cfg = cfg with { Printers = printers };
                changed = true;
                Program.Log($"[machine-config] Local printer list was empty; took {printers.Count} printer(s) from the dashboard-held config.");
            }
        }

        if (!string.IsNullOrWhiteSpace(remote.BridgeVersionLatest) &&
            !string.Equals(remote.BridgeVersionLatest.Trim(), Program.Version, StringComparison.Ordinal))
        {
            Program.Log($"[machine-config] This bridge is {Program.Version}; the dashboard's latest known bridge version is {remote.BridgeVersionLatest.Trim()}.");
        }

        if (changed)
        {
            try
            {
                Config.Save(cfg);
            }
            catch (Exception ex)
            {
                Program.Log($"[machine-config] Version {remote.MachineConfigVersion} fetched but could not be saved to the local config ({ex.Message}); it will be re-fetched.");
                return null;
            }
            Program.Log($"[machine-config] Version {remote.MachineConfigVersion} applied to the local bridge config.");
        }
        else
        {
            Program.Log($"[machine-config] Version {remote.MachineConfigVersion} checked (no local changes needed).");
        }
        LastAppliedVersion = remote.MachineConfigVersion;
        return changed ? cfg : null;
    }
}
