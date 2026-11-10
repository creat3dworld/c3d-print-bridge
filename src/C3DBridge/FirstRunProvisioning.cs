// C3D Print Bridge — first-run provisioning (2026.1109).
//
// Two ways a new install gets its bridge key with NO typing or
// pasting. The dashboard-prepackaged installer is the PRIMARY
// route; the pairing-code exchange is the FALLBACK for a bridge
// installed from a GitHub file.
//
//  1. PREPACKAGED (primary): the dashboard builds a one-time
//     installer download that drops bridge-setup.json next to
//     the exe: { dashboard_url, machine_id, machine_name,
//     bridge_key }. On first start Apply() stores the key via
//     Config.StoreKey (DPAPI on Windows), writes the dashboard
//     URL + machine identity into bridge-config.json, deletes
//     bridge-setup.json, and logs that provisioning completed.
//     The key value is never logged, never printed, never in
//     an exception message. If a key is already stored, the
//     setup file is IGNORED — a re-install can never overwrite
//     the live key — and deleted when it carries a key, so no
//     plaintext key lies around afterwards.
//  2. PAIRING CODE (fallback): `C3DBridge.exe setup
//     --pairing-code <code> --dashboard <url> --machine <name>`
//     exchanges the single-use code at the dashboard's
//     /bridge/pairing/exchange endpoint and stores the
//     returned bridge_key the same way. On any failure it
//     prints the exact error and exits non-zero; a partial
//     key is never stored.
//
// This class deliberately does NOT call Program.Log: it logs
// with its own Log() (same timestamped format) so it can be
// compiled and tested on its own, without the WinForms entry
// point. The key-format check below mirrors
// DashboardClient.IsValidRotatedKey and the PROTOCOL.md
// contract (dashboard keys are bin2hex(random_bytes(32)):
// exactly 64 lowercase hex chars) — if the dashboard ever
// changes its key format, both places must change together.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace C3DBridge;

public static class FirstRunProvisioning
{
    /// <summary>Prepackaged setup file, written next to the exe by
    /// the dashboard-built installer. Single use: consumed (and
    /// deleted) on first start.</summary>
    public static string SetupPath => Path.Combine(Config.AppDir, "bridge-setup.json");

    private const string DefaultDashboardUrl = "https://creat3dworld.com";
    private const string PairingExchangePath = "/wp-json/c3d-shipping/v1/bridge/pairing/exchange";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>The dashboard-prepackaged setup file. All fields
    /// optional except bridge_key: a missing dashboard_url means
    /// the default site, a missing machine_id means this PC's
    /// hostname slug, a missing machine_name means the hostname.</summary>
    public sealed record SetupFile
    {
        [JsonPropertyName("dashboard_url")] public string? DashboardUrl { get; init; }
        [JsonPropertyName("machine_id")] public string? MachineId { get; init; }
        [JsonPropertyName("machine_name")] public string? MachineName { get; init; }
        [JsonPropertyName("bridge_key")] public string? BridgeKey { get; init; }
        /// <summary>BRIDGE-CONFIG HEALTH (2026.1109.2139,
        /// CONTRACT.md §Q.5): the dashboard's slicer
        /// default install locations, keyed by slicer word
        /// to a default install directory. Optional; absent
        /// means the C:\Program Files fallback. Read here
        /// and persisted into bridge-config.json so
        /// SlicerSetup can discover slicers there.</summary>
        [JsonPropertyName("slicer_default_locations")] public Dictionary<string, string>? SlicerDefaultLocations { get; init; }
    }

    private static readonly object LogLock = new();

    private static void Log(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
        lock (LogLock)
        {
            Console.WriteLine(line);
            try { File.AppendAllText(Config.LogPath, line + Environment.NewLine); }
            catch { /* logging must never crash the bridge */ }
        }
    }

    /// <summary>Consume bridge-setup.json on first start, if one
    /// is present. No-op when the file is absent. Never throws:
    /// a bad setup file is logged and left in place for the
    /// operator instead of crashing the bridge. The key value
    /// is never logged.</summary>
    public static void Apply()
    {
        if (!File.Exists(SetupPath)) return;

        SetupFile? setup;
        try
        {
            setup = JsonSerializer.Deserialize<SetupFile>(File.ReadAllText(SetupPath), JsonOptions);
        }
        catch (Exception ex)
        {
            Log($"WARNING: bridge-setup.json could not be read ({ex.Message}); leaving it in place — fix it or delete it by hand.");
            return;
        }
        if (setup is null)
        {
            Log("WARNING: bridge-setup.json is empty; leaving it in place.");
            return;
        }

        var hasKey = !string.IsNullOrWhiteSpace(setup.BridgeKey);

        if (Config.ReadKey() is not null)
        {
            // A key is already stored: the packaged file is
            // ignored (a re-install never overwrites the live
            // key) and deleted when it carries a key, so no
            // plaintext key lies around afterwards.
            if (hasKey)
            {
                try
                {
                    File.Delete(SetupPath);
                    Log("bridge-setup.json removed — a bridge key is already stored, so the packaged key was ignored (never logged).");
                }
                catch (Exception ex)
                {
                    Log($"WARNING: bridge-setup.json could not be deleted ({ex.Message}); it still holds a plaintext key — delete it by hand.");
                }
            }
            return;
        }

        if (!hasKey)
        {
            Log("WARNING: bridge-setup.json has no bridge_key; nothing provisioned. Delete it or replace it with a fresh package from the dashboard.");
            return;
        }

        var key = setup.BridgeKey!.Trim();
        if (!IsValidBridgeKey(key))
        {
            Log("ERROR: bridge-setup.json carries a bridge_key that is not a dashboard-issued key (64 hex chars); nothing stored. Get a fresh package from the dashboard.");
            return;
        }

        var dashboardUrl = NormalizeDashboardUrl(setup.DashboardUrl);
        var machineName = string.IsNullOrWhiteSpace(setup.MachineName) ? Environment.MachineName : setup.MachineName!.Trim();
        var (machineId, problem) = MachineInfo.NormalizeMachineId(setup.MachineId);
        if (machineId is null)
        {
            if (problem is not null)
                Log($"WARNING: packaged machine_id rejected ({problem}); using this PC's hostname instead.");
            machineId = SlugifyMachineName(machineName);
        }

        try
        {
            Config.StoreKey(key);
        }
        catch (Exception ex)
        {
            Log($"ERROR: bridge key from bridge-setup.json could not be stored ({ex.Message}); setup file left in place.");
            return;
        }

        try
        {
            ApplyIdentityToConfig(dashboardUrl, machineId, machineName, setup.SlicerDefaultLocations);
        }
        catch (Exception ex)
        {
            Log($"ERROR: key stored, but dashboard/machine identity could not be written to bridge-config.json ({ex.Message}); setup file left in place — the bridge runs with the stored key and the existing config.");
            return;
        }

        try
        {
            File.Delete(SetupPath);
        }
        catch (Exception ex)
        {
            Log($"WARNING: provisioning completed, but bridge-setup.json could not be deleted ({ex.Message}); delete it by hand — it still holds the plaintext key.");
            return;
        }

        Log($"First-run provisioning completed: bridge key stored, dashboard {dashboardUrl}, machine {machineId} ({machineName}).");
    }

    /// <summary>Pairing-code setup command (fallback route):
    /// exchange a single-use code for the bridge key, store it,
    /// and write the dashboard URL + machine identity into the
    /// config. Returns 0 on success, 1 on any failure — and on
    /// failure nothing is stored, so a bad code can never leave
    /// a partial key behind. The key value is never printed.</summary>
    public static async Task<int> RunPairingSetupAsync(string code, string? dashboardUrl, string? machineNameArg)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            Console.WriteLine("No pairing code given. Use: C3DBridge.exe setup --pairing-code <code> [--dashboard <url>] [--machine <name>]");
            return 1;
        }

        var dashboard = NormalizeDashboardUrl(dashboardUrl);
        var machineName = string.IsNullOrWhiteSpace(machineNameArg) ? Environment.MachineName : machineNameArg!.Trim();
        var machineId = SlugifyMachineName(machineName);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("C3DBridge/1.0");
        var body = JsonSerializer.Serialize(new
        {
            code = code.Trim(),
            machine_id = machineId,
            machine_name = machineName,
        }, JsonOptions);

        HttpResponseMessage res;
        try
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            res = await http.PostAsync(dashboard + PairingExchangePath, content);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Pairing exchange failed: could not reach the dashboard at {dashboard} ({ex.Message}). Nothing stored.");
            return 1;
        }

        using (res)
        {
            var text = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                Console.WriteLine($"Pairing exchange failed: HTTP {(int)res.StatusCode} {res.ReasonPhrase}. {SafeErrorDetail(text)}");
                Console.WriteLine("Nothing stored. Get a fresh pairing code from the dashboard (codes are single-use and expire after 30 minutes).");
                return 1;
            }

            // Parse + validate the reply first: a bad reply stores
            // nothing, even though the HTTP call succeeded.
            var (key, respMachineId, respMachineName) = ParseExchangeResponse(text);
            if (key is null)
            {
                Console.WriteLine("Pairing exchange failed: the dashboard did not return a valid bridge key. Nothing stored.");
                return 1;
            }

            var finalId = respMachineId is not null && MachineInfo.NormalizeMachineId(respMachineId).Id is string rid
                ? rid
                : machineId;
            var finalName = string.IsNullOrWhiteSpace(respMachineName) ? machineName : respMachineName!.Trim();

            try
            {
                Config.StoreKey(key.Trim());
                ApplyIdentityToConfig(dashboard, finalId, finalName);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Pairing exchange succeeded and the key was stored, but the dashboard/machine identity could not be written to bridge-config.json ({ex.Message}). Edit bridge-config.json by hand (dashboard_url, machine_id, machine_name), or delete bridge-key.txt and run setup again with a fresh pairing code.");
                return 1;
            }

            Console.WriteLine($"Bridge key stored for machine {finalId} ({finalName}); dashboard {dashboard}.");
            Console.WriteLine("The key is in the restricted local key file on this PC and was not printed.");
            return 0;
        }
    }

    /// <summary>Parse a pairing-exchange reply: (bridge_key,
    /// machine_id, machine_name), or (null, null, null) when the
    /// reply is unreadable or carries no valid dashboard key.
    /// Extracted so the reply handling is testable without a
    /// live HTTP round-trip; the transport itself follows the
    /// same pattern as DashboardClient.</summary>
    internal static (string? Key, string? MachineId, string? MachineName) ParseExchangeResponse(string body)
    {
        string? key;
        string? respMachineId = null;
        string? respMachineName = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            key = root.TryGetProperty("bridge_key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
            if (root.TryGetProperty("machine_id", out var mi) && mi.ValueKind == JsonValueKind.String) respMachineId = mi.GetString();
            if (root.TryGetProperty("machine_name", out var mn) && mn.ValueKind == JsonValueKind.String) respMachineName = mn.GetString();
        }
        catch
        {
            return (null, null, null);
        }
        if (string.IsNullOrWhiteSpace(key) || !IsValidBridgeKey(key.Trim()))
            return (null, null, null);
        return (key.Trim(), respMachineId, respMachineName);
    }

    /// <summary>Write the dashboard URL + machine identity into
    /// bridge-config.json, preserving everything else (printers,
    /// slicer paths, intervals). Creates a minimal config when
    /// none exists yet. The packaged slicer default locations,
    /// when present, are merged in (non-blank values only) so
    /// SlicerSetup discovers slicers there. Throws when an existing
    /// config file cannot be read — a broken config is never
    /// silently overwritten with a fresh one that would lose printers.</summary>
    private static void ApplyIdentityToConfig(string dashboardUrl, string machineId, string machineName,
        IReadOnlyDictionary<string, string>? slicerDefaultLocations = null)
    {
        BridgeConfig cfg;
        if (!File.Exists(Config.ConfigPath))
        {
            cfg = new BridgeConfig
            {
                DashboardBaseUrl = dashboardUrl,
                BridgeName = machineId,
                MachineId = machineId,
                MachineName = machineName,
                Printers = new(),
            };
        }
        else
        {
            var (existing, error) = Config.Load();
            if (existing is null)
                throw new InvalidOperationException($"bridge-config.json could not be read: {error}");
            cfg = existing with
            {
                DashboardBaseUrl = dashboardUrl,
                MachineId = machineId,
                MachineName = machineName,
            };
        }
        if (slicerDefaultLocations is not null)
        {
            var defaults = new Dictionary<string, string>(cfg.SlicerDefaultLocations, StringComparer.OrdinalIgnoreCase);
            foreach (var (slicer, location) in slicerDefaultLocations)
            {
                if (!string.IsNullOrWhiteSpace(location))
                    defaults[slicer] = location.Trim();
            }
            cfg = cfg with { SlicerDefaultLocations = defaults };
        }
        Config.Save(cfg);
    }

    /// <summary>Dashboard keys are bin2hex(random_bytes(32)):
    /// exactly 64 lowercase hex chars. Anything else is not a
    /// key the dashboard generated — never store it, so a
    /// malformed or hostile reply can never corrupt the key
    /// file. Mirrors DashboardClient.IsValidRotatedKey.</summary>
    private static bool IsValidBridgeKey(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate) || candidate.Length != 64) return false;
        foreach (var ch in candidate)
        {
            var ok = (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F');
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>Turn a PC/hostname into a machine_id slug:
    /// lowercase, [a-z0-9_-] only, anything else becomes "-"
    /// (collapsed, trimmed). "3D1PC" -&gt; "3d1pc". Never empty:
    /// a name with no usable characters becomes "bridge".</summary>
    public static string SlugifyMachineName(string? name)
    {
        var sb = new StringBuilder();
        var lastDash = false;
        foreach (var ch in (name ?? "").Trim().ToLowerInvariant())
        {
            var ok = (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-' || ch == '_';
            if (ok)
            {
                sb.Append(ch);
                lastDash = false;
            }
            else if (!lastDash && sb.Length > 0)
            {
                sb.Append('-');
                lastDash = true;
            }
        }
        var slug = sb.ToString().Trim('-', '_');
        return slug.Length == 0 ? "bridge" : slug;
    }

    private static string NormalizeDashboardUrl(string? raw)
    {
        var url = (raw ?? "").Trim();
        if (url.Length == 0) return DefaultDashboardUrl;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            Log($"WARNING: dashboard_url \"{url}\" is not a valid http(s) URL; using {DefaultDashboardUrl}.");
            return DefaultDashboardUrl;
        }
        return uri.ToString().TrimEnd('/');
    }

    /// <summary>Error detail safe to print: truncated, and
    /// withheld entirely when it mentions a key (a failure
    /// body must never be the channel a key leaks through).</summary>
    private static string SafeErrorDetail(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var detail = text.Trim();
        if (detail.Contains("bridge_key", StringComparison.OrdinalIgnoreCase))
            return "(dashboard error detail withheld — it mentioned a key)";
        if (detail.Length > 300) detail = detail[..300] + "…";
        return detail;
    }
}
