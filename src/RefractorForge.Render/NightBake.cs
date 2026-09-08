using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using RefractorForge.Formats.Terrain;

namespace RefractorForge.Render;

/// <summary>
/// Lamp light with real shadows: what the placed lights deliver to a point once the buildings, the props and the
/// terrain between it and each lamp are taken into account.
///
/// Neither Refractor engine has a runtime point light (a frame capture of BfVietnam shows none; BF1942's `Light`
/// object class stores a colour that nothing in the executable ever reads), so a night map is lit entirely by what
/// is BAKED - into the ground texture and into the per-object lightmaps - plus an additive sprite for the bulb. That
/// is how GC_Bespin_Night and Dystopia City were made, and it is what this class computes.
///
/// Every lamp is treated as a small DISC of the light's <see cref="PointLight.SourceSize"/>, sampled across, so a
/// wall's shadow on the ground is sharp at its foot and soft further out - the penumbra is most of what separates a
/// lit street from a stencil. Occlusion is the level's own geometry (one shared <see cref="MeshOccluder"/> over
/// every placed object, the same one the sun bake casts with) and the heightmap.
/// </summary>
public static class NightBake
{
    /// <summary>What every lamp test needs: the geometry that can stand between a point and a light.</summary>
    public sealed class Scene
    {
        public MeshOccluder? Objects { get; init; }
        public Heightmap Hm { get; init; } = null!;
        public TerrainConfig Cfg { get; init; } = null!;
        public MeshOccluder.Cursor? NewCursor() => Objects?.NewCursor();
    }

    public static Scene Build(Heightmap hm, TerrainConfig cfg, IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C)>? tris)
        => new() { Hm = hm, Cfg = cfg, Objects = tris is { Count: > 0 } ? MeshOccluder.Build(tris) : null };

    /// <summary>
    /// How much of light <paramref name="l"/> a point can see, 0..1. One sample is a hard shadow from the lamp's
    /// centre; more spread over the source disc and average, which is the penumbra.
    /// <paramref name="seed"/> decorrelates the sample pattern between neighbouring texels so a soft edge dithers
    /// rather than banding.
    /// </summary>
    public static float Visibility(Scene s, Vector3 p, Vector3 n, PointLight l, MeshOccluder.Cursor? cur, int samples, uint seed)
    {
        var lp = new Vector3(l.Position.X, l.Position.Y, l.Position.Z);
        if (!l.CastsShadows) return 1f;
        samples = Math.Max(1, samples);
        float size = MathF.Max(0f, l.SourceSize);
        if (samples == 1 || size <= 0.001f) return Clear(s, p, n, lp, cur) ? 1f : 0f;

        // A basis across the disc, perpendicular to the direction to the lamp.
        var toL = lp - p;
        float dist = toL.Length();
        if (dist < 1e-4f) return 1f;
        var dir = toL / dist;
        var u = Vector3.Normalize(Vector3.Cross(dir, MathF.Abs(dir.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        var v = Vector3.Cross(dir, u);

        // Golden-angle spiral: even coverage for any count, rotated per texel by the seed.
        float rot = Hash01(seed) * MathF.Tau;
        int seen = 0;
        for (int k = 0; k < samples; k++)
        {
            float r = size * MathF.Sqrt((k + 0.5f) / samples);
            float a = k * 2.399963f + rot;
            var lq = lp + u * (MathF.Cos(a) * r) + v * (MathF.Sin(a) * r);
            if (Clear(s, p, n, lq, cur)) seen++;
        }
        return seen / (float)samples;
    }

    /// <summary>Is the straight line from a surface point to a light position unobstructed?</summary>
    public static bool Clear(Scene s, Vector3 p, Vector3 n, Vector3 lightPos, MeshOccluder.Cursor? cur)
    {
        var d = lightPos - p;
        float len = d.Length();
        if (len < 1e-4f) return true;
        d /= len;
        // Start the ray a hair off the surface along its normal, so the surface never shadows itself and a lamp
        // hung just under a ceiling still lights the floor beneath it.
        var o = p + n * 0.03f;
        if (s.Objects is not null && cur is not null && s.Objects.Occluded(o, d, len - 0.05f, cur, 0.01f)) return false;
        return TerrainClear(s.Hm, s.Cfg, o, lightPos);
    }

    /// <summary>
    /// The light every enabled lamp in the rig delivers to a point, as colour, with shadows and the surface's slope.
    /// The Lambert term is WRAPPED a little, as the viewport preview does, so a bare lamp lights the ground around
    /// it rather than only the half facing it; without the wrap a flat floor under a bulb reads as a hard disc.
    /// </summary>
    /// <param name="ground">Which lights to honour: the ones flagged for the ground, or the ones for objects.</param>
    public static Vector3 Lamp(Scene s, Vector3 p, Vector3 n, LightRig rig, MeshOccluder.Cursor? cur, int samples,
                               bool ground, uint seed, float wrap = 0.15f)
    {
        var sum = Vector3.Zero;
        foreach (var l in rig.Lights)
        {
            if (ground ? !l.OnGround : !l.OnObjects) continue;
            float a = l.Attenuation(p.X, p.Y, p.Z);
            if (a <= 0f) continue;
            var toL = new Vector3(l.Position.X - p.X, l.Position.Y - p.Y, l.Position.Z - p.Z);
            float len = toL.Length();
            float ndl = len > 1e-4f ? MathF.Max(Vector3.Dot(n, toL / len), 0f) : 1f;
            ndl = ndl * (1f - wrap) + wrap;
            if (ndl <= 0f) continue;
            float vis = Visibility(s, p, n, l, cur, samples, seed);
            if (vis <= 0f) continue;
            float k = a * ndl * vis;
            sum += new Vector3(l.ColorR, l.ColorG, l.ColorB) * k;
        }
        return sum;
    }

    /// <summary>
    /// The lamp light on the ground, as an RGB map laid out like the terrain atlas (texel (x,y) = world
    /// (x/size*ws, y/size*ws)), so it burns into the tiles without resampling. Rows run in parallel; only texels
    /// inside some lamp's reach are visited, so a rig of a dozen lamps on a 2 km map costs a few seconds, not minutes.
    /// </summary>
    public static Texture2D BakeGround(Scene s, LightRig rig, int size, int samples,
                                       Action<int, int>? progress = null, CancellationToken token = default)
    {
        if (size < 4) size = 4;
        float ws = s.Cfg.WorldSize <= 0 ? 1f : s.Cfg.WorldSize;
        var rgba = new byte[size * size * 4];

        // Per-light reach in texel rows, so a row with no lamp near it is skipped outright.
        var active = new List<PointLight>();
        foreach (var l in rig.Lights)
            if (l.Enabled && l.OnGround && l.Intensity > 0f && l.Radius > 0f) active.Add(l);
        if (active.Count == 0) return new Texture2D(size, size, rgba);

        int done = 0;
        var opts = new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };
        Parallel.For(0, size, opts, () => s.NewCursor(), (py, _, cur) =>
        {
            float wz = (py + 0.5f) / size * ws;
            bool any = false;
            foreach (var l in active)
                if (MathF.Abs(l.Position.Z - wz) <= l.Radius) { any = true; break; }
            if (any)
            {
                for (int px = 0; px < size; px++)
                {
                    float wx = (px + 0.5f) / size * ws;
                    bool near = false;
                    foreach (var l in active)
                    {
                        float dx = l.Position.X - wx, dz = l.Position.Z - wz;
                        if (dx * dx + dz * dz <= l.Radius * l.Radius) { near = true; break; }
                    }
                    if (!near) continue;
                    float wy = GroundAt(s.Hm, s.Cfg, wx, wz);
                    var n = Normal(s.Hm, s.Cfg, wx, wz);
                    var c = Lamp(s, new Vector3(wx, wy, wz), n, rig, cur, samples, ground: true, seed: (uint)(py * 73856093 ^ px * 19349663));
                    if (c.X <= 0f && c.Y <= 0f && c.Z <= 0f) continue;
                    int o = (py * size + px) * 4;
                    rgba[o] = Clamp(c.X); rgba[o + 1] = Clamp(c.Y); rgba[o + 2] = Clamp(c.Z); rgba[o + 3] = 255;
                }
            }
            int d = Interlocked.Increment(ref done);
            progress?.Invoke(d, size);
            return cur;
        }, _ => { });
        return new Texture2D(size, size, rgba);
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    /// <summary>March the heightmap between two points; true when the ground never rises above the segment.</summary>
    public static bool TerrainClear(Heightmap hm, TerrainConfig cfg, Vector3 from, Vector3 to)
    {
        float dx = to.X - from.X, dy = to.Y - from.Y, dz = to.Z - from.Z;
        float horiz = MathF.Sqrt(dx * dx + dz * dz);
        if (horiz < 1e-3f) return true;
        float spacing = cfg.HorizontalSpacing <= 0 ? 1f : cfg.HorizontalSpacing;
        int steps = Math.Clamp((int)MathF.Ceiling(horiz / spacing), 2, 4096);
        for (int i = 1; i < steps; i++)
        {
            float t = i / (float)steps;
            float sy = from.Y + dy * t;
            if (GroundAt(hm, cfg, from.X + dx * t, from.Z + dz * t) > sy + 0.15f) return false;
        }
        return true;
    }

    public static float GroundAt(Heightmap hm, TerrainConfig cfg, float wx, float wz)
    {
        float ws = cfg.WorldSize <= 0 ? 1f : cfg.WorldSize;
        float fx = Math.Clamp(wx / ws * (hm.Width - 1), 0f, hm.Width - 1f);
        float fy = Math.Clamp(wz / ws * (hm.Height - 1), 0f, hm.Height - 1f);
        int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(x0 + 1, hm.Width - 1), y1 = Math.Min(y0 + 1, hm.Height - 1);
        float tx = fx - x0, ty = fy - y0;
        float a = cfg.HeightToMeters(hm[x0, y0]), b = cfg.HeightToMeters(hm[x1, y0]);
        float c = cfg.HeightToMeters(hm[x0, y1]), d = cfg.HeightToMeters(hm[x1, y1]);
        return (a + (b - a) * tx) + ((c + (d - c) * tx) - (a + (b - a) * tx)) * ty;
    }

    public static Vector3 Normal(Heightmap hm, TerrainConfig cfg, float wx, float wz)
    {
        float sp = cfg.HorizontalSpacing <= 0 ? 1f : cfg.HorizontalSpacing;
        float hl = GroundAt(hm, cfg, wx - sp, wz), hr = GroundAt(hm, cfg, wx + sp, wz);
        float hd = GroundAt(hm, cfg, wx, wz - sp), hu = GroundAt(hm, cfg, wx, wz + sp);
        return Vector3.Normalize(new Vector3(hl - hr, 2f * sp, hd - hu));
    }

    private static float Hash01(uint x)
    {
        x ^= x >> 16; x *= 0x7feb352d; x ^= x >> 15; x *= 0x846ca68b; x ^= x >> 16;
        return (x & 0xffffff) / 16777216f;
    }

    private static byte Clamp(float v) => (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);
}
