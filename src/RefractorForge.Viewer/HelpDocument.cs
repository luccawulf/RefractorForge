using System.Text.RegularExpressions;

namespace RefractorForge.Viewer;

/// <summary>
/// USER_GUIDE.md as the Help window draws it. The guide is Markdown written for GitHub, and the window used to print
/// it raw: every heading read "## 5. ...", every bold word kept its asterisks, a table was a wall of pipes, and the
/// guide's arrows and dashes came out as '?' because the editor's font carries Latin glyphs only. This reads it into
/// blocks - headings, paragraphs, bullets, numbered steps, quotes, tables, code, rules - with the inline marks taken
/// out and the symbols the font lacks spelled in ASCII. A paragraph or list item that the source hard-wraps is joined
/// into one line, so it wraps to the window rather than to the width of the file.
/// </summary>
public static class HelpDocument
{
    public enum Kind { Heading, Paragraph, Bullet, Numbered, Quote, Table, Code, Rule, Blank }

    /// <summary>One block. <see cref="Level"/> is a heading's level (1-6) or a list item's nesting depth;
    /// <see cref="Marker"/> a numbered step's "1."; <see cref="Rows"/> a table's cells, header row first.</summary>
    public sealed record Block(Kind Kind, string Text, int Level = 0, string Marker = "", IReadOnlyList<string[]>? Rows = null);

    /// <summary>The guide's symbols the editor's Latin font cannot draw, in ASCII.</summary>
    public static string Ascii(string s) => s
        .Replace("▸", ">").Replace("→", "->").Replace("←", "<-").Replace("—", "-").Replace("–", "-")
        .Replace("…", "...").Replace("’", "'").Replace("‘", "'").Replace("“", "\"").Replace("”", "\"")
        .Replace("≤", "<=").Replace("≥", ">=").Replace("≈", "~").Replace("•", "-").Replace("−", "-")
        .Replace(" ", " ");

    private static readonly Regex LinkRx = new(@"\[([^\]]+)\]\(([^)]+)\)");
    private static readonly Regex BoldRx = new(@"\*\*(.+?)\*\*");
    private static readonly Regex ItalicRx = new(@"(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?![\w*])");

    /// <summary>A line's text with the inline Markdown taken out: links keep their text, bold, italics and code
    /// lose their marks.</summary>
    public static string Inline(string s)
    {
        s = LinkRx.Replace(s, "$1");
        s = BoldRx.Replace(s, "$1");
        s = ItalicRx.Replace(s, "$1");
        s = s.Replace("`", "");
        return Ascii(s);
    }

    private static readonly Regex HeadingRx = new(@"^(#{1,6})\s+(.*)$");
    private static readonly Regex BulletRx = new(@"^(\s*)[-*+]\s+(.*)$");
    private static readonly Regex NumberedRx = new(@"^(\s*)(\d+)\.\s+(.*)$");
    private static readonly Regex RuleRx = new(@"^\s*(-{3,}|\*{3,}|_{3,})\s*$");
    private static readonly Regex TableSepRx = new(@"^:?-{3,}:?$");

    private static bool IsStructural(string line) =>
        line.TrimStart().StartsWith("```", StringComparison.Ordinal) || HeadingRx.IsMatch(line) || RuleRx.IsMatch(line)
        || line.TrimStart().StartsWith("|", StringComparison.Ordinal) || line.StartsWith(">", StringComparison.Ordinal)
        || BulletRx.IsMatch(line) || NumberedRx.IsMatch(line);

    private static int Indent(string line) => line.Length - line.TrimStart(' ').Length;

    public static List<Block> Parse(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var blocks = new List<Block>();
        void Blank() { if (blocks.Count > 0 && blocks[^1].Kind != Kind.Blank) blocks.Add(new Block(Kind.Blank, "")); }

        int i = 0;
        while (i < lines.Length)
        {
            var line = lines[i].TrimEnd();
            if (line.Trim().Length == 0) { Blank(); i++; continue; }

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal); i++) code.Add(Ascii(lines[i].TrimEnd()));
                i++;   // the closing fence
                blocks.Add(new Block(Kind.Code, string.Join("\n", code)));
                continue;
            }
            if (HeadingRx.Match(line) is { Success: true } h)
            {
                blocks.Add(new Block(Kind.Heading, Inline(h.Groups[2].Value.Trim()), h.Groups[1].Value.Length));
                i++; continue;
            }
            if (RuleRx.IsMatch(line)) { blocks.Add(new Block(Kind.Rule, "")); i++; continue; }
            if (line.TrimStart().StartsWith("|", StringComparison.Ordinal))
            {
                var rows = new List<string[]>();
                for (; i < lines.Length && lines[i].TrimStart().StartsWith("|", StringComparison.Ordinal); i++)
                {
                    var cells = lines[i].Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
                    if (cells.All(c => TableSepRx.IsMatch(c))) continue;   // the |---|---| line under the header
                    rows.Add(cells.Select(Inline).ToArray());
                }
                if (rows.Count > 0) blocks.Add(new Block(Kind.Table, "", Rows: rows));
                continue;
            }
            if (line.StartsWith(">", StringComparison.Ordinal))
            {
                var parts = new List<string>();
                for (; i < lines.Length && lines[i].StartsWith(">", StringComparison.Ordinal); i++) parts.Add(lines[i].TrimStart('>').Trim());
                blocks.Add(new Block(Kind.Quote, Inline(string.Join(" ", parts.Where(p => p.Length > 0)))));
                continue;
            }

            // A list item, with the lines under it that are indented past its marker.
            var num = NumberedRx.Match(line);
            var bul = num.Success ? Match.Empty : BulletRx.Match(line);
            if (num.Success || bul.Success)
            {
                int ind = Indent(line);
                string text = num.Success ? num.Groups[3].Value : bul.Groups[2].Value;
                var sb = new List<string> { text.Trim() };
                for (i++; i < lines.Length; i++)
                {
                    var next = lines[i].TrimEnd();
                    if (next.Trim().Length == 0 || Indent(next) <= ind || IsStructural(next)) break;
                    sb.Add(next.Trim());
                }
                int level = num.Success ? ind / 3 : ind / 2;
                blocks.Add(num.Success
                    ? new Block(Kind.Numbered, Inline(string.Join(" ", sb)), level, num.Groups[2].Value + ".")
                    : new Block(Kind.Bullet, Inline(string.Join(" ", sb)), level));
                continue;
            }

            // A paragraph: every following line up to a blank one or the next structure.
            var para = new List<string> { line.Trim() };
            for (i++; i < lines.Length; i++)
            {
                var next = lines[i].TrimEnd();
                if (next.Trim().Length == 0 || IsStructural(next)) break;
                para.Add(next.Trim());
            }
            blocks.Add(new Block(Kind.Paragraph, Inline(string.Join(" ", para))));
        }
        while (blocks.Count > 0 && blocks[^1].Kind == Kind.Blank) blocks.RemoveAt(blocks.Count - 1);
        return blocks;
    }
}
