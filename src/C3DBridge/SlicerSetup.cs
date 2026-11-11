// C3D Print Bridge — slicer auto-configuration
// (BRIDGE-CONFIG HEALTH, 2026.1109.2139, CONTRACT.md §Q.1/Q.5).
//
// Marlena, Nov 10, 2026 (06:56): when a bridge is installed,
// it must install ALL the slicers the dashboard has AND
// automatically set up all of them (executable paths
// configured, no manual slice-pilot step). A bridge must
// never land with slicer paths unconfigured — that is what
// blocked the X2D/U1 slices Nov 10 (22 failed slice jobs,
// "not configured (no executable path)" since Nov 2–6).
//
// This is the runtime half of that rule: for each of the
// three known slicers, when the configured path is blank or
// points at a missing exe, search (a) the dashboard default
// location for that slicer (slicer_default_locations from
// the machine config / bridge-setup.json, the exe directly
// under it and in a slicer subfolder), then (b) the
// C:\Program Files fallback paths. A found exe is written
// into bridge-config.json (load first, keep every other
// field) and logged. A slicer found nowhere reports
// slicer_missing once per startup via the error reporter.
// A working local path is NEVER overwritten — a discovery
// only fills a gap. No D: drive path is hardcoded here:
// a D: location only ever arrives via the dashboard-
// provided default location.

namespace C3DBridge;

public static class SlicerSetup
{
    /// <summary>C:\Program Files fallback exe paths, per
    /// slicer. Snapmaker Orca has two known exe name
    /// variants on disk ("snapmaker-orca.exe" and
    /// "Snapmaker Orca.exe") — both are tried.</summary>
    private static IEnumerable<string> ProgramFilesCandidates(string slicer) => slicer switch
    {
        "bambu-studio" => new[] { @"C:\Program Files\Bambu Studio\bambu-studio.exe" },
        "orcaslicer" => new[] { @"C:\Program Files\OrcaSlicer\orcaslicer.exe" },
        "snapmaker-orca" => new[]
        {
            @"C:\Program Files\Snapmaker Orca\snapmaker-orca.exe",
            @"C:\Program Files\Snapmaker Orca\Snapmaker Orca.exe",
        },
        _ => Array.Empty<string>(),
    };

    /// <summary>Display folder name for a slicer word, for
    /// the "<default location>\<Display Name>\<exe>" search
    /// — e.g. a default location of D:\C3D-Tools\Slicers\
    /// holding a "Bambu Studio" subfolder.</summary>
    private static string DisplayNameFor(string slicer) => slicer switch
    {
        "bambu-studio" => "Bambu Studio",
        "snapmaker-orca" => "Snapmaker Orca",
        "orcaslicer" => "OrcaSlicer",
        _ => slicer,
    };

    /// <summary>No-space folder name for a slicer word, for
    /// the "<default location>\<NoSpaceName>\<exe>" search
    /// (2026.1111.2141) — e.g. a default location of
    /// D:\C3D-Tools\Slicers\ holding a "BambuStudio"
    /// subfolder (no space) instead of "Bambu Studio".
    /// "OrcaSlicer" is the same as its display name, so
    /// that candidate simply repeats the display-name one
    /// (one extra File.Exists call, no behaviour change).</summary>
    private static string NoSpaceNameFor(string slicer) => slicer switch
    {
        "bambu-studio" => "BambuStudio",
        "snapmaker-orca" => "SnapmakerOrca",
        "orcaslicer" => "OrcaSlicer",
        _ => slicer,
    };

    /// <summary>Find a slicer's exe without touching the
    /// config: dashboard default location first (directly
    /// under it, in a slicer-word subfolder, in a
    /// display-name subfolder, in a no-space subfolder),
    /// then the C:\Program Files fallback paths. Returns
    /// the first path that actually exists, or null when
    /// nothing is found.</summary>
    public static string? FindSlicerExe(string slicer, IReadOnlyDictionary<string, string> defaultLocations)
    {
        var exe = MachineInfo.ExeNameFor(slicer);
        if (exe.Length == 0) return null;
        if (defaultLocations.TryGetValue(slicer, out var loc) && !string.IsNullOrWhiteSpace(loc))
        {
            foreach (var candidate in new[]
            {
                Path.Combine(loc.Trim(), exe),
                Path.Combine(loc.Trim(), slicer, exe),
                Path.Combine(loc.Trim(), DisplayNameFor(slicer), exe),
                Path.Combine(loc.Trim(), NoSpaceNameFor(slicer), exe),
            })
            {
                try { if (File.Exists(candidate)) return candidate; } catch { /* an unusable path is a miss, not a crash */ }
            }
        }
        foreach (var candidate in ProgramFilesCandidates(slicer))
        {
            try { if (File.Exists(candidate)) return candidate; } catch { /* same */ }
        }
        return null;
    }

    /// <summary>Ensure every known slicer is configured:
    /// a configured path that exists is left alone; any
    /// other slicer is searched (dashboard default
    /// location, then C:\Program Files) and, when found,
    /// written into bridge-config.json. Returns the
    /// updated config (fresh-loaded, applied, saved) when
    /// anything changed, else the loaded config, or null
    /// when the local config could not be read at all. A
    /// slicer found nowhere reports slicer_missing once
    /// per startup (the reporter's 15-minute dedupe
    /// covers the repeat calls). Runs at startup (after
    /// the machine-config fetch) and on machine-config
    /// version change.</summary>
    public static async Task<BridgeConfig?> EnsureSlicersConfiguredAsync(ErrorReporter errors, CancellationToken ct)
    {
        var (loaded, error) = Config.Load();
        if (loaded is null)
        {
            Program.Log($"[slicer-setup] Could not reload the local config to configure slicers ({error}).");
            return null;
        }
        var cfg = loaded;
        var changed = false;
        foreach (var slicer in MachineInfo.KnownSlicers)
        {
            var configured = MachineInfo.ConfiguredPathFor(cfg, slicer);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                try { if (File.Exists(configured.Trim())) continue; } catch { /* fall through to discovery */ }
            }
            var found = FindSlicerExe(slicer, cfg.SlicerDefaultLocations);
            if (found is not null)
            {
                cfg = MachineInfo.WithSlicerPath(cfg, slicer, found);
                changed = true;
                Program.Log($"[slicer-setup] {slicer}: discovered {found}; saved to the local bridge config.");
            }
            else
            {
                Program.Log($"[slicer-setup] {slicer}: no executable found (dashboard default location and C:\\Program Files checked).");
                await errors.ReportAsync("slicer_missing", "error", "slicer_config",
                    $"Slicer '{slicer}' is missing on this bridge.",
                    "No executable was found at the configured path, the dashboard default location, or the C:\\Program Files fallback.",
                    "", "", ct);
            }
        }
        if (!changed) return cfg;
        try
        {
            Config.Save(cfg);
        }
        catch (Exception ex)
        {
            Program.Log($"[slicer-setup] Discovered slicer paths could not be saved to the local config ({ex.Message}); they will be re-discovered on the next run.");
            return null;
        }
        return cfg;
    }
}
