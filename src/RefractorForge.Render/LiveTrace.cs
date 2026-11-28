using System;
using System.Collections.Generic;
using System.Numerics;
using RefractorForge.Formats.Terrain;

namespace RefractorForge.Render;

/// <summary>
/// The level's light scene as the flat arrays the viewport's live traced view uploads to the GPU: the
/// <see cref="LightScene"/> BVH exactly as <see cref="RayScene.ExportFlat"/> gives it, plus its cut-outs, so leaves and
/// fences cast the shadow of their texture there as they do on the CPU.
///
/// <para>Seven storage buffers, every one a plain array of 4-byte values:</para>
/// <list type="number">
/// <item><see cref="NodeBounds"/>: two vec4 a node, min then max.</item>
/// <item><see cref="NodeLinks"/>: two ints a node, leftFirst and count (as <see cref="LightmapGpuScene"/>).</item>
/// <item><see cref="Tris"/>: four vec4 a triangle - a, e1, e2, then albedo with its CUT-OUT INDEX in w (-1 for a
/// solid triangle). Stored as a float value, not bit-cast: small integers bit-cast into floats are denormals, which
/// some GPUs flush to zero.</item>
/// <item><see cref="Index"/>: the leaf runs.</item>
/// <item><see cref="Cuts"/>: two vec4 a cut-out - (uv0, uv1), (uv2, the mask's first word, width x 256 + height).</item>
/// <item><see cref="MaskBits"/>: every mask, one bit a texel (1 = surface), row-major from its first word.</item>
/// </list>
/// <para>The seventh and eighth buffers, the frame's accumulator and surface, belong to the viewer.</para>
/// </summary>
public sealed class LiveTraceScene
{
    public float[] NodeBounds = Array.Empty<float>();
    public int[] NodeLinks = Array.Empty<int>();
    public float[] Tris = Array.Empty<float>();
    public int[] Index = Array.Empty<int>();
    public float[] Cuts = Array.Empty<float>();
    public uint[] MaskBits = Array.Empty<uint>();
    public int Root;
    /// <summary>Triangles below this index are terrain (<see cref="LightScene.Build"/> adds the terrain first).</summary>
    public int TerrainTris;

    public int NodeCount => NodeLinks.Length / 2;
    public int TriCount => Tris.Length / 16;

    /// <summary>Pack a built scene. Null when it has no geometry.</summary>
    public static LiveTraceScene? From(LightScene.Built built)
    {
        var rs = built.Scene;
        if (rs is null) return null;
        var f = rs.ExportFlat();
        var s = new LiveTraceScene
        {
            NodeBounds = new float[f.Nodes.Length * 8],
            NodeLinks = new int[f.Nodes.Length * 2],
            Tris = new float[f.A.Length * 16],
            Index = (int[])f.Index.Clone(),
            Root = f.Root,
            TerrainTris = built.Stats.TerrainTriangles,
        };
        for (int i = 0; i < f.Nodes.Length; i++)
        {
            var n = f.Nodes[i];
            int o = i * 8;
            s.NodeBounds[o] = n.Min.X; s.NodeBounds[o + 1] = n.Min.Y; s.NodeBounds[o + 2] = n.Min.Z;
            s.NodeBounds[o + 4] = n.Max.X; s.NodeBounds[o + 5] = n.Max.Y; s.NodeBounds[o + 6] = n.Max.Z;
            s.NodeLinks[i * 2] = n.LeftFirst; s.NodeLinks[i * 2 + 1] = n.Count;
        }
        var cuts = rs.ExportCutouts();
        for (int t = 0; t < f.A.Length; t++)
        {
            int o = t * 16;
            Put(s.Tris, o, f.A[t]); Put(s.Tris, o + 4, f.E1[t]); Put(s.Tris, o + 8, f.E2[t]);
            Put(s.Tris, o + 12, f.Albedo is null ? new Vector3(0.5f) : f.Albedo[t]);
            s.Tris[o + 15] = cuts is null ? -1f : cuts.CutOf[t];
        }
        if (cuts is not null)
        {
            var at = new Dictionary<RayScene.AlphaMask, int>(ReferenceEqualityComparer.Instance);
            var bits = new List<uint>();
            int nc = cuts.Masks.Length;
            s.Cuts = new float[nc * 8];
            for (int c = 0; c < nc; c++)
            {
                var m = cuts.Masks[c];
                if (!at.TryGetValue(m, out int first))
                {
                    first = bits.Count;
                    at[m] = first;
                    int total = m.Width * m.Height;
                    var words = new uint[(total + 31) / 32];
                    for (int y = 0; y < m.Height; y++)
                        for (int x = 0; x < m.Width; x++)
                            if (m.SolidTexel(x, y)) { int b = y * m.Width + x; words[b >> 5] |= 1u << (b & 31); }
                    bits.AddRange(words);
                }
                int o = c * 8;
                var u0 = cuts.Uv[c * 3]; var u1 = cuts.Uv[c * 3 + 1]; var u2 = cuts.Uv[c * 3 + 2];
                s.Cuts[o] = u0.X; s.Cuts[o + 1] = u0.Y; s.Cuts[o + 2] = u1.X; s.Cuts[o + 3] = u1.Y;
                s.Cuts[o + 4] = u2.X; s.Cuts[o + 5] = u2.Y; s.Cuts[o + 6] = first; s.Cuts[o + 7] = m.Width * 256 + m.Height;
            }
            s.MaskBits = bits.ToArray();
        }
        return s;
    }

    private static void Put(float[] a, int o, Vector3 v) { a[o] = v.X; a[o + 1] = v.Y; a[o + 2] = v.Z; }

    /// <summary>Bytes the scene takes on the GPU.</summary>
    public long Bytes => 4L * (NodeBounds.Length + NodeLinks.Length + Tris.Length + Index.Length + Cuts.Length + MaskBits.Length);
}

/// <summary>
/// One sample of the live traced view, in C#: a line-by-line copy of the compute shader the viewer runs
/// (<c>Viewer/LiveTraceView.cs</c>), over the same arrays (<see cref="LiveTraceScene"/>) and with the same random
/// numbers. The tests hold it against <see cref="RayScene"/> and <see cref="LightTracer"/>; the viewer holds the GPU
/// against it before it shows the mode. Keep the two in step.
///
/// <para>What one sample is: the camera ray through the pixel into the scene, then the estimator of
/// <see cref="LightTracer.Gather"/> with ONE ray of each kind - one at a point on the sun's disc, one cosine-weighted
/// path over the hemisphere that may bounce, and the placed lamps of <see cref="LampLight.Display"/> with one shadow
/// ray each. The mean over many frames is the light <see cref="LightPreview"/> draws.</para>
/// </summary>
public static class LiveTraceKernel
{
    /// <summary>Lamps one frame can hold. It was 24, taken in the rig's own order, so a level with more - al_vietnas
    /// has 49 street lamps - simply lost the later ones from the picture.</summary>
    public const int MaxLamps = 96;
    public const int StackSize = 66;
    private const int MaxVisits = 1048576;
    private const float FltMax = float.MaxValue;

    /// <summary>A lamp as the kernel reads it. <c>CosOuter</c> is -2 for a point light.</summary>
    /// <param name="Flags">1 casts shadows, 2 lights the ground, 4 lights objects.</param>
    public readonly record struct Lamp(Vector3 Position, float Radius, Vector3 Colour, float Intensity,
                                       Vector3 SpotDir, float CosOuter, float CosInner, float Falloff, float SourceSize, int Flags);

    /// <summary>Everything a frame reads besides the scene.</summary>
    public sealed record Params(Matrix4x4 InvViewProj, Vector3 CamPos, int Width, int Height,
                                Vector3 ToSun, Vector3 SunIrradiance, float SunTanHalf,
                                Vector3 SkyZenith, Vector3 SkyHorizon, Vector3 GroundRadiance,
                                float Exposure, float Gamma, float BounceGain, int Bounces,
                                float WorldSize, Lamp[] Lamps, Texture2D? Ground)
    {
        public static Params From(DaylightModel day, LightRig? rig, Matrix4x4 viewProj, Vector3 camPos, int width, int height,
                                  Texture2D? ground, float worldSize, int bounces = 2)
        {
            Matrix4x4.Invert(viewProj, out var inv);
            float half = MathF.Max(0f, day.Settings.SunAngularDiameterDeg) * 0.5f * MathF.PI / 180f;
            return new Params(inv, camPos, width, height, day.ToSun, day.SunIrradiance, half <= 1e-6f ? 0f : MathF.Tan(half),
                              day.SkyZenithRadiance, day.SkyHorizonRadiance, day.GroundRadiance,
                              day.Exposure, day.DisplayGamma, MathF.Max(day.Settings.BounceStrength, 0f), Math.Clamp(bounces, 0, 3),
                              worldSize, LiveTraceKernel.Lamps(rig, camPos), ground);
        }
    }

    /// <summary>The rig's lamps that can light anything, at most <see cref="MaxLamps"/>, in the kernel's form.</summary>
    /// <param name="camPos">With more lamps than fit, the ones whose reach is nearest this point are kept.</param>
    public static Lamp[] Lamps(LightRig? rig, Vector3? camPos = null)
    {
        var list = new List<Lamp>();
        if (rig is null) return list.ToArray();
        IEnumerable<PointLight> lights = rig.Lights.Where(l => l.Enabled && l.Intensity > 0f && l.Radius > 0f);
        if (lights.Count() > MaxLamps && camPos is { } cp)
            lights = lights.OrderBy(l => MathF.Max(0f, (new Vector3(l.Position.X, l.Position.Y, l.Position.Z) - cp).Length() - l.Radius));
        foreach (var l in lights)
        {
            if (list.Count == MaxLamps) break;
            var d = l.Direction();
            float cone = Math.Clamp(l.ConeDeg, 1f, 179f);
            float outer = l.IsSpot ? MathF.Cos(cone * 0.5f * MathF.PI / 180f) : -2f;
            float inner = MathF.Cos(cone * 0.5f * (1f - Math.Clamp(l.ConeSoft, 0f, 1f)) * MathF.PI / 180f);
            int flags = (l.CastsShadows ? 1 : 0) | (l.OnGround ? 2 : 0) | (l.OnObjects ? 4 : 0);
            list.Add(new Lamp(new Vector3(l.Position.X, l.Position.Y, l.Position.Z), l.Radius,
                              new Vector3(l.ColorR, l.ColorG, l.ColorB), l.Intensity, new Vector3(d.X, d.Y, d.Z),
                              outer, inner, MathF.Max(l.Falloff, 0.1f), MathF.Max(0f, l.SourceSize), flags));
        }
        return list.ToArray();
    }

    /// <summary>One pixel's sample. <see cref="Distance"/> is from the camera position, negative for sky.</summary>
    public readonly record struct Sample(Vector3 Light, Vector3 Albedo, float Distance);

    /// <summary>What the rasterised frame says is at a pixel: an object's texture colour (Object false where it was
    /// terrain or nothing) and how far from the camera.</summary>
    public readonly record struct Raster(Vector3 Albedo, bool Object, float Distance);

    // ---- the GLSL, transliterated -----------------------------------------------------------------------------

    public static uint Mix(uint a, uint b) => LightTracer.Mix(a, b);
    public static float Hash01(uint x) => LightTracer.Hash01(x);

    private static void Basis(Vector3 n, out Vector3 u, out Vector3 v)
    {
        var pick = MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        u = Vector3.Normalize(Vector3.Cross(n, pick));
        v = Vector3.Cross(n, u);
    }

    private static Vector3 Reciprocal(Vector3 d) => new(
        1f / (d.X != 0f ? d.X : 1e-20f), 1f / (d.Y != 0f ? d.Y : 1e-20f), 1f / (d.Z != 0f ? d.Z : 1e-20f));

    private static Vector3 V(float[] a, int o) => new(a[o], a[o + 1], a[o + 2]);

    private static bool SlabHit(LiveTraceScene s, int node, Vector3 o, Vector3 inv, float maxDist)
    {
        var lo = V(s.NodeBounds, node * 8); var hi = V(s.NodeBounds, node * 8 + 4);
        float t0 = (lo.X - o.X) * inv.X, t1 = (hi.X - o.X) * inv.X;
        float tmin = MathF.Min(t0, t1), tmax = MathF.Max(t0, t1);
        t0 = (lo.Y - o.Y) * inv.Y; t1 = (hi.Y - o.Y) * inv.Y;
        tmin = MathF.Max(tmin, MathF.Min(t0, t1)); tmax = MathF.Min(tmax, MathF.Max(t0, t1));
        t0 = (lo.Z - o.Z) * inv.Z; t1 = (hi.Z - o.Z) * inv.Z;
        tmin = MathF.Max(tmin, MathF.Min(t0, t1)); tmax = MathF.Min(tmax, MathF.Max(t0, t1));
        return tmax >= MathF.Max(tmin, 0f) && tmin <= maxDist;
    }

    private static bool CutSolid(LiveTraceScene s, int c, float u, float v)
    {
        int o = c * 8;
        float uvx = s.Cuts[o] * (1f - u - v) + s.Cuts[o + 2] * u + s.Cuts[o + 4] * v;
        float uvy = s.Cuts[o + 1] * (1f - u - v) + s.Cuts[o + 3] * u + s.Cuts[o + 5] * v;
        int first = (int)s.Cuts[o + 6], wh = (int)s.Cuts[o + 7];
        int w = wh >> 8, h = wh & 255;
        float fu = uvx - MathF.Floor(uvx), fv = uvy - MathF.Floor(uvy);
        int x = Math.Min(w - 1, (int)(fu * w)), y = Math.Min(h - 1, (int)(fv * h));
        int b = y * w + x;
        return ((s.MaskBits[first + (b >> 5)] >> (b & 31)) & 1u) != 0u;
    }

    private static float HitDistance(LiveTraceScene s, int i, Vector3 o, Vector3 d)
    {
        var a = V(s.Tris, i * 16); var e1 = V(s.Tris, i * 16 + 4); var e2 = V(s.Tris, i * 16 + 8);
        var pvec = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, pvec);
        if (MathF.Abs(det) < 1e-9f) return -1f;
        float inv = 1f / det;
        var tvec = o - a;
        float u = Vector3.Dot(tvec, pvec) * inv;
        if (u < 0f || u > 1f) return -1f;
        var qvec = Vector3.Cross(tvec, e1);
        float v = Vector3.Dot(d, qvec) * inv;
        if (v < 0f || u + v > 1f) return -1f;
        float t = Vector3.Dot(e2, qvec) * inv;
        float cut = s.Tris[i * 16 + 15];
        if (cut >= 0f && t > 0f && !CutSolid(s, (int)cut, u, v)) return -1f;
        return t;
    }

    public static bool Occluded(LiveTraceScene s, Vector3 origin, Vector3 dir, float maxDist, float skip)
    {
        origin += dir * skip;
        maxDist -= skip;
        if (maxDist <= 0f) return false;
        var inv = Reciprocal(dir);
        Span<int> stack = stackalloc int[StackSize];
        int sp = 0, node = s.Root;
        for (int visits = 0; visits < MaxVisits; visits++)
        {
            if (SlabHit(s, node, origin, inv, maxDist))
            {
                int lf = s.NodeLinks[node * 2], cnt = s.NodeLinks[node * 2 + 1];
                if (cnt > 0)
                {
                    for (int i = lf; i < lf + cnt; i++)
                    {
                        float t = HitDistance(s, s.Index[i], origin, dir);
                        if (t > 1e-4f && t <= maxDist) return true;
                    }
                }
                else
                {
                    if (sp < StackSize) stack[sp++] = lf + 1;
                    node = lf;
                    continue;
                }
            }
            if (sp == 0) return false;
            node = stack[--sp];
        }
        return false;
    }

    /// <summary>The nearest hit: its triangle, distance from <paramref name="origin"/>, point, normal (facing the
    /// ray) and albedo. False on a miss.</summary>
    public static bool Trace(LiveTraceScene s, Vector3 origin, Vector3 dir, float maxDist, float skip,
                             out int tri, out float dist, out Vector3 point, out Vector3 normal, out Vector3 albedo)
    {
        tri = -1; dist = 0f; point = normal = albedo = Vector3.Zero;
        origin += dir * skip;
        maxDist -= skip;
        if (maxDist <= 0f) return false;
        var inv = Reciprocal(dir);
        float best = maxDist; int bestTri = -1;
        Span<int> stack = stackalloc int[StackSize];
        int sp = 0, node = s.Root;
        for (int visits = 0; visits < MaxVisits; visits++)
        {
            if (SlabHit(s, node, origin, inv, best))
            {
                int lf = s.NodeLinks[node * 2], cnt = s.NodeLinks[node * 2 + 1];
                if (cnt > 0)
                {
                    for (int i = lf; i < lf + cnt; i++)
                    {
                        int t = s.Index[i];
                        float d = HitDistance(s, t, origin, dir);
                        if (d > 1e-4f && d < best) { best = d; bestTri = t; }
                    }
                }
                else
                {
                    if (sp < StackSize) stack[sp++] = lf + 1;
                    node = lf;
                    continue;
                }
            }
            if (sp == 0) break;
            node = stack[--sp];
        }
        if (bestTri < 0) return false;
        var nrm = Vector3.Cross(V(s.Tris, bestTri * 16 + 4), V(s.Tris, bestTri * 16 + 8));
        float len = nrm.Length();
        nrm = len > 1e-12f ? nrm / len : Vector3.UnitY;
        if (Vector3.Dot(nrm, dir) > 0f) nrm = -nrm;
        tri = bestTri; dist = best + skip; point = origin + dir * best; normal = nrm;
        albedo = V(s.Tris, bestTri * 16 + 12);
        return true;
    }

    private static Vector3 Environment(Params p, Vector3 dir)
        => dir.Y > 0f ? Vector3.Lerp(p.SkyHorizon, p.SkyZenith, MathF.Pow(Math.Clamp(dir.Y, 0f, 1f), 0.6f)) : p.GroundRadiance;

    private static Vector3 FromDisplay(Params p, Vector3 d)
    {
        if (p.Exposure <= 1e-9f) return Vector3.Zero;
        return new Vector3(MathF.Pow(MathF.Max(d.X, 0f), p.Gamma), MathF.Pow(MathF.Max(d.Y, 0f), p.Gamma),
                           MathF.Pow(MathF.Max(d.Z, 0f), p.Gamma)) / p.Exposure;
    }

    private static Vector3 SunDisc(Params p, float e1, float e2)
    {
        if (p.SunTanHalf <= 0f) return p.ToSun;
        Basis(p.ToSun, out var u, out var v);
        float r = p.SunTanHalf * MathF.Sqrt(e1), a = e2 * MathF.Tau;
        return Vector3.Normalize(p.ToSun + u * (MathF.Cos(a) * r) + v * (MathF.Sin(a) * r));
    }

    private static float Attenuation(in Lamp l, Vector3 q)
    {
        var dv = q - l.Position;
        float d2 = Vector3.Dot(dv, dv), r2 = l.Radius * l.Radius;
        if (d2 >= r2) return 0f;
        float dist = MathF.Sqrt(d2), t = dist / l.Radius;
        float window = 1f - t * t; window *= window;
        float a = l.Intensity * MathF.Pow(1f - t, l.Falloff) * window;
        if (l.CosOuter > -1.5f)
        {
            if (dist < 1e-4f) return a;
            float cosA = Vector3.Dot(dv, l.SpotDir) / dist;
            if (cosA <= l.CosOuter) return 0f;
            if (!(cosA >= l.CosInner || l.CosInner <= l.CosOuter))
            {
                float k = (cosA - l.CosOuter) / (l.CosInner - l.CosOuter);
                a *= k * k * (3f - 2f * k);
            }
        }
        return a;
    }

    private static bool LampClear(LiveTraceScene s, Vector3 q, Vector3 n, Vector3 lightPos)
    {
        var o = q + n * 0.03f;
        var d = lightPos - o;
        float len = d.Length();
        if (len < 1e-4f) return true;
        float reach = len - MathF.Max(0.05f, NightBake.FixtureRadius);
        if (reach <= 0f) return true;
        return !Occluded(s, o, d / len, reach, 0.01f);
    }

    /// <summary>The lamps on a point, on the screen's scale (<see cref="LampLight.Display"/> with one shadow ray).</summary>
    /// <param name="ground">0 = every lamp, 1 = the ground's lamps, 2 = the objects' lamps.</param>
    private static Vector3 Lamps(LiveTraceScene s, Params p, Vector3 q, Vector3 n, int ground, uint seed)
    {
        var sum = Vector3.Zero;
        const float wrap = 0.15f;
        foreach (var l in p.Lamps)
        {
            if (ground == 1 && (l.Flags & 2) == 0) continue;
            if (ground == 2 && (l.Flags & 4) == 0) continue;
            float a = Attenuation(l, q);
            if (a <= 0f) continue;
            var toL = l.Position - q;
            float len = toL.Length();
            float raw = len > 1e-4f ? Vector3.Dot(n, toL / len) : 1f;
            if (raw <= 0f) continue;
            float vis = 1f;
            if ((l.Flags & 1) != 0 && len >= 1e-4f)
            {
                if (l.SourceSize <= 0.001f) vis = LampClear(s, q, n, l.Position) ? 1f : 0f;
                else
                {
                    var dir = toL / len;
                    var u = Vector3.Normalize(Vector3.Cross(dir, MathF.Abs(dir.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
                    var v = Vector3.Cross(dir, u);
                    float rot = Hash01(seed) * MathF.Tau;
                    float r1 = l.SourceSize * MathF.Sqrt(Hash01(Mix(seed, 31u)));
                    vis = LampClear(s, q, n, l.Position + u * (MathF.Cos(rot) * r1) + v * (MathF.Sin(rot) * r1)) ? 1f : 0f;
                }
            }
            if (vis <= 0f) continue;
            sum += l.Colour * (a * (raw * (1f - wrap) + wrap) * vis);
        }
        return sum;
    }

    /// <summary>The primary ray through a pixel (row 0 at the BOTTOM, as OpenGL counts): from the near plane.</summary>
    public static void PrimaryRay(Params p, float px, float py, out Vector3 origin, out Vector3 dir)
    {
        float nx = px / p.Width * 2f - 1f, ny = py / p.Height * 2f - 1f;
        var a = Vector4.Transform(new Vector4(nx, ny, 0f, 1f), p.InvViewProj);
        var b = Vector4.Transform(new Vector4(nx, ny, 1f, 1f), p.InvViewProj);
        origin = new Vector3(a.X, a.Y, a.Z) / a.W;
        var far = new Vector3(b.X, b.Y, b.Z) / b.W;
        dir = Vector3.Normalize(far - origin);
    }

    /// <summary>The ground picture under a world point, bilinear, as <see cref="LightPreview"/> samples it.</summary>
    public static Vector3 Ground(Params p, Vector3 q)
    {
        var t = p.Ground;
        if (t is null) return new Vector3(0.45f, 0.42f, 0.33f);
        float u = Math.Clamp(q.X / p.WorldSize, 0f, 1f), v = Math.Clamp(q.Z / p.WorldSize, 0f, 1f);
        float fx = u * t.Width - 0.5f, fy = v * t.Height - 0.5f;
        int x0 = Math.Clamp((int)MathF.Floor(fx), 0, t.Width - 1), y0 = Math.Clamp((int)MathF.Floor(fy), 0, t.Height - 1);
        int x1 = Math.Min(x0 + 1, t.Width - 1), y1 = Math.Min(y0 + 1, t.Height - 1);
        float tx = Math.Clamp(fx - x0, 0f, 1f), ty = Math.Clamp(fy - y0, 0f, 1f);
        var px = t.Rgba; int w = t.Width;
        Vector3 P(int x, int y) { int i = (y * w + x) * 4; return new Vector3(px[i], px[i + 1], px[i + 2]) * (1f / 255f); }
        return Vector3.Lerp(Vector3.Lerp(P(x0, y0), P(x1, y0), tx), Vector3.Lerp(P(x0, y1), P(x1, y1), tx), ty);
    }

    /// <summary>
    /// One sample of pixel (<paramref name="px"/>, <paramref name="py"/>) in frame <paramref name="frame"/>.
    /// </summary>
    /// <param name="raster">What the rasterised frame has at this pixel, or null to use the triangles' own colours
    /// (the GPU self-test runs that way, so it needs no raster pass).</param>
    public static Sample Shade(LiveTraceScene s, Params p, int px, int py, uint frame, Raster? raster)
    {
        uint seed = Mix(Mix((uint)px, (uint)py), frame);
        PrimaryRay(p, px + 0.5f, py + 0.5f, out var origin, out var dir);
        if (!Trace(s, origin, dir, FltMax, 0f, out int tri, out _, out var hp, out var n, out var triAlb))
            return new Sample(Vector3.Zero, Vector3.Zero, -1f);
        float camDist = Vector3.Distance(hp, p.CamPos);
        bool terrain = tri < s.TerrainTris;

        Vector3 albedo;
        if (terrain) albedo = Ground(p, hp);
        else if (raster is { Object: true } r && MathF.Abs(r.Distance - camDist) < 0.3f + 0.02f * camDist) albedo = r.Albedo;
        else albedo = new Vector3(MathF.Pow(triAlb.X, 1f / 2.2f), MathF.Pow(triAlb.Y, 1f / 2.2f), MathF.Pow(triAlb.Z, 1f / 2.2f));

        var start = hp + n * 0.02f;
        var e = Vector3.Zero;

        // The sun: one ray at a point on its disc.
        float ndl = Vector3.Dot(n, p.ToSun);
        if (ndl > 0f)
        {
            var sd = SunDisc(p, Hash01(Mix(seed, 1u)), Hash01(Mix(seed, 2u)));
            if (!Occluded(s, start, sd, FltMax, 0.02f)) e += p.SunIrradiance * ndl;
        }

        // One cosine-weighted direction for the sky and everything bounced.
        {
            Basis(n, out var u, out var v);
            float r2 = Hash01(Mix(seed, 3u)), a = Hash01(Mix(seed, 4u)) * MathF.Tau, r = MathF.Sqrt(r2);
            var hd = Vector3.Normalize(u * (MathF.Cos(a) * r) + v * (MathF.Sin(a) * r) + n * MathF.Sqrt(MathF.Max(0f, 1f - r2)));
            if (!Trace(s, start, hd, FltMax, 0.02f, out _, out _, out var bp, out var bn, out var balb))
                e += Environment(p, hd) * MathF.PI;
            else
            {
                var throughput = Vector3.One;
                var radiance = Vector3.Zero;
                uint ps = Mix(seed, 5u);
                for (int depth = 0; depth < p.Bounces; depth++)
                {
                    throughput *= balb * p.BounceGain;
                    if (throughput.X + throughput.Y + throughput.Z < 1e-4f) break;
                    float bl = Vector3.Dot(bn, p.ToSun);
                    if (bl > 0f)
                    {
                        var sd = SunDisc(p, Hash01(Mix(ps, (uint)(depth * 2 + 1))), Hash01(Mix(ps, (uint)(depth * 2 + 2))));
                        if (!Occluded(s, bp, sd, FltMax, 0.02f)) radiance += throughput * p.SunIrradiance * (bl / MathF.PI);
                    }
                    if (p.Lamps.Length > 0)
                    {
                        var lamp = Lamps(s, p, bp, bn, 0, Mix(ps, (uint)(depth * 2 + 201)));
                        if (lamp.X + lamp.Y + lamp.Z > 0f) radiance += throughput * FromDisplay(p, lamp) * (1f / MathF.PI);
                    }
                    Basis(bn, out var hu, out var hv);
                    float e1 = Hash01(Mix(ps, (uint)(depth * 2 + 101))), e2 = Hash01(Mix(ps, (uint)(depth * 2 + 102)));
                    float hr = MathF.Sqrt(e1), ha = e2 * MathF.Tau;
                    var next = Vector3.Normalize(hu * (MathF.Cos(ha) * hr) + hv * (MathF.Sin(ha) * hr) + bn * MathF.Sqrt(MathF.Max(0f, 1f - e1)));
                    if (!Trace(s, bp, next, FltMax, 0.02f, out _, out _, out var np, out var nn, out var nalb))
                    {
                        radiance += throughput * Environment(p, next);
                        break;
                    }
                    bp = np; bn = nn; balb = nalb;
                }
                e += radiance * MathF.PI;
            }
        }

        // The lamps straight onto the point.
        if (p.Lamps.Length > 0)
            e += FromDisplay(p, Lamps(s, p, start, n, terrain ? 1 : 2, Mix(seed, 6u)));

        return new Sample(e, albedo, camDist);
    }

    /// <summary>Linear light to the display multiplier, as <see cref="DaylightModel.ToDisplay"/>.</summary>
    public static Vector3 ToDisplay(Params p, Vector3 linear)
    {
        float g = 1f / p.Gamma, k = p.Exposure;
        return new Vector3(MathF.Pow(MathF.Max(linear.X * k, 0f), g), MathF.Pow(MathF.Max(linear.Y * k, 0f), g),
                           MathF.Pow(MathF.Max(linear.Z * k, 0f), g));
    }
}
