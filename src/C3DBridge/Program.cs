// C3D Print Bridge — entry point.
//
// Usage:
//   C3DBridge                 Tray icon + relay loop (default on
//                             Windows; console relay on other OSes).
//   C3DBridge --configure     Open the tray Configure window
//                             directly (Windows only).
//   C3DBridge setup           Store the dashboard bridge API key
//                             (masked prompt; never echoed, never
//                             written anywhere but the restricted
//                             local key file).
//   C3DBridge setup --key K   Same, with the key as an argument
//                             (note: the key may land in shell
//                             history — prefer the prompt form).
//   C3DBridge --self-test     Check config, key, dashboard reach,
//                             bridge endpoints, and each printer.
//   C3DBridge --discover      Probe this PC's own subnet for printer
//                             candidates (suggestions only; the
//                             config file is never edited by it —
//                             use Configure + Scan to save found
//                             printers instead).
//   C3DBridge --version       Print the version and exit.
//
// The bridge never listens on any port and never accepts inbound
// connections. It only calls out: to the dashboard (HTTPS) and to
// the printers on the LAN.

using System.Net;
using System.Windows.Forms;
using C3DBridge.Printers;

namespace C3DBridge;

public static class Program
{
    public const string Version = "2026.1021.2263";

    // Single-instance, newest-wins: only one bridge runs at a time.
    // A running instance writes its version + pid to a temp file.
    // A new instance that is NEWER kills the older running process
    // and takes over; a new instance that is older or the same exits.
    // Short commands (--version, setup, --self-test, --configure,
    // --discover) skip this check entirely.
    private static System.Threading.Mutex? _singleInstanceMutex;
    private static string RunningFilePath => Path.Combine(Path.GetTempPath(), "C3DBridge-running.json");
    private static string SortableVersion(string v)
    {
        // Current format is YYYY.MMDD.HHMM (dotted, zero-padded),
        // optionally with a .# suffix for same-minute revisions.
        // Anything else (1.1.0, DDMMYYYYHHMM, etc.) is treated as older.
        // Parts are zero-padded so .10 correctly sorts above .2.
        var parts = v.Split('.');
        if ((parts.Length == 3 || parts.Length == 4) && parts.All(x => x.All(char.IsDigit)))
            return string.Join(".", parts.Select(x => x.PadLeft(4, '0')));
        return "0000";
    }
    private static bool IsNewer(string current, string running)
        => string.CompareOrdinal(SortableVersion(current), SortableVersion(running)) > 0;
    private static bool TryTakeSingleInstance()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var mutex = new System.Threading.Mutex(true, "C3DPrintBridgeSingleInstance", out bool createdNew);
            if (createdNew)
            {
                _singleInstanceMutex = mutex;
                File.WriteAllText(RunningFilePath, System.Text.Json.JsonSerializer.Serialize(new { version = Version, pid = Environment.ProcessId }));
                return true;
            }
            mutex.Dispose();
            // Another instance holds the mutex. Read its version/pid.
            string runningVersion = "";
            int runningPid = 0;
            try
            {
                var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(RunningFilePath));
                runningVersion = doc.RootElement.GetProperty("version").GetString() ?? "";
                runningPid = doc.RootElement.GetProperty("pid").GetInt32();
            }
            catch { /* stale/missing file: fall through to pid check below */ }
            bool alive = false;
            try { if (runningPid > 0) { var proc = System.Diagnostics.Process.GetProcessById(runningPid); alive = proc.ProcessName.Contains("C3DBridge", StringComparison.OrdinalIgnoreCase); } } catch { alive = false; }
            if (!alive)
            {
                try { File.Delete(RunningFilePath); } catch { }
                continue; // stale lock, retry acquiring
            }
            if (IsNewer(Version, runningVersion))
            {
                try
                {
                    var old = System.Diagnostics.Process.GetProcessById(runningPid);
                    old.Kill();
                    old.WaitForExit(5000);
                }
                catch { }
                try { File.Delete(RunningFilePath); } catch { }
                continue; // retry acquiring, now as the newest
            }
            // Older or same: exit, do not run side by side.
            if (OperatingSystem.IsWindows())
                System.Windows.Forms.MessageBox.Show($"C3D Print Bridge {runningVersion} is already running. Only the newest version runs, so this version ({Version}) will exit.", "C3D Print Bridge");
            else
                Console.WriteLine($"Bridge {runningVersion} is already running; this version ({Version}) will exit.");
            return false;
        }
        return false;
    }
    private static void ReleaseSingleInstance()
    {
        try
        {
            if (File.Exists(RunningFilePath))
            {
                var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(RunningFilePath));
                if (doc.RootElement.GetProperty("pid").GetInt32() == Environment.ProcessId) File.Delete(RunningFilePath);
            }
        }
        catch { }
        try { _singleInstanceMutex?.ReleaseMutex(); _singleInstanceMutex?.Dispose(); } catch { }
    }

    private static readonly object LogLock = new();

    public static void Log(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
        lock (LogLock)
        {
            Console.WriteLine(line);
            try { File.AppendAllText(Config.LogPath, line + Environment.NewLine); }
            catch { /* logging must never crash the bridge */ }
        }
    }

    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Contains("--version"))
        {
            Console.WriteLine($"C3D Print Bridge {Version}");
            return 0;
        }

        if (args.Contains("--configure") && OperatingSystem.IsWindows())
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var (loadedCfg, _) = Config.Load();
            var cfgForForm = loadedCfg ?? new BridgeConfig
            {
                DashboardBaseUrl = "https://creat3dworld.com",
                BridgeName = "shop-pc",
                Printers = new(),
            };
            using var form = new ConfigureForm(cfgForForm, Config.ReadKey() is not null);
            return form.ShowDialog() == DialogResult.OK ? 0 : 1;
        }

        if (args.Contains("--discover"))
        {
            foreach (var line in await Discovery.ProbeSubnetAsync())
                Console.WriteLine(line);
            return 0;
        }

        if (args.Contains("setup"))
        {
            var keyIndex = Array.IndexOf(args, "--key");
            string? key = keyIndex >= 0 && keyIndex + 1 < args.Length ? args[keyIndex + 1] : null;
            if (key is null)
            {
                Console.Write("Paste the bridge API key from the dashboard settings (input hidden): ");
                key = ReadMaskedLine();
                Console.WriteLine();
            }
            if (string.IsNullOrWhiteSpace(key))
            {
                Console.WriteLine("No key entered; nothing stored.");
                return 1;
            }
            Config.StoreKey(key);
            Console.WriteLine("Bridge API key stored in the restricted local key file on this PC.");
            Console.WriteLine("It is not in the repo, not in the config file, and was not printed back.");
            return 0;
        }

        var (cfg, error) = Config.Load();
        if (cfg is null)
        {
            // Tray mode still opens: the user can fix the config
            // from Configure instead of editing JSON by hand. The
            // tray context itself logs this and keeps running.
            if (args.Length == 0 && OperatingSystem.IsWindows())
            {
                if (!TryTakeSingleInstance()) return 0;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApplicationContext());
                ReleaseSingleInstance();
                return 0;
            }
            Console.WriteLine($"ERROR: {error}");
            Console.WriteLine("Run the installer (see SETUP.md) to create a config file, then edit it with your printers.");
            return 1;
        }

        if (args.Contains("--self-test"))
            return await SelfTestAsync(cfg);

        if (args.Length == 0 && OperatingSystem.IsWindows())
        {
            if (!TryTakeSingleInstance()) return 0;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApplicationContext());
            ReleaseSingleInstance();
            return 0;
        }

        var apiKey = Config.ReadKey();
        using var dashboard = new DashboardClient(cfg.DashboardBaseUrl, cfg.BridgeName, apiKey);
        var registry = new PrinterRegistry(cfg.Printers);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        Log("C3D Print Bridge starting. Press Ctrl+C to stop.");
        SleepPreventer.Prevent();
        await new Worker(cfg, registry, dashboard).RunAsync(cts.Token);
        SleepPreventer.Allow();
        Log("C3D Print Bridge stopped.");
        return 0;
    }

    /// <summary>Plain-language health check. Exit 0 means the config
    /// is valid and the dashboard host answers; printer failures
    /// are printed as FAIL lines (a printer may simply be off) and
    /// do not change the exit code — the owner reads the report.</summary>
    private static async Task<int> SelfTestAsync(BridgeConfig cfg)
    {
        var ok = true;
        Console.WriteLine($"C3D Print Bridge {Version} — self-test");
        Console.WriteLine($"Config: {Config.ConfigPath} — OK ({cfg.Printers.Count} printer(s)).");

        var key = Config.ReadKey();
        Console.WriteLine(key is null
            ? "Bridge API key: MISSING — dashboard reporting will be disabled until setup stores one (see SETUP.md)."
            : "Bridge API key: stored locally (value never printed).");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            using var res = await http.GetAsync(cfg.DashboardBaseUrl.TrimEnd('/') + "/wp-json/");
            Console.WriteLine($"Dashboard host: {(res.IsSuccessStatusCode ? "PASS" : $"FAIL (HTTP {(int)res.StatusCode})")} — {cfg.DashboardBaseUrl}");
            if (!res.IsSuccessStatusCode) ok = false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Dashboard host: FAIL — {ex.Message}");
            ok = false;
        }

        // The jobs endpoint distinguishes the three states that
        // matter: 404 = endpoints not installed yet (later plugin
        // update), 401 = endpoints live but key missing/rejected,
        // 200 = endpoints live and key accepted.
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get,
                cfg.DashboardBaseUrl.TrimEnd('/') + "/wp-json/c3d-shipping/v1/bridge/jobs?bridge=" + Uri.EscapeDataString(cfg.BridgeName));
            if (key is not null) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            using var res = await http.SendAsync(req);
            Console.WriteLine(res.StatusCode switch
            {
                HttpStatusCode.OK => "Bridge endpoints: PASS — dashboard accepted the key.",
                HttpStatusCode.Unauthorized => "Bridge endpoints: INSTALLED, key rejected — re-copy the key from the dashboard settings and run setup again.",
                HttpStatusCode.NotFound => "Bridge endpoints: NOT INSTALLED YET — they ship in a later plugin update. Local printer checks below still apply.",
                var s => $"Bridge endpoints: unexpected HTTP {(int)s}.",
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Bridge endpoints: could not check — {ex.Message}");
        }

        var registry = new PrinterRegistry(cfg.Printers);
        foreach (var adapter in registry.All)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var st = await adapter.GetStatusAsync(cts.Token);
                Console.WriteLine($"Printer '{st.Name}': {st.State.ToUpperInvariant()}{(string.IsNullOrEmpty(st.Detail) ? "" : " — " + st.Detail)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Printer '{adapter.Name}': FAIL — {ex.Message}");
            }
        }

        Console.WriteLine(ok ? "Self-test done." : "Self-test done with failures above.");
        return ok ? 0 : 1;
    }

    private static string ReadMaskedLine()
    {
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace && sb.Length > 0)
            {
                sb.Length--;
                Console.Write("\b \b");
            }
            else if (!char.IsControl(key.KeyChar))
            {
                sb.Append(key.KeyChar);
                Console.Write("*");
            }
        }
        return sb.ToString();
    }
}
