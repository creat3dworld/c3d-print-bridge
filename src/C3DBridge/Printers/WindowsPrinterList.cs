// C3D Print Bridge — Windows printer queues for NON-3D printers.
// Owner request, Oct 24, 2026: "add printer" for non-3D printers
// (e.g. a label printer) for easy driver connections.
//
// What this does and does NOT do:
// - DOES list printer queues Windows already sees (Get-Printer),
//   so the owner picks one by name instead of typing an IP or
//   serial. No pairing, no driver install, no Bluetooth stack:
//   a device Windows cannot see cannot be listed, and the
//   bridge never pairs anything itself (same rule as the
//   earlier Bluetooth/USB design).
// - DOES NOT make a listed queue a 3D printer. Non-3D printers
//   live only in BridgeConfig.Non3DPrinters and are NEVER
//   added to PrinterRegistry, so ExecuteReadyJobsAsync and
//   FarmPoller (which only iterate _printers.All) can never
//   send a 3D print job to one. They may receive label/document
//   output only, once a dashboard label route exists for them.
// - The thermal label printer is currently paired to the Mac,
//   not the shop PC: until Windows can see it too, it will
//   simply not appear in this list. That is a pairing step
//   for the owner, in person, not something the bridge does.
using System.Diagnostics;

namespace C3DBridge.Printers;

public static class WindowsPrinterList
{
    /// <summary>Queue names Windows reports, in order, de-duplicated.
    /// Returns an empty list (never null, never an exception) when
    /// PowerShell is unavailable or reports nothing.</summary>
    public static async Task<IReadOnlyList<string>> ListAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -Command \"Get-Printer | Select-Object -ExpandProperty Name\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return Array.Empty<string>();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var output = await proc.StandardOutput.ReadToEndAsync(timeout.Token);
            await proc.WaitForExitAsync(timeout.Token);
            var names = new List<string>();
            foreach (var line in output.Split('\n'))
            {
                var name = line.Trim();
                if (name.Length > 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    names.Add(name);
            }
            return names;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
