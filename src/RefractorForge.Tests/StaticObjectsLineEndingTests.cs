using System.Text;
using RefractorForge.Formats;
using RefractorForge.Formats.Con;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// StaticObjects.con grew one '\r' on every preserved header line, every save. The packed-level reader splits the
/// file on '\n', which leaves each CRLF line ending in '\r'; the header (Battlecraft's "rem StaticObjects created
/// by ..." block and its blank lines) was stored untrimmed, and the saver rejoins with "\r\n" - so "rem x\r" came
/// back as "rem x\r\r\n". echo's al_vietnas had reached fourteen CRs per header line. The same split also left a
/// phantom empty line after the final newline, which became a blank line on the last object: one more per save.
/// </summary>
public class StaticObjectsLineEndingTests
{
    private const string Objects =
        "object.create house_1\r\n" +
        "object.absolutePosition 100.5/12/200.25\r\n" +
        "object.rotation 90/0/0\r\n" +
        "object.create tree_2\r\n" +
        "object.absolutePosition 300/14.75/400\r\n" +
        "object.rotation 0/0/0\r\n" +
        "object.layer 2\r\n";

    private const string CleanHeader =
        "rem\r\n" +
        "rem StaticObjects created by Battlecraft Vietnam\r\n" +
        "rem\r\n" +
        "\r\n";

    // The packed-level read (LevelArchive's own line splitter) and the packed-level write LevelSaver uses.
    private static byte[] Save(byte[] bytes) =>
        LevelSaver.SerializeStaticObjects(StaticObjectsFile.Parse(LevelArchive.Lines(bytes)));

    private static bool HasLoneCr(byte[] b)
    {
        for (int i = 0; i < b.Length; i++)
            if (b[i] == '\r' && (i + 1 == b.Length || b[i + 1] != '\n')) return true;
        return false;
    }

    [Fact]
    public void A_header_already_carrying_extra_CRs_comes_out_as_plain_CRLF_and_stays_that_way()
    {
        var damaged = Encoding.Latin1.GetBytes(CleanHeader.Replace("\r\n", "\r\r\r\n") + Objects);

        var first = Save(damaged);
        var second = Save(first);

        Assert.False(HasLoneCr(first), "the first save still wrote a lone CR");
        Assert.Equal(first, second);
        Assert.Equal(CleanHeader + Objects, Encoding.Latin1.GetString(first));
    }

    [Fact]
    public void A_plain_CRLF_file_round_trips_byte_for_byte()
    {
        var clean = Encoding.Latin1.GetBytes(CleanHeader + Objects);

        var first = Save(clean);
        Assert.Equal(clean, first);
        Assert.Equal(clean, Save(first));
    }

    [Fact]
    public void Header_indentation_is_kept()
    {
        var text = "  rem indented\r\n\trem tabbed\r\n" + Objects;
        Assert.Equal(text, Encoding.Latin1.GetString(Save(Encoding.Latin1.GetBytes(text))));
    }

    // ---- CR-only and mixed line endings ------------------------------------------------------------------------
    //
    // A hand-edited al_vietnas StaticObjects.con (252,846 B) had 12,452 CRs and not one LF, and began with a `rem`.
    // Split on '\n' it was ONE line - a comment - so the editor loaded 0 objects, and a save wrote that back.

    /// <summary>A StaticObjects.con shaped like that file: a rem header, then thousands of objects.</summary>
    private static string BigCrlfFile(int objects)
    {
        var sb = new StringBuilder(CleanHeader);
        for (int i = 0; i < objects; i++)
            sb.Append(FormattableString.Invariant($"object.create tpl_{i % 37}\r\n"))
              .Append(FormattableString.Invariant($"object.absolutePosition {i * 0.5f:0.###}/{12 + i % 9}/{2048 - i * 0.25f:0.###}\r\n"))
              .Append(FormattableString.Invariant($"object.rotation {i % 360}/0/0\r\n"))
              .Append(FormattableString.Invariant($"object.layer {1 + i % 3}\r\n"));
        return sb.ToString();
    }

    [Fact]
    public void A_CR_only_file_loads_every_object()
    {
        const int count = 3000;
        var crlf = BigCrlfFile(count);
        var crOnly = Encoding.Latin1.GetBytes(crlf.Replace("\r\n", "\r"));
        Assert.DoesNotContain((byte)'\n', crOnly);

        var so = StaticObjectsFile.Parse(LevelArchive.Lines(crOnly));

        Assert.Equal(count, so.Objects.Count);
        Assert.Equal(new[] { "rem", "rem StaticObjects created by Battlecraft Vietnam", "rem", "" }, so.Header);
        Assert.Equal("tpl_36", so.Objects[36].Template);
        Assert.Equal(3, so.Objects[2999].Layer);
        Assert.Equal("1499.5/14/1298.25", so.Objects[2999].PositionSource);
    }

    [Fact]
    public void A_CR_only_file_is_saved_as_CRLF_and_then_stays_put()
    {
        var crlf = BigCrlfFile(500);
        var crOnly = Encoding.Latin1.GetBytes(crlf.Replace("\r\n", "\r"));

        var first = Save(crOnly);
        Assert.Equal(crlf, Encoding.Latin1.GetString(first));
        Assert.False(HasLoneCr(first), "the save still wrote a lone CR");
        Assert.Equal(first, Save(first));
    }

    [Fact]
    public void A_CR_only_files_blank_lines_are_kept()
    {
        // In a CR-only file a blank line is two CRs in a row, not a CR-growth artefact - there is no LF to pile onto.
        var crOnly = Encoding.Latin1.GetBytes(CleanHeader.Replace("\r\n", "\r") + Objects.Replace("\r\n", "\r"));
        Assert.Equal(CleanHeader + Objects, Encoding.Latin1.GetString(Save(crOnly)));
    }

    [Fact]
    public void Mixed_line_endings_load_every_object_and_save_as_CRLF()
    {
        // CRLF header, then objects whose lines end in CR, LF and CRLF in turn - and no newline after the last one.
        var lines = (CleanHeader + Objects).Split("\r\n")[..^1];
        var endings = new[] { "\r", "\n", "\r\n" };
        var sb = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
            sb.Append(lines[i]).Append(i < 4 ? "\r\n" : i == lines.Length - 1 ? "" : endings[i % 3]);
        var mixed = Encoding.Latin1.GetBytes(sb.ToString());

        var so = StaticObjectsFile.Parse(LevelArchive.Lines(mixed));
        Assert.Equal(2, so.Objects.Count);
        Assert.Equal("300/14.75/400", so.Objects[1].PositionSource);
        Assert.Equal(2, so.Objects[1].Layer);

        var saved = Save(mixed);
        Assert.Equal(CleanHeader + Objects, Encoding.Latin1.GetString(saved));
        Assert.Equal(saved, Save(saved));
    }

    [Fact]
    public void A_big_CRLF_file_round_trips_byte_for_byte()
    {
        var clean = Encoding.Latin1.GetBytes(BigCrlfFile(3000));
        Assert.Equal(clean, Save(clean));
    }

    [Fact]
    public void A_folder_load_reads_the_same_lines_as_a_packed_one()
    {
        var path = Path.Combine(Path.GetTempPath(), "rf_soeol_" + Guid.NewGuid().ToString("N")[..8] + ".con");
        try
        {
            // CR-only objects under a header still carrying the old CR-growth artefact.
            var bytes = Encoding.Latin1.GetBytes(CleanHeader.Replace("\r\n", "\r\r\r\n") + Objects.Replace("\r\n", "\r"));
            File.WriteAllBytes(path, bytes);

            var folder = StaticObjectsFile.Load(path);
            var packed = StaticObjectsFile.Parse(LevelArchive.Lines(bytes));

            Assert.Equal(2, folder.Objects.Count);
            Assert.Equal(packed.Header, folder.Header);
            Assert.Equal(packed.Write(), folder.Write());
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
