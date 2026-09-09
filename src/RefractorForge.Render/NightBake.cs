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

    /// <summary>
    /// How close to the LIGHT a ray stops. A lamp is mounted in or on something - a ceiling rose, a lamp head, a
    /// wall bracket - so the last half-metre before the bulb is nearly always inside that fixture or the surface
    /// it hangs from. Stopping at the bulb makes such a lamp shadow itself completely: measured on Saigon68, a
    /// lamp sitting 0.10 m under a 0.35 m ceiling slab had 0% of the room's rays reach it, and the room came out
    /// flat ambient. With this allowance the same lamp reaches 95-98%.
    /// </summary>
    public const float FixtureRadius = 0.35f;

    /// <summary>Is the straight line from a surface point to a light position unobstructed?</summary>
    public static bool Clear(Scene s, Vector3 p, Vector3 n, Vector3 lightPos, MeshOccluder.Cursor? cur,
                             float fixtureRadius = FixtureRadius)
    {
        var d = lightPos - p;
        float len = d.Length();
        if (len < 1e-4f) return true;
        d /= len;
        // Start the ray a hair off the surface along its normal, so the surface never shadows itself and a lamp
        // hung just under a ceiling still lights the floor beneath it; stop it short of the fixture at the far end.
        var o = p + n * 0.03f;
        float reach = len - MathF.Max(0.05f, fixtureRadius);
        if (reach > 0f && s.Objects is not null && cur is not null && s.Objects.Occluded(o, d, reach, cur, 0.01f)) return false;
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
            float raw = len > 1e-4f ? Vector3.Dot(n, toL / len) : 1f;
            // A lamp BEHIND a face never lights it - the wrap only softens the terminator on the lit side, it must
            // not let a bulb under a roof brighten the roof's top.
            if (raw <= 0f) continue;
            float ndl = raw * (1f - wrap) + wrap;
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

    /// <summary>
    /// The ONE lighting value an object without a lightmap unwrap can carry, as colour.
    /// <para>
    /// Measured on the engine (BfVietnam_DEBUG.exe, StandardMesh::createLightSampling): the lightmap generator
    /// reads a per-texel sample table from <c>StandardMesh/&lt;mesh&gt;.samples</c> that DICE's exporter wrote,
    /// and gives up when it is missing - none ship, so there is no auto-unwrap to reproduce. At runtime such a
    /// mesh's vertices all carry UV (0,0), so the whole object samples a single texel of its map (in every shipped
    /// map for these meshes that texel is black: Hue's wall corners render at ambient only). A flat map therefore
    /// lights the object uniformly, and the right uniform value is the area-weighted average of what its surface
    /// would receive: the moon where the sun direction reaches it, plus every lamp with the level's shadows.
    /// </para>
    /// </summary>
    /// <param name="sunLevel">What a moonlit face is worth in the map (see the object bake's parameter of the same name).</param>
    /// <param name="maxTris">Triangles sampled (the largest by area); cheap next to a real bake.</param>
    public static Vector3 AverageLamp(Scene s, MeshLibrary.Mesh mesh, Matrix4x4 world, LightRig? rig,
                                      Vector3 sunDir, float sunLevel, int lampSamples, int maxTris = 400)
    {
        var pos = mesh.Positions;
        var tris = new List<(Vector3 A, Vector3 B, Vector3 C, float Area)>();
        foreach (var part in mesh.Parts)
        {
            var idx = part.Indices;
            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                int a = idx[t], b = idx[t + 1], c = idx[t + 2];
                if ((uint)a >= (uint)pos.Length || (uint)b >= (uint)pos.Length || (uint)c >= (uint)pos.Length) continue;
                var wa = Vector3.Transform(pos[a], world); var wb = Vector3.Transform(pos[b], world); var wc = Vector3.Transform(pos[c], world);
                float area = Vector3.Cross(wb - wa, wc - wa).Length() * 0.5f;
                if (area > 1e-6f) tris.Add((wa, wb, wc, area));
            }
        }
        if (tris.Count == 0) return Vector3.Zero;
        tris.Sort((x, y) => y.Area.CompareTo(x.Area));
        if (tris.Count > maxTris) tris.RemoveRange(maxTris, tris.Count - maxTris);

        var cur = s.NewCursor();
        var sun = Vector3.Normalize(sunDir);
        var (_, maxH) = TerrainShadow.HeightSpan(s.Hm, s.Cfg);
        var sunV = new RefractorForge.Formats.Geometry.Vec3(sun.X, sun.Y, sun.Z);
        Vector3 sum = Vector3.Zero; float wsum = 0f;
        int k = 0;
        foreach (var (a, b, c, area) in tris)
        {
            var p = (a + b + c) / 3f;
            // Direct3D clockwise winding: the outward normal is the negated cross product.
            var n = -Vector3.Normalize(Vector3.Cross(b - a, c - a));
            float ndl = Vector3.Dot(n, sun);
            float moon = 0f;
            if (ndl > 0f)
            {
                bool lit = TerrainShadow.PointLit(p.X, p.Y, p.Z, sunV, s.Hm, s.Cfg, maxH)
                           && (s.Objects is null || cur is null || !s.Objects.Occluded(p + n * 0.03f, sun, cur));
                moon = lit ? sunLevel : 0f;
            }
            var v = new Vector3(moon);
            if (rig is not null && rig.Lights.Count > 0)
                v += Lamp(s, p, n, rig, cur, lampSamples, ground: false, seed: (uint)(k * 2654435761u));
            sum += v * area; wsum += area; k++;
        }
        return wsum > 0f ? Vector3.Min(sum / wsum, Vector3.One) : Vector3.Zero;
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
