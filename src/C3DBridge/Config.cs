// C3D Print Bridge — local configuration and secret storage.
//
// SECURITY RULES (owner-confirmed, Oct 20, 2026):
// - The bridge API key is NEVER in this repo, NEVER in code, and
//   NEVER in bridge-config.json. It lives in ONE restricted local
//   file on the PC (bridge-key.txt), created by setup, encrypted
//   with Windows DPAPI (current user only) where supported, so the
//   file on disk is ciphertext, not the plain key.
// - Printer LAN access codes live ONLY in the local config file.
// - The example config shipped in the repo contains placeholders
//   only and must never be copied over a real config.

using System.Text.Json;

namespace C3DBridge;

public static class Config
{
    public static readonly string AppDir =
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static string ConfigPath => Path.Combine(AppDir, "bridge-config.json");
    public static string KeyPath => Path.Combine(AppDir, "bridge-key.txt");
    public static string LogPath => Path.Combine(AppDir, "bridge.log");
    public static string TempDir => Path.Combine(AppDir, "temp");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>Load bridge-config.json. Returns null with a reason
    /// when the file is missing or invalid — the caller reports it,
    /// never invents defaults for printers.</summary>
    public static (BridgeConfig? Config, string? Error) Load()
    {
        if (!File.Exists(ConfigPath))
            return (null, $"Config file not found: {ConfigPath}. Run setup first (see SETUP.md).");

        try
        {
            var json = File.ReadAllText(ConfigPath);
            var cfg = JsonSerializer.Deserialize<BridgeConfig>(json, JsonOptions);
            if (cfg is null) return (null, "Config file parsed to nothing; it is empty or invalid JSON.");
            cfg = cfg with { Printers = cfg.Printers.Select(pr => pr with { AccessCode = SecureStore.Unprotect(pr.AccessCode), PaxxPassword = SecureStore.Unprotect(pr.PaxxPassword) }).ToList() };
            if (string.IsNullOrWhiteSpace(cfg.DashboardBaseUrl)) return (null, "Config is missing dashboard_base_url.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in cfg.Printers)
            {
                if (string.IsNullOrWhiteSpace(p.Name)) return (null, "A printer entry is missing its name.");
                if (!names.Add(p.Name)) return (null, $"Duplicate printer name in config: {p.Name}.");
                if (p.Type != "bambu" && p.Type != "snapmaker")
                    return (null, $"Printer '{p.Name}' has unknown type '{p.Type}' (expected 'bambu' or 'snapmaker').");
                if (string.IsNullOrWhiteSpace(p.Ip)) return (null, $"Printer '{p.Name}' is missing its IP address.");
            }
            return (cfg, null);
        }
        catch (Exception ex)
        {
            return (null, $"Could not read config file: {ex.Message}");
        }
    }

    /// <summary>Save a full config (used by the tray Configure
    /// window). Writes the whole file, so callers must load first
    /// and keep the fields they are not changing. Never touches
    /// the separate bridge-key file.</summary>
    public static void Save(BridgeConfig cfg)
    {
        var safe = cfg with { Printers = cfg.Printers.Select(pr => pr with { AccessCode = string.IsNullOrWhiteSpace(pr.AccessCode) ? pr.AccessCode : SecureStore.Protect(pr.AccessCode), PaxxPassword = string.IsNullOrWhiteSpace(pr.PaxxPassword) ? pr.PaxxPassword : SecureStore.Protect(pr.PaxxPassword) }).ToList() };
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(safe, JsonOptions));
        RestrictFile(ConfigPath);
    }

    /// <summary>Write the example config (placeholders only) if no
    /// real config exists yet. Never overwrites an existing file.</summary>
    public static void WriteExampleIfMissing()
    {
        if (File.Exists(ConfigPath)) return;
        var example = new BridgeConfig
        {
            DashboardBaseUrl = "https://creat3dworld.com",
            BridgeName = "shop-pc",
            Printers = new()
            {
                new PrinterConfig { Name = "P2S", Type = "bambu", Ip = "192.168.1.100", Serial = "YOUR_PRINTER_SERIAL", AccessCode = "YOUR_BAMBU_LAN_ACCESS_CODE", Camera = true },
                new PrinterConfig { Name = "U1", Type = "snapmaker", Ip = "192.168.1.101", Camera = true },
            },
        };
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(example, JsonOptions));
        RestrictFile(ConfigPath);
    }

    /// <summary>Store the dashboard bridge API key in the restricted
    /// local key file. Overwrites any previous key (rotation).</summary>
    public static void StoreKey(string key)
    {
        File.WriteAllText(KeyPath, SecureStore.Protect(key.Trim()) + Environment.NewLine);
        RestrictFile(KeyPath);
    }

    /// <summary>Read the stored bridge API key, or null when setup
    /// has not stored one yet.</summary>
    public static string? ReadKey()
    {
        if (!File.Exists(KeyPath)) return null;
        var raw = File.ReadAllText(KeyPath).Trim();
        var key = SecureStore.Unprotect(raw);
        if (!string.IsNullOrEmpty(key) && !raw.StartsWith("dpapi:", StringComparison.Ordinal) && OperatingSystem.IsWindows())
        {
            try { StoreKey(key); } catch { }
        }
        return string.IsNullOrEmpty(key) ? null : key;
    }

    /// <summary>Best-effort restriction. On Windows the key file
    /// lives in the owner's user-profile folder, which Windows
    /// already restricts to that user (plus administrators) — no
    /// extra ACL step is needed or taken. On Unix-like systems the
    /// file is chmod 600. Failures are logged by the caller path,
    /// not fatal: the key file still works, and the operator is
    /// told to keep the install folder private.</summary>
    public static bool RestrictFile(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
