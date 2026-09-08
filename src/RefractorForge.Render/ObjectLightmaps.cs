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

    /// <summary>
    /// Does this lightmap file carry REAL baked lighting, or is it a flat placeholder?
    ///
    /// <para>The distinction decides whether a bake may replace it. A mesh with no lightmap unwrap can only be given
    /// a FLAT map, and a bake must not overwrite a map that DICE (or an earlier good bake) actually produced — but
    /// it MUST be free to replace a blank. Without this test the two are indistinguishable, and a level got stuck
    /// with 293 blank white maps that every subsequent bake politely preserved.</para>
    ///
    /// <para>A shipped lightmap is an 8-bit colour-mapped TGA: one palette index per texel, so "flat" is every index
    /// being the same byte — no decode and no palette needed. Anything of another shape (a <c>.dds</c>, say) counts
    /// as detailed, because refusing to overwrite is the safe direction.</para>
    /// </summary>
    public static bool HasDetail(byte[] file)
    {
        if (file is null || file.Length < 18) return true;
        int idLen = file[0], cmType = file[1], imgType = file[2], bpp = file[16];
        if (cmType != 1 || imgType != 1 || bpp != 8) return true;
        int cmLen = file[5] | (file[6] << 8), cmBits = file[7];
        long data = 18L + idLen + (long)cmLen * ((cmBits + 7) / 8);
        if (data >= file.Length) return true;
        byte first = file[data];
        for (long i = data + 1; i < file.Length; i++) if (file[i] != first) return true;
        return false;
    }

    /// <summary>
    /// Raise a baked object lightmap so nothing on the object is darker than <paramref name="floor"/>.
    ///
    /// This is how you brighten an object the level does NOT own the shader for. A stock object's <c>.rs</c> lives
    /// in the shared archives - Saigon68's sewers are <c>standardMesh/O_sewers_A_M1.rs</c> - and editing that would
    /// change the object on every map on the install. Its LIGHTMAP, though, is level-local
    /// (<c>ObjectLightMaps/&lt;mesh&gt;_&lt;x&gt;-&lt;y&gt;-&lt;z&gt;.tga</c>), and the lightmap is exactly what makes
    /// it dark: the engine shades it <c>saturate(2*(prelight*N.L + LMambient)) * texture</c>, and Saigon68's sewers
    /// ship a lightmap that is 1024x1024 of solid ZERO, so only <c>renderer.LMambientColor</c> survives.
    ///
    /// The header and palette are left byte-identical; only the index bytes move, each to the palette entry whose
    /// grey is nearest the floor. Returns null when the file is not an 8-bit colour-mapped TGA - the one shape
    /// every shipped lightmap uses - because guessing at another format is how you corrupt a map.
    /// </summary>
    public static byte[]? LiftFloor(byte[] file, byte floor)
    {
        if (file is null || file.Length < 18) return null;
        int idLen = file[0], cmType = file[1], imgType = file[2], bpp = file[16];
        if (cmType != 1 || imgType != 1 || bpp != 8) return null;
        int cmLen = file[5] | (file[6] << 8), cmBits = file[7];
        int cmBytes = (cmBits + 7) / 8;
        long palOff = 18L + idLen;
        long data = palOff + (long)cmLen * cmBytes;
        if (data >= file.Length || cmLen <= 0 || cmBytes < 3) return null;

        // Grey value of every palette entry, and the entry closest to the floor from ABOVE (so lifting can never
        // darken a texel). The shipped palette is an identity ramp, but read it rather than assume it.
        var grey = new int[cmLen];
        for (int i = 0; i < cmLen; i++)
        {
            long o = palOff + (long)i * cmBytes;
            grey[i] = (file[o] + file[o + 1] + file[o + 2]) / 3;      // BGR, and every entry is grey anyway
        }
        int best = -1, bestGrey = int.MaxValue;
        for (int i = 0; i < cmLen; i++)
            if (grey[i] >= floor && grey[i] < bestGrey) { bestGrey = grey[i]; best = i; }
        if (best < 0) { best = 0; for (int i = 1; i < cmLen; i++) if (grey[i] > grey[best]) best = i; }   // nothing that bright

        var outp = (byte[])file.Clone();
        bool moved = false;
        for (long i = data; i < file.Length; i++)
        {
            int idx = file[i];
            if (idx < cmLen && grey[idx] < floor) { outp[i] = (byte)best; moved = true; }
        }
        return moved ? outp : file;
    }

    /// <summary>
    /// The file BASE a lightmap must be stored under for a given LOD mesh name — the leaf, never a path.
    ///
    /// <para><c>GeometryTemplate.file</c> is free to carry a relative path (<c>../standardMesh/city_dumpster1</c> is
    /// real, and appears in Saigon68). Used verbatim as a file name that writes the map to
    /// <c>ObjectLightMaps/../standardMesh/city_dumpster1_449-10-173.tga</c>, which normalises straight OUT of the
    /// folder the engine reads. The object then has no lightmap, and because its mesh still carries a lightmap
    /// channel the engine binds the lightmap shader anyway and asserts on the null texture handle
    /// (<c>RaShaderPVLS1DifLmp.cpp:84, m_LightMapD3DH</c>). Every retail level stores leaf names only.</para>
    /// </summary>
    public static string FileBase(string meshName)
    {
        if (string.IsNullOrWhiteSpace(meshName)) return "";
        var n = meshName.Replace('\\', '/').Trim();
        int slash = n.LastIndexOf('/');
        if (slash >= 0) n = n[(slash + 1)..];
        // A trailing extension would double up ("x.sm_10-2-3.tga"); the engine's own names carry none.
        int dot = n.LastIndexOf('.');
        if (dot > 0 && n.Length - dot <= 5) n = n[..dot];
        return n.Trim();
    }

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
    public static ObjectLightmaps FromArchives(IEnumerable<RefractorFlatArchive> archives)
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
        var arcs = new List<RefractorFlatArchive>();
        foreach (var p in paths)
        {
            if (!File.Exists(p) || Path.GetFileName(p).StartsWith("~")) continue;
            try { arcs.Add(new RefractorFlatArchive(p)); } catch { }
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
