using System.Text;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Rfa;
using RefractorForge.Formats.Sound;
using RefractorForge.Formats.Terrain;

namespace RefractorForge.Render;

/// <summary>
/// Loads a Battlefield Vietnam level straight out of a packed <c>.rfa</c> — no extraction. Reads
/// <c>Terrain.con</c>, <c>Heightmap.raw</c>, <c>StaticObjects.con</c> and the terrain tiles directly
/// into memory using the same in-archive read path the mesh/texture archives already use. Entry names
/// are matched by their trailing file name, so internal folder prefixes (and back-slash separators)
/// don't matter.
/// </summary>
public static class LevelArchive
{
    public sealed record Loaded(TerrainConfig Config, Heightmap Heightmap, StaticObjectsFile StaticObjects, TerrainTexture? Terrain, GameplayObjects Gameplay, MaterialMap? Material, GrowthMaps? Growth, EnvironmentSettings? Environment, LightmapShadowBits? Shadow = null, SoundLibrary? Sounds = null);

    /// <summary>True when the path points at an existing <c>.rfa</c> file (vs. an extracted folder).</summary>
    public static bool IsRfa(string path) =>
        File.Exists(path) && path.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase);

    /// <summary>Load a level from ONE or MORE packed <c>.rfa</c> — base + patch archives are merged, with
    /// LATER archives overriding earlier ones for same-named files (this is how Refractor patch .rfa work).</summary>
    public static Loaded FromRfa(params string[] rfaPaths)
    {
        var arcs = new List<RefractorFlatArchive>();
        foreach (var p in rfaPaths.Where(File.Exists))
        {
            if (Path.GetFileName(p).StartsWith("~")) continue;   // ~$… temp/lock leftovers
            try { arcs.Add(new RefractorFlatArchive(p)); }
            catch (Exception ex) { System.Console.WriteLine($"LevelArchive: skipping unreadable '{Path.GetFileName(p)}' ({ex.GetType().Name})"); }
        }
        if (arcs.Count == 0) throw new FileNotFoundException("No readable .rfa archive supplied.");
        string label = arcs.Count == 1 ? Path.GetFileName(rfaPaths[0]) : $"{arcs.Count} archives";

        // Find an entry by trailing file name; LATER archives (patches) win over earlier ones.
        (RefractorFlatArchive Arc, RefractorFlatArchiveEntry Entry)? Find(string fileName)
        {
            // Prefer the GLOBALLY SHALLOWEST match across ALL archives: heavily-scripted maps carry per-game-mode
            // copies in sub-folders (Dystopia_City has BattleMode/StaticObjects.con AND ChallengeMode/StaticObjects.con
            // AND the root StaticObjects.con with the 1257 global statics; Animations/Init.con + Menu/Init.con shadow
            // the root Init.con). Depth MUST be compared across archives, not by returning the shallowest entry of
            // whichever (last) archive happens to contain the name — otherwise a _NNN patch that ships only a deeper
            // Menu/init.con shadows the base's root Init.con and silently drops its fog/render settings. Among EQUAL
            // depths a later archive (the patch) wins.
            (RefractorFlatArchive Arc, RefractorFlatArchiveEntry Entry)? best = null; int bestDepth = int.MaxValue;
            for (int i = 0; i < arcs.Count; i++)
                foreach (var e in arcs[i].Entries)
                {
                    var n = e.Name.Replace('\\', '/');
                    if (!n.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase) && !e.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    int depth = n.Count(c => c == '/');
                    if (depth <= bestDepth) { best = (arcs[i], e); bestDepth = depth; }   // <= so a later (patch) archive wins depth ties
                }
            return best;
        }
        byte[] Need(string fileName)
        {
            var f = Find(fileName) ?? throw new FileNotFoundException($"'{fileName}' not found inside {label}.");
            return f.Arc.Read(f.Entry);
        }
        byte[]? Opt(string fileName) { var f = Find(fileName); return f is null ? null : f.Value.Arc.Read(f.Value.Entry); }

        var cfg = TerrainConfig.Parse(Lines(Need("Terrain.con")));

        // Prefer the heightmap the config names, else the conventional Heightmap.raw.
        var heightEntry = (cfg.HeightmapRef is { Length: > 0 } href ? Find(Path.GetFileName(href.Replace('\\', '/'))) : null)
                          ?? Find("Heightmap.raw") ?? throw new FileNotFoundException($"no heightmap found inside {label}.");
        var hm = Heightmap.LoadForMaterialSize(heightEntry.Arc.Read(heightEntry.Entry), cfg.MaterialSize);

        var so = StaticObjectsFile.Parse(Lines(Need("StaticObjects.con")));

        // Terrain tiles: gather txNNxNN from ALL archives (later archives override same-named tiles).
        var tileMap = new Dictionary<string, (RefractorFlatArchive Arc, RefractorFlatArchiveEntry Entry)>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in arcs)
            foreach (var e in a.Entries)
                if (System.Text.RegularExpressions.Regex.IsMatch(
                        Path.GetFileNameWithoutExtension(e.Name.Replace('\\', '/')), @"^tx\d+x\d+$",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    tileMap[Path.GetFileName(e.Name.Replace('\\', '/'))] = (a, e);
        var tiles = tileMap.Select(kv => (kv.Key, kv.Value.Arc.Read(kv.Value.Entry)));
        var tex = TerrainTexture.FromTileBytes(tiles, cfg.WorldSize, Opt("detail.dds"));

        // Gameplay layer: prefer the multiplayer Conquest/ copies of each file (later archives win).
        IEnumerable<string>? Conquest(string fileName)
        {
            for (int i = arcs.Count - 1; i >= 0; i--)
            {
                var e = arcs[i].Entries.FirstOrDefault(x =>
                    x.Name.Replace('\\', '/').ToLowerInvariant() is var n && n.Contains("/conquest/") && n.EndsWith("/" + fileName.ToLowerInvariant()));
                if (e is not null) return Lines(arcs[i].Read(e));
            }
            var f = Find(fileName);
            return f is null ? null : Lines(f.Value.Arc.Read(f.Value.Entry));
        }
        var gameplay = GameplayObjects.Parse(
            Conquest("ControlPoints.con"), Conquest("ControlPointTemplates.con"),
            Conquest("ObjectSpawns.con"), Conquest("ObjectSpawnTemplates.con"),
            Conquest("SoldierSpawns.con"), Conquest("SoldierSpawnTemplates.con"));

        // Material map (one index/cell), named by the config or the conventional MaterialMap.raw.
        var matEntry = (cfg.MaterialMapRef is { Length: > 0 } mref ? Find(Path.GetFileName(mref.Replace('\\', '/')) + ".raw") : null)
                       ?? Find("MaterialMap.raw");
        MaterialMap? material = matEntry is null ? null : MaterialMap.FromBytes(matEntry.Value.Arc.Read(matEntry.Value.Entry), cfg.MaterialSize, cfg.MaterialSize);

        // Foliage layers (undergrowth/overgrowth): index maps + their .wst palettes (own resolutions).
        GrowthMaps? growth = null;
        var ugMap = Find("UnderGrowthMap.raw");
        var ogMap = Find("OverGrowthMap.raw");
        if (ugMap is not null || ogMap is not null)
        {
            growth = new GrowthMaps();
            var ugW = Opt("underGrowth.wst"); if (ugW is not null) { try { growth.UnderPalette = FoliagePalette.Parse(Encoding.Latin1.GetString(ugW)); } catch { } }
            var ogW = Opt("overGrowth.wst"); if (ogW is not null) { try { growth.OverPalette = FoliagePalette.Parse(Encoding.Latin1.GetString(ogW)); } catch { } }
            if (ugMap is not null) (growth.Under, growth.UnderSide) = GrowthMaps.LoadMap(ugMap.Value.Arc.Read(ugMap.Value.Entry), growth.UnderPalette?.MaterialMapSideSize ?? 0);
            if (ogMap is not null) (growth.Over, growth.OverSide) = GrowthMaps.LoadMap(ogMap.Value.Arc.Read(ogMap.Value.Entry), growth.OverPalette?.MaterialMapSideSize ?? 0);
        }

        // Lighting/sky environment from SkyAndSun.con + Terrain.con + Init.con.
        var env = EnvironmentSettings.Parse(
            Opt("SkyAndSun.con") is byte[] sb ? Lines(sb) : null,
            Opt("Terrain.con") is byte[] tb ? Lines(tb) : null,
            Opt("Init.con") is byte[] ib ? Lines(ib) : null);

        // Packed terrain sun-shadow (per-patch run-length lightmap), if present.
        var lsbBytes = Opt("LightmapShadowBits.lsb");
        LightmapShadowBits? shadow = lsbBytes is null ? null : LightmapShadowBits.Decode(lsbBytes);

        // Sound layer (Sounds/*.con templates -> *.ssc scripts), so .rfa levels can edit + save sound emitters too.
        var soundCons = new List<string>();
        var soundSsc = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in arcs)
            foreach (var e in a.Entries)
            {
                var n = e.Name.Replace('\\', '/');
                if (!n.ToLowerInvariant().Contains("/sounds/")) continue;
                if (n.EndsWith(".con", StringComparison.OrdinalIgnoreCase)) soundCons.Add(Encoding.Latin1.GetString(a.Read(e)));
                else if (n.EndsWith(".ssc", StringComparison.OrdinalIgnoreCase)) soundSsc[Path.GetFileName(n)] = a.Read(e);
            }
        var sounds = SoundLibrary.FromTexts(soundCons, soundSsc);

        return new Loaded(cfg, hm, so, tex, gameplay, material, growth, env, shadow, sounds);
    }

    private static IEnumerable<string> Lines(byte[] textBytes) => Encoding.Latin1.GetString(textBytes).Split('\n');
}
