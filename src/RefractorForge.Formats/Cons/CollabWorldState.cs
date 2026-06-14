using System.Text;
using RefractorForge.Formats.Terrain;

namespace RefractorForge.Formats.Con;

/// <summary>
/// The canonical NON-object layers of a collaborative session — the heightmap, the material/foliage maps, and the
/// gameplay layer (control points, vehicle + soldier spawns) — held by the central relay so they can be (a) kept
/// up to date as edits stream in, (b) replayed to late joiners, and (c) persisted so they survive a server
/// restart. Object edits live in the relay's <c>StaticObjectsFile</c>; this is everything else.
///
/// It speaks the exact collaboration wire forms the editor sends:
///   <c>TERRAIN x0 y0 w h &lt;b64 u16 LE rect&gt;</c>,
///   <c>MATERIAL layer x0 y0 w h &lt;b64 byte rect&gt;</c> (layer 0=material 1=undergrowth 2=overgrowth),
///   <c>GAMEPLAY &lt;b64 GameplaySync text&gt;</c> (full-state).
/// </summary>
public sealed class CollabWorldState
{
    public Heightmap? Height { get; set; }
    public MaterialMap? Material { get; set; }   // layer 0
    public MaterialMap? Under { get; set; }       // layer 1 (undergrowth)
    public MaterialMap? Over { get; set; }        // layer 2 (overgrowth)
    public string? Gameplay { get; set; }         // GameplaySync.Serialize(...) text (decoded, not base64)
    public float? Water { get; set; }             // env water level (Terrain.con waterLevel): synced live + seeded to joiners
    public string? Overgrowth { get; set; }       // overgrowth-tree overlay settings wire ("OVERGROWTH show spacing density")
    /// <summary>Imported .obj meshes shared over the wire: template name -> the verbatim "OBJMESH name b64" op that
    /// recreates the render mesh on a peer. Stored so late joiners get imports too.</summary>
    public Dictionary<string, string> ObjMeshes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Any => Height is not null || Material is not null || Under is not null || Over is not null || !string.IsNullOrEmpty(Gameplay) || Water is not null || !string.IsNullOrEmpty(Overgrowth) || ObjMeshes.Count > 0;

    /// <summary>Apply one streamed op (TERRAIN/MATERIAL/GAMEPLAY) to the canonical state. Returns true if it was a
    /// recognised non-object op (so the caller knows not to treat it as an object edit). Terrain/material rects are
    /// dropped when there is no map of matching kind (e.g. an un-seeded relay); gameplay is always stored.</summary>
    public bool ApplyOp(string payload)
    {
        int sp = payload.IndexOf(' ');
        string verb = sp < 0 ? payload : payload[..sp];
        switch (verb)
        {
            case "TERRAIN":
            {
                if (Height is null) return true;   // recognised, but nothing to write into
                var p = payload.Split(' ');
                int x0 = int.Parse(p[1]), y0 = int.Parse(p[2]), w = int.Parse(p[3]), h = int.Parse(p[4]);
                var buf = Convert.FromBase64String(p[5]);
                for (int yy = 0; yy < h; yy++)
                    for (int xx = 0; xx < w; xx++)
                    {
                        int gx = x0 + xx, gy = y0 + yy;
                        if (gx < 0 || gy < 0 || gx >= Height.Width || gy >= Height.Height) continue;
                        int o = (yy * w + xx) * 2; if (o + 1 >= buf.Length) continue;
                        Height[gx, gy] = (ushort)(buf[o] | (buf[o + 1] << 8));
                    }
                return true;
            }
            case "MATERIAL":
            {
                var p = payload.Split(' ');
                int layer = int.Parse(p[1]), x0 = int.Parse(p[2]), y0 = int.Parse(p[3]), w = int.Parse(p[4]), h = int.Parse(p[5]);
                var map = layer == 1 ? Under : layer == 2 ? Over : Material;
                if (map is null) return true;
                var buf = Convert.FromBase64String(p[6]);
                for (int yy = 0; yy < h; yy++)
                    for (int xx = 0; xx < w; xx++)
                    {
                        int gx = x0 + xx, gy = y0 + yy;
                        if (gx < 0 || gy < 0 || gx >= map.Width || gy >= map.Height) continue;
                        int o = yy * w + xx; if (o >= buf.Length) continue;
                        map[gx, gy] = buf[o];
                    }
                return true;
            }
            case "GAMEPLAY":
            {
                int b = payload.IndexOf(' ');
                Gameplay = b < 0 ? "" : Encoding.UTF8.GetString(Convert.FromBase64String(payload[(b + 1)..]));
                return true;
            }
            case "WATER":
            {
                var p = payload.Split(' ');
                if (p.Length >= 2 && float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var wl)) Water = wl;
                return true;
            }
            case "OVERGROWTH":
            {
                Overgrowth = payload;   // store the whole op verbatim; the editor parses show/spacing/density on apply
                return true;
            }
            case "OBJMESH":
            {
                // OBJMESH <name> <b64 geometry blob> — keyed by name so re-imports of the same template replace.
                var p = payload.Split(' ', 3);
                if (p.Length >= 3) ObjMeshes[p[1]] = payload;
                return true;
            }
            default: return false;   // an object op (ADD/MOVE/ROT/SCALE/DEL) — not ours
        }
    }

    /// <summary>The full-state ops that recreate this world on a late joiner (whole maps as single rects + the
    /// gameplay snapshot). The joiner applies them exactly like live edits.</summary>
    public IEnumerable<string> SnapshotOps()
    {
        if (Height is not null)
            yield return $"TERRAIN 0 0 {Height.Width} {Height.Height} {Convert.ToBase64String(Height.ToBytes())}";
        if (Material is not null)
            yield return $"MATERIAL 0 0 0 {Material.Width} {Material.Height} {Convert.ToBase64String(Material.Samples)}";
        if (Under is not null)
            yield return $"MATERIAL 1 0 0 {Under.Width} {Under.Height} {Convert.ToBase64String(Under.Samples)}";
        if (Over is not null)
            yield return $"MATERIAL 2 0 0 {Over.Width} {Over.Height} {Convert.ToBase64String(Over.Samples)}";
        if (!string.IsNullOrEmpty(Gameplay))
            yield return "GAMEPLAY " + Convert.ToBase64String(Encoding.UTF8.GetBytes(Gameplay));
        if (Water is float wv)
            yield return "WATER " + wv.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var op in ObjMeshes.Values)   // imported meshes BEFORE overgrowth (overgrowth may reference them)
            yield return op;
        if (!string.IsNullOrEmpty(Overgrowth))
            yield return Overgrowth;
    }

    /// <summary>Persist the maps + gameplay into a state directory (the relay's own resume format).</summary>
    public void Save(string dir)
    {
        Directory.CreateDirectory(dir);
        Height?.SaveRaw(Path.Combine(dir, "Heightmap.raw"));
        Material?.SaveRaw(Path.Combine(dir, "MaterialMap.raw"));
        Under?.SaveRaw(Path.Combine(dir, "UnderGrowthMap.raw"));
        Over?.SaveRaw(Path.Combine(dir, "OverGrowthMap.raw"));
        if (!string.IsNullOrEmpty(Gameplay)) File.WriteAllText(Path.Combine(dir, "gameplay.sync"), Gameplay);
        if (Water is float wv) File.WriteAllText(Path.Combine(dir, "water.txt"), wv.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(Overgrowth)) File.WriteAllText(Path.Combine(dir, "overgrowth.txt"), Overgrowth);
        if (ObjMeshes.Count > 0) File.WriteAllLines(Path.Combine(dir, "objmeshes.txt"), ObjMeshes.Values);   // one "OBJMESH ..." op per line
    }

    /// <summary>Reload a state directory written by <see cref="Save"/>; null if it holds none of these layers.
    /// Map dimensions are inferred from each file's size (the maps are square).</summary>
    public static CollabWorldState? Load(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        var w = new CollabWorldState();
        var hp = Path.Combine(dir, "Heightmap.raw");
        if (File.Exists(hp)) { var b = File.ReadAllBytes(hp); int s = SquareSide(b.Length, 2); if (s > 0) w.Height = Heightmap.FromBytes(b, s, s); }
        w.Material = LoadMap(Path.Combine(dir, "MaterialMap.raw"));
        w.Under = LoadMap(Path.Combine(dir, "UnderGrowthMap.raw"));
        w.Over = LoadMap(Path.Combine(dir, "OverGrowthMap.raw"));
        var gp = Path.Combine(dir, "gameplay.sync");
        if (File.Exists(gp)) w.Gameplay = File.ReadAllText(gp);
        var wf = Path.Combine(dir, "water.txt");
        if (File.Exists(wf) && float.TryParse(File.ReadAllText(wf), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var wl)) w.Water = wl;
        var of = Path.Combine(dir, "overgrowth.txt");
        if (File.Exists(of)) { var t = File.ReadAllText(of).Trim(); if (t.StartsWith("OVERGROWTH", StringComparison.Ordinal)) w.Overgrowth = t; }
        var omf = Path.Combine(dir, "objmeshes.txt");
        if (File.Exists(omf))
            foreach (var line in File.ReadAllLines(omf))
            {
                var p = line.Split(' ', 3);
                if (p.Length >= 3 && p[0] == "OBJMESH") w.ObjMeshes[p[1]] = line;
            }
        return w.Any ? w : null;
    }

    private static MaterialMap? LoadMap(string path)
    {
        if (!File.Exists(path)) return null;
        var b = File.ReadAllBytes(path);
        int s = SquareSide(b.Length, 1);
        return s > 0 ? MaterialMap.FromBytes(b, s, s) : null;
    }

    /// <summary>Side of a square grid given its on-disk byte count and bytes-per-cell; 0 if not square.</summary>
    private static int SquareSide(int byteCount, int bytesPerCell)
    {
        if (byteCount <= 0 || byteCount % bytesPerCell != 0) return 0;
        int cells = byteCount / bytesPerCell;
        int s = (int)Math.Round(Math.Sqrt(cells));
        return (long)s * s == cells ? s : 0;
    }
}
