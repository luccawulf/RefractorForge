using System;
using System.Numerics;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;

namespace RefractorForge.Render;

/// <summary>
/// The two integrals that separate a baked lightmap from a stencil: a sun with real ANGULAR SIZE, and how much
/// of the sky a point can actually see.
///
/// <para>What the existing bake does is cast one ray at a point sun and write 1 or 0
/// (<see cref="ObjectLightmapBaker"/>). That is a hard shadow with no penumbra anywhere, and it is measurably
/// not what the retail maps look like - DICE's own object lightmaps carry soft gradients and contact darkening,
/// and average 90-114 where ours are binary. Sub-sampling a texel anti-aliases the shadow EDGE inside that texel
/// but never softens the shadow itself; only sampling the light's own extent does that.</para>
///
/// <para><b>What the format can carry.</b> A Refractor lightmap is not stored irradiance - the engine's shader
/// reads it as <c>Prelight</c>, a multiplier on the direct sun term:
/// <c>saturate(2 * (Prelight * (dif*N.L + secondary*N.BL) + LMambient)) * texture</c>. Both quantities here are
/// therefore legitimate contents for that channel: they are both "how much light reaches this texel". Two honest
/// limits follow, and neither is a bug to be fixed later. Occlusion in the map darkens the DIRECT term only,
/// because the engine adds <c>LMambient</c> after the multiply - so ambient occlusion shows on lit surfaces and
/// cannot darken the ambient floor. And a face with <c>N.L &lt;= 0</c> for both light directions receives only
/// <c>LMambient</c> whatever the map says, so undersides can never be lit.</para>
/// </summary>
public static class LightSampling
{
    /// <summary>The sun's angular diameter seen from Earth, in degrees. Physically it is about half a degree,
    /// which is what makes a shadow sharp at its contact point and soft a few metres out. Larger values read as
    /// haze or an overcast day and are a genuinely useful control rather than an error.</summary>
    public const float SunAngularDiameterDeg = 0.53f;

    /// <summary>
    /// How much of the sun's DISC reaches a point, 0..1 - the penumbra.
    ///
    /// <para>With <paramref name="samples"/> 1, or a zero angular diameter, this is the old binary test and
    /// costs exactly what it always did. Above that, directions are drawn across the sun's disc and averaged,
    /// so a shadow edge widens with the distance to whatever casts it: about a centimetre per metre at the real
    /// angular size. The pattern is a golden-angle spiral rotated per texel by <paramref name="seed"/>, the same
    /// construction the lamp penumbra uses, so a soft edge dithers instead of banding into rings.</para>
    /// </summary>
    /// <param name="objects">The level's geometry. Null falls back to terrain-only occlusion.</param>
    /// <param name="self">The object's OWN geometry, tested per sample alongside the level so that a roof
    /// shadowing its own floor gets the same soft edge as one building shadowing another. Testing it after the
    /// fact instead - multiplying a soft level term by a hard self term - would put a hard edge back in.</param>
    /// <param name="maxH">The terrain's highest point, from <see cref="TerrainShadow.HeightSpan"/>.</param>
    public static float SunVisibility(RayScene? objects, RayScene? self, Heightmap hm, TerrainConfig cfg, float maxH,
                                      Vector3 p, Vec3 sunDir, float angularDiameterDeg, int samples, uint seed)
    {
        var sun = Vector3.Normalize(new Vector3(sunDir.X, sunDir.Y, sunDir.Z));
        samples = Math.Max(1, samples);
        float halfAngle = MathF.Max(0f, angularDiameterDeg) * 0.5f * MathF.PI / 180f;
        if (samples == 1 || halfAngle <= 1e-6f)
            return Clear(objects, self, hm, cfg, maxH, p, sunDir, sun) ? 1f : 0f;

        Basis(sun, out var u, out var v);
        float tanHalf = MathF.Tan(halfAngle);
        float rot = Hash01(seed) * MathF.Tau;

        int seen = 0;
        for (int k = 0; k < samples; k++)
        {
            // Even coverage of the disc for any count: radius by sqrt of the stratum, angle by the golden angle.
            float r = tanHalf * MathF.Sqrt((k + 0.5f) / samples);
            float a = k * 2.399963f + rot;
            var dir = Vector3.Normalize(sun + u * (MathF.Cos(a) * r) + v * (MathF.Sin(a) * r));
            if (Clear(objects, self, hm, cfg, maxH, p, new Vec3(dir.X, dir.Y, dir.Z), dir)) seen++;
        }
        return seen / (float)samples;
    }

    /// <summary>
    /// How much of the SKY a point can see, cosine-weighted about its normal, 0..1.
    ///
    /// <para>This is the ambient-occlusion integral, and it is what puts darkness under eaves, inside doorways
    /// and in the crease where two walls meet - the cues that make a bake read as three-dimensional rather than
    /// as a shadow decal. Cosine weighting is not decoration: it is the correct measure for a diffuse surface,
    /// and it is obtained for free by sampling the disc and lifting it onto the hemisphere.</para>
    ///
    /// <para><paramref name="maxDist"/> bounds how far a ray looks. Unbounded, this is true sky visibility, and a
    /// courtyard reads as dark as a cupboard. Bounded to a metre or two it becomes contact darkening - the
    /// classic "dirt" pass - which is usually what is wanted on top of a sun term. Both are useful; they are the
    /// same integral with a different reach.</para>
    /// </summary>
    public static float SkyVisibility(RayScene? objects, RayScene? self, Heightmap hm, TerrainConfig cfg, float maxH,
                                      Vector3 p, Vector3 n, int samples, float maxDist, uint seed)
    {
        samples = Math.Max(1, samples);
        if (n.LengthSquared() < 1e-12f) return 1f;
        n = Vector3.Normalize(n);
        Basis(n, out var u, out var v);
        float rot = Hash01(seed) * MathF.Tau;
        bool bounded = maxDist > 0f && !float.IsPositiveInfinity(maxDist);

        int open = 0;
        for (int k = 0; k < samples; k++)
        {
            // Concentric disc -> hemisphere: a point at radius r on the disc lifts to height sqrt(1 - r^2),
            // which distributes directions by the cosine of the angle to the normal.
            float r2 = (k + 0.5f) / samples;
            float r = MathF.Sqrt(r2);
            float a = k * 2.399963f + rot;
            var dir = u * (MathF.Cos(a) * r) + v * (MathF.Sin(a) * r) + n * MathF.Sqrt(MathF.Max(0f, 1f - r2));
            float len = dir.Length();
            if (len < 1e-6f) { open++; continue; }
            dir /= len;

            float reach = bounded ? maxDist : float.MaxValue;
            bool blocked = (objects is not null && objects.Occluded(p, dir, reach))
                           || (self is not null && self.Occluded(p, dir, reach));
            // Terrain only matters for a ray that can actually reach it; a short contact-darkening ray cannot.
            if (!blocked && !bounded)
                blocked = !TerrainShadow.PointLit(p.X, p.Y, p.Z, new Vec3(dir.X, dir.Y, dir.Z), hm, cfg, maxH);
            if (!blocked) open++;
        }
        return open / (float)samples;
    }

    /// <summary>
    /// Light that reaches a point after bouncing off other surfaces - the term that makes a shadowed wall pick up
    /// the colour of the sunlit one opposite it.
    ///
    /// <para>Path-traced with cosine-weighted directions, which is what makes the estimator a plain average: the
    /// cosine in the rendering equation and the cosine in the sampling density cancel, so no weight has to be
    /// carried. Each bounce ray finds a surface, works out how much sun that surface receives, and brings back
    /// that light multiplied by the surface's albedo.</para>
    ///
    /// <para><b>What this can and cannot do in this format.</b> The lightmap multiplies the engine's direct sun
    /// term, so bounce can only LIFT a texel that the sun already reaches at some angle - it cannot light a face
    /// the shader has already zeroed with <c>N.L</c>, and it saturates at 1. On BF1942, where the map is 24-bit
    /// RGB, the colour survives to the game and real colour bleeding is visible. On BfVietnam the shader reads
    /// only the blue channel, so the caller folds this to luma and what survives is the brightening, not the hue.
    /// Neither is a defect to fix later; both follow from the shader.</para>
    /// </summary>
    /// <param name="sunColour">The level renderer's diffuse colour - what a fully lit white surface returns.</param>
    /// <param name="depth">Bounces to follow. 1 is a single bounce; 2 catches light that has crossed a courtyard
    /// twice. Beyond 2 the contribution is far below the 8-bit quantisation of the map.</param>
    /// <param name="maxDist">How far a bounce ray looks. Bounded, because light arriving from 500 m away is
    /// both negligible and expensive.</param>
    /// <param name="sunSamples">Shadow rays cast at each BOUNCE HIT to see how lit that surface is. One is
    /// normally right: whether the wall a bounce came off has a soft or a hard shadow edge on it cannot be seen
    /// after averaging the incoming directions, and this is the term that dominates the whole bake - at 24 bounce
    /// rays each re-running a 32-sample soft-sun test, it was 792 of the 872 rays a texel cast.</param>
    public static Vector3 Bounce(RayScene? scene, RayScene? self, Heightmap hm, TerrainConfig cfg, float maxH,
                                 Vector3 p, Vector3 n, Vec3 sunDir, Vector3 sunColour,
                                 int samples, int depth, float maxDist, uint seed,
                                 float sunAngularDiameterDeg = 0f, int sunSamples = 1)
    {
        if (samples <= 0 || depth <= 0 || (scene is null && self is null)) return Vector3.Zero;
        if (n.LengthSquared() < 1e-12f) return Vector3.Zero;
        n = Vector3.Normalize(n);
        Basis(n, out var u, out var v);
        float rot = Hash01(seed) * MathF.Tau;
        float reach = maxDist > 0f ? maxDist : float.MaxValue;

        var sum = Vector3.Zero;
        for (int k = 0; k < samples; k++)
        {
            float r2 = (k + 0.5f) / samples;
            float r = MathF.Sqrt(r2);
            float a = k * 2.399963f + rot;
            var dir = u * (MathF.Cos(a) * r) + v * (MathF.Sin(a) * r) + n * MathF.Sqrt(MathF.Max(0f, 1f - r2));
            float len = dir.Length();
            if (len < 1e-6f) continue;
            dir /= len;

            if (!TraceNearest(scene, self, p, dir, reach, out var hit)) continue;   // a miss is sky, handled by SkyFill

            // How much sun the surface we landed on receives, and therefore how much it can pass on.
            float ndl = MathF.Max(0f, Vector3.Dot(hit.Normal, new Vector3(sunDir.X, sunDir.Y, sunDir.Z)));
            if (ndl > 0f)
            {
                float vis = SunVisibility(scene, self, hm, cfg, maxH, hit.Point, sunDir,
                                          sunAngularDiameterDeg, sunSamples, seed ^ (uint)(k * 2654435761u));
                if (vis > 0f) sum += hit.Albedo * sunColour * (ndl * vis);
            }

            // One more bounce, from the surface we landed on, with a quarter of the rays - the second bounce is
            // a small correction and does not deserve the same budget as the first.
            if (depth > 1)
                sum += hit.Albedo * Bounce(scene, self, hm, cfg, maxH, hit.Point, hit.Normal, sunDir, sunColour,
                                           Math.Max(1, samples / 4), depth - 1, reach,
                                           seed ^ (uint)(k * 40503u + 7u), sunAngularDiameterDeg, sunSamples);
        }
        return sum / samples;
    }

    /// <summary>The nearer of two scenes, so an object's own geometry and the level around it are one surface.</summary>
    private static bool TraceNearest(RayScene? a, RayScene? b, Vector3 p, Vector3 dir, float reach, out RayScene.Hit hit)
    {
        RayScene.Hit na = default, nb = default;
        bool ha = a is not null && a.Trace(p, dir, out na, reach);
        bool hb = b is not null && b.Trace(p, dir, out nb, reach);
        if (ha && hb) { hit = na.Distance <= nb.Distance ? na : nb; return true; }
        if (ha) { hit = na; return true; }
        if (hb) { hit = nb; return true; }
        hit = default; return false;
    }

    /// <summary>Is the straight line from a point toward <paramref name="dir"/> clear of the level and the terrain?</summary>
    private static bool Clear(RayScene? objects, RayScene? self, Heightmap hm, TerrainConfig cfg, float maxH,
                              Vector3 p, Vec3 dirV, Vector3 dir)
        => TerrainShadow.PointLit(p.X, p.Y, p.Z, dirV, hm, cfg, maxH)
           && (objects is null || !objects.Occluded(p, dir))
           && (self is null || !self.Occluded(p, dir));

    /// <summary>An orthonormal pair perpendicular to <paramref name="n"/>. The axis picked to cross against is
    /// whichever <paramref name="n"/> is least aligned with, so the cross product never collapses.</summary>
    private static void Basis(Vector3 n, out Vector3 u, out Vector3 v)
    {
        var pick = MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        u = Vector3.Normalize(Vector3.Cross(n, pick));
        v = Vector3.Cross(n, u);
    }

    /// <summary>A cheap deterministic hash to 0..1. Deterministic matters: a bake that cannot be reproduced
    /// cannot be regression-tested, and two runs of the same scene must give the same file.</summary>
    internal static float Hash01(uint x)
    {
        x ^= x >> 16; x *= 0x7feb352d;
        x ^= x >> 15; x *= 0x846ca68b;
        x ^= x >> 16;
        return (x & 0xFFFFFF) / 16777216f;
    }
}
