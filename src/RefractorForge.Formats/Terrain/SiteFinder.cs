using RefractorForge.Formats.Con;

namespace RefractorForge.Formats.Terrain;

/// <summary>What the ground is doing at one spot.</summary>
/// <param name="Height">Metres above sea level.</param>
/// <param name="SlopeDeg">Steepness, from a central difference of the height field.</param>
/// <param name="UnderWater">True when the ground is below the level's water line.</param>
/// <param name="DepthBelowWater">How far under the water line, or 0 on dry land.</param>
/// <param name="Material">Painted material index, or -1 when the level has no material map.</param>
public readonly record struct TerrainProbe(float Height, float SlopeDeg, bool UnderWater, float DepthBelowWater, int Material);

/// <summary>A patch of ground worth building on.</summary>
/// <param name="X">Centre, world metres.</param>
/// <param name="Z">Centre, world metres.</param>
/// <param name="Radius">How far the flat ground extends before the constraints break.</param>
/// <param name="Height">Mean height over the patch.</param>
/// <param name="MaxSlopeDeg">Steepest single sample inside it. Informational: one ditch or hedgerow bank makes this
/// large on ground that is otherwise perfectly level, so it is reported rather than used to reject.</param>
/// <param name="HeightSpread">Highest minus lowest, in metres — the number that decides whether a village looks
/// terraced or level.</param>
/// <param name="SteepFraction">How much of the patch is steeper than the slope limit, 0..1. THIS is the usable
/// slope measure: a field crossed by one bank scores near zero, a hillside scores near one.</param>
public readonly record struct BuildSite(float X, float Z, float Radius, float Height, float MaxSlopeDeg,
                                        float HeightSpread, float SteepFraction);

/// <summary>
/// Answers "where can I build?" against the height field.
///
/// This exists because placing without it is guesswork: a village generated on ground that looks fine in plan can
/// come out terraced down eighteen metres of hillside. The finder walks a coarse grid, measures each candidate for
/// slope, height spread, water and clearance from what is already placed, and returns the best patches with
/// non-maximum suppression so the results are distinct places rather than a cluster of neighbours.
/// </summary>
public static class SiteFinder
{
    /// <summary>Sample the ground at a world position.</summary>
    public static TerrainProbe Probe(Heightmap hm, TerrainConfig cfg, float x, float z, MaterialMap? material = null)
    {
        float sp = cfg.HorizontalSpacing <= 0 ? 1f : cfg.HorizontalSpacing;
        float h = HeightAt(hm, cfg, x, z);
        float slope = ObjectScatter.SlopeDegrees((ax, az) => HeightAt(hm, cfg, ax, az), x, z, sp, cfg.WorldSize);

        int mat = -1;
        if (material is not null)
        {
            // The material map is its own grid; sample it by fraction of the world, not by heightmap cell.
            int mx = Math.Clamp((int)(x / MathF.Max(cfg.WorldSize, 1f) * material.Width), 0, material.Width - 1);
            int mz = Math.Clamp((int)(z / MathF.Max(cfg.WorldSize, 1f) * material.Height), 0, material.Height - 1);
            mat = material[mx, mz];
        }

        float below = cfg.WaterLevel - h;
        return new TerrainProbe(h, slope, below > 0f, MathF.Max(below, 0f), mat);
    }

    /// <summary>Height in metres at a world position, matching the editor's nearest-sample convention.</summary>
    public static float HeightAt(Heightmap hm, TerrainConfig cfg, float x, float z)
    {
        float sp = cfg.HorizontalSpacing <= 0 ? 1f : cfg.HorizontalSpacing;
        int gx = Math.Clamp((int)(x / sp), 0, hm.Width - 1);
        int gz = Math.Clamp((int)(z / sp), 0, hm.Height - 1);
        return cfg.HeightToMeters(hm[gx, gz]);
    }

    /// <summary>Find patches of ground that satisfy the constraints, best first.</summary>
    /// <param name="radius">How much flat ground is wanted, in metres.</param>
    /// <param name="maxSlopeDeg">What counts as "steep" when measuring <see cref="BuildSite.SteepFraction"/>.</param>
    /// <param name="maxSteepFraction">How much of the patch may exceed <paramref name="maxSlopeDeg"/> and still
    /// pass. Judging a site by its single steepest cell rejects good ground for one ditch, which is why this is a
    /// fraction rather than a maximum.</param>
    /// <param name="maxSpread">Largest height difference tolerated across it, in metres. This is the constraint
    /// that actually keeps a settlement level; slope alone passes a long even gradient.</param>
    /// <param name="clearOf">Keep this far from anything in <paramref name="occupied"/>, in metres.</param>
    public static List<BuildSite> Find(
        Heightmap hm, TerrainConfig cfg, float radius, float maxSlopeDeg, float maxSpread,
        bool avoidWater = true, float waterClearance = 1f, int max = 8,
        IEnumerable<(float X, float Z)>? occupied = null, float clearOf = 0f,
        float minX = 0f, float minZ = 0f, float maxX = float.MaxValue, float maxZ = float.MaxValue,
        float maxSteepFraction = 0.05f)
    {
        var found = new List<BuildSite>();
        if (radius <= 0f || max <= 0) return found;

        float sp = cfg.HorizontalSpacing <= 0 ? 1f : cfg.HorizontalSpacing;
        float ws = cfg.WorldSize;
        maxX = MathF.Min(maxX, ws); maxZ = MathF.Min(maxZ, ws);
        minX = MathF.Max(minX, 0f); minZ = MathF.Max(minZ, 0f);

        var busy = occupied?.ToList() ?? new List<(float X, float Z)>();
        float clear2 = clearOf * clearOf;

        // Slope once per heightmap cell rather than once per sample. A candidate disc overlaps its neighbours
        // heavily, so recomputing per candidate did the same finite differences thousands of times over.
        var slope = new float[hm.Width * hm.Height];
        for (int gz = 0; gz < hm.Height; gz++)
            for (int gx = 0; gx < hm.Width; gx++)
                slope[gz * hm.Width + gx] = ObjectScatter.SlopeDegrees(
                    (ax, az) => HeightAt(hm, cfg, ax, az), gx * sp, gz * sp, sp, ws);

        float SlopeAt(float x, float z)
        {
            int gx = Math.Clamp((int)(x / sp), 0, hm.Width - 1);
            int gz = Math.Clamp((int)(z / sp), 0, hm.Height - 1);
            return slope[gz * hm.Width + gx];
        }

        // Step half a radius so a good patch cannot fall between candidates, but the scan stays cheap on a 2 km map.
        float step = MathF.Max(radius * 0.5f, sp);
        var cand = new List<BuildSite>();

        for (float cz = minZ + radius; cz <= maxZ - radius; cz += step)
            for (float cx = minX + radius; cx <= maxX - radius; cx += step)
            {
                if (clearOf > 0f && busy.Any(o => (o.X - cx) * (o.X - cx) + (o.Z - cz) * (o.Z - cz) < clear2)) continue;

                float lo = float.MaxValue, hi = float.MinValue, sum = 0f, worst = 0f;
                int n = 0, steep = 0;
                bool dry = true;

                // Sample the disc on the heightmap's own grid - no point testing finer than the data. Slope and
                // spread are MEASURED rather than used to reject, so a caller whose limits nothing meets can still
                // be told what the best ground actually looks like. Water is the one hard reject: no amount of
                // "best available" makes a submerged village useful.
                for (float dz = -radius; dz <= radius && dry; dz += sp)
                    for (float dx = -radius; dx <= radius; dx += sp)
                    {
                        if (dx * dx + dz * dz > radius * radius) continue;
                        float x = cx + dx, z = cz + dz;
                        float h = HeightAt(hm, cfg, x, z);
                        if (avoidWater && h < cfg.WaterLevel + waterClearance) { dry = false; break; }

                        float s = SlopeAt(x, z);
                        if (h < lo) lo = h;
                        if (h > hi) hi = h;
                        if (s > worst) worst = s;
                        if (s > maxSlopeDeg) steep++;
                        sum += h; n++;
                    }

                if (!dry || n == 0) continue;
                cand.Add(new BuildSite(cx, cz, radius, sum / n, worst, hi - lo, steep / (float)n));
            }

        // Prefer sites that meet the caller's limits; if none do, fall back to the flattest ground there is. The
        // caller can see from HeightSpread and MaxSlopeDeg how far off it is and decide - which beats "no".
        var passing = cand.Where(c => c.HeightSpread <= maxSpread && c.SteepFraction <= maxSteepFraction).ToList();
        var ranked = (passing.Count > 0 ? passing : cand)
            .OrderBy(c => c.HeightSpread).ThenBy(c => c.SteepFraction);

        // Drop anything overlapping a better site, so the answers are distinct PLACES rather than one good patch
        // reported nine times from nine neighbouring centres.
        foreach (var c in ranked)
        {
            if (found.Count >= max) break;
            float sep = radius * 1.5f;
            if (found.Any(f => (f.X - c.X) * (f.X - c.X) + (f.Z - c.Z) * (f.Z - c.Z) < sep * sep)) continue;
            found.Add(c);
        }
        return found;
    }

    /// <summary>True when a site meets the limits it was searched with (as opposed to being a best-effort fallback).</summary>
    public static bool Meets(BuildSite s, float maxSpread, float maxSteepFraction)
        => s.HeightSpread <= maxSpread && s.SteepFraction <= maxSteepFraction;
}
