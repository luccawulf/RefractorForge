using System.Buffers.Binary;

namespace RefractorBridge.Mesh;

/// <summary>What capping a texture did.</summary>
public sealed record DdsCapResult(byte[] Data, int Width, int Height, int LevelsDropped, string? Reason)
{
    public bool Changed => LevelsDropped > 0;
}

/// <summary>
/// Caps a DDS by DROPPING TOP MIP LEVELS - a byte-range copy plus a header edit, never a re-encode.
///
/// This is not an optimisation, it is the difference between a shippable level and an unusable one. A ported
/// level embeds the textures its objects name, and if the source install carries an AI-upscaled texture pack
/// those arrive at 4096 square: porting Berlin pulled in 101 textures totalling 692 MB, one sandbag texture
/// alone at 22 MB, for a 706 MB level archive. Capped at 512 the same content is a few megabytes.
///
/// Dropping mips works because a mipmapped DDS already contains the smaller images: level 1 IS the half-size
/// texture. So the cap is a slice - skip the first N levels' bytes, write the new width, height and mip count
/// into the header, and the pixels that remain are the artist's own, untouched by any recompression.
/// </summary>
public static class DdsCap
{
    private const int HeaderBytes = 128;          // magic + 124-byte DDSURFACEDESC2
    private const uint DdpfFourCc = 0x4;
    private const uint Ddscaps2Cubemap = 0x200;

    public const int DefaultMaxSide = 512;

    public static DdsCapResult Cap(byte[] dds, int maxSide = DefaultMaxSide)
    {
        if (dds.Length < HeaderBytes || BinaryPrimitives.ReadUInt32LittleEndian(dds) != 0x20534444) // "DDS "
            return new DdsCapResult(dds, 0, 0, 0, "not a DDS");

        uint height = R(dds, 12), width = R(dds, 16), mips = R(dds, 28);
        uint pfFlags = R(dds, 80), fourCc = R(dds, 84), bitCount = R(dds, 88);
        uint caps2 = R(dds, 112);

        // A cube map stores six faces back to back; slicing one needs per-face work, and the level's sky is the
        // only thing that uses them. Left alone.
        if ((caps2 & Ddscaps2Cubemap) != 0)
            return new DdsCapResult(dds, (int)width, (int)height, 0, "cube map");

        if (width <= maxSide && height <= maxSide)
            return new DdsCapResult(dds, (int)width, (int)height, 0, null);

        if (mips <= 1)
            return new DdsCapResult(dds, (int)width, (int)height, 0,
                "no mip chain to drop - capping would need a re-encode");

        bool compressed = (pfFlags & DdpfFourCc) != 0;
        int bytesPerPixel = compressed ? 0 : (int)Math.Max(1, bitCount / 8);
        int blockBytes = fourCc switch
        {
            0x31545844 => 8,    // DXT1
            0x32545844 => 16,   // DXT2
            0x33545844 => 16,   // DXT3
            0x34545844 => 16,   // DXT4
            0x35545844 => 16,   // DXT5
            _ => compressed ? 0 : 0,
        };
        if (compressed && blockBytes == 0)
            return new DdsCapResult(dds, (int)width, (int)height, 0, "unrecognised compressed format");

        int dropped = 0, offset = HeaderBytes;
        uint w = width, h = height, remaining = mips;

        while ((w > maxSide || h > maxSide) && remaining > 1)
        {
            offset += LevelBytes(w, h, compressed, blockBytes, bytesPerPixel);
            if (offset > dds.Length)
                return new DdsCapResult(dds, (int)width, (int)height, 0, "mip chain is shorter than the header claims");

            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
            remaining--;
            dropped++;
        }

        if (dropped == 0) return new DdsCapResult(dds, (int)width, (int)height, 0, null);

        var output = new byte[HeaderBytes + (dds.Length - offset)];
        Array.Copy(dds, 0, output, 0, HeaderBytes);
        Array.Copy(dds, offset, output, HeaderBytes, dds.Length - offset);

        W(output, 12, h);
        W(output, 16, w);
        W(output, 28, remaining);
        W(output, 20, (uint)LevelBytes(w, h, compressed, blockBytes, bytesPerPixel));   // pitch / linear size

        return new DdsCapResult(output, (int)w, (int)h, dropped, null);
    }

    private static int LevelBytes(uint w, uint h, bool compressed, int blockBytes, int bytesPerPixel)
    {
        if (!compressed) return (int)(w * h * (uint)bytesPerPixel);
        uint bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
        return (int)(bw * bh * (uint)blockBytes);
    }

    private static uint R(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
    private static void W(byte[] b, int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v);
}
