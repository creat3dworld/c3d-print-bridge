// C3D Print Bridge — start-time safety interlock.
//
// This is deliberately NOT a decision engine. The dashboard decides
// whether a job may start (its safety_verdict) and assigns the
// printer. The bridge only re-checks the live facts immediately
// before forwarding the start command, and REFUSES when the facts
// have changed since the dashboard's verdict:
//
//   (a) the dashboard verdict must be "ready";
//   (b) the printer must currently be idle (not printing);
//   (c) when the printer reports AMS material/color, they must not
//       CONTRADICT the job's requirements (same string, or same
//       hex). A name versus a hex value is incomparable, not a
//       contradiction — the dashboard's verdict stands there.
//
// Bed-empty is NOT re-verified here: the bridge cannot see the bed
// (no vision analysis — that is deliberately out of scope for this
// thin relay). The dashboard's verdict already covers the bed via
// camera snapshot + owner confirmation, and the bridge never claims
// a bed is empty on its own. A refusal reports its reason; it never
// silently picks another printer or reschedules anything.

namespace C3DBridge;

public static class Safety
{
    /// <summary>Returns null when the start may be forwarded, or a
    /// human-readable refusal reason when it must not.</summary>
    public static string? InterlockRefusal(DashboardJob job, PrinterStatus live)
    {
        if (!string.Equals(job.SafetyVerdict, "ready", StringComparison.OrdinalIgnoreCase))
            return $"Dashboard safety verdict is '{job.SafetyVerdict}', not 'ready'; start refused.";

        if (!string.Equals(live.State, "idle", StringComparison.OrdinalIgnoreCase))
            return $"Printer '{live.Name}' is currently '{live.State}', not idle; start refused.";

        if (live.Ams.Known)
        {
            if (!string.IsNullOrWhiteSpace(job.Material) &&
                !string.IsNullOrWhiteSpace(live.Ams.Material) &&
                !string.Equals(job.Material.Trim(), live.Ams.Material.Trim(), StringComparison.OrdinalIgnoreCase))
                return $"AMS material changed since the dashboard verdict: printer reports '{live.Ams.Material}', job needs '{job.Material}'; start refused.";

            // Color: refuse ONLY on a proven contradiction — both
            // sides comparable and different. A name ("Black") versus
            // a hex value ("000000") is NOT comparable, so it is NOT
            // a contradiction: the dashboard's verdict stands and
            // the bridge does not second-guess what it cannot
            // compare. (The dashboard itself follows the same rule:
            // a mismatch is only reported when both sides are known
            // in a comparable form.)
            if (!string.IsNullOrWhiteSpace(job.Color) &&
                !string.IsNullOrWhiteSpace(live.Ams.Color) &&
                ColorsContradict(job.Color, live.Ams.Color))
                return $"AMS color changed since the dashboard verdict: printer reports '{live.Ams.Color}', job needs '{job.Color}'; start refused.";
        }

        return null;
    }

    /// <summary>True only when two color values are comparable AND
    /// different. Comparable means both are hex (after stripping an
    /// alpha prefix and '#') or both are names. A name versus a hex
    /// is incomparable — not a contradiction.</summary>
    public static bool ColorsContradict(string wanted, string actual)
    {
        var w = wanted.Trim();
        var a = actual.Trim();
        if (string.Equals(w, a, StringComparison.OrdinalIgnoreCase)) return false;

        var wHex = TryHex(w);
        var aHex = TryHex(a);
        if (wHex is not null && aHex is not null)
            return !string.Equals(wHex, aHex, StringComparison.OrdinalIgnoreCase);
        if (wHex is null && aHex is null)
            return true; // both names, and they differ
        return false; // one name, one hex: incomparable, not a contradiction
    }

    /// <summary>Color comparison that tolerates the two shapes the
    /// dashboard and printers use: a name ("Black") or a hex value
    /// ("000000" / "FF000000" with optional alpha). Names compare
    /// case-insensitively; hex values compare on their last 6 digits
    /// (RGB), ignoring an alpha prefix. A name never matches a hex —
    /// that stays unverifiable on the dashboard side, not a pass here.</summary>
    public static bool ColorsMatch(string wanted, string actual)
    {
        var w = wanted.Trim();
        var a = actual.Trim();
        if (string.Equals(w, a, StringComparison.OrdinalIgnoreCase)) return true;

        var wHex = TryHex(w);
        var aHex = TryHex(a);
        if (wHex is not null && aHex is not null)
            return string.Equals(wHex, aHex, StringComparison.OrdinalIgnoreCase);
        return false;
    }

    private static string? TryHex(string value)
    {
        var v = value.Trim().TrimStart('#');
        if (v.Length == 6 && v.All(Uri.IsHexDigit)) return v.ToUpperInvariant();
        if (v.Length == 8 && v.All(Uri.IsHexDigit)) return v[2..].ToUpperInvariant();
        return null;
    }
}
