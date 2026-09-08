using System;
using System.Collections.Generic;
using System.Numerics;
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
        NightBake.Scene? night = null, bool colour = false, int lampSamples = 1, float sunLevel = 1f)
    {
        var lm = mesh.LightmapUvs;
        if (lm is null || lm.Length == 0 || size < 4) return null;
        samples = Math.Clamp(samples, 1, 4);
        var pos = mesh.Positions;
        var (_, maxH) = TerrainShadow.HeightSpan(hm, cfg);
        var sun = Vector3.Normalize(new Vector3(sunDir.X, sunDir.Y, sunDir.Z));
        var lampCursor = night?.NewCursor();

        MeshOccluder? occ = null;
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
            occ = MeshOccluder.Build(tris);
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
        int triId = 0;

        foreach (var part in mesh.Parts)
        {
            var idx = part.Indices;
            for (int t = 0; t + 2 < idx.Length; t += 3, triId++)
            {
                int a = idx[t], b = idx[t + 1], c = idx[t + 2];
                if ((uint)a >= (uint)pos.Length || (uint)b >= (uint)pos.Length || (uint)c >= (uint)pos.Length) continue;
                if (a >= lm.Length || b >= lm.Length || c >= lm.Length) continue;

                Vector3 wa = Vector3.Transform(pos[a], world), wb = Vector3.Transform(pos[b], world), wc = Vector3.Transform(pos[c], world);
                Vector3 fn = Vector3.Cross(wb - wa, wc - wa);
                if (fn.LengthSquared() < 1e-12f) continue;
                // Refractor meshes are Direct3D CLOCKWISE-front: cross(b-a, c-a) points INTO the object (measured on
                // Saigon68's barrel: 0 of 48 triangles outward). The outward normal is the negation. With the sign
                // wrong, a lamp on the lit side of a wall was rejected as "behind" it and the shadow ray started
                // inside the wall - the lamp painted almost nothing on real buildings.
                fn = -Vector3.Normalize(fn);

                Vector2 ta = lm[a] * size, tb = lm[b] * size, tc = lm[c] * size;
                // A vertex with a NaN in its second UV set (retail BfVietnam meshes carry a few) would turn the
                // whole triangle's texel range into int.MinValue and skip - silently. Skip it on purpose.
                if (!float.IsFinite(ta.X) || !float.IsFinite(ta.Y) || !float.IsFinite(tb.X) || !float.IsFinite(tb.Y) || !float.IsFinite(tc.X) || !float.IsFinite(tc.Y)) continue;
                int thisTri = triId;
                RasterTriangle(ta, tb, tc, size, samples, (px, py, w0, w1, w2) =>
                {
                    int o = py * size + px;
                    if (owner[o] == -1) owner[o] = thisTri;
                    else if (owner[o] != thisTri) return;               // first triangle wins (atlas overlaps are rare)
                    var wp = w0 * wa + w1 * wb + w2 * wc;
                    // Visibility only. Which side of the face the sun is on is the shader's business (N.L); whether
                    // the object's own roof or wall is in the way is ours. The ray starts a little along the sun
                    // direction, so a single-sided plane does not shadow itself.
                    bool lit = TerrainShadow.PointLit(wp.X, wp.Y, wp.Z, sunDir, hm, cfg, maxH)
                               && (occ is null || !occ.Occluded(wp, sun));
                    float v = ambient + (1f - ambient) * (lit ? sunLevel : 0f);

                    // Placed lights add on top of the sun. With a night scene they are shadowed by the whole level
                    // and softened over the lamp's size; without one, terrain-only occlusion as before. The colour
                    // is kept for a 24-bit map (BF1942) and folded to Rec. 709 luma for a grey one (BfVietnam).
                    float lr = 0f, lg = 0f, lb = 0f;
                    if (rig is not null && rig.Lights.Count > 0)
                    {
                        if (night is not null)
                        {
                            var c = NightBake.Lamp(night, wp, fn, rig, lampCursor, lampSamples, ground: false,
                                                   seed: (uint)(px * 73856093 ^ py * 19349663));
                            lr = c.X; lg = c.Y; lb = c.Z;
                        }
                        else
                        {
                            float add = LightBake.Intensity(wp.X, wp.Y, wp.Z, rig, hm, cfg);
                            // Angle still matters - a face turned away from a lamp should not brighten - but with
                            // the same soft wrap the viewport preview uses, so the bake matches what was aimed.
                            float lndl = 0f;
                            foreach (var l in rig.Lights)
                            {
                                if (!l.Enabled) continue;
                                var toL = new Vector3(l.Position.X - wp.X, l.Position.Y - wp.Y, l.Position.Z - wp.Z);
                                if (toL.LengthSquared() < 1e-8f) { lndl = 1f; break; }
                                lndl = MathF.Max(lndl, MathF.Max(0f, Vector3.Dot(fn, Vector3.Normalize(toL))));
                            }
                            lr = lg = lb = add * (lndl * 0.85f + 0.15f);
                        }
                    }
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
                });
            }
        }

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
                                       Action<int, int, float, float, float> px)
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
                        px(x, y, w0, w1, w2);
                        any = true;
                    }
                // A texel whose centre-ish samples all missed but which the triangle still clips: keep the old
                // single-centre behaviour so thin geometry does not lose its texels entirely.
                if (!any && samples > 1)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    float w0 = Edge(b, c, p) * inv, w1 = Edge(c, a, p) * inv, w2 = Edge(a, b, p) * inv;
                    if (w0 >= -0.002f && w1 >= -0.002f && w2 >= -0.002f) px(x, y, w0, w1, w2);
                }
            }
    }

    private static float Edge(Vector2 a, Vector2 b, Vector2 c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

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
