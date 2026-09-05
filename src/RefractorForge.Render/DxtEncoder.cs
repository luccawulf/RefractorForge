using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace RefractorForge.Render;

/// <summary>
/// Writes the DDS the game's terrain actually reads: DXT1 (BC1) with a full mip chain. Every retail BfVietnam and
/// BF1942 terrain tile is that (256x256, 9 mips, 43,832 bytes); the uncompressed 32-bit tiles the editor used to
/// write came up black in the game. Header written field for field as the shipped files have it.
/// The encoder is a range fit: each 4x4 block's two endpoints are the block's extreme colours along its principal
/// spread, quantised to 565; texels take the nearest of the four palette colours. Good enough for ground.
/// </summary>
public static class DxtEncoder
{
    public static byte[] EncodeDxt1Mipped(Texture2D top)
    {
        var levels = new List<Texture2D> { top };
        while (levels[^1].Width > 1 || levels[^1].Height > 1) levels.Add(HalveBox(levels[^1]));

        int total = 0;
        foreach (var l in levels) total += Dxt1Size(l.Width, l.Height);
        var buf = new byte[128 + total];
        buf[0] = (byte)'D'; buf[1] = (byte)'D'; buf[2] = (byte)'S'; buf[3] = (byte)' ';
        void U32(int off, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off), v);
        U32(4, 124);
        U32(8, 0x1 | 0x2 | 0x4 | 0x1000 | 0x20000 | 0x80000);   // CAPS|HEIGHT|WIDTH|PIXELFORMAT|MIPMAPCOUNT|LINEARSIZE = 0xA1007
        U32(12, (uint)top.Height);
        U32(16, (uint)top.Width);
        U32(20, (uint)Dxt1Size(top.Width, top.Height));         // dwPitchOrLinearSize: the top level's compressed size
        U32(28, (uint)levels.Count);
        U32(76, 32);
        U32(80, 0x4);                                           // DDPF_FOURCC
        buf[84] = (byte)'D'; buf[85] = (byte)'X'; buf[86] = (byte)'T'; buf[87] = (byte)'1';
        U32(108, 0x1000 | 0x8 | 0x400000);                      // TEXTURE|COMPLEX|MIPMAP = 0x401008
        int o = 128;
        foreach (var l in levels) o += EncodeLevel(l, buf, o);
        return buf;
    }

    /// <summary>
    /// DXT1 with NO mip chain - the format the menu art ships in. Of the 84 BFV levels that carry an
    /// <c>ingamemap.dds</c>, 81 are 512x512 DXT1 with no mips and the other three DXT5; not one is uncompressed,
    /// though the editor had been writing uncompressed BGRA. BF1942 agrees (184 of 240). Thumbnails are the same
    /// story at 128x128. Menu art is drawn at one size, so a chain would only be weight.
    /// </summary>
    public static byte[] EncodeDxt1Flat(Texture2D top)
    {
        var buf = new byte[128 + Dxt1Size(top.Width, top.Height)];
        buf[0] = (byte)'D'; buf[1] = (byte)'D'; buf[2] = (byte)'S'; buf[3] = (byte)' ';
        void U32(int off, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off), v);
        U32(4, 124);
        U32(8, 0x1 | 0x2 | 0x4 | 0x1000 | 0x80000);             // CAPS|HEIGHT|WIDTH|PIXELFORMAT|LINEARSIZE - no MIPMAPCOUNT
        U32(12, (uint)top.Height);
        U32(16, (uint)top.Width);
        U32(20, (uint)Dxt1Size(top.Width, top.Height));
        U32(28, 0);                                             // no mip levels
        U32(76, 32);
        U32(80, 0x4);                                           // DDPF_FOURCC
        buf[84] = (byte)'D'; buf[85] = (byte)'X'; buf[86] = (byte)'T'; buf[87] = (byte)'1';
        U32(108, 0x1000);                                       // TEXTURE only - not COMPLEX, not MIPMAP
        EncodeLevel(top, buf, 128);
        return buf;
    }

    public static int Dxt1Size(int w, int h) => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 8;

    private static int EncodeLevel(Texture2D t, byte[] dst, int at)
    {
        int w = t.Width, h = t.Height, bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
        var px = t.Rgba;
        Span<byte> r = stackalloc byte[16], g = stackalloc byte[16], b = stackalloc byte[16];
        int o = at;
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                for (int i = 0; i < 16; i++)
                {
                    int x = Math.Min(w - 1, bx * 4 + (i & 3)), y = Math.Min(h - 1, by * 4 + (i >> 2));   // clamp = edge repeat
                    int p = (y * w + x) * 4;
                    r[i] = px[p]; g[i] = px[p + 1]; b[i] = px[p + 2];
                }
                EncodeBlock(r, g, b, dst, o);
                o += 8;
            }
        return o - at;
    }

    private static void EncodeBlock(ReadOnlySpan<byte> r, ReadOnlySpan<byte> g, ReadOnlySpan<byte> b, byte[] dst, int o)
    {
        // Principal spread: project every texel onto the block's colour range diagonal and keep the two extremes.
        int minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0;
        for (int i = 0; i < 16; i++)
        {
            if (r[i] < minR) minR = r[i]; if (r[i] > maxR) maxR = r[i];
            if (g[i] < minG) minG = g[i]; if (g[i] > maxG) maxG = g[i];
            if (b[i] < minB) minB = b[i]; if (b[i] > maxB) maxB = b[i];
        }
        float dr = maxR - minR, dg = maxG - minG, db = maxB - minB;
        int lo = 0, hi = 0; float loD = float.MaxValue, hiD = float.MinValue;
        for (int i = 0; i < 16; i++)
        {
            float d = (r[i] - minR) * dr + (g[i] - minG) * dg + (b[i] - minB) * db;
            if (d < loD) { loD = d; lo = i; }
            if (d > hiD) { hiD = d; hi = i; }
        }
        ushort c0 = To565(r[hi], g[hi], b[hi]), c1 = To565(r[lo], g[lo], b[lo]);
        if (c0 == c1)
        {
            // Solid block: one colour, every index 0. (c0 == c1 is the 3-colour mode; index 0 is still c0.)
            BinaryPrimitives.WriteUInt16LittleEndian(dst.AsSpan(o), c0);
            BinaryPrimitives.WriteUInt16LittleEndian(dst.AsSpan(o + 2), c1);
            dst[o + 4] = dst[o + 5] = dst[o + 6] = dst[o + 7] = 0;
            return;
        }
        if (c0 < c1) (c0, c1) = (c1, c0);                     // c0 > c1 selects the 4-colour palette
        Span<int> pr = stackalloc int[4], pg = stackalloc int[4], pb = stackalloc int[4];
        From565(c0, out pr[0], out pg[0], out pb[0]);
        From565(c1, out pr[1], out pg[1], out pb[1]);
        pr[2] = (2 * pr[0] + pr[1]) / 3; pg[2] = (2 * pg[0] + pg[1]) / 3; pb[2] = (2 * pb[0] + pb[1]) / 3;
        pr[3] = (pr[0] + 2 * pr[1]) / 3; pg[3] = (pg[0] + 2 * pg[1]) / 3; pb[3] = (pb[0] + 2 * pb[1]) / 3;
        uint idx = 0;
        for (int i = 0; i < 16; i++)
        {
            int best = 0, bestD = int.MaxValue;
            for (int k = 0; k < 4; k++)
            {
                int er = r[i] - pr[k], eg = g[i] - pg[k], eb = b[i] - pb[k];
                int d = er * er + eg * eg + eb * eb;
                if (d < bestD) { bestD = d; best = k; }
            }
            idx |= (uint)best << (2 * i);
        }
        BinaryPrimitives.WriteUInt16LittleEndian(dst.AsSpan(o), c0);
        BinaryPrimitives.WriteUInt16LittleEndian(dst.AsSpan(o + 2), c1);
        BinaryPrimitives.WriteUInt32LittleEndian(dst.AsSpan(o + 4), idx);
    }

    private static ushort To565(int r, int g, int b) => (ushort)(((r * 31 + 127) / 255 << 11) | ((g * 63 + 127) / 255 << 5) | ((b * 31 + 127) / 255));

    private static void From565(ushort c, out int r, out int g, out int b)
    {
        int r5 = c >> 11, g6 = (c >> 5) & 63, b5 = c & 31;
        r = (r5 << 3) | (r5 >> 2); g = (g6 << 2) | (g6 >> 4); b = (b5 << 3) | (b5 >> 2);
    }

    // 2x2 box filter; odd edges repeat the last texel.
    private static Texture2D HalveBox(Texture2D s)
    {
        int w = Math.Max(1, s.Width / 2), h = Math.Max(1, s.Height / 2);
        var d = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Min(s.Width - 1, x * 2), x1 = Math.Min(s.Width - 1, x * 2 + 1);
                int y0 = Math.Min(s.Height - 1, y * 2), y1 = Math.Min(s.Height - 1, y * 2 + 1);
                for (int c = 0; c < 4; c++)
                {
                    int sum = s.Rgba[(y0 * s.Width + x0) * 4 + c] + s.Rgba[(y0 * s.Width + x1) * 4 + c]
                            + s.Rgba[(y1 * s.Width + x0) * 4 + c] + s.Rgba[(y1 * s.Width + x1) * 4 + c];
                    d[(y * w + x) * 4 + c] = (byte)((sum + 2) / 4);
                }
            }
        return new Texture2D(w, h, d);
    }
}
