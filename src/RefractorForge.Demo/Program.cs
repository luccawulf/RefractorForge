using System.Diagnostics;
using System.Numerics;
using RefractorForge.Formats;
using RefractorForge.Formats.Animation;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Editing;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Mesh;
using RefractorForge.Formats.Rfa;
using RefractorForge.Formats.Sound;
using RefractorForge.Formats.Terrain;
using RefractorForge.Render;

// Usage:
//   dotnet run -- render <levelDir> <out.bmp> [stride] [meshArchive...]  headless 3D preview
//   dotnet run -- edit <levelDir>                editing-engine + collaboration tests
//   dotnet run -- <levelDir>                     validate a real extracted level
//   dotnet run -- out                            synthetic no-limits demo into ./out
string arg = args.Length > 0 ? args[0] : "out";

if (arg == "newlevel")
{
    // From-scratch level generation: build a level folder, then load it back the way the Viewer's folder
    // path does and assert the round-trip. Self-contained (no real level needed).
    string outDir = args.Length >= 2 ? args[1] : Path.Combine(Path.GetTempPath(), "rf_newlevel_test");
    if (Directory.Exists(outDir)) Directory.Delete(outDir, true);

    int fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }
    bool Near(float a, float b) => MathF.Abs(a - b) < 1e-3f;
    string FindIn(string d, string n) => Directory.EnumerateFiles(d, n, SearchOption.AllDirectories).First();

    // --- Map 1: flat, custom dimensions + env ---
    {
        var dir = Path.Combine(outDir, "FlatTest");
        var cfg = new TerrainConfig { MaterialSize = 256, WorldSize = 1024, YScale = 0.5f, WaterLevel = 20, SeaFloorLevel = 0, WaveHeight = 1f };
        ushort flatRaw = cfg.MetersToRaw(25f);
        var hm = HeightmapGenerator.Flat(cfg.MaterialSize, flatRaw);   // flat plateau at 25 m
        var env = new EnvironmentSettings { SunDirection = new Vec3(0.64f, 0.34f, -0.68f), SkyRotationAngle = -45f, FogEnabled = true };
        var written = LevelSaver.CreateNewLevel(dir, "FlatTest", cfg, hm, env);
        Console.WriteLine($"FlatTest: wrote {written.Count} files");

        var rc = TerrainConfig.Load(FindIn(dir, "Terrain.con"));
        Check(rc.MaterialSize == 256, $"materialSize 256 (got {rc.MaterialSize})");
        Check(rc.WorldSize == 1024, $"worldSize 1024 (got {rc.WorldSize})");
        Check(Near(rc.YScale, 0.5f), $"yScale 0.5 (got {rc.YScale})");
        Check(Near(rc.WaterLevel, 20f), $"waterLevel 20 (got {rc.WaterLevel})");
        Check(Near(rc.WaveHeight, 1f), $"waveHeight 1 (got {rc.WaveHeight})");

        var rhm = Heightmap.LoadForMaterialSize(FindIn(dir, "Heightmap.raw"), rc.MaterialSize);
        Check(rhm.Width == 256 && rhm.Height == 256, $"heightmap 256^2 (got {rhm.Width}x{rhm.Height})");
        Check(rhm[10, 10] == flatRaw, $"flat sample == 25 m raw (got {rhm[10, 10]}, want {flatRaw})");

        var rso = StaticObjectsFile.Load(FindIn(dir, "StaticObjects.con"));
        Check(rso.Objects.Count == 0, $"StaticObjects empty (got {rso.Objects.Count})");

        var renv = EnvironmentSettings.LoadFolder(dir);
        Check(Near(renv.SunDirection.X, 0.64f) && Near(renv.SunDirection.Z, -0.68f),
              $"sun dir round-trip (got {renv.SunDirection})");
        Check(renv.FogEnabled, "fog enabled round-trip");
        Check(Near(renv.SkyRotationAngle, -45f), $"sky rotAngle -45 (got {renv.SkyRotationAngle})");

        // The rest of what the Viewer's folder-load path does — gameplay (none) + terrain mesh build.
        var rgp = GameplayObjects.LoadFolder(dir);
        Check(rgp.ControlPoints.Count == 0 && rgp.VehicleSpawns.Count == 0 && rgp.SoldierSpawns.Count == 0,
              "gameplay loads empty without throwing");
        var rmesh = TerrainMesh.FromHeightmap(rhm, rc, 1);
        Check(rmesh.Positions.Length > 0 && rmesh.Indices.Length > 0,
              $"terrain mesh builds ({rmesh.Positions.Length} verts, {rmesh.Indices.Length} indices)");
    }

    // --- Map 2: diamond-square, heightmap must survive byte-exact ---
    {
        var dir = Path.Combine(outDir, "FractalTest");
        var cfg = new TerrainConfig { MaterialSize = 512, WorldSize = 2048, YScale = 0.35f, WaterLevel = 30 };
        var hm = HeightmapGenerator.DiamondSquare(cfg.MaterialSize, seed: 7, roughness: 0.55f, min: 0, max: 20000);
        LevelSaver.CreateNewLevel(dir, "FractalTest", cfg, hm, new EnvironmentSettings());

        var rc = TerrainConfig.Load(FindIn(dir, "Terrain.con"));
        var rhm = Heightmap.LoadForMaterialSize(FindIn(dir, "Heightmap.raw"), rc.MaterialSize);
        Check(rc.MaterialSize == 512 && rc.WorldSize == 2048, "fractal cfg 512/2048");
        bool same = hm.Samples.Length == rhm.Samples.Length;
        for (int i = 0; i < hm.Samples.Length && same; i++) if (hm.Samples[i] != rhm.Samples[i]) same = false;
        Check(same, "fractal heightmap byte-exact round-trip");
    }

    // --- Map 3: playable Conquest map — the gameplay layer must generate and load back ---
    {
        var dir = Path.Combine(outDir, "PlayableTest");
        var cfg = new TerrainConfig { MaterialSize = 256, WorldSize = 1024, YScale = 0.5f, WaterLevel = 20 };
        var hm = HeightmapGenerator.Flat(cfg.MaterialSize, cfg.MetersToRaw(30f));
        LevelSaver.CreateNewLevel(dir, "PlayableTest", cfg, hm, new EnvironmentSettings(), playable: true);

        var gp = GameplayObjects.LoadFolder(dir);   // the same parser the Viewer's folder load uses
        Check(gp.ControlPoints.Count == 3, $"3 control points generated (got {gp.ControlPoints.Count})");
        Check(gp.SoldierSpawns.Count == 12, $"12 soldier spawns generated (got {gp.SoldierSpawns.Count})");

        var cpt = File.ReadAllText(Path.Combine(dir, "Conquest", "ControlPointTemplates.con"));
        Check(cpt.Contains("ObjectTemplate.team 2") && cpt.Contains("ObjectTemplate.team 1") && cpt.Contains("ObjectTemplate.team 0"),
              "US / NVA / neutral CP teams present");
        var initTxt = File.ReadAllText(FindIn(dir, "Init.con"));
        Check(initTxt.Contains("game.setKit 2 0 USArmy_Recon") && initTxt.Contains("setBeforeSpawnCameraPosition"),
              "Init.con carries kits + pre-spawn cameras");
        Check(File.Exists(Path.Combine(dir, "Conquest.con")) && File.Exists(Path.Combine(dir, "GameTypes", "Conquest.con")),
              "Conquest.con + GameTypes/Conquest.con written");

        var aip = Path.Combine(dir, "AIpathFinding.con");
        var aiTxt = File.Exists(aip) ? File.ReadAllText(aip) : "";
        int searchMaps = System.Text.RegularExpressions.Regex.Matches(aiTxt, @"ai\.addSearchMap").Count;
        Check(searchMaps == 7, $"AIpathFinding.con has 7 search maps (got {searchMaps})");
        Check(aiTxt.Contains("ai.addSearchType Tank 0") && aiTxt.Contains("ai.loadMaps"), "AIpathFinding.con has Tank + loadMaps");

        // The AI navmaps themselves are generated by CreateNewLevel for a playable map — BOTH the engine compressed
        // form (<Veh>Level<L>Map.raw) and the editor 8Bit form (<Veh>Level<L>Map8Bit.raw).
        var navDir = Path.Combine(dir, "Pathfinding");
        var navFiles = Directory.Exists(navDir) ? Directory.GetFiles(navDir) : Array.Empty<string>();
        int navEight = navFiles.Count(f => f.EndsWith("Map8Bit.raw"));
        int navComp = navFiles.Count(f => f.EndsWith("Map.raw") && !f.EndsWith("Map8Bit.raw"));
        Check(navEight == 21, $"21 editor 8Bit navmaps written (got {navEight})");
        Check(navComp == navEight && navComp == 21, $"21 engine compressed navmaps written (got {navComp})");
        Check(File.Exists(Path.Combine(navDir, "Tank0Level2Map.raw")), "engine-form Tank0Level2Map.raw written");
        var tankL2 = Path.Combine(navDir, "Tank0Level2Map8Bit.raw");
        Check(File.Exists(tankL2) && new FileInfo(tankL2).Length == cfg.MaterialSize * cfg.MaterialSize,
              "Tank0Level2 navmap is materialSize^2 bytes");
    }

    Console.WriteLine(fails == 0 ? "NEW LEVEL TESTS PASSED" : $"NEW LEVEL TESTS FAILED ({fails})");
    return fails == 0 ? 0 : 1;
}

if (arg == "heightmapio")
{
    // Heightmap import/export math: raw byte round-trip, square-side inference, resample identity + scaling,
    // and the in-place CopyFrom the Viewer's import uses. Self-contained (no real level needed).
    int fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }

    // A diagonal 16-bit ramp so bilinear resampling is (near) exact and easy to reason about.
    var src = new Heightmap(64, 64);
    for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) src[x, y] = (ushort)((x + y) * 500);

    // ToBytes -> FromBytes is byte-exact (the on-disk Heightmap.raw form).
    var bytes = src.ToBytes();
    Check(bytes.Length == 64 * 64 * 2, $"ToBytes length 64^2*2 (got {bytes.Length})");
    var rt = Heightmap.FromBytes(bytes, 64, 64);
    bool exact = true; for (int i = 0; i < src.Samples.Length && exact; i++) if (src.Samples[i] != rt.Samples[i]) exact = false;
    Check(exact, "raw byte round-trip exact");

    // Square-side inference from a written .raw (what the Viewer's import uses).
    string tmp = Path.Combine(Path.GetTempPath(), "rf_heightmapio.raw");
    src.SaveRaw(tmp);
    var square = Heightmap.LoadRawSquare(tmp);
    Check(square.Width == 64 && square.Height == 64, $"LoadRawSquare inferred 64^2 (got {square.Width}x{square.Height})");

    // Identity resample is an exact copy.
    var same = src.Resample(64, 64);
    bool idEq = true; for (int i = 0; i < src.Samples.Length && idEq; i++) if (src.Samples[i] != same.Samples[i]) idEq = false;
    Check(idEq, "identity resample exact");

    // Up- then down-sample a linear field returns close to the original (corners exact).
    var up = src.Resample(128, 128);
    Check(up.Width == 128 && up[0, 0] == src[0, 0] && up[127, 127] == src[63, 63], "upsample keeps corners");
    var down = up.Resample(64, 64);
    int maxErr = 0; for (int i = 0; i < src.Samples.Length; i++) maxErr = Math.Max(maxErr, Math.Abs(src.Samples[i] - down.Samples[i]));
    Check(maxErr <= 1, $"linear field survives up/down resample (max err {maxErr})");

    // CopyFrom (the in-place import path): mismatched sizes resample to materialSize, then overwrite in place.
    int materialSize = 64;
    var imported = Heightmap.LoadRawSquare(tmp);              // 64^2 here, but exercise the resample branch anyway
    if (imported.Width != materialSize) imported = imported.Resample(materialSize, materialSize);
    var live = new Heightmap(materialSize, materialSize);     // stand-in for the level's live heightmap
    var liveRef = live.Samples;                              // the SAME array must survive CopyFrom (in-place)
    live.CopyFrom(imported);
    Check(ReferenceEquals(live.Samples, liveRef), "CopyFrom is in place (same backing array)");
    bool copied = true; for (int i = 0; i < live.Samples.Length && copied; i++) if (live.Samples[i] != imported.Samples[i]) copied = false;
    Check(copied, "CopyFrom overwrote samples");

    bool threw = false; try { live.CopyFrom(up); } catch (ArgumentException) { threw = true; }
    Check(threw, "CopyFrom rejects a dimension mismatch");

    try { File.Delete(tmp); } catch { }
    Console.WriteLine(fails == 0 ? "HEIGHTMAP IO TESTS PASSED" : $"HEIGHTMAP IO TESTS FAILED ({fails})");
    return fails == 0 ? 0 : 1;
}

if (arg == "spawnlinks")
{
    // Spawn -> control-point linkage: the new template fields (CP objectSpawnerId, soldier setGroup) and the
    // resolver that ties each spawn to its owning flag. Self-contained; if a real level dir is passed as arg[1]
    // it also validates the documented Operation_Irving linkage.
    int fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }

    // Two flags with distinct group / spawner ids. NVA_base sits far from US_base so the id match can be told
    // apart from the proximity fallback.
    var cps = new List<ControlPointDef>
    {
        new("US_base",  new Vec3(100, 0, 100), 30f, 1, 2, 25, 40, "US_base", 1),
        new("NVA_base", new Vec3(900, 0, 900), 25f, 2, 1, 25, 40, "NVA_base", 2),
    };
    // Soldier group 2 -> NVA_base BY ID, even though this spawn sits nearer US_base (proves id beats proximity).
    Check(GameplayObjects.OwningControlPointIndex(cps, new Vec3(120, 0, 120), 2, false) == 1, "soldier group 2 -> NVA_base by id");
    Check(GameplayObjects.OwningControlPointIndex(cps, new Vec3(880, 0, 880), 1, true)  == 0, "vehicle OSId 1 -> US_base by id");
    Check(GameplayObjects.OwningControlPointIndex(cps, new Vec3(110, 0, 110), 0, false) == 0, "id 0 -> nearest (US_base)");
    Check(GameplayObjects.OwningControlPointIndex(cps, new Vec3(905, 0, 880), 0, true)  == 1, "id 0 -> nearest (NVA_base)");
    Check(GameplayObjects.OwningControlPointIndex(cps, new Vec3(905, 0, 880), 77, true) == 1, "unclaimed id -> nearest fallback");
    Check(GameplayObjects.OwningControlPointIndex(new List<ControlPointDef>(), Vec3.Zero, 1, true) == -1, "no flags -> -1");

    if (args.Length >= 2 && Directory.Exists(args[1]))
    {
        var gp = GameplayObjects.LoadFolder(args[1]);
        var rc = gp.ControlPoints;
        var us = rc.FirstOrDefault(c => c.Name.Equals("US_base", StringComparison.OrdinalIgnoreCase));
        Check(us.SpawnGroupId == 1 && us.ObjectSpawnerId == 1, $"US_base spawnGroupId=1 objectSpawnerId=1 (got {us.SpawnGroupId}/{us.ObjectSpawnerId})");
        var usb1 = gp.SoldierSpawns.FirstOrDefault(s => s.Name.Equals("usbase1", StringComparison.OrdinalIgnoreCase));
        Check(usb1.Group == 1, $"soldier usbase1 group=1 (got {usb1.Group})");
        int usIdx = rc.ToList().FindIndex(c => c.Name.Equals("US_base", StringComparison.OrdinalIgnoreCase));
        Check(usIdx >= 0 && GameplayObjects.OwningControlPointIndex(rc, usb1.Position, usb1.Group, false) == usIdx, "usbase1 resolves to US_base");
        var veh1 = gp.VehicleSpawns.FirstOrDefault(v => v.OsId == 1);
        Check(!string.IsNullOrEmpty(veh1.Name) && GameplayObjects.OwningControlPointIndex(rc, veh1.Position, veh1.OsId, true) == usIdx, "an OSId-1 vehicle resolves to US_base");
        Console.WriteLine($"  (real level: {rc.Count} CPs, {gp.VehicleSpawns.Count} vehicle spawns, {gp.SoldierSpawns.Count} soldier spawns)");
    }

    Console.WriteLine(fails == 0 ? "SPAWN LINKS TESTS PASSED" : $"SPAWN LINKS TESTS FAILED ({fails})");
    return fails == 0 ? 0 : 1;
}

if (arg == "patchrfa" && args.Length >= 2)
{
    // Patch .rfa save: edit a level loaded from .rfa, write a PATCH (changed files only), then mount [base, patch]
    // through the real loader (later archives win, same as the engine) and prove the edits override while
    // base-only files still load. Needs a real base level .rfa as arg[1]; arg[2] = optional out dir.
    string baseRfa = args[1];
    string outDir = args.Length >= 3 ? args[2] : Path.GetTempPath();
    Directory.CreateDirectory(outDir);
    string patchPath = Path.Combine(outDir, "rf_patch_test_001.rfa");

    int fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }

    var baseLoad = LevelArchive.FromRfa(baseRfa);
    var hm = baseLoad.Heightmap;
    ushort origH = hm[10, 10];
    ushort editH = (ushort)(origH ^ 0x4000);          // clearly different
    hm[10, 10] = editH;

    var gp = new EditableGameplay(baseLoad.Gameplay);
    bool haveCp = gp.ControlPoints.Count > 0;
    Vec3 cpOrig = haveCp ? gp.ControlPoints[0].Position : default;
    if (haveCp) gp.ControlPoints[0] = gp.ControlPoints[0] with { Position = new Vec3(cpOrig.X + 50f, cpOrig.Y, cpOrig.Z) };

    var names = LevelSaver.WritePatchRfa(baseRfa, patchPath, baseLoad.StaticObjects, hm, baseLoad.Material, gp, baseLoad.Growth, null, null);
    Check(names.Count > 0, $"patch wrote {names.Count} edited entries");

    var basA = RefractorForge.Formats.Rfa.RfaArchive.Open(baseRfa);
    var patA = RefractorForge.Formats.Rfa.RfaArchive.Open(patchPath);
    Check(patA.Entries.Count < basA.Entries.Count, $"patch is a subset ({patA.Entries.Count} < {basA.Entries.Count} entries)");
    var baseNames = new HashSet<string>(basA.Entries.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
    Check(patA.Entries.All(e => baseNames.Contains(e.Name)), "every patch entry name matches a base entry (so it overrides)");
    var hmEntry = patA.Entries.First(e => e.Name.Replace('\\', '/').EndsWith("/Heightmap.raw", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith("Heightmap.raw", StringComparison.OrdinalIgnoreCase));
    var hmBytes = patA.Read(hmEntry); var want = hm.ToBytes();
    Check(hmBytes.Length == want.Length && hmBytes.SequenceEqual(want), "patch Heightmap.raw round-trips byte-exact");

    // End-to-end: mount [base, patch] (later wins) and confirm the edits are what loads.
    var merged = LevelArchive.FromRfa(baseRfa, patchPath);
    Check(merged.Heightmap[10, 10] == editH, $"merged heightmap[10,10] == edited ({merged.Heightmap[10, 10]} want {editH})");
    Check(merged.Heightmap[10, 10] != origH, "the edit actually changed the loaded value (vs base)");
    Check(merged.Config.MaterialSize == baseLoad.Config.MaterialSize, "base-only Terrain.con still loads from the base archive");
    if (haveCp)
    {
        var mcp = merged.Gameplay.ControlPoints[0].Position;
        Check(Math.Abs(mcp.X - (cpOrig.X + 50f)) < 0.5f, $"merged CP[0] moved +50 X ({mcp.X} want {cpOrig.X + 50f})");
    }

    // Sound script in a patch (.rfa-level sound save): edit an emitter's .ssc, write it into a patch, reload
    // [base, patch] and confirm the edited volume is what loads.
    var slib = baseLoad.Sounds;
    if (slib is not null && slib.Count > 0)
    {
        var em = slib.Emitters.FirstOrDefault(e => e.Script is not null);
        if (em is not null)
        {
            em.Script!.SetVolume(0.137f); em.Dirty = true;
            string sPatch = Path.Combine(outDir, "rf_patch_snd_001.rfa");
            var sn = LevelSaver.WritePatchRfa(baseRfa, sPatch, null, null, null, null, extraFiles: slib.DirtyScripts());
            Check(sn.Count == 1, $"sound patch wrote 1 entry ({string.Join(",", sn.Select(Path.GetFileName))})");
            var merged2 = LevelArchive.FromRfa(baseRfa, sPatch);
            var em2 = merged2.Sounds!.Get(em.Template);
            Check(em2?.Script is not null && Math.Abs(em2.Script.Volume - 0.137f) < 1e-4, $"merged .ssc has the edited volume ({em2?.Script?.Volume})");
            try { File.Delete(sPatch); } catch { }
        }
    }

    // Surface tiles in a patch (.rfa-level surface-tile save): paint the terrain atlas a solid marker colour,
    // split it back to txCxR.dds tiles, write them into a patch via extraFiles, reload [base, patch] and confirm
    // the painted colour is what bakes back. Proves every tile name matched a base entry (the FindEntry/EndsWith
    // canary -> tn.Count == tile count) and the uncompressed-DDS bytes round-trip through the archive. Guarded so
    // a base .rfa with no terrain tiles still passes.
    var tt = baseLoad.Terrain;
    if (tt is not null)
    {
        var atlas = tt.BakeAtlas(1024);
        for (int i = 0; i < atlas.Rgba.Length; i += 4) { atlas.Rgba[i] = 255; atlas.Rgba[i + 1] = 0; atlas.Rgba[i + 2] = 255; atlas.Rgba[i + 3] = 255; }  // solid magenta (survives any tile resample)
        var tileBytes = new List<(string Name, byte[] Bytes)>();
        foreach (var (fileName, tile) in tt.SplitToTiles(atlas))
            tileBytes.Add((fileName, RefractorForge.Render.DdsTexture.EncodeUncompressed(tile)));
        Check(tileBytes.Count > 0, $"split painted atlas into {tileBytes.Count} txCxR.dds tile(s)");
        string tPatch = Path.Combine(outDir, "rf_patch_tex_001.rfa");
        var tn = LevelSaver.WritePatchRfa(baseRfa, tPatch, null, null, null, null, extraFiles: tileBytes);
        Check(tn.Count == tileBytes.Count, $"every painted tile matched a base entry ({tn.Count}/{tileBytes.Count}) -- no name collision");
        var merged3 = LevelArchive.FromRfa(baseRfa, tPatch);
        Check(merged3.Terrain is not null, "merged level re-loaded its terrain tiles from [base, patch]");
        if (merged3.Terrain is not null)
        {
            var a2 = merged3.Terrain.BakeAtlas(1024);
            int ci = (a2.Height / 2 * a2.Width + a2.Width / 2) * 4;
            Check(a2.Rgba[ci] > 200 && a2.Rgba[ci + 1] < 60 && a2.Rgba[ci + 2] > 200, $"baked-back atlas shows the painted marker colour (R{a2.Rgba[ci]} G{a2.Rgba[ci + 1]} B{a2.Rgba[ci + 2]})");
        }
        try { File.Delete(tPatch); } catch { }
    }
    else Console.WriteLine("  (skipped surface-tile patch: base .rfa has no terrain tiles)");

    try { File.Delete(patchPath); } catch { }
    Console.WriteLine(fails == 0 ? "PATCH RFA TESTS PASSED" : $"PATCH RFA TESTS FAILED ({fails})");
    return fails == 0 ? 0 : 1;
}

if (arg == "soundedit")
{
    // Sound script (.ssc) parse/edit/round-trip + the SoundLibrary scan. Self-contained; with a real level dir
    // as arg[1] it also scans that level's Sounds/ and validates the template->emitter map.
    int fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }

    // CRLF script with two tiers + an effect block (mirrors a real ambient emitter).
    string ssc = "#templateLevel HIGH\r\n\r\nnewPatch\r\n\r\nstream @ROOT/Sound/@RTD/frogs_1.wav\r\nloop\r\nminDistance 10\r\nvolume 1\r\n\r\nbeginEffect\r\n\tcontrolDestination Volume\r\n\tcontrolSource Distance\r\nendEffect\r\n";
    var s = SoundScript.Parse(ssc);
    Check(s.ToText() == ssc, "unedited .ssc round-trips byte-exact");
    Check(s.Wav == "@ROOT/Sound/@RTD/frogs_1.wav" && s.SourceMode == "stream", "reads wav + source mode");
    Check(System.Math.Abs(s.Volume - 1f) < 1e-4 && System.Math.Abs(s.MinDistance - 10f) < 1e-4, "reads volume + minDistance");
    Check(s.Loop && !s.Stereo, "reads loop flag (and stereo absent)");

    s.SetVolume(0.5f); s.SetMinDistance(25f); s.SetLoop(false); s.SetStereo(true); s.SetWav("@ROOT/Sound/@RTD/frogs_2.wav");
    var s2 = SoundScript.Parse(s.ToText());   // re-parse the edited text
    Check(System.Math.Abs(s2.Volume - 0.5f) < 1e-4, $"volume edit persists ({s2.Volume})");
    Check(System.Math.Abs(s2.MinDistance - 25f) < 1e-4, $"minDistance edit persists ({s2.MinDistance})");
    Check(!s2.Loop, "loop turned off (line removed)");
    Check(s2.Stereo, "stereo turned on (line added)");
    Check(s2.Wav == "@ROOT/Sound/@RTD/frogs_2.wav", $"wav swapped ({s2.Wav})");
    Check(s.ToText().Contains("beginEffect") && s.ToText().Contains("controlSource Distance"), "effect block preserved through edits");

    // A wave with NO volume line: SetVolume must INSERT one (right after the source line).
    var noVol = SoundScript.Parse("newPatch\r\nload sound.wav\r\nminDistance 5\r\n");
    Check(System.Math.Abs(noVol.Volume - 1f) < 1e-4, "missing volume defaults to 1");
    noVol.SetVolume(0.3f);
    Check(System.Math.Abs(SoundScript.Parse(noVol.ToText()).Volume - 0.3f) < 1e-4, "SetVolume inserts a volume line when absent");

    // Folder save round-trip: SoundLibrary.LoadFolder -> edit -> SaveDirty -> reload (self-contained temp level).
    string tmpLvl = Path.Combine(Path.GetTempPath(), "rf_soundedit_lvl");
    string tmpSnd = Path.Combine(tmpLvl, "Sounds");
    Directory.CreateDirectory(tmpSnd);
    File.WriteAllText(Path.Combine(tmpSnd, "Frogs.con"),
        "ObjectTemplate.create SimpleObject Frogs\r\nObjectTemplate.saveInSeparateFile 1\r\nObjectTemplate.loadSoundScript Frogs.ssc\r\n");
    File.WriteAllText(Path.Combine(tmpSnd, "Frogs.ssc"),
        "newPatch\r\nstream @ROOT/Sound/@RTD/frogs_1.wav\r\nloop\r\nminDistance 10\r\nvolume 1\r\n");
    var lib0 = SoundLibrary.LoadFolder(tmpLvl);
    var fr = lib0.Get("Frogs");
    Check(fr is not null && fr.SscPath is not null && fr.Script is not null, "folder load mapped Frogs + resolved its .ssc path");
    fr!.Script!.SetVolume(0.25f); fr.Script.SetMinDistance(42f); fr.Dirty = true;
    var wrote = lib0.SaveDirty();
    Check(wrote.Count == 1 && !fr.Dirty, "SaveDirty wrote 1 file + cleared dirty");
    var lib1 = SoundLibrary.LoadFolder(tmpLvl);
    var fr1 = lib1.Get("Frogs");
    Check(fr1?.Script is not null && System.Math.Abs(fr1.Script.Volume - 0.25f) < 1e-4 && System.Math.Abs(fr1.Script.MinDistance - 42f) < 1e-4,
          $"edits persisted to disk + reloaded (vol {fr1?.Script?.Volume}, minDist {fr1?.Script?.MinDistance})");
    try { Directory.Delete(tmpLvl, true); } catch { }

    if (args.Length >= 2 && Directory.Exists(args[1]))
    {
        var lib = SoundLibrary.LoadFolder(args[1]);
        Check(lib.Count > 0, $"scanned {lib.Count} sound emitter template(s) from Sounds/");
        var frogs = lib.Get("Frogs");
        Check(frogs is not null && frogs.SscName.Equals("Frogs.ssc", StringComparison.OrdinalIgnoreCase), "mapped template 'Frogs' -> Frogs.ssc");
        Check(frogs?.Script is not null && frogs.Script.Wav is not null, "loaded Frogs.ssc script (has a wav)");
        Check(lib.IsSound("Flies1") && !lib.IsSound("USTank"), "IsSound true for an emitter, false for a tank");
        Console.WriteLine("  emitters: " + string.Join(", ", lib.TemplateNames));
    }

    Console.WriteLine(fails == 0 ? "SOUND EDIT TESTS PASSED" : $"SOUND EDIT TESTS FAILED ({fails})");
    return fails == 0 ? 0 : 1;
}

if (arg == "objsm")
{
    // OBJ import -> standard-mesh (.sm) writer: write a parsed .obj and prove it parses back through the
    // StandardMesh READER to identical geometry (winding, uv, normals, bbox, multi-material). Self-contained;
    // with a real .obj as arg[1] it round-trips that too.
    int fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }
    bool Near(float a, float b) => MathF.Abs(a - b) < 1e-4f;

    // A unit quad (2 triangles) with explicit uv + normal, one material.
    string quad = "v 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\nvt 0 0\nvt 1 0\nvt 1 1\nvt 0 1\nvn 0 0 1\nf 1/1/1 2/2/1 3/3/1\nf 1/1/1 3/3/1 4/4/1\n";
    var om = ObjMesh.Parse(quad);
    Check(om.SubMeshes.Count == 1 && om.TotalVertices == 4 && om.TotalFaces == 2, $"parsed quad (1 mat, {om.TotalVertices} verts, {om.TotalFaces} tris)");

    var sm = StandardMesh.Parse(StandardMeshWriter.Write(om));
    Check(sm.Version == 10 && sm.NumLods == 1 && sm.Lods.Count == 1, "wrote v10 single-LOD mesh");
    Check(sm.Consumed == sm.Total, $"reader consumed every byte ({sm.Consumed}/{sm.Total})");
    var mat = sm.Lods[0][0];
    Check(sm.Lods[0].Count == 1 && mat.NumVertices == 4 && mat.Faces.Length == 2, "round-trips 1 section, 4 verts, 2 tris");
    Check(mat.Faces[0] == (0, 1, 2) && mat.Faces[1] == (0, 2, 3), $"triangle winding preserved ({mat.Faces[0]} {mat.Faces[1]})");
    Check(Near(mat.Vertices[1].X, 1f) && Near(mat.Vertices[2].Y, 1f), "positions round-trip");
    Check(Near(mat.Uvs[2].U, 1f) && Near(mat.Uvs[2].V, 1f), "uvs round-trip");
    Check(Near(mat.Normals[0].Z, 1f), "normals round-trip");
    Check(Near(sm.BoundingBox[0], 0f) && Near(sm.BoundingBox[3], 1f) && Near(sm.BoundingBox[4], 1f), "bbox round-trips");

    // No normals in the file -> derived from the face (a +Z quad).
    var noN = ObjMesh.Parse("v 0 0 0\nv 1 0 0\nv 1 1 0\nf 1 2 3\n");
    Check(noN.TotalFaces == 1 && Near(MathF.Abs(noN.SubMeshes[0].Normals[0].Z), 1f), "missing normals computed from faces");

    // Two usemtl groups -> two material sections, names preserved, the unused 'default' dropped.
    var multi = ObjMesh.Parse("v 0 0 0\nv 1 0 0\nv 0 1 0\nv 2 0 0\nv 3 0 0\nv 2 1 0\nusemtl red\nf 1 2 3\nusemtl blue\nf 4 5 6\n");
    Check(multi.SubMeshes.Count == 2, $"two usemtl -> two submeshes ({multi.SubMeshes.Count})");
    var sm2 = StandardMesh.Parse(StandardMeshWriter.Write(multi));
    Check(sm2.Lods[0].Count == 2 && sm2.Lods[0][0].Name == "red" && sm2.Lods[0][1].Name == "blue", "material names round-trip");

    // The editor's render conversion (what gets injected into the mesh library for in-editor preview/placement).
    var rmesh = RefractorForge.Render.MeshLibrary.MeshFromObj(multi);
    Check(rmesh.Positions.Length == multi.TotalVertices && rmesh.Triangles == multi.TotalFaces && rmesh.Parts.Length == 2,
          $"MeshFromObj keeps counts ({rmesh.Positions.Length} verts, {rmesh.Triangles} tris, {rmesh.Parts.Length} parts)");

    // .mtl materials (colours + textures) + the .rs export shader round-trip + mtllib capture (richer pass).
    var mtl = ObjMtl.Parse("newmtl wood\nKd 0.6 0.4 0.2\nmap_Kd textures/oak.png\nnewmtl glass\nKd 0.1 0.2 0.9\n");
    Check(mtl.Count == 2 && Near(mtl["wood"].Diffuse.X, 0.6f) && mtl["wood"].TextureName == "oak" && mtl["glass"].TextureFile is null,
          $".mtl parses colours + texture (wood tex '{mtl["wood"].TextureName}')");
    var rsText = RsShaderSet.Write(new (string, string?, Vector3)[] {
        ("wood", "oak", new Vector3(0.6f, 0.4f, 0.2f)), ("glass", null, new Vector3(0.1f, 0.2f, 0.9f)) });
    var rs = RsShaderSet.Parse(rsText);
    Check(rs.Materials.Count == 2 && rs.Materials["wood"].Texture == "oak" && Near(rs.Materials["wood"].Diffuse.X, 0.6f) && rs.Materials["glass"].Texture is null,
          ".rs write round-trips through the reader (material/texture/diffuse)");
    var withMtl = ObjMesh.Parse("mtllib scene.mtl\nv 0 0 0\nv 1 0 0\nv 0 1 0\nusemtl wood\nf 1 2 3\n");
    Check(withMtl.MtlLibs.Count == 1 && withMtl.MtlLibs[0] == "scene.mtl" && withMtl.SubMeshes[0].Material == "wood", "mtllib + usemtl captured");

    // Collision-section capture (RE groundwork — see docs/SM_Collision_RE.md): the reader now retains raw
    // collision bytes; real sections start with magic 0xEB97C2FA (a serialized DShape).
    {
        var cms = new System.IO.MemoryStream(); var cw = new System.IO.BinaryWriter(cms);
        cw.Write((uint)10); cw.Write(new byte[4]); for (int i = 0; i < 6; i++) cw.Write(0f); cw.Write((byte)0);
        cw.Write((uint)1);                                            // numCollisionMeshes
        cw.Write((uint)8); cw.Write(0xEB97C2FAu); cw.Write((uint)0);   // one 8-byte section: magic + 4 bytes
        cw.Write((uint)0);                                            // numLods
        var smc = StandardMesh.Parse(cms.ToArray());
        Check(smc.NumCollisionMeshes == 1 && smc.CollisionSections.Count == 1 && smc.CollisionSections[0].Length == 8, "reader captures the collision section (was skipped)");
        Check(BitConverter.ToUInt32(smc.CollisionSections[0], 0) == 0xEB97C2FA, "captured section starts with the collision magic 0xEB97C2FA");
    }
    {
        // Decode a collision section into verts + tris (DShape layout: magic/ver/numVerts/verts16B/numTris/tri 4xu16).
        var pms = new System.IO.MemoryStream(); var pw = new System.IO.BinaryWriter(pms);
        pw.Write(0xEB97C2FAu); pw.Write((uint)5); pw.Write((uint)4);
        float[,] vv = { { 0, 0, 0 }, { 1, 0, 0 }, { 1, 0, 1 }, { 0, 0, 1 } };
        for (int i = 0; i < 4; i++) { pw.Write(vv[i, 0]); pw.Write(vv[i, 1]); pw.Write(vv[i, 2]); pw.Write(0f); }
        pw.Write((uint)2);
        pw.Write((ushort)0); pw.Write((ushort)1); pw.Write((ushort)2); pw.Write((ushort)99);
        pw.Write((ushort)0); pw.Write((ushort)2); pw.Write((ushort)3); pw.Write((ushort)99);
        bool ok = StandardMesh.TryParseCollision(pms.ToArray(), out var cverts, out var cidx);
        Check(ok && cverts.Length == 4 && cidx.Length == 6, "collision section parses to 4 verts / 2 tris");
        Check(ok && cidx[0] == 0 && cidx[1] == 1 && cidx[2] == 2 && cidx[5] == 3, "collision triangle indices decode (sep skipped)");
        Check(!StandardMesh.TryParseCollision(new byte[] { 1, 2, 3, 4 }, out _, out _), "garbage collision section rejected");
    }
    {
        // Full parse + writer round-trip byte-exact (header + verts + tris + verbatim BSP/DShape tail).
        var oms = new System.IO.MemoryStream(); var ow = new System.IO.BinaryWriter(oms);
        ow.Write(0xEB97C2FAu); ow.Write((uint)5); ow.Write((uint)3);
        for (int i = 0; i < 3; i++) { ow.Write((float)i); ow.Write(0.5f); ow.Write(-(float)i); ow.Write(9f); }
        ow.Write((uint)1); ow.Write((ushort)0); ow.Write((ushort)1); ow.Write((ushort)2); ow.Write((ushort)42);
        ow.Write(new byte[] { 7, 0, 0, 0, 1, 2, 3 });   // arbitrary tail
        var orig = oms.ToArray();
        bool fok = StandardMesh.TryParseCollisionFull(orig, out var cd);
        Check(fok && cd.VertexCount == 3 && cd.TriangleCount == 1 && cd.Tail.Length == 7, "full collision parse (verts/tris/tail)");
        Check(fok && StandardMeshWriter.WriteCollisionSection(cd).AsSpan().SequenceEqual((ReadOnlySpan<byte>)orig), "collision section round-trips byte-exact");
    }
    {
        // GENERATE an (empty-BSP) collision section from geometry, parse it back self-consistently, embed in a .sm.
        var gv = new System.Collections.Generic.List<Vec3> { new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1) };
        var gt = new System.Collections.Generic.List<(int, int, int)> { (0, 1, 2), (0, 2, 3) };
        var gsec = StandardMeshWriter.BuildCollisionSection(gv, gt);
        Check(StandardMesh.TryParseCollision(gsec, out var gpv, out var gpi) && gpv.Length == 4 && gpi.Length == 6, "generated collision section parses back (4v/2t)");
        var withCol = StandardMesh.Parse(StandardMeshWriter.Write(om, gsec));
        Check(withCol.NumCollisionMeshes == 1 && withCol.Consumed == withCol.Total && StandardMesh.TryParseCollision(withCol.CollisionSections[0], out _, out var ei) && ei.Length == 6, ".sm embeds + re-reads the generated collision");
    }

    if (args.Length >= 2 && File.Exists(args[1]))
    {
        var real = ObjMesh.Load(args[1]);
        var rsm = StandardMesh.Parse(StandardMeshWriter.Write(real));
        var (rm, rv, rf) = rsm.Lod0Counts();
        Check(rsm.Consumed == rsm.Total && rv == real.TotalVertices && rf == real.TotalFaces,
              $"real .obj round-trips ({rm} mats, {rv} verts, {rf} tris)");
    }

    Console.WriteLine(fails == 0 ? "OBJ SM TESTS PASSED" : $"OBJ SM TESTS FAILED ({fails})");
    return fails == 0 ? 0 : 1;
}

if (arg == "conblock" && args.Length >= 3)
{
    // Print the full ObjectTemplate.create block whose name contains <arg2>.
    var arc = RefractorForge.Formats.Rfa.RfaArchive.Open(args[1]);
    string want = args[2].ToLowerInvariant();
    foreach (var e in arc.Entries)
    {
        if (!e.Name.EndsWith(".con", StringComparison.OrdinalIgnoreCase)) continue;
        string text; try { text = System.Text.Encoding.Latin1.GetString(arc.Read(e)); } catch { continue; }
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i].Replace("\r", "").Trim();
            if (l.StartsWith("ObjectTemplate.create", StringComparison.OrdinalIgnoreCase) && l.ToLowerInvariant().Contains(want))
            {
                Console.WriteLine($"--- {Path.GetFileName(e.Name.Replace('\\', '/'))} ---\n{l}");
                for (int j = i + 1; j < lines.Length; j++)
                {
                    var lj = lines[j].Replace("\r", "").Trim();
                    if (lj.StartsWith("ObjectTemplate.create", StringComparison.OrdinalIgnoreCase)) return 0;
                    if (lj.Length > 0 && !lj.StartsWith("rem", StringComparison.OrdinalIgnoreCase)) Console.WriteLine("  " + lj);
                }
                return 0;
            }
        }
    }
    Console.WriteLine($"no template matching '{want}'");
    return 0;
}

if (arg == "congrep" && args.Length >= 3)
{
    // Scan every .con in an archive for a keyword; print matching lines in their ObjectTemplate context.
    var arc = RefractorForge.Formats.Rfa.RfaArchive.Open(args[1]);
    string kw = args[2].ToLowerInvariant();
    int max = args.Length >= 4 && int.TryParse(args[3], out var mx) ? mx : 80;
    int hits = 0;
    foreach (var e in arc.Entries)
    {
        var name = e.Name.Replace('\\', '/');
        if (!name.EndsWith(".con", StringComparison.OrdinalIgnoreCase)) continue;
        string text; try { text = System.Text.Encoding.Latin1.GetString(arc.Read(e)); } catch { continue; }
        if (text.ToLowerInvariant().IndexOf(kw, StringComparison.Ordinal) < 0) continue;
        string tpl = "";
        foreach (var raw in text.Split('\n'))
        {
            var l = raw.Replace("\r", "").Trim();
            if (l.StartsWith("ObjectTemplate.create", StringComparison.OrdinalIgnoreCase)) tpl = l.Substring("ObjectTemplate.create".Length).Trim();
            if (l.Length > 0 && !l.StartsWith("rem", StringComparison.OrdinalIgnoreCase) && l.ToLowerInvariant().Contains(kw))
            {
                Console.WriteLine($"  [{Path.GetFileName(name)}] ({tpl}) {l}");
                if (++hits >= max) { Console.WriteLine($"... ({max} cap)"); return 0; }
            }
        }
    }
    Console.WriteLine($"{hits} hits for '{kw}' in {Path.GetFileName(args[1])}");
    return 0;
}

if (arg == "smcol" && args.Length >= 2)
{
    // RE probe: survey/dump the .sm COLLISION mesh sections (opaque to our reader). With no mesh name, lists the
    // meshes that have collision + section sizes + geometry counts (to pick a small sample). With a name, dumps
    // each collision section as u32 / f32 / hex so the structure can be worked out.
    var arc = RefractorForge.Formats.Rfa.RfaArchive.Open(args[1]);
    var smEntries = arc.Entries.Where(e => e.Name.EndsWith(".sm", StringComparison.OrdinalIgnoreCase)).ToList();
    string? want = args.Length >= 3 ? args[2].ToLowerInvariant() : null;

    if (want is null)
    {
        Console.WriteLine($"{smEntries.Count} .sm entries; scanning for collision...");
        int withCol = 0, secTotal = 0, secOk = 0, rtOk = 0;
        foreach (var e in smEntries)
        {
            if (!StandardMesh.TryParse(arc.Read(e), out var sm) || sm is null || sm.NumCollisionMeshes == 0) continue;
            withCol++;
            foreach (var sec in sm.CollisionSections)
            {
                secTotal++;
                if (StandardMesh.TryParseCollision(sec, out _, out var ti) && ti.Length > 0) secOk++;
                if (StandardMesh.TryParseCollisionFull(sec, out var full) && StandardMeshWriter.WriteCollisionSection(full).AsSpan().SequenceEqual((ReadOnlySpan<byte>)sec)) rtOk++;
            }
            if (withCol <= 30)
            {
                var (mc, vc, fc) = sm.Lod0Counts();
                bool ok0 = StandardMesh.TryParseCollision(sm.CollisionSections[0], out var cv, out var cti);
                Console.WriteLine($"  {Path.GetFileName(e.Name.Replace('\\', '/')),-32} col={sm.NumCollisionMeshes}  [0] {(ok0 ? $"{cv.Length}v/{cti.Length / 3}t" : "PARSE-FAIL")}  lod0 {vc}v/{fc}f");
            }
        }
        Console.WriteLine($"{withCol}/{smEntries.Count} meshes have collision; PARSED {secOk}/{secTotal}; ROUND-TRIP byte-exact {rtOk}/{secTotal}.");
        return 0;
    }

    var entry = smEntries.FirstOrDefault(e => Path.GetFileName(e.Name.Replace('\\', '/')).ToLowerInvariant().Contains(want));
    if (entry is null) { Console.WriteLine($"no .sm matching '{want}'"); return 1; }
    var sm2 = StandardMesh.Parse(arc.Read(entry));
    var (mm, vv, ff) = sm2.Lod0Counts();
    Console.WriteLine($"{Path.GetFileName(entry.Name.Replace('\\', '/'))}: v{sm2.Version}, {sm2.NumCollisionMeshes} col mesh(es), lod0 {mm}mat/{vv}v/{ff}f");
    Console.WriteLine($"  bbox: [{string.Join(" ", sm2.BoundingBox.Select(x => x.ToString("0.##")))}]");
    int maxRows = args.Length >= 4 && int.TryParse(args[3], out var mr) ? mr : 64;
    for (int ci = 0; ci < sm2.CollisionSections.Count; ci++)
    {
        var s = sm2.CollisionSections[ci];
        Console.WriteLine($"\n=== collision[{ci}]: {s.Length} bytes ({s.Length / 4} words) ===");
        // ASCII strings (>=3 printable) reveal named sub-structures (DShape buffers etc.).
        var cur = new System.Text.StringBuilder();
        for (int i = 0; i <= s.Length; i++)
        {
            if (i < s.Length && s[i] >= 32 && s[i] < 127) cur.Append((char)s[i]);
            else { if (cur.Length >= 3) Console.WriteLine($"  str@{i - cur.Length}: \"{cur}\""); cur.Clear(); }
        }
        Console.WriteLine("   off        u32       hex          f32      u16,u16");
        for (int i = 0, row = 0; i + 4 <= s.Length && row < maxRows; i += 4, row++)
        {
            uint uval = BitConverter.ToUInt32(s, i);
            float fval = BitConverter.ToSingle(s, i);
            ushort lo = BitConverter.ToUInt16(s, i), hi = BitConverter.ToUInt16(s, i + 2);
            Console.WriteLine($"  [{i,4}] {uval,11} 0x{uval:X8} {fval,13:0.#####}   {lo,5},{hi,5}");
        }
    }
    return 0;
}

if (arg == "sculptmodes")
{
    // Exercise every sculpt mode + falloff the editor now exposes (Raise/Lower/Flatten/Set;
    // Smooth/Linear/Constant/Gaussian) against the pure TerrainEditor — the model the UI drives.
    int fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }
    var cfg = new TerrainConfig { MaterialSize = 128, WorldSize = 512, YScale = 0.5f };  // 4 m / cell
    int cc = 64;                                   // centre cell
    float cx = cc * cfg.HorizontalSpacing, cz = cc * cfg.HorizontalSpacing;
    Heightmap Flat50() => HeightmapGenerator.Flat(cfg.MaterialSize, cfg.MetersToRaw(50f));
    float HM(Heightmap h) => cfg.HeightToMeters(h[cc, cc]);

    var hRaise = Flat50(); new TerrainEditor(hRaise, cfg).Stamp(cx, cz, new TerrainBrush(BrushMode.Raise, 40f, 5f, BrushFalloff.Smooth));
    Check(HM(hRaise) > 53f, $"Raise lifts centre ~+5 m (got {HM(hRaise):0.0})");

    var hLower = Flat50(); new TerrainEditor(hLower, cfg).Stamp(cx, cz, new TerrainBrush(BrushMode.Lower, 40f, 5f, BrushFalloff.Smooth));
    Check(HM(hLower) < 47f, $"Lower drops centre ~-5 m (got {HM(hLower):0.0})");

    var hSet = Flat50(); new TerrainEditor(hSet, cfg).Stamp(cx, cz, new TerrainBrush(BrushMode.Set, 40f, 1f, BrushFalloff.Smooth, 10f));
    Check(MathF.Abs(HM(hSet) - 10f) < 0.5f, $"Set forces centre to target 10 m (got {HM(hSet):0.0})");

    var hFlat = Flat50(); new TerrainEditor(hFlat, cfg).Stamp(cx, cz, new TerrainBrush(BrushMode.Flatten, 40f, 0.5f, BrushFalloff.Smooth, 30f));
    Check(HM(hFlat) > 36f && HM(hFlat) < 44f, $"Flatten eases centre 50->~40 toward 30 (got {HM(hFlat):0.0})");

    // Falloff contrast: Set target 0 over flat 50; sample a cell at 0.9 r. Constant zeroes it; Gaussian barely touches it.
    int edge = cc + (int)(0.9f * 40f / cfg.HorizontalSpacing);
    var hC = Flat50(); new TerrainEditor(hC, cfg).Stamp(cx, cz, new TerrainBrush(BrushMode.Set, 40f, 1f, BrushFalloff.Constant, 0f));
    var hG = Flat50(); new TerrainEditor(hG, cfg).Stamp(cx, cz, new TerrainBrush(BrushMode.Set, 40f, 1f, BrushFalloff.Gaussian, 0f));
    float ce = cfg.HeightToMeters(hC[edge, cc]), ge = cfg.HeightToMeters(hG[edge, cc]);
    Check(ce < 5f, $"Constant falloff zeroes edge cell (got {ce:0.0} m)");
    Check(ge > 40f, $"Gaussian falloff leaves edge cell high (got {ge:0.0} m)");
    Check(ce < ge - 20f, $"Constant vs Gaussian clearly differ at edge ({ce:0.0} vs {ge:0.0} m)");

    Console.WriteLine(fails == 0 ? "SCULPT MODE TESTS PASSED" : $"SCULPT MODE TESTS FAILED ({fails})");
    return fails == 0 ? 0 : 1;
}

if (arg == "brushshape")
{
    // Bitmap brush shapes (Battlecraft brushes\*.bmp): decode the real masks + check the mask path in
    // TerrainStroke.Dab affects the whole footprint, not just the inscribed circle.
    int fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }

    string bdir = args.Length >= 2 ? args[1]
        : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "RefractorForge.Viewer", "Assets", "brushes"));
    Console.WriteLine($"brushes dir: {bdir} (exists={Directory.Exists(bdir)})");

    if (File.Exists(Path.Combine(bdir, "Round.bmp")))
    {
        var round = BrushMask.FromBmp(Path.Combine(bdir, "Round.bmp"));
        Check(round.Sample(0.5f, 0.5f) > 0.9f, $"Round centre full weight (got {round.Sample(0.5f, 0.5f):0.00})");
        Check(round.Sample(0.02f, 0.02f) < 0.1f, $"Round corner ~zero (got {round.Sample(0.02f, 0.02f):0.00})");
        var square = BrushMask.FromBmp(Path.Combine(bdir, "Square.bmp"));
        Check(square.Sample(0.12f, 0.12f) > 0.5f, $"Square corner solid (got {square.Sample(0.12f, 0.12f):0.00})");
    }
    else Console.WriteLine("  (skipped real-BMP checks: bundled brushes not found)");

    var cfg = new TerrainConfig { MaterialSize = 128, WorldSize = 512, YScale = 0.5f };  // 4 m / cell
    int cc = 64; float cx = cc * cfg.HorizontalSpacing, cz = cc * cfg.HorizontalSpacing;
    var solid = new float[16]; Array.Fill(solid, 1f);
    var solidMask = new BrushMask("solid", 4, solid);
    int corner = cc + (int)(40f / cfg.HorizontalSpacing) - 1;   // ~box edge, beyond the inscribed circle

    var hMask = HeightmapGenerator.Flat(cfg.MaterialSize, cfg.MetersToRaw(50f));
    new TerrainEditor(hMask, cfg).Stamp(cx, cz, new TerrainBrush(BrushMode.Set, 40f, 1f, BrushFalloff.Smooth, 0f, solidMask));
    var hRad = HeightmapGenerator.Flat(cfg.MaterialSize, cfg.MetersToRaw(50f));
    new TerrainEditor(hRad, cfg).Stamp(cx, cz, new TerrainBrush(BrushMode.Set, 40f, 1f, BrushFalloff.Constant, 0f));
    float maskCorner = cfg.HeightToMeters(hMask[corner, corner]), radCorner = cfg.HeightToMeters(hRad[corner, corner]);
    Check(maskCorner < 5f, $"square mask reaches box corner (got {maskCorner:0.0} m)");
    Check(radCorner > 45f, $"radial brush leaves box corner untouched (got {radCorner:0.0} m)");

    // Procedural square (no bitmap): Chebyshev metric reaches the box corner that the radial disc misses.
    var hSq = HeightmapGenerator.Flat(cfg.MaterialSize, cfg.MetersToRaw(50f));
    new TerrainEditor(hSq, cfg).Stamp(cx, cz, new TerrainBrush(BrushMode.Set, 40f, 1f, BrushFalloff.Constant, 0f, null, Square: true));
    Check(cfg.HeightToMeters(hSq[corner, corner]) < 5f, $"procedural square reaches box corner (got {cfg.HeightToMeters(hSq[corner, corner]):0.0} m)");

    // Square RAISE under a soft (Gaussian) falloff must still read as a square: a flat-topped square (centre ==
    // an inner-square diagonal cell), NOT a round peak. (Radial Gaussian peaks at centre and barely lifts the
    // same diagonal cell.) cc=64, 10-cell radius => the flat top covers Chebyshev <= 7 cells; (69,69) is 5 in.
    var hSqG = HeightmapGenerator.Flat(cfg.MaterialSize, cfg.MetersToRaw(50f));
    new TerrainEditor(hSqG, cfg).Stamp(cx, cz, new TerrainBrush(BrushMode.Raise, 40f, 10f, BrushFalloff.Gaussian, null, null, Square: true));
    var hRadG = HeightmapGenerator.Flat(cfg.MaterialSize, cfg.MetersToRaw(50f));
    new TerrainEditor(hRadG, cfg).Stamp(cx, cz, new TerrainBrush(BrushMode.Raise, 40f, 10f, BrushFalloff.Gaussian, null, null, Square: false));
    float sqCentre = cfg.HeightToMeters(hSqG[64, 64]), sqDiag = cfg.HeightToMeters(hSqG[69, 69]);
    float radDiag = cfg.HeightToMeters(hRadG[69, 69]);
    Check(Math.Abs(sqCentre - sqDiag) < 1f && sqDiag > 58f, $"square Gaussian raise is FLAT-TOPPED (centre {sqCentre:0.0} ~= diagonal {sqDiag:0.0} m)");
    Check(radDiag < 53f, $"radial Gaussian peaks at centre, barely lifts the diagonal (got {radDiag:0.0} m)");

    // Same square metric for the material painter: corner painted by the square, left alone by the radial.
    var mSq = new MaterialMap(cfg.MaterialSize, cfg.MaterialSize);
    new MaterialPainter(mSq, cfg).Stamp(cx, cz, new MaterialBrush(7, 40f, 1f, BrushFalloff.Constant, null, Square: true));
    var mRad = new MaterialMap(cfg.MaterialSize, cfg.MaterialSize);
    new MaterialPainter(mRad, cfg).Stamp(cx, cz, new MaterialBrush(7, 40f, 1f, BrushFalloff.Constant));
    Check(mSq[corner, corner] == 7, "material square paints the box corner");
    Check(mRad[corner, corner] == 0, "material radial leaves the box corner");

    Console.WriteLine(fails == 0 ? "BRUSH SHAPE TESTS PASSED" : $"BRUSH SHAPE TESTS FAILED ({fails})");
    return fails == 0 ? 0 : 1;
}

if (arg == "texpaint")
{
    // Terrain TEXTURE paint save pipeline: the surface-texture BMP loader, and atlas -> txCxR.dds tile split
    // (with an uncompressed-DDS round-trip). The brush math itself is Viewer-side (click-tested). `texpaint [lvl]`.
    int fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }

    string tdir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "RefractorForge.Viewer", "Assets", "textures"));
    var bmpPath = Path.Combine(tdir, "surf01.bmp");
    if (File.Exists(bmpPath))
    {
        var tx = RefractorForge.Render.Texture2D.LoadBmp(bmpPath);   // 8-bit paletted clean surface, 256x256
        Check(tx is not null && tx.Width == 256 && tx.Height == 256, $"LoadBmp surf01 (8-bit) 256x256 (got {tx?.Width}x{tx?.Height})");
    }
    else Console.WriteLine("  (skipped LoadBmp: bundled surface textures not found)");

    if (args.Length >= 2)
    {
        string lvl = args[1];
        var terr = Directory.EnumerateFiles(lvl, "Terrain.con", SearchOption.AllDirectories).FirstOrDefault();
        var texDir = Directory.EnumerateDirectories(lvl, "Textures", SearchOption.AllDirectories).FirstOrDefault();
        if (terr is not null && texDir is not null)
        {
            var tcfg = TerrainConfig.Load(terr);
            var tt = RefractorForge.Render.TerrainTexture.Load(texDir, tcfg.WorldSize);
            Check(tt is not null, "loaded terrain tiles");
            if (tt is not null)
            {
                var atlas = tt.BakeAtlas(2048);
                int N = atlas.Width;                              // paint a red block into the atlas
                for (int y = 400; y < 700; y++)
                    for (int x = 400; x < 700; x++) { int i = (y * N + x) * 4; atlas.Rgba[i] = 255; atlas.Rgba[i + 1] = 0; atlas.Rgba[i + 2] = 0; atlas.Rgba[i + 3] = 255; }
                var tiles = tt.SplitToTiles(atlas).ToList();
                Check(tiles.Count > 0, $"atlas split into {tiles.Count} tile(s)");
                Check(tiles.All(t => System.Text.RegularExpressions.Regex.IsMatch(t.fileName, @"^tx\d+x\d+\.dds$")), "tile names are txCxR.dds");
                var enc = RefractorForge.Render.DdsTexture.EncodeUncompressed(tiles[0].tile);
                var dec = RefractorForge.Render.DdsTexture.Decode(enc);
                Check(dec.Width == tiles[0].tile.Width && dec.Height == tiles[0].tile.Height, "tile DDS round-trips dimensions");
                bool red = false;
                foreach (var (_, tile) in tiles)
                { for (int i = 0; i < tile.Rgba.Length; i += 4) if (tile.Rgba[i] > 200 && tile.Rgba[i + 1] < 60 && tile.Rgba[i + 2] < 60) { red = true; break; } if (red) break; }
                Check(red, "painted region survives the atlas -> tile bake");
            }
        }
        else Console.WriteLine("  (skipped level round-trip: no Terrain.con / Textures dir)");
    }

    Console.WriteLine(fails == 0 ? "TEXPAINT TESTS PASSED" : $"TEXPAINT TESTS FAILED ({fails})");
    return fails == 0 ? 0 : 1;
}

if (arg == "cpedit")
{
    // Edit-Control-Point round-trip: parse the new template fields (team / areaValue / conversionTime /
    // controlPointName), surgically patch them back (geometry preserved), and sync them via GameplaySync.
    int fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }
    string tmpl =
        "ObjectTemplate.create ControlPoint us_base\r\n" +
        "ObjectTemplate.controlPointName OI_base1\r\n" +
        "ObjectTemplate.radius 40\r\n" +
        "ObjectTemplate.team 2\r\n" +
        "ObjectTemplate.spawnGroupId 1\r\n" +
        "ObjectTemplate.areaValue 25\r\n" +
        "ObjectTemplate.conversionTime 90\r\n" +
        "ObjectTemplate.geometry USflagbase_m1\r\n";
    string pts = "Object.create us_base\r\nObject.absolutePosition 841.57/35.14/528.64\r\n";

    var cps = RefractorForge.Formats.Con.GameplayObjects.ParseControlPoints(pts.Split('\n'), tmpl.Split('\n'));
    var cp = cps[0];
    Check(cp.Team == 2 && cp.AreaValue == 25 && cp.ConversionTime == 90 && cp.ControlPointName == "OI_base1",
          $"parsed CP fields (team {cp.Team}, area {cp.AreaValue}, conv {cp.ConversionTime}, cpName {cp.ControlPointName})");

    var edited = cp with { Team = 1, AreaValue = 50, ConversionTime = 120, Radius = 35f, ControlPointName = "OI_alt" };
    var patched = RefractorForge.Formats.Con.GameplayWriter.PatchControlPointRadii(tmpl.Split('\n'), new[] { edited });
    Check(patched.Contains("ObjectTemplate.team 1"), "patched team");
    Check(patched.Contains("ObjectTemplate.areaValue 50"), "patched areaValue");
    Check(patched.Contains("ObjectTemplate.conversionTime 120"), "patched conversionTime");
    Check(patched.Contains("ObjectTemplate.radius 35"), "patched radius");
    Check(patched.Contains("ObjectTemplate.controlPointName OI_alt"), "patched controlPointName");
    Check(patched.Contains("ObjectTemplate.geometry USflagbase_m1"), "geometry preserved verbatim");
    Check(!patched.Contains("ObjectTemplate.team 2"), "old team value replaced");

    var gp = new RefractorForge.Formats.Con.EditableGameplay(new RefractorForge.Formats.Con.GameplayObjects(
        new[] { edited }, System.Array.Empty<RefractorForge.Formats.Con.VehicleSpawnDef>(), System.Array.Empty<RefractorForge.Formats.Con.SoldierSpawnDef>()));
    var (rt, _, _) = RefractorForge.Formats.Con.GameplaySync.Parse(RefractorForge.Formats.Con.GameplaySync.Serialize(gp));
    Check(rt.Count == 1 && rt[0].Team == 1 && rt[0].AreaValue == 50 && rt[0].ConversionTime == 120 && rt[0].ControlPointName == "OI_alt",
          "GameplaySync round-trips the CP fields");

    Console.WriteLine(fails == 0 ? "CP EDIT TESTS PASSED" : $"CP EDIT TESTS FAILED ({fails})");
    return fails == 0 ? 0 : 1;
}

if (arg == "prefab")
{
    // Object-group prefabs: capture a selection, re-base to a shared origin, round-trip the text format.
    int fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }
    bool Near(float a, float b) => MathF.Abs(a - b) < 1e-3f;

    var src = StaticObjectsFile.Parse(new[]
    {
        "object.create o_tent",   "object.absolutePosition 100/10/200", "object.rotation 90/0/0",
        "object.create o_wall",   "object.absolutePosition 110/12/205", "object.rotation 0/0/0",
        "object.create o_bunker", "object.absolutePosition 90/14/195",  "object.rotation 180/0/0",
    });
    Check(src.Objects.Count == 3, $"3 source objects ({src.Objects.Count})");

    var pf = Prefab.FromObjects("Test Camp", src.Objects);
    Check(pf.Members.Count == 3, $"3 members ({pf.Members.Count})");
    Check(Near(pf.Members[0].Offset.X, 0f) && Near(pf.Members[0].Offset.Z, 0f), $"tent at XZ centroid (got {pf.Members[0].Offset})");
    Check(Near(pf.Members[0].Offset.Y, 0f), $"lowest object Y offset 0 (got {pf.Members[0].Offset.Y})");
    Check(Near(pf.Members[1].Offset.Y, 2f), $"wall Y offset +2 (got {pf.Members[1].Offset.Y})");

    var tmp = Path.Combine(Path.GetTempPath(), "rf_prefab_test.rfprefab");
    pf.Save(tmp);
    var rl = Prefab.Load(tmp);
    Check(rl.Name == "Test Camp", $"name round-trip ({rl.Name})");
    bool same = rl.Members.Count == 3;
    for (int i = 0; i < rl.Members.Count && same; i++)
    {
        var a = pf.Members[i]; var b = rl.Members[i];
        if (a.Template != b.Template || !Near(a.Offset.X, b.Offset.X) || !Near(a.Offset.Y, b.Offset.Y)
            || !Near(a.Offset.Z, b.Offset.Z) || !Near(a.Rotation.X, b.Rotation.X)) same = false;
    }
    Check(same, "member transforms round-trip");

    Console.WriteLine(fails == 0 ? "PREFAB TESTS PASSED" : $"PREFAB TESTS FAILED ({fails})");
    return fails == 0 ? 0 : 1;
}

if (arg == "collabtcp")
{
    // Real-socket collaboration: a TCP relay host + two joiners; verify sync, live add/move, convergence.
    int fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }
    bool WaitFor(Func<bool> cond, int ms = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; System.Threading.Thread.Sleep(20); }
        return cond();
    }

    var seed = new StaticObjectsFile();
    seed.Objects.Add(new StaticObject("o_seed") { Id = "seed1", Position = new Vec3(10, 0, 20) });
    var relay = new RefractorForge.Collab.RelayServer(seed);
    var host = new RefractorForge.Collab.TcpRelayHost(relay, System.Net.IPAddress.Loopback, 0);
    host.Start();
    int port = host.Port;
    Console.WriteLine($"relay on 127.0.0.1:{port}");

    var connA = new RefractorForge.Collab.TcpClientConnection("127.0.0.1", port);
    var A = new RefractorForge.Collab.CollabClient("A", "Alice", connA); connA.Attach(A);
    var connB = new RefractorForge.Collab.TcpClientConnection("127.0.0.1", port);
    var B = new RefractorForge.Collab.CollabClient("B", "Bob", connB); connB.Attach(B);

    Check(WaitFor(() => A.Ready && B.Ready), "both clients synced over TCP");
    Check(WaitFor(() => A.Doc.Objects.Count == 1 && B.Doc.Objects.Count == 1),
          $"both received the seed object (A={A.Doc.Objects.Count}, B={B.Doc.Objects.Count})");

    string newId = A.Add("o_added", new Vec3(50, 5, 60), Vec3.Zero);
    Check(WaitFor(() => B.Doc.FindById(newId) is not null), "B sees A's newly added object");

    B.Move("seed1", new Vec3(99, 1, 99));
    Check(WaitFor(() => A.Doc.FindById("seed1") is { } o && MathF.Abs(o.Position.X - 99f) < 0.01f), "A sees B's move of the seed");

    Check(WaitFor(() => A.Doc.Objects.Count == 2 && B.Doc.Objects.Count == 2), $"converged at 2 objects (A={A.Doc.Objects.Count}, B={B.Doc.Objects.Count})");

    connA.Dispose(); connB.Dispose(); host.Stop();
    Console.WriteLine(fails == 0 ? "COLLAB TCP TESTS PASSED" : $"COLLAB TCP TESTS FAILED ({fails})");
    return fails == 0 ? 0 : 1;
}

if (arg == "collabadmin")
{
    // Relay admin hardening over real sockets: password auth, kick, and reconnect-identity (a dying old
    // socket must not evict a client that already reconnected under the same id).
    int fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }
    bool WaitFor(Func<bool> cond, int ms = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; System.Threading.Thread.Sleep(20); }
        return cond();
    }
    (System.Net.Sockets.TcpClient, StreamReader, StreamWriter) Open(int port)
    {
        var c = new System.Net.Sockets.TcpClient(); c.Connect("127.0.0.1", port); c.ReceiveTimeout = 3000;
        var s = c.GetStream();
        return (c, new StreamReader(s, System.Text.Encoding.UTF8),
                   new StreamWriter(s, new System.Text.UTF8Encoding(false)) { AutoFlush = true });
    }

    var relay = new RefractorForge.Collab.RelayServer(null, null, "sekret");
    var host = new RefractorForge.Collab.TcpRelayHost(relay, System.Net.IPAddress.Loopback, 0);
    host.Start();
    int port = host.Port;
    Console.WriteLine($"password relay on 127.0.0.1:{port}");

    // 1. JOIN without AUTH is rejected (ERROR + close), and the client never registers.
    var (c1, r1, w1) = Open(port);
    w1.WriteLine(RefractorForge.Collab.Message.Join("nope", "NoAuth").Encode());
    string? line1 = null; try { line1 = r1.ReadLine(); } catch { }
    Check(line1 is not null && line1.StartsWith("ERROR"), $"unauthed JOIN rejected (got '{line1}')");
    Check(WaitFor(() => relay.ClientCount == 0), "rejected client not registered");
    try { c1.Close(); } catch { }

    // 2. AUTH then JOIN is accepted.
    var (c2, r2, w2) = Open(port);
    w2.WriteLine(RefractorForge.Collab.Message.Auth("sekret").Encode());
    w2.WriteLine(RefractorForge.Collab.Message.Join("good", "Authed").Encode());
    Check(WaitFor(() => relay.ClientCount == 1), "authed client registered");

    // 3. Kick by name removes it from the roster.
    var kicked = relay.Kick("Authed");
    Check(kicked == "Authed", $"kick matched and returned the name (got '{kicked ?? "null"}')");
    Check(WaitFor(() => relay.ClientCount == 0), "kicked client removed from roster");
    try { c2.Close(); } catch { }

    // 4. Reconnect identity: same id on a fresh socket survives the old socket's teardown.
    var (c3, r3, w3) = Open(port);
    w3.WriteLine(RefractorForge.Collab.Message.Auth("sekret").Encode());
    w3.WriteLine(RefractorForge.Collab.Message.Join("dup", "First").Encode());
    Check(WaitFor(() => relay.ClientList().Any(x => x.Id == "dup")), "first 'dup' registered");
    var (c4, r4, w4) = Open(port);
    w4.WriteLine(RefractorForge.Collab.Message.Auth("sekret").Encode());
    w4.WriteLine(RefractorForge.Collab.Message.Join("dup", "Second").Encode());
    System.Threading.Thread.Sleep(250);
    try { c3.Close(); } catch { }                  // old socket dies -> DisconnectIf must be a no-op
    System.Threading.Thread.Sleep(500);
    Check(relay.ClientList().Any(x => x.Id == "dup"), "reconnected 'dup' survives the old socket teardown");

    try { c4.Close(); } catch { } host.Stop();
    Console.WriteLine(fails == 0 ? "COLLAB ADMIN TESTS PASSED" : $"COLLAB ADMIN TESTS FAILED ({fails})");
    return fails == 0 ? 0 : 1;
}

if (arg == "collabsync")
{
    // The terrain/material rect serialization used to sync sculpt + paint strokes between editors.
    int fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }
    int x0 = 10, y0 = 12, w = 8, h = 6;

    // --- terrain height rect (16-bit LE) round-trip into a fresh map ---
    var hmA = new Heightmap(64, 64);
    for (int i = 0; i < hmA.Samples.Length; i++) hmA.Samples[i] = (ushort)((i * 7) % 60000);
    var tbuf = new byte[w * h * 2];
    for (int yy = 0; yy < h; yy++) for (int xx = 0; xx < w; xx++)
        { ushort val = hmA[x0 + xx, y0 + yy]; int o = (yy * w + xx) * 2; tbuf[o] = (byte)val; tbuf[o + 1] = (byte)(val >> 8); }
    var tdec = Convert.FromBase64String(Convert.ToBase64String(tbuf));
    var hmB = new Heightmap(64, 64);
    for (int yy = 0; yy < h; yy++) for (int xx = 0; xx < w; xx++)
        { int o = (yy * w + xx) * 2; hmB[x0 + xx, y0 + yy] = (ushort)(tdec[o] | (tdec[o + 1] << 8)); }
    bool tok = true;
    for (int yy = 0; yy < h && tok; yy++) for (int xx = 0; xx < w; xx++) if (hmB[x0 + xx, y0 + yy] != hmA[x0 + xx, y0 + yy]) { tok = false; break; }
    Check(tok, "terrain rect round-trips byte-exact");
    Check(hmB[0, 0] == 0 && hmB[63, 63] == 0, "cells outside the rect are untouched");

    // --- material rect (1 byte/cell) round-trip ---
    var mA = new MaterialMap(64, 64);
    for (int i = 0; i < mA.Samples.Length; i++) mA.Samples[i] = (byte)(i % 16);
    var mbuf = new byte[w * h];
    for (int yy = 0; yy < h; yy++) for (int xx = 0; xx < w; xx++) mbuf[yy * w + xx] = mA[x0 + xx, y0 + yy];
    var mdec = Convert.FromBase64String(Convert.ToBase64String(mbuf));
    var mB = new MaterialMap(64, 64);
    for (int yy = 0; yy < h; yy++) for (int xx = 0; xx < w; xx++) mB[x0 + xx, y0 + yy] = mdec[yy * w + xx];
    bool mok = true;
    for (int yy = 0; yy < h && mok; yy++) for (int xx = 0; xx < w; xx++) if (mB[x0 + xx, y0 + yy] != mA[x0 + xx, y0 + yy]) { mok = false; break; }
    Check(mok, "material rect round-trips byte-exact");

    // --- water level op: applied to the canonical world state + snapshotted to late joiners ---
    var wstate = new RefractorForge.Formats.Con.CollabWorldState();
    bool wrec = wstate.ApplyOp("WATER 42.5");
    Check(wrec && wstate.Water == 42.5f, $"WATER op applied to world state (Water={wstate.Water})");
    Check(wstate.SnapshotOps().Any(s => s == "WATER 42.5"), "world state snapshots WATER for late joiners");
    var wsp = "WATER 17.25".Split(' ');
    Check(wsp.Length >= 2 && float.TryParse(wsp[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pwl) && pwl == 17.25f, "editor parses WATER op value (invariant culture)");

    // --- overgrowth-settings op: stored, snapshotted, and survives a save/reload (relay seeds late joiners) ---
    Check(wstate.ApplyOp("OVERGROWTH 1 12.5 1.25"), "OVERGROWTH op recognised by world state");
    Check(wstate.Overgrowth == "OVERGROWTH 1 12.5 1.25", "OVERGROWTH op stored verbatim");
    Check(wstate.SnapshotOps().Any(s => s == "OVERGROWTH 1 12.5 1.25"), "world state snapshots OVERGROWTH for late joiners");

    // --- imported .obj mesh op: keyed by name, snapshotted, replaced on re-import ---
    Check(wstate.ApplyOp("OBJMESH mybunker QUJD"), "OBJMESH op recognised by world state");
    Check(wstate.ObjMeshes.TryGetValue("mybunker", out var omv) && omv == "OBJMESH mybunker QUJD", "OBJMESH stored by name");
    wstate.ApplyOp("OBJMESH mybunker Wlla");   // re-import same name replaces
    Check(wstate.ObjMeshes["mybunker"] == "OBJMESH mybunker Wlla" && wstate.ObjMeshes.Count == 1, "re-import replaces the same-named mesh");
    Check(wstate.SnapshotOps().Any(s => s == "OBJMESH mybunker Wlla"), "world state snapshots OBJMESH for late joiners");

    // Save -> reload: overgrowth + obj meshes persist across a relay restart.
    var stateDir = Path.Combine(Path.GetTempPath(), "rf_collabsync_" + Guid.NewGuid().ToString("N")[..8]);
    try
    {
        wstate.Save(stateDir);
        var reloaded = RefractorForge.Formats.Con.CollabWorldState.Load(stateDir);
        Check(reloaded is not null && reloaded.Overgrowth == "OVERGROWTH 1 12.5 1.25", "OVERGROWTH persists across restart");
        Check(reloaded is not null && reloaded.ObjMeshes.TryGetValue("mybunker", out var rv) && rv == "OBJMESH mybunker Wlla", "OBJMESH persists across restart");
    }
    finally { try { Directory.Delete(stateDir, true); } catch { } }

    // Presence wire now carries a heading token (diamond look-direction); parses back invariant.
    var pres = RefractorForge.Collab.Message.Presence("u1", "Bob", "-", new Vec3(1, 2, 3), 1.5708f).Encode();
    var redec = RefractorForge.Collab.Message.Decode(pres);
    Check(redec.Args.Length >= 5 && float.TryParse(redec.Args[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ph) && MathF.Abs(ph - 1.5708f) < 1e-3f, "PRESENCE heading round-trips");

    Console.WriteLine(fails == 0 ? "COLLAB SYNC TESTS PASSED" : $"COLLAB SYNC TESTS FAILED ({fails})");
    return fails == 0 ? 0 : 1;
}

if (arg == "collabgp")
{
    // Full-state gameplay (control points / vehicle spawns / soldier spawns) serialization for collab sync.
    int fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }
    bool Near(float a, float b) => MathF.Abs(a - b) < 1e-3f;

    var gp = new EditableGameplay(GameplayObjects.Empty);
    gp.ControlPoints.Add(new ControlPointDef("US_base", new Vec3(100, 5, 200), 30f, 1));
    gp.ControlPoints.Add(new ControlPointDef("NVA_base", new Vec3(800, 6, 900), 25f, 2));
    gp.VehicleSpawns.Add(new VehicleSpawnDef("Spawner", new Vec3(120, 5, 210), new Vec3(90, 0, 0), "M48Patton", 1));
    gp.SoldierSpawns.Add(new SoldierSpawnDef("sp1", new Vec3(105, 5, 205), new Vec3(45, 0, 0)));

    var wire = GameplaySync.Serialize(gp);
    var gp2 = new EditableGameplay(GameplayObjects.Empty);
    GameplaySync.Apply(gp2, wire);

    Check(gp2.ControlPoints.Count == 2, $"2 control points ({gp2.ControlPoints.Count})");
    Check(gp2.VehicleSpawns.Count == 1 && gp2.VehicleSpawns[0].Vehicle == "M48Patton",
          $"vehicle spawn round-trips ({(gp2.VehicleSpawns.Count > 0 ? gp2.VehicleSpawns[0].Vehicle : "none")})");
    Check(gp2.SoldierSpawns.Count == 1, $"1 soldier spawn ({gp2.SoldierSpawns.Count})");
    Check(Near(gp2.ControlPoints[1].Radius, 25f) && gp2.ControlPoints[1].Name == "NVA_base", "CP radius + name round-trip");
    Check(Near(gp2.VehicleSpawns[0].Rotation.X, 90f), "vehicle spawn rotation round-trip");

    gp2.ControlPoints.Add(new ControlPointDef("stale", Vec3.Zero, 1f, 0));
    GameplaySync.Apply(gp2, wire);    // full-state re-apply must replace, not accumulate
    Check(gp2.ControlPoints.Count == 2, $"re-apply replaces (no leftover stale) ({gp2.ControlPoints.Count})");

    Console.WriteLine(fails == 0 ? "COLLAB GAMEPLAY TESTS PASSED" : $"COLLAB GAMEPLAY TESTS FAILED ({fails})");
    return fails == 0 ? 0 : 1;
}

if (arg == "rfamerge")
{
    // Multi-.rfa merge: load a base + a patch archive; the patch's same-named files must override the base.
    int fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }
    var tmp = Path.Combine(Path.GetTempPath(), "rf_rfamerge");
    if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    var baseDir = Path.Combine(tmp, "base");
    var cfg = new TerrainConfig { MaterialSize = 128, WorldSize = 512, YScale = 0.5f };
    LevelSaver.CreateNewLevel(baseDir, "MergeTest", cfg, HeightmapGenerator.Flat(128, cfg.MetersToRaw(20f)), new EnvironmentSettings());
    var baseRfa = Path.Combine(tmp, "base.rfa"); LevelSaver.PackFolder(baseDir, baseRfa);

    var patchDir = Path.Combine(tmp, "patch"); Directory.CreateDirectory(patchDir);
    File.WriteAllText(Path.Combine(patchDir, "StaticObjects.con"),
        "object.create o_patchtest\nobject.absolutePosition 10/5/20\nobject.rotation 0/0/0\n");
    var patchRfa = Path.Combine(tmp, "patch.rfa"); LevelSaver.PackFolder(patchDir, patchRfa);

    var single = LevelArchive.FromRfa(baseRfa);
    Check(single.StaticObjects.Objects.Count == 0, $"base alone: 0 static objects (got {single.StaticObjects.Objects.Count})");
    var merged = LevelArchive.FromRfa(baseRfa, patchRfa);
    Check(merged.StaticObjects.Objects.Count == 1 && merged.StaticObjects.Objects[0].Template == "o_patchtest",
          $"patch overrides StaticObjects.con (got {merged.StaticObjects.Objects.Count} objects)");
    Check(merged.Config.MaterialSize == 128 && merged.Config.WorldSize == 512, "terrain still loads from base archive");

    Console.WriteLine(fails == 0 ? "RFA MERGE TESTS PASSED" : $"RFA MERGE TESTS FAILED ({fails})");
    return fails == 0 ? 0 : 1;
}

if (arg == "navtest" && args.Length >= 2)
{
    // Reverse-engineer the AI navmaps: how well does a slope + water model reproduce the real
    // <vehicle>Level2Map8Bit.raw (512^2, same grid as the heightmap)?  0xff = passable, 0x00 = blocked.
    var dir = args[1];
    string Find(string n) => Directory.EnumerateFiles(dir, n, SearchOption.AllDirectories).First();
    var cfg = TerrainConfig.Load(Find("Terrain.con"));
    var hm = Heightmap.LoadForMaterialSize(Find("Heightmap.raw"), cfg.MaterialSize);
    int N = cfg.MaterialSize;
    float sp = cfg.HorizontalSpacing, water = cfg.WaterLevel;
    Console.WriteLine($"heightmap {N}x{N}, spacing {sp} m, waterLevel {water} m");

    // (name, maxSlopeDeg, waterMode)  waterMode: 0=land only, 1=needs water (boat)
    var vehicles = new (string file, float maxSlope, int waterMode)[]
    {
        ("Tank0Level2Map8Bit.raw",     30f, 0),
        ("Infantry1Level2Map8Bit.raw", 40f, 0),
        ("Boat2Level2Map8Bit.raw",     30f, 1),
        ("Heli5Level2Map8Bit.raw",     20f, 0),
    };
    foreach (var (file, maxSlope, waterMode) in vehicles)
    {
        var real = Directory.EnumerateFiles(dir, file, SearchOption.AllDirectories).FirstOrDefault();
        if (real is null) continue;
        var rb = File.ReadAllBytes(real);
        if (rb.Length != N * N) { Console.WriteLine($"{file}: {rb.Length} bytes != {N * N}, skip"); continue; }

        float tanMax = MathF.Tan(maxSlope * MathF.PI / 180f);
        // try a few orientations (the navmap may be transposed/flipped vs the heightmap) and keep the best.
        int bestAgree = 0; string bestOri = "";
        foreach (var ori in new[] { "xy", "yx", "xY", "Xy" })
        {
            int agree = 0;
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    int hx = x, hy = y;
                    if (ori == "yx") { hx = y; hy = x; }
                    else if (ori == "xY") hy = N - 1 - y;
                    else if (ori == "Xy") hx = N - 1 - x;
                    float h = cfg.HeightToMeters(hm[hx, hy]);
                    float g = 0;
                    if (hx > 0) g = MathF.Max(g, MathF.Abs(cfg.HeightToMeters(hm[hx - 1, hy]) - h) / sp);
                    if (hx < N - 1) g = MathF.Max(g, MathF.Abs(cfg.HeightToMeters(hm[hx + 1, hy]) - h) / sp);
                    if (hy > 0) g = MathF.Max(g, MathF.Abs(cfg.HeightToMeters(hm[hx, hy - 1]) - h) / sp);
                    if (hy < N - 1) g = MathF.Max(g, MathF.Abs(cfg.HeightToMeters(hm[hx, hy + 1]) - h) / sp);
                    bool passable = waterMode == 1 ? (h < water) : (g <= tanMax && h >= water);
                    bool realPass = rb[y * N + x] == 0xff;
                    if (passable == realPass) agree++;
                }
            if (agree > bestAgree) { bestAgree = agree; bestOri = ori; }
        }
        int realPassCt = rb.Count(b => b == 0xff);
        Console.WriteLine($"{file,-28} real {100.0 * realPassCt / rb.Length,5:0.0}% passable   best model agreement {100.0 * bestAgree / rb.Length,5:0.0}% (ori {bestOri})");
    }
    return 0;
}

if (arg == "waterpatch")
{
    // Gate for the live water-level save: PatchConLines must change waterLevel (and seaFloor/wave) while leaving
    // every other Terrain.con line (worldSize/materialSize/yScale/file refs) byte-identical.
    int fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }
    var cfg = new TerrainConfig { MaterialSize = 512, WorldSize = 2048, YScale = 0.35f, WaterLevel = 30f, SeaFloorLevel = 0f, WaveHeight = 1f };
    var original = cfg.ToTerrainConLines(@"BfVietnam\levels\Test").ToList();
    cfg.WaterLevel = 47.5f;   // user dragged the slider
    var patched = cfg.PatchConLines(original).ToList();

    Check(patched.Count == original.Count, $"line count unchanged ({original.Count})");
    var rc = TerrainConfig.Parse(patched);
    Check(Math.Abs(rc.WaterLevel - 47.5f) < 1e-3f, $"waterLevel patched to 47.5 (got {rc.WaterLevel})");
    Check(rc.WorldSize == 2048 && rc.MaterialSize == 512 && Math.Abs(rc.YScale - 0.35f) < 1e-3f, "worldSize/materialSize/yScale preserved");
    // every non-waterLevel line must be byte-identical.
    int changed = 0; for (int i = 0; i < original.Count; i++) if (original[i] != patched[i]) changed++;
    Check(changed == 1, $"exactly ONE line changed (the waterLevel line) — got {changed}");
    Check(patched.Any(l => l.Contains("GeometryTemplate.waterLevel 47.5")), "patched line has correct text + casing");

    Console.WriteLine(fails == 0 ? "WATER PATCH TESTS PASSED" : $"WATER PATCH TESTS FAILED ({fails})");
    return fails == 0 ? 0 : 1;
}

if (arg == "findtex" && args.Length >= 3)
{
    // Probe an archive for skybox cubemap faces: which env/sky face-name variants resolve to a real .dds?
    var lib = RefractorForge.Render.TextureLibrary.Open(args.Skip(1).Where(a => a.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase)).ToArray());
    Console.WriteLine($"{lib.Count} .dds entries indexed");
    string baseName = args[^1];
    foreach (var suf in new[] { "", "_01", "_02", "_03", "_04", "_05", "_06", "_0", "_1", "_2", "_3", "_4", "_5",
                                "up", "dn", "ft", "bk", "lf", "rt", "_up", "_dn", "_ft", "_bk", "_lf", "_rt", "_top", "_bottom" })
    {
        var t = lib.Resolve(baseName + suf);
        if (t is not null) Console.WriteLine($"  FOUND  {baseName}{suf}  ({t.Width}x{t.Height})");
    }
    return 0;
}

if (arg == "rfablockstats" && args.Length >= 2)
{
    // What block forms does this archive use? Retail BFV archives tell us which forms BfVietnam.exe's loader
    // is guaranteed to accept (LZO comp<unc, stored-verbatim comp==unc, or whole-entry-verbatim).
    var a = RefractorForge.Formats.Rfa.RfaArchive.Open(args[1]);
    long lzo = 0, verb = 0, lit = 0, wholeVerb = 0, entries = 0, blocks = 0;
    foreach (var e in a.Entries)
    {
        entries++;
        var bs = a.BlockSizes(e);
        if (bs.Count == 0) { wholeVerb++; continue; }
        foreach (var (comp, unc) in bs) { blocks++; if (comp < unc) lzo++; else if (comp == unc) verb++; else lit++; }
    }
    Console.WriteLine($"{Path.GetFileName(args[1])}: {entries} entries, {blocks} blocks");
    Console.WriteLine($"  whole-entry-verbatim (BlockSize==UncSize): {wholeVerb} entries");
    Console.WriteLine($"  LZO blocks (comp<unc):       {lzo}");
    Console.WriteLine($"  stored-verbatim (comp==unc): {verb}");
    Console.WriteLine($"  literal-padded (comp>unc):   {lit}");
    return 0;
}

if (arg == "scattercheck")
{
    // Gate random object scatter: placements stay above water, within the slope band, keep min spacing, and only
    // use the given candidates. Synthetic map: left third underwater, middle dry plain, right dry steep cliff.
    int fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }
    int ms = 64; var cfg = new TerrainConfig { MaterialSize = ms, WorldSize = 256, YScale = 1f, WaterLevel = 30f };
    var hm = new Heightmap(ms, ms);
    for (int r = 0; r < ms; r++) for (int c = 0; c < ms; c++)
        hm[c, r] = cfg.MetersToRaw(c < ms / 3 ? 20f : c < 2 * ms / 3 ? 50f : 50f + (c - 2 * ms / 3) * 4f);
    float HeightAt(float x, float z) => SearchMapGenerator.SampleHeight(cfg, hm, x, z);
    var candidates = new[] { "tree_m1", "bush_m1", "hut_m1" };

    float maxSlope = 20f, clearance = 1f, spacing = 5f;
    var placed = ObjectScatter.Scatter(candidates, cfg, HeightAt, count: 80, minSlopeDeg: 0f, maxSlopeDeg: maxSlope,
        avoidWater: true, waterClearance: clearance, minSpacing: spacing, seed: 3);
    Console.WriteLine($"  placed {placed.Count} objects");
    Check(placed.Count > 0, "placed at least some objects");
    int dry = placed.Count(p => p.Position.Y >= cfg.WaterLevel + clearance);
    Check(dry == placed.Count, $"all above water+clearance ({dry}/{placed.Count})");
    int onSlope = placed.Count(p =>
    {
        float st = cfg.HorizontalSpacing;
        float gx = (HeightAt(p.Position.X + st, p.Position.Z) - HeightAt(p.Position.X - st, p.Position.Z)) / (2 * st);
        float gz = (HeightAt(p.Position.X, p.Position.Z + st) - HeightAt(p.Position.X, p.Position.Z - st)) / (2 * st);
        return MathF.Atan(MathF.Sqrt(gx * gx + gz * gz)) * 180f / MathF.PI <= maxSlope + 0.5f;
    });
    Check(onSlope == placed.Count, $"all within slope band ({onSlope}/{placed.Count}) — none on the cliff");
    bool spacingOk = true;
    for (int i = 0; i < placed.Count && spacingOk; i++) for (int j = i + 1; j < placed.Count; j++)
    {
        float dx = placed[i].Position.X - placed[j].Position.X, dz = placed[i].Position.Z - placed[j].Position.Z;
        if (dx * dx + dz * dz < spacing * spacing) { spacingOk = false; break; }
    }
    Check(spacingOk, $"min spacing {spacing} m respected between all pairs");
    Check(placed.All(p => candidates.Contains(p.Template)), "every placement uses a candidate template");
    Check(placed.All(p => p.Yaw is >= 0f and <= 360f), "yaw randomized in [0,360]");
    // determinism
    var again = ObjectScatter.Scatter(candidates, cfg, HeightAt, 80, 0f, maxSlope, true, clearance, spacing, 3);
    Check(again.Count == placed.Count && again[0].Position.Equals(placed[0].Position), "deterministic for a fixed seed");

    Console.WriteLine(fails == 0 ? "SCATTER TESTS PASSED" : $"SCATTER TESTS FAILED ({fails})");
    return fails == 0 ? 0 : 1;
}

if (arg == "fractalcheck")
{
    // Gate the New Map terrain generator: every type produces relief, the height range is respected (with a
    // fitting yScale), islands sink to water at the edges, and mountains skew toward lower ground with sharp peaks.
    int fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }
    int sz = 256;
    var cfg = new TerrainConfig { MaterialSize = sz, WorldSize = 1024, YScale = 1f, WaterLevel = 30f }; // yScale 1 fits 0..255 m
    ushort lo = cfg.MetersToRaw(0f), hi = cfg.MetersToRaw(200f);

    double Sd(Heightmap h) { double s = 0, s2 = 0; foreach (var v in h.Samples) { s += v; s2 += (double)v * v; } double m = s / h.Samples.Length; return Math.Sqrt(Math.Max(0, s2 / h.Samples.Length - m * m)); }
    ushort Max(Heightmap h) { ushort m = 0; foreach (var v in h.Samples) if (v > m) m = v; return m; }

    var hills = HeightmapGenerator.Fractal(sz, 2026, 0.55f, lo, hi);
    Check(Sd(hills) > 1000, $"hills have real relief (sd {Sd(hills):0} raw)");
    Check(Math.Abs(cfg.HeightToMeters(Max(hills)) - 200f) < 2f, $"height range respected: peak {cfg.HeightToMeters(Max(hills)):0} m ~ 200 m (no clamp)");

    var mtn = HeightmapGenerator.Fractal(sz, 2026, 0.45f, lo, hi, island: false, peak: 2.2f);
    double meanHills = 0, meanMtn = 0; foreach (var v in hills.Samples) meanHills += v; foreach (var v in mtn.Samples) meanMtn += v;
    Check(meanMtn < meanHills, $"mountains skew lower (more valley, sharp peaks): mean {meanMtn / mtn.Samples.Length:0} < {meanHills / hills.Samples.Length:0}");

    var isl = HeightmapGenerator.Fractal(sz, 2026, 0.55f, lo, hi, island: true);
    double edge = 0; int ne = 0, c = sz / 2; double center = 0; int nc = 0;
    for (int x = 0; x < sz; x++) { edge += isl[x, 0] + isl[x, sz - 1]; ne += 2; }
    for (int y = c - 8; y < c + 8; y++) for (int x = c - 8; x < c + 8; x++) { center += isl[x, y]; nc++; }
    Check(edge / ne < center / nc * 0.5, $"island edges sink to water (edge {edge / ne:0} << centre {center / nc:0})");

    Console.WriteLine(fails == 0 ? "FRACTAL TESTS PASSED" : $"FRACTAL TESTS FAILED ({fails})");
    return fails == 0 ? 0 : 1;
}

if (arg == "gencheck")
{
    // Is DiamondSquare producing relief at every materialSize, and does the height range scale it?
    foreach (int ms in new[] { 256, 512, 1024 })
    {
        var cfg = new TerrainConfig { MaterialSize = ms, WorldSize = ms * 4, YScale = 0.5f, WaterLevel = 30f };
        void Report(string label, Heightmap hm)
        {
            ushort lo = ushort.MaxValue, hi = 0; double sum = 0, sum2 = 0; int nn = hm.Samples.Length;
            foreach (var v in hm.Samples) { if (v < lo) lo = v; if (v > hi) hi = v; sum += v; sum2 += (double)v * v; }
            double mean = sum / nn, var = sum2 / nn - mean * mean, sd = Math.Sqrt(Math.Max(0, var));
            Console.WriteLine($"  ms{ms,-5} {label,-22} = {cfg.HeightToMeters(lo):0.0}..{cfg.HeightToMeters(hi):0.0} m   relief sd {cfg.HeightToMeters((ushort)sd):0.0} m");
        }
        Report("rough0.55 20-80m", HeightmapGenerator.DiamondSquare(ms, 2026, 0.55f, cfg.MetersToRaw(20f), cfg.MetersToRaw(80f)));
        Report("rough0.55 0-250m", HeightmapGenerator.DiamondSquare(ms, 2026, 0.55f, cfg.MetersToRaw(0f), cfg.MetersToRaw(250f)));
    }
    Console.WriteLine("(relief sd should grow with the height range and be non-zero at every size; sd~0 == FLAT bug)");
    return 0;
}

if (arg == "repackcheck" && args.Length >= 2)
{
    // Save-corruption probe: repack an .rfa with NO edits, reload, and verify every entry decodes byte-identical.
    // Also flags the BlockSize==UncompressedSize collision (would make the reader treat a block region as raw).
    var path = args[1];
    var orig = RefractorForge.Formats.Rfa.RfaArchive.Open(path);
    Console.WriteLine($"{orig.Entries.Count} entries in {Path.GetFileName(path)}");
    var repackedBytes = RefractorForge.Formats.Rfa.RfaWriter.Repack(orig, new Dictionary<string, byte[]>());
    var re = RefractorForge.Formats.Rfa.RfaArchive.Load(repackedBytes);
    Console.WriteLine($"repacked: {orig.Entries.Count} -> {re.Entries.Count} entries, {new FileInfo(path).Length} -> {repackedBytes.Length} bytes ({100.0 * repackedBytes.Length / new FileInfo(path).Length:0}%)");

    var reByName = re.Entries.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
    int bad = 0, collide = 0, checkd = 0;
    foreach (var e in orig.Entries)
    {
        if (!reByName.TryGetValue(e.Name, out var r2)) { Console.WriteLine($"  MISSING after repack: {e.Name}"); bad++; continue; }
        if (r2.BlockSize == r2.UncompressedSize && e.UncompressedSize != 0) collide++;   // reader will treat as raw
        byte[] a, b;
        try { a = orig.Read(e); } catch (Exception ex) { Console.WriteLine($"  orig read fail {e.Name}: {ex.Message}"); bad++; continue; }
        try { b = re.Read(r2); } catch (Exception ex) { Console.WriteLine($"  REPACK read FAIL {e.Name}: {ex.Message}"); bad++; continue; }
        checkd++;
        if (a.Length != b.Length || !a.AsSpan().SequenceEqual(b)) { if (bad < 12) Console.WriteLine($"  MISMATCH {e.Name}: {a.Length} vs {b.Length} bytes"); bad++; }
    }
    // collisions here are just the archive's own pre-existing whole-verbatim entries passed through (harmless,
    // they decode fine); the round-trip mismatch count is the real signal.
    Console.WriteLine($"checked {checkd}; mismatches/failures {bad}; pre-existing whole-verbatim entries {collide}");
    Console.WriteLine(bad == 0 ? "REPACK ROUND-TRIP OK (writer self-consistent)" : "REPACK PROBLEM DETECTED");
    return bad == 0 ? 0 : 1;
}

if (arg == "savefix")
{
    // Regression gate for the .rfa save-corruption fix: edit ONE entry, repack, and prove (a) every UNCHANGED
    // entry's on-disk region is byte-identical (verbatim passthrough — never re-encoded), (b) the edited entry
    // decodes to the new bytes and uses ONLY literal blocks (no match-compressed block our encoder can't
    // guarantee liblzo2 accepts), (c) the whole archive re-reads. Self-contained: builds a tiny archive.
    int fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }
    var rng = new Random(7);
    var entries = new List<(string Name, byte[] Data)>();
    for (int i = 0; i < 20; i++) { var d = new byte[1000 + i * 777]; rng.NextBytes(d); entries.Add(($"folder/file{i:00}.dat", d)); }
    // a compressible text entry + a zero-length entry (edge cases)
    entries.Add(("Init/StaticObjects.con", System.Text.Encoding.Latin1.GetBytes(string.Concat(Enumerable.Repeat("object.create foo\r\nobject.absolutePosition 1/2/3\r\n", 50)))));
    entries.Add(("empty.txt", Array.Empty<byte>()));

    var baseBytes = RefractorForge.Formats.Rfa.RfaWriter.Build(entries);
    var orig = RefractorForge.Formats.Rfa.RfaArchive.Load(baseBytes);
    Check(orig.Entries.Count == entries.Count, $"base archive built ({orig.Entries.Count} entries)");

    // edit ONE entry; everything else must pass through untouched.
    var edited = System.Text.Encoding.Latin1.GetBytes("rem edited by RefractorForge\r\nobject.create bar\r\nobject.absolutePosition 9/9/9\r\n");
    var repl = new Dictionary<string, byte[]> { ["Init/StaticObjects.con"] = edited };
    var repackBytes = RefractorForge.Formats.Rfa.RfaWriter.Repack(orig, repl);
    var re = RefractorForge.Formats.Rfa.RfaArchive.Load(repackBytes);
    Check(re.Entries.Count == orig.Entries.Count, "entry count preserved");

    var reByName = re.Entries.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
    int verbatim = 0, decoded = 0;
    foreach (var oe in orig.Entries)
    {
        var r2 = reByName[oe.Name];
        bool isEdited = oe.Name == "Init/StaticObjects.con";
        // UNCHANGED -> region bytes must be byte-identical (verbatim passthrough).
        if (!isEdited && orig.RawRegion(oe).AsSpan().SequenceEqual(re.RawRegion(r2))) verbatim++;
        // every entry must still decode.
        try { var _ = re.Read(r2); decoded++; } catch { }
    }
    Check(verbatim == orig.Entries.Count - 1, $"all {orig.Entries.Count - 1} unchanged entries copied through byte-identical (got {verbatim})");
    Check(decoded == re.Entries.Count, $"every entry re-decodes ({decoded}/{re.Entries.Count})");

    var editedEntry = reByName["Init/StaticObjects.con"];
    Check(re.Read(editedEntry).AsSpan().SequenceEqual(edited), "edited entry decodes to the NEW bytes");
    bool onlyLiteral = re.BlockSizes(editedEntry).All(b => b.Comp >= b.Unc);   // literal-only never shrinks
    Check(onlyLiteral, "edited entry uses ONLY literal blocks (no unverifiable match-compressed block)");

    Console.WriteLine(fails == 0 ? "SAVE FIX TESTS PASSED" : $"SAVE FIX TESTS FAILED ({fails})");
    return fails == 0 ? 0 : 1;
}

if (arg == "navgen")
{
    // Navmap generation gate: functional + format/structure asserts (self-contained), then an HONEST
    // retail-agreement diagnostic when a real level dir is passed. Generation is createSearchMaps-STYLE
    // (terrain-derived); retail navmaps are hand-edited on top (loadEdited8BitMaps), so per-cell match tops
    // out ~70% (see pathfinding RE notes) — the diagnostic is informational, NOT a pass/fail bar.
    int fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }

    // synthetic map: left third underwater, middle dry flat plain, right third a dry steep cliff (45deg).
    int ms = 64, ws = 256;
    var cfg = new TerrainConfig { MaterialSize = ms, WorldSize = ws, YScale = 1f, WaterLevel = 30f };
    var hm = new Heightmap(ms, ms);
    for (int row = 0; row < ms; row++)
        for (int col = 0; col < ms; col++)
        {
            float h = col < ms / 3 ? 20f                                  // underwater (below water 30)
                    : col < 2 * ms / 3 ? 50f                              // dry flat plain
                    : 50f + (col - 2 * ms / 3) * 4f;                      // dry steep cliff (4 m/cell == ~45deg)
            hm[col, row] = cfg.MetersToRaw(h);
        }

    int L = 2, side = SearchMapGenerator.LevelSide(ms, L);   // == ms at L2 (1:1 with heightmap)
    float mpc = (float)ws / side;
    SearchMapParams P(string n) => SearchMapParams.Standard.First(s => s.Name.StartsWith(n));
    (int pass, int wetPass, int dryPass) Scan(SearchMapParams p)
    {
        var d = SearchMapGenerator.Generate(cfg, hm, p, L);
        int pass = 0, wet = 0, dry = 0;
        for (int y = 0; y < side; y++) for (int x = 0; x < side; x++)
            if (d[y * side + x] == 0x00)   // 0x00 = passable (corrected polarity)
            {
                pass++;
                var (gx, gy) = SearchMapGenerator.GridForNav(x, y, side);
                if (SearchMapGenerator.SampleHeight(cfg, hm, (gx + 0.5f) * mpc, (gy + 0.5f) * mpc) < cfg.WaterLevel) wet++; else dry++;
            }
        return (pass, wet, dry);
    }

    var tank = Scan(P("Tank0"));
    Check(tank.pass > 0 && tank.wetPass == 0, $"tank: passable cells exist, NONE underwater ({tank.wetPass} wet / {tank.pass})");
    Check(tank.pass < side * side / 2, $"tank: water+cliff removed (passable {100.0 * tank.pass / (side * side):0}% < 50%)");
    var boat = Scan(P("Boat2"));
    Check(boat.pass > 0 && boat.dryPass == 0, $"boat: passable cells exist, ALL in water ({boat.dryPass} dry / {boat.pass})");
    var heli = Scan(P("Heli5"));
    Check(heli.wetPass > 0, $"heli: flies over water — not water-gated (wet-passable {heli.wetPass})");
    Check(heli.pass > tank.pass, $"heli passable ({heli.pass}) > tank passable ({tank.pass})");
    var amph = Scan(P("Amphibius"));
    Check(amph.wetPass > 0 && amph.dryPass > 0, $"amphibious: passable on water ({amph.wetPass}) AND land ({amph.dryPass})");

    // object footprint carves a hole in the tank map.
    var hole = new[] { new ObjectFootprint(ws * 0.5f, ws * 0.5f, 12f, 5f) };  // mid plain, 12 m radius, 5 m tall
    var tNo = SearchMapGenerator.Generate(cfg, hm, P("Tank0"), L);
    var tYes = SearchMapGenerator.Generate(cfg, hm, P("Tank0"), L, hole);
    int blockedAdded = 0; for (int i = 0; i < tNo.Length; i++) if (tNo[i] == 0x00 && tYes[i] == 0xff) blockedAdded++;  // passable -> blocked
    Check(blockedAdded > 0, $"object footprint blocks tank cells on the plain ({blockedAdded} newly blocked)");

    // format / structure: GenerateAll now returns BOTH the editor 8Bit and the engine compressed form.
    var all = SearchMapGenerator.GenerateAll(cfg, hm);
    var eightFiles = all.Where(a => a.FileName.EndsWith("Map8Bit.raw")).ToList();
    var compFiles = all.Where(a => a.FileName.EndsWith("Map.raw") && !a.FileName.EndsWith("8Bit.raw")).ToList();
    Check(eightFiles.Count > 0 && eightFiles.Count == compFiles.Count, $"equal 8Bit ({eightFiles.Count}) + compressed ({compFiles.Count}) files");
    bool sizesOk = true, binOk = true;
    foreach (var (file, data) in eightFiles)
    {
        int lvl = int.Parse(file.Substring(file.IndexOf("Level") + 5, 1));
        int exp = SearchMapGenerator.LevelSide(ms, lvl); exp *= exp;
        if (data.Length != exp) sizesOk = false;
        foreach (var b in data) if (b != 0x00 && b != 0xff) { binOk = false; break; }
    }
    Check(sizesOk, "each 8Bit file is LevelSide^2 bytes");
    Check(binOk, "every 8Bit byte is 0x00 (passable) or 0xFF (blocked)");
    // engine-form round-trip: each compressed Map.raw must decode back to its matching 8Bit.
    bool rtOk = true;
    foreach (var (cfile, cdata) in compFiles)
    {
        var match = eightFiles.FirstOrDefault(e => e.FileName == cfile.Replace("Map.raw", "Map8Bit.raw"));
        if (match.Data is null) { rtOk = false; continue; }
        var dec = CompressedSearchMap.Decode(cdata, out _, out _);
        if (dec.Length != match.Data.Length) { rtOk = false; continue; }
        for (int i = 0; i < dec.Length; i++) if (dec[i] != match.Data[i]) { rtOk = false; break; }
    }
    Check(rtOk, "each compressed Map.raw decodes back to its 8Bit (engine-form round-trip)");
    Check(eightFiles.Any(e => e.FileName == "Tank0Level0Map8Bit.raw") && eightFiles.Any(e => e.FileName == "Tank0Level2Map8Bit.raw"), "tank ships levels 0..2");
    Check(eightFiles.Any(e => e.FileName == "Boat2Level2Map8Bit.raw") && !eightFiles.Any(e => e.FileName == "Boat2Level0Map8Bit.raw"), "boat starts at level 2 (no level 0/1)");

    // WriteFolder round-trip to a temp dir.
    string tmp = Path.Combine(Path.GetTempPath(), "rf_navgen_test");
    if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    int written = SearchMapGenerator.WriteFolder(tmp, cfg, hm);
    Check(written == all.Count, $"WriteFolder wrote all {all.Count} files (got {written})");
    Check(File.Exists(Path.Combine(tmp, "Pathfinding", "Tank0Level2Map.raw")) && File.Exists(Path.Combine(tmp, "Pathfinding", "Tank0Level2Map8Bit.raw")), "Pathfinding/ has both compressed + 8Bit for Tank0Level2");

    // optional: honest retail-agreement diagnostic for a real level (+ mesh .rfa for object footprints).
    if (args.Length >= 2)
    {
        var dir = args[1];
        var archives = args.Skip(2).Where(a => a.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase)).ToArray();
        string Find(string n) => Directory.EnumerateFiles(dir, n, SearchOption.AllDirectories).First();
        var rcfg = TerrainConfig.Load(Find("Terrain.con"));
        var rhm = Heightmap.LoadForMaterialSize(Find("Heightmap.raw"), rcfg.MaterialSize);
        List<ObjectFootprint>? foots = null;
        if (archives.Length > 0)
        {
            var lib = RefractorForge.Render.MeshLibrary.Open(archives);
            var so = StaticObjectsFile.Load(Find("StaticObjects.con"));
            foots = RefractorForge.Render.SearchMapBuilder.Footprints(so.Objects, lib);
            Console.WriteLine($"\nretail diagnostic: {so.Objects.Count} objects -> {foots.Count} footprints");
        }
        else Console.WriteLine("\nretail diagnostic (terrain-only; pass mesh .rfa to include objects):");
        foreach (var p in SearchMapParams.Standard)
            foreach (int lvl in p.LevelSet)
            {
                var realPath = Directory.EnumerateFiles(dir, SearchMapGenerator.FileName(p, lvl), SearchOption.AllDirectories).FirstOrDefault();
                if (realPath is null) continue;
                var real = File.ReadAllBytes(realPath);
                var gen = SearchMapGenerator.Generate(rcfg, rhm, p, lvl, foots);
                if (real.Length != gen.Length) { Console.WriteLine($"  {SearchMapGenerator.FileName(p, lvl),-30} size {real.Length} != {gen.Length}"); continue; }
                long ag = 0; for (int i = 0; i < real.Length; i++) if (real[i] == gen[i]) ag++;
                Console.WriteLine($"  {SearchMapGenerator.FileName(p, lvl),-30} {100.0 * ag / real.Length,5:0.0}% agree");
            }
        Console.WriteLine("(per-cell match tops ~70%: retail dry-land 'blocked' is designer hand-editing, not terrain — we emit the clean terrain base)");
    }

    Console.WriteLine(fails == 0 ? "NAVGEN TESTS PASSED" : $"NAVGEN TESTS FAILED ({fails})");
    return fails == 0 ? 0 : 1;
}

if (arg == "navpaint")
{
    // AI Path painting save-path gate (self-contained): seed a vehicle's WORLD-GRID finest map, "paint" a
    // blocked square, then verify EncodeVehicleLevels downsamples + nav-orients + compresses correctly and
    // round-trips, and that the painted region survives the conservative downsample to coarser levels.
    int fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }

    int ms = 64, ws = 256;
    var cfg = new TerrainConfig { MaterialSize = ms, WorldSize = ws, YScale = 1f, WaterLevel = 30f };
    var hm = new Heightmap(ms, ms);
    for (int row = 0; row < ms; row++) for (int col = 0; col < ms; col++) hm[col, row] = cfg.MetersToRaw(50f);  // flat dry plain (all passable)

    var tank = SearchMapParams.Standard.First(s => s.Name == "Tank0");
    int finest = SearchMapGenerator.FinestSide(ms);
    var grid = SearchMapGenerator.GenerateGrid(cfg, hm, tank, 0);   // world-grid L0
    Check(grid.Length == finest * finest, $"world-grid finest map is {finest}^2 bytes");
    int blocked0 = grid.Count(b => b == 0xFF);

    int x0 = finest / 4, y0 = finest / 4, sq = finest / 8;
    for (int y = y0; y < y0 + sq; y++) for (int x = x0; x < x0 + sq; x++) grid[y * finest + x] = 0xFF;  // paint a blocked square
    Check(grid.Count(b => b == 0xFF) == blocked0 + sq * sq, $"painting added {sq * sq} blocked cells");

    var files = SearchMapGenerator.EncodeVehicleLevels(tank, grid, finest);
    var eight = files.Where(f => f.FileName.EndsWith("Map8Bit.raw")).ToList();
    var comp = files.Where(f => f.FileName.EndsWith("Map.raw") && !f.FileName.EndsWith("8Bit.raw")).ToList();
    Check(eight.Count == 3 && comp.Count == 3, $"tank ships 3 levels x (8Bit + compressed) (got {eight.Count}/{comp.Count})");

    bool rt = true;
    foreach (var (cf, cd) in comp)
    {
        var m = eight.First(e => e.FileName == cf.Replace("Map.raw", "Map8Bit.raw"));
        if (!CompressedSearchMap.Decode(cd, out _, out _).SequenceEqual(m.Data)) rt = false;
    }
    Check(rt, "every compressed level decodes back to its 8Bit (engine round-trip)");

    var l0 = eight.First(e => e.FileName == "Tank0Level0Map8Bit.raw").Data;
    Check(l0.Count(b => b == 0xFF) == grid.Count(b => b == 0xFF), "L0 nav-oriented 8Bit preserves the painted blocked count");

    int l2side = finest >> 2, f2 = finest / l2side;
    var grid2 = SearchMapGenerator.DownsampleBlocked(grid, finest, l2side);
    int cxr = (x0 + sq / 2) / f2, cyr = (y0 + sq / 2) / f2;
    Check(grid2[cyr * l2side + cxr] == 0xFF, "painted region stays blocked after conservative downsample to L2");

    string tmp = Path.Combine(Path.GetTempPath(), "rf_navpaint_test");
    if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    int n = SearchMapGenerator.WriteVehicleEditedFolder(tmp, tank, grid, finest);
    Check(n == 6, $"WriteVehicleEditedFolder wrote 6 files (got {n})");
    var wc = File.ReadAllBytes(Path.Combine(tmp, "Pathfinding", "Tank0Level0Map.raw"));
    var we = File.ReadAllBytes(Path.Combine(tmp, "Pathfinding", "Tank0Level0Map8Bit.raw"));
    Check(CompressedSearchMap.Decode(wc, out _, out _).SequenceEqual(we), "written compressed L0 decodes to written 8Bit");

    // .rfa-level navmap save (the Viewer's DirtyNavFiles -> extraFiles path): build a base archive that already
    // ships this vehicle's navmaps, then write a PATCH from the EDITED grid via extraFiles (bare leaf names) and
    // confirm every nav entry matched a base entry and overrides it byte-exact (same mechanism as tiles/sounds).
    var baseNav = SearchMapGenerator.EncodeVehicleLevels(tank, SearchMapGenerator.GenerateGrid(cfg, hm, tank, 0), finest);  // unpainted base
    const string navPrefix = "BfVietnam/levels/RFNav/Pathfinding/";
    var baseEntries = baseNav.Select(f => (navPrefix + f.FileName, f.Data)).ToList();
    string navBase = Path.Combine(Path.GetTempPath(), "rf_navpatch_base.rfa");
    string navPatch = Path.Combine(Path.GetTempPath(), "rf_navpatch_001.rfa");
    RefractorForge.Formats.Rfa.RfaWriter.WriteFile(navBase, baseEntries);
    var editedNav = SearchMapGenerator.EncodeVehicleLevels(tank, grid, finest);   // grid = painted
    var navNames = LevelSaver.WritePatchRfa(navBase, navPatch, null, null, null, null, extraFiles: editedNav);
    Check(navNames.Count == editedNav.Count, $"every edited navmap matched a base Pathfinding entry ({navNames.Count}/{editedNav.Count})");
    var navPatchA = RefractorForge.Formats.Rfa.RfaArchive.Open(navPatch);
    var nl0e = navPatchA.Entries.FirstOrDefault(e => e.Name.EndsWith("Tank0Level0Map.raw", StringComparison.OrdinalIgnoreCase) && !e.Name.EndsWith("Map8Bit.raw", StringComparison.OrdinalIgnoreCase));
    var wantL0 = editedNav.First(f => f.FileName == "Tank0Level0Map.raw").Data;
    Check(nl0e is not null && navPatchA.Read(nl0e).SequenceEqual(wantL0), "patch Tank0Level0Map.raw is the EDITED compressed navmap, byte-exact");
    try { File.Delete(navBase); File.Delete(navPatch); Directory.Delete(tmp, true); } catch { }

    Console.WriteLine(fails == 0 ? "NAVPAINT TESTS PASSED" : $"NAVPAINT TESTS FAILED ({fails})");
    return fails == 0 ? 0 : 1;
}

if (arg == "foliagescatter")
{
    // Overgrowth foliage scatter (self-contained): the GAME-MATCHED patch model. From a .wst palette + index map it
    // must place ~the game's density (~2.1 trees per occupied 12.5 m patch, calibrated from a captured dump), only on
    // tree-bearing materials, with a probability roulette (species MIX, not just the top one), uniform yaw, scale from
    // the .wst, deterministically, scaling with the density multiplier. (Render side -- height/mesh -- is Viewer GL.)
    int fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }

    string wst = "<WRAP><overGrowth materialMapSideSize=\"64\"><materials>"
               + "<default><types></types></default><water><types></types></water>"
               + "<dryGrass><types>"
               +   "<type geometryName=\"c05f_trees_m2\" probability=\"0.5\"  scale=\"CRDUniform 0.6 1.2\"/>"
               +   "<type geometryName=\"c03f_trees_m2\" probability=\"0.35\" scale=\"0.6 1.2\"/>"
               +   "<type geometryName=\"c07f_jungle_m2\" probability=\"0.15\" scale=\"0.6 1.2\"/>"
               + "</types></dryGrass>"
               + "<dryDirt><types><type geometryName=\"c02f_trees_m2\" probability=\"1\" scale=\"0.8 1.4\"/></types></dryDirt>"
               + "</materials></overGrowth></WRAP>";
    var pal = FoliagePalette.Parse(wst);
    Check(pal.Materials.Count == 4, $"palette parsed 4 slots (got {pal.Materials.Count})");

    int side = 64;
    var allGrass = new byte[side * side]; for (int i = 0; i < allGrass.Length; i++) allGrass[i] = 2;   // all dryGrass (every patch occupied)
    var growth = new GrowthMaps { Over = MaterialMap.FromBytes(allGrass, side, side), OverSide = side, OverPalette = pal };
    var cfg = new TerrainConfig { WorldSize = 256, MaterialSize = side };
    int grid = (int)Math.Round(256.0 / 12.5); int patches = grid * grid;   // game patch grid

    var inst = OvergrowthFoliage.Scatter(growth, cfg, 12.5f, 1f, over: true);
    double avg = (double)inst.Count / patches;
    Check(avg > 1.7 && avg < 2.5, $"game-matched density ~2.1 trees/occupied patch (got {avg:0.00}; {inst.Count} trees over {patches} patches)");
    Check(inst.All(f => f.WorldX >= 0 && f.WorldX < 256 && f.WorldZ >= 0 && f.WorldZ < 256), "all instances in world bounds");
    Check(inst.All(f => f.Geometry is "c05f_trees_m2" or "c03f_trees_m2" or "c07f_jungle_m2"), "only the dryGrass geometries appear");
    var hist = inst.GroupBy(f => f.Geometry).ToDictionary(g => g.Key, g => g.Count());
    Check(hist.Count == 3, $"roulette uses ALL types (species MIX, got {hist.Count})");
    Check(hist.GetValueOrDefault("c05f_trees_m2") > hist.GetValueOrDefault("c07f_jungle_m2"), "higher-probability type is more common (c05f 0.5 > c07f 0.15)");
    Check(inst.All(f => f.YawDeg >= 0f && f.YawDeg <= 360f), "yaw in [0,360]");
    Check(inst.All(f => f.Scale >= 0.55f && f.Scale <= 1.25f), "scale within the .wst CRDUniform range");

    var inst2 = OvergrowthFoliage.Scatter(growth, cfg, 12.5f, 1f, over: true);
    Check(inst.Count == inst2.Count && inst.Zip(inst2).All(p => p.First == p.Second), "deterministic: identical on repeat");

    var dense = OvergrowthFoliage.Scatter(growth, cfg, 12.5f, 2f, over: true);
    Check(dense.Count > inst.Count * 1.6 && dense.Count < inst.Count * 2.4, $"density x2 ~doubles the trees ({dense.Count} vs {inst.Count})");

    var eb = new byte[side * side]; for (int i = 0; i < eb.Length; i++) eb[i] = (byte)(i % 2);   // 0/1 both empty
    var eg = new GrowthMaps { Over = MaterialMap.FromBytes(eb, side, side), OverSide = side, OverPalette = pal };
    Check(OvergrowthFoliage.Scatter(eg, cfg, 12.5f, 1f).Count == 0, "all-empty-slot map scatters nothing");

    Console.WriteLine(fails == 0 ? "FOLIAGE SCATTER TESTS PASSED" : $"FOLIAGE SCATTER TESTS FAILED ({fails})");
    return fails == 0 ? 0 : 1;
}

if (arg == "overgrowthbake")
{
    // Bake overgrowth -> StaticObjects.con (self-contained): scatter, build a StaticObjectsFile the way the Viewer
    // does (template = geometry, absolutePosition, rotation = yaw/0/0, optional scale), write the .con and re-parse
    // it, proving the bake emits valid object.create entries that round-trip.
    int fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }

    string wst = "<WRAP><overGrowth materialMapSideSize=\"64\"><materials>"
               + "<default><types></types></default><water><types></types></water>"
               + "<dryGrass><types><type geometryName=\"c01f_trees_m2\" probability=\"0.7\" scale=\"0.6 1.2\"/></types></dryGrass>"
               + "<dryDirt><types><type geometryName=\"c08f_jungle_m2\" probability=\"1\" scale=\"0.8 1.4\"/></types></dryDirt>"
               + "</materials></overGrowth></WRAP>";
    int side = 64;
    var bytes = new byte[side * side];
    for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)((i % 7 == 0) ? 2 : (i % 5 == 0 ? 3 : 0));   // mix of dryGrass(2)/dryDirt(3)/empty(0)
    var growth = new GrowthMaps { Over = MaterialMap.FromBytes(bytes, side, side), OverSide = side, OverPalette = FoliagePalette.Parse(wst) };
    var cfg = new TerrainConfig { WorldSize = 256, MaterialSize = side };

    var inst = OvergrowthFoliage.Scatter(growth, cfg, 12.5f, 1f, over: true);
    var sof = new StaticObjectsFile();
    foreach (var fi in inst)
    {
        var o = new StaticObject(fi.Geometry) { Position = new Vec3(fi.WorldX, 50f, fi.WorldZ), Rotation = new Vec3(fi.YawDeg, 0f, 0f) };
        if (Math.Abs(fi.Scale - 1f) > 1e-3f) o.Scale = fi.Scale;
        sof.Objects.Add(o);
    }
    Check(inst.Count > 0 && sof.Objects.Count == inst.Count, $"baked {sof.Objects.Count} overgrowth objects from the scatter");

    var text = string.Join("\n", sof.Write());
    Check(text.Contains("object.create ") && text.Contains("object.absolutePosition ") && text.Contains("object.rotation "), "emits object.create / absolutePosition / rotation lines");

    string tmp = Path.Combine(Path.GetTempPath(), "rf_ogbake_test.con");
    sof.Save(tmp);
    var re = StaticObjectsFile.Load(tmp);
    Check(re.Objects.Count == inst.Count, $"con round-trips object count ({re.Objects.Count}/{inst.Count})");
    Check(re.Objects.All(o => o.Template is "c01f_trees_m2" or "c08f_jungle_m2"), "templates preserved through the .con");
    var a = sof.Objects[0]; var b = re.Objects[0];
    bool posOk = Math.Abs(a.Position.X - b.Position.X) < 0.05f && Math.Abs(a.Position.Y - b.Position.Y) < 0.05f && Math.Abs(a.Position.Z - b.Position.Z) < 0.05f;
    bool rotOk = Math.Abs(a.Rotation.X - b.Rotation.X) < 0.05f;
    Check(posOk && rotOk && a.Template == b.Template, "first object's template + position + yaw round-trip");
    Check(re.Objects.Any(o => o.Scale is float sc && Math.Abs(sc - 1f) > 1e-3f), "a jungle object kept its non-1 scale (object.geometry.scale)");
    try { File.Delete(tmp); } catch { }

    Console.WriteLine(fails == 0 ? "OVERGROWTH BAKE TESTS PASSED" : $"OVERGROWTH BAKE TESTS FAILED ({fails})");
    return fails == 0 ? 0 : 1;
}

if (arg == "foliagecount" && args.Length >= 2)
{
    // Diagnostic: scatter the REAL level's overgrowth at the game-matched density and report the total + species mix
    // (sanity-check vs the captured ~19k on a 2048 map). `foliagecount <levelDir> [patchMeters=12.5] [density=1]`.
    string lvl = args[1];
    float pm = args.Length >= 3 ? float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 12.5f;
    float dn = args.Length >= 4 ? float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 1f;
    var terr = Directory.EnumerateFiles(lvl, "Terrain.con", SearchOption.AllDirectories).FirstOrDefault();
    if (terr is null) { Console.WriteLine("no Terrain.con"); return 1; }
    var cfg = TerrainConfig.Load(terr);
    var growth = GrowthMaps.LoadFolder(lvl);
    if (growth.Over is null || growth.OverPalette is null) { Console.WriteLine("no overgrowth"); return 1; }
    var inst = OvergrowthFoliage.Scatter(growth, cfg, pm, dn, over: true);
    Console.WriteLine($"world {cfg.WorldSize}  overSide {growth.OverSide}  patch {pm}m  density x{dn}  ->  {inst.Count} trees");
    foreach (var g in inst.GroupBy(f => f.Geometry).OrderByDescending(g => g.Count()).Take(10))
        Console.WriteLine($"   {g.Key}: {g.Count()}");
    return 0;
}

if (arg == "tga")
{
    // TGA decoder gate (self-contained): uncompressed + RLE, 24-bit BGR->RGBA, origin bit. Lets the editor
    // import .tga surface textures (GDI+ can't read TGA).
    int fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }
    // 2x2 image: (0,0)=red (1,0)=green (0,1)=blue (1,1)=white. BGR pixels in TGA storage order.
    byte[] Pix(bool topLeft) => topLeft
        ? new byte[] { 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255 }   // row0=top: red,green ; row1: blue,white
        : new byte[] { 255, 0, 0, 255, 255, 255, 0, 0, 255, 0, 255, 0 };  // row0=bottom: blue,white ; row1: red,green
    byte[] MakeUncompressed(bool topLeft)
    {
        var p = Pix(topLeft); var t = new byte[18 + p.Length];
        t[2] = 2; t[12] = 2; t[14] = 2; t[16] = 24; t[17] = (byte)(topLeft ? 0x20 : 0x00);
        Array.Copy(p, 0, t, 18, p.Length); return t;
    }
    var decBL = RefractorForge.Render.TgaTexture.Decode(MakeUncompressed(false));
    Check(decBL is not null && decBL.Width == 2 && decBL.Height == 2, "uncompressed 24-bit TGA decodes 2x2");
    bool corners = decBL is not null
        && decBL.Rgba[0] == 255 && decBL.Rgba[1] == 0 && decBL.Rgba[2] == 0           // (0,0) red
        && decBL.Rgba[4] == 0 && decBL.Rgba[5] == 255 && decBL.Rgba[6] == 0           // (1,0) green
        && decBL.Rgba[8] == 0 && decBL.Rgba[9] == 0 && decBL.Rgba[10] == 255          // (0,1) blue
        && decBL.Rgba[12] == 255 && decBL.Rgba[13] == 255 && decBL.Rgba[14] == 255;   // (1,1) white
    Check(corners, "BGR->RGBA + bottom-left origin map to the right corners");
    var decTL = RefractorForge.Render.TgaTexture.Decode(MakeUncompressed(true));
    Check(decTL is not null && decBL is not null && decTL.Rgba.AsSpan(0, 16).SequenceEqual(decBL.Rgba.AsSpan(0, 16)), "top-left origin decodes to the same image");
    // RLE: one raw packet of all 4 pixels (header 0x03), bottom-left.
    var rp = Pix(false); var rle = new byte[18 + 1 + rp.Length];
    rle[2] = 10; rle[12] = 2; rle[14] = 2; rle[16] = 24; rle[18] = 0x03; Array.Copy(rp, 0, rle, 19, rp.Length);
    var decR = RefractorForge.Render.TgaTexture.Decode(rle);
    Check(decR is not null && decBL is not null && decR.Rgba.AsSpan(0, 16).SequenceEqual(decBL.Rgba.AsSpan(0, 16)), "RLE raw-packet decodes identically to uncompressed");
    // RLE run: 2x1, one RLE packet (0x81 = run of 2) of a single red pixel (BGR 0,0,255).
    var run = new byte[18 + 1 + 3]; run[2] = 10; run[12] = 2; run[14] = 1; run[16] = 24;
    run[18] = 0x81; run[19] = 0; run[20] = 0; run[21] = 255;
    var decRun = RefractorForge.Render.TgaTexture.Decode(run);
    Check(decRun is not null && decRun.Width == 2 && decRun.Rgba[0] == 255 && decRun.Rgba[1] == 0 && decRun.Rgba[4] == 255, "RLE run packet repeats the pixel");
    Console.WriteLine(fails == 0 ? "TGA TESTS PASSED" : $"TGA TESTS FAILED ({fails})");
    return fails == 0 ? 0 : 1;
}

if (arg == "matgen")
{
    // Auto material-map generator gate (self-contained): water line -> Water, steep -> Rock, flat -> grass.
    int fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }
    int ms = 64, ws = 256;
    var cfg = new TerrainConfig { MaterialSize = ms, WorldSize = ws, YScale = 1f, WaterLevel = 30f };
    var hm = new Heightmap(ms, ms);
    for (int row = 0; row < ms; row++)
        for (int col = 0; col < ms; col++)
        {
            float h = col < ms / 3 ? 20f : col < 2 * ms / 3 ? 50f : 50f + (col - 2 * ms / 3) * 5f;  // underwater | flat | cliff
            hm[col, row] = cfg.MetersToRaw(h);
        }
    var m = MaterialMapGenerator.FromTerrain(cfg, hm);
    Check(m.Width == ms && m.Height == ms, "material map is materialSize^2");
    int C(float frac) => Math.Clamp((int)(frac * ms), 0, ms - 1);
    byte under = m[C(0.1f), ms / 2], cliff = m[C(0.95f), ms / 2], flat = m[C(0.5f), ms / 2];
    Check(under == 15, $"underwater -> Water(15) (got {under})");
    Check(cliff == 9, $"steep cliff -> Rock(9) (got {cliff})");
    Check(flat == 1 || flat == 3 || flat == 6, $"flat plain -> grass/dirt/sand (got {flat})");
    Console.WriteLine(fails == 0 ? "MATGEN TESTS PASSED" : $"MATGEN TESTS FAILED ({fails})");
    return fails == 0 ? 0 : 1;
}

if (arg == "surfacebake")
{
    // Surface-atlas bake gate (self-contained): each material cell maps through matToSurf to a surface slot.
    int fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }
    var mat = new MaterialMap(2, 2);
    mat[0, 0] = 1; mat[1, 0] = 1; mat[0, 1] = 9; mat[1, 1] = 9;          // top row material 1, bottom row material 9
    var surfaces = new RefractorForge.Render.Texture2D?[16];
    for (int i = 0; i < 16; i++) surfaces[i] = new RefractorForge.Render.Texture2D(1, 1, new byte[] { (byte)(i * 16), (byte)(255 - i * 16), 0, 255 });
    int[] matToSurf = { 0, 2, 3, 4, 5, 6, 10, 11, 8, 12, 14, 13, 15, 15, 9, 1 };
    int atlasSize = 4;
    var atlas = RefractorForge.Render.TerrainTexture.BakeAtlasFromMaterial(mat, surfaces, matToSurf, atlasSize, 256f, 8f);
    Check(atlas.Width == atlasSize && atlas.Height == atlasSize, "atlas is atlasSize^2");
    int top = matToSurf[1], bot = matToSurf[9];
    Check(atlas.Rgba[0] == (byte)(top * 16) && atlas.Rgba[1] == (byte)(255 - top * 16), $"material 1 -> surf slot {top} colour");
    int ob = ((atlasSize - 1) * atlasSize + 0) * 4;
    Check(atlas.Rgba[ob] == (byte)(bot * 16) && atlas.Rgba[ob + 1] == (byte)(255 - bot * 16), $"material 9 -> surf slot {bot} colour");
    Console.WriteLine(fails == 0 ? "SURFACEBAKE TESTS PASSED" : $"SURFACEBAKE TESTS FAILED ({fails})");
    return fails == 0 ? 0 : 1;
}

if (arg == "navcompress" && args.Length >= 2)
{
    // RE the COMPRESSED pathmap .raw (the form ai.loadMaps reads) using matched compressed+8Bit pairs.
    // Header = 8x int32 LE [bb,bb,cb,level,0,2,0,-1] (bb+cb=11, side=64*2^bb). 8Bit: 0x00 passable / 0xFF blocked
    // (per Cajunwolf: black=can-go, white=cannot). In the compressed bitmap a SET bit = blocked (matched block(1,1)).
    // Strategy: hash every 8Bit 64x64 block under all 8 dihedral orientations (bit=blocked), then slide a 512-byte
    // window over the whole compressed file and report every offset that is a verbatim block bitmap -> reveals the
    // block order / where bitmaps live / which blocks are encoded differently.
    var dir = args[1];
    var comps = System.IO.Directory.EnumerateFiles(dir, "*Map.raw", System.IO.SearchOption.AllDirectories)
        .Where(p => !p.EndsWith("8Bit.raw", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p).ToList();
    Console.WriteLine($"navcompress: {comps.Count} compressed maps under {dir}");
    bool allOk = comps.Count > 0;
    foreach (var cp in comps)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(cp);
        var e8 = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(cp)!, name + "8Bit.raw");
        if (!System.IO.File.Exists(e8)) continue;
        var comp = System.IO.File.ReadAllBytes(cp);
        var eight = System.IO.File.ReadAllBytes(e8);
        int Hi(int i) => BitConverter.ToInt32(comp, i * 4);
        int bb = Hi(0), cb = Hi(2), level = Hi(3), nb = 1 << bb, side = nb * 64;
        Console.WriteLine($"\n{name}: hdr[{Hi(0)},{Hi(1)},{Hi(2)},{Hi(3)},{Hi(4)},{Hi(5)},{Hi(6)},{Hi(7)}]  comp {comp.Length}B  8bit {eight.Length} ({side}^2, {nb}x{nb} blocks, level {level})");
        if (eight.Length != side * side) { Console.WriteLine($"  !! 8bit size {eight.Length} != side^2 {side * side}"); continue; }
        // DECODE via the codec and verify vs the real 8Bit; then ENCODE the real 8Bit and verify BYTE-EXACT vs
        // the real compressed file (the writer gate).
        byte[] dec; int dside, dlevel;
        try { dec = RefractorForge.Formats.Terrain.CompressedSearchMap.Decode(comp, out dside, out dlevel); }
        catch (Exception ex) { Console.WriteLine($"  DECODE FAILED: {ex.Message}"); allOk = false; continue; }
        int ddiff = dec.Length != eight.Length ? -1 : 0;
        if (ddiff == 0) for (int i = 0; i < dec.Length; i++) if (dec[i] != eight[i]) ddiff++;
        var enc = RefractorForge.Formats.Terrain.CompressedSearchMap.Encode(eight, side, dlevel);
        int ediff = enc.Length != comp.Length ? -1 : 0, efirst = -1;
        if (ediff == 0) for (int i = 0; i < enc.Length; i++) if (enc[i] != comp[i]) { ediff++; if (efirst < 0) efirst = i; }
        bool decOk = ddiff == 0, encOk = ediff == 0;
        if (!decOk || !encOk) allOk = false;
        Console.WriteLine($"  DECODE vs 8Bit: {(ddiff == 0 ? "100% EXACT" : ddiff < 0 ? "SIZE MISMATCH" : $"{ddiff} cells differ")}"
            + $"   ENCODE vs real .raw: {(encOk ? "BYTE-EXACT" : ediff < 0 ? $"SIZE {enc.Length} vs {comp.Length}" : $"{ediff} bytes differ (first @ {efirst})")}");
    }
    Console.WriteLine(allOk ? "\nNAVCOMPRESS TESTS PASSED" : "\nNAVCOMPRESS TESTS FAILED");
    return allOk ? 0 : 1;
}

if (arg == "navcalib" && args.Length >= 2)
{
    // Reverse-engineer the real passability rule at Level2 (512^2 == heightmap). Two questions the naive
    // per-cell stats can't answer: (1) the exact grid ORIENTATION (navtest picked yx by raw agreement, which
    // ~= base rate, so it's weak), and (2) whether the macro structure is flood-fill REACHABILITY from spawns,
    // not just a per-cell test. Resolve orientation via the strong water signal (Youden's J, base-rate-robust),
    // then test flood-fill.
    var dir = args[1];
    string Find(string n) => Directory.EnumerateFiles(dir, n, SearchOption.AllDirectories).First();
    var cfg = TerrainConfig.Load(Find("Terrain.con"));
    var hm = Heightmap.LoadForMaterialSize(Find("Heightmap.raw"), cfg.MaterialSize);
    int N = cfg.MaterialSize;
    float sp = cfg.HorizontalSpacing, water = cfg.WaterLevel;
    Console.WriteLine($"heightmap {N}x{N}, spacing {sp} m, waterLevel {water} m");

    // 8 dihedral mappings nav(x,y) -> heightmap(hx,hy).
    (int hx, int hy) Map(int ori, int x, int y) => ori switch
    {
        0 => (x, y),
        1 => (y, x),                       // transpose
        2 => (N - 1 - x, y),               // flipX
        3 => (x, N - 1 - y),               // flipY
        4 => (N - 1 - x, N - 1 - y),       // rot180
        5 => (y, N - 1 - x),               // rot90
        6 => (N - 1 - y, x),               // rot270
        _ => (N - 1 - y, N - 1 - x),       // transpose+rot180
    };
    string[] oriName = { "id", "yx", "flipX", "flipY", "rot180", "rot90", "rot270", "yx+rot180" };

    byte[] Load(string f) { var p = Directory.EnumerateFiles(dir, f, SearchOption.AllDirectories).FirstOrDefault(); return p is null ? Array.Empty<byte>() : File.ReadAllBytes(p); }
    var tank = Load("Tank0Level2Map8Bit.raw");
    if (tank.Length != N * N) { Console.WriteLine("no Tank Level2 map"); return 1; }

    // (1) ORIENTATION via the water model (passable <=> dry) on the Tank map. Youden J = sens + spec - 1.
    Console.WriteLine("\n-- orientation search (Tank, model: passable <=> height>=water) --");
    int bestOri = 0; double bestJ = -1;
    for (int ori = 0; ori < 8; ori++)
    {
        long tp = 0, tn = 0, fp = 0, fn = 0;
        for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
        {
            var (hx, hy) = Map(ori, x, y);
            bool model = cfg.HeightToMeters(hm[hx, hy]) >= water;     // predicted passable (dry)
            bool real = tank[y * N + x] == 0xff;
            if (model && real) tp++; else if (!model && !real) tn++; else if (model && !real) fp++; else fn++;
        }
        double sens = (double)tp / Math.Max(1, tp + fn), spec = (double)tn / Math.Max(1, tn + fp);
        double j = sens + spec - 1, agree = 100.0 * (tp + tn) / (N * N);
        Console.WriteLine($"  {oriName[ori],-10} agree {agree,5:0.0}%  sens {sens:0.00} spec {spec:0.00}  J {j:0.000}");
        if (j > bestJ) { bestJ = j; bestOri = ori; }
    }
    Console.WriteLine($"  => best orientation by water signal: {oriName[bestOri]} (J {bestJ:0.000})");

    // height in nav space using the winning orientation.
    var H = new float[N, N];
    for (int y = 0; y < N; y++) for (int x = 0; x < N; x++) { var (hx, hy) = Map(bestOri, x, y); H[x, y] = cfg.HeightToMeters(hm[hx, hy]); }
    float Slope(int x, int y)
    {
        float h = H[x, y], g = 0;
        if (x > 0) g = MathF.Max(g, MathF.Abs(H[x - 1, y] - h));
        if (x < N - 1) g = MathF.Max(g, MathF.Abs(H[x + 1, y] - h));
        if (y > 0) g = MathF.Max(g, MathF.Abs(H[x, y - 1] - h));
        if (y < N - 1) g = MathF.Max(g, MathF.Abs(H[x, y + 1] - h));
        return MathF.Atan2(g, sp) * 180f / MathF.PI;
    }

    // (2) FLOOD-FILL test (Tank): local-passable = dry & slope<=cap, then keep only cells reachable (4-conn)
    // from the map centre. Compare connectivity-pruned vs raw local test.
    Console.WriteLine("\n-- flood-fill reachability test (Tank, dry & slope<=35) --");
    var local = new bool[N, N];
    for (int y = 0; y < N; y++) for (int x = 0; x < N; x++) local[x, y] = H[x, y] >= water && Slope(x, y) <= 35f;
    var reach = new bool[N, N];
    var stack = new Stack<(int, int)>();
    // seed: nearest local-passable cell to centre
    for (int r = 0; r < N && stack.Count == 0; r++)
        for (int dy = -r; dy <= r && stack.Count == 0; dy++)
            for (int dx = -r; dx <= r && stack.Count == 0; dx++)
            { int sx = N / 2 + dx, sy = N / 2 + dy; if (sx >= 0 && sy >= 0 && sx < N && sy < N && local[sx, sy]) stack.Push((sx, sy)); }
    while (stack.Count > 0)
    {
        var (cx, cy) = stack.Pop();
        if (cx < 0 || cy < 0 || cx >= N || cy >= N || reach[cx, cy] || !local[cx, cy]) continue;
        reach[cx, cy] = true;
        stack.Push((cx + 1, cy)); stack.Push((cx - 1, cy)); stack.Push((cx, cy + 1)); stack.Push((cx, cy - 1));
    }
    long la = 0, ra = 0; int lp = 0, rp = 0;
    for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
    {
        bool real = tank[y * N + x] == 0xff;
        if (local[x, y] == real) la++;
        if (reach[x, y] == real) ra++;
        if (local[x, y]) lp++;
        if (reach[x, y]) rp++;
    }
    Console.WriteLine($"  local-only:   {100.0 * la / (N * N):0.0}% agree  ({100.0 * lp / (N * N):0.0}% passable)");
    Console.WriteLine($"  flood-filled: {100.0 * ra / (N * N):0.0}% agree  ({100.0 * rp / (N * N):0.0}% passable)");
    Console.WriteLine($"  real Tank:    {100.0 * tank.Count(b => b == 0xff) / (N * N):0.0}% passable");
    return 0;
}

if (arg == "navfol" && args.Length >= 2)
{
    // Does FOLIAGE (overgrowth=trees, undergrowth=brush) explain the central dry-blocked blob? Decompose the
    // real Tank Level2 blocked cells (HM space, rot90) into water / foliage / unexplained, and report the
    // agreement lift of a (dry & !dense-foliage) model.
    var dir = args[1];
    string Find(string n) => Directory.EnumerateFiles(dir, n, SearchOption.AllDirectories).First();
    var cfg = TerrainConfig.Load(Find("Terrain.con"));
    var hm = Heightmap.LoadForMaterialSize(Find("Heightmap.raw"), cfg.MaterialSize);
    int N = cfg.MaterialSize; float water = cfg.WaterLevel;
    var g = GrowthMaps.LoadFolder(dir);
    Console.WriteLine($"hm {N}^2 water {water}m;  over {(g.HasOver ? g.OverSide + "^2" : "none")}  under {(g.HasUnder ? g.UnderSide + "^2" : "none")}");
    var rb = File.ReadAllBytes(Find("Tank0Level2Map8Bit.raw"));
    int ori = 5;
    (int hx, int hy) Map(int x, int y) => ori switch { 1 => (y, x), 5 => (y, N - 1 - x), 6 => (N - 1 - y, x), _ => (N - 1 - y, N - 1 - x) };
    bool Over(int hx, int hy) { if (!g.HasOver) return false; int s = g.OverSide; int ox = hx * s / N, oy = hy * s / N; return g.Over![Math.Min(ox, s - 1), Math.Min(oy, s - 1)] > 0; }
    bool Under(int hx, int hy) { if (!g.HasUnder) return false; int s = g.UnderSide; int ux = hx * s / N, uy = hy * s / N; return g.Under![Math.Min(ux, s - 1), Math.Min(uy, s - 1)] > 0; }

    long bl = 0, wOnly = 0, foOnly = 0, both = 0, none = 0;
    long agW = 0, agWO = 0, agWOU = 0;
    for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
    {
        var (hx, hy) = Map(x, y);
        bool real = rb[y * N + x] == 0xff;
        bool dry = cfg.HeightToMeters(hm[hx, hy]) >= water;
        bool over = Over(hx, hy), under = Under(hx, hy);
        if (dry == real) agW++;                                   // water-only model
        if ((dry && !over) == real) agWO++;                       // + overgrowth
        if ((dry && !over && !under) == real) agWOU++;            // + under+overgrowth
        if (!real)
        {
            bl++;
            bool w = !dry, f = over || under;
            if (w && f) both++; else if (w) wOnly++; else if (f) foOnly++; else none++;
        }
    }
    Console.WriteLine($"real blocked decomposition: water {100.0 * wOnly / bl:0.0}%  foliage {100.0 * foOnly / bl:0.0}%  both {100.0 * both / bl:0.0}%  UNEXPLAINED {100.0 * none / bl:0.0}%");

    // foliage is DENSE where it blocks: sweep the over/under index thresholds (>=T blocks) to find the cutoff.
    byte OverV(int hx, int hy) { if (!g.HasOver) return 0; int s = g.OverSide; return g.Over![Math.Min(hx * s / N, s - 1), Math.Min(hy * s / N, s - 1)]; }
    byte UnderV(int hx, int hy) { if (!g.HasUnder) return 0; int s = g.UnderSide; return g.Under![Math.Min(hx * s / N, s - 1), Math.Min(hy * s / N, s - 1)]; }
    // mean indices passable vs blocked
    double poN = 0, poS = 0, puS = 0, boN = 0, boS = 0, buS = 0;
    for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
    {
        var (hx, hy) = Map(x, y); bool real = rb[y * N + x] == 0xff;
        if (real) { poN++; poS += OverV(hx, hy); puS += UnderV(hx, hy); } else { boN++; boS += OverV(hx, hy); buS += UnderV(hx, hy); }
    }
    Console.WriteLine($"mean over-idx  passable {poS / poN:0.0}  blocked {boS / boN:0.0}   mean under-idx  passable {puS / poN:0.0}  blocked {buS / boN:0.0}");
    (int to, int tu, double ag) best = (0, 0, 0);
    for (int to = 1; to <= 15; to++) for (int tu = 1; tu <= 15; tu++)
    {
        long ag = 0;
        for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
        {
            var (hx, hy) = Map(x, y);
            bool model = cfg.HeightToMeters(hm[hx, hy]) >= water && OverV(hx, hy) < to && UnderV(hx, hy) < tu;
            if (model == (rb[y * N + x] == 0xff)) ag++;
        }
        if (ag > best.ag) best = (to, tu, ag);
    }
    Console.WriteLine($"BEST: passable = dry & over<{best.to} & under<{best.tu}  ->  {100.0 * best.ag / (N * N):0.0}% agreement  (real {100.0 * rb.Count(b => b == 0xff) / (N * N):0.0}% passable)");
    return 0;
}

if (arg == "navdump" && args.Length >= 2)
{
    // Eyeball the real Tank Level2 map vs the underwater mask (HM space, rot90) at 64x64, to spot gross
    // misalignment vs a genuinely irreducible residual.  legend: '.'=pass  '#'=blocked  and overlay panel:
    // ' '=dry-pass  '~'=wet  'X'=blocked-but-DRY (the unexplained residual)  'o'=blocked-and-wet (expected)
    var dir = args[1];
    string Find(string n) => Directory.EnumerateFiles(dir, n, SearchOption.AllDirectories).First();
    var cfg = TerrainConfig.Load(Find("Terrain.con"));
    var hm = Heightmap.LoadForMaterialSize(Find("Heightmap.raw"), cfg.MaterialSize);
    int N = cfg.MaterialSize; float water = cfg.WaterLevel;
    var rb = File.ReadAllBytes(Find("Tank0Level2Map8Bit.raw"));
    int ori = args.Length >= 3 ? int.Parse(args[2]) : 5;
    (int hx, int hy) Map(int x, int y) => ori switch { 1 => (y, x), 5 => (y, N - 1 - x), 6 => (N - 1 - y, x), _ => (N - 1 - y, N - 1 - x) };
    var pass = new bool[N, N]; var wet = new bool[N, N];
    for (int y = 0; y < N; y++) for (int x = 0; x < N; x++) { var (hx, hy) = Map(x, y); pass[hx, hy] = rb[y * N + x] == 0xff; wet[hx, hy] = cfg.HeightToMeters(hm[hx, hy]) < water; }
    int S = 64, step = N / S;
    Console.WriteLine($"ori {ori}; left=real(.=pass #=block)  right=overlay( ' 'dry-pass ~wet Xblock-DRY o block-wet )");
    for (int gy = 0; gy < S; gy++)
    {
        var a = new System.Text.StringBuilder(); var b = new System.Text.StringBuilder();
        for (int gx = 0; gx < S; gx++)
        {
            int np = 0, nw = 0, n = 0;
            for (int dy = 0; dy < step; dy++) for (int dx = 0; dx < step; dx++) { int hx = gx * step + dx, hy = gy * step + dy; if (hx < N && hy < N) { n++; if (pass[hx, hy]) np++; if (wet[hx, hy]) nw++; } }
            bool p = np * 2 >= n, w = nw * 2 >= n;
            a.Append(p ? '.' : '#');
            b.Append(p ? (w ? '~' : ' ') : (w ? 'o' : 'X'));
        }
        Console.WriteLine(a + "  " + b);
    }
    return 0;
}

if (arg == "navflood" && args.Length >= 2)
{
    // Is the ~45% unexplained-blocked just UNREACHABLE terrain? Flood-fill (8-conn) from the real gameplay
    // seeds (control points + spawns) over a permissive local rule, in heightmap space, vs real Tank Level2.
    var dir = args[1];
    string Find(string n) => Directory.EnumerateFiles(dir, n, SearchOption.AllDirectories).First();
    var cfg = TerrainConfig.Load(Find("Terrain.con"));
    var hm = Heightmap.LoadForMaterialSize(Find("Heightmap.raw"), cfg.MaterialSize);
    int N = cfg.MaterialSize; float sp = cfg.HorizontalSpacing, water = cfg.WaterLevel;
    var gp = GameplayObjects.LoadFolder(dir);
    var seeds = new List<(int x, int y)>();
    void Seed(Vec3 p) { int x = (int)(p.X / sp), y = (int)(p.Z / sp); if (x >= 0 && y >= 0 && x < N && y < N) seeds.Add((x, y)); }
    foreach (var c in gp.ControlPoints) Seed(c.Position);
    foreach (var v in gp.VehicleSpawns) Seed(v.Position);
    foreach (var s in gp.SoldierSpawns) Seed(s.Position);
    Console.WriteLine($"hm {N}^2 sp {sp}m water {water}m;  {seeds.Count} gameplay seeds");

    var rb = File.ReadAllBytes(Find("Tank0Level2Map8Bit.raw"));
    (int hx, int hy) Map(int ori, int x, int y) => ori switch { 1 => (y, x), _ => (y, N - 1 - x) }; // yx | rot90
    float H(int hx, int hy) => cfg.HeightToMeters(hm[hx, hy]);
    float SlopeDeg(int hx, int hy)
    {
        float h = H(hx, hy), g = 0;
        if (hx > 0) g = MathF.Max(g, MathF.Abs(H(hx - 1, hy) - h));
        if (hx < N - 1) g = MathF.Max(g, MathF.Abs(H(hx + 1, hy) - h));
        if (hy > 0) g = MathF.Max(g, MathF.Abs(H(hx, hy - 1) - h));
        if (hy < N - 1) g = MathF.Max(g, MathF.Abs(H(hx, hy + 1) - h));
        return MathF.Atan2(g, sp) * 180f / MathF.PI;
    }
    foreach (int ori in new[] { 1, 5 })
    {
        var realHM = new bool[N, N];
        for (int y = 0; y < N; y++) for (int x = 0; x < N; x++) { var (hx, hy) = Map(ori, x, y); realHM[hx, hy] = rb[y * N + x] == 0xff; }
        foreach (var (label, localFn) in new (string, Func<int, int, bool>)[]
        {
            ("dry",            (hx, hy) => H(hx, hy) >= water),
            ("dry&slope<=30",  (hx, hy) => H(hx, hy) >= water && SlopeDeg(hx, hy) <= 30f),
            ("dry-0.5wade",    (hx, hy) => H(hx, hy) >= water - 0.5f),
        })
        {
            var local = new bool[N, N];
            for (int y = 0; y < N; y++) for (int x = 0; x < N; x++) local[x, y] = localFn(x, y);
            var reach = new bool[N, N];
            var st = new Stack<(int, int)>();
            foreach (var (sx, sy) in seeds) if (local[sx, sy]) st.Push((sx, sy));
            while (st.Count > 0)
            {
                var (cx, cy) = st.Pop();
                if (cx < 0 || cy < 0 || cx >= N || cy >= N || reach[cx, cy] || !local[cx, cy]) continue;
                reach[cx, cy] = true;
                st.Push((cx + 1, cy)); st.Push((cx - 1, cy)); st.Push((cx, cy + 1)); st.Push((cx, cy - 1));
                st.Push((cx + 1, cy + 1)); st.Push((cx - 1, cy - 1)); st.Push((cx + 1, cy - 1)); st.Push((cx - 1, cy + 1));
            }
            long agL = 0, agR = 0; int pL = 0, pR = 0;
            for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
            { if (local[x, y] == realHM[x, y]) agL++; if (reach[x, y] == realHM[x, y]) agR++; if (local[x, y]) pL++; if (reach[x, y]) pR++; }
            string oriName = ori == 1 ? "yx" : "rot90";
            Console.WriteLine($"  [{oriName,-5}] local={label,-14} local {100.0 * agL / (N * N),5:0.0}% ({100.0 * pL / (N * N):0.0}%pass)  flood {100.0 * agR / (N * N),5:0.0}% ({100.0 * pR / (N * N):0.0}%pass)");
        }
    }
    Console.WriteLine($"  real Tank: {100.0 * rb.Count(b => b == 0xff) / (N * N):0.0}% passable");
    return 0;
}

if (arg == "navobj" && args.Length >= 3)
{
    // Decompose the real Tank Level2 BLOCKED cells into water / object-footprint / unexplained, in heightmap
    // space (col=X/sp, row=Z/sp, the known world mapping), across the 4 transpose orientations. Tells us how
    // much of the residual (the part water alone can't explain) is static objects.
    var dir = args[1];
    var archives = args.Skip(2).Where(a => a.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase)).ToArray();
    string Find(string n) => Directory.EnumerateFiles(dir, n, SearchOption.AllDirectories).First();
    var cfg = TerrainConfig.Load(Find("Terrain.con"));
    var hm = Heightmap.LoadForMaterialSize(Find("Heightmap.raw"), cfg.MaterialSize);
    int N = cfg.MaterialSize; float sp = cfg.HorizontalSpacing, water = cfg.WaterLevel;
    var so = StaticObjectsFile.Load(Find("StaticObjects.con"));
    var lib = RefractorForge.Render.MeshLibrary.Open(archives);
    Console.WriteLine($"hm {N}^2  sp {sp}m  water {water}m;  {so.Objects.Count} objects;  {archives.Length} archive(s)");

    // object footprints rasterized into heightmap-cell space.
    var obj = new bool[N, N];
    int resolved = 0;
    foreach (var o in so.Objects)
    {
        if (!lib.TryGet(o.Template, out var mesh) && !lib.TryGetAssembledMesh(o.Template, out mesh)) continue;
        if (mesh.Positions.Length == 0) continue;
        resolved++;
        float minx = 1e9f, maxx = -1e9f, miny = 1e9f, maxy = -1e9f, minz = 1e9f, maxz = -1e9f;
        foreach (var p in mesh.Positions) { minx = MathF.Min(minx, p.X); maxx = MathF.Max(maxx, p.X); miny = MathF.Min(miny, p.Y); maxy = MathF.Max(maxy, p.Y); minz = MathF.Min(minz, p.Z); maxz = MathF.Max(maxz, p.Z); }
        float scale = o.Scale ?? 1f;
        float rad = MathF.Max(maxx - minx, maxz - minz) * 0.5f * scale;
        float hgt = (maxy - miny) * scale;
        if (hgt < 0.3f) continue;   // lowClip-ish: skip flat ground decals
        int icx = (int)(o.Position.X / sp), icz = (int)(o.Position.Z / sp);
        int rc = Math.Max(0, (int)MathF.Round(rad / sp));
        for (int dz = -rc; dz <= rc; dz++) for (int dx = -rc; dx <= rc; dx++)
        {
            int hx = icx + dx, hy = icz + dz;
            if (hx < 0 || hy < 0 || hx >= N || hy >= N) continue;
            if (dx * dx + dz * dz <= rc * rc) obj[hx, hy] = true;
        }
    }
    int objCells = 0; for (int y = 0; y < N; y++) for (int x = 0; x < N; x++) if (obj[x, y]) objCells++;
    Console.WriteLine($"resolved {resolved}/{so.Objects.Count} meshes;  object-blocked {objCells} cells ({100.0 * objCells / (N * N):0.0}%)\n");

    var rb = File.ReadAllBytes(Find("Tank0Level2Map8Bit.raw"));
    (int hx, int hy) Map(int ori, int x, int y) => ori switch { 1 => (y, x), 5 => (y, N - 1 - x), 6 => (N - 1 - y, x), _ => (N - 1 - y, N - 1 - x) };
    var nm = new Dictionary<int, string> { [1] = "yx", [5] = "rot90", [6] = "rot270", [7] = "yx+rot180" };
    foreach (int ori in new[] { 1, 5, 6, 7 })
    {
        var realHM = new bool[N, N];
        for (int y = 0; y < N; y++) for (int x = 0; x < N; x++) { var (hx, hy) = Map(ori, x, y); realHM[hx, hy] = rb[y * N + x] == 0xff; }
        long bl = 0, blUW = 0, blObj = 0, blBoth = 0, blNone = 0, agree = 0;
        for (int hy = 0; hy < N; hy++) for (int hx = 0; hx < N; hx++)
        {
            bool dry = cfg.HeightToMeters(hm[hx, hy]) >= water;
            bool model = dry && !obj[hx, hy];   // predicted passable
            if (model == realHM[hx, hy]) agree++;
            if (!realHM[hx, hy])
            {
                bl++;
                if (!dry && obj[hx, hy]) blBoth++;
                else if (!dry) blUW++;
                else if (obj[hx, hy]) blObj++;
                else blNone++;
            }
        }
        Console.WriteLine($"  [{nm[ori],-9}] model(dry & !obj) agree {100.0 * agree / (N * N),5:0.0}%   of blocked: water {100.0 * blUW / bl,4:0.0}%  obj {100.0 * blObj / bl,4:0.0}%  both {100.0 * blBoth / bl,4:0.0}%  UNEXPLAINED {100.0 * blNone / bl,4:0.0}%");
    }
    return 0;
}

if (arg == "rfatree" && args.Length >= 2)
{
    // Dump the internal folder structure of an .rfa (to see whether object category is encoded in the path).
    var arc = RefractorForge.Formats.Rfa.RfaArchive.Open(args[1]);
    Console.WriteLine($"{arc.Entries.Count} entries in {Path.GetFileName(args[1])}");
    // a few sample .sm paths
    Console.WriteLine("---- sample .sm paths ----");
    foreach (var e in arc.Entries.Where(e => e.Name.EndsWith(".sm", StringComparison.OrdinalIgnoreCase)).Take(6))
        Console.WriteLine("  " + e.Name.Replace('\\', '/'));
    // distinct folder prefixes (depth 1..3) by count
    var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    foreach (var e in arc.Entries)
    {
        var segs = e.Name.Replace('\\', '/').Split('/');
        for (int d = 1; d <= Math.Min(3, segs.Length - 1); d++)
            { var pre = string.Join("/", segs.Take(d)); counts[pre] = counts.GetValueOrDefault(pre) + 1; }
    }
    Console.WriteLine("---- top folder prefixes (count) ----");
    foreach (var kv in counts.OrderByDescending(k => k.Value).Take(30)) Console.WriteLine($"  {kv.Value,6}  {kv.Key}");
    return 0;
}

if (arg == "rfadump" && args.Length >= 2)
{
    // Decompress the first entry whose name contains <substr> and dump <n> header bytes (hex + ascii), to learn
    // an unknown in-archive format. e.g. rfadump treeMesh.rfa .tm 96
    var arc = RefractorForge.Formats.Rfa.RfaArchive.Open(args[1]);
    string sub = args.Length >= 3 ? args[2] : "";
    int n = args.Length >= 4 ? int.Parse(args[3]) : 64;
    Console.WriteLine($"{arc.Entries.Count} entries in {Path.GetFileName(args[1])}");
    foreach (var e in arc.Entries)
    {
        if (sub.Length > 0 && !e.Name.Contains(sub, StringComparison.OrdinalIgnoreCase)) continue;
        byte[] data; try { data = arc.Read(e); } catch (Exception ex) { Console.WriteLine($"  {e.Name}: read FAILED {ex.Message}"); break; }
        int take = Math.Min(n, data.Length);
        Console.WriteLine($"\n{e.Name}  unc={e.UncompressedSize} blk={e.BlockSize} (read {data.Length})");
        Console.WriteLine("  hex: " + string.Join(" ", data.Take(take).Select(b => b.ToString("x2"))));
        Console.WriteLine("  asc: " + new string(data.Take(take).Select(b => b >= 32 && b < 127 ? (char)b : '.').ToArray()));
        break;
    }
    return 0;
}

if (arg == "treemesh" && args.Length >= 2)
{
    // Exhaustive TreeMesh (.tm) parser gate: parse every .tm in treeMesh.rfa, verify byte-exact consumption +
    // valid index/vertex ranges. e.g. treemesh "D:\...\treeMesh.rfa"
    int fails = 0, ok = 0;
    void Check(bool c, string what) { Console.WriteLine($"  [{(c ? "PASS" : "FAIL")}] {what}"); if (!c) fails++; }
    var arc = RefractorForge.Formats.Rfa.RfaArchive.Open(args[1]);
    var tms = arc.Entries.Where(e => e.Name.EndsWith(".tm", StringComparison.OrdinalIgnoreCase)).ToList();
    Console.WriteLine($"{tms.Count} .tm entries in {Path.GetFileName(args[1])}");
    int consumedMismatch = 0, rangeBad = 0, refBad = 0, verBad = 0, convBad = 0;
    long totalV = 0, totalI = 0; var bad = new List<string>();
    foreach (var e in tms)
    {
        byte[] data = arc.Read(e);
        RefractorForge.Formats.Rfa.TreeMesh tm;
        try { tm = RefractorForge.Formats.Rfa.TreeMesh.Parse(data); }
        catch (Exception ex) { fails++; bad.Add($"{e.Name}: throw {ex.Message}"); continue; }
        ok++; totalV += tm.Vertices.Length; totalI += tm.Indices.Length;
        if (tm.Version != 3) { verBad++; bad.Add($"{e.Name}: ver {tm.Version}"); }
        if (tm.Consumed != data.Length) { consumedMismatch++; bad.Add($"{e.Name}: consumed {tm.Consumed}/{data.Length}"); }
        bool rng = false; foreach (var g in tm.Groups) foreach (var m in g) if (m.Start < 0 || m.Start + m.Count * 3 > tm.Indices.Length) rng = true;
        if (rng) { rangeBad++; bad.Add($"{e.Name}: bad index range"); }
        bool rf = false; foreach (var ix in tm.Indices) if (ix >= tm.Vertices.Length) { rf = true; break; }
        if (rf) { refBad++; bad.Add($"{e.Name}: index out of vertex range"); }
        var mesh = RefractorForge.Render.MeshLibrary.MeshFromTreeMesh(tm);
        bool cv = mesh.Positions.Length == tm.Vertices.Length && mesh.Uvs.Length == tm.Vertices.Length;
        foreach (var pt in mesh.Parts) foreach (var ix in pt.Indices) if (ix < 0 || ix >= mesh.Positions.Length) { cv = false; break; }
        if (!cv) { convBad++; bad.Add($"{e.Name}: conversion invalid"); }
    }
    Check(ok == tms.Count, $"all {tms.Count} .tm parsed without throwing (ok={ok})");
    Check(verBad == 0, $"every .tm is version 3 (bad={verBad})");
    Check(consumedMismatch == 0, $"every .tm consumes byte-exact (mismatch={consumedMismatch})");
    Check(rangeBad == 0, $"every material's index range is in-bounds (bad={rangeBad})");
    Check(refBad == 0, $"every index references a valid vertex (bad={refBad})");
    Check(convBad == 0, $"every .tm converts to a valid render Mesh (bad={convBad})");
    Console.WriteLine($"  totals: {totalV} verts, {totalI} indices");
    foreach (var bn in bad.Take(12)) Console.WriteLine("   - " + bn);
    Console.WriteLine(fails == 0 ? "TREEMESH TESTS PASSED" : $"TREEMESH TESTS FAILED ({fails})");
    return fails == 0 ? 0 : 1;
}

if (arg == "catalogcheck" && args.Length >= 2)
{
    // Mirror the Viewer's LoadCatalog: open the given mesh archive(s) and build the SAME grouped left-panel
    // list, proving the object list reflects WHICHEVER objects.rfa is loaded (not a hardcoded BFV list).
    var archives = args.Skip(1).Where(a => a.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase)).ToArray();
    var lib = RefractorForge.Render.MeshLibrary.Open(archives);
    Console.WriteLine($"opened {archives.Length} archive(s); .sm meshes = {lib.MeshCount}; assembled (vehicles/weapons) = {lib.AssembledTemplateNames.Count()}");

    var catPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "RefractorForge.Viewer", "objcatalog.json"));
    Dictionary<string, string[]>? dict = null;
    try { if (File.Exists(catPath)) dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(catPath)); } catch { }
    (string key, string label)[] order =
    {
        ("STRUCTURES","Structures"),("VEGETATION","Vegetation"),("OVERGROWTH","Overgrowth"),("UNDERGROWTH","Undergrowth"),
        ("LAND_VEHICLES","Land Vehicles"),("WATER_VEHICLES","Water Vehicles"),("AIR_VEHICLES","Air Vehicles"),
        ("STATIONARY_WEAPONS","Stationary Weapons"),("PROPS_HIGH","Props"),("PROPS_LOW","Props (Low)"),
        ("USABLE_ITEMS","Pickups"),("EFFECTS","Effects"),("TUNNEL_OBJECTS","Tunnels"),("C99_MESHES","Destructibles"),
    };
    static string Stem(string n)
    {
        var s = n.EndsWith(".sm", StringComparison.OrdinalIgnoreCase) ? n[..^3] : n;
        return System.Text.RegularExpressions.Regex.Replace(s, @"_(?:m\d+|l\d+|lod\d+)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
    var present = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    void Add(string name) { var k = Stem(name).ToLowerInvariant(); if (!present.ContainsKey(k)) present[k] = Stem(name); }
    foreach (var bn in lib.MeshBaseNames) Add(bn);
    foreach (var v in lib.AssembledTemplateNames) present[v.ToLowerInvariant()] = v;
    var labelOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (dict is not null) foreach (var (key, label) in order) if (dict.TryGetValue(key, out var items)) foreach (var t in items) labelOf[Stem(t).ToLowerInvariant()] = label;
    var cats = lib.CategoryOf;   // primary: the archive's own folder structure (auto, any mod)
    var byLabel = new Dictionary<string, List<string>>();
    foreach (var kv in present)
    {
        string label = cats.TryGetValue(kv.Key, out var c) ? c : labelOf.TryGetValue(kv.Key, out var l) ? l : "Other";
        if (!byLabel.TryGetValue(label, out var list)) byLabel[label] = list = new();
        list.Add(kv.Value);
    }
    string[] pref = { "Structures", "Vegetation", "Overgrowth", "Undergrowth", "Land Vehicles", "Water Vehicles", "Air Vehicles", "Vehicles", "Stationary Weapons", "Hand Weapons", "Soldiers", "Props", "Props (Low)", "Pickups", "Effects", "Tunnels", "Destructibles", "Misc" };
    Console.WriteLine("---- left-panel catalog for this archive ----");
    void Emit(string label)
    {
        if (byLabel.TryGetValue(label, out var items) && items.Count > 0)
        { Console.WriteLine($"  {label,-18} {items.Count,4}   e.g. {string.Join(", ", items.OrderBy(x => x).Take(4))}"); byLabel.Remove(label); }
    }
    foreach (var label in pref) Emit(label);
    foreach (var label in byLabel.Keys.Where(k => k != "Other").OrderBy(k => k).ToArray()) Emit(label);
    Emit("Other");
    return 0;
}

if (arg == "materialbrush" && args.Length >= 2)
{
    var lvl = LevelArchive.IsRfa(args[1])
        ? LevelArchive.FromRfa(args[1])
        : null;
    TerrainConfig cfg2; MaterialMap map;
    if (lvl is not null) { cfg2 = lvl.Config; map = lvl.Material ?? throw new Exception("no material map in rfa"); }
    else
    {
        cfg2 = TerrainConfig.Load(Directory.EnumerateFiles(args[1], "Terrain.con", SearchOption.AllDirectories).First());
        var mp = Directory.EnumerateFiles(args[1], "MaterialMap.raw", SearchOption.AllDirectories).First();
        map = MaterialMap.LoadForMaterialSize(mp, cfg2.MaterialSize);
    }
    Console.WriteLine($"material map {map.Width}x{map.Height}, spacing {cfg2.HorizontalSpacing}m");
    var painter = new MaterialPainter(map, cfg2);
    float wx = 30 * cfg2.HorizontalSpacing, wz = 30 * cfg2.HorizontalSpacing;
    byte before = map[30, 30];
    byte paintVal = (byte)(before == 9 ? 3 : 9);   // guaranteed different from the current cell
    var stroke = painter.BeginStroke();
    stroke.Dab(wx, wz, new MaterialBrush(paintVal, 40f, 1f));
    stroke.Dab(wx + 20, wz, new MaterialBrush(paintVal, 40f, 1f));
    var edit = stroke.Finish() ?? throw new Exception("empty stroke");
    byte afterPaint = map[30, 30];
    int changed = 0; var snap = map.Clone();
    for (int yy = edit.Y0; yy < edit.Y0 + edit.H; yy++)
        for (int xx = edit.X0; xx < edit.X0 + edit.W; xx++) if (map[xx, yy] == paintVal) changed++;
    Console.WriteLine($"painted {paintVal} over rect {edit.W}x{edit.H} @ {edit.X0},{edit.Y0}; cell[30,30] {before} -> {afterPaint}; {changed} cells now == {paintVal}");

    int rebuilds = 0;
    var cmd = new MaterialStrokeCommand(edit, map, () => rebuilds++);
    var so = new StaticObjectsFile();
    cmd.Apply(so); byte applied = map[30, 30];   // edit already applied during stroke, Redo is idempotent here
    cmd.Undo(so); byte undone = map[30, 30];
    cmd.Apply(so); byte redone = map[30, 30];
    Console.WriteLine($"wire={cmd.ToWire()}");
    Console.WriteLine($"cmd: applied={applied} undo={undone}(=={before}? {undone == before}) redo={redone}(=={afterPaint}? {redone == afterPaint}) rebuilds={rebuilds}");
    return 0;
}

if (arg == "fxdump" && args.Length >= 2)
{
    // fxdump <level.rfa[,patch...]> — parse the level's particle effects and report which PLACED static objects resolve
    // to an effect (emitter texture/rate/velocity/size/blend), so we can verify the effect preview before rendering.
    var lvlRfas = args[1].Split(',', StringSplitOptions.RemoveEmptyEntries).Where(File.Exists).ToArray();
    var fx = RefractorForge.Render.EffectsLibrary.FromRfaPaths(lvlRfas);
    Console.WriteLine($"parsed {fx.BundleCount} EffectBundle(s)");
    var texPaths = (args.Length > 2 ? args[2].Split(',', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>())
                   .Concat(lvlRfas).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    var texLib = RefractorForge.Render.TextureLibrary.Open(texPaths);
    var lvl = RefractorForge.Render.LevelArchive.FromRfa(lvlRfas);
    var placed = lvl.StaticObjects.Objects.GroupBy(o => o.Template, StringComparer.OrdinalIgnoreCase).Select(g => (T: g.Key, C: g.Count())).OrderByDescending(g => g.C).ToList();
    int hit = 0;
    foreach (var (t, c) in placed)
    {
        if (!fx.TryResolve(t, out var ems)) continue;
        hit++;
        Console.WriteLine($"  EFFECT {t} x{c}: {ems.Length} emitter(s)");
        foreach (var e in ems.GroupBy(x => x.Texture).Select(g => g.First()))
        {
            var tex = texLib.Resolve(e.Texture);
            Console.WriteLine($"      tex={e.Texture} [{(tex is null ? "TEX MISSING" : $"{tex.Width}x{tex.Height}")}] rate={e.Rate:0.#} ttl={e.ParticleTtl:0.#} vel=({e.Velocity.X:0.#},{e.Velocity.Y:0.#},{e.Velocity.Z:0.#}) size={e.Size:0.##}->{e.SizeEnd:0.##} {(e.Additive ? "ADD" : "alpha")}");
        }
    }
    Console.WriteLine($"{hit} placed templates resolve to effects (of {placed.Count} unique)");
    return 0;
}

if (arg == "skydump" && args.Length >= 2)
{
    // skydump <level.rfa[,patch...]> [meshArchives,...] — report the level's skybox (MESH and/or cubemap faces) + cloud
    // mesh and whether each resolves WITH textures, mirroring the editor (AttachTextures, level rfa included).
    var lvlRfas = args[1].Split(',', StringSplitOptions.RemoveEmptyEntries).Where(File.Exists).ToArray();
    var lvl = RefractorForge.Render.LevelArchive.FromRfa(lvlRfas);
    var env = lvl.Environment;
    var meshA = (args.Length > 2 ? args[2].Split(',', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>())
                .Concat(lvlRfas).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    var texLib = RefractorForge.Render.TextureLibrary.Open(meshA);
    var lib = RefractorForge.Render.MeshLibrary.Open(meshA);
    lib.AttachTextures(texLib);   // mirror the editor so mesh textures + cubemap faces resolve
    Console.WriteLine($"SkyBoxMesh = {env.SkyBoxMesh ?? "(none)"}   SkyRotAngle = {env.SkyRotationAngle}");
    bool meshOk = false;
    if (!string.IsNullOrEmpty(env.SkyBoxMesh))
    {
        RefractorForge.Render.MeshLibrary.Mesh? sm = null;
        if ((lib.TryGet(env.SkyBoxMesh, out sm) && sm is not null && sm.Positions.Length > 0)
            || (lib.TryGetRenderMesh(env.SkyBoxMesh, out sm) && sm is not null && sm.Positions.Length > 0))
        {
            int tx = 0; foreach (var p in sm!.Parts) if (p.Texture is not null) tx++;
            Console.WriteLine($"  skybox MESH resolves: {sm.Positions.Length} verts, {sm.Parts.Length} part(s), {tx} textured");
            meshOk = tx > 0;
        }
        else Console.WriteLine("  skybox MESH not shipped (normal: most maps ship cubemap faces, not the mesh)");
    }
    bool cubeOk = false;
    if (!string.IsNullOrEmpty(env.SkyBoxMesh))
    {
        var stem = System.Text.RegularExpressions.Regex.Replace(env.SkyBoxMesh, @"_m\d+$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (var bn in new[] { stem, "env_default" })
        {
            int got = 0; var sizes = new System.Collections.Generic.List<string>();
            for (int i = 1; i <= 6; i++) { var t = texLib.Resolve($"{bn}_0{i}"); if (t is not null) { got++; sizes.Add($"{t.Width}x{t.Height}"); } }
            Console.WriteLine($"  cubemap faces '{bn}_01..06': {got}/6 resolve{(got > 0 ? " [" + string.Join(",", sizes) + "]" : "")}");
            if (got == 6) { cubeOk = true; break; }
        }
    }
    Console.WriteLine($"cloud layers = {env.Clouds.Count}   CloudMeshFile = {env.CloudMeshFile}");
    if (env.Clouds.Count > 0)
    {
        var cn = string.IsNullOrEmpty(env.CloudMeshFile) ? "cloud" : env.CloudMeshFile;
        RefractorForge.Render.MeshLibrary.Mesh? cm = null;
        if ((lib.TryGet(cn, out cm) && cm is not null && cm.Positions.Length > 0)
            || (lib.TryGetRenderMesh(cn, out cm) && cm is not null && cm.Positions.Length > 0))
        {
            int tx = 0; foreach (var p in cm!.Parts) if (p.Texture is not null) tx++;
            Console.WriteLine($"  cloud mesh '{cn}' resolves: {cm.Positions.Length} verts, {cm.Parts.Length} part(s), {tx} textured");
        }
        else Console.WriteLine($"  cloud mesh '{cn}' NOT RESOLVED");
    }
    Console.WriteLine($"SKYDUMP: skyboxMesh={(meshOk ? "OK" : "-")} cubemap={(cubeOk ? "OK" : "-")}");
    return 0;
}

if (arg == "ddsprobe" && args.Length >= 2)
{
    // ddsprobe <file.dds> — decode a .dds and report size + alpha histogram + the average colour of OPAQUE texels
    // (i.e. what a cloud's bubbles actually look like). Diagnoses "bubbles render blue not white".
    var bytes = File.ReadAllBytes(args[1]);
    var t = RefractorForge.Render.DdsTexture.Decode(bytes);
    if (t is null) { Console.WriteLine("decode FAILED"); return 0; }
    int n = t.Width * t.Height; long tr = 0, tg = 0, tb = 0; int a0 = 0, a255 = 0, aMid = 0; long or = 0, og = 0, ob = 0, oc = 0;
    for (int i = 0; i < n; i++)
    {
        int o = i * 4; byte r = t.Rgba[o], g = t.Rgba[o + 1], b = t.Rgba[o + 2], a = t.Rgba[o + 3];
        tr += r; tg += g; tb += b;
        if (a < 10) a0++; else if (a > 245) a255++; else aMid++;
        if (a > 200) { or += r; og += g; ob += b; oc++; }
    }
    Console.WriteLine($"{t.Width}x{t.Height}  avgRGB(all)={tr / n},{tg / n},{tb / n}");
    Console.WriteLine($"alpha: transparent(<10)={a0}  opaque(>245)={a255}  mid={aMid}  ({100.0 * a0 / n:0.#}% transparent)");
    if (oc > 0) Console.WriteLine($"avgRGB of OPAQUE texels (the bubbles): {or / oc},{og / oc},{ob / oc}");
    void P(int x, int y) { int o = (y * t.Width + x) * 4; Console.WriteLine($"  px({x},{y}) = R{t.Rgba[o]} G{t.Rgba[o + 1]} B{t.Rgba[o + 2]} A{t.Rgba[o + 3]}"); }
    P(0, 0); P(t.Width / 2, t.Height / 2); P(t.Width - 1, t.Height - 1);
    return 0;
}

if (arg == "tmdump" && args.Length >= 3)
{
    // tmdump <a.rfa,...> <treename> — parse a BF1942 .tm and dump its 4 groups + per-group first-material vertices
    // (pos/uv/normal/|n|), to reverse-engineer how the SPRITE group encodes its billboards (corner-UV quads vs points,
    // and whether the normal magnitude carries a size).
    var archives = args[1].Split(',', StringSplitOptions.RemoveEmptyEntries).Where(File.Exists).ToArray();
    string want = args[2].ToLowerInvariant(); if (!want.EndsWith(".tm")) want += ".tm";
    RefractorForge.Formats.Rfa.TreeMesh? tm = null;
    foreach (var ap in archives)
    {
        var a = RefractorForge.Formats.Rfa.RfaArchive.Open(ap);
        foreach (var e in a.Entries)
        {
            var leaf = e.Name.Replace('\\', '/'); leaf = leaf[(leaf.LastIndexOf('/') + 1)..];
            if (leaf.Equals(want, StringComparison.OrdinalIgnoreCase) && RefractorForge.Formats.Rfa.TreeMesh.TryParse(a.Read(e), out tm)) break;
        }
        if (tm is not null) break;
    }
    if (tm is null) { Console.WriteLine($"{want}: not found / parse fail"); return 1; }
    Console.WriteLine($"ver={tm.Version} verts={tm.Vertices.Length} indices={tm.Indices.Length} bounds=({tm.Min.X:0.#},{tm.Min.Y:0.#},{tm.Min.Z:0.#})..({tm.Max.X:0.#},{tm.Max.Y:0.#},{tm.Max.Z:0.#})");
    string[] gn = { "leaf", "trunk", "sprite", "extra" };
    for (int g = 0; g < tm.Groups.Length; g++)
        foreach (var m in tm.Groups[g])
            Console.WriteLine($"  G{g}({gn[g]}) start={m.Start} count={m.Count} tex={m.TexName}");
    for (int g = 0; g < tm.Groups.Length; g++)
    {
        if (tm.Groups[g].Count == 0) continue;
        var m = tm.Groups[g][0];
        Console.WriteLine($"-- G{g}({gn[g]}) tex={m.TexName} verts of first tris from start={m.Start} --");
        int shown = 0;
        for (int t = m.Start; t + 2 < tm.Indices.Length && shown < 4; t += 3, shown++)
            for (int k = 0; k < 3; k++)
            {
                int vi = tm.Indices[t + k]; var v = tm.Vertices[vi];
                float nl = MathF.Sqrt(v.Nx * v.Nx + v.Ny * v.Ny + v.Nz * v.Nz);
                Console.WriteLine($"    t{shown}v{k} idx={vi} pos=({v.Px:0.##},{v.Py:0.##},{v.Pz:0.##}) uv=({v.U:0.###},{v.V:0.###}) n=({v.Nx:0.##},{v.Ny:0.##},{v.Nz:0.##}) |n|={nl:0.###}");
            }
    }
    return 0;
}

if (arg == "rotbundle" && args.Length >= 3)
{
    // rotbundle <a.rfa,...> <template> — report a placed template's continuously-rotating (RotationalBundle) parts +
    // each part's speed/pivot/mesh, verifying the windmill/watermill/mod-rotor animation parse + mesh-resolve headlessly.
    var lib = RefractorForge.Render.MeshLibrary.Open(args[1].Split(',', StringSplitOptions.RemoveEmptyEntries).Where(File.Exists).ToArray());
    if (lib.TryGetAnimatedParts(args[2], out var parts) && parts.Length > 0)
    {
        Console.WriteLine($"{args[2]}: {parts.Length} animated part(s)");
        foreach (var p in parts)
            Console.WriteLine($"  speed(deg/s)=({p.SpeedDeg.X:0.#},{p.SpeedDeg.Y:0.#},{p.SpeedDeg.Z:0.#}) pivot=({p.Pivot.X:0.#},{p.Pivot.Y:0.#},{p.Pivot.Z:0.#}) mesh={p.Mesh.Triangles} tris");
    }
    else Console.WriteLine($"{args[2]}: no animated (RotationalBundle) parts");
    return 0;
}

if (arg == "soundresolve" && args.Length >= 2)
{
    // soundresolve <level.rfa[,...]> [extraSound.rfa,...] — list placed sound emitters + whether each .ssc's .wav
    // resolves (by leaf) in the level / shared archives and decodes as a RIFF WAV. Verifies the playback resolution.
    var lvlRfas = args[1].Split(',', StringSplitOptions.RemoveEmptyEntries).Where(File.Exists).ToArray();
    var lvl = RefractorForge.Render.LevelArchive.FromRfa(lvlRfas);
    var snd = lvl.Sounds ?? RefractorForge.Formats.Sound.SoundLibrary.Empty;
    var arcs = lvlRfas.Concat(args.Length > 2 ? args[2].Split(',', StringSplitOptions.RemoveEmptyEntries).Where(File.Exists) : Array.Empty<string>())
                      .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    Console.WriteLine($"{snd.Count} sound emitter(s)");
    int ok = 0;
    foreach (var em in snd.Emitters)
    {
        var raw = em.Script?.Wav;
        string leaf = string.IsNullOrEmpty(raw) ? "" : raw.Replace('\\', '/').TrimStart('/');
        if (leaf.Contains('/')) leaf = leaf[(leaf.LastIndexOf('/') + 1)..];
        leaf = leaf.ToLowerInvariant();
        byte[]? bytes = null; string from = "";
        if (leaf.Length > 0)
            foreach (var ap in arcs)
            {
                RefractorForge.Formats.Rfa.RfaArchive a; try { a = RefractorForge.Formats.Rfa.RfaArchive.Open(ap); } catch { continue; }
                foreach (var e in a.Entries)
                {
                    var en = e.Name.Replace('\\', '/').ToLowerInvariant();
                    if (en == leaf || en.EndsWith("/" + leaf)) { try { bytes = a.Read(e); from = System.IO.Path.GetFileName(ap); } catch { } break; }
                }
                if (bytes is not null) break;
            }
        bool riff = bytes is not null && bytes.Length > 12 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F';
        if (riff) ok++;
        Console.WriteLine($"  {em.Template,-20} loop={(em.Script?.Loop ?? false),-5} minDist={em.MinDistance,-4:0} wav={raw} -> {(bytes is null ? "NOT FOUND" : $"{bytes.Length}B {(riff ? "RIFF" : "?")} [{from}]")}");
    }
    Console.WriteLine($"{ok}/{snd.Count} resolve to a RIFF wav");
    return 0;
}

if (arg == "wavrt")
{
    // Self-test the WaveFileWriter(IeeeFloat) -> WaveFileReader round-trip the editor's capture uses: write a known
    // 0.5-amplitude buffer, read it back, report the peak. If it isn't ~0.5, the writer/format scales the data.
    var fmt = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
    Console.WriteLine($"writer fmt: {fmt.Encoding} {fmt.BitsPerSample}bit {fmt.Channels}ch");
    var data = new float[44100 * 2]; for (int i = 0; i < data.Length; i++) data[i] = 0.5f;
    string p = "C:\\Users\\lucas\\Desktop\\wavrt.wav";
    using (var w = new NAudio.Wave.WaveFileWriter(p, fmt)) w.WriteSamples(data, 0, data.Length);
    using (var r = new NAudio.Wave.WaveFileReader(p))
    {
        Console.WriteLine($"readback fmt: {r.WaveFormat.Encoding} {r.WaveFormat.BitsPerSample}bit {r.WaveFormat.Channels}ch");
        var sp = NAudio.Wave.WaveExtensionMethods.ToSampleProvider(r);
        var buf = new float[4096]; int n; float pk = 0; long c = 0;
        while ((n = sp.Read(buf, 0, buf.Length)) > 0) { for (int i = 0; i < n; i++) { float a = Math.Abs(buf[i]); if (a > pk) pk = a; } c += n; }
        Console.WriteLine($"wrote 0.5 x{data.Length}; read back {c} samples, peak={pk:0.000}  (healthy round-trip => 0.500)");
    }
    return 0;
}

if (arg == "wavprobe" && args.Length >= 2)
{
    // wavprobe <a.rfa,...> <wavLeaf>  — find a .wav by leaf in the archives and analyse it; OR
    // wavprobe <file.wav>             — analyse a loose .wav directly (e.g. the editor's sound_capture.wav).
    // Reports format + duration + the float-sample count drained, and a per-second RMS loudness profile.
    byte[]? bytes = null; string from = "";
    if (args.Length == 2 && args[1].EndsWith(".wav", StringComparison.OrdinalIgnoreCase) && File.Exists(args[1]))
    {
        bytes = File.ReadAllBytes(args[1]); from = args[1];
    }
    else if (args.Length >= 3)
    {
        var arcs = args[1].Split(',', StringSplitOptions.RemoveEmptyEntries).Where(File.Exists).ToArray();
        string leaf = args[2].Replace('\\', '/'); leaf = (leaf.Contains('/') ? leaf[(leaf.LastIndexOf('/') + 1)..] : leaf).ToLowerInvariant();
        if (!leaf.EndsWith(".wav")) leaf += ".wav";
        foreach (var ap in arcs)
        {
            RefractorForge.Formats.Rfa.RfaArchive a; try { a = RefractorForge.Formats.Rfa.RfaArchive.Open(ap); } catch { continue; }
            foreach (var e in a.Entries)
            {
                var en = e.Name.Replace('\\', '/').ToLowerInvariant();
                if (en == leaf || en.EndsWith("/" + leaf)) { try { bytes = a.Read(e); from = e.Name; } catch { } break; }
            }
            if (bytes is not null) break;
        }
    }
    if (bytes is null) { Console.WriteLine("wav not found (give <file.wav> or <archive.rfa> <leaf>)"); return 1; }
    Console.WriteLine($"found {from} ({bytes.Length} bytes)");
    try
    {
        using var ms = new System.IO.MemoryStream(bytes);
        using var rdr = new NAudio.Wave.WaveFileReader(ms);
        var f = rdr.WaveFormat;
        Console.WriteLine($"fmt: enc={f.Encoding} {f.SampleRate}Hz {f.Channels}ch {f.BitsPerSample}bit avgBps={f.AverageBytesPerSecond} TotalTime={rdr.TotalTime.TotalSeconds:0.###}s");
        var sp = NAudio.Wave.WaveExtensionMethods.ToSampleProvider(rdr);
        long got = 0; var buf = new float[4096]; int n;
        int perSec = System.Math.Max(1, f.SampleRate * f.Channels);
        var rms = new System.Collections.Generic.List<double>(); double acc = 0; long inSec = 0;
        while ((n = sp.Read(buf, 0, buf.Length)) > 0)
        {
            for (int i = 0; i < n; i++) { acc += buf[i] * (double)buf[i]; if (++inSec >= perSec) { rms.Add(System.Math.Sqrt(acc / perSec)); acc = 0; inSec = 0; } }
            got += n;
        }
        if (inSec > 0) rms.Add(System.Math.Sqrt(acc / inSec));
        double secs = f.SampleRate > 0 && f.Channels > 0 ? got / (double)(f.SampleRate * f.Channels) : 0;
        Console.WriteLine($"drained {got} float samples = {secs:0.###}s of audio (TotalTime says {rdr.TotalTime.TotalSeconds:0.###}s)");
        // Dump the first chunk of loud samples + min/max to reveal the data PATTERN (byte-like 0..255? bird waveform? etc.)
        using (var ms3 = new System.IO.MemoryStream(bytes))
        using (var r3 = new NAudio.Wave.WaveFileReader(ms3))
        {
            var sp3 = NAudio.Wave.WaveExtensionMethods.ToSampleProvider(r3);
            var b3 = new float[4096]; int n3; float mn3 = float.MaxValue, mx3 = float.MinValue; long over1 = 0, total = 0;
            var first = new System.Collections.Generic.List<float>(); bool grabbing = false;
            while ((n3 = sp3.Read(b3, 0, b3.Length)) > 0)
                for (int i = 0; i < n3; i++) { float v = b3[i]; if (v < mn3) mn3 = v; if (v > mx3) mx3 = v; if (System.Math.Abs(v) > 1f) over1++; total++; if (!grabbing && System.Math.Abs(v) > 1f) grabbing = true; if (grabbing && first.Count < 40) first.Add(v); }
            Console.WriteLine($"min={mn3:0.000} max={mx3:0.000} over1.0={over1}/{total} ({100.0 * over1 / System.Math.Max(1, total):0.0}%)");
            Console.WriteLine("first loud samples: " + string.Join(" ", first.Select(v => v.ToString("0.0"))));
        }
        // Per-second loudness profile (0-9 per second). Periodic high/low => the RECORDING itself pulses (e.g. a bird
        // call that chirps then pauses) — which a listener hears as "repeating", independent of any looping logic.
        double peak = rms.Count > 0 ? System.Linq.Enumerable.Max(rms) : 1;
        var prof = string.Concat(rms.Select(v => (char)('0' + (int)System.Math.Round(9 * v / (peak <= 0 ? 1 : peak)))));
        Console.WriteLine($"RMS/sec (0=silent..9=loud, peak={peak:0.000}): {prof}");
        // Mirror SoundPlayback.Decode EXACTLY: resample to 44100 + mono->stereo, drain to a float[] (the path the live
        // editor uses). Catches a resampler/EOF bug — the prime suspect for the sub-second-then-respawn glitch.
        using (var ms2 = new System.IO.MemoryStream(bytes))
        using (var r2 = new NAudio.Wave.WaveFileReader(ms2))
        {
            NAudio.Wave.ISampleProvider c = NAudio.Wave.WaveExtensionMethods.ToSampleProvider(r2);
            Console.WriteLine($"  stage ToSampleProvider: {c.WaveFormat.Encoding} {c.WaveFormat.SampleRate}Hz {c.WaveFormat.Channels}ch");
            if (c.WaveFormat.SampleRate != 44100) { c = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(c, 44100); Console.WriteLine($"  + resample -> {c.WaveFormat.SampleRate}Hz {c.WaveFormat.Channels}ch"); }
            if (c.WaveFormat.Channels == 1) { c = new NAudio.Wave.SampleProviders.MonoToStereoSampleProvider(c); Console.WriteLine($"  + mono->stereo -> {c.WaveFormat.Channels}ch"); }
            long cgot = 0; var cbuf = new float[8192]; int cn; float cpeak = 0; double csum = 0;
            while ((cn = c.Read(cbuf, 0, cbuf.Length)) > 0) { for (int i = 0; i < cn; i++) { float a2 = System.Math.Abs(cbuf[i]); if (a2 > cpeak) cpeak = a2; csum += cbuf[i] * (double)cbuf[i]; } cgot += cn; }
            Console.WriteLine($"full chain (->44100 stereo): {cgot} floats = {cgot / (double)(44100 * 2):0.###}s; PEAK={cpeak:0.000} RMS={(cgot > 0 ? System.Math.Sqrt(csum / cgot) : 0):0.000}  (a healthy float wav peaks <= ~1.0)");
        }
    }
    catch (System.Exception ex) { Console.WriteLine($"NAudio decode FAILED: {ex.GetType().Name}: {ex.Message}"); }
    return 0;
}

if (arg == "vehspawns" && args.Length >= 2)
{
    // vehspawns <level.rfa[,patch...]> [meshArchives,...] — load the gameplay layer from the .rfa and report every
    // vehicle spawn + whether its vehicle template RESOLVES to a render mesh (the same path the editor uses).
    var lvlRfas = args[1].Split(',', StringSplitOptions.RemoveEmptyEntries).Where(File.Exists).ToArray();
    var lvl = RefractorForge.Render.LevelArchive.FromRfa(lvlRfas);
    var gpv = lvl.Gameplay;
    Console.WriteLine($"control points: {gpv.ControlPoints.Count}, VEHICLE SPAWNS: {gpv.VehicleSpawns.Count}, soldier spawns: {gpv.SoldierSpawns.Count}");
    var meshA = (args.Length > 2 ? args[2].Split(',', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>())
                .Concat(lvlRfas).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    var lib = RefractorForge.Render.MeshLibrary.Open(meshA);
    var byVeh = gpv.VehicleSpawns.GroupBy(v => v.Vehicle, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count());
    int ok = 0, fail = 0;
    foreach (var g in byVeh)
    {
        bool r = !string.IsNullOrWhiteSpace(g.Key) && lib.TryGetRenderMesh(g.Key, out var m) && m is not null;
        int colParts = !string.IsNullOrWhiteSpace(g.Key) && lib.TryGetRenderCollision(g.Key, out var cp) ? cp.Length : 0;
        if (r) ok++; else fail++;
        Console.WriteLine($"  {(r ? "OK  " : "MISS")} {g.Key,-28} x{g.Count(),-4} collisionParts={colParts}");
    }
    Console.WriteLine($"{ok} vehicle templates resolve, {fail} MISS");
    return 0;
}

if (arg == "gameplaydump" && args.Length >= 2)
{
    var gp = GameplayObjects.LoadFolder(args[1]);
    foreach (var c in gp.ControlPoints)
        Console.WriteLine($"CP\t{c.Name}\t{c.Position.X}\t{c.Position.Y}\t{c.Position.Z}\t{c.Radius}");
    foreach (var v in gp.VehicleSpawns)
        Console.WriteLine($"VEH\t{v.Name}\t{v.Position.X}\t{v.Position.Y}\t{v.Position.Z}\t{v.Vehicle}");
    foreach (var s in gp.SoldierSpawns)
        Console.WriteLine($"SOL\t{s.Name}\t{s.Position.X}\t{s.Position.Y}\t{s.Position.Z}");
    return 0;
}

if (arg == "gameplay" && args.Length >= 2)
{
    var gp = GameplayObjects.LoadFolder(args[1]);
    Console.WriteLine($"control points: {gp.ControlPoints.Count}");
    foreach (var c in gp.ControlPoints)
        Console.WriteLine($"  {c.Name,-16} pos {c.Position.X:0}/{c.Position.Y:0}/{c.Position.Z:0}  radius {c.Radius:0}  sg {c.SpawnGroupId}");
    Console.WriteLine($"vehicle spawns: {gp.VehicleSpawns.Count}");
    foreach (var v in gp.VehicleSpawns.Take(8))
        Console.WriteLine($"  {v.Name,-16} -> {v.Vehicle,-12} pos {v.Position.X:0}/{v.Position.Z:0}");
    Console.WriteLine($"soldier spawns: {gp.SoldierSpawns.Count}");
    return 0;
}

if (arg == "vehteams" && args.Length >= 2)
{
    // vehteams <a.rfa,b.rfa,...> — load the level EXACTLY like the editor (FromRfa, last-wins merge of base + patches)
    // and print, per vehicle spawn, the owning control point + resolved team + vehicle (mirrors the Viewer's SpawnTeam).
    var rfas = args[1].Split(',', StringSplitOptions.RemoveEmptyEntries);
    var lvl = LevelArchive.FromRfa(rfas);
    var gp = lvl.Gameplay;
    var cps = gp.ControlPoints;
    Console.WriteLine($"control points ({cps.Count}):");
    for (int i = 0; i < cps.Count; i++)
        Console.WriteLine($"  [{i}] {cps[i].Name,-34} team={cps[i].Team} osId={cps[i].ObjectSpawnerId} sg={cps[i].SpawnGroupId} pos {cps[i].Position.X:0}/{cps[i].Position.Z:0}");
    Console.WriteLine($"vehicle spawns ({gp.VehicleSpawns.Count}):");
    foreach (var v in gp.VehicleSpawns)
    {
        int ci = GameplayObjects.OwningControlPointIndex(cps, v.Position, v.OsId, true);
        int team = ci >= 0 ? cps[ci].Team : 2;
        string s = team == 1 ? v.Vehicle1 : v.Vehicle2;
        if (string.IsNullOrEmpty(s)) s = v.Vehicle;
        string owner = ci >= 0 ? cps[ci].Name : "(none)";
        Console.WriteLine($"  {v.Name,-18} pos {v.Position.X,5:0}/{v.Position.Z,5:0} osId={v.OsId,-3} -> CP[{ci}] {owner,-30} team={team} => {s}   (v1={v.Vehicle1} v2={v.Vehicle2})");
    }
    return 0;
}

if (arg == "minimap" && args.Length >= 2)
{
    // minimap <levelDir> [outDir] [size] — generate ingamemap.dds + Thumbnail.dds (top-down terrain).
    string outDir = args.Length > 2 ? args[2] : ".";
    int size = args.Length > 3 ? int.Parse(args[3]) : 256;
    System.IO.Directory.CreateDirectory(outDir);

    var cfg = TerrainConfig.Load(Directory.EnumerateFiles(args[1], "Terrain.con", SearchOption.AllDirectories).First());
    var hm = Heightmap.LoadForMaterialSize(Directory.EnumerateFiles(args[1], "Heightmap.raw", SearchOption.AllDirectories).First(), cfg.MaterialSize);
    var texDir = Directory.EnumerateDirectories(args[1], "Textures", SearchOption.AllDirectories).FirstOrDefault() ?? args[1];
    var tex = TerrainTexture.Load(texDir, cfg.WorldSize);
    var matPath = Directory.EnumerateFiles(args[1], "MaterialMap.raw", SearchOption.AllDirectories).FirstOrDefault();
    var mat = matPath is null ? null : MaterialMap.LoadForMaterialSize(matPath, cfg.MaterialSize);
    Console.WriteLine($"source: world={cfg.WorldSize} heightmap={hm.Width}x{hm.Height} atlas={(tex is null ? "none" : "yes")} material={(mat is null ? "none" : mat.Width + "^2")}");

    var ingame = Minimap.Render(size, hm, cfg, tex, mat);
    var thumb = Minimap.Render(256, hm, cfg, tex, mat);
    string ingamePath = System.IO.Path.Combine(outDir, "ingamemap.dds");
    string thumbPath = System.IO.Path.Combine(outDir, "Thumbnail.dds");
    DdsTexture.Save(ingame, ingamePath);
    DdsTexture.Save(thumb, thumbPath);

    // Round-trip the DDS we wrote (read back, verify dimensions + exact pixel parity).
    var rt = DdsTexture.Load(ingamePath);
    bool dimsOk = rt.Width == size && rt.Height == size;
    long diff = 0; for (int i = 0; i < rt.Rgba.Length; i++) diff += Math.Abs(rt.Rgba[i] - ingame.Rgba[i]);
    Console.WriteLine($"ingamemap {size}x{size} -> {ingamePath}  (roundtrip dims {(dimsOk ? "OK" : "BAD")}, pixel diff {diff})");
    Console.WriteLine($"thumbnail 256x256 -> {thumbPath}");

    // BMP preview for visual inspection.
    string bmpPath = System.IO.Path.Combine(outDir, "minimap_preview.bmp");
    var img = new ImageBuffer(ingame.Width, ingame.Height);
    for (int i = 0; i < ingame.Width * ingame.Height; i++)
    { img.Rgb[i * 3] = ingame.Rgba[i * 4]; img.Rgb[i * 3 + 1] = ingame.Rgba[i * 4 + 1]; img.Rgb[i * 3 + 2] = ingame.Rgba[i * 4 + 2]; }
    img.SaveBmp(bmpPath);
    Console.WriteLine($"preview -> {bmpPath}");

    if (dimsOk && diff == 0) Console.WriteLine("MINIMAP TESTS PASSED");
    return 0;
}

if (arg == "shadowbake" && args.Length >= 2)
{
    // shadowbake <levelDir> [outDir] [size] — bake the terrain sun cast-shadow map.
    string outDir = args.Length > 2 ? args[2] : ".";
    int size = args.Length > 3 ? int.Parse(args[3]) : 1024;
    System.IO.Directory.CreateDirectory(outDir);
    var cfg = TerrainConfig.Load(Directory.EnumerateFiles(args[1], "Terrain.con", SearchOption.AllDirectories).First());
    var hm = Heightmap.LoadForMaterialSize(Directory.EnumerateFiles(args[1], "Heightmap.raw", SearchOption.AllDirectories).First(), cfg.MaterialSize);
    var envv = RefractorForge.Formats.Terrain.EnvironmentSettings.LoadFolder(args[1]);
    var sw = Stopwatch.StartNew();
    var shadow = TerrainShadow.Bake(size, hm, cfg, envv.SunDirection);
    sw.Stop();
    string ddsPath = System.IO.Path.Combine(outDir, "TerrainShadow.dds");
    DdsTexture.Save(shadow, ddsPath);
    var rt = DdsTexture.Load(ddsPath);
    long diff = 0; for (int i = 0; i < rt.Rgba.Length; i++) diff += Math.Abs(rt.Rgba[i] - shadow.Rgba[i]);
    long shad = 0; for (int i = 0; i < size * size; i++) if (shadow.Rgba[i * 4] < 128) shad++;
    Console.WriteLine($"sun={envv.SunDirection}  baked {size}x{size} in {sw.ElapsedMilliseconds} ms, {100.0 * shad / (size * size):0.0}% shadowed, roundtrip diff {diff}");
    Console.WriteLine($"shadow -> {ddsPath}");
    // North-up BMP preview (flip rows: bake is UV-aligned where row 0 = worldZ 0 = south).
    var img = new ImageBuffer(size, size);
    for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        { int src = ((size - 1 - y) * size + x) * 4; int dd = (y * size + x) * 3; img.Rgb[dd] = shadow.Rgba[src]; img.Rgb[dd + 1] = shadow.Rgba[src + 1]; img.Rgb[dd + 2] = shadow.Rgba[src + 2]; }
    string bmp = System.IO.Path.Combine(outDir, "shadow_preview.bmp");
    img.SaveBmp(bmp);
    Console.WriteLine($"preview -> {bmp}");
    if (diff == 0) Console.WriteLine("SHADOW BAKE TESTS PASSED");
    return 0;
}

if (arg == "lsbbake" && args.Length >= 2)
{
    // lsbbake <levelDir> [out.lsb] [bakeSize]
    // Bake the sun shadow and pack it as a game-readable LightmapShadowBits.lsb, then validate structure.
    var cfg = TerrainConfig.Load(System.IO.Directory.EnumerateFiles(args[1], "Terrain.con", System.IO.SearchOption.AllDirectories).First());
    var hm = Heightmap.LoadForMaterialSize(System.IO.Directory.EnumerateFiles(args[1], "Heightmap.raw", System.IO.SearchOption.AllDirectories).First(), cfg.MaterialSize);
    var env = EnvironmentSettings.LoadFolder(args[1]);

    // Grid size: copy the map's existing .lsb grid if it has one (most reliable), else default 8x8.
    var existing = RefractorForge.Formats.Terrain.LightmapShadowBits.TryLoadFolder(args[1]);
    int gridDim = existing?.GridDim ?? 8;
    int tilePx = existing is { TilePixels: > 0 } ? existing.TilePixels : 1024;
    int bakeSize = args.Length > 3 ? int.Parse(args[3]) : 0;

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var lsb = TerrainShadow.BakeToLsb(hm, cfg, env.SunDirection, gridDim, tilePx, bakeSize);
    sw.Stop();
    Console.WriteLine($"sun={env.SunDirection}  grid={gridDim}x{gridDim} tile={tilePx}px  baked in {sw.ElapsedMilliseconds} ms");

    // Structural validation: the generated file must decode back to the same tiles it was built from.
    byte[] bytes = lsb.Encode();
    var reloaded = RefractorForge.Formats.Terrain.LightmapShadowBits.Decode(bytes);
    bool structOk = reloaded.Encode().AsSpan().SequenceEqual(bytes) && reloaded.Tiles.Count == gridDim * gridDim;

    string outPath = args.Length > 2 ? args[2] : System.IO.Path.Combine(".", "LightmapShadowBits.lsb");
    System.IO.File.WriteAllBytes(outPath, bytes);
    Console.WriteLine($"wrote {bytes.Length} bytes -> {outPath}  (tiles={reloaded.Tiles.Count}, struct {(structOk ? "OK" : "BAD")})");

    // If the map ships an original .lsb, compare shapes (a rough, non-authoritative orientation sanity check).
    if (existing is not null)
    {
        var a = existing.ToVisibility(out int sideA);
        var b = lsb.ToVisibility(out int sideB);
        if (sideA == sideB)
        {
            long same = 0; for (int i = 0; i < a.Length; i++) if ((a[i] != 0) == (b[i] != 0)) same++;
            Console.WriteLine($"shape agreement vs original: {100.0 * same / a.Length:F1}% ({sideA}x{sideA})");
        }
        DumpVis(existing.ToVisibility(out _), gridDim * tilePx, System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outPath))!, "lsb_original.dds"));
        DumpVis(lsb.ToVisibility(out _), gridDim * tilePx, System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outPath))!, "lsb_baked.dds"));
        Console.WriteLine("dumped lsb_original.dds / lsb_baked.dds for visual comparison");
    }

    Console.WriteLine(structOk ? "LSB BAKE PASSED" : "LSB BAKE FAILED");
    return structOk ? 0 : 1;

    // Downsample a full visibility raster to a 512-square grayscale DDS for eyeballing.
    static void DumpVis(byte[] vis, int side, string path)
    {
        int outSide = Math.Min(512, side);
        var rgba = new byte[outSide * outSide * 4];
        for (int y = 0; y < outSide; y++)
            for (int x = 0; x < outSide; x++)
            {
                byte v = vis[(y * side / outSide) * side + (x * side / outSide)];
                int o = (y * outSide + x) * 4;
                rgba[o] = rgba[o + 1] = rgba[o + 2] = v; rgba[o + 3] = 255;
            }
        DdsTexture.Save(new Texture2D(outSide, outSide, rgba), path);
    }
}

if (arg == "waterinfo" && args.Length >= 2)
{
    // waterinfo <level.rfa[,patch...]> [texArchive1,texArchive2,...] — parse the level's water.* config and report
    // the texture-layer names + whether they RESOLVE to real textures via the TextureLibrary (level + given archives).
    var lvlRfas = args[1].Split(',', StringSplitOptions.RemoveEmptyEntries).Where(File.Exists).ToArray();
    var lvl = RefractorForge.Render.LevelArchive.FromRfa(lvlRfas);
    var e = lvl.Environment;
    if (e is null) { Console.WriteLine("no environment"); return 1; }
    Console.WriteLine($"water.color={e.WaterColor} alpha={e.WaterAlpha} deep={e.DeepColor}");
    Console.WriteLine($"HasWaterTextures={e.HasWaterTextures}");
    Console.WriteLine($"  texLayer1 = {e.WaterTexLayer1 ?? "(none)"}  tile={e.TileLayer1} scroll={e.ScrollDir1X}/{e.ScrollDir1Y}*{e.ScrollSpeed1}");
    Console.WriteLine($"  texLayer2 = {e.WaterTexLayer2 ?? "(none)"}  tile={e.TileLayer2} scroll={e.ScrollDir2X}/{e.ScrollDir2Y}*{e.ScrollSpeed2}");
    Console.WriteLine($"  normalMap = {e.WaterNormalMap ?? "(none)"}  tile={e.TileNormal} scroll={e.ScrollDirNX}/{e.ScrollDirNY}*{e.ScrollSpeedN}");
    Console.WriteLine($"  baseTex   = {e.WaterBaseTex ?? "(none)"}   specular={e.WaterSpecularEnable} {e.WaterSpecularColor}");
    var texPaths = (args.Length > 2 ? args[2].Split(',', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>())
                   .Concat(lvlRfas).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    var texLib = RefractorForge.Render.TextureLibrary.Open(texPaths);
    foreach (var (label, name) in new[] { ("texLayer1", e.WaterTexLayer1), ("texLayer2", e.WaterTexLayer2), ("normalMap", e.WaterNormalMap), ("baseTex", e.WaterBaseTex) })
    {
        if (string.IsNullOrEmpty(name)) continue;
        var t = texLib.Resolve(name);
        Console.WriteLine($"  RESOLVE {label} '{name}' -> {(t is null ? "NOT FOUND" : $"{t.Width}x{t.Height}")}");
    }
    return 0;
}

if (arg == "env" && args.Length >= 2)
{
    // env <levelDir> — parse SkyAndSun.con + Terrain.con and report sun/sky/water settings.
    var cfg = TerrainConfig.Load(System.IO.Directory.EnumerateFiles(args[1], "Terrain.con", System.IO.SearchOption.AllDirectories).First());
    var e = RefractorForge.Formats.Terrain.EnvironmentSettings.LoadFolder(args[1]);
    Console.WriteLine($"sun dir = {e.SunDirection}  (rotAngle {e.SkyRotationAngle})");
    Console.WriteLine($"shadow ambient = {e.ShadowAmbient}");
    Console.WriteLine($"skybox mesh = {e.SkyBoxMesh ?? "(none)"}");
    Console.WriteLine($"water: level={cfg.WaterLevel} seaFloor={cfg.SeaFloorLevel} waveHeight={cfg.WaveHeight}");
    return 0;
}

if (arg == "foliage" && args.Length >= 2)
{
    // foliage <levelDir> — load the growth layers and report sizes, palettes, value distribution.
    var g = RefractorForge.Formats.Terrain.GrowthMaps.LoadFolder(args[1]);
    void Report(string label, RefractorForge.Formats.Terrain.MaterialMap? m, int side, RefractorForge.Formats.Terrain.FoliagePalette? pal)
    {
        if (m is null) { Console.WriteLine($"{label}: (none)"); return; }
        var h = new int[256]; foreach (var b in m.Samples) h[b]++;
        int distinct = 0; for (int i = 0; i < 256; i++) if (h[i] > 0) distinct++;
        Console.WriteLine($"{label}: {m.Width}x{m.Height} ({m.Samples.Length}B), {distinct} distinct values, palette types={pal?.TypeCount ?? 0}, side(from .wst)={pal?.MaterialMapSideSize ?? 0}");
        if (pal is not null && pal.DistinctGeometries.Count > 0) Console.WriteLine($"    geometries: {string.Join(", ", pal.DistinctGeometries)}");
    }
    Report("undergrowth", g.Under, g.UnderSide, g.UnderPalette);
    Report("overgrowth", g.Over, g.OverSide, g.OverPalette);
    return 0;
}

if (arg == "foliageedit" && args.Length >= 2)
{
    // foliageedit <levelDir> — paint a stroke, verify undo/redo, then round-trip via folder AND .rfa.
    var levelDir = args[1];
    var cfg = TerrainConfig.Load(System.IO.Directory.EnumerateFiles(levelDir, "Terrain.con", System.IO.SearchOption.AllDirectories).First());
    var g = RefractorForge.Formats.Terrain.GrowthMaps.LoadFolder(levelDir);
    if (!g.Any) { Console.WriteLine("no growth maps found"); return 1; }

    var map = g.Under ?? g.Over!;
    int side = g.Under is not null ? g.UnderSide : g.OverSide;
    var layerCfg = new TerrainConfig { MaterialSize = side, WorldSize = cfg.WorldSize, YScale = cfg.YScale, WaterLevel = cfg.WaterLevel };
    var painter = new RefractorForge.Formats.Terrain.MaterialPainter(map, layerCfg);
    int cx = side / 2;
    byte before = map[cx, cx];
    byte paint = (byte)(before == 9 ? 8 : 9);
    var edit = painter.Stamp(cfg.WorldSize / 2f, cfg.WorldSize / 2f, new RefractorForge.Formats.Terrain.MaterialBrush(paint, 30f, 1f));
    if (edit is null) { Console.WriteLine("FAIL: stamp produced no edit"); return 1; }
    if (map[cx, cx] != paint) { Console.WriteLine("FAIL: centre not painted"); return 1; }
    edit.Undo(map); if (map[cx, cx] != before) { Console.WriteLine("FAIL: undo did not restore"); return 1; }
    edit.Redo(map); if (map[cx, cx] != paint) { Console.WriteLine("FAIL: redo did not reapply"); return 1; }
    Console.WriteLine($"paint/undo/redo OK on {(g.Under is not null ? "undergrowth" : "overgrowth")} (centre {before}->{paint}, {edit.CellCount} cells)");

    // Folder round-trip: write the (edited) growth maps to an empty temp dir, reload, compare bytes.
    string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rf_fol_" + System.Guid.NewGuid().ToString("N").Substring(0, 8));
    System.IO.Directory.CreateDirectory(tmp);
    var written = RefractorForge.Formats.LevelSaver.SaveFolder(tmp, null, null, null, null, null, g);
    var g2 = RefractorForge.Formats.Terrain.GrowthMaps.LoadFolder(tmp);
    bool ok = true;
    if (g.Under is not null) ok &= g2.Under is not null && System.Linq.Enumerable.SequenceEqual(g.Under.Samples, g2.Under.Samples);
    if (g.Over is not null) ok &= g2.Over is not null && System.Linq.Enumerable.SequenceEqual(g.Over.Samples, g2.Over.Samples);
    if (!ok) { Console.WriteLine("FAIL: folder round-trip mismatch"); return 1; }
    Console.WriteLine($"folder round-trip byte-exact ({written.Count} files)");

    // .rfa round-trip: pack the level, substitute the edited growth maps, read them back, compare.
    string rfa1 = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rf_fol1_" + System.Guid.NewGuid().ToString("N").Substring(0, 8) + ".rfa");
    string rfa2 = rfa1.Replace(".rfa", "_out.rfa");
    RefractorForge.Formats.LevelSaver.PackFolder(levelDir, rfa1, "bf1942/levels/test");
    var names = RefractorForge.Formats.LevelSaver.RepackToRfa(rfa1, rfa2, null, null, null, null, g);
    var arch = RefractorForge.Formats.Rfa.RfaArchive.Open(rfa2);
    bool rfaOk = true;
    if (g.Under is not null)
    {
        var e = arch.Entries.First(x => x.Name.EndsWith("UnderGrowthMap.raw", System.StringComparison.OrdinalIgnoreCase));
        rfaOk &= System.Linq.Enumerable.SequenceEqual(arch.Read(e), g.Under.Samples);
    }
    if (g.Over is not null)
    {
        var e = arch.Entries.First(x => x.Name.EndsWith("OverGrowthMap.raw", System.StringComparison.OrdinalIgnoreCase));
        rfaOk &= System.Linq.Enumerable.SequenceEqual(arch.Read(e), g.Over.Samples);
    }
    if (!rfaOk) { Console.WriteLine("FAIL: .rfa growth substitution mismatch"); return 1; }
    Console.WriteLine($".rfa substitution byte-exact ({string.Join(", ", names)})");

    Console.WriteLine("FOLIAGE EDIT TESTS PASSED");
    return 0;
}

if (arg == "fogfind" && args.Length >= 2)
{
    // fogfind <level.rfa> — print every .con line mentioning fog/viewdistance across the level archive.
    var arc = RefractorForge.Formats.Rfa.RfaArchive.Open(args[1]);
    foreach (var e in arc.Entries)
    {
        var n = e.Name.Replace('\\', '/');
        if (!n.EndsWith(".con", StringComparison.OrdinalIgnoreCase)) continue;
        string txt;
        try { txt = System.Text.Encoding.Latin1.GetString(arc.Read(e)); } catch { continue; }
        foreach (var line in txt.Split('\n'))
            if (line.Contains("fog", StringComparison.OrdinalIgnoreCase) || line.Contains("viewdistance", StringComparison.OrdinalIgnoreCase))
                Console.WriteLine($"  [{n}] {line.Trim()}");
    }
    return 0;
}

if (arg == "smfind" && args.Length >= 3)
{
    // smfind <std.rfa,obj.rfa> <substr1> [substr2 ...] — list .sm basenames containing each substring.
    var lib = RefractorForge.Render.MeshLibrary.Open(args[1].Split(',', StringSplitOptions.RemoveEmptyEntries));
    Console.WriteLine($"meshes={lib.MeshCount}");
    foreach (var key in args.Skip(2))
    {
        var hits = lib.MeshBaseNames.Where(x => x.Contains(key, StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.Length).Take(20).ToList();
        Console.WriteLine($"  '{key}' ({hits.Count}+): {string.Join(", ", hits)}");
    }
    return 0;
}

if (arg == "vehlist" && args.Length >= 2)
{
    // vehlist <objects.rfa> — list every .../Vehicles/<Cat>/<Name>/Objects.con folder name.
    var arc = RefractorForge.Formats.Rfa.RfaArchive.Open(args[1]);
    var names = arc.Entries
        .Select(e => e.Name.Replace('\\', '/'))
        .Where(n => n.EndsWith("/Objects.con", StringComparison.OrdinalIgnoreCase) && n.Contains("/Vehicles/", StringComparison.OrdinalIgnoreCase))
        .Select(n => { var s = n.Split('/'); return s[^2]; })
        .OrderBy(x => x).ToList();
    Console.WriteLine($"vehicle folders ({names.Count}):");
    foreach (var n in names) Console.WriteLine("  " + n);
    return 0;
}

if (arg == "vehasm" && args.Length >= 3)
{
    // vehasm <stdMesh.rfa,objects.rfa> <vehicle1> [vehicle2 ...]
    // Assemble each vehicle from its Objects.con hierarchy and report part count + combined bounds.
    var archives = args[1].Split(',', StringSplitOptions.RemoveEmptyEntries);
    var lib = RefractorForge.Render.MeshLibrary.Open(archives);
    Console.WriteLine($"meshes={lib.MeshCount}");
    foreach (var veh in args.Skip(2))
    {
        bool ok = lib.TryAssembleVehicle(veh, out var parts);
        if (!ok) { Console.WriteLine($"{veh,-14} ASSEMBLE FAILED (no con / no geometry)"); continue; }
        float minX=float.MaxValue,minY=float.MaxValue,minZ=float.MaxValue,maxX=float.MinValue,maxY=float.MinValue,maxZ=float.MinValue;
        int tris = 0;
        foreach (var p in parts)
        {
            tris += p.Mesh.Triangles;
            foreach (var v in p.Mesh.Positions)
            {
                var w = System.Numerics.Vector3.Transform(v, p.Local);
                minX=Math.Min(minX,w.X); maxX=Math.Max(maxX,w.X); minY=Math.Min(minY,w.Y); maxY=Math.Max(maxY,w.Y); minZ=Math.Min(minZ,w.Z); maxZ=Math.Max(maxZ,w.Z);
            }
        }
        Console.WriteLine($"{veh,-14} parts={parts.Length} tris={tris} bbox=({minX:0.0}..{maxX:0.0}, {minY:0.0}..{maxY:0.0}, {minZ:0.0}..{maxZ:0.0})");
    }
    return 0;
}

if (arg == "cloudcon")
{
    // cloudcon — Cloud system parse<->emit round-trip + patch preserves skybox/sun + remove.
    int fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }
    bool Near(float a, float b) => Math.Abs(a - b) < 1e-3f;

    // (1) Emit a cloud layer, re-parse, assert it round-trips.
    var e = new RefractorForge.Formats.Terrain.EnvironmentSettings { SkyBoxMesh = "Sky_OI_m1", SkyRotationAngle = -45f };
    e.Clouds.Add(new RefractorForge.Formats.Terrain.EnvironmentSettings.CloudLayer { Name = "cloud_0", SpeedX = -0.03f, SpeedY = 0.015f, Height = 3500f, TexScale = 8f });
    var emitted = e.ToSkyAndSunConLines().ToList();
    var re = RefractorForge.Formats.Terrain.EnvironmentSettings.Parse(emitted, null, null);
    Check(re.Clouds.Count == 1, $"cloud layer round-trips ({re.Clouds.Count})");
    Check(re.SkyBoxMesh == "Sky_OI_m1", $"skybox mesh NOT mistaken for cloud mesh ({re.SkyBoxMesh})");
    Check(re.CloudMeshFile == "cloud", $"cloud mesh file parsed ({re.CloudMeshFile})");
    if (re.Clouds.Count == 1)
    {
        var c = re.Clouds[0];
        Check(Near(c.SpeedX, -0.03f) && Near(c.SpeedY, 0.015f), $"cloud speed round-trips ({c.SpeedX}/{c.SpeedY})");
        Check(Near(c.Height, 3500f) && Near(c.TexScale, 8f), $"cloud height/texScale round-trip ({c.Height}/{c.TexScale})");
    }

    // (2) Patch into an existing SkyAndSun.con (skybox + sun, no clouds) -> skybox/sun preserved + cloud block added.
    var baseSky = new List<string>
    {
        "GeometryTemplate.create StandardMesh SkyBox", "GeometryTemplate.file Sky_OI_m1", "Sky.initSky",
        "Sky.setRotAngle -45", "sky.sunLightDirectionVec 0.64/0.34/-0.68",
    };
    var patched = e.PatchSkyAndSunConLines(baseSky);
    Check(patched.Any(l => l.Contains("Sky_OI_m1")), "patch preserves skybox mesh");
    Check(patched.Any(l => l.Contains("sunLightDirectionVec")), "patch preserves sun direction");
    Check(patched.Any(l => l.Trim().Equals("Sky.addCloud", StringComparison.OrdinalIgnoreCase)), "patch adds the cloud block");
    var reParsed = RefractorForge.Formats.Terrain.EnvironmentSettings.Parse(patched, null, null);
    Check(reParsed.SkyBoxMesh == "Sky_OI_m1" && reParsed.Clouds.Count == 1, "patched file re-parses (skybox + 1 cloud)");

    // (3) Re-patch (idempotent: strip old + re-add -> still exactly one cloud block).
    var patched2 = e.PatchSkyAndSunConLines(patched);
    Check(patched2.Count(l => l.Trim().Equals("Sky.addCloud", StringComparison.OrdinalIgnoreCase)) == 1, "re-patch does not duplicate the cloud block");

    // (4) Remove clouds (empty list) -> cloud block stripped, skybox preserved.
    var noClouds = new RefractorForge.Formats.Terrain.EnvironmentSettings { SkyBoxMesh = "Sky_OI_m1" };
    var removed = noClouds.PatchSkyAndSunConLines(patched2);
    Check(!removed.Any(l => l.Trim().Equals("Sky.addCloud", StringComparison.OrdinalIgnoreCase)), "removing clouds strips the cloud block");
    Check(removed.Any(l => l.Contains("Sky_OI_m1")), "removing clouds preserves the skybox");

    Console.WriteLine(fails == 0 ? "CLOUD CON TESTS PASSED" : $"CLOUD CON TESTS FAILED ({fails})");
    return fails == 0 ? 0 : 1;
}

if (arg == "catdump" && args.Length >= 3)
{
    // catdump <archive.rfa> <entry-substring> — print the text of the first matching entry.
    var a = RefractorForge.Formats.Rfa.RfaArchive.Open(args[1]);
    var sub = args[2].ToLowerInvariant();
    foreach (var e in a.Entries)
        if (e.Name.Replace('\\', '/').ToLowerInvariant().Contains(sub))
        { Console.WriteLine($"--- {e.Name} ---"); Console.WriteLine(System.Text.Encoding.Latin1.GetString(a.Read(e))); return 0; }
    Console.WriteLine($"no entry matching '{sub}'"); return 1;
}

if (arg == "rsdump" && args.Length >= 3)
{
    // rsdump <archive.rfa> <entry-substring> — parse the first matching .rs shader set and print each material's flags.
    var a = RefractorForge.Formats.Rfa.RfaArchive.Open(args[1]);
    var sub = args[2].ToLowerInvariant();
    foreach (var e in a.Entries)
        if (e.Name.Replace('\\', '/').ToLowerInvariant().Contains(sub) && e.Name.ToLowerInvariant().EndsWith(".rs"))
        {
            var ss = RefractorForge.Render.RsShaderSet.Parse(System.Text.Encoding.Latin1.GetString(a.Read(e)));
            Console.WriteLine($"--- {e.Name} ({ss.Materials.Count} materials) ---");
            foreach (var m in ss.Materials.Values)
                Console.WriteLine($"  {m.Name,-34} tex={m.Texture,-22} fade={m.TextureFade} transparent={m.Transparent}");
            return 0;
        }
    Console.WriteLine($"no .rs matching '{sub}'"); return 1;
}

if (arg == "meshresolve" && args.Length >= 3)
{
    // meshresolve <a.rfa,...> <name> [name2...] — does TryGet resolve each name to a mesh?
    var lib = RefractorForge.Render.MeshLibrary.Open(args[1].Split(',', StringSplitOptions.RemoveEmptyEntries));
    foreach (var name in args.Skip(2))
    {
        bool ok = lib.TryGet(name, out var m);
        Console.WriteLine($"{name,-28} {(ok && m is not null ? $"RESOLVED {m.Triangles} tris" : "NOT FOUND")}  HasMeshEntry={lib.HasMeshEntry(name)}");
    }
    return 0;
}

if (arg == "objauditall" && args.Length >= 3)
{
    // objauditall <modDir> <levelsDir> [skip] [take] — run the objaudit logic over every base level .rfa in a
    // folder (skipping _NNN patch archives; they're auto-mounted per level). One summary line per level,
    // failure detail lines only when something fails, load errors reported instead of crashing the sweep.
    var modDirA = args[1].TrimEnd('\\', '/');
    var levelsDir = args[2].TrimEnd('\\', '/');
    int skip = args.Length > 3 && int.TryParse(args[3], out var s0) ? s0 : 0;
    int take = args.Length > 4 && int.TryParse(args[4], out var t0) ? t0 : int.MaxValue;

    string? gameRootA = null;
    for (var d = new DirectoryInfo(modDirA); d?.Parent is not null; d = d.Parent)
        if (d.Name.Equals("Mods", StringComparison.OrdinalIgnoreCase)) { gameRootA = d.Parent.FullName; break; }
    if (gameRootA is null) { Console.WriteLine("modDir is not under a Mods\\ folder"); return 1; }
    var modPathsA = new List<string>();
    var initConA = System.IO.Path.Combine(modDirA, "init.con");
    if (File.Exists(initConA))
        foreach (var raw in File.ReadAllLines(initConA))
        {
            var line = raw.Trim();
            int sp = line.IndexOf(' ');
            if (sp < 0 || !line[..sp].Equals("game.addModPath", StringComparison.OrdinalIgnoreCase)) continue;
            var rel = line[(sp + 1)..].Trim().Trim('"').Replace('/', System.IO.Path.DirectorySeparatorChar).TrimEnd(System.IO.Path.DirectorySeparatorChar);
            if (rel.Length == 0) continue;
            var abs = System.IO.Path.GetFullPath(System.IO.Path.Combine(gameRootA, rel));
            if (Directory.Exists(abs) && !modPathsA.Any(p => p.Equals(abs, StringComparison.OrdinalIgnoreCase))) modPathsA.Add(abs);
        }
    if (modPathsA.Count == 0) modPathsA.Add(modDirA);
    var baseGuessA = new[] { "BfVietnam", "bf1942", "bfvietnam" }.Select(b => System.IO.Path.Combine(gameRootA, "Mods", b)).FirstOrDefault(Directory.Exists);
    if (baseGuessA is not null && !modPathsA.Any(p => p.Equals(baseGuessA, StringComparison.OrdinalIgnoreCase))) modPathsA.Add(baseGuessA);
    string[] AllRfaA(string dir) => Directory.Exists(dir)
        ? Directory.EnumerateFiles(dir, "*.rfa", SearchOption.AllDirectories).Where(f => !System.IO.Path.GetFileName(f).StartsWith("~")).ToArray()
        : Array.Empty<string>();
    var allRfasA = new List<string>();
    foreach (var mp in modPathsA)
        allRfasA.AddRange(AllRfaA(Directory.Exists(System.IO.Path.Combine(mp, "Archives")) ? System.IO.Path.Combine(mp, "Archives") : mp));
    var meshListA = allRfasA.Where(p => !System.IO.Path.GetFileName(p).StartsWith("texture", StringComparison.OrdinalIgnoreCase)
                                        && !p.Replace('\\', '/').ToLowerInvariant().Contains("/levels/"))
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    var levels = Directory.EnumerateFiles(levelsDir, "*.rfa", SearchOption.TopDirectoryOnly)
        .Where(f => !System.Text.RegularExpressions.Regex.IsMatch(System.IO.Path.GetFileNameWithoutExtension(f), @"_\d+$"))
        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
        .Skip(skip).Take(take).ToArray();
    Console.WriteLine($"chain [{string.Join(" -> ", modPathsA.Select(System.IO.Path.GetFileName))}], {meshListA.Length} mesh archive(s), {levels.Length} level(s) [skip {skip}]");

    foreach (var lvlPath in levels)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(lvlPath);
        try
        {
            var lvlRfasA = new List<string> { lvlPath };
            var dirL = System.IO.Path.GetDirectoryName(lvlPath)!;
            foreach (var sib in Directory.EnumerateFiles(dirL, name + "_*.rfa"))
                if (System.Text.RegularExpressions.Regex.IsMatch(System.IO.Path.GetFileNameWithoutExtension(sib), "^" + System.Text.RegularExpressions.Regex.Escape(name) + @"_\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    lvlRfasA.Add(sib);
            var lvlA = RefractorForge.Render.LevelArchive.FromRfa(lvlRfasA.ToArray());
            var sofA = lvlA.StaticObjects;
            if (sofA is null || sofA.Objects.Count == 0) { Console.WriteLine($"LEVEL {name}: 0 placed (no static objects)"); continue; }
            var libA = RefractorForge.Render.MeshLibrary.Open(meshListA.Concat(lvlRfasA).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            var groupsA = sofA.Objects.GroupBy(o => o.Template, StringComparer.OrdinalIgnoreCase)
                                      .Select(g => (Template: g.Key, Count: g.Count())).ToList();
            int okT2 = 0, failT2 = 0, failI2 = 0;
            var fails2 = new List<(string T, int C, string W)>();
            foreach (var (t, c) in groupsA)
            {
                if (libA.TryGetRenderMesh(t, out _)) okT2++;
                else { failT2++; failI2 += c; fails2.Add((t, c, libA.Diagnose(t))); }
            }
            // VEHICLE SPAWNS: resolve each spawned vehicle the same way the editor renders them (TryGetRenderMesh).
            var vg = lvlA.Gameplay.VehicleSpawns.GroupBy(v => v.Vehicle, StringComparer.OrdinalIgnoreCase).Select(g => (T: g.Key, C: g.Count())).ToList();
            int vok = 0, vfail = 0; var vfails = new List<(string T, int C)>();
            foreach (var (t, c) in vg)
            {
                if (!string.IsNullOrWhiteSpace(t) && libA.TryGetRenderMesh(t, out _)) vok++;
                else { vfail++; vfails.Add((t, c)); }
            }
            Console.WriteLine($"LEVEL {name}: {sofA.Objects.Count} placed, {groupsA.Count} unique, {okT2} ok, {failT2} fail ({failI2} instances) | vehicles {vok} ok, {vfail} fail");
            foreach (var f in fails2.OrderByDescending(x => x.C))
                Console.WriteLine($"FAIL {name} | {f.T} | x{f.C} | {f.W}");
            foreach (var f in vfails.OrderByDescending(x => x.C))
                Console.WriteLine($"VEHFAIL {name} | {f.T} | x{f.C}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"LEVEL {name}: ERROR {ex.GetType().Name}: {ex.Message}");
        }
    }
    return 0;
}

if (arg == "objaudit" && args.Length >= 3)
{
    // objaudit <modDir> <level.rfa[,patch.rfa,...]> [-v]
    // Audit EVERY placed static-object template in a level against the same archive set the Viewer's
    // Open Mod path builds (init.con mount chain + base mod + the level's own .rfa), and explain each failure.
    var modDir = args[1].TrimEnd('\\', '/');
    bool verbose = args.Contains("-v");

    // --- replicate OpenMod's archive collection ---
    string? gameRoot = null;
    for (var d = new DirectoryInfo(modDir); d?.Parent is not null; d = d.Parent)
        if (d.Name.Equals("Mods", StringComparison.OrdinalIgnoreCase)) { gameRoot = d.Parent.FullName; break; }
    if (gameRoot is null) { Console.WriteLine("modDir is not under a Mods\\ folder"); return 1; }
    var modPaths = new List<string>();
    var initCon = System.IO.Path.Combine(modDir, "init.con");
    if (File.Exists(initCon))
        foreach (var raw in File.ReadAllLines(initCon))
        {
            var line = raw.Trim();
            int sp = line.IndexOf(' ');
            if (sp < 0 || !line[..sp].Equals("game.addModPath", StringComparison.OrdinalIgnoreCase)) continue;
            var rel = line[(sp + 1)..].Trim().Trim('"').Replace('/', System.IO.Path.DirectorySeparatorChar).TrimEnd(System.IO.Path.DirectorySeparatorChar);
            if (rel.Length == 0) continue;
            var abs = System.IO.Path.GetFullPath(System.IO.Path.Combine(gameRoot, rel));
            if (Directory.Exists(abs) && !modPaths.Any(p => p.Equals(abs, StringComparison.OrdinalIgnoreCase))) modPaths.Add(abs);
        }
    if (modPaths.Count == 0) modPaths.Add(modDir);
    var baseGuess = new[] { "BfVietnam", "bf1942", "bfvietnam" }.Select(b => System.IO.Path.Combine(gameRoot, "Mods", b)).FirstOrDefault(Directory.Exists);
    if (baseGuess is not null && !modPaths.Any(p => p.Equals(baseGuess, StringComparison.OrdinalIgnoreCase))) modPaths.Add(baseGuess);

    string[] AllRfa(string dir) => Directory.Exists(dir)
        ? Directory.EnumerateFiles(dir, "*.rfa", SearchOption.AllDirectories).Where(f => !System.IO.Path.GetFileName(f).StartsWith("~")).ToArray()
        : Array.Empty<string>();
    bool IsLevelRfa(string p) => p.Replace('\\', '/').ToLowerInvariant().Contains("/levels/");
    bool IsTex(string p) => System.IO.Path.GetFileName(p).StartsWith("texture", StringComparison.OrdinalIgnoreCase);
    var allRfas = new List<string>();
    foreach (var mp in modPaths)
        allRfas.AddRange(AllRfa(Directory.Exists(System.IO.Path.Combine(mp, "Archives")) ? System.IO.Path.Combine(mp, "Archives") : mp));
    var meshList = allRfas.Where(p => !IsTex(p) && !IsLevelRfa(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    // --- the level: include any _NNN patch siblings automatically ---
    var lvlRfas = args[2].Split(',', StringSplitOptions.RemoveEmptyEntries).Where(File.Exists).ToList();
    foreach (var b in lvlRfas.ToArray())
    {
        var dir = System.IO.Path.GetDirectoryName(b)!;
        var stem = System.IO.Path.GetFileNameWithoutExtension(b);
        foreach (var sib in Directory.EnumerateFiles(dir, stem + "_*.rfa"))
            if (System.Text.RegularExpressions.Regex.IsMatch(System.IO.Path.GetFileNameWithoutExtension(sib), "^" + System.Text.RegularExpressions.Regex.Escape(stem) + @"_\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                && !lvlRfas.Any(x => x.Equals(sib, StringComparison.OrdinalIgnoreCase)))
                lvlRfas.Add(sib);
    }
    Console.WriteLine($"chain [{string.Join(" -> ", modPaths.Select(System.IO.Path.GetFileName))}], {meshList.Length} mesh archive(s), level: {string.Join(" + ", lvlRfas.Select(System.IO.Path.GetFileName))}");

    var lvl = RefractorForge.Render.LevelArchive.FromRfa(lvlRfas.ToArray());
    var sof = lvl.StaticObjects;
    if (sof is null || sof.Objects.Count == 0) { Console.WriteLine("level has no static objects"); return 1; }

    // --- the mesh library, exactly like the Viewer: user/mod archives first, level rfas after ---
    var meshA = meshList.Concat(lvlRfas).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    var lib2 = RefractorForge.Render.MeshLibrary.Open(meshA);

    var groups = sof.Objects.GroupBy(o => o.Template, StringComparer.OrdinalIgnoreCase)
                            .Select(g => (Template: g.Key, Count: g.Count()))
                            .OrderByDescending(g => g.Count).ToList();
    int okT = 0, failT = 0, okI = 0, failI = 0;
    var fails = new List<(string Template, int Count, string Why)>();
    foreach (var (t, c) in groups)
    {
        if (lib2.TryGetRenderMesh(t, out var rm) && rm is not null)
        {
            okT++; okI += c;
            // A mesh that resolves but has very few triangles renders as little/nothing — flag it (it "loads"
            // by the audit but the user sees a missing building). Collision-only meshes have 0 tris.
            if (verbose || rm.Triangles < 50) Console.WriteLine($"  {(rm.Triangles < 50 ? "LOW " : "ok  ")}  {t,-40} x{c,-4} {rm.Triangles} tris");
        }
        else { failT++; failI += c; fails.Add((t, c, lib2.Diagnose(t))); }
    }
    Console.WriteLine($"{sof.Objects.Count} placed objects, {groups.Count} unique templates: {okT} resolve ({okI} instances), {failT} FAIL ({failI} instances)");
    foreach (var grp in fails.GroupBy(f => f.Why.Split(':')[0]).OrderByDescending(g => g.Sum(x => x.Count)))
    {
        Console.WriteLine($"--- {grp.Key} ({grp.Count()} templates, {grp.Sum(x => x.Count)} instances) ---");
        foreach (var f in grp.OrderByDescending(x => x.Count))
            Console.WriteLine($"  {f.Template,-44} x{f.Count,-5} {f.Why}");
    }
    return 0;
}

if (arg == "badarchive")
{
    // badarchive — a corrupt / temp / non-RFA file in an archive list must be SKIPPED, never crash the load
    // (a stray 162-byte ~$andardMesh.rfa in Op_Remembrance hard-crashed the editor via an unhandled exception).
    int fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }
    var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rf_badarc_" + System.Guid.NewGuid().ToString("N")[..6]);
    System.IO.Directory.CreateDirectory(tmp);
    var temp = System.IO.Path.Combine(tmp, "~$andardMesh.rfa"); System.IO.File.WriteAllBytes(temp, new byte[162]);   // temp/lock leftover
    var junk = System.IO.Path.Combine(tmp, "garbage.rfa"); System.IO.File.WriteAllBytes(junk, new byte[] { 1, 2, 3, 4, 5 });  // too short to parse
    try
    {
        try { var _ = RefractorForge.Render.MeshLibrary.Open(temp, junk); Check(true, "MeshLibrary.Open survived a temp + corrupt archive (no crash)"); }
        catch (Exception ex) { Check(false, $"MeshLibrary.Open threw {ex.GetType().Name}"); }
        try { var _ = RefractorForge.Render.TextureLibrary.Open(temp, junk); Check(true, "TextureLibrary.Open survived"); }
        catch (Exception ex) { Check(false, $"TextureLibrary.Open threw {ex.GetType().Name}"); }
        // FromRfa should cleanly report "no readable archive" (FileNotFound), not crash with ArgumentOutOfRange.
        try { RefractorForge.Render.LevelArchive.FromRfa(temp, junk); Check(false, "LevelArchive.FromRfa should have reported no readable archive"); }
        catch (System.IO.FileNotFoundException) { Check(true, "LevelArchive.FromRfa cleanly reports no readable archive"); }
        catch (Exception ex) { Check(false, $"LevelArchive.FromRfa threw {ex.GetType().Name}, expected FileNotFoundException"); }
    }
    finally { try { System.IO.Directory.Delete(tmp, true); } catch { } }
    Console.WriteLine(fails == 0 ? "BAD ARCHIVE TESTS PASSED" : $"BAD ARCHIVE TESTS FAILED ({fails})");
    return fails == 0 ? 0 : 1;
}

if (arg == "rfals" && args.Length >= 3)
{
    // rfals <archive.rfa> <substring> — list archive entry names containing the substring (asset discovery).
    var a = RefractorForge.Formats.Rfa.RfaArchive.Open(args[1]);
    var sub = args[2].ToLowerInvariant();
    int n = 0;
    foreach (var e in a.Entries) { var nm = e.Name.Replace('\\', '/'); if (nm.ToLowerInvariant().Contains(sub)) { Console.WriteLine("  " + nm); if (++n >= 80) break; } }
    Console.WriteLine($"{n} entr(ies) matching '{sub}' in {System.IO.Path.GetFileName(args[1])} (of {a.Entries.Count})");
    return 0;
}

if (arg == "rfatoc" && args.Length >= 2)
{
    // rfatoc <archive.rfa> [substring] — dump EVERY entry name + uncompressed size (NO 80 cap). RE/asset discovery.
    var a = RefractorForge.Formats.Rfa.RfaArchive.Open(args[1]);
    var sub = (args.Length >= 3 ? args[2] : "").ToLowerInvariant();
    int n = 0; long tot = 0;
    foreach (var e in a.Entries)
    {
        var nm = e.Name.Replace('\\', '/');
        if (sub.Length > 0 && !nm.ToLowerInvariant().Contains(sub)) continue;
        Console.WriteLine($"{e.UncompressedSize,10}  {nm}");
        n++; tot += e.UncompressedSize;
    }
    Console.WriteLine($"{n} entr(ies) (of {a.Entries.Count}), {tot} bytes uncompressed");
    return 0;
}

if (arg == "rfahex" && args.Length >= 3)
{
    // rfahex <archive.rfa> <entry-substring> [count] — decode the first matching entry and hex-dump the first
    // [count] bytes (default 256) with offset/hex/ASCII, plus a dword view (int/float/hex) of the header. Format RE.
    var a = RefractorForge.Formats.Rfa.RfaArchive.Open(args[1]);
    var sub = args[2].ToLowerInvariant();
    RefractorForge.Formats.Rfa.RfaEntry? hit = null;
    foreach (var e in a.Entries) { if (e.Name.Replace('\\', '/').ToLowerInvariant().Contains(sub)) { hit = e; break; } }
    if (hit == null) { Console.WriteLine($"no entry matching '{sub}'"); return 1; }
    var data = a.Read(hit);
    int count = args.Length >= 4 ? Math.Min(int.Parse(args[3]), data.Length) : Math.Min(256, data.Length);
    Console.WriteLine($"ENTRY {hit.Name.Replace('\\', '/')}  size={data.Length} bytes");
    for (int o = 0; o < count; o += 16)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"{o:X4}  ");
        for (int i = 0; i < 16; i++) { if (o + i < count) sb.Append($"{data[o + i]:X2} "); else sb.Append("   "); if (i == 7) sb.Append(' '); }
        sb.Append(" |");
        for (int i = 0; i < 16 && o + i < count; i++) { byte b = data[o + i]; sb.Append(b >= 32 && b < 127 ? (char)b : '.'); }
        sb.Append('|');
        Console.WriteLine(sb.ToString());
    }
    Console.WriteLine("-- dword view (offset: int / float / hex) --");
    for (int o = 0; o + 4 <= Math.Min(count, 96); o += 4)
        Console.WriteLine($"  +{o,3}: {BitConverter.ToInt32(data, o),12} / {BitConverter.ToSingle(data, o),16:G6} / 0x{BitConverter.ToInt32(data, o):X8}");
    return 0;
}

if (arg == "skeletal" && args.Length >= 2)
{
    // skeletal <animations.rfa> — parse .ske/.baf/.skn, compose a soldier rest pose, pose it with a clip,
    // and validate everything headlessly: humanoid rest pose (the decisive matrix-convention check),
    // unit quaternions on every animated frame, and skin weights summing to 1.
    var rfa = RfaArchive.Open(args[1]);
    int fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }

    byte[]? Find(params string[] subs)
    {
        foreach (var sub in subs)
            foreach (var e in rfa.Entries)
                if (e.Name.Replace('\\', '/').ToLowerInvariant().Contains(sub.ToLowerInvariant()))
                    return rfa.Read(e);
        return null;
    }

    // ---------- Skeleton ----------
    var skeData = Find("ussoldier.ske", "japsoldier.ske", "soldier.ske", ".ske");
    if (skeData == null) { Console.WriteLine("no .ske in archive"); return 1; }

    var ske = Skeleton.Load(skeData);
    Console.WriteLine($"skeleton: version {ske.Version}, {ske.Bones.Count} bones");
    var world = ske.ComputeWorld(); // rest pose
    int Bone(string sub)
    {
        for (int i = 0; i < ske.Bones.Count; i++)
            if (ske.Bones[i].Name.ToLowerInvariant().Contains(sub.ToLowerInvariant())) return i;
        return -1;
    }
    (float X, float Y, float Z) P(int i) => i < 0 ? (0, 0, 0) : SkeletalMath.Translation(world[i]);
    float Axis((float X, float Y, float Z) p, int a) => a == 0 ? p.X : a == 1 ? p.Y : p.Z;

    // The Biped is authored Z-up; find the figure's tall axis (largest world span) and validate the humanoid
    // along it without assuming which axis is "up".
    var mn = (X: 1e9f, Y: 1e9f, Z: 1e9f); var mx = (X: -1e9f, Y: -1e9f, Z: -1e9f);
    foreach (var w in world) { var t = SkeletalMath.Translation(w); mn = (MathF.Min(mn.X, t.X), MathF.Min(mn.Y, t.Y), MathF.Min(mn.Z, t.Z)); mx = (MathF.Max(mx.X, t.X), MathF.Max(mx.Y, t.Y), MathF.Max(mx.Z, t.Z)); }
    var span = (mx.X - mn.X, mx.Y - mn.Y, mx.Z - mn.Z);
    int up = span.Item1 >= span.Item2 && span.Item1 >= span.Item3 ? 0 : (span.Item2 >= span.Item3 ? 1 : 2);
    float height = up == 0 ? span.Item1 : up == 1 ? span.Item2 : span.Item3;
    int iHead = Bone("head"), iPelvis = Bone("pelvis"), iLFoot = Bone("l foot"), iRFoot = Bone("r foot"),
        iLHand = Bone("l hand"), iRHand = Bone("r hand");
    var head = P(iHead); var pelvis = P(iPelvis); var lfoot = P(iLFoot); var rfoot = P(iRFoot);
    var lhand = P(iLHand); var rhand = P(iRHand);
    Console.WriteLine($"  rest-pose span X={span.Item1:0.00} Y={span.Item2:0.00} Z={span.Item3:0.00}  -> up-axis {"XYZ"[up]}, height {height:0.00} m");
    Console.WriteLine($"  head=({head.X:0.00},{head.Y:0.00},{head.Z:0.00}) pelvis=({pelvis.X:0.00},{pelvis.Y:0.00},{pelvis.Z:0.00}) Lfoot=({lfoot.X:0.00},{lfoot.Y:0.00},{lfoot.Z:0.00}) Lhand=({lhand.X:0.00},{lhand.Y:0.00},{lhand.Z:0.00})");

    Check(height > 1.4f && height < 2.0f, $"rest-pose height humanoid (~1.7m), got {height:0.00}");
    float upMin = Axis((mn.X, mn.Y, mn.Z), up), upMax = Axis((mx.X, mx.Y, mx.Z), up);
    if (iHead >= 0 && iLFoot >= 0)
    {
        // head and feet sit at opposite extremes of the up axis (whichever sign is "up")
        float h = Axis(head, up), lf = Axis(lfoot, up), rf = Axis(rfoot, up);
        bool headTop = MathF.Abs(h - upMin) < 0.30f || MathF.Abs(h - upMax) < 0.30f;
        bool feetBottom = MathF.Abs(lf - upMin) < 0.30f || MathF.Abs(lf - upMax) < 0.30f;
        Check(headTop && feetBottom, "head and feet at opposite extremes of the up axis");
        Check(MathF.Abs(h - lf) > 1.2f, $"head-to-foot distance along up axis ~human ({MathF.Abs(h - lf):0.00} m)");
    }
    if (iHead >= 0 && iPelvis >= 0)
        Check(MathF.Abs(Axis(head, up) - Axis(pelvis, up)) > 0.3f, "head clearly separated from pelvis along up axis");
    // standing figure: the two non-up axes are narrow
    float w1 = up == 0 ? span.Item2 : span.Item1, w2 = up == 2 ? span.Item2 : span.Item3;
    Check(w1 < 1.2f && w2 < 1.2f, $"figure is tall and narrow (cross spans {w1:0.00}, {w2:0.00})");
    if (iLHand >= 0 && iRHand >= 0)
    {
        float sep = MathF.Sqrt((lhand.X - rhand.X) * (lhand.X - rhand.X) + (lhand.Y - rhand.Y) * (lhand.Y - rhand.Y) + (lhand.Z - rhand.Z) * (lhand.Z - rhand.Z));
        Check(sep > 0.20f, $"hands separated ({sep:0.00} m apart)");
    }
    // every bone connects to its parent within a sane bone length
    float maxBoneLen = 0f;
    for (int i = 0; i < ske.Bones.Count; i++)
    {
        int p = ske.Bones[i].Parent;
        if (p < 0) continue;
        var a = SkeletalMath.Translation(world[i]); var b = SkeletalMath.Translation(world[p]);
        maxBoneLen = MathF.Max(maxBoneLen, MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z)));
    }
    Check(maxBoneLen < 1.5f, $"all bones connected (max bone length {maxBoneLen:0.00} m)");

    // ---------- Animation ----------
    // Find a soldier clip that binds several Bip01 bones; pose the skeleton and check unit quaternions.
    BoneAnimation? clip = null; int boundCount = 0;
    foreach (var e in rfa.Entries)
    {
        var nm = e.Name.Replace('\\', '/');
        if (!nm.ToLowerInvariant().EndsWith(".baf")) continue;
        BoneAnimation c;
        try { c = BoneAnimation.Load(rfa.Read(e)); } catch { continue; }
        int bound = 0; foreach (var ab in c.Bones) if (ske.FindBone(ab.Name) >= 0) bound++;
        if (bound > boundCount) { boundCount = bound; clip = c; if (bound >= 8) { Console.WriteLine($"  clip: {nm}"); break; } }
    }
    if (clip != null)
    {
        Console.WriteLine($"  clip: {clip.Bones.Count} bones ({boundCount} bound), {clip.FrameCount} frames, divisor {clip.Divisor:0}");
        float worstQ = 0f;
        foreach (var ab in clip.Bones)
            for (int f = 0; f < clip.FrameCount; f++)
            {
                var q = clip.GetQuat(ab, f);
                float len = MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
                worstQ = MathF.Max(worstQ, MathF.Abs(len - 1f));
            }
        Check(worstQ < 0.02f, $"all clip quaternions unit-length (worst |q|-1 = {worstQ:0.0000})");

        var posed = SkeletalPose.PoseWorld(ske, clip, 0.0f);
        bool finite = true;
        foreach (var w in posed) foreach (var v in w) if (float.IsNaN(v) || float.IsInfinity(v)) finite = false;
        Check(finite, "posed world matrices finite");
        // posed figure should still be roughly human-sized along its tallest axis (consistency of the
        // quaternion-built animated locals with the .ske rest locals)
        var pmn = (X: 1e9f, Y: 1e9f, Z: 1e9f); var pmx = (X: -1e9f, Y: -1e9f, Z: -1e9f);
        foreach (var w in posed) { var t = SkeletalMath.Translation(w); pmn = (MathF.Min(pmn.X, t.X), MathF.Min(pmn.Y, t.Y), MathF.Min(pmn.Z, t.Z)); pmx = (MathF.Max(pmx.X, t.X), MathF.Max(pmx.Y, t.Y), MathF.Max(pmx.Z, t.Z)); }
        float pHeight = MathF.Max(pmx.X - pmn.X, MathF.Max(pmx.Y - pmn.Y, pmx.Z - pmn.Z));
        Check(pHeight > 1.3f && pHeight < 2.2f, $"posed figure human-sized (tallest span {pHeight:0.00} m)");
    }
    else Console.WriteLine("  (no .baf bound to this skeleton)");

    // ---------- Skin ----------
    var sknData = Find("body.skn", "1pusbody.skn", ".skn");
    if (sknData != null)
    {
        var skn = Skin.Load(sknData);
        Console.WriteLine($"  skin: version {skn.Version}, {skn.Vertices.Count} verts, {skn.BoneNames.Count} bones");
        float worstSum = 0f; int maxInfl = 0, maxBoneIdx = -1;
        foreach (var v in skn.Vertices)
        {
            float sum = 0f; maxInfl = Math.Max(maxInfl, v.Influences.Length);
            foreach (var inf in v.Influences) { sum += inf.Weight; maxBoneIdx = Math.Max(maxBoneIdx, inf.LocalBoneIndex); }
            worstSum = MathF.Max(worstSum, MathF.Abs(sum - 1f));
        }
        Check(worstSum < 1e-3f, $"skin weights sum to 1 (worst |sum-1| = {worstSum:0.000000})");
        Check(maxBoneIdx < skn.BoneNames.Count, $"skin bone indices in range (max {maxBoneIdx} < {skn.BoneNames.Count})");
        Console.WriteLine($"  skin influences/vertex up to {maxInfl}");
    }

    Console.WriteLine(fails == 0 ? "SKELETAL TESTS PASSED" : $"SKELETAL TESTS FAILED ({fails})");
    return fails == 0 ? 0 : 1;
}

if (arg == "skinalign" && args.Length >= 5)
{
    // skinalign <meshArchive.rfa> <animations.rfa> <smName> <sknName> — does the soldier .sm's LOD0 vertex
    // count match the .skn skin's vertex count (so skin weights index the mesh vertices 1:1)?
    var meshRfa = RfaArchive.Open(args[1]);
    var animRfa = RfaArchive.Open(args[2]);
    byte[]? Find(RfaArchive a, string sub)
    {
        foreach (var e in a.Entries)
            if (e.Name.Replace('\\', '/').ToLowerInvariant().Contains(sub.ToLowerInvariant())) return a.Read(e);
        return null;
    }
    var smData = Find(meshRfa, args[3]);
    var sknData = Find(animRfa, args[4]);
    if (smData == null) { Console.WriteLine($"no .sm matching '{args[3]}'"); return 1; }
    if (sknData == null) { Console.WriteLine($"no .skn matching '{args[4]}'"); return 1; }
    var sm = StandardMesh.Parse(smData);
    var skn = RefractorForge.Formats.Animation.Skin.Load(sknData);
    Console.WriteLine($"sm '{args[3]}': {sm.NumLods} LODs");
    for (int l = 0; l < sm.Lods.Count; l++)
    {
        int v = 0, f = 0;
        foreach (var m in sm.Lods[l]) { v += m.NumVertices; f += m.Faces.Length; }
        Console.WriteLine($"  LOD{l}: {sm.Lods[l].Count} mats, {v} verts, {f} tris  [{string.Join(",", System.Linq.Enumerable.Select(sm.Lods[l], m => m.NumVertices))}]");
    }
    Console.WriteLine($"skn '{args[4]}': {skn.Vertices.Count} verts, {skn.BoneNames.Count} bones");
    int lod0 = 0; foreach (var m in sm.Lods[0]) lod0 += m.NumVertices;
    Console.WriteLine(lod0 == skn.Vertices.Count
        ? $"MATCH: LOD0 verts == skn verts ({lod0}) -> skin maps 1:1"
        : $"NO 1:1 MATCH: LOD0 {lod0} != skn {skn.Vertices.Count} (testing position-weld mapping...)");

    // Position-weld test: can each .sm LOD0 vertex be matched to a .skn vertex by bind position?
    var sknPos = new System.Collections.Generic.List<System.Numerics.Vector3>(skn.Vertices.Count);
    foreach (var v in skn.Vertices) sknPos.Add(new System.Numerics.Vector3(v.X, v.Y, v.Z));
    float worst = 0f; int exact = 0, smVerts = 0;
    foreach (var mat in sm.Lods[0])
        foreach (var p in mat.Vertices)
        {
            smVerts++;
            float best = float.MaxValue;
            var pp = new System.Numerics.Vector3(p.X, p.Y, p.Z);
            foreach (var s in sknPos) { float d = System.Numerics.Vector3.DistanceSquared(pp, s); if (d < best) best = d; }
            best = MathF.Sqrt(best);
            if (best < 1e-4f) exact++;
            worst = MathF.Max(worst, best);
        }
    Console.WriteLine($"position-weld: {exact}/{smVerts} sm verts match a skn vertex within 1e-4 m; worst nearest-dist {worst:0.000000} m");
    Console.WriteLine(exact == smVerts ? "WELD OK: every sm vertex maps to a skn vertex by position" : "WELD PARTIAL: some sm verts have no exact skn match");
    return 0;
}

if (arg == "weaponattach" && args.Length >= 3)
{
    // weaponattach <animations.rfa> <standardMesh.rfa> [weaponSmName] — diagnose how a hand-held weapon mesh sits
    // relative to the soldier's right-hand bone, so we can attach it correctly. Prints the hand rest position and
    // the weapon's bounding box; the relationship tells us whether the weapon is authored in WEAPON-local space
    // (bbox near origin) or already in SOLDIER-model space (bbox at chest height near the hand).
    var animRfa = RfaArchive.Open(args[1]);
    var meshRfa = RfaArchive.Open(args[2]);
    string weaponName = args.Length >= 4 ? args[3] : "M1_Garand_base_M1";
    byte[]? Find(RfaArchive a, params string[] subs)
    {
        foreach (var sub in subs)
            foreach (var e in a.Entries)
                if (e.Name.Replace('\\', '/').ToLowerInvariant().Contains(sub.ToLowerInvariant())) return a.Read(e);
        return null;
    }
    var skeData = Find(animRfa, "ussoldier.ske", "soldier.ske", ".ske");
    if (skeData == null) { Console.WriteLine("no .ske"); return 1; }
    var ske = Skeleton.Load(skeData);
    var world = ske.ComputeWorld();
    int Bone(string sub) { for (int i = 0; i < ske.Bones.Count; i++) if (ske.Bones[i].Name.ToLowerInvariant().Contains(sub.ToLowerInvariant())) return i; return -1; }
    int iR = Bone("r hand"), iL = Bone("l hand");
    var rh = iR < 0 ? (0f, 0f, 0f) : SkeletalMath.Translation(world[iR]);
    var lh = iL < 0 ? (0f, 0f, 0f) : SkeletalMath.Translation(world[iL]);
    Console.WriteLine($"R Hand bone[{iR}] '{(iR < 0 ? "?" : ske.Bones[iR].Name)}' rest model pos = ({rh.Item1:0.000},{rh.Item2:0.000},{rh.Item3:0.000})");
    Console.WriteLine($"L Hand bone[{iL}] '{(iL < 0 ? "?" : ske.Bones[iL].Name)}' rest model pos = ({lh.Item1:0.000},{lh.Item2:0.000},{lh.Item3:0.000})");

    var wData = Find(meshRfa, "/" + weaponName.ToLowerInvariant() + ".sm", weaponName.ToLowerInvariant() + ".sm");
    if (wData == null) { Console.WriteLine($"no weapon .sm matching '{weaponName}'"); return 1; }
    var wm = StandardMesh.Parse(wData);
    var mn = new System.Numerics.Vector3(1e9f); var mx = new System.Numerics.Vector3(-1e9f); int wv = 0;
    foreach (var m in wm.Lods[0])
        foreach (var p in m.Vertices) { var v = new System.Numerics.Vector3(p.X, p.Y, p.Z); mn = System.Numerics.Vector3.Min(mn, v); mx = System.Numerics.Vector3.Max(mx, v); wv++; }
    var ctr = (mn + mx) * 0.5f; var ext = mx - mn;
    Console.WriteLine($"weapon '{weaponName}' LOD0: {wv} verts, {wm.Lods[0].Count} mats");
    Console.WriteLine($"  bbox min=({mn.X:0.000},{mn.Y:0.000},{mn.Z:0.000}) max=({mx.X:0.000},{mx.Y:0.000},{mx.Z:0.000})");
    Console.WriteLine($"  center=({ctr.X:0.000},{ctr.Y:0.000},{ctr.Z:0.000}) extent=({ext.X:0.000},{ext.Y:0.000},{ext.Z:0.000})  longest={MathF.Max(ext.X, MathF.Max(ext.Y, ext.Z)):0.000} m");
    float distCtrToHand = System.Numerics.Vector3.Distance(ctr, new System.Numerics.Vector3(rh.Item1, rh.Item2, rh.Item3));
    Console.WriteLine($"  weapon-center to R-hand = {distCtrToHand:0.000} m  -> {(ctr.Length() < 0.4f ? "WEAPON-LOCAL space (origin near grip)" : distCtrToHand < 0.6f ? "SOLDIER-MODEL space (already at the hand)" : "UNKNOWN frame")}");
    // .rs texture for the weapon (so the rifle isn't flat)
    var rsData = Find(meshRfa, "/" + weaponName.ToLowerInvariant() + ".rs", weaponName.ToLowerInvariant() + ".rs");
    if (rsData != null)
    {
        var rs = RefractorForge.Render.RsShaderSet.Parse(System.Text.Encoding.Latin1.GetString(rsData));
        foreach (var m in wm.Lods[0])
            Console.WriteLine($"  mat '{m.Name}' -> texture '{(rs.Materials.TryGetValue(m.Name, out var sh) ? sh.Texture : "(none)")}'");
    }
    return 0;
}

if (arg == "weaponpose" && args.Length >= 3)
{
    // weaponpose <animations.rfa> <standardMesh.rfa> [weaponSmName] — validate the held-weapon attach math headlessly
    // (mirrors SoldierRig.PoseWeapon, which lives in the GUI Exe project the Demo can't reference). Poses the soldier
    // standing (lower + upper M1Garand aim clip), anchors the rifle at the right hand with +Z along right->left hand,
    // and asserts the rifle is rifle-sized, the trigger hand holds it, and it points forward. No GPU.
    var animRfa = RfaArchive.Open(args[1]);
    var meshRfa = RfaArchive.Open(args[2]);
    string weaponName = args.Length >= 4 ? args[3] : "M1_Garand_base_M1";
    int fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }
    byte[]? Find(RfaArchive a, params string[] subs)
    {
        foreach (var sub in subs)
            foreach (var e in a.Entries)
                if (e.Name.Replace('\\', '/').ToLowerInvariant().Contains(sub.ToLowerInvariant())) return a.Read(e);
        return null;
    }
    var skeData = Find(animRfa, "ussoldier.ske", "soldier.ske", ".ske");
    var lowerData = Find(animRfa, "3pstandlower.baf");
    var upperData = Find(animRfa, "3pstandaimupperm1garand.baf", "3pstandupperm1garand.baf");
    var wData = Find(meshRfa, "/" + weaponName.ToLowerInvariant() + ".sm", weaponName.ToLowerInvariant() + ".sm");
    if (skeData == null || lowerData == null || wData == null) { Console.WriteLine("missing .ske / lower clip / weapon .sm"); return 1; }

    var ske = Skeleton.Load(skeData);
    var lower = BoneAnimation.Load(lowerData);
    var layers = new System.Collections.Generic.List<(BoneAnimation, float, int[]?)>();
    if (upperData != null) { var up = BoneAnimation.Load(upperData); layers.Add((up, 0f, SkeletalPose.BindClip(ske, up))); }
    layers.Add((lower, 0f, SkeletalPose.BindClip(ske, lower)));
    var world = SkeletalPose.PoseWorldLayered(ske, layers.ToArray());

    // same reorient (Biped Z-up -> engine Y-up) as SoldierRig
    var reorient = new System.Numerics.Matrix4x4(0, 0, -1, 0, 1, 0, 0, 0, 0, -1, 0, 0, 0, 0, 0, 1);
    int Bone(string sub) { for (int i = 0; i < ske.Bones.Count; i++) { var n = ske.Bones[i].Name.ToLowerInvariant(); if (!n.Contains("finger") && n.Contains(sub)) return i; } return -1; }
    int iR = Bone("r hand"), iL = Bone("l hand");
    if (iR < 0 || iL < 0) { Console.WriteLine("no hand bones"); return 1; }
    System.Numerics.Vector3 HandPos(int b) { var t = SkeletalMath.Translation(world[b]); return System.Numerics.Vector3.Transform(new System.Numerics.Vector3(t.X, t.Y, t.Z), reorient); }
    var rH = HandPos(iR); var lH = HandPos(iL);
    float handSep = System.Numerics.Vector3.Distance(rH, lH);

    var fwd = lH - rH; if (fwd.LengthSquared() < 1e-6f) fwd = System.Numerics.Vector3.UnitZ; fwd = System.Numerics.Vector3.Normalize(fwd);
    var worldUp = System.Numerics.Vector3.UnitY;
    var right = System.Numerics.Vector3.Cross(fwd, worldUp); if (right.LengthSquared() < 1e-6f) right = System.Numerics.Vector3.UnitX; right = System.Numerics.Vector3.Normalize(right);
    var gunUp = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(right, fwd));

    var wm = StandardMesh.Parse(wData);
    var mn = new System.Numerics.Vector3(1e9f); var mx = new System.Numerics.Vector3(-1e9f);
    float nearestR = 1e9f, nearestL = 1e9f, maxFwd = -1e9f, minFwd = 1e9f; int wv = 0;
    foreach (var m in wm.Lods[0])
        foreach (var p in m.Vertices)
        {
            var lp = new System.Numerics.Vector3(p.X, p.Y, p.Z);
            var wp = rH + right * lp.X + gunUp * lp.Y + fwd * lp.Z;   // same transform as SoldierRig.PoseWeapon
            mn = System.Numerics.Vector3.Min(mn, wp); mx = System.Numerics.Vector3.Max(mx, wp); wv++;
            nearestR = MathF.Min(nearestR, System.Numerics.Vector3.Distance(wp, rH));
            nearestL = MathF.Min(nearestL, System.Numerics.Vector3.Distance(wp, lH));
            float f = System.Numerics.Vector3.Dot(wp - rH, fwd);
            maxFwd = MathF.Max(maxFwd, f); minFwd = MathF.Min(minFwd, f);
        }
    var ext = mx - mn; float longest = MathF.Max(ext.X, MathF.Max(ext.Y, ext.Z));
    Console.WriteLine($"posed: hand-sep={handSep:0.000} m, rifle bbox extent=({ext.X:0.000},{ext.Y:0.000},{ext.Z:0.000}) longest={longest:0.000} m");
    Console.WriteLine($"  rH->nearest rifle vert={nearestR:0.000} m, lH->nearest rifle vert={nearestL:0.000} m, muzzle fwd={maxFwd:0.000} m, stock fwd={minFwd:0.000} m");

    Check(wv > 100, $"weapon mesh has geometry ({wv} verts)");
    Check(longest > 0.9f && longest < 1.4f, $"rifle is rifle-sized ({longest:0.00} m long, scale preserved)");
    Check(nearestR < 0.35f, $"trigger (right) hand holds the rifle (nearest vert {nearestR:0.000} m)");
    Check(nearestL < 0.50f, $"foregrip (left) hand near the rifle (nearest vert {nearestL:0.000} m)");
    Check(maxFwd > 0.4f, $"rifle points forward of the trigger hand (muzzle {maxFwd:0.000} m fwd)");
    Console.WriteLine(fails == 0 ? "WEAPON POSE TESTS PASSED" : $"WEAPON POSE TESTS FAILED ({fails})");
    return fails == 0 ? 0 : 1;
}

if (arg == "vehrender" && args.Length >= 4)
{
    // vehrender <a.rfa,...> <vehicle> <out.bmp> — rasterize the flattened assembled vehicle (CPU) so we can SEE
    // whether it's correctly assembled, headlessly.
    var archives = args[1].Split(',', StringSplitOptions.RemoveEmptyEntries);
    var lib = RefractorForge.Render.MeshLibrary.Open(archives);
    if (!lib.TryGetAssembledMesh(args[2], out var m)) { Console.WriteLine($"{args[2]}: no assembly"); return 1; }
    var idx = new System.Collections.Generic.List<int>();
    foreach (var p in m.Parts) idx.AddRange(p.Indices);
    System.Numerics.Vector3 mn = new(float.MaxValue), mx = new(float.MinValue);
    foreach (var v in m.Positions) { mn = System.Numerics.Vector3.Min(mn, v); mx = System.Numerics.Vector3.Max(mx, v); }
    var center = (mn + mx) * 0.5f; float radius = MathF.Max((mx - mn).Length() * 0.5f, 0.5f);
    var img = new RefractorForge.Render.ImageBuffer(900, 700);
    img.ClearGradient(new System.Numerics.Vector3(0.55f, 0.62f, 0.72f), new System.Numerics.Vector3(0.82f, 0.86f, 0.90f));
    var off = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(0.85f, 0.55f, 0.95f)) * radius * 3f;
    var cam = new RefractorForge.Render.Camera { Position = center + off, Aspect = 900f / 700f, Far = radius * 30f };
    cam.LookAt(center);
    RefractorForge.Render.SoftwareRenderer.DrawMeshSmooth(img, cam, System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(0.5f, 0.85f, 0.4f)),
        m.Positions, idx.ToArray(), new System.Numerics.Vector3(0.72f, 0.70f, 0.62f), 0);
    img.SaveBmp(args[3]);
    Console.WriteLine($"rendered {args[2]} ({m.Triangles} tris, bbox {mx.X - mn.X:0.0}x{mx.Y - mn.Y:0.0}x{mx.Z - mn.Z:0.0}) -> {args[3]}");
    return 0;
}

if (arg == "vehscan" && args.Length >= 2)
{
    // vehscan <a.rfa,...> — enumerate every vehicle folder in the archives and report which assemble (part count)
    // and which FAIL (0 parts), so we can see what's still missing.
    var archives = args[1].Split(',', StringSplitOptions.RemoveEmptyEntries);
    var lib = RefractorForge.Render.MeshLibrary.Open(archives);
    var names = new System.Collections.Generic.SortedSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var ap in archives)
    {
        if (!System.IO.File.Exists(ap) || System.IO.Path.GetFileName(ap).StartsWith("~")) continue;
        RefractorForge.Formats.Rfa.RfaArchive a; try { a = RefractorForge.Formats.Rfa.RfaArchive.Open(ap); } catch { continue; }
        foreach (var e in a.Entries)
        {
            var n = e.Name.Replace('\\', '/');
            if (!n.EndsWith("/Objects.con", StringComparison.OrdinalIgnoreCase)) continue;
            if (!n.ToLowerInvariant().Contains("vehicles")) continue;
            var segs = n.Split('/');
            var folder = segs[segs.Length - 2];
            if (folder.Equals("AI", StringComparison.OrdinalIgnoreCase) || folder.Equals("Sounds", StringComparison.OrdinalIgnoreCase)) continue;
            names.Add(folder);
        }
    }
    int ok = 0, fail = 0; var rows = new System.Collections.Generic.List<(string Name, int Parts)>();
    foreach (var v in names) { int p = lib.TryAssembleVehicle(v, out var parts) ? parts.Length : 0; rows.Add((v, p)); if (p > 0) ok++; else fail++; }
    Console.WriteLine($"{names.Count} vehicle folders: {ok} assemble, {fail} produce NO parts");
    Console.WriteLine("--- FAIL (0 parts) ---");
    foreach (var r in rows.Where(r => r.Parts == 0).OrderBy(r => r.Name)) Console.WriteLine("  " + r.Name);
    return 0;
}

if (arg == "vehdump" && args.Length >= 3)
{
    // vehdump <a.rfa,b.rfa,...> <vehicle> — print the assembled parts' local translation + tri count, to see
    // which parts are present and where (diagnosing scattered/mispositioned vehicle assemblies).
    var archives = args[1].Split(',', StringSplitOptions.RemoveEmptyEntries);
    var lib = RefractorForge.Render.MeshLibrary.Open(archives);
    var veh = args[2];
    if (lib.TryAssembleVehicle(veh, out var parts))
    {
        Console.WriteLine($"{veh}: {parts.Length} assembled part(s)");
        int i = 0;
        foreach (var p in parts)
        {
            var t = p.Local.Translation;
            int tris = 0; foreach (var mp in p.Mesh.Parts) tris += mp.Indices.Length / 3;
            Console.WriteLine($"  [{i++,2}] pos ({t.X,7:0.00},{t.Y,7:0.00},{t.Z,7:0.00})  {tris,5} tris  {p.Mesh.Positions.Length,5} verts");
        }
    }
    else Console.WriteLine($"{veh}: no assembly (TryAssembleVehicle=false)");
    return 0;
}

if (arg == "treeresolve" && args.Length >= 2)
{
    // treeresolve <treeMesh.rfa> [more.rfa...] — index BF1942 .tm trees and resolve one end-to-end through the
    // SAME static-object path placed map trees use (TryGet -> Build -> ResolveTree -> MeshFromTreeMesh).
    int fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }
    var archives = args.Skip(1).Where(a => a.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase)).ToArray();
    var lib = RefractorForge.Render.MeshLibrary.Open(archives);
    var a0 = RefractorForge.Formats.Rfa.RfaArchive.Open(archives[0]);
    string? treeName = null;
    foreach (var e in a0.Entries)
    {
        var n = e.Name.Replace('\\', '/'); var bn = n[(n.LastIndexOf('/') + 1)..];
        if (bn.EndsWith(".tm", StringComparison.OrdinalIgnoreCase)) { treeName = bn[..^3]; break; }
    }
    Check(treeName is not null, $"found a .tm tree in {System.IO.Path.GetFileName(archives[0])}: {treeName}");
    if (treeName is not null)
    {
        bool got = lib.TryGet(treeName, out var m);
        Check(got && m is not null, $"TryGet('{treeName}') resolves the .tm to a Mesh (was previously a proxy box)");
        if (got && m is not null)
        {
            Check(m.Triangles > 0, $"tree mesh has triangles ({m.Triangles})");
            Check(m.Parts.Length > 0, $"tree mesh has {m.Parts.Length} material part(s) (trunk + leaves)");
            Check(m.Positions.Length > 0, $"tree mesh has {m.Positions.Length} vertices");
        }
        Check(lib.HasMeshEntry(treeName), "HasMeshEntry reports the tree as present (not a missing asset)");
    }
    Console.WriteLine(fails == 0 ? "TREEMESH TESTS PASSED" : $"TREEMESH TESTS FAILED ({fails})");
    return fails == 0 ? 0 : 1;
}

if (arg == "roadspline")
{
    // roadspline — self-contained: the Catmull-Rom road centerline (curve through points, arc spacing, width
    // lerp) + the ORIENTED road sweep (u across the width, v along the arc — lane markings follow the road).
    int fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }

    // 1) Spline passes through its control points, arc length is monotonic, spacing is bounded.
    var ctrl = new List<(float X, float Y, float Z, float HalfW)> { (10, 5, 10, 4), (50, 8, 40, 4), (90, 5, 10, 4) };
    var rs = RefractorForge.Render.RoadSpline.Resample(ctrl, 1f);
    Check(rs.Count > 50, $"dense resample produced {rs.Count} samples");
    foreach (var c in ctrl)
    {
        float best = float.MaxValue;
        foreach (var s in rs) { float dx = s.X - c.X, dz = s.Z - c.Z; best = MathF.Min(best, MathF.Sqrt(dx * dx + dz * dz)); }
        Check(best < 1.5f, $"curve passes through control ({c.X},{c.Z}) (nearest {best:0.00} m)");
    }
    bool arcMono = true, spacingOk = true;
    for (int i = 1; i < rs.Count; i++)
    {
        if (rs[i].ArcLen <= rs[i - 1].ArcLen - 1e-4f) arcMono = false;
        float dx = rs[i].X - rs[i - 1].X, dz = rs[i].Z - rs[i - 1].Z;
        if (MathF.Sqrt(dx * dx + dz * dz) > 3f) spacingOk = false;
    }
    Check(arcMono, "arc length is monotonic");
    Check(spacingOk, "consecutive sample spacing bounded");
    // Smoothness: the raw polyline has one sharp ~73 deg corner at the middle control; the spline must spread
    // it into many small turns (no per-step turn anywhere near the polyline's corner).
    float maxTurn = 0f;
    for (int i = 1; i + 1 < rs.Count; i++)
    {
        float ax = rs[i].X - rs[i - 1].X, az = rs[i].Z - rs[i - 1].Z;
        float bx = rs[i + 1].X - rs[i].X, bz = rs[i + 1].Z - rs[i].Z;
        float la = MathF.Sqrt(ax * ax + az * az), lb = MathF.Sqrt(bx * bx + bz * bz);
        if (la < 1e-4f || lb < 1e-4f) continue;
        float cos = Math.Clamp((ax * bx + az * bz) / (la * lb), -1f, 1f);
        maxTurn = MathF.Max(maxTurn, MathF.Acos(cos));
    }
    Check(maxTurn < 0.35f, $"curve is smooth (max per-step turn {maxTurn * 180f / MathF.PI:0.0} deg, polyline corner would be ~73)");

    // 2) Two points = straight line; final arc ~= distance; width lerps between per-point widths.
    var straight = RefractorForge.Render.RoadSpline.Resample(
        new List<(float, float, float, float)> { (0, 0, 0, 2f), (100, 0, 0, 6f) }, 1f);
    bool collinear = straight.All(s => MathF.Abs(s.Z) < 0.05f);
    Check(collinear, "two-point road is a straight segment");
    Check(MathF.Abs(straight[^1].ArcLen - 100f) < 1f, $"arc length ~= 100 m (got {straight[^1].ArcLen:0.0})");
    var mid = straight.OrderBy(s => MathF.Abs(s.X - 50f)).First();
    Check(MathF.Abs(mid.HalfWidth - 4f) < 1f, $"width lerps along the road (mid halfW {mid.HalfWidth:0.0} ~ 4)");

    // 3) SweepOriented: u runs ACROSS the road, v runs ALONG it. Atlas 128 over a 128 m world; road along +X
    //    at z=64, halfW 8. (a) across-texture: left half red / right half green -> north of centerline green,
    //    south red. (b) along-texture: varies only along v -> start vs end of the road differ.
    static RefractorForge.Render.Texture2D TexUV(bool acrossU)
    {
        var px = new byte[8 * 8 * 4];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                int o = (y * 8 + x) * 4;
                bool hi = acrossU ? x >= 4 : y >= 4;
                px[o] = (byte)(hi ? 0 : 255); px[o + 1] = (byte)(hi ? 255 : 0); px[o + 2] = 0; px[o + 3] = 255;
            }
        return new RefractorForge.Render.Texture2D(8, 8, px);
    }
    var samplesX = new List<(float X, float Z, float HalfW, float ArcLen)>();
    for (float x = 4; x <= 124; x += 2) samplesX.Add((x, 64f, 8f, x - 4));

    // NOTE: AtlasPaintStroke lives in the Viewer (it's the editor's stroke engine); replicate its oriented
    // sampling here EXACTLY (nearest segment -> signed lateral u, arc v) to gate the math headlessly.
    static (byte r, byte g) OrientedSampleAt(RefractorForge.Render.Texture2D tex, List<(float X, float Z, float HalfW, float ArcLen)> pts, float wx, float wz, float tileV)
    {
        float bd = float.MaxValue, bside = 0, bhw = 1, barc = 0;
        for (int s = 0; s + 1 < pts.Count; s++)
        {
            float ax = pts[s].X, az = pts[s].Z, ex = pts[s + 1].X - ax, ez = pts[s + 1].Z - az;
            float el2 = ex * ex + ez * ez; if (el2 < 1e-6f) continue;
            float t = Math.Clamp(((wx - ax) * ex + (wz - az) * ez) / el2, 0f, 1f);
            float dx = wx - (ax + ex * t), dz = wz - (az + ez * t);
            float d = MathF.Sqrt(dx * dx + dz * dz);
            if (d < bd) { bd = d; bside = MathF.Sign(ex * dz - ez * dx); bhw = pts[s].HalfW + (pts[s + 1].HalfW - pts[s].HalfW) * t; barc = pts[s].ArcLen + (pts[s + 1].ArcLen - pts[s].ArcLen) * t; }
        }
        float across = Math.Clamp(0.5f + bside * bd / MathF.Max(bhw * 2f, 0.2f), 0.003f, 0.997f);
        var c = tex.Sample(barc / tileV, across);   // alongU=true: along the road -> U (1st arg), across -> V (2nd)
        return ((byte)(c.X * 255), (byte)(c.Y * 255));
    }
    // ACROSS the road maps to the texture's V (2nd arg): a texture split top/bottom (acrossU:false) -> north vs
    // south of the centerline sample opposite halves.
    var texAcross = TexUV(acrossU: false);
    var north = OrientedSampleAt(texAcross, samplesX, 64f, 64f + 4f, 128f);
    var south = OrientedSampleAt(texAcross, samplesX, 64f, 64f - 4f, 128f);
    Check(north.g > 180 && north.r < 80, $"north of centerline samples one width half (got R{north.r} G{north.g})");
    Check(south.r > 180 && south.g < 80, $"south of centerline samples the other width half (got R{south.r} G{south.g})");
    // ALONG the road maps to the texture's U (1st arg): a texture split left/right (acrossU:true) -> start vs end
    // of the road sample opposite halves. Probe mid-band (U 0.25 -> red, U 0.75 -> green; tile = 160 m of road).
    var texAlong = TexUV(acrossU: true);
    var nearStart = OrientedSampleAt(texAlong, samplesX, 44f, 64f, 160f);
    var nearEnd = OrientedSampleAt(texAlong, samplesX, 124f, 64f, 160f);
    Check(nearStart.r > 180 && nearEnd.g > 180 && nearEnd.r < 80, $"texture varies ALONG the road (40 m R{nearStart.r}, 120 m G{nearEnd.g})");

    Console.WriteLine(fails == 0 ? "ROAD SPLINE TESTS PASSED" : $"ROAD SPLINE TESTS FAILED ({fails})");
    return fails == 0 ? 0 : 1;
}

if (arg == "texturelayer")
{
    // texturelayer — self-contained: bake an Editor42-style height/slope + noise layer into an atlas and assert
    // the A/B blend, the whole-terrain fill, noise irregularity, and determinism. No GL, no level needed.
    int fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }

    int side = 64, asize = 64;
    var cfg = new RefractorForge.Formats.Terrain.TerrainConfig { MaterialSize = side, WorldSize = 256, YScale = 1f };
    // Height ramp: 0 m at row z=0 -> ~30 m at z=side-1 (uniform across x).
    var hm = new RefractorForge.Formats.Terrain.Heightmap(side, side);
    for (int z = 0; z < side; z++)
    {
        ushort raw = (ushort)Math.Clamp((int)(z / (float)(side - 1) * 30f * 256f / cfg.YScale), 0, 65535);
        for (int x = 0; x < side; x++) hm[x, z] = raw;
    }
    static RefractorForge.Render.Texture2D Solid(byte r, byte g, byte b)
    {
        var px = new byte[4 * 4 * 4];
        for (int i = 0; i < 16; i++) { px[i * 4] = r; px[i * 4 + 1] = g; px[i * 4 + 2] = b; px[i * 4 + 3] = 255; }
        return new RefractorForge.Render.Texture2D(4, 4, px);
    }
    var red = Solid(255, 0, 0);     // texture A
    var green = Solid(0, 255, 0);   // texture B

    var atlas = new RefractorForge.Render.Texture2D(asize, asize, new byte[asize * asize * 4]);
    var spec = new RefractorForge.Render.TextureLayerSpec
    {
        Selector = RefractorForge.Render.LayerSelector.Height,
        ThresholdLow = 10f, ThresholdHigh = 20f, NoiseOn = false, TileMetersA = 8f, TileMetersB = 8f,
    };
    RefractorForge.Render.TerrainTextureLayer.BakeLayerToAtlas(atlas, hm, cfg, red, green, spec);

    int lo0 = (asize / 2 + 0 * asize) * 4;                          // row 0 (low terrain)
    int hi0 = (asize / 2 + (asize - 1) * asize) * 4;               // top row (high terrain)
    Check(atlas.Rgba[lo0] > 200 && atlas.Rgba[lo0 + 1] < 60, $"low terrain = texture A (red) got ({atlas.Rgba[lo0]},{atlas.Rgba[lo0 + 1]})");
    Check(atlas.Rgba[hi0 + 1] > 200 && atlas.Rgba[hi0] < 60, $"high terrain = texture B (green) got ({atlas.Rgba[hi0]},{atlas.Rgba[hi0 + 1]})");
    bool blend = false;
    for (int y = 0; y < asize; y++) { int o = (asize / 2 + y * asize) * 4; if (atlas.Rgba[o] > 40 && atlas.Rgba[o + 1] > 40) { blend = true; break; } }
    Check(blend, "a smooth A->B blend band exists between the thresholds");

    // FillAtlas: whole atlas becomes texture A.
    RefractorForge.Render.TerrainTextureLayer.FillAtlas(atlas, red, cfg.WorldSize, 8f);
    Check(atlas.Rgba[0] > 200 && atlas.Rgba[1] < 60 && atlas.Rgba[atlas.Rgba.Length - 4] > 200, "FillAtlas fills the whole atlas with one texture");

    // Noise irregularity: a boundary row should contain BOTH A-ish and B-ish texels (a clean contour would be uniform).
    var atlasN = new RefractorForge.Render.Texture2D(asize, asize, new byte[asize * asize * 4]);
    spec.NoiseOn = true; spec.ThresholdWidth = 0.8f; spec.Seed = 2300; spec.FirstOctave = 2; spec.OctaveCount = 6;
    RefractorForge.Render.TerrainTextureLayer.BakeLayerToAtlas(atlasN, hm, cfg, red, green, spec);
    bool jagged = false;
    for (int y = 0; y < asize && !jagged; y++)
    {
        int redC = 0, grnC = 0;
        for (int x = 0; x < asize; x++) { int o = (x + y * asize) * 4; if (atlasN.Rgba[o] > atlasN.Rgba[o + 1]) redC++; else grnC++; }
        if (redC > asize / 8 && grnC > asize / 8) jagged = true;
    }
    Check(jagged, "noise gradation makes the A/B boundary irregular (a mixed row)");

    // Determinism: same seed -> identical bake.
    var atlasN2 = new RefractorForge.Render.Texture2D(asize, asize, new byte[asize * asize * 4]);
    RefractorForge.Render.TerrainTextureLayer.BakeLayerToAtlas(atlasN2, hm, cfg, red, green, spec);
    Check(atlasN.Rgba.AsSpan().SequenceEqual(atlasN2.Rgba), "same seed -> identical (deterministic) bake");

    // Slope selector smoke: flat terrain -> slope 0 -> all texture A.
    var flat = new RefractorForge.Formats.Terrain.Heightmap(side, side);
    for (int i = 0; i < flat.Samples.Length; i++) flat.Samples[i] = 1000;
    var atlasS = new RefractorForge.Render.Texture2D(asize, asize, new byte[asize * asize * 4]);
    var sslope = new RefractorForge.Render.TextureLayerSpec { Selector = RefractorForge.Render.LayerSelector.Slope, ThresholdLow = 10f, ThresholdHigh = 40f, NoiseOn = false };
    RefractorForge.Render.TerrainTextureLayer.BakeLayerToAtlas(atlasS, flat, cfg, red, green, sslope);
    int sc = (asize / 2 + (asize / 2) * asize) * 4;
    Check(atlasS.Rgba[sc] > 200 && atlasS.Rgba[sc + 1] < 60, "slope selector on flat terrain = texture A");

    // ProofPreview: bottom -> A, top -> B (matches the bake's value ramp).
    var proof = RefractorForge.Render.TerrainTextureLayer.ProofPreview(64, red, green, new RefractorForge.Render.TextureLayerSpec { Selector = RefractorForge.Render.LayerSelector.Height, ThresholdLow = 10f, ThresholdHigh = 20f, NoiseOn = false });
    int pbot = (32 + 63 * 64) * 4, ptop = (32 + 0 * 64) * 4;
    Check(proof.Rgba[pbot] > 200 && proof.Rgba[ptop + 1] > 200, "Proof preview shows A at the bottom and B at the top");

    Console.WriteLine(fails == 0 ? "TEXTURE LAYER TESTS PASSED" : $"TEXTURE LAYER TESTS FAILED ({fails})");
    return fails == 0 ? 0 : 1;
}

if (arg == "weathergen")
{
    // weathergen — self-contained: generate rain/snow/dust Effects.con + particle texture and assert structure.
    int fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }
    foreach (var type in new[] { RefractorForge.Formats.Con.WeatherType.Snow, RefractorForge.Formats.Con.WeatherType.Rain, RefractorForge.Formats.Con.WeatherType.Dust, RefractorForge.Formats.Con.WeatherType.DustStorm })
    {
        var inst = RefractorForge.Formats.Con.WeatherEffect.InstancePosition(2048f, 30f);
        string con = RefractorForge.Formats.Con.WeatherEffect.BuildEffectsCon(type, 5000, 3f, 2048f, inst);
        string bundle = RefractorForge.Formats.Con.WeatherEffect.BundleName(type);
        Check(con.Contains("ObjectTemplate.create SpriteParticle"), $"{type}: has SpriteParticle");
        Check(con.Contains("ObjectTemplate.create Emitter"), $"{type}: has Emitter");
        Check(con.Contains("ObjectTemplate.create EffectBundle " + bundle), $"{type}: has EffectBundle");
        Check(con.Contains("setLooping 1"), $"{type}: emitter loops");
        Check(con.Contains("Object.create " + bundle), $"{type}: instance placed");
        Check(con.Contains(RefractorForge.Formats.Con.WeatherEffect.TextureName(type)), $"{type}: references its texture");
        // Intensity must be clamped under the sprite ceiling (we asked for 5000).
        int safe = RefractorForge.Formats.Con.WeatherEffect.SafeIntensity(type, 5000);
        Check(safe < 5000 && safe > 0, $"{type}: intensity clamped {5000}->{safe} (under 5000-sprite limit)");
        // Texture: 32x32 RGBA, white, with alpha variation (a real falloff, not flat).
        var rgba = RefractorForge.Formats.Con.WeatherEffect.BuildParticleRgba(type, 32);
        Check(rgba.Length == 32 * 32 * 4, $"{type}: texture is 32x32 RGBA");
        bool hasClear = false, hasOpaque = false;
        for (int i = 3; i < rgba.Length; i += 4) { if (rgba[i] < 20) hasClear = true; if (rgba[i] > 200) hasOpaque = true; }
        Check(hasClear && hasOpaque, $"{type}: texture has an alpha falloff");
    }
    // Placeable model: BuildTemplatesCon (multi-type, NO instances) + TypeOfBundle round-trip.
    var multi = RefractorForge.Formats.Con.WeatherEffect.BuildTemplatesCon(
        new[] { RefractorForge.Formats.Con.WeatherType.Snow, RefractorForge.Formats.Con.WeatherType.Rain }, 200, 0f, 2048f);
    Check(multi.Contains("create EffectBundle e_RF_WeatherSnow") && multi.Contains("create EffectBundle e_RF_WeatherRain"), "templates-con has both bundle types");
    Check(!multi.Contains("Object.create"), "templates-con has NO instances (placed objects are the instances)");
    Check(RefractorForge.Formats.Con.WeatherEffect.TypeOfBundle("e_RF_WeatherDust") == RefractorForge.Formats.Con.WeatherType.Dust, "TypeOfBundle maps a placed bundle name back to its type");
    Check(RefractorForge.Formats.Con.WeatherEffect.TypeOfBundle("o_rock_m1") is null, "TypeOfBundle ignores non-weather templates");

    // Optional: weathergen <baseLevel.rfa> — round-trip the .rfa new-entry + Init-edit plumbing against a real archive.
    if (args.Length >= 2 && args[1].EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
    {
        var baseRfa = args[1];
        var conBytes = System.Text.Encoding.Latin1.GetBytes(
            RefractorForge.Formats.Con.WeatherEffect.BuildEffectsCon(RefractorForge.Formats.Con.WeatherType.Snow, 200, 2f, 2048f, new RefractorForge.Formats.Geometry.Vec3(1024, 110, 1024)));
        // Init edit: read the level-root Init.con, append the run-include.
        var a0 = RefractorForge.Formats.Rfa.RfaArchive.Open(baseRfa);
        var initE = a0.Entries.Where(x => x.Name.EndsWith("Init.con", StringComparison.OrdinalIgnoreCase) && !x.Name.Replace('\\','/').ToLowerInvariant().Contains("/menu/")).OrderBy(x => x.Name.Length).FirstOrDefault();
        var extras = new System.Collections.Generic.List<(string, byte[])>();
        if (initE is not null)
            extras.Add(("Init.con", System.Text.Encoding.Latin1.GetBytes(System.Text.Encoding.Latin1.GetString(a0.Read(initE)).TrimEnd() + "\r\n" + RefractorForge.Formats.Con.WeatherEffect.RunInclude() + "\r\n")));
        var newEntries = new System.Collections.Generic.List<(string, byte[])> { ("Effects/" + RefractorForge.Formats.Con.WeatherEffect.ConFileName, conBytes) };
        var outPatch = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rf_weather_patch_" + System.Guid.NewGuid().ToString("N")[..6] + ".rfa");
        try
        {
            RefractorForge.Formats.LevelSaver.WritePatchRfa(baseRfa, outPatch, null, null, null, null, extraFiles: extras, newEntries: newEntries);
            var pa = RefractorForge.Formats.Rfa.RfaArchive.Open(outPatch);
            var con = pa.Entries.FirstOrDefault(x => x.Name.EndsWith("Effects/" + RefractorForge.Formats.Con.WeatherEffect.ConFileName, StringComparison.OrdinalIgnoreCase));
            Check(con is not null, "patch contains new Effects/RF_Weather.con entry (under level prefix)");
            if (con is not null) Check(con.Name.Contains("/Effects/"), $"new entry path: {con.Name}");
            var ie = pa.Entries.FirstOrDefault(x => x.Name.EndsWith("Init.con", StringComparison.OrdinalIgnoreCase) && !x.Name.Replace('\\','/').ToLowerInvariant().Contains("/menu/"));
            Check(ie is not null && System.Text.Encoding.Latin1.GetString(pa.Read(ie)).Contains(RefractorForge.Formats.Con.WeatherEffect.ConFileName), "patch Init.con carries the run-include");
        }
        finally { try { System.IO.File.Delete(outPatch); } catch { } }
    }
    Console.WriteLine(fails == 0 ? "WEATHER GEN TESTS PASSED" : $"WEATHER GEN TESTS FAILED ({fails})");
    return fails == 0 ? 0 : 1;
}

if (arg == "vehcollision" && args.Length >= 2)
{
    // vehcollision <stdMesh.rfa,objects.rfa> [vehicle1 ...]
    // Verify the new per-part vehicle COLLISION assembly: each vehicle should yield collision parts that line up
    // with its render parts. Gate: at least one of the probe vehicles must produce collision geometry.
    var archives = args[1].Split(',', StringSplitOptions.RemoveEmptyEntries);
    var lib = RefractorForge.Render.MeshLibrary.Open(archives);
    var probes = args.Length > 2 ? args.Skip(2).ToArray()
               : new[] { "Sheridan", "M113", "ZSU", "Stationary_M60", "MortarUS" };
    int fails = 0, withCollision = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }
    foreach (var veh in probes)
    {
        bool rOk = lib.TryAssembleVehicle(veh, out var rparts);
        bool cOk = lib.TryAssembleVehicleCollision(veh, out var cparts);
        int ctris = 0;
        if (cOk) foreach (var (col, _) in cparts) ctris += col.Indices.Length / 3;
        Console.WriteLine($"  {veh,-16} render={(rOk ? rparts.Length + "p" : "-")}  collision={(cOk ? cparts.Length + "p/" + ctris + "tri" : "none")}");
        if (cOk) { withCollision++; Check(ctris > 0, $"{veh} collision has triangles"); }
        // A vehicle that renders should not THROW on the collision path (it may legitimately have no collision).
        Check(true, $"{veh} collision assembly did not throw");
    }
    Check(withCollision > 0, "at least one probe vehicle produced collision geometry");
    Console.WriteLine(fails == 0 ? "VEHICLE COLLISION TESTS PASSED" : $"VEHICLE COLLISION TESTS FAILED ({fails})");
    return fails == 0 ? 0 : 1;
}

if (arg == "condump" && args.Length >= 3)
{
    // condump <objects.rfa> <vehicleName> [full]
    // List the .con files inside the archive whose path contains the vehicle name; if "full", print the
    // first matching .con's text so we can read the part-hierarchy (addTemplate/setPosition/setRotation).
    var arc = RefractorForge.Formats.Rfa.RfaArchive.Open(args[1]);
    string veh = args[2].ToLowerInvariant();
    bool full = args.Length > 3 && args[3] == "full";
    var cons = arc.Entries.Where(e => e.Name.ToLowerInvariant() is var n && n.EndsWith(".con") && n.Contains(veh)).ToList();
    Console.WriteLine($"archive entries={arc.Entries.Count}; .con files containing '{veh}': {cons.Count}");
    foreach (var e in cons) Console.WriteLine($"  {e.Name} ({e.UncompressedSize}B)");
    if (full && cons.Count > 0)
    {
        // Prefer the shortest path (usually the top-level vehicle def, not a sub-part).
        var pick = cons.OrderBy(e => e.Name.Length).First();
        Console.WriteLine($"\n===== {pick.Name} =====");
        Console.WriteLine(System.Text.Encoding.Latin1.GetString(arc.Read(pick)));
    }
    return 0;
}

if (arg == "meshnames")
{
    // meshnames <stdMesh.rfa> <objects.rfa> [name1 name2 ...]
    // Diagnostic: open the mesh archives, report .sm count, and test whether the given template
    // names resolve to a mesh (used to find out why vehicle spawns don't render as meshes).
    var archives = args.Skip(1).Where(a => a.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase)).ToArray();
    var probes = args.Skip(1).Where(a => !a.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase)).ToArray();
    var lib = RefractorForge.Render.MeshLibrary.Open(archives);
    Console.WriteLine($"opened {archives.Length} archive(s); .sm meshes = {lib.MeshCount}");
    foreach (var n in probes)
    {
        bool ok = lib.TryGet(n, out var m);
        Console.WriteLine($"  {n,-16} resolved={ok} tris={(ok ? m.Triangles : 0)}");
    }
    // Dump .sm basenames that look vehicle-ish for reference.
    foreach (var key in new[] { "sheridan", "t54", "t-54", "chinook", "phantom", "f4", "zsu", "pbr", "huey", "uh1", "mig", "bmp", "sampan", "m113", "jeep" })
        Console.WriteLine($"  contains '{key}': {string.Join(", ", lib.MeshBaseNames.Where(x => x.Contains(key, StringComparison.OrdinalIgnoreCase)).Take(8))}");

    // Centroid/bounds test: are a vehicle's parts authored IN-PLACE (centroids spread out) or
    // PART-LOCAL (all centered near origin)? Pick the first probe name as the vehicle prefix.
    string veh = probes.FirstOrDefault() ?? "sheridan";
    Console.WriteLine($"--- part bounds for vehicle '{veh}' (template '{veh}'-prefixed Ve_ parts) ---");
    foreach (var nm in lib.MeshBaseNames.Where(x => x.StartsWith("ve_" + veh, StringComparison.OrdinalIgnoreCase)).Take(10))
    {
        var t = System.IO.Path.GetFileNameWithoutExtension(nm);
        if (lib.TryGet(t, out var m))
        {
            float cx = 0, cy = 0, cz = 0; var P = m.Positions;
            float minX=float.MaxValue,minY=float.MaxValue,minZ=float.MaxValue,maxX=float.MinValue,maxY=float.MinValue,maxZ=float.MinValue;
            foreach (var p in P){ cx+=p.X; cy+=p.Y; cz+=p.Z; minX=Math.Min(minX,p.X); maxX=Math.Max(maxX,p.X); minY=Math.Min(minY,p.Y); maxY=Math.Max(maxY,p.Y); minZ=Math.Min(minZ,p.Z); maxZ=Math.Max(maxZ,p.Z); }
            int n = P.Length;
            Console.WriteLine($"  {t,-26} centroid=({cx/n:0.0},{cy/n:0.0},{cz/n:0.0}) bbox=({minX:0.0}..{maxX:0.0},{minY:0.0}..{maxY:0.0},{minZ:0.0}..{maxZ:0.0})");
        }
    }
    return 0;
}

if (arg == "meshtest" && args.Length >= 2)
{
    // meshtest <heightmap.raw> [materialSize] [worldSize] [yScale] [stride]
    int ms = args.Length > 2 ? int.Parse(args[2]) : 2048;
    float ws = args.Length > 3 ? float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 32768;
    float ys = args.Length > 4 ? float.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : 10;
    int stride = args.Length > 5 ? int.Parse(args[5]) : 1;
    var bytes = File.ReadAllBytes(args[1]);
    var cfg = new TerrainConfig { MaterialSize = ms, WorldSize = (int)ws, YScale = ys };
    var hm = Heightmap.LoadForMaterialSize(bytes, ms);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var mesh = RefractorForge.Render.TerrainMesh.FromHeightmap(hm, cfg, stride);
    sw.Stop();
    long before = System.GC.GetTotalMemory(false);
    Console.WriteLine($"heightmap {hm.Width}x{hm.Height}  stride={stride}  grid={mesh.GridW}x{mesh.GridH}");
    Console.WriteLine($"vertices={mesh.Positions.Length:N0}  indices={mesh.Indices.Length:N0}  built in {sw.ElapsedMilliseconds}ms  heapMB={before/1024/1024}");
    // sanity: heights finite + within plausible range
    float mn = float.MaxValue, mx = float.MinValue;
    foreach (var p in mesh.Positions) { if (p.Y < mn) mn = p.Y; if (p.Y > mx) mx = p.Y; }
    Console.WriteLine($"height range: {mn:0.0}m .. {mx:0.0}m  worldExtent={mesh.GridW * cfg.HorizontalSpacing * stride:0}m");
    Console.WriteLine("MESH BUILD OK");
    return 0;
}

if (arg == "packfolder" && args.Length >= 3)
{
    string dir = args[1], outp = args[2];
    int n = LevelSaver.PackFolder(dir, outp, args.Length >= 4 ? args[3] : null);
    var a = RefractorForge.Formats.Rfa.RfaArchive.Open(outp);
    Console.WriteLine($"packed {n} files -> {outp} ({new FileInfo(outp).Length / 1024}KiB)");
    bool ok = a.Entries.Count == n; int checand = 0;
    foreach (var e in a.Entries)
    {
        // entry name is the folder-relative path; compare bytes to disk
        var disk = Path.Combine(dir, e.Name.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(disk) && (++checand) <= 6)
        {
            var dataDisk = File.ReadAllBytes(disk);
            bool same = dataDisk.AsSpan().SequenceEqual(a.Read(e));
            Console.WriteLine($"  [{(same ? "PASS" : "FAIL")}] {e.Name} ({dataDisk.Length}B)"); ok &= same;
        }
    }
    Console.WriteLine(ok ? "PACK FOLDER PASSED" : "PACK FOLDER FAILED");
    return ok ? 0 : 1;
}

if (arg == "meshlm" && args.Length >= 3)
{
    // meshlm <a.rfa,...> <template> [more] — report whether a resolved mesh carries a 2nd-UV (object-lightmap) channel.
    var lib = RefractorForge.Render.MeshLibrary.Open(args[1].Split(',', StringSplitOptions.RemoveEmptyEntries));
    foreach (var name in args.Skip(2))
    {
        if (!lib.TryGet(name, out var m) || m is null) { Console.WriteLine($"{name,-28} NOT FOUND"); continue; }
        var lm = m.LightmapUvs;
        string sample = lm is { Length: > 0 } ? $"e.g. ({lm[0].X:0.000},{lm[0].Y:0.000}) ({lm[lm.Length / 2].X:0.000},{lm[lm.Length / 2].Y:0.000})" : "";
        Console.WriteLine($"{name,-28} {m.Triangles} tris, lightmapUv={(lm is not null ? "YES " + lm.Length + " " + sample : "no")}");
    }
    return 0;
}

if (arg == "olmbundle" && args.Length >= 4)
{
    // olmbundle <level.rfa[,patch...]> <a1.rfa,...> <template> — verify object-lightmap matching for a (possibly
    // multi-part Bundle) static object: primary geometry name, assembled-mesh lightmap UVs, and per-instance match
    // by template-name vs geometry-name.
    var lvlRfas = args[1].Split(',', StringSplitOptions.RemoveEmptyEntries).Where(File.Exists).ToArray();
    var lib = RefractorForge.Render.MeshLibrary.Open(args[2].Split(',', StringSplitOptions.RemoveEmptyEntries).Where(File.Exists).Concat(lvlRfas).ToArray());
    var olm = RefractorForge.Render.ObjectLightmaps.FromRfaPaths(lvlRfas);
    var tpl = args[3];
    Console.WriteLine($"olm entries: {olm.Count}");
    var geom = lib.PrimaryGeometryName(tpl);
    Console.WriteLine($"PrimaryGeometryName({tpl}) = {geom}");
    Console.WriteLine($"render mesh: {(lib.TryGetRenderMesh(tpl, out var rm) && rm is not null ? $"{rm.Triangles} tris, LightmapUvs={(rm.LightmapUvs is null ? "NULL" : rm.LightmapUvs.Length.ToString())}" : "NONE")}");
    var lvl = RefractorForge.Render.LevelArchive.FromRfa(lvlRfas);
    foreach (var o in lvl.StaticObjects.Objects.Where(o => o.Template.Equals(tpl, StringComparison.OrdinalIgnoreCase)))
    {
        var byTpl = olm.Match(o.Template, o.Position.X, o.Position.Y, o.Position.Z) is not null;
        var byGeom = olm.Match(geom, o.Position.X, o.Position.Y, o.Position.Z) is not null;
        Console.WriteLine($"  @{o.Position.X:0.#}/{o.Position.Y:0.#}/{o.Position.Z:0.#}: byTemplate={byTpl} byGeometry={byGeom}");
    }
    return 0;
}

if (arg == "whicharc" && args.Length >= 3)
{
    // whicharc <level.rfa[,patch...]> <a1.rfa,a2.rfa,...> — for every UNIQUE placed template in the level, report
    // which of the given archives FIRST resolves its render mesh (each archive opened on its own + the level).
    // Pinpoints "this building's mesh lives in an archive the editor didn't load".
    var lvlRfas = args[1].Split(',', StringSplitOptions.RemoveEmptyEntries).Where(File.Exists).ToArray();
    var archives = args[2].Split(',', StringSplitOptions.RemoveEmptyEntries).Where(File.Exists).ToArray();
    var lvl = RefractorForge.Render.LevelArchive.FromRfa(lvlRfas);
    var sof = lvl.StaticObjects;
    if (sof is null) { Console.WriteLine("no static objects"); return 1; }
    var libs = archives.Select(a => (Name: System.IO.Path.GetFileName(a),
                                     Lib: RefractorForge.Render.MeshLibrary.Open(new[] { a }.Concat(lvlRfas).ToArray()))).ToArray();
    var levelOnly = RefractorForge.Render.MeshLibrary.Open(lvlRfas);
    var groups = sof.Objects.GroupBy(o => o.Template, StringComparer.OrdinalIgnoreCase)
                            .Select(g => (T: g.Key, C: g.Count())).OrderBy(g => g.T).ToList();
    var byArc = new System.Collections.Generic.SortedDictionary<string, System.Collections.Generic.List<string>>();
    foreach (var (t, c) in groups)
    {
        string where;
        if (levelOnly.TryGetRenderMesh(t, out _)) where = "(level rfa)";
        else where = libs.FirstOrDefault(l => l.Lib.TryGetRenderMesh(t, out _)).Name ?? "*** NONE ***";
        if (!byArc.TryGetValue(where, out var lst)) byArc[where] = lst = new();
        lst.Add($"{t} x{c}");
    }
    foreach (var kv in byArc)
    {
        Console.WriteLine($"=== {kv.Key} ({kv.Value.Count} templates) ===");
        foreach (var s in kv.Value) Console.WriteLine("  " + s);
    }
    return 0;
}

if (arg == "smrender" && args.Length >= 4)
{
    // smrender <a.rfa,...> <template> <out.bmp> — resolve a STATIC object's render mesh (same path the editor
    // uses) and rasterize it, so we can SEE whether a "building that isn't loading" produces real geometry.
    var lib = RefractorForge.Render.MeshLibrary.Open(args[1].Split(',', StringSplitOptions.RemoveEmptyEntries));
    if (!lib.TryGetRenderMesh(args[2], out var m) || m is null) { Console.WriteLine($"{args[2]}: NO render mesh"); return 1; }
    var idx = new System.Collections.Generic.List<int>();
    foreach (var pt in m.Parts) idx.AddRange(pt.Indices);
    System.Numerics.Vector3 mn = new(float.MaxValue), mx = new(float.MinValue);
    foreach (var v in m.Positions) { mn = System.Numerics.Vector3.Min(mn, v); mx = System.Numerics.Vector3.Max(mx, v); }
    var center = (mn + mx) * 0.5f; float radius = MathF.Max((mx - mn).Length() * 0.5f, 0.5f);
    var img = new RefractorForge.Render.ImageBuffer(900, 700);
    img.ClearGradient(new System.Numerics.Vector3(0.55f, 0.62f, 0.72f), new System.Numerics.Vector3(0.82f, 0.86f, 0.90f));
    var off = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(0.85f, 0.55f, 0.95f)) * radius * 3f;
    var cam = new RefractorForge.Render.Camera { Position = center + off, Aspect = 900f / 700f, Far = radius * 30f };
    cam.LookAt(center);
    RefractorForge.Render.SoftwareRenderer.DrawMeshSmooth(img, cam, System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(0.5f, 0.85f, 0.4f)),
        m.Positions, idx.ToArray(), new System.Numerics.Vector3(0.72f, 0.70f, 0.62f), 0);
    img.SaveBmp(args[3]);
    Console.WriteLine($"{args[2]}: {m.Triangles} tris, {m.Parts.Length} part(s), bbox {mx.X - mn.X:0.0} x {mx.Y - mn.Y:0.0} x {mx.Z - mn.Z:0.0} -> {args[3]}");
    return 0;
}

if (arg == "rfaprobe" && args.Length >= 2)
{
    // rfaprobe <archive.rfa> [substr] — read EVERY entry (optionally filtered) and report which fail to
    // decompress, with per-block sizes + the LZO failure detail. Pinpoints the "back-reference" levels.
    var a = RefractorForge.Formats.Rfa.RfaArchive.Open(args[1]);
    string? filt = args.Length > 2 ? args[2].ToLowerInvariant() : null;
    int ok = 0, fail = 0;
    foreach (var e in a.Entries)
    {
        if (filt is not null && !e.Name.ToLowerInvariant().Contains(filt)) continue;
        try { _ = a.Read(e); ok++; }
        catch (Exception ex)
        {
            fail++;
            var blocks = a.BlockSizes(e);
            Console.WriteLine($"FAIL {e.Name}");
            Console.WriteLine($"     unc={e.UncompressedSize} blockSize={e.BlockSize} blocks={blocks.Count}: {string.Join(", ", blocks.Take(8).Select(b => $"{b.Comp}->{b.Unc}"))}{(blocks.Count > 8 ? " ..." : "")}");
            Console.WriteLine($"     {ex.GetType().Name}: {ex.Message}");
        }
    }
    Console.WriteLine($"{ok} ok, {fail} FAILED to decompress in {System.IO.Path.GetFileName(args[1])}");
    return fail == 0 ? 0 : 1;
}

if (arg == "smwalk" && args.Length >= 3)
{
    // smwalk <archive.rfa> <meshname> — manually walk the .sm header and print each field + cursor offset,
    // so we can see where a mesh that "yields no geometry" diverges from the assumed layout.
    var a = RefractorForge.Formats.Rfa.RfaArchive.Open(args[1]);
    string want = args[2].ToLowerInvariant(); if (!want.EndsWith(".sm")) want += ".sm";
    foreach (var e in a.Entries)
    {
        var bn = e.Name.Replace('\\', '/'); bn = bn[(bn.LastIndexOf('/') + 1)..].ToLowerInvariant();
        if (bn != want) continue;
        var buf = a.Read(e);
        int p = 0;
        uint U32() { uint v = BitConverter.ToUInt32(buf, p); p += 4; return v; }
        float F32() { float v = BitConverter.ToSingle(buf, p); p += 4; return v; }
        Console.WriteLine($"{e.Name}: {buf.Length} bytes");
        uint ver = U32(); Console.WriteLine($"  @0 version={ver}");
        Console.WriteLine($"  @{p} pad={U32()}");
        var bb = new float[6]; for (int i = 0; i < 6; i++) bb[i] = F32();
        Console.WriteLine($"  @{p-24} bbox=({bb[0]:0.#},{bb[1]:0.#},{bb[2]:0.#})..({bb[3]:0.#},{bb[4]:0.#},{bb[5]:0.#})");
        if (ver == 10) { Console.WriteLine($"  @{p} qflag={buf[p]}"); p += 1; }
        int numCol = (int)U32(); Console.WriteLine($"  @{p-4} numCol={numCol}");
        for (int i = 0; i < numCol; i++) { int sz = (int)U32(); Console.WriteLine($"    col[{i}] @{p-4} size={sz} (skip to @{p+sz})"); p += sz; }
        int numLods = (int)U32(); Console.WriteLine($"  @{p-4} numLods={numLods}");
        for (int l = 0; l < numLods && l < 3; l++)
        {
            int numMat = (int)U32(); Console.WriteLine($"  LOD{l} @{p-4} numMat={numMat}");
            for (int m = 0; m < numMat && m < 4; m++)
            {
                int nlen = (int)U32(); string name = System.Text.Encoding.Latin1.GetString(buf, p, Math.Min(nlen, 64)); p += nlen;
                p += 12; uint rt = U32(), vf = U32(), vbs = U32(); int nv = (int)U32(), nfv = (int)U32(); uint ms = U32();
                Console.WriteLine($"    mat[{m}] '{name}' rt={rt} vf={vf} vbs={vbs} nv={nv} nfv={nfv} ms={ms}");
                p += nv * (int)vbs + nfv * 2;   // skip the vertex + face block to reach the next material
            }
            if (numMat == 0) Console.WriteLine("    (LOD has ZERO materials — cursor misaligned upstream)");
        }
        return 0;
    }
    Console.WriteLine("not found"); return 1;
}

if (arg == "smfmt" && args.Length >= 3)
{
    // smfmt <archive.rfa> <meshname> — parse a .sm and report each LOD0 material's vertex format / byte size / lightmap-uv.
    var a = RefractorForge.Formats.Rfa.RfaArchive.Open(args[1]);
    string want = args[2].ToLowerInvariant(); if (!want.EndsWith(".sm")) want += ".sm";
    foreach (var e in a.Entries)
    {
        var bn = e.Name.Replace('\\', '/'); bn = bn[(bn.LastIndexOf('/') + 1)..].ToLowerInvariant();
        if (bn != want) continue;
        if (!RefractorForge.Formats.Rfa.StandardMesh.TryParse(a.Read(e), out var sm) || sm is null) { Console.WriteLine("parse fail"); return 1; }
        Console.WriteLine($"{e.Name}: v{sm.Version}, {sm.Lods.Count} LOD(s)");
        foreach (var m in sm.Lods[0])
        {
            Console.WriteLine($"  mat '{m.Name}' vf={m.VertexFormat} vbs={m.VertexByteSize} nv={m.NumVertices} hasLm={m.HasLightmapUv}");
            if (m.PlanarExtra is { } pe && m.NumVertices > 0)
            {
                int fpv = pe.Length / m.NumVertices;     // floats per vertex in the extra block
                Console.WriteLine($"    planar extra: {fpv} floats/vertex. diffuse uv0 v0=({m.Uvs[0].U:0.000},{m.Uvs[0].V:0.000})");
                for (int v = 0; v < Math.Min(4, m.NumVertices); v++)
                {
                    var sb = new System.Text.StringBuilder($"    v{v} extra:");
                    for (int f = 0; f < fpv; f++) sb.Append($" [{f}]{pe[v * fpv + f]:0.000}");
                    Console.WriteLine(sb.ToString());
                }
            }
        }
        return 0;
    }
    Console.WriteLine("not found"); return 1;
}

if (arg == "bakereal" && args.Length >= 3)
{
    // bakereal <a.rfa,...> <template> [more] — bake a REAL building mesh's lightmap (flat synthetic terrain) to exercise
    // the actual UV ranges + transforms; report coverage, gray range, and TGA round-trip.
    var lib = RefractorForge.Render.MeshLibrary.Open(args[1].Split(',', StringSplitOptions.RemoveEmptyEntries));
    var hm = new RefractorForge.Formats.Terrain.Heightmap(64, 64);
    var cfg = new RefractorForge.Formats.Terrain.TerrainConfig { WorldSize = 2048, MaterialSize = 64, YScale = 1f, WaterLevel = 0 };
    var sun = new RefractorForge.Formats.Geometry.Vec3(0.6f, 0.6f, 0.3f);
    foreach (var name in args.Skip(2))
    {
        if (!lib.TryGet(name, out var m) || m?.LightmapUvs is null) { Console.WriteLine($"{name,-28} no lightmap-UV mesh"); continue; }
        var world = System.Numerics.Matrix4x4.CreateTranslation(1000, 50, 1000);
        var lm = RefractorForge.Render.ObjectLightmapBaker.Bake(m, world, hm, cfg, sun, 256);
        if (lm is null) { Console.WriteLine($"{name,-28} bake null"); continue; }
        int cov = 0; byte mn = 255, mx = 0;
        for (int i = 0; i < lm.Width * lm.Height; i++) { byte v = lm.Rgba[i * 4]; if (v > 0) cov++; if (v < mn) mn = v; if (v > mx) mx = v; }
        var tga = RefractorForge.Render.TgaTexture.EncodeGrayColormapped(lm);
        var dec = RefractorForge.Render.TgaTexture.Decode(tga);
        bool rt = dec is not null && dec.Width == 256; if (rt) for (int i = 0; i < 256 * 256; i++) if (dec!.Rgba[i * 4] != lm.Rgba[i * 4]) { rt = false; break; }
        Console.WriteLine($"{name,-28} baked 256^2, {cov} covered ({100.0 * cov / (256 * 256):0}%), gray {mn}..{mx}, tga round-trip {(rt ? "OK" : "FAIL")}");
    }
    return 0;
}

if (arg == "bakeobjlm")
{
    // bakeobjlm — self-contained: bake a synthetic peaked-roof object's lightmap (two slopes with different normals ->
    // different N.L), verify the lightmap has lit/shadowed VARIATION, and round-trip it through the colour-mapped TGA.
    var hm = new RefractorForge.Formats.Terrain.Heightmap(64, 64);   // flat low terrain (no occlusion) -> variation is pure N.L
    var cfg = new RefractorForge.Formats.Terrain.TerrainConfig { WorldSize = 256, MaterialSize = 64, YScale = 1f, WaterLevel = 0 };
    var pos = new System.Numerics.Vector3[]
    {
        new(40,10,40), new(40,10,160), new(100,40,40), new(100,40,160),   // left slope
        new(100,40,40), new(100,40,160), new(160,10,40), new(160,10,160), // right slope
    };
    var lmuv = new System.Numerics.Vector2[]
    {
        new(0f,0f), new(0f,1f), new(0.5f,0f), new(0.5f,1f),
        new(0.5f,0f), new(0.5f,1f), new(1f,0f), new(1f,1f),
    };
    var uv = new System.Numerics.Vector2[8];
    var indices = new[] { 0,1,2, 1,3,2, 4,5,6, 5,7,6 };
    var part = new RefractorForge.Render.MeshLibrary.MaterialPart(indices, System.Numerics.Vector3.One, null, false);
    var mesh = new RefractorForge.Render.MeshLibrary.Mesh(pos, uv, new[] { part }) { LightmapUvs = lmuv };
    var sun = new RefractorForge.Formats.Geometry.Vec3(0.7f, 0.5f, 0.2f);
    var lm = RefractorForge.Render.ObjectLightmapBaker.Bake(mesh, System.Numerics.Matrix4x4.Identity, hm, cfg, sun, 128);
    if (lm is null) { Console.WriteLine("OBJLM BAKE FAILED: null"); return 1; }
    byte mn = 255, mx = 0; int covered = 0;
    for (int i = 0; i < lm.Width * lm.Height; i++) { byte v = lm.Rgba[i * 4]; if (v > 0) covered++; if (v < mn) mn = v; if (v > mx) mx = v; }
    var tga = RefractorForge.Render.TgaTexture.EncodeGrayColormapped(lm);
    var dec = RefractorForge.Render.TgaTexture.Decode(tga);
    bool rt = dec is not null && dec.Width == lm.Width && dec.Height == lm.Height;
    if (rt) for (int i = 0; i < lm.Width * lm.Height; i++) if (dec!.Rgba[i * 4] != lm.Rgba[i * 4]) { rt = false; break; }
    bool variation = (mx - mn) > 20 && covered > lm.Width;   // the two slopes shade differently
    Console.WriteLine($"baked {lm.Width}^2, gray {mn}..{mx}, {covered} covered texels; TGA round-trip {(rt ? "OK" : "FAIL")}");
    Console.WriteLine(variation && rt ? "OBJLM BAKE TESTS PASSED" : "OBJLM BAKE TESTS FAILED");
    return variation && rt ? 0 : 1;
}

if (arg == "olmtest" && args.Length >= 2)
{
    // olmtest <level.rfa,...> — decode the level's ObjectLightMaps/*.tga (colour-mapped TGA) + match each to a placed
    // static object by template + world position. Verifies the whole per-object-lightmap loader path headlessly.
    var arcs = args[1].Split(',', StringSplitOptions.RemoveEmptyEntries).Where(System.IO.File.Exists).ToArray();
    var rfas = new System.Collections.Generic.List<RefractorForge.Formats.Rfa.RfaArchive>();
    foreach (var a in arcs) { try { rfas.Add(RefractorForge.Formats.Rfa.RfaArchive.Open(a)); } catch { } }
    var olm = RefractorForge.Render.ObjectLightmaps.FromArchives(rfas);
    var lvl = RefractorForge.Render.LevelArchive.FromRfa(arcs);
    int matched = 0, total = lvl.StaticObjects.Objects.Count;
    foreach (var o in lvl.StaticObjects.Objects)
        if (olm.Match(o.Template, o.Position.X, o.Position.Y, o.Position.Z) is not null) matched++;
    Console.WriteLine($"ObjectLightmaps: {olm.Count} lightmap(s) decoded; matched {matched}/{total} placed objects");
    foreach (var e in olm.Entries.Take(10)) Console.WriteLine($"  {e.Template} @ {e.X},{e.Y},{e.Z}  ->  {e.Texture.Width}x{e.Texture.Height}");
    Console.WriteLine(olm.Count > 0 && matched >= olm.Count ? "OLM TEST PASSED" : (olm.Count == 0 ? "OLM TEST: no lightmaps in level" : "OLM TEST: some lightmaps unmatched"));
    return 0;
}

if (arg == "rfaextract" && args.Length >= 4)
{
    // rfaextract <archive.rfa> <entry-substring> <out> — write the first matching entry's raw bytes to a file.
    var a = RefractorForge.Formats.Rfa.RfaArchive.Open(args[1]);
    var sub = args[2].ToLowerInvariant();
    foreach (var e in a.Entries)
        if (e.Name.Replace('\\', '/').ToLowerInvariant().Contains(sub))
        {
            var bytes = a.Read(e);
            System.IO.File.WriteAllBytes(args[3], bytes);
            Console.WriteLine($"wrote {e.Name} -> {args[3]} ({bytes.Length} bytes)");
            if (bytes.Length >= 18 && (args[2].EndsWith(".tga") || e.Name.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)))
            {
                int idLen = bytes[0], cmap = bytes[1], type = bytes[2];
                int w = bytes[12] | (bytes[13] << 8), h = bytes[14] | (bytes[15] << 8), bpp = bytes[16];
                Console.WriteLine($"  TGA: idLen={idLen} cmapType={cmap} imageType={type} {w}x{h} {bpp}bpp (type 1/9=colormapped, 2/10=truecolor, 3/11=grayscale)");
            }
            return 0;
        }
    Console.WriteLine($"no entry matching '{sub}'"); return 1;
}

if (arg == "lsbvis" && args.Length >= 2)
{
    // lsbvis <level.rfa,...> — load a level the same way the editor does, pull its LightmapShadowBits, and run the
    // exact ToVisibility path the editor's InitTerrainShadowOnLoad uses. Verifies the display lightmap won't crash and
    // reports the raster side + lit fraction.
    var arcs = args[1].Split(',', StringSplitOptions.RemoveEmptyEntries);
    var lvl = RefractorForge.Render.LevelArchive.FromRfa(arcs);
    if (lvl.Shadow is null) { Console.WriteLine("no LightmapShadowBits.lsb in this level"); return 0; }
    var vis = lvl.Shadow.ToVisibility(out int side);
    long lit = 0; foreach (var v in vis) if (v != 0) lit++;
    Console.WriteLine($"lsb: {lvl.Shadow.Tiles.Count} tiles, GridDim {lvl.Shadow.GridDim}, TilePixels {lvl.Shadow.TilePixels} -> visibility {side}^2, lit {100.0 * lit / Math.Max(1, vis.Length):0.0}%");
    Console.WriteLine("LSBVIS OK");
    return 0;
}

if (arg == "lsbroundtrip" && args.Length >= 2)
{
    // Byte-exact round-trip of the engine's Textures/LightmapShadowBits.lsb: read -> decode -> encode -> compare.
    byte[] lsbOrig = System.IO.File.ReadAllBytes(args[1]);
    var lsb = RefractorForge.Formats.Terrain.LightmapShadowBits.Decode(lsbOrig);
    byte[] lsbRe = lsb.Encode();
    Console.WriteLine($"decoded {lsb.Tiles.Count} tiles from {lsbOrig.Length} bytes; re-encoded {lsbRe.Length} bytes");
    bool lsbOk = lsbRe.Length == lsbOrig.Length;
    if (lsbOk)
        for (int i = 0; i < lsbOrig.Length; i++)
            if (lsbOrig[i] != lsbRe[i]) { Console.WriteLine($"   first diff at offset {i}"); lsbOk = false; break; }
    Console.WriteLine(lsbOk ? "LSB ROUND-TRIP PASSED" : "LSB ROUND-TRIP FAILED");
    return lsbOk ? 0 : 1;
}

if (arg == "rfaroundtrip" && args.Length >= 2)
{
    // Read every entry from a REAL archive, repack with our writer, reopen, and compare byte-for-byte.
    // We STREAM the rebuilt archive to a temp file and verify each entry by seeking to its on-disk offset,
    // rather than holding the whole thing in a byte[]. That's mandatory for big archives: the base BF1942
    // texture.rfa is ~2.3 GiB uncompressed and literal-re-encodes past the ~2 GiB managed-array ceiling, so
    // the old Build()->byte[]->Load() path couldn't even allocate it (it overflowed an int offset accumulator).
    var orig = RefractorForge.Formats.Rfa.RfaArchive.Open(args[1]);
    long uncTotal = 0; foreach (var e in orig.Entries) uncTotal += e.UncompressedSize;

    string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rf_rt_" + System.Guid.NewGuid().ToString("N") + ".rfa");
    bool ok; int mism = 0; long ours = 0;
    try
    {
        using (var ofs = new System.IO.FileStream(tmp, System.IO.FileMode.Create, System.IO.FileAccess.ReadWrite, System.IO.FileShare.None))
            RefractorForge.Formats.Rfa.RfaWriter.WriteTo(ofs, orig.Entries.Count,
                i => orig.Entries[i].Name, i => orig.Read(orig.Entries[i]));
        ours = new System.IO.FileInfo(tmp).Length;

        var toc = RefractorForge.Formats.Rfa.RfaArchive.ReadToc(tmp);
        ok = toc.Count == orig.Entries.Count;
        using (var vfs = new System.IO.FileStream(tmp, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read))
            for (int i = 0; i < orig.Entries.Count && i < toc.Count; i++)
            {
                var want = orig.Read(orig.Entries[i]);             // freshly decompressed original
                vfs.Seek(toc[i].Offset, System.IO.SeekOrigin.Begin);
                var region = new byte[toc[i].BlockSize];
                vfs.ReadExactly(region);
                var got = RefractorForge.Formats.Rfa.RfaArchive.DecodeRegion(region, toc[i].UncompressedSize, toc[i].Name);
                if (toc[i].Name != orig.Entries[i].Name || !got.AsSpan().SequenceEqual(want))
                { ok = false; if (++mism <= 5) Console.WriteLine($"   MISMATCH {orig.Entries[i].Name}"); }
            }
    }
    finally { try { if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp); } catch { } }

    Console.WriteLine($"files={orig.Entries.Count} uncompressed={uncTotal/1024}KiB  ours={ours/1024}KiB  mismatches={mism}");
    Console.WriteLine(ok ? "RFA ROUND-TRIP PASSED" : "RFA ROUND-TRIP FAILED");
    return ok ? 0 : 1;
}

if (arg == "rfawrite")
{
    bool ok = true;
    void Check(string label, bool cond) { Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {label}"); ok &= cond; }
    // 0) compressor round-trips through the byte-exact decoder, and actually shrinks compressible data.
    bool cOk = true; long cIn = 0, cOut = 0;
    System.Action<string, byte[]> roundtrip = (label, d) =>
    {
        var packed = RefractorForge.Formats.Rfa.Lzo1x.Compress(d);
        var back = RefractorForge.Formats.Rfa.Lzo1x.Decompress(packed, d.Length);
        bool eq = d.AsSpan().SequenceEqual(back);
        if (!eq) { cOk = false; Console.WriteLine($"    compress[{label}] MISMATCH"); }
        cIn += d.Length; cOut += packed.Length;
    };
    roundtrip("zeros", new byte[40000]);
    var rle = new byte[40000]; for (int i = 0; i < rle.Length; i++) rle[i] = (byte)(i / 500); roundtrip("runs", rle);
    var rep = new byte[40000]; for (int i = 0; i < rep.Length; i++) rep[i] = (byte)("ABCDEFGH"[i % 8]); roundtrip("periodic", rep);
    var txt = new byte[20000]; var phrase = System.Text.Encoding.ASCII.GetBytes("the quick brown fox jumps over the lazy dog. ");
    for (int i = 0; i < txt.Length; i++) txt[i] = phrase[i % phrase.Length]; roundtrip("text", txt);
    foreach (var sz in new[] { 1, 2, 3, 4, 5, 33, 34, 300, 32768 }) { var r = new byte[sz]; new System.Random(sz).NextBytes(r); roundtrip($"rand{sz}", r); }
    Check($"compressor round-trips all patterns (compressible {cIn/1024}KiB -> {cOut/1024}KiB)", cOk);
    Check("compressible data shrinks", cOut < cIn / 2);

    var rnd = new System.Random(7);

    // 1) literal-block encoder round-trips through the byte-exact decoder at every boundary size.
    int[] sizes = { 1, 2, 3, 4, 5, 17, 18, 19, 20, 254, 255, 256, 257, 272, 273, 274, 1000, 32767, 32768 };
    bool encOk = true;
    foreach (var n in sizes)
    {
        var src = new byte[n]; rnd.NextBytes(src);
        var enc = RefractorForge.Formats.Rfa.RfaWriter.EncodeLiteralBlock(src);
        var dec = RefractorForge.Formats.Rfa.Lzo1x.Decompress(enc, n);
        if (!src.AsSpan().SequenceEqual(dec)) { encOk = false; Console.WriteLine($"    size {n} MISMATCH (enc {enc.Length}B)"); }
    }
    Check($"literal-block encoder round-trips all boundary sizes", encOk);

    // 2) whole-archive build -> read back each entry (incl. a multi-block file > 32 KiB and an empty file).
    var big = new byte[80000]; rnd.NextBytes(big);          // 3 blocks
    var entries = new System.Collections.Generic.List<(string, byte[])>
    {
        ("levels/test/StaticObjects.con", System.Text.Encoding.ASCII.GetBytes("Object.create Foo\r\nObject.absolutePosition 1/2/3\r\n")),
        ("levels/test/Heightmap.raw", new byte[5000]),
        ("levels/test/big.bin", big),
        ("levels/test/empty.txt", System.Array.Empty<byte>()),
    };
    for (int i = 0; i < entries.Count; i++) rnd.NextBytes(entries[i].Item2);   // fill (empty stays empty: len 0)
    entries[3] = ("levels/test/empty.txt", System.Array.Empty<byte>());
    var bytes = RefractorForge.Formats.Rfa.RfaWriter.Build(entries);
    var arch = RefractorForge.Formats.Rfa.RfaArchive.Load(bytes);
    Check($"archive has {entries.Count} entries", arch.Entries.Count == entries.Count);
    bool readOk = true;
    for (int i = 0; i < entries.Count; i++)
    {
        var got = arch.Read(arch.Entries[i]);
        if (!entries[i].Item2.AsSpan().SequenceEqual(got)) { readOk = false; Console.WriteLine($"    entry {entries[i].Item1} MISMATCH ({got.Length} vs {entries[i].Item2.Length})"); }
    }
    Check("every built entry reads back byte-identical (incl. 3-block + empty)", readOk);

    // 3) repack with a substitution: replace one entry, copy the rest.
    var repl = new System.Collections.Generic.Dictionary<string, byte[]>
        { ["levels/test/StaticObjects.con"] = System.Text.Encoding.ASCII.GetBytes("Object.create Bar\r\nObject.absolutePosition 9/9/9\r\n") };
    var repacked = RefractorForge.Formats.Rfa.RfaWriter.Repack(arch, repl);
    var arch2 = RefractorForge.Formats.Rfa.RfaArchive.Load(repacked);
    var subEntry = arch2.Entries.First(e => e.Name.EndsWith("StaticObjects.con"));
    var bigEntry = arch2.Entries.First(e => e.Name.EndsWith("big.bin"));
    Check("repack: substituted entry has new bytes", System.Text.Encoding.ASCII.GetString(arch2.Read(subEntry)).Contains("Bar"));
    Check("repack: untouched 3-block entry still byte-identical", big.AsSpan().SequenceEqual(arch2.Read(bigEntry)));

    Console.WriteLine(ok ? "RFA WRITE TESTS PASSED" : "RFA WRITE TESTS FAILED");
    return ok ? 0 : 1;
}

if (arg == "savelevel" && args.Length >= 3)
{
    // savelevel <srcLevelDir> <dstDir> : copy, apply edits, save to disk, reload, verify persistence.
    string src = args[1], dst = args[2];
    if (Directory.Exists(dst)) Directory.Delete(dst, true);
    foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
    {
        var rel = Path.GetRelativePath(src, f);
        var outp = Path.Combine(dst, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(outp)!);
        File.Copy(f, outp, true);
    }
    var cfg = TerrainConfig.Load(Directory.EnumerateFiles(dst, "Terrain.con", SearchOption.AllDirectories).First());
    var hm = Heightmap.LoadForMaterialSize(Directory.EnumerateFiles(dst, "Heightmap.raw", SearchOption.AllDirectories).First(), cfg.MaterialSize);
    var soPath = Directory.EnumerateFiles(dst, "StaticObjects.con", SearchOption.AllDirectories).First();
    var so = StaticObjectsFile.Load(soPath);
    var matPath = Directory.EnumerateFiles(dst, "MaterialMap.raw", SearchOption.AllDirectories).FirstOrDefault();
    var mat = matPath is null ? null : MaterialMap.LoadForMaterialSize(matPath, cfg.MaterialSize);
    var gp = new EditableGameplay(GameplayObjects.LoadFolder(dst));

    // --- apply edits ---
    var cp0 = gp.GetPos(GpKind.ControlPoint, 0);
    var cpMoved = new Vec3(cp0.X + 40f, cp0.Y, cp0.Z - 25f);
    gp.SetPos(GpKind.ControlPoint, 0, cpMoved);
    gp.SetRadius(0, 77f);
    if (gp.VehicleSpawns.Count > 0) gp.SetYaw(GpKind.Vehicle, 0, 123f);
    int placedIndex = gp.Add(GpKind.ControlPoint, EditableGameplay.NewControlPoint(new Vec3(600, 30, 600)));
    // place a vehicle spawn and assign it a vehicle (should get an ObjectSpawnTemplates.con entry on save)
    int vIdx = gp.Add(GpKind.Vehicle, EditableGameplay.NewVehicleSpawn(new Vec3(700, 30, 700)));
    var vplaced = (VehicleSpawnDef)gp.GetItem(GpKind.Vehicle, vIdx);
    gp.SetItem(GpKind.Vehicle, vIdx, vplaced with { Name = "RF_TestSpawner", Vehicle = "sheridan" });
    int hmW = hm.Width;
    ushort h0 = hm[10, 10]; hm[10, 10] = (ushort)System.Math.Min(65535, h0 + 1000);
    if (so.Objects.Count > 0) so.Objects[0].Position = new Vec3(so.Objects[0].Position.X + 5f, so.Objects[0].Position.Y, so.Objects[0].Position.Z);
    if (mat is not null) mat[5, 5] = 9;

    var written = LevelSaver.SaveFolder(dst, so, soPath, hm, mat, gp);
    Console.WriteLine($"wrote {written.Count} files:");
    foreach (var w in written) Console.WriteLine("   " + Path.GetRelativePath(dst, w));

    // --- reload from disk and verify ---
    var cfg2 = TerrainConfig.Load(Directory.EnumerateFiles(dst, "Terrain.con", SearchOption.AllDirectories).First());
    var hm2 = Heightmap.LoadForMaterialSize(Directory.EnumerateFiles(dst, "Heightmap.raw", SearchOption.AllDirectories).First(), cfg2.MaterialSize);
    var gp2 = GameplayObjects.LoadFolder(dst);
    var so2 = StaticObjectsFile.Load(soPath);
    var mat2 = matPath is null ? null : MaterialMap.LoadForMaterialSize(matPath, cfg2.MaterialSize);

    bool ok = true;
    void Check(string label, bool cond) { Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {label}"); ok &= cond; }
    Check($"CP0 moved persisted ({gp2.ControlPoints[0].Position.X:0}/{gp2.ControlPoints[0].Position.Z:0})",
          System.Math.Abs(gp2.ControlPoints[0].Position.X - cpMoved.X) < 0.01f && System.Math.Abs(gp2.ControlPoints[0].Position.Z - cpMoved.Z) < 0.01f);
    Check($"CP0 radius persisted ({gp2.ControlPoints[0].Radius:0})", System.Math.Abs(gp2.ControlPoints[0].Radius - 77f) < 0.01f);
    if (gp2.VehicleSpawns.Count > 0) Check($"vehicle yaw persisted ({gp2.VehicleSpawns[0].Rotation.X:0})", System.Math.Abs(gp2.VehicleSpawns[0].Rotation.X - 123f) < 0.01f);
    Check($"placed CP persisted (count {gp2.ControlPoints.Count})", gp2.ControlPoints.Count == gp.ControlPoints.Count);
    var placedSpawn = gp2.VehicleSpawns.FirstOrDefault(s => s.Name == "RF_TestSpawner");
    Check($"placed vehicle spawn resolves its vehicle ('{placedSpawn.Vehicle}')", placedSpawn.Name == "RF_TestSpawner" && placedSpawn.Vehicle == "sheridan");
    Check($"heightmap edit persisted (h={hm2[10,10]})", hm2[10, 10] == hm[10, 10]);
    Check($"static object move persisted", System.Math.Abs(so2.Objects[0].Position.X - so.Objects[0].Position.X) < 0.01f);
    if (mat2 is not null) Check($"material paint persisted (m={mat2[5,5]})", mat2[5, 5] == 9);

    Console.WriteLine(ok ? "SAVE ROUND-TRIP PASSED" : "SAVE ROUND-TRIP FAILED");
    return ok ? 0 : 1;
}

if (arg == "gameplayedit" && args.Length >= 2)
{
    // Validate move + radius edits through the shared undo stack (gameplay commands ignore the file).
    var gp = new EditableGameplay(GameplayObjects.LoadFolder(args[1]));
    if (gp.ControlPoints.Count == 0) { Console.WriteLine("no control points to test"); return 1; }
    var hist = new EditHistory(new StaticObjectsFile());
    int fired = 0; System.Action onChanged = () => fired++;
    bool ok = true;

    // --- move CP 0 ---
    var p0 = gp.GetPos(GpKind.ControlPoint, 0);
    var moved = new Vec3(p0.X + 25f, p0.Y + 3f, p0.Z - 10f);
    hist.Do(new GameplayMoveCommand(gp, GpKind.ControlPoint, 0, moved, onChanged));
    ok &= gp.GetPos(GpKind.ControlPoint, 0).X == moved.X && gp.GetPos(GpKind.ControlPoint, 0).Z == moved.Z;
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] move CP0 -> {gp.GetPos(GpKind.ControlPoint,0).X:0}/{gp.GetPos(GpKind.ControlPoint,0).Z:0}");
    hist.Undo(); bool back = gp.GetPos(GpKind.ControlPoint, 0).X == p0.X && gp.GetPos(GpKind.ControlPoint, 0).Z == p0.Z;
    Console.WriteLine($"  [{(back ? "PASS" : "FAIL")}] undo restores {gp.GetPos(GpKind.ControlPoint,0).X:0}/{gp.GetPos(GpKind.ControlPoint,0).Z:0}"); ok &= back;
    hist.Redo(); ok &= gp.GetPos(GpKind.ControlPoint, 0).X == moved.X;
    Console.WriteLine($"  [{(gp.GetPos(GpKind.ControlPoint,0).X==moved.X ? "PASS" : "FAIL")}] redo re-applies");

    // --- radius CP 0 ---
    float r0 = gp.GetRadius(0);
    hist.Do(new GameplayRadiusCommand(gp, 0, r0 + 15f, onChanged));
    bool rOk = System.Math.Abs(gp.GetRadius(0) - (r0 + 15f)) < 1e-3f;
    Console.WriteLine($"  [{(rOk ? "PASS" : "FAIL")}] radius {r0:0} -> {gp.GetRadius(0):0}"); ok &= rOk;
    hist.Undo(); bool rBack = System.Math.Abs(gp.GetRadius(0) - r0) < 1e-3f;
    Console.WriteLine($"  [{(rBack ? "PASS" : "FAIL")}] undo radius -> {gp.GetRadius(0):0}"); ok &= rBack;

    // --- move a vehicle + a soldier spawn (index bounds) ---
    if (gp.VehicleSpawns.Count > 0) { var vp = gp.GetPos(GpKind.Vehicle, 0); hist.Do(new GameplayMoveCommand(gp, GpKind.Vehicle, 0, new Vec3(vp.X+5,vp.Y,vp.Z), onChanged)); hist.Undo(); }
    if (gp.SoldierSpawns.Count > 0) { var sp = gp.GetPos(GpKind.Soldier, 0); hist.Do(new GameplayMoveCommand(gp, GpKind.Soldier, 0, new Vec3(sp.X+5,sp.Y,sp.Z), onChanged)); hist.Undo(); }

    // --- rotate a vehicle spawn via set-item (yaw) ---
    if (gp.VehicleSpawns.Count > 0)
    {
        var v = (VehicleSpawnDef)gp.GetItem(GpKind.Vehicle, 0);
        hist.Do(new GameplaySetItemCommand(gp, GpKind.Vehicle, 0, v with { Rotation = new Vec3(90f, 0, 0) }, onChanged));
        bool rotOk = System.Math.Abs(gp.GetRotation(GpKind.Vehicle, 0).X - 90f) < 1e-3f;
        Console.WriteLine($"  [{(rotOk ? "PASS" : "FAIL")}] rotate vehicle yaw -> {gp.GetRotation(GpKind.Vehicle,0).X:0}"); ok &= rotOk;
        hist.Undo(); ok &= System.Math.Abs(gp.GetRotation(GpKind.Vehicle, 0).X - v.Rotation.X) < 1e-3f;
        Console.WriteLine($"  [{(System.Math.Abs(gp.GetRotation(GpKind.Vehicle,0).X - v.Rotation.X)<1e-3f ? "PASS" : "FAIL")}] undo rotation");
    }

    // --- place a new control point, then delete it (with undo round-trips) ---
    int cpBefore = gp.ControlPoints.Count;
    var add = new GameplayAddCommand(gp, GpKind.ControlPoint, EditableGameplay.NewControlPoint(new Vec3(500, 30, 500)), onChanged);
    hist.Do(add);
    bool addOk = gp.ControlPoints.Count == cpBefore + 1 && add.Index == cpBefore;
    Console.WriteLine($"  [{(addOk ? "PASS" : "FAIL")}] place CP -> count {gp.ControlPoints.Count}, index {add.Index}"); ok &= addOk;
    hist.Undo(); bool addUndo = gp.ControlPoints.Count == cpBefore;
    Console.WriteLine($"  [{(addUndo ? "PASS" : "FAIL")}] undo placement -> count {gp.ControlPoints.Count}"); ok &= addUndo;
    hist.Redo(); // re-add for delete test
    int cpNow = gp.ControlPoints.Count;
    hist.Do(new GameplayDeleteCommand(gp, GpKind.ControlPoint, cpNow - 1, onChanged));
    bool delOk = gp.ControlPoints.Count == cpNow - 1;
    hist.Undo(); bool delUndo = gp.ControlPoints.Count == cpNow;
    Console.WriteLine($"  [{(delOk && delUndo ? "PASS" : "FAIL")}] delete CP + undo re-inserts -> count {gp.ControlPoints.Count}"); ok &= delOk && delUndo;

    Console.WriteLine($"  onChanged fired {fired} times");
    Console.WriteLine(ok && back && rOk && rBack ? "GAMEPLAY EDIT TESTS PASSED" : "GAMEPLAY EDIT TESTS FAILED");
    return ok ? 0 : 1;
}

if (arg == "terrainbrush" && args.Length >= 2)
{
    var L = LevelArchive.IsRfa(args[1]) ? LevelArchive.FromRfa(args[1]) : null;
    Heightmap hm; TerrainConfig cfg; StaticObjectsFile so;
    if (L is not null) { hm = L.Heightmap; cfg = L.Config; so = L.StaticObjects; }
    else
    {
        cfg = TerrainConfig.Load(Directory.EnumerateFiles(args[1], "*.con", SearchOption.AllDirectories).First(f => f.EndsWith("Terrain.con", StringComparison.OrdinalIgnoreCase)));
        hm = Heightmap.LoadForMaterialSize(Directory.EnumerateFiles(args[1], "*.raw", SearchOption.AllDirectories).First(), cfg.MaterialSize);
        so = StaticObjectsFile.Load(Directory.EnumerateFiles(args[1], "StaticObjects.con", SearchOption.AllDirectories).First());
    }
    int cc = hm.Width / 2, cr = hm.Height / 2; float sp = cfg.HorizontalSpacing;
    ushort before = hm[cc, cr];

    // Sculpt with the real editor (the same path the viewer uses), coalesced into one TerrainEdit.
    var ed = new TerrainEditor(hm, cfg);
    var stroke = ed.BeginStroke();
    for (int i = 0; i < 4; i++) stroke.Dab(cc * sp, cr * sp, new TerrainBrush(BrushMode.Raise, 30f * sp, 3f, BrushFalloff.Smooth));
    var edit = stroke.Finish();
    int afterCentre = hm[cc, cr];

    // Wrap into the object history; verify Apply is an idempotent re-apply and Undo/Redo round-trip.
    int rebuilds = 0;
    var hist = new EditHistory(so);
    hist.Do(new TerrainStrokeCommand(edit!, hm, () => rebuilds++));
    int afterApply = hm[cc, cr];
    hist.Undo(); int afterUndo = hm[cc, cr];
    hist.Redo(); int afterRedo = hm[cc, cr];

    Console.WriteLine($"raise: centre {before} -> {afterCentre} (rose by {afterCentre - before})");
    Console.WriteLine($"edit rect = {edit!.W}x{edit.H} cells");
    Console.WriteLine($"wrapper: apply-noop={afterApply == afterCentre}  undo={afterUndo == before}  redo={afterRedo == afterCentre}  rebuilds={rebuilds} (>=3)");
    return 0;
}

if (arg == "compositetest" && args.Length >= 2)
{
    var sof = LevelArchive.IsRfa(args[1]) ? LevelArchive.FromRfa(args[1]).StaticObjects
        : StaticObjectsFile.Load(Directory.EnumerateFiles(args[1], "StaticObjects.con", SearchOption.AllDirectories).First());
    var h = new EditHistory(sof);
    var a = sof.Objects[0]; var b = sof.Objects[1];
    var a0 = a.Position; var b0 = b.Position;
    h.Do(new CompositeCommand(new IEditCommand[]
    {
        new MoveObject(a.Id, new Vec3(a0.X + 10, a0.Y, a0.Z)),
        new MoveObject(b.Id, new Vec3(b0.X, b0.Y, b0.Z + 5)),
    }));
    Console.WriteLine($"after composite: a dX={a.Position.X - a0.X:0.#} (10)  b dZ={b.Position.Z - b0.Z:0.#} (5)");
    h.Undo();
    Console.WriteLine($"after undo: a reverted={a.Position.Equals(a0)}  b reverted={b.Position.Equals(b0)}");
    return 0;
}

if (arg == "gizmotest")
{
    // validate translate-gizmo math against known geometry
    var origin = new System.Numerics.Vector3(100, 50, 200);
    System.Numerics.Vector3 V(float x, float y, float z) => new(x, y, z);
    RefractorForge.Render.Ray DownAt(System.Numerics.Vector3 p) => new(new System.Numerics.Vector3(p.X, p.Y + 150f, p.Z), V(0, -1, 0));

    float tX = RefractorForge.Render.Gizmo.ClosestAxisParam(DownAt(origin + V(10, 0, 0)), origin, System.Numerics.Vector3.UnitX);
    float tZ = RefractorForge.Render.Gizmo.ClosestAxisParam(DownAt(origin + V(0, 0, 7)), origin, System.Numerics.Vector3.UnitZ);
    Console.WriteLine($"ClosestAxisParam X: expect 10 -> {tX:0.###}   Z: expect 7 -> {tZ:0.###}");

    int ax = RefractorForge.Render.Gizmo.PickAxis(DownAt(origin + V(5, 0, 0)), origin, 12f, 3f);
    int az = RefractorForge.Render.Gizmo.PickAxis(DownAt(origin + V(0, 0, 5)), origin, 12f, 3f);
    int none = RefractorForge.Render.Gizmo.PickAxis(DownAt(origin + V(40, 0, 40)), origin, 12f, 3f);
    Console.WriteLine($"PickAxis  on-X -> {ax} (0)   on-Z -> {az} (2)   far -> {none} (-1)");

    // simulate a drag along X: start ray over +3, move to over +9 -> delta should be ~6
    float t0 = RefractorForge.Render.Gizmo.ClosestAxisParam(DownAt(origin + V(3, 0, 0)), origin, System.Numerics.Vector3.UnitX);
    float t1 = RefractorForge.Render.Gizmo.ClosestAxisParam(DownAt(origin + V(9, 0, 0)), origin, System.Numerics.Vector3.UnitX);
    Console.WriteLine($"drag delta along X: expect ~6 -> {t1 - t0:0.###}");

    // rotate rings: ray straight down hits the yaw ring (XZ plane) at +X (angle 0) and +Z (angle 90deg)
    var o0 = System.Numerics.Vector3.Zero;
    int ry0 = RefractorForge.Render.Gizmo.PickRing(DownAt(V(10, 0, 0)), o0, 10f, 2.5f);   // expect channel 0 (yaw)
    int rz = RefractorForge.Render.Gizmo.PickRing(new RefractorForge.Render.Ray(V(10, 0, 60), V(0, 0, -1)), o0, 10f, 2.5f); // XY-plane -> roll(2)
    Console.WriteLine($"PickRing  on-yaw -> {ry0} (0)   on-roll -> {rz} (2)");
    RefractorForge.Render.Gizmo.RayPlaneHit(DownAt(V(0, 0, 10)), o0, System.Numerics.Vector3.UnitY, out var rh, out _);
    float ang = RefractorForge.Render.Gizmo.RingAngle(rh, o0, 0) * 180f / MathF.PI;
    Console.WriteLine($"RingAngle yaw at +Z: expect 90 -> {ang:0.#}");
    return 0;
}

if (arg == "groundpick" && args.Length >= 2)
{
    // groundpick <level>  — validate ray-vs-terrain against the real heightmap
    TerrainConfig gcfg; Heightmap ghm;
    if (LevelArchive.IsRfa(args[1])) { var L = LevelArchive.FromRfa(args[1]); gcfg = L.Config; ghm = L.Heightmap; }
    else
    {
        string Find(string n) => Directory.EnumerateFiles(args[1], n, SearchOption.AllDirectories).First();
        gcfg = TerrainConfig.Load(Find("Terrain.con"));
        ghm = Heightmap.LoadForMaterialSize(Find("Heightmap.raw"), gcfg.MaterialSize);
    }
    var tp = new TerrainPick(ghm, gcfg);
    float sp = gcfg.HorizontalSpacing;
    int cx = 128, cz = 128; float wx = cx * sp, wz = cz * sp;
    float exact = gcfg.HeightToMeters(ghm[cx, cz]);
    Console.WriteLine($"HeightAt grid ({cx},{cz}) world ({wx},{wz}): exact={exact:0.###} bilinear={tp.HeightAt(wx, wz):0.###} (diff {MathF.Abs(exact - tp.HeightAt(wx, wz)):0.0000})");
    var down = new RefractorForge.Render.Ray(new System.Numerics.Vector3(wx, 800f, wz), new System.Numerics.Vector3(0, -1, 0));
    Console.WriteLine(tp.Raycast(down, out var h1)
        ? $"down-ray  hit=({h1.X:0.#},{h1.Y:0.##},{h1.Z:0.#}) expectY={exact:0.##} dX={MathF.Abs(h1.X - wx):0.00} dZ={MathF.Abs(h1.Z - wz):0.00}"
        : "down-ray  MISS");
    var camPos = new System.Numerics.Vector3(-200f, 450f, -200f);
    var target = new System.Numerics.Vector3(tp.MaxX * 0.5f, tp.HeightAt(tp.MaxX * 0.5f, tp.MaxZ * 0.5f), tp.MaxZ * 0.5f);
    var ang = new RefractorForge.Render.Ray(camPos, target - camPos);
    if (tp.Raycast(ang, out var h2))
    {
        float surf = tp.HeightAt(h2.X, h2.Z);
        Console.WriteLine($"angle-ray hit=({h2.X:0.#},{h2.Y:0.##},{h2.Z:0.#}) surfaceY={surf:0.##} onSurface={MathF.Abs(h2.Y - surf) < 0.05f}");
    }
    else Console.WriteLine("angle-ray MISS");
    return 0;
}

if (arg == "rfaload" && args.Length >= 2)
{
    // rfaload <level.rfa> [outAtlas.bmp]   — load a level straight from a .rfa and report
    var L = LevelArchive.FromRfa(args[1]);
    long hsum = 0; foreach (var s in L.Heightmap.Samples) hsum += s;
    Console.WriteLine($"materialSize={L.Config.MaterialSize} worldSize={L.Config.WorldSize} water={L.Config.WaterLevel} yScale={L.Config.YScale}");
    Console.WriteLine($"heightmap={L.Heightmap.Width}x{L.Heightmap.Height} heightSum={hsum} objects={L.StaticObjects.Objects.Count} terrainTex={(L.Terrain is null ? "none" : L.Terrain.AtlasSize + "px")}");
    if (L.StaticObjects.Objects.Count > 0) Console.WriteLine($"obj[0]={L.StaticObjects.Objects[0].Template} @ {L.StaticObjects.Objects[0].Position}");
    if (L.Growth is not null)
        Console.WriteLine($"growth: under={(L.Growth.Under is null ? "none" : L.Growth.UnderSide + "^2")} over={(L.Growth.Over is null ? "none" : L.Growth.OverSide + "^2")} " +
                          $"(palette types: under={L.Growth.UnderPalette?.TypeCount ?? 0}, over={L.Growth.OverPalette?.TypeCount ?? 0})");
    if (L.Environment is not null)
        Console.WriteLine($"env: sun={L.Environment.SunDirection} skybox={L.Environment.SkyBoxMesh ?? "none"} water={L.Config.WaterLevel}");
    if (L.Terrain is not null && args.Length > 2)
    {
        var atlas = L.Terrain.BakeAtlas(2048);
        var img = new ImageBuffer(atlas.Width, atlas.Height);
        for (int i = 0; i < atlas.Width * atlas.Height; i++)
        { img.Rgb[i * 3] = atlas.Rgba[i * 4]; img.Rgb[i * 3 + 1] = atlas.Rgba[i * 4 + 1]; img.Rgb[i * 3 + 2] = atlas.Rgba[i * 4 + 2]; }
        img.SaveBmp(args[2]);
        Console.WriteLine($"baked atlas -> {args[2]}");
    }
    return 0;
}

if (arg == "bakeatlas" && args.Length >= 3)
{
    // bakeatlas <levelDir> <out.bmp> [worldSize] [size]
    var texDir = System.IO.Directory.EnumerateDirectories(args[1], "Textures", System.IO.SearchOption.AllDirectories).FirstOrDefault() ?? args[1];
    float worldSize = args.Length > 3 ? float.Parse(args[3]) : 2048f;
    int size = args.Length > 4 ? int.Parse(args[4]) : 2048;
    var tex = TerrainTexture.Load(texDir, worldSize);
    if (tex is null) { Console.WriteLine("no terrain tiles found"); return 0; }
    var sw = Stopwatch.StartNew();
    var atlas = tex.BakeAtlas(size);
    sw.Stop();
    var img = new ImageBuffer(atlas.Width, atlas.Height);
    for (int i = 0; i < atlas.Width * atlas.Height; i++)
    { img.Rgb[i * 3] = atlas.Rgba[i * 4]; img.Rgb[i * 3 + 1] = atlas.Rgba[i * 4 + 1]; img.Rgb[i * 3 + 2] = atlas.Rgba[i * 4 + 2]; }
    img.SaveBmp(args[2]);
    Console.WriteLine($"Baked {size}x{size} atlas from {texDir} in {sw.ElapsedMilliseconds} ms -> {args[2]}");
    return 0;
}

if (arg == "texinfo" && args.Length >= 2)
{
    var texDir = System.IO.Directory.EnumerateDirectories(args[1], "Textures", System.IO.SearchOption.AllDirectories).FirstOrDefault();
    Console.WriteLine($"Textures dir: {texDir ?? "(none)"}");
    if (texDir is not null)
    {
        var dds = System.IO.Directory.EnumerateFiles(texDir, "tx*.dds").ToList();
        Console.WriteLine($"tx*.dds count: {dds.Count}");
        try
        {
            var tex = TerrainTexture.Load(texDir, 2048f);
            Console.WriteLine(tex is null ? "Load returned null" : $"Loaded atlas {tex.AtlasSize}px");
        }
        catch (Exception ex) { Console.WriteLine($"EXCEPTION: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); }
    }
    return 0;
}

if (arg == "bench" && args.Length >= 2)
{
    var rfas = args.Where(a => a.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase)).ToArray();
    var texA = rfas.Where(a => Path.GetFileName(a).StartsWith("texture", StringComparison.OrdinalIgnoreCase)).ToArray();
    var meshA = rfas.Except(texA).ToArray();
    var scene = LevelScene.Load(args[1]);
    if (meshA.Length > 0) { var lib = MeshLibrary.Open(meshA); if (texA.Length > 0) lib.AttachTextures(TextureLibrary.Open(texA)); scene.AttachMeshes(lib); }
    var cam = scene.CreateAerialCamera(4f / 3f);
    void time(string label, int w, int h, bool fast, int n)
    {
        scene.Render(cam, w, h, -1, fast);   // warm caches
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < n; i++) scene.Render(cam, w, h, -1, fast);
        sw.Stop();
        double ms = sw.Elapsed.TotalMilliseconds / n;
        Console.WriteLine($"  {label,-26} {w}x{h}  {ms,7:F1} ms/frame  ({1000.0 / ms,5:F1} fps)");
    }
    Console.WriteLine("Render performance (single-threaded software):");
    time("fast (interaction)", 640, 480, true, 8);
    time("fast (interaction)", 960, 720, true, 5);
    time("quality (idle)", 960, 720, false, 3);
    time("quality (idle)", 1600, 1000, false, 2);
    return 0;
}

if (arg == "topdown" && args.Length >= 3)
{
    // Near-top-down, north (+Z) up, east (+X) right — to compare against the canonical InGameMap.
    float yawDeg = args.Length >= 4 && float.TryParse(args[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var yd) ? yd : 0f;
    var rfas = args.Where(a => a.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase)).ToArray();
    var texA = rfas.Where(a => Path.GetFileName(a).StartsWith("texture", StringComparison.OrdinalIgnoreCase)).ToArray();
    var meshA = rfas.Except(texA).ToArray();
    var probe = LevelScene.Load(args[1]);
    float ws = probe.WorldSize, c = ws / 2f;
    float pitch = -82f * MathF.PI / 180f, yaw = yawDeg * MathF.PI / 180f;
    var fwd = new Vector3(MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch), MathF.Cos(pitch) * MathF.Cos(yaw));
    float dist = ws * 0.62f;
    var target = new Vector3(c, probe.MidHeight, c);
    var eye = target - fwd * dist;
    HeadlessPreview.RenderLevelView(args[1], args[2], eye, target, 60f, 1024, 1024, 1,
                                    meshA.Length > 0 ? meshA : null, texA.Length > 0 ? texA : null);
    return 0;
}

if (arg == "detailcompare" && args.Length >= 5)
{
    // detailcompare <levelDir> <outPrefix> <cx> <cz> [radius]   (terrain only)
    float cx = float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
    float cz = float.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture);
    float radius = args.Length >= 6 && float.TryParse(args[5], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : 45f;
    int W = 1200, H = 860;
    var scene = LevelScene.Load(args[1]);
    if (scene.TerrainTex is null) { Console.WriteLine("no terrain texture"); return 0; }
    float gy = scene.HeightAtWorld(cx, cz);
    var target = new Vector3(cx, gy + radius * 0.04f, cz);
    var eye = new Vector3(cx - radius * 0.5f, gy + radius * 0.32f, cz - radius * 0.5f);
    var fwd = Vector3.Normalize(target - eye);
    var light = Vector3.Normalize(new Vector3(0.4f, 0.85f, 0.35f));
    Camera Cam() => new Camera {
        Position = eye, Pitch = MathF.Asin(Math.Clamp(fwd.Y, -1f, 1f)), Yaw = MathF.Atan2(fwd.X, fwd.Z),
        FovY = 55f * MathF.PI / 180f, Aspect = (float)W / H, Near = 0.3f, Far = scene.WorldSize * 3f };
    bool hasDetail = scene.TerrainTex.Detail is not null;
    var on = new ImageBuffer(W, H);
    SoftwareRenderer.DrawTerrainTextured(on, scene.Mesh, Cam(), light, scene.TerrainTex, scene.Config.WaterLevel);
    on.SaveBmp(args[2] + "_on.bmp");
    scene.TerrainTex.Detail = null;
    var off = new ImageBuffer(W, H);
    SoftwareRenderer.DrawTerrainTextured(off, scene.Mesh, Cam(), light, scene.TerrainTex, scene.Config.WaterLevel);
    off.SaveBmp(args[2] + "_off.bmp");
    Console.WriteLine($"detail loaded: {hasDetail}; wrote {args[2]}_on.bmp / _off.bmp");
    return 0;
}

if (arg == "closeup" && args.Length >= 5)
{
    // closeup <levelDir> <out.bmp> <cx> <cz> [radius] [meshArchive...]
    float cx = float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
    float cz = float.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture);
    float radius = args.Length >= 6 && float.TryParse(args[5], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : 60f;
    var rfas = args.Where(a => a.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase)).ToArray();
    var texArchives = rfas.Where(a => Path.GetFileName(a).StartsWith("texture", StringComparison.OrdinalIgnoreCase)).ToArray();
    var meshArchives = rfas.Except(texArchives).ToArray();
    // Ground height at the focus point, so the camera frames the props sitting on the terrain.
    var probe = LevelScene.Load(args[1]);
    float gy = probe.HeightAtWorld(cx, cz);
    var target = new Vector3(cx, gy + radius * 0.12f, cz);
    var eye = new Vector3(cx - radius * 0.85f, gy + radius * 0.6f, cz - radius * 0.85f);
    HeadlessPreview.RenderLevelView(args[1], args[2], eye, target, 55f, 1200, 900, 1,
                                    meshArchives.Length > 0 ? meshArchives : null,
                                    texArchives.Length > 0 ? texArchives : null);
    return 0;
}

if (arg == "render" && args.Length >= 3)
{
    int stride = args.Length >= 4 && int.TryParse(args[3], out var s) ? s : 1;
    // Any args after stride are RFA archives; texture*.rfa feed object/terrain textures, the rest geometry.
    var rfas = args.Skip(4).Where(a => a.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase)).ToArray();
    var texArchives = rfas.Where(a => Path.GetFileName(a).StartsWith("texture", StringComparison.OrdinalIgnoreCase)).ToArray();
    var meshArchives = rfas.Except(texArchives).ToArray();
    HeadlessPreview.RenderLevel(args[1], args[2], stride: stride,
                                meshArchives: meshArchives.Length > 0 ? meshArchives : null,
                                textureArchives: texArchives.Length > 0 ? texArchives : null);
    return 0;
}

if (arg == "edit" && args.Length >= 2)
    return EditTests(args[1]);

if (Directory.Exists(arg) && FindFile(arg, "StaticObjects.con") is not null)
    return ValidateRealLevel(arg);

return SyntheticDemo(arg);

// ---------------------------------------------------------------------------
static int ValidateRealLevel(string dir)
{
    Console.WriteLine("RefractorForge — validating real level");
    Console.WriteLine("======================================");

    var terrainPath = FindFile(dir, "Terrain.con");
    var hmPath = FindFile(dir, "Heightmap.raw");
    var soPath = FindFile(dir, "StaticObjects.con")!;

    var level = new Level { Name = new DirectoryInfo(dir).Name };
    bool ok = true;

    // --- Terrain.con ---
    if (terrainPath is not null)
    {
        var t = TerrainConfig.Load(terrainPath);
        level.Terrain = t;
        level.WorldSize = t.WorldSize;
        Console.WriteLine($"Terrain: materialSize={t.MaterialSize}, worldSize={t.WorldSize} " +
                          $"(ratio {(double)t.WorldSize / t.MaterialSize:0.#}:1), yScale={t.YScale}, water={t.WaterLevel}m");

        // --- Heightmap.raw, dimension == materialSize ---
        if (hmPath is not null)
        {
            var hm = Heightmap.LoadForMaterialSize(hmPath, t.MaterialSize);
            level.Heightmap = hm;
            ushort lo = ushort.MaxValue, hi = 0;
            foreach (var s in hm.Samples) { if (s < lo) lo = s; if (s > hi) hi = s; }
            Console.WriteLine($"Heightmap: {hm.Width}x{hm.Height} 16-bit, raw {lo}..{hi} " +
                              $"=> {t.HeightToMeters(lo):0.0}..{t.HeightToMeters(hi):0.0} m " +
                              $"(spacing {t.HorizontalSpacing:0.#} m/sample)");
            bool dimOk = hm.Width == t.MaterialSize;
            Console.WriteLine($"  grid side == materialSize: {(dimOk ? "OK" : "FAIL")}");
            ok &= dimOk;
        }
    }

    // --- StaticObjects.con round-trip ---
    var so = StaticObjectsFile.Load(soPath);
    Console.WriteLine($"StaticObjects: parsed {so.Objects.Count} objects.");

    // Re-serialize, re-parse, and confirm structural equality (lossless round-trip).
    var rewritten = so.Write().ToList();
    var reparsed = StaticObjectsFile.Parse(rewritten);
    bool countOk = reparsed.Objects.Count == so.Objects.Count;
    bool dataOk = true;
    for (int i = 0; i < so.Objects.Count && dataOk; i++)
    {
        var a = so.Objects[i]; var b = reparsed.Objects[i];
        if (a.Template != b.Template || a.Position != b.Position ||
            a.Rotation != b.Rotation || a.Scale != b.Scale ||
            a.ExtraLines.Count != b.ExtraLines.Count)
            dataOk = false;
    }
    Console.WriteLine($"  round-trip: count {(countOk ? "OK" : "FAIL")}, per-object data {(dataOk ? "OK" : "FAIL")}");

    // Verbatim: original coordinate text must survive a save unchanged (no map mangling).
    var rewrittenSet = new HashSet<string>(rewritten);
    int withSrc = so.Objects.Count(o => o.PositionSource is not null);
    int survived = so.Objects.Count(o => o.PositionSource is not null &&
                                         rewrittenSet.Contains($"object.absolutePosition {o.PositionSource}"));
    bool verbatim = withSrc > 0 && survived == withSrc;
    Console.WriteLine($"  verbatim coords preserved: {survived}/{withSrc} {(verbatim ? "OK" : "FAIL")}");
    ok &= verbatim;

    // Scientific-notation rotation preserved? (real exports use e.g. 9.88312e-006)
    var sci = so.Objects.FirstOrDefault(o => o.Rotation.Z != 0 && MathF.Abs(o.Rotation.Z) < 1e-3f);
    if (sci is not null)
        Console.WriteLine($"  sci-notation rotation parsed: {sci.Rotation.Z} (Z) -> OK");

    ok &= countOk && dataOk;
    Console.WriteLine();
    Console.WriteLine(ok ? "REAL-MAP VALIDATION PASSED." : "REAL-MAP VALIDATION FAILED.");
    return ok ? 0 : 1;
}

// ---------------------------------------------------------------------------
static int SyntheticDemo(string outDir)
{
    Directory.CreateDirectory(outDir);
    Console.WriteLine("RefractorForge — synthetic no-limits demo");
    Console.WriteLine("=========================================");

    // 8 km map: worldSize 8192, materialSize 2048 (the size Battlecraft can't make).
    var t = new TerrainConfig { MaterialSize = 2048, WorldSize = 8192, YScale = 0.5f, WaterLevel = 30 };
    var level = new Level { Name = "huge_test", WorldSize = t.WorldSize, Terrain = t };
    Console.WriteLine($"worldSize={t.WorldSize}, materialSize={t.MaterialSize} (4:1), no caps.");

    var sw = Stopwatch.StartNew();
    level.Heightmap = HeightmapGenerator.DiamondSquare(t.MaterialSize, seed: 2026, roughness: 0.55f);
    sw.Stop();
    var rawPath = Path.Combine(outDir, "Heightmap.raw");
    level.Heightmap.SaveRaw(rawPath);
    Console.WriteLine($"Generated {level.Heightmap.Width}x{level.Heightmap.Height} heightmap in {sw.ElapsedMilliseconds} ms " +
                      $"-> {new FileInfo(rawPath).Length:n0} bytes (grid==materialSize: {level.Heightmap.Width == t.MaterialSize}).");

    const int N = 5000;
    var rng = new Random(2026);
    string[] tpl = { "palmtree_group", "hut_01", "sandbag_wall", "barrel", "rock_med", "fence_segment" };
    for (int i = 0; i < N; i++)
    {
        var o = new StaticObject(tpl[i % tpl.Length])
        {
            Position = new Vec3(rng.NextSingle() * t.WorldSize, rng.NextSingle() * 40f, rng.NextSingle() * t.WorldSize),
            Rotation = new Vec3(rng.NextSingle() * 360f, 0, 0),
            Layer = 1,
        };
        level.StaticObjects.Objects.Add(o);
    }
    var conPath = Path.Combine(outDir, "StaticObjects.con");
    level.StaticObjects.Save(conPath);
    var reparsed = StaticObjectsFile.Load(conPath);
    bool ok = reparsed.Objects.Count == N;
    Console.WriteLine($"Wrote+reparsed {reparsed.Objects.Count:n0} objects: {(ok ? "OK" : "FAIL")}.");
    Console.WriteLine();
    Console.WriteLine(ok ? $"SYNTHETIC DEMO PASSED — {N:n0} objects, 8 km terrain." : "FAILED.");
    return ok ? 0 : 1;
}

// ---------------------------------------------------------------------------
static string? FindFile(string dir, string name) =>
    Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault();

// ---------------------------------------------------------------------------
static int EditTests(string levelDir)
{
    Console.WriteLine("RefractorForge — editing engine + collaboration");
    Console.WriteLine("===============================================");
    bool ok = true;

    var soPath = FindFile(levelDir, "StaticObjects.con")!;
    var cfgPath = FindFile(levelDir, "Terrain.con");
    var cfg = cfgPath is not null ? TerrainConfig.Load(cfgPath) : new TerrainConfig { WorldSize = 2048 };

    // --- 1) Picking math: project 3 separated objects, then click their pixels back. ---
    var pts = new List<Vector3>
    {
        new(300, 40, 300), new(1700, 60, 400), new(1000, 30, 1600)
    };
    var cam = Camera.FrameAerial(cfg.WorldSize, 45f, 16f / 9f);
    int W = 1280, H = 720;
    var vp = cam.ViewProjection;
    bool pickOk = true;
    for (int i = 0; i < pts.Count; i++)
    {
        var c = Vector4.Transform(new Vector4(pts[i], 1f), vp);
        float sx = (c.X / c.W * 0.5f + 0.5f) * W;
        float sy = (1f - (c.Y / c.W * 0.5f + 0.5f)) * H;
        var ray = Picking.ScreenToRay(cam, sx, sy, W, H);
        int hit = Picking.PickNearest(ray, pts, radius: 25f);
        if (hit != i) pickOk = false;
    }
    Console.WriteLine($"1. Ray-pick (project -> click -> pick): {(pickOk ? "OK" : "FAIL")}");
    ok &= pickOk;

    // --- 2) Edit + undo/redo on the real object list. ---
    var so = StaticObjectsFile.Load(soPath);
    var hist = new EditHistory(so);
    var target = so.Objects[0];
    var orig = target.Position;
    var moved = new Vec3(orig.X + 10, orig.Y, orig.Z + 10);
    hist.Do(new MoveObject(target.Id, moved));
    bool didMove = so.FindById(target.Id)!.Position == moved;
    hist.Undo(); bool undoOk = so.FindById(target.Id)!.Position == orig;
    hist.Redo(); bool redoOk = so.FindById(target.Id)!.Position == moved;
    Console.WriteLine($"2. Move + undo + redo: move {(didMove ? "OK" : "FAIL")}, undo {(undoOk ? "OK" : "FAIL")}, redo {(redoOk ? "OK" : "FAIL")}");
    ok &= didMove && undoOk && redoOk;

    // --- 3) Edit then save: edited object reformats, untouched objects stay verbatim. ---
    var so2 = StaticObjectsFile.Load(soPath);
    string untouchedSrc = so2.Objects[5].PositionSource!;
    new EditHistory(so2).Do(new MoveObject(so2.Objects[0].Id, new Vec3(123.5f, 45f, 678.25f)));
    string tmp = Path.Combine(Path.GetTempPath(), "edited_StaticObjects.con");
    so2.Save(tmp);
    var savedLines = File.ReadAllLines(tmp);
    bool untouchedKept = savedLines.Contains($"object.absolutePosition {untouchedSrc}");
    bool editApplied = savedLines.Any(l => l.StartsWith("object.absolutePosition 123.5/45/678.25"));
    bool countSame = StaticObjectsFile.Load(tmp).Objects.Count == so2.Objects.Count;
    Console.WriteLine($"3. Save: edited line written {(editApplied ? "OK" : "FAIL")}, untouched verbatim {(untouchedKept ? "OK" : "FAIL")}, count {(countSame ? "OK" : "FAIL")}");
    ok &= untouchedKept && editApplied && countSame;

    // --- 4) Collaboration: replay a wire command stream onto a 2nd copy; both converge. ---
    var A = StaticObjectsFile.Load(soPath);
    var B = A.Clone();                              // a joining collaborator receives this state
    var histA = new EditHistory(A);
    var commands = new IEditCommand[]
    {
        new MoveObject(A.Objects[0].Id, new Vec3(A.Objects[0].Position.X + 5, A.Objects[0].Position.Y + 1, A.Objects[0].Position.Z + 5)),
        new RotateObject(A.Objects[1].Id, new Vec3(45, 0, 0)),
        new AddObject("userB-0001", "sandbag_wall", new Vec3(500, 35, 500), Vec3.Zero),
        new DeleteObject(A.Objects[2].Id),
        new ScaleObject(A.Objects[3].Id, 1.5f),
    };
    Console.WriteLine("4. Collaboration — broadcasting edit stream:");
    foreach (var cmd in commands)
    {
        histA.Do(cmd);                              // user A's local edit
        string wire = cmd.ToWire();                 // ... serialized to the wire ...
        Console.WriteLine($"     -> {wire}");
        EditWire.Parse(wire).Apply(B);              // ... replayed on user B
    }
    bool converged = Converged(A, B);
    Console.WriteLine($"   A and B converged: {converged} ({A.Objects.Count} objects each)");
    ok &= converged;

    Console.WriteLine();
    Console.WriteLine(ok ? "EDITING + COLLABORATION TESTS PASSED." : "SOME TESTS FAILED.");
    return ok ? 0 : 1;

    static bool Converged(StaticObjectsFile a, StaticObjectsFile b)
    {
        if (a.Objects.Count != b.Objects.Count) return false;
        string Sig(StaticObjectsFile f) => string.Join("|",
            f.Objects.OrderBy(o => o.Id).Select(o => $"{o.Id}:{o.Template}:{o.Position}:{o.Rotation}:{o.Scale}"));
        return Sig(a) == Sig(b);
    }
}
