using System.Numerics;
using RefractorForge.Formats;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Terrain;

namespace RefractorForge.Render;

/// <summary>An RGB framebuffer with a z-buffer; saves 24-bit BMP (no image library needed).</summary>
public sealed class ImageBuffer
{
    public readonly int W, H;
    public readonly byte[] Rgb;
    public readonly float[] Depth;

    public ImageBuffer(int w, int h)
    {
        W = w; H = h;
        Rgb = new byte[w * h * 3];
        Depth = new float[w * h];
        Array.Fill(Depth, float.MaxValue);
    }

    public void Clear(Vector3 c)
    {
        byte r = ToByte(c.X), g = ToByte(c.Y), b = ToByte(c.Z);
        for (int i = 0; i < W * H; i++) { Rgb[i * 3] = r; Rgb[i * 3 + 1] = g; Rgb[i * 3 + 2] = b; Depth[i] = float.MaxValue; }
    }

    public void SaveBmp(string path)
    {
        int rowPad = (4 - (W * 3) % 4) % 4;
        int dataSize = (W * 3 + rowPad) * H;
        using var bw = new BinaryWriter(File.Create(path));
        bw.Write((byte)'B'); bw.Write((byte)'M');
        bw.Write(54 + dataSize); bw.Write(0); bw.Write(54);
        bw.Write(40); bw.Write(W); bw.Write(H);
        bw.Write((short)1); bw.Write((short)24);
        bw.Write(0); bw.Write(dataSize);
        bw.Write(2835); bw.Write(2835); bw.Write(0); bw.Write(0);
        var pad = new byte[rowPad];
        for (int y = H - 1; y >= 0; y--)   // BMP rows are bottom-up
        {
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 3;
                bw.Write(Rgb[i + 2]); bw.Write(Rgb[i + 1]); bw.Write(Rgb[i]); // BGR
            }
            bw.Write(pad);
        }
    }

    internal static byte ToByte(float v) => (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);

    /// <summary>Box-filter downsample by an integer factor (supersampling anti-aliasing).</summary>
    public ImageBuffer DownsampleBy(int factor)
    {
        if (factor <= 1) return this;
        int tw = W / factor, th = H / factor;
        var outImg = new ImageBuffer(tw, th);
        int f2 = factor * factor;
        for (int y = 0; y < th; y++)
            for (int x = 0; x < tw; x++)
            {
                int r = 0, g = 0, b = 0;
                for (int dy = 0; dy < factor; dy++)
                    for (int dx = 0; dx < factor; dx++)
                    {
                        int si = ((y * factor + dy) * W + (x * factor + dx)) * 3;
                        r += Rgb[si]; g += Rgb[si + 1]; b += Rgb[si + 2];
                    }
                int di = (y * tw + x) * 3;
                outImg.Rgb[di] = (byte)(r / f2); outImg.Rgb[di + 1] = (byte)(g / f2); outImg.Rgb[di + 2] = (byte)(b / f2);
            }
        return outImg;
    }

    /// <summary>Fill the framebuffer with a smooth vertical gradient (studio backdrop).</summary>
    public void ClearGradient(Vector3 top, Vector3 bottom)
    {
        for (int y = 0; y < H; y++)
        {
            float t = H <= 1 ? 0f : (float)y / (H - 1);
            var c = Vector3.Lerp(top, bottom, t);
            byte r = ToByte(c.X), g = ToByte(c.Y), b = ToByte(c.Z);
            for (int x = 0; x < W; x++) { int i = (y * W + x) * 3; Rgb[i] = r; Rgb[i + 1] = g; Rgb[i + 2] = b; }
        }
        Array.Fill(Depth, float.MaxValue);
    }
}

/// <summary>A renderable mesh as plain arrays. Proxy geometry today; real StandardMesh later
/// fills the same shape, so the render path doesn't change when meshes arrive.</summary>
public readonly record struct ModelInstance(Matrix4x4 World, Vector3 Color);

/// <summary>Minimal painter's-correct software renderer: z-buffered, flat-shaded triangles.</summary>
public static class SoftwareRenderer
{
    /// <summary>Unit cube centered at origin (1m). Face normals are derived at draw time.</summary>
    public static readonly Vector3[] CubePositions =
    {
        new(-.5f,-.5f,-.5f), new(.5f,-.5f,-.5f), new(.5f,.5f,-.5f), new(-.5f,.5f,-.5f),
        new(-.5f,-.5f, .5f), new(.5f,-.5f, .5f), new(.5f,.5f, .5f), new(-.5f,.5f, .5f),
    };
    public static readonly int[] CubeIndices =
    {
        0,2,1, 0,3,2,  4,5,6, 4,6,7,  0,1,5, 0,5,4,
        2,3,7, 2,7,6,  1,2,6, 1,6,5,  0,4,7, 0,7,3,
    };

    /// <summary>Draw many instances of one model (e.g. the cube) with per-instance transform+color.</summary>
    /// <summary>Instanced draw of a mesh part sampling a real texture via per-vertex UVs (perspective
    /// correct), modulated by face-normal diffuse lighting and an optional per-instance tint. Optional
    /// alpha test discards transparent texels (foliage cutouts).</summary>
    public static void DrawModelsTextured(ImageBuffer img, Camera cam, Vector3 lightDir,
                                          Vector3[] modelPos, Vector2[] modelUv, int[] modelIdx,
                                          Texture2D tex, bool alphaTest, IReadOnlyList<ModelInstance> instances)
    {
        var ld = Vector3.Normalize(lightDir);
        var vpCam = cam.ViewProjection;
        int vn = modelPos.Length;
        var wp = new Vector3[vn];
        var sx = new float[vn]; var sy = new float[vn]; var sz = new float[vn]; var iwArr = new float[vn]; var ok = new bool[vn];
        foreach (var inst in instances)
        {
            for (int i = 0; i < vn; i++)
            {
                wp[i] = Vector3.Transform(modelPos[i], inst.World);
                var c = Vector4.Transform(new Vector4(wp[i], 1f), vpCam);
                if (c.W <= 1e-4f) { ok[i] = false; continue; }
                float iw = 1f / c.W;
                sx[i] = (c.X * iw * 0.5f + 0.5f) * img.W;
                sy[i] = (1f - (c.Y * iw * 0.5f + 0.5f)) * img.H;
                sz[i] = c.Z * iw; iwArr[i] = iw; ok[i] = true;
            }
            for (int t = 0; t < modelIdx.Length; t += 3)
            {
                int a = modelIdx[t], b = modelIdx[t + 1], c2 = modelIdx[t + 2];
                if (!ok[a] || !ok[b] || !ok[c2]) continue;
                var fn = Vector3.Cross(wp[b] - wp[a], wp[c2] - wp[a]);
                if (fn.LengthSquared() < 1e-12f) continue;
                fn = Vector3.Normalize(fn);
                float diff = 0.45f + 0.55f * MathF.Abs(Vector3.Dot(fn, ld));
                RasterTriangleTextured(img, tex, alphaTest, inst.Color, diff,
                    sx[a], sy[a], sz[a], iwArr[a], modelUv[a],
                    sx[b], sy[b], sz[b], iwArr[b], modelUv[b],
                    sx[c2], sy[c2], sz[c2], iwArr[c2], modelUv[c2]);
            }
        }
    }

    private static void RasterTriangleTextured(ImageBuffer img, Texture2D tex, bool alphaTest, Vector3 tint, float diff,
        float x0, float y0, float z0, float w0i, Vector2 t0,
        float x1, float y1, float z1, float w1i, Vector2 t1,
        float x2, float y2, float z2, float w2i, Vector2 t2)
    {
        int minX = Math.Max(0, (int)MathF.Floor(Math.Min(x0, Math.Min(x1, x2))));
        int maxX = Math.Min(img.W - 1, (int)MathF.Ceiling(Math.Max(x0, Math.Max(x1, x2))));
        int minY = Math.Max(0, (int)MathF.Floor(Math.Min(y0, Math.Min(y1, y2))));
        int maxY = Math.Min(img.H - 1, (int)MathF.Ceiling(Math.Max(y0, Math.Max(y1, y2))));
        if (minX > maxX || minY > maxY) return;
        float area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
        if (MathF.Abs(area) < 1e-6f) return;
        float inv = 1f / area;
        // perspective-correct UV: interpolate u/w, v/w, 1/w
        float u0 = t0.X * w0i, v0 = t0.Y * w0i, u1 = t1.X * w1i, v1 = t1.Y * w1i, u2 = t2.X * w2i, v2 = t2.Y * w2i;
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                float fx = x + 0.5f, fy = y + 0.5f;
                float b0 = ((x1 - fx) * (y2 - fy) - (x2 - fx) * (y1 - fy)) * inv;
                float b1 = ((x2 - fx) * (y0 - fy) - (x0 - fx) * (y2 - fy)) * inv;
                float b2 = 1f - b0 - b1;
                if (b0 < 0f || b1 < 0f || b2 < 0f) continue;
                float z = b0 * z0 + b1 * z1 + b2 * z2;
                int pi = y * img.W + x;
                if (z >= img.Depth[pi]) continue;
                float iw = b0 * w0i + b1 * w1i + b2 * w2i;
                if (iw <= 0f) continue;
                float u = (b0 * u0 + b1 * u1 + b2 * u2) / iw;
                float v = (b0 * v0 + b1 * v1 + b2 * v2) / iw;
                var rgba = tex.SampleRGBA(u, v);
                if (alphaTest && rgba.W < 0.5f) continue;     // discard transparent cutout texels
                img.Depth[pi] = z;
                img.Rgb[pi * 3] = ImageBuffer.ToByte(rgba.X * tint.X * diff);
                img.Rgb[pi * 3 + 1] = ImageBuffer.ToByte(rgba.Y * tint.Y * diff);
                img.Rgb[pi * 3 + 2] = ImageBuffer.ToByte(rgba.Z * tint.Z * diff);
            }
    }

    public static void DrawModels(ImageBuffer img, Camera cam, Vector3 lightDir,
                                  Vector3[] modelPos, int[] modelIdx, IReadOnlyList<ModelInstance> instances)
    {
        var ld = Vector3.Normalize(lightDir);
        var vpCam = cam.ViewProjection;
        int vn = modelPos.Length;
        var wp = new Vector3[vn];                  // world positions (reused per instance)
        var sx = new float[vn]; var sy = new float[vn]; var sz = new float[vn]; var ok = new bool[vn];

        foreach (var inst in instances)
        {
            for (int i = 0; i < vn; i++)
            {
                wp[i] = Vector3.Transform(modelPos[i], inst.World);
                var c = Vector4.Transform(new Vector4(wp[i], 1f), vpCam);
                if (c.W <= 1e-4f) { ok[i] = false; continue; }
                float iw = 1f / c.W;
                sx[i] = (c.X * iw * 0.5f + 0.5f) * img.W;
                sy[i] = (1f - (c.Y * iw * 0.5f + 0.5f)) * img.H;
                sz[i] = c.Z * iw;
                ok[i] = true;
            }
            for (int t = 0; t < modelIdx.Length; t += 3)
            {
                int a = modelIdx[t], b = modelIdx[t + 1], c2 = modelIdx[t + 2];
                if (!ok[a] || !ok[b] || !ok[c2]) continue;
                var fn = Vector3.Cross(wp[b] - wp[a], wp[c2] - wp[a]);
                if (fn.LengthSquared() < 1e-12f) continue;
                fn = Vector3.Normalize(fn);
                float diff = 0.35f + 0.65f * MathF.Abs(Vector3.Dot(fn, ld));
                RasterTriangle(img, sx[a], sy[a], sz[a], sx[b], sy[b], sz[b], sx[c2], sy[c2], sz[c2], inst.Color * diff);
            }
        }
    }

    /// <summary>
    /// Draw a single mesh with computed smooth normals, Gouraud shading and a 3-point light rig.
    /// Used for previewing real StandardMesh geometry. File vertex normals are often non-normalized
    /// or absent on these assets, so normals are recomputed by accumulating (area-weighted) face
    /// normals per vertex — this gives clean, faceting-free shading regardless of the source data.
    /// Two-sided (a small back term keeps inward-facing triangles from going black on open meshes).
    /// </summary>
    public static void DrawMeshSmooth(ImageBuffer img, Camera cam, Vector3 keyDir,
                                      Vector3[] pos, int[] idx, Vector3 baseColor, int cull = 0)
    {
        int vn = pos.Length;
        // 1) smooth vertex normals (area-weighted: cross product is left unnormalized on purpose).
        var nrm = new Vector3[vn];
        for (int t = 0; t + 2 < idx.Length; t += 3)
        {
            int a = idx[t], b = idx[t + 1], c = idx[t + 2];
            var fn = Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]);
            nrm[a] += fn; nrm[b] += fn; nrm[c] += fn;
        }
        for (int i = 0; i < vn; i++)
            nrm[i] = nrm[i].LengthSquared() > 1e-20f ? Vector3.Normalize(nrm[i]) : Vector3.UnitY;

        // 2) light rig.
        var key = Vector3.Normalize(keyDir);
        var fill = Vector3.Normalize(new Vector3(-key.X, 0.2f, -key.Z));   // opposite-ish, low
        const float ambient = 0.30f, keyI = 0.85f, fillI = 0.30f, backI = 0.18f;

        // 3) project + per-vertex shade.
        var vp = cam.ViewProjection;
        var sx = new float[vn]; var sy = new float[vn]; var sz = new float[vn];
        var sh = new float[vn]; var ok = new bool[vn];
        for (int i = 0; i < vn; i++)
        {
            var c = Vector4.Transform(new Vector4(pos[i], 1f), vp);
            if (c.W <= 1e-4f) { ok[i] = false; continue; }
            float iw = 1f / c.W;
            sx[i] = (c.X * iw * 0.5f + 0.5f) * img.W;
            sy[i] = (1f - (c.Y * iw * 0.5f + 0.5f)) * img.H;
            sz[i] = c.Z * iw; ok[i] = true;
            var n = nrm[i];
            float ndl = Vector3.Dot(n, key);
            float s = ambient
                    + keyI * MathF.Max(0f, ndl)
                    + fillI * MathF.Max(0f, Vector3.Dot(n, fill))
                    + backI * MathF.Max(0f, -ndl);            // two-sided softener
            sh[i] = MathF.Min(1.15f, s);
        }

        // 4) raster with Gouraud-interpolated shade.
        for (int t = 0; t + 2 < idx.Length; t += 3)
        {
            int a = idx[t], b = idx[t + 1], c = idx[t + 2];
            if (!ok[a] || !ok[b] || !ok[c]) continue;
            if (cull != 0)
            {
                // signed screen-space area; sign indicates facing. cull=1 drops CW, cull=-1 drops CCW.
                float sa = (sx[b] - sx[a]) * (sy[c] - sy[a]) - (sx[c] - sx[a]) * (sy[b] - sy[a]);
                if (cull > 0 ? sa <= 0f : sa >= 0f) continue;
            }
            RasterTriangleGouraud(img, sx[a], sy[a], sz[a], sh[a], sx[b], sy[b], sz[b], sh[b],
                                  sx[c], sy[c], sz[c], sh[c], baseColor);
        }
    }

    private static void RasterTriangleGouraud(ImageBuffer img,
        float x0, float y0, float z0, float s0, float x1, float y1, float z1, float s1,
        float x2, float y2, float z2, float s2, Vector3 col)
    {
        int minX = Math.Max(0, (int)MathF.Floor(Math.Min(x0, Math.Min(x1, x2))));
        int maxX = Math.Min(img.W - 1, (int)MathF.Ceiling(Math.Max(x0, Math.Max(x1, x2))));
        int minY = Math.Max(0, (int)MathF.Floor(Math.Min(y0, Math.Min(y1, y2))));
        int maxY = Math.Min(img.H - 1, (int)MathF.Ceiling(Math.Max(y0, Math.Max(y1, y2))));
        if (minX > maxX || minY > maxY) return;

        float area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
        if (MathF.Abs(area) < 1e-6f) return;
        float inv = 1f / area;

        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                float fx = x + 0.5f, fy = y + 0.5f;
                float w0 = ((x1 - fx) * (y2 - fy) - (x2 - fx) * (y1 - fy)) * inv;
                float w1 = ((x2 - fx) * (y0 - fy) - (x0 - fx) * (y2 - fy)) * inv;
                float w2 = 1f - w0 - w1;
                if (w0 < 0f || w1 < 0f || w2 < 0f) continue;
                float z = w0 * z0 + w1 * z1 + w2 * z2;
                int pi = y * img.W + x;
                if (z < img.Depth[pi])
                {
                    img.Depth[pi] = z;
                    float s = w0 * s0 + w1 * s1 + w2 * s2;
                    img.Rgb[pi * 3] = ImageBuffer.ToByte(col.X * s);
                    img.Rgb[pi * 3 + 1] = ImageBuffer.ToByte(col.Y * s);
                    img.Rgb[pi * 3 + 2] = ImageBuffer.ToByte(col.Z * s);
                }
            }
    }

    /// <summary>Draw the terrain textured with the level's real baked tiles: per-pixel texture sample
    /// (world XZ → UV) modulated by per-vertex diffuse lighting, plus a translucent water surface below
    /// <paramref name="waterLevel"/>. Falls back to <see cref="DrawTerrain"/> when no texture is supplied.</summary>
    public static void DrawTerrainTextured(ImageBuffer img, TerrainMesh mesh, Camera cam, Vector3 lightDir, TerrainTexture tex, float waterLevel)
    {
        var vp = cam.ViewProjection;
        int n = mesh.Positions.Length;
        var px = new float[n]; var py = new float[n]; var pz = new float[n]; var ok = new bool[n];
        var uu = new float[n]; var vv = new float[n]; var sh = new float[n]; var wy = new float[n];
        var ld = Vector3.Normalize(lightDir);
        for (int i = 0; i < n; i++)
        {
            var c = Vector4.Transform(new Vector4(mesh.Positions[i], 1f), vp);
            if (c.W <= 1e-4f) { ok[i] = false; continue; }
            float iw = 1f / c.W;
            px[i] = (c.X * iw * 0.5f + 0.5f) * img.W;
            py[i] = (1f - (c.Y * iw * 0.5f + 0.5f)) * img.H;
            pz[i] = c.Z * iw; ok[i] = true;
            (uu[i], vv[i]) = tex.Uv(mesh.Positions[i].X, mesh.Positions[i].Z);
            sh[i] = 0.45f + 0.55f * MathF.Max(0f, Vector3.Dot(mesh.Normals[i], ld));
            wy[i] = mesh.Positions[i].Y;
        }
        var idx = mesh.Indices;
        for (int t = 0; t < idx.Length; t += 3)
        {
            int a = idx[t], b = idx[t + 1], c2 = idx[t + 2];
            if (!ok[a] || !ok[b] || !ok[c2]) continue;
            RasterTriangleTexLit(img, tex, waterLevel,
                px[a], py[a], pz[a], uu[a], vv[a], sh[a], wy[a],
                px[b], py[b], pz[b], uu[b], vv[b], sh[b], wy[b],
                px[c2], py[c2], pz[c2], uu[c2], vv[c2], sh[c2], wy[c2]);
        }
    }

    private static readonly Vector3 WaterColor = new(0.16f, 0.34f, 0.46f);

    private static void RasterTriangleTexLit(ImageBuffer img, TerrainTexture tex, float waterLevel,
        float x0, float y0, float z0, float u0, float v0, float s0, float h0,
        float x1, float y1, float z1, float u1, float v1, float s1, float h1,
        float x2, float y2, float z2, float u2, float v2, float s2, float h2)
    {
        int minX = Math.Max(0, (int)MathF.Floor(Math.Min(x0, Math.Min(x1, x2))));
        int maxX = Math.Min(img.W - 1, (int)MathF.Ceiling(Math.Max(x0, Math.Max(x1, x2))));
        int minY = Math.Max(0, (int)MathF.Floor(Math.Min(y0, Math.Min(y1, y2))));
        int maxY = Math.Min(img.H - 1, (int)MathF.Ceiling(Math.Max(y0, Math.Max(y1, y2))));
        if (minX > maxX || minY > maxY) return;
        float area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
        if (MathF.Abs(area) < 1e-6f) return;
        float inv = 1f / area;
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                float fx = x + 0.5f, fy = y + 0.5f;
                float w0 = ((x1 - fx) * (y2 - fy) - (x2 - fx) * (y1 - fy)) * inv;
                float w1 = ((x2 - fx) * (y0 - fy) - (x0 - fx) * (y2 - fy)) * inv;
                float w2 = 1f - w0 - w1;
                if (w0 < 0f || w1 < 0f || w2 < 0f) continue;
                float z = w0 * z0 + w1 * z1 + w2 * z2;
                int pi = y * img.W + x;
                if (z < img.Depth[pi])
                {
                    img.Depth[pi] = z;
                    float u = w0 * u0 + w1 * u1 + w2 * u2;
                    float v = w0 * v0 + w1 * v1 + w2 * v2;
                    float s = w0 * s0 + w1 * s1 + w2 * s2;
                    var col = tex.SampleUvDetailed(u, v) * s;
                    float wh = w0 * h0 + w1 * h1 + w2 * h2;
                    if (wh < waterLevel)
                    {
                        // translucent water: shallow shows the bed, deep turns opaque blue
                        float depth = waterLevel - wh;
                        float a = Math.Clamp(depth / 4f, 0.25f, 0.85f);
                        col = Vector3.Lerp(col * 0.7f, WaterColor, a);
                    }
                    img.Rgb[pi * 3] = ImageBuffer.ToByte(col.X);
                    img.Rgb[pi * 3 + 1] = ImageBuffer.ToByte(col.Y);
                    img.Rgb[pi * 3 + 2] = ImageBuffer.ToByte(col.Z);
                }
            }
    }

    public static void DrawTerrain(ImageBuffer img, TerrainMesh mesh, Camera cam, Vector3 lightDir,
                                   float waterLevel, float minH, float maxH)
    {
        var vp = cam.ViewProjection;
        int n = mesh.Positions.Length;
        var px = new float[n]; var py = new float[n]; var pz = new float[n]; var ok = new bool[n];
        for (int i = 0; i < n; i++)
        {
            var c = Vector4.Transform(new Vector4(mesh.Positions[i], 1f), vp);
            if (c.W <= 1e-4f) { ok[i] = false; continue; }
            float iw = 1f / c.W;
            px[i] = (c.X * iw * 0.5f + 0.5f) * img.W;
            py[i] = (1f - (c.Y * iw * 0.5f + 0.5f)) * img.H;
            pz[i] = c.Z * iw;
            ok[i] = true;
        }

        var ld = Vector3.Normalize(lightDir);
        var idx = mesh.Indices;
        for (int t = 0; t < idx.Length; t += 3)
        {
            int a = idx[t], b = idx[t + 1], c2 = idx[t + 2];
            if (!ok[a] || !ok[b] || !ok[c2]) continue;

            var fn = mesh.Normals[a] + mesh.Normals[b] + mesh.Normals[c2];
            if (fn.LengthSquared() < 1e-12f) continue;
            fn = Vector3.Normalize(fn);
            float diff = 0.4f + 0.6f * MathF.Max(0f, Vector3.Dot(fn, ld));
            float avgH = (mesh.Positions[a].Y + mesh.Positions[b].Y + mesh.Positions[c2].Y) / 3f;
            var col = Ramp(avgH, waterLevel, minH, maxH) * diff;

            RasterTriangle(img, px[a], py[a], pz[a], px[b], py[b], pz[b], px[c2], py[c2], pz[c2], col);
        }
    }

    public static void DrawMarkers(ImageBuffer img, IEnumerable<Vector3> worldPoints, Camera cam, Vector3 color, int radius)
    {
        var vp = cam.ViewProjection;
        foreach (var p in worldPoints)
        {
            var c = Vector4.Transform(new Vector4(p, 1f), vp);
            if (c.W <= 1e-4f) continue;
            float iw = 1f / c.W;
            int sx = (int)((c.X * iw * 0.5f + 0.5f) * img.W);
            int sy = (int)((1f - (c.Y * iw * 0.5f + 0.5f)) * img.H);
            float sz = c.Z * iw;
            for (int dy = -radius; dy <= radius; dy++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (dx * dx + dy * dy > radius * radius) continue;
                    int x = sx + dx, y = sy + dy;
                    if ((uint)x >= (uint)img.W || (uint)y >= (uint)img.H) continue;
                    int pi = y * img.W + x;
                    if (sz <= img.Depth[pi] + 1e-4f)   // in front of (or on) terrain
                    {
                        img.Rgb[pi * 3] = ImageBuffer.ToByte(color.X);
                        img.Rgb[pi * 3 + 1] = ImageBuffer.ToByte(color.Y);
                        img.Rgb[pi * 3 + 2] = ImageBuffer.ToByte(color.Z);
                    }
                }
        }
    }

    private static void RasterTriangle(ImageBuffer img,
        float x0, float y0, float z0, float x1, float y1, float z1, float x2, float y2, float z2, Vector3 col)
    {
        int minX = Math.Max(0, (int)MathF.Floor(Math.Min(x0, Math.Min(x1, x2))));
        int maxX = Math.Min(img.W - 1, (int)MathF.Ceiling(Math.Max(x0, Math.Max(x1, x2))));
        int minY = Math.Max(0, (int)MathF.Floor(Math.Min(y0, Math.Min(y1, y2))));
        int maxY = Math.Min(img.H - 1, (int)MathF.Ceiling(Math.Max(y0, Math.Max(y1, y2))));
        if (minX > maxX || minY > maxY) return;

        float area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
        if (MathF.Abs(area) < 1e-6f) return;
        float inv = 1f / area;
        byte cr = ImageBuffer.ToByte(col.X), cg = ImageBuffer.ToByte(col.Y), cb = ImageBuffer.ToByte(col.Z);

        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                float fx = x + 0.5f, fy = y + 0.5f;
                float w0 = ((x1 - fx) * (y2 - fy) - (x2 - fx) * (y1 - fy)) * inv;
                float w1 = ((x2 - fx) * (y0 - fy) - (x0 - fx) * (y2 - fy)) * inv;
                float w2 = 1f - w0 - w1;
                if (w0 < 0f || w1 < 0f || w2 < 0f) continue;
                float z = w0 * z0 + w1 * z1 + w2 * z2;
                int pi = y * img.W + x;
                if (z < img.Depth[pi])
                {
                    img.Depth[pi] = z;
                    img.Rgb[pi * 3] = cr; img.Rgb[pi * 3 + 1] = cg; img.Rgb[pi * 3 + 2] = cb;
                }
            }
    }

    /// <summary>Height color ramp: water -> beach/green -> brown -> rock.</summary>
    public static Vector3 Ramp(float h, float water, float minH, float maxH)
    {
        if (h < water) return new Vector3(0.16f, 0.35f, 0.55f);   // water
        float t = Math.Clamp((h - water) / MathF.Max(maxH - water, 1f), 0f, 1f);
        // green -> brown gradient
        var lo = new Vector3(0.25f, 0.55f, 0.20f);
        var hi = new Vector3(0.55f, 0.45f, 0.30f);
        var mid = Vector3.Lerp(lo, hi, t);
        if (t > 0.85f) mid = Vector3.Lerp(mid, new Vector3(0.8f, 0.8f, 0.82f), (t - 0.85f) / 0.15f); // rock caps
        return mid;
    }
}

/// <summary>Loads a level and renders an aerial 3D preview BMP using only parsed data.</summary>
public static class HeadlessPreview
{
    /// <summary>The level's own Standardmesh/ folder, which holds per-level .rs material overrides.</summary>
    private static string? FindLevelShaderDir(string levelDir)
        => Directory.EnumerateDirectories(levelDir, "Standardmesh", SearchOption.AllDirectories).FirstOrDefault();

    /// <summary>Render the level from an arbitrary eye looking at a target (for close-ups of object
    /// clusters). Attaches real meshes when archives are given.</summary>
    public static void RenderLevelView(string levelDir, string outBmp, Vector3 eye, Vector3 target,
                                       float fovDeg = 55f, int width = 1100, int height = 825, int stride = 1,
                                       string[]? meshArchives = null, string[]? textureArchives = null)
    {
        var scene = LevelScene.Load(levelDir, stride);
        if (meshArchives is { Length: > 0 })
        {
            var lib = MeshLibrary.Open(meshArchives);
            lib.AttachShaderOverrides(FindLevelShaderDir(levelDir));
            if (textureArchives is { Length: > 0 }) lib.AttachTextures(TextureLibrary.Open(textureArchives));
            scene.AttachMeshes(lib);
        }
        var fwd = Vector3.Normalize(target - eye);
        var cam = new Camera
        {
            Position = eye,
            Pitch = MathF.Asin(Math.Clamp(fwd.Y, -1f, 1f)),
            Yaw = MathF.Atan2(fwd.X, fwd.Z),
            FovY = fovDeg * MathF.PI / 180f,
            Aspect = (float)width / height,
            Near = 0.3f, Far = scene.WorldSize * 3f,
        };
        var img = scene.Render(cam, width, height);
        img.SaveBmp(outBmp);
        string texInfo = scene.TerrainTex is null ? "ramp (no terrain texture)" : $"textured atlas {scene.TerrainTex.AtlasSize}px";
        Console.WriteLine($"Close-up -> {outBmp}  eye=({eye.X:F0},{eye.Y:F0},{eye.Z:F0}) target=({target.X:F0},{target.Y:F0},{target.Z:F0})  terrain={texInfo}");
    }

    public static void RenderLevel(string levelDir, string outBmp, int width = 1100, int height = 825, int stride = 1,
                                   string[]? meshArchives = null, string[]? textureArchives = null)
    {
        var scene = LevelScene.Load(levelDir, stride);
        int resolved = 0, total = scene.Objects.Objects.Count;
        if (meshArchives is { Length: > 0 })
        {
            var lib = MeshLibrary.Open(meshArchives);
            lib.AttachShaderOverrides(FindLevelShaderDir(levelDir));
            if (textureArchives is { Length: > 0 }) lib.AttachTextures(TextureLibrary.Open(textureArchives));
            scene.AttachMeshes(lib);
            foreach (var o in scene.Objects.Objects)
                if (lib.TryGet(o.Template, out _)) resolved++;
        }
        var cam = scene.CreateAerialCamera((float)width / height);
        var img = scene.Render(cam, width, height);
        img.SaveBmp(outBmp);
        string objSummary = scene.Meshes is null
            ? $"{total} object boxes"
            : $"{resolved}/{total} objects as real meshes ({total - resolved} boxed)";
        Console.WriteLine($"Rendered {scene.Mesh.Indices.Length / 3:n0} terrain tris + {objSummary} " +
                          $"({scene.Mesh.GridW}x{scene.Mesh.GridH} grid, stride {stride}) -> {outBmp}");
    }
}
