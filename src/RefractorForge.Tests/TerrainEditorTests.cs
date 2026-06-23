using RefractorForge.Formats.Con;
using RefractorForge.Formats.Terrain;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

public class TerrainEditorTests
{
    [Fact]
    public void Sculpt_modes_raise_lower_set_flatten_falloffs()
    {
        var cfg = new TerrainConfig { MaterialSize = 128, WorldSize = 512, YScale = 0.5f };
        int cc = 64;
        float cx = cc * cfg.HorizontalSpacing, cz = cc * cfg.HorizontalSpacing;
        Heightmap Flat50() => HeightmapGenerator.Flat(cfg.MaterialSize, cfg.MetersToRaw(50f));
        float HM(Heightmap h) => cfg.HeightToMeters(h[cc, cc]);

        var hRaise = Flat50(); new TerrainEditor(hRaise, cfg).Stamp(cx, cz, new TerrainBrush(BrushMode.Raise, 40f, 5f, BrushFalloff.Smooth));
        Assert.True(HM(hRaise) > 53f, $"Raise lifts centre ~+5 m (got {HM(hRaise):0.0})");

        var hLower = Flat50(); new TerrainEditor(hLower, cfg).Stamp(cx, cz, new TerrainBrush(BrushMode.Lower, 40f, 5f, BrushFalloff.Smooth));
        Assert.True(HM(hLower) < 47f, $"Lower drops centre ~-5 m (got {HM(hLower):0.0})");

        var hSet = Flat50(); new TerrainEditor(hSet, cfg).Stamp(cx, cz, new TerrainBrush(BrushMode.Set, 40f, 1f, BrushFalloff.Smooth, 10f));
        Assert.True(MathF.Abs(HM(hSet) - 10f) < 0.5f, $"Set forces centre to target 10 m (got {HM(hSet):0.0})");

        var hFlat = Flat50(); new TerrainEditor(hFlat, cfg).Stamp(cx, cz, new TerrainBrush(BrushMode.Flatten, 40f, 0.5f, BrushFalloff.Smooth, 30f));
        Assert.True(HM(hFlat) > 36f && HM(hFlat) < 44f, $"Flatten eases centre toward 30 (got {HM(hFlat):0.0})");

        int edge = cc + (int)(0.9f * 40f / cfg.HorizontalSpacing);
        var hC = Flat50(); new TerrainEditor(hC, cfg).Stamp(cx, cz, new TerrainBrush(BrushMode.Set, 40f, 1f, BrushFalloff.Constant, 0f));
        var hG = Flat50(); new TerrainEditor(hG, cfg).Stamp(cx, cz, new TerrainBrush(BrushMode.Set, 40f, 1f, BrushFalloff.Gaussian, 0f));
        float ce = cfg.HeightToMeters(hC[edge, cc]), ge = cfg.HeightToMeters(hG[edge, cc]);
        Assert.True(ce < 5f, $"Constant falloff zeroes edge cell (got {ce:0.0} m)");
        Assert.True(ge > 40f, $"Gaussian falloff leaves edge cell high (got {ge:0.0} m)");
        Assert.True(ce < ge - 20f, "Constant vs Gaussian clearly differ at edge");
    }

    [Fact]
    public void Brush_shapes_mask_vs_radial_and_square()
    {
        var cfg = new TerrainConfig { MaterialSize = 128, WorldSize = 512, YScale = 0.5f };
        int cc = 64; float cx = cc * cfg.HorizontalSpacing, cz = cc * cfg.HorizontalSpacing;
        var solid = new float[16]; Array.Fill(solid, 1f);
        var solidMask = new BrushMask("solid", 4, solid);
        int corner = cc + (int)(40f / cfg.HorizontalSpacing) - 1;

        var hMask = HeightmapGenerator.Flat(cfg.MaterialSize, cfg.MetersToRaw(50f));
        new TerrainEditor(hMask, cfg).Stamp(cx, cz, new TerrainBrush(BrushMode.Set, 40f, 1f, BrushFalloff.Smooth, 0f, solidMask));
        var hRad = HeightmapGenerator.Flat(cfg.MaterialSize, cfg.MetersToRaw(50f));
        new TerrainEditor(hRad, cfg).Stamp(cx, cz, new TerrainBrush(BrushMode.Set, 40f, 1f, BrushFalloff.Constant, 0f));
        float maskCorner = cfg.HeightToMeters(hMask[corner, corner]), radCorner = cfg.HeightToMeters(hRad[corner, corner]);
        Assert.True(maskCorner < 5f, $"square mask reaches box corner (got {maskCorner:0.0} m)");
        Assert.True(radCorner > 45f, $"radial brush leaves box corner untouched (got {radCorner:0.0} m)");

        var hSq = HeightmapGenerator.Flat(cfg.MaterialSize, cfg.MetersToRaw(50f));
        new TerrainEditor(hSq, cfg).Stamp(cx, cz, new TerrainBrush(BrushMode.Set, 40f, 1f, BrushFalloff.Constant, 0f, null, Square: true));
        Assert.True(cfg.HeightToMeters(hSq[corner, corner]) < 5f, "procedural square reaches box corner");

        var hSqG = HeightmapGenerator.Flat(cfg.MaterialSize, cfg.MetersToRaw(50f));
        new TerrainEditor(hSqG, cfg).Stamp(cx, cz, new TerrainBrush(BrushMode.Raise, 40f, 10f, BrushFalloff.Gaussian, null, null, Square: true));
        var hRadG = HeightmapGenerator.Flat(cfg.MaterialSize, cfg.MetersToRaw(50f));
        new TerrainEditor(hRadG, cfg).Stamp(cx, cz, new TerrainBrush(BrushMode.Raise, 40f, 10f, BrushFalloff.Gaussian, null, null, Square: false));
        float sqCentre = cfg.HeightToMeters(hSqG[64, 64]), sqDiag = cfg.HeightToMeters(hSqG[69, 69]);
        float radDiag = cfg.HeightToMeters(hRadG[69, 69]);
        Assert.True(Math.Abs(sqCentre - sqDiag) < 1f && sqDiag > 58f, $"square Gaussian raise is flat-topped (centre {sqCentre:0.0} ~= diagonal {sqDiag:0.0} m)");
        Assert.True(radDiag < 53f, $"radial Gaussian peaks at centre (got {radDiag:0.0} m)");

        var mSq = new MaterialMap(cfg.MaterialSize, cfg.MaterialSize);
        new MaterialPainter(mSq, cfg).Stamp(cx, cz, new MaterialBrush(7, 40f, 1f, BrushFalloff.Constant, null, Square: true));
        var mRad = new MaterialMap(cfg.MaterialSize, cfg.MaterialSize);
        new MaterialPainter(mRad, cfg).Stamp(cx, cz, new MaterialBrush(7, 40f, 1f, BrushFalloff.Constant));
        Assert.True(mSq[corner, corner] == 7, "material square paints the box corner");
        Assert.True(mRad[corner, corner] == 0, "material radial leaves the box corner");
    }

    [Fact]
    public void Object_scatter_constraints()
    {
        int ms = 64; var cfg = new TerrainConfig { MaterialSize = ms, WorldSize = 256, YScale = 1f, WaterLevel = 30f };
        var hm = new Heightmap(ms, ms);
        for (int r = 0; r < ms; r++) for (int c = 0; c < ms; c++)
            hm[c, r] = cfg.MetersToRaw(c < ms / 3 ? 20f : c < 2 * ms / 3 ? 50f : 50f + (c - 2 * ms / 3) * 4f);
        float HeightAt(float x, float z) => SearchMapGenerator.SampleHeight(cfg, hm, x, z);
        var candidates = new[] { "tree_m1", "bush_m1", "hut_m1" };

        float maxSlope = 20f, clearance = 1f, spacing = 5f;
        var placed = ObjectScatter.Scatter(candidates, cfg, HeightAt, count: 80, minSlopeDeg: 0f, maxSlopeDeg: maxSlope,
            avoidWater: true, waterClearance: clearance, minSpacing: spacing, seed: 3);
        Assert.True(placed.Count > 0, "placed at least some objects");
        int dry = placed.Count(p => p.Position.Y >= cfg.WaterLevel + clearance);
        Assert.True(dry == placed.Count, $"all above water+clearance ({dry}/{placed.Count})");
        int onSlope = placed.Count(p =>
        {
            float st = cfg.HorizontalSpacing;
            float gx = (HeightAt(p.Position.X + st, p.Position.Z) - HeightAt(p.Position.X - st, p.Position.Z)) / (2 * st);
            float gz = (HeightAt(p.Position.X, p.Position.Z + st) - HeightAt(p.Position.X, p.Position.Z - st)) / (2 * st);
            return MathF.Atan(MathF.Sqrt(gx * gx + gz * gz)) * 180f / MathF.PI <= maxSlope + 0.5f;
        });
        Assert.True(onSlope == placed.Count, $"all within slope band ({onSlope}/{placed.Count})");
        bool spacingOk = true;
        for (int i = 0; i < placed.Count && spacingOk; i++) for (int j = i + 1; j < placed.Count; j++)
        {
            float dx = placed[i].Position.X - placed[j].Position.X, dz = placed[i].Position.Z - placed[j].Position.Z;
            if (dx * dx + dz * dz < spacing * spacing) { spacingOk = false; break; }
        }
        Assert.True(spacingOk, "min spacing respected");
        Assert.True(placed.All(p => candidates.Contains(p.Template)), "every placement uses a candidate template");
        Assert.True(placed.All(p => p.Yaw is >= 0f and <= 360f), "yaw randomized in [0,360]");
        var again = ObjectScatter.Scatter(candidates, cfg, HeightAt, 80, 0f, maxSlope, true, clearance, spacing, 3);
        Assert.True(again.Count == placed.Count && again[0].Position.Equals(placed[0].Position), "deterministic for fixed seed");
    }

    [Fact]
    public void Fractal_generator_relief_and_island_edges()
    {
        int sz = 256;
        var cfg = new TerrainConfig { MaterialSize = sz, WorldSize = 1024, YScale = 1f, WaterLevel = 30f };
        ushort lo = cfg.MetersToRaw(0f), hi = cfg.MetersToRaw(200f);

        double Sd(Heightmap h) { double s = 0, s2 = 0; foreach (var v in h.Samples) { s += v; s2 += (double)v * v; } double m = s / h.Samples.Length; return Math.Sqrt(Math.Max(0, s2 / h.Samples.Length - m * m)); }
        ushort Max(Heightmap h) { ushort m = 0; foreach (var v in h.Samples) if (v > m) m = v; return m; }

        var hills = HeightmapGenerator.Fractal(sz, 2026, 0.55f, lo, hi);
        Assert.True(Sd(hills) > 1000, $"hills have real relief (sd {Sd(hills):0} raw)");
        Assert.True(Math.Abs(cfg.HeightToMeters(Max(hills)) - 200f) < 2f, $"height range respected: peak {cfg.HeightToMeters(Max(hills)):0} m ~ 200 m");

        var mtn = HeightmapGenerator.Fractal(sz, 2026, 0.45f, lo, hi, island: false, peak: 2.2f);
        double meanHills = 0, meanMtn = 0; foreach (var v in hills.Samples) meanHills += v; foreach (var v in mtn.Samples) meanMtn += v;
        Assert.True(meanMtn < meanHills, "mountains skew lower");

        var isl = HeightmapGenerator.Fractal(sz, 2026, 0.55f, lo, hi, island: true);
        double edge = 0; int ne = 0, c = sz / 2; double center = 0; int nc = 0;
        for (int x = 0; x < sz; x++) { edge += isl[x, 0] + isl[x, sz - 1]; ne += 2; }
        for (int y = c - 8; y < c + 8; y++) for (int x = c - 8; x < c + 8; x++) { center += isl[x, y]; nc++; }
        Assert.True(edge / ne < center / nc * 0.5, "island edges sink to water");
    }

    [Fact]
    public void Material_map_generator_from_terrain()
    {
        int ms = 64, ws = 256;
        var cfg = new TerrainConfig { MaterialSize = ms, WorldSize = ws, YScale = 1f, WaterLevel = 30f };
        var hm = new Heightmap(ms, ms);
        for (int row = 0; row < ms; row++)
            for (int col = 0; col < ms; col++)
            {
                float h = col < ms / 3 ? 20f : col < 2 * ms / 3 ? 50f : 50f + (col - 2 * ms / 3) * 5f;
                hm[col, row] = cfg.MetersToRaw(h);
            }
        var m = MaterialMapGenerator.FromTerrain(cfg, hm);
        Assert.True(m.Width == ms && m.Height == ms, "material map is materialSize^2");
        int C(float frac) => Math.Clamp((int)(frac * ms), 0, ms - 1);
        byte under = m[C(0.1f), ms / 2], cliff = m[C(0.95f), ms / 2], flat = m[C(0.5f), ms / 2];
        Assert.True(under == 15, $"underwater -> Water(15) (got {under})");
        Assert.True(cliff == 9, $"steep cliff -> Rock(9) (got {cliff})");
        Assert.True(flat == 1 || flat == 3 || flat == 6, $"flat plain -> grass/dirt/sand (got {flat})");
    }

    [Fact]
    public void Surface_atlas_bake_from_material()
    {
        var mat = new MaterialMap(2, 2);
        mat[0, 0] = 1; mat[1, 0] = 1; mat[0, 1] = 9; mat[1, 1] = 9;
        var surfaces = new Texture2D?[16];
        for (int i = 0; i < 16; i++) surfaces[i] = new Texture2D(1, 1, new byte[] { (byte)(i * 16), (byte)(255 - i * 16), 0, 255 });
        int[] matToSurf = { 0, 2, 3, 4, 5, 6, 10, 11, 8, 12, 14, 13, 15, 15, 9, 1 };
        int atlasSize = 4;
        var atlas = TerrainTexture.BakeAtlasFromMaterial(mat, surfaces, matToSurf, atlasSize, 256f, 8f);
        Assert.True(atlas.Width == atlasSize && atlas.Height == atlasSize, "atlas is atlasSize^2");
        int top = matToSurf[1], bot = matToSurf[9];
        Assert.True(atlas.Rgba[0] == (byte)(top * 16) && atlas.Rgba[1] == (byte)(255 - top * 16), $"material 1 -> surf slot {top} colour");
        int ob = ((atlasSize - 1) * atlasSize + 0) * 4;
        Assert.True(atlas.Rgba[ob] == (byte)(bot * 16) && atlas.Rgba[ob + 1] == (byte)(255 - bot * 16), $"material 9 -> surf slot {bot} colour");
    }
}
