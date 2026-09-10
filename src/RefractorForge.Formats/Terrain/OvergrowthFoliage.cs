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

    /// <summary>Placement grid in metres: the growth map's OWN cell size, <c>worldSize / materialMapSideSize</c>.
    ///
    /// This used to be derived from the palette's viewDistance (the exe's <c>arg/(COUNT/2-1)</c>), which put trees
    /// 37 to 137 metres apart depending on the map and left every level looking bare. Measuring the maps settled
    /// it: the growth index map is a 4 m grid, retail levels paint 26,000-34,000 tree-bearing cells per square
    /// kilometre, and that density is near identical from map to map - Ho Chi Minh Trail 30,398, Ia Drang 33,620,
    /// Khe Sanh 33,552. The view-distance model predicted those three would differ by an order of magnitude. They
    /// do not, in the editor or in the game, so the placement grid is the painted map itself.
    ///
    /// The per-type <c>probability</c> values corroborate it: within a material they sum to about 1.0, so they are
    /// a roulette between types for a cell that is getting something, not a chance of the cell staying empty. And
    /// <c>minRadiusDistToEquals</c> / <c>minRadiusDistToOthers</c> are 0.2 to 2 metres, so the engine is quite
    /// happy to stand two trees a metre apart - which no 112-metre patch model can produce.</summary>
    public static float PatchMetersFor(FoliagePalette? pal, float worldSize, bool over = true)
    {
        int side = pal?.MaterialMapSideSize ?? 0;
        if (side <= 0 || worldSize <= 0f) return over ? VegOverPatchMeters : VegUnderPatchMeters;
        return worldSize / side;
    }

    // How far undergrowth reaches. Retail declares 35 to 61 metres (Khe Sanh declares nothing at all), which is why
    // it is a carpet you walk through rather than scenery you look at - and why generating it for the WHOLE map is
    // both wrong and impossible: Ia Drang paints 86% of a 2048-cell, one-metre map, which is two million clumps.
    public const float DefaultUnderView = 50f;
    public const float UnderViewMin = 20f, UnderViewMax = 120f;

    /// <summary>How far from the camera a layer is worth generating and drawing. Undergrowth answers with the
    /// palette's own (short) view distance, clamped to the band retail actually uses; overgrowth answers 0, meaning
    /// "the whole map" - the caller culls it against fog or the world size.</summary>
    public static float ViewMetersFor(FoliagePalette? pal, bool over)
    {
        float vd = pal?.ViewDistance ?? 0f;
        if (over) return vd > 0f ? vd : 0f;
        return Math.Clamp(vd > 0f ? vd : DefaultUnderView, UnderViewMin, UnderViewMax);
    }

    /// <summary>Kept for callers that still name a build; the placement grid no longer depends on one.</summary>
    public static float PatchMetersFor(VegetationBuild build, FoliagePalette? pal, bool over) =>
        over ? VegOverPatchMeters : VegUnderPatchMeters;

    /// <summary>The fraction of instances a build keeps. Low/Medium/High subsample; Stock and Ultra keep all.</summary>
    public static float KeepFractionFor(VegetationBuild build) => build switch
    {
        VegetationBuild.VegLow => 0.25f,
        VegetationBuild.VegMedium => 0.5f,
        VegetationBuild.VegHigh => 0.75f,
        _ => 1f,
    };

    /// <summary>Instances per painted cell, MEASURED against a capture of the running game.
    ///
    /// Operation Flaming Dart, read out of the engine's own patch grid: 41,559 instances against 81,474 painted
    /// cells on the 4 m growth map, so 0.510 per cell. Broken down by material it is 0.509 on juicyGrass (whose
    /// type probabilities sum to 1.0) and 0.480 on wetDirt (0.8) - near enough identical, so the rate is a
    /// constant and <c>probability</c> only chooses BETWEEN types, which is what the roulette already does with
    /// it. 0.26% of instances landed on a material that grows nothing, matching the jitter carrying a few over a
    /// cell boundary.
    ///
    /// This is the number the whole preview hangs on: one per cell drew five times what the game does, and the
    /// old viewDistance patch grid drew a fraction of a percent of it.</summary>
    /// <para>
    /// UNDERGROWTH uses the same rate against ITS own map, which is a 1-2 m grid rather than a 4 m one, so the same
    /// number comes out roughly four times denser per square metre - a clump every metre and a half on Ia Drang.
    /// That is what the layer looks like in game, and it is what the palette expects: undergrowth
    /// <c>minRadiusDistToEquals</c> runs 0.2-1 m. It is NOT separately measured - no capture of the undergrowth
    /// exists - but applying the measured rule to the layer's own painted map is the only reading the data supports,
    /// and it replaces a patch model that drew a few hundred clumps where the game draws thousands.
    /// </para></summary>
    public const float CellOccupancy = 0.51f;

    /// <summary>Scatter from the over- (default) or under-growth layer. <paramref name="patchMeters"/> is the
    /// placement grid (the growth map's own cell size); <paramref name="densityScale"/> multiplies the per-cell rate
    /// (1.0 = game-matched). Empty material slots (default / water) yield nothing. Deterministic: the seed comes from
    /// the cell coordinate, so a windowed scatter places its plants in exactly the spots a whole-map one would.</summary>
    /// <param name="keepFraction">The Low/Medium/High builds hash each patch and keep only a quarter, a half or
    /// three quarters of them. 1 = keep everything (Stock and Ultra).</param>
    /// <param name="radius">When positive, generate ONLY within this many metres of
    /// (<paramref name="centreX"/>, <paramref name="centreZ"/>) - the engine's own behaviour, which keeps a grid of
    /// patches around the camera rather than the whole world. Undergrowth needs it: at a 1 m grid a whole map is
    /// millions of clumps, and none of them beyond its 50 m view distance is ever drawn.</param>
    /// <param name="maxInstances">A hard ceiling, so a pathological palette or a hand-typed grid size cannot lock
    /// the editor up. Generation stops there rather than thinning, so what you get is the near field, complete.</param>
    public static List<FoliageInstance> Scatter(GrowthMaps growth, TerrainConfig cfg, float patchMeters, float densityScale = 1f, bool over = true,
                                                float keepFraction = 1f,
                                                float centreX = 0f, float centreZ = 0f, float radius = 0f,
                                                int maxInstances = int.MaxValue)
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

        // The camera window, as whole cells, so the same cell always carries the same seed however you got to it.
        int cx0 = 0, cx1 = grid - 1, cy0 = 0, cy1 = grid - 1;
        float reach2 = 0f;
        if (radius > 0f)
        {
            cx0 = Math.Max(0, (int)MathF.Floor((centreX - radius) / ps));
            cx1 = Math.Min(grid - 1, (int)MathF.Ceiling((centreX + radius) / ps));
            cy0 = Math.Max(0, (int)MathF.Floor((centreZ - radius) / ps));
            cy1 = Math.Min(grid - 1, (int)MathF.Ceiling((centreZ + radius) / ps));
            float reach = radius + ps;
            reach2 = reach * reach;
        }

        for (int cy = cy0; cy <= cy1; cy++)
            for (int cx = cx0; cx <= cx1; cx++)
            {
                if (list.Count >= maxInstances) return list;
                if (reach2 > 0f)
                {
                    float dx = (cx + 0.5f) * ps - centreX, dz = (cy + 0.5f) * ps - centreZ;
                    if (dx * dx + dz * dz > reach2) continue;
                }
                // Cheap occupancy reject: if the patch centre's material grows nothing, skip the whole patch.
                int mcx0 = Math.Clamp((int)((cx + 0.5f) * ps / ws * side), 0, side - 1);
                int mcy0 = Math.Clamp((int)((cy + 0.5f) * ps / ws * side), 0, side - 1);
                var patchSlot = pal.SlotForIndex(map[mcx0, mcy0]);
                if (patchSlot is null || patchSlot.Types.Count == 0) continue;

                // The Low/Medium/High builds drop whole patches by a hash of the patch. Same shape here: a stable
                // per-patch hash so the thinning is deterministic and does not crawl as you pan.
                if (keepFraction < 1f && PatchHash01(cx, cy) >= keepFraction) continue;

                uint state = (uint)((cy * 4711 + cx * 13 + 23) & 0x7fffffff);          // engine per-patch seed
                // One candidate per cell of the painted map, at the rate measured against the running game. Both
                // layers use it: the difference between a forest and a lawn is the grid the layer paints on (4 m
                // for overgrowth, 1-2 m for undergrowth), not a different rule.
                float accept = Math.Clamp(CellOccupancy * densityScale, 0f, 8f);
                int count = (int)accept + (NextF(ref state) < (accept - (int)accept) ? 1 : 0);
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
