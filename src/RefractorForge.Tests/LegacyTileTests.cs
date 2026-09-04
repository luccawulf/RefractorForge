using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The ground came up black in game because an older build of the editor saved terrain tiles as 1024x1024
/// uncompressed DDS with no mipmaps - a form the engine's terrain will not draw. Every retail tile is DXT1 with a
/// full mip chain at 256 (or 512 in a mod), so a tile that is neither is a repair job, and the editor has to notice.
/// </summary>
public class LegacyTileTests
{
    private static Texture2D Flat(int n, byte v)
    {
        var px = new byte[n * n * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = v; px[i + 1] = v; px[i + 2] = v; px[i + 3] = 255; }
        return new Texture2D(n, n, px);
    }

    private static TerrainTexture Build(params (string Name, byte[] Dds)[] tiles)
        => TerrainTexture.FromTileBytes(tiles.Select(t => (t.Name, t.Dds)), 1024f)!;

    [Fact]
    public void Retail_shaped_tiles_are_left_alone()
    {
        var tt = Build(("tx00x00.dds", DxtEncoder.EncodeDxt1Mipped(Flat(256, 120))),
                       ("tx01x00.dds", DxtEncoder.EncodeDxt1Mipped(Flat(256, 130))));
        Assert.False(tt.HasLegacyTiles);
    }

    [Fact]
    public void A_512_dxt_tile_is_fine_too()
        => Assert.False(Build(("tx00x00.dds", DxtEncoder.EncodeDxt1Mipped(Flat(512, 90)))).HasLegacyTiles);

    [Fact]
    public void The_uncompressed_tiles_the_old_editor_wrote_are_flagged()
    {
        // Exactly what was found in the user's saved map: uncompressed, no mips.
        var tt = Build(("tx00x00.dds", DdsTexture.EncodeUncompressed(Flat(1024, 100))));
        Assert.True(tt.HasLegacyTiles);
    }

    [Fact]
    public void One_bad_tile_among_good_ones_is_enough_to_flag_the_map()
    {
        var tt = Build(("tx00x00.dds", DxtEncoder.EncodeDxt1Mipped(Flat(256, 120))),
                       ("tx01x00.dds", DdsTexture.EncodeUncompressed(Flat(256, 120))));
        Assert.True(tt.HasLegacyTiles);
    }

    [Fact]
    public void A_repaired_map_comes_back_in_the_shipped_shape()
    {
        // The repair path: split the atlas back into tiles and re-encode. A tile that arrived as 1024 uncompressed
        // goes back out at the retail 256, DXT1, with mips - which is what the game reads.
        var tt = Build(("tx00x00.dds", DdsTexture.EncodeUncompressed(Flat(1024, 100))));
        var atlas = tt.BakeAtlas(512);
        var written = tt.SplitToTiles(atlas).ToList();
        Assert.Single(written);
        Assert.Equal(256, written[0].tile.Width);
        var dds = DxtEncoder.EncodeDxt1Mipped(written[0].tile);
        Assert.Equal(43832, dds.Length);                 // byte for byte the size of a retail tile
        Assert.Equal((256, true), DdsTexture.HeaderInfo(dds));
        Assert.False(Build((written[0].fileName, dds)).HasLegacyTiles);
    }
}
