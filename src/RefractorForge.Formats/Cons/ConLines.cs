using System.Text;

namespace RefractorForge.Formats.Con;

/// <summary>
/// A .con file's lines, whatever ends them. Retail BFV/BF1942 scripts are all CRLF, but community maps ship CR-only
/// files - al_vietnas has shipped a hand-edited StaticObjects.con with 12,452 CRs and not one LF, twice - and the game
/// reads them. Splitting on '\n' alone turned such a file into ONE line beginning with its first <c>rem</c>, so the
/// editor loaded no objects at all, and a save would then have written the level an empty StaticObjects.con.
///
/// CRLF, a lone CR and a lone LF each end one line. A RUN of CRs that ends in an LF is also one terminator: that is
/// the shape the old per-save CR growth left behind (<c>"rem x\r\r\r\n"</c>, see StaticObjectsLineEndingTests), and
/// reading it as one line is what mends it. A run of CRs NOT followed by an LF is a CR-only file's blank lines, so
/// each of those CRs ends a line of its own. A file that ends in a terminator has no phantom empty line after it.
/// </summary>
public static class ConLines
{
    /// <summary>The engine's own line ending, and the only one the savers write.</summary>
    public const string NewLine = "\r\n";

    public static string[] Split(byte[] latin1Bytes) => Split(Encoding.Latin1.GetString(latin1Bytes));

    public static string[] Split(string text)
    {
        var lines = new List<string>();
        int start = 0, i = 0, n = text.Length;
        while (i < n)
        {
            char c = text[i];
            if (c == '\n') { lines.Add(text[start..i]); start = ++i; continue; }
            if (c != '\r') { i++; continue; }

            int run = i;
            while (i < n && text[i] == '\r') i++;
            lines.Add(text[start..run]);
            if (i < n && text[i] == '\n') i++;                        // CRLF, or CRs piled up in front of one
            else for (int k = run + 1; k < i; k++) lines.Add("");      // lone CRs: each is a line of its own
            start = i;
        }
        if (start < n) lines.Add(text[start..]);
        return lines.ToArray();
    }

    /// <summary>True when the text's last line is terminated (by CR or LF), so a rewrite can keep that.</summary>
    public static bool EndsWithTerminator(string text) => text.Length > 0 && text[^1] is '\r' or '\n';
}
