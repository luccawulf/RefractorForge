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

    /// <summary>Every game-mode folder in the level — any directory holding gameplay instance files. Found by
    /// content rather than by a fixed name list, so a mod's own mode (or a differently-cased <c>CTF</c>) is picked
    /// up as readily as the stock Conquest/Ctf/TDM/SinglePlayer/Coop set.</summary>
    public static List<string> GameModeDirs(string levelDir)
    {
        var dirs = new List<string>();
        if (!Directory.Exists(levelDir)) return dirs;
        foreach (var d in Directory.EnumerateDirectories(levelDir, "*", SearchOption.AllDirectories))
            if (File.Exists(Path.Combine(d, "ControlPoints.con")) ||
                File.Exists(Path.Combine(d, "ObjectSpawns.con")) ||
                File.Exists(Path.Combine(d, "SoldierSpawns.con")))
                dirs.Add(d);
        return dirs;
    }

    /// <summary>Patch the three template files that carry editable per-object properties, in one gameplay folder.
    /// Each mode keeps its own copies, so they are patched per folder rather than once for the level.</summary>
    private static void PatchGameplayTemplates(string levelDir, string dir, GameplayObjects immo, List<string> written)
    {
        // Radius/team/timings live on the control-point templates.
        var tpl = Path.Combine(dir, "ControlPointTemplates.con");
        if (!File.Exists(tpl)) tpl = Resolve(levelDir, "ControlPointTemplates.con");
        if (File.Exists(tpl)) { GameplayWriter.PatchControlPointRadiiFile(tpl, immo.ControlPoints); written.Add(tpl); }

        // Soldier spawn group / spawnId / paratrooper live on the SoldierSpawn templates.
        var stpl = Path.Combine(dir, "SoldierSpawnTemplates.con");
        if (!File.Exists(stpl)) stpl = Resolve(levelDir, "SoldierSpawnTemplates.con");
        if (File.Exists(stpl)) { GameplayWriter.PatchSoldierSpawnTemplatesFile(stpl, immo.SoldierSpawns); written.Add(stpl); }

        // Give newly placed vehicle spawners a template so they spawn in-game.
        var ost = Path.Combine(dir, "ObjectSpawnTemplates.con");
        if (!File.Exists(ost)) ost = Resolve(levelDir, "ObjectSpawnTemplates.con");
        if (File.Exists(ost))
        {
            // Patch the editable spawner fields (vehicles + spawn timing) into the templates that exist, THEN append
            // templates for any newly placed spawner. Patch-then-append, so an edit to an existing spawner is not
            // lost behind a freshly appended duplicate.
            var patched = GameplayWriter.PatchVehicleSpawnTemplates(File.ReadAllLines(ost), immo.VehicleSpawns);
            File.WriteAllText(ost, GameplayWriter.AppendMissingSpawnerTemplates(
                patched.Split((char)10).Select(l => l.TrimEnd((char)13)), immo.VehicleSpawns));
            written.Add(ost);
        }
    }

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
            // Read the loaded mode as it still is on disk, BEFORE it is overwritten below. Without this "before"
            // the other modes cannot be told which instances actually moved, and guessing corrupts them.
            GameplayObjects originalFolderGp;
            try
            {
                string[] LF(string f) { var p = Path.Combine(cdir, f); return File.Exists(p) ? File.ReadAllLines(p) : System.Array.Empty<string>(); }
                originalFolderGp = GameplayObjects.Parse(LF("ControlPoints.con"), LF("ControlPointTemplates.con"),
                                                         LF("ObjectSpawns.con"), LF("ObjectSpawnTemplates.con"),
                                                         LF("SoldierSpawns.con"), LF("SoldierSpawnTemplates.con"));
            }
            catch { originalFolderGp = immo; }   // unreadable -> treat as "nothing moved" and leave other modes alone
            GameplayWriter.WriteInstanceFiles(cdir, immo);
            written.Add(Path.Combine(cdir, "ControlPoints.con"));
            written.Add(Path.Combine(cdir, "ObjectSpawns.con"));
            written.Add(Path.Combine(cdir, "SoldierSpawns.con"));

            PatchGameplayTemplates(levelDir, cdir, immo, written);

            // The level's OTHER game modes. A map ships parallel Conquest/Ctf/TDM/SinglePlayer folders holding the
            // same objects at the same coordinates, so an edit that only touches the mode we loaded from leaves the
            // rest of the map silently stale. Those folders are PATCHED, never rewritten: the modes deliberately
            // differ (Kharkov_Day2 has 5 control points in Conquest and TDM but 3 in CTF), so we move what they
            // already have and add or remove nothing.
            foreach (var other in GameModeDirs(levelDir))
            {
                if (string.Equals(other, cdir, System.StringComparison.OrdinalIgnoreCase)) continue;
                written.AddRange(GameplayWriter.PatchInstanceFiles(other, originalFolderGp, immo));
                PatchGameplayTemplates(levelDir, other, immo, written);
            }
        }
        if (shadow is not null)
        {
            // Engine reads Textures/LightmapShadowBits.lsb; land on the existing file if present.
            var p = Resolve(levelDir, "LightmapShadowBits.lsb");
            shadow.Save(p); written.Add(p);
        }

        // The support files Battlecraft keeps in step on save. Additive: an object added in the editor gets an
        // entry, anything already there (including a mapper's deliberate tuning) is left exactly as it was.
        written.AddRange(UpdateSupportFiles(levelDir, staticObjects, gameplay?.ToImmutable()));
        return written;
    }

    /// <summary>Bring <c>cullRadius.con</c>, <c>PreCache.con</c> and <c>ai/StrategicAreas.con</c> in step with the
    /// level's current objects. Returns the paths written (empty when nothing was missing).</summary>
    public static List<string> UpdateSupportFiles(string levelDir, StaticObjectsFile? staticObjects, GameplayObjects? gameplay)
    {
        var written = new List<string>();
        if (!Directory.Exists(levelDir)) return written;

        // cullRadius.con — every static template the level places needs a cull scale, or it pops in at distance.
        if (staticObjects is not null)
        {
            var templates = staticObjects.Objects.Select(o => o.Template)
                                         .Where(n => !string.IsNullOrWhiteSpace(n))
                                         .Distinct(System.StringComparer.OrdinalIgnoreCase);
            var path = Path.Combine(levelDir, "cullRadius.con");
            var existing = File.Exists(path) ? File.ReadAllLines(path) : null;
            if (LevelSupportFiles.AppendMissingCullRadius(existing, templates) is { } text)
            { File.WriteAllText(path, text); written.Add(path); }
        }

        // PreCache.con — the load-time warm-up list; a vehicle missing from it stutters when it first spawns.
        if (gameplay is not null)
        {
            var templates = gameplay.VehicleSpawns
                                    .SelectMany(v => new[] { v.Vehicle, v.Vehicle1, v.Vehicle2 })
                                    .Where(n => !string.IsNullOrWhiteSpace(n))
                                    .Select(n => n!)
                                    .Distinct(System.StringComparer.OrdinalIgnoreCase);
            var path = Path.Combine(levelDir, "PreCache.con");
            var existing = File.Exists(path) ? File.ReadAllLines(path) : null;
            if (LevelSupportFiles.AppendMissingPreCache(existing, templates) is { } text)
            { File.WriteAllText(path, text); written.Add(path); }
        }

        // ai/StrategicAreas.con — the commander AI's picture of the map. Only written when the level has NONE:
        // a shipped file is usually hand-authored around terrain, and a generated one would be a downgrade.
        if (gameplay is not null && gameplay.ControlPoints.Count > 0)
        {
            var aiDir = Directory.EnumerateDirectories(levelDir, "ai", SearchOption.AllDirectories).FirstOrDefault()
                        ?? Path.Combine(levelDir, "ai");
            var path = Path.Combine(aiDir, "StrategicAreas.con");
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(aiDir);
                File.WriteAllText(path, LevelSupportFiles.BuildStrategicAreas(gameplay.ControlPoints));
                written.Add(path);
            }
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

    /// <summary>Editor-side files that must NEVER be packed into a game archive: project manifests, sidecars,
    /// backups, temp/lock leftovers and OS litter. Packing these shipped junk into the level (and a stale
    /// <c>Backups\</c> tree inside an archive means duplicated old .con files mounted at weird sub-paths).</summary>
    public static bool IsEditorOnlyFile(string relPath)
    {
        var p = relPath.Replace('\\', '/');
        var leaf = Path.GetFileName(p);
        if (p.StartsWith("Backups/", System.StringComparison.OrdinalIgnoreCase) || p.Contains("/Backups/", System.StringComparison.OrdinalIgnoreCase)) return true;
        var ext = Path.GetExtension(leaf).ToLowerInvariant();
        if (ext is ".rfproj" or ".rfatmp" or ".bak") return true;
        return leaf.Equals("refractorforge.game", System.StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("refractorforge.json", System.StringComparison.OrdinalIgnoreCase)
            // Placed lights are authoring data. The engine has no concept of them - they reach the game only
            // once baked into the lightmaps - so the sidecar must never be packed.
            || leaf.Equals(Terrain.LightRig.FileName, System.StringComparison.OrdinalIgnoreCase)
            // Object groups and review notes are editor-side too: the engine has no notion of either.
            || leaf.Equals(Editing.ObjectGroups.FileName, System.StringComparison.OrdinalIgnoreCase)
            || leaf.Equals(Editing.Annotations.FileName, System.StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("sound_debug.log", System.StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("imgui.ini", System.StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("Thumbs.db", System.StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("desktop.ini", System.StringComparison.OrdinalIgnoreCase)
            || leaf.StartsWith("~", System.StringComparison.Ordinal);
    }

    /// <summary>Pack a level <em>folder</em> into a fresh .rfa. Entry names are the folder-relative
    /// paths (forward slashes) under an optional <paramref name="prefix"/> (e.g. "bf1942/levels/Foo/").
    /// Editor-side files (<see cref="IsEditorOnlyFile"/>: .rfproj, Backups\, sidecars, OS litter) are skipped.
    /// <paramref name="xPackId"/> lets expansion/mod maps keep their DLL binding (default = base game).</summary>
    public static int PackFolder(string folderDir, string outRfaPath, string? prefix = null, XPackId xPackId = XPackId.Default)
    {
        prefix = string.IsNullOrEmpty(prefix) ? "" : prefix.Replace('\\', '/').TrimEnd('/') + "/";
        var entries = new List<(string, byte[])>();
        foreach (var f in Directory.EnumerateFiles(folderDir, "*", SearchOption.AllDirectories).OrderBy(x => x))
        {
            var rel = Path.GetRelativePath(folderDir, f).Replace('\\', '/');
            if (IsEditorOnlyFile(rel)) continue;
            entries.Add((prefix + rel, File.ReadAllBytes(f)));
        }
        RefractorFlatArchive.WriteFile(outRfaPath, entries, compress: true, xPackId: xPackId);
        return entries.Count;
    }

    private static string? FindEntry(RefractorFlatArchive a, string suffix, bool preferConquest)
    {
        // The SHALLOWEST match wins, not the first in archive order. A level carries several files of the same
        // leaf name (Init.con at the root and under Menu/ and Animations/; StaticObjects.con per game mode), and
        // the root one is the level's own - the loader picks it the same way. Archive order is whatever the
        // packer wrote, so "first" could quietly aim an Init.con patch at Menu/init.con, where the game never
        // reads a renderer or tunnel setting.
        string? best = null; int bestDepth = int.MaxValue;
        foreach (var e in a.Entries)
        {
            if (!e.Name.EndsWith(suffix, System.StringComparison.OrdinalIgnoreCase)) continue;
            if (preferConquest && e.Name.ToLowerInvariant().Contains("conquest")) return e.Name;
            int depth = e.Name.Count(c => c == '/' || c == '\\');
            if (depth < bestDepth) { bestDepth = depth; best = e.Name; }
        }
        return best;
    }

    /// <summary>EVERY entry ending in <paramref name="suffix"/>. A packed level carries one copy of each gameplay
    /// file per game mode (Conquest/, Ctf/, TDM/, SinglePlayer/), so replacing only the first left the rest of the
    /// map describing the pre-edit layout — the same divergence the folder save had.</summary>
    private static List<string> FindEntries(RefractorFlatArchive a, string suffix)
    {
        var names = new List<string>();
        foreach (var e in a.Entries)
            if (e.Name.EndsWith(suffix, System.StringComparison.OrdinalIgnoreCase))
                names.Add(e.Name);
        return names;
    }

    private static string[] EntryLines(RefractorFlatArchive a, string name)
        => Encoding.Latin1.GetString(a.Read(a.Entries.First(e => e.Name == name))).Split('\n');

    /// <summary>Compute the name->bytes substitutions for the edited files against a base archive: each edited
    /// asset is matched to its existing entry (by trailing file name) so the replacement reuses the archive's
    /// EXACT entry name. Shared by <see cref="RepackToRfa"/> (full repack) and <see cref="WritePatchRfa"/>
    /// (standalone patch).</summary>
    /// <summary>The archive's level-folder entry prefix (e.g. "bfvietnam/levels/Foo/"), derived from where
    /// StaticObjects.con (or Init.con / Heightmap.raw) lives, so brand-new files can be added under the same path.</summary>
    public static string ArchivePrefix(RefractorFlatArchive arch)
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
        RefractorFlatArchive arch, StaticObjectsFile? so, Heightmap? heightmap, MaterialMap? material,
        EditableGameplay? gameplay, GrowthMaps? growth, LightmapShadowBits? shadow, TerrainConfig? terrainConfig,
        IEnumerable<(string Name, byte[] Bytes)>? extraFiles,
        IEnumerable<(string RelPath, byte[] Bytes)>? newEntries = null)
    {
        var repl = new Dictionary<string, byte[]>(System.StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        void Put(string? name, byte[]? data) { if (name is not null && data is not null) { repl[name] = data; names.Add(name); } }

        // Same, but for a core level file the archive may not ship at all. Add-on maps that borrow their base
        // map's terrain often carry no MaterialMap.raw or growth maps of their own, and matching-only meant an
        // edit to one was silently dropped: the save reported success and the change was simply gone. Writing it
        // under the level's own prefix is what the engine reads, exactly as a level that shipped the file.
        void PutOrAdd(string leaf, byte[]? data, string? addAt = null)
        {
            if (data is null) return;
            Put(FindEntry(arch, leaf, false) ?? (ArchivePrefix(arch) + (addAt ?? leaf)), data);
        }

        if (terrainConfig is not null)
        {
            var tcName = FindEntry(arch, "Terrain.con", false);
            if (tcName is not null)
            {
                var lines = Encoding.Latin1.GetString(arch.Read(arch.Entries.First(e => e.Name == tcName))).Split('\n');
                Put(tcName, Latin1(string.Join("\n", terrainConfig.PatchConLines(lines))));
            }
        }
        if (so is not null) PutOrAdd("StaticObjects.con", SerializeStaticObjects(so));
        if (heightmap is not null) PutOrAdd("Heightmap.raw", heightmap.ToBytes());
        if (material is not null) PutOrAdd("MaterialMap.raw", material.Samples);
        if (growth?.Under is not null) PutOrAdd("UnderGrowthMap.raw", growth.Under.Samples);
        if (growth?.Over is not null) PutOrAdd("OverGrowthMap.raw", growth.Over.Samples);
        if (shadow is not null) PutOrAdd("LightmapShadowBits.lsb", shadow.Encode(), "Textures/LightmapShadowBits.lsb");

        if (gameplay is not null)
        {
            var immo = gameplay.ToImmutable();

            // The mode the editor loaded from is written in full; every OTHER mode's copy is PATCHED in place, so
            // an edit reaches the whole map without adding a Conquest-only object to CTF or dropping a CTF-only one.
            // The mode the editor loaded, exactly as the archive still holds it. Without this "before" a patcher
            // cannot tell which instances the user moved, and every attempt to guess (by template name, by
            // ordinal) corrupted the other modes on saves that changed nothing at all.
            string[] OL(string suffix)
            {
                var n = FindEntry(arch, suffix, true);
                return n is null ? System.Array.Empty<string>() : EntryLines(arch, n);
            }
            var originalGp = GameplayObjects.Parse(OL("ControlPoints.con"), OL("ControlPointTemplates.con"),
                                                   OL("ObjectSpawns.con"), OL("ObjectSpawnTemplates.con"),
                                                   OL("SoldierSpawns.con"), OL("SoldierSpawnTemplates.con"));

            void WriteInstances(string suffix, string full)
            {
                var primary = FindEntry(arch, suffix, true);
                Put(primary, Latin1(full));
                foreach (var name in FindEntries(arch, suffix))
                {
                    if (string.Equals(name, primary, System.StringComparison.OrdinalIgnoreCase)) continue;
                    Put(name, Latin1(GameplayWriter.PatchInstanceTransforms(EntryLines(arch, name), originalGp, immo)));
                }
            }

            WriteInstances("ControlPoints.con", GameplayWriter.BuildControlPoints(immo.ControlPoints));
            WriteInstances("ObjectSpawns.con", GameplayWriter.BuildObjectSpawns(immo.VehicleSpawns));
            WriteInstances("SoldierSpawns.con", GameplayWriter.BuildSoldierSpawns(immo.SoldierSpawns));

            // Templates carry per-object properties and each mode keeps its own copy, so patch them all.
            foreach (var n in FindEntries(arch, "ControlPointTemplates.con"))
                Put(n, Latin1(GameplayWriter.PatchControlPointRadii(EntryLines(arch, n), immo.ControlPoints)));
            foreach (var n in FindEntries(arch, "SoldierSpawnTemplates.con"))
                Put(n, Latin1(GameplayWriter.PatchSoldierSpawnTemplates(EntryLines(arch, n), immo.SoldierSpawns)));
            foreach (var n in FindEntries(arch, "ObjectSpawnTemplates.con"))
                Put(n, Latin1(GameplayWriter.AppendMissingSpawnerTemplates(EntryLines(arch, n), immo.VehicleSpawns)));
        }

        // The support files Battlecraft keeps in step. Additive against whatever the archive already ships.
        if (so is not null && FindEntry(arch, "cullRadius.con", false) is { } cullName)
        {
            var templates = so.Objects.Select(o => o.Template)
                              .Where(n => !string.IsNullOrWhiteSpace(n))
                              .Distinct(System.StringComparer.OrdinalIgnoreCase);
            if (LevelSupportFiles.AppendMissingCullRadius(EntryLines(arch, cullName), templates) is { } t)
                Put(cullName, Latin1(t));
        }
        if (gameplay is not null && FindEntry(arch, "PreCache.con", false) is { } preName)
        {
            var immo = gameplay.ToImmutable();
            var templates = immo.VehicleSpawns.SelectMany(v => new[] { v.Vehicle, v.Vehicle1, v.Vehicle2 })
                                .Where(n => !string.IsNullOrWhiteSpace(n))
                                .Distinct(System.StringComparer.OrdinalIgnoreCase);
            if (LevelSupportFiles.AppendMissingPreCache(EntryLines(arch, preName), templates) is { } t)
                Put(preName, Latin1(t));
        }

        // Arbitrary edited assets matched by trailing file name (e.g. sound .ssc scripts).
        if (extraFiles is not null)
            foreach (var (name, bytes) in extraFiles)
                Put(FindEntry(arch, name, false), bytes);

        // New files — UPSERT, resolved by PATH first. An entry at exactly <prefix><rel> wins; otherwise a leaf
        // match, but only when that leaf is UNIQUE in the archive (that fallback exists for re-baked
        // ObjectLightMaps/*.tga and painted Pathfinding/*.raw, whose directory case can differ); otherwise the
        // file is added verbatim under the level's prefix.
        //
        // Leaf-only matching used to be unconditional, which silently redirected a level-local object's
        // Objects.con / Geometries.con onto whatever unrelated object happened to come first in the archive —
        // destroying that object and leaving the new one with no files at all. Those leaves are the most common
        // names in any level that already ships local objects.
        if (newEntries is not null)
        {
            string prefix = ArchivePrefix(arch);
            foreach (var (rel, bytes) in newEntries)
            {
                var relNorm = rel.Replace('\\', '/').TrimStart('/');
                var full = prefix + relNorm;
                string? target = arch.Entries.FirstOrDefault(e =>
                    string.Equals(e.Name.Replace('\\', '/'), full, System.StringComparison.OrdinalIgnoreCase))?.Name;
                if (target is null)
                {
                    var leaf = relNorm[(relNorm.LastIndexOf('/') + 1)..];
                    var byLeaf = FindEntries(arch, "/" + leaf);
                    if (byLeaf.Count == 1) target = byLeaf[0];
                }
                Put(target ?? full, bytes);
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
        var arch = new RefractorFlatArchive(originalRfaPath);
        var (repl, names) = BuildReplacements(arch, so, heightmap, material, gameplay, growth, shadow, terrainConfig, extraFiles, newEntries);
        RefractorFlatArchive.RepackToFile(outRfaPath, arch, repl);   // stream: a huge base archive won't fit in one byte[]
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
        IEnumerable<(string RelPath, byte[] Bytes)>? newEntries = null,
        bool serverSideOnly = false)
    {
        var arch = new RefractorFlatArchive(baseRfaPath);
        var (repl, names) = BuildReplacements(arch, so, heightmap, material, gameplay, growth, shadow, terrainConfig, extraFiles, newEntries);
        // SSM (server-side mod) patches: drop client-only content (textures, sounds, movies, baked light) so the
        // patch only carries what a dedicated server needs — the .con gameplay files. Clients never download it.
        if (serverSideOnly)
        {
            foreach (var drop in repl.Keys.Where(RefractorFlatArchive.IsClientOnlyEntry).ToList())
            { repl.Remove(drop); names.RemoveAll(n => string.Equals(n, drop, System.StringComparison.OrdinalIgnoreCase)); }
        }
        // Deterministic order; entries are addressed by name so order doesn't affect mounting.
        var entries = repl.OrderBy(kv => kv.Key, System.StringComparer.OrdinalIgnoreCase)
                          .Select(kv => (kv.Key, kv.Value)).ToList();
        // The patch inherits the BASE archive's XPack ID — a Road-to-Rome / Secret Weapons map patch stamped with
        // the base-game ID would bind the wrong game DLL.
        RefractorFlatArchive.WriteFile(outPatchPath, entries, compress: true, xPackId: arch.XPackId);
        return names;
    }

    /// <summary>
    /// Where the NEXT save of an .rfa level should go, patch-first: never the base archive. Rules:
    /// <list type="bullet">
    /// <item>Strip any <c>_NNN</c> suffix to find the level's base stem, then find the highest existing
    /// <c>&lt;stem&gt;_NNN.rfa</c> beside it.</item>
    /// <item>If that highest patch was written by RefractorForge (header fingerprint), it is OUR working patch —
    /// return it so repeated saves keep rewriting one file instead of littering _004, _005, _006…</item>
    /// <item>Otherwise (retail/other-tool patch, or no patch yet) return <c>&lt;stem&gt;_&lt;max+1&gt;.rfa</c> so
    /// existing archives are never modified.</item>
    /// </list>
    /// </summary>
    public static string NextPatchPath(string baseRfaPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(baseRfaPath)) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(baseRfaPath);
        var m = System.Text.RegularExpressions.Regex.Match(stem, @"^(.*?)_(\d{3})$");
        string baseStem = m.Success ? m.Groups[1].Value : stem;
        int max = 0; string? maxPath = null;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, baseStem + "_*.rfa"))
            {
                var mm = System.Text.RegularExpressions.Regex.Match(Path.GetFileNameWithoutExtension(f), @"_(\d{3})$");
                if (mm.Success && int.TryParse(mm.Groups[1].Value, out var n) && n > max) { max = n; maxPath = f; }
            }
        }
        catch { }
        if (maxPath is not null && RefractorFlatArchive.WasWrittenByRefractorForge(maxPath))
            return maxPath;                                        // our own working patch — keep rewriting it
        return Path.Combine(dir, $"{baseStem}_{max + 1:000}.rfa");  // retail/foreign patches stay untouched
    }

    /// <summary>Extract level <c>.rfa</c>(s) (base + patches, later archives win) into <paramref name="destDir"/>,
    /// stripping the shared leading folders (e.g. <c>bf1942/levels/Bocage/</c>) so the files land directly in the
    /// folder (<c>Heightmap.raw</c>, <c>Init/Terrain.con</c>, …). This is the project workflow's "open RFA → unpack
    /// to a working folder" step; the folder is then edited + saved like any folder level, and export re-packs only
    /// the modified files. Shared mesh/texture archives are NOT passed here — only the level's own <c>.rfa</c>.
    /// Returns the number of files written.</summary>
    /// <summary>Add each archive's numeric-suffix patch siblings (<c>Wake_001.rfa</c> next to <c>Wake.rfa</c>),
    /// appended AFTER their base and numerically ordered so last-wins merging matches the engine.
    ///
    /// The engine layers these automatically and a level's terrain textures often live in one, so anything that
    /// consumes a level's archives has to expand them. The viewer's load path always did; creating a PROJECT did
    /// not, and extracted only the archives the user happened to multi-select in the file dialog - which is why a
    /// project opened from a base .rfa came up with the wrong ground textures.</summary>
    public static string[] WithPatchArchives(IEnumerable<string> rfaPaths)
    {
        var outp = new List<string>();
        bool Known(string s) => outp.Any(x => Path.GetFullPath(x).Equals(Path.GetFullPath(s), StringComparison.OrdinalIgnoreCase));
        foreach (var baseRfa in rfaPaths)
        {
            if (string.IsNullOrEmpty(baseRfa)) continue;
            if (!Known(baseRfa)) outp.Add(baseRfa);
            var dir = Path.GetDirectoryName(Path.GetFullPath(baseRfa));
            var stem = Path.GetFileNameWithoutExtension(baseRfa);
            if (dir is null || !Directory.Exists(dir)) continue;
            // The suffix must be PURELY numeric: Bocage_006 is a patch of Bocage, Bocage_Day2 and Bocage_MOD are
            // separate maps that merely share the prefix.
            var rx = new System.Text.RegularExpressions.Regex(
                "^" + System.Text.RegularExpressions.Regex.Escape(stem) + @"_(\d+)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            // Order by the NUMBER, not the text. Levels carry anywhere from zero to many patches and do not pad to a
            // fixed width, so a lexicographic sort would mount _10 before _2 and let the lower patch win the merge.
            foreach (var s in Directory.EnumerateFiles(dir, stem + "_*.rfa")
                         .Select(s => (Path: s, M: rx.Match(Path.GetFileNameWithoutExtension(s))))
                         .Where(t => t.M.Success)
                         .OrderBy(t => long.TryParse(t.M.Groups[1].Value, out var v) ? v : long.MaxValue)
                         .ThenBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
                         .Select(t => t.Path))
                if (!Known(s)) outp.Add(s);
        }
        return outp.ToArray();
    }

    public static int ExtractToFolder(IEnumerable<string> rfaPaths, string destDir)
    {
        // Merge entries by name across archives so a patch overrides the base (later wins), matching the loader.
        var merged = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in rfaPaths)
        {
            if (string.IsNullOrEmpty(p) || !File.Exists(p) || Path.GetFileName(p).StartsWith("~")) continue;
            var arc = new RefractorFlatArchive(p);
            foreach (var e in arc.Entries) merged[e.Name.Replace('\\', '/')] = arc.Read(e);
        }
        string prefix = CommonDirPrefix(merged.Keys);
        Directory.CreateDirectory(destDir);
        int n = 0;
        foreach (var (name, bytes) in merged)
        {
            var rel = name.Length > prefix.Length ? name[prefix.Length..] : Path.GetFileName(name);
            var dst = Path.Combine(destDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.WriteAllBytes(dst, bytes);
            n++;
        }
        return n;
    }

    /// <summary>The longest common leading DIRECTORY prefix (forward-slashed, trailing '/') shared by all entry
    /// names; "" if none. A level archive's entries all sit under one folder, so this recovers it.</summary>
    private static string CommonDirPrefix(IEnumerable<string> names)
    {
        string[]? common = null;
        foreach (var n in names)
        {
            var parts = n.Split('/');
            var dirs = parts.Take(parts.Length - 1).ToArray();   // drop the filename
            if (common is null) { common = dirs; continue; }
            int k = 0;
            while (k < common.Length && k < dirs.Length && common[k].Equals(dirs[k], StringComparison.OrdinalIgnoreCase)) k++;
            common = common.Take(k).ToArray();
        }
        return common is { Length: > 0 } ? string.Join('/', common) + "/" : "";
    }
}
