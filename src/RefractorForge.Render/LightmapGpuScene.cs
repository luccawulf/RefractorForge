using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;

namespace RefractorForge.Render;

/// <summary>
/// The lightmap bake's geometry as FLAT ARRAYS - exactly the buffers a GPU compute shader reads.
///
/// <para>Two BVHs (the whole level, and the object being baked) and the terrain heightmap are packed into one set
/// of arrays. The level comes first and is uploaded once per bake; the object's own geometry is a tail appended
/// after it, re-uploaded per object. Every index is stored ABSOLUTE - a node's child, a leaf's run in the index
/// array, a triangle id - so a kernel needs nothing but these arrays and two root node ids.</para>
///
/// <para>Integer and float data live in SEPARATE arrays rather than bit-cast into one. Small integers stored as
/// float bits are denormals, and some GPUs flush denormals to zero on load; a node's child index would silently
/// become 0 and traversal would go wrong on exactly the hardware that is hardest to debug.</para>
/// </summary>
public sealed class LightmapGpuScene
{
    public const int FloatsPerNode = 8;    // min.xyz, 0, max.xyz, 0              (two vec4)
    public const int IntsPerNode = 2;      // leftFirst (absolute), count
    /// <summary>a.xyz,0  e1.xyz,0  e2.xyz,0  albedo.rgb,0 - four vec4. The albedo rides WITH its triangle
    /// rather than in a buffer of its own because OpenGL 4.3 only guarantees EIGHT storage buffers to a compute
    /// shader, and the kernel needs every one of them.</summary>
    public const int FloatsPerTri = 16;

    public float[] NodeBounds = Array.Empty<float>();
    public int[] NodeLinks = Array.Empty<int>();
    public float[] Tris = Array.Empty<float>();
    public int[] Index = Array.Empty<int>();
    /// <summary>Absolute root node of each BVH, or -1 when that scene is absent.</summary>
    public int RootLevel = -1, RootSelf = -1;
    /// <summary>Where the object's tail starts - the level's own sizes. A GPU uploads the level once and then
    /// rewrites only the arrays past these offsets for each object.</summary>
    public int LevelNodes, LevelTris, LevelIndex;

    // Terrain, pre-converted to metres with the same expression the CPU uses, so a comparison against it agrees.
    public float[] Heights = Array.Empty<float>();
    public int HW, HH;
    public float WorldSize, MaxH;

    public int NodeCount => NodeLinks.Length / IntsPerNode;
    public int TriCount => Tris.Length / FloatsPerTri;

    public static LightmapGpuScene Build(RayScene? level, RayScene? self, Heightmap hm, TerrainConfig cfg, float maxH)
    {
        var lf = level?.ExportFlat();
        var sf = self?.ExportFlat();
        int ln = lf?.Nodes.Length ?? 0, lt = lf?.A.Length ?? 0, li = lf?.Index.Length ?? 0;
        int sn = sf?.Nodes.Length ?? 0, st = sf?.A.Length ?? 0, si = sf?.Index.Length ?? 0;

        var sc = new LightmapGpuScene
        {
            NodeBounds = new float[(ln + sn) * FloatsPerNode],
            NodeLinks = new int[(ln + sn) * IntsPerNode],
            Tris = new float[(lt + st) * FloatsPerTri],
            Index = new int[li + si],
            LevelNodes = ln, LevelTris = lt, LevelIndex = li,
        };
        if (lf is not null) { Pack(sc, lf, 0, 0, 0); sc.RootLevel = lf.Root; }
        if (sf is not null) { Pack(sc, sf, ln, lt, li); sc.RootSelf = sf.Root + ln; }

        sc.HW = hm.Width; sc.HH = hm.Height; sc.WorldSize = cfg.WorldSize; sc.MaxH = maxH;
        sc.Heights = new float[hm.Width * hm.Height];
        for (int y = 0; y < hm.Height; y++)
            for (int x = 0; x < hm.Width; x++)
                sc.Heights[y * hm.Width + x] = cfg.HeightToMeters(hm[x, y]);
        return sc;
    }

    /// <summary>
    /// Just the OBJECT's own BVH, packed to sit after a level already uploaded - what a GPU re-uploads per object.
    /// Identical to the tail <see cref="Build"/> would produce for the same level sizes, so the level's arrays
    /// never have to be sent twice.
    /// </summary>
    public static LightmapGpuScene BuildTail(RayScene? self, int levelNodes, int levelTris, int levelIndex)
    {
        var sc = new LightmapGpuScene { LevelNodes = levelNodes, LevelTris = levelTris, LevelIndex = levelIndex };
        if (self is null) return sc;
        var sf = self.ExportFlat();
        // Pack writes at ABSOLUTE positions, so pack into full-size arrays and keep only the tail.
        var tmp = new LightmapGpuScene
        {
            NodeBounds = new float[(levelNodes + sf.Nodes.Length) * FloatsPerNode],
            NodeLinks = new int[(levelNodes + sf.Nodes.Length) * IntsPerNode],
            Tris = new float[(levelTris + sf.A.Length) * FloatsPerTri],
            Index = new int[levelIndex + sf.Index.Length],
        };
        Pack(tmp, sf, levelNodes, levelTris, levelIndex);
        sc.NodeBounds = tmp.NodeBounds[(levelNodes * FloatsPerNode)..];
        sc.NodeLinks = tmp.NodeLinks[(levelNodes * IntsPerNode)..];
        sc.Tris = tmp.Tris[(levelTris * FloatsPerTri)..];
        sc.Index = tmp.Index[levelIndex..];
        sc.RootSelf = sf.Root + levelNodes;
        return sc;
    }

    /// <summary>Copy one BVH in at the given bases, rewriting every index to be absolute.</summary>
    private static void Pack(LightmapGpuScene sc, RayScene.Flat f, int nodeBase, int triBase, int indexBase)
    {
        for (int i = 0; i < f.Nodes.Length; i++)
        {
            var nd = f.Nodes[i];
            int o = (nodeBase + i) * FloatsPerNode;
            sc.NodeBounds[o] = nd.Min.X; sc.NodeBounds[o + 1] = nd.Min.Y; sc.NodeBounds[o + 2] = nd.Min.Z;
            sc.NodeBounds[o + 4] = nd.Max.X; sc.NodeBounds[o + 5] = nd.Max.Y; sc.NodeBounds[o + 6] = nd.Max.Z;
            int l = (nodeBase + i) * IntsPerNode;
            // A leaf's LeftFirst points into the index array; an interior node's points at its left child.
            sc.NodeLinks[l] = nd.Count > 0 ? nd.LeftFirst + indexBase : nd.LeftFirst + nodeBase;
            sc.NodeLinks[l + 1] = nd.Count;
        }
        for (int t = 0; t < f.A.Length; t++)
        {
            int o = (triBase + t) * FloatsPerTri;
            sc.Tris[o] = f.A[t].X; sc.Tris[o + 1] = f.A[t].Y; sc.Tris[o + 2] = f.A[t].Z;
            sc.Tris[o + 4] = f.E1[t].X; sc.Tris[o + 5] = f.E1[t].Y; sc.Tris[o + 6] = f.E1[t].Z;
            sc.Tris[o + 8] = f.E2[t].X; sc.Tris[o + 9] = f.E2[t].Y; sc.Tris[o + 10] = f.E2[t].Z;
            // RayScene hands back 0.5 grey for a scene built without albedo; so must this.
            var al = f.Albedo is null ? new Vector3(0.5f) : f.Albedo[t];
            sc.Tris[o + 12] = al.X; sc.Tris[o + 13] = al.Y; sc.Tris[o + 14] = al.Z;
        }
        for (int i = 0; i < f.Index.Length; i++) sc.Index[indexBase + i] = f.Index[i] + triBase;
    }
}

/// <summary>
/// The GPU lightmap kernel, written in C# against <see cref="LightmapGpuScene"/>'s flat arrays.
///
/// <para>This exists to be CHECKED. The GLSL compute shader is a line-by-line transliteration of these methods,
/// and this project cannot run GPU work headlessly - so the things most likely to be wrong in a GPU port (the
/// buffer layout, absolute indices across two concatenated BVHs, the traversal stack, the terrain indexing) are
/// proven here instead, by requiring this to agree BIT FOR BIT with <see cref="LightSampling"/> and
/// <see cref="RayScene"/>. The operations are the same <c>Vector3</c> calls in the same order for that reason;
/// the only thing that differs is where the data comes from.</para>
/// </summary>
public static class LightmapKernel
{
    public const int StackSize = 66;               // RayScene.MaxDepth + 2
    private const float Skip = 0.02f;

    /// <summary>The per-object constants a dispatch carries as uniforms.</summary>
    public readonly record struct Params(
        Vec3 SunDir, Vector3 Sun, bool SoftSun, float SunAngleDeg, int SunPerSample,
        int SkyPerSample, float AoRadius,
        int BouncePerSample, int BounceDepth, float BounceDistance, Vector3 SunColour, int BounceShadowSamples)
    {
        public static Params From(ShadeContext x)
        {
            var a = x.Advanced;
            return new Params(x.SunDir, x.Sun, x.SoftSun, a?.SunAngularDiameterDeg ?? 0f, x.SunPerSample,
                              x.SkyPerSample, a?.AoRadius ?? 0f,
                              x.BouncePerSample, a?.BounceDepth ?? 1, a?.BounceDistance ?? 30f,
                              a?.SunColour ?? Vector3.One, Math.Max(1, a?.BounceShadowSamples ?? 1));
        }
    }

    /// <summary>One sample: sun visibility, sky visibility, bounced light. Exactly what the CPU shader computes
    /// for the advanced tier; lamps are not part of the kernel.</summary>
    public static (float Sun, float Sky, Vector3 Bounce) Shade(LightmapGpuScene sc, in Params p, Vector3 wp,
                                                              Vector3 fn, uint seed, bool sky, bool bounce)
    {
        float sunVis = p.SoftSun
            ? SunVisibility(sc, wp, p.SunDir, p.SunAngleDeg, p.SunPerSample, seed)
            : (Clear(sc, wp, p.SunDir, p.Sun) ? 1f : 0f);
        float skyVis = sky && p.SkyPerSample > 0 ? SkyVisibility(sc, wp, fn, p.SkyPerSample, p.AoRadius, seed) : 1f;
        var b = bounce && p.BouncePerSample > 0
            ? Bounce(sc, p, wp, fn, p.BouncePerSample, p.BounceDepth, p.BounceDistance, seed)
            : Vector3.Zero;
        return (sunVis, skyVis, b);
    }

    // ---- sampling (mirrors LightSampling) ------------------------------------------------------------------

    public static float SunVisibility(LightmapGpuScene sc, Vector3 p, Vec3 sunDir, float angularDiameterDeg, int samples, uint seed)
    {
        var sun = Vector3.Normalize(new Vector3(sunDir.X, sunDir.Y, sunDir.Z));
        samples = Math.Max(1, samples);
        float halfAngle = MathF.Max(0f, angularDiameterDeg) * 0.5f * MathF.PI / 180f;
        if (samples == 1 || halfAngle <= 1e-6f)
            return Clear(sc, p, sunDir, sun) ? 1f : 0f;

        Basis(sun, out var u, out var v);
        float tanHalf = MathF.Tan(halfAngle);
        float rot = Hash01(seed) * MathF.Tau;
        int seen = 0;
        for (int k = 0; k < samples; k++)
        {
            float r = tanHalf * MathF.Sqrt((k + 0.5f) / samples);
            float a = k * 2.399963f + rot;
            var dir = Vector3.Normalize(sun + u * (MathF.Cos(a) * r) + v * (MathF.Sin(a) * r));
            if (Clear(sc, p, new Vec3(dir.X, dir.Y, dir.Z), dir)) seen++;
        }
        return seen / (float)samples;
    }

    public static float SkyVisibility(LightmapGpuScene sc, Vector3 p, Vector3 n, int samples, float maxDist, uint seed)
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
            float r2 = (k + 0.5f) / samples;
            float r = MathF.Sqrt(r2);
            float a = k * 2.399963f + rot;
            var dir = u * (MathF.Cos(a) * r) + v * (MathF.Sin(a) * r) + n * MathF.Sqrt(MathF.Max(0f, 1f - r2));
            float len = dir.Length();
            if (len < 1e-6f) { open++; continue; }
            dir /= len;
            float reach = bounded ? maxDist : float.MaxValue;
            bool blocked = (sc.RootLevel >= 0 && Occluded(sc, sc.RootLevel, p, dir, reach))
                           || (sc.RootSelf >= 0 && Occluded(sc, sc.RootSelf, p, dir, reach));
            if (!blocked && !bounded)
                blocked = !PointLit(sc, p.X, p.Y, p.Z, new Vec3(dir.X, dir.Y, dir.Z));
            if (!blocked) open++;
        }
        return open / (float)samples;
    }

    /// <summary>Depth 1 and 2 only - a GPU kernel cannot recurse, so the second bounce is a separate call of the
    /// same shape rather than a recursive one. Deeper bounces fall back to the CPU (none of the tiers use them).</summary>
    public static Vector3 Bounce(LightmapGpuScene sc, in Params prm, Vector3 p, Vector3 n, int samples, int depth, float maxDist, uint seed)
    {
        if (samples <= 0 || depth <= 0 || (sc.RootLevel < 0 && sc.RootSelf < 0)) return Vector3.Zero;
        if (n.LengthSquared() < 1e-12f) return Vector3.Zero;
        n = Vector3.Normalize(n);
        Basis(n, out var u, out var v);
        float rot = Hash01(seed) * MathF.Tau;
        float reach = maxDist > 0f ? maxDist : float.MaxValue;
        var sunV = new Vector3(prm.SunDir.X, prm.SunDir.Y, prm.SunDir.Z);

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

            if (!TraceNearest(sc, p, dir, reach, out var hPoint, out var hNormal, out var hAlbedo)) continue;

            float ndl = MathF.Max(0f, Vector3.Dot(hNormal, sunV));
            if (ndl > 0f)
            {
                float vis = SunVisibility(sc, hPoint, prm.SunDir, prm.SunAngleDeg, prm.BounceShadowSamples,
                                          seed ^ (uint)(k * 2654435761u));
                if (vis > 0f) sum += hAlbedo * prm.SunColour * (ndl * vis);
            }
            if (depth > 1)
                sum += hAlbedo * Bounce(sc, prm, hPoint, hNormal, Math.Max(1, samples / 4), depth - 1, reach,
                                        seed ^ (uint)(k * 40503u + 7u));
        }
        return sum / samples;
    }

    private static bool Clear(LightmapGpuScene sc, Vector3 p, Vec3 dirV, Vector3 dir)
        => PointLit(sc, p.X, p.Y, p.Z, dirV)
           && (sc.RootLevel < 0 || !Occluded(sc, sc.RootLevel, p, dir, float.MaxValue))
           && (sc.RootSelf < 0 || !Occluded(sc, sc.RootSelf, p, dir, float.MaxValue));

    private static bool TraceNearest(LightmapGpuScene sc, Vector3 p, Vector3 dir, float reach,
                                     out Vector3 point, out Vector3 normal, out Vector3 albedo)
    {
        bool ha = false, hb = false;
        float da = 0f, db = 0f;
        Vector3 pa = default, na = default, aa = default, pb = default, nb = default, ab = default;
        if (sc.RootLevel >= 0) ha = Trace(sc, sc.RootLevel, p, dir, reach, out da, out pa, out na, out aa);
        if (sc.RootSelf >= 0) hb = Trace(sc, sc.RootSelf, p, dir, reach, out db, out pb, out nb, out ab);
        if (ha && hb)
        {
            bool first = da <= db;
            point = first ? pa : pb; normal = first ? na : nb; albedo = first ? aa : ab;
            return true;
        }
        if (ha) { point = pa; normal = na; albedo = aa; return true; }
        if (hb) { point = pb; normal = nb; albedo = ab; return true; }
        point = default; normal = default; albedo = default;
        return false;
    }

    // ---- traversal (mirrors RayScene) -----------------------------------------------------------------------

    public static bool Occluded(LightmapGpuScene sc, int root, Vector3 origin, Vector3 dir, float maxDist)
    {
        origin += dir * Skip;
        maxDist -= Skip;
        if (maxDist <= 0f) return false;
        var inv = Reciprocal(dir);
        Span<int> stack = stackalloc int[StackSize];
        int sp = 0, node = root;
        while (true)
        {
            if (SlabHit(sc, node, origin, inv, maxDist))
            {
                int lf = sc.NodeLinks[node * 2], cnt = sc.NodeLinks[node * 2 + 1];
                if (cnt > 0)
                {
                    for (int i = lf; i < lf + cnt; i++)
                    {
                        float t = HitDistance(sc, sc.Index[i], origin, dir);
                        if (t > 1e-4f && t <= maxDist) return true;
                    }
                }
                else
                {
                    if (sp < stack.Length) stack[sp++] = lf + 1;
                    node = lf;
                    continue;
                }
            }
            if (sp == 0) return false;
            node = stack[--sp];
        }
    }

    public static bool Trace(LightmapGpuScene sc, int root, Vector3 origin, Vector3 dir, float maxDist,
                             out float distance, out Vector3 point, out Vector3 normal, out Vector3 albedo)
    {
        distance = 0f; point = default; normal = default; albedo = default;
        origin += dir * Skip;
        maxDist -= Skip;
        if (maxDist <= 0f) return false;
        var inv = Reciprocal(dir);
        float best = maxDist; int bestTri = -1;
        Span<int> stack = stackalloc int[StackSize];
        int sp = 0, node = root;
        while (true)
        {
            if (SlabHit(sc, node, origin, inv, best))
            {
                int lf = sc.NodeLinks[node * 2], cnt = sc.NodeLinks[node * 2 + 1];
                if (cnt > 0)
                {
                    for (int i = lf; i < lf + cnt; i++)
                    {
                        int t = sc.Index[i];
                        float d = HitDistance(sc, t, origin, dir);
                        if (d > 1e-4f && d < best) { best = d; bestTri = t; }
                    }
                }
                else
                {
                    if (sp < stack.Length) stack[sp++] = lf + 1;
                    node = lf;
                    continue;
                }
            }
            if (sp == 0) break;
            node = stack[--sp];
        }
        if (bestTri < 0) return false;

        var nrm = Vector3.Cross(E1(sc, bestTri), E2(sc, bestTri));
        float len = nrm.Length();
        nrm = len > 1e-12f ? nrm / len : Vector3.UnitY;
        if (Vector3.Dot(nrm, dir) > 0f) nrm = -nrm;
        int ao = bestTri * LightmapGpuScene.FloatsPerTri + 12;
        distance = best + Skip;
        point = origin + dir * best;
        normal = nrm;
        albedo = new Vector3(sc.Tris[ao], sc.Tris[ao + 1], sc.Tris[ao + 2]);
        return true;
    }

    private static Vector3 A(LightmapGpuScene sc, int t) { int o = t * LightmapGpuScene.FloatsPerTri; return new(sc.Tris[o], sc.Tris[o + 1], sc.Tris[o + 2]); }
    private static Vector3 E1(LightmapGpuScene sc, int t) { int o = t * LightmapGpuScene.FloatsPerTri + 4; return new(sc.Tris[o], sc.Tris[o + 1], sc.Tris[o + 2]); }
    private static Vector3 E2(LightmapGpuScene sc, int t) { int o = t * LightmapGpuScene.FloatsPerTri + 8; return new(sc.Tris[o], sc.Tris[o + 1], sc.Tris[o + 2]); }

    private static Vector3 Reciprocal(Vector3 d) => new(
        1f / (d.X != 0f ? d.X : 1e-20f),
        1f / (d.Y != 0f ? d.Y : 1e-20f),
        1f / (d.Z != 0f ? d.Z : 1e-20f));

    private static bool SlabHit(LightmapGpuScene sc, int node, Vector3 o, Vector3 inv, float maxDist)
    {
        int b = node * LightmapGpuScene.FloatsPerNode;
        var lo = new Vector3(sc.NodeBounds[b], sc.NodeBounds[b + 1], sc.NodeBounds[b + 2]);
        var hi = new Vector3(sc.NodeBounds[b + 4], sc.NodeBounds[b + 5], sc.NodeBounds[b + 6]);
        float t0 = (lo.X - o.X) * inv.X, t1 = (hi.X - o.X) * inv.X;
        float tmin = MathF.Min(t0, t1), tmax = MathF.Max(t0, t1);
        t0 = (lo.Y - o.Y) * inv.Y; t1 = (hi.Y - o.Y) * inv.Y;
        tmin = MathF.Max(tmin, MathF.Min(t0, t1)); tmax = MathF.Min(tmax, MathF.Max(t0, t1));
        t0 = (lo.Z - o.Z) * inv.Z; t1 = (hi.Z - o.Z) * inv.Z;
        tmin = MathF.Max(tmin, MathF.Min(t0, t1)); tmax = MathF.Min(tmax, MathF.Max(t0, t1));
        return tmax >= MathF.Max(tmin, 0f) && tmin <= maxDist;
    }

    private static float HitDistance(LightmapGpuScene sc, int i, Vector3 o, Vector3 d)
    {
        var a = A(sc, i); var e1 = E1(sc, i); var e2 = E2(sc, i);
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
        return Vector3.Dot(e2, qvec) * inv;
    }

    // ---- terrain (mirrors TerrainShadow.PointLit) -----------------------------------------------------------

    public static bool PointLit(LightmapGpuScene sc, float wx, float wy, float wz, Vec3 sunDir)
    {
        float ws = sc.WorldSize; int hw = sc.HW, hh = sc.HH;
        float horiz = MathF.Sqrt(sunDir.X * sunDir.X + sunDir.Z * sunDir.Z); if (horiz < 1e-4f) horiz = 1e-4f;
        float dirX = sunDir.X / horiz, dirZ = sunDir.Z / horiz;
        float rise = MathF.Max(sunDir.Y, 0.02f) / horiz;
        float step = ws / 1024f;
        float cx = wx, cz = wz, rh = wy + 0.35f;
        const int maxSteps = 2200;
        for (int s = 1; s <= maxSteps; s++)
        {
            cx += dirX * step; cz += dirZ * step; rh += rise * step;
            if (rh > sc.MaxH) return true;
            if (cx < 0f || cz < 0f || cx > ws || cz > ws) return true;
            float fx = cx / ws * (hw - 1), fz = cz / ws * (hh - 1);
            int hx = Math.Clamp((int)(fx + 0.5f), 0, hw - 1), hy = Math.Clamp((int)(fz + 0.5f), 0, hh - 1);
            if (sc.Heights[hy * hw + hx] > rh) return false;
        }
        return true;
    }

    private static void Basis(Vector3 n, out Vector3 u, out Vector3 v)
    {
        var pick = MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        u = Vector3.Normalize(Vector3.Cross(n, pick));
        v = Vector3.Cross(n, u);
    }

    internal static float Hash01(uint x)
    {
        x ^= x >> 16; x *= 0x7feb352d;
        x ^= x >> 15; x *= 0x846ca68b;
        x ^= x >> 16;
        return (x & 0xFFFFFF) / 16777216f;
    }
}

/// <summary>
/// An <see cref="ILightmapShader"/> that shades through <see cref="LightmapKernel"/> on the CPU. It is the
/// reference the GPU shader is measured against, and - because the kernel reads only the packed arrays - running a
/// real bake through it proves the packing end to end: its output must be byte-identical to
/// <see cref="CpuLightmapShader"/>'s.
/// </summary>
public sealed class LightmapKernelShader : ILightmapShader
{
    private readonly ConditionalWeakTable<ShadeContext, LightmapGpuScene> _scenes = new();
    public int PreferredChunk => 16384;

    public void Shade(SampleChunk c, ShadeContext x, CancellationToken cancel)
    {
        // The kernel covers the advanced tier's three terms. The classic tier (one binary sun ray, the grid
        // self-shadow) and a second bounce's recursion beyond depth 2 stay on the CPU path, which is already fast.
        if (x.Advanced is null || (x.Advanced.BounceSamples > 0 && x.Advanced.BounceDepth > 2))
        {
            CpuLightmapShader.Instance.Shade(c, x, cancel);
            return;
        }
        var sc = _scenes.GetValue(x, ctx => LightmapGpuScene.Build(ctx.Advanced!.Scene, ctx.SelfScene, ctx.Hm, ctx.Cfg, ctx.MaxH));
        var prm = LightmapKernel.Params.From(x);
        bool sky = x.Advanced.SkySamples > 0, bounce = x.Advanced.BounceSamples > 0;
        var pool = x.CursorPool;
        try
        {
            Parallel.For(0, c.Count, new ParallelOptions { CancellationToken = cancel },
                () => pool.TryTake(out var cur) ? cur : (Grid: x.SelfGrid?.NewCursor(), Lamp: x.Night?.NewCursor()),
                (i, _, cur) =>
                {
                    var (s, k, b) = LightmapKernel.Shade(sc, prm, c.Position[i], c.Normal[i], c.SubSeed[i], sky, bounce);
                    c.Sun[i] = s; c.Sky[i] = k; c.Bounce[i] = b;
                    c.Lamp[i] = CpuLightmapShader.ShadeLamp(c, i, x, cur.Lamp);
                    return cur;
                },
                cur => pool.Add(cur));
        }
        catch (OperationCanceledException) { }
    }
}
