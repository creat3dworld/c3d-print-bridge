// C3D Print Bridge — system tray app (v1.1.0, owner-approved).
//
// When the bridge runs with no arguments on Windows (the normal
// Scheduled-Task-at-logon case), it lives here: a tray icon by
// the clock with Configure / Self-test / Get updates / Exit.
// The relay loop (Worker) runs in the background exactly as it
// did as a console app; the tray adds no decisions, no slicing,
// no listening port, and no inbound connections. The console
// window is hidden in tray mode; the log file still records
// everything, so nothing is lost by hiding the window.
//
// Configure saves a new config and restarts the relay loop with
// it. Get updates checks the repo for a newer version and, when
// one is confirmed, hands off to the installer and closes this
// app so the exe can be replaced (see Updater.cs). Exit stops
// the relay loop and removes the icon.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace C3DBridge;

public sealed class TrayApplicationContext : ApplicationContext
{
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private readonly NotifyIcon _icon;
    private CancellationTokenSource? _workerCts;
    private Task? _workerTask;
    private bool _exitRequested;

    public TrayApplicationContext()
    {
        // Hide the console window: tray mode is icon-only.
        try { ShowWindow(GetConsoleWindow(), 0); } catch { /* console may not exist */ }

        _icon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = $"C3D Print Bridge {Program.Version}",
            Visible = true,
        };
        var menu = new ContextMenuStrip();
        var configureItem = new ToolStripMenuItem("Configure…");
        configureItem.Click += (_, _) => ShowConfigure();
        var selfTestItem = new ToolStripMenuItem("Self-test");
        selfTestItem.Click += (_, _) => RunSelfTest();
        var updatesItem = new ToolStripMenuItem("Get updates…");
        updatesItem.Click += async (_, _) => await GetUpdatesAsync();
        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApp();
        menu.Items.Add(configureItem);
        menu.Items.Add(selfTestItem);
        menu.Items.Add(updatesItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => ShowConfigure();

        StartWorker();
    }

    private void StartWorker()
    {
        _workerCts = new CancellationTokenSource();
        var ct = _workerCts.Token;
        _workerTask = Task.Run(async () =>
        {
            var (cfg, error) = Config.Load();
            if (cfg is null)
            {
                Program.Log($"ERROR: {error}");
                Program.Log("Open Configure from the tray icon, fill in your printers, and save — the bridge will start with the new config.");
                return;
            }
            var apiKey = Config.ReadKey();
            using var dashboard = new DashboardClient(cfg.DashboardBaseUrl, cfg.BridgeName, apiKey);
            var registry = new PrinterRegistry(cfg.Printers);
            try { await new Worker(cfg, registry, dashboard).RunAsync(ct); }
            catch (OperationCanceledException) { /* restart or exit */ }
        }, ct);
    }

    private async void RestartWorker()
    {
        try
        {
            _workerCts?.Cancel();
            if (_workerTask is not null) await _workerTask;
        }
        catch { /* stopping is best-effort */ }
        if (!_exitRequested) StartWorker();
    }

    private void ShowConfigure()
    {
        var (cfg, error) = Config.Load();
        if (cfg is null)
        {
            // No usable config yet: start from the example shape so
            // the form still opens. Save writes a real config.
            cfg = new BridgeConfig
            {
                DashboardBaseUrl = "https://creat3dworld.com",
                BridgeName = "shop-pc",
                Printers = new(),
            };
            Program.Log($"Configure opened without a valid config ({error}); saving will create one.");
        }
        using var form = new ConfigureForm(cfg, Config.ReadKey() is not null);
        if (form.ShowDialog() == DialogResult.OK)
        {
            Program.Log("Config saved from the tray Configure window; restarting the relay loop.");
            RestartWorker();
        }
    }

    private void RunSelfTest()
    {
        // Self-test runs as its own visible console process so its
        // output is readable and this app keeps running.
        try
        {
            var exe = Environment.ProcessPath ?? Path.Combine(Config.AppDir, "C3DBridge.exe");
            Process.Start(new ProcessStartInfo(exe, "--self-test") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Self-test could not start: {ex.Message}", "C3D Print Bridge",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task GetUpdatesAsync()
    {
        try
        {
            var result = await Updater.CheckForUpdateAsync();
            switch (result.Kind)
            {
                case UpdateCheckResultKind.UpToDate:
                    MessageBox.Show(result.Detail, "C3D Print Bridge",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    break;
                case UpdateCheckResultKind.NeedsSignIn:
                case UpdateCheckResultKind.Failed:
                    MessageBox.Show(result.Detail, "C3D Print Bridge",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    break;
                case UpdateCheckResultKind.Available:
                    var answer = MessageBox.Show(
                        $"{result.Detail}\n\nUpdate now? The bridge will close so the installer can replace it, then start again with your config and key preserved.",
                        "C3D Print Bridge", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (answer != DialogResult.Yes) break;
                    if (!Updater.StartUpdate(result.LatestVersion!, out var reason))
                    {
                        MessageBox.Show($"Update could not start: {reason}", "C3D Print Bridge",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        break;
                    }
                    // Close now so the installer can overwrite the exe.
                    // The update script verifies the new version and
                    // reports its own result, then starts the bridge.
                    ExitApp();
                    break;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Get updates failed: {ex.Message}", "C3D Print Bridge",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ExitApp()
    {
        _exitRequested = true;
        try { _workerCts?.Cancel(); } catch { /* best effort */ }
        _icon.Visible = false;
        _icon.Dispose();
        Application.Exit();
    }
}
