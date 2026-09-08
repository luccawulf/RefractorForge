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
/// <summary>Which BfVietnam build the preview should match. The vegetation is NOT the same between them, and the
/// difference is enormous - the constants below are read straight out of the executables.</summary>
public enum VegetationBuild { Stock, VegLow, VegMedium, VegHigh, VegUltra }

public static class OvergrowthFoliage
{
    // ---- What each build actually does (disassembled from the shipped .exe files, 2026-09-08) -------------------
    //
    // The generator keeps a COUNT x COUNT grid of patches around the camera, `spacing` metres apart.
    //   BfVietnam.exe (STOCK):  COUNT = 10 for both layers (0x71e902 / 0x71fe4e: `mov [esi+0x10], 0xa`), and
    //                           0x75e110 COMPUTES the spacing: `[obj+0x14] = viewDistance / (COUNT/2 - 1)`
    //                           (cdq/sar 1/dec -> 4, then `fdivr`). So spacing = viewDistance / 4.
    //   BfVietnam_Veg_*.exe:    COUNT = 80 over / 34 under, and 0x75e110 is REPLACED by a hardcoded
    //                           12.5 (over, vtable 0xBAA0A4) / 17.5 (under).
    //   Low/Medium/High also patch 0x760950 to jump to injected code that hashes the patch and keeps only
    //   tier/4 of them (1/4, 2/4, 3/4). Ultra has no such hook - it is the full set.
    //
    // This matters because the editor's captured density model (2.11 trees per occupied patch) was measured on the
    // ULTRA build. Previewing a stock-exe map with Ultra's 12.5 m grid put ~80x too many trees on the map: Saigon68
    // declares viewdistance 450, so the stock game plants a patch every 112.5 m, not every 12.5 m.
    public const int StockPatchCount = 10;
    public const float VegOverPatchMeters = 12.5f;
    public const float VegUnderPatchMeters = 17.5f;

    /// <summary>Patch grid size in metres for a build + this level's own palette.</summary>
    public static float PatchMetersFor(VegetationBuild build, FoliagePalette? pal, bool over)
    {
        float fallback = over ? VegOverPatchMeters : VegUnderPatchMeters;
        if (build != VegetationBuild.Stock) return fallback;
        float vd = pal?.ViewDistance ?? 0f;
        if (vd <= 0f) return fallback;                       // no viewdistance declared: nothing to derive from
        return vd / (StockPatchCount / 2 - 1);               // the stock exe's own formula
    }

    /// <summary>The fraction of instances a build keeps. Low/Medium/High subsample; Stock and Ultra keep all.</summary>
    public static float KeepFractionFor(VegetationBuild build) => build switch
    {
        VegetationBuild.VegLow => 0.25f,
        VegetationBuild.VegMedium => 0.5f,
        VegetationBuild.VegHigh => 0.75f,
        _ => 1f,
    };

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
    /// <param name="keepFraction">The Low/Medium/High builds hash each patch and keep only a quarter, a half or
    /// three quarters of them. 1 = keep everything (Stock and Ultra).</param>
    public static List<FoliageInstance> Scatter(GrowthMaps growth, TerrainConfig cfg, float patchMeters, float densityScale = 1f, bool over = true,
                                                float keepFraction = 1f)
    {
        var list = new List<FoliageInstance>();
        var map = over ? growth.Over : growth.Under;
        var pal = over ? growth.OverPalette : growth.UnderPalette;
        int side = over ? growth.OverSide : growth.UnderSide;
        if (map is null || pal is null || side <= 0 || cfg.WorldSize <= 0) return list;
        if (pal.Materials.Count == 0) return list;

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
                var patchSlot = pal.SlotForIndex(map[mcx0, mcy0]);
                if (patchSlot is null || patchSlot.Types.Count == 0) continue;

                // The Low/Medium/High builds drop whole patches by a hash of the patch. Same shape here: a stable
                // per-patch hash so the thinning is deterministic and does not crawl as you pan.
                if (keepFraction < 1f && PatchHash01(cx, cy) >= keepFraction) continue;

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
                    var slot = pal.SlotForIndex(map[mcx, mcy]);
                    if (slot is null || slot.Types.Count == 0) continue;
                    var types = slot.Types;

                    var ft = Roulette(types, ref state);
                    if (string.IsNullOrWhiteSpace(ft.GeometryName)) continue;
                    float yaw = NextF(ref state) * 360f;
                    float scl = ScaleFor(ft.Scale, ref state);
                    list.Add(new FoliageInstance(ft.GeometryName, wx, wz, yaw, scl));
                }
            }
        return list;
    }

    /// <summary>A stable 0..1 hash of a patch coordinate, for the Low/Medium/High builds' patch thinning. Uses the
    /// same lowbias32 constants the injected code does (<c>imul 0x7feb352d</c> / <c>imul 0x846ca68b</c>), so the
    /// thinning has the engine's character even though it hashes coordinates rather than the engine's patch
    /// pointer - which the editor has no equivalent of.</summary>
    static float PatchHash01(int cx, int cy)
    {
        uint h = (uint)(cx * 73856093 ^ cy * 19349663);
        h ^= h >> 16; h *= 0x7feb352d;
        h ^= h >> 15; h *= 0x846ca68b;
        h ^= h >> 16;
        return (h >> 8) / 16777216f;
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
