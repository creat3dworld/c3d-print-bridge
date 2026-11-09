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
//  - A failed slice is KEPT, not deleted: the work folder
//    is moved to failed-slices\slice_<jobId>_FAILED_<
//    YYYYMMDD-HHMM> under the app dir, and the slicer's
//    command line plus the tail of its stdout/stderr go
//    into bridge.log — so the next bare exit code (e.g.
//    Bambu Studio -2, Nov 8, 2026) is diagnosable
//    instead of a dead end. Only a successful slice
//    deletes its work folder.
//  - CLI flags are the OrcaSlicer/Bambu Studio family
//    (OrcaSlicer wiki "CLI Mode" + Bambu Studio "Command
//    Line Usage"): --load-settings, --load-filaments,
//    --slice, --outputdir. --export-statistics and
//    --logfile are OrcaSlicer-ONLY: Snapmaker Orca does
//    NOT accept them (prints Usage + "setup params
//    error", proven Nov 8, 2026 on 2.4.1), and Bambu
//    Studio 02.09.01.70 does NOT accept them either —
//    it prints its --help text and exits -2 (proven Nov
//    8, 2026; its --help flag table lists neither flag).
//    So this poller adds both flags only for upstream
//    OrcaSlicer. Snapmaker Orca's CLI also crashes
//    on 3MF input carrying plate data (upstream issue
//    #935, exit -1073741819, proven Nov 8, 2026: mount
//    3MF crashed, mount STL sliced exit 0), so for
//    Snapmaker Orca the input 3MF is converted to STL
//    first (tools\3mf-to-stl.py, Python + trimesh) and
//    the STL is fed to the slicer. Bambu Studio and
//    OrcaSlicer keep the 3MF input; only OrcaSlicer
//    gets the full flag set.

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
        DateTime StartedAt,
        Task<string> StdOutTask,
        Task<string> StdErrTask);

    public SlicePoller(DashboardClient dashboard, BridgeConfig config)
    {
        _dashboard = dashboard;
        _config = config;
    }

    /// <summary>MULTI-BRIDGE (2026.1108): true while a slice
    /// is running on this bridge. The Worker reports it as
    /// the slice_machine block's slice_status ("busy" /
    /// "idle"), so the dashboard knows this machine is free
    /// to take the next slice job.</summary>
    public bool IsSlicing => _running is not null;

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
            RetainFailedSliceDir(workDir, job.SliceJobId);
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
                RetainFailedSliceDir(workDir, job.SliceJobId);
                return;
            }
        }
        catch (Exception ex)
        {
            await _dashboard.ReportSliceAsync(job.SliceJobId, "failed", $"Could not read the library file: {ex.Message}", null, null, null, ct);
            RetainFailedSliceDir(workDir, job.SliceJobId);
            return;
        }

        // Snapmaker Orca's CLI crashes on 3MF input carrying
        // plate data (upstream issue #935, proven Nov 8,
        // 2026), so for Snapmaker Orca the model is
        // converted to STL first and the STL is fed to the
        // slicer. Bambu Studio / OrcaSlicer take the 3MF
        // as-is. A failed conversion fails the job honestly
        // — a slice is never started from a bad file.
        var isSnapmakerOrca = slicer == "snapmaker-orca";
        var sliceInputPath = inputPath;
        if (isSnapmakerOrca)
        {
            var stlPath = Path.Combine(workDir, "input.stl");
            var (convOk, convReason) = await Convert3mfToStlAsync(inputPath, stlPath, ct);
            if (!convOk)
            {
                Program.Log($"[slice] Job {job.SliceJobId}: 3MF-to-STL conversion failed ({convReason}).");
                await _dashboard.ReportSliceAsync(job.SliceJobId, "failed", $"Could not convert the 3MF model to STL for Snapmaker Orca: {convReason}", null, null, null, ct);
                RetainFailedSliceDir(workDir, job.SliceJobId);
                return;
            }
            sliceInputPath = stlPath;
        }

        var logPath = Path.Combine(workDir, "slice.log");
        var args = $"\"{sliceInputPath}\" --load-settings \"{printerJson};{processJson}\" " +
            $"--load-filaments \"{filamentJson}\" --slice {job.Plate} " +
            $"--outputdir \"{outDir}\"";
        // --export-statistics and --logfile are
        // OrcaSlicer-only. Snapmaker Orca rejects them
        // ("setup params error", proven Nov 8, 2026 on
        // 2.4.1), and Bambu Studio 02.09.01.70 rejects
        // them too — it prints its --help text and exits
        // -2 (proven Nov 8, 2026; its --help flag table
        // lists neither flag). Add both flags only for
        // upstream OrcaSlicer.
        if (slicer == "orcaslicer")
            args += $" --export-statistics --logfile \"{logPath}\"";
        // Log the exact command line before starting: when
        // a slicer exits with a bare code (Bambu Studio -2,
        // Nov 8, 2026) the command is the first thing
        // needed, and nothing else on the shop PC records
        // it. Paths only — no secrets ever pass through
        // here.
        Program.Log($"[slice] Job {job.SliceJobId}: running {exe} {args}");
        var start = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Capture the slicer's own output: without
            // redirection a failing slicer's error text
            // goes nowhere and only its exit code survives.
            // The reads start immediately so a chatty
            // slicer can never block on a full pipe.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
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
            RetainFailedSliceDir(workDir, job.SliceJobId);
            return;
        }
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        _running = new RunningSlice(job, proc, workDir, outDir, DateTime.UtcNow, stdoutTask, stderrTask);
        Program.Log($"[slice] Job {job.SliceJobId}: slicing started with '{slicer}' for model '{job.TargetModel}', plate {job.Plate} (runs in the background; status polls continue).");
        // MULTI-BRIDGE (2026.1108): claim the job the moment
        // slicing starts, so the dashboard marks it 'slicing'
        // and no second bridge can take it (first claim wins).
        // The claim carries this bridge's machine_id when
        // configured; in legacy mode it carries none and the
        // job keeps its assignment (or stays unassigned, as
        // today). A claim that fails to reach the dashboard
        // is logged, not fatal: the job is assigned to this
        // machine, so the next poll serves it to this bridge
        // only, and the running slice still reports
        // done/failed when it finishes.
        var claimed = await _dashboard.ReportSliceAsync(job.SliceJobId, "started", "", null, null, null, ct);
        if (!claimed)
            Program.Log($"[slice] Job {job.SliceJobId}: the 'started' claim did not reach the dashboard; the slice is still running locally and will report done/failed when it finishes.");
    }

    private async Task CheckRunningAsync(CancellationToken ct)
    {
        var run = _running!;
        var timeout = TimeSpan.FromMinutes(_config.SliceTimeoutMinutes <= 0 ? 15 : _config.SliceTimeoutMinutes);
        if (!run.Process.HasExited && DateTime.UtcNow - run.StartedAt > timeout)
        {
            try { run.Process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            // The kill closes the slicer's streams, so the
            // output reads finish; collect what it printed
            // before it was killed.
            var timeoutStdout = await ReadOutputSoonAsync(run.StdOutTask);
            var timeoutStderr = await ReadOutputSoonAsync(run.StdErrTask);
            run.Process.Dispose();
            _running = null;
            LogSlicerOutputTail(run.Job.SliceJobId, timeoutStdout, timeoutStderr);
            var timeoutKept = RetainFailedSliceDir(run.WorkDir, run.Job.SliceJobId);
            var timeoutHint = EvidenceSuffix(timeoutKept);
            Program.Log($"[slice] Job {run.Job.SliceJobId}: slicing timed out after {timeout.TotalMinutes} minutes. Killed and failed.{timeoutHint}");
            await _dashboard.ReportSliceAsync(run.Job.SliceJobId, "failed", $"Slicing timed out after {timeout.TotalMinutes} minutes.{timeoutHint}", null, null, null, ct);
            return;
        }
        if (!run.Process.HasExited)
            return;
        var exitCode = run.Process.ExitCode;
        // Collect the slicer's output BEFORE disposing the
        // process: disposing closes the redirected streams
        // and the reads would come back empty.
        var stdout = await ReadOutputSoonAsync(run.StdOutTask);
        var stderr = await ReadOutputSoonAsync(run.StdErrTask);
        run.Process.Dispose();
        _running = null;
        if (exitCode != 0)
        {
            LogSlicerOutputTail(run.Job.SliceJobId, stdout, stderr);
            var kept = RetainFailedSliceDir(run.WorkDir, run.Job.SliceJobId);
            // The old hint ("See the slice log on the shop
            // PC") pointed at a log the bridge had already
            // deleted with the work folder. The folder is
            // now retained under its own name, so say so;
            // Snapmaker Orca still writes no CLI log file,
            // but the retained folder (input + the slicer's
            // captured output in bridge.log) is the
            // evidence either way.
            var logHint = kept is not null
                ? $"Failed slice kept in failed-slices\\{kept} on the shop PC; the slicer's output is in bridge.log."
                : "The failed slice folder could not be retained on the shop PC.";
            Program.Log($"[slice] Job {run.Job.SliceJobId}: slicer exited with code {exitCode}. Failed; {logHint}");
            await _dashboard.ReportSliceAsync(run.Job.SliceJobId, "failed", $"Slicer exited with code {exitCode}. {logHint}", null, null, null, ct);
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
            var noGcodeKept = RetainFailedSliceDir(run.WorkDir, run.Job.SliceJobId);
            Program.Log($"[slice] Job {run.Job.SliceJobId}: slicer exited 0 but no plate_{plate}.gcode was produced. Failed; nothing attached.{EvidenceSuffix(noGcodeKept)}");
            await _dashboard.ReportSliceAsync(run.Job.SliceJobId, "failed", $"Slicer finished but produced no plate_{plate}.gcode.{EvidenceSuffix(noGcodeKept)}", null, null, null, ct);
            return;
        }
        var verify = VerifyGcode(gcodePath);
        if (verify is not null)
        {
            var verifyKept = RetainFailedSliceDir(run.WorkDir, run.Job.SliceJobId);
            Program.Log($"[slice] Job {run.Job.SliceJobId}: G-code verification failed ({verify}). Nothing attached.{EvidenceSuffix(verifyKept)}");
            await _dashboard.ReportSliceAsync(run.Job.SliceJobId, "failed", $"Sliced G-code failed verification: {verify}{EvidenceSuffix(verifyKept)}", null, null, null, ct);
            return;
        }
        var bytes = await File.ReadAllBytesAsync(gcodePath, ct);
        if (bytes.Length > MaxReportBytes)
        {
            var tooBigKept = RetainFailedSliceDir(run.WorkDir, run.Job.SliceJobId);
            Program.Log($"[slice] Job {run.Job.SliceJobId}: sliced G-code is {bytes.Length} bytes, over the {MaxReportBytes} report cap. Failed; nothing attached.{EvidenceSuffix(tooBigKept)}");
            await _dashboard.ReportSliceAsync(run.Job.SliceJobId, "failed", $"Sliced G-code is too large to report to the dashboard.{EvidenceSuffix(tooBigKept)}", null, null, null, ct);
            return;
        }
        // Slice-output name rule (Nov 7, 2026): a slice output
        // filename must not contain "_". Strip underscores entirely
        // (delete, do not substitute a hyphen). The slicer's own
        // on-disk file is still "plate_N.gcode" (that is the name
        // the slicer writes and the name this code looks it up by
        // above); only the name reported to the dashboard — the
        // name the stored slice carries — is stripped.
        var fileName = $"plate{plate}.gcode";
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

    /// <summary>Convert a 3MF model to STL with the bundled
    /// Python script (tools\3mf-to-stl.py, trimesh) — the
    /// Snapmaker Orca slice path only. Returns (ok, reason);
    /// a failure means the slice must not start.</summary>
    private async Task<(bool Ok, string Reason)> Convert3mfToStlAsync(string input3mf, string outputStl, CancellationToken ct)
    {
        // Python: the bridge's shared install, the same one
        // the lithophane mesh step uses (lithophane_python_
        // path in bridge-config.json, default "python" from
        // PATH). trimesh AND networkx must be installed
        // (pip install trimesh networkx) — trimesh's 3MF
        // loader needs networkx for its scene graph.
        var python = string.IsNullOrWhiteSpace(_config.LithophanePythonPath) ? "python" : _config.LithophanePythonPath!.Trim();
        var pythonExe = python.Contains(Path.DirectorySeparatorChar) || python.Contains(Path.AltDirectorySeparatorChar)
            ? (File.Exists(python) ? python : null)
            : FindOnPath(python.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? python : python + ".exe")
              ?? FindOnPath(python);
        if (pythonExe is null)
            return (false, "Python is not installed on the shop PC, so the 3MF model could not be converted to STL for Snapmaker Orca. Install Python 3.10+ with trimesh and networkx (pip install trimesh networkx) and make python available on PATH.");
        var script = Path.Combine(Config.AppDir, "tools", "3mf-to-stl.py");
        if (!File.Exists(script))
            return (false, "The 3MF-to-STL script is missing on the shop PC (tools\\3mf-to-stl.py). Redeploy the bridge.");
        var (ok, text, reason) = await RunToolWithOutputAsync(
            pythonExe, $"\"{script}\" --in \"{input3mf}\" --out \"{outputStl}\"", TimeSpan.FromMinutes(5), ct);
        if (!ok)
            return (false, string.IsNullOrWhiteSpace(reason) ? "the converter failed." : reason);
        if (!File.Exists(outputStl) || new FileInfo(outputStl).Length == 0)
            return (false, "the converter reported success but wrote no STL file.");
        Program.Log($"[slice] 3MF converted to STL for Snapmaker Orca ({new FileInfo(outputStl).Length} bytes; {TrimReason(text)}).");
        return (true, "");
    }

    private static string? FindOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var candidate = Path.Combine(dir.Trim(), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* ignore an unusable PATH entry */ }
        }
        return null;
    }

    private static async Task<(bool Ok, string Text, string Reason)> RunToolWithOutputAsync(
        string exe, string args, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return (false, "", "");
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            var outTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return (false, "", "Tool timed out.");
            }
            var text = await outTask;
            var err = await errTask;
            return (proc.ExitCode == 0, text, TrimReason(err));
        }
        catch (Exception ex)
        {
            return (false, "", ex.Message);
        }
    }

    private static string TrimReason(string s)
    {
        s = (s ?? "").Trim();
        return s.Length <= 200 ? s : s[..200] + "…";
    }

    /// <summary>Move a failed slice's work folder out of
    /// temp into failed-slices under the app dir, stamped
    /// with the job id and the local date+time (YYYYMMDD-
    /// HHMM, the same date+time rule as log archives) so
    /// one failure never overwrites another. The folder
    /// keeps the downloaded input, the slicer's slice.log
    /// (when it writes one) and any partial output.
    /// Returns the retained folder name, or null when the
    /// move failed — then the folder is deleted instead so
    /// temp does not fill up, and the caller says so.</summary>
    private static string? RetainFailedSliceDir(string workDir, string jobId)
    {
        // Job ids come from the dashboard ("slice_<n>_<n>")
        // but never trust a name that ends up in a path.
        var safeId = string.Concat(jobId.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        if (string.IsNullOrWhiteSpace(safeId)) safeId = "unknown";
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmm");
        var name = $"slice_{safeId}_FAILED_{stamp}";
        var retainedRoot = Path.Combine(Config.AppDir, "failed-slices");
        try
        {
            Directory.CreateDirectory(retainedRoot);
            var dest = Path.Combine(retainedRoot, name);
            // Two failures in the same minute must not
            // collide: suffix until the name is free.
            var n = 2;
            while (Directory.Exists(dest))
            {
                dest = Path.Combine(retainedRoot, $"{name}-{n}");
                n++;
            }
            Directory.Move(workDir, dest);
            var kept = Path.GetFileName(dest);
            Program.Log($"[slice] Job {jobId}: failed slice kept in failed-slices\\{kept} (input, slice log and any partial output).");
            return kept;
        }
        catch (Exception ex)
        {
            Program.Log($"[slice] Job {jobId}: could not retain the failed slice folder ({ex.Message}); deleting it instead.");
            CleanupDir(workDir);
            return null;
        }
    }

    /// <summary>Dashboard-facing suffix naming where the
    /// failed slice's evidence was kept ("" when it could
    /// not be retained).</summary>
    private static string EvidenceSuffix(string? kept)
        => kept is null ? "" : $" Evidence kept in failed-slices\\{kept} on the shop PC.";

    /// <summary>Read a slicer output stream that should
    /// already be finished (the process exited or was
    /// killed). Waits at most 10 seconds, then gives up
    /// with "" rather than stalling the poll loop.</summary>
    private static async Task<string> ReadOutputSoonAsync(Task<string> task)
    {
        var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (done != task) return "";
        try { return await task; } catch { return ""; }
    }

    /// <summary>Log the tail of the slicer's combined
    /// stdout/stderr (last 30 non-blank lines, stderr
    /// first, each line truncated) so the slicer's own
    /// error text survives in bridge.log when it exits
    /// non-zero or is killed. A slicer that printed
    /// nothing says so in one line.</summary>
    private static void LogSlicerOutputTail(string jobId, string stdout, string stderr)
    {
        var tail = new Queue<string>();
        void AddLines(string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.Length > 300) line = line[..300] + "…";
                tail.Enqueue(line);
                while (tail.Count > 30) tail.Dequeue();
            }
        }
        AddLines(stderr);
        AddLines(stdout);
        if (tail.Count == 0)
        {
            Program.Log($"[slice] Job {jobId}: slicer printed nothing to stdout/stderr.");
            return;
        }
        Program.Log($"[slice] Job {jobId}: slicer output (last {tail.Count} lines):");
        foreach (var line in tail)
            Program.Log($"[slice] Job {jobId} slicer: {line}");
    }

    private static void CleanupDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
