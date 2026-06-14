namespace RefractorForge.Formats.Terrain;

/// <summary>
/// A grayscale brush-shape mask loaded from a Battlecraft-style BMP (Round / Square / Noise / Splatter / …).
/// The weight is the pixel's grey value normalized to 0..1 — white = full effect, black = none — exactly the
/// convention Battlecraft's <c>brushes\*.bmp</c> use (verified: Round is a soft white disc on black, Square a
/// solid block, Noise a speckled field). Sampled in the brush's normalized [0,1]² footprint so the same
/// shape scales to any brush radius.
/// </summary>
public sealed class BrushMask
{
    public string Name { get; }
    public int Size { get; }
    /// <summary>Row-major, top-to-bottom, length Size*Size, each 0..1.</summary>
    public float[] Weights { get; }

    public BrushMask(string name, int size, float[] weights) { Name = name; Size = size; Weights = weights; }

    /// <summary>Bilinear sample at normalized (u, v) in [0,1] (top-left origin). Outside the unit square → 0.</summary>
    public float Sample(float u, float v)
    {
        if (u < 0f || u > 1f || v < 0f || v > 1f) return 0f;
        float fx = u * (Size - 1), fy = v * (Size - 1);
        int x0 = (int)fx, y0 = (int)fy;
        int x1 = Math.Min(x0 + 1, Size - 1), y1 = Math.Min(y0 + 1, Size - 1);
        float tx = fx - x0, ty = fy - y0;
        float a = Weights[y0 * Size + x0], b = Weights[y0 * Size + x1];
        float c = Weights[y1 * Size + x0], d = Weights[y1 * Size + x1];
        return (a + (b - a) * tx) * (1f - ty) + (c + (d - c) * tx) * ty;
    }

    /// <summary>Load an 8-bit (paletted) or 24-bit Windows BMP and convert it to a 0..1 grey weight mask.
    /// Handles bottom-up and top-down rows and 4-byte row padding.</summary>
    public static BrushMask FromBmp(string path, string? name = null)
    {
        var b = File.ReadAllBytes(path);
        if (b.Length < 54 || b[0] != (byte)'B' || b[1] != (byte)'M')
            throw new InvalidDataException($"'{path}' is not a BMP.");

        int dataOff = BitConverter.ToInt32(b, 10);
        int w = BitConverter.ToInt32(b, 18);
        int hRaw = BitConverter.ToInt32(b, 22);
        short bpp = BitConverter.ToInt16(b, 28);
        bool topDown = hRaw < 0;
        int h = Math.Abs(hRaw);
        int side = Math.Min(w, h);                 // brushes are square; clamp defensively
        var weights = new float[side * side];

        static float Lum(byte r, byte g, byte bl) => (0.299f * r + 0.587f * g + 0.114f * bl) / 255f;

        if (bpp == 8)
        {
            const int palOff = 54;                 // BITMAPFILEHEADER(14) + BITMAPINFOHEADER(40)
            int stride = ((w + 3) / 4) * 4;        // 1 byte/pixel, rows padded to 4
            for (int y = 0; y < side; y++)
            {
                int srcRow = topDown ? y : h - 1 - y;
                for (int x = 0; x < side; x++)
                {
                    int idx = b[dataOff + srcRow * stride + x];
                    weights[y * side + x] = Lum(b[palOff + idx * 4 + 2], b[palOff + idx * 4 + 1], b[palOff + idx * 4]);
                }
            }
        }
        else if (bpp == 24)
        {
            int stride = ((w * 3 + 3) / 4) * 4;
            for (int y = 0; y < side; y++)
            {
                int srcRow = topDown ? y : h - 1 - y;
                for (int x = 0; x < side; x++)
                {
                    int p = dataOff + srcRow * stride + x * 3;
                    weights[y * side + x] = Lum(b[p + 2], b[p + 1], b[p]);
                }
            }
        }
        else throw new InvalidDataException($"'{path}' is {bpp}-bit; only 8- and 24-bit BMP are supported.");

        return new BrushMask(name ?? Path.GetFileNameWithoutExtension(path), side, weights);
    }
}
