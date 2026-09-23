using System.Buffers.Binary;
using System.Numerics;

namespace RefractorForge.Render;

/// <summary>A simple RGBA texture with bilinear sampling, decoded from the game's DDS files.</summary>
public sealed class Texture2D
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Rgba { get; }   // length = Width*Height*4

    public Texture2D(int w, int h, byte[] rgba) { Width = w; Height = h; Rgba = rgba; }

    /// <summary>Bilinear sample with wrapping; returns linear-ish RGB in 0..1 (texture is treated as sRGB-ish).</summary>
    public Vector3 Sample(float u, float v)
    {
        // wrap into [0,1)
        u -= MathF.Floor(u); v -= MathF.Floor(v);
        float fx = u * Width - 0.5f, fy = v * Height - 0.5f;
        int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
        float tx = fx - x0, ty = fy - y0;
        int x1 = x0 + 1, y1 = y0 + 1;
        x0 = Wrap(x0, Width); x1 = Wrap(x1, Width); y0 = Wrap(y0, Height); y1 = Wrap(y1, Height);
        var c00 = Texel(x0, y0); var c10 = Texel(x1, y0);
        var c01 = Texel(x0, y1); var c11 = Texel(x1, y1);
        return Vector3.Lerp(Vector3.Lerp(c00, c10, tx), Vector3.Lerp(c01, c11, tx), ty);
    }

    private Vector3 Texel(int x, int y)
    {
        int i = (y * Width + x) * 4;
        const float inv = 1f / 255f;
        return new Vector3(Rgba[i] * inv, Rgba[i + 1] * inv, Rgba[i + 2] * inv);
    }

    /// <summary>Nearest-neighbour RGBA sample (wrapping) — used where the alpha channel is needed
    /// (alpha-tested foliage), avoiding bilinear bleed across cutout edges.</summary>
    public Vector4 SampleRGBA(float u, float v)
    {
        u -= MathF.Floor(u); v -= MathF.Floor(v);
        int x = Wrap((int)MathF.Round(u * Width - 0.5f), Width);
        int y = Wrap((int)MathF.Round(v * Height - 0.5f), Height);
        int i = (y * Width + x) * 4;
        const float inv = 1f / 255f;
        return new Vector4(Rgba[i] * inv, Rgba[i + 1] * inv, Rgba[i + 2] * inv, Rgba[i + 3] * inv);
    }

    private static int Wrap(int v, int n) { v %= n; return v < 0 ? v + n : v; }

    /// <summary>Load an 8-bit paletted, 24-bit, or 32-bit Windows BMP into an RGBA texture (BMP stores BGR(A);
    /// handles bottom-up and top-down rows and 4-byte row padding). Returns null on an unsupported/short file.</summary>
    public static Texture2D? LoadBmp(string path)
    {
        var b = System.IO.File.ReadAllBytes(path);
        if (b.Length < 54 || b[0] != (byte)'B' || b[1] != (byte)'M') return null;
        int dataOff = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(10));
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(14));
        int w = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(18));
        int hRaw = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(22));
        short bpp = BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(28));
        bool topDown = hRaw < 0; int h = Math.Abs(hRaw);
        if (w <= 0 || h <= 0 || (bpp != 8 && bpp != 24 && bpp != 32)) return null;
        var rgba = new byte[w * h * 4];

        if (bpp == 8)
        {
            int palOff = 14 + headerSize;                 // palette follows the info header
            int stride = ((w + 3) / 4) * 4;               // 1 byte/pixel, rows padded to 4
            for (int y = 0; y < h; y++)
            {
                int srcRow = topDown ? y : h - 1 - y;
                for (int x = 0; x < w; x++)
                {
                    int p = dataOff + srcRow * stride + x;
                    if (p >= b.Length) continue;
                    int e = palOff + b[p] * 4;            // palette entry = B,G,R,(reserved)
                    if (e + 2 >= b.Length) continue;
                    int o = (y * w + x) * 4;
                    rgba[o] = b[e + 2]; rgba[o + 1] = b[e + 1]; rgba[o + 2] = b[e]; rgba[o + 3] = 255;
                }
            }
            return new Texture2D(w, h, rgba);
        }

        int bypp = bpp / 8, str2 = ((w * bypp + 3) / 4) * 4;
        for (int y = 0; y < h; y++)
        {
            int srcRow = topDown ? y : h - 1 - y;
            for (int x = 0; x < w; x++)
            {
                int p = dataOff + srcRow * str2 + x * bypp;
                if (p + 2 >= b.Length) continue;
                int o = (y * w + x) * 4;
                rgba[o] = b[p + 2]; rgba[o + 1] = b[p + 1]; rgba[o + 2] = b[p];   // BGR -> RGB
                rgba[o + 3] = bypp == 4 ? b[p + 3] : (byte)255;
            }
        }
        return new Texture2D(w, h, rgba);
    }
}

/// <summary>Minimal Targa (.tga) reader: uncompressed (type 2 truecolour / 3 grayscale) and RLE (type 10 / 11),
/// 24- or 32-bit BGR(A) and 8-bit grayscale, honouring the image-descriptor origin bit. Decodes to top-row-first
/// RGBA (matching <see cref="Texture2D"/>). Returns null on an unsupported/short file. Lets the editor import
/// .tga surface textures, which GDI+ can't read.</summary>
public static class TgaTexture
{
    public static Texture2D? Decode(byte[] b)
    {
        if (b is null || b.Length < 18) return null;
        int idLen = b[0], cmapType = b[1], imgType = b[2];
        int w = b[12] | (b[13] << 8), h = b[14] | (b[15] << 8);
        int bpp = b[16], desc = b[17];
        if (w <= 0 || h <= 0) return null;
        bool topLeft = (desc & 0x20) != 0;                          // bit 5: origin top-left (else bottom-left)
        bool rle = imgType is 10 or 11;
        // 1 = truecolour, 2 = grayscale, 3 = colour-mapped (palette). Object lightmaps (ObjectLightMaps/*.tga) are
        // colour-mapped 8-bit with an embedded 24-bit BGR ramp, so colour-mapped support is what lets them load.
        int kind = imgType is 2 or 10 ? 1 : imgType is 3 or 11 ? 2 : imgType is 1 or 9 ? 3 : 0;
        if (kind == 0) return null;                                  // unsupported image type
        int bypp = bpp / 8;                                         // bytes per image element (truecolour pixel / palette index)
        if (kind == 1) { if (bypp != 3 && bypp != 4) return null; }
        else if (kind == 3) { if (bypp != 1 && bypp != 2) return null; }   // 8- or 16-bit palette indices
        else { if (bypp != 1) return null; }
        int pos = 18 + idLen;
        // The colour map (palette). For colour-mapped images we KEEP it to resolve indices; for truecolour we skip it.
        byte[]? cmap = null; int cmEntBytes = 0;
        if (cmapType == 1)
        {
            int cmLen = b[5] | (b[6] << 8); int cmBits = b[7];
            cmEntBytes = (cmBits + 7) / 8;
            int cmBytes = cmLen * cmEntBytes;
            if (kind == 3)
            {
                if (cmEntBytes < 1 || pos + cmBytes > b.Length) return null;
                cmap = new byte[cmBytes];
                Array.Copy(b, pos, cmap, 0, cmBytes);
            }
            pos += cmBytes;
        }
        if (kind == 3 && cmap is null) return null;                  // colour-mapped but no palette present
        int npix = w * h;
        var pix = new byte[npix * bypp];
        if (!rle)
        {
            if (pos + pix.Length > b.Length) return null;
            Array.Copy(b, pos, pix, 0, pix.Length);
        }
        else
        {
            int outPix = 0, i = pos;
            while (outPix < npix && i < b.Length)
            {
                int hdr = b[i++], count = (hdr & 0x7F) + 1;
                if ((hdr & 0x80) != 0)                               // RLE packet: one pixel repeated `count` times
                {
                    if (i + bypp > b.Length) break;
                    for (int c = 0; c < count && outPix < npix; c++, outPix++) Array.Copy(b, i, pix, outPix * bypp, bypp);
                    i += bypp;
                }
                else                                                 // raw packet: `count` literal pixels
                {
                    for (int c = 0; c < count && outPix < npix; c++, outPix++)
                    { if (i + bypp > b.Length) break; Array.Copy(b, i, pix, outPix * bypp, bypp); i += bypp; }
                }
            }
        }
        var rgba = new byte[npix * 4];
        for (int y = 0; y < h; y++)
        {
            int srcRow = topLeft ? y : h - 1 - y;
            for (int x = 0; x < w; x++)
            {
                int s = (srcRow * w + x) * bypp, o = (y * w + x) * 4;
                if (kind == 2) { byte g = pix[s]; rgba[o] = g; rgba[o + 1] = g; rgba[o + 2] = g; rgba[o + 3] = 255; }
                else if (kind == 3)                                  // colour-mapped: index -> palette entry (BGR or BGRA)
                {
                    int idx = bypp == 2 ? (pix[s] | (pix[s + 1] << 8)) : pix[s];
                    int c = idx * cmEntBytes;
                    if (c + 2 < cmap!.Length)
                    { rgba[o] = cmap[c + 2]; rgba[o + 1] = cmap[c + 1]; rgba[o + 2] = cmap[c]; rgba[o + 3] = cmEntBytes >= 4 ? cmap[c + 3] : (byte)255; }
                    else { rgba[o] = rgba[o + 1] = rgba[o + 2] = 0; rgba[o + 3] = 255; }
                }
                else { rgba[o] = pix[s + 2]; rgba[o + 1] = pix[s + 1]; rgba[o + 2] = pix[s]; rgba[o + 3] = bypp == 4 ? pix[s + 3] : (byte)255; }
            }
        }
        return new Texture2D(w, h, rgba);
    }

    /// <summary>Encode an intensity texture (uses the .r channel) as the 8-bit colour-mapped TGA the engine reads for
    /// object lightmaps — exactly the format the originals use: 18-byte header, a 256-entry 24-bit BGR grayscale ramp
    /// colour map, then one index per pixel (= the gray value), bottom-left origin (rows written bottom-up).</summary>
    /// <summary>The RIFF PAL every lightmapped retail level ships beside its maps (<c>Objectlightmaps/Palette.pal</c>,
    /// 1,048 bytes, a grey ramp): the engine loads it before the maps (exe: <c>loadLightmapPalette</c>), so a level
    /// that never had lightmaps gets one written with its first bake.</summary>
    public static byte[] GreyPalettePal()
    {
        var b = new byte[1048];
        void U32(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
        b[0] = (byte)'R'; b[1] = (byte)'I'; b[2] = (byte)'F'; b[3] = (byte)'F'; U32(4, 1040);
        b[8] = (byte)'P'; b[9] = (byte)'A'; b[10] = (byte)'L'; b[11] = (byte)' ';
        b[12] = (byte)'d'; b[13] = (byte)'a'; b[14] = (byte)'t'; b[15] = (byte)'a'; U32(16, 1028);
        b[20] = 0; b[21] = 3;                         // palVersion 0x0300
        b[22] = 0; b[23] = 1;                         // 256 entries
        for (int i = 0; i < 256; i++) { b[24 + i * 4] = (byte)i; b[25 + i * 4] = (byte)i; b[26 + i * 4] = (byte)i; b[27 + i * 4] = 0; }
        return b;
    }

    /// <summary>
    /// A 24-bit truecolour TGA - the object-lightmap format that carries COLOUR.
    ///
    /// Measured off GC_Bespin_Night (the mod's Blender-baked night lightmaps): image type 2, 24 bpp, no colour map,
    /// descriptor 0 (bottom-left origin), BGR texels - and Battlefield 1942 loads them with the lamp colour intact.
    /// Its day maps are the usual 8-bit grey palette, so the two formats sit side by side in one game. BfVietnam's
    /// shader reads only the blue channel of a lightmap, so a coloured map there is brightness-only; write
    /// <see cref="EncodeGrayColormapped"/> for that game.
    /// </summary>
    public static byte[] EncodeRgb24(Texture2D t)
    {
        int w = t.Width, h = t.Height;
        var buf = new byte[18 + w * h * 3];
        buf[2] = 2;                                   // image type 2 = truecolour, uncompressed
        buf[12] = (byte)(w & 0xFF); buf[13] = (byte)((w >> 8) & 0xFF);
        buf[14] = (byte)(h & 0xFF); buf[15] = (byte)((h >> 8) & 0xFF);
        buf[16] = 24;
        buf[17] = 0;                                  // bottom-left origin, like every shipped lightmap
        var px = t.Rgba;
        int p = 18;
        for (int y = 0; y < h; y++)
        {
            int srcRow = (h - 1 - y) * w;
            for (int x = 0; x < w; x++)
            {
                int o = (srcRow + x) * 4;
                buf[p++] = px[o + 2]; buf[p++] = px[o + 1]; buf[p++] = px[o];   // BGR
            }
        }
        return buf;
    }

    public static byte[] EncodeGrayColormapped(Texture2D t)
    {
        int w = t.Width, h = t.Height;
        var buf = new byte[18 + 256 * 3 + w * h];
        buf[1] = 1;                                   // colour-map type = present
        buf[2] = 1;                                   // image type 1 = colour-mapped, uncompressed
        buf[5] = 0; buf[6] = 1;                       // colour-map length = 256
        buf[7] = 24;                                  // 24-bit colour-map entries (BGR)
        buf[12] = (byte)(w & 0xFF); buf[13] = (byte)((w >> 8) & 0xFF);
        buf[14] = (byte)(h & 0xFF); buf[15] = (byte)((h >> 8) & 0xFF);
        buf[16] = 8;                                  // 8 bits per index
        buf[17] = 0;                                  // descriptor: bottom-left origin (matches the shipped lightmaps)
        int p = 18;
        for (int i = 0; i < 256; i++) { buf[p++] = (byte)i; buf[p++] = (byte)i; buf[p++] = (byte)i; }   // gray ramp (B=G=R=i)
        var px = t.Rgba;
        for (int y = 0; y < h; y++)                   // bottom-up: file row 0 = image bottom
        {
            int srcRow = (h - 1 - y) * w;
            for (int x = 0; x < w; x++) buf[p++] = px[(srcRow + x) * 4];   // index = .r (gray value)
        }
        return buf;
    }
}

/// <summary>What kind of pixels a DDS holds, as far as decoding it goes.</summary>
public enum DdsPixelFormat
{
    /// <summary>Anything this reader cannot decode: another FourCC (ATI1/2, a numeric D3DFMT), a DX10 format outside the
    /// classic set (BC4-BC7, float), bump or YUV layouts.</summary>
    Unknown,
    Dxt1, Dxt2, Dxt3, Dxt4, Dxt5,
    /// <summary>Uncompressed colour laid out by the four masks: A8R8G8B8, X8R8G8B8, R8G8B8, R5G6B5, A4R4G4B4...</summary>
    Rgb,
    /// <summary>Grey in the red mask, with or without an alpha mask: L8, A8L8, A4L4, L16.</summary>
    Luminance,
    /// <summary>Alpha only (A8): decodes as black with that alpha, the way Direct3D samples it.</summary>
    Alpha,
}

/// <summary>
/// A DDS header, read without touching the pixels - enough for a browser to say what a texture is, and for a writer
/// to put a replacement back in the shape the original shipped in (the games read what they shipped; a replacement in
/// a format the original never used is the classic black texture in game).
/// </summary>
/// <param name="MipCount">Levels in the file, at least 1 (a header that says 0 means one).</param>
/// <param name="BitCount">Bits per pixel of an uncompressed layout; 0 for block compression.</param>
/// <param name="IsDx10">The file carries the 20-byte DX10 extension header; <see cref="DxgiFormat"/> says what follows.</param>
public sealed record DdsInfo(int Width, int Height, int MipCount, DdsPixelFormat Format, int BitCount,
                             uint RMask, uint GMask, uint BMask, uint AMask, bool IsCube, bool IsDx10)
{
    /// <summary>The FourCC as written ("DXT1", "DX10", "ATI2"), a numeric one as "#36"; empty for a masked layout.</summary>
    public string FourCC { get; init; } = "";

    /// <summary>The DXGI_FORMAT of a DX10 file, else 0.</summary>
    public int DxgiFormat { get; init; }

    /// <summary>Slices of a volume texture, else 1.</summary>
    public int Depth { get; init; } = 1;

    /// <summary>Where the top level's pixels start: 128, or 148 after a DX10 header.</summary>
    public int DataOffset { get; init; } = 128;

    public bool IsCompressed => Format is DdsPixelFormat.Dxt1 or DdsPixelFormat.Dxt2 or DdsPixelFormat.Dxt3
                                          or DdsPixelFormat.Dxt4 or DdsPixelFormat.Dxt5;

    /// <summary>Whether <see cref="DdsTexture.Decode(byte[], int)"/> reads it (the first face of a cube map, the first
    /// slice of a volume).</summary>
    public bool CanDecode => IsCompressed || (Format != DdsPixelFormat.Unknown && BitCount is 8 or 16 or 24 or 32);

    /// <summary>Bytes of one block (compressed) or one pixel (uncompressed).</summary>
    internal int UnitBytes => Format == DdsPixelFormat.Dxt1 ? 8 : IsCompressed ? 16 : BitCount / 8;

    /// <summary>Bytes of one level of <paramref name="w"/> x <paramref name="h"/> (one face, one slice).</summary>
    internal long LevelBytes(int w, int h)
        => IsCompressed ? (long)Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * UnitBytes : (long)w * h * UnitBytes;

    /// <summary>The name a texture tool would give it: DXT1, A8R8G8B8, X8R8G8B8, R5G6B5, A4R4G4B4, L8, A8L8, A8 - or,
    /// for what this reader leaves alone, the FourCC or the DX10 format ("DX10 BC7_UNORM").</summary>
    public string Name
    {
        get
        {
            if (IsDx10 && !CanDecode) return "DX10 " + DdsTexture.DxgiName(DxgiFormat);
            if (IsCompressed) return Format.ToString().ToUpperInvariant();
            if (Format == DdsPixelFormat.Unknown) return FourCC.Length > 0 ? FourCC : $"{BitCount}-bit";
            var channels = new List<(uint Mask, char Letter)>();
            if (Format == DdsPixelFormat.Luminance) channels.Add((RMask, 'L'));
            else if (Format == DdsPixelFormat.Rgb) { channels.Add((RMask, 'R')); channels.Add((GMask, 'G')); channels.Add((BMask, 'B')); }
            channels.Add((AMask, 'A'));
            channels.RemoveAll(c => c.Mask == 0);
            channels.Sort((a, b) => b.Mask.CompareTo(a.Mask));   // highest bits first, as the names are written
            var sb = new System.Text.StringBuilder();
            int position = BitCount;
            foreach (var (mask, letter) in channels)
            {
                int top = 32 - BitOperations.LeadingZeroCount(mask);
                if (top < position) sb.Append('X').Append(position - top);
                sb.Append(letter).Append(BitOperations.PopCount(mask));
                position = BitOperations.TrailingZeroCount(mask);
            }
            if (position > 0 && sb.Length > 0) sb.Append('X').Append(position);
            return sb.ToString();
        }
    }
}

/// <summary>
/// DDS reader for what the games and their mods ship: DXT1/3/5 (and the premultiplied DXT2/4) and every uncompressed
/// layout a legacy header can describe with its bit masks - A8R8G8B8, X8R8G8B8, R8G8B8, the 16-bit R5G6B5 /
/// A4R4G4B4 / A1R5G5B5, luminance and alpha - plus the same formats behind a DX10 header. Decodes one level to RGBA
/// with no external image dependency, so the editor reads the game's <c>.dds</c> files directly.
/// </summary>
public static class DdsTexture
{
    public static Texture2D Load(string path) => Decode(File.ReadAllBytes(path));

    /// <summary>What a DDS file is, without decoding it: its width and whether it is block-compressed (DXT/BC).
    /// (0, false) for anything that is not a DDS. <see cref="Describe(byte[])"/> says everything else.</summary>
    public static (int Width, bool Dxt) HeaderInfo(byte[] d)
    {
        if (d.Length < 128 || d[0] != (byte)'D' || d[1] != (byte)'D' || d[2] != (byte)'S' || d[3] != (byte)' ') return (0, false);
        int width = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(16));
        uint pfFlags = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(80));
        bool dxt = (pfFlags & 0x4) != 0 && d[84] == (byte)'D' && d[85] == (byte)'X' && d[86] == (byte)'T';
        return (width, dxt);
    }

    // DDS_PIXELFORMAT flags.
    private const uint PfAlphaPixels = 0x1, PfAlpha = 0x2, PfFourCc = 0x4, PfRgb = 0x40, PfLuminance = 0x20000;

    /// <summary>
    /// Read a DDS header: size, levels, pixel layout and masks, cube and DX10 flags. Only the first 128 bytes (148
    /// with a DX10 header) are looked at, so a caller can pass the head of an archive entry. Null for anything that
    /// is not a DDS.
    /// </summary>
    public static DdsInfo? Describe(ReadOnlySpan<byte> d)
    {
        if (d.Length < 128 || d[0] != (byte)'D' || d[1] != (byte)'D' || d[2] != (byte)'S' || d[3] != (byte)' ') return null;
        uint flags = U32(d, 8);
        int height = (int)U32(d, 12), width = (int)U32(d, 16);
        int depth = (flags & 0x800000) != 0 ? Math.Max(1, (int)U32(d, 24)) : 1;
        int mips = Math.Max(1, (int)U32(d, 28));
        uint pf = U32(d, 80), fourcc = U32(d, 84);
        int bits = (int)U32(d, 88);
        uint r = U32(d, 92), g = U32(d, 96), b = U32(d, 100), a = U32(d, 104);
        uint caps2 = U32(d, 112);
        bool cube = (caps2 & 0x200) != 0;
        if ((caps2 & 0x200000) == 0) depth = 1;                          // a volume only when DDSCAPS2_VOLUME says so

        if ((pf & PfFourCc) != 0)
        {
            string cc = FourCcText(fourcc);
            if (cc == "DX10")
            {
                if (d.Length < 148)
                    return new DdsInfo(width, height, mips, DdsPixelFormat.Unknown, 0, 0, 0, 0, 0, cube, true) { FourCC = cc, Depth = depth, DataOffset = 148 };
                int dxgi = (int)U32(d, 128);
                cube |= (U32(d, 136) & 0x4) != 0;                          // D3D11_RESOURCE_MISC_TEXTURECUBE
                var (fmt, fBits, fr, fg, fb, fa) = FromDxgi(dxgi);
                return new DdsInfo(width, height, mips, fmt, fBits, fr, fg, fb, fa, cube, true)
                    { FourCC = cc, DxgiFormat = dxgi, Depth = depth, DataOffset = 148 };
            }
            var f = cc switch
            {
                "DXT1" => DdsPixelFormat.Dxt1, "DXT2" => DdsPixelFormat.Dxt2, "DXT3" => DdsPixelFormat.Dxt3,
                "DXT4" => DdsPixelFormat.Dxt4, "DXT5" => DdsPixelFormat.Dxt5, _ => DdsPixelFormat.Unknown,
            };
            // A FourCC file's masks and bit count are whatever the tool left there - retail DXT headers carry garbage.
            return new DdsInfo(width, height, mips, f, 0, 0, 0, 0, 0, cube, false) { FourCC = cc, Depth = depth };
        }

        // A legacy masked layout. The alpha mask is honoured whenever it is set - DirectXTex matches a layout by its
        // masks, not by DDPF_ALPHAPIXELS - and a layout without one is opaque.
        var format = (pf & PfRgb) != 0 ? DdsPixelFormat.Rgb
                   : (pf & PfLuminance) != 0 ? DdsPixelFormat.Luminance
                   : (pf & PfAlpha) != 0 ? DdsPixelFormat.Alpha
                   : DdsPixelFormat.Unknown;
        if (format == DdsPixelFormat.Alpha) { r = g = b = 0; if (a == 0 && bits == 8) a = 0xFF; }
        if (format == DdsPixelFormat.Luminance) g = b = 0;
        return new DdsInfo(width, height, mips, format, bits, r, g, b, a, cube, false) { Depth = depth };
    }

    /// <inheritdoc cref="Describe(ReadOnlySpan{byte})"/>
    public static DdsInfo? Describe(byte[] d) => Describe(d.AsSpan());

    private static uint U32(ReadOnlySpan<byte> d, int off) => BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(off));

    private static string FourCcText(uint v)
    {
        Span<char> c = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            byte ch = (byte)(v >> (8 * i));
            if (ch < 32 || ch > 126) return "#" + v.ToString(System.Globalization.CultureInfo.InvariantCulture);   // a D3DFMT number
            c[i] = (char)ch;
        }
        return new string(c);
    }

    /// <summary>The classic formats behind a DX10 header, as the legacy layout whose bytes they are.</summary>
    private static (DdsPixelFormat Format, int Bits, uint R, uint G, uint B, uint A) FromDxgi(int dxgi) => dxgi switch
    {
        70 or 71 or 72 => (DdsPixelFormat.Dxt1, 0, 0u, 0u, 0u, 0u),                           // BC1 typeless / unorm / srgb
        73 or 74 or 75 => (DdsPixelFormat.Dxt3, 0, 0u, 0u, 0u, 0u),                           // BC2
        76 or 77 or 78 => (DdsPixelFormat.Dxt5, 0, 0u, 0u, 0u, 0u),                           // BC3
        27 or 28 or 29 => (DdsPixelFormat.Rgb, 32, 0xFFu, 0xFF00u, 0xFF0000u, 0xFF000000u),   // R8G8B8A8
        87 or 90 or 91 => (DdsPixelFormat.Rgb, 32, 0xFF0000u, 0xFF00u, 0xFFu, 0xFF000000u),   // B8G8R8A8
        88 or 92 or 93 => (DdsPixelFormat.Rgb, 32, 0xFF0000u, 0xFF00u, 0xFFu, 0u),            // B8G8R8X8
        85 => (DdsPixelFormat.Rgb, 16, 0xF800u, 0x07E0u, 0x001Fu, 0u),                        // B5G6R5
        86 => (DdsPixelFormat.Rgb, 16, 0x7C00u, 0x03E0u, 0x001Fu, 0x8000u),                   // B5G5R5A1
        115 => (DdsPixelFormat.Rgb, 16, 0x0F00u, 0x00F0u, 0x000Fu, 0xF000u),                  // B4G4R4A4
        65 => (DdsPixelFormat.Alpha, 8, 0u, 0u, 0u, 0xFFu),                                   // A8
        _ => (DdsPixelFormat.Unknown, 0, 0u, 0u, 0u, 0u),
    };

    /// <summary>A DXGI_FORMAT by name, for messages about the ones this reader leaves alone.</summary>
    internal static string DxgiName(int dxgi) => dxgi switch
    {
        79 or 80 or 81 => "BC4", 82 or 83 or 84 => "BC5", 94 or 95 or 96 => "BC6H", 97 => "BC7_TYPELESS",
        98 => "BC7_UNORM", 99 => "BC7_UNORM_SRGB", 2 => "R32G32B32A32_FLOAT", 10 => "R16G16B16A16_FLOAT",
        24 => "R10G10B10A2_UNORM", 61 => "R8_UNORM", 49 => "R8G8_UNORM",
        _ => $"DXGI format {dxgi}",
    };

    public static Texture2D Decode(byte[] d) => Decode(d, int.MaxValue);

    /// <summary>
    /// Decode the largest mip level no bigger than <paramref name="maxSide"/> on either side, or the smallest level
    /// the file has when none is that small.
    ///
    /// For terrain tiles, whose only job in the editor is to be baked into a preview atlas of at most 8192 px. An 8 km
    /// map is 32 x 32 tiles, so the atlas holds 256 px of each - and decoding every 1024 px tile whole held 4 GB of
    /// pixels to produce it (retail Tobruk's 48 tiles of 4096 px: 3 GB). The mips are already in the file, box-filtered
    /// by whoever made it, so reading the right one is both 16x smaller and a better downsample than point-sampling
    /// the top level. A file whose mip data is cut short falls back to the last level it fully holds; one whose chosen
    /// level is itself cut short is an <see cref="InvalidDataException"/>, never a read past the end.
    /// </summary>
    public static Texture2D Decode(byte[] d, int maxSide)
    {
        var info = Describe(d) ?? throw new InvalidDataException("Not a DDS file.");
        if (!info.CanDecode) throw new InvalidDataException($"Unsupported DDS pixel format '{info.Name}'.");
        int width = info.Width, height = info.Height, dataOff = info.DataOffset;
        if (width <= 0 || height <= 0) throw new InvalidDataException($"DDS size {width} x {height}.");

        if (maxSide < Math.Max(width, height) && info.Depth == 1)
        {
            int off = dataOff, w = width, h = height;
            for (int level = 0; level < info.MipCount - 1 && Math.Max(w, h) > maxSide; level++)
            {
                long size = info.LevelBytes(w, h);
                int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
                if (off + size + info.LevelBytes(nw, nh) > d.Length) break;   // the next level is not all there
                off += (int)size; w = nw; h = nh;
            }
            dataOff = off; width = w; height = h;
        }
        long need = info.LevelBytes(width, height);
        if (dataOff + need > d.Length)
            throw new InvalidDataException($"DDS pixel data is cut short: {width} x {height} {info.Name} needs {need:N0} bytes, the file holds {Math.Max(0, d.Length - dataOff):N0}.");

        var rgba = new byte[width * height * 4];
        if (info.IsCompressed)
        {
            bool dxt1 = info.Format == DdsPixelFormat.Dxt1;
            bool explicitAlpha = info.Format is DdsPixelFormat.Dxt2 or DdsPixelFormat.Dxt3;
            int blockBytes = info.UnitBytes;
            int bx = (width + 3) / 4, by = (height + 3) / 4;
            int p = dataOff;
            Span<byte> alpha = stackalloc byte[16];   // one 16-byte scratch buffer, reused per block
            for (int byk = 0; byk < by; byk++)
                for (int bxk = 0; bxk < bx; bxk++)
                {
                    int colorOff = p;
                    if (dxt1) alpha.Fill(255);
                    else if (explicitAlpha) { DecodeDxt3Alpha(d, p, alpha); colorOff = p + 8; }
                    else { DecodeDxt5Alpha(d, p, alpha); colorOff = p + 8; }
                    DecodeColorBlock(d, colorOff, dxt1, bxk * 4, byk * 4, width, height, rgba, alpha);
                    p += blockBytes;
                }
        }
        else DecodeMasked(d, dataOff, width * height, info, rgba);
        return new Texture2D(width, height, rgba);
    }

    /// <summary>Uncompressed pixels: each channel cut out by its mask and widened to 8 bits. The layout nearly every
    /// uncompressed file uses - A8R8G8B8 or X8R8G8B8, bytes B G R A - is a straight copy.</summary>
    private static void DecodeMasked(byte[] d, int off, int count, DdsInfo info, byte[] rgba)
    {
        int bpp = info.BitCount / 8;
        if (bpp == 4 && info.Format == DdsPixelFormat.Rgb && info.RMask == 0x00FF0000 && info.GMask == 0x0000FF00
            && info.BMask == 0x000000FF && info.AMask is 0xFF000000 or 0)
        {
            bool alpha = info.AMask != 0;
            for (int i = 0, q = off, o = 0; i < count; i++, q += 4, o += 4)
            {
                rgba[o] = d[q + 2]; rgba[o + 1] = d[q + 1]; rgba[o + 2] = d[q];
                rgba[o + 3] = alpha ? d[q + 3] : (byte)255;
            }
            return;
        }

        bool grey = info.Format == DdsPixelFormat.Luminance;
        var r = new Channel(info.Format == DdsPixelFormat.Alpha ? 0 : info.RMask, 0);
        var g = new Channel(info.Format == DdsPixelFormat.Rgb ? info.GMask : 0, 0);
        var b = new Channel(info.Format == DdsPixelFormat.Rgb ? info.BMask : 0, 0);
        var a = new Channel(info.AMask, 255);
        for (int i = 0, q = off, o = 0; i < count; i++, q += bpp, o += 4)
        {
            uint px = bpp switch
            {
                1 => d[q],
                2 => (uint)(d[q] | d[q + 1] << 8),
                3 => (uint)(d[q] | d[q + 1] << 8 | d[q + 2] << 16),
                _ => (uint)(d[q] | d[q + 1] << 8 | d[q + 2] << 16 | d[q + 3] << 24),
            };
            byte rv = r.Of(px);
            rgba[o] = rv;
            rgba[o + 1] = grey ? rv : g.Of(px);
            rgba[o + 2] = grey ? rv : b.Of(px);
            rgba[o + 3] = a.Of(px);
        }
    }

    /// <summary>One channel of a masked layout: where it sits, and how it widens to 8 bits - by table up to 8 bits (4-bit
    /// 15 is 255, 5-bit 16 is 132), wider channels by scaling. A channel the layout lacks reads as <c>missing</c>: 0
    /// for a colour, 255 for alpha.</summary>
    private readonly struct Channel
    {
        private readonly uint _mask;
        private readonly int _shift;
        private readonly uint _max;
        private readonly byte[]? _widen;
        private readonly byte _missing;

        public Channel(uint mask, byte missing)
        {
            _mask = mask; _missing = missing;
            _shift = mask == 0 ? 0 : BitOperations.TrailingZeroCount(mask);
            _max = mask >> _shift;                                      // 2^bits - 1 for the contiguous masks files use
            _widen = null;
            if (_max is > 0 and <= 255)
            {
                int max = (int)_max;
                _widen = new byte[max + 1];
                for (int v = 0; v <= max; v++) _widen[v] = (byte)((v * 255 + max / 2) / max);
            }
        }

        public byte Of(uint px)
        {
            if (_mask == 0) return _missing;
            uint v = (px & _mask) >> _shift;
            return _widen is not null ? _widen[v] : (byte)((ulong)v * 255 / _max);
        }
    }

    private static void DecodeColorBlock(byte[] d, int o, bool dxt1, int ox, int oy, int w, int h, byte[] rgba, ReadOnlySpan<byte> alpha)
    {
        ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o));
        ushort c1 = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o + 2));
        Span<int> r = stackalloc int[4]; Span<int> g = stackalloc int[4]; Span<int> b = stackalloc int[4];
        Unpack565(c0, out r[0], out g[0], out b[0]);
        Unpack565(c1, out r[1], out g[1], out b[1]);
        bool fourColor = !dxt1 || c0 > c1;
        if (fourColor)
        {
            r[2] = (2 * r[0] + r[1]) / 3; g[2] = (2 * g[0] + g[1]) / 3; b[2] = (2 * b[0] + b[1]) / 3;
            r[3] = (r[0] + 2 * r[1]) / 3; g[3] = (g[0] + 2 * g[1]) / 3; b[3] = (b[0] + 2 * b[1]) / 3;
        }
        else
        {
            r[2] = (r[0] + r[1]) / 2; g[2] = (g[0] + g[1]) / 2; b[2] = (b[0] + b[1]) / 2;
            r[3] = 0; g[3] = 0; b[3] = 0; // index 3 = transparent black in 3-color mode (only DXT1 has that mode)
        }
        uint bits = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o + 4));
        for (int py = 0; py < 4; py++)
            for (int px = 0; px < 4; px++)
            {
                int sel = (int)((bits >> (2 * (py * 4 + px))) & 3);
                int x = ox + px, y = oy + py;
                if (x >= w || y >= h) continue;
                int q = (y * w + x) * 4;
                rgba[q] = (byte)r[sel]; rgba[q + 1] = (byte)g[sel]; rgba[q + 2] = (byte)b[sel];
                rgba[q + 3] = !fourColor && sel == 3 ? (byte)0 : alpha[py * 4 + px];
            }
    }

    private static void DecodeDxt3Alpha(byte[] d, int o, Span<byte> a)
    {
        for (int k = 0; k < 16; k++)
        {
            int nib = (d[o + k / 2] >> ((k & 1) * 4)) & 0xF;
            a[k] = (byte)(nib * 17);
        }
    }

    private static void DecodeDxt5Alpha(byte[] d, int o, Span<byte> a)
    {
        int a0 = d[o], a1 = d[o + 1];
        Span<int> al = stackalloc int[8];
        al[0] = a0; al[1] = a1;
        if (a0 > a1) for (int i = 1; i <= 6; i++) al[i + 1] = ((7 - i) * a0 + i * a1) / 7;
        else { for (int i = 1; i <= 4; i++) al[i + 1] = ((5 - i) * a0 + i * a1) / 5; al[6] = 0; al[7] = 255; }
        long bits = 0; for (int i = 0; i < 6; i++) bits |= (long)d[o + 2 + i] << (8 * i);
        for (int k = 0; k < 16; k++) a[k] = (byte)al[(int)((bits >> (3 * k)) & 7)];
    }

    private static void Unpack565(ushort c, out int r, out int g, out int b)
    {
        r = ((c >> 11) & 0x1F); r = (r << 3) | (r >> 2);
        g = ((c >> 5) & 0x3F); g = (g << 2) | (g >> 4);
        b = (c & 0x1F); b = (b << 3) | (b >> 2);
    }

    /// <summary>Encode a texture as an uncompressed 32-bit B8G8R8A8 .dds (the simplest form the engine —
    /// and our own <see cref="Decode"/> — read back losslessly). Used for generated minimaps/thumbnails.</summary>
    public static byte[] EncodeUncompressed(Texture2D t)
    {
        int w = t.Width, h = t.Height;
        var buf = new byte[128 + w * h * 4];
        buf[0] = (byte)'D'; buf[1] = (byte)'D'; buf[2] = (byte)'S'; buf[3] = (byte)' ';
        void U32(int off, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off), v);
        U32(4, 124);                                  // dwSize
        U32(8, 0x1 | 0x2 | 0x4 | 0x8 | 0x1000);       // CAPS|HEIGHT|WIDTH|PITCH|PIXELFORMAT
        U32(12, (uint)h);                             // dwHeight
        U32(16, (uint)w);                             // dwWidth
        U32(20, (uint)(w * 4));                       // dwPitchOrLinearSize
        U32(76, 32);                                  // ddspf.dwSize
        U32(80, 0x40 | 0x1);                          // DDPF_RGB | DDPF_ALPHAPIXELS
        U32(88, 32);                                  // RGB bit count
        U32(92, 0x00FF0000);                          // R mask  (dword ARGB -> bytes BGRA)
        U32(96, 0x0000FF00);                          // G mask
        U32(100, 0x000000FF);                         // B mask
        U32(104, 0xFF000000);                         // A mask
        U32(108, 0x1000);                             // dwCaps = DDSCAPS_TEXTURE
        var px = t.Rgba;
        int o = 128;
        for (int i = 0; i < w * h; i++)
        {
            buf[o++] = px[i * 4 + 2];   // B
            buf[o++] = px[i * 4 + 1];   // G
            buf[o++] = px[i * 4 + 0];   // R
            buf[o++] = px[i * 4 + 3];   // A
        }
        return buf;
    }

    public static void Save(Texture2D t, string path) => File.WriteAllBytes(path, EncodeUncompressed(t));

    /// <summary>
    /// The same uncompressed 32-bit surface, but with a full box-filtered mipmap chain and the header
    /// bookkeeping shipped textures use. Object textures need this: a single-level texture on a quad seen at a
    /// distance aliases hard, and 2,343 of 2,550 retail .dds carry a chain. The engine reads only the FourCC,
    /// bit count and green mask to choose a format, so the surface stays plain ARGB8888 and the alpha survives
    /// for alphaTestRef.
    /// </summary>
    public static byte[] EncodeUncompressedMipped(Texture2D t)
    {
        var levels = new List<Texture2D> { t };
        while (levels[^1].Width > 1 || levels[^1].Height > 1) levels.Add(HalveBox(levels[^1]));

        int total = 0;
        foreach (var l in levels) total += l.Width * l.Height * 4;
        var buf = new byte[128 + total];
        buf[0] = (byte)'D'; buf[1] = (byte)'D'; buf[2] = (byte)'S'; buf[3] = (byte)' ';
        void U32(int off, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off), v);
        U32(4, 124);
        U32(8, 0x1 | 0x2 | 0x4 | 0x1000 | 0x20000 | 0x80000);   // CAPS|HEIGHT|WIDTH|PIXELFORMAT|MIPMAPCOUNT|LINEARSIZE
        U32(12, (uint)t.Height);
        U32(16, (uint)t.Width);
        U32(20, (uint)(t.Width * t.Height * 4));                // dwPitchOrLinearSize = the surface, as shipped files write it
        U32(28, (uint)levels.Count);                            // dwMipMapCount
        U32(76, 32);
        U32(80, 0x40 | 0x1);
        U32(88, 32);
        U32(92, 0x00FF0000);
        U32(96, 0x0000FF00);
        U32(100, 0x000000FF);
        U32(104, 0xFF000000);
        U32(108, 0x1000 | 0x8 | 0x400000);                      // TEXTURE|COMPLEX|MIPMAP
        int o = 128;
        foreach (var l in levels)
        {
            var px = l.Rgba;
            for (int i = 0; i < l.Width * l.Height; i++)
            {
                buf[o++] = px[i * 4 + 2]; buf[o++] = px[i * 4 + 1];
                buf[o++] = px[i * 4 + 0]; buf[o++] = px[i * 4 + 3];
            }
        }
        return buf;
    }

    /// <summary>Half-size box filter, one mip level down (each axis floors at 1).</summary>
    private static Texture2D HalveBox(Texture2D s)
    {
        int w = Math.Max(1, s.Width / 2), h = Math.Max(1, s.Height / 2);
        var dst = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Min(x * 2, s.Width - 1), x1 = Math.Min(x * 2 + 1, s.Width - 1);
                int y0 = Math.Min(y * 2, s.Height - 1), y1 = Math.Min(y * 2 + 1, s.Height - 1);
                for (int c = 0; c < 4; c++)
                    dst[(y * w + x) * 4 + c] = (byte)((
                        s.Rgba[(y0 * s.Width + x0) * 4 + c] + s.Rgba[(y0 * s.Width + x1) * 4 + c] +
                        s.Rgba[(y1 * s.Width + x0) * 4 + c] + s.Rgba[(y1 * s.Width + x1) * 4 + c] + 2) / 4);
            }
        return new Texture2D(w, h, dst);
    }

    /// <summary>
    /// Resample to power-of-two dimensions, clamped to [min,max]. The engine's texture manager runs an
    /// is-power-of-two test on both axes and, in its default mode, logs "Ignoring non-pow2 texture" and drops
    /// the image — so a photo or screenshot handed straight through would leave the object untextured.
    /// Stretches rather than pads, so the quad's UVs still cover the whole picture.
    /// </summary>
    /// <summary>
    /// Put a picture in the top-left of a power-of-two canvas and leave the rest black, reporting how much of the
    /// canvas it fills. This is what the engine does with a Bink frame - a 1920x800 movie is decoded into a 2048x1024
    /// texture - so a quad that maps 0..1 over the result shows the padding as black bands. Feed the fractions back
    /// into the mesh's UVs and the picture fills the quad exactly.
    /// </summary>
    public static Texture2D PadToPowerOfTwo(Texture2D t, out float uMax, out float vMax)
    {
        static int Up(int v) { int p = 1; while (p < v && p < 1 << 20) p *= 2; return Math.Max(p, 1); }
        int w = Up(t.Width), h = Up(t.Height);
        uMax = t.Width / (float)w;
        vMax = t.Height / (float)h;
        if (w == t.Width && h == t.Height) return t;

        var dst = new byte[w * h * 4];
        for (int y = 0; y < t.Height; y++)
        {
            Buffer.BlockCopy(t.Rgba, y * t.Width * 4, dst, y * w * 4, t.Width * 4);
            for (int x = t.Width; x < w; x++) dst[(y * w + x) * 4 + 3] = 255;   // opaque black in the padding
        }
        for (int y = t.Height; y < h; y++)
            for (int x = 0; x < w; x++) dst[(y * w + x) * 4 + 3] = 255;
        return new Texture2D(w, h, dst);
    }

    public static Texture2D ToPowerOfTwo(Texture2D t, int min = 4, int max = 1024)
    {
        static int Snap(int v, int min, int max)
        {
            int p = 1;
            while (p * 2 <= v && p < 1 << 20) p *= 2;
            if (v > p && p * 2 - v < v - p) p *= 2;      // nearest, not always down
            return Math.Clamp(p, min, max);
        }
        int w = Snap(t.Width, min, max), h = Snap(t.Height, min, max);
        if (w == t.Width && h == t.Height) return t;

        var dst = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            float sy = (y + 0.5f) * t.Height / h - 0.5f;
            int y0 = Math.Clamp((int)MathF.Floor(sy), 0, t.Height - 1);
            int y1 = Math.Min(y0 + 1, t.Height - 1);
            float fy = Math.Clamp(sy - y0, 0f, 1f);
            for (int x = 0; x < w; x++)
            {
                float sx = (x + 0.5f) * t.Width / w - 0.5f;
                int x0 = Math.Clamp((int)MathF.Floor(sx), 0, t.Width - 1);
                int x1 = Math.Min(x0 + 1, t.Width - 1);
                float fx = Math.Clamp(sx - x0, 0f, 1f);
                for (int c = 0; c < 4; c++)
                {
                    float a = t.Rgba[(y0 * t.Width + x0) * 4 + c] * (1 - fx) + t.Rgba[(y0 * t.Width + x1) * 4 + c] * fx;
                    float b = t.Rgba[(y1 * t.Width + x0) * 4 + c] * (1 - fx) + t.Rgba[(y1 * t.Width + x1) * 4 + c] * fx;
                    dst[(y * w + x) * 4 + c] = (byte)MathF.Round(a * (1 - fy) + b * fy);
                }
            }
        }
        return new Texture2D(w, h, dst);
    }
}

/// <summary>
/// The baked terrain texture for a level: an N×N grid of <c>txCxR.dds</c> tiles (which may be at
/// different resolutions — borders are often 256², detail areas 1024²). Tiles are kept separately and
/// sampled per-tile, preserving full detail without a huge stitched atlas. Maps a world XZ position to
/// a texel so the terrain mesh can be drawn with the real game surface instead of a height ramp.
/// </summary>
public sealed class TerrainTexture
{
    private readonly Texture2D?[,] _tiles;
    private readonly string?[,]? _tileNames;   // original on-disk leaf name per tile (e.g. "tx00x00.dds"), for byte-exact save-back
    private readonly (int W, bool Dxt)[,]? _tileNative;   // the shipped file's size and whether it was DXT, so save-back matches it
    private readonly int _gridW, _gridH;
    private readonly float _worldSize;
    private int _maxTile;

    /// <summary>World Z maps directly to tile rows (the baked tiles align 1:1 with the heightmap), so
    /// no V flip is needed — flipping V mirrors the ground texture vertically off the terrain.</summary>
    public bool FlipV { get; set; } = false;
    /// <summary>Some maps need the texture column axis flipped to match world X.</summary>
    public bool FlipU { get; set; } = false;
    /// <summary>Whether tile col/row map to world (X,Z) transposed.</summary>
    public bool Transpose { get; set; } = false;

    /// <summary>Fine tiling detail texture (BF's detailTexName) multiplied over the base tiles up close;
    /// neutral at mid-grey (×2). Null = base tiles only.</summary>
    public Texture2D? Detail { get; set; }
    /// <summary>World distance (metres) over which the detail texture repeats once.</summary>
    public float DetailRepeatMeters { get; set; } = 8f;
    /// <summary>UV multiplier for the detail texture (world span / repeat) — feeds the GPU shader.</summary>
    public float DetailScale => _worldSize / DetailRepeatMeters;

    /// <summary>Representative tile resolution (largest tile width) — for reporting.</summary>
    public int AtlasSize => _maxTile;

    /// <summary>True when this level's terrain tiles are in a form the game cannot draw: not block-compressed. Only
    /// one thing ever wrote those - an older RefractorForge, which saved 1024x1024 uncompressed mip-less tiles and
    /// left the ground BLACK in game. The editor re-encodes them on the next save. SIZE is not a fault: retail BFV
    /// ships 256 px, mods 512, and Saigon68 / PoE_El_Alamein ship 1024 px DXT1 tiles the game draws fine - flagging
    /// those made every save of such a map re-encode (and slightly degrade) sixteen perfectly good tiles.</summary>
    public bool HasLegacyTiles
    {
        get
        {
            if (_tileNative is null) return false;
            for (int r = 0; r < _gridH; r++)
                for (int c = 0; c < _gridW; c++)
                {
                    var n = _tileNative[c, r];
                    if (n.W > 0 && !n.Dxt) return true;
                }
            return false;
        }
    }

    /// <summary>The atlas resolution that preserves the source tiles' full detail: the largest tile size times
    /// the grid side. A high-res terrain texture (e.g. 2048px tiles in a 2×2 grid) wants a 4096 atlas, not 2048.</summary>
    public int NativeSize => _maxTile * Math.Max(_gridW, _gridH);

    /// <summary>Metres of world covered by one terrain tile. Constant across the retail maps: Bocage and El Alamein
    /// ship 8x8 tiles over 2048 m, Kharkov 4x4 over 1024 m.</summary>
    public const float MetresPerTile = 256f;

    /// <summary>Tile offset of the shipped tiles within the map's full tile grid — non-zero when a level textures
    /// only part of its world.</summary>
    private float _tileOriginX, _tileOriginY;      // cleared by ValidateCentringAgainst if the map disagrees
    private int _gridFullW, _gridFullH;

    private TerrainTexture(Texture2D?[,] tiles, int gw, int gh, float worldSize, int maxTile, string?[,]? tileNames = null, (int W, bool Dxt)[,]? native = null)
    {
        _tiles = tiles; _gridW = gw; _gridH = gh; _worldSize = worldSize; _maxTile = maxTile; _tileNames = tileNames; _tileNative = native;
        // A tile is always 256 m, so a map's FULL grid follows from its world size. Naval maps texture only the
        // middle of the world and ship fewer tiles than that (Wake: 4x4 for a map that spans 8x8), leaving open
        // ocean around the edge. Anchoring those tiles at the origin corner - which is what indexing them directly
        // does - drags the whole painted surface outward, so the beach ends up out at sea. Centre them instead.
        int fullW = worldSize > 0 ? (int)Math.Round(worldSize / MetresPerTile) : gw;
        _gridFullW = Math.Max(gw, fullW);
        _gridFullH = Math.Max(gh, fullW);
        _tileOriginX = (_gridFullW - gw) * 0.5f;
        _tileOriginY = (_gridFullH - gh) * 0.5f;
    }

    /// <summary>The shipped tiles' extent within the world, as a UV rectangle. <c>(0,0,1,1)</c> when the level
    /// textures its whole map.</summary>
    public (float U0, float V0, float U1, float V1) TexturedExtent => (
        _tileOriginX / _gridFullW, _tileOriginY / _gridFullH,
        (_tileOriginX + _gridW) / _gridFullW, (_tileOriginY + _gridH) / _gridFullH);

    /// <summary>
    /// CHECK the centring against the level's own data instead of trusting the rule.
    ///
    /// Centring is inferred from tile count versus world size, but that alone cannot tell "only the middle is
    /// textured" from "fewer, larger tiles covering everything" - and guessing wrong moves the ground texture on a
    /// map that was fine. The MaterialMap spans the whole world and says where the level actually has terrain
    /// content, so it settles it per map: if that content does not fit inside the centred block, the assumption is
    /// wrong and we fall back to the original corner-anchored mapping.
    ///
    /// Measured on the retail maps: Wake/Iwo Jima/Coral Sea/Midway all have their material content inside the
    /// centred block (Wake 0.29..0.74 inside 0.25..0.75), while Berlin's sits in the TOP-RIGHT corner
    /// (0.81..0.93) - which is exactly the case this guard exists to refuse.
    /// </summary>
    public void ValidateCentringAgainst(byte[] materialSamples, int side)
    {
        if (_tileOriginX == 0f && _tileOriginY == 0f) return;             // nothing inferred, nothing to check
        if (materialSamples is null || side <= 0 || materialSamples.Length < side * side) return;

        var hist = new Dictionary<byte, int>();
        foreach (var b in materialSamples) { hist.TryGetValue(b, out var c); hist[b] = c + 1; }
        if (hist.Count <= 1) return;                                       // uniform: tells us nothing either way
        byte bg = hist.OrderByDescending(k => k.Value).First().Key;

        int x0 = side, y0 = side, x1 = -1, y1 = -1;
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
                if (materialSamples[y * side + x] != bg)
                {
                    if (x < x0) x0 = x; if (x > x1) x1 = x;
                    if (y < y0) y0 = y; if (y > y1) y1 = y;
                }
        if (x1 < 0) return;

        var (u0, v0, u1, v1) = TexturedExtent;
        const float slack = 0.02f;   // a little tolerance: content may touch the very edge of its tiles
        bool fits = x0 / (float)side >= u0 - slack && (x1 + 1) / (float)side <= u1 + slack &&
                    y0 / (float)side >= v0 - slack && (y1 + 1) / (float)side <= v1 + slack;
        if (fits) return;

        Console.WriteLine($"Terrain tiles: content spans u {x0 / (float)side:0.00}..{(x1 + 1) / (float)side:0.00}, " +
                          $"v {y0 / (float)side:0.00}..{(y1 + 1) / (float)side:0.00}, outside the centred block " +
                          $"{u0:0.00}..{u1:0.00} - keeping the tiles corner-anchored.");
        _tileOriginX = 0f; _tileOriginY = 0f;
        _gridFullW = _gridW; _gridFullH = _gridH;
    }

    /// <summary>
    /// How big to decode each tile: enough for the atlas the viewer will bake them into (the tiles' real size across
    /// the grid, clamped to 2048..<paramref name="atlasCap"/>, exactly as the viewer sizes it), as a power of two.
    /// </summary>
    public static int DecodeSideFor(int maxNativeTile, int gridSide, int atlasCap = 8192)
    {
        gridSide = Math.Max(gridSide, 1);
        int atlas = Math.Clamp(Math.Max(maxNativeTile, 1) * gridSide, 2048, Math.Max(2048, atlasCap));
        int perTile = (atlas + gridSide - 1) / gridSide;
        int side = 1;
        while (side < perTile) side <<= 1;
        return side;
    }

    public static TerrainTexture? Load(string texturesDir, float worldSize, int atlasCap = 8192)
    {
        if (!Directory.Exists(texturesDir)) return null;
        var paths = new Dictionary<(int col, int row), string>();
        int maxCol = -1, maxRow = -1;
        foreach (var f in Directory.EnumerateFiles(texturesDir, "tx*.dds"))
        {
            // Case-INSENSITIVE: the files ship as Tx00x00.dds with a capital T. The enumeration glob above is
            // case-insensitive on Windows so it finds them, but a case-sensitive parse rejected every one, leaving a
            // level opened from an extracted FOLDER with no terrain texture at all (it worked from .rfa, which
            // matches entries elsewhere).
            var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileNameWithoutExtension(f), @"^tx(\d+)x(\d+)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            int col = int.Parse(m.Groups[1].Value), row = int.Parse(m.Groups[2].Value);
            paths[(col, row)] = f; maxCol = Math.Max(maxCol, col); maxRow = Math.Max(maxRow, row);
        }
        if (paths.Count == 0) return null;

        int gw = maxCol + 1, gh = maxRow + 1;
        var tiles = new Texture2D?[gw, gh];
        var names = new string?[gw, gh];
        var native = new (int W, bool Dxt)[gw, gh];
        foreach (var kv in paths) names[kv.Key.col, kv.Key.row] = Path.GetFileName(kv.Value);   // preserve the on-disk name

        // Headers first (128 bytes each), so every tile can be decoded at the size the atlas needs - see FromTileBytes.
        int maxNative = 0;
        foreach (var kv in paths)
        {
            try
            {
                var head = new byte[128];
                using (var fs = File.OpenRead(kv.Value)) fs.ReadAtLeast(head, 128, throwOnEndOfStream: false);
                native[kv.Key.col, kv.Key.row] = DdsTexture.HeaderInfo(head);
                maxNative = Math.Max(maxNative, native[kv.Key.col, kv.Key.row].W);
            }
            catch { }
        }
        int decodeSide = DecodeSideFor(maxNative, Math.Max(gw, gh), atlasCap);
        // Decode tiles in parallel (each writes its own cell; a high-res terrain texture has many large tiles).
        System.Threading.Tasks.Parallel.ForEach(paths, kv =>
        {
            try { tiles[kv.Key.col, kv.Key.row] = DdsTexture.Decode(File.ReadAllBytes(kv.Value), decodeSide); }
            catch { }
        });
        int maxTile = maxNative;                                  // the REAL size - see FromTileBytes
        if (maxTile == 0) foreach (var t in tiles) if (t is not null) maxTile = Math.Max(maxTile, t.Width);
        var tt = new TerrainTexture(tiles, gw, gh, worldSize, maxTile, names, native);
        var detailPath = Path.Combine(texturesDir, "detail.dds");
        if (File.Exists(detailPath)) { try { tt.Detail = DdsTexture.Load(detailPath); } catch { } }
        return tt;
    }

    /// <summary>Global texture UV for a world XZ position (transpose + flips applied).</summary>
    public (float u, float v) Uv(float worldX, float worldZ)
    {
        float u = worldX / _worldSize, v = worldZ / _worldSize;
        if (Transpose) (u, v) = (v, u);
        if (FlipU) u = 1f - u;
        if (FlipV) v = 1f - v;
        return (u, v);
    }

    public Vector3 Sample(float worldX, float worldZ) { var (u, v) = Uv(worldX, worldZ); return SampleUv(u, v); }

    /// <summary>Base tile colour with the fine detail texture multiplied in (BF-style ×2 around mid-grey).
    /// The detail tiles every <see cref="DetailRepeatMeters"/>, adding crisp surface texture up close.</summary>
    public Vector3 SampleUvDetailed(float u, float v)
    {
        var baseCol = SampleUv(u, v);
        if (Detail is null) return baseCol;
        float du = u * _worldSize / DetailRepeatMeters, dv = v * _worldSize / DetailRepeatMeters;
        var d = Detail.Sample(du - MathF.Floor(du), dv - MathF.Floor(dv));
        return new Vector3(
            Math.Clamp(baseCol.X * d.X * 2f, 0f, 1f),
            Math.Clamp(baseCol.Y * d.Y * 2f, 0f, 1f),
            Math.Clamp(baseCol.Z * d.Z * 2f, 0f, 1f));
    }

    /// <summary>Sample at a global UV (used by the rasterizer with interpolated UVs): pick the tile, then
    /// sample within it. Falls back to a neutral tone for missing tiles.</summary>
    public Vector3 SampleUv(float u, float v)
    {
        u -= MathF.Floor(u); v -= MathF.Floor(v);
        // Work in FULL-grid tile units, then step into the shipped block. For a level that textures its whole map
        // the origin is 0 and this is exactly the old maths; for one that textures only the middle (naval maps),
        // it puts the tiles where they actually belong instead of jammed into the origin corner.
        float fu = u * _gridFullW - _tileOriginX, fv = v * _gridFullH - _tileOriginY;
        if (fu < 0f || fv < 0f || fu >= _gridW || fv >= _gridH)
            return new Vector3(0.45f, 0.5f, 0.38f);   // outside the textured area: the untextured-ground colour
        int col = Math.Clamp((int)fu, 0, _gridW - 1);
        int row = Math.Clamp((int)fv, 0, _gridH - 1);
        float lu = fu - col, lv = fv - row;
        var tile = _tiles[col, row];
        if (tile is null) return new Vector3(0.45f, 0.5f, 0.38f);
        return tile.Sample(lu, lv);
    }

    /// <summary>Build a terrain texture from in-memory tile DDS bytes (e.g. read straight from a level
    /// .rfa), keyed by the txCOLxROW.dds file name. Mirrors <see cref="Load"/> without a directory.</summary>
    /// <param name="atlasCap">The largest atlas the viewer will bake these tiles into. Each tile is decoded at the mip
    /// that atlas actually uses (see <see cref="DdsTexture.Decode(byte[], int)"/>) rather than whole - which is what lets
    /// an 8 km map of 1024 px tiles open without holding 4 GB of pixels. <see cref="NativeSize"/> still reports the
    /// tiles' real size, so the atlas the viewer picks, and the size a save writes tiles back at, are unchanged.</param>
    public static TerrainTexture? FromTileBytes(IEnumerable<(string fileName, byte[] dds)> tiles, float worldSize, byte[]? detailDds = null,
                                                int atlasCap = 8192)
    {
        var parsed = new Dictionary<(int col, int row), byte[]>();
        var pnames = new Dictionary<(int col, int row), string>();
        int maxCol = -1, maxRow = -1;
        foreach (var (fileName, dds) in tiles)
        {
            var leaf = Path.GetFileName(fileName.Replace('\\', '/'));
            var m = System.Text.RegularExpressions.Regex.Match(
                Path.GetFileNameWithoutExtension(leaf), @"^tx(\d+)x(\d+)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            int col = int.Parse(m.Groups[1].Value), row = int.Parse(m.Groups[2].Value);
            // Keep the name AS GIVEN, not the bare leaf. When the caller passes a full archive path
            // (Textures/tx00x00.dds) SplitToTiles yields it back, and the save resolves that one entry instead of
            // leaf-matching into whatever folder happens to hold a same-named tile - a level's BACKUP_ copy, say.
            parsed[(col, row)] = dds; pnames[(col, row)] = fileName.Replace('\\', '/'); maxCol = Math.Max(maxCol, col); maxRow = Math.Max(maxRow, row);
        }
        if (parsed.Count == 0) return null;

        int gw = maxCol + 1, gh = maxRow + 1;
        var grid = new Texture2D?[gw, gh];
        var names = new string?[gw, gh];
        var native = new (int W, bool Dxt)[gw, gh];
        foreach (var kv in pnames) names[kv.Key.col, kv.Key.row] = kv.Value;   // the in-archive name, path and all

        // Headers first: the tiles' real size decides the atlas, and the atlas decides how much of each tile to decode.
        int maxNative = 0;
        foreach (var kv in parsed)
        {
            var info = DdsTexture.HeaderInfo(kv.Value);
            native[kv.Key.col, kv.Key.row] = info;
            maxNative = Math.Max(maxNative, info.Width);
        }
        int decodeSide = DecodeSideFor(maxNative, Math.Max(gw, gh), atlasCap);
        System.Threading.Tasks.Parallel.ForEach(parsed, kv =>
        {
            try { grid[kv.Key.col, kv.Key.row] = DdsTexture.Decode(kv.Value, decodeSide); }
            catch { }
        });
        // The REAL tile size, not the decoded one - NativeSize and the atlas size follow from it. Only a grid with no
        // readable DDS header at all falls back to what was decoded.
        int maxTile = maxNative;
        if (maxTile == 0) foreach (var t in grid) if (t is not null) maxTile = Math.Max(maxTile, t.Width);
        var tt = new TerrainTexture(grid, gw, gh, worldSize, maxTile, names, native);
        if (detailDds is not null) { try { tt.Detail = DdsTexture.Decode(detailDds); } catch { } }
        return tt;
    }

    /// <summary>Flatten the mixed-resolution tiles into one square RGBA atlas by sampling SampleUv over
    /// [0,1). Row 0 corresponds to worldZ≈0 (matching Uv()), so a GPU terrain mesh that samples at
    /// (worldX/worldSize, worldZ/worldSize) reproduces this software terrain texture exactly.</summary>
    public Texture2D BakeAtlas(int size)
    {
        var rgba = new byte[size * size * 4];
        // Per-row, embarrassingly parallel (rows write disjoint spans; SampleUv only reads immutable tiles).
        // The bake is the bulk of a texture-heavy map's load time, so this scales it across cores.
        System.Threading.Tasks.Parallel.For(0, size, py =>
        {
            float v = (py + 0.5f) / size;
            for (int px = 0; px < size; px++)
            {
                float u = (px + 0.5f) / size;
                var c = SampleUv(u, v);
                int i = (py * size + px) * 4;
                rgba[i]     = (byte)Math.Clamp((int)(c.X * 255f + 0.5f), 0, 255);
                rgba[i + 1] = (byte)Math.Clamp((int)(c.Y * 255f + 0.5f), 0, 255);
                rgba[i + 2] = (byte)Math.Clamp((int)(c.Z * 255f + 0.5f), 0, 255);
                rgba[i + 3] = 255;
            }
        });
        return new Texture2D(size, size, rgba);
    }

    /// <summary>Bake a surface atlas straight from a material-index map + the editor's 16-slot texture set: each
    /// atlas pixel takes the material under it, maps it through <paramref name="matToSurf"/> to a surface slot, and
    /// samples that texture tiled at <paramref name="tileMeters"/>. This is "Generate Surface Maps" - it auto-paints
    /// the whole map from material types so the user only touches up. atlas (x,y) -> world (x/size*ws, y/size*ws),
    /// matching <see cref="BakeAtlas"/>.</summary>
    public static Texture2D BakeAtlasFromMaterial(RefractorForge.Formats.Terrain.MaterialMap mat, IReadOnlyList<Texture2D?> surfaces,
                                                  int[] matToSurf, int atlasSize, float worldSize, float tileMeters)
    {
        var rgba = new byte[atlasSize * atlasSize * 4];
        int mside = mat.Width;
        float inv = 1f / atlasSize, tile = MathF.Max(tileMeters, 0.01f);
        System.Threading.Tasks.Parallel.For(0, atlasSize, y =>
        {
            float wz = (y + 0.5f) * inv * worldSize;
            int mcy = Math.Clamp((int)((y + 0.5f) * inv * mside), 0, mside - 1);
            for (int x = 0; x < atlasSize; x++)
            {
                int mcx = Math.Clamp((int)((x + 0.5f) * inv * mside), 0, mside - 1);
                int matIdx = mat[mcx, mcy] & 15;
                int slot = (matToSurf is not null && matIdx < matToSurf.Length) ? (matToSurf[matIdx] & 15) : matIdx;
                var tex = (slot >= 0 && slot < surfaces.Count) ? surfaces[slot] : null;
                int o = (y * atlasSize + x) * 4;
                if (tex is null) { rgba[o] = 128; rgba[o + 1] = 128; rgba[o + 2] = 128; rgba[o + 3] = 255; continue; }
                var c = tex.Sample(((x + 0.5f) * inv * worldSize) / tile, wz / tile);
                rgba[o]     = (byte)Math.Clamp((int)(c.X * 255f + 0.5f), 0, 255);
                rgba[o + 1] = (byte)Math.Clamp((int)(c.Y * 255f + 0.5f), 0, 255);
                rgba[o + 2] = (byte)Math.Clamp((int)(c.Z * 255f + 0.5f), 0, 255);
                rgba[o + 3] = 255;
            }
        });
        return new Texture2D(atlasSize, atlasSize, rgba);
    }

    public int GridW => _gridW;
    public int GridH => _gridH;

    /// <summary>The atlas rectangle a tile covers - the inverse of <see cref="SplitToTiles"/>'s sampling.</summary>
    public (int X, int Y, int W, int H) TileRect(int col, int row, int atlasW, int atlasH)
    {
        int x0 = (int)((long)col * atlasW / _gridW), x1 = (int)((long)(col + 1) * atlasW / _gridW);
        int y0 = (int)((long)row * atlasH / _gridH), y1 = (int)((long)(row + 1) * atlasH / _gridH);
        return (x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>The tiles an atlas rectangle touches.</summary>
    public IEnumerable<(int Col, int Row)> TilesIn(int x, int y, int w, int h, int atlasW, int atlasH)
    {
        if (w <= 0 || h <= 0 || atlasW <= 0 || atlasH <= 0) yield break;
        int c0 = Math.Clamp((int)((long)x * _gridW / atlasW), 0, _gridW - 1);
        int c1 = Math.Clamp((int)((long)(x + w - 1) * _gridW / atlasW), 0, _gridW - 1);
        int r0 = Math.Clamp((int)((long)y * _gridH / atlasH), 0, _gridH - 1);
        int r1 = Math.Clamp((int)((long)(y + h - 1) * _gridH / atlasH), 0, _gridH - 1);
        for (int r = r0; r <= r1; r++)
            for (int c = c0; c <= c1; c++)
                if (_tiles[c, r] is not null) yield return (c, r);
    }

    /// <summary>Which tile a level file is, by its leaf name ("tx01x02.dds"), or null for anything else.</summary>
    public (int Col, int Row)? TileFor(string pathOrLeaf)
    {
        var leaf = pathOrLeaf.Replace('\\', '/');
        leaf = leaf[(leaf.LastIndexOf('/') + 1)..];
        for (int r = 0; r < _gridH; r++)
            for (int c = 0; c < _gridW; c++)
            {
                if (_tiles[c, r] is null) continue;
                var n = (_tileNames?[c, r] ?? $"tx{c}x{r}.dds").Replace('\\', '/');
                n = n[(n.LastIndexOf('/') + 1)..];
                if (n.Equals(leaf, StringComparison.OrdinalIgnoreCase)) return (c, r);
            }
        return null;
    }

    /// <summary>Paint a tile that arrived from somewhere else into the atlas, at the atlas's own resolution.
    /// Returns the atlas rectangle it covered.</summary>
    public (int X, int Y, int W, int H) BlitTile(Texture2D atlas, int col, int row, Texture2D tile)
    {
        var (x0, y0, w, h) = TileRect(col, row, atlas.Width, atlas.Height);
        var dst = atlas.Rgba;
        for (int y = 0; y < h; y++)
        {
            float v = (y + 0.5f) / h;
            for (int x = 0; x < w; x++)
            {
                var c = tile.Sample((x + 0.5f) / w, v);
                int o = ((y0 + y) * atlas.Width + x0 + x) * 4;
                dst[o] = (byte)Math.Clamp((int)(c.X * 255f + 0.5f), 0, 255);
                dst[o + 1] = (byte)Math.Clamp((int)(c.Y * 255f + 0.5f), 0, 255);
                dst[o + 2] = (byte)Math.Clamp((int)(c.Z * 255f + 0.5f), 0, 255);
                dst[o + 3] = 255;
            }
        }
        return (x0, y0, w, h);
    }

    /// <summary>Split a (painted) atlas back into the level's terrain tiles for saving: for each tile that
    /// originally existed, resample the atlas region covering that tile into a fresh Texture2D at the tile's
    /// native size, yielding ("txCOLxROW.dds", tile). Inverts <see cref="BakeAtlas"/> (atlas u=worldX/ws ->
    /// column, v=worldZ/ws -> row), so the saved tiles line up with the heightmap exactly as the originals did.
    /// <paramref name="include"/> limits it to the tiles that were actually painted: re-encoding an untouched tile
    /// degrades it a little and changes its bytes, which to a sync looks like an edit nobody made.</summary>
    public IEnumerable<(string fileName, Texture2D tile)> SplitToTiles(Texture2D atlas, Func<int, int, bool>? include = null)
    {
        for (int row = 0; row < _gridH; row++)
            for (int col = 0; col < _gridW; col++)
            {
                var orig = _tiles[col, row];
                if (orig is null) continue;                       // only re-emit tiles that existed
                if (include is not null && !include(col, row)) continue;
                // At the SHIPPED size, not the loaded one. A DXT tile goes back at its own size (256 in every retail
                // level, 512 in some mods). An uncompressed tile - only this editor ever wrote those: 1024, no mips,
                // which the game drew as black ground - goes back to the retail 256.
                int tw = orig.Width, th = orig.Height;
                if (_tileNative is not null && _tileNative[col, row].W > 0)
                {
                    var nt = _tileNative[col, row];
                    tw = th = nt.Dxt ? nt.W : Math.Min(nt.W, 256);
                }
                var rgba = new byte[tw * th * 4];
                for (int ty = 0; ty < th; ty++)
                {
                    float gv = (row + (ty + 0.5f) / th) / _gridH;
                    for (int tx = 0; tx < tw; tx++)
                    {
                        float gu = (col + (tx + 0.5f) / tw) / _gridW;
                        var c = atlas.Sample(gu, gv);             // bilinear (downsamples the high-res atlas)
                        int o = (ty * tw + tx) * 4;
                        rgba[o]     = (byte)Math.Clamp((int)(c.X * 255f + 0.5f), 0, 255);
                        rgba[o + 1] = (byte)Math.Clamp((int)(c.Y * 255f + 0.5f), 0, 255);
                        rgba[o + 2] = (byte)Math.Clamp((int)(c.Z * 255f + 0.5f), 0, 255);
                        rgba[o + 3] = 255;
                    }
                }
                // Re-emit under the tile's ORIGINAL on-disk name (BFV uses zero-padded tx00x00.dds; an unpadded
                // tx0x0.dds would neither overwrite the folder original nor match the archive entry on save).
                string name = _tileNames?[col, row] ?? $"tx{col}x{row}.dds";
                yield return (name, new Texture2D(tw, th, rgba));
            }
    }
}
