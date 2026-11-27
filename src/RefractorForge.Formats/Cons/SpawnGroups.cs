using System.Globalization;
using System.Text;

namespace RefractorForge.Formats.Con;

/// <summary>
/// Which team each soldier-spawn GROUP belongs to.
///
/// A spawn point (<c>ObjectTemplate.create SpawnPoint</c>) belongs to a group (<c>ObjectTemplate.setGroup N</c>), and
/// a group's team is declared by <c>spawnPointManager.group N</c> / <c>spawnPointManager.groupTeam T</c>. Control
/// points own their group through <c>spawnGroupId</c>, so those groups change hands with the flag. Groups nothing
/// owns keep the team they were declared with - and the spawn points built into ships and carriers are in exactly
/// such groups, declared once for the whole game in <c>game/GlobalSpawnGroups.con</c> (both games ship one: the
/// Enterprise's are team 2, the Shokaku's team 1). A level adds or redeclares groups in each mode's
/// <c>SpawnpointManagerSettings.con</c>, which runs after the global file.
///
/// That is why a ship given to the other team still fills with the old team's soldiers: BF1918 declares the SMS
/// Dresden's group 73 for team 1, whoever's spawner spawned it. Retail's answer is a second ship with its own groups
/// (the "Alternative Fletcher" groups 80/81); <see cref="TeamVehicleCopy"/> builds one for a level.
/// </summary>
public sealed class SpawnGroupTable
{
    /// <summary>One declared group: its team and every other line of its declaration (icon, onlyForHuman...), kept
    /// so a copy can be declared the same way.</summary>
    public sealed class Group
    {
        public int Id { get; init; }
        public int Team { get; set; }
        public string Source { get; set; } = "";
        public List<string> OtherLines { get; } = new();
    }

    private readonly Dictionary<int, Group> _groups = new();
    public IReadOnlyDictionary<int, Group> Groups => _groups;

    /// <summary>Apply a script's declarations over what is already known (later declarations win, as they do when
    /// the engine runs the level's settings after the global file).</summary>
    public SpawnGroupTable Apply(string text, string source)
    {
        Group? cur = null;
        foreach (var raw in ConLines.Split(text ?? ""))
        {
            var t = raw.Trim();
            if (t.Length == 0 || IsComment(t)) continue;
            var p = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var k = p[0].ToLowerInvariant();
            if (!k.StartsWith("spawnpointmanager.")) { cur = null; continue; }
            if (k == "spawnpointmanager.group" && p.Length > 1 && int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            {
                if (!_groups.TryGetValue(id, out cur)) { cur = new Group { Id = id }; _groups[id] = cur; }
                cur.Source = source;
                cur.OtherLines.Clear();
            }
            else if (cur is not null && k == "spawnpointmanager.groupteam" && p.Length > 1 &&
                     int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int team))
                cur.Team = team;
            else if (cur is not null)
                cur.OtherLines.Add(t);
        }
        return this;
    }

    public int? TeamOf(int group) => _groups.TryGetValue(group, out var g) ? g.Team : null;

    private static bool IsComment(string t) =>
        t.StartsWith("rem", StringComparison.OrdinalIgnoreCase) || t.StartsWith("//") || t.StartsWith(';');
}

/// <summary>
/// The object templates a set of <c>.con</c> scripts define, read just far enough to follow a vehicle's parts: each
/// template's type, its <c>addTemplate</c> children, a spawn point's <c>setGroup</c>, a spawner's <c>team</c> and
/// <c>setObjectTemplate</c> vehicles - and where its lines are, so a template can be copied under a new name. The
/// first definition of a name wins, as the mesh library's archives do (highest priority first).
/// </summary>
public sealed class ObjectTemplateIndex
{
    public sealed class Template
    {
        public string Name { get; init; } = "";
        public string Type { get; init; } = "";
        public List<string> Children { get; } = new();
        public int? Group { get; set; }
        public int? Team { get; set; }
        public Dictionary<int, string> SpawnsVehicle { get; } = new();
        /// <summary>Every line that applies to this template: its create block and any later
        /// <c>ObjectTemplate.active</c> block, in file order, the switching lines themselves included.</summary>
        public List<string> Lines { get; } = new();
    }

    private readonly Dictionary<string, Template> _byName = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, Template> Templates => _byName;
    public Template? Get(string name) => _byName.TryGetValue(name, out var t) ? t : null;

    public static ObjectTemplateIndex Build(IEnumerable<(string Source, string Text)> scripts)
    {
        var idx = new ObjectTemplateIndex();
        foreach (var (_, text) in scripts) idx.Add(text);
        return idx;
    }

    /// <summary>Read one script. A name already known is not redefined; an <c>active</c> block for it still adds
    /// its lines (the engine applies those to the one template there is).</summary>
    public void Add(string text)
    {
        Template? cur = null;
        bool curIsNew = false;
        bool inBlockComment = false;
        foreach (var raw in ConLines.Split(text ?? ""))
        {
            var t = raw.Trim();
            if (inBlockComment) { if (t.StartsWith("endrem", StringComparison.OrdinalIgnoreCase)) inBlockComment = false; continue; }
            if (t.StartsWith("beginrem", StringComparison.OrdinalIgnoreCase)) { inBlockComment = true; continue; }
            if (t.Length == 0 || t.StartsWith("rem", StringComparison.OrdinalIgnoreCase)) { if (cur is not null && curIsNew) cur.Lines.Add(raw.TrimEnd()); continue; }
            var p = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var k = p[0].ToLowerInvariant();

            if (k == "objecttemplate.create" && p.Length >= 3)
            {
                if (_byName.TryGetValue(p[2], out var existing)) { cur = null; curIsNew = false; continue; }   // first wins
                cur = new Template { Name = p[2], Type = p[1] };
                _byName[p[2]] = cur;
                curIsNew = true;
                cur.Lines.Add(t);
                continue;
            }
            if ((k == "objecttemplate.active" && p.Length >= 2) || (k == "objecttemplate.activesafe" && p.Length >= 3))
            {
                var name = k == "objecttemplate.active" ? p[1] : p[2];
                cur = _byName.TryGetValue(name, out var a) ? a : null;
                curIsNew = cur is not null;
                cur?.Lines.Add(t);
                continue;
            }
            // Anything that makes another kind of thing active (a geometry, an AI template, networkable info...) or
            // pulls in another file ends the current block.
            if (k.EndsWith(".create") || k.EndsWith(".createnewinfo") || k == "run" || k == "include" ||
                (k.Contains('.') && !k.StartsWith("objecttemplate.")))
            {
                if (k.EndsWith(".create") || k.EndsWith(".createnewinfo") || k == "run" || k == "include") cur = null;
                else if (cur is not null && curIsNew) { /* a foreign property inside the block: leave it with its owner */ }
                continue;
            }
            if (cur is null || !curIsNew) continue;
            cur.Lines.Add(t);
            switch (k)
            {
                case "objecttemplate.addtemplate" when p.Length >= 2: cur.Children.Add(p[1]); break;
                case "objecttemplate.setgroup" when p.Length >= 2 && int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var g): cur.Group = g; break;
                case "objecttemplate.team" when p.Length >= 2 && int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tm): cur.Team = tm; break;
                case "objecttemplate.setobjecttemplate" when p.Length >= 3 && int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var st): cur.SpawnsVehicle[st] = p[2]; break;
            }
        }
    }

    /// <summary>Every template reachable from <paramref name="root"/> through <c>addTemplate</c>, with the chain of
    /// templates that leads to it (root first). Cycles are cut; a template added several times is visited each
    /// time, so the counts are what the vehicle carries.</summary>
    public IEnumerable<(Template T, IReadOnlyList<string> Path)> Walk(string root)
    {
        var start = Get(root);
        if (start is null) yield break;
        var stack = new Stack<(Template, List<string>)>();
        stack.Push((start, new List<string> { start.Name }));
        int guard = 0;
        while (stack.Count > 0 && guard++ < 20000)
        {
            var (t, path) = stack.Pop();
            yield return (t, path);
            for (int i = t.Children.Count - 1; i >= 0; i--)
            {
                var c = Get(t.Children[i]);
                if (c is null || path.Contains(c.Name, StringComparer.OrdinalIgnoreCase)) continue;
                stack.Push((c, new List<string>(path) { c.Name }));
            }
        }
    }
}

/// <summary>A vehicle's built-in soldier spawns or launch spawner whose team is not the team it is spawned for.</summary>
public sealed record SpawnTeamMismatch(string Template, int Count, int Group, int GroupTeam, bool IsSpawner);

/// <summary>Checks a spawned vehicle's built-in spawns against the team that gets the vehicle.</summary>
public static class VehicleSpawnTeams
{
    /// <summary>The built-in spawn points (by group) and launch spawners (by team) of <paramref name="vehicle"/>
    /// that belong to another team than <paramref name="team"/>. A group nothing declares, or a group declared for
    /// team 0, is left alone - its team is not fixed.</summary>
    public static List<SpawnTeamMismatch> Check(ObjectTemplateIndex index, SpawnGroupTable groups, string vehicle, int team)
    {
        var res = new List<SpawnTeamMismatch>();
        if (team is not (1 or 2) || string.IsNullOrWhiteSpace(vehicle)) return res;
        var counts = new Dictionary<(string, bool), (int N, int Group, int GroupTeam)>();
        foreach (var (t, path) in index.Walk(vehicle))
        {
            if (path.Count < 2) continue;   // the vehicle itself
            if (t.Type.Equals("SpawnPoint", StringComparison.OrdinalIgnoreCase))
            {
                int g = t.Group ?? 0;
                if (groups.TeamOf(g) is int gt && gt is 1 or 2 && gt != team)
                {
                    var key = (t.Name, false);
                    counts[key] = counts.TryGetValue(key, out var c) ? (c.N + 1, g, gt) : (1, g, gt);
                }
            }
            else if (t.Type.Equals("ObjectSpawner", StringComparison.OrdinalIgnoreCase) && t.Team is int st && st is 1 or 2 && st != team)
            {
                var key = (t.Name, true);
                counts[key] = counts.TryGetValue(key, out var c) ? (c.N + 1, 0, st) : (1, 0, st);
            }
        }
        foreach (var kv in counts) res.Add(new SpawnTeamMismatch(kv.Key.Item1, kv.Value.N, kv.Value.Group, kv.Value.GroupTeam, kv.Key.Item2));
        return res;
    }
}

/// <summary>Text blocks a gameplay save appends to a mode's file - once: the block's first line is its marker, and a
/// file that already holds the marker is left alone.</summary>
public sealed record GameplayAddition(string FileLeaf, string Marker, string Text);

/// <summary>
/// A copy of a vehicle for one team, for a level: the vehicle's root template and every template on the way to its
/// built-in soldier spawns and launch spawners, under new names, with the spawns moved to new groups declared for
/// that team and the spawners set to it. Every other part (hull, guns, lods, effects) is shared with the original by
/// name, which is how Refractor composes a vehicle anyway - so the copy is a few dozen lines, not the whole ship.
/// This is what DICE did for the second Fletcher on a map ("Alternative Fletcher", groups 80/81).
/// </summary>
public static class TeamVehicleCopy
{
    public sealed record Result(string CopyName, string TemplatesText, string GroupsText, IReadOnlyDictionary<int, int> GroupMap)
    {
        public IEnumerable<GameplayAddition> Additions(string marker) => new[]
        {
            new GameplayAddition("ObjectSpawnTemplates.con", marker, TemplatesText),
            new GameplayAddition("SpawnpointManagerSettings.con", marker, GroupsText),
        };
    }

    public static string CopyNameFor(string vehicle, int team) => $"{vehicle}_T{team}";

    /// <summary>Build the copy, or null when the vehicle has nothing that needs it.</summary>
    /// <param name="usedGroups">Every group id already in use anywhere (global file, level, scripts, control points);
    /// new groups start above them.</param>
    public static Result? Build(ObjectTemplateIndex index, SpawnGroupTable groups, string vehicle, int team, ISet<int> usedGroups, int firstGroup = 200)
    {
        var root = index.Get(vehicle);
        if (root is null || team is not (1 or 2)) return null;
        var mism = VehicleSpawnTeams.Check(index, groups, vehicle, team);
        if (mism.Count == 0) return null;
        string suffix = "_T" + team;

        // Which templates to copy: the mismatched spawns and spawners, and every template on the way to them.
        var bad = new HashSet<string>(mism.Select(m => m.Template), StringComparer.OrdinalIgnoreCase);
        var copy = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.Name };
        foreach (var (t, path) in index.Walk(vehicle))
            if (bad.Contains(t.Name)) foreach (var n in path) copy.Add(n);

        // New groups, one per original group, above everything in use.
        var map = new Dictionary<int, int>();
        int next = Math.Max(firstGroup, usedGroups.Count > 0 ? usedGroups.Max() + 1 : firstGroup);
        foreach (var g in mism.Where(m => !m.IsSpawner).Select(m => m.Group).Distinct().OrderBy(g => g))
        {
            while (usedGroups.Contains(next)) next++;
            map[g] = next++;
        }

        var sb = new StringBuilder();
        var nl = ConLines.NewLine;
        // Root first, then the rest in walk order - Refractor resolves addTemplate by name later, as retail files rely on.
        var order = new List<string> { root.Name };
        foreach (var (t, _) in index.Walk(vehicle))
            if (copy.Contains(t.Name) && !order.Contains(t.Name, StringComparer.OrdinalIgnoreCase)) order.Add(t.Name);
        foreach (var name in order)
        {
            var t = index.Get(name)!;
            sb.Append("rem *** ").Append(name).Append(suffix).Append(" ***").Append(nl);
            // A block's trailing comments introduce the NEXT template in the original file ("rem *** lodShip ***").
            int end = t.Lines.Count;
            while (end > 1 && (t.Lines[end - 1].Trim().Length == 0 || t.Lines[end - 1].TrimStart().StartsWith("rem", StringComparison.OrdinalIgnoreCase))) end--;
            foreach (var line in t.Lines.Take(end))
            {
                var tt = line.Trim();
                if (tt.Length == 0) { sb.Append(nl); continue; }
                var p = tt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                var k = p[0].ToLowerInvariant();
                string outLine = tt;
                if (k == "objecttemplate.create" && p.Length >= 3) outLine = $"{p[0]} {p[1]} {p[2]}{suffix}";
                else if (k == "objecttemplate.active" || k == "objecttemplate.activesafe")
                    continue;   // the copy's create already made it active; its later lines follow straight on
                else if (k == "objecttemplate.addtemplate" && p.Length >= 2 && copy.Contains(p[1]))
                    outLine = $"{p[0]} {p[1]}{suffix}";
                else if (k == "objecttemplate.setgroup" && p.Length >= 2 && int.TryParse(p[1], out var g) && map.TryGetValue(g, out var g2))
                    outLine = $"{p[0]} {g2}";
                else if (k == "objecttemplate.team" && p.Length >= 2 && t.Type.Equals("ObjectSpawner", StringComparison.OrdinalIgnoreCase))
                    outLine = $"{p[0]} {team}";
                sb.Append(outLine).Append(nl);
            }
            sb.Append(nl);
        }

        var gs = new StringBuilder();
        foreach (var (old, nw) in map.OrderBy(kv => kv.Value))
        {
            gs.Append("spawnPointManager.group ").Append(nw).Append(nl);
            gs.Append("spawnPointManager.groupTeam ").Append(team).Append(nl);
            var other = groups.Groups.TryGetValue(old, out var og) ? og.OtherLines : new List<string>();
            if (!other.Any(l => l.StartsWith("spawnPointManager.groupIcon", StringComparison.OrdinalIgnoreCase)))
                gs.Append("spawnPointManager.groupIcon test2.tga").Append(nl);
            foreach (var l in other) gs.Append(l).Append(nl);
            gs.Append(nl);
        }
        return new Result(root.Name + suffix, sb.ToString(), gs.ToString(), map);
    }

    /// <summary>The marker line that opens a copy's blocks: <c>rem</c>, so the engine reads past it.</summary>
    public static string MarkerFor(string copyName) => $"rem RefractorForge team copy: {copyName}";

    /// <summary>Append each addition for <paramref name="fileLeaf"/> whose marker the text does not already hold.</summary>
    public static string AppendAdditions(string text, IEnumerable<GameplayAddition> additions, string fileLeaf)
    {
        var sb = new StringBuilder(text ?? "");
        var nl = ConLines.NewLine;
        foreach (var a in additions)
        {
            if (!a.FileLeaf.Equals(fileLeaf, StringComparison.OrdinalIgnoreCase)) continue;
            if (sb.ToString().Contains(a.Marker, StringComparison.OrdinalIgnoreCase)) continue;
            if (sb.Length > 0 && !sb.ToString().EndsWith("\n")) sb.Append(nl);
            sb.Append(nl).Append(a.Marker).Append(nl).Append(a.Text);
        }
        return sb.ToString();
    }
}
