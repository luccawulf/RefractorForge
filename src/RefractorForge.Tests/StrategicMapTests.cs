using RefractorForge.Formats.Terrain;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Gates for <see cref="StrategicMap"/> - the <c>Pathfinding/&lt;Veh&gt;.raw</c> companion map.
/// The layout came out of <c>dice::bf::ai::StrategicMap::save</c> in the symbol-bearing Linux dedicated server;
/// these tests hold it to the two things that matter: the accessors mean what the disassembly says, and every
/// real file on this machine round-trips byte-exact (padding crumbs included).
/// </summary>
public class StrategicMapTests
{
    private static IEnumerable<string> RealVehicleMaps()
    {
        foreach (var root in new[]
                 {
                     @"D:\Games\EA GAMES\Battlefield 1942",
                     @"D:\Games\EA GAMES\Battlefield Vietnam",
                 })
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.raw", SearchOption.AllDirectories); }
            catch { continue; }
            foreach (var f in files)
            {
                if (!f.Contains(Path.DirectorySeparatorChar + "Pathfinding" + Path.DirectorySeparatorChar,
                                StringComparison.OrdinalIgnoreCase)) continue;
                var name = Path.GetFileNameWithoutExtension(f);
                // the companions are "<Veh>"; skip "<Veh>Info" and the fine "<Veh>Level<L>Map*" grids
                if (name.EndsWith("Info", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Contains("Level", StringComparison.OrdinalIgnoreCase)) continue;
                yield return f;
            }
        }
    }

    [Fact]
    public void Header_and_cell_stride_match_the_engine_layout()
    {
        var m = new StrategicMap(16, 16);
        var bytes = m.Save();
        Assert.Equal(8 + 16 * 16 * 16, bytes.Length);          // int32 w, int32 h, then 16 bytes per cell
        Assert.Equal(16, BitConverter.ToInt32(bytes, 0));
        Assert.Equal(16, BitConverter.ToInt32(bytes, 4));
        Assert.Equal(16, StrategicMap.StrategicSideFor(1024)); // one strategic cell per 64x64 fine block
        Assert.Equal(32, StrategicMap.StrategicSideFor(2048));
    }

    [Fact]
    public void Portal_accessors_use_the_engine_bit_layout()
    {
        var m = new StrategicMap(4, 4);
        Assert.False(m.IsUsed(1, 2, 0));
        Assert.Equal(0, m.UsedPortals(1, 2));

        m.SetPortal(1, 2, 0, 34, 62);
        m.SetPortal(1, 2, 2, 63, 0, linkBits: 0x55);

        Assert.True(m.IsUsed(1, 2, 0));
        Assert.False(m.IsUsed(1, 2, 1));
        Assert.True(m.IsUsed(1, 2, 2));
        Assert.Equal(2, m.UsedPortals(1, 2));
        Assert.Equal((34, 62), m.Portal(1, 2, 0));
        Assert.Equal((63, 0), m.Portal(1, 2, 2));
        Assert.Equal(0x55, m.LinkBits(1, 2, 2));

        // isUsed(i) is flags & (0x10 << i) -> slots 0 and 2 set the high nibble to 0b0101
        var raw = m.CellBytes(1, 2);
        Assert.Equal(0x50, raw[0x0C]);
        // positions are one byte each at 0x04 + slot*2, masked to 6 bits
        Assert.Equal(34, raw[0x04]);
        Assert.Equal(62, raw[0x05]);

        // a portal's fine-navmap cell is the block origin plus the in-block offset
        Assert.Equal((1 * 64 + 34, 2 * 64 + 62), m.PortalWorldCell(1, 2, 0));

        Assert.Throws<ArgumentOutOfRangeException>(() => m.SetPortal(0, 0, 0, 64, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => m.Portal(0, 0, 4));
    }

    [Fact]
    public void Round_trips_byte_exact_through_load_and_save()
    {
        var m = new StrategicMap(8, 8);
        m.SetPortal(0, 0, 0, 5, 6);
        m.SetPortal(7, 7, 3, 63, 63, linkBits: 0x11);
        var bytes = m.Save();
        Assert.Equal(bytes, StrategicMap.Load(bytes).Save());
    }

    [Fact]
    public void Rejects_files_that_are_not_strategic_maps()
    {
        // the sibling <Veh>Info.raw is a compressed CellMap: same folder, different structure
        var infoHeader = new byte[64];
        foreach (var (i, v) in new[] { (0, 4), (1, 4), (2, 6), (3, 1), (4, 1), (5, 2), (6, 0), (7, -1) })
            BitConverter.GetBytes(v).CopyTo(infoHeader, i * 4);
        Assert.False(StrategicMap.LooksLikeStrategicMap(infoHeader));
        Assert.False(StrategicMap.LooksLikeStrategicMap(new byte[3]));
        Assert.Throws<InvalidDataException>(() => StrategicMap.Load(new byte[] { 1, 2, 3 }));

        var good = new StrategicMap(2, 2).Save();
        Assert.True(StrategicMap.LooksLikeStrategicMap(good));
        var truncated = good[..(good.Length - 16)];
        Assert.Throws<InvalidDataException>(() => StrategicMap.Load(truncated));
    }

    /// <summary>The real gate: every shipped companion map on this machine must parse AND re-serialise to the
    /// identical bytes - which also proves the uninitialised padding is carried through untouched.</summary>
    [Fact]
    public void Every_installed_vehicle_map_round_trips_byte_exact()
    {
        var files = RealVehicleMaps().Take(400).ToList();
        if (files.Count == 0) return;   // no game install on this machine - nothing to prove

        int checkedFiles = 0, portals = 0;
        var failures = new List<string>();
        foreach (var f in files)
        {
            byte[] data;
            try { data = File.ReadAllBytes(f); } catch { continue; }
            if (!StrategicMap.LooksLikeStrategicMap(data)) { failures.Add("not recognised: " + f); continue; }

            var m = StrategicMap.Load(data);
            if (!m.Save().SequenceEqual(data)) { failures.Add("round-trip differs: " + f); continue; }
            checkedFiles++;

            // the strategic grid is the fine navmap divided by 64, so it is square and modest
            Assert.Equal(m.Width, m.Height);
            for (int y = 0; y < m.Height; y++)
                for (int x = 0; x < m.Width; x++)
                    for (int s = 0; s < StrategicMap.PortalSlots; s++)
                        if (m.IsUsed(x, y, s))
                        {
                            var (px, pz) = m.Portal(x, y, s);
                            Assert.InRange(px, 0, StrategicMap.BlockSide - 1);
                            Assert.InRange(pz, 0, StrategicMap.BlockSide - 1);
                            portals++;
                        }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(10)));
        Assert.True(checkedFiles > 0, "found candidate files but parsed none");
        Assert.True(portals > 0, "no populated portals found across " + checkedFiles + " files");
    }
}
