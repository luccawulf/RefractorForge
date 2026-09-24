using RefractorForge.Formats;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// <c>lexiconAll.dat</c> (<see cref="Lexicon"/>): every retail lexicon of both games comes back byte-exact, a vehicle's
/// name is found under its template whatever the case, a new lexicon is written in the retail layout, and the
/// community files that are not - a size table a column short, a file cut short - are read past or refused, never
/// misread.
/// </summary>
public class LexiconTests
{
    private const string Bf1942Mods = Installs.Bf1942Clean + @"\Mods";
    private const string BfvMods = Installs.BfvOriginal + @"\Mods";

    /// <summary>The main install: the clean one plus 100-odd community mods, many with their own lexicon.</summary>
    private const string CommunityMods = @"D:\Games\EA GAMES\Battlefield 1942\Mods";

    [InstallFact(Installs.Bf1942Clean)]
    public void Every_retail_lexicon_round_trips_byte_exact()
    {
        foreach (var (mod, rows, languages) in new[]
                 {
                     (Bf1942Mods + @"\bf1942", 1692, Lexicon.Bf1942Languages),
                     (Bf1942Mods + @"\XPack1", 125, Lexicon.Bf1942Languages),
                     (Bf1942Mods + @"\XPack2", 379, Lexicon.Bf1942Languages.Append("Notes").ToList()),
                     (BfvMods + @"\BfVietnam", 1467, Lexicon.BfvLanguages),
                 })
        {
            var path = Path.Combine(mod, Lexicon.FileName);
            if (!File.Exists(path)) continue;
            var bytes = File.ReadAllBytes(path);
            var lex = Lexicon.Read(bytes);
            Assert.True(lex.SizeTableMatches, mod);
            Assert.Equal(rows, lex.Rows.Count);
            Assert.Equal(new[] { Lexicon.KeyTitle }.Concat(languages), lex.Header);
            Assert.Equal(bytes, lex.ToBytes());
        }
    }

    /// <summary>A vehicle's name is its template's key; retail writes the key in its own case (BFV lower-cases).</summary>
    [InstallFact(Installs.Bf1942Clean, Installs.BfvOriginal)]
    public void A_vehicle_name_is_found_under_its_template_case_ignored()
    {
        Assert.Equal("Type 38", Lexicon.Read(Path.Combine(Bf1942Mods, "bf1942", Lexicon.FileName)).Text("type38"));
        Assert.Equal("M8 Greyhound", Lexicon.Read(Path.Combine(Bf1942Mods, "XPack2", Lexicon.FileName)).Text("Greyhound"));
        var bfv = Lexicon.Read(Path.Combine(BfvMods, "BfVietnam", Lexicon.FileName));
        Assert.Equal("Huey Slick", bfv.Text("UH1Transport"));
        Assert.Equal("Huey-Helikopter", bfv.Text("UH1Transport", "german"));      // retail is translated
        Assert.Null(bfv.Text("UH1Transport", "Klingon"));
        Assert.Null(bfv.Text("RdkBoxHeliA"));
    }

    [Fact]
    public void A_new_lexicon_is_written_in_the_retail_layout()
    {
        var lex = Lexicon.For(isVietnam: false);
        lex.Set("RdkJeep", "Jeep Renegade");
        lex.Set("RdkJeep", "German", "Jeep Renegade (DE)");
        lex.Set("RdkTruck", "German", "Lastwagen");                     // a new key: every language
        var bytes = lex.ToBytes();

        Assert.Equal(3u, BitConverter.ToUInt32(bytes, 0));                 // the header row counts
        Assert.Equal(9u, BitConverter.ToUInt32(bytes, 4));
        var table = Enumerable.Range(0, 9).Select(c => BitConverter.ToUInt32(bytes, bytes.Length - 36 + c * 4)).ToArray();
        Assert.Equal((uint)("LANGUAGE".Length + 1 + "RdkJeep".Length + 1 + "RdkTruck".Length + 1), table[0]);   // characters
        Assert.Equal((uint)(("English".Length + 1 + "Jeep Renegade".Length + 1 + "Lastwagen".Length + 1) * 2), table[1]);   // bytes

        var back = Lexicon.Read(bytes);
        Assert.True(back.SizeTableMatches);
        Assert.Equal("Jeep Renegade (DE)", back.Text("rdkjeep", "German"));
        Assert.Equal("Jeep Renegade", back.Text("RdkJeep", "French"));
        Assert.Equal("Lastwagen", back.Text("RdkTruck"));
        Assert.Equal(bytes, back.ToBytes());
        Assert.Equal(1, back.Remove("RDKJEEP"));
        Assert.Null(back.Text("RdkJeep"));
        Assert.Throws<ArgumentException>(() => back.Set("Bad", "a\0b"));
        Assert.Throws<ArgumentException>(() => back.Set("", "empty"));
        Assert.Equal(new[] { "LANGUAGE", "English", "French", "Italian", "Spanish", "German", "Japanese", "Chinese", "Korean" }, Lexicon.For(isVietnam: true).Header);
    }

    /// <summary>What the code units are, they stay: a stray surrogate half is not "repaired" on the way through.</summary>
    [Fact]
    public void Text_comes_back_code_unit_for_code_unit()
    {
        var lex = new Lexicon(new[] { "English" });
        lex.Set("Odd", "half \uD800 a pair, then \U0001F600 and\nsecond line");
        var bytes = lex.ToBytes();
        Assert.Equal("half \uD800 a pair, then \U0001F600 and\nsecond line", Lexicon.Read(bytes).Text("Odd"));
        Assert.Equal(bytes, Lexicon.Read(bytes).ToBytes());
    }

    /// <summary>A size table a column short (nineteen community lexicons, FHR's among them) is read past, and the
    /// file written back whole.</summary>
    [Fact]
    public void A_short_size_table_is_read_past_and_written_whole()
    {
        var lex = Lexicon.For(isVietnam: false);
        lex.Set("Key", "Text");
        var whole = lex.ToBytes();
        var shorter = whole[..^4];
        var back = Lexicon.Read(shorter);
        Assert.False(back.SizeTableMatches);
        Assert.Equal("Text", back.Text("Key", "Chinese"));
        Assert.Equal(whole, back.ToBytes());
    }

    [Fact]
    public void A_file_cut_short_or_not_a_lexicon_is_refused()
    {
        var lex = Lexicon.For(isVietnam: false);
        lex.Set("Key", "Text");
        var whole = lex.ToBytes();
        var cut = Assert.Throws<InvalidDataException>(() => Lexicon.Read(whole[..60]));
        Assert.Contains("cut short", cut.Message);
        Assert.Throws<InvalidDataException>(() => Lexicon.Read(new byte[] { 1, 2, 3 }));
        Assert.Throws<InvalidDataException>(() => Lexicon.Read(System.Text.Encoding.ASCII.GetBytes("game.addModPath Mods/bf1942/\r\n")));
        Assert.Throws<InvalidDataException>(() => Lexicon.Read(new byte[16]));
    }

    /// <summary>Every lexicon the community mods of the main install carry either reads - and writes back the rows it
    /// read - or is refused as not a lexicon or cut short; nothing else is thrown.</summary>
    [InstallFact(CommunityMods)]
    public void Community_lexicons_read_or_are_refused_cleanly()
    {
        int read = 0, exact = 0, refused = 0;
        foreach (var path in Directory.EnumerateFiles(CommunityMods, Lexicon.FileName, SearchOption.AllDirectories)
                                      .Where(p => Path.GetDirectoryName(Path.GetDirectoryName(p)) is { } d && d.Equals(CommunityMods, StringComparison.OrdinalIgnoreCase)))
        {
            var bytes = File.ReadAllBytes(path);
            Lexicon lex;
            try { lex = Lexicon.Read(bytes); }
            catch (InvalidDataException) { refused++; continue; }
            read++;
            var again = Lexicon.Read(lex.ToBytes());
            Assert.True(again.SizeTableMatches, path);
            Assert.Equal(lex.Rows.Count, again.Rows.Count);
            Assert.Equal(lex.Rows.Select(r => string.Join("\u0001", r)), again.Rows.Select(r => string.Join("\u0001", r)));
            if (lex.SizeTableMatches && lex.ToBytes().AsSpan().SequenceEqual(bytes)) exact++;
        }
        Assert.True(read > 0);
        Assert.True(exact * 10 >= read * 7, $"{exact} of {read} exact");
        Assert.True(refused * 20 <= read + refused, $"{refused} refused of {read + refused}");
    }
}
