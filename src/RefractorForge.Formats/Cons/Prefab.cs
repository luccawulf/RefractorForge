using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Geometry;

namespace RefractorForge.Formats.Con;

/// <summary>
/// A reusable group of static objects (a "prefab", à la Battlecraft's <c>.pfa</c> stamps) stored as
/// positions <em>relative</em> to the group's footprint: XZ relative to the centroid, Y relative to the
/// lowest object so the whole cluster plants on the terrain wherever it is stamped. Plain text so it
/// round-trips and is easy to hand-edit.
/// </summary>
public sealed class Prefab
{
    public string Name { get; set; } = "prefab";
    public List<Member> Members { get; } = new();

    /// <summary>One object in the prefab: its template and transform relative to the prefab origin.</summary>
    public readonly record struct Member(string Template, Vec3 Offset, Vec3 Rotation, float Scale);

    /// <summary>Capture a prefab from a set of placed objects, re-basing them to a shared origin.</summary>
    public static Prefab FromObjects(string name, IReadOnlyList<StaticObject> objs)
    {
        var pf = new Prefab { Name = name };
        if (objs.Count == 0) return pf;
        float cx = objs.Average(o => o.Position.X);
        float cz = objs.Average(o => o.Position.Z);
        float cy = objs.Min(o => o.Position.Y);          // base on the lowest object → group sits on the ground
        foreach (var o in objs)
            pf.Members.Add(new Member(o.Template,
                new Vec3(o.Position.X - cx, o.Position.Y - cy, o.Position.Z - cz), o.Rotation, o.Scale ?? 1f));
        return pf;
    }

    public IEnumerable<string> Write()
    {
        yield return "rem RefractorForge prefab";
        yield return $"prefab {Name}";
        foreach (var m in Members)
            yield return $"member {m.Template} {m.Offset} {m.Rotation} {m.Scale.ToString("0.######", CultureInfo.InvariantCulture)}";
    }

    public static Prefab Parse(IEnumerable<string> lines)
    {
        var pf = new Prefab();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("rem", System.StringComparison.OrdinalIgnoreCase) || line.StartsWith("//")) continue;
            var sp = line.IndexOf(' ');
            if (sp < 0) continue;
            var cmd = line[..sp].ToLowerInvariant();
            var rest = line[(sp + 1)..].Trim();
            if (cmd == "prefab") { pf.Name = rest; continue; }
            if (cmd != "member") continue;

            // member <template> <ox/oy/oz> <rx/ry/rz> <scale>
            var tok = rest.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            if (tok.Length < 3) continue;
            var template = tok[0];
            if (!Vec3.TryParse(tok[1], out var off)) continue;
            Vec3.TryParse(tok[2], out var rot);
            float scale = tok.Length >= 4 && float.TryParse(tok[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 1f;
            pf.Members.Add(new Member(template, off, rot, scale));
        }
        return pf;
    }

    public void Save(string path) => File.WriteAllLines(path, Write());
    public static Prefab Load(string path) => Parse(File.ReadLines(path));
}
