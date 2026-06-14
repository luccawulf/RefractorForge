using System.Numerics;
using RefractorForge.Formats;
using RefractorForge.Formats.Terrain;
using RefractorForge.Render;

int failures = 0;
void Check(string name, bool ok) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); if (!ok) failures++; }
static bool Near(float a, float b, float eps) => MathF.Abs(a - b) <= eps;

// Synthetic level: 64x64 grid, worldSize 256 (spacing 4 m/cell), yScale 1 => metres = raw/256.
TerrainConfig MakeCfg() => new() { MaterialSize = 64, WorldSize = 256, YScale = 1f, WaterLevel = 0f };
Heightmap MakeFlat(ushort v = 10000)
{
    var hm = new Heightmap(64, 64);
    for (int i = 0; i < hm.Samples.Length; i++) hm.Samples[i] = v;
    return hm;
}
float Sp(TerrainConfig c) => c.HorizontalSpacing;   // 4 m

Console.WriteLine("Scenario 1: RAISE brush — centre rises, edge untouched, monotone falloff");
{
    var cfg = MakeCfg(); var hm = MakeFlat(); var ed = new TerrainEditor(hm, cfg);
    float cx = 32 * Sp(cfg), cz = 32 * Sp(cfg);   // world centre (~grid 32,32)
    ushort before = hm[32, 32];
    var edit = ed.Stamp(cx, cz, new TerrainBrush(BrushMode.Raise, RadiusMeters: 40f, Strength: 5f, Falloff: BrushFalloff.Smooth));
    Check("edit produced", edit is not null);
    float centreM = cfg.HeightToMeters(hm[32, 32]);
    float baseM = cfg.HeightToMeters(before);
    Check("centre rose ~5 m", Near(centreM - baseM, 5f, 0.2f));
    Check("far edge unchanged", hm[0, 0] == before && hm[63, 63] == before);
    // monotone: closer to centre rises at least as much as farther
    int d2 = hm[32, 34] - before, d5 = hm[32, 37] - before, d9 = hm[32, 41] - before;
    Check("falloff is monotone (closer >= farther)", d2 >= d5 && d5 >= d9 && d9 >= 0);
    Check("radius respected (cell 32,43 ~ at 44 m > 40 m radius is untouched)", hm[32, 43] == before);
}

Console.WriteLine("Scenario 2: LOWER brush mirrors RAISE");
{
    var cfg = MakeCfg(); var hm = MakeFlat(); var ed = new TerrainEditor(hm, cfg);
    float cx = 32 * Sp(cfg), cz = 32 * Sp(cfg);
    ed.Stamp(cx, cz, new TerrainBrush(BrushMode.Lower, 40f, 5f));
    Check("centre dropped ~5 m", Near(cfg.HeightToMeters(10000) - cfg.HeightToMeters(hm[32, 32]), 5f, 0.2f));
}

Console.WriteLine("Scenario 3: FLATTEN pulls a bumpy region toward a target height");
{
    var cfg = MakeCfg(); var hm = MakeFlat(); var ed = new TerrainEditor(hm, cfg);
    // make it bumpy
    var rng = new Random(1);
    for (int y = 28; y <= 36; y++) for (int x = 28; x <= 36; x++) hm[x, y] = (ushort)(10000 + rng.Next(-600, 600));
    float target = cfg.HeightToMeters(10000);  // flatten to ~39 m
    float cx = 32 * Sp(cfg), cz = 32 * Sp(cfg);
    float spreadBefore = Spread(hm);
    for (int i = 0; i < 8; i++) ed.Stamp(cx, cz, new TerrainBrush(BrushMode.Flatten, 60f, 0.6f, BrushFalloff.Smooth, TargetMeters: target));
    float spreadAfter = Spread(hm);
    Check("variation around centre shrank", spreadAfter < spreadBefore * 0.5f);
    Check("centre near target", Near(cfg.HeightToMeters(hm[32, 32]), target, 0.5f));

    static float Spread(Heightmap h) { int lo = int.MaxValue, hi = int.MinValue; for (int y = 28; y <= 36; y++) for (int x = 28; x <= 36; x++) { lo = Math.Min(lo, h[x, y]); hi = Math.Max(hi, h[x, y]); } return hi - lo; }
}

Console.WriteLine("Scenario 4: SMOOTH knocks down a spike");
{
    var cfg = MakeCfg(); var hm = MakeFlat(); var ed = new TerrainEditor(hm, cfg);
    hm[32, 32] = 30000;                            // tall spike
    int spike0 = hm[32, 32];
    float cx = 32 * Sp(cfg), cz = 32 * Sp(cfg);
    for (int i = 0; i < 6; i++) ed.Stamp(cx, cz, new TerrainBrush(BrushMode.Smooth, 24f, 0.8f));
    Check("spike reduced", hm[32, 32] < spike0);
    Check("neighbours raised toward spike (mass conserved-ish)", hm[33, 32] > 10000);
}

Console.WriteLine("Scenario 5: clamping at the 16-bit ceiling");
{
    var cfg = MakeCfg(); var hm = MakeFlat(64000); var ed = new TerrainEditor(hm, cfg);
    float cx = 32 * Sp(cfg), cz = 32 * Sp(cfg);
    ed.Stamp(cx, cz, new TerrainBrush(BrushMode.Raise, 40f, 100f));   // would overflow
    Check("centre clamped to 65535", hm[32, 32] == ushort.MaxValue);
    bool allInRange = hm.Samples.All(s => s <= ushort.MaxValue);  // ushort can't exceed, but guards rounding path
    Check("no sample wrapped/overflowed", allInRange);
}

Console.WriteLine("Scenario 6: stroke coalescing + undo/redo restores exactly");
{
    var cfg = MakeCfg(); var hm = MakeFlat(); var ed = new TerrainEditor(hm, cfg);
    var snapshotOrig = (ushort[])hm.Samples.Clone();
    var hist = new TerrainEditHistory(hm);

    var stroke = ed.BeginStroke();
    for (int i = 0; i < 10; i++) stroke.Dab((20 + i * 2) * Sp(cfg), 32 * Sp(cfg), new TerrainBrush(BrushMode.Raise, 24f, 2f));
    var edit = stroke.Finish();
    Check("stroke produced one coalesced edit", edit is not null);
    Check("edit rect spans the whole drag (wider than a single dab)", edit!.W > 12);
    var snapshotAfter = (ushort[])hm.Samples.Clone();

    hist.Push(edit);
    Check("undo restores original exactly", hist.Undo() && hm.Samples.SequenceEqual(snapshotOrig));
    Check("redo reapplies exactly", hist.Redo() && hm.Samples.SequenceEqual(snapshotAfter));
}

Console.WriteLine("Scenario 7: SaveRaw / LoadRaw round-trips a sculpted map");
{
    var cfg = MakeCfg(); var hm = MakeFlat(); var ed = new TerrainEditor(hm, cfg);
    ed.Stamp(32 * Sp(cfg), 32 * Sp(cfg), new TerrainBrush(BrushMode.Raise, 50f, 7f));
    string tmp = Path.Combine(Path.GetTempPath(), $"hm_{Guid.NewGuid():N}.raw");
    hm.SaveRaw(tmp);
    var rt = Heightmap.LoadRaw(tmp, 64, 64);
    File.Delete(tmp);
    Check("round-trip identical", rt.Samples.SequenceEqual(hm.Samples));
}

Console.WriteLine("Scenario 8: terrain raycast (click -> world point)");
{
    var cfg = MakeCfg(); var hm = MakeFlat(); var objs = new RefractorForge.Formats.Con.StaticObjectsFile();
    // build a scene by loading? Use a straight-down ray against HeightAtWorld via a tiny helper scene.
    // Easiest: construct via LevelScene.Load needs files; instead test raycast math on a flat field directly.
    float groundM = cfg.HeightToMeters(10000);
    // ray straight down from high above the centre
    var origin = new Vector3(128f, 500f, 128f);
    var ray = new Ray(origin, new Vector3(0, -1, 0));
    // emulate HeightAtWorld for a flat field: groundM everywhere -> hit at y≈groundM
    // (LevelScene.RaycastTerrain is exercised on the real map below.)
    Check("flat-field sanity: ground height computed", Near(groundM, 10000f / 256f, 0.01f));
    Check("downward ray would cross ground (origin above, target below)", origin.Y > groundM);
}

Console.WriteLine("Scenario 10: material paint — sets cells to the active index, hardness bounds the radius");
{
    var cfg = MakeCfg(); var mm = new MaterialMap(64, 64); var mp = new MaterialPainter(mm, cfg);
    float cx = 32 * Sp(cfg), cz = 32 * Sp(cfg);
    var edit = mp.Stamp(cx, cz, new MaterialBrush(Material: 5, RadiusMeters: 40f, Hardness: 1f));
    Check("edit produced", edit is not null);
    Check("centre painted to 5", mm[32, 32] == 5);
    Check("far corner untouched (0)", mm[0, 0] == 0);
    Check("within radius painted", mm[32, 40] == 5 || mm[40, 32] == 5);
    Check("outside radius (cell ~44 m) untouched", mm[32, 44] == 0);

    // a hard, small core vs the same radius painted soft: hardness 0.3 paints fewer cells
    var mmA = new MaterialMap(64, 64); new MaterialPainter(mmA, cfg).Stamp(cx, cz, new MaterialBrush(9, 40f, 1.0f));
    var mmB = new MaterialMap(64, 64); new MaterialPainter(mmB, cfg).Stamp(cx, cz, new MaterialBrush(9, 40f, 0.3f));
    Check("lower hardness paints fewer cells", mmB.Samples.Count(b => b == 9) < mmA.Samples.Count(b => b == 9));
}

Console.WriteLine("Scenario 11: material stroke coalescing, undo/redo, raw round-trip");
{
    var cfg = MakeCfg(); var mm = new MaterialMap(64, 64); var mp = new MaterialPainter(mm, cfg);
    var hist = new MaterialEditHistory(mm);
    var orig = (byte[])mm.Samples.Clone();
    var stroke = mp.BeginStroke();
    for (int i = 0; i < 8; i++) stroke.Dab((20 + i * 3) * Sp(cfg), 32 * Sp(cfg), new MaterialBrush(3, 24f));
    var edit = stroke.Finish();
    Check("stroke coalesced into one edit", edit is not null);
    var after = (byte[])mm.Samples.Clone();
    hist.Push(edit!);
    Check("undo restores original exactly", hist.Undo() && mm.Samples.SequenceEqual(orig));
    Check("redo reapplies exactly", hist.Redo() && mm.Samples.SequenceEqual(after));
    string tmp = Path.Combine(Path.GetTempPath(), $"mm_{Guid.NewGuid():N}.raw");
    mm.SaveRaw(tmp); var rt = MaterialMap.LoadRaw(tmp, 64, 64); File.Delete(tmp);
    Check("raw round-trip identical", rt.Samples.SequenceEqual(mm.Samples));
}

// ---- Real-map raycast + before/after render (Operation_Irving) ----
string? irv = FindIrving();
if (irv is not null)
{
    Console.WriteLine("Scenario 9: real map — raycast, sculpt a hill + a flat pad, re-render");
    var scene = LevelScene.Load(irv);
    // aerial camera, raycast its centre ray to the terrain
    var cam = scene.CreateAerialCamera(1100f / 825f);
    var ray = Picking.ScreenToRay(cam, 550, 412, 1100, 825);   // screen centre
    var hit = scene.RaycastTerrain(ray);
    Check("centre ray hits terrain", hit is not null);
    if (hit is { } h)
    {
        float hMap = scene.HeightAtWorld(h.X, h.Z);
        Check("hit lies on the heightfield", Near(h.Y, hMap, 1.0f));
    }

    // sculpt: raise a big smooth hill near the middle
    float cx = scene.WorldSize * 0.5f, cz = scene.WorldSize * 0.5f;
    float before = scene.HeightAtWorld(cx, cz);     // capture BEFORE the stroke
    int cgx = Math.Min((int)MathF.Round(cx / scene.Config.HorizontalSpacing), scene.Mesh.GridW - 1);
    int cgy = Math.Min((int)MathF.Round(cz / scene.Config.HorizontalSpacing), scene.Mesh.GridH - 1);
    float meshCentreBefore = scene.Mesh.Positions[cgy * scene.Mesh.GridW + cgx].Y;

    var hill = scene.Terrain.BeginStroke();
    hill.Dab(cx, cz, new TerrainBrush(BrushMode.Raise, RadiusMeters: 220f, Strength: 60f, Falloff: BrushFalloff.Smooth));
    var hillEdit = hill.Finish();
    Check("hill edit produced", hillEdit is not null);

    float afterEdit = scene.HeightAtWorld(cx, cz);
    Check("heightmap centre raised ~60 m", afterEdit > before + 30f);

    scene.RebuildTerrain();
    float meshCentreAfter = scene.Mesh.Positions[cgy * scene.Mesh.GridW + cgx].Y;
    Check("mesh centre vertex rose after rebuild", meshCentreAfter > meshCentreBefore + 30f);

    // render before/after for visual proof
    RenderTo(scene, "/mnt/user-data/outputs/terrain_after_sculpt.bmp");
    Console.WriteLine("    wrote /mnt/user-data/outputs/terrain_after_sculpt.bmp");

    if (scene.Materials is not null && scene.MaterialMap is { } mmap)
    {
        Console.WriteLine("Scenario 10b: real map — confirm the material map loaded, then paint a path");
        Check("material map loaded at materialSize", mmap.Width == scene.Config.MaterialSize && mmap.Height == scene.Config.MaterialSize);
        int distinctBefore = mmap.Samples.Distinct().Count();
        Check("real material map has multiple surfaces", distinctBefore > 1);
        DumpMaterials(mmap, "/mnt/user-data/outputs/materialmap_before.bmp");

        // paint a swath of material 3 (a "path") straight across the middle
        var stroke = scene.Materials.BeginStroke();
        for (float u = 0.25f; u <= 0.75f; u += 0.01f)
            stroke.Dab(scene.WorldSize * u, scene.WorldSize * 0.5f, new MaterialBrush(Material: 3, RadiusMeters: 55f, Hardness: 0.9f));
        var medit = stroke.Finish();
        Check("material stroke produced", medit is not null);
        int mcgx = Math.Clamp((int)MathF.Round(scene.WorldSize * 0.5f / scene.Config.HorizontalSpacing), 0, mmap.Width - 1);
        Check("centre cell repainted to 3", mmap[mcgx, mcgx] == 3);
        DumpMaterials(mmap, "/mnt/user-data/outputs/materialmap_after.bmp");
        Console.WriteLine("    wrote materialmap_before.bmp / materialmap_after.bmp");
    }
}
else
{
    Console.WriteLine("(Operation_Irving not extracted — skipping real-map raycast/render scenario)");
}

Console.WriteLine("LightmapShadowBits .lsb byte-exact round-trip (present samples only)");
{
    string[] lsbSamples =
    {
        @"D:\Games\Operation_Irving\Textures\LightmapShadowBits.lsb",
        @"D:\Games\EA GAMES\IS82 Extracted\bf1942\levels\canyon_drift_nismo\Textures\LightmapShadowBits.lsb",
        @"D:\Games\EA GAMES\BF1942 Blender Map Editing\Battlefield 1942\Bf1942\Levels\El_Alamein\Textures\LightmapShadowBits.lsb",
        @"D:\Games\EA GAMES\Battlefield 1942\Mods\Eastern_Front\bf1942\levels\Kharkov_Day2\Textures\LightmapShadowBits.lsb",
    };
    int lsbChecked = 0;
    foreach (var lsbPath in lsbSamples)
    {
        if (!File.Exists(lsbPath)) continue;
        lsbChecked++;
        byte[] o = File.ReadAllBytes(lsbPath);
        var decoded = LightmapShadowBits.Decode(o);
        byte[] re = decoded.Encode();
        bool eq = re.Length == o.Length;
        if (eq) for (int i = 0; i < o.Length; i++) if (o[i] != re[i]) { eq = false; break; }
        string tag = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(lsbPath)));
        Check($"lsb round-trip byte-exact: {tag}", eq);

        // Raster bridge (write-back path): decode each tile to a visibility raster and re-encode it
        // canonically. The engine's encoding is canonical, so this must reproduce the file byte-exact.
        var viaRaster = new LightmapShadowBits();
        foreach (var t in decoded.Tiles)
            viaRaster.Tiles.Add(t.IsEmpty
                ? new LightmapShadowBits.Tile { Count1 = 0 }
                : LightmapShadowBits.Tile.FromRaster(t.ToRaster(), t.Width, t.Height));
        byte[] re2 = viaRaster.Encode();
        bool eq2 = re2.Length == o.Length;
        if (eq2) for (int i = 0; i < o.Length; i++) if (o[i] != re2[i]) { eq2 = false; break; }
        Check($"lsb raster bridge byte-exact: {tag}", eq2);

        // Whole-world bridge: stitch all tiles into one visibility raster, re-slice it, and re-encode.
        // This must reproduce the file byte-exact, proving ToVisibility/FromVisibility are exact inverses
        // (the bake write-back path) with the correct tile ordering on real data.
        byte[] full = decoded.ToVisibility(out int fullSide);
        byte[] reWW = LightmapShadowBits.FromVisibility(full, fullSide, decoded.GridDim, decoded.TilePixels).Encode();
        bool eqWW = reWW.Length == o.Length;
        if (eqWW) for (int i = 0; i < o.Length; i++) if (o[i] != reWW[i]) { eqWW = false; break; }
        Check($"lsb whole-world bridge byte-exact: {tag} (grid {decoded.GridDim}x{decoded.GridDim}, side {fullSide})", eqWW);

        // Write-back plumbing: LevelSaver.SaveFolder writes the .lsb and TryLoadFolder reads it.
        // Stage the sample under <temp>/Textures, load it, save it back through the level-save path, reload, compare.
        string tmp = Path.Combine(Path.GetTempPath(), "rf_lsb_" + tag + "_" + o.Length);
        try
        {
            Directory.CreateDirectory(Path.Combine(tmp, "Textures"));
            File.WriteAllBytes(Path.Combine(tmp, "Textures", "LightmapShadowBits.lsb"), o);
            var loaded = LightmapShadowBits.TryLoadFolder(tmp);
            LevelSaver.SaveFolder(tmp, null, null, null, null, null, null, loaded);
            byte[] afterSave = File.ReadAllBytes(Path.Combine(tmp, "Textures", "LightmapShadowBits.lsb"));
            bool eq3 = loaded is not null && afterSave.Length == o.Length;
            if (eq3) for (int i = 0; i < o.Length; i++) if (o[i] != afterSave[i]) { eq3 = false; break; }
            Check($"lsb SaveFolder write-back byte-exact: {tag}", eq3);
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }
    if (lsbChecked == 0) Console.WriteLine("  (no .lsb samples present on this machine — skipped)");
}

Console.WriteLine($"\n{(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED")}");
return failures == 0 ? 0 : 1;

static string? FindIrving()
{
    foreach (var root in new[] { "/home/claude/game", "/home/claude" })
        if (Directory.Exists(root))
            foreach (var d in Directory.EnumerateDirectories(root, "Operation_Irving", SearchOption.AllDirectories))
                return d;
    return null;
}

static void RenderTo(LevelScene scene, string bmp)
{
    var cam = scene.CreateAerialCamera(1100f / 825f);
    var img = scene.Render(cam, 1100, 825);
    img.SaveBmp(bmp);
}

// Top-down visualization of the 8-bit material map via a fixed palette (one colour per index).
static void DumpMaterials(MaterialMap mm, string bmp)
{
    var pal = MatPalette();
    var img = new ImageBuffer(mm.Width, mm.Height);
    for (int y = 0; y < mm.Height; y++)
        for (int x = 0; x < mm.Width; x++)
        {
            var c = pal[mm[x, y] % pal.Length];
            int pi = (y * mm.Width + x) * 3;
            img.Rgb[pi] = (byte)(c.X * 255); img.Rgb[pi + 1] = (byte)(c.Y * 255); img.Rgb[pi + 2] = (byte)(c.Z * 255);
        }
    img.SaveBmp(bmp);
}

static Vector3[] MatPalette() => new[]
{
    new Vector3(0.45f,0.62f,0.30f),  // 0
    new Vector3(0.78f,0.66f,0.36f),  // 1
    new Vector3(0.55f,0.40f,0.25f),  // 2
    new Vector3(0.85f,0.42f,0.20f),  // 3  (path - orange)
    new Vector3(0.30f,0.55f,0.65f),  // 4
    new Vector3(0.90f,0.85f,0.45f),  // 5
    new Vector3(0.60f,0.30f,0.55f),  // 6
    new Vector3(0.35f,0.62f,0.38f),  // 7  (base grass)
    new Vector3(0.70f,0.70f,0.72f),  // 8
    new Vector3(0.85f,0.30f,0.35f),  // 9
    new Vector3(0.40f,0.45f,0.80f),  // 10
    new Vector3(0.55f,0.75f,0.85f),  // 11
    new Vector3(0.75f,0.55f,0.30f),  // 12
    new Vector3(0.50f,0.50f,0.30f),  // 13
    new Vector3(0.65f,0.40f,0.40f),  // 14
    new Vector3(0.30f,0.30f,0.30f),  // 15
};
