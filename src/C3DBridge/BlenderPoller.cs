// C3D Print Bridge — Blender mesh poller
// (2026.1109.0163-blender-mesh-tool).
//
// "Blender mesh tool" means: the dashboard records a Check
// mesh / Fix mesh button press on a File Library entry,
// and THIS bridge — on 3d1PC, where Blender is installed —
// runs Blender headless on the library file and reports the
// result back. Blender NEVER runs on the Hostinger web
// server (shared hosting cannot run desktop binaries), and
// the bridge NEVER invents a result: a Blender failure is
// reported with Blender's own error text.
//
// FLOW (one Blender job at a time — Blender is GPU/CPU-heavy):
//  - Poll GET farm/blender-jobs for pending Blender jobs.
//  - Resolve blender.exe from bridge-config (blender_path;
//    default D:\C3D-Tools\Blender\blender.exe on 3d1PC;
//    empty/missing = not configured → fail honestly, never
//    a guessed binary) and the bundled scripts from
//    tools\blender under the bridge install folder.
//  - Download the library file over the authenticated
//    blender-file route (SHA-256 verified). A 3MF input is
//    converted to STL first (tools\3mf-to-stl.py, Python +
//    trimesh) because Blender has no 3MF importer; the
//    conversion merges all parts into one mesh. Other
//    formats (STL / OBJ / GLB / GLTF / PLY) go straight to
//    Blender.
//  - mesh_check: one Blender run of mesh_report.py --json.
//    The MESH_JSON line is parsed and reported as the
//    result; the dashboard computes the pass/needs-work
//    verdict from the counts.
//  - mesh_fix: three Blender runs in sequence —
//    mesh_report.py --json (before), clean_for_print.py
//    (Recipe A: join, Merge by Distance, Delete Loose,
//    Degenerate Dissolve, Recalculate Normals, origin to
//    bottom centre; no height/ratio, so the original size
//    is kept and nothing is decimated), mesh_report.py
//    --json on the fixed STL (after). The before/after
//    results and the fixed STL (base64, 64 MB cap) are
//    reported; the dashboard saves the fixed file as a NEW
//    library entry and never overwrites the original.
//  - Each Blender run happens in the background (never
//    blocking the worker loop — status polls and heartbeats
//    keep flowing). A failed run is KEPT, not deleted: the
//    work folder is moved to failed-blender\blender_<jobId>
//    _FAILED_<YYYYMMDD-HHMM> under the app dir, and the
//    tail of Blender's stdout/stderr goes into bridge.log.
//    Only a successful job deletes its work folder.

using System.Diagnostics;
using System.Text.Json;

namespace C3DBridge;

public sealed class BlenderPoller
{
    private const long MaxReportBytes = 64L * 1024 * 1024;
    private const string DefaultBlenderPath = @"D:\C3D-Tools\Blender\blender.exe";

    private readonly DashboardClient _dashboard;
    private readonly BridgeConfig _config;
    private RunningBlender? _running;
    private bool _endpointMissingLogged;

    private sealed record RunningBlender(
        BlenderJob Job,
        Process Process,
        string WorkDir,
        string ModelPath,
        string FixedStlPath,
        string Phase,
        DateTime StartedAt,
        Task<string> StdOutTask,
        Task<string> StdErrTask,
        JsonElement? Before);

    public BlenderPoller(DashboardClient dashboard, BridgeConfig config)
    {
        _dashboard = dashboard;
        _config = config;
    }

    /// <summary>True while a Blender run is in progress on
    /// this bridge.</summary>
    public bool IsBlending => _running is not null;

    public async Task PollAsync(CancellationToken ct)
    {
        if (!_dashboard.HasKey)
            return;
        if (_running is not null)
        {
            await CheckRunningAsync(ct);
            return;
        }
        var (jobs, problem) = await _dashboard.PollBlenderJobsAsync(ct);
        if (jobs is null)
        {
            // 404 = the dashboard Blender routes are not
            // installed yet: log once, then stay quiet
            // until restart, exactly like the OCR rule.
            if (problem is not null && problem.Contains("not installed", StringComparison.OrdinalIgnoreCase))
            {
                if (!_endpointMissingLogged)
                {
                    _endpointMissingLogged = true;
                    Program.Log($"[blender] Poll failed: {problem}");
                }
                return;
            }
            if (!string.IsNullOrWhiteSpace(problem))
                Program.Log($"[blender] Poll failed: {problem}");
            return;
        }
        foreach (var job in jobs)
        {
            await StartBlenderAsync(job, ct);
            // One Blender job at a time; the rest wait for later polls.
            if (_running is not null) break;
        }
    }

    private async Task StartBlenderAsync(BlenderJob job, CancellationToken ct)
    {
        var action = (job.Action ?? "").Trim().ToLowerInvariant();
        if (action != "mesh_check" && action != "mesh_fix")
        {
            Program.Log($"[blender] Job {job.BlenderJobId}: unknown action '{job.Action}'. Failing honestly, Blender never ran.");
            await _dashboard.ReportBlenderAsync(job.BlenderJobId, "failed", $"Unknown Blender action '{job.Action}'.", null, null, null, ct);
            return;
        }
        var exe = string.IsNullOrWhiteSpace(_config.BlenderPath) ? DefaultBlenderPath : _config.BlenderPath!.Trim();
        if (!File.Exists(exe))
        {
            Program.Log($"[blender] Job {job.BlenderJobId}: Blender is not installed on this bridge (no blender.exe at '{exe}'). Failing honestly.");
            await _dashboard.ReportBlenderAsync(job.BlenderJobId, "failed", "Blender is not installed on this bridge (no blender.exe found). Install Blender in the 3D tools folder and set blender_path in bridge-config.json.", null, null, null, ct);
            return;
        }
        var scriptsDir = Path.Combine(Config.AppDir, "tools", "blender");
        var reportScript = Path.Combine(scriptsDir, "mesh_report.py");
        var cleanScript = Path.Combine(scriptsDir, "clean_for_print.py");
        if (!File.Exists(reportScript))
        {
            Program.Log($"[blender] Job {job.BlenderJobId}: mesh_report.py is missing (tools\\blender\\mesh_report.py). Failing honestly.");
            await _dashboard.ReportBlenderAsync(job.BlenderJobId, "failed", "The Blender mesh scripts are missing on this bridge (tools\\blender\\mesh_report.py). Redeploy the bridge.", null, null, null, ct);
            return;
        }
        if (action == "mesh_fix" && !File.Exists(cleanScript))
        {
            Program.Log($"[blender] Job {job.BlenderJobId}: clean_for_print.py is missing (tools\\blender\\clean_for_print.py). Failing honestly.");
            await _dashboard.ReportBlenderAsync(job.BlenderJobId, "failed", "The Blender mesh scripts are missing on this bridge (tools\\blender\\clean_for_print.py). Redeploy the bridge.", null, null, null, ct);
            return;
        }
        var ext = (job.InputExt ?? "").Trim().ToLowerInvariant();
        if (ext is not ("3mf" or "stl" or "obj" or "glb" or "gltf" or "ply"))
        {
            Program.Log($"[blender] Job {job.BlenderJobId}: unsupported input extension '{job.InputExt}'. Failing honestly.");
            await _dashboard.ReportBlenderAsync(job.BlenderJobId, "failed", $"Blender cannot check a .{job.InputExt} file.", null, null, null, ct);
            return;
        }

        var workDir = Path.Combine(Config.TempDir, "blender_" + job.BlenderJobId);
        try { Directory.CreateDirectory(workDir); }
        catch (Exception ex)
        {
            await _dashboard.ReportBlenderAsync(job.BlenderJobId, "failed", $"Could not create the Blender work folder: {ex.Message}", null, null, null, ct);
            return;
        }
        var inputPath = Path.Combine(workDir, "input." + ext);
        var (dlOk, dlReason) = await _dashboard.DownloadFileAsync(job.InputUrl, inputPath, job.InputSha256, ct);
        if (!dlOk)
        {
            Program.Log($"[blender] Job {job.BlenderJobId}: library file download failed ({dlReason}).");
            await _dashboard.ReportBlenderAsync(job.BlenderJobId, "failed", $"Library file download failed: {dlReason}", null, null, null, ct);
            RetainFailedBlenderDir(workDir, job.BlenderJobId);
            return;
        }

        // Blender has no 3MF importer, so a 3MF input is
        // converted to STL first (the conversion merges all
        // parts into one mesh — the same merge the Fix run
        // does later). Other formats go straight to Blender.
        // A failed conversion fails the job honestly — a
        // Blender run is never started from a bad file.
        var modelPath = inputPath;
        if (ext == "3mf")
        {
            var stlPath = Path.Combine(workDir, "input.stl");
            var (convOk, convReason) = await Convert3mfToStlAsync(inputPath, stlPath, ct);
            if (!convOk)
            {
                Program.Log($"[blender] Job {job.BlenderJobId}: 3MF-to-STL conversion failed ({convReason}).");
                await _dashboard.ReportBlenderAsync(job.BlenderJobId, "failed", $"Could not convert the 3MF model to STL for Blender: {convReason}", null, null, null, ct);
                RetainFailedBlenderDir(workDir, job.BlenderJobId);
                return;
            }
            modelPath = stlPath;
        }

        var fixedPath = Path.Combine(workDir, "fixed.stl");
        var firstPhase = action == "mesh_fix" ? "fix-before" : "check";
        var started = await StartBlenderRun(job, workDir, modelPath, fixedPath, firstPhase, null,
            reportScript, $"-- --in \"{modelPath}\" --json", ct);
        if (!started)
        {
            RetainFailedBlenderDir(workDir, job.BlenderJobId);
            return;
        }
        Program.Log($"[blender] Job {job.BlenderJobId}: Blender {action} started on '{Path.GetFileName(modelPath)}' (runs in the background; status polls continue).");
        // Claim the job the moment the first Blender run
        // starts, so the dashboard marks it 'running' and no
        // second bridge can take it (first claim wins). A
        // claim that fails to reach the dashboard is logged,
        // not fatal: the running job still reports done/failed
        // when it finishes.
        var claimed = await _dashboard.ReportBlenderAsync(job.BlenderJobId, "started", "", null, null, null, ct);
        if (!claimed)
            Program.Log($"[blender] Job {job.BlenderJobId}: the 'started' claim did not reach the dashboard; the Blender run is still going locally and will report done/failed when it finishes.");
    }

    /// <summary>Start one Blender run in the background and
    /// record it as the running job. Returns false (and
    /// reports the job failed) when Blender could not be
    /// started at all. The caller keeps the work folder on
    /// a false return.</summary>
    private async Task<bool> StartBlenderRun(BlenderJob job, string workDir, string modelPath, string fixedPath,
        string phase, JsonElement? before, string script, string scriptArgs, CancellationToken ct)
    {
        var exe = string.IsNullOrWhiteSpace(_config.BlenderPath) ? DefaultBlenderPath : _config.BlenderPath!.Trim();
        var args = $"--background --python \"{script}\" --python-exit-code 1 {scriptArgs}";
        // Log the exact command line before starting: when
        // Blender exits with a bare code the command is the
        // first thing needed, and nothing else on the PC
        // records it. Paths only — no secrets ever pass
        // through here.
        Program.Log($"[blender] Job {job.BlenderJobId}: running {exe} {args}");
        var start = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Capture Blender's own output: without
            // redirection a failing run's error text goes
            // nowhere and only its exit code survives. The
            // reads start immediately so a chatty run can
            // never block on a full pipe.
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
            Program.Log($"[blender] Job {job.BlenderJobId}: could not start Blender ({ex.Message}).");
            await _dashboard.ReportBlenderAsync(job.BlenderJobId, "failed", $"Could not start Blender: {ex.Message}", null, null, null, ct);
            return false;
        }
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        _running = new RunningBlender(job, proc, workDir, modelPath, fixedPath, phase, DateTime.UtcNow, stdoutTask, stderrTask, before);
        return true;
    }

    private async Task CheckRunningAsync(CancellationToken ct)
    {
        var run = _running!;
        var timeout = TimeSpan.FromMinutes(_config.BlenderTimeoutMinutes <= 0 ? 15 : _config.BlenderTimeoutMinutes);
        if (!run.Process.HasExited && DateTime.UtcNow - run.StartedAt > timeout)
        {
            try { run.Process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            var timeoutStdout = await ReadOutputSoonAsync(run.StdOutTask);
            var timeoutStderr = await ReadOutputSoonAsync(run.StdErrTask);
            run.Process.Dispose();
            _running = null;
            LogBlenderOutputTail(run.Job.BlenderJobId, timeoutStdout, timeoutStderr);
            var timeoutKept = RetainFailedBlenderDir(run.WorkDir, run.Job.BlenderJobId);
            var timeoutHint = EvidenceSuffix(timeoutKept);
            Program.Log($"[blender] Job {run.Job.BlenderJobId}: Blender timed out after {timeout.TotalMinutes} minutes in phase '{run.Phase}'. Killed and failed.{timeoutHint}");
            await _dashboard.ReportBlenderAsync(run.Job.BlenderJobId, "failed", $"Blender timed out after {timeout.TotalMinutes} minutes in phase '{run.Phase}'.{timeoutHint}", null, null, null, ct);
            return;
        }
        if (!run.Process.HasExited)
            return;
        var exitCode = run.Process.ExitCode;
        // Collect Blender's output BEFORE disposing the
        // process: disposing closes the redirected streams
        // and the reads would come back empty.
        var stdout = await ReadOutputSoonAsync(run.StdOutTask);
        var stderr = await ReadOutputSoonAsync(run.StdErrTask);
        run.Process.Dispose();
        _running = null;
        if (exitCode != 0)
        {
            LogBlenderOutputTail(run.Job.BlenderJobId, stdout, stderr);
            var kept = RetainFailedBlenderDir(run.WorkDir, run.Job.BlenderJobId);
            var errTail = TailText(stderr, 300);
            var failReason = string.IsNullOrWhiteSpace(errTail)
                ? $"Blender exited with code {exitCode} and printed no error."
                : $"Blender exited with code {exitCode}: {errTail}";
            Program.Log($"[blender] Job {run.Job.BlenderJobId}: {failReason} Failed; nothing attached.{EvidenceSuffix(kept)}");
            await _dashboard.ReportBlenderAsync(run.Job.BlenderJobId, "failed", $"{failReason}{EvidenceSuffix(kept)}", null, null, null, ct);
            return;
        }

        if (run.Phase == "check")
        {
            await FinishCheckAsync(run, stdout, ct);
            return;
        }
        if (run.Phase == "fix-before")
        {
            var before = ParseMeshJson(stdout);
            if (before is null)
            {
                await FailMissingJsonAsync(run, stdout, stderr, "before the fix", ct);
                return;
            }
            // Next run: clean_for_print.py (Recipe A, no
            // height/ratio — keeps the original size, no
            // decimate) on the same model, writing fixed.stl.
            var cleanScript = Path.Combine(Config.AppDir, "tools", "blender", "clean_for_print.py");
            var ok = await StartBlenderRun(run.Job, run.WorkDir, run.ModelPath, run.FixedStlPath, "fix-clean", before,
                cleanScript, $"-- --in \"{run.ModelPath}\" --out \"{run.FixedStlPath}\"", ct);
            if (!ok)
            {
                RetainFailedBlenderDir(run.WorkDir, run.Job.BlenderJobId);
                return;
            }
            Program.Log($"[blender] Job {run.Job.BlenderJobId}: before-check done; running the fix (Recipe A).");
            return;
        }
        if (run.Phase == "fix-clean")
        {
            if (!File.Exists(run.FixedStlPath) || new FileInfo(run.FixedStlPath).Length == 0)
            {
                var kept = RetainFailedBlenderDir(run.WorkDir, run.Job.BlenderJobId);
                Program.Log($"[blender] Job {run.Job.BlenderJobId}: the fix run exited 0 but wrote no fixed STL. Failed; nothing attached.{EvidenceSuffix(kept)}");
                await _dashboard.ReportBlenderAsync(run.Job.BlenderJobId, "failed", $"Blender finished the fix but wrote no fixed STL file.{EvidenceSuffix(kept)}", null, null, null, ct);
                return;
            }
            // Next run: mesh_report.py --json on the fixed STL.
            var reportScript = Path.Combine(Config.AppDir, "tools", "blender", "mesh_report.py");
            var ok = await StartBlenderRun(run.Job, run.WorkDir, run.ModelPath, run.FixedStlPath, "fix-after", run.Before,
                reportScript, $"-- --in \"{run.FixedStlPath}\" --json", ct);
            if (!ok)
            {
                RetainFailedBlenderDir(run.WorkDir, run.Job.BlenderJobId);
                return;
            }
            Program.Log($"[blender] Job {run.Job.BlenderJobId}: fix done; checking the fixed file.");
            return;
        }
        if (run.Phase == "fix-after")
        {
            await FinishFixAsync(run, stdout, ct);
            return;
        }
        // An unknown phase can only come from this code, so
        // fail honestly rather than reporting a result the
        // dashboard cannot place.
        var unknownKept = RetainFailedBlenderDir(run.WorkDir, run.Job.BlenderJobId);
        Program.Log($"[blender] Job {run.Job.BlenderJobId}: unknown Blender phase '{run.Phase}'. Failed; nothing attached.{EvidenceSuffix(unknownKept)}");
        await _dashboard.ReportBlenderAsync(run.Job.BlenderJobId, "failed", $"Blender finished an unknown phase '{run.Phase}'.{EvidenceSuffix(unknownKept)}", null, null, null, ct);
    }

    private async Task FinishCheckAsync(RunningBlender run, string stdout, CancellationToken ct)
    {
        var result = ParseMeshJson(stdout);
        if (result is null)
        {
            await FailMissingJsonAsync(run, stdout, "", "the mesh check", ct);
            return;
        }
        var summary = CheckSummary(result.Value);
        var sent = await _dashboard.ReportBlenderAsync(run.Job.BlenderJobId, "done", summary, result, null, null, ct);
        Program.Log(sent
            ? $"[blender] Job {run.Job.BlenderJobId}: mesh check reported ({summary})."
            : $"[blender] Job {run.Job.BlenderJobId}: mesh check finished but the report did not reach the dashboard; the job stays running and will be retried (Blender will run again).");
        CleanupDir(run.WorkDir);
    }

    private async Task FinishFixAsync(RunningBlender run, string stdout, CancellationToken ct)
    {
        var after = ParseMeshJson(stdout);
        if (after is null)
        {
            await FailMissingJsonAsync(run, stdout, "", "after the fix", ct);
            return;
        }
        var combined = CombineBeforeAfter(run.Before, after.Value);
        if (combined is null)
        {
            var kept = RetainFailedBlenderDir(run.WorkDir, run.Job.BlenderJobId);
            Program.Log($"[blender] Job {run.Job.BlenderJobId}: could not combine the before/after results. Failed; nothing attached.{EvidenceSuffix(kept)}");
            await _dashboard.ReportBlenderAsync(run.Job.BlenderJobId, "failed", $"Blender finished the fix but the before/after results could not be combined.{EvidenceSuffix(kept)}", null, null, null, ct);
            return;
        }
        var bytes = await File.ReadAllBytesAsync(run.FixedStlPath, ct);
        if (bytes.Length > MaxReportBytes)
        {
            var tooBigKept = RetainFailedBlenderDir(run.WorkDir, run.Job.BlenderJobId);
            Program.Log($"[blender] Job {run.Job.BlenderJobId}: fixed STL is {bytes.Length} bytes, over the {MaxReportBytes} report cap. Failed; nothing attached.{EvidenceSuffix(tooBigKept)}");
            await _dashboard.ReportBlenderAsync(run.Job.BlenderJobId, "failed", $"Fixed STL is too large to report to the dashboard.{EvidenceSuffix(tooBigKept)}", null, null, null, ct);
            return;
        }
        // Fixed-file name rule (Nov 7, 2026): no underscores.
        // The library entry id has them, so they become
        // hyphens here; the dashboard strips any that remain.
        var fixedName = run.Job.LibraryFileId.Replace('_', '-') + "-blender-fix.stl";
        var summary = FixSummary(run.Before, after.Value);
        var sent = await _dashboard.ReportBlenderAsync(run.Job.BlenderJobId, "done", summary, combined, Convert.ToBase64String(bytes), fixedName, ct);
        Program.Log(sent
            ? $"[blender] Job {run.Job.BlenderJobId}: mesh fix reported ({summary}; {bytes.Length} bytes, {fixedName})."
            : $"[blender] Job {run.Job.BlenderJobId}: mesh fix finished but the report did not reach the dashboard; the job stays running and will be retried (Blender will run again).");
        CleanupDir(run.WorkDir);
    }

    private async Task FailMissingJsonAsync(RunningBlender run, string stdout, string stderr, string where, CancellationToken ct)
    {
        LogBlenderOutputTail(run.Job.BlenderJobId, stdout, stderr);
        var kept = RetainFailedBlenderDir(run.WorkDir, run.Job.BlenderJobId);
        Program.Log($"[blender] Job {run.Job.BlenderJobId}: Blender finished {where} but printed no mesh result. Failed; nothing attached.{EvidenceSuffix(kept)}");
        await _dashboard.ReportBlenderAsync(run.Job.BlenderJobId, "failed", $"Blender finished {where} but printed no mesh result (no MESH_JSON line).{EvidenceSuffix(kept)}", null, null, null, ct);
    }

    /// <summary>Parse the last MESH_JSON line from a
    /// mesh_report.py run. Returns null when no such line
    /// exists or it does not parse — a missing result is a
    /// failure, never a guessed one.</summary>
    private static JsonElement? ParseMeshJson(string stdout)
    {
        string? line = null;
        foreach (var raw in (stdout ?? "").Split('\n'))
        {
            var t = raw.Trim();
            if (t.StartsWith("MESH_JSON: ", StringComparison.Ordinal))
                line = t["MESH_JSON: ".Length..];
        }
        if (line is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Combine a before and an after check result
    /// into one { before, after } JSON object for the fix
    /// report. Returns null when either side is missing.</summary>
    private static JsonElement? CombineBeforeAfter(JsonElement? before, JsonElement after)
    {
        if (before is null) return null;
        try
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WritePropertyName("before");
                before.Value.WriteTo(w);
                w.WritePropertyName("after");
                after.WriteTo(w);
                w.WriteEndObject();
            }
            using var doc = JsonDocument.Parse(ms.ToArray());
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static int JsonInt(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : 0;

    private static bool JsonBool(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string CheckSummary(JsonElement result)
        => $"Mesh check: {JsonInt(result, "faces")} faces, manifold: {(JsonBool(result, "manifold") ? "yes" : "no")}, " +
           $"{JsonInt(result, "degenerate_faces")} degenerate faces, {JsonInt(result, "non_manifold_edges")} non-manifold edges, " +
           $"{JsonInt(result, "inverted_normals")} inverted normals, {JsonInt(result, "loose_verts")} loose vertices.";

    private static string FixSummary(JsonElement? before, JsonElement after)
        => before is null
            ? $"Mesh fix: {JsonInt(after, "degenerate_faces")} degenerate faces, {JsonInt(after, "non_manifold_edges")} non-manifold edges after the fix."
            : $"Mesh fix: degenerate {JsonInt(before.Value, "degenerate_faces")} → {JsonInt(after, "degenerate_faces")}, " +
              $"non-manifold edges {JsonInt(before.Value, "non_manifold_edges")} → {JsonInt(after, "non_manifold_edges")}.";

    /// <summary>Convert a 3MF model to STL with the bundled
    /// Python script (tools\3mf-to-stl.py, trimesh) — the
    /// Blender path only, because Blender has no 3MF
    /// importer. Returns (ok, reason); a failure means the
    /// Blender run must not start.</summary>
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
            return (false, "Python is not installed on the shop PC, so the 3MF model could not be converted to STL for Blender. Install Python 3.10+ with trimesh and networkx (pip install trimesh networkx) and make python available on PATH.");
        var script = Path.Combine(Config.AppDir, "tools", "3mf-to-stl.py");
        if (!File.Exists(script))
            return (false, "The 3MF-to-STL script is missing on the shop PC (tools\\3mf-to-stl.py). Redeploy the bridge.");
        var (ok, text, reason) = await RunToolWithOutputAsync(
            pythonExe, $"\"{script}\" --in \"{input3mf}\" --out \"{outputStl}\"", TimeSpan.FromMinutes(5), ct);
        if (!ok)
            return (false, string.IsNullOrWhiteSpace(reason) ? "the converter failed." : reason);
        if (!File.Exists(outputStl) || new FileInfo(outputStl).Length == 0)
            return (false, "the converter reported success but wrote no STL file.");
        Program.Log($"[blender] 3MF converted to STL for Blender ({new FileInfo(outputStl).Length} bytes; {TrimReason(text)}).");
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

    /// <summary>Last non-blank lines of Blender's stderr,
    /// trimmed to a short tail for the failure reason —
    /// Blender's own error text, not a paraphrase.</summary>
    private static string TailText(string? text, int maxChars)
    {
        var lines = new List<string>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            lines.Add(line);
        }
        var tail = string.Join(" ", lines.TakeLast(3));
        return tail.Length <= maxChars ? tail : tail[..maxChars] + "…";
    }

    /// <summary>Move a failed Blender job's work folder out
    /// of temp into failed-blender under the app dir, stamped
    /// with the job id and the local date+time (YYYYMMDD-
    /// HHMM, the same date+time rule as log archives) so
    /// one failure never overwrites another. The folder
    /// keeps the downloaded input, any converted STL, and
    /// any partial output. Returns the retained folder name,
    /// or null when the move failed — then the folder is
    /// deleted instead so temp does not fill up, and the
    /// caller says so.</summary>
    private static string? RetainFailedBlenderDir(string workDir, string jobId)
    {
        // Job ids come from the dashboard ("blender_<n>_<n>")
        // but never trust a name that ends up in a path.
        var safeId = string.Concat(jobId.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        if (string.IsNullOrWhiteSpace(safeId)) safeId = "unknown";
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmm");
        var name = $"blender_{safeId}_FAILED_{stamp}";
        var retainedRoot = Path.Combine(Config.AppDir, "failed-blender");
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
            Program.Log($"[blender] Job {jobId}: failed Blender job kept in failed-blender\\{kept} (input, converted STL and any partial output).");
            return kept;
        }
        catch (Exception ex)
        {
            Program.Log($"[blender] Job {jobId}: could not retain the failed Blender folder ({ex.Message}); deleting it instead.");
            CleanupDir(workDir);
            return null;
        }
    }

    /// <summary>Dashboard-facing suffix naming where the
    /// failed Blender job's evidence was kept ("" when it
    /// could not be retained).</summary>
    private static string EvidenceSuffix(string? kept)
        => kept is null ? "" : $" Evidence kept in failed-blender\\{kept} on the shop PC.";

    /// <summary>Read a Blender output stream that should
    /// already be finished (the process exited or was
    /// killed). Waits at most 10 seconds, then gives up
    /// with "" rather than stalling the poll loop.</summary>
    private static async Task<string> ReadOutputSoonAsync(Task<string> task)
    {
        var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (done != task) return "";
        try { return await task; } catch { return ""; }
    }

    /// <summary>Log the tail of Blender's combined
    /// stdout/stderr (last 30 non-blank lines, stderr
    /// first, each line truncated) so Blender's own error
    /// text survives in bridge.log when it exits non-zero
    /// or is killed. A Blender run that printed nothing
    /// says so in one line.</summary>
    private static void LogBlenderOutputTail(string jobId, string stdout, string stderr)
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
            Program.Log($"[blender] Job {jobId}: Blender printed nothing to stdout/stderr.");
            return;
        }
        Program.Log($"[blender] Job {jobId}: Blender output (last {tail.Count} lines):");
        foreach (var line in tail)
            Program.Log($"[blender] Job {jobId} Blender: {line}");
    }

    private static void CleanupDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
