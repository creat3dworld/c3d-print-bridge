// C3D Print Bridge — opt-in LAN probe (--discover).
//
// Finds printer candidates on the PC's own subnet by attempting
// short TCP connections to the two ports this bridge speaks:
//   8883  — Bambu local MQTT
//   7125  — Snapmaker Moonraker
// A reachable port is a CANDIDATE, not a confirmed printer: the
// output is suggested config lines for the owner to review and add
// by hand. Discovery never edits the config file itself, and it
// never runs unless the owner asks for it (it scans her own LAN
// only — the subnet derived from the PC's own IP address).
//
// Deliberately NOT implemented: Bambu's native UDP discovery
// packet. Its exact bytes could not be verified from public docs
// during this build, and a guessed packet is worse than a plain
// TCP probe that reports what it actually found.

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace C3DBridge;

public sealed record Candidate(string Ip, int Port, string Kind);

public static class Discovery
{
    /// <summary>Probe the PC's own /24 subnet and return raw
    /// candidates. A reachable port is a CANDIDATE, not a confirmed
    /// printer. Returns an empty list (never null) when nothing is
    /// found or no LAN address exists; the caller reports that.</summary>
    public static async Task<List<Candidate>> ProbeCandidatesAsync()
    {
        var found = new List<Candidate>();
        var local = LocalIpv4();
        if (local is null) return found;

        var bytes = local.GetAddressBytes();
        var prefix = $"{bytes[0]}.{bytes[1]}.{bytes[2]}.";
        var tasks = new List<Task>();
        var gate = new SemaphoreSlim(64);
        for (var host = 1; host <= 254; host++)
        {
            var ip = prefix + host;
            if (ip == local.ToString()) continue;
            foreach (var (port, kind) in new[] { (8883, "bambu"), (7125, "snapmaker") })
            {
                tasks.Add(Task.Run(async () =>
                {
                    await gate.WaitAsync();
                    try
                    {
                        using var tcp = new TcpClient();
                        var connect = tcp.ConnectAsync(ip, port);
                        if (await Task.WhenAny(connect, Task.Delay(400)) == connect && tcp.Connected)
                        {
                            lock (found) { found.Add(new Candidate(ip, port, kind)); }
                        }
                    }
                    catch { /* unreachable: not a candidate */ }
                    finally { gate.Release(); }
                }));
            }
        }
        await Task.WhenAll(tasks);
        return found.OrderBy(f => f.Ip).ThenBy(f => f.Port).ToList();
    }

    public static async Task<List<string>> ProbeSubnetAsync()
    {
        var lines = new List<string>();
        var local = LocalIpv4();
        if (local is null)
        {
            lines.Add("Could not determine this PC's local IPv4 address; discovery needs a LAN address.");
            return lines;
        }

        var bytes = local.GetAddressBytes();
        var prefix = $"{bytes[0]}.{bytes[1]}.{bytes[2]}.";
        lines.Add($"Probing {prefix}0/24 (this PC is {local}) — TCP connect to ports 8883 (Bambu) and 7125 (Moonraker), 400 ms timeout each.");

        var found = await ProbeCandidatesAsync();

        if (found.Count == 0)
        {
            lines.Add("No printer candidates found. Check the printers are on, on the same network, and in Developer/LAN mode (Bambu) or with Paxx installed (U1).");
            return lines;
        }

        foreach (var (ip, port, kind) in found)
        {
            lines.Add($"Candidate: {ip} port {port} ({kind}). Suggested config line — review before adding:");
            if (kind == "bambu")
                lines.Add($"  {{ \"name\": \"RENAME-ME\", \"type\": \"bambu\", \"ip\": \"{ip}\", \"serial\": \"YOUR_PRINTER_SERIAL\", \"access_code\": \"YOUR_BAMBU_LAN_ACCESS_CODE\", \"camera\": true }},");
            else
                lines.Add($"  {{ \"name\": \"RENAME-ME\", \"type\": \"snapmaker\", \"ip\": \"{ip}\", \"camera\": true }},");
        }
        lines.Add("Candidates are NOT added automatically. Add the lines you want to bridge-config.json, fill in the serial/access code, and restart the bridge.");
        return lines;
    }

    private static IPAddress? LocalIpv4()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    return addr.Address;
            }
        }
        return null;
    }
}
