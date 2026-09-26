using System.Text;
using RefractorForge.Formats.Menu;
using RefractorForge.Formats.Rfa;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>The menus' bitmap fonts (<see cref="DifFont"/>): the glyph table of a <c>.dif</c>, read as the engine
/// reads it - every <c>.dif</c> both games ship, in every language, reads with the metrics the menus are laid out by -
/// and written in DICE's FontMakerEx layout, byte for byte as the retail files the tool made.</summary>
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
        Assert.Equal(new[] { 32, 65 }, f.GlyphList.Select(g => g.Code));
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

    /// <summary>A made font writes in the tool's layout - glyphs sorted by code, a repeat dropped, kerning after the
    /// glyphs, fractions kept - and reads back as itself; a name the header cannot hold is refused.</summary>
    [Fact]
    public void A_new_font_writes_in_the_tools_layout_and_reads_back_as_itself()
    {
        var f = DifFont.Create("My Font", 128, 64, 19, new[]
        {
            new DifGlyph(66, 1, 7, 1, 14, 10, 0, 17, 14),
            new DifGlyph(32, 0, 1, 5, 0, 0, 0, 1, 1),
            new DifGlyph(65, 0, 11, 1.5f, 14, 2, 0, 13, 14),
            new DifGlyph(66, 9, 9, 9, 9, 9, 9, 9, 9),
        }, new[] { (65, 86, -1f), (65, 86, -2f), (70, 46, -0.5f) });
        var text = Encoding.Latin1.GetString(f.ToBytes());
        Assert.Equal("header\r\n2\r\nMy Font\r\n128\r\n64\r\n19\r\nglyphs\r\n3\r\n"
                     + "32\t0\t1\t5\t0\t0\t0\t1\t1\r\n65\t0\t11\t1.5\t14\t2\t0\t13\t14\r\n66\t1\t7\t1\t14\t10\t0\t17\t14\r\n"
                     + "kerning\r\n2\r\n65\t86\t-1\r\n70\t46\t-0.5\r\n", text);
        var back = DifFont.Parse(f.ToBytes());
        Assert.Equal(f.GlyphList, back.GlyphList);
        Assert.Equal(f.Kerning, back.Kerning);
        Assert.Equal(("My Font", 128, 64, 19f), (back.Name, back.Width, back.Height, back.LineHeight));

        Assert.Throws<ArgumentException>(() => DifFont.Create("", 64, 64, 10, Array.Empty<DifGlyph>()));
        Assert.Throws<ArgumentException>(() => DifFont.Create(new string('x', 33), 64, 64, 10, Array.Empty<DifGlyph>()));
        Assert.Throws<ArgumentException>(() => DifFont.Create(" lead", 64, 64, 10, Array.Empty<DifGlyph>()));
        Assert.Throws<ArgumentException>(() => DifFont.Create("two\nlines", 64, 64, 10, Array.Empty<DifGlyph>()));
        Assert.Throws<ArgumentException>(() => DifFont.Create("x", 64, 64, 10, new[] { new DifGlyph(0x10000, 0, 1, 0, 0, 0, 0, 1, 1) }));
        Assert.Null(DifFont.NameProblem(new string('x', 32)));
    }

    /// <summary>The atlas half: an 8-bit greyscale TGA, top row first, reads back pixel for pixel.</summary>
    [Fact]
    public void A_grey_atlas_writes_as_the_tools_tga_and_reads_back()
    {
        var grey = new byte[5 * 3];
        for (int i = 0; i < grey.Length; i++) grey[i] = (byte)(i * 17);
        var tga = TgaTexture.EncodeGray8(5, 3, grey);
        Assert.Equal(18 + 15, tga.Length);
        Assert.Equal((3, 8, 0x20), (tga[2], tga[16], tga[17]));
        var t = TgaTexture.Decode(tga)!;
        Assert.Equal((5, 3), (t.Width, t.Height));
        for (int i = 0; i < grey.Length; i++) Assert.Equal(grey[i], t.Rgba[i * 4]);
        Assert.Throws<ArgumentException>(() => TgaTexture.EncodeGray8(5, 3, new byte[14]));
    }

    /// <summary>Every <c>.dif</c> in both games' <c>font.rfa</c> reads; the ones the retail menus name have the line
    /// heights the engine lays text out by (standard6 8, Trebuchet MS8 11, MS11 15, MS14 19, MS18 24) and a glyph for
    /// every printable ASCII character. Every one in FontMakerEx's own layout (not re-saved from a spreadsheet) writes
    /// back byte for byte, and every one writes a file that reads back as the same font.</summary>
    [InstallFact(Installs.Bf1942Clean, Installs.BfvOriginal)]
    public void Every_retail_dif_reads_and_the_tools_own_write_back_byte_for_byte()
    {
        int read = 0, exact = 0;
        foreach (var archive in new[] { Path.Combine(Installs.Bf1942CleanArchives, "font.rfa"), Path.Combine(Installs.BfvOriginalArchives, "font.rfa") })
        {
            if (!File.Exists(archive)) continue;
            var rfa = new RefractorFlatArchive(archive);
            foreach (var e in rfa.Entries.Where(e => e.Name.EndsWith(".dif", StringComparison.OrdinalIgnoreCase)))
            {
                var bytes = rfa.Read(e);
                var f = DifFont.Parse(bytes);
                Assert.True(f.Glyphs.Count > 90, e.Name);
                read++;
                var again = DifFont.Parse(f.ToBytes());
                Assert.Equal(f.GlyphList, again.GlyphList);
                Assert.Equal(f.Kerning, again.Kerning);
                Assert.Equal((f.Name, f.Width, f.Height, f.LineHeight), (again.Name, again.Width, again.Height, again.LineHeight));
                if (bytes.AsSpan().StartsWith("header\r\n"u8))
                {
                    Assert.True(bytes.AsSpan().SequenceEqual(f.ToBytes()), $"{e.Name} does not write back byte for byte");
                    exact++;
                }
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
        Assert.True(exact >= 20, $"{exact} fonts in the tool's layout");
    }
}
