// C3D Print Bridge — in-dashboard slice poller
// (AUTO-START BUILD).
//
// "In the dashboard" means: the dashboard records a Slice
// action on a File Library entry, and THIS bridge — on the
// shop PC, where the slicers are installed — runs the
// slicer CLI and reports verified G-code back. Slicing
// NEVER runs on the Hostinger web server (shared hosting
// cannot run desktop slicer binaries), and the bridge
// NEVER uploads a slice it did not verify.
//
// FLOW (one slice at a time — slicing is CPU-heavy):
//  - Poll GET farm/slice-jobs for pending slice jobs.
//  - Resolve the slicer executable from bridge-config
//    (empty = not configured → fail honestly, never a
//    guessed binary) and the preset JSONs from the
//    bridge's slicer presets folder. Presets MUST be full
//    exports from the slicer's GUI: the slicer CLI does
//    not resolve inherits/include, and an unresolved
//    preset slices "successfully" onto a generic bed with
//    no printer start G-code. That trap cannot be caught
//    by byte checks alone, so the pilot checklist (see
//    the slice pilot script) verifies bed size and start
//    markers in the header by eye on the first slice per
//    printer — the code checks below are necessary but
//    not sufficient, and they say so.
//  - Download the library file over the authenticated
//    slice-file route (SHA-256 verified), run the CLI in
//    the background (never blocking the worker loop —
//    status polls and heartbeats keep flowing), and on
//    exit verify: exit code 0, plate_N.gcode present,
//    not a ZIP, no NUL bytes, at least 5 G/M/T command
//    lines in the head. Then POST farm/slice-report with
//    the G-code (base64, 25 MB cap) and the target model
//    as "Sliced for". Any failure reports 'failed' with
//    the reason and attaches nothing.
//  - CLI flags are the OrcaSlicer/Bambu Studio family
//    (OrcaSlicer wiki "CLI Mode" + Bambu Studio "Command
//    Line Usage"): --load-settings, --load-filaments,
//    --slice, --outputdir, --export-statistics, --logfile.
//    Snapmaker Orca shares the Orca lineage, but its CLI
//    is NOT yet verified on the shop PC — the pilot
//    script proves it (or falls back to plain OrcaSlicer
//    with a U1 profile, or to GUI slicing) before any
//    production slice job uses it.

using System.Diagnostics;
using System.IO.Compression;

namespace C3DBridge;

public sealed class SlicePoller
{
    private const long MaxReportBytes = 25L * 1024 * 1024;

    private readonly DashboardClient _dashboard;
    private readonly BridgeConfig _config;
    private RunningSlice? _running;

    private sealed record RunningSlice(
        SliceJob Job,
        Process Process,
        string WorkDir,
        string OutDir,
        DateTime StartedAt);

    public SlicePoller(DashboardClient dashboard, BridgeConfig config)
    {
        _dashboard = dashboard;
        _config = config;
    }

    public async Task PollAsync(CancellationToken ct)
    {
        if (!_dashboard.HasKey)
            return;
        if (_running is not null)
        {
            await CheckRunningAsync(ct);
            return;
        }
        var (jobs, problem) = await _dashboard.PollSliceJobsAsync(ct);
        if (jobs is null)
        {
            if (!string.IsNullOrWhiteSpace(problem))
                Program.Log($"[slice] Poll failed: {problem}");
            return;
        }
        foreach (var job in jobs)
        {
            await StartSliceAsync(job, ct);
            // One slice at a time; the rest wait for later polls.
            if (_running is not null) break;
        }
    }

    private async Task StartSliceAsync(SliceJob job, CancellationToken ct)
    {
        var slicer = (job.Slicer ?? "").Trim().ToLowerInvariant();
        var exe = slicer switch
        {
            "orcaslicer" => _config.SlicerOrcaslicerPath,
            "snapmaker-orca" => _config.SlicerSnapmakerOrcaPath,
            "bambu-studio" => _config.SlicerBambuStudioPath,
            _ => null,
        };
        if (slicer != "orcaslicer" && slicer != "snapmaker-orca" && slicer != "bambu-studio")
        {
            Program.Log($"[slice] Job {job.SliceJobId}: unknown slicer '{job.Slicer}'. Failing honestly, nothing sliced.");
            await _dashboard.ReportSliceAsync(job.SliceJobId, "failed", $"Unknown slicer '{job.Slicer}'.", null, null, null, ct);
            return;
        }
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            Program.Log($"[slice] Job {job.SliceJobId}: slicer '{slicer}' is not configured (no executable path in bridge-config) or the path does not exist. Failing honestly, nothing sliced.");
            await _dashboard.ReportSliceAsync(job.SliceJobId, "failed", $"Slicer '{slicer}' is not configured on this bridge (no executable path). Run the slice pilot to discover and configure it.", null, null, null, ct);
            return;
        }
        if (string.IsNullOrWhiteSpace(_config.SlicerPresetsDir) || !Directory.Exists(_config.SlicerPresetsDir))
        {
            Program.Log($"[slice] Job {job.SliceJobId}: slicer presets folder is not configured or does not exist. Failing honestly.");
            await _dashboard.ReportSliceAsync(job.SliceJobId, "failed", "Slicer presets folder is not configured on this bridge.", null, null, null, ct);
            return;
        }
        // Preset names must be bare file names under the
        // presets folder — never a path that escapes it.
        string? ResolvePreset(string name, string what)
        {
            if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name)
            {
                Program.Log($"[slice] Job {job.SliceJobId}: {what} preset name '{name}' is not a bare file name. Failing honestly.");
                return null;
            }
            var path = Path.Combine(_config.SlicerPresetsDir!, name);
            if (!File.Exists(path))
            {
                Program.Log($"[slice] Job {job.SliceJobId}: {what} preset file '{name}' was not found in the slicer presets folder. Failing honestly.");
                return null;
            }
            return path;
        }
        var printerJson = ResolvePreset(job.PresetPrinter, "printer");
        if (printerJson is null) { await _dashboard.ReportSliceAsync(job.SliceJobId, "failed", $"Printer preset '{job.PresetPrinter}' was not found in the slicer presets folder.", null, null, null, ct); return; }
        var processJson = ResolvePreset(job.PresetProcess, "process");
        if (processJson is null) { await _dashboard.ReportSliceAsync(job.SliceJobId, "failed", $"Process preset '{job.PresetProcess}' was not found in the slicer presets folder.", null, null, null, ct); return; }
        var filamentJson = ResolvePreset(job.PresetFilament, "filament");
        if (filamentJson is null) { await _dashboard.ReportSliceAsync(job.SliceJobId, "failed", $"Filament preset '{job.PresetFilament}' was not found in the slicer presets folder.", null, null, null, ct); return; }
        if (job.Plate < 0)
        {
            await _dashboard.ReportSliceAsync(job.SliceJobId, "failed", "Slice plate number is invalid.", null, null, null, ct);
            return;
        }

        var workDir = Path.Combine(Config.TempDir, "slice_" + job.SliceJobId);
        var outDir = Path.Combine(workDir, "out");
        try { Directory.CreateDirectory(outDir); }
        catch (Exception ex)
        {
            await _dashboard.ReportSliceAsync(job.SliceJobId, "failed", $"Could not create the slice work folder: {ex.Message}", null, null, null, ct);
            return;
        }
        var inputPath = Path.Combine(workDir, "input.3mf");
        var (dlOk, dlReason) = await _dashboard.DownloadFileAsync(job.InputUrl, inputPath, job.InputSha256, ct);
        if (!dlOk)
        {
            Program.Log($"[slice] Job {job.SliceJobId}: library file download failed ({dlReason}).");
            await _dashboard.ReportSliceAsync(job.SliceJobId, "failed", $"Library file download failed: {dlReason}", null, null, null, ct);
            CleanupDir(workDir);
            return;
        }
        // The input must be a 3MF (ZIP); anything else is
        // refused before the slicer ever sees it.
        try
        {
            using var fs = File.OpenRead(inputPath);
            var magic = new byte[2];
            if (await fs.ReadAsync(magic, ct) < 2 || magic[0] != (byte)'P' || magic[1] != (byte)'K')
            {
                Program.Log($"[slice] Job {job.SliceJobId}: library file is not a 3MF (no PK magic). Refused.");
                await _dashboard.ReportSliceAsync(job.SliceJobId, "failed", "Library file is not a 3MF file.", null, null, null, ct);
                CleanupDir(workDir);
                return;
            }
        }
        catch (Exception ex)
        {
            await _dashboard.ReportSliceAsync(job.SliceJobId, "failed", $"Could not read the library file: {ex.Message}", null, null, null, ct);
            CleanupDir(workDir);
            return;
        }

        var logPath = Path.Combine(workDir, "slice.log");
        var args = $"\"{inputPath}\" --load-settings \"{printerJson};{processJson}\" " +
            $"--load-filaments \"{filamentJson}\" --slice {job.Plate} " +
            $"--outputdir \"{outDir}\" --export-statistics --logfile \"{logPath}\"";
        var start = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Process proc;
        try
        {
            proc = Process.Start(start)!;
            if (proc is null) throw new InvalidOperationException("Process.Start returned nothing.");
        }
        catch (Exception ex)
        {
            Program.Log($"[slice] Job {job.SliceJobId}: could not start the slicer ({ex.Message}).");
            await _dashboard.ReportSliceAsync(job.SliceJobId, "failed", $"Could not start the slicer: {ex.Message}", null, null, null, ct);
            CleanupDir(workDir);
            return;
        }
        _running = new RunningSlice(job, proc, workDir, outDir, DateTime.UtcNow);
        Program.Log($"[slice] Job {job.SliceJobId}: slicing started with '{slicer}' for model '{job.TargetModel}', plate {job.Plate} (runs in the background; status polls continue).");
    }

    private async Task CheckRunningAsync(CancellationToken ct)
    {
        var run = _running!;
        var timeout = TimeSpan.FromMinutes(_config.SliceTimeoutMinutes <= 0 ? 15 : _config.SliceTimeoutMinutes);
        if (!run.Process.HasExited && DateTime.UtcNow - run.StartedAt > timeout)
        {
            try { run.Process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            Program.Log($"[slice] Job {run.Job.SliceJobId}: slicing timed out after {timeout.TotalMinutes} minutes. Killed and failed.");
            await _dashboard.ReportSliceAsync(run.Job.SliceJobId, "failed", $"Slicing timed out after {timeout.TotalMinutes} minutes.", null, null, null, ct);
            _running = null;
            CleanupDir(run.WorkDir);
            return;
        }
        if (!run.Process.HasExited)
            return;
        var exitCode = run.Process.ExitCode;
        run.Process.Dispose();
        _running = null;
        if (exitCode != 0)
        {
            Program.Log($"[slice] Job {run.Job.SliceJobId}: slicer exited with code {exitCode}. Failed; see the slice log on the shop PC.");
            await _dashboard.ReportSliceAsync(run.Job.SliceJobId, "failed", $"Slicer exited with code {exitCode}. See the slice log on the shop PC.", null, null, null, ct);
            CleanupDir(run.WorkDir);
            return;
        }
        // Find plate_N.gcode in the output dir; if the
        // slicer only wrote a sliced 3MF, extract
        // Metadata/plate_N.gcode from it (a sliced 3MF is
        // a ZIP carrying its plate G-code).
        var plate = run.Job.Plate <= 0 ? 1 : run.Job.Plate;
        var gcodePath = Path.Combine(run.OutDir, $"plate_{plate}.gcode");
        if (!File.Exists(gcodePath))
        {
            var threeMf = Directory.GetFiles(run.OutDir, "*.3mf").FirstOrDefault();
            if (threeMf is not null)
            {
                try
                {
                    using var zip = ZipFile.OpenRead(threeMf);
                    var entry = zip.GetEntry($"Metadata/plate_{plate}.gcode");
                    if (entry is not null)
                    {
                        entry.ExtractToFile(gcodePath, overwrite: true);
                        Program.Log($"[slice] Job {run.Job.SliceJobId}: plate G-code extracted from the sliced 3MF.");
                    }
                }
                catch (Exception ex)
                {
                    Program.Log($"[slice] Job {run.Job.SliceJobId}: could not extract plate G-code from the sliced 3MF ({ex.Message}).");
                }
            }
        }
        if (!File.Exists(gcodePath))
        {
            Program.Log($"[slice] Job {run.Job.SliceJobId}: slicer exited 0 but no plate_{plate}.gcode was produced. Failed; nothing attached.");
            await _dashboard.ReportSliceAsync(run.Job.SliceJobId, "failed", $"Slicer finished but produced no plate_{plate}.gcode.", null, null, null, ct);
            CleanupDir(run.WorkDir);
            return;
        }
        var verify = VerifyGcode(gcodePath);
        if (verify is not null)
        {
            Program.Log($"[slice] Job {run.Job.SliceJobId}: G-code verification failed ({verify}). Nothing attached.");
            await _dashboard.ReportSliceAsync(run.Job.SliceJobId, "failed", $"Sliced G-code failed verification: {verify}", null, null, null, ct);
            CleanupDir(run.WorkDir);
            return;
        }
        var bytes = await File.ReadAllBytesAsync(gcodePath, ct);
        if (bytes.Length > MaxReportBytes)
        {
            Program.Log($"[slice] Job {run.Job.SliceJobId}: sliced G-code is {bytes.Length} bytes, over the {MaxReportBytes} report cap. Failed; nothing attached.");
            await _dashboard.ReportSliceAsync(run.Job.SliceJobId, "failed", "Sliced G-code is too large to report to the dashboard.", null, null, null, ct);
            CleanupDir(run.WorkDir);
            return;
        }
        var fileName = $"plate_{plate}.gcode";
        var sent = await _dashboard.ReportSliceAsync(run.Job.SliceJobId, "done",
            $"Sliced for {run.Job.TargetModel}, plate {plate}.", Convert.ToBase64String(bytes), fileName, run.Job.TargetModel, ct);
        Program.Log(sent
            ? $"[slice] Job {run.Job.SliceJobId}: G-code verified and reported ({bytes.Length} bytes, {fileName})."
            : $"[slice] Job {run.Job.SliceJobId}: G-code verified but the report did not reach the dashboard; the slice job stays pending and will be retried (the slicer will run again).");
        CleanupDir(run.WorkDir);
    }

    /// <summary>Byte-level G-code verification, mirroring
    /// the dashboard's sliced-file rule: plain text with
    /// real G/M/T command lines, never a ZIP, never binary
    /// junk. Returns null when the file passes, else the
    /// failure reason. This does NOT prove the bed size
    /// or start G-code match the target printer — an
    /// unresolved preset can still slice "successfully"
    /// onto a generic bed, which is why the pilot
    /// checklist verifies the header by eye on the first
    /// slice per printer.</summary>
    private static string? VerifyGcode(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length < 1024) return "file is suspiciously small (< 1 KB).";
            using var fs = File.OpenRead(path);
            var head = new byte[Math.Min(65536, (int)info.Length)];
            var read = fs.Read(head, 0, head.Length);
            if (read >= 2 && head[0] == (byte)'P' && head[1] == (byte)'K')
                return "file is a ZIP archive, not G-code.";
            var text = System.Text.Encoding.UTF8.GetString(head, 0, read);
            if (text.Contains('\0')) return "file contains NUL bytes; not plain G-code text.";
            var commands = 0;
            foreach (var line in text.Split('\n'))
            {
                var t = line.TrimStart();
                if (t.Length >= 2 && (t[0] == 'G' || t[0] == 'M' || t[0] == 'T') && char.IsDigit(t[1]))
                    commands++;
                if (commands >= 5) break;
            }
            if (commands < 5) return "fewer than 5 G/M/T command lines in the file head; not recognizable G-code.";
            return null;
        }
        catch (Exception ex)
        {
            return $"could not be read ({ex.Message}).";
        }
    }

    private static void CleanupDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
