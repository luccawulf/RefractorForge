using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using RefractorForge.Formats;
using RefractorForge.Formats.Imaging;
using RefractorForge.Formats.Terrain;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Terrain from an external tool: reading its PNGs, turning a north-up heightmap into the engine's grid at the right
/// height, cutting a colour map into the level's tiles on the grid the engine uses, and opening a big high-res map
/// without decoding gigabytes of pixels. The failure these guard is mostly SILENT - a terrain mirrored north-south,
/// ten times too tall, or its texture a quarter-turn off still loads and looks almost right.
/// </summary>
public class TerrainImportTests
{
    // ---- a PNG writer with every row filter, so the reader is tested against the format, not just our own writer ----

    private static byte[] MakePng(int w, int h, int colorType, int bitDepth, Func<int, byte[]> rawRow,
                                  int[]? filters = null, byte[]? palette = null, byte[]? trns = null)
    {
        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        int bpp = Math.Max(1, channels * bitDepth / 8);
        var body = new MemoryStream();
        using (var z = new ZLibStream(body, CompressionLevel.Optimal, leaveOpen: true))
        {
            byte[] prev = new byte[rawRow(0).Length];
            for (int y = 0; y < h; y++)
            {
                var raw = rawRow(y);
                int f = filters is null ? 0 : filters[y % filters.Length];
                var enc = new byte[raw.Length];
                for (int i = 0; i < raw.Length; i++)
                {
                    int a = i >= bpp ? raw[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                    int pred = f switch
                    {
                        0 => 0, 1 => a, 2 => b, 3 => (a + b) >> 1,
                        _ => Paeth(a, b, c),
                    };
                    enc[i] = (byte)(raw[i] - pred);
                }
                z.WriteByte((byte)f); z.Write(enc);
                prev = raw;
            }
        }
        var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)w); BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)h);
        ihdr[8] = (byte)bitDepth; ihdr[9] = (byte)colorType;
        Chunk(png, "IHDR", ihdr);
        if (palette is not null) Chunk(png, "PLTE", palette);
        if (trns is not null) Chunk(png, "tRNS", trns);
        Chunk(png, "IDAT", body.ToArray());
        Chunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();

        static int Paeth(int a, int b, int c)
        {
            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }
        static void Chunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length); s.Write(len);
            s.Write(System.Text.Encoding.ASCII.GetBytes(type)); s.Write(data);
            s.Write(new byte[4]);                                         // CRC: not checked by the reader
        }
    }

    [Fact]
    public void A_16_bit_heightmap_keeps_every_bit()
    {
        // The whole reason for our own reader: Windows' decoder drops a 16-bit grey PNG to 8 bits.
        int w = 5, h = 3;
        ushort V(int x, int y) => (ushort)(y * 20000 + x * 3001 + 1);
        var png = MakePng(w, h, 0, 16, y =>
        {
            var row = new byte[w * 2];
            for (int x = 0; x < w; x++) BinaryPrimitives.WriteUInt16BigEndian(row.AsSpan(x * 2), V(x, y));
            return row;
        }, filters: new[] { 1, 4, 3 });
        var s = PngReader.ReadGray16(png, out int rw, out int rh);
        Assert.Equal((w, h), (rw, rh));
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) Assert.Equal(V(x, y), s[y * w + x]);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void Every_row_filter_decodes(int filter)
    {
        int w = 7, h = 4;
        byte C(int x, int y, int c) => (byte)((x * 37 + y * 91 + c * 53) & 255);
        var png = MakePng(w, h, 2, 8, y =>
        {
            var row = new byte[w * 3];
            for (int x = 0; x < w; x++) for (int c = 0; c < 3; c++) row[x * 3 + c] = C(x, y, c);
            return row;
        }, filters: new[] { filter });
        var rgba = PngReader.ReadRgba8(png, out _, out _);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) for (int c = 0; c < 3; c++)
            Assert.Equal(C(x, y, c), rgba[(y * w + x) * 4 + c]);
    }

    [Fact]
    public void A_palette_image_with_transparency_expands()
    {
        var pal = new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255 };
        var png = MakePng(4, 1, 3, 2, _ => new byte[] { 0b00_01_10_01 }, palette: pal, trns: new byte[] { 255, 128, 0 });
        var rgba = PngReader.ReadRgba8(png, out _, out _);
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 255, 0, 128, 0, 0, 255, 0, 0, 255, 0, 128 }, rgba);
    }

    [Fact]
    public void Our_own_png_writer_round_trips()
    {
        var src = new byte[3 * 2 * 4];
        for (int i = 0; i < src.Length; i++) src[i] = (byte)(i * 11);
        for (int i = 3; i < src.Length; i += 4) src[i] = 255;
        var back = PngReader.ReadRgba8(PngWriter.Encode(3, 2, src), out int w, out int h);
        Assert.Equal((3, 2), (w, h));
        Assert.Equal(src, back);
    }

    [Fact]
    public void Interlaced_and_non_png_files_are_refused_with_a_reason()
    {
        Assert.Throws<InvalidDataException>(() => PngReader.Read(new byte[] { 1, 2, 3 }));
        var png = MakePng(2, 2, 0, 8, _ => new byte[2]);
        png[8 + 8 + 12] = 1;                                              // IHDR interlace byte
        var ex = Assert.Throws<InvalidDataException>(() => PngReader.Read(png));
        Assert.Contains("nterlac", ex.Message);
    }

    // ---- a new BF1942 map points at bf1942\levels -----------------------------------------------------------------

    [Theory]
    [InlineData("bf1942")]
    [InlineData("BfVietnam")]
    public void A_new_map_points_its_terrain_at_its_own_game(string baseSub)
    {
        // Every new map once wrote BfVietnam\levels\<name> - a BF1942 map that cannot find its own heightmap.
        var dir = Path.Combine(Path.GetTempPath(), "rf_newmap_" + Guid.NewGuid().ToString("N"));
        try
        {
            var cfg = new TerrainConfig { MaterialSize = 64, WorldSize = 8192, YScale = 1f, WaterLevel = 0f };
            LevelSaver.CreateNewLevel(dir, "Wide", cfg, new Heightmap(64, 64), new EnvironmentSettings(), baseSub: baseSub);
            var terrain = File.ReadAllText(Path.Combine(dir, "Init", "Terrain.con"));
            Assert.Contains($@"GeometryTemplate.file {baseSub}\levels\Wide\Heightmap", terrain);
            Assert.Contains($@"GeometryTemplate.texBaseName {baseSub}\levels\Wide\Textures\Tx", terrain);
            Assert.DoesNotContain(baseSub == "bf1942" ? "BfVietnam" : @"bf1942\", terrain);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---- heightmaps ------------------------------------------------------------------------------------------------

    [Fact]
    public void The_top_of_a_heightmap_image_is_north()
    {
        // A ridge along the image's TOP row must land on the engine's LAST row (world Z max = north); the engine's
        // first row is south. Get this backwards and the map comes in mirrored north-south.
        int n = 8;
        var img = new ushort[n * n];
        for (int x = 0; x < n; x++) img[x] = 65535;                       // image row 0 = top = north
        var hm = HeightmapImport.FromNorthUpImage(img, n, n, n, 100f, HeightmapImport.YScaleFor(100f), out _);
        for (int x = 0; x < n; x++)
        {
            Assert.Equal(65535, hm[x, n - 1]);                            // north edge
            Assert.Equal(0, hm[x, 0]);                                    // south edge
        }
    }

    [Fact]
    public void The_highest_point_lands_at_the_height_asked_for()
    {
        // 65535 in the image means "the highest point"; the caller says that is 300 m. With the yScale YScaleFor
        // picks, the engine must read exactly 300 m there - and half-way up the image, 150 m.
        float top = 300f, ys = HeightmapImport.YScaleFor(top);
        var hm = HeightmapImport.FromNorthUpImage(new ushort[] { 65535, 32768, 0, 0 }, 2, 2, 2, top, ys, out int clamped);
        var cfg = new TerrainConfig { YScale = ys };
        Assert.Equal(300f, cfg.HeightToMeters(hm[0, 1]), 1);
        Assert.Equal(150f, cfg.HeightToMeters(hm[1, 1]), 0);
        Assert.Equal(0, clamped);
        Assert.Equal(300f, HeightmapImport.MaxMetres(ys), 1);
    }

    [Fact]
    public void Terrain_taller_than_the_level_can_hold_is_clamped_and_counted()
    {
        // An existing level keeps its yScale; at 0.5 it holds 128 m, so a 300 m mountain is cut - and says so.
        var hm = HeightmapImport.FromNorthUpImage(new ushort[] { 65535, 65535, 0, 0 }, 2, 2, 2, 300f, 0.5f, out int clamped);
        Assert.Equal(2, clamped);
        Assert.Equal(ushort.MaxValue, hm[0, 1]);
    }

    [Fact]
    public void A_same_size_image_maps_sample_for_sample()
    {
        int n = 5;
        var img = Enumerable.Range(0, n * n).Select(i => (ushort)(i * 1000)).ToArray();
        var hm = HeightmapImport.FromNorthUpImage(img, n, n, n, 100f, HeightmapImport.YScaleFor(100f), out _);
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
            Assert.Equal(img[(n - 1 - y) * n + x], hm[x, y]);
    }

    // ---- the tile grid and the colour map ----------------------------------------------------------------------------

    [Theory]
    [InlineData(2048, 32)]   // 128_planes, and an 8 km map at 4 m spacing
    [InlineData(1024, 16)]   // SwimmingShores, big_map
    [InlineData(512, 8)]     // the 2 km Interstate maps
    [InlineData(128, 2)]     // 40_thousand_feet
    public void The_tile_grid_is_the_heightmap_side_over_64(int heightmapSide, int grid)
        => Assert.Equal(grid, TerrainColorImport.GridSide(heightmapSide));

    [Fact]
    public void Tiles_are_named_the_way_the_engine_spells_them()
    {
        Assert.Equal("tx00x00.dds", TerrainColorImport.TileFileName(0, 0));
        Assert.Equal("tx31x07.dds", TerrainColorImport.TileFileName(31, 7));
    }

    [Fact]
    public void A_colour_map_is_cut_north_up_with_each_tiles_first_row_south()
    {
        // A 2 x 2 grid from a picture whose top half is red and bottom half blue: the NORTH tiles (row 1) are red, the
        // south tiles (row 0) blue. And a picture with its top-LEFT quarter green puts green in the north-WEST tile.
        int w = 64, h = 64;
        var img = new byte[w * h * 4];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            int o = (y * w + x) * 4;
            bool top = y < h / 2, left = x < w / 2;
            (img[o], img[o + 1], img[o + 2]) = top && left ? ((byte)0, (byte)255, (byte)0) : top ? ((byte)255, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)255);
            img[o + 3] = 255;
        }
        var nw = TerrainColorImport.CutTile(img, w, h, 2, 0, 1, 16);
        var ne = TerrainColorImport.CutTile(img, w, h, 2, 1, 1, 16);
        var sw = TerrainColorImport.CutTile(img, w, h, 2, 0, 0, 16);
        Assert.Equal(255, nw.Rgba[8 * 16 * 4 + 8 * 4 + 1]);             // green, north-west
        Assert.Equal(255, ne.Rgba[8 * 16 * 4 + 8 * 4 + 0]);             // red, north-east
        Assert.Equal(255, sw.Rgba[8 * 16 * 4 + 8 * 4 + 2]);             // blue, south-west

        // Within a tile: its first row is the tile's SOUTH edge. A picture fading from white at the top to black at the
        // bottom gives a one-tile map whose first row is dark and last row light.
        var fade = new byte[4 * 64 * 4];
        for (int y = 0; y < 64; y++) for (int x = 0; x < 4; x++) { int o = (y * 4 + x) * 4; fade[o] = fade[o + 1] = fade[o + 2] = (byte)(255 - y * 4); fade[o + 3] = 255; }
        var t = TerrainColorImport.CutTile(fade, 4, 64, 1, 0, 0, 16);
        Assert.True(t.Rgba[0] < 30 && t.Rgba[15 * 16 * 4] > 225, $"first row {t.Rgba[0]}, last row {t.Rgba[15 * 16 * 4]}");
    }

    [Fact]
    public void A_pre_cut_tile_picture_is_turned_the_same_way()
    {
        var pic = new byte[2 * 2 * 4] { 255, 255, 255, 255, 255, 255, 255, 255, 0, 0, 0, 255, 0, 0, 0, 255 };   // white top row
        var t = TerrainColorImport.FromTilePicture(pic, 2, 2, 2);
        Assert.Equal(0, t.Rgba[0]);                                        // first row = south = the picture's bottom
        Assert.Equal(255, t.Rgba[2 * 4]);
    }

    [Theory]
    [InlineData("tile_3_7.png", 3, 7)]
    [InlineData("3_7.tga", 3, 7)]
    [InlineData("tx03x07.dds", 3, 7)]
    [InlineData("Tile-12-0.png", 12, 0)]
    public void Tile_pictures_are_matched_by_name(string name, int col, int row)
        => Assert.Equal((col, row), TerrainColorImport.ParseTileName(name));

    [Fact]
    public void Other_file_names_are_not_tiles()
        => Assert.Null(TerrainColorImport.ParseTileName("heightmap.png"));

    [Theory]
    [InlineData(32768, 32, 1024)]   // an 8 km map at 4 px per metre
    [InlineData(16384, 32, 512)]
    [InlineData(8192, 32, 256)]
    [InlineData(4096, 32, 256)]     // never below the retail 256
    [InlineData(131072, 16, 4096)]  // never above what the games load
    public void The_suggested_tile_size_keeps_the_sources_detail(int sourcePx, int grid, int expected)
        => Assert.Equal(expected, TerrainColorImport.SuggestTileSize(sourcePx, grid));

    [Fact]
    public void The_size_estimate_is_what_the_encoder_writes()
    {
        var tile = new Texture2D(256, 256, new byte[256 * 256 * 4]);
        long one = TerrainColorImport.Encode(tile).Length;
        long est = TerrainColorImport.EstimateBytes(1, 256);
        Assert.InRange(est, one * 0.97, one * 1.03);
    }

    // ---- opening a big high-res map without decoding gigabytes -------------------------------------------------------

    private static byte[] SolidTile(int px, byte r, byte g, byte b)
    {
        var rgba = new byte[px * px * 4];
        for (int i = 0; i < px * px; i++) { rgba[i * 4] = r; rgba[i * 4 + 1] = g; rgba[i * 4 + 2] = b; rgba[i * 4 + 3] = 255; }
        return DxtEncoder.EncodeDxt1Mipped(new Texture2D(px, px, rgba));
    }

    [Fact]
    public void A_dds_decodes_at_the_mip_asked_for()
    {
        var dds = SolidTile(1024, 200, 100, 50);
        var small = DdsTexture.Decode(dds, 256);
        Assert.Equal((256, 256), (small.Width, small.Height));
        Assert.InRange(small.Rgba[0], 190, 210);                           // the same colour, from its own mip
        Assert.Equal(1024, DdsTexture.Decode(dds).Width);                  // and the old call is unchanged
    }

    [Fact]
    public void A_dds_with_its_mips_cut_short_falls_back_to_what_it_has()
    {
        var dds = SolidTile(1024, 10, 20, 30);
        var cut = dds.AsSpan(0, 128 + DxtEncoder.Dxt1Size(1024, 1024) + 10).ToArray();   // mip 1 is not all there
        Assert.Equal(1024, DdsTexture.Decode(cut, 256).Width);
    }

    [Fact]
    public void An_8_km_grid_of_1024_px_tiles_decodes_at_what_the_atlas_needs()
    {
        // 32 x 32 tiles of 1024 px is a 32768 px texture; the viewer bakes it into an 8192 atlas, 256 px of each tile.
        // Decoding them whole was 4 GB. This builds a 4 x 4 corner of that grid (the decode size depends on the grid
        // side, so the test says 32 via a sparse tile at 31,31) and checks each one came out at 256.
        var one = SolidTile(1024, 90, 120, 60);
        var tiles = new List<(string, byte[])>();
        for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) tiles.Add((TerrainColorImport.TileFileName(c, r), one));
        tiles.Add((TerrainColorImport.TileFileName(31, 31), one));
        var tt = TerrainTexture.FromTileBytes(tiles, 8192f)!;
        Assert.Equal(32 * 1024, tt.NativeSize);                            // the atlas choice still sees the real size
        Assert.Equal(256, TerrainTexture.DecodeSideFor(1024, 32));
        var c0 = tt.SampleUv(0.01f, 0.01f);
        Assert.InRange(c0.X * 255f, 80f, 100f);                           // and the colour survived
    }

    [Theory]
    [InlineData(256, 16, 256)]      // a retail 4 km map: 16 tiles of 256 px - decoded whole, as before
    [InlineData(1024, 8, 1024)]     // 8 x 8 of 1024 px: the 8192 atlas uses all of it
    [InlineData(4096, 8, 1024)]     // Tobruk-style 4096 px tiles: 1024 is all an 8192 atlas holds
    [InlineData(1024, 32, 256)]
    public void The_decode_size_never_throws_away_detail_the_atlas_would_show(int native, int grid, int expected)
        => Assert.Equal(expected, TerrainTexture.DecodeSideFor(native, grid));
}
