using System.Text;

namespace RefractorBridge.Con;

/// <summary>One source line and the exact bytes that ended it.</summary>
public readonly record struct ConLine(string Content, string Terminator)
{
    public override string ToString() => Content + Terminator;
}

/// <summary>
/// Splits a .con file into lines WITH their terminators, so a rewrite that changes nothing reproduces the file
/// byte for byte. That no-op guarantee is the gate on the whole converter: if a transform cannot leave a file
/// alone, it cannot be trusted to change one.
///
/// Formats.Con.ConLines already settled what ends a line in these files (CRLF, a lone CR, a lone LF; a RUN of CRs
/// ending in LF is one terminator; a run of CRs not followed by LF is a CR-only file's blank lines). The rules
/// here are deliberately identical - only the terminator is kept rather than discarded, which ConLines.Split
/// cannot do. Community maps really do ship CR-only .con files and the game reads them, so this is not academic.
/// </summary>
public static class ConText
{
    public static List<ConLine> Split(byte[] latin1Bytes) => Split(Encoding.Latin1.GetString(latin1Bytes));

    public static List<ConLine> Split(string text)
    {
        var lines = new List<ConLine>();
        int start = 0, i = 0, n = text.Length;

        while (i < n)
        {
            char c = text[i];
            if (c == '\n') { lines.Add(new ConLine(text[start..i], "\n")); start = ++i; continue; }
            if (c != '\r') { i++; continue; }

            int run = i;
            while (i < n && text[i] == '\r') i++;

            if (i < n && text[i] == '\n')
            {
                i++;                                                   // CRLF, or CRs piled up in front of one LF
                lines.Add(new ConLine(text[start..run], text[run..i]));
            }
            else
            {
                lines.Add(new ConLine(text[start..run], "\r"));         // lone CRs: each ends a line of its own
                for (int k = run + 1; k < i; k++) lines.Add(new ConLine("", "\r"));
            }
            start = i;
        }

        if (start < n) lines.Add(new ConLine(text[start..], ""));       // a final line with nothing ending it
        return lines;
    }

    /// <summary>Reassemble. <c>Join(Split(x)) == x</c> for every input, which the tests gate.</summary>
    public static string Join(IEnumerable<ConLine> lines)
    {
        var sb = new StringBuilder();
        foreach (var l in lines) sb.Append(l.Content).Append(l.Terminator);
        return sb.ToString();
    }

    public static byte[] JoinBytes(IEnumerable<ConLine> lines) => Encoding.Latin1.GetBytes(Join(lines));
}
