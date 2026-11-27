using System;
using System.Numerics;
using RefractorForge.Formats.Terrain;

namespace RefractorForge.Render;

/// <summary>
/// How much light reaches a point, traced: the sun through its disc, the sky as a dome with a colour, and both
/// again after they have bounced off the ground and the walls.
///
/// <para>This is the reference - what the level would look like if the engine could draw light. Nothing here knows
/// about lightmaps or Init.con; the result is LINEAR irradiance in <see cref="Daylight"/>'s units (the sun delivers
/// luma 1 to a surface facing it), split into the three parts a bake treats differently:</para>
/// <list type="bullet">
/// <item><b>Sun</b> - the fraction of the disc that is visible. Kept as a visibility because its edge is the one
/// sharp thing in the picture: it is never blurred with the rest.</item>
/// <item><b>Sky</b> - what arrives straight from the dome. Smooth; this is where ambient occlusion lives.</item>
/// <item><b>Indirect</b> - everything that touched a surface first. Smooth and coloured.</item>
/// </list>
///
/// <para><b>The estimator.</b> Directions are drawn over the hemisphere with a density proportional to the cosine,
/// which cancels the cosine in the integral, so irradiance is pi times the plain average of what the rays bring
/// back. A ray that reaches the sky brings the sky's radiance. One that lands on a surface brings what that surface
/// sends back: its albedo times the light IT receives - the sun, tested with one shadow ray, plus one further ray
/// drawn the same way. Followed for a fixed number of bounces; a path still on a surface after the last one simply
/// ends, which loses a little light and never invents any.</para>
/// </summary>
public static class LightTracer
{
    /// <summary>How many rays a sample point casts.</summary>
    /// <param name="SunSamples">Directions across the sun's disc. 1 tests ONE point on it, chosen by the seed, so
    /// sub-samples that each cast one still average to a penumbra. 0 skips the sun altogether, for a caller that
    /// samples it some other way.</param>
    /// <param name="HemiSamples">Directions over the hemisphere, shared by the sky and the indirect light.</param>
    /// <param name="Bounces">Surfaces a path may touch after leaving the point. 0 is sky only; 2 is where a
    /// courtyard stops getting brighter in 8 bits.</param>
    public readonly record struct Budget(int SunSamples, int HemiSamples, int Bounces);

    /// <summary>What reaches a point. <see cref="Sun"/> is a visibility; the other two are linear irradiance.</summary>
    public readonly record struct Light(float Sun, Vector3 Sky, Vector3 Indirect)
    {
        /// <summary>The whole of it as linear irradiance on a surface facing <paramref name="n"/>.</summary>
        public Vector3 Irradiance(DaylightModel day, Vector3 n)
            => day.SunIrradiance * (MathF.Max(0f, Vector3.Dot(n, day.ToSun)) * Sun) + Sky + Indirect;
    }

    /// <summary>
    /// Trace the light reaching <paramref name="p"/> on a surface facing <paramref name="n"/>.
    /// </summary>
    /// <param name="scene">The level (see <see cref="LightScene"/>). Null is open air: nothing occludes.</param>
    /// <param name="seed">Decorrelates neighbouring samples. The same seed gives the same answer - a bake that
    /// cannot be reproduced cannot be tested.</param>
    /// <param name="lamps">Placed lamps whose light should BOUNCE: the pool a street lamp throws on the ground
    /// lights the wall beside it and the eaves above. The lamps' direct light on the point itself is not included -
    /// a bake adds that separately (<see cref="LampLight.Display"/>), because it has edges this smooth part must
    /// not be filtered together with.</param>
    public static Light Gather(RayScene? scene, DaylightModel day, Vector3 p, Vector3 n, in Budget budget, uint seed,
                               LightRig? lamps = null)
    {
        if (n.LengthSquared() < 1e-12f) return new Light(1f, Vector3.Zero, Vector3.Zero);
        n = Vector3.Normalize(n);

        float sun = budget.SunSamples > 0 && Vector3.Dot(n, day.ToSun) > 0f
            ? SunVisibility(scene, day, p, budget.SunSamples, seed)
            : 0f;                                   // facing away: the sun term is zero whatever is in the way

        int count = Math.Max(0, budget.HemiSamples);
        if (count == 0) return new Light(sun, Vector3.Zero, Vector3.Zero);

        Basis(n, out var u, out var v);
        float rot = Hash01(seed ^ 0x9E3779B9u) * MathF.Tau;
        float bounceGain = MathF.Max(day.Settings.BounceStrength, 0f);
        var sky = Vector3.Zero; var indirect = Vector3.Zero;

        for (int k = 0; k < count; k++)
        {
            // Stratified over the disc and lifted onto the hemisphere: cosine-weighted, evenly spread for any count.
            float r2 = (k + 0.5f) / count;
            float r = MathF.Sqrt(r2);
            float a = k * 2.399963f + rot;
            var dir = Vector3.Normalize(u * (MathF.Cos(a) * r) + v * (MathF.Sin(a) * r) + n * MathF.Sqrt(MathF.Max(0f, 1f - r2)));

            if (scene is null || !scene.Trace(p, dir, out var hit))
            {
                sky += day.Environment(dir);
                continue;
            }

            // The ray landed on something. Follow the light back along the path.
            var throughput = Vector3.One;
            var radiance = Vector3.Zero;
            uint pathSeed = Mix(seed, (uint)k);
            for (int depth = 0; depth < budget.Bounces; depth++)
            {
                throughput *= hit.Albedo * bounceGain;
                if (throughput.X + throughput.Y + throughput.Z < 1e-4f) break;

                // The sun on the surface we are standing on: one ray at a point on its disc.
                float ndl = Vector3.Dot(hit.Normal, day.ToSun);
                if (ndl > 0f)
                {
                    var toSun = SunDiscDirection(day, Hash01(Mix(pathSeed, (uint)(depth * 2 + 1))), Hash01(Mix(pathSeed, (uint)(depth * 2 + 2))));
                    if (!scene.Occluded(hit.Point, toSun))
                        radiance += throughput * day.SunIrradiance * (ndl / MathF.PI);
                }

                // The lamps on that surface, each with one shadow ray.
                if (lamps is not null)
                {
                    var lamp = LampLight.Display(scene, lamps, hit.Point, hit.Normal, 1, null, Mix(pathSeed, (uint)(depth * 2 + 201)));
                    if (lamp.X + lamp.Y + lamp.Z > 0f) radiance += throughput * day.FromDisplay(lamp) * (1f / MathF.PI);
                }

                // And one direction for everything else that surface receives.
                Basis(hit.Normal, out var hu, out var hv);
                float e1 = Hash01(Mix(pathSeed, (uint)(depth * 2 + 101))), e2 = Hash01(Mix(pathSeed, (uint)(depth * 2 + 102)));
                float hr = MathF.Sqrt(e1), ha = e2 * MathF.Tau;
                var next = Vector3.Normalize(hu * (MathF.Cos(ha) * hr) + hv * (MathF.Sin(ha) * hr) + hit.Normal * MathF.Sqrt(MathF.Max(0f, 1f - e1)));
                if (!scene.Trace(hit.Point, next, out var nextHit))
                {
                    radiance += throughput * day.Environment(next);
                    break;
                }
                hit = nextHit;
            }
            indirect += radiance;
        }
        float scale = MathF.PI / count;
        return new Light(sun, sky * scale, indirect * scale);
    }

    /// <summary>The fraction of the sun's disc a point can see, 0..1 - 1 in the open, 0 in full shadow, and the
    /// penumbra in between, which widens with the distance to whatever casts it.</summary>
    public static float SunVisibility(RayScene? scene, DaylightModel day, Vector3 p, int samples, uint seed)
    {
        if (scene is null) return 1f;
        samples = Math.Max(1, samples);
        float halfAngle = MathF.Max(0f, day.Settings.SunAngularDiameterDeg) * 0.5f * MathF.PI / 180f;
        if (halfAngle <= 1e-6f) return scene.Occluded(p, day.ToSun) ? 0f : 1f;
        if (samples == 1)
            return scene.Occluded(p, SunDiscDirection(day, Hash01(seed), Hash01(Mix(seed, 17u)))) ? 0f : 1f;

        Basis(day.ToSun, out var u, out var v);
        float tanHalf = MathF.Tan(halfAngle);
        float rot = Hash01(seed) * MathF.Tau;
        int seen = 0;
        for (int k = 0; k < samples; k++)
        {
            float r = tanHalf * MathF.Sqrt((k + 0.5f) / samples);
            float a = k * 2.399963f + rot;
            var dir = Vector3.Normalize(day.ToSun + u * (MathF.Cos(a) * r) + v * (MathF.Sin(a) * r));
            if (!scene.Occluded(p, dir)) seen++;
        }
        return seen / (float)samples;
    }

    /// <summary>A direction toward a point on the sun's disc chosen by two numbers in 0..1.</summary>
    private static Vector3 SunDiscDirection(DaylightModel day, float e1, float e2)
    {
        float halfAngle = MathF.Max(0f, day.Settings.SunAngularDiameterDeg) * 0.5f * MathF.PI / 180f;
        if (halfAngle <= 1e-6f) return day.ToSun;
        Basis(day.ToSun, out var u, out var v);
        float r = MathF.Tan(halfAngle) * MathF.Sqrt(e1), a = e2 * MathF.Tau;
        return Vector3.Normalize(day.ToSun + u * (MathF.Cos(a) * r) + v * (MathF.Sin(a) * r));
    }

    /// <summary>An orthonormal pair perpendicular to <paramref name="n"/>.</summary>
    private static void Basis(Vector3 n, out Vector3 u, out Vector3 v)
    {
        var pick = MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        u = Vector3.Normalize(Vector3.Cross(n, pick));
        v = Vector3.Cross(n, u);
    }

    /// <summary>Two numbers into one well-mixed seed.</summary>
    public static uint Mix(uint a, uint b)
    {
        uint x = a ^ (b + 0x9E3779B9u + (a << 6) + (a >> 2));
        x ^= x >> 16; x *= 0x7feb352d;
        x ^= x >> 15; x *= 0x846ca68b;
        x ^= x >> 16;
        return x;
    }

    /// <summary>A seed to 0..1.</summary>
    public static float Hash01(uint x)
    {
        x ^= x >> 16; x *= 0x7feb352d;
        x ^= x >> 15; x *= 0x846ca68b;
        x ^= x >> 16;
        return (x & 0xFFFFFF) / 16777216f;
    }
}
