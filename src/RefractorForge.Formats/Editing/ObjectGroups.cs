using System.Text.Json;

namespace RefractorForge.Formats.Editing;

/// <summary>
/// Named sets of objects that can be hidden, locked and coloured together - Battlecraft's layers.
///
/// Membership is by object id, not index, so a group survives sorting, undo and collaboration. It is editor
/// data with no meaning to the engine, kept in a sidecar the packer never ships.
/// </summary>
public sealed class ObjectGroup
{
    public string Name { get; set; } = "Group";
    public bool Hidden { get; set; }
    public bool Locked { get; set; }
    public float ColorR { get; set; } = 0.55f;
    public float ColorG { get; set; } = 0.75f;
    public float ColorB { get; set; } = 1.0f;
    public HashSet<string> Ids { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ObjectGroups
{
    public const string FileName = "RefractorForgeGroups.json";

    public List<ObjectGroup> Groups { get; set; } = new();

    public static string PathFor(string levelDir) => Path.Combine(levelDir, FileName);

    public static ObjectGroups Load(string levelDir)
    {
        try
        {
            var p = PathFor(levelDir);
            if (File.Exists(p))
            {
                var g = JsonSerializer.Deserialize<ObjectGroups>(File.ReadAllText(p)) ?? new ObjectGroups();
                // JSON round-trips the set as case-sensitive; restore the comparer ids are matched with.
                foreach (var grp in g.Groups) grp.Ids = new HashSet<string>(grp.Ids, StringComparer.OrdinalIgnoreCase);
                return g;
            }
        }
        catch { }
        return new ObjectGroups();
    }

    public void Save(string levelDir)
    {
        var p = PathFor(levelDir);
        if (Groups.Count == 0) { try { if (File.Exists(p)) File.Delete(p); } catch { } return; }
        Directory.CreateDirectory(levelDir);
        File.WriteAllText(p, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public ObjectGroup Create(string name)
    {
        var g = new ObjectGroup { Name = UniqueName(name) };
        Groups.Add(g);
        return g;
    }

    private string UniqueName(string name)
    {
        if (!Groups.Any(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return name;
        for (int i = 2; ; i++)
        {
            string n = $"{name} {i}";
            if (!Groups.Any(g => g.Name.Equals(n, StringComparison.OrdinalIgnoreCase))) return n;
        }
    }

    /// <summary>An object is hidden if ANY group it belongs to is hidden - the same rule as locked below. One
    /// hidden layer hiding a thing is what a user expects; a thing staying visible because it also happens to be
    /// in a visible group is not.</summary>
    public bool IsHidden(string id) => Groups.Any(g => g.Hidden && g.Ids.Contains(id));
    public bool IsLocked(string id) => Groups.Any(g => g.Locked && g.Ids.Contains(id));

    /// <summary>The colour of the first coloured group an object is in, if any.</summary>
    public (float R, float G, float B)? ColorOf(string id)
    {
        foreach (var g in Groups) if (g.Ids.Contains(id)) return (g.ColorR, g.ColorG, g.ColorB);
        return null;
    }

    public IEnumerable<ObjectGroup> GroupsOf(string id) => Groups.Where(g => g.Ids.Contains(id));

    /// <summary>Every id that is hidden, as one set, for the renderer to skip in one lookup per object.</summary>
    public HashSet<string> HiddenIds()
    {
        var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in Groups) if (g.Hidden) s.UnionWith(g.Ids);
        return s;
    }

    public HashSet<string> LockedIds()
    {
        var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in Groups) if (g.Locked) s.UnionWith(g.Ids);
        return s;
    }

    /// <summary>Drop ids that no longer exist in the level, so deleted objects do not haunt the groups.</summary>
    public int Prune(IEnumerable<string> liveIds)
    {
        var live = new HashSet<string>(liveIds, StringComparer.OrdinalIgnoreCase);
        int removed = 0;
        foreach (var g in Groups) removed += g.Ids.RemoveWhere(id => !live.Contains(id));
        return removed;
    }
}
