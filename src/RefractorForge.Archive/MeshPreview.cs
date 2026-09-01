using System.Numerics;
using RefractorForge.Formats.Rfa;
using RefractorForge.Formats.Terrain;
using RefractorForge.Render;

namespace RefractorForge.Archive;

/// <summary>
/// Previews for the two file types you cannot judge from a hex dump: StandardMesh models and terrain
/// <c>.raw</c> maps.
///
/// Both render on the CPU through the project's existing software rasteriser rather than by standing up an
/// OpenGL context. A file browser has to be able to draw a thumbnail for whatever the user clicked without
/// owning a GL surface, and a few thousand triangles at preview size costs milliseconds.
/// </summary>
public static class MeshPreview
{
    /// <summary>Render at 2x and box-filter down — cheap supersampling, and edges on a wireframe-ish model
    /// alias badly without it.</summary>
    private const int Supersample = 2;

    // ── StandardMesh (.sm) ───────────────────────────────────────────────────

    public sealed record MeshInfo(int Lods, int Materials, int Vertices, int Triangles, Vector3 Size);

    /// <summary>
    /// Draw LOD 0 of a StandardMesh, orbited by <paramref name="yawDeg"/> / <paramref name="pitchDeg"/> and
    /// framed automatically from its own bounds.
    ///
    /// LOD 0 is the detailed one; the lower LODs are the distance stand-ins and are not what someone opening a
    /// model wants to look at. Every material in the LOD is concatenated into one buffer because the preview is
    /// about shape, not shading, and Refractor splits a single object across materials freely.
    /// </summary>
    public static Bitmap? RenderMesh(byte[] data, int width, int height,
                                     float yawDeg, float pitchDeg, float zoom, out MeshInfo? info)
    {
        info = null;
        if (width < 8 || height < 8) return null;

        StandardMesh mesh;
        try
        {
            if (!StandardMesh.TryParse(data, out var parsed) || parsed is null) return null;
            mesh = parsed;
        }
        catch { return null; }

        if (mesh.Lods.Count == 0) return null;
        var lod = mesh.Lods[0];

        // Concatenate every material in the LOD, offsetting each one's face indices as we go.
        var pos = new List<Vector3>();
        var idx = new List<int>();
        foreach (var mat in lod)
        {
            int base_ = pos.Count;
            foreach (var v in mat.Vertices) pos.Add(new Vector3(v.X, v.Y, v.Z));
            foreach (var (a, b, c) in mat.Faces)
            {
                // A face index outside its own material's vertex list means the parse and the data disagree;
                // dropping the triangle is better than throwing away the whole preview.
                if (a < 0 || b < 0 || c < 0) continue;
                if (a >= mat.Vertices.Length || b >= mat.Vertices.Length || c >= mat.Vertices.Length) continue;
                idx.Add(base_ + a); idx.Add(base_ + b); idx.Add(base_ + c);
            }
        }
        if (pos.Count == 0 || idx.Count < 3) return null;

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in pos) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        var centre = (min + max) * 0.5f;
        float radius = MathF.Max((max - min).Length() * 0.5f, 1e-3f);

        info = new MeshInfo(mesh.Lods.Count, lod.Count, pos.Count, idx.Count / 3, max - min);

        int rw = width * Supersample, rh = height * Supersample;
        var img = new ImageBuffer(rw, rh);
        img.ClearGradient(new Vector3(0.20f, 0.22f, 0.26f), new Vector3(0.07f, 0.07f, 0.09f));

        var cam = new Camera
        {
            Aspect = rw / (float)rh,
            FovY = MathF.PI / 4f,
            Near = MathF.Max(radius * 0.01f, 0.01f),
            Far = radius * 100f,
        };

        float yaw = yawDeg * MathF.PI / 180f, pitch = Math.Clamp(pitchDeg, -85f, 85f) * MathF.PI / 180f;

        // The view axes for this orbit, worked out before the camera is placed so the model can be measured
        // against them.
        var dir = new Vector3(
            MathF.Cos(pitch) * MathF.Sin(yaw),
            MathF.Sin(pitch),
            MathF.Cos(pitch) * MathF.Cos(yaw));                       // model -> camera
        var fwd = -dir;                                                // camera -> model
        var right = Vector3.Cross(fwd, Vector3.UnitY);
        right = right.LengthSquared() > 1e-8f ? Vector3.Normalize(right) : Vector3.UnitX;
        var up = Vector3.Normalize(Vector3.Cross(right, fwd));

        // Fit the BOUNDING BOX as it actually projects, not the bounding sphere. Refractor models are mostly
        // long and flat - a wing is 18 m across and 2 m tall - and a sphere drawn round one is mostly empty
        // space, so fitting it leaves the model small and adrift in the window.
        //
        // Solved per corner rather than from half-extents, because perspective means a corner's distance from
        // the camera decides how much of the frame it takes: a wingtip swung toward the viewer needs far more
        // room than the same wingtip swung away. For a corner at offset o, staying inside the frustum needs
        //     |dot(o, right)| <= tanH * (d + dot(o, fwd))
        // and the same vertically, so d is the largest value any corner demands.
        float tanV = MathF.Tan(cam.FovY * 0.5f);
        float tanH = tanV * cam.Aspect;
        float dist = 0f;
        for (int c = 0; c < 8; c++)
        {
            var o = new Vector3(
                (c & 1) == 0 ? min.X : max.X,
                (c & 2) == 0 ? min.Y : max.Y,
                (c & 4) == 0 ? min.Z : max.Z) - centre;
            float depth = Vector3.Dot(o, fwd);
            dist = MathF.Max(dist, MathF.Abs(Vector3.Dot(o, right)) / tanH - depth);
            dist = MathF.Max(dist, MathF.Abs(Vector3.Dot(o, up)) / tanV - depth);
        }
        dist = MathF.Max(dist * 1.06f, radius * 0.05f) * Math.Clamp(zoom, 0.15f, 8f);   // 6% breathing room

        cam.Position = centre + dir * dist;
        cam.LookAt(centre);

        // Light rigged to the CAMERA rather than to the world, so a model is lit from over the viewer's
        // shoulder at every orbit angle instead of turning into a silhouette from one side.
        var key = Vector3.Normalize(-fwd * 0.85f + up * 0.45f - right * 0.35f);
        SoftwareRenderer.DrawMeshSmooth(img, cam, key,
            pos.ToArray(), idx.ToArray(), new Vector3(0.80f, 0.81f, 0.84f));

        return ToBitmap(Supersample > 1 ? img.DownsampleBy(Supersample) : img);
    }

    // ── Terrain .raw maps ────────────────────────────────────────────────────

    public sealed record RawInfo(int Side, bool SixteenBit, int Min, int Max);

    /// <summary>
    /// Render a headerless <c>.raw</c> map. Two kinds live under that extension and they are told apart by
    /// size, not by name:
    ///
    ///   * a 16-bit heightmap (Heightmap.raw) — drawn as a shaded relief;
    ///   * an 8-bit index map (UnderGrowthMap / OverGrowthMap / MaterialMap) — drawn as flat colour per index,
    ///     because those hold discrete palette indices in the range 0-14, not a height ramp. Shading them
    ///     would invent gradients that are not in the data.
    ///
    /// Both are square and headerless, so the side comes from the file length. Only one interpretation ever
    /// yields a whole number for the sizes these maps actually use, which makes the test unambiguous; the name
    /// is used only to break a tie.
    /// </summary>
    public static Bitmap? RenderRaw(byte[] data, string name, int maxSize, out RawInfo? info)
    {
        info = null;
        int side16 = 0;
        bool sixteen = data.Length % 2 == 0 && IsSquare(data.Length / 2, out side16);
        bool eight = IsSquare(data.Length, out int side8);
        if (!sixteen && !eight) return null;

        bool use16;
        if (sixteen && eight)
        {
            // Genuinely ambiguous: fall back to what the file calls itself.
            string n = name.ToLowerInvariant();
            use16 = !(n.Contains("growthmap") || n.Contains("materialmap") || n.Contains("colormap"));
        }
        else use16 = sixteen;

        int side = use16 ? side16 : side8;
        if (side < 2) return null;

        return use16 ? RenderHeight(data, side, maxSize, out info)
                     : RenderIndex(data, side, maxSize, out info);
    }

    private static Bitmap RenderHeight(byte[] data, int side, int maxSize, out RawInfo? info)
    {
        var hm = Heightmap.FromBytes(data, side, side);

        int min = int.MaxValue, max = int.MinValue;
        foreach (var s in hm.Samples) { if (s < min) min = s; if (s > max) max = s; }
        info = new RawInfo(side, true, min, max);
        float range = MathF.Max(max - min, 1);

        int outSize = Math.Min(side, maxSize);
        int step = Math.Max(1, side / outSize);
        outSize = side / step;

        var bmp = new Bitmap(outSize, outSize, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var bits = bmp.LockBits(new Rectangle(0, 0, outSize, outSize),
            System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[outSize * 4];
            // Light from the north-west, the convention every terrain tool uses, so relief reads as raised
            // rather than sunken.
            var light = Vector3.Normalize(new Vector3(-0.6f, 0.75f, -0.6f));
            for (int y = 0; y < outSize; y++)
            {
                int sy = y * step;
                for (int x = 0; x < outSize; x++)
                {
                    int sx = x * step;
                    float h = (hm[sx, sy] - min) / range;

                    // Central differences on the full-resolution grid; the vertical scale is arbitrary here
                    // because this is a legibility aid, not a measurement.
                    float hx = (Sample(hm, sx + step, sy) - Sample(hm, sx - step, sy)) / range;
                    float hy = (Sample(hm, sx, sy + step) - Sample(hm, sx, sy - step)) / range;
                    var nrm = Vector3.Normalize(new Vector3(-hx * 12f, 1f, -hy * 12f));
                    float shade = 0.45f + 0.55f * MathF.Max(Vector3.Dot(nrm, light), 0f);

                    // A cool-to-warm ramp: low ground blue-grey, high ground pale. Easier to read than pure grey.
                    var lo = new Vector3(0.16f, 0.22f, 0.30f);
                    var hi = new Vector3(0.94f, 0.92f, 0.86f);
                    var c = Vector3.Lerp(lo, hi, h) * shade;

                    int d = x * 4;
                    row[d + 0] = Byte(c.Z); row[d + 1] = Byte(c.Y); row[d + 2] = Byte(c.X); row[d + 3] = 255;
                }
                System.Runtime.InteropServices.Marshal.Copy(row, 0, bits.Scan0 + y * bits.Stride, row.Length);
            }
        }
        finally { bmp.UnlockBits(bits); }
        return bmp;
    }

    private static Bitmap RenderIndex(byte[] data, int side, int maxSize, out RawInfo? info)
    {
        int min = 255, max = 0;
        foreach (var b in data) { if (b < min) min = b; if (b > max) max = b; }
        info = new RawInfo(side, false, min, max);

        int outSize = Math.Min(side, maxSize);
        int step = Math.Max(1, side / outSize);
        outSize = side / step;

        var bmp = new Bitmap(outSize, outSize, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var bits = bmp.LockBits(new Rectangle(0, 0, outSize, outSize),
            System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[outSize * 4];
            for (int y = 0; y < outSize; y++)
            {
                int sy = y * step;
                for (int x = 0; x < outSize; x++)
                {
                    byte v = data[sy * side + x * step];
                    var c = IndexColour(v);
                    int d = x * 4;
                    row[d + 0] = c.B; row[d + 1] = c.G; row[d + 2] = c.R; row[d + 3] = 255;
                }
                System.Runtime.InteropServices.Marshal.Copy(row, 0, bits.Scan0 + y * bits.Stride, row.Length);
            }
        }
        finally { bmp.UnlockBits(bits); }
        return bmp;
    }

    /// <summary>Index 0 (nothing painted) reads as near-black; the rest get well-separated hues from the
    /// golden-ratio walk, so neighbouring indices never look like the same material.</summary>
    private static Color IndexColour(byte v)
    {
        if (v == 0) return Color.FromArgb(24, 26, 30);
        float hue = (v * 0.61803399f) % 1f * 360f;
        return FromHsv(hue, 0.55f, 0.92f);
    }

    private static Color FromHsv(float h, float s, float v)
    {
        int hi = (int)(h / 60f) % 6;
        float f = h / 60f - MathF.Floor(h / 60f);
        float p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
        (float r, float g, float b) = hi switch
        {
            0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t),
            3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q),
        };
        return Color.FromArgb(Byte(r), Byte(g), Byte(b));
    }

    private static float Sample(Heightmap hm, int x, int y) =>
        hm[Math.Clamp(x, 0, hm.Width - 1), Math.Clamp(y, 0, hm.Height - 1)];

    private static bool IsSquare(int n, out int side)
    {
        side = n <= 0 ? 0 : (int)Math.Round(Math.Sqrt(n));
        return side > 0 && (long)side * side == n;
    }

    private static byte Byte(float v) => (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);

    /// <summary>ImageBuffer is 3-byte RGB, top-down; GDI+ wants 4-byte BGRA.</summary>
    private static Bitmap ToBitmap(ImageBuffer img)
    {
        var bmp = new Bitmap(img.W, img.H, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var bits = bmp.LockBits(new Rectangle(0, 0, img.W, img.H),
            System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[img.W * 4];
            for (int y = 0; y < img.H; y++)
            {
                int src = y * img.W * 3;
                for (int x = 0; x < img.W; x++)
                {
                    int s = src + x * 3, d = x * 4;
                    row[d + 0] = img.Rgb[s + 2];
                    row[d + 1] = img.Rgb[s + 1];
                    row[d + 2] = img.Rgb[s + 0];
                    row[d + 3] = 255;
                }
                System.Runtime.InteropServices.Marshal.Copy(row, 0, bits.Scan0 + y * bits.Stride, row.Length);
            }
        }
        finally { bmp.UnlockBits(bits); }
        return bmp;
    }
}
