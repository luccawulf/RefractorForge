using System.Text;
using RefractorForge.Formats;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Rfa;
using RefractorForge.Formats.Terrain;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// CRLF, lone CR and lone LF each end one .con line. Retail scripts are all CRLF, but community maps ship CR-only
/// files that the game reads fine - and a '\n' split read one as a single line, so al_vietnas's hand-edited
/// StaticObjects.con loaded 0 objects. StaticObjectsLineEndingTests covers that file's own round trip; this is the
/// splitter's rules and the whole packed load -> save path with every .con CR-only.
/// </summary>
public class ConLineEndingTests : IDisposable
{
    private readonly string _dir;
    public ConLineEndingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "rfeol_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Theory]
    [InlineData("a\r\nb\r\n", new[] { "a", "b" })]
    [InlineData("a\rb\r", new[] { "a", "b" })]
    [InlineData("a\nb\n", new[] { "a", "b" })]
    [InlineData("a\r\nb\rc\nd", new[] { "a", "b", "c", "d" })]
    [InlineData("a", new[] { "a" })]
    [InlineData("", new string[0])]
    [InlineData("\r\n", new[] { "" })]
    [InlineData("a\r\n\r\nb", new[] { "a", "", "b" })]
    [InlineData("a\n\nb\n", new[] { "a", "", "b" })]
    // A CR-only file's blank line: CRs with no LF after them each end a line.
    [InlineData("a\r\rb\r", new[] { "a", "", "b" })]
    [InlineData("a\r\r", new[] { "a", "" })]
    // The old per-save CR growth: CRs piled up in front of an LF are ONE terminator, which is what mends it.
    [InlineData("rem x\r\r\r\nobject.create y\r\n", new[] { "rem x", "object.create y" })]
    [InlineData("rem x\r\r\r\n\r\r\r\ny", new[] { "rem x", "", "y" })]
    public void Split_ends_a_line_at_CRLF_CR_or_LF(string text, string[] expected)
        => Assert.Equal(expected, ConLines.Split(text));

    [Fact]
    public void Split_never_keeps_a_line_ending_inside_a_line()
    {
        var rng = new Random(1942);
        var pieces = new[] { "a", "rem", " ", "\r", "\n", "\r\n", "\r\r\n" };
        for (int n = 0; n < 2000; n++)
        {
            var sb = new StringBuilder();
            for (int k = rng.Next(0, 20); k > 0; k--) sb.Append(pieces[rng.Next(pieces.Length)]);
            foreach (var line in ConLines.Split(sb.ToString()))
                Assert.True(line.IndexOfAny(new[] { '\r', '\n' }) < 0, $"line ending left in a line of {sb.ToString().Replace("\r", "\\r").Replace("\n", "\\n")}");
        }
    }

    // ---- a whole packed level, every .con CR-only -------------------------------------------------------------

    private const string Prefix = "bfvietnam/levels/CrOnly/";
    private static readonly TerrainConfig Cfg = new() { MaterialSize = 64, WorldSize = 256, YScale = 0.5f, WaterLevel = 30f, SeaFloorLevel = 0f, WaveHeight = 1f };

    private static string StaticObjectsCrlf()
    {
        var sb = new StringBuilder("rem\r\nrem StaticObjects created by Battlecraft Vietnam\r\nrem\r\n\r\n");
        for (int i = 0; i < 400; i++)
            sb.Append(FormattableString.Invariant($"object.create hut_{i % 5}\r\nobject.absolutePosition {i}/20/{i * 2}\r\nobject.rotation {i % 90}/0/0\r\n"));
        return sb.ToString();
    }

    private static string TerrainCrlf() => string.Join("\r\n", Cfg.ToTerrainConLines(@"BfVietnam\levels\CrOnly")) + "\r\n";

    private static readonly string[] Flags = { "AXISBASE_Cpoint", "ALLIESBase_Cpoint", "Village_Cpoint" };

    private static string ControlPointsCrlf()
        => string.Concat(Flags.Select((f, i) => $"rem\r\nObject.create {f}\r\nObject.absolutePosition {100 + i * 50}/33/{200 + i * 40}\r\n"));

    private static string ControlPointTemplatesCrlf()
        => string.Concat(Flags.Select(f => $"ObjectTemplate.create ControlPoint {f}\r\nObjectTemplate.radius 40\r\nObjectTemplate.team 1\r\n\r\n"));

    private string MakeArchive(Func<string, string> endings)
    {
        byte[] B(string crlf) => Encoding.Latin1.GetBytes(endings(crlf));
        var entries = new List<(string, byte[])>
        {
            ($"{Prefix}StaticObjects.con", B(StaticObjectsCrlf())),
            ($"{Prefix}Init/Terrain.con", B(TerrainCrlf())),
            ($"{Prefix}Conquest/ControlPoints.con", B(ControlPointsCrlf())),
            ($"{Prefix}Conquest/ControlPointTemplates.con", B(ControlPointTemplatesCrlf())),
            ($"{Prefix}Heightmap.raw", Enumerable.Range(0, 64 * 64).SelectMany(i => BitConverter.GetBytes((ushort)(i % 500))).ToArray()),
        };
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N")[..6] + ".rfa");
        RefractorFlatArchive.WriteFile(path, entries, compress: false, XPackId.Default);
        return path;
    }

    private static string Entry(string rfa, string suffix)
    {
        var a = new RefractorFlatArchive(rfa);
        return Encoding.Latin1.GetString(a.Read(a.Entries.First(e => e.Name.Replace('\\', '/').EndsWith(suffix, StringComparison.OrdinalIgnoreCase))));
    }

    private static bool HasLoneCr(string s)
    {
        for (int i = 0; i < s.Length; i++)
            if (s[i] == '\r' && (i + 1 == s.Length || s[i + 1] != '\n')) return true;
        return false;
    }

    [Fact]
    public void A_CR_only_packed_level_loads_every_object_flag_and_terrain_setting()
    {
        var level = LevelArchive.FromRfa(MakeArchive(t => t.Replace("\r\n", "\r")));

        Assert.Equal(400, level.StaticObjects.Objects.Count);
        Assert.Equal(64, level.Config.MaterialSize);
        Assert.Equal(256, level.Config.WorldSize);
        Assert.Equal(30f, level.Config.WaterLevel, 3);
        Assert.Equal(3, level.Gameplay.ControlPoints.Count);
    }

    [Fact]
    public void A_zero_edit_repack_of_a_CR_only_level_writes_CRLF_and_loses_nothing()
    {
        var rfa = MakeArchive(t => t.Replace("\r\n", "\r"));
        var level = LevelArchive.FromRfa(rfa);
        var outPath = Path.Combine(_dir, "repacked.rfa");

        LevelSaver.RepackToRfa(rfa, outPath, level.StaticObjects, null, null, new EditableGameplay(level.Gameplay), terrainConfig: level.Config);
        Assert.Null(RefractorFlatArchive.Validate(outPath));

        Assert.Equal(StaticObjectsCrlf(), Entry(outPath, "StaticObjects.con"));
        Assert.Equal(TerrainCrlf(), Entry(outPath, "Init/Terrain.con"));
        foreach (var con in new[] { "Conquest/ControlPoints.con", "Conquest/ControlPointTemplates.con" })
            Assert.False(HasLoneCr(Entry(outPath, con)), $"{con} still has a lone CR after the save");

        var again = LevelArchive.FromRfa(outPath);
        Assert.Equal(400, again.StaticObjects.Objects.Count);
        Assert.Equal(3, again.Gameplay.ControlPoints.Count);
        Assert.Equal(40f, again.Gameplay.ControlPoints[0].Radius, 3);
    }

    [Fact]
    public void A_water_edit_to_a_CR_only_Terrain_con_is_written()
    {
        // Split on '\n', a CR-only Terrain.con was one line, so the patch found no waterLevel line to rewrite.
        var rfa = MakeArchive(t => t.Replace("\r\n", "\r"));
        var level = LevelArchive.FromRfa(rfa);
        level.Config.WaterLevel = 47.5f;
        var outPath = Path.Combine(_dir, "water.rfa");

        LevelSaver.RepackToRfa(rfa, outPath, null, null, null, null, terrainConfig: level.Config);

        var text = Entry(outPath, "Init/Terrain.con");
        Assert.False(HasLoneCr(text));
        Assert.Contains("GeometryTemplate.waterLevel 47.5\r\n", text);
        Assert.Equal(47.5f, LevelArchive.FromRfa(outPath).Config.WaterLevel, 3);
    }

    [Fact]
    public void A_zero_edit_repack_of_a_CRLF_level_is_byte_identical()
    {
        var rfa = MakeArchive(t => t);
        var level = LevelArchive.FromRfa(rfa);
        var outPath = Path.Combine(_dir, "crlf.rfa");

        LevelSaver.RepackToRfa(rfa, outPath, level.StaticObjects, null, null, null, terrainConfig: level.Config);

        Assert.Equal(StaticObjectsCrlf(), Entry(outPath, "StaticObjects.con"));
        Assert.Equal(TerrainCrlf(), Entry(outPath, "Init/Terrain.con"));
    }
}
