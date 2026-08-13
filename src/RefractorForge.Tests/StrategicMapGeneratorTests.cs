using RefractorForge.Formats.Terrain;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Gates for regenerating the strategic pair from an edited navmap. A regenerated pair is NOT byte-identical to a
/// shipped one (DICE's labelling order and the link bytes are not fully decoded), so these assert the properties
/// the two-stage search actually depends on instead: portals sit on passable cells, disconnected areas of a block
/// get separate regions, and every cell's info value indexes a portal slot that is marked used.
/// </summary>
public class StrategicMapGeneratorTests
{
    private static SearchMapParams Land => SearchMapParams.Standard[0];   // Tank0, levels 0,1,2
    private static SearchMapParams Water => SearchMapParams.Standard[2];  // Boat2, levels 2,3,4,5

    private static byte[] AllPassable(int side) => new byte[side * side];    // 0x00 = passable

    [Fact]
    public void Companion_level_follows_the_retail_convention()
    {
        // land ships a level-1 info map, water a level-3 one - the second level of each set
        Assert.Equal(1, StrategicMapGenerator.CompanionLevel(Land));
        Assert.Equal(3, StrategicMapGenerator.CompanionLevel(Water));
    }

    [Fact]
    public void Grid_sizes_match_the_shipped_relationship()
    {
        int fineSide = 1024;
        var r = StrategicMapGenerator.Generate(AllPassable(fineSide), fineSide, Land);

        Assert.Equal(fineSide / 64, r.Table.Width);                  // one strategic cell per 64x64 fine block
        Assert.Equal(r.Table.Width, r.Table.Height);
        int blockSide = StrategicInfoMap.BlockSideFor(StrategicInfoMap.BaseCellBits, r.Level);
        Assert.Equal(r.Table.Width * blockSide, r.InfoSide);         // the invariant the round-trip gate checks
        Assert.Equal(r.InfoSide * r.InfoSide, r.InfoCells.Length);

        // water sits at a coarser level, so its info map is smaller for the same terrain
        var rw = StrategicMapGenerator.Generate(AllPassable(fineSide), fineSide, Water);
        Assert.Equal(fineSide / 64, rw.Table.Width);
        Assert.True(rw.InfoSide < r.InfoSide);
    }

    [Fact]
    public void Open_terrain_gives_every_cell_exactly_one_region()
    {
        int fineSide = 256;
        var r = StrategicMapGenerator.Generate(AllPassable(fineSide), fineSide, Land);
        for (int y = 0; y < r.Table.Height; y++)
            for (int x = 0; x < r.Table.Width; x++)
                Assert.Equal(1, r.Table.UsedPortals(x, y));
        Assert.All(r.InfoCells, c => Assert.Equal(0, c));
    }

    [Fact]
    public void Fully_blocked_terrain_produces_no_portals()
    {
        int fineSide = 128;
        var fine = new byte[fineSide * fineSide];
        Array.Fill(fine, CompressedSearchMap.Blocked);
        var r = StrategicMapGenerator.Generate(fine, fineSide, Land);
        for (int y = 0; y < r.Table.Height; y++)
            for (int x = 0; x < r.Table.Width; x++)
                Assert.Equal(0, r.Table.UsedPortals(x, y));
    }

    [Fact]
    public void A_wall_splits_one_block_into_two_regions()
    {
        // a single 64x64 block, cut in half by a vertical wall -> two disconnected passable areas
        int fineSide = 64;
        var fine = new byte[fineSide * fineSide];
        for (int y = 0; y < fineSide; y++) fine[y * fineSide + 32] = CompressedSearchMap.Blocked;

        var r = StrategicMapGenerator.Generate(fine, fineSide, Land);
        Assert.Equal(1, r.Table.Width);
        Assert.Equal(2, r.Table.UsedPortals(0, 0));

        // the two portals must land on opposite sides of the wall, each on a passable cell
        var (ax, _) = r.Table.Portal(0, 0, 0);
        var (bx, _) = r.Table.Portal(0, 0, 1);
        Assert.NotEqual(ax < 32, bx < 32);
    }

    [Fact]
    public void Every_portal_sits_on_a_passable_cell()
    {
        // a lumpy map: blocked wherever both coordinates share a bit pattern, so blocks fragment variously
        int fineSide = 256;
        var fine = new byte[fineSide * fineSide];
        for (int y = 0; y < fineSide; y++)
            for (int x = 0; x < fineSide; x++)
                if (((x / 7) + (y / 5)) % 3 == 0) fine[y * fineSide + x] = CompressedSearchMap.Blocked;

        var r = StrategicMapGenerator.Generate(fine, fineSide, Land);
        int checkedPortals = 0;
        for (int by = 0; by < r.Table.Height; by++)
            for (int bx = 0; bx < r.Table.Width; bx++)
                for (int s = 0; s < StrategicMap.PortalSlots; s++)
                {
                    if (!r.Table.IsUsed(bx, by, s)) continue;
                    var (fx, fz) = r.Table.PortalWorldCell(bx, by, s);
                    Assert.Equal(CompressedSearchMap.Passable, fine[fz * fineSide + fx]);
                    checkedPortals++;
                }
        Assert.True(checkedPortals > 0, "the fixture produced no portals to check");
    }

    [Fact]
    public void Info_values_always_index_a_used_portal_slot()
    {
        int fineSide = 256;
        var fine = new byte[fineSide * fineSide];
        for (int y = 0; y < fineSide; y++)
            for (int x = 0; x < fineSide; x++)
                if ((x % 23) == 0 || (y % 29) == 0) fine[y * fineSide + x] = CompressedSearchMap.Blocked;

        var r = StrategicMapGenerator.Generate(fine, fineSide, Land);
        int blockSide = StrategicInfoMap.BlockSideFor(StrategicInfoMap.BaseCellBits, r.Level);
        for (int iy = 0; iy < r.InfoSide; iy++)
            for (int ix = 0; ix < r.InfoSide; ix++)
            {
                int v = r.InfoCells[iy * r.InfoSide + ix];
                Assert.InRange(v, 0, StrategicInfoMap.MaxRegion);
                int bx = ix / blockSide, by = iy / blockSide;
                // slot 0 is the fallback for "no region here", so only non-zero values must be backed by a portal
                if (v != 0) Assert.True(r.Table.IsUsed(bx, by, v),
                    $"info cell ({ix},{iy}) names slot {v} but cell ({bx},{by}) does not use it");
            }
    }

    [Fact]
    public void Encoded_companions_are_named_and_shaped_like_the_shipped_pair()
    {
        int fineSide = 256;
        var files = StrategicMapGenerator.EncodeCompanions(Land, AllPassable(fineSide), fineSide);

        Assert.Equal(2, files.Count);
        Assert.Equal("Tank.raw", files[0].FileName);        // BaseName drops the trailing map number
        Assert.Equal("TankInfo.raw", files[1].FileName);

        // both must read back through the codecs the engine's own files go through
        Assert.True(StrategicMap.LooksLikeStrategicMap(files[0].Data));
        var table = StrategicMap.Load(files[0].Data);
        var info = StrategicInfoMap.Decode(files[1].Data, out int infoSide, out int level);

        Assert.Equal(fineSide / 64, table.Width);
        Assert.Equal(1, level);
        Assert.Equal(table.Width * StrategicInfoMap.BlockSideFor(StrategicInfoMap.BaseCellBits, level), infoSide);
        Assert.Equal(infoSide * infoSide, info.Length);

        // and they must survive their own round-trip
        Assert.Equal(files[0].Data, table.Save());
        Assert.Equal(files[1].Data, StrategicInfoMap.Encode(info, infoSide, level));
    }
}
