using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace RefractorForge.Formats.Imaging;

/// <summary>
/// A decoded PNG: interleaved samples, one or two bytes per channel as the file stored them.
/// </summary>
public sealed class PngImage
{
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary>1 grey, 2 grey+alpha, 3 RGB, 4 RGBA. A palette image comes out as 3 or 4.</summary>
    public int Channels { get; init; }
    /// <summary>8 or 16. Grey stored at 1, 2 or 4 bits is widened to 8.</summary>
    public int BitDepth { get; init; }
    /// <summary>Set when <see cref="BitDepth"/> is 8.</summary>
    public byte[]? Samples8 { get; init; }
    /// <summary>Set when <see cref="BitDepth"/> is 16.</summary>
    public ushort[]? Samples16 { get; init; }
}

/// <summary>
/// Reads PNG files - the format terrain tools export heightmaps and colour maps in.
///
/// Written here rather than borrowed from Windows' image decoder because that one quietly reduces a 16-bit greyscale
/// image to 8 bits, and a heightmap is exactly the 16-bit greyscale image that must NOT lose its precision: 256 grey
/// levels over a 300 m mountain is a terrace every metre. Everything the PNG specification allows except interlacing
/// is supported - interlaced files are refused with a message, and no terrain tool writes them by default.
///
/// Rows come out as the file stores them, top row first. Which world direction that is, is the caller's business
/// (see <c>HeightmapImport</c> and the terrain colour import, which both take the top of an image as NORTH).
/// </summary>
public static class PngReader
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    /// <summary>
    /// The largest image this reads in one piece. The decoded samples live in one array, and .NET caps an array at
    /// just under 2^31 elements - a 23,000 px square of RGBA. Bigger terrain textures come in as a folder of tiles.
    /// </summary>
    public const long MaxSamples = 0x7FFFFFC7L;

    public static bool IsPng(ReadOnlySpan<byte> data) => data.Length >= 8 && data[..8].SequenceEqual(Signature);

    public static PngImage Read(string path) => Read(File.ReadAllBytes(path));

    public static PngImage Read(byte[] png)
    {
        if (!IsPng(png)) throw new InvalidDataException("Not a PNG file.");

        int width = 0, height = 0, bitDepth = 0, colorType = -1, interlace = 0;
        byte[]? palette = null, paletteAlpha = null;
        var idat = new MemoryStream();
        int pos = 8;
        bool sawEnd = false;
        while (pos + 8 <= png.Length)
        {
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos));
            string type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            int data = pos + 8;
            if (len < 0 || data + len + 4 > png.Length) throw new InvalidDataException($"PNG chunk '{type}' runs past the end of the file.");
            var span = png.AsSpan(data, len);
            switch (type)
            {
                case "IHDR":
                    width = (int)BinaryPrimitives.ReadUInt32BigEndian(span);
                    height = (int)BinaryPrimitives.ReadUInt32BigEndian(span[4..]);
                    bitDepth = span[8]; colorType = span[9]; interlace = span[12];
                    break;
                case "PLTE": palette = span.ToArray(); break;
                case "tRNS": if (colorType == 3) paletteAlpha = span.ToArray(); break;
                case "IDAT": idat.Write(span); break;
                case "IEND": sawEnd = true; break;
            }
            pos = data + len + 4;                                  // skip the CRC
            if (sawEnd) break;
        }
        if (width <= 0 || height <= 0) throw new InvalidDataException("PNG has no image header.");
        if (interlace != 0) throw new InvalidDataException("Interlaced PNGs are not supported - export it without interlacing.");

        int srcChannels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException($"Unknown PNG colour type {colorType}.") };
        bool validDepth = colorType switch
        {
            0 => bitDepth is 1 or 2 or 4 or 8 or 16,
            3 => bitDepth is 1 or 2 or 4 or 8,
            _ => bitDepth is 8 or 16,
        };
        if (!validDepth) throw new InvalidDataException($"PNG colour type {colorType} cannot have bit depth {bitDepth}.");
        if (colorType == 3 && palette is null) throw new InvalidDataException("Palette PNG without a palette.");

        int outChannels = colorType == 3 ? (paletteAlpha is null ? 3 : 4) : srcChannels;
        int outDepth = bitDepth == 16 ? 16 : 8;
        long total = (long)width * height * outChannels;
        if (total > MaxSamples)
            throw new InvalidDataException($"{width}x{height} is too large to read in one piece - split it into tiles.");

        int bitsPerPixel = srcChannels * bitDepth;
        int rowBytes = (int)(((long)width * bitsPerPixel + 7) / 8);
        int bpp = Math.Max(1, bitsPerPixel / 8);                  // the filters' "bytes per complete pixel"

        byte[]? out8 = outDepth == 8 ? new byte[total] : null;
        ushort[]? out16 = outDepth == 16 ? new ushort[total] : null;

        // Row by row out of the inflater, keeping only the previous row for the filters: a big terrain texture
        // would otherwise need its whole inflated form in memory on top of the output.
        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        var prev = new byte[rowBytes];
        var cur = new byte[rowBytes];
        for (int y = 0; y < height; y++)
        {
            int filter = z.ReadByte();
            if (filter < 0) throw new InvalidDataException($"PNG image data ends at row {y} of {height}.");
            z.ReadExactly(cur);
            Unfilter(filter, cur, prev, bpp);
            long o = (long)y * width * outChannels;                // < MaxSamples, so it fits an int index too
            if (colorType == 3) ExpandPalette(cur, width, bitDepth, palette!, paletteAlpha, out8!, o, outChannels);
            else if (bitDepth == 16)
                for (int i = 0; i < width * srcChannels; i++) out16![o + i] = (ushort)((cur[i * 2] << 8) | cur[i * 2 + 1]);
            else if (bitDepth == 8) cur.AsSpan(0, width * srcChannels).CopyTo(out8.AsSpan((int)o, width * srcChannels));
            else ExpandLowGrey(cur, width, bitDepth, out8!, o);   // grey stored at 1, 2 or 4 bits
            (prev, cur) = (cur, prev);
        }

        return new PngImage { Width = width, Height = height, Channels = outChannels, BitDepth = outDepth, Samples8 = out8, Samples16 = out16 };
    }

    /// <summary>
    /// A PNG as one 16-bit value per pixel - a heightmap. Greyscale is read as stored (8-bit widened so 255 becomes
    /// 65535); a colour image takes its first channel, since heightmaps saved as RGB carry the same value in all three.
    /// </summary>
    public static ushort[] ReadGray16(byte[] png, out int width, out int height)
    {
        var img = Read(png);
        width = img.Width; height = img.Height;
        var result = new ushort[(long)img.Width * img.Height];
        for (long i = 0; i < result.LongLength; i++)
        {
            long s = i * img.Channels;
            result[i] = img.BitDepth == 16 ? img.Samples16![s] : (ushort)(img.Samples8![s] * 257);
        }
        return result;
    }

    /// <summary>A PNG as 8-bit RGBA - a colour map. 16-bit channels keep their high byte; grey is spread to RGB.</summary>
    public static byte[] ReadRgba8(byte[] png, out int width, out int height)
    {
        var img = Read(png);
        width = img.Width; height = img.Height;
        long n = (long)img.Width * img.Height;
        if (n * 4 > MaxSamples) throw new InvalidDataException($"{img.Width}x{img.Height} is too large to read in one piece - split it into tiles.");
        var rgba = new byte[n * 4];
        for (long i = 0; i < n; i++)
        {
            long s = i * img.Channels;
            byte C(int c) => img.BitDepth == 16 ? (byte)(img.Samples16![s + c] >> 8) : img.Samples8![s + c];
            byte r, g, b, a = 255;
            switch (img.Channels)
            {
                case 1: r = g = b = C(0); break;
                case 2: r = g = b = C(0); a = C(1); break;
                case 3: r = C(0); g = C(1); b = C(2); break;
                default: r = C(0); g = C(1); b = C(2); a = C(3); break;
            }
            long o = i * 4;
            rgba[o] = r; rgba[o + 1] = g; rgba[o + 2] = b; rgba[o + 3] = a;
        }
        return rgba;
    }

    private static void Unfilter(int filter, byte[] cur, byte[] prev, int bpp)
    {
        switch (filter)
        {
            case 0: return;
            case 1: for (int i = bpp; i < cur.Length; i++) cur[i] += cur[i - bpp]; return;
            case 2: for (int i = 0; i < cur.Length; i++) cur[i] += prev[i]; return;
            case 3:
                for (int i = 0; i < cur.Length; i++)
                    cur[i] += (byte)(((i >= bpp ? cur[i - bpp] : 0) + prev[i]) >> 1);
                return;
            case 4:
                for (int i = 0; i < cur.Length; i++)
                {
                    int a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                    int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                    cur[i] += (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
                }
                return;
            default: throw new InvalidDataException($"Unknown PNG row filter {filter}.");
        }
    }

    private static int Packed(byte[] row, int index, int bitDepth)
    {
        if (bitDepth == 8) return row[index];
        int perByte = 8 / bitDepth, b = row[index / perByte], shift = 8 - bitDepth * (index % perByte + 1);
        return (b >> shift) & ((1 << bitDepth) - 1);
    }

    private static void ExpandPalette(byte[] row, int width, int bitDepth, byte[] palette, byte[]? alpha, byte[] dst, long o, int channels)
    {
        for (int x = 0; x < width; x++)
        {
            int idx = Packed(row, x, bitDepth);
            long d = o + (long)x * channels;
            int p = idx * 3;
            dst[d] = p + 2 < palette.Length ? palette[p] : (byte)0;
            dst[d + 1] = p + 2 < palette.Length ? palette[p + 1] : (byte)0;
            dst[d + 2] = p + 2 < palette.Length ? palette[p + 2] : (byte)0;
            if (channels == 4) dst[d + 3] = alpha is not null && idx < alpha.Length ? alpha[idx] : (byte)255;
        }
    }

    private static void ExpandLowGrey(byte[] row, int width, int bitDepth, byte[] dst, long o)
    {
        int max = (1 << bitDepth) - 1;
        for (int x = 0; x < width; x++) dst[o + x] = (byte)(Packed(row, x, bitDepth) * 255 / max);
    }
}
