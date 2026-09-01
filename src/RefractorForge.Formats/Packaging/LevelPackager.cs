using System.IO.Compression;
using System.Text;

namespace RefractorForge.Formats.Packaging;

/// <summary>
/// Everything a release needs, in one zip: the map archive, a server-side copy with the client-only content
/// stripped, the minimap and thumbnail as loose images, and a readme.
///
/// Each of those already existed as a separate action; what people actually forgot was one of them. The
/// wizard hands the caller the pieces it needs generated (the images are rendered by the editor, which owns
/// the terrain textures) and does the assembly and the writing here, where it can be tested.
/// </summary>
public static class LevelPackager
{
    public sealed class Inputs
    {
        public required string LevelName { get; init; }
        public required string ModName { get; init; }
        public required string Game { get; init; }             // "BF1942" | "BFVietnam"
        public string Author { get; init; } = "";
        public string Version { get; init; } = "1.0";
        public string Description { get; init; } = "";
        public required string ClientRfaPath { get; init; }    // the level .rfa (patch or full)
        public string? ServerRfaPath { get; init; }             // SSM copy; null = not included
        public byte[]? MinimapPng { get; init; }
        public byte[]? ThumbnailPng { get; init; }
        public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    }

    /// <summary>The readme, so it can be shown before writing and so it is the same text every time.</summary>
    public static string Readme(Inputs i)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{i.LevelName}");
        sb.AppendLine(new string('=', i.LevelName.Length));
        sb.AppendLine();
        if (i.Author.Length > 0) sb.AppendLine($"Author:   {i.Author}");
        sb.AppendLine($"Version:  {i.Version}");
        sb.AppendLine($"Game:     {(i.Game.Equals("BFVietnam", StringComparison.OrdinalIgnoreCase) ? "Battlefield Vietnam" : "Battlefield 1942")}");
        sb.AppendLine($"Mod:      {i.ModName}");
        sb.AppendLine();
        if (i.Description.Length > 0) { sb.AppendLine(i.Description.Trim()); sb.AppendLine(); }

        sb.AppendLine("INSTALL");
        sb.AppendLine("-------");
        sb.AppendLine($"Copy {Path.GetFileName(i.ClientRfaPath)} to:");
        sb.AppendLine($"    <game folder>\\Mods\\{i.ModName}\\Archives\\bf1942\\levels\\");
        sb.AppendLine();
        if (i.ServerRfaPath is not null)
        {
            sb.AppendLine("DEDICATED SERVER");
            sb.AppendLine("----------------");
            sb.AppendLine($"Servers only need {Path.GetFileName(i.ServerRfaPath)} (client content stripped - it is much smaller).");
            sb.AppendLine("Clients keep the full archive; the server one carries just the gameplay files.");
            sb.AppendLine();
        }
        if (i.Notes.Count > 0)
        {
            sb.AppendLine("NOTES");
            sb.AppendLine("-----");
            foreach (var n in i.Notes) sb.AppendLine("- " + n);
            sb.AppendLine();
        }
        sb.AppendLine("Packaged with RefractorForge.");
        return sb.ToString();
    }

    /// <summary>Write the zip. Returns the entries written, for the report.</summary>
    public static List<(string Entry, long Bytes)> Write(Inputs i, string zipPath)
    {
        var written = new List<(string, long)>();
        if (File.Exists(zipPath)) File.Delete(zipPath);

        string root = SafeName(i.LevelName) + "/";
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        void Put(string entry, byte[] bytes)
        {
            var e = zip.CreateEntry(entry, CompressionLevel.Optimal);
            using var s = e.Open();
            s.Write(bytes, 0, bytes.Length);
            written.Add((entry, bytes.Length));
        }

        Put(root + Path.GetFileName(i.ClientRfaPath), File.ReadAllBytes(i.ClientRfaPath));
        if (i.ServerRfaPath is not null && File.Exists(i.ServerRfaPath))
            Put(root + "server/" + Path.GetFileName(i.ServerRfaPath), File.ReadAllBytes(i.ServerRfaPath));
        if (i.MinimapPng is not null) Put(root + "minimap.png", i.MinimapPng);
        if (i.ThumbnailPng is not null) Put(root + "thumbnail.png", i.ThumbnailPng);
        Put(root + "README.txt", new UTF8Encoding(false).GetBytes(Readme(i)));

        return written;
    }

    private static string SafeName(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s) sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        return sb.Length == 0 ? "level" : sb.ToString();
    }
}
