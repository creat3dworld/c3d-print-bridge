// C3D Print Bridge — "Get updates" (tray menu, v1.1.0).
//
// PUBLIC-FIRST DESIGN (owner decision, Oct 20, 2026: the repo is
// being moved from private to public; the flip was in progress,
// not confirmed, when this was written, so both paths exist):
//   1. Try an UNAUTHENTICATED check first: fetch Program.cs from
//      the public raw URL and read its Version constant. No token
//      is stored on the PC, and no token is ever asked for here.
//   2. Only if that fetch returns 404 (the repo is still private)
//      fall back to the GitHub CLI sign-in: if `gh auth status`
//      passes, fetch the same file through `gh api` and read its
//      version. If `gh` is not signed in, report that plainly and
//      stop — never fake a version check, never claim up-to-date
//      on a failed check.
// A network failure or any other error is reported as FAILED
// with its reason, never as up-to-date.
//
// Updating, when a newer version is confirmed, reuses the normal
// installer in the existing source folder (or a fresh public
// download of it when the source folder has no git history).
// bridge-config.json and bridge-key.txt are never touched by
// the update: the installer only overwrites the program itself.
// After the installer finishes, the update script verifies the
// new version by running the new exe with --version and reports
// the result itself, because the tray app has already closed by
// then (its exe would otherwise be locked on Windows).

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace C3DBridge;

public enum UpdateCheckResultKind
{
    UpToDate,
    Available,
    NeedsSignIn,
    Failed,
}

public sealed record UpdateCheckResult(UpdateCheckResultKind Kind, string CurrentVersion, string? LatestVersion, string Detail);

public static class Updater
{
    // Exact repo name as verified on GitHub (owner lower-case,
    // repo with capital C). Raw + API URLs are case-insensitive
    // on GitHub, but keep the verified spelling anyway.
    public const string RepoOwner = "creat3dworld";
    public const string RepoName = "C3d-print-bridge";
    public const string Branch = "main";

    private static string RawUrl =>
        $"https://raw.githubusercontent.com/{RepoOwner}/{RepoName}/{Branch}/src/C3DBridge/Program.cs";

    // Version scheme (Oct 21, 2026): DDMMYYYYHHMM, 24-hour, to the minute
    // (e.g. 211020261941 = Oct 21, 2026, 19:41). Old dotted versions
    // (1.1.0 etc.) are still parsed, and any DDMMYYYYHHMM version is
    // treated as newer than any dotted version.
    private static string? ParseVersion(string programCs)
    {
        var m = Regex.Match(programCs, @"Version\s*=\s*""(\d{12})""");
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(programCs, @"Version\s*=\s*""(\d+\.\d+\.\d+)""");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static int CompareVersions(string a, string b)
    {
        var aStamp = a.Length == 12 && a.All(char.IsDigit);
        var bStamp = b.Length == 12 && b.All(char.IsDigit);
        if (aStamp && bStamp) return string.CompareOrdinal(a, b);
        if (aStamp) return 1;
        if (bStamp) return -1;
        var pa = a.Split('.').Select(s => int.TryParse(s, out var n) ? n : 0).ToArray();
        var pb = b.Split('.').Select(s => int.TryParse(s, out var n) ? n : 0).ToArray();
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var x = i < pa.Length ? pa[i] : 0;
            var y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    private static async Task<(bool Ok, string Output)> RunGhAsync(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("gh", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return (false, "gh could not be started.");
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return (proc.ExitCode == 0, (stdout + stderr).Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static async Task<UpdateCheckResult> CheckForUpdateAsync()
    {
        var current = Program.Version;
        // Path 1: public, unauthenticated.
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var res = await http.GetAsync(RawUrl);
            if (res.IsSuccessStatusCode)
            {
                var text = await res.Content.ReadAsStringAsync();
                var latest = ParseVersion(text);
                if (latest is null)
                    return new UpdateCheckResult(UpdateCheckResultKind.Failed, current, null,
                        "The version file was fetched but its version could not be read.");
                if (CompareVersions(latest, current) <= 0)
                    return new UpdateCheckResult(UpdateCheckResultKind.UpToDate, current, latest,
                        $"Already up to date (v{current}).");
                return new UpdateCheckResult(UpdateCheckResultKind.Available, current, latest,
                    $"Version v{latest} is available (you have v{current}).");
            }
            if (res.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                return new UpdateCheckResult(UpdateCheckResultKind.Failed, current, null,
                    $"Version check failed: the update server answered HTTP {(int)res.StatusCode}.");
            }
            // 404 on the raw URL: either the repo is still private
            // or the file moved. Fall through to the gh path, which
            // distinguishes those cases by its own result.
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(UpdateCheckResultKind.Failed, current, null,
                $"Version check failed: {ex.Message}");
        }

        // Path 2 (fallback only): repo still private, gh sign-in.
        var (authOk, _) = await RunGhAsync("auth status");
        if (!authOk)
        {
            return new UpdateCheckResult(UpdateCheckResultKind.NeedsSignIn, current, null,
                "Could not check for updates: the repo still looks private and GitHub is not signed in on this PC. Run 'gh auth login' in PowerShell once, then try Get updates again — or reinstall with the normal install command, which handles sign-in for you.");
        }
        var (apiOk, apiOut) = await RunGhAsync(
            $"api repos/{RepoOwner}/{RepoName}/contents/src/C3DBridge/Program.cs --jq .content");
        if (!apiOk)
        {
            return new UpdateCheckResult(UpdateCheckResultKind.Failed, current, null,
                "Version check failed through GitHub sign-in. The repo may still be changing visibility; try again shortly.");
        }
        try
        {
            var cleaned = new string(apiOut.Where(c => !char.IsWhiteSpace(c)).ToArray());
            var bytes = Convert.FromBase64String(cleaned);
            var latest = ParseVersion(Encoding.UTF8.GetString(bytes));
            if (latest is null)
                return new UpdateCheckResult(UpdateCheckResultKind.Failed, current, null,
                    "The version file was fetched but its version could not be read.");
            if (CompareVersions(latest, current) <= 0)
                return new UpdateCheckResult(UpdateCheckResultKind.UpToDate, current, latest,
                    $"Already up to date (v{current}).");
            return new UpdateCheckResult(UpdateCheckResultKind.Available, current, latest,
                $"Version v{latest} is available (you have v{current}).");
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(UpdateCheckResultKind.Failed, current, null,
                $"Version check failed: {ex.Message}");
        }
    }

    /// <summary>Launch the update script and return. The caller
    /// must close the tray app right after this returns, so the
    /// installer can overwrite the exe. The script itself runs
    /// the installer, verifies the new --version, reports the
    /// result in its own message box, and starts the bridge task.
    /// Returns false (with a reason) when the update cannot even
    /// be started; a started update reports its own outcome.</summary>
    public static bool StartUpdate(string latestVersion, out string reason)
    {
        reason = "";
        try
        {
            var appDir = Config.AppDir;
            var parent = Directory.GetParent(appDir)?.FullName;
            if (parent is null) { reason = "Could not find the install folder."; return false; }
            var sourceDir = Path.Combine(parent, "source");
            var installer = Path.Combine(sourceDir, "Install-C3DBridge.ps1");
            if (!File.Exists(installer))
            {
                reason = "The installer was not found in the source folder. Reinstall with the normal install command to update.";
                return false;
            }
            var scriptPath = Path.Combine(Path.GetTempPath(), "C3DBridge-update.ps1");
            var script = BuildUpdateScript(parent, sourceDir, installer, appDir, latestVersion);
            File.WriteAllText(scriptPath, script);
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = false,
            };
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            reason = $"Could not start the update: {ex.Message}";
            return false;
        }
    }

    private static string BuildUpdateScript(string installRoot, string sourceDir, string installer, string appDir, string latestVersion)
    {
        // No secrets in this script: public repo URLs only, plus
        // local paths. The private-repo fallback (gh) is not used
        // here: if the repo was still private, the check above
        // already required gh sign-in, and git/gh then use that
        // sign-in for the fetch. A public repo needs no auth.
        var zipUrl = $"https://github.com/{RepoOwner}/{RepoName}/archive/refs/heads/{Branch}.zip";
        return $@"
$ErrorActionPreference = 'Stop'
Write-Host 'C3D Print Bridge updater — updating to v{latestVersion}...'
Start-Sleep -Seconds 3
Get-Process C3DBridge -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
if (Test-Path '{sourceDir}\.git') {{
    Write-Host 'Updating the bridge source (git pull)...'
    git -C '{sourceDir}' pull
}} else {{
    Write-Host 'Refreshing the bridge source (public download)...'
    $zip = Join-Path $env:TEMP 'c3d-bridge-update.zip'
    Invoke-WebRequest -Uri '{zipUrl}' -OutFile $zip -UseBasicParsing
    $tmp = Join-Path $env:TEMP 'c3d-bridge-update-src'
    if (Test-Path $tmp) {{ Remove-Item $tmp -Recurse -Force }}
    Expand-Archive $zip $tmp
    $inner = Get-ChildItem $tmp -Directory | Select-Object -First 1
    if (Test-Path '{sourceDir}') {{ Remove-Item '{sourceDir}' -Recurse -Force }}
    Move-Item $inner.FullName '{sourceDir}'
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}}
Write-Host 'Running the installer (your config and key are preserved)...'
powershell -NoProfile -ExecutionPolicy Bypass -File '{installer}' -InstallDir '{installRoot}' -SourceDir '{sourceDir}'
$ver = & '{appDir}\C3DBridge.exe' --version 2>$null
Add-Type -AssemblyName System.Windows.Forms
if ($ver -match '{Regex.Escape(latestVersion)}') {{
    Start-ScheduledTask -TaskName 'C3D Print Bridge' -ErrorAction SilentlyContinue
    [System.Windows.Forms.MessageBox]::Show('Bridge updated to ' + $ver + '. It is starting now.', 'C3D Print Bridge')
}} else {{
    [System.Windows.Forms.MessageBox]::Show('Bridge update did not verify: expected v{latestVersion}, the installed program reports ' + $ver + '. Your old config and key were preserved. Try Get updates again, or reinstall with the normal install command.', 'C3D Print Bridge')
}}
";
    }
}
