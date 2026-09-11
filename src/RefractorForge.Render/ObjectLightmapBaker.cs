using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;

namespace RefractorForge.Render;

/// <summary>
/// Bakes a per-object lightmap from the editor's sun, so "bake lighting to the game" ships what you see. The engine's
/// own shader (<c>effects/RaShaderPPLSTs1DifLmp.fx</c>) reads the map as <c>Prelight</c>, a SUN-VISIBILITY mask:
/// <c>colour = saturate(2 * (Prelight * sunColour * N.L + LMambient)) * texture</c>. So a texel is 1 where the sun reaches
/// the surface and 0 where the terrain or the object itself is in the way - N.L is NOT folded in, the shader applies
/// it. For each texel of the 2nd-UV atlas the surface point is found by rasterising the mesh in UV space, tested
/// against the heightmap (<see cref="TerrainShadow.PointLit"/>) and against the mesh (<see cref="MeshOccluder"/>).
/// Placed lights are added as extra visibility. Pure CPU, so it runs headlessly and is testable.
/// </summary>
public static class ObjectLightmapBaker
{
    /// <summary>
    /// The offline-renderer half of the bake: a sun with angular size, and sky visibility. Every field defaults
    /// to OFF, and with <c>advanced</c> left null <see cref="Bake"/> computes exactly what it always did - the
    /// binary sun test - so nothing that relies on the old look changes until it asks for the new one.
    ///
    /// <para>What each knob can and cannot do follows from the engine's shader, which reads the map as a
    /// multiplier on the direct sun term rather than as stored irradiance. See <see cref="LightSampling"/>.</para>
    /// </summary>
    /// <param name="Scene">The whole level as an occluder. Without it the sun and sky terms see only the terrain
    /// and the object itself, so a building cannot shadow its neighbour.</param>
    /// <param name="SunAngularDiameterDeg">The sun's angular size. 0.53 is the real one;
    /// <see cref="LightSampling.SunAngularDiameterDeg"/>. 0 keeps the hard shadow.</param>
    /// <param name="SunSamples">Directions drawn across the sun's disc. 1 is the old binary test.</param>
    /// <param name="SkySamples">Cosine-weighted hemisphere rays per texel. 0 disables both sky terms.</param>
    /// <param name="AoRadius">How far a sky ray looks, in metres. 0 means unbounded - true sky visibility, so a
    /// courtyard reads as enclosed. A metre or two makes it contact darkening instead.</param>
    /// <param name="AoStrength">How much sky occlusion darkens the DIRECT sun term. This is the honest half of
    /// ambient occlusion in this format: it shows on sunlit surfaces and cannot darken the engine's own ambient
    /// floor, which is added after the multiply.</param>
    /// <param name="SkyFill">
    /// What a texel the sun never reaches is written as, scaled by how much sky it can see. This is the trick
    /// that makes ambient occlusion visible in SHADOW, where <paramref name="AoStrength"/> by construction cannot
    /// reach: the engine has no separate ambient channel to modulate, but a small value in the mask buys a
    /// shadowed-but-open surface a little sun-coloured light while a shadowed-and-enclosed one stays black. 0
    /// keeps shadows at pure black, as they are today.
    /// </param>
    /// <param name="BounceSamples">Path-traced indirect rays per texel. 0 disables the bounce entirely.</param>
    /// <param name="BounceDepth">How many bounces to follow. 1 is usually all that survives 8-bit quantisation.</param>
    /// <param name="BounceDistance">How far a bounce ray looks, in metres.</param>
    /// <param name="SunColour">The level's <c>renderer.diffuseColor</c> - what a fully lit white surface returns,
    /// and therefore how bright and what colour the light passed on by a bounce is.</param>
    public sealed record Advanced(
        RayScene? Scene = null,
        float SunAngularDiameterDeg = 0f,
        int SunSamples = 1,
        int SkySamples = 0,
        float AoRadius = 0f,
        float AoStrength = 0f,
        float SkyFill = 0f,
        int BounceSamples = 0,
        int BounceDepth = 1,
        float BounceDistance = 30f,
        int BounceShadowSamples = 1,
        Vector3? SunColour = null,
        int DenoiseIterations = 0,
        float DenoiseNormalSharpness = 16f,
        float DenoiseTexelRadius = 2f,
        float DenoiseEdgeSensitivity = 0.15f);

    /// <summary>
    /// The fraction of this mesh's surface the sun actually reaches, 0..1 — what a baked map for it would average.
    ///
    /// <para>For a mesh whose lightmap-UV slot carries no unwrap there is nothing to rasterise, so it can only be
    /// given a FLAT map. Choosing that flat value badly is very visible: testing the sun at the object's origin
    /// alone answers "does the terrain shade this spot", which is nearly always yes, so every such object came out
    /// at 255 and glowed beside its baked neighbours. Real baked maps average far lower — 26 to 80 across retail
    /// Saigon68 — because a mesh SHADOWS ITSELF: undersides, interiors and back faces are dark.</para>
    ///
    /// <para>So this samples the surface the same way <see cref="Bake"/> does — triangle centroids against the
    /// terrain and against the mesh's own geometry — and returns the mean. The flat map then carries the level a
    /// real bake would have produced, and the object sits correctly among the ones that could be unwrapped.</para>
    /// </summary>
    /// <param name="maxSamples">Triangles to sample. The whole point is that this is cheap next to a bake.</param>
    public static float AverageLit(MeshLibrary.Mesh mesh, Matrix4x4 world, Heightmap hm, TerrainConfig cfg, Vec3 sunDir,
                                   LightRig? rig = null, int maxSamples = 256)
    {
        var pos = mesh.Positions;
        if (pos is null || pos.Length == 0) return 1f;
        var (_, maxH) = TerrainShadow.HeightSpan(hm, cfg);
        var sun = Vector3.Normalize(new Vector3(sunDir.X, sunDir.Y, sunDir.Z));

        // Every triangle in world space: the occluder needs them all, even though only a sample is tested.
        var tris = new List<(Vector3 A, Vector3 B, Vector3 C)>();
        foreach (var part in mesh.Parts)
        {
            var idx = part.Indices;
            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                int a = idx[t], b = idx[t + 1], c = idx[t + 2];
                if ((uint)a >= (uint)pos.Length || (uint)b >= (uint)pos.Length || (uint)c >= (uint)pos.Length) continue;
                tris.Add((Vector3.Transform(pos[a], world), Vector3.Transform(pos[b], world), Vector3.Transform(pos[c], world)));
            }
        }
        if (tris.Count == 0) return 1f;
        var occ = MeshOccluder.Build(tris);

        // Walk the list with a stride rather than taking the first N: the first triangles of a mesh are all one
        // part, and a stride spreads the samples over the whole object.
        int stride = Math.Max(1, tris.Count / Math.Max(maxSamples, 1));
        float sum = 0f; int n = 0;
        for (int i = 0; i < tris.Count; i += stride)
        {
            var (a, b, c) = tris[i];
            var p = (a + b + c) / 3f;
            // Nudge off the surface so a face never shadows itself at its own centroid.
            var probe = p + sun * 0.05f;
            float v = (TerrainShadow.PointLit(probe.X, probe.Y, probe.Z, sunDir, hm, cfg, maxH)
                       && !occ.Occluded(probe, sun)) ? 1f : 0f;
            if (rig is not null && rig.Lights.Count > 0) v += LightBake.Intensity(p.X, p.Y, p.Z, rig, hm, cfg);
            sum += Math.Clamp(v, 0f, 1f); n++;
        }
        return n == 0 ? 1f : sum / n;
    }

    /// <summary>Bake one object's lightmap. <paramref name="mesh"/> must carry lightmap UVs (40-byte / format-9233 mesh);
    /// returns null otherwise. <paramref name="world"/> places the mesh in world space. Intensity = ambient + (1-ambient)·
    /// N·L · shadow.</summary>
    /// <param name="samples">Sub-samples per texel AXIS: 2 means a 2x2 grid inside each texel, 3 a 3x3. The sun
    /// test is binary — a texel is lit or it is not — so one sample per texel puts a hard staircase along every
    /// shadow edge and every triangle border, which is exactly what a baked map looks like when it looks "jagged".
    /// Averaging several sub-samples turns that step into a proper gradient. Cost is samples² rays per texel.</param>
    /// <param name="night">The level's geometry as lamp occluders (see <see cref="NightBake.Build"/>). With it, a
    /// placed light is shadowed by every building and prop on the map and softened over its source size; without
    /// it lights fall back to terrain-only occlusion.</param>
    /// <param name="colour">Keep the lamps' COLOUR in the map (a 24-bit map, which Battlefield 1942 reads with the
    /// hue intact - GC_Bespin_Night ships exactly that). False folds them to brightness for BfVietnam, whose shader
    /// reads only the blue channel.</param>
    /// <param name="lampSamples">Shadow samples per lamp per texel; 1 is a hard shadow, 4-8 a penumbra.</param>
    /// <param name="sunLevel">
    /// What a face the sun reaches is written as. 1 for a day bake. For a NIGHT bake it must be well below 1: the
    /// engine draws a face as <c>lightmap x sunColour x N.L</c>, so a moonlit wall written at 1.0 already sits at
    /// the top of the range and no lamp can add to it - lamps would show only in the moon's shadow. The reference
    /// night maps average 0.04-0.13 with their lamps up at 1.0 (Dystopia City, GC_Bespin_Night), which is what
    /// makes a lamp-lit wall several times brighter than a moonlit one. 0.25 is a good night value.
    /// </param>
    public static Texture2D? Bake(MeshLibrary.Mesh mesh, Matrix4x4 world, Heightmap hm, TerrainConfig cfg, Vec3 sunDir,
        int size = 256, float ambient = 0.4f, LightRig? rig = null, bool selfShadow = true, int samples = 2,
        NightBake.Scene? night = null, bool colour = false, int lampSamples = 1, float sunLevel = 1f,
        Advanced? advanced = null, CancellationToken cancel = default, ILightmapShader? shader = null)
    {
        var lm = mesh.LightmapUvs;
        if (lm is null || lm.Length == 0 || size < 4) return null;
        samples = Math.Clamp(samples, 1, 4);
        var pos = mesh.Positions;
        var (_, maxH) = TerrainShadow.HeightSpan(hm, cfg);
        var sun = Vector3.Normalize(new Vector3(sunDir.X, sunDir.Y, sunDir.Z));

        MeshOccluder? occ = null;
        RayScene? selfScene = null;
        if (selfShadow)
        {
            var tris = new List<(Vector3, Vector3, Vector3)>();
            foreach (var part in mesh.Parts)
            {
                var idx = part.Indices;
                for (int t = 0; t + 2 < idx.Length; t += 3)
                {
                    int a = idx[t], b = idx[t + 1], c = idx[t + 2];
                    if ((uint)a >= (uint)pos.Length || (uint)b >= (uint)pos.Length || (uint)c >= (uint)pos.Length) continue;
                    tris.Add((Vector3.Transform(pos[a], world), Vector3.Transform(pos[b], world), Vector3.Transform(pos[c], world)));
                }
            }
            // The advanced path casts hundreds of rays per texel, so it wants the BVH - which is also stateless,
            // where the grid needs one int per triangle per thread. The grid stays for the plain path, which is
            // already tested and already fast enough at one ray per texel.
            if (advanced is not null) selfScene = RayScene.Build(tris);
            else occ = MeshOccluder.Build(tris);
        }

        var inten = new float[size * size];
        var cover = new bool[size * size];
        // Sub-sample accumulators. `owner` keeps the first-triangle-wins rule while still letting every sub-sample
        // of THAT triangle contribute, so the averaging smooths shadow edges without smearing across an atlas seam.
        // Three channels: the sun term is grey, the lamps may not be.
        var sum = new float[size * size];
        var sumG = new float[size * size];
        var sumB = new float[size * size];
        var cnt = new int[size * size];
        var owner = new int[size * size];
        Array.Fill(owner, -1);
        // Where each texel actually is, and which way it faces. A denoiser needs these to tell "the texel next
        // door on the same wall" from "the texel next door that is a different surface packed beside it in the
        // atlas" - without them a blur would smear light straight across a chart boundary.
        var texelPos = advanced is { DenoiseIterations: > 0 } ? new Vector3[size * size] : null;
        var texelNrm = texelPos is null ? null : new Vector3[size * size];
        int triId = 0;

        // ---- The bake in three phases -----------------------------------------------------------------------
        // GATHER walks the triangles in order and records a sample point for every sub-sample a triangle owns -
        // cheap, sequential, and the only part where order matters (first triangle wins a texel). SHADE casts
        // every ray for a chunk of those points; the samples are independent, so this is what runs in parallel,
        // and it is the part a GPU takes over. ACCUMULATE then adds the results in exactly the order the points
        // were gathered, which is why the output is bit-for-bit what the old single loop produced.
        int subCount = samples * samples;
        var (sunPer, skyPer, bouncePer) = ShadeContext.ForSubSamples(advanced, subCount);
        var ctx = new ShadeContext
        {
            Hm = hm, Cfg = cfg, MaxH = maxH, SunDir = sunDir, Sun = sun,
            SelfGrid = occ, SelfScene = selfScene, Advanced = advanced,
            Rig = rig, Night = night, LampSamples = lampSamples,
            SoftSun = advanced is { SunSamples: > 1 } && advanced.SunAngularDiameterDeg > 0f,
            SunPerSample = sunPer, SkyPerSample = skyPer, BouncePerSample = bouncePer,
        };
        shader ??= CpuLightmapShader.Instance;
        var chunk = new SampleChunk(shader.PreferredChunk);
        float aoStrength = advanced?.AoStrength ?? 0f;
        float skyFill = advanced?.SkyFill ?? 0f;

        void Flush()
        {
            if (chunk.Count == 0) return;
            // Checked BEFORE shading as well as after. Cancellation is otherwise only noticed per triangle, and one
            // large triangle can be thousands of chunks - so without this the gather went on refilling chunks and
            // handing them to the shader long after Cancel. The CPU shader happens to return at once on a cancelled
            // token, but a GPU shader would have kept queueing real work.
            if (cancel.IsCancellationRequested) { chunk.Clear(); return; }
            shader.Shade(chunk, ctx, cancel);
            if (cancel.IsCancellationRequested) { chunk.Clear(); return; }
            for (int i = 0; i < chunk.Count; i++)
            {
                int o = chunk.Texel[i];
                float sunVis = chunk.Sun[i], sky = chunk.Sky[i];
                float direct = sunVis * sunLevel * (1f - aoStrength * (1f - sky));
                float fill = skyFill * sky;
                float v = ambient + (1f - ambient) * MathF.Max(direct, fill);
                // Indirect light and lamps are COLOUR: kept as RGB for a 24-bit BF1942 map, folded to luma for
                // BfVietnam's blue-channel-only shader.
                var bn = chunk.Bounce[i]; var lp = chunk.Lamp[i];
                float lr = bn.X + lp.X, lg = bn.Y + lp.Y, lb = bn.Z + lp.Z;
                if (colour)
                {
                    sum[o] += MathF.Min(v + lr, 1f); sumG[o] += MathF.Min(v + lg, 1f); sumB[o] += MathF.Min(v + lb, 1f);
                }
                else
                {
                    float luma = 0.2126f * lr + 0.7152f * lg + 0.0722f * lb;
                    sum[o] += MathF.Min(v + luma, 1f);
                }
                cnt[o]++;
            }
            chunk.Clear();
        }

        foreach (var part in mesh.Parts)
        {
            var idx = part.Indices;
            for (int t = 0; t + 2 < idx.Length; t += 3, triId++)
            {
                // Cancellation is checked per triangle as well as inside the shader: one object is one iteration
                // of the caller's Parallel.For, which only consults its token between iterations.
                if (cancel.IsCancellationRequested) return null;
                int a = idx[t], b = idx[t + 1], c = idx[t + 2];
                if ((uint)a >= (uint)pos.Length || (uint)b >= (uint)pos.Length || (uint)c >= (uint)pos.Length) continue;
                if (a >= lm.Length || b >= lm.Length || c >= lm.Length) continue;

                Vector3 wa = Vector3.Transform(pos[a], world), wb = Vector3.Transform(pos[b], world), wc = Vector3.Transform(pos[c], world);
                Vector3 fn = Vector3.Cross(wb - wa, wc - wa);
                if (fn.LengthSquared() < 1e-12f) continue;
                // Refractor meshes are Direct3D CLOCKWISE-front: cross(b-a, c-a) points INTO the object (measured on
                // Saigon68's barrel: 0 of 48 triangles outward). The outward normal is the negation.
                fn = -Vector3.Normalize(fn);

                Vector2 ta = lm[a] * size, tb = lm[b] * size, tc = lm[c] * size;
                // A vertex with a NaN in its second UV set (retail BfVietnam meshes carry a few) would turn the
                // whole triangle's texel range into int.MinValue and skip - silently. Skip it on purpose.
                if (!float.IsFinite(ta.X) || !float.IsFinite(ta.Y) || !float.IsFinite(tb.X) || !float.IsFinite(tb.Y) || !float.IsFinite(tc.X) || !float.IsFinite(tc.Y)) continue;
                int thisTri = triId;
                RasterTriangle(ta, tb, tc, size, samples, (px, py, w0, w1, w2, k) =>
                {
                    if (cancel.IsCancellationRequested) return;      // stop gathering once cancelled
                    int o = py * size + px;
                    if (owner[o] == -1) owner[o] = thisTri;
                    else if (owner[o] != thisTri) return;               // first triangle wins (atlas overlaps are rare)
                    var wp = w0 * wa + w1 * wb + w2 * wc;
                    uint texelSeed = (uint)(px * 73856093 ^ py * 19349663);
                    if (texelPos is not null) { texelPos[o] = wp; texelNrm![o] = fn; }
                    chunk.Add(o, wp, fn, texelSeed, SubSeed(texelSeed, k));
                    if (chunk.Full) Flush();
                });
            }
        }
        Flush();
        if (cancel.IsCancellationRequested) return null;

        var intenG = colour ? new float[size * size] : inten;
        var intenB = colour ? new float[size * size] : inten;
        for (int i = 0; i < inten.Length; i++)
            if (cnt[i] > 0)
            {
                inten[i] = sum[i] / cnt[i]; cover[i] = true;
                if (colour) { intenG[i] = sumG[i] / cnt[i]; intenB[i] = sumB[i] / cnt[i]; }
            }

        // A mesh whose second UV set is all one point (BfVietnam props and walls ship 0,0 on every vertex - the slot
        // exists, the unwrap does not) rasterises nothing. Baking it anyway shipped an all-black map that turned the
        // object black in the editor and in the game; such an object has no lightmap and stays dynamically lit.
        int covered = 0;
        foreach (var c in cover) if (c) covered++;
        if (covered < size * size / 2000) return null;

        // Denoise BEFORE dilating. Dilation invents texels in the gutter by averaging real ones; denoising them
        // afterwards would let those invented values feed back into the surface they came from.
        if (cancel.IsCancellationRequested) return null;
        if (advanced is { DenoiseIterations: > 0 } dn && texelPos is not null)
        {
            Denoise(inten, cover, texelPos, texelNrm!, size, dn);
            if (colour)
            {
                Denoise(intenG, cover, texelPos, texelNrm!, size, dn);
                Denoise(intenB, cover, texelPos, texelNrm!, size, dn);
            }
        }

        // Spread into the UV gutter so seams don't bleed black. Dilate fills `cover` as it goes, so each channel
        // gets its own copy of the pre-dilation coverage.
        if (colour)
        {
            var coverG = (bool[])cover.Clone(); var coverB = (bool[])cover.Clone();
            Dilate(inten, cover, size); Dilate(intenG, coverG, size); Dilate(intenB, coverB, size);
        }
        else Dilate(inten, cover, size);

        var rgba = new byte[size * size * 4];
        for (int i = 0; i < size * size; i++)
        {
            rgba[i * 4] = (byte)Math.Clamp((int)(inten[i] * 255f + 0.5f), 0, 255);
            rgba[i * 4 + 1] = (byte)Math.Clamp((int)(intenG[i] * 255f + 0.5f), 0, 255);
            rgba[i * 4 + 2] = (byte)Math.Clamp((int)(intenB[i] * 255f + 0.5f), 0, 255);
            rgba[i * 4 + 3] = 255;
        }
        return new Texture2D(size, size, rgba);
    }

    // Half-space barycentric triangle rasterizer in texel space. `samples` sub-divides each texel on both axes and
    // calls back once per sub-sample that lands inside the triangle, so the caller can average them.
    private static void RasterTriangle(Vector2 a, Vector2 b, Vector2 c, int size, int samples,
                                       Action<int, int, float, float, float, int> px)
    {
        int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
        int maxX = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
        int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
        int maxY = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
        float area = Edge(a, b, c);
        if (MathF.Abs(area) < 1e-6f) return;
        float inv = 1f / area;
        float step = 1f / samples;
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                bool any = false;
                for (int sy = 0; sy < samples; sy++)
                    for (int sx = 0; sx < samples; sx++)
                    {
                        var p = new Vector2(x + (sx + 0.5f) * step, y + (sy + 0.5f) * step);
                        float w0 = Edge(b, c, p) * inv, w1 = Edge(c, a, p) * inv, w2 = Edge(a, b, p) * inv;
                        if (w0 < -0.002f || w1 < -0.002f || w2 < -0.002f) continue;   // outside (epsilon catches seam texels)
                        px(x, y, w0, w1, w2, sy * samples + sx);
                        any = true;
                    }
                // A texel whose centre-ish samples all missed but which the triangle still clips: keep the old
                // single-centre behaviour so thin geometry does not lose its texels entirely.
                if (!any && samples > 1)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    float w0 = Edge(b, c, p) * inv, w1 = Edge(c, a, p) * inv, w2 = Edge(a, b, p) * inv;
                    if (w0 >= -0.002f && w1 >= -0.002f && w2 >= -0.002f) px(x, y, w0, w1, w2, 0);
                }
            }
    }

    private static float Edge(Vector2 a, Vector2 b, Vector2 c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    /// <summary>A different seed for each sub-sample of a texel, so they draw different directions. Sub-sample 0
    /// keeps the texel's own seed, so a one-sample bake is exactly what it always was.</summary>
    internal static uint SubSeed(uint texelSeed, int k)
        => k == 0 ? texelSeed : (texelSeed ^ ((uint)k * 0x9E3779B9u)) * 0x85EBCA6Bu;

    /// <summary>
    /// A-trous wavelet denoise, guided by each texel's world position and normal.
    ///
    /// <para>This is what makes a path-traced bake affordable. Monte Carlo estimates converge as the square root
    /// of the sample count, so going from visible noise to clean by sampling alone costs roughly sixteen times
    /// the rays; an edge-aware filter gets most of the way there for a few passes over the image. The wavelet
    /// form widens its taps by a power of two each iteration instead of enlarging the kernel, so N iterations
    /// cover a 2^N radius at constant cost per texel.</para>
    ///
    /// <para><b>What stops it smearing across charts.</b> Neighbouring texels in the ATLAS are frequently
    /// unrelated surfaces - that is what packing is - so a plain blur would carry a sunlit roof's light onto the
    /// shaded wall packed beside it. Weighting each tap by how far away it is in WORLD space and how far its
    /// normal has turned makes the filter follow the surface rather than the image: a tap on the other side of a
    /// chart boundary is metres away or facing elsewhere, and its weight collapses. No chart map is needed, which
    /// matters because the baker does not have one - the unwrap is not in scope at bake time.</para>
    ///
    /// <para><b>What stops it erasing shadows.</b> Position and normal cannot tell a shadow edge from noise,
    /// because both sides of it are the same flat surface a texel apart - and a shadow is at its SHARPEST exactly
    /// where an occluder meets the ground, since the penumbra is zero at contact. Measured: with only the two
    /// geometric weights, denoising a five-sample bake moved it further from the converged answer, not closer
    /// (RMS 12 to 29), by rounding off every contact shadow in the map. The third weight - on the difference in
    /// the values themselves - is what separates "these differ by about the noise, average them" from "these
    /// differ by far more than the noise, leave them alone". It is the standard third term of an A-trous
    /// denoiser, and omitting it is not an optimisation.</para>
    /// </summary>
    private static void Denoise(float[] v, bool[] cover, Vector3[] pos, Vector3[] nrm, int size, Advanced o)
    {
        int iterations = Math.Clamp(o.DenoiseIterations, 1, 5);
        float nrmPower = MathF.Max(o.DenoiseNormalSharpness, 1f);

        // How far apart neighbouring texels actually are IN THE WORLD. This has to be measured, not chosen: the
        // same object is baked at 64 px on one map and 1024 on another, and a lightmap texel can cover anything
        // from a few millimetres to a couple of metres. A fixed metre radius would blur eighty texels on one map
        // and none at all on the next; expressing the reach in TEXELS and converting through the measured
        // footprint makes the filter behave the same way at every resolution.
        float footprint = TexelFootprint(cover, pos, size);
        float posSigma = MathF.Max(o.DenoiseTexelRadius * footprint, 1e-4f);
        // How big a difference still counts as noise. Anything larger is treated as an edge and preserved.
        float valSigma = MathF.Max(o.DenoiseEdgeSensitivity, 1e-4f);
        // The 5-tap B3 spline kernel the A-trous scheme is built on.
        ReadOnlySpan<float> kernel = stackalloc float[5] { 1f / 16f, 4f / 16f, 6f / 16f, 4f / 16f, 1f / 16f };
        var src = new float[v.Length];

        for (int it = 0; it < iterations; it++)
        {
            Array.Copy(v, src, v.Length);
            int step = 1 << it;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int o0 = y * size + x;
                    if (!cover[o0]) continue;
                    var p0 = pos[o0]; var n0 = nrm[o0];
                    float acc = 0f, wsum = 0f;
                    for (int dy = -2; dy <= 2; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            int nx = x + dx * step, ny = y + dy * step;
                            if (nx < 0 || ny < 0 || nx >= size || ny >= size) continue;
                            int o1 = ny * size + nx;
                            if (!cover[o1]) continue;
                            float w = kernel[dx + 2] * kernel[dy + 2];
                            // Same surface? Distance in metres, and the angle between the normals.
                            float d = (pos[o1] - p0).Length() / posSigma;
                            w *= MathF.Exp(-d * d);
                            float nd = MathF.Max(0f, Vector3.Dot(n0, nrm[o1]));
                            w *= MathF.Pow(nd, nrmPower);
                            // The edge-stopping term: a tap that differs by much more than the noise is a
                            // different lighting condition, not a noisy sample of this one.
                            float dv = (src[o1] - src[o0]) / valSigma;
                            w *= MathF.Exp(-dv * dv);
                            if (w <= 1e-6f) continue;
                            acc += src[o1] * w; wsum += w;
                        }
                    if (wsum > 1e-6f) v[o0] = acc / wsum;
                }
        }
    }

    /// <summary>The median world-space distance between horizontally adjacent covered texels - one texel's own
    /// size on the surface. Median rather than mean, because a chart boundary puts a handful of enormous
    /// distances in the sample and they would drag an average anywhere.</summary>
    private static float TexelFootprint(bool[] cover, Vector3[] pos, int size)
    {
        var d = new List<float>(size * 2);
        for (int y = 0; y < size; y++)
            for (int x = 0; x + 1 < size; x++)
            {
                int a = y * size + x;
                if (!cover[a] || !cover[a + 1]) continue;
                float len = (pos[a + 1] - pos[a]).Length();
                if (len > 0f) d.Add(len);
            }
        if (d.Count == 0) return 0.05f;
        d.Sort();
        return d[d.Count / 2];
    }

    // Grow covered intensities a few texels into the uncovered gutter so bilinear sampling at UV seams doesn't read black.
    private static void Dilate(float[] inten, bool[] cover, int size)
    {
        for (int pass = 0; pass < 3; pass++)
        {
            var src = (bool[])cover.Clone();
            bool any = false;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int o = y * size + x;
                    if (src[o]) continue;
                    float sum = 0; int n = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= size || ny >= size || (dx == 0 && dy == 0)) continue;
                            int no = ny * size + nx;
                            if (src[no]) { sum += inten[no]; n++; }
                        }
                    if (n > 0) { inten[o] = sum / n; cover[o] = true; any = true; }
                }
            if (!any) break;
        }
    }
}
