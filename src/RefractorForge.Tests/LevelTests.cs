using RefractorForge.Formats;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

public class LevelTests
{
    static bool Near(float a, float b) => MathF.Abs(a - b) < 1e-3f;

    [Fact]
    public void NewLevel_creates_and_roundtrips_folder()
    {
        string outDir = Path.Combine(Path.GetTempPath(), "rf_newlevel_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string FindIn(string d, string n) => Directory.EnumerateFiles(d, n, SearchOption.AllDirectories).First();

            // Map 1: flat, custom dimensions + env
            {
                var dir = Path.Combine(outDir, "FlatTest");
                var cfg = new TerrainConfig { MaterialSize = 256, WorldSize = 1024, YScale = 0.5f, WaterLevel = 20, SeaFloorLevel = 0, WaveHeight = 1f };
                ushort flatRaw = cfg.MetersToRaw(25f);
                var hm = HeightmapGenerator.Flat(cfg.MaterialSize, flatRaw);
                var env = new EnvironmentSettings { SunDirection = new Vec3(0.64f, 0.34f, -0.68f), SkyRotationAngle = -45f, FogEnabled = true };
                LevelSaver.CreateNewLevel(dir, "FlatTest", cfg, hm, env);

                var rc = TerrainConfig.Load(FindIn(dir, "Terrain.con"));
                Assert.True(rc.MaterialSize == 256, $"materialSize 256 (got {rc.MaterialSize})");
                Assert.True(rc.WorldSize == 1024, $"worldSize 1024 (got {rc.WorldSize})");
                Assert.True(Near(rc.YScale, 0.5f), $"yScale 0.5 (got {rc.YScale})");
                Assert.True(Near(rc.WaterLevel, 20f), $"waterLevel 20 (got {rc.WaterLevel})");
                Assert.True(Near(rc.WaveHeight, 1f), $"waveHeight 1 (got {rc.WaveHeight})");

                var rhm = Heightmap.LoadForMaterialSize(FindIn(dir, "Heightmap.raw"), rc.MaterialSize);
                Assert.True(rhm.Width == 256 && rhm.Height == 256, $"heightmap 256^2 (got {rhm.Width}x{rhm.Height})");
                Assert.True(rhm[10, 10] == flatRaw, $"flat sample == 25m raw");

                var rso = StaticObjectsFile.Load(FindIn(dir, "StaticObjects.con"));
                Assert.True(rso.Objects.Count == 0, "StaticObjects empty");

                var renv = EnvironmentSettings.LoadFolder(dir);
                Assert.True(Near(renv.SunDirection.X, 0.64f) && Near(renv.SunDirection.Z, -0.68f), "sun dir round-trip");
                Assert.True(renv.FogEnabled, "fog enabled round-trip");
                Assert.True(Near(renv.SkyRotationAngle, -45f), "sky rotAngle -45");

                var rgp = GameplayObjects.LoadFolder(dir);
                Assert.True(rgp.ControlPoints.Count == 0 && rgp.VehicleSpawns.Count == 0 && rgp.SoldierSpawns.Count == 0, "gameplay loads empty");
                var rmesh = TerrainMesh.FromHeightmap(rhm, rc, 1);
                Assert.True(rmesh.Positions.Length > 0 && rmesh.Indices.Length > 0, "terrain mesh builds");
            }

            // Map 2: diamond-square byte-exact
            {
                var dir = Path.Combine(outDir, "FractalTest");
                var cfg = new TerrainConfig { MaterialSize = 512, WorldSize = 2048, YScale = 0.35f, WaterLevel = 30 };
                var hm = HeightmapGenerator.DiamondSquare(cfg.MaterialSize, seed: 7, roughness: 0.55f, min: 0, max: 20000);
                LevelSaver.CreateNewLevel(dir, "FractalTest", cfg, hm, new EnvironmentSettings());
                var rc = TerrainConfig.Load(FindIn(dir, "Terrain.con"));
                var rhm = Heightmap.LoadForMaterialSize(FindIn(dir, "Heightmap.raw"), rc.MaterialSize);
                Assert.True(rc.MaterialSize == 512 && rc.WorldSize == 2048, "fractal cfg 512/2048");
                bool same = hm.Samples.Length == rhm.Samples.Length;
                for (int i = 0; i < hm.Samples.Length && same; i++) if (hm.Samples[i] != rhm.Samples[i]) same = false;
                Assert.True(same, "fractal heightmap byte-exact round-trip");
            }

            // Map 3: playable Conquest map
            {
                var dir = Path.Combine(outDir, "PlayableTest");
                var cfg = new TerrainConfig { MaterialSize = 256, WorldSize = 1024, YScale = 0.5f, WaterLevel = 20 };
                var hm = HeightmapGenerator.Flat(cfg.MaterialSize, cfg.MetersToRaw(30f));
                LevelSaver.CreateNewLevel(dir, "PlayableTest", cfg, hm, new EnvironmentSettings(), playable: true);

                var gp = GameplayObjects.LoadFolder(dir);
                Assert.True(gp.ControlPoints.Count == 3, $"3 control points generated (got {gp.ControlPoints.Count})");
                Assert.True(gp.SoldierSpawns.Count == 12, $"12 soldier spawns generated (got {gp.SoldierSpawns.Count})");

                var cpt = File.ReadAllText(Path.Combine(dir, "Conquest", "ControlPointTemplates.con"));
                Assert.True(cpt.Contains("ObjectTemplate.team 2") && cpt.Contains("ObjectTemplate.team 1") && cpt.Contains("ObjectTemplate.team 0"), "US/NVA/neutral CP teams present");
                var initTxt = File.ReadAllText(FindIn(dir, "Init.con"));
                Assert.True(initTxt.Contains("game.setKit 2 0 USArmy_Recon") && initTxt.Contains("setBeforeSpawnCameraPosition"), "Init.con carries kits + cameras");
                Assert.True(File.Exists(Path.Combine(dir, "Conquest.con")) && File.Exists(Path.Combine(dir, "GameTypes", "Conquest.con")), "Conquest.con files written");

                var aip = Path.Combine(dir, "AIpathFinding.con");
                var aiTxt = File.Exists(aip) ? File.ReadAllText(aip) : "";
                int searchMaps = System.Text.RegularExpressions.Regex.Matches(aiTxt, @"ai\.addSearchMap").Count;
                Assert.True(searchMaps == 7, $"AIpathFinding.con has 7 search maps (got {searchMaps})");

                var navDir = Path.Combine(dir, "Pathfinding");
                var navFiles = Directory.Exists(navDir) ? Directory.GetFiles(navDir) : Array.Empty<string>();
                int navEight = navFiles.Count(f => f.EndsWith("Map8Bit.raw"));
                int navComp = navFiles.Count(f => f.EndsWith("Map.raw") && !f.EndsWith("Map8Bit.raw"));
                Assert.True(navEight == 21, $"21 editor 8Bit navmaps written (got {navEight})");
                Assert.True(navComp == navEight && navComp == 21, $"21 engine compressed navmaps written (got {navComp})");
            }
        }
        finally { try { Directory.Delete(outDir, true); } catch { } }
    }

    [Fact]
    public void Heightmap_io_roundtrip()
    {
        var src = new Heightmap(64, 64);
        for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) src[x, y] = (ushort)((x + y) * 500);

        var bytes = src.ToBytes();
        Assert.True(bytes.Length == 64 * 64 * 2, $"ToBytes length 64^2*2 (got {bytes.Length})");
        var rt = Heightmap.FromBytes(bytes, 64, 64);
        bool exact = true; for (int i = 0; i < src.Samples.Length && exact; i++) if (src.Samples[i] != rt.Samples[i]) exact = false;
        Assert.True(exact, "raw byte round-trip exact");

        string tmp = Path.Combine(Path.GetTempPath(), "rf_heightmapio.raw");
        try
        {
            src.SaveRaw(tmp);
            var square = Heightmap.LoadRawSquare(tmp);
            Assert.True(square.Width == 64 && square.Height == 64, $"LoadRawSquare inferred 64^2");

            var same = src.Resample(64, 64);
            bool idEq = true; for (int i = 0; i < src.Samples.Length && idEq; i++) if (src.Samples[i] != same.Samples[i]) idEq = false;
            Assert.True(idEq, "identity resample exact");

            var up = src.Resample(128, 128);
            Assert.True(up.Width == 128 && up[0, 0] == src[0, 0] && up[127, 127] == src[63, 63], "upsample keeps corners");
            var down = up.Resample(64, 64);
            int maxErr = 0; for (int i = 0; i < src.Samples.Length; i++) maxErr = Math.Max(maxErr, Math.Abs(src.Samples[i] - down.Samples[i]));
            Assert.True(maxErr <= 1, $"linear field survives up/down resample (max err {maxErr})");

            int materialSize = 64;
            var imported = Heightmap.LoadRawSquare(tmp);
            if (imported.Width != materialSize) imported = imported.Resample(materialSize, materialSize);
            var live = new Heightmap(materialSize, materialSize);
            var liveRef = live.Samples;
            live.CopyFrom(imported);
            Assert.True(ReferenceEquals(live.Samples, liveRef), "CopyFrom is in place");
            bool copied = true; for (int i = 0; i < live.Samples.Length && copied; i++) if (live.Samples[i] != imported.Samples[i]) copied = false;
            Assert.True(copied, "CopyFrom overwrote samples");

            bool threw = false; try { live.CopyFrom(up); } catch (ArgumentException) { threw = true; }
            Assert.True(threw, "CopyFrom rejects dimension mismatch");
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void SpawnLinks_owning_controlpoint_index()
    {
        var cps = new List<ControlPointDef>
        {
            new("US_base",  new Vec3(100, 0, 100), 30f, 1, 2, 25, 40, "US_base", 1),
            new("NVA_base", new Vec3(900, 0, 900), 25f, 2, 1, 25, 40, "NVA_base", 2),
        };
        Assert.True(GameplayObjects.OwningControlPointIndex(cps, new Vec3(120, 0, 120), 2, false) == 1, "soldier group 2 -> NVA_base by id");
        Assert.True(GameplayObjects.OwningControlPointIndex(cps, new Vec3(880, 0, 880), 1, true)  == 0, "vehicle OSId 1 -> US_base by id");
        Assert.True(GameplayObjects.OwningControlPointIndex(cps, new Vec3(110, 0, 110), 0, false) == 0, "id 0 -> nearest (US_base)");
        Assert.True(GameplayObjects.OwningControlPointIndex(cps, new Vec3(905, 0, 880), 0, true)  == 1, "id 0 -> nearest (NVA_base)");
        Assert.True(GameplayObjects.OwningControlPointIndex(cps, new Vec3(905, 0, 880), 77, true) == 1, "unclaimed id -> nearest fallback");
        Assert.True(GameplayObjects.OwningControlPointIndex(new List<ControlPointDef>(), Vec3.Zero, 1, true) == -1, "no flags -> -1");
    }

    [Fact]
    public void WaterPatch_only_changes_water_line()
    {
        var cfg = new TerrainConfig { MaterialSize = 512, WorldSize = 2048, YScale = 0.35f, WaterLevel = 30f, SeaFloorLevel = 0f, WaveHeight = 1f };
        var original = cfg.ToTerrainConLines(@"BfVietnam\levels\Test").ToList();
        cfg.WaterLevel = 47.5f;
        var patched = cfg.PatchConLines(original).ToList();

        Assert.True(patched.Count == original.Count, $"line count unchanged ({original.Count})");
        var rc = TerrainConfig.Parse(patched);
        Assert.True(Math.Abs(rc.WaterLevel - 47.5f) < 1e-3f, $"waterLevel patched to 47.5 (got {rc.WaterLevel})");
        Assert.True(rc.WorldSize == 2048 && rc.MaterialSize == 512 && Math.Abs(rc.YScale - 0.35f) < 1e-3f, "worldSize/materialSize/yScale preserved");
        int changed = 0; for (int i = 0; i < original.Count; i++) if (original[i] != patched[i]) changed++;
        Assert.True(changed == 1, $"exactly ONE line changed (got {changed})");
        Assert.True(patched.Any(l => l.Contains("GeometryTemplate.waterLevel 47.5")), "patched line has correct text");
    }

    [Fact]
    public void Rfa_merge_patch_overrides_base()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "rf_rfamerge_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var baseDir = Path.Combine(tmp, "base");
            var cfg = new TerrainConfig { MaterialSize = 128, WorldSize = 512, YScale = 0.5f };
            LevelSaver.CreateNewLevel(baseDir, "MergeTest", cfg, HeightmapGenerator.Flat(128, cfg.MetersToRaw(20f)), new EnvironmentSettings());
            var baseRfa = Path.Combine(tmp, "base.rfa"); LevelSaver.PackFolder(baseDir, baseRfa);

            var patchDir = Path.Combine(tmp, "patch"); Directory.CreateDirectory(patchDir);
            File.WriteAllText(Path.Combine(patchDir, "StaticObjects.con"),
                "object.create o_patchtest\nobject.absolutePosition 10/5/20\nobject.rotation 0/0/0\n");
            var patchRfa = Path.Combine(tmp, "patch.rfa"); LevelSaver.PackFolder(patchDir, patchRfa);

            var single = LevelArchive.FromRfa(baseRfa);
            Assert.True(single.StaticObjects.Objects.Count == 0, "base alone: 0 static objects");
            var merged = LevelArchive.FromRfa(baseRfa, patchRfa);
            Assert.True(merged.StaticObjects.Objects.Count == 1 && merged.StaticObjects.Objects[0].Template == "o_patchtest", "patch overrides StaticObjects.con");
            Assert.True(merged.Config.MaterialSize == 128 && merged.Config.WorldSize == 512, "terrain still loads from base archive");
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }
}
