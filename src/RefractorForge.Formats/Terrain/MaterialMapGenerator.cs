namespace RefractorForge.Formats.Terrain;

/// <summary>
/// Auto-generates a starting material-index map from terrain (water line / slope / altitude) - the editor's
/// "Generate Material Map". Indices match the editor's material palette order (matNames): 1 Dry Grass,
/// 3 Dry Dirt, 6 Dry Sand, 8 Gravel, 9 Rock, 15 Water. The user then refines by hand and bakes the surface
/// atlas from it (Generate Surface Maps). Reuses <see cref="SearchMapGenerator.SampleHeight"/> for sampling.
/// </summary>
public static class MaterialMapGenerator
{
    public static MaterialMap FromTerrain(TerrainConfig cfg, Heightmap hm)
    {
        int side = cfg.MaterialSize;
        var m = new MaterialMap(side, side);
        float sp = (float)cfg.WorldSize / side;     // metres per material cell
        float water = cfg.WaterLevel;

        // height range above the water line, for altitude banding.
        float maxH = float.MinValue;
        for (int gy = 0; gy < side; gy++)
            for (int gx = 0; gx < side; gx++)
            {
                float h0 = SearchMapGenerator.SampleHeight(cfg, hm, (gx + 0.5f) * sp, (gy + 0.5f) * sp);
                if (h0 > maxH) maxH = h0;
            }
        float maxAbove = MathF.Max(maxH - water, 1f);

        for (int gy = 0; gy < side; gy++)
            for (int gx = 0; gx < side; gx++)
            {
                float wx = (gx + 0.5f) * sp, wz = (gy + 0.5f) * sp;
                float h = SearchMapGenerator.SampleHeight(cfg, hm, wx, wz);
                byte idx;
                if (h < water) idx = 15;                            // Water
                else
                {
                    float slope = SlopeDeg(cfg, hm, wx, wz, sp);
                    float above = h - water, frac = above / maxAbove;
                    if (slope > 38f) idx = 9;                        // Rock (cliffs)
                    else if (above < 2f) idx = 6;                    // beach: Dry Sand near the water line
                    else if (frac < 0.5f) idx = 1;                   // Dry Grass (lowlands)
                    else if (frac < 0.8f) idx = 3;                   // Dry Dirt (uplands)
                    else idx = 8;                                    // Gravel (peaks)
                }
                m[gx, gy] = idx;
            }
        return m;
    }

    static float SlopeDeg(TerrainConfig cfg, Heightmap hm, float wx, float wz, float sp)
    {
        float hl = SearchMapGenerator.SampleHeight(cfg, hm, wx - sp, wz), hr = SearchMapGenerator.SampleHeight(cfg, hm, wx + sp, wz);
        float hd = SearchMapGenerator.SampleHeight(cfg, hm, wx, wz - sp), hu = SearchMapGenerator.SampleHeight(cfg, hm, wx, wz + sp);
        float dzx = (hr - hl) / (2 * sp), dzz = (hu - hd) / (2 * sp);
        return MathF.Atan(MathF.Sqrt(dzx * dzx + dzz * dzz)) * 180f / MathF.PI;
    }
}
