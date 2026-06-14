using System.Text.RegularExpressions;
using RefractorForge.Formats.Rfa;

namespace RefractorForge.Render;

/// <summary>
/// The level's baked per-object lightmaps: <c>ObjectLightMaps/&lt;template&gt;_&lt;x&gt;-&lt;y&gt;-&lt;z&gt;.tga</c> — one
/// colour-mapped TGA per placed static object, holding that instance's baked lighting (terrain + neighbour shadows).
/// We key each by its world position (the bake rounds the instance's placed coords), which uniquely identifies the
/// instance; the template name is kept as a tiebreaker. The sibling <c>Palette.pal</c> is unused — each .tga carries
/// its own colour map (a grayscale intensity ramp). Decoded once to <see cref="Texture2D"/>; the editor samples it via
/// the mesh's 2nd UV channel (<see cref="StandardMesh"/> lightmap UVs).
/// </summary>
public sealed class ObjectLightmaps
{
    public sealed record Entry(string Template, int X, int Y, int Z, Texture2D Texture);

    private readonly List<Entry> _entries = new();
    // (normalised template, position) -> texture. Keying on BOTH is essential: a lightmap belongs to one specific
    // template at one position, and dense maps pack different templates (e.g. citymesh1_m1 vs ruin_citymesh1_m1) a
    // metre apart — a position-only key would hand a building's lightmap to the ruin beside it (scrambled UVs).
    private readonly Dictionary<(string T, int X, int Y, int Z), Texture2D> _byKey = new();
    // The LOD we kept per key. Each instance often has BOTH a _M1 (high-detail) and _M2 (low-detail) lightmap at the
    // same position, baked with DIFFERENT UV unwraps. The editor renders LOD0 (the _M1 mesh) with its _M1 UVs, so it
    // MUST use the _M1 lightmap — the _M2 one scrambles (this is the "second building wrong" bug). Keep the lowest LOD.
    private readonly Dictionary<(string T, int X, int Y, int Z), int> _lodByKey = new();

    public IReadOnlyList<Entry> Entries => _entries;
    public int Count => _entries.Count;

    // <template>_<x>-<y>-<z> ; the template can contain underscores/digits, so anchor the three trailing signed ints.
    private static readonly Regex NameRx = new(@"^(.*)_(-?\d+)-(-?\d+)-(-?\d+)$", RegexOptions.Compiled);

    // LOD number from a trailing _M1/_M2/_M3 (any case); no suffix = highest detail (1).
    private static int LodOf(string t)
    {
        if (t.Length >= 3 && t[^2] is 'm' or 'M' && t[^3] == '_' && char.IsDigit(t[^1])) return t[^1] - '0';
        return 1;
    }

    private void Add(string fileBase, byte[] data)
    {
        var mm = NameRx.Match(fileBase);
        if (!mm.Success) return;
        if (!int.TryParse(mm.Groups[2].Value, out int x) || !int.TryParse(mm.Groups[3].Value, out int y) || !int.TryParse(mm.Groups[4].Value, out int z)) return;
        var rawTpl = mm.Groups[1].Value;
        int lod = LodOf(rawTpl);
        var key = (NormTemplate(rawTpl), x, y, z);
        if (_lodByKey.TryGetValue(key, out var have) && have <= lod) return;   // already have an equal/better LOD -> skip
        // The bake ships object lightmaps as either .tga (stock BF1942/BFV) OR .dds (many CUSTOM maps — Dystopia_City's
        // ObjectLightMaps/*.dds are DXT1). Detect by the "DDS " magic and decode with the right codec.
        Texture2D? tex;
        try
        {
            bool isDds = data.Length > 4 && data[0] == 'D' && data[1] == 'D' && data[2] == 'S' && data[3] == ' ';
            tex = isDds ? DdsTexture.Decode(data) : TgaTexture.Decode(data);
        }
        catch { return; }
        if (tex is null) return;
        _entries.Add(new Entry(rawTpl, x, y, z, tex));
        _byKey[key] = tex;
        _lodByKey[key] = lod;
    }

    /// <summary>Load every ObjectLightMaps/*.tga straight out of the level .rfa archives.</summary>
    public static ObjectLightmaps FromArchives(IEnumerable<RfaArchive> archives)
    {
        var olm = new ObjectLightmaps();
        foreach (var a in archives)
            foreach (var e in a.Entries)
            {
                var n = e.Name.Replace('\\', '/');
                if (n.IndexOf("/objectlightmaps/", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (n.IndexOf("/night/", StringComparison.OrdinalIgnoreCase) >= 0) continue;   // skip the night-lighting set; use the day bake
                if (!n.EndsWith(".tga", StringComparison.OrdinalIgnoreCase) && !n.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) continue;
                string fb = n[(n.LastIndexOf('/') + 1)..]; fb = fb[..^4];   // basename without ".tga"/".dds" (both 4 chars)
                try { olm.Add(fb, a.Read(e)); } catch { }
            }
        return olm;
    }

    /// <summary>Inject an already-decoded lightmap (e.g. one freshly baked in-editor) under a template + position, so it
    /// matches + displays exactly like a loaded one. Used by "Bake Object Lightmaps".</summary>
    public void AddBaked(string template, int x, int y, int z, Texture2D tex)
    {
        _entries.Add(new Entry(template, x, y, z, tex));
        _byKey[(NormTemplate(template), x, y, z)] = tex;
        _lodByKey[(NormTemplate(template), x, y, z)] = 1;
    }

    /// <summary>Open the given level .rfa paths and load their ObjectLightMaps/*.tga (skips missing/unreadable).</summary>
    public static ObjectLightmaps FromRfaPaths(IEnumerable<string> paths)
    {
        var arcs = new List<RfaArchive>();
        foreach (var p in paths)
        {
            if (!File.Exists(p) || Path.GetFileName(p).StartsWith("~")) continue;
            try { arcs.Add(RfaArchive.Open(p)); } catch { }
        }
        return FromArchives(arcs);
    }

    /// <summary>Load every ObjectLightMaps/*.tga from a level FOLDER (recursive).</summary>
    public static ObjectLightmaps FromFolder(string levelDir)
    {
        var olm = new ObjectLightmaps();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(levelDir, "ObjectLightMaps", SearchOption.AllDirectories))
            {
                if (dir.Replace('\\', '/').IndexOf("/night/", StringComparison.OrdinalIgnoreCase) >= 0
                    || dir.EndsWith("Night", StringComparison.OrdinalIgnoreCase)) continue;   // skip night set
                foreach (var f in Directory.EnumerateFiles(dir, "*.*"))
                    if (f.EndsWith(".tga", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                        try { olm.Add(Path.GetFileNameWithoutExtension(f), File.ReadAllBytes(f)); } catch { }
            }
        }
        catch { }
        return olm;
    }

    private static string NormTemplate(string t)   // strip a trailing LOD suffix so afr_house1_ste vs ..._M1 still match
    {
        if (t.EndsWith("_M1", StringComparison.OrdinalIgnoreCase) || t.EndsWith("_M2", StringComparison.OrdinalIgnoreCase)) t = t[..^3];
        // LOWERCASE: the _byKey tuple uses the default (ORDINAL, case-sensitive) string comparer, but a placed template
        // (e.g. "Supplyde_m1") and its baked tga filename ("supplyde_m1_...") routinely differ in case. Normalising case
        // here keys both sides identically so the match isn't lost to capitalisation.
        return t.ToLowerInvariant();
    }

    /// <summary>Find a placed object's lightmap — STRICT on BOTH template and position. The bake names each file
    /// <c>&lt;template&gt;_&lt;x&gt;-&lt;y&gt;-&lt;z&gt;</c> with the coords TRUNCATED to int (79.8811 -> 79), so we try the
    /// same-template truncated key first, then round, then a same-template ±1 cell (round-vs-truncate slack). There is
    /// deliberately NO cross-template / nearest fallback: a near-but-different template returning a neighbour's lightmap
    /// is exactly what scrambled most objects. Returns null when the instance has no baked lightmap (-> dynamic shading).</summary>
    public Texture2D? Match(string template, float wx, float wy, float wz)
    {
        if (_byKey.Count == 0) return null;
        string nt = NormTemplate(template);
        if (_byKey.TryGetValue((nt, (int)wx, (int)wy, (int)wz), out var trunc)) return trunc;   // the bake's (int) cast
        int rx = (int)MathF.Round(wx), ry = (int)MathF.Round(wy), rz = (int)MathF.Round(wz);
        if (_byKey.TryGetValue((nt, rx, ry, rz), out var rnd)) return rnd;
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                    if (_byKey.TryGetValue((nt, rx + dx, ry + dy, rz + dz), out var t)) return t;   // SAME template only
        return null;
    }
}
