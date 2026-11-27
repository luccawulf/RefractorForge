using System;
using System.Numerics;
using RefractorForge.Formats.Terrain;

namespace RefractorForge.Render;

/// <summary>
/// The light placed lamps deliver to a point, shadowed by the same scene the sun and the sky are traced in.
///
/// <para>The lamp MODEL is the one the Night Lighting window has always used - reach, falloff, cone, a source with a
/// size (<see cref="PointLight"/>, <see cref="NightBake.Lamp"/>) - so a rig someone has already tuned means the same
/// thing here. What changes is what stands in the way: one tree of triangles that holds the terrain, every object
/// and the holes in fences and leaves (<see cref="LightScene"/>), instead of a grid of solid triangles plus a
/// separate march over the height-field. A lamp behind a chain-link fence now throws the fence's shadow.</para>
///
/// <para><b>Units.</b> A lamp is authored on the screen's scale: a value of 1 in a channel is a lamp that, alone,
/// draws a surface at its texture's full brightness. Real light adds in LINEAR units, so that value crosses over
/// through <see cref="DaylightModel.FromDisplay"/> before it joins the moon or another lamp. One lamp in darkness
/// then looks exactly as it was authored; a lamp over moonlit ground lifts it the way light does, and two pools
/// that overlap brighten without burning out.</para>
/// </summary>
public static class LampLight
{
    /// <summary>
    /// What every enabled lamp delivers to <paramref name="p"/> on a surface facing <paramref name="n"/>, on the
    /// screen's scale (see the class remarks), with shadows.
    /// </summary>
    /// <param name="samples">Shadow rays per lamp, spread over its source: 1 is a hard shadow.</param>
    /// <param name="ground">Honour the lamps flagged for the ground (true), for objects (false), or every enabled
    /// lamp (null) - which is what light that is about to bounce wants.</param>
    public static Vector3 Display(RayScene? scene, LightRig rig, Vector3 p, Vector3 n, int samples, bool? ground, uint seed,
                                  float wrap = 0.15f)
    {
        var sum = Vector3.Zero;
        foreach (var l in rig.Lights)
        {
            if (ground is { } g && (g ? !l.OnGround : !l.OnObjects)) continue;
            float a = l.Attenuation(p.X, p.Y, p.Z);
            if (a <= 0f) continue;
            var toL = new Vector3(l.Position.X - p.X, l.Position.Y - p.Y, l.Position.Z - p.Z);
            float len = toL.Length();
            float raw = len > 1e-4f ? Vector3.Dot(n, toL / len) : 1f;
            // A lamp behind a face never lights it: the wrap softens the terminator on the lit side only.
            if (raw <= 0f) continue;
            float vis = Visibility(scene, p, n, l, samples, seed);
            if (vis <= 0f) continue;
            sum += new Vector3(l.ColorR, l.ColorG, l.ColorB) * (a * (raw * (1f - wrap) + wrap) * vis);
        }
        return sum;
    }

    /// <summary>How much of a lamp's source a point can see, 0..1.</summary>
    public static float Visibility(RayScene? scene, Vector3 p, Vector3 n, PointLight l, int samples, uint seed)
    {
        if (scene is null || !l.CastsShadows) return 1f;
        var lp = new Vector3(l.Position.X, l.Position.Y, l.Position.Z);
        samples = Math.Max(1, samples);
        float size = MathF.Max(0f, l.SourceSize);
        var toL = lp - p;
        float dist = toL.Length();
        if (dist < 1e-4f) return 1f;
        if (size <= 0.001f) return Clear(scene, p, n, lp) ? 1f : 0f;

        var dir = toL / dist;
        var u = Vector3.Normalize(Vector3.Cross(dir, MathF.Abs(dir.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        var v = Vector3.Cross(dir, u);
        float rot = LightTracer.Hash01(seed) * MathF.Tau;
        if (samples == 1)
        {
            // One ray, at one point of the source chosen by the seed: neighbours and sub-samples differ, so they
            // average to the soft edge a single centred ray would never give.
            float r1 = size * MathF.Sqrt(LightTracer.Hash01(LightTracer.Mix(seed, 31u)));
            return Clear(scene, p, n, lp + u * (MathF.Cos(rot) * r1) + v * (MathF.Sin(rot) * r1)) ? 1f : 0f;
        }
        int seen = 0;
        for (int k = 0; k < samples; k++)
        {
            float r = size * MathF.Sqrt((k + 0.5f) / samples);
            float a = k * 2.399963f + rot;
            if (Clear(scene, p, n, lp + u * (MathF.Cos(a) * r) + v * (MathF.Sin(a) * r))) seen++;
        }
        return seen / (float)samples;
    }

    /// <summary>Is the line from a surface point to a point on a lamp free? The ray starts a hair off the surface
    /// and stops short of the lamp, which sits in or on a fixture and would otherwise shadow itself
    /// (<see cref="NightBake.FixtureRadius"/>).</summary>
    public static bool Clear(RayScene scene, Vector3 p, Vector3 n, Vector3 lightPos)
    {
        var o = p + n * 0.03f;
        var d = lightPos - o;
        float len = d.Length();
        if (len < 1e-4f) return true;
        float reach = len - MathF.Max(0.05f, NightBake.FixtureRadius);
        if (reach <= 0f) return true;
        return !scene.Occluded(o, d / len, reach, 0.01f);
    }

    /// <summary>Does any enabled lamp reach this point at all? A cheap test for skipping work.</summary>
    public static bool InReach(LightRig rig, float wx, float wz, bool ground)
    {
        foreach (var l in rig.Lights)
        {
            if (!l.Enabled || l.Intensity <= 0f || l.Radius <= 0f) continue;
            if (ground ? !l.OnGround : !l.OnObjects) continue;
            float dx = l.Position.X - wx, dz = l.Position.Z - wz;
            if (dx * dx + dz * dz <= l.Radius * l.Radius) return true;
        }
        return false;
    }
}
