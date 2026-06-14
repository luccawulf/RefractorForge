using System.Collections.Generic;
using System.Globalization;
using System.IO;
using RefractorForge.Formats.Geometry;

namespace RefractorForge.Formats.Mesh;

/// <summary>One Wavefront material (<c>.mtl</c>) entry: its diffuse colour (<c>Kd</c>) and diffuse texture
/// (<c>map_Kd</c>) — enough to colour + texture an imported mesh and to author its Refractor <c>.rs</c> shader.</summary>
public sealed class ObjMaterial
{
    public string Name = "";
    public Vec3 Diffuse = new(0.8f, 0.8f, 0.8f);
    public string? TextureFile;   // the map_Kd value as written (filename, possibly with a path)

    /// <summary>The texture's base name without extension — the form a Refractor <c>.rs</c> + texture archive use.</summary>
    public string? TextureName => TextureFile is null ? null : Path.GetFileNameWithoutExtension(TextureFile.Replace('\\', '/'));
}

/// <summary>Parses a Wavefront <c>.mtl</c> material library (<c>newmtl</c> / <c>Kd</c> / <c>map_Kd</c>).</summary>
public static class ObjMtl
{
    public static Dictionary<string, ObjMaterial> Parse(string text)
    {
        var map = new Dictionary<string, ObjMaterial>(System.StringComparer.OrdinalIgnoreCase);
        ObjMaterial? cur = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Replace("\r", "").Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var t = line.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 2) continue;
            switch (t[0].ToLowerInvariant())
            {
                case "newmtl": cur = new ObjMaterial { Name = t[1] }; map[t[1]] = cur; break;
                case "kd" when cur is not null && t.Length >= 4: cur.Diffuse = new Vec3(F(t[1]), F(t[2]), F(t[3])); break;
                // map_Kd may carry options (e.g. "-o 1 1 tex.png"); the filename is the last token.
                case "map_kd" when cur is not null: cur.TextureFile = t[t.Length - 1]; break;
            }
        }
        return map;
    }

    public static Dictionary<string, ObjMaterial> Load(string path) => Parse(File.ReadAllText(path));

    private static float F(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0.8f;
}
