namespace RefractorForge.Formats.Validation;

/// <summary>
/// Which soldier spawn templates the game will leave UNDER the terrain.
///
/// BfVietnam lifts every spawn point that sits below the ground up to the surface: <c>BFSpawnPoint::
/// fixPositionVsTerrain</c> runs whenever a spawn's position is read or set, and unless the template's
/// below-ground flag is on it moves the point to terrain height + 0.5 m (bfv_linded.static, 0x08788cc0). The flag
/// is <c>ObjectTemplate.allowSpawningBelowGround 1</c> - retail Saigon68 and Cedar Falls set it on every tunnel spawn.
///
/// al_vietnas (2026-09-11) wrote <c>allowSpawingBelowGround</c>, one letter short, on all 14 of its tunnel spawns in
/// both game modes. The console rejects an unknown command without a word, the flag stayed off, and every soldier
/// choosing the Sewers or Turbines flag appeared on the grass 18-21 m above it. The spelling is the whole bug, so a
/// near-miss is reported by name.
/// </summary>
public static class SpawnBelowGround
{
    public const string Command = "allowSpawningBelowGround";

    /// <summary>Templates that set the flag, templates that carry a MISSPELLING of it (and so do not), and every
    /// template the file defines - a spawn this mode does not define is not this mode's business.</summary>
    public sealed record Flags(IReadOnlySet<string> Allowed, IReadOnlyDictionary<string, string> Misspelled, IReadOnlySet<string> Defined);

    public static Flags Parse(IEnumerable<string> templateLines)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var defined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? cur = null;
        foreach (var raw in templateLines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("rem", StringComparison.OrdinalIgnoreCase)) continue;
            var t = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (!t[0].StartsWith("ObjectTemplate.", StringComparison.OrdinalIgnoreCase)) continue;
            string cmd = t[0]["ObjectTemplate.".Length..];
            if (cmd.Equals("create", StringComparison.OrdinalIgnoreCase) && t.Length >= 3) { cur = t[2]; defined.Add(cur); continue; }
            if (cmd.Equals("active", StringComparison.OrdinalIgnoreCase) && t.Length >= 2) { cur = t[1]; continue; }
            if (cur is null) continue;
            bool on = t.Length < 2 || t[1] != "0";
            if (cmd.Equals(Command, StringComparison.OrdinalIgnoreCase)) { if (on) allowed.Add(cur); else allowed.Remove(cur); }
            else if (IsMisspelling(cmd)) missp[cur] = cmd;
        }
        return new Flags(allowed, missp, defined);
    }

    /// <summary>allowSpawingBelowGround, alowSpawningBelowGround, allowSpawningBellowGround ... - anything that is
    /// plainly this command but is not it.</summary>
    public static bool IsMisspelling(string cmd)
    {
        if (cmd.Equals(Command, StringComparison.OrdinalIgnoreCase)) return false;
        var c = cmd.ToLowerInvariant();
        return (c.Contains("spaw") && c.Contains("ground") && (c.StartsWith("allow") || c.StartsWith("alow")))
               || Distance(c, Command.ToLowerInvariant()) <= 2;
    }

    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }
}
