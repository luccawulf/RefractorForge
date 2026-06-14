using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Rfa;
using RefractorForge.Formats.Terrain;

namespace RefractorForge.Formats;

/// <summary>
/// Writes an edited level back to a folder: StaticObjects.con (lossless), the sculpted Heightmap.raw,
/// the painted MaterialMap.raw, and the gameplay .con files (with capture radii patched into the
/// control-point templates). Each target path is resolved the way the loader finds it — by searching
/// for the existing file — so writes land on the real files; absent files fall back to the level root.
/// (Packed .rfa output is handled separately by the archive writer.)
/// </summary>
public static class LevelSaver
{
    private static string Resolve(string levelDir, string name)
        => Directory.EnumerateFiles(levelDir, name, SearchOption.AllDirectories).FirstOrDefault()
           ?? Path.Combine(levelDir, name);

    public static List<string> SaveFolder(string levelDir,
        StaticObjectsFile? staticObjects, string? staticObjectsPath,
        Heightmap? heightmap, MaterialMap? material, EditableGameplay? gameplay,
        GrowthMaps? growth = null, LightmapShadowBits? shadow = null, TerrainConfig? terrainConfig = null)
    {
        var written = new List<string>();

        // Patch the editable terrain scalars (water level etc.) into the existing Terrain.con, preserving the rest.
        if (terrainConfig is not null)
        {
            var tc = Directory.EnumerateFiles(levelDir, "Terrain.con", SearchOption.AllDirectories).FirstOrDefault();
            if (tc is not null) { File.WriteAllLines(tc, terrainConfig.PatchConLines(File.ReadAllLines(tc))); written.Add(tc); }
        }

        if (staticObjects is not null)
        {
            var p = staticObjectsPath ?? Resolve(levelDir, "StaticObjects.con");
            staticObjects.Save(p); written.Add(p);
        }
        if (heightmap is not null)
        {
            var p = Resolve(levelDir, "Heightmap.raw");
            heightmap.SaveRaw(p); written.Add(p);
        }
        if (material is not null)
        {
            var p = Resolve(levelDir, "MaterialMap.raw");
            material.SaveRaw(p); written.Add(p);
        }
        if (growth is not null)
        {
            // Foliage index maps (the .wst palettes are unchanged by painting, so they aren't rewritten).
            if (growth.Under is not null) { var p = Resolve(levelDir, "UnderGrowthMap.raw"); growth.Under.SaveRaw(p); written.Add(p); }
            if (growth.Over is not null) { var p = Resolve(levelDir, "OverGrowthMap.raw"); growth.Over.SaveRaw(p); written.Add(p); }
        }
        if (gameplay is not null)
        {
            string cdir = Directory.Exists(Path.Combine(levelDir, "Conquest"))
                ? Path.Combine(levelDir, "Conquest") : levelDir;
            var immo = gameplay.ToImmutable();
            GameplayWriter.WriteInstanceFiles(cdir, immo);
            written.Add(Path.Combine(cdir, "ControlPoints.con"));
            written.Add(Path.Combine(cdir, "ObjectSpawns.con"));
            written.Add(Path.Combine(cdir, "SoldierSpawns.con"));

            // Radius lives on the control-point templates; patch the file in the gameplay dir
            // (the one the gameplay actually loads from), falling back to a search.
            var tpl = Path.Combine(cdir, "ControlPointTemplates.con");
            if (!File.Exists(tpl)) tpl = Resolve(levelDir, "ControlPointTemplates.con");
            if (File.Exists(tpl)) { GameplayWriter.PatchControlPointRadiiFile(tpl, immo.ControlPoints); written.Add(tpl); }

            // Soldier spawn group / spawnId / paratrooper live on the SoldierSpawn templates — patch them too.
            var stpl = Path.Combine(cdir, "SoldierSpawnTemplates.con");
            if (!File.Exists(stpl)) stpl = Resolve(levelDir, "SoldierSpawnTemplates.con");
            if (File.Exists(stpl)) { GameplayWriter.PatchSoldierSpawnTemplatesFile(stpl, immo.SoldierSpawns); written.Add(stpl); }

            // Give newly placed vehicle spawners a template so they spawn in-game.
            var ost = Path.Combine(cdir, "ObjectSpawnTemplates.con");
            if (!File.Exists(ost)) ost = Resolve(levelDir, "ObjectSpawnTemplates.con");
            if (File.Exists(ost))
            {
                File.WriteAllText(ost, GameplayWriter.AppendMissingSpawnerTemplates(File.ReadAllLines(ost), immo.VehicleSpawns));
                written.Add(ost);
            }
        }
        if (shadow is not null)
        {
            // Engine reads Textures/LightmapShadowBits.lsb; land on the existing file if present.
            var p = Resolve(levelDir, "LightmapShadowBits.lsb");
            shadow.Save(p); written.Add(p);
        }
        return written;
    }

    /// <summary>
    /// Write a brand-new level folder from scratch (the thing <see cref="SaveFolder"/> can't do — it only
    /// updates files that already exist). Produces the minimal set the loader needs to open a level:
    /// <c>Heightmap.raw</c>, <c>MaterialMap.raw</c>, <c>Init/Terrain.con</c>, <c>Init/SkyAndSun.con</c>,
    /// <c>Init.con</c> and an empty <c>StaticObjects.con</c>. The <c>.con</c> files mirror retail
    /// Operation_Irving so the result loads in both this editor and the game's terrain. Returns the paths
    /// written. Verify by loading <paramref name="levelDir"/> straight back.
    /// </summary>
    public static List<string> CreateNewLevel(string levelDir, string name, TerrainConfig cfg,
        Heightmap heightmap, EnvironmentSettings env, MaterialMap? material = null, bool playable = false)
    {
        var written = new List<string>();
        Directory.CreateDirectory(levelDir);
        Directory.CreateDirectory(Path.Combine(levelDir, "Init"));

        // Heightmap.raw — 16-bit LE, grid side == materialSize.
        var hp = Path.Combine(levelDir, "Heightmap.raw"); heightmap.SaveRaw(hp); written.Add(hp);

        // MaterialMap.raw — one index/cell; default all-zero (engine falls back to material 0) when absent.
        var mat = material ?? new MaterialMap(cfg.MaterialSize, cfg.MaterialSize);
        var mp = Path.Combine(levelDir, "MaterialMap.raw"); mat.SaveRaw(mp); written.Add(mp);

        // The game VFS base the .con refs hang off (e.g. BfVietnam\levels\MyMap).
        string enginePath = $@"BfVietnam\levels\{name}";

        // Init/Terrain.con — GeometryTemplate block (from the config) + shadow settings (from the env).
        var terrain = cfg.ToTerrainConLines(enginePath).ToList();
        terrain.Add("");
        terrain.AddRange(env.ToTerrainShadowLines());
        var tp = Path.Combine(levelDir, "Init", "Terrain.con"); File.WriteAllLines(tp, terrain); written.Add(tp);

        // Init/SkyAndSun.con.
        var skyPath = Path.Combine(levelDir, "Init", "SkyAndSun.con"); File.WriteAllLines(skyPath, env.ToSkyAndSunConLines()); written.Add(skyPath);

        // StaticObjects.con — empty (a blank map has no placed objects yet).
        var op = Path.Combine(levelDir, "StaticObjects.con"); new StaticObjectsFile().Save(op); written.Add(op);

        // Init.con = rendering/fog/water; for a playable map also a full Conquest layer + kit/flag block.
        var initLines = env.ToInitConLines().ToList();
        if (playable)
        {
            float spacing = cfg.HorizontalSpacing <= 0 ? 1f : cfg.HorizontalSpacing;
            float safeY = cfg.WaterLevel + 0.5f;
            float HeightAt(float wx, float wz)
            {
                int gx = System.Math.Clamp((int)(wx / spacing), 0, heightmap.Width - 1);
                int gz = System.Math.Clamp((int)(wz / spacing), 0, heightmap.Height - 1);
                return System.Math.Max(cfg.HeightToMeters(heightmap[gx, gz]), safeY);
            }

            var bases = NewMapGameplay.DefaultBases();
            foreach (var kv in NewMapGameplay.BuildFiles(name, cfg.WorldSize, HeightAt, bases))
            {
                var gp = Path.Combine(levelDir, kv.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(gp)!);
                File.WriteAllText(gp, kv.Value);
                written.Add(gp);
            }
            initLines.AddRange(NewMapGameplay.InitConBlock(cfg.WorldSize, HeightAt, bases));

            // AIpathFinding.con — AI search-map config (slope/water/brush params per vehicle).
            var aip = Path.Combine(levelDir, "AIpathFinding.con");
            File.WriteAllLines(aip, NewMapGameplay.AiPathFindingCon(cfg.WorldSize, bases)); written.Add(aip);

            // Pathfinding/*.raw — the 8Bit AI navmaps themselves, terrain-derived (a fresh map has no static
            // objects yet to carve out). 7 vehicles x 3 levels.
            string navDir = Path.Combine(levelDir, "Pathfinding");
            Directory.CreateDirectory(navDir);
            foreach (var (file, data) in SearchMapGenerator.GenerateAll(cfg, heightmap))
            {
                var np = Path.Combine(navDir, file);
                File.WriteAllBytes(np, data); written.Add(np);
            }
        }

        var ip = Path.Combine(levelDir, "Init.con"); File.WriteAllLines(ip, initLines); written.Add(ip);
        return written;
    }

    private static byte[] Latin1(string s) => Encoding.Latin1.GetBytes(s);

    /// <summary>The edited StaticObjects.con as bytes (CRLF), for packing into a .rfa.</summary>
    public static byte[] SerializeStaticObjects(StaticObjectsFile so)
        => Latin1(string.Join("\r\n", so.Write()) + "\r\n");

    /// <summary>Pack a level <em>folder</em> into a fresh .rfa. Entry names are the folder-relative
    /// paths (forward slashes) under an optional <paramref name="prefix"/> (e.g. "bf1942/levels/Foo/").</summary>
    public static int PackFolder(string folderDir, string outRfaPath, string? prefix = null)
    {
        prefix = string.IsNullOrEmpty(prefix) ? "" : prefix.Replace('\\', '/').TrimEnd('/') + "/";
        var entries = new List<(string, byte[])>();
        foreach (var f in Directory.EnumerateFiles(folderDir, "*", SearchOption.AllDirectories).OrderBy(x => x))
        {
            var rel = Path.GetRelativePath(folderDir, f).Replace('\\', '/');
            entries.Add((prefix + rel, File.ReadAllBytes(f)));
        }
        RfaWriter.WriteFile(outRfaPath, entries);
        return entries.Count;
    }

    private static string? FindEntry(RfaArchive a, string suffix, bool preferConquest)
    {
        string? first = null;
        foreach (var e in a.Entries)
        {
            if (!e.Name.EndsWith(suffix, System.StringComparison.OrdinalIgnoreCase)) continue;
            if (preferConquest && e.Name.ToLowerInvariant().Contains("conquest")) return e.Name;
            first ??= e.Name;
        }
        return first;
    }

    /// <summary>Compute the name->bytes substitutions for the edited files against a base archive: each edited
    /// asset is matched to its existing entry (by trailing file name) so the replacement reuses the archive's
    /// EXACT entry name. Shared by <see cref="RepackToRfa"/> (full repack) and <see cref="WritePatchRfa"/>
    /// (standalone patch).</summary>
    /// <summary>The archive's level-folder entry prefix (e.g. "bfvietnam/levels/Foo/"), derived from where
    /// StaticObjects.con (or Init.con / Heightmap.raw) lives, so brand-new files can be added under the same path.</summary>
    private static string ArchivePrefix(RfaArchive arch)
    {
        foreach (var anchor in new[] { "StaticObjects.con", "Init.con", "Heightmap.raw" })
        {
            var n = FindEntry(arch, anchor, false);
            if (n is not null)
            {
                var fwd = n.Replace('\\', '/');
                int slash = fwd.LastIndexOf('/');
                return slash >= 0 ? fwd[..(slash + 1)] : "";
            }
        }
        return "";
    }

    private static (Dictionary<string, byte[]> Repl, List<string> Names) BuildReplacements(
        RfaArchive arch, StaticObjectsFile? so, Heightmap? heightmap, MaterialMap? material,
        EditableGameplay? gameplay, GrowthMaps? growth, LightmapShadowBits? shadow, TerrainConfig? terrainConfig,
        IEnumerable<(string Name, byte[] Bytes)>? extraFiles,
        IEnumerable<(string RelPath, byte[] Bytes)>? newEntries = null)
    {
        var repl = new Dictionary<string, byte[]>(System.StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        void Put(string? name, byte[]? data) { if (name is not null && data is not null) { repl[name] = data; names.Add(name); } }

        if (terrainConfig is not null)
        {
            var tcName = FindEntry(arch, "Terrain.con", false);
            if (tcName is not null)
            {
                var lines = Encoding.Latin1.GetString(arch.Read(arch.Entries.First(e => e.Name == tcName))).Split('\n');
                Put(tcName, Latin1(string.Join("\n", terrainConfig.PatchConLines(lines))));
            }
        }
        if (so is not null) Put(FindEntry(arch, "StaticObjects.con", false), SerializeStaticObjects(so));
        if (heightmap is not null) Put(FindEntry(arch, "Heightmap.raw", false), heightmap.ToBytes());
        if (material is not null) Put(FindEntry(arch, "MaterialMap.raw", false), material.Samples);
        if (growth?.Under is not null) Put(FindEntry(arch, "UnderGrowthMap.raw", false), growth.Under.Samples);
        if (growth?.Over is not null) Put(FindEntry(arch, "OverGrowthMap.raw", false), growth.Over.Samples);
        if (shadow is not null) Put(FindEntry(arch, "LightmapShadowBits.lsb", false), shadow.Encode());

        if (gameplay is not null)
        {
            var immo = gameplay.ToImmutable();
            Put(FindEntry(arch, "ControlPoints.con", true), Latin1(GameplayWriter.BuildControlPoints(immo.ControlPoints)));
            Put(FindEntry(arch, "ObjectSpawns.con", true), Latin1(GameplayWriter.BuildObjectSpawns(immo.VehicleSpawns)));
            Put(FindEntry(arch, "SoldierSpawns.con", true), Latin1(GameplayWriter.BuildSoldierSpawns(immo.SoldierSpawns)));

            var tplName = FindEntry(arch, "ControlPointTemplates.con", true);
            if (tplName is not null)
            {
                var tplEntry = arch.Entries.First(e => e.Name == tplName);
                var lines = Encoding.Latin1.GetString(arch.Read(tplEntry)).Split('\n');
                Put(tplName, Latin1(GameplayWriter.PatchControlPointRadii(lines, immo.ControlPoints)));
            }

            var stplName = FindEntry(arch, "SoldierSpawnTemplates.con", true);
            if (stplName is not null)
            {
                var stplEntry = arch.Entries.First(e => e.Name == stplName);
                var slines = Encoding.Latin1.GetString(arch.Read(stplEntry)).Split('\n');
                Put(stplName, Latin1(GameplayWriter.PatchSoldierSpawnTemplates(slines, immo.SoldierSpawns)));
            }

            var ostName = FindEntry(arch, "ObjectSpawnTemplates.con", true);
            if (ostName is not null)
            {
                var ostEntry = arch.Entries.First(e => e.Name == ostName);
                var lines = Encoding.Latin1.GetString(arch.Read(ostEntry)).Split('\n');
                Put(ostName, Latin1(GameplayWriter.AppendMissingSpawnerTemplates(lines, immo.VehicleSpawns)));
            }
        }

        // Arbitrary edited assets matched by trailing file name (e.g. sound .ssc scripts).
        if (extraFiles is not null)
            foreach (var (name, bytes) in extraFiles)
                Put(FindEntry(arch, name, false), bytes);

        // New files — UPSERT: if an entry with the same leaf already exists (e.g. re-baked ObjectLightMaps/*.tga),
        // override it in place (preserving the archive's exact path/case); otherwise add it verbatim under the level's
        // archive prefix (e.g. a brand-new Effects/RF_Weather.con, or object lightmaps on a level that shipped none).
        if (newEntries is not null)
        {
            string prefix = ArchivePrefix(arch);
            foreach (var (rel, bytes) in newEntries)
            {
                var relNorm = rel.Replace('\\', '/').TrimStart('/');
                var leaf = relNorm[(relNorm.LastIndexOf('/') + 1)..];
                Put(FindEntry(arch, "/" + leaf, false) ?? (prefix + relNorm), bytes);
            }
        }

        return (repl, names);
    }

    /// <summary>Re-pack an edited level back into a .rfa by substituting only the changed files into a
    /// copy of the original archive (everything else preserved verbatim). Returns the entry names replaced.</summary>
    public static List<string> RepackToRfa(string originalRfaPath, string outRfaPath,
        StaticObjectsFile? so, Heightmap? heightmap, MaterialMap? material, EditableGameplay? gameplay,
        GrowthMaps? growth = null, LightmapShadowBits? shadow = null, TerrainConfig? terrainConfig = null,
        IEnumerable<(string Name, byte[] Bytes)>? extraFiles = null,
        IEnumerable<(string RelPath, byte[] Bytes)>? newEntries = null)
    {
        var arch = RfaArchive.Open(originalRfaPath);
        var (repl, names) = BuildReplacements(arch, so, heightmap, material, gameplay, growth, shadow, terrainConfig, extraFiles, newEntries);
        RfaWriter.RepackToFile(outRfaPath, arch, repl);   // stream: a huge base archive won't fit in one byte[]
        return names;
    }

    /// <summary>
    /// Write a PATCH .rfa containing ONLY the edited files, named with the base archive's exact entry paths so
    /// the engine mounts it OVER the base (later archives win — the same mechanism as retail
    /// <c>&lt;Level&gt;_001.rfa</c> patches). The base archive is left untouched, the patch is small, and a level
    /// loaded as <c>[base, patch]</c> reads the edits. Returns the entry names written (empty = nothing edited).
    /// </summary>
    public static List<string> WritePatchRfa(string baseRfaPath, string outPatchPath,
        StaticObjectsFile? so, Heightmap? heightmap, MaterialMap? material, EditableGameplay? gameplay,
        GrowthMaps? growth = null, LightmapShadowBits? shadow = null, TerrainConfig? terrainConfig = null,
        IEnumerable<(string Name, byte[] Bytes)>? extraFiles = null,
        IEnumerable<(string RelPath, byte[] Bytes)>? newEntries = null)
    {
        var arch = RfaArchive.Open(baseRfaPath);
        var (repl, names) = BuildReplacements(arch, so, heightmap, material, gameplay, growth, shadow, terrainConfig, extraFiles, newEntries);
        // Deterministic order; entries are addressed by name so order doesn't affect mounting.
        var entries = repl.OrderBy(kv => kv.Key, System.StringComparer.OrdinalIgnoreCase)
                          .Select(kv => (kv.Key, kv.Value)).ToList();
        RfaWriter.WriteFile(outPatchPath, entries);
        return names;
    }
}
