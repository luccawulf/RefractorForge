using System.Globalization;

namespace RefractorForge.Formats.Validation;

/// <summary>
/// <c>Game.setActiveCombatArea offsetX offsetZ sizeX sizeZ</c> from the level's Init.con.
///
/// The argument order is easy to get wrong, and getting it wrong silently maps the whole level onto the wrong
/// square. The MDT documentation is explicit: the first two numbers are OFFSETS, the last two are SCALES
/// ("the X and Z scales, which are always the same since all maps are square"), and the box overrides the
/// terrain's worldSize for map coordinates. So Berlin's <c>1536 1536 512 512</c> is a 512 m square whose
/// corner sits at (1536, 1536) - not a rectangle from 1536 down to 512.
/// </summary>
public readonly record struct CombatArea(float X, float Z, float Width, float Height)
{
    public const string Key = "game.setactivecombatarea";

    public float X1 => X + Width;
    public float Z1 => Z + Height;

    public bool Contains(float wx, float wz) => wx >= X && wx <= X1 && wz >= Z && wz <= Z1;

    /// <summary>Distance outside the box, 0 when inside. Lets a report say "8 m past the west edge".</summary>
    public float DistanceOutside(float wx, float wz)
    {
        float dx = wx < X ? X - wx : wx > X1 ? wx - X1 : 0f;
        float dz = wz < Z ? Z - wz : wz > Z1 ? wz - Z1 : 0f;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>The whole world, for a level that never declares one - what the engine falls back to.</summary>
    public static CombatArea Whole(float worldSize) => new(0f, 0f, worldSize, worldSize);

    public static bool TryParse(string line, out CombatArea area)
    {
        area = default;
        var t = line.Trim();
        int sp = t.IndexOf(' ');
        if (sp < 0 || !t[..sp].Equals("game.setactivecombatarea", StringComparison.OrdinalIgnoreCase)) return false;
        var p = t[(sp + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 4) return false;
        float[] v = new float[4];
        for (int i = 0; i < 4; i++)
            if (!float.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i])) return false;
        area = new CombatArea(v[0], v[1], v[2], v[3]);
        return true;
    }

    public string ToConLine() =>
        string.Format(CultureInfo.InvariantCulture, "game.setActiveCombatArea {0:0.###} {1:0.###} {2:0.###} {3:0.###}",
            X, Z, Width, Height);

    /// <summary>Find the declaration in an Init.con, if there is one.</summary>
    public static CombatArea? FromInitCon(IEnumerable<string> lines)
    {
        foreach (var l in lines)
            if (TryParse(l, out var a)) return a;
        return null;
    }

    /// <summary>
    /// Rewrite an Init.con with this area, replacing the existing line or appending after the last
    /// <c>game.</c> line so it sits with its neighbours. Everything else passes through untouched.
    /// </summary>
    public List<string> PatchInitConLines(IEnumerable<string> existing)
    {
        var outLines = new List<string>();
        bool done = false;
        int lastGame = -1;
        foreach (var raw in existing)
        {
            var t = raw.Trim();
            int sp = t.IndexOf(' ');
            var key = (sp < 0 ? t : t[..sp]).ToLowerInvariant();
            if (key == Key && !done) { outLines.Add(ToConLine()); done = true; }
            else outLines.Add(raw);
            if (key.StartsWith("game.", StringComparison.Ordinal)) lastGame = outLines.Count - 1;
        }
        if (!done) outLines.Insert(lastGame >= 0 ? lastGame + 1 : outLines.Count, ToConLine());
        return outLines;
    }
}
