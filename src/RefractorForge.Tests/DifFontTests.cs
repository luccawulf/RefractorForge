using System.Text;
using RefractorForge.Formats.Menu;
using RefractorForge.Formats.Rfa;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>The menus' bitmap fonts (<see cref="DifFont"/>): the glyph table of a <c>.dif</c>, read as the engine
/// reads it - every <c>.dif</c> both games ship, in every language, reads with the metrics the menus are laid out by.</summary>
public class DifFontTests
{
    [Fact]
    public void A_dif_reads_its_header_glyphs_and_kerning_and_keeps_the_first_of_a_repeat()
    {
        var text = "header\r\n2\r\nTrebuchet MS\r\n256\r\n128\r\n19\r\n"
                 + "kerning\r\n2\r\n65 86 -1.5\r\n65 86 -3\r\n"
                 + "glyphs\r\n3\r\n"
                 + "32\t0\t1\t2\t0\t0\t0\t1\t1\r\n"
                 + "65\t0\t9\t1\t14\t10\t0\t19\t18\r\n"
                 + "65\t5\t5\t5\t5\t5\t5\t5\t5\r\n";
        var f = DifFont.Parse(Encoding.Latin1.GetBytes(text));
        Assert.Equal(2, f.Version);
        Assert.Equal("Trebuchet MS", f.Name);
        Assert.Equal((256, 128), (f.Width, f.Height));
        Assert.Equal(19f, f.LineHeight);
        Assert.Equal(2, f.Glyphs.Count);
        var a = f.Glyph('A')!;
        Assert.Equal(10f, a.Advance);
        Assert.Equal(18, a.Height);
        Assert.Equal((10, 0, 19, 18), (a.X0, a.Y0, a.X1, a.Y1));
        Assert.Equal(-1.5f, f.Kerning[(65, 86)]);
        Assert.Null(f.Glyph('B'));
    }

    [Fact]
    public void A_version_1_header_has_a_square_atlas_and_a_file_without_a_header_is_refused()
    {
        var f = DifFont.Parse(Encoding.Latin1.GetBytes("header 1 old font\n64 12 glyphs 0"));
        Assert.Equal((64, 64), (f.Width, f.Height));
        Assert.Equal(12f, f.LineHeight);
        Assert.Throws<InvalidDataException>(() => DifFont.Parse(Encoding.Latin1.GetBytes("glyphs 0")));
        Assert.Throws<InvalidDataException>(() => DifFont.Parse(Encoding.Latin1.GetBytes("header 2 x\n10 10 1 glyphs 1 65 a b c 0 0 0 1 1")));
    }

    /// <summary>Every <c>.dif</c> in both games' <c>font.rfa</c> reads; the ones the retail menus name have the line
    /// heights the engine lays text out by (standard6 8, Trebuchet MS8 11, MS11 15, MS14 19, MS18 24) and a glyph for
    /// every printable ASCII character.</summary>
    [InstallFact(Installs.Bf1942Clean, Installs.BfvOriginal)]
    public void Every_retail_dif_reads()
    {
        int read = 0;
        foreach (var archive in new[] { Path.Combine(Installs.Bf1942CleanArchives, "font.rfa"), Path.Combine(Installs.BfvOriginalArchives, "font.rfa") })
        {
            if (!File.Exists(archive)) continue;
            var rfa = new RefractorFlatArchive(archive);
            foreach (var e in rfa.Entries.Where(e => e.Name.EndsWith(".dif", StringComparison.OrdinalIgnoreCase)))
            {
                var f = DifFont.Parse(rfa.Read(e));
                Assert.True(f.Glyphs.Count > 90, e.Name);
                read++;
                var leaf = Path.GetFileName(e.Name.Replace('\\', '/'));
                bool root = e.Name.Replace('\\', '/').Count(c => c == '/') == 1;
                if (!root) continue;
                float? expected = leaf switch
                {
                    "standard6.dif" => 8, "Trebuchet MS8.dif" => 11, "Trebuchet MS11.dif" => 15, "Trebuchet MS14.dif" => 19, "Trebuchet MS18.dif" => 24, _ => null,
                };
                if (expected is { } lh) Assert.Equal(lh, f.LineHeight);
                for (int c = 32; c < 127; c++) Assert.True(f.Glyph(c) is not null, $"{e.Name} has no glyph {(char)c}");
            }
        }
        Assert.True(read >= 20, $"{read} fonts read");
    }
}
