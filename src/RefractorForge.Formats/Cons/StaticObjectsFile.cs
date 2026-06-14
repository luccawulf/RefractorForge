using System.Globalization;
using RefractorForge.Formats.Geometry;

namespace RefractorForge.Formats.Con;

/// <summary>
/// A parsed StaticObjects.con file. The object list is a plain <see cref="List{T}"/>
/// with NO cap — this is the whole point. Battlecraft's 1024/2048 walls were a property
/// of its in-memory level struct, not of this file format, which is just text.
/// </summary>
public sealed class StaticObjectsFile
{
    /// <summary>All placed objects, in file order. No limit.</summary>
    public List<StaticObject> Objects { get; } = new();

    /// <summary>Lines that appear before the first <c>object.create</c> (comments, etc.), preserved.</summary>
    public List<string> Header { get; } = new();

    public StaticObject? FindById(string id)
    {
        foreach (var o in Objects) if (o.Id == id) return o;
        return null;
    }

    /// <summary>Deep copy including ids and source text (used to sync a collaborator's starting state).</summary>
    public StaticObjectsFile Clone()
    {
        var f = new StaticObjectsFile();
        f.Header.AddRange(Header);
        foreach (var o in Objects) f.Objects.Add(o.Clone());
        return f;
    }

    public static StaticObjectsFile Load(string path) => Parse(File.ReadLines(path));

    public void Save(string path) => File.WriteAllLines(path, Write());

    public static StaticObjectsFile Parse(IEnumerable<string> lines)
    {
        var file = new StaticObjectsFile();
        StaticObject? current = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();

            // Blank or comment lines: keep in header if no object yet, else attach to current object.
            if (line.Length == 0 || IsComment(line))
            {
                if (current is null) file.Header.Add(raw);
                else current.ExtraLines.Add(line);
                continue;
            }

            var (command, rest) = SplitCommand(line);
            var cmd = command.ToLowerInvariant();

            switch (cmd)
            {
                case "object.create":
                    current = new StaticObject(Unquote(rest));
                    file.Objects.Add(current);
                    break;

                case "object.absoluteposition":
                    if (current is not null && Vec3.TryParse(rest, out var pos)) current.InitPosition(pos, rest);
                    break;

                case "object.rotation":
                    if (current is not null && Vec3.TryParse(rest, out var rot)) current.InitRotation(rot, rest);
                    break;

                case "object.layer":
                    if (current is not null && int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layer))
                        current.Layer = layer;
                    break;

                case "object.geometry.scale":
                case "object.scale":
                    if (current is not null && float.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale))
                        current.InitScale(scale, rest);
                    break;

                default:
                    // Unknown object.* (or anything attached to an object) -> preserve verbatim.
                    if (current is not null) current.ExtraLines.Add(line);
                    else file.Header.Add(raw);
                    break;
            }
        }

        return file;
    }

    public IEnumerable<string> Write()
    {
        foreach (var h in Header) yield return h;

        foreach (var o in Objects)
        {
            yield return $"object.create {o.Template}";
            yield return $"object.absolutePosition {o.PositionSource ?? o.Position.ToString()}";
            yield return $"object.rotation {o.RotationSource ?? o.Rotation.ToString()}";
            if (o.Layer is int l) yield return $"object.layer {l}";
            if (o.Scale is float s)
                yield return $"object.geometry.scale {o.ScaleSource ?? s.ToString("0.######", CultureInfo.InvariantCulture)}";
            foreach (var extra in o.ExtraLines) yield return extra;
        }
    }

    private static bool IsComment(string line) =>
        line.StartsWith("rem", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("//") || line.StartsWith(';');

    private static (string command, string rest) SplitCommand(string line)
    {
        int sp = line.IndexOf(' ');
        return sp < 0 ? (line, "") : (line[..sp], line[(sp + 1)..].Trim());
    }

    private static string Unquote(string s) =>
        s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;
}
