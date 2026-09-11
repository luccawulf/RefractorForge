using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Editing;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;

namespace RefractorForge.Collab;

/// <summary>
/// A map's syncable content as a set of named pieces ("keys"), each with a hash of its value. Everything that
/// lets people work on one map at different times is built on this: two copies of a map are compared key by
/// key, so "what did the server get while I was away" and "what did I change offline" are both just the keys
/// whose hash moved since the version the two copies last agreed on.
///
/// The pieces are chosen so that two people editing different things never collide:
/// <code>
///   o:&lt;id&gt;               one placed object (template, position, rotation, scale)
///   h:&lt;bx&gt;,&lt;by&gt;          one 32x32 block of the heightmap
///   m0: m1: m2:&lt;bx&gt;,&lt;by&gt;  one block of the material / undergrowth / overgrowth map
///   gp                   the gameplay layer - whole, because its records are addressed by index
///   s:&lt;VERB&gt;             one settings op, whole (WATER, LIGHT, OVERGROWTH, LIGHTRIG, ANNOT)
///   mesh:&lt;name&gt;         an imported mesh
///   lvl:&lt;template&gt;      a level-local object's files
///   f:&lt;path&gt;             any other file in the level archive, by level-relative path
/// </code>
/// Hashes are taken of the WIRE form of each value, because that is what the other copy will actually hold
/// after the value has crossed the network: a float the editor holds to nine digits arrives with six.
/// </summary>
public static class SyncKeys
{
    /// <summary>Side of a terrain / material block, in cells.</summary>
    public const int Block = 32;

    /// <summary>The settings a world state carries whole, by the verb of the op that sets each one.</summary>
    /// <remarks>OVERGROWTH is not one: it is how the editor SHOWS the trees (on or off, spacing), a view setting that
    /// travels live between people working together but is nobody's change to the map.</remarks>
    public static readonly string[] SettingVerbs = { "WATER", "LIGHT", "LIGHTRIG", "ANNOT" };

    // ---- hashing ----------------------------------------------------------------------------------------

    public static string Hash(string s) => Hash(Encoding.UTF8.GetBytes(s));

    public static string Hash(ReadOnlySpan<byte> bytes)
    {
        Span<byte> h = stackalloc byte[32];
        SHA256.HashData(bytes, h);
        return Convert.ToHexString(h[..8]).ToLowerInvariant();
    }

    /// <summary>Every key of a map and the hash of its value. <paramref name="files"/> is the level's other
    /// files by level-relative path and content hash; pass null when they are not part of the comparison.</summary>
    public static Dictionary<string, string> Hashes(StaticObjectsFile? objects, CollabWorldState? world,
                                                    IReadOnlyDictionary<string, string>? files = null)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (objects is not null)
            foreach (var o in objects.Objects)
                d["o:" + o.Id] = Hash(ObjectCanon(o));
        if (world is not null)
        {
            if (world.Height is { } hm) HashHeight(hm, d);
            if (world.Material is { } m0) HashMap(m0, "m0:", d);
            if (world.Under is { } m1) HashMap(m1, "m1:", d);
            if (world.Over is { } m2) HashMap(m2, "m2:", d);
            if (!string.IsNullOrEmpty(world.Gameplay)) d["gp"] = Hash(GameplayCanon(world.Gameplay));
            foreach (var (verb, op) in Settings(world)) d["s:" + verb] = Hash(op);
            foreach (var kv in world.ObjMeshes) d["mesh:" + kv.Key] = Hash(kv.Value);
            foreach (var kv in world.LevelFiles) d["lvl:" + kv.Key] = Hash(kv.Value);
        }
        if (files is not null)
            foreach (var kv in files) d[FileKey(kv.Key)] = kv.Value;
        return d;
    }

    /// <summary>A level file's key. Lower case: the engine finds files without regard to case, so two copies that
    /// spell one path differently still mean the same file.</summary>
    public static string FileKey(string relPath) => "f:" + NormPath(relPath).ToLowerInvariant();

    /// <summary>What an object IS, for comparison: the fields the collaboration wire carries, in the form they
    /// have after crossing it. Template case is ignored - the engine ignores it.</summary>
    public static string ObjectCanon(StaticObject o)
        => $"{o.Template.ToLowerInvariant()}|{Round(o.Position)}|{Round(o.Rotation)}|{Round(o.Scale ?? 1f)}";

    private static string Round(Vec3 v) => Vec3.Parse(v.ToString()).ToString();
    private static string Round(float f)
        => float.Parse(f.ToString("0.######", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
                .ToString("0.######", CultureInfo.InvariantCulture);

    private static string GameplayCanon(string text) => text.Replace("\r", "").Trim();

    private static void HashHeight(Heightmap hm, Dictionary<string, string> d)
    {
        var buf = new byte[Block * Block * 2];
        for (int by = 0; by * Block < hm.Height; by++)
            for (int bx = 0; bx * Block < hm.Width; bx++)
            {
                int x0 = bx * Block, y0 = by * Block;
                int w = Math.Min(Block, hm.Width - x0), h = Math.Min(Block, hm.Height - y0), n = 0;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        ushort v = hm[x0 + x, y0 + y];
                        buf[n++] = (byte)v; buf[n++] = (byte)(v >> 8);
                    }
                d[$"h:{bx},{by}"] = Hash(buf.AsSpan(0, n));
            }
    }

    private static void HashMap(MaterialMap m, string prefix, Dictionary<string, string> d)
    {
        var buf = new byte[Block * Block];
        for (int by = 0; by * Block < m.Height; by++)
            for (int bx = 0; bx * Block < m.Width; bx++)
            {
                int x0 = bx * Block, y0 = by * Block;
                int w = Math.Min(Block, m.Width - x0), h = Math.Min(Block, m.Height - y0), n = 0;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++) buf[n++] = m[x0 + x, y0 + y];
                d[$"{prefix}{bx},{by}"] = Hash(buf.AsSpan(0, n));
            }
    }

    /// <summary>The whole-value settings a world holds, as (verb, op).</summary>
    public static IEnumerable<(string Verb, string Op)> Settings(CollabWorldState w)
    {
        if (!string.IsNullOrEmpty(w.Water)) yield return ("WATER", w.Water!);
        if (!string.IsNullOrEmpty(w.Light)) yield return ("LIGHT", w.Light!);
        if (!string.IsNullOrEmpty(w.LightRig)) yield return ("LIGHTRIG", w.LightRig!);
        if (!string.IsNullOrEmpty(w.Annotations)) yield return ("ANNOT", w.Annotations!);
    }

    // ---- turning a key back into edits --------------------------------------------------------------------

    /// <summary>The ops that give a copy of the map the value <paramref name="objects"/>/<paramref name="world"/>
    /// have for <paramref name="key"/>. Every op is an absolute set, so applying them twice is harmless. A file
    /// key yields nothing - its bytes are not held here; see <see cref="FileOp"/>.</summary>
    public static IEnumerable<string> OpsFor(string key, StaticObjectsFile? objects, CollabWorldState? world)
    {
        if (key.StartsWith("o:", StringComparison.Ordinal))
        {
            string id = key[2..];
            var o = objects?.FindById(id);
            if (o is null) { yield return new DeleteObject(id).ToWire(); yield break; }
            yield return new AddObject(o.Id, o.Template, o.Position, o.Rotation).ToWire();
            // ADD does nothing to an object the other copy already has, so the fields travel on their own too.
            yield return new RetemplateObject(o.Id, o.Template).ToWire();
            yield return new MoveObject(o.Id, o.Position).ToWire();
            yield return new RotateObject(o.Id, o.Rotation).ToWire();
            yield return new ScaleObject(o.Id, o.Scale ?? 1f).ToWire();
            yield break;
        }
        if (world is null) yield break;
        if (key.StartsWith("h:", StringComparison.Ordinal) && world.Height is { } hm && TryBlock(key[2..], out int hbx, out int hby))
        {
            int x0 = hbx * Block, y0 = hby * Block;
            int w = Math.Min(Block, hm.Width - x0), h = Math.Min(Block, hm.Height - y0);
            if (w <= 0 || h <= 0) yield break;
            var buf = new byte[w * h * 2];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    ushort v = hm[x0 + x, y0 + y]; int i = (y * w + x) * 2;
                    buf[i] = (byte)v; buf[i + 1] = (byte)(v >> 8);
                }
            yield return $"TERRAIN {x0} {y0} {w} {h} {Convert.ToBase64String(buf)}";
            yield break;
        }
        if (key.Length > 3 && key[0] == 'm' && key[2] == ':' && key[1] is '0' or '1' or '2' && TryBlock(key[3..], out int mbx, out int mby))
        {
            int layer = key[1] - '0';
            var m = layer == 1 ? world.Under : layer == 2 ? world.Over : world.Material;
            if (m is null) yield break;
            int x0 = mbx * Block, y0 = mby * Block;
            int w = Math.Min(Block, m.Width - x0), h = Math.Min(Block, m.Height - y0);
            if (w <= 0 || h <= 0) yield break;
            var buf = new byte[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++) buf[y * w + x] = m[x0 + x, y0 + y];
            yield return $"MATERIAL {layer} {x0} {y0} {w} {h} {Convert.ToBase64String(buf)}";
            yield break;
        }
        if (key == "gp")
        {
            if (!string.IsNullOrEmpty(world.Gameplay))
                yield return "GAMEPLAY " + Convert.ToBase64String(Encoding.UTF8.GetBytes(world.Gameplay!));
            yield break;
        }
        if (key.StartsWith("s:", StringComparison.Ordinal))
        {
            string verb = key[2..];
            foreach (var (v, op) in Settings(world)) if (v == verb) { yield return op; yield break; }
            yield break;
        }
        if (key.StartsWith("mesh:", StringComparison.Ordinal) && world.ObjMeshes.TryGetValue(key[5..], out var mop)) { yield return mop; yield break; }
        if (key.StartsWith("lvl:", StringComparison.Ordinal) && world.LevelFiles.TryGetValue(key[4..], out var lop)) { yield return lop; yield break; }
    }

    private static bool TryBlock(string s, out int bx, out int by)
    {
        bx = by = 0;
        int c = s.IndexOf(',');
        return c > 0 && int.TryParse(s[..c], NumberStyles.Integer, CultureInfo.InvariantCulture, out bx)
                     && int.TryParse(s[(c + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out by);
    }

    // ---- files --------------------------------------------------------------------------------------------

    /// <summary>A level file as an op: <c>FILE &lt;path&gt; &lt;base64&gt;</c>. The path is level-relative with
    /// forward slashes and percent-escaped, so it is always exactly one token.</summary>
    public static string FileOp(string relPath, ReadOnlySpan<byte> bytes)
        => $"FILE {EscapePath(relPath)} {Convert.ToBase64String(bytes)}";

    /// <summary>Read a FILE op back. False for anything that is not one.</summary>
    public static bool TryParseFileOp(string op, out string relPath, out byte[] bytes)
    {
        relPath = ""; bytes = Array.Empty<byte>();
        if (!op.StartsWith("FILE ", StringComparison.Ordinal)) return false;
        var p = op.Split(' ', 3);
        if (p.Length < 3) return false;
        try { relPath = UnescapePath(p[1]); bytes = Convert.FromBase64String(p[2]); return relPath.Length > 0; }
        catch { return false; }
    }

    public static string EscapePath(string p) => Uri.EscapeDataString(NormPath(p)).Replace("%2F", "/");
    public static string UnescapePath(string p) => NormPath(Uri.UnescapeDataString(p));
    public static string NormPath(string p) => p.Replace('\\', '/').TrimStart('/');

    /// <summary>Archive entry name -> path relative to the level root ("Textures/tx00x00.dds"), given the level's
    /// prefix ("bfvietnam/levels/al_vietnas/"). Null for an entry outside the level.</summary>
    public static string? LevelRelative(string entryName, string prefix)
    {
        var n = entryName.Replace('\\', '/');
        var p = prefix.Replace('\\', '/');
        if (p.Length == 0) return n.TrimStart('/');
        return n.StartsWith(p, StringComparison.OrdinalIgnoreCase) ? n[p.Length..] : null;
    }

    /// <summary>Entries a sync handles as STRUCTURED content rather than as files: every editor rebuilds them on
    /// save from the objects, terrain, maps and gameplay, which are merged piece by piece. Sending them as whole
    /// files as well would make two people moving different objects collide on one file. The sync record and
    /// editor-only sidecars never travel at all.</summary>
    public static bool IsStructuredEntry(string relPath)
    {
        var leaf = relPath.Replace('\\', '/');
        leaf = leaf[(leaf.LastIndexOf('/') + 1)..].ToLowerInvariant();
        return leaf is "staticobjects.con" or "heightmap.raw" or "materialmap.raw"
                    or "undergrowthmap.raw" or "overgrowthmap.raw"
                    or "controlpoints.con" or "objectspawns.con" or "soldierspawns.con"
                    or "controlpointtemplates.con" or "objectspawntemplates.con" or "soldierspawntemplates.con"
                    // Kept in step with the objects and spawns by every save (additive lists of templates).
                    or "cullradius.con" or "precache.con"
                    or SyncRecord.EntryLeafLower
            || RefractorForge.Formats.LevelSaver.IsEditorOnlyFile(relPath);
    }

    // ---- what an op touches -------------------------------------------------------------------------------

    /// <summary>The keys an op changes, for the change history. Terrain and material rects map onto the blocks
    /// they overlap; everything else names itself.</summary>
    public static IEnumerable<string> KeysOf(string op)
    {
        int sp = op.IndexOf(' ');
        string verb = sp < 0 ? op : op[..sp];
        switch (verb)
        {
            case "ADD": case "MOVE": case "ROT": case "SCALE": case "DEL": case "TPL":
            {
                var p = op.Split(' ', 3);
                if (p.Length >= 2) yield return "o:" + p[1];
                yield break;
            }
            case "TERRAIN":
            {
                var p = op.Split(' ', 6);
                if (p.Length >= 5 && TryRect(p, 1, out int x0, out int y0, out int w, out int h))
                    foreach (var b in BlocksOf(x0, y0, w, h)) yield return "h:" + b;
                yield break;
            }
            case "MATERIAL":
            {
                var p = op.Split(' ', 7);
                if (p.Length >= 6 && TryRect(p, 2, out int x0, out int y0, out int w, out int h))
                    foreach (var b in BlocksOf(x0, y0, w, h)) yield return "m" + p[1] + ":" + b;
                yield break;
            }
            case "GAMEPLAY": yield return "gp"; yield break;
            case "OBJMESH": { var p = op.Split(' ', 3); if (p.Length >= 2) yield return "mesh:" + p[1]; yield break; }
            case "LVLFILE": { var p = op.Split(' ', 3); if (p.Length >= 2) yield return "lvl:" + p[1]; yield break; }
            case "FILE": { var p = op.Split(' ', 3); if (p.Length >= 2) yield return FileKey(UnescapePath(p[1])); yield break; }
        }
        if (Array.IndexOf(SettingVerbs, verb) >= 0) yield return "s:" + verb;
    }

    private static bool TryRect(string[] p, int at, out int x0, out int y0, out int w, out int h)
    {
        x0 = y0 = w = h = 0;
        return int.TryParse(p[at], out x0) && int.TryParse(p[at + 1], out y0)
            && int.TryParse(p[at + 2], out w) && int.TryParse(p[at + 3], out h) && w > 0 && h > 0;
    }

    private static IEnumerable<string> BlocksOf(int x0, int y0, int w, int h)
    {
        for (int by = Math.Max(0, y0) / Block; by <= (y0 + h - 1) / Block; by++)
            for (int bx = Math.Max(0, x0) / Block; bx <= (x0 + w - 1) / Block; bx++)
                yield return $"{bx},{by}";
    }

    // ---- saying it to a person ----------------------------------------------------------------------------

    /// <summary>What a key is, in the words a mapper would use. Keys of one kind are counted together.</summary>
    public static string Group(string key)
    {
        if (key.StartsWith("o:", StringComparison.Ordinal)) return "objects";
        if (key.StartsWith("h:", StringComparison.Ordinal)) return "terrain";
        if (key.StartsWith("m0:", StringComparison.Ordinal)) return "materials";
        if (key.StartsWith("m1:", StringComparison.Ordinal) || key.StartsWith("m2:", StringComparison.Ordinal)) return "foliage";
        if (key == "gp") return "gameplay";
        if (key == "s:WATER") return "water";
        if (key == "s:LIGHT" || key == "s:LIGHTRIG") return "lighting";
        if (key == "s:ANNOT") return "notes";
        if (key.StartsWith("mesh:", StringComparison.Ordinal) || key.StartsWith("lvl:", StringComparison.Ordinal)) return "imported objects";
        if (key.StartsWith("f:", StringComparison.Ordinal))
        {
            var p = key[2..].ToLowerInvariant();
            int slash = p.LastIndexOf('/');
            string leaf = slash >= 0 ? p[(slash + 1)..] : p;
            if (leaf.StartsWith("tx") && leaf.EndsWith(".dds")) return "ground texture";
            if (p.Contains("objectlightmaps/") || leaf.EndsWith(".lsb") || leaf.StartsWith("rf_groundshadow")) return "lighting bakes";
            return "level files";
        }
        return "other";
    }

    /// <summary>"12 objects, terrain, 3 level files" - a count per kind, biggest first.</summary>
    public static string Describe(IEnumerable<string> keys)
    {
        var counts = keys.GroupBy(Group).Select(g => (g.Key, N: g.Count())).OrderByDescending(t => t.N).ToList();
        if (counts.Count == 0) return "nothing";
        return string.Join(", ", counts.Select(t => t.Key switch
        {
            "objects" => $"{t.N} object{(t.N == 1 ? "" : "s")}",
            "terrain" or "materials" or "foliage" => $"{t.Key} ({t.N} area{(t.N == 1 ? "" : "s")})",
            "level files" => $"{t.N} level file{(t.N == 1 ? "" : "s")}",
            "ground texture" => $"ground texture ({t.N} tile{(t.N == 1 ? "" : "s")})",
            "lighting bakes" => $"lighting bakes ({t.N} file{(t.N == 1 ? "" : "s")})",
            _ => t.Key,
        }));
    }
}
