using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace C3DBridge;

// Resolve a printer IP to its MAC via ping + arp, Windows only.
// Returns null when the MAC cannot be resolved. Never throws.
public static class MacResolver
{
    public static string? Resolve(string ip)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(ip)) return null;
        try
        {
            try { new Ping().Send(ip, 800); } catch { }
            var psi = new ProcessStartInfo("arp", $"-a {ip}") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            var m = Regex.Match(output, @"([0-9a-fA-F]{2}[:-]){5}[0-9a-fA-F]{2}");
            if (!m.Success) return null;
            return m.Value.Replace('-', ':').ToUpperInvariant();
        }
        catch { return null; }
    }
}
