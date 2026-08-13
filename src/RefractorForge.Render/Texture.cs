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

/// <summary>
/// Minimal DDS reader for the BC1/BC2/BC3 (DXT1/3/5) textures Battlefield Vietnam ships. Decodes only
/// the top mip to RGBA — enough for previewing terrain tiles and (later) object textures — with no
/// external image dependency, so the editor reads the game's <c>.dds</c> files directly.
/// </summary>
public static class DdsTexture
{
    public static Texture2D Load(string path) => Decode(File.ReadAllBytes(path));

    public static Texture2D Decode(byte[] d)
    {
        if (d.Length < 128 || d[0] != (byte)'D' || d[1] != (byte)'D' || d[2] != (byte)'S' || d[3] != (byte)' ')
            throw new InvalidDataException("Not a DDS file.");
        int height = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(12));
        int width = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(16));
        uint pfFlags = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(80));
        string fourcc = System.Text.Encoding.ASCII.GetString(d, 84, 4);
        int dataOff = 128;
        var rgba = new byte[width * height * 4];

        bool DXT1 = fourcc == "DXT1";
        bool DXT3 = fourcc == "DXT3";
        bool DXT5 = fourcc == "DXT5";
        if (DXT1 || DXT3 || DXT5)
        {
            int blockBytes = DXT1 ? 8 : 16;
            int bx = (width + 3) / 4, by = (height + 3) / 4;
            int p = dataOff;
            Span<byte> alpha = stackalloc byte[16];   // one 16-byte scratch buffer, reused per block
            for (int byk = 0; byk < by; byk++)
                for (int bxk = 0; bxk < bx; bxk++)
                {
                    for (int k = 0; k < 16; k++) alpha[k] = 255;
                    int colorOff = p;
                    if (DXT3) { DecodeDxt3Alpha(d, p, alpha); colorOff = p + 8; }
                    else if (DXT5) { DecodeDxt5Alpha(d, p, alpha); colorOff = p + 8; }
                    DecodeColorBlock(d, colorOff, DXT1, bxk * 4, byk * 4, width, height, rgba, alpha);
                    p += blockBytes;
                }
        }
        else if ((pfFlags & 0x40) != 0)   // uncompressed RGB(A)
        {
            uint rgbBits = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(88));
            int bpp = (int)rgbBits / 8;
            // assume B8G8R8(A8) ordering (common for DDS)
            for (int i = 0, q = dataOff; i < width * height; i++, q += bpp)
            {
                byte b = d[q], g = d[q + 1], r = d[q + 2];
                byte a = bpp >= 4 ? d[q + 3] : (byte)255;
                int o = i * 4; rgba[o] = r; rgba[o + 1] = g; rgba[o + 2] = b; rgba[o + 3] = a;
            }
        }
        else throw new InvalidDataException($"Unsupported DDS pixel format '{fourcc}'.");
        return new Texture2D(width, height, rgba);
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
            r[3] = 0; g[3] = 0; b[3] = 0; // index 3 = transparent black in 3-color mode
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
                rgba[q + 3] = alpha[py * 4 + px];
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

    /// <summary>The atlas resolution that preserves the source tiles' full detail: the largest tile size times
    /// the grid side. A high-res terrain texture (e.g. 2048px tiles in a 2×2 grid) wants a 4096 atlas, not 2048.</summary>
    public int NativeSize => _maxTile * Math.Max(_gridW, _gridH);

    /// <summary>Metres of world covered by one terrain tile. Constant across the retail maps: Bocage and El Alamein
    /// ship 8x8 tiles over 2048 m, Kharkov 4x4 over 1024 m.</summary>
    public const float MetresPerTile = 256f;

    /// <summary>Tile offset of the shipped tiles within the map's full tile grid — non-zero when a level textures
    /// only part of its world.</summary>
    private readonly float _tileOriginX, _tileOriginY;
    private readonly int _gridFullW, _gridFullH;

    private TerrainTexture(Texture2D?[,] tiles, int gw, int gh, float worldSize, int maxTile, string?[,]? tileNames = null)
    {
        _tiles = tiles; _gridW = gw; _gridH = gh; _worldSize = worldSize; _maxTile = maxTile; _tileNames = tileNames;
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

    public static TerrainTexture? Load(string texturesDir, float worldSize)
    {
        if (!Directory.Exists(texturesDir)) return null;
        var paths = new Dictionary<(int col, int row), string>();
        int maxCol = -1, maxRow = -1;
        foreach (var f in Directory.EnumerateFiles(texturesDir, "tx*.dds"))
        {
            var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileNameWithoutExtension(f), @"^tx(\d+)x(\d+)$");
            if (!m.Success) continue;
            int col = int.Parse(m.Groups[1].Value), row = int.Parse(m.Groups[2].Value);
            paths[(col, row)] = f; maxCol = Math.Max(maxCol, col); maxRow = Math.Max(maxRow, row);
        }
        if (paths.Count == 0) return null;

        int gw = maxCol + 1, gh = maxRow + 1;
        var tiles = new Texture2D?[gw, gh];
        var names = new string?[gw, gh];
        foreach (var kv in paths) names[kv.Key.col, kv.Key.row] = Path.GetFileName(kv.Value);   // preserve the on-disk name
        // Decode tiles in parallel (each writes its own cell; a high-res terrain texture has many large tiles).
        System.Threading.Tasks.Parallel.ForEach(paths, kv =>
        {
            try { tiles[kv.Key.col, kv.Key.row] = DdsTexture.Load(kv.Value); } catch { }
        });
        int maxTile = 0;
        foreach (var t in tiles) if (t is not null) maxTile = Math.Max(maxTile, t.Width);
        var tt = new TerrainTexture(tiles, gw, gh, worldSize, maxTile, names);
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
    public static TerrainTexture? FromTileBytes(IEnumerable<(string fileName, byte[] dds)> tiles, float worldSize, byte[]? detailDds = null)
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
            parsed[(col, row)] = dds; pnames[(col, row)] = leaf; maxCol = Math.Max(maxCol, col); maxRow = Math.Max(maxRow, row);
        }
        if (parsed.Count == 0) return null;

        int gw = maxCol + 1, gh = maxRow + 1;
        var grid = new Texture2D?[gw, gh];
        var names = new string?[gw, gh];
        foreach (var kv in pnames) names[kv.Key.col, kv.Key.row] = kv.Value;   // preserve the in-archive name (e.g. tx00x00.dds)
        System.Threading.Tasks.Parallel.ForEach(parsed, kv =>
        {
            try { grid[kv.Key.col, kv.Key.row] = DdsTexture.Decode(kv.Value); } catch { }
        });
        int maxTile = 0;
        foreach (var t in grid) if (t is not null) maxTile = Math.Max(maxTile, t.Width);
        var tt = new TerrainTexture(grid, gw, gh, worldSize, maxTile, names);
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

    /// <summary>Split a (painted) atlas back into the level's terrain tiles for saving: for each tile that
    /// originally existed, resample the atlas region covering that tile into a fresh Texture2D at the tile's
    /// native size, yielding ("txCOLxROW.dds", tile). Inverts <see cref="BakeAtlas"/> (atlas u=worldX/ws ->
    /// column, v=worldZ/ws -> row), so the saved tiles line up with the heightmap exactly as the originals did.</summary>
    public IEnumerable<(string fileName, Texture2D tile)> SplitToTiles(Texture2D atlas)
    {
        for (int row = 0; row < _gridH; row++)
            for (int col = 0; col < _gridW; col++)
            {
                var orig = _tiles[col, row];
                if (orig is null) continue;                       // only re-emit tiles that existed
                int tw = orig.Width, th = orig.Height;
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
