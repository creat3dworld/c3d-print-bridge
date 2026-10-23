// C3D Print Bridge — Action1 client install helper (2277).
//
// The Action1 agent is ACCOUNT-TIED: the installer downloaded from
// the Action1 console (shop@creat3dworld.com account) carries that
// account's organisation binding in the file itself. The bridge
// NEVER downloads an installer, NEVER embeds an Action1 URL, token,
// organisation name, or account detail, and NEVER commits an
// installer path with a real value to the repo. The operator
// downloads the agent from their own Action1 console, picks that
// local file once through the tray menu, and the bridge stores only
// that local path in bridge-config.json on the PC (local only,
// like every other bridge setting, preserved by the installer).
//
// Install runs ELEVATED (UAC prompt) because an agent is a
// per-machine install. Action1's own documented unattended
// arguments are /quiet and /norestart, with /L*V for a log.
// Success is NEVER claimed from an exit code alone: after the
// installer finishes, the bridge re-checks for an Action1 agent
// on the machine and only then reports installed.

using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;

namespace C3DBridge;

public static class Action1Client
{
    /// <summary>True when an Action1 agent looks present on this PC.
    /// Checks three independent signals, because the exact service
    /// name is not assumed: any Windows service mentioning
    /// Action1, an Action1 program folder, or an uninstall entry
    /// mentioning Action1. Any one signal counts as detected.</summary>
    public static bool IsInstalled()
    {
        try
        {
            var services = ServiceController.GetServices();
            if (services.Any(s =>
                (s.ServiceName?.Contains("Action1", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (s.DisplayName?.Contains("Action1", StringComparison.OrdinalIgnoreCase) ?? false)))
                return true;
        }
        catch { /* service enumeration unavailable: fall through to other signals */ }

        try
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (Directory.Exists(Path.Combine(programFiles, "Action1")) ||
                Directory.Exists(Path.Combine(programFilesX86, "Action1")))
                return true;
        }
        catch { }

        try
        {
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                using var uninstall = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(name);
                    var display = key?.GetValue("DisplayName") as string;
                    if (!string.IsNullOrWhiteSpace(display) &&
                        display.Contains("Action1", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        catch { }

        return false;
    }

    /// <summary>Install the account-tied agent from a local installer
    /// file the operator already downloaded from their Action1
    /// console. Runs elevated, waits for the installer, then
    /// re-checks IsInstalled() before reporting success.</summary>
    public static async Task<(bool Ok, string Detail)> InstallAsync(string installerPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
            return (false, "Action1 installer file not found. Download the agent from your Action1 console first, then pick that file again.");

        var logPath = Path.Combine(Config.AppDir, "action1_install.log");
        var ext = Path.GetExtension(installerPath).ToLowerInvariant();
        var psi = new ProcessStartInfo
        {
            UseShellExecute = true,
            Verb = "runas", // UAC elevation: agent install is per-machine.
            CreateNoWindow = false,
        };
        if (ext == ".msi")
        {
            psi.FileName = "msiexec.exe";
            psi.Arguments = $"/i \"{installerPath}\" /quiet /norestart /L*V \"{logPath}\"";
        }
        else
        {
            // .exe wrapper from the console: same documented args.
            psi.FileName = installerPath;
            psi.Arguments = $"/quiet /norestart /L*V \"{logPath}\"";
        }

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
                return (false, "Action1 installer did not start. Check the installer file and try again.");
            await proc.WaitForExitAsync(ct);
            Program.Log($"Action1 installer finished with exit code {proc.ExitCode}; log: {logPath}");
            if (IsInstalled())
                return (true, "Action1 client installed. This PC should now appear as an endpoint in your Action1 console — allow a minute for it to check in.");
            return (false, $"Action1 installer finished (exit code {proc.ExitCode}) but no Action1 agent was detected afterwards. Check {logPath} for the installer's own error.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 1223 = user cancelled the UAC elevation prompt.
            return (false, "Action1 install cancelled at the administrator prompt. Nothing was installed.");
        }
        catch (Exception ex)
        {
            Program.Log($"Action1 install failed: {ex.Message}");
            return (false, $"Action1 install failed: {ex.Message}");
        }
    }
}
