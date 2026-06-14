namespace RefractorForge.Formats.Terrain;

/// <summary>
/// A 16-bit heightfield. Refractor's Heightmap.raw is a headerless grid of unsigned
/// 16-bit little-endian samples, row-major. There is no dimension cap here — the
/// editor decides the resolution, not a fixed struct.
/// </summary>
/// <remarks>
/// NOTE: the exact grid dimension and vertical scale Battlefield uses for a given
/// world size must be confirmed against a real extracted map. This class is correct
/// for arbitrary square or rectangular 16-bit raw grids; the BFV-specific dimension
/// mapping will live in a higher layer once we verify it against a sample.
/// </remarks>
public sealed class Heightmap
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>Row-major samples, length = Width * Height.</summary>
    public ushort[] Samples { get; }

    public Heightmap(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width;
        Height = height;
        Samples = new ushort[(long)width * height <= int.MaxValue
            ? width * height
            : throw new ArgumentOutOfRangeException(nameof(width), "Grid too large for a single array.")];
    }

    public ushort this[int x, int y]
    {
        get => Samples[y * Width + x];
        set => Samples[y * Width + x] = value;
    }

    /// <summary>Load a 16-bit LE raw heightmap of the given dimensions.</summary>
    public static Heightmap LoadRaw(string path, int width, int height)
    {
        var bytes = File.ReadAllBytes(path);
        long expected = (long)width * height * 2;
        if (bytes.Length < expected)
            throw new InvalidDataException(
                $"File '{path}' is {bytes.Length} bytes; {width}x{height} 16-bit needs {expected}.");

        var hm = new Heightmap(width, height);
        for (int i = 0; i < hm.Samples.Length; i++)
            hm.Samples[i] = (ushort)(bytes[i * 2] | (bytes[i * 2 + 1] << 8));
        return hm;
    }

    /// <summary>
    /// Load a BFV heightmap given the level's materialSize. Confirmed against retail maps:
    /// the grid side equals materialSize exactly (e.g. materialSize 512 -> 512x512).
    /// </summary>
    public static Heightmap LoadForMaterialSize(string path, int materialSize) =>
        LoadRaw(path, materialSize, materialSize);

    /// <summary>Parse a 16-bit LE raw heightmap from an in-memory buffer (e.g. read straight from a .rfa).</summary>
    public static Heightmap FromBytes(byte[] bytes, int width, int height)
    {
        long expected = (long)width * height * 2;
        if (bytes.Length < expected)
            throw new InvalidDataException($"Heightmap buffer is {bytes.Length} bytes; {width}x{height} 16-bit needs {expected}.");
        var hm = new Heightmap(width, height);
        for (int i = 0; i < hm.Samples.Length; i++)
            hm.Samples[i] = (ushort)(bytes[i * 2] | (bytes[i * 2 + 1] << 8));
        return hm;
    }

    /// <summary>Load a BFV heightmap (grid side == materialSize) from an in-memory buffer.</summary>
    public static Heightmap LoadForMaterialSize(byte[] bytes, int materialSize) => FromBytes(bytes, materialSize, materialSize);

    /// <summary>Convenience for square maps where length implies the side.</summary>
    public static Heightmap LoadRawSquare(string path)
    {
        long count = new FileInfo(path).Length / 2;
        int side = (int)Math.Round(Math.Sqrt(count));
        if ((long)side * side != count)
            throw new InvalidDataException($"'{path}' ({count} samples) is not a square grid; pass dimensions explicitly.");
        return LoadRaw(path, side, side);
    }

    /// <summary>The raw 16-bit little-endian heightmap bytes (the on-disk Heightmap.raw form).</summary>
    public byte[] ToBytes()
    {
        var bytes = new byte[Samples.Length * 2];
        for (int i = 0; i < Samples.Length; i++)
        {
            bytes[i * 2] = (byte)(Samples[i] & 0xFF);
            bytes[i * 2 + 1] = (byte)(Samples[i] >> 8);
        }
        return bytes;
    }

    public void SaveRaw(string path) => File.WriteAllBytes(path, ToBytes());

    /// <summary>
    /// Bilinearly resample this heightmap onto a new grid size. Corner-aligned (the four corners map
    /// exactly, so the world extent is preserved) and edge-clamped. Identity dimensions return an exact
    /// copy. Used when an imported Heightmap.raw doesn't match the level's materialSize.
    /// </summary>
    public Heightmap Resample(int newWidth, int newHeight)
    {
        if (newWidth <= 0 || newHeight <= 0) throw new ArgumentOutOfRangeException(nameof(newWidth));
        var dst = new Heightmap(newWidth, newHeight);
        if (newWidth == Width && newHeight == Height) { Array.Copy(Samples, dst.Samples, Samples.Length); return dst; }

        float sx = newWidth  <= 1 ? 0f : (Width  - 1) / (float)(newWidth  - 1);
        float sy = newHeight <= 1 ? 0f : (Height - 1) / (float)(newHeight - 1);
        for (int y = 0; y < newHeight; y++)
        {
            float fy = y * sy; int y0 = (int)fy; int y1 = Math.Min(y0 + 1, Height - 1); float ty = fy - y0;
            for (int x = 0; x < newWidth; x++)
            {
                float fx = x * sx; int x0 = (int)fx; int x1 = Math.Min(x0 + 1, Width - 1); float tx = fx - x0;
                float top = this[x0, y0] * (1 - tx) + this[x1, y0] * tx;
                float bot = this[x0, y1] * (1 - tx) + this[x1, y1] * tx;
                dst[x, y] = (ushort)Math.Clamp((int)MathF.Round(top * (1 - ty) + bot * ty), 0, 65535);
            }
        }
        return dst;
    }

    /// <summary>Overwrite this heightmap's samples from another of identical dimensions, in place — so existing
    /// references (TerrainPick / TerrainEditor / undo snapshots) keep pointing at the same valid object.</summary>
    public void CopyFrom(Heightmap other)
    {
        if (other.Width != Width || other.Height != Height)
            throw new ArgumentException($"CopyFrom dimension mismatch: {other.Width}x{other.Height} into {Width}x{Height}.");
        Array.Copy(other.Samples, Samples, Samples.Length);
    }
}
