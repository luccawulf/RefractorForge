using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using RefractorForge.Formats.Terrain;

namespace RefractorForge.Render;

/// <summary>
/// The light on the ground, traced, and written into the ground's own picture.
///
/// <para>Battlefield Vietnam draws terrain as <c>colour tile x detail texture x 2</c> and nothing else
/// (<c>effects/RaShaderTerrain.fx</c>: the fixed-function technique is the live one, its lit pixel shader is
/// commented out). There is no sun, no ambient and no lightmap at draw time - the tiles ARE the lighting. That is a
/// limit and a freedom at once: nothing can change after the bake, and nothing stands between the bake and the
/// screen. Whatever colour the traced light has, the ground can show it: the blue of open shade, the warm bounce at
/// the foot of a sunlit wall, a soft-edged tree shadow, a slope turned away from the sun.</para>
///
/// <para><b>Two resolutions.</b> The sun's shadow is the sharp part and is traced at the full size, several rays a
/// texel from different spots inside it, as a bare visibility. Sky and bounce are smooth and cost fifty times as
/// many rays, so they are traced at a fraction of the size and filtered. The two meet again, with the slope's own
/// facing, when the result is applied - so nothing the size of the whole ground is ever held in float colour.</para>
/// </summary>
public static class GroundLightBake
{
    /// <summary>What a ground bake traced. It is not yet a picture: <see cref="Apply"/> makes it one.</summary>
    public sealed class Result
    {
        /// <summary>Side of <see cref="Sun"/>, in texels across the whole world.</summary>
        public int Size { get; init; }
        /// <summary>How much of the sun's disc each texel sees, 0..255. Cast shadow only - which way the slope
        /// faces is not in it, so it is also exactly what the engine's shadow bits want.</summary>
        public byte[] Sun { get; init; } = Array.Empty<byte>();
        /// <summary>Side of <see cref="Soft"/>.</summary>
        public int SoftSize { get; init; }
        /// <summary>Sky and bounce light per texel, linear irradiance.</summary>
        public Vector3[] Soft { get; init; } = Array.Empty<Vector3>();
        /// <summary>The share of the ground in the sun's shadow, 0..1.</summary>
        public double ShadowShare { get; init; }
    }

    /// <summary>
    /// Trace the light on the ground.
    /// </summary>
    /// <param name="scene">The level, terrain included (<see cref="LightScene.Build"/>).</param>
    /// <param name="terrainStride">The scene's terrain stride, so samples sit on the surface the scene has.</param>
    /// <param name="size">Texels across the world for the sun's shadow. 4 per metre is what the engine's own
    /// shadow bits use; fewer makes the edges soft and stepped.</param>
    /// <param name="budget"><c>SunSamples</c> rays per shadow texel, <c>HemiSamples</c> per sky texel.</param>
    /// <param name="softDivide">The sky and bounce are traced at <paramref name="size"/> over this.</param>
    /// <param name="progress">Called with (rows done, rows in all) as the bake goes; both passes count.</param>
    /// <param name="rig">Placed lamps, so the light they throw BOUNCES into the smooth part. Their direct pools are
    /// not traced here: <see cref="Apply"/> adds those texel by texel, at the picture's own resolution.</param>
    public static Result? Bake(RayScene scene, DaylightModel day, Heightmap hm, TerrainConfig cfg, int terrainStride,
        int size, LightTracer.Budget budget, int softDivide = 2, int denoiseIterations = 2,
        Action<int, int>? progress = null, CancellationToken cancel = default, LightRig? rig = null)
    {
        if (rig is { Lights.Count: 0 }) rig = null;
        size = Math.Max(16, size);
        int soft = Math.Max(16, size / Math.Max(1, softDivide));
        float ws = cfg.WorldSize;
        int total = size + soft, done = 0;
        var opts = new ParallelOptions { CancellationToken = cancel };

        // ---- the sun's shadow, full size -----------------------------------------------------------------------
        var sun = new byte[size * size];
        int sunRays = Math.Max(1, budget.SunSamples);
        float texel = ws / size;
        float halfAngle = MathF.Max(0f, day.Settings.SunAngularDiameterDeg) * 0.5f * MathF.PI / 180f;
        float tanHalf = MathF.Tan(halfAngle);
        Basis(day.ToSun, out var su, out var sv);
        long shadowed = 0;
        try
        {
            Parallel.For(0, size, opts, y =>
            {
                long rowShadow = 0;
                for (int x = 0; x < size; x++)
                {
                    uint seed = LightTracer.Mix((uint)x, (uint)y);
                    float rot = LightTracer.Hash01(seed) * MathF.Tau;
                    float ox = LightTracer.Hash01(seed ^ 0x51ED270Bu), oz = LightTracer.Hash01(seed ^ 0xA3C59AC3u);
                    int seen = 0;
                    for (int k = 0; k < sunRays; k++)
                    {
                        // A different spot inside the texel for every ray (the R2 sequence spreads any count evenly),
                        // and a different point on the sun: one set of rays softens the edge both ways.
                        float jx = Frac(ox + k * 0.7548776662f), jz = Frac(oz + k * 0.5698402910f);
                        float wx = (x + jx) * texel, wz = (y + jz) * texel;
                        var (h, n) = LightScene.Ground(hm, cfg, wx, wz, terrainStride);
                        var p = new Vector3(wx, h, wz) + n * 0.03f;
                        float r = tanHalf * MathF.Sqrt((k + 0.5f) / sunRays), a = k * 2.399963f + rot;
                        var dir = Vector3.Normalize(day.ToSun + su * (MathF.Cos(a) * r) + sv * (MathF.Sin(a) * r));
                        if (!scene.Occluded(p, dir)) seen++;
                    }
                    byte vis = (byte)(seen * 255 / sunRays);
                    sun[y * size + x] = vis;
                    if (vis < 128) rowShadow++;
                }
                Interlocked.Add(ref shadowed, rowShadow);
                progress?.Invoke(Interlocked.Increment(ref done), total);
            });
        }
        catch (OperationCanceledException) { return null; }

        // ---- sky and bounce, reduced ---------------------------------------------------------------------------
        var softLight = new Vector3[soft * soft];
        var softHeight = new float[soft * soft];
        var hemi = new LightTracer.Budget(0, Math.Max(1, budget.HemiSamples), budget.Bounces);
        float stexel = ws / soft;
        try
        {
            Parallel.For(0, soft, opts, y =>
            {
                for (int x = 0; x < soft; x++)
                {
                    float wx = (x + 0.5f) * stexel, wz = (y + 0.5f) * stexel;
                    var (h, n) = LightScene.Ground(hm, cfg, wx, wz, terrainStride);
                    var l = LightTracer.Gather(scene, day, new Vector3(wx, h, wz) + n * 0.03f, n, hemi,
                                               LightTracer.Mix((uint)(x + 7919), (uint)(y + 104729)), rig);
                    softLight[y * soft + x] = l.Sky + l.Indirect;
                    softHeight[y * soft + x] = h;
                }
                progress?.Invoke(Interlocked.Increment(ref done), total);
            });
        }
        catch (OperationCanceledException) { return null; }
        if (cancel.IsCancellationRequested) return null;

        Denoise(softLight, softHeight, soft, denoiseIterations);
        return new Result { Size = size, Sun = sun, SoftSize = soft, Soft = softLight, ShadowShare = shadowed / (double)((long)size * size) };
    }

    /// <summary>
    /// An edge-aware filter over the smooth light. A tap counts for less the more its light differs from the
    /// centre's - which keeps the dark line along the foot of a wall - and the more its HEIGHT differs, so the top
    /// of a bank does not bleed onto the ground below it.
    /// </summary>
    private static void Denoise(Vector3[] v, float[] height, int size, int iterations)
    {
        iterations = Math.Clamp(iterations, 0, 4);
        if (iterations == 0) return;
        ReadOnlySpan<float> kernel = stackalloc float[5] { 1f / 16f, 4f / 16f, 6f / 16f, 4f / 16f, 1f / 16f };
        var k5 = kernel.ToArray();
        var src = new Vector3[v.Length];
        const float valSigma = 0.05f, heightSigma = 0.75f;
        for (int it = 0; it < iterations; it++)
        {
            Array.Copy(v, src, v.Length);
            int step = 1 << it;
            Parallel.For(0, size, y =>
            {
                for (int x = 0; x < size; x++)
                {
                    int o0 = y * size + x;
                    var c0 = src[o0]; float l0 = EngineLight.Luma(c0), h0 = height[o0];
                    var acc = Vector3.Zero; float wsum = 0f;
                    for (int dy = -2; dy <= 2; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            int nx = x + dx * step, ny = y + dy * step;
                            if ((uint)nx >= (uint)size || (uint)ny >= (uint)size) continue;
                            int o1 = ny * size + nx;
                            float w = k5[dx + 2] * k5[dy + 2];
                            float dv = (EngineLight.Luma(src[o1]) - l0) / valSigma;
                            float dh = (height[o1] - h0) / heightSigma;
                            w *= MathF.Exp(-dv * dv - dh * dh);
                            if (w <= 1e-6f) continue;
                            acc += src[o1] * w; wsum += w;
                        }
                    if (wsum > 1e-6f) v[o0] = acc / wsum;
                }
            });
        }
    }

    /// <summary>
    /// What the ground should be multiplied by at a world position: the traced light there, on screen. 1 leaves the
    /// painted colour as it is; open shade under a clear sky is about half, with the sky's colour in it.
    /// </summary>
    /// <param name="lamps">Where to find the placed lamps' direct light; null for none.</param>
    /// <param name="floor">Nothing is drawn darker than this. The engine's lightmapped objects cannot go below
    /// their ambient, so ground left to go black beside them - under a roof with no floor of its own - would be the
    /// one thing in the level darker than the walls around it.</param>
    public static Vector3 DisplayAt(Result light, DaylightModel day, Heightmap hm, TerrainConfig cfg, float wx, float wz,
                                    Lamps? lamps = null, Vector3 floor = default, uint seed = 0)
    {
        float ws = cfg.WorldSize;
        float vis = SunAt(light, wx / ws * light.Size - 0.5f, wz / ws * light.Size - 0.5f);
        var soft = SoftAt(light, wx / ws * light.SoftSize - 0.5f, wz / ws * light.SoftSize - 0.5f);
        var n = SmoothNormal(hm, cfg, wx, wz);
        var e = day.SunIrradiance * (MathF.Max(0f, Vector3.Dot(n, day.ToSun)) * vis) + soft;
        if (lamps is not null && LampLight.InReach(lamps.Rig, wx, wz, ground: true))
        {
            var (h, gn) = LightScene.Ground(hm, cfg, wx, wz, lamps.TerrainStride);
            var lamp = LampLight.Display(lamps.Scene, lamps.Rig, new Vector3(wx, h, wz), gn, lamps.Samples, true, seed);
            if (lamp.X + lamp.Y + lamp.Z > 0f) e += day.FromDisplay(lamp);
        }
        return Vector3.Max(day.ToDisplay(e), floor);
    }

    /// <summary>What the placed lamps' direct light on the ground is traced against.</summary>
    /// <param name="Samples">Shadow rays per lamp per texel.</param>
    public sealed record Lamps(RayScene Scene, LightRig Rig, int TerrainStride, int Samples = 4);

    /// <summary>
    /// Write the traced light into the ground's picture: <paramref name="target"/> = <paramref name="original"/>
    /// x light. Always from the ORIGINAL - the ground as painted, before any light went into it - so baking twice
    /// is baking once, and a changed sun replaces the old light rather than landing on top of it.
    /// </summary>
    /// <param name="target">The picture to write. Laid over the whole world like the terrain atlas: texel (x, y)
    /// at world (x / width * worldSize, y / height * worldSize). May be the same size as the original or not.</param>
    /// <param name="original">The ground as painted. May be <paramref name="target"/> itself.</param>
    /// <param name="gain">Scales the result; 1 is the traced light as it is.</param>
    /// <param name="lamps">The placed lamps: their pools are traced here, at this picture's own resolution, with
    /// their shadows, and added in linear light.</param>
    /// <param name="floor">See <see cref="DisplayAt"/>.</param>
    public static void Apply(Texture2D target, Texture2D original, Result light, DaylightModel day,
                             Heightmap hm, TerrainConfig cfg, float gain = 1f, Lamps? lamps = null, Vector3 floor = default,
                             CancellationToken cancel = default)
    {
        int w = target.Width, h = target.Height, ow = original.Width, oh = original.Height;
        float ws = cfg.WorldSize;
        var src = ReferenceEquals(target, original) ? (byte[])original.Rgba.Clone() : original.Rgba;
        var dst = target.Rgba;
        if (lamps is { Rig.Lights.Count: 0 }) lamps = null;
        Parallel.For(0, h, new ParallelOptions { CancellationToken = cancel }, y =>
        {
            float wz = (y + 0.5f) / h * ws;
            int oy = Math.Min(oh - 1, (int)((y + 0.5f) / h * oh));
            for (int x = 0; x < w; x++)
            {
                float wx = (x + 0.5f) / w * ws;
                var d = DisplayAt(light, day, hm, cfg, wx, wz, lamps, floor, LightTracer.Mix((uint)x, (uint)y)) * gain;
                int ox = Math.Min(ow - 1, (int)((x + 0.5f) / w * ow));
                int si = (oy * ow + ox) * 4, di = (y * w + x) * 4;
                dst[di] = ToByte(src[si] * d.X);
                dst[di + 1] = ToByte(src[si + 1] * d.Y);
                dst[di + 2] = ToByte(src[si + 2] * d.Z);
                dst[di + 3] = 255;
            }
        });
    }

    /// <summary>The sun's cast shadow as a grey picture, white where the sun reaches - what the engine's shadow bits
    /// are packed from.</summary>
    public static Texture2D SunPicture(Result light)
    {
        int n = light.Size * light.Size;
        var px = new byte[n * 4];
        for (int i = 0; i < n; i++)
        {
            byte v = light.Sun[i];
            px[i * 4] = v; px[i * 4 + 1] = v; px[i * 4 + 2] = v; px[i * 4 + 3] = 255;
        }
        return new Texture2D(light.Size, light.Size, px);
    }

    /// <summary>
    /// The ground as it was before the editor's OLDER bakes went into it, worked back from the records those bakes
    /// kept: the sun-shadow factor (<see cref="LightBake.ShadowFactorMap"/>) is divided out and the light-pool factor
    /// (<see cref="LightBake.PoolFactorMap"/>) taken off.
    ///
    /// <para>This is for a level that was baked before the editor started keeping a copy of the painted ground. A
    /// traced bake multiplies light onto the ground AS PAINTED; started from ground that already carries pools and
    /// shadows it would light them a second time.</para>
    ///
    /// <para>One thing cannot be divided back: where a pool was bright enough to clip a texel at white, the texel no
    /// longer says what it was, and dividing leaves a dark blot in the middle of the pool. Those texels are filled
    /// in from the ground around them instead - a guess, but ground-coloured, where the alternative is a hole.</para>
    /// </summary>
    public static Texture2D Unbaked(Texture2D baked, Texture2D? shadowFactor, Texture2D? poolFactor)
    {
        int w = baked.Width, h = baked.Height;
        var t = new Texture2D(w, h, (byte[])baked.Rgba.Clone());
        bool[]? lost = null;
        if (poolFactor is not null)
        {
            var bp = baked.Rgba; var fp = poolFactor.Rgba;
            int fw = poolFactor.Width, fh = poolFactor.Height;
            for (int y = 0; y < h; y++)
            {
                int fy = Math.Min(fh - 1, y * fh / h);
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4, fo = (fy * fw + Math.Min(fw - 1, x * fw / w)) * 4;
                    for (int c = 0; c < 3; c++)
                        if (bp[o + c] >= 254 && fp[fo + c] < 250) { (lost ??= new bool[w * h])[y * w + x] = true; break; }
                }
            }
            LightBake.ApplyPoolFactor(t, poolFactor, remove: true);
        }
        if (shadowFactor is not null) LightBake.DivideFactorOutOfAtlas(t, shadowFactor);
        if (lost is not null) FillFromNeighbours(t, lost);
        return t;
    }

    // Grow the known ground inward over the marked texels, a ring at a time.
    private static void FillFromNeighbours(Texture2D t, bool[] lost)
    {
        int w = t.Width, h = t.Height; var px = t.Rgba;
        var ring = new List<int>();
        for (int pass = 0; pass < 256; pass++)
        {
            ring.Clear();
            for (int i = 0; i < lost.Length; i++)
            {
                if (!lost[i]) continue;
                int x = i % w, y = i / w, r = 0, g = 0, b = 0, n = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if ((dx | dy) == 0 || (uint)nx >= (uint)w || (uint)ny >= (uint)h || lost[ny * w + nx]) continue;
                        int o = (ny * w + nx) * 4;
                        r += px[o]; g += px[o + 1]; b += px[o + 2]; n++;
                    }
                if (n == 0) continue;
                px[i * 4] = (byte)(r / n); px[i * 4 + 1] = (byte)(g / n); px[i * 4 + 2] = (byte)(b / n);
                ring.Add(i);
            }
            if (ring.Count == 0) break;
            foreach (int i in ring) lost[i] = false;      // only after the whole ring is read, so it grows evenly
        }
    }

    /// <summary>
    /// The sun's cast shadow packed as the engine's <c>LightmapShadowBits.lsb</c>: one bit per texel, set where the
    /// sun does NOT reach (the engine stores shadow, not light). The game draws nothing on the ground from this file -
    /// the ground's shadow is in its tiles - but it is what the engine consults for things that move through it.
    /// </summary>
    /// <param name="gridDim">Patches along one side: the level's terrain tile grid.</param>
    public static LightmapShadowBits ToShadowBits(Result light, int gridDim, int tilePx = 1024)
    {
        if (gridDim < 1) throw new ArgumentOutOfRangeException(nameof(gridDim));
        int full = gridDim * tilePx, s = light.Size;
        var bits = new byte[full * full];
        Parallel.For(0, full, y =>
        {
            int sy = Math.Min(s - 1, (int)((long)y * s / full)) * s, row = y * full;
            for (int x = 0; x < full; x++)
                bits[row + x] = light.Sun[sy + Math.Min(s - 1, (int)((long)x * s / full))] < 128 ? (byte)255 : (byte)0;
        });
        return LightmapShadowBits.FromShadowMask(bits, full, gridDim, tilePx);
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)(v + 0.5f), 0, 255);

    private static float SunAt(Result l, float fx, float fy)
    {
        int s = l.Size;
        fx = Math.Clamp(fx, 0f, s - 1f); fy = Math.Clamp(fy, 0f, s - 1f);
        int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(x0 + 1, s - 1), y1 = Math.Min(y0 + 1, s - 1);
        float tx = fx - x0, ty = fy - y0;
        float a = l.Sun[y0 * s + x0] + (l.Sun[y0 * s + x1] - l.Sun[y0 * s + x0]) * tx;
        float b = l.Sun[y1 * s + x0] + (l.Sun[y1 * s + x1] - l.Sun[y1 * s + x0]) * tx;
        return (a + (b - a) * ty) / 255f;
    }

    private static Vector3 SoftAt(Result l, float fx, float fy)
    {
        int s = l.SoftSize;
        fx = Math.Clamp(fx, 0f, s - 1f); fy = Math.Clamp(fy, 0f, s - 1f);
        int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(x0 + 1, s - 1), y1 = Math.Min(y0 + 1, s - 1);
        float tx = fx - x0, ty = fy - y0;
        var a = Vector3.Lerp(l.Soft[y0 * s + x0], l.Soft[y0 * s + x1], tx);
        var b = Vector3.Lerp(l.Soft[y1 * s + x0], l.Soft[y1 * s + x1], tx);
        return Vector3.Lerp(a, b, ty);
    }

    /// <summary>
    /// The ground's facing for shading, from the heights a spacing either side. The terrain's own triangles are
    /// flat, and a bake that shaded each by its own normal would print the triangulation onto the ground; the slope
    /// across neighbouring samples is what the eye reads as the shape of the land.
    /// </summary>
    public static Vector3 SmoothNormal(Heightmap hm, TerrainConfig cfg, float wx, float wz)
    {
        float sp = cfg.HorizontalSpacing;
        float hl = HeightAt(hm, cfg, wx - sp, wz), hr = HeightAt(hm, cfg, wx + sp, wz);
        float hd = HeightAt(hm, cfg, wx, wz - sp), hu = HeightAt(hm, cfg, wx, wz + sp);
        return Vector3.Normalize(new Vector3(hl - hr, 2f * sp, hd - hu));
    }

    /// <summary>The terrain's height at a world position, bilinear between samples.</summary>
    public static float HeightAt(Heightmap hm, TerrainConfig cfg, float wx, float wz)
    {
        float sp = cfg.HorizontalSpacing;
        float fx = Math.Clamp(wx / sp, 0f, hm.Width - 1f), fz = Math.Clamp(wz / sp, 0f, hm.Height - 1f);
        int x0 = (int)fx, z0 = (int)fz, x1 = Math.Min(x0 + 1, hm.Width - 1), z1 = Math.Min(z0 + 1, hm.Height - 1);
        float tx = fx - x0, tz = fz - z0;
        float a = cfg.HeightToMeters(hm[x0, z0]), b = cfg.HeightToMeters(hm[x1, z0]);
        float c = cfg.HeightToMeters(hm[x0, z1]), d = cfg.HeightToMeters(hm[x1, z1]);
        return (a + (b - a) * tx) * (1f - tz) + (c + (d - c) * tx) * tz;
    }

    private static float Frac(float v) => v - MathF.Floor(v);

    private static void Basis(Vector3 n, out Vector3 u, out Vector3 v)
    {
        var pick = MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        u = Vector3.Normalize(Vector3.Cross(n, pick));
        v = Vector3.Cross(n, u);
    }
}
