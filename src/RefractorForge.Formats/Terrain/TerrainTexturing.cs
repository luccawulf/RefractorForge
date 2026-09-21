
namespace RefractorForge.Formats.Terrain;

/// <summary>
/// What the ground at each material cell IS, measured from the heightmap: how high above the water line it sits, how
/// steep it is, whether it bulges or hollows, how much water runs across it, and how far it is from the shore.
///
/// This is the half of World Machine-style texturing that actually matters here. The engine gives a level ONE material
/// index per cell out of sixteen and no blending at all, so the interesting part is not compositing layers - it is
/// deciding, per cell, which of the sixteen this piece of ground is. Every measurement below is a question a mapper
/// already asks by eye ("that's a cliff", "that's a gully", "that's beach"), turned into a number a rule can test.
/// </summary>
public sealed class TerrainAnalysis
{
    public int Size { get; private init; }
    /// <summary>Metres between neighbouring material cells (worldSize / materialSize).</summary>
    public float MetersPerCell { get; private init; }

    /// <summary>Ground height in metres.</summary>
    public float[] Height { get; private init; } = Array.Empty<float>();
    /// <summary>Metres above the water line; negative under water.</summary>
    public float[] AboveWater { get; private init; } = Array.Empty<float>();
    /// <summary>0 at the water line, 1 at the highest point of the map. Lets a rule say "the top third" on any map.</summary>
    public float[] Altitude { get; private init; } = Array.Empty<float>();
    /// <summary>Steepness in degrees, 0 flat .. 90 vertical.</summary>
    public float[] SlopeDeg { get; private init; } = Array.Empty<float>();
    /// <summary>-1 in a hollow (gully floor, bowl), 0 on an even face, +1 on a bulge (ridge line, boulder top).</summary>
    public float[] Convexity { get; private init; } = Array.Empty<float>();
    /// <summary>0 = rain that falls here goes nowhere, 1 = the main channel. Log-scaled flow accumulation.</summary>
    public float[] Flow { get; private init; } = Array.Empty<float>();
    /// <summary>Metres to the nearest water cell; <see cref="float.MaxValue"/> when the map has no water at all.</summary>
    public float[] ShoreMeters { get; private init; } = Array.Empty<float>();
    /// <summary>The highest point above the water line, in metres (1 when the map is entirely flat or flooded).</summary>
    public float PeakAboveWater { get; private init; }

    public static TerrainAnalysis From(TerrainConfig cfg, Heightmap hm)
    {
        int n = cfg.MaterialSize;
        float sp = (float)cfg.WorldSize / n;
        float water = cfg.WaterLevel;

        var height = new float[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                height[y * n + x] = SearchMapGenerator.SampleHeight(cfg, hm, (x + 0.5f) * sp, (y + 0.5f) * sp);

        var above = new float[n * n];
        float peak = 1f;
        for (int i = 0; i < height.Length; i++) { above[i] = height[i] - water; if (above[i] > peak) peak = above[i]; }
        var altitude = new float[n * n];
        for (int i = 0; i < height.Length; i++) altitude[i] = Math.Clamp(above[i] / peak, 0f, 1f);

        float H(int x, int y) => height[Math.Clamp(y, 0, n - 1) * n + Math.Clamp(x, 0, n - 1)];

        var slope = new float[n * n];
        var convex = new float[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dzx = (H(x + 1, y) - H(x - 1, y)) / (2 * sp);
                float dzy = (H(x, y + 1) - H(x, y - 1)) / (2 * sp);
                slope[y * n + x] = MathF.Atan(MathF.Sqrt(dzx * dzx + dzy * dzy)) * (180f / MathF.PI);

                // Height against the average of the eight neighbours: positive sticks out, negative is a dip. Scaled by
                // a quarter of a cell so ordinary ground lands near 0 and only real ridges/gullies reach +-1.
                float avg = (H(x - 1, y) + H(x + 1, y) + H(x, y - 1) + H(x, y + 1)
                           + H(x - 1, y - 1) + H(x + 1, y - 1) + H(x - 1, y + 1) + H(x + 1, y + 1)) / 8f;
                convex[y * n + x] = Math.Clamp((H(x, y) - avg) / (sp * 0.25f), -1f, 1f);
            }

        return new TerrainAnalysis
        {
            Size = n,
            MetersPerCell = sp,
            Height = height,
            AboveWater = above,
            Altitude = altitude,
            SlopeDeg = slope,
            Convexity = convex,
            Flow = FlowAccumulation(height, n),
            ShoreMeters = ShoreDistance(height, n, water, sp),
            PeakAboveWater = peak,
        };
    }

    /// <summary>
    /// How much water crosses each cell, if rain fell evenly over the whole map: walk the cells from the highest down
    /// and hand each one's water to its steepest lower neighbour. Gullies and river beds accumulate; ridges stay at
    /// their own single unit. Returned log-scaled to 0..1, because raw accumulation is dominated by a handful of cells.
    /// </summary>
    private static float[] FlowAccumulation(float[] height, int n)
    {
        var order = new int[n * n];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) => height[b].CompareTo(height[a]));         // highest first

        var acc = new float[n * n];
        Array.Fill(acc, 1f);
        foreach (var i in order)
        {
            int x = i % n, y = i / n;
            int best = -1;
            float bestDrop = 0f;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= n || ny >= n) continue;
                    int j = ny * n + nx;
                    float drop = (height[i] - height[j]) / (dx != 0 && dy != 0 ? 1.41421f : 1f);
                    if (drop > bestDrop) { bestDrop = drop; best = j; }
                }
            if (best >= 0) acc[best] += acc[i];                              // a pit keeps its water
        }

        float max = 1f;
        foreach (var a in acc) if (a > max) max = a;
        float scale = MathF.Log(1f + max);
        var flow = new float[acc.Length];
        for (int i = 0; i < acc.Length; i++) flow[i] = MathF.Log(1f + acc[i]) / scale;
        return flow;
    }

    /// <summary>Metres from each cell to the nearest cell at or below the water line (two-pass chamfer distance).</summary>
    private static float[] ShoreDistance(float[] height, int n, float water, float sp)
    {
        const float Far = float.MaxValue;
        var d = new float[n * n];
        bool anyWater = false;
        for (int i = 0; i < d.Length; i++) { bool wet = height[i] <= water; d[i] = wet ? 0f : Far; anyWater |= wet; }
        if (!anyWater) { Array.Fill(d, Far); return d; }

        const float Straight = 1f, Diag = 1.41421f;
        void Relax(int i, int j, float cost) { if (d[j] != Far && d[j] + cost < d[i]) d[i] = d[j] + cost; }
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int i = y * n + x;
                if (x > 0) Relax(i, i - 1, Straight);
                if (y > 0) Relax(i, i - n, Straight);
                if (x > 0 && y > 0) Relax(i, i - n - 1, Diag);
                if (x < n - 1 && y > 0) Relax(i, i - n + 1, Diag);
            }
        for (int y = n - 1; y >= 0; y--)
            for (int x = n - 1; x >= 0; x--)
            {
                int i = y * n + x;
                if (x < n - 1) Relax(i, i + 1, Straight);
                if (y < n - 1) Relax(i, i + n, Straight);
                if (x < n - 1 && y < n - 1) Relax(i, i + n + 1, Diag);
                if (x > 0 && y < n - 1) Relax(i, i + n - 1, Diag);
            }
        for (int i = 0; i < d.Length; i++) if (d[i] != Far) d[i] *= sp;
        return d;
    }
}

/// <summary>
/// One "ground like THIS gets material X" rule. Every limit is optional: a rule that sets none paints everywhere, which
/// is exactly what a base layer should do. Rules are applied in order and the last one that accepts a cell wins, so the
/// list reads top to bottom like a painter works - ground, then what covers it, then the edges.
/// </summary>
public sealed class MaterialRule
{
    public string Name { get; set; } = "Rule";
    /// <summary>Material index 0..15, in the editor's palette order. Never 7 - see <see cref="DeathMaterial"/>.</summary>
    public int Material { get; set; } = 1;
    public bool Enabled { get; set; } = true;

    public float MinAboveWater { get; set; } = float.NegativeInfinity;
    public float MaxAboveWater { get; set; } = float.PositiveInfinity;
    public float MinAltitude { get; set; }
    public float MaxAltitude { get; set; } = 1f;
    public float MinSlopeDeg { get; set; }
    public float MaxSlopeDeg { get; set; } = 90f;
    public float MinConvexity { get; set; } = -1f;
    public float MaxConvexity { get; set; } = 1f;
    public float MinFlow { get; set; }
    public float MaxFlow { get; set; } = 1f;
    public float MaxShoreMeters { get; set; } = float.PositiveInfinity;

    /// <summary>How much of the ground the rule accepts where it applies: 1 all of it, 0.5 half in patches.</summary>
    public float Coverage { get; set; } = 1f;
    /// <summary>Size of those patches in metres.</summary>
    public float PatchMeters { get; set; } = 48f;
    /// <summary>
    /// How far past each limit the rule fades out, as a fraction of that limit's own range. The fade is not a blend -
    /// the engine has no blending - it is where the edge DITHERS: cells near the boundary take the material in
    /// proportion to how far in they are, which is what turns a cut-out shape into a natural-looking transition.
    /// </summary>
    public float EdgeSoftness { get; set; } = 0.3f;

    public MaterialRule Clone() => (MaterialRule)MemberwiseClone();
}

/// <summary>Everything that is not a rule: what to leave alone, and the seed the dithering uses.</summary>
public sealed class TexturingOptions
{
    /// <summary>
    /// Leave every cell that is already material 7 exactly as it is. On by default and worth keeping that way: 7 is
    /// deathMaterial, the engine's out-of-bounds, and on a real map it is the painted void around the playable island.
    /// Regenerating over it would quietly move the edge of the world.
    /// </summary>
    public bool KeepOutOfBounds { get; set; } = true;
    /// <summary>Leave the four road materials (Dirt/Sand/Paved/Wet Road) where they are - they are drawn by hand.</summary>
    public bool KeepRoads { get; set; } = true;
    public int Seed { get; set; } = 1337;
}

/// <summary>
/// Turns terrain into a material map through <see cref="MaterialRule"/>s - the editor's "Texture Terrain".
///
/// The result is deliberately deterministic: same terrain, same rules, same seed, same map, so a mapper can nudge one
/// slider and see only what that slider did.
/// </summary>
public static class TerrainTexturing
{
    /// <summary>Road materials, which the generator leaves alone by default.</summary>
    public static readonly int[] RoadMaterials = { 10, 11, 12, 13 };

    public static MaterialMap Generate(TerrainConfig cfg, Heightmap hm, IReadOnlyList<MaterialRule> rules,
                                       TexturingOptions? options = null, MaterialMap? existing = null)
        => Generate(TerrainAnalysis.From(cfg, hm), rules, options, existing);

    public static MaterialMap Generate(TerrainAnalysis ta, IReadOnlyList<MaterialRule> rules,
                                       TexturingOptions? options = null, MaterialMap? existing = null)
    {
        var opt = options ?? new TexturingOptions();
        int n = ta.Size;
        var map = new MaterialMap(n, n);

        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int i = y * n + x;

                if (existing is not null && existing.Width == n && existing.Height == n)
                {
                    int had = existing[x, y] & 15;
                    if (opt.KeepOutOfBounds && had == DeathMaterial.Index) { map[x, y] = (byte)had; continue; }
                    if (opt.KeepRoads && Array.IndexOf(RoadMaterials, had) >= 0) { map[x, y] = (byte)had; continue; }
                }

                int chosen = -1;
                foreach (var r in rules)
                {
                    if (!r.Enabled) continue;
                    if (r.Material == DeathMaterial.Index) continue;          // never paint the out-of-bounds material
                    float m = Membership(r, ta, i);
                    if (m <= 0f) continue;
                    // The dither: a cell fully inside the rule (m == 1) always takes it; nearer the edge it takes it
                    // only if its own fixed random value falls under m, which scatters the boundary instead of cutting it.
                    if (m >= 1f || Hash01(x, y, opt.Seed + r.Material * 7919) < m) chosen = r.Material;
                }
                map[x, y] = (byte)(chosen < 0 ? 0 : chosen & 15);
            }
        return map;
    }

    /// <summary>How much this cell belongs to the rule: 1 fully inside, 0 outside, in between across the soft edge.</summary>
    private static float Membership(MaterialRule r, TerrainAnalysis ta, int i)
    {
        float m = 1f;
        m = MathF.Min(m, Band(ta.AboveWater[i], r.MinAboveWater, r.MaxAboveWater, r.EdgeSoftness, 2f));
        if (m <= 0f) return 0f;
        m = MathF.Min(m, Band(ta.Altitude[i], r.MinAltitude, r.MaxAltitude, r.EdgeSoftness, 0.05f));
        if (m <= 0f) return 0f;
        m = MathF.Min(m, Band(ta.SlopeDeg[i], r.MinSlopeDeg, r.MaxSlopeDeg, r.EdgeSoftness, 4f));
        if (m <= 0f) return 0f;
        m = MathF.Min(m, Band(ta.Convexity[i], r.MinConvexity, r.MaxConvexity, r.EdgeSoftness, 0.15f));
        if (m <= 0f) return 0f;
        m = MathF.Min(m, Band(ta.Flow[i], r.MinFlow, r.MaxFlow, r.EdgeSoftness, 0.05f));
        if (m <= 0f) return 0f;
        if (r.MaxShoreMeters < float.PositiveInfinity)
        {
            float shore = ta.ShoreMeters[i];
            if (shore == float.MaxValue) return 0f;                            // no water on this map at all
            m = MathF.Min(m, Band(shore, float.NegativeInfinity, r.MaxShoreMeters, r.EdgeSoftness, ta.MetersPerCell));
            if (m <= 0f) return 0f;
        }
        if (r.Coverage < 1f)
        {
            float patch = ValueNoise(i % ta.Size, i / ta.Size, MathF.Max(r.PatchMeters / ta.MetersPerCell, 1f), r.Material * 31 + 17);
            // Coverage 1 keeps everything, 0 keeps nothing; the noise decides WHICH parts survive in between.
            m *= Math.Clamp((r.Coverage - patch) * 4f + 0.5f, 0f, 1f);
        }
        return m;
    }

    /// <summary>1 inside [min,max], fading to 0 across a feather band outside each end.</summary>
    private static float Band(float v, float min, float max, float softness, float minFeather)
    {
        float span = (float.IsInfinity(min) || float.IsInfinity(max)) ? float.PositiveInfinity : max - min;
        float feather = float.IsInfinity(span) ? minFeather : MathF.Max(span * Math.Clamp(softness, 0f, 1f), minFeather);
        float m = 1f;
        if (!float.IsNegativeInfinity(min))
        {
            if (v < min - feather) return 0f;
            if (v < min) m = MathF.Min(m, (v - (min - feather)) / feather);
        }
        if (!float.IsPositiveInfinity(max))
        {
            if (v > max + feather) return 0f;
            if (v > max) m = MathF.Min(m, ((max + feather) - v) / feather);
        }
        return m;
    }

    /// <summary>A fixed random value in [0,1) for a cell - no state, same answer every run.</summary>
    private static float Hash01(int x, int y, int seed)
    {
        uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1274126177);
        h = (h ^ (h >> 13)) * 1274126177u;
        return ((h ^ (h >> 16)) & 0xFFFFFF) / 16777216f;
    }

    /// <summary>Smooth value noise in [0,1), used for patchiness. <paramref name="cells"/> is the feature size in cells.</summary>
    private static float ValueNoise(int x, int y, float cells, int seed)
    {
        float fx = x / cells, fy = y / cells;
        int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
        float tx = fx - x0, ty = fy - y0;
        tx = tx * tx * (3f - 2f * tx); ty = ty * ty * (3f - 2f * ty);         // smoothstep, so patches have soft sides
        float a = Hash01(x0, y0, seed), b = Hash01(x0 + 1, y0, seed);
        float c = Hash01(x0, y0 + 1, seed), d = Hash01(x0 + 1, y0 + 1, seed);
        return (a + (b - a) * tx) + ((c + (d - c) * tx) - (a + (b - a) * tx)) * ty;
    }
}
