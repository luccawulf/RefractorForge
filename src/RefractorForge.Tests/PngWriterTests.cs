using System.Buffers.Binary;
using System.IO.Compression;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The PNG encoder is checked by DECODING what it wrote rather than by eyeballing a byte count: chunk framing,
/// CRCs and the zlib payload all have to be right or the image silently fails to open in whatever is meant to
/// display it, which is the whole point of writing PNG in the first place.
/// </summary>
public class PngWriterTests
{
    static byte[] Gradient(int w, int h)
    {
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                rgba[o] = (byte)(x * 255 / Math.Max(w - 1, 1));
                rgba[o + 1] = (byte)(y * 255 / Math.Max(h - 1, 1));
                rgba[o + 2] = 0x40;
                rgba[o + 3] = 0xFF;
            }
        return rgba;
    }

    /// <summary>Walk the chunks, verifying every CRC, and return them in order.</summary>
    static List<(string Type, byte[] Data)> ReadChunks(byte[] png)
    {
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        var chunks = new List<(string, byte[])>();
        int p = 8;
        while (p < png.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(p)); p += 4;
            string type = System.Text.Encoding.ASCII.GetString(png, p, 4);
            var body = png.AsSpan(p, 4 + len).ToArray();          // type + data, which is what the CRC covers
            var data = png.AsSpan(p + 4, len).ToArray();
            p += 4 + len;
            uint stored = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(p)); p += 4;
            Assert.Equal(Crc32(body), stored);
            chunks.Add((type, data));
        }
        return chunks;
    }

    static uint Crc32(byte[] d)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (var b in d)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }

    [Fact]
    public void Writes_a_structurally_valid_png_with_correct_crcs()
    {
        var png = PngWriter.Encode(37, 19, Gradient(37, 19));
        var chunks = ReadChunks(png);   // asserts every CRC

        Assert.Equal("IHDR", chunks[0].Type);
        Assert.Equal("IDAT", chunks[1].Type);
        Assert.Equal("IEND", chunks[^1].Type);
        Assert.Empty(chunks[^1].Data);

        var ihdr = chunks[0].Data;
        Assert.Equal(37, BinaryPrimitives.ReadInt32BigEndian(ihdr.AsSpan(0)));
        Assert.Equal(19, BinaryPrimitives.ReadInt32BigEndian(ihdr.AsSpan(4)));
        Assert.Equal(8, ihdr[8]);       // bit depth
        Assert.Equal(2, ihdr[9]);       // colour type 2 = RGB
        Assert.Equal(0, ihdr[12]);      // not interlaced
    }

    [Fact]
    public void The_pixels_survive_the_round_trip()
    {
        const int W = 24, H = 11;
        var src = Gradient(W, H);
        var png = PngWriter.Encode(W, H, src);

        var idat = ReadChunks(png).First(c => c.Type == "IDAT").Data;
        using var ms = new MemoryStream(idat);
        using var z = new ZLibStream(ms, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        z.CopyTo(outMs);
        var raw = outMs.ToArray();

        Assert.Equal(H * (1 + W * 3), raw.Length);
        for (int y = 0; y < H; y++)
        {
            int row = y * (1 + W * 3);
            Assert.Equal(0, raw[row]);                       // filter None
            for (int x = 0; x < W; x++)
            {
                int s = (y * W + x) * 4, d = row + 1 + x * 3;
                Assert.Equal(src[s], raw[d]);
                Assert.Equal(src[s + 1], raw[d + 1]);
                Assert.Equal(src[s + 2], raw[d + 2]);
            }
        }
    }

    [Fact]
    public void Alpha_is_kept_only_when_asked_for()
    {
        var rgba = Gradient(8, 8);
        Assert.Equal(2, ReadChunks(PngWriter.Encode(8, 8, rgba))[0].Data[9]);
        Assert.Equal(6, ReadChunks(PngWriter.Encode(8, 8, rgba, keepAlpha: true))[0].Data[9]);
    }

    [Fact]
    public void A_short_buffer_is_refused_rather_than_read_off_the_end()
    {
        Assert.Throws<ArgumentException>(() => PngWriter.Encode(64, 64, new byte[10]));
        Assert.Throws<ArgumentException>(() => PngWriter.Encode(0, 4, new byte[64]));
    }

    [Fact]
    public void A_texture_encodes_straight_through()
    {
        var tex = new Texture2D(6, 4, Gradient(6, 4));
        var chunks = ReadChunks(PngWriter.Encode(tex));
        Assert.Equal(6, BinaryPrimitives.ReadInt32BigEndian(chunks[0].Data.AsSpan(0)));
        Assert.Equal(4, BinaryPrimitives.ReadInt32BigEndian(chunks[0].Data.AsSpan(4)));
    }
}
