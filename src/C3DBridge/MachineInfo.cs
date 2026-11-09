// C3D Print Bridge — slice-machine identity + hardware report
// (MULTI-BRIDGE, 2026.1108, slice priority queue).
//
// Two bridges can now run side by side (3d1PC #1, shop PC #2)
// without fighting over the same slice jobs. Each bridge
// identifies itself with a machine_id from bridge-config.json,
// and the dashboard routes each slice job to one machine only.
//
// The hardware strings here are REFERENCE ONLY — the dashboard
// stores them on the machine record so the Slice priority card
// can show them, and routing never reads them (it uses the
// priority order + live idle/busy). A field this PC cannot
// report is "" and the dashboard keeps its stored value, so a
// probe that fails never blanks a known spec. Nothing here is
// ever guessed: unknown stays blank.

using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace C3DBridge;

public static class MachineInfo
{
    /// <summary>Normalize a configured machine_id to the form
    /// the dashboard expects (lowercase, [a-z0-9_-] — the
    /// dashboard's sanitize_key alphabet). Returns (id, null)
    /// when usable; (null, null) when absent/blank (legacy
    /// mode); (null, reason) when the value carries
    /// characters the dashboard would strip — a bad id fails
    /// closed to legacy instead of registering a machine the
    /// dashboard cannot route to.</summary>
    public static (string? Id, string? Problem) NormalizeMachineId(string? raw)
    {
        var id = (raw ?? "").Trim().ToLowerInvariant();
        if (id.Length == 0) return (null, null);
        foreach (var ch in id)
        {
            var ok = (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-' || ch == '_';
            if (!ok)
            {
                var shown = raw!.Trim();
                if (shown.Length > 40) shown = shown[..40] + "…";
                return (null, $"machine_id \"{shown}\" contains '{ch}', which is not a letter, digit, '-' or '_'");
            }
        }
        return (id, null);
    }

    /// <summary>Build the slice_machine heartbeat block the
    /// dashboard's Slice priority registration reads
    /// (C3D_Slice_Machines::report_from_heartbeat), or null
    /// when this bridge has no machine id (legacy mode —
    /// nothing is sent). slice_status is the live slice
    /// state ("idle" / "busy"); anything else is sent as
    /// null and the dashboard keeps its stored value.</summary>
    public static object? BuildSliceMachineBlock(string machineId, string? machineName, BridgeConfig cfg, string? sliceStatus)
    {
        var hw = Hardware.Value;
        var status = sliceStatus == "busy" ? "busy" : sliceStatus == "idle" ? "idle" : null;
        return new
        {
            machine_id = machineId,
            name = string.IsNullOrWhiteSpace(machineName) ? machineId : machineName!.Trim(),
            cpu = hw.Cpu,
            gpu = hw.Gpu,
            ram = hw.Ram,
            storage = hw.Storage,
            os = hw.Os,
            slicers = SlicersPresent(cfg),
            slice_status = status,
        };
    }

    /// <summary>Which of the three known slicers are actually
    /// installed on this PC, by the dashboard's slicer keys.
    /// Read from the configured executable paths — a path
    /// that is not configured, or a file that is not there,
    /// is not present. Checked live on every heartbeat, so a
    /// slicer installed mid-run shows up without a restart.</summary>
    public static List<string> SlicersPresent(BridgeConfig cfg)
    {
        var list = new List<string>();
        if (SlicerExists(cfg.SlicerBambuStudioPath)) list.Add("bambu-studio");
        if (SlicerExists(cfg.SlicerSnapmakerOrcaPath)) list.Add("snapmaker-orca");
        if (SlicerExists(cfg.SlicerOrcaslicerPath)) list.Add("orcaslicer");
        return list;
    }

    private static bool SlicerExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return File.Exists(path.Trim()); } catch { return false; }
    }

    // Hardware does not change while the bridge runs, so it is
    // probed once and cached. A probe that throws returns ""
    // for that field — never a crash, never a guess.
    private sealed record HardwareInfo(string Cpu, string Gpu, string Ram, string Storage, string Os);

    private static readonly Lazy<HardwareInfo> Hardware = new(CollectHardware);

    private static HardwareInfo CollectHardware()
    {
        return new HardwareInfo(
            Cpu: CpuDescription(),
            Gpu: GpuDescription(),
            Ram: RamDescription(),
            Storage: StorageDescription(),
            Os: OsDescription());
    }

    private static string CpuDescription()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return "";
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key is null) return "";
            var name = (key.GetValue("ProcessorNameString") as string ?? "").Trim();
            name = name.Replace("(R)", "", StringComparison.OrdinalIgnoreCase)
                .Replace("(TM)", "", StringComparison.OrdinalIgnoreCase)
                .Replace("  ", " ").Trim();
            var cores = Environment.ProcessorCount;
            var mhz = key.GetValue("~MHz");
            var clock = mhz is int m && m > 0 ? $" @ {m / 1000.0:0.0} GHz" : "";
            if (name.Length == 0) return cores > 0 ? $"{cores} cores" : "";
            return $"{name}, {cores} cores{clock}";
        }
        catch { return ""; }
    }

    private static string GpuDescription()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return "";
            // Display adapters live under the display class GUID.
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls is null) return "";
            var names = new List<string>();
            foreach (var sub in cls.GetSubKeyNames())
            {
                if (sub.Length != 4 || !int.TryParse(sub, out _)) continue; // 0000, 0001, …
                using var dev = cls.OpenSubKey(sub);
                var desc = (dev?.GetValue("DriverDesc") as string ?? "").Trim();
                if (desc.Length > 0 && !names.Contains(desc, StringComparer.OrdinalIgnoreCase))
                    names.Add(desc);
            }
            return string.Join("; ", names);
        }
        catch { return ""; }
    }

    private static string RamDescription()
    {
        try
        {
            var bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (bytes <= 0) return "";
            return $"{bytes / 1024.0 / 1024.0 / 1024.0:0} GB";
        }
        catch { return ""; }
    }

    private static string StorageDescription()
    {
        try
        {
            long total = 0, free = 0;
            var count = 0;
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                try
                {
                    total += drive.TotalSize;
                    free += drive.TotalFreeSpace;
                    count++;
                }
                catch { /* one unreadable drive must not hide the rest */ }
            }
            if (count == 0 || total <= 0) return "";
            const double gb = 1024.0 * 1024.0 * 1024.0;
            return $"{total / gb:N0} GB total across {count} drive(s) ({free / gb:N0} GB free)";
        }
        catch { return ""; }
    }

    private static string OsDescription()
    {
        try { return RuntimeInformation.OSDescription.Trim(); }
        catch { return ""; }
    }
}
