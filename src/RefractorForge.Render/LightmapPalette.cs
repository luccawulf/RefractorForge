using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace RefractorForge.Render;

/// <summary>
/// The palette BfVietnam reads its 8-bit object lightmaps through: the level's <c>Objectlightmaps/Palette.pal</c>.
///
/// <para>Measured in the game with the calibration level (docs/Realistic_Lighting.md): an 8-bit colour-mapped
/// lightmap's index is looked up in the LEVEL'S palette file - the colour map inside the .tga is ignored, a board
/// with a recoloured one came out identical to its grey twin - and the result is held in a 5:6:5 texture, so a
/// lightmapped surface can show 32 levels of light. A 24-bit lightmap is read as it is, at full precision.</para>
///
/// <para>That matters because the palettes are not ramps. The retail one has a toe (index 64 is 47, 96 is 73),
/// Operation Irving's is black up to index 80, and Battlecraft writes a tinted one that runs from about 25 to 185.
/// A bake that writes "index = light" - which is what the editor did, with a linear ramp inside each file that the
/// game never looks at - is therefore shown through whatever curve the level happens to carry. This picks the
/// index whose SHOWN value is the light that was meant.</para>
/// </summary>
public sealed class LightmapPalette
{
    private readonly byte[] _rgb;                     // 256 x (r, g, b)

    private LightmapPalette(byte[] rgb) => _rgb = rgb;

    /// <summary>The plain ramp: index i is grey i. What <see cref="TgaTexture.GreyPalettePal"/> writes.</summary>
    public static LightmapPalette Linear { get; } = MakeLinear();

    private static LightmapPalette MakeLinear()
    {
        var rgb = new byte[768];
        for (int i = 0; i < 256; i++) rgb[i * 3] = rgb[i * 3 + 1] = rgb[i * 3 + 2] = (byte)i;
        return new LightmapPalette(rgb);
    }

    /// <summary>Read a RIFF PAL (the 1,048-byte file retail levels ship). Null when it is not one.</summary>
    public static LightmapPalette? Parse(byte[]? riff)
    {
        if (riff is null || riff.Length < 24 + 4 || riff[0] != 'R' || riff[1] != 'I' || riff[2] != 'F' || riff[3] != 'F') return null;
        int data = -1;
        for (int i = 8; i + 8 <= riff.Length; i++)
            if (riff[i] == 'd' && riff[i + 1] == 'a' && riff[i + 2] == 't' && riff[i + 3] == 'a') { data = i + 8; break; }
        if (data < 0 || data + 4 > riff.Length) return null;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(riff.AsSpan(data + 2));
        if (count <= 0) return null;
        var rgb = new byte[768];
        for (int i = 0; i < 256; i++)
        {
            int o = data + 4 + Math.Min(i, count - 1) * 4;    // a short palette repeats its last entry
            if (o + 2 >= riff.Length) break;
            rgb[i * 3] = riff[o]; rgb[i * 3 + 1] = riff[o + 1]; rgb[i * 3 + 2] = riff[o + 2];
        }
        return new LightmapPalette(rgb);
    }

    /// <summary>The palette as the RIFF PAL file the engine loads.</summary>
    public byte[] ToRiff()
    {
        var b = TgaTexture.GreyPalettePal();
        for (int i = 0; i < 256; i++) { b[24 + i * 4] = _rgb[i * 3]; b[25 + i * 4] = _rgb[i * 3 + 1]; b[26 + i * 4] = _rgb[i * 3 + 2]; }
        return b;
    }

    public (byte R, byte G, byte B) this[int index] => (_rgb[index * 3], _rgb[index * 3 + 1], _rgb[index * 3 + 2]);

    public bool IsLinear
    {
        get
        {
            for (int i = 0; i < 256; i++) if (_rgb[i * 3] != i || _rgb[i * 3 + 1] != i || _rgb[i * 3 + 2] != i) return false;
            return true;
        }
    }

    /// <summary>A byte after a trip through a 5-bit (or, for green, 6-bit) texture channel: the low bits are dropped
    /// and the top ones repeated into their place. 47 comes back as 41, 128 as 132, 191 as 189 - the values the
    /// calibration boards showed.</summary>
    public static byte Through565(byte v, bool green = false)
        => green ? (byte)((v & 0xFC) | (v >> 6)) : (byte)((v & 0xF8) | (v >> 5));

    /// <summary>What the game shows for an index: the palette entry, through the 5:6:5 texture it is held in.</summary>
    public (byte R, byte G, byte B) Shown(int index)
        => (Through565(_rgb[index * 3]), Through565(_rgb[index * 3 + 1], green: true), Through565(_rgb[index * 3 + 2]));

    /// <summary>
    /// Can a bake say what it means with this palette? It has to be grey (the fixed-function shader multiplies the
    /// light by all three channels, so a tinted palette tints every lit wall) and it has to reach both black and
    /// full. The retail palette and Operation Irving's pass; Battlecraft's tinted 25-to-185 ramp does not.
    /// </summary>
    public bool Usable
    {
        get
        {
            int lo = 255, hi = 0;
            for (int i = 0; i < 256; i++)
            {
                var (r, g, b) = this[i];
                if (Math.Abs(r - b) > 10 || Math.Abs(g - b) > 10) return false;
                lo = Math.Min(lo, b); hi = Math.Max(hi, b);
            }
            return lo <= 6 && hi >= 249;
        }
    }

    // The distinct light levels this palette can show (by the blue channel, the one the per-pixel shader reads),
    // darkest first, each with the index that shows it most nearly grey.
    private (byte Value, byte Index)[]? _levels;
    private (byte Value, byte Index)[] Levels
    {
        get
        {
            if (_levels is not null) return _levels;
            var best = new Dictionary<byte, (int Index, int Tint)>();
            for (int i = 0; i < 256; i++)
            {
                var (r, g, b) = Shown(i);
                int tint = Math.Abs(r - b) + Math.Abs(g - b);
                if (!best.TryGetValue(b, out var have) || tint < have.Tint) best[b] = (i, tint);
            }
            return _levels = best.OrderBy(kv => kv.Key).Select(kv => (kv.Key, (byte)kv.Value.Index)).ToArray();
        }
    }

    /// <summary>The darkest and the brightest light this palette can show, 0..1.</summary>
    public (float Min, float Max) Range => (Levels[0].Value / 255f, Levels[^1].Value / 255f);

    // 4 x 4 ordered dither: a texel between two showable levels takes the upper one in the right share of places,
    // and the game's own bilinear filter blends them back. Without it a soft shadow edge is 32 hard bands.
    private static readonly byte[] Bayer = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };

    /// <summary>The index to write at texel (x, y) so the game shows <paramref name="value"/> (0..255) there.</summary>
    public byte IndexFor(byte value, int x, int y)
    {
        var lv = Levels;
        if (value <= lv[0].Value) return lv[0].Index;
        if (value >= lv[^1].Value) return lv[^1].Index;
        int lo = 0, hi = lv.Length - 1;
        while (hi - lo > 1) { int mid = (lo + hi) / 2; if (lv[mid].Value <= value) lo = mid; else hi = mid; }
        float t = (value - lv[lo].Value) / (float)(lv[hi].Value - lv[lo].Value);
        float threshold = (Bayer[(y & 3) * 4 + (x & 3)] + 0.5f) / 16f;
        return t > threshold ? lv[hi].Index : lv[lo].Index;
    }

    /// <summary>
    /// Encode a light map (its red channel, 0..255 = no light .. full) as the 8-bit colour-mapped .tga the game
    /// reads, with every index chosen so that THIS palette shows the value that was meant. The file's own colour
    /// map is this palette too - the game ignores it, but anything else that opens the file then sees what the
    /// game will.
    /// </summary>
    public byte[] Encode(Texture2D t)
    {
        int w = t.Width, h = t.Height;
        var buf = new byte[18 + 256 * 3 + w * h];
        buf[1] = 1; buf[2] = 1;                       // colour-mapped, uncompressed
        buf[5] = 0; buf[6] = 1; buf[7] = 24;          // 256 entries of 24 bits
        buf[12] = (byte)(w & 0xFF); buf[13] = (byte)(w >> 8);
        buf[14] = (byte)(h & 0xFF); buf[15] = (byte)(h >> 8);
        buf[16] = 8; buf[17] = 0;                     // 8-bit indices, bottom-left origin: the shipped form
        int p = 18;
        for (int i = 0; i < 256; i++) { buf[p++] = _rgb[i * 3 + 2]; buf[p++] = _rgb[i * 3 + 1]; buf[p++] = _rgb[i * 3]; }
        var px = t.Rgba;
        for (int y = 0; y < h; y++)                   // file row 0 is the picture's bottom row
        {
            int row = h - 1 - y;
            for (int x = 0; x < w; x++) buf[p++] = IndexFor(px[(row * w + x) * 4], x, row);
        }
        return buf;
    }

    /// <summary>
    /// Decode an 8-bit colour-mapped lightmap the way the game does: its indices through this palette and the
    /// 5:6:5 texture. Null for any other kind of file (24-bit, DDS, RLE) - those go through the ordinary readers.
    /// </summary>
    public Texture2D? Decode(byte[]? tga)
    {
        if (tga is null || tga.Length < 18 || tga[1] != 1 || tga[2] != 1 || tga[16] != 8) return null;
        int w = tga[12] | (tga[13] << 8), h = tga[14] | (tga[15] << 8);
        int mapLen = tga[5] | (tga[6] << 8), entry = (tga[7] + 7) / 8;
        int pos = 18 + tga[0] + mapLen * entry;
        if (w <= 0 || h <= 0 || (long)pos + (long)w * h > tga.Length) return null;
        bool topLeft = (tga[17] & 0x20) != 0;
        var shown = new (byte R, byte G, byte B)[256];
        for (int i = 0; i < 256; i++) shown[i] = Shown(i);
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int src = pos + (topLeft ? y : h - 1 - y) * w;
            for (int x = 0; x < w; x++)
            {
                var c = shown[tga[src + x]];
                int o = (y * w + x) * 4;
                rgba[o] = c.R; rgba[o + 1] = c.G; rgba[o + 2] = c.B; rgba[o + 3] = 255;
            }
        }
        return new Texture2D(w, h, rgba);
    }
}
