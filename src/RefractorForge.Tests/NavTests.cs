using RefractorForge.Formats;
using RefractorForge.Formats.Rfa;
using RefractorForge.Formats.Terrain;
using Xunit;

namespace RefractorForge.Tests;

public class NavTests
{
    static (Heightmap hm, TerrainConfig cfg) MakeSyntheticMap(int ms = 64, int ws = 256)
    {
        var cfg = new TerrainConfig { MaterialSize = ms, WorldSize = ws, YScale = 1f, WaterLevel = 30f };
        var hm = new Heightmap(ms, ms);
        for (int row = 0; row < ms; row++)
            for (int col = 0; col < ms; col++)
            {
                float h = col < ms / 3 ? 20f : col < 2 * ms / 3 ? 50f : 50f + (col - 2 * ms / 3) * 4f;
                hm[col, row] = cfg.MetersToRaw(h);
            }
        return (hm, cfg);
    }

    [Fact]
    public void Navmap_generation_vehicles_structure_and_folder_roundtrip()
    {
        var (hm, cfg) = MakeSyntheticMap();
        int ms = 64, ws = 256;

        int L = 2, side = SearchMapGenerator.LevelSide(ms, L);
        float mpc = (float)ws / side;
        SearchMapParams P(string n) => SearchMapParams.Standard.First(s => s.Name.StartsWith(n));
        (int pass, int wetPass, int dryPass) Scan(SearchMapParams p)
        {
            var d = SearchMapGenerator.Generate(cfg, hm, p, L);
            int pass = 0, wet = 0, dry = 0;
            for (int y = 0; y < side; y++) for (int x = 0; x < side; x++)
                if (d[y * side + x] == 0x00)
                {
                    pass++;
                    var (gx, gy) = SearchMapGenerator.GridForNav(x, y, side);
                    if (SearchMapGenerator.SampleHeight(cfg, hm, (gx + 0.5f) * mpc, (gy + 0.5f) * mpc) < cfg.WaterLevel) wet++; else dry++;
                }
            return (pass, wet, dry);
        }

        var tank = Scan(P("Tank0"));
        Assert.True(tank.pass > 0 && tank.wetPass == 0, $"tank: passable cells exist, NONE underwater ({tank.wetPass} wet)");
        Assert.True(tank.pass < side * side / 2, $"tank: water+cliff removed (passable {100.0 * tank.pass / (side * side):0}%)");
        var boat = Scan(P("Boat2"));
        Assert.True(boat.pass > 0 && boat.dryPass == 0, $"boat: passable cells exist, ALL in water ({boat.dryPass} dry)");
        var heli = Scan(P("Heli5"));
        Assert.True(heli.wetPass > 0, "heli: flies over water");
        Assert.True(heli.pass > tank.pass, $"heli passable ({heli.pass}) > tank passable ({tank.pass})");
        var amph = Scan(P("Amphibius"));
        Assert.True(amph.wetPass > 0 && amph.dryPass > 0, "amphibious: passable on water AND land");

        var hole = new[] { new ObjectFootprint(ws * 0.5f, ws * 0.5f, 12f, 5f) };
        var tNo = SearchMapGenerator.Generate(cfg, hm, P("Tank0"), L);
        var tYes = SearchMapGenerator.Generate(cfg, hm, P("Tank0"), L, hole);
        int blockedAdded = 0; for (int i = 0; i < tNo.Length; i++) if (tNo[i] == 0x00 && tYes[i] == 0xff) blockedAdded++;
        Assert.True(blockedAdded > 0, $"object footprint blocks tank cells ({blockedAdded} newly blocked)");

        var all = SearchMapGenerator.GenerateAll(cfg, hm);
        var eightFiles = all.Where(a => a.FileName.EndsWith("Map8Bit.raw")).ToList();
        var compFiles = all.Where(a => a.FileName.EndsWith("Map.raw") && !a.FileName.EndsWith("8Bit.raw")).ToList();
        Assert.True(eightFiles.Count > 0 && eightFiles.Count == compFiles.Count, $"equal 8Bit ({eightFiles.Count}) + compressed ({compFiles.Count}) files");
        bool sizesOk = true, binOk = true;
        foreach (var (file, data) in eightFiles)
        {
            int lvl = int.Parse(file.Substring(file.IndexOf("Level") + 5, 1));
            int exp = SearchMapGenerator.LevelSide(ms, lvl); exp *= exp;
            if (data.Length != exp) sizesOk = false;
            foreach (var b in data) if (b != 0x00 && b != 0xff) { binOk = false; break; }
        }
        Assert.True(sizesOk, "each 8Bit file is LevelSide^2 bytes");
        Assert.True(binOk, "every 8Bit byte is 0x00 or 0xFF");

        bool rtOk = true;
        foreach (var (cfile, cdata) in compFiles)
        {
            var match = eightFiles.FirstOrDefault(e => e.FileName == cfile.Replace("Map.raw", "Map8Bit.raw"));
            if (match.Data is null) { rtOk = false; continue; }
            var dec = CompressedSearchMap.Decode(cdata, out _, out _);
            if (dec.Length != match.Data.Length) { rtOk = false; continue; }
            for (int i = 0; i < dec.Length; i++) if (dec[i] != match.Data[i]) { rtOk = false; break; }
        }
        Assert.True(rtOk, "each compressed Map.raw decodes back to its 8Bit");
        Assert.True(eightFiles.Any(e => e.FileName == "Tank0Level0Map8Bit.raw") && eightFiles.Any(e => e.FileName == "Tank0Level2Map8Bit.raw"), "tank ships levels 0..2");
        Assert.True(eightFiles.Any(e => e.FileName == "Boat2Level2Map8Bit.raw") && !eightFiles.Any(e => e.FileName == "Boat2Level0Map8Bit.raw"), "boat starts at level 2");

        string tmp = Path.Combine(Path.GetTempPath(), "rf_navgen_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            int written = SearchMapGenerator.WriteFolder(tmp, cfg, hm);
            Assert.True(written == all.Count, $"WriteFolder wrote all {all.Count} files (got {written})");
            Assert.True(File.Exists(Path.Combine(tmp, "Pathfinding", "Tank0Level2Map.raw")) && File.Exists(Path.Combine(tmp, "Pathfinding", "Tank0Level2Map8Bit.raw")), "Pathfinding/ has both forms for Tank0Level2");
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    [Fact]
    public void Navmap_paint_encode_decode_and_rfa_patch_roundtrip()
    {
        var (hm, cfg) = MakeSyntheticMap();
        for (int row = 0; row < 64; row++) for (int col = 0; col < 64; col++) hm[col, row] = cfg.MetersToRaw(50f);

        var tank = SearchMapParams.Standard.First(s => s.Name == "Tank0");
        int finest = SearchMapGenerator.FinestSide(64);
        var grid = SearchMapGenerator.GenerateGrid(cfg, hm, tank, 0);
        Assert.True(grid.Length == finest * finest, $"world-grid finest map is {finest}^2 bytes");
        int blocked0 = grid.Count(b => b == 0xFF);

        int x0 = finest / 4, y0 = finest / 4, sq = finest / 8;
        for (int y = y0; y < y0 + sq; y++) for (int x = x0; x < x0 + sq; x++) grid[y * finest + x] = 0xFF;
        Assert.True(grid.Count(b => b == 0xFF) == blocked0 + sq * sq, $"painting added {sq * sq} blocked cells");

        var files = SearchMapGenerator.EncodeVehicleLevels(tank, grid, finest);
        var eight = files.Where(f => f.FileName.EndsWith("Map8Bit.raw")).ToList();
        var comp = files.Where(f => f.FileName.EndsWith("Map.raw") && !f.FileName.EndsWith("8Bit.raw")).ToList();
        Assert.True(eight.Count == 3 && comp.Count == 3, $"tank ships 3 levels (got {eight.Count}/{comp.Count})");

        bool rt = true;
        foreach (var (cf, cd) in comp)
        {
            var m = eight.First(e => e.FileName == cf.Replace("Map.raw", "Map8Bit.raw"));
            if (!CompressedSearchMap.Decode(cd, out _, out _).SequenceEqual(m.Data)) rt = false;
        }
        Assert.True(rt, "every compressed level decodes back to its 8Bit");

        var l0 = eight.First(e => e.FileName == "Tank0Level0Map8Bit.raw").Data;
        Assert.True(l0.Count(b => b == 0xFF) == grid.Count(b => b == 0xFF), "L0 nav-oriented 8Bit preserves painted blocked count");

        int l2side = finest >> 2, f2 = finest / l2side;
        var grid2 = SearchMapGenerator.DownsampleBlocked(grid, finest, l2side);
        int cxr = (x0 + sq / 2) / f2, cyr = (y0 + sq / 2) / f2;
        Assert.True(grid2[cyr * l2side + cxr] == 0xFF, "painted region stays blocked after conservative downsample");

        string tmpDir = Path.Combine(Path.GetTempPath(), "rf_navpaint_" + Guid.NewGuid().ToString("N")[..8]);
        string navBase = Path.Combine(Path.GetTempPath(), "rf_navpatch_base_" + Guid.NewGuid().ToString("N")[..8] + ".rfa");
        string navPatch = Path.Combine(Path.GetTempPath(), "rf_navpatch_001_" + Guid.NewGuid().ToString("N")[..8] + ".rfa");
        try
        {
            int n = SearchMapGenerator.WriteVehicleEditedFolder(tmpDir, tank, grid, finest);
            // 3 levels x (8Bit + compressed) = 6, plus the strategic companion pair rebuilt from the same grid,
            // so the coarse search layer never describes the pre-edit terrain.
            Assert.True(n == 8, $"WriteVehicleEditedFolder wrote 8 files (got {n})");
            Assert.True(File.Exists(Path.Combine(tmpDir, "Pathfinding", "Tank.raw")), "companion Tank.raw written");
            Assert.True(File.Exists(Path.Combine(tmpDir, "Pathfinding", "TankInfo.raw")), "companion TankInfo.raw written");
            var wc = File.ReadAllBytes(Path.Combine(tmpDir, "Pathfinding", "Tank0Level0Map.raw"));
            var we = File.ReadAllBytes(Path.Combine(tmpDir, "Pathfinding", "Tank0Level0Map8Bit.raw"));
            Assert.True(CompressedSearchMap.Decode(wc, out _, out _).SequenceEqual(we), "written compressed L0 decodes to written 8Bit");

            var baseNav = SearchMapGenerator.EncodeVehicleLevels(tank, SearchMapGenerator.GenerateGrid(cfg, hm, tank, 0), finest);
            const string navPrefix = "BfVietnam/levels/RFNav/Pathfinding/";
            var baseEntries = baseNav.Select(f => (navPrefix + f.FileName, f.Data)).ToList();
            RefractorFlatArchive.WriteFile(navBase, baseEntries, compress: true, xPackId: XPackId.Default);
            var editedNav = SearchMapGenerator.EncodeVehicleLevels(tank, grid, finest);
            var navNames = LevelSaver.WritePatchRfa(navBase, navPatch, null, null, null, null, extraFiles: editedNav);
            Assert.True(navNames.Count == editedNav.Count, $"every edited navmap matched a base Pathfinding entry ({navNames.Count}/{editedNav.Count})");
            var navPatchA = new RefractorFlatArchive(navPatch);
            var nl0e = navPatchA.Entries.FirstOrDefault(e => e.Name.EndsWith("Tank0Level0Map.raw", StringComparison.OrdinalIgnoreCase) && !e.Name.EndsWith("Map8Bit.raw", StringComparison.OrdinalIgnoreCase));
            var wantL0 = editedNav.First(f => f.FileName == "Tank0Level0Map.raw").Data;
            Assert.True(nl0e is not null && navPatchA.Read(nl0e).SequenceEqual(wantL0), "patch Tank0Level0Map.raw is the edited compressed navmap");
        }
        finally
        {
            try { File.Delete(navBase); } catch { }
            try { File.Delete(navPatch); } catch { }
            try { Directory.Delete(tmpDir, true); } catch { }
        }
    }

    [Fact]
    public void Pathmap_raw_loads_both_forms_and_unrotates()
    {
        // A synthetic painted world-grid map (vertical blocked stripes). Encode to BOTH on-disk forms, load them
        // back via PathmapRaw + UnemitNav, and confirm the world grid is recovered exactly. Guards the native
        // pathmap viewer (Open .raw + post-save preview) against an orientation/format regression.
        int side = 128;
        var world = new byte[side * side];
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
                world[y * side + x] = (x / 16 % 2 == 0) ? (byte)0x00 : (byte)0xFF;

        var nav = SearchMapGenerator.EmitNav(world, side);   // on-disk 8Bit (nav-oriented)

        var eight = PathmapRaw.Load(nav, "Tank0Level0Map8Bit.raw", out int s8);
        Assert.True(s8 == side && SearchMapGenerator.UnemitNav(eight, s8).SequenceEqual(world), "8Bit .raw round-trips to the world grid");

        var comp = CompressedSearchMap.Encode(nav, side, 0);
        var dec = PathmapRaw.Load(comp, "Tank0Level0Map.raw", out int sc);
        Assert.True(sc == side && SearchMapGenerator.UnemitNav(dec, sc).SequenceEqual(world), "compressed .raw round-trips to the world grid");
    }

    [Fact]
    public void LoadVehicleWorldGrid_prefers_the_levels_existing_maps()
    {
        // The editor must seed the AI Path painter from the navmap the level SHIPS (designer intent), not a fresh
        // terrain generation. Simulated level storage = a leaf-name dictionary, as the Viewer provides.
        int side = 128;
        var world = new byte[side * side];
        for (int i = 0; i < world.Length; i++) world[i] = (i % 5 == 0) ? (byte)0xFF : (byte)0x00;
        var nav = SearchMapGenerator.EmitNav(world, side);
        var tank = SearchMapParams.Standard[0];   // Tank0, levels {0,1,2}
        var boat = SearchMapParams.Standard[2];   // Boat2, levels {2,3,4,5}

        // 8Bit form present at the finest level -> loaded exactly
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        { [$"Tank0Level0Map8Bit.raw"] = nav };
        var got = PathmapRaw.LoadVehicleWorldGrid(n => files.GetValueOrDefault(n), tank, side);
        Assert.NotNull(got);
        Assert.True(got!.SequenceEqual(world), "8Bit map loads back as the exact world grid");

        // only the COMPRESSED engine form present -> decoded via CompressedSearchMap
        files = new(StringComparer.OrdinalIgnoreCase) { ["Tank0Level0Map.raw"] = CompressedSearchMap.Encode(nav, side, 0) };
        got = PathmapRaw.LoadVehicleWorldGrid(n => files.GetValueOrDefault(n), tank, side);
        Assert.True(got is not null && got.SequenceEqual(world), "compressed-only levels load too");

        // water vehicle shipping only its coarser L2 -> upsampled to the paint grid
        files = new(StringComparer.OrdinalIgnoreCase) { ["Boat2Level2Map8Bit.raw"] = nav };
        got = PathmapRaw.LoadVehicleWorldGrid(n => files.GetValueOrDefault(n), boat, side * 4);
        Assert.True(got is not null && got.Length == side * 4 * side * 4, "coarser map upsamples to the target side");
        Assert.Equal(0xFF, got![0]);   // first blocked cell survives the upsample

        // nothing in the level -> null (caller falls back to generation)
        Assert.Null(PathmapRaw.LoadVehicleWorldGrid(_ => null, tank, side));

        // a corrupt file is skipped, not fatal
        files = new(StringComparer.OrdinalIgnoreCase) { ["Tank0Level0Map8Bit.raw"] = new byte[123] };
        Assert.Null(PathmapRaw.LoadVehicleWorldGrid(n => files.GetValueOrDefault(n), tank, side));
    }
}
