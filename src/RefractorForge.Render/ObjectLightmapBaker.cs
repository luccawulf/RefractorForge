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
    /// <summary>Bake one object's lightmap. <paramref name="mesh"/> must carry lightmap UVs (40-byte / format-9233 mesh);
    /// returns null otherwise. <paramref name="world"/> places the mesh in world space. Intensity = ambient + (1-ambient)·
    /// N·L · shadow.</summary>
    public static Texture2D? Bake(MeshLibrary.Mesh mesh, Matrix4x4 world, Heightmap hm, TerrainConfig cfg, Vec3 sunDir,
        int size = 256, float ambient = 0.4f, LightRig? rig = null, bool selfShadow = true)
    {
        var lm = mesh.LightmapUvs;
        if (lm is null || lm.Length == 0 || size < 4) return null;
        var pos = mesh.Positions;
        var (_, maxH) = TerrainShadow.HeightSpan(hm, cfg);
        var sun = Vector3.Normalize(new Vector3(sunDir.X, sunDir.Y, sunDir.Z));

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

        foreach (var part in mesh.Parts)
        {
            var idx = part.Indices;
            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                int a = idx[t], b = idx[t + 1], c = idx[t + 2];
                if ((uint)a >= (uint)pos.Length || (uint)b >= (uint)pos.Length || (uint)c >= (uint)pos.Length) continue;
                if (a >= lm.Length || b >= lm.Length || c >= lm.Length) continue;

                Vector3 wa = Vector3.Transform(pos[a], world), wb = Vector3.Transform(pos[b], world), wc = Vector3.Transform(pos[c], world);
                Vector3 fn = Vector3.Cross(wb - wa, wc - wa);
                if (fn.LengthSquared() < 1e-12f) continue;
                fn = Vector3.Normalize(fn);

                Vector2 ta = lm[a] * size, tb = lm[b] * size, tc = lm[c] * size;
                // A vertex with a NaN in its second UV set (retail BfVietnam meshes carry a few) would turn the
                // whole triangle's texel range into int.MinValue and skip - silently. Skip it on purpose.
                if (!float.IsFinite(ta.X) || !float.IsFinite(ta.Y) || !float.IsFinite(tb.X) || !float.IsFinite(tb.Y) || !float.IsFinite(tc.X) || !float.IsFinite(tc.Y)) continue;
                RasterTriangle(ta, tb, tc, size, (px, py, w0, w1, w2) =>
                {
                    int o = py * size + px;
                    if (cover[o]) return;                              // first triangle wins (atlas overlaps are rare)
                    var wp = w0 * wa + w1 * wb + w2 * wc;
                    // Visibility only. Which side of the face the sun is on is the shader's business (N.L); whether
                    // the object's own roof or wall is in the way is ours. The ray starts a little along the sun
                    // direction, so a single-sided plane does not shadow itself.
                    bool lit = TerrainShadow.PointLit(wp.X, wp.Y, wp.Z, sunDir, hm, cfg, maxH)
                               && (occ is null || !occ.Occluded(wp, sun));
                    float v = ambient + (1f - ambient) * (lit ? 1f : 0f);

                    // Placed lights add on top of the sun, as INTENSITY. Every shipped object lightmap checked
                    // across retail levels is a grey-palette TGA, so the format carries brightness and the
                    // engine is never handed a hue here: a coloured lamp brightens an object without tinting
                    // it, and the colour lives in the ground texture instead.
                    if (rig is not null && rig.Lights.Count > 0)
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
                        v += add * (lndl * 0.85f + 0.15f);
                    }

                    inten[o] = MathF.Min(v, 1f);
                    cover[o] = true;
                });
            }
        }

        // A mesh whose second UV set is all one point (BfVietnam props and walls ship 0,0 on every vertex - the slot
        // exists, the unwrap does not) rasterises nothing. Baking it anyway shipped an all-black map that turned the
        // object black in the editor and in the game; such an object has no lightmap and stays dynamically lit.
        int covered = 0;
        foreach (var c in cover) if (c) covered++;
        if (covered < size * size / 2000) return null;

        Dilate(inten, cover, size);                                   // spread into the UV gutter so seams don't bleed black

        var rgba = new byte[size * size * 4];
        for (int i = 0; i < size * size; i++)
        {
            byte v = (byte)Math.Clamp((int)(inten[i] * 255f + 0.5f), 0, 255);
            rgba[i * 4] = v; rgba[i * 4 + 1] = v; rgba[i * 4 + 2] = v; rgba[i * 4 + 3] = 255;
        }
        return new Texture2D(size, size, rgba);
    }

    // Half-space barycentric triangle rasterizer in texel space.
    private static void RasterTriangle(Vector2 a, Vector2 b, Vector2 c, int size, Action<int, int, float, float, float> px)
    {
        int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
        int maxX = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
        int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
        int maxY = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
        float area = Edge(a, b, c);
        if (MathF.Abs(area) < 1e-6f) return;
        float inv = 1f / area;
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                float w0 = Edge(b, c, p) * inv, w1 = Edge(c, a, p) * inv, w2 = Edge(a, b, p) * inv;
                if (w0 < -0.002f || w1 < -0.002f || w2 < -0.002f) continue;   // outside (epsilon catches seam texels)
                px(x, y, w0, w1, w2);
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
