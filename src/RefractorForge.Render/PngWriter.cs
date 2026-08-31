using System.Buffers.Binary;
using System.IO.Compression;

namespace RefractorForge.Render;

/// <summary>
/// Minimal PNG encoder. The editor writes DDS and TGA for the game, but neither of those is any use for handing an
/// image to something that wants to LOOK at it — an MCP client, a bug report, a README. PNG is the one format
/// everything reads, and encoding it needs nothing beyond the BCL: <see cref="ZLibStream"/> supplies the exact
/// container IDAT wants (zlib header + deflate + Adler-32), so only the chunk framing and CRC are left.
/// </summary>
public static class PngWriter
{
    /// <summary>Encode an RGBA image as a PNG. Alpha is dropped unless <paramref name="keepAlpha"/>, because a
    /// screenshot-style image is smaller and more predictable as RGB.</summary>
    public static byte[] Encode(int width, int height, byte[] rgba, bool keepAlpha = false)
    {
        if (width < 1 || height < 1) throw new ArgumentException("empty image");
        if (rgba.Length < width * height * 4) throw new ArgumentException("rgba is shorter than width*height*4");

        int channels = keepAlpha ? 4 : 3;
        // Each scanline is prefixed with its filter byte; 0 = None, which costs a little size and no complexity.
        var raw = new byte[height * (1 + width * channels)];
        int o = 0;
        for (int y = 0; y < height; y++)
        {
            raw[o++] = 0;
            int src = y * width * 4;
            for (int x = 0; x < width; x++, src += 4)
            {
                raw[o++] = rgba[src];
                raw[o++] = rgba[src + 1];
                raw[o++] = rgba[src + 2];
                if (keepAlpha) raw[o++] = rgba[src + 3];
            }
        }

        byte[] idat;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw, 0, raw.Length);
            idat = ms.ToArray();
        }

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;                              // bit depth
        ihdr[9] = (byte)(keepAlpha ? 6 : 2);      // colour type: 6 = RGBA, 2 = RGB
        ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0; // deflate / adaptive filtering / no interlace

        using var outMs = new MemoryStream(idat.Length + 128);
        outMs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        Chunk(outMs, "IHDR", ihdr);
        Chunk(outMs, "IDAT", idat);
        Chunk(outMs, "IEND", Array.Empty<byte>());
        return outMs.ToArray();
    }

    /// <summary>Encode a decoded texture straight to PNG.</summary>
    public static byte[] Encode(Texture2D tex, bool keepAlpha = false)
        => Encode(tex.Width, tex.Height, tex.Rgba, keepAlpha);

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);

        var tag = new byte[4];
        for (int i = 0; i < 4; i++) tag[i] = (byte)type[i];
        s.Write(tag);
        s.Write(data);

        // The CRC covers the type AND the data, in that order, but not the length field.
        uint crc = Accumulate(Accumulate(0xFFFFFFFFu, tag), data) ^ 0xFFFFFFFFu;
        Span<byte> c = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc);
        s.Write(c);
    }

    private static readonly uint[] _crcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Accumulate(uint crc, byte[] data)
    {
        foreach (var b in data) crc = _crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

}
