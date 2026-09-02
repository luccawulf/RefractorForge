using System.Text.RegularExpressions;

namespace RefractorForge.Archive;

/// <summary>
/// Colour for Refractor's script dialect: <c>.con</c>, <c>.rs</c>, <c>.ssc</c>, <c>.inc</c>. The grammar is
/// tiny - a comment marker, a handful of control words, dotted <c>Object.property</c> commands, strings and
/// numbers - and colouring it is the difference between a wall of text and something you can scan. Runs once
/// over the whole document into a RichTextBox; documents are a few thousand lines at most.
/// </summary>
public static class ConSyntax
{
    private static readonly HashSet<string> Ext = new(StringComparer.OrdinalIgnoreCase)
        { ".con", ".rs", ".ssc", ".inc", ".sst", ".lst" };

    public static bool Handles(string name) => Ext.Contains(Path.GetExtension(name));

    // One pass, alternation order = priority.
    private static readonly Regex Rx = new(
        @"(?<comment>^\s*rem\b.*$|^\s*beginrem\b|^\s*endrem\b)" +
        @"|(?<string>""[^""\r\n]*"")" +
        @"|(?<run>^\s*(run|include|exec)\b)" +
        @"|(?<kw>^\s*(if|elseIf|else|endIf|while|endWhile|var|const|return|beginEffect|endEffect|subshader)\b)" +
        @"|(?<cmd>\b[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_][A-Za-z0-9_]*)" +
        @"|(?<number>(?<![A-Za-z_])[-+]?\d+(\.\d+)?([eE][-+]?\d+)?(?![A-Za-z_]))",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static void Colorize(RichTextBox box, string text)
    {
        // RichTextBox keeps "\n" alone inside; matching against "\r\n" text would put every colour one
        // character further right per line, drifting across the whole document.
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        box.SuspendLayout();
        box.Clear();
        box.Font = Theme.Mono;
        box.ForeColor = Theme.Text;
        box.Text = text;
        box.SelectAll();
        box.SelectionColor = Theme.Text;

        foreach (Match m in Rx.Matches(text))
        {
            Color c;
            if (m.Groups["comment"].Success) c = Theme.SynComment;
            else if (m.Groups["string"].Success) c = Theme.SynString;
            else if (m.Groups["run"].Success) c = Theme.SynRun;
            else if (m.Groups["kw"].Success) c = Theme.SynKeyword;
            else if (m.Groups["cmd"].Success)
            {
                // "Object.create" - the object half and the property half read differently.
                int dot = m.Value.IndexOf('.');
                box.Select(m.Index, dot); box.SelectionColor = Theme.SynObject;
                box.Select(m.Index + dot + 1, m.Length - dot - 1); box.SelectionColor = Theme.SynProp;
                continue;
            }
            else c = Theme.SynNumber;
            box.Select(m.Index, m.Length);
            box.SelectionColor = c;
        }
        box.Select(0, 0);
        box.ResumeLayout();
    }

    /// <summary>The template a line declares or names, for "go to definition": the last word of an
    /// <c>ObjectTemplate.create Kind Name</c>, or the argument of <c>addTemplate</c>/<c>geometry</c> etc.</summary>
    public static string? TemplateAt(string line)
    {
        var sp = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (sp.Length < 2) return null;
        var head = sp[0].ToLowerInvariant();
        if (head.EndsWith(".create") && sp.Length >= 3) return sp[2];
        if (head.EndsWith(".addtemplate") || head.EndsWith(".geometry") || head.EndsWith(".settemplate")
            || head.EndsWith(".setobjecttemplate") || head.EndsWith(".setnetworkableinfo") || head.EndsWith(".createcomponent"))
            return sp[^1].Trim('"');
        return null;
    }
}
