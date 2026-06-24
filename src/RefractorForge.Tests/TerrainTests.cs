using RefractorForge.Formats;
using RefractorForge.Formats.Terrain;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

public class TerrainTests
{
    static TerrainConfig MakeCfg() => new() { MaterialSize = 64, WorldSize = 256, YScale = 1f, WaterLevel = 0f };
    static Heightmap MakeFlat(ushort v = 10000) { var hm = new Heightmap(64, 64); for (int i = 0; i < hm.Samples.Length; i++) hm.Samples[i] = v; return hm; }
    static float Sp(TerrainConfig c) => c.HorizontalSpacing;
    static bool Near(float a, float b, float eps) => MathF.Abs(a - b) <= eps;

    [Fact]
    public void Raise_brush_lifts_centre_with_monotone_falloff()
    {
        var cfg = MakeCfg(); var hm = MakeFlat(); var ed = new TerrainEditor(hm, cfg);
        float cx = 32 * Sp(cfg), cz = 32 * Sp(cfg);
        ushort before = hm[32, 32];
        var edit = ed.Stamp(cx, cz, new TerrainBrush(BrushMode.Raise, RadiusMeters: 40f, Strength: 5f, Falloff: BrushFalloff.Smooth));
        Assert.True(edit is not null, "edit produced");
        float centreM = cfg.HeightToMeters(hm[32, 32]);
        float baseM = cfg.HeightToMeters(before);
        Assert.True(Near(centreM - baseM, 5f, 0.2f), "centre rose ~5 m");
        Assert.True(hm[0, 0] == before && hm[63, 63] == before, "far edge unchanged");
        int d2 = hm[32, 34] - before, d5 = hm[32, 37] - before, d9 = hm[32, 41] - before;
        Assert.True(d2 >= d5 && d5 >= d9 && d9 >= 0, "falloff is monotone");
        Assert.True(hm[32, 43] == before, "radius respected (cell 32,43 ~44m > 40m radius)");
    }

    [Fact]
    public void Flatten_brush_reduces_variation_toward_target()
    {
        var cfg = MakeCfg(); var hm = MakeFlat(); var ed = new TerrainEditor(hm, cfg);
        var rng = new Random(1);
        for (int y = 28; y <= 36; y++) for (int x = 28; x <= 36; x++) hm[x, y] = (ushort)(10000 + rng.Next(-600, 600));
        float target = cfg.HeightToMeters(10000);
        float cx = 32 * Sp(cfg), cz = 32 * Sp(cfg);
        float spreadBefore = Spread(hm);
        for (int i = 0; i < 8; i++) ed.Stamp(cx, cz, new TerrainBrush(BrushMode.Flatten, 60f, 0.6f, BrushFalloff.Smooth, TargetMeters: target));
        float spreadAfter = Spread(hm);
        Assert.True(spreadAfter < spreadBefore * 0.5f, "variation around centre shrank");
        Assert.True(Near(cfg.HeightToMeters(hm[32, 32]), target, 0.5f), "centre near target");

        static float Spread(Heightmap h) { int lo = int.MaxValue, hi = int.MinValue; for (int y = 28; y <= 36; y++) for (int x = 28; x <= 36; x++) { lo = Math.Min(lo, h[x, y]); hi = Math.Max(hi, h[x, y]); } return hi - lo; }
    }

    [Fact]
    public void Smooth_brush_reduces_spike()
    {
        var cfg = MakeCfg(); var hm = MakeFlat(); var ed = new TerrainEditor(hm, cfg);
        hm[32, 32] = 30000;
        int spike0 = hm[32, 32];
        float cx = 32 * Sp(cfg), cz = 32 * Sp(cfg);
        for (int i = 0; i < 6; i++) ed.Stamp(cx, cz, new TerrainBrush(BrushMode.Smooth, 24f, 0.8f));
        Assert.True(hm[32, 32] < spike0, "spike reduced");
        Assert.True(hm[33, 32] > 10000, "neighbours raised toward spike");
    }

    [Fact]
    public void Height_clamped_at_ushort_max()
    {
        var cfg = MakeCfg(); var hm = MakeFlat(64000); var ed = new TerrainEditor(hm, cfg);
        float cx = 32 * Sp(cfg), cz = 32 * Sp(cfg);
        ed.Stamp(cx, cz, new TerrainBrush(BrushMode.Raise, 40f, 100f));
        Assert.True(hm[32, 32] == ushort.MaxValue, "centre clamped to 65535");
        Assert.True(hm.Samples.All(s => s <= ushort.MaxValue), "no sample wrapped");
    }

    [Fact]
    public void Stroke_coalescing_and_undo_redo()
    {
        var cfg = MakeCfg(); var hm = MakeFlat(); var ed = new TerrainEditor(hm, cfg);
        var snapshotOrig = (ushort[])hm.Samples.Clone();
        var hist = new TerrainEditHistory(hm);
        var stroke = ed.BeginStroke();
        for (int i = 0; i < 10; i++) stroke.Dab((20 + i * 2) * Sp(cfg), 32 * Sp(cfg), new TerrainBrush(BrushMode.Raise, 24f, 2f));
        var edit = stroke.Finish();
        Assert.True(edit is not null, "stroke produced one coalesced edit");
        Assert.True(edit!.W > 12, "edit rect spans the whole drag");
        var snapshotAfter = (ushort[])hm.Samples.Clone();
        hist.Push(edit);
        Assert.True(hist.Undo() && hm.Samples.SequenceEqual(snapshotOrig), "undo restores original exactly");
        Assert.True(hist.Redo() && hm.Samples.SequenceEqual(snapshotAfter), "redo reapplies exactly");
    }

    [Fact]
    public void Material_paint_sets_cells_within_radius()
    {
        var cfg = MakeCfg(); var mm = new MaterialMap(64, 64); var mp = new MaterialPainter(mm, cfg);
        float cx = 32 * Sp(cfg), cz = 32 * Sp(cfg);
        var edit = mp.Stamp(cx, cz, new MaterialBrush(Material: 5, RadiusMeters: 40f, Hardness: 1f));
        Assert.True(edit is not null, "edit produced");
        Assert.True(mm[32, 32] == 5, "centre painted to 5");
        Assert.True(mm[0, 0] == 0, "far corner untouched");
        Assert.True(mm[32, 40] == 5 || mm[40, 32] == 5, "within radius painted");
        Assert.True(mm[32, 44] == 0, "outside radius untouched");
        var mmA = new MaterialMap(64, 64); new MaterialPainter(mmA, cfg).Stamp(cx, cz, new MaterialBrush(9, 40f, 1.0f));
        var mmB = new MaterialMap(64, 64); new MaterialPainter(mmB, cfg).Stamp(cx, cz, new MaterialBrush(9, 40f, 0.3f));
        Assert.True(mmB.Samples.Count(b => b == 9) < mmA.Samples.Count(b => b == 9), "lower hardness paints fewer cells");
    }

    [Fact]
    public void Material_stroke_undo_redo_raw_roundtrip()
    {
        var cfg = MakeCfg(); var mm = new MaterialMap(64, 64); var mp = new MaterialPainter(mm, cfg);
        var hist = new MaterialEditHistory(mm);
        var orig = (byte[])mm.Samples.Clone();
        var stroke = mp.BeginStroke();
        for (int i = 0; i < 8; i++) stroke.Dab((20 + i * 3) * Sp(cfg), 32 * Sp(cfg), new MaterialBrush(3, 24f));
        var edit = stroke.Finish();
        Assert.True(edit is not null, "stroke coalesced into one edit");
        var after = (byte[])mm.Samples.Clone();
        hist.Push(edit!);
        Assert.True(hist.Undo() && mm.Samples.SequenceEqual(orig), "undo restores original exactly");
        Assert.True(hist.Redo() && mm.Samples.SequenceEqual(after), "redo reapplies exactly");
        string tmp = Path.Combine(Path.GetTempPath(), $"mm_{Guid.NewGuid():N}.raw");
        mm.SaveRaw(tmp); var rt = MaterialMap.LoadRaw(tmp, 64, 64); File.Delete(tmp);
        Assert.True(rt.Samples.SequenceEqual(mm.Samples), "raw round-trip identical");
    }
}
