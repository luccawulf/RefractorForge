using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace RefractorForge.Render;

/// <summary>
/// A whole-map colour texture from an external terrain tool, cut into the level's ground tiles.
///
/// THE GRID. The engine gives every 64 x 64 cells of the heightmap one texture tile, so a level's tile grid is its
/// heightmap side / 64 in each direction - measured on 848 BF1942 levels (Interstate, stock, DC Final, FH, the
/// expansion packs): 685 match exactly, 156 are a smaller block inside that grid placed by texOffsetX/Y, and the
/// other 7 ship a stray extra row of tiles or keep their heightmap in a patch archive. It is NOT "256 m per tile":
/// that only holds when the heightmap has 4 m between samples. A 2048 heightmap over an 8 km world is 32 x 32 tiles
/// of 256 m; a 1024 heightmap over 32 km is 16 x 16 tiles of 2 km (bf1942's big_map).
///
/// ORIENTATION. The source is a picture with NORTH at the top and WEST on the left - what every terrain tool shows
/// and what this editor's own minimap writes. A tile's first texel row is its SOUTH edge (the convention the editor's
/// tiles have always used, and that shipped maps confirm in game), so rows are turned over on the way in.
///
/// Tile sizes are powers of two up to 4096, the largest texture both games load (retail Tobruk ships 4096 px tiles).
/// Everything is written DXT1 with a full mip chain - the only form the terrain draws.
/// </summary>
public static class TerrainColorImport
{
    /// <summary>Heightmap cells per texture tile, along each side.</summary>
    public const int CellsPerTile = 64;

    public static readonly int[] TileSizes = { 256, 512, 1024, 2048, 4096 };

    /// <summary>Tiles along each side of a level whose heightmap is <paramref name="heightmapSide"/> samples wide.</summary>
    public static int GridSide(int heightmapSide) => Math.Max(1, heightmapSide / CellsPerTile);

    /// <summary>The engine's tile name: zero-padded, as every retail level spells it.</summary>
    public static string TileFileName(int col, int row) => $"tx{col:00}x{row:00}.dds";

    /// <summary>What a grid of DXT1 tiles with mips costs on disk: half a byte per texel, plus a third for the mips.</summary>
    public static long EstimateBytes(int grid, int tilePx) => (long)grid * grid * (DxtEncoder.Dxt1Size(tilePx, tilePx) * 4L / 3 + 128);

    /// <summary>
    /// The tile size that keeps all the detail a source of <paramref name="sourcePx"/> pixels across has, spread over
    /// <paramref name="grid"/> tiles - the power of two at or above its pixels per tile, within 256..4096. Going larger
    /// only stores a blurrier picture in more bytes.
    /// </summary>
    public static int SuggestTileSize(int sourcePx, int grid)
    {
        int per = Math.Max(1, sourcePx / Math.Max(grid, 1));
        foreach (var s in TileSizes) if (s >= per) return s;
        return TileSizes[^1];
    }

    /// <summary>
    /// Tile (<paramref name="col"/>, <paramref name="row"/>) of a <paramref name="grid"/>-square level, sampled out of a
    /// north-up RGBA image at <paramref name="tilePx"/> square. Column 0 is the west edge, row 0 the SOUTH edge.
    /// Bilinear and CLAMPED at the image border - wrapping would bleed the east edge of the map into the west.
    /// </summary>
    public static Texture2D CutTile(byte[] rgba, int width, int height, int grid, int col, int row, int tilePx)
    {
        var dst = new byte[tilePx * tilePx * 4];
        for (int ty = 0; ty < tilePx; ty++)
        {
            // World Z as a fraction of the map, 0 = south; the image's top row is north.
            double z = (row + (ty + 0.5) / tilePx) / grid;
            double sy = (1.0 - z) * height - 0.5;
            for (int tx = 0; tx < tilePx; tx++)
            {
                double x = (col + (tx + 0.5) / tilePx) / grid;
                double sx = x * width - 0.5;
                Bilinear(rgba, width, height, sx, sy, dst, (ty * tilePx + tx) * 4);
            }
        }
        return new Texture2D(tilePx, tilePx, dst);
    }

    /// <summary>
    /// One tile from its own north-up picture (the folder route, for textures too big for one image): resampled to
    /// <paramref name="tilePx"/> and turned over so its first row is the tile's south edge.
    /// </summary>
    public static Texture2D FromTilePicture(byte[] rgba, int width, int height, int tilePx)
    {
        var dst = new byte[tilePx * tilePx * 4];
        for (int ty = 0; ty < tilePx; ty++)
        {
            double sy = (1.0 - (ty + 0.5) / tilePx) * height - 0.5;
            for (int tx = 0; tx < tilePx; tx++)
                Bilinear(rgba, width, height, (tx + 0.5) / tilePx * width - 0.5, sy, dst, (ty * tilePx + tx) * 4);
        }
        return new Texture2D(tilePx, tilePx, dst);
    }

    /// <summary>
    /// The column and row a pre-cut tile picture is for, from its file name: <c>tile_3_7</c>, <c>3_7</c> or
    /// <c>tx03x07</c>. Column counts west to east and row SOUTH to north - the engine's own numbering, so a folder
    /// of tiles names the same squares the level's tx files do. Null for any other name.
    /// </summary>
    public static (int Col, int Row)? ParseTileName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var m = Regex.Match(stem, @"^(?:tile[_-]?)?(\d+)[_x-](\d+)$", RegexOptions.IgnoreCase);
        if (!m.Success) m = Regex.Match(stem, @"^tx(\d+)x(\d+)$", RegexOptions.IgnoreCase);
        return m.Success ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : null;
    }

    /// <summary>The DXT1 + mips bytes the level stores for a tile.</summary>
    public static byte[] Encode(Texture2D tile) => DxtEncoder.EncodeDxt1Mipped(tile);

    private static void Bilinear(byte[] src, int w, int h, double sx, double sy, byte[] dst, int o)
    {
        sx = Math.Clamp(sx, 0, w - 1); sy = Math.Clamp(sy, 0, h - 1);
        int x0 = (int)sx, y0 = (int)sy, x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        double fx = sx - x0, fy = sy - y0;
        long a = ((long)y0 * w + x0) * 4, b = ((long)y0 * w + x1) * 4, c = ((long)y1 * w + x0) * 4, d = ((long)y1 * w + x1) * 4;
        for (int ch = 0; ch < 3; ch++)
        {
            double v = (src[a + ch] * (1 - fx) + src[b + ch] * fx) * (1 - fy) + (src[c + ch] * (1 - fx) + src[d + ch] * fx) * fy;
            dst[o + ch] = (byte)Math.Clamp((int)(v + 0.5), 0, 255);
        }
        dst[o + 3] = 255;
    }
}
