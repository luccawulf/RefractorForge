using System.Text;

namespace RefractorForge.Formats.Online;

/// <summary>
/// <c>ModelCredits.txt</c>, inside the level: one entry per model brought in from a library, naming its author, where
/// it came from and its licence. CC-BY asks for exactly this, Sketchfab's terms ask that the credit follow the model,
/// and even a CC0 model is better with its source written down. The file ships inside the map (the game ignores it),
/// so whoever opens the archive finds it, and the Package Level readme repeats it.
/// </summary>
public static class ModelCredits
{
    public const string FileName = "ModelCredits.txt";
    private const string Marker = "[Template: ";

    private static readonly string Header =
        "Third-party models in this map" + "\r\n" +
        "==============================" + "\r\n" +
        "\r\n" +
        "Brought in with RefractorForge's model browser. Each entry names the model's author, where it came from" + "\r\n" +
        "and its licence; keep this file with the map." + "\r\n";

    /// <summary>One model's entry.</summary>
    public static string Entry(string template, ModelCredit c)
    {
        var sb = new StringBuilder();
        sb.Append(Marker).Append(template).Append(']').Append("\r\n");
        sb.Append('"').Append(c.Title).Append("\" by ").Append(c.Author).Append("\r\n");
        sb.Append("Source:  ").Append(c.Source).Append(" - ").Append(c.PageUrl).Append("\r\n");
        sb.Append("Licence: ").Append(c.License);
        if (!string.IsNullOrEmpty(c.LicenseUrl)) sb.Append(" - ").Append(c.LicenseUrl);
        sb.Append("\r\n");
        if (!string.IsNullOrWhiteSpace(c.Attribution)) sb.Append("Credit:  ").Append(c.Attribution!.Trim()).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>The file with <paramref name="template"/>'s entry added, or replaced if the template is already
    /// listed (a model imported again under the same name). Everything else in the file is kept as it was.</summary>
    public static string Add(string? existing, string template, ModelCredit credit)
    {
        var text = string.IsNullOrWhiteSpace(existing) ? Header : existing!.Replace("\r\n", "\n").Replace("\n", "\r\n");
        var entries = Split(text, out var head);
        entries.RemoveAll(e => e.Template.Equals(template, StringComparison.OrdinalIgnoreCase));
        entries.Add((template, Entry(template, credit)));
        var sb = new StringBuilder(head.TrimEnd('\r', '\n')).Append("\r\n");
        foreach (var (_, body) in entries) sb.Append("\r\n").Append(body.TrimEnd('\r', '\n')).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>Just the entries, without the file's own header: what a readme repeats. Empty when there are none.</summary>
    public static string Body(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var entries = Split(text, out _);
        return string.Join("\r\n", entries.Select(e => e.Body.TrimEnd('\r', '\n') + "\r\n"));
    }

    /// <summary>The templates the file credits, in order.</summary>
    public static IReadOnlyList<string> Templates(string? text) =>
        string.IsNullOrEmpty(text) ? Array.Empty<string>() : Split(text, out _).Select(e => e.Template).ToList();

    private static List<(string Template, string Body)> Split(string text, out string head)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var entries = new List<(string, string)>();
        var headSb = new StringBuilder();
        string? cur = null; var body = new StringBuilder();
        foreach (var line in lines)
        {
            if (line.StartsWith(Marker, StringComparison.Ordinal) && line.TrimEnd().EndsWith(']'))
            {
                if (cur is not null) entries.Add((cur, body.ToString()));
                cur = line.TrimEnd()[Marker.Length..^1];
                body.Clear().Append(line.TrimEnd()).Append("\r\n");
            }
            else if (cur is null) headSb.Append(line).Append("\r\n");
            else body.Append(line).Append("\r\n");
        }
        if (cur is not null) entries.Add((cur, body.ToString()));
        head = headSb.ToString();
        return entries;
    }
}
