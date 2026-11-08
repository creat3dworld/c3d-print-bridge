// C3D Print Bridge — lithophane print-file generation poller
// (plugin c3d-lithophane v2, 2026.1107).
//
// FLOW (one generation at a time — mesh generation and slicing
// are both CPU-heavy):
//  - Poll GET c3d-lithophane/v1/generation-jobs for pending
//    jobs (a job exists once the buyer's photo reached Drive).
//  - Read the photo from the local Drive for Desktop sync copy
//    (Lithophane Orders / order-N / <photo>) — never from a
//    dashboard download, so the known dashboard-download
//    SHA-256 failure mode cannot block a generation. The photo
//    size must match the job's photo_size; a missing or
//    wrong-sized copy fails honestly with the reason.
//  - Generate the lithophane 3MF with the bundled Python mesh
//    script (tools\lithophane-mesh.py — deterministic, offline,
//    no browser, no SupaVoxel round-trip). The script prints a
//    JSON summary; ok:false or a non-watertight mesh fails the
//    job before the slicer ever runs.
//  - Slice the 3MF with the SAME slicer CLI form SlicePoller
//    uses (--load-settings printer;process, --load-filaments,
//    --slice, --outputdir, --export-statistics, --logfile),
//    with the job's lithophane presets (0.12 mm layers, 100%
//    infill, white PLA). The same G-code verification applies.
//  - Write the model 3MF + sliced G-code (+ the sliced plate
//    .gcode.3mf when the slicer produced one — a Bambu target
//    prints ONLY from the packaged plate) into the local Drive
//    order folder, so Drive sync carries them to Drive. Then
//    report done with names, sizes and layer count; the
//    dashboard verifies the files are actually in Drive.
//  - Any failure reports 'failed' with the reason and writes
//    nothing. Generation never starts a print, never touches
//    a printer, and never blocks an order — the order already
//    completed before the job was queued.
//
// SETUP (shop PC, free tools only):
//  - Python 3.10+ on PATH (python --version) with numpy,
//    Pillow and trimesh (pip install numpy Pillow trimesh).
//  - tools\lithophane-mesh.py under the bridge install folder
//    (deployed with the bridge; config lithophane_mesh_script
//    overrides the path when set).
//  - Lithophane preset exports in the slicer presets folder:
//    litho-<printer>-printer.json, litho-<printer>-process.json
//    (0.12 mm, 100% infill), litho-pla-white-filament.json.
//    Full GUI exports, never fragments — the same rule as
//    slice jobs, for the same generic-bed trap.

using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

namespace C3DBridge;

public sealed class LithophanePoller
{
    private readonly DashboardClient _dashboard;
    private readonly BridgeConfig _config;
    private RunningLitho? _running;
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);
    private bool _endpointMissingLogged;
    private bool _pythonMissingLogged;
    private bool _scriptMissingLogged;

    private static readonly TimeSpan MeshTimeout = TimeSpan.FromMinutes(5);

    private sealed record RunningLitho(
        LithophaneJob Job,
        Process Process,
        string WorkDir,
        string OutDir,
        string Model3mfPath,
        string DriveOrderDir,
        string BaseName,
        string PrinterSlug,
        DateTime StartedAt);

    public LithophanePoller(DashboardClient dashboard, BridgeConfig config)
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
        var (jobs, problem) = await _dashboard.PollLithophaneJobsAsync(ct);
        if (jobs is null)
        {
            // 404 = the lithophane plugin routes are not
            // installed yet: log once, then stay quiet until
            // restart, exactly like the OCR endpoint rule.
            if (problem is not null && problem.Contains("not installed", StringComparison.OrdinalIgnoreCase))
            {
                if (!_endpointMissingLogged)
                {
                    _endpointMissingLogged = true;
                    Program.Log($"[litho] Poll failed: {problem}");
                }
                return;
            }
            if (!string.IsNullOrWhiteSpace(problem))
                Program.Log($"[litho] Poll failed: {problem}");
            return;
        }
        foreach (var job in jobs)
        {
            if (_reported.Contains(job.JobId))
                continue; // Never report the same job twice in a run.
            await StartGenerationAsync(job, ct);
            // One generation at a time; the rest wait for later polls.
            if (_running is not null) break;
        }
    }

    private async Task StartGenerationAsync(LithophaneJob job, CancellationToken ct)
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
            Program.Log($"[litho] Job {job.JobId}: unknown slicer '{job.Slicer}'. Failing honestly, nothing generated.");
            await ReportFailedAsync(job, $"Unknown slicer '{job.Slicer}'.", ct);
            return;
        }
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            Program.Log($"[litho] Job {job.JobId}: slicer '{slicer}' is not configured (no executable path in bridge-config) or the path does not exist. Failing honestly.");
            await ReportFailedAsync(job, $"Slicer '{slicer}' is not configured on this bridge (no executable path).", ct);
            return;
        }
        if (string.IsNullOrWhiteSpace(_config.SlicerPresetsDir) || !Directory.Exists(_config.SlicerPresetsDir))
        {
            Program.Log($"[litho] Job {job.JobId}: slicer presets folder is not configured or does not exist. Failing honestly.");
            await ReportFailedAsync(job, "Slicer presets folder is not configured on this bridge.", ct);
            return;
        }
        // Preset names must be bare file names under the
        // presets folder — never a path that escapes it.
        // (Same rule as SlicePoller.)
        string? ResolvePreset(string name, string what)
        {
            if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name)
            {
                Program.Log($"[litho] Job {job.JobId}: {what} preset name '{name}' is not a bare file name. Failing honestly.");
                return null;
            }
            var path = Path.Combine(_config.SlicerPresetsDir!, name);
            if (!File.Exists(path))
            {
                Program.Log($"[litho] Job {job.JobId}: {what} preset file '{name}' was not found in the slicer presets folder. Failing honestly.");
                return null;
            }
            return path;
        }
        var printerJson = ResolvePreset(job.PresetPrinter, "printer");
        if (printerJson is null) { await ReportFailedAsync(job, $"Printer preset '{job.PresetPrinter}' was not found in the slicer presets folder.", ct); return; }
        var processJson = ResolvePreset(job.PresetProcess, "process");
        if (processJson is null) { await ReportFailedAsync(job, $"Process preset '{job.PresetProcess}' was not found in the slicer presets folder.", ct); return; }
        var filamentJson = ResolvePreset(job.PresetFilament, "filament");
        if (filamentJson is null) { await ReportFailedAsync(job, $"Filament preset '{job.PresetFilament}' was not found in the slicer presets folder.", ct); return; }

        // Mesh toolchain: Python + the bundled script.
        var python = string.IsNullOrWhiteSpace(_config.LithophanePythonPath) ? "python" : _config.LithophanePythonPath!.Trim();
        var pythonExe = python.Contains(Path.DirectorySeparatorChar) || python.Contains(Path.AltDirectorySeparatorChar)
            ? (File.Exists(python) ? python : null)
            : FindOnPath(python.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? python : python + ".exe")
              ?? FindOnPath(python);
        if (pythonExe is null)
        {
            if (!_pythonMissingLogged)
            {
                _pythonMissingLogged = true;
                Program.Log("[litho] Python was not found on this PC, so lithophane meshes cannot be generated. Install Python 3.10+ (free, python.org) with numpy, Pillow and trimesh (pip install numpy Pillow trimesh) and make python available on PATH.");
            }
            await ReportFailedAsync(job, "Python is not installed on the shop PC, so the lithophane mesh could not be generated.", ct);
            return;
        }
        var script = string.IsNullOrWhiteSpace(_config.LithophaneMeshScript)
            ? Path.Combine(Config.AppDir, "tools", "lithophane-mesh.py")
            : _config.LithophaneMeshScript!.Trim();
        if (!File.Exists(script))
        {
            if (!_scriptMissingLogged)
            {
                _scriptMissingLogged = true;
                Program.Log($"[litho] Mesh script was not found at '{script}'. It ships with the bridge under tools\\ — redeploy the bridge or set lithophane_mesh_script in bridge-config.json.");
            }
            await ReportFailedAsync(job, "The lithophane mesh script is missing on the shop PC, so the mesh could not be generated.", ct);
            return;
        }

        // Photo input: the local Drive for Desktop sync copy.
        // The dashboard no longer holds the photo bytes (its
        // local copy is deleted once the Drive push succeeds),
        // so there is no dashboard download to fall back to —
        // Drive sync IS the input path, by design.
        var syncRoot = DriveDesktopClient.ResolveSyncRoot(_config);
        if (syncRoot is null)
        {
            Program.Log($"[litho] Job {job.JobId}: Drive for Desktop is not mounted on this PC, so the photo cannot be read. Failing honestly.");
            await ReportFailedAsync(job, "Drive for Desktop is not mounted on the shop PC, so the lithophane photo could not be read from Drive.", ct);
            return;
        }
        var photoFile = Path.GetFileName((job.PhotoName ?? "").Trim());
        if (string.IsNullOrWhiteSpace(photoFile) || !string.Equals(photoFile, (job.PhotoName ?? "").Trim(), StringComparison.Ordinal))
        {
            await ReportFailedAsync(job, "The photo name in the generation job is not a plain file name.", ct);
            return;
        }
        var driveOrderDir = Path.Combine(syncRoot, "Lithophane Orders", "order-" + job.OrderId);
        var photoLocal = Path.Combine(driveOrderDir, photoFile);
        if (!File.Exists(photoLocal))
        {
            Program.Log($"[litho] Job {job.JobId}: photo '{photoFile}' was not found in the local Drive copy (Lithophane Orders / order-{job.OrderId}). Drive sync may be lagging — the job stays failed, not retried in a loop; re-queue it from the order page once the photo is visible in Drive on this PC.");
            await ReportFailedAsync(job, $"Photo '{photoFile}' was not found in the local Drive copy (Lithophane Orders / order-{job.OrderId}). Check Drive sync on the shop PC, then re-queue from the order page.", ct);
            return;
        }
        if (job.PhotoSize > 0 && new FileInfo(photoLocal).Length != job.PhotoSize)
        {
            Program.Log($"[litho] Job {job.JobId}: local photo size does not match the job's photo size — the sync copy may be partial. Failing honestly.");
            await ReportFailedAsync(job, "The local Drive copy of the photo does not match its expected size (sync may be incomplete).", ct);
            return;
        }

        var safeJob = Sanitize(job.JobId);
        var workDir = Path.Combine(Config.TempDir, "litho_" + safeJob);
        var outDir = Path.Combine(workDir, "out");
        try { Directory.CreateDirectory(outDir); }
        catch (Exception ex)
        {
            await ReportFailedAsync(job, $"Could not create the generation work folder: {ex.Message}", ct);
            return;
        }
        var photoWork = Path.Combine(workDir, photoFile);
        var model3mf = Path.Combine(workDir, "model.3mf");
        try { File.Copy(photoLocal, photoWork, overwrite: true); }
        catch (Exception ex)
        {
            await ReportFailedAsync(job, $"Could not read the photo from the local Drive copy: {ex.Message}", ct);
            CleanupDir(workDir);
            return;
        }

        // Step 1 — mesh. Runs synchronously here: the script
        // caps its grid (200 samples across), so it finishes
        // in seconds; the 5-minute tool timeout bounds it.
        var (meshOk, meshOut, meshErr) = await RunToolWithOutputAsync(
            pythonExe, $"\"{script}\" --photo \"{photoWork}\" --out \"{model3mf}\" --stl \"{Path.Combine(workDir, "model.stl")}\"", MeshTimeout, ct);
        if (!meshOk || !File.Exists(model3mf) || new FileInfo(model3mf).Length == 0)
        {
            var why = MeshSummaryError(meshOut) ?? TrimReason(meshErr);
            Program.Log($"[litho] Job {job.JobId}: mesh generation failed{(string.IsNullOrWhiteSpace(why) ? "." : ": " + why)}");
            await ReportFailedAsync(job, $"Lithophane mesh generation failed{(string.IsNullOrWhiteSpace(why) ? "." : ": " + why)}", ct);
            CleanupDir(workDir);
            return;
        }
        Program.Log($"[litho] Job {job.JobId}: mesh generated ({new FileInfo(model3mf).Length} bytes). Slicing for {job.TargetModel}.");

        // Step 2 — slice. Same CLI form and verification as
        // SlicePoller; runs in the background, never blocking
        // the worker loop.
        var baseName = OutputBaseName(photoFile);
        var slug = PrinterSlug(job.TargetModel);
        var logPath = Path.Combine(workDir, "slice.log");
        var args = $"\"{model3mf}\" --load-settings \"{printerJson};{processJson}\" " +
            $"--load-filaments \"{filamentJson}\" --slice 1 " +
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
            Program.Log($"[litho] Job {job.JobId}: could not start the slicer ({ex.Message}).");
            await ReportFailedAsync(job, $"Could not start the slicer: {ex.Message}", ct);
            CleanupDir(workDir);
            return;
        }
        _running = new RunningLitho(job, proc, workDir, outDir, model3mf, driveOrderDir, baseName, slug, DateTime.UtcNow);
        Program.Log($"[litho] Job {job.JobId}: slicing started with '{slicer}' for {job.TargetModel} (runs in the background; status polls continue).");
    }

    private async Task CheckRunningAsync(CancellationToken ct)
    {
        var run = _running!;
        var timeout = TimeSpan.FromMinutes(_config.SliceTimeoutMinutes <= 0 ? 15 : _config.SliceTimeoutMinutes);
        if (!run.Process.HasExited && DateTime.UtcNow - run.StartedAt > timeout)
        {
            try { run.Process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            Program.Log($"[litho] Job {run.Job.JobId}: slicing timed out after {timeout.TotalMinutes} minutes. Killed and failed.");
            await ReportFailedAsync(run.Job, $"Slicing timed out after {timeout.TotalMinutes} minutes.", ct);
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
            Program.Log($"[litho] Job {run.Job.JobId}: slicer exited with code {exitCode}. Failed; see the slice log on the shop PC.");
            await ReportFailedAsync(run.Job, $"Slicer exited with code {exitCode}. See the slice log on the shop PC.", ct);
            CleanupDir(run.WorkDir);
            return;
        }
        // Find plate_1.gcode in the output dir; if the slicer
        // only wrote a sliced 3MF, extract Metadata/plate_1.gcode
        // from it (same as SlicePoller).
        var gcodePath = Path.Combine(run.OutDir, "plate_1.gcode");
        string? slicedPlate = null;
        if (!File.Exists(gcodePath))
        {
            var threeMf = Directory.GetFiles(run.OutDir, "*.3mf").FirstOrDefault();
            if (threeMf is not null)
            {
                slicedPlate = threeMf;
                try
                {
                    using var zip = ZipFile.OpenRead(threeMf);
                    var entry = zip.GetEntry("Metadata/plate_1.gcode");
                    if (entry is not null)
                    {
                        entry.ExtractToFile(gcodePath, overwrite: true);
                        Program.Log($"[litho] Job {run.Job.JobId}: plate G-code extracted from the sliced 3MF.");
                    }
                }
                catch (Exception ex)
                {
                    Program.Log($"[litho] Job {run.Job.JobId}: could not extract plate G-code from the sliced 3MF ({ex.Message}).");
                }
            }
        }
        else
        {
            slicedPlate = Directory.GetFiles(run.OutDir, "*.3mf").FirstOrDefault();
        }
        if (!File.Exists(gcodePath))
        {
            Program.Log($"[litho] Job {run.Job.JobId}: slicer exited 0 but no plate_1.gcode was produced. Failed; nothing written to Drive.");
            await ReportFailedAsync(run.Job, "Slicer finished but produced no plate_1.gcode.", ct);
            CleanupDir(run.WorkDir);
            return;
        }
        var verify = VerifyGcode(gcodePath);
        if (verify is not null)
        {
            Program.Log($"[litho] Job {run.Job.JobId}: G-code verification failed ({verify}). Nothing written to Drive.");
            await ReportFailedAsync(run.Job, $"Sliced G-code failed verification: {verify}", ct);
            CleanupDir(run.WorkDir);
            return;
        }
        // Write the artifacts into the local Drive order
        // folder — Drive sync carries them to Drive. Names
        // are hyphen-only, per the standing file-name rule.
        var modelName = $"{run.BaseName}-lithophane-100mm.3mf";
        var gcodeName = $"{run.BaseName}-lithophane-100mm-{run.PrinterSlug}.gcode";
        string? plateName = null;
        try
        {
            Directory.CreateDirectory(run.DriveOrderDir);
            File.Copy(run.Model3mfPath, Path.Combine(run.DriveOrderDir, modelName), overwrite: true);
            File.Copy(gcodePath, Path.Combine(run.DriveOrderDir, gcodeName), overwrite: true);
            if (slicedPlate is not null && File.Exists(slicedPlate))
            {
                plateName = $"{run.BaseName}-lithophane-100mm-{run.PrinterSlug}-plate.gcode.3mf";
                File.Copy(slicedPlate, Path.Combine(run.DriveOrderDir, plateName), overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Program.Log($"[litho] Job {run.Job.JobId}: could not write the print files into the Drive order folder ({ex.Message}).");
            await ReportFailedAsync(run.Job, $"Print files could not be written to the Drive order folder: {ex.Message}", ct);
            CleanupDir(run.WorkDir);
            return;
        }
        var modelBytes = new FileInfo(Path.Combine(run.DriveOrderDir, modelName)).Length;
        var gcodeBytes = new FileInfo(Path.Combine(run.DriveOrderDir, gcodeName)).Length;
        if (modelBytes <= 0 || gcodeBytes <= 0)
        {
            await ReportFailedAsync(run.Job, "A print file written to the Drive order folder came back empty.", ct);
            CleanupDir(run.WorkDir);
            return;
        }
        var layers = CountLayers(gcodePath);
        var sent = await _dashboard.ReportLithophaneAsync(run.Job.JobId, "done", "",
            modelName, gcodeName, plateName, modelBytes, gcodeBytes, layers, ct);
        if (sent)
        {
            _reported.Add(run.Job.JobId);
            Program.Log($"[litho] Job {run.Job.JobId}: done — {modelName} ({modelBytes} bytes) + {gcodeName} ({gcodeBytes} bytes, {layers} layers) in Lithophane Orders / order-{run.Job.OrderId}.");
        }
        else
        {
            Program.Log($"[litho] Job {run.Job.JobId}: files are in the Drive order folder but the report did not reach the dashboard; the job stays pending and will be re-run (the files will be overwritten with the same names).");
        }
        CleanupDir(run.WorkDir);
    }

    private async Task ReportFailedAsync(LithophaneJob job, string reason, CancellationToken ct)
    {
        var sent = await _dashboard.ReportLithophaneAsync(job.JobId, "failed", reason, null, null, null, 0, 0, 0, ct);
        if (sent)
            _reported.Add(job.JobId);
        Program.Log($"[litho] Job {job.JobId}: failed — {reason}");
    }

    /// <summary>The mesh script prints one JSON line; pull its
    /// error out of it when present, so the failure reason
    /// names the cause instead of the raw stdout.</summary>
    private static string? MeshSummaryError(string stdout)
    {
        try
        {
            var line = (stdout ?? "").Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith("{"));
            if (line is null) return null;
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False &&
                doc.RootElement.TryGetProperty("error", out var err))
                return err.GetString();
            return null;
        }
        catch { return null; }
    }

    /// <summary>Output base name from the photo name: no
    /// underscores, no invalid characters, hyphens only.</summary>
    private static string OutputBaseName(string photoFile)
    {
        var stem = Path.GetFileNameWithoutExtension(photoFile);
        foreach (var c in Path.GetInvalidFileNameChars())
            stem = stem.Replace(c, '-');
        stem = stem.Replace('_', '-');
        stem = stem.Trim('-');
        return string.IsNullOrWhiteSpace(stem) ? "lithophane" : stem;
    }

    private static string PrinterSlug(string targetModel)
    {
        var m = (targetModel ?? "").ToLowerInvariant();
        if (m.Contains("u1")) return "u1";
        if (m.Contains("p2s")) return "p2s";
        if (m.Contains("x2d")) return "x2d";
        var slug = new string(m.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
        return string.IsNullOrWhiteSpace(slug) ? "printer" : slug;
    }

    /// <summary>Layer count, most reliable source first:
    /// (1) exact ";LAYER_CHANGE" lines — OrcaSlicer/Klipper
    /// flavor emits one per layer; (2) the "; total layer
    /// number: N" header line; (3) distinct Z values on
    /// Z-only moves. NOT distinct Z across all G1 lines —
    /// Z-hop and varying decimal precision inflate that
    /// wildly (a 636-layer U1 lithophane measured 15,134
    /// distinct Z strings that way).</summary>
    private static int CountLayers(string gcodePath)
    {
        try
        {
            var changes = 0;
            var total = 0;
            var zOnly = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in File.ReadLines(gcodePath))
            {
                var t = line.Trim();
                if (t.Equals(";LAYER_CHANGE", StringComparison.Ordinal))
                    changes++;
                else if (total == 0 && t.StartsWith("; total layer number:", StringComparison.Ordinal) &&
                    int.TryParse(t["; total layer number:".Length..].Trim(), out var n))
                    total = n;
                else if (t.StartsWith("G1 Z", StringComparison.Ordinal) &&
                    !t.Contains('X') && !t.Contains('Y') && !t.Contains('E'))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(t, @"\bZ(-?\d+\.?\d*)");
                    if (m.Success && double.TryParse(m.Groups[1].Value,
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var z))
                        zOnly.Add(z.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            if (changes > 0) return changes;
            if (total > 0) return total;
            return zOnly.Count;
        }
        catch { return 0; }
    }

    /// <summary>Byte-level G-code verification — the same rule
    /// as SlicePoller.VerifyGcode (plain text, real G/M/T
    /// lines, never a ZIP, never NUL bytes). Duplicated here
    /// so this poller stays self-contained; the two must stay
    /// in step.</summary>
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

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "litho-job" : name;
    }

    private static void CleanupDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
