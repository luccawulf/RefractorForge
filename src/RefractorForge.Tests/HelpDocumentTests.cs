using RefractorForge.Viewer;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The in-editor Help window draws USER_GUIDE.md through HelpDocument. The editor's English font has Latin glyphs
/// only (Basic Latin + Latin-1), so every character the window is handed must be one of those - the guide's arrows
/// and dashes used to come out as '?' - and no Markdown mark may be left showing.
/// </summary>
public class HelpDocumentTests
{
    private static string Guide()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "RefractorForge.sln"))) d = d.Parent;
        Assert.NotNull(d);
        return File.ReadAllText(Path.Combine(d!.FullName, "USER_GUIDE.md"));
    }

    private static IEnumerable<string> Texts(IEnumerable<HelpDocument.Block> blocks)
    {
        foreach (var b in blocks)
        {
            yield return b.Text;
            yield return b.Marker;
            if (b.Rows is not null) foreach (var r in b.Rows) foreach (var c in r) yield return c;
        }
    }

    [Fact]
    public void The_user_guide_reads_into_sections_the_font_can_draw()
    {
        var doc = HelpDocument.Parse(Guide());
        var h2 = doc.Where(b => b.Kind == HelpDocument.Kind.Heading && b.Level == 2).Select(b => b.Text).ToList();
        Assert.Contains(h2, t => t.StartsWith("5. Gameplay editing"));
        Assert.Contains(h2, t => t.StartsWith("12. Keyboard reference"));
        var h3 = doc.Where(b => b.Kind == HelpDocument.Kind.Heading && b.Level == 3).Select(b => b.Text).ToList();
        foreach (var want in new[] { "Game modes", "When a ship or carrier spawns the wrong team", "Pathmaps on bridges",
                                     "Collision meshes and AI meshes", "Copy Level To", "Overgrowth and undergrowth", "Free models from Poly Haven" })
            Assert.Contains(h3, t => t.StartsWith(want));
        Assert.Contains(doc, b => b.Kind == HelpDocument.Kind.Table);
        Assert.Contains(doc, b => b.Kind == HelpDocument.Kind.Numbered);

        foreach (var t in Texts(doc))
        {
            Assert.DoesNotContain("**", t);
            Assert.DoesNotContain("`", t);
            Assert.True(t.All(c => c == '\n' || (c >= ' ' && c <= '~') || (c >= '¡' && c <= 'ÿ')),
                        "a character the Latin font cannot draw: " + t);
        }
    }

    [Fact]
    public void Wrapped_list_items_and_paragraphs_are_joined_and_marks_removed()
    {
        var doc = HelpDocument.Parse(
            "## Title\n\n- **Bold** item that\n  continues here\n  - nested *one*\n1. Step one\n   still step one\n\n" +
            "A paragraph\nover two lines with `code` and [a link](http://x).\n\n| A | B |\n|---|---|\n| **x** | y |\n\n```\nraw *text*\n```\n");
        Assert.Equal(new[] { HelpDocument.Kind.Heading, HelpDocument.Kind.Blank, HelpDocument.Kind.Bullet, HelpDocument.Kind.Bullet,
                             HelpDocument.Kind.Numbered, HelpDocument.Kind.Blank, HelpDocument.Kind.Paragraph, HelpDocument.Kind.Blank,
                             HelpDocument.Kind.Table, HelpDocument.Kind.Blank, HelpDocument.Kind.Code }, doc.Select(b => b.Kind));
        Assert.Equal("Bold item that continues here", doc[2].Text);
        Assert.Equal(("nested one", 1), (doc[3].Text, doc[3].Level));
        Assert.Equal(("Step one still step one", "1."), (doc[4].Text, doc[4].Marker));
        Assert.Equal("A paragraph over two lines with code and a link.", doc[6].Text);
        Assert.Equal(2, doc[8].Rows!.Count);
        Assert.Equal(new[] { "x", "y" }, doc[8].Rows![1]);
        Assert.Equal("raw *text*", doc[10].Text);   // code is shown as written
        Assert.Equal("F1-F6 > Layers -> on", HelpDocument.Inline("F1–F6 ▸ Layers → on"));
    }
}
