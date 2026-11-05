// C3D Print Bridge — Google Drive for Desktop install helper.
//
// Installs Google's official Drive for Desktop sync app (the G:
// drive), so a fresh PC can read its Drive files straight from
// the file system as a fallback next to the bridge's own Drive
// handling. The installer is Google's public evergreen package —
// https://dl.google.com/drive-file-stream/GoogleDriveSetup.exe —
// from Google's own setup page (support.google.com/a/answer/
// 7491144), so unlike the Action1 client there is NO account-
// tied link to paste, NO prompt form, and NOTHING to store in
// bridge-config.json: the URL is a constant here, never logged
// with any account detail, and no credentials, tokens, or
// account names are ever embedded or sent anywhere.
//
// Install runs ELEVATED (UAC prompt) because Drive for Desktop
// is a per-machine install. Google's documented silent flags
// are --silent (plus --desktop_shortcut, --skip_launch_new,
// --gsuite_shortcuts=false); this helper uses --silent with
// --skip_launch_new (the tray menu reports the result, so the
// app must not pop open afterwards) and --gsuite_shortcuts=false
// (no Docs/Sheets/Slides shortcuts), and deliberately skips
// --desktop_shortcut so a bridge-driven install adds no desktop
// clutter. Signing in to Google inside the app is NOT part of
// the install and stays the owner's manual step.
// Success is NEVER claimed from an exit code alone: after the
// installer finishes, the bridge re-checks for Drive for
// Desktop on the machine and only then reports installed.

using System.Diagnostics;
using Microsoft.Win32;

namespace C3DBridge;

public static class DriveDesktopClient
{
    /// <summary>Google's official evergreen Drive for Desktop
    /// installer for Windows, from Google's own setup page
    /// (support.google.com/a/answer/7491144). Public, not
    /// account-tied — the same package for every PC.</summary>
    public const string DownloadUrl = "https://dl.google.com/drive-file-stream/GoogleDriveSetup.exe";

    /// <summary>Google's published uninstall registry key for
    /// Drive for Desktop (from the same support page).</summary>
    private const string UninstallKeyName = "{6BBAE539-2232-434A-A4E5-9A33560C6283}";

    /// <summary>True when Drive for Desktop looks present on
    /// this PC. Checks five independent signals, because no
    /// single one is assumed: Google's published uninstall
    /// key, an uninstall entry mentioning Google Drive, the
    /// program folder, a mounted virtual drive labelled
    /// Google Drive (default letter G:), or the sync process
    /// running. Any one signal counts as detected.</summary>
    public static bool IsInstalled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + UninstallKeyName);
            if (key is not null) return true;
        }
        catch { /* registry unavailable: fall through to other signals */ }

        try
        {
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                using var uninstall = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var sub = uninstall.OpenSubKey(name);
                    var display = sub?.GetValue("DisplayName") as string;
                    if (!string.IsNullOrWhiteSpace(display) &&
                        display.Contains("Google Drive", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        catch { }

        try
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (Directory.Exists(Path.Combine(programFiles, "Google", "Drive File Stream")) ||
                Directory.Exists(Path.Combine(programFilesX86, "Google", "Drive File Stream")))
                return true;
        }
        catch { }

        if (VirtualDriveLetter() is not null) return true;

        try
        {
            if (Process.GetProcessesByName("GoogleDriveFS").Length > 0) return true;
        }
        catch { }

        return false;
    }

    /// <summary>The mounted Drive for Desktop virtual drive
    /// (e.g. "G:\") when one is present, else null. Matched
    /// on the volume label, not the letter: Google's default
    /// is G: but the operator may have remapped it.</summary>
    public static string? VirtualDriveLetter()
    {
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady) continue;
                var label = drive.VolumeLabel ?? "";
                if (label.Contains("Google Drive", StringComparison.OrdinalIgnoreCase))
                    return drive.Name;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Download Google's official installer into the
    /// bridge temp folder and return the local path, or null
    /// with a reason. Runs on a worker thread by callers so
    /// the tray UI stays responsive. The URL is a public
    /// constant (see DownloadUrl) and is never logged with
    /// any account detail.</summary>
    public static async Task<(string? Path, string? Error)> DownloadAsync(CancellationToken ct = default)
    {
        try
        {
            var uri = new Uri(DownloadUrl);
            var dir = Config.TempDir;
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, "GoogleDriveSetup.exe");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await using var fs = File.Create(dest);
            await resp.Content.CopyToAsync(fs, ct);
            // The full package is ~277 MB: anything under
            // 50 MB is a failed or stub download, not an
            // installer.
            if (new FileInfo(dest).Length < 50_000_000)
            {
                try { File.Delete(dest); } catch { }
                return (null, "The Google Drive for Desktop download came back too small to be an installer — check the connection and try again.");
            }
            Program.Log("Google Drive for Desktop installer downloaded: GoogleDriveSetup.exe");
            return (dest, null);
        }
        catch (Exception ex)
        {
            Program.Log($"Google Drive for Desktop download failed: {ex.Message}");
            return (null, $"Google Drive for Desktop download failed: {ex.Message}");
        }
    }

    /// <summary>Install Drive for Desktop from a local installer
    /// file (normally the one DownloadAsync just fetched).
    /// Runs elevated, waits for the installer, then re-checks
    /// IsInstalled() — polling briefly, since the installer
    /// can take a moment to register after it exits — before
    /// reporting success.</summary>
    public static async Task<(bool Ok, string Detail)> InstallAsync(string installerPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
            return (false, "Google Drive for Desktop installer file not found. Download it from Google first, then try again.");

        var psi = new ProcessStartInfo
        {
            FileName = installerPath,
            // Google's documented silent flags (support.google.com/a/answer/7491144):
            // --silent installs with no prompts; --skip_launch_new keeps the app
            // from opening afterwards (the tray menu reports the result instead);
            // --gsuite_shortcuts=false skips the Docs/Sheets/Slides shortcuts.
            // --desktop_shortcut is Google's optional extra flag and is
            // deliberately left off: a bridge-driven install adds no desktop clutter.
            Arguments = "--silent --skip_launch_new --gsuite_shortcuts=false",
            UseShellExecute = true,
            Verb = "runas", // UAC elevation: per-machine install.
            CreateNoWindow = false,
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
                return (false, "Google Drive for Desktop installer did not start. Check the installer file and try again.");
            await proc.WaitForExitAsync(ct);
            Program.Log($"Google Drive for Desktop installer finished with exit code {proc.ExitCode}.");
            // The installer can take a moment to register after
            // it exits: poll detection briefly before reporting.
            for (var i = 0; i < 24; i++)
            {
                if (IsInstalled())
                    return (true, "Google Drive for Desktop installed. Open it and sign in to Google to see your Drive files — signing in is a separate step the app itself handles.");
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
            return (false, $"Google Drive for Desktop installer finished (exit code {proc.ExitCode}) but no Drive for Desktop install was detected afterwards. Restart the PC and try again, or install it by hand from google.com/drive/download.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 1223 = user cancelled the UAC elevation prompt.
            return (false, "Google Drive for Desktop install cancelled at the administrator prompt. Nothing was installed.");
        }
        catch (Exception ex)
        {
            Program.Log($"Google Drive for Desktop install failed: {ex.Message}");
            return (false, $"Google Drive for Desktop install failed: {ex.Message}");
        }
    }
}
