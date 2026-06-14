using System;
using System.Collections.Generic;
using System.Globalization;

namespace RefractorForge.Formats.Terrain;

/// <summary>One scattered foliage instance from an overgrowth map: which geometry, where (world XZ), and a
/// deterministic yaw + uniform scale. The caller supplies the ground Y and filters by mesh resolvability / water —
/// this layer is pure data so it can be gate-tested headlessly.</summary>
public readonly record struct FoliageInstance(string Geometry, float WorldX, float WorldZ, float YawDeg, float Scale);

/// <summary>
/// Scatters the trees/vegetation an overgrowth (or undergrowth) index map + its <c>.wst</c> palette describe, using
/// the SAME patch model BfVietnam.exe's OverGrowthManager uses so the editor's DENSITY matches the game (verified
/// against a captured tree dump: a ~12.5 m patch grid, ~2.1 trees per occupied patch [range 1-6], a probability
/// roulette over the cell material's <c>&lt;types&gt;</c>, uniform yaw, uniform scale from the <c>.wst</c>). Per-patch
/// deterministic (engine seed <c>cellY*4711 + cellX*13 + 23</c>), so the scatter is stable across runs. This is a
/// VIEW/preview + bake source — it matches the game's DENSITY and species mix, not its exact tree-for-tree RNG (for
/// that, the in-game capture tool reads the engine's real output). See memory <c>overgrowth-engine-re</c>.
/// </summary>
public static class OvergrowthFoliage
{
    // Trees-per-occupied-patch distribution captured from the running game (19,179 trees / 9,091 patches, avg 2.11).
    // Cumulative: 1:0.292  2:0.680  3:0.924  4:0.994  5:0.9996  6:1.0.
    static int CountForPatch(ref uint s)
    {
        float r = NextF(ref s);
        if (r < 0.292f) return 1;
        if (r < 0.680f) return 2;
        if (r < 0.924f) return 3;
        if (r < 0.994f) return 4;
        if (r < 0.9996f) return 5;
        return 6;
    }

    /// <summary>Scatter from the over- (default) or under-growth layer. <paramref name="patchMeters"/> is the patch
    /// grid size (the game uses ~12.5 m); <paramref name="densityScale"/> multiplies the per-patch tree count
    /// (1.0 = game-matched). Empty material slots (default / water) yield nothing. Deterministic.</summary>
    public static List<FoliageInstance> Scatter(GrowthMaps growth, TerrainConfig cfg, float patchMeters, float densityScale = 1f, bool over = true)
    {
        var list = new List<FoliageInstance>();
        var map = over ? growth.Over : growth.Under;
        var pal = over ? growth.OverPalette : growth.UnderPalette;
        int side = over ? growth.OverSide : growth.UnderSide;
        if (map is null || pal is null || side <= 0 || cfg.WorldSize <= 0) return list;
        var slots = pal.Materials;
        if (slots.Count == 0) return list;

        float ws = cfg.WorldSize;
        int grid = Math.Max(1, (int)MathF.Round(ws / MathF.Max(patchMeters, 1f)));   // patches per axis (game ~163 @ 12.5 m on 2048)
        float ps = ws / grid;                                                         // actual patch size
        densityScale = Math.Clamp(densityScale, 0.05f, 8f);

        for (int cy = 0; cy < grid; cy++)
            for (int cx = 0; cx < grid; cx++)
            {
                // Cheap occupancy reject: if the patch centre's material grows nothing, skip the whole patch.
                int mcx0 = Math.Clamp((int)((cx + 0.5f) * ps / ws * side), 0, side - 1);
                int mcy0 = Math.Clamp((int)((cy + 0.5f) * ps / ws * side), 0, side - 1);
                int cidx = map[mcx0, mcy0];
                if (cidx < 0 || cidx >= slots.Count || slots[cidx].Types.Count == 0) continue;

                uint state = (uint)((cy * 4711 + cx * 13 + 23) & 0x7fffffff);          // engine per-patch seed
                int count = Math.Max(0, (int)MathF.Round(CountForPatch(ref state) * densityScale));
                for (int k = 0; k < count; k++)
                {
                    float wx = cx * ps + NextF(ref state) * ps;
                    float wz = cy * ps + NextF(ref state) * ps;
                    if (wx >= ws || wz >= ws) continue;
                    // Per-tree material check (like the engine -> ~99.7% land on a tree-bearing material).
                    int mcx = Math.Clamp((int)(wx / ws * side), 0, side - 1);
                    int mcy = Math.Clamp((int)(wz / ws * side), 0, side - 1);
                    int idx = map[mcx, mcy];
                    if (idx < 0 || idx >= slots.Count) continue;
                    var types = slots[idx].Types;
                    if (types.Count == 0) continue;

                    var ft = Roulette(types, ref state);
                    if (string.IsNullOrWhiteSpace(ft.GeometryName)) continue;
                    float yaw = NextF(ref state) * 360f;
                    float scl = ScaleFor(ft.Scale, ref state);
                    list.Add(new FoliageInstance(ft.GeometryName, wx, wz, yaw, scl));
                }
            }
        return list;
    }

    /// <summary>Pick a foliage type weighted by its <c>probability</c> (the engine's roulette); uniform if none set.</summary>
    static FoliageType Roulette(IReadOnlyList<FoliageType> types, ref uint s)
    {
        float sum = 0f;
        foreach (var t in types) sum += MathF.Max(t.Probability, 0f);
        if (sum <= 0f) return types[Math.Min((int)(NextF(ref s) * types.Count), types.Count - 1)];
        float r = NextF(ref s) * sum, acc = 0f;
        foreach (var t in types) { acc += MathF.Max(t.Probability, 0f); if (r <= acc) return t; }
        return types[types.Count - 1];
    }

    /// <summary>Parse a <c>.wst</c> scale field ("CRDUniform 0.6 1.2", "1", "0.8 1.4"…) into a uniform scale,
    /// lerping between a min/max pair (the keyword tokens are skipped); defaults to 1 when absent/non-positive.</summary>
    static float ScaleFor(string scale, ref uint s)
    {
        if (string.IsNullOrWhiteSpace(scale)) return 1f;
        var parts = scale.Split(new[] { ' ', ',', '/', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        float a = 1f, b = 1f; int n = 0;
        foreach (var p in parts)
            if (float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                if (n == 0) { a = v; b = v; } else if (n == 1) b = v;
                n++;
            }
        if (n == 0) return 1f;
        if (b < a) (a, b) = (b, a);
        return MathF.Max(0.01f, a + (b - a) * NextF(ref s));
    }

    /// <summary>Deterministic LCG (same family as the engine's RNG) -> float in [0,1). State is the per-patch seed.</summary>
    static float NextF(ref uint s)
    {
        s = s * 214013u + 2531011u;
        return ((s >> 16) & 0x7fff) / 32768f;
    }
}
