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
}
