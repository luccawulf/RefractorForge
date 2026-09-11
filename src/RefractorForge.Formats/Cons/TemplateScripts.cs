using System;
using System.Collections.Generic;
using System.Linq;

namespace RefractorForge.Formats.Con;

/// <summary>
/// Every template definition in a set of <c>.con</c> scripts, read the way the ENGINE reads them, and kept verbatim
/// so a definition can be copied line for line.
///
/// <para>Three things a simpler reader gets wrong, each of which would corrupt a copy:</para>
/// <list type="bullet">
/// <item><b>Comments.</b> <c>rem</c> lines, and whole <c>beginrem</c> … <c>endrem</c> regions. Retail's own ammo
/// box (<c>Objects_US/O_USAmmo_M1/Objects.con</c>) ends with a <c>beginrem</c> block holding two commented-out
/// <c>ObjectTemplate.create SupplyDepot</c> definitions. Treat those as live and the ammo box "has" templates it
/// does not; cut a copy at one of them and the copy carries an unterminated <c>beginrem</c> that comments out
/// everything written after it.</item>
/// <item><b>Families.</b> Each <c>*Template</c> family has its own active template. A
/// <c>LodSelectorTemplate.create</c> in the middle of a Bundle does not end the Bundle - the next
/// <c>ObjectTemplate.*</c> line still applies to it.</item>
/// <item><b><c>.active</c>.</b> A later <c>ObjectTemplate.active X</c> adds to X, wherever it is.</item>
/// </list>
/// </summary>
public sealed class TemplateScripts
{
    /// <summary>One template: its family, the type it was created with, and every body line that applies to it,
    /// in script order, verbatim (whitespace trimmed, comments dropped, create/active headers excluded).</summary>
    public sealed class Def
    {
        public string Family { get; init; } = "";
        public string Type { get; internal set; } = "";
        public string Name { get; init; } = "";
        public string Source { get; internal set; } = "";
        public List<string> Lines { get; } = new();
        /// <summary>How many <c>create</c>s named it. More than one with different bodies is a definition the engine
        /// resolves in a way nobody here has established, so a copy of it cannot be called faithful.</summary>
        public int Creates { get; internal set; }
        public bool ConflictingCreates { get; internal set; }
    }

    private readonly Dictionary<(string Family, string Name), Def> _defs = new(new KeyComparer());

    private sealed class KeyComparer : IEqualityComparer<(string Family, string Name)>
    {
        public bool Equals((string Family, string Name) a, (string Family, string Name) b)
            => string.Equals(a.Family, b.Family, StringComparison.OrdinalIgnoreCase)
               && string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Family, string Name) k)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(k.Family),
                                StringComparer.OrdinalIgnoreCase.GetHashCode(k.Name));
    }

    public Def? Find(string family, string name)
        => _defs.TryGetValue((family, name), out var d) ? d : null;

    public Def? Object(string name) => Find("ObjectTemplate", name);
    public Def? Geometry(string name) => Find("GeometryTemplate", name);

    public bool Knows(string family, string name) => _defs.ContainsKey((family, name));

    /// <summary>Read scripts in the order the engine runs them (mod archives, then the level).</summary>
    public static TemplateScripts Parse(IEnumerable<(string Source, string Text)> scripts)
    {
        var ts = new TemplateScripts();
        foreach (var (source, text) in scripts) ts.Add(source, text);
        return ts;
    }

    /// <summary>Add one more script - a level's queued files, say - on top of what is already known.</summary>
    public void Add(string source, string text)
    {
        var active = new Dictionary<string, Def>(StringComparer.OrdinalIgnoreCase);
        // A re-create's body is collected separately and compared with the first when the block ends.
        var recreated = new Dictionary<Def, List<string>>();
        bool inBlockComment = false;

        void CloseRecreate(Def d)
        {
            if (!recreated.Remove(d, out var body)) return;
            if (!body.SequenceEqual(d.Lines, StringComparer.OrdinalIgnoreCase)) d.ConflictingCreates = true;
        }

        foreach (var raw in (text ?? "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (inBlockComment)
            {
                if (line.StartsWith("endrem", StringComparison.OrdinalIgnoreCase)) inBlockComment = false;
                continue;
            }
            if (line.StartsWith("beginrem", StringComparison.OrdinalIgnoreCase)) { inBlockComment = true; continue; }
            if (IsRem(line)) continue;

            int dot = line.IndexOf('.');
            if (dot <= 0) continue;
            string family = line[..dot];
            if (!family.EndsWith("Template", StringComparison.OrdinalIgnoreCase)) continue;
            family = Canonical(family);
            var (cmd, arg) = Split(line[(dot + 1)..]);

            if (cmd.Equals("create", StringComparison.OrdinalIgnoreCase))
            {
                var t = arg.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length < 2) { active.Remove(family); continue; }
                if (active.TryGetValue(family, out var prev)) CloseRecreate(prev);
                var key = (family, t[1]);
                if (_defs.TryGetValue(key, out var existing))
                {
                    existing.Creates++;
                    if (!existing.Type.Equals(t[0], StringComparison.OrdinalIgnoreCase)) existing.ConflictingCreates = true;
                    recreated[existing] = new List<string>();
                    active[family] = existing;
                }
                else
                {
                    var d = new Def { Family = family, Type = t[0], Name = t[1], Source = source, Creates = 1 };
                    _defs[key] = d;
                    active[family] = d;
                }
            }
            else if (cmd.Equals("active", StringComparison.OrdinalIgnoreCase))
            {
                if (active.TryGetValue(family, out var prev)) CloseRecreate(prev);
                string name = arg.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                if (name.Length == 0) { active.Remove(family); continue; }
                var key = (family, name);
                if (!_defs.TryGetValue(key, out var d))
                    _defs[key] = d = new Def { Family = family, Name = name, Source = source };
                active[family] = d;
            }
            else if (active.TryGetValue(family, out var cur))
            {
                if (recreated.TryGetValue(cur, out var body)) body.Add(line);
                else cur.Lines.Add(line);
            }
        }
        foreach (var d in recreated.Keys.ToList()) CloseRecreate(d);
    }

    /// <summary><c>rem</c> alone or followed by whitespace - not a command that merely starts with the letters.</summary>
    public static bool IsRem(string line)
        => line.StartsWith("rem", StringComparison.OrdinalIgnoreCase)
           && (line.Length == 3 || char.IsWhiteSpace(line[3]));

    /// <summary><c>ObjectTemplate.geometry X</c> -> ("geometry", "X").</summary>
    public static (string Cmd, string Arg) Command(string line)
    {
        int dot = line.IndexOf('.');
        return dot < 0 ? ("", "") : Split(line[(dot + 1)..]);
    }

    private static (string, string) Split(string rest)
    {
        int sp = rest.IndexOfAny(new[] { ' ', '\t' });
        return sp < 0 ? (rest, "") : (rest[..sp], rest[(sp + 1)..].Trim());
    }

    /// <summary>First whitespace-separated token of an argument - a template name, with any trailing value dropped.</summary>
    public static string FirstToken(string arg)
        => arg.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

    // Retail spells the family both ways ("objectTemplate.cullRadiusScale"); the engine does not care, so neither do we.
    private static string Canonical(string family)
    {
        if (family.Equals("ObjectTemplate", StringComparison.OrdinalIgnoreCase)) return "ObjectTemplate";
        if (family.Equals("GeometryTemplate", StringComparison.OrdinalIgnoreCase)) return "GeometryTemplate";
        return family;
    }
}
