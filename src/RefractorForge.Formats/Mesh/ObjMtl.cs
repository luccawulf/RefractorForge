using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using RefractorForge.Formats.Geometry;

namespace RefractorForge.Formats.Mesh;

/// <summary>One Wavefront material (<c>.mtl</c>) entry: its diffuse colour (<c>Kd</c>) and diffuse texture
/// (<c>map_Kd</c>) — enough to colour + texture an imported mesh and to author its Refractor <c>.rs</c> shader.</summary>
public sealed class ObjMaterial
{
    public string Name = "";
    public Vec3 Diffuse = new(0.8f, 0.8f, 0.8f);
    public string? TextureFile;   // the map_Kd path as written (relative or absolute; spaces and all)

    /// <summary>The texture's base name without extension — the form a Refractor <c>.rs</c> + texture archive use.</summary>
    public string? TextureName => TextureFile is null ? null : Path.GetFileNameWithoutExtension(TextureFile.Replace('\\', '/'));
}

/// <summary>Parses a Wavefront <c>.mtl</c> material library (<c>newmtl</c> / <c>Kd</c> / <c>map_Kd</c>), and finds
/// the texture files it names — which, for a model that came from someone else's machine, are rarely where the
/// file says they are.</summary>
public static class ObjMtl
{
    public static Dictionary<string, ObjMaterial> Parse(string text)
    {
        var map = new Dictionary<string, ObjMaterial>(StringComparer.OrdinalIgnoreCase);
        ObjMaterial? cur = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Replace("\r", "").Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var t = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 2) continue;
            switch (t[0].ToLowerInvariant())
            {
                case "newmtl": cur = new ObjMaterial { Name = t[1] }; map[t[1]] = cur; break;
                case "kd" when cur is not null && t.Length >= 4: cur.Diffuse = new Vec3(F(t[1]), F(t[2]), F(t[3])); break;
                case "map_kd" when cur is not null: cur.TextureFile = MapFileName(t); break;
            }
        }
        return map;
    }

    public static Dictionary<string, ObjMaterial> Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>
    /// The file a <c>map_Kd</c> line names. Options come first (<c>-o 1 1 tex.png</c>, <c>-bm 0.5</c>, <c>-clamp on</c>)
    /// and each takes a known number of values; everything after them is the path — and a path may contain spaces
    /// (<c>map_Kd D:/Jose Bronze/Documents/car.jpg</c>), so it is the REST of the line, not the last token.
    /// </summary>
    public static string MapFileName(string[] tokens)
    {
        int i = 1;
        while (i < tokens.Length && tokens[i].StartsWith('-') && tokens[i].Length > 1 && !char.IsDigit(tokens[i][1]))
        {
            int args = tokens[i].ToLowerInvariant() switch
            {
                "-o" or "-s" or "-t" => 3,          // u v w
                "-mm" => 2,                          // base gain
                "-blendu" or "-blendv" or "-clamp" or "-bm" or "-imfchan" or "-texres" or "-boost" or "-cc" => 1,
                _ => 1,
            };
            // The u/v/w options may carry fewer than three numbers; stop at the first non-number.
            int taken = 0;
            i++;
            while (taken < args && i < tokens.Length && (args == 1 || float.TryParse(tokens[i], NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            { i++; taken++; }
        }
        return i < tokens.Length ? string.Join(" ", tokens.Skip(i)) : "";
    }

    /// <summary>Extensions Blender and the editor can both read; searched in this order when the named file is
    /// gone but a sibling in another format is there (a pack that shipped .png where the .mtl says .tga).</summary>
    public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".tga", ".dds", ".bmp", ".tif", ".tiff" };

    /// <summary>
    /// Where the texture a <c>.mtl</c> names actually is, or null. Tried in order: the path as written (relative
    /// to the model, or absolute); the bare file name beside the model; the bare file name in any subfolder up to
    /// three deep (<c>textures/</c>, <c>maps/</c>, the model's own folder); and the same stem in any other image
    /// format. Names are matched without regard to case, since the path came from somebody else's machine.
    /// </summary>
    public static string? ResolveTexture(string modelDir, string? mapKd)
    {
        if (string.IsNullOrWhiteSpace(mapKd)) return null;
        string given = mapKd.Trim().Replace('\\', '/');
        try
        {
            string direct = Path.IsPathRooted(given) ? given : Path.Combine(modelDir, given);
            if (File.Exists(direct)) return Path.GetFullPath(direct);
        }
        catch { }

        string file = Path.GetFileName(given);
        if (file.Length == 0) return null;
        string stem = Path.GetFileNameWithoutExtension(file);
        string? byName = null, byStem = null;
        int stemRank = int.MaxValue;
        try
        {
            foreach (var path in Walk(modelDir, 3))
            {
                string n = Path.GetFileName(path);
                if (byName is null && n.Equals(file, StringComparison.OrdinalIgnoreCase)) { byName = path; break; }
                if (Path.GetFileNameWithoutExtension(n).Equals(stem, StringComparison.OrdinalIgnoreCase))
                {
                    int rank = Array.FindIndex(ImageExtensions, e => e.Equals(Path.GetExtension(n), StringComparison.OrdinalIgnoreCase));
                    if (rank >= 0 && rank < stemRank) { stemRank = rank; byStem = path; }
                }
            }
        }
        catch { }
        return byName ?? byStem;
    }

    private static IEnumerable<string> Walk(string dir, int depth)
    {
        if (!Directory.Exists(dir)) yield break;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(dir); } catch { yield break; }
        foreach (var f in files) yield return f;
        if (depth <= 0) yield break;
        IEnumerable<string> subs;
        try { subs = Directory.EnumerateDirectories(dir); } catch { yield break; }
        foreach (var d in subs)
            foreach (var f in Walk(d, depth - 1)) yield return f;
    }

    private static float F(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0.8f;
}
