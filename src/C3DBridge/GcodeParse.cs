// C3D Print Bridge — streaming G-code declaration parse (bridge-reads build).
//
// Purpose: read a downloaded, SHA-verified G-code file and report what the
// FILE declares — the tool-change sequence in order, the slicer header,
// print temperatures, and the slicer profile name — so the bridge and the
// dashboard can gate on the file's own declarations instead of guessing.
//
// HARD RULES (from the bridge-reads spec):
//  - Streaming only: the file is read line by line with a StreamReader.
//    A 41 MB file is never loaded whole into memory (no File.ReadAllText,
//    no File.ReadAllBytes here). Scanning to end-of-file is required:
//    tool changes can occur anywhere in the file, not only in the header.
//  - Parse failure = hold, never a failed print, never a guess. A file
//    that cannot be opened or read reports ParseOk=false and the caller
//    holds the job with reason "file unreadable". A readable file with
//    no bare T command reports an EMPTY tool list (declared tool
//    unknown), never a defaulted T0.
//  - What counts as a tool change: a line whose FIRST token is exactly
//    "T" followed by digits (e.g. "T0"), on a non-comment line. Lines
//    such as "M104 T0 S130" (a temperature for a tool) and "M104 S0
//    T0/T1/T2/T3" start-up housekeeping are NOT tool changes, and
//    comment lines (first non-space character ';') never count.
//  - Sliced nozzle: derived ONLY from the slicer's own profile label
//    (filament_settings_id, e.g. "Generic PLA @U1 0.2 nozzle") when it
//    names a "<number> nozzle" size. Extrusion widths in the header
//    (e.g. 0.22 mm) are slicer profile values and are NEVER read as
//    the nozzle diameter. Absent label = null (unknown), never guessed.
//  - This parser never alters the file and never starts a print.

using System.Text.RegularExpressions;

namespace C3DBridge;

/// <summary>What a G-code file declares. All fields are the file's own
/// words; empty/null means the file did not declare it.</summary>
public sealed record GcodeDeclaration
{
    /// <summary>False only when the file could not be opened/read at
    /// all (or was empty). True even when no tool was declared.</summary>
    public bool ParseOk { get; init; }
    /// <summary>Short failure reason when ParseOk is false.</summary>
    public string ParseError { get; init; } = "";
    /// <summary>Bare tool changes in file order, e.g. ["T0", "T0"].
    /// Empty = the file declared no tool (unknown, never defaulted).</summary>
    public List<string> ToolsInOrder { get; init; } = new();
    /// <summary>First declared tool (e.g. "T0"), or "" when none.</summary>
    public string FirstTool { get; init; } = "";
    /// <summary>Slicer line from the header, e.g. "Snapmaker Orca
    /// 2.4.0", or "" when the header does not name one.</summary>
    public string Slicer { get; init; } = "";
    /// <summary>Total layer count from the header, or null.</summary>
    public int? Layers { get; init; }
    /// <summary>Object/model name from the header, or "" when the
    /// header does not label one in "object:" form.</summary>
    public string HeaderObject { get; init; } = "";
    /// <summary>First header line mentioning an extrusion width,
    /// verbatim, or "". Informational only: widths are NEVER
    /// read as a nozzle diameter.</summary>
    public string WidthsNote { get; init; } = "";
    /// <summary>First bed temperature seen (M140/M190 S value), or null.</summary>
    public double? BedTemp { get; init; }
    /// <summary>Distinct nozzle temperatures seen (M104/M109 S values),
    /// in file order, capped at 16. Informational only.</summary>
    public List<double> NozzleTemps { get; init; } = new();
    /// <summary>Slicer profile name (filament_settings_id), or "".</summary>
    public string Profile { get; init; } = "";
    /// <summary>Nozzle size in mm from the profile label only
    /// ("<number> nozzle"), or null when the label names none.</summary>
    public double? SlicedNozzleMm { get; init; }
    /// <summary>Total lines scanned (for diagnostics only).</summary>
    public long LinesScanned { get; init; }
}

public static class GcodeFileParser
{
    private const int MaxToolsStored = 128;
    private const int MaxNozzleTempsStored = 16;

    private static readonly Regex ToolLine =
        new(@"^T(\d+)\s*(;.*)?$", RegexOptions.Compiled);
    private static readonly Regex TempNozzle =
        new(@"^M10[49](?:\s+T\d+)?\s+S(-?\d+(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex TempBed =
        new(@"^M(?:140|190)\s+S(-?\d+(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex ProfileLine =
        new(@"filament_settings_id\s*=\s*""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex ProfileNozzle =
        new(@"(\d+(?:\.\d+)?)\s*mm?\s+nozzle", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LayersLine =
        new(@"total\s+layers?\s*[:=]\s*(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex GeneratedBy =
        new(@"generated\s+by\s+(.+?)\s+on\s+\d", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ObjectLine =
        new(@"^\s*;\s*object\s*[:=]\s*(.+?)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Stream-parse a G-code file. Never throws for a
    /// missing/unreadable file: that is ParseOk=false.</summary>
    public static async Task<GcodeDeclaration> ParseAsync(string path, CancellationToken ct)
    {
        var tools = new List<string>();
        var nozzleTemps = new List<double>();
        string slicer = "", headerObject = "", widthsNote = "", profile = "";
        int? layers = null;
        double? bedTemp = null;
        long lines = 0;
        try
        {
            using var reader = new StreamReader(path);
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) != null)
            {
                lines++;
                var t = line.Trim();
                if (t.Length == 0)
                    continue;
                // Comment lines: header/settings only, never commands.
                if (t[0] == ';')
                {
                    if (slicer.Length == 0)
                    {
                        var g = GeneratedBy.Match(t);
                        if (g.Success)
                            slicer = g.Groups[1].Value.Trim();
                    }
                    if (layers is null)
                    {
                        var lm = LayersLine.Match(t);
                        if (lm.Success && int.TryParse(lm.Groups[1].Value, out var lv))
                            layers = lv;
                    }
                    if (profile.Length == 0)
                    {
                        var pm = ProfileLine.Match(t);
                        if (pm.Success)
                            profile = pm.Groups[1].Value.Trim();
                    }
                    if (headerObject.Length == 0)
                    {
                        var om = ObjectLine.Match(t);
                        if (om.Success)
                            headerObject = om.Groups[1].Value.Trim();
                    }
                    if (widthsNote.Length == 0 && t.IndexOf("extrusion width", StringComparison.OrdinalIgnoreCase) >= 0)
                        widthsNote = t.Trim();
                    continue;
                }
                // Trailing slicer-settings lines are not ';'-led in
                // every slicer; catch the profile wherever it appears.
                if (profile.Length == 0)
                {
                    var pm = ProfileLine.Match(t);
                    if (pm.Success)
                        profile = pm.Groups[1].Value.Trim();
                }
                var firstToken = t.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (firstToken.Length == 0)
                    continue;
                var head = firstToken[0];
                var tm = ToolLine.Match(head);
                if (tm.Success)
                {
                    // Bare T command only (head == "T<n>" exactly,
                    // because ToolLine anchors the whole token).
                    if (tools.Count < MaxToolsStored)
                        tools.Add("T" + tm.Groups[1].Value);
                    continue;
                }
                var nm = TempNozzle.Match(t);
                if (nm.Success && double.TryParse(nm.Groups[1].Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var nt)
                    && nozzleTemps.Count < MaxNozzleTempsStored
                    && !nozzleTemps.Contains(nt))
                {
                    nozzleTemps.Add(nt);
                    continue;
                }
                var bm = TempBed.Match(t);
                if (bm.Success && bedTemp is null && double.TryParse(bm.Groups[1].Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var bt))
                {
                    bedTemp = bt;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new GcodeDeclaration { ParseOk = false, ParseError = TrimReason(ex.Message), LinesScanned = lines };
        }
        if (lines == 0)
            return new GcodeDeclaration { ParseOk = false, ParseError = "File is empty.", LinesScanned = 0 };
        double? slicedNozzle = null;
        if (profile.Length > 0)
        {
            var nm = ProfileNozzle.Match(profile);
            if (nm.Success && double.TryParse(nm.Groups[1].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var nz))
                slicedNozzle = nz;
        }
        return new GcodeDeclaration
        {
            ParseOk = true,
            ToolsInOrder = tools,
            FirstTool = tools.Count > 0 ? tools[0] : "",
            Slicer = slicer,
            Layers = layers,
            HeaderObject = headerObject,
            WidthsNote = widthsNote,
            BedTemp = bedTemp,
            NozzleTemps = nozzleTemps,
            Profile = profile,
            SlicedNozzleMm = slicedNozzle,
            LinesScanned = lines,
        };
    }

    /// <summary>Normalize a dashboard ams_slot value to a tool
    /// index, or null when it cannot be mapped. "T1" maps to 1.
    /// A bare integer N maps to N-1 (ASSUMED 1-based slot
    /// numbering: the Oct 30 collar job carried Plate/AMS "1"
    /// for a file that hard-selects T0). Anything else
    /// ("AMS A2", blank, free text) maps to null = unknown,
    /// never guessed. A wrong 1-based assumption can only
    /// HOLD a job (safe direction), never mis-start one —
    /// but confirm the numbering on the first live run.</summary>
    public static int? SlotToToolIndex(string? amsSlot)
    {
        if (string.IsNullOrWhiteSpace(amsSlot))
            return null;
        var s = amsSlot.Trim();
        var tm = Regex.Match(s, @"^T(\d+)$", RegexOptions.IgnoreCase);
        if (tm.Success && int.TryParse(tm.Groups[1].Value, out var ti))
            return ti;
        if (int.TryParse(s, out var n) && n >= 1 && n <= 16)
            return n - 1;
        return null;
    }

    /// <summary>Tool index of a "T<n>" token, or null.</summary>
    public static int? ToolToIndex(string? tool)
    {
        if (string.IsNullOrWhiteSpace(tool))
            return null;
        var m = Regex.Match(tool.Trim(), @"^T(\d+)$", RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out var i) ? i : null;
    }

    private static string TrimReason(string s) => s.Length <= 200 ? s : s[..200] + "…";
}
