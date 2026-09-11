using System.Globalization;
using System.Text;
using RefractorForge.Formats.Rfa;

namespace RefractorForge.Collab;

/// <summary>
/// What a level archive remembers about the server map it is kept in step with: which map, which incarnation of
/// it (<see cref="Epoch"/>), the version it last agreed with (<see cref="Seq"/>), the archive the map was built on,
/// and a hash of every piece of the map as it stood at that version.
///
/// It travels INSIDE the .rfa, as <c>RF_Sync.txt</c> at the level root - a file the game never reads - so the
/// level stays one packed file that can be copied to another PC and still knows where it stands. The hashes are
/// the whole trick: comparing the map now against them says what was changed here since, and comparing the
/// server's map against them says what changed there, without either side keeping a copy of the old map.
/// </summary>
public sealed class SyncRecord
{
    public const string EntryLeaf = "RF_Sync.txt";
    public const string EntryLeafLower = "rf_sync.txt";
    private const string Magic = "RFSYNC 1";

    public string Map { get; set; } = "";
    public string Epoch { get; set; } = "";
    public long Seq { get; set; }
    /// <summary>The archive the server map is built on - what this copy announces to be let in, since its own
    /// bytes stopped matching the moment anything in it was saved.</summary>
    public LevelBase.Id? Base { get; set; }
    /// <summary>Every key's hash at <see cref="Seq"/>, files included.</summary>
    public Dictionary<string, string> Hashes { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Whether this record belongs to that map on that server incarnation.</summary>
    public bool IsFor(string map, string epoch) =>
        Map.Equals(map, StringComparison.OrdinalIgnoreCase) && Epoch == epoch && epoch.Length > 0;

    public string Serialize()
    {
        var sb = new StringBuilder();
        sb.Append(Magic).Append('\n');
        sb.Append("map ").Append(Map).Append('\n');
        sb.Append("epoch ").Append(Epoch).Append('\n');
        sb.Append("seq ").Append(Seq.ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (Base is not null) sb.Append("base ").Append(Base.Encode()).Append('\n');
        foreach (var kv in Hashes.OrderBy(k => k.Key, StringComparer.Ordinal))
            sb.Append("k ").Append(kv.Value).Append(' ').Append(kv.Key).Append('\n');
        return sb.ToString();
    }

    public byte[] ToBytes() => Encoding.UTF8.GetBytes(Serialize());

    /// <summary>Read a record back. Null for anything that is not one - a missing or damaged record means "never
    /// synced", which is a safe thing to fall back to, never a reason to refuse a level.</summary>
    public static SyncRecord? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var lines = text.Replace("\r", "").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != Magic) return null;
        var r = new SyncRecord();
        foreach (var raw in lines.Skip(1))
        {
            if (raw.Length == 0) continue;
            int sp = raw.IndexOf(' ');
            if (sp < 0) continue;
            string tag = raw[..sp], rest = raw[(sp + 1)..];
            switch (tag)
            {
                case "map": r.Map = rest.Trim(); break;
                case "epoch": r.Epoch = rest.Trim(); break;
                case "seq": if (long.TryParse(rest.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) r.Seq = s; break;
                case "base": r.Base = LevelBase.Id.TryDecode(rest.Trim()); break;
                case "k":
                {
                    int sp2 = rest.IndexOf(' ');
                    if (sp2 > 0) r.Hashes[rest[(sp2 + 1)..]] = rest[..sp2];
                    break;
                }
            }
        }
        return r.Map.Length > 0 ? r : null;
    }

    public static SyncRecord? Parse(byte[]? bytes) => bytes is null ? null : Parse(Encoding.UTF8.GetString(bytes));
}

/// <summary>
/// The three-way comparison at the heart of working apart: a BASELINE (the map as both copies last agreed on it),
/// the map HERE, and the map on the SERVER. A key moved on one side only is that side's change, to be carried to
/// the other; moved on both sides to the same value is no change at all; moved on both to different values is a
/// conflict, which only a person can settle.
/// </summary>
public sealed class SyncPlan
{
    public List<string> ServerChanged { get; } = new();
    public List<string> LocalChanged { get; } = new();
    /// <summary>Changed on both sides, differently. Every conflict is also in both lists above.</summary>
    public List<string> Conflicts { get; } = new();

    public bool NothingToDo => ServerChanged.Count == 0 && LocalChanged.Count == 0;

    /// <summary>A missing key is a value in its own right ("no such object"), so a delete is a change like any
    /// other. <paramref name="server"/> should already carry the baseline's value for a file the server does not
    /// list - a server that never received a file still has the one the map was built with.</summary>
    public static SyncPlan Compute(IReadOnlyDictionary<string, string> baseline,
                                   IReadOnlyDictionary<string, string> local,
                                   IReadOnlyDictionary<string, string> server)
    {
        var plan = new SyncPlan();
        var keys = new HashSet<string>(baseline.Keys, StringComparer.Ordinal);
        keys.UnionWith(local.Keys);
        keys.UnionWith(server.Keys);
        foreach (var k in keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            baseline.TryGetValue(k, out var b);
            local.TryGetValue(k, out var l);
            server.TryGetValue(k, out var s);
            bool theirs = s != b, mine = l != b;
            if (theirs && mine && l == s) continue;          // both made the same change: nothing to carry either way
            if (theirs) plan.ServerChanged.Add(k);
            if (mine) plan.LocalChanged.Add(k);
            if (theirs && mine) plan.Conflicts.Add(k);
        }
        return plan;
    }
}

/// <summary>
/// Lining up the objects of a copy that has no ids of its own with the objects of another. A level that was saved
/// before ids were written has objects the server and this editor number differently, and comparing by id would
/// read every one of them as deleted on one side and added on the other. So objects that are plainly the same -
/// same template, same place, same facing - are given the other copy's id first. Anything moved simply fails to
/// match and is carried as a delete plus an add, which lands in the same place.
/// </summary>
public static class SyncObjects
{
    /// <summary>Re-key <paramref name="local"/>'s objects that satisfy <paramref name="needsKey"/> onto the ids of
    /// matching objects in <paramref name="reference"/>. Returns how many were matched.</summary>
    public static int Rekey(Formats.Con.StaticObjectsFile local, Formats.Con.StaticObjectsFile reference,
                            Func<Formats.Con.StaticObject, bool> needsKey)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in local.Objects) if (!needsKey(o)) taken.Add(o.Id);
        var todo = local.Objects.Where(needsKey).ToList();
        var refById = new Dictionary<string, Formats.Con.StaticObject>(StringComparer.Ordinal);
        foreach (var r in reference.Objects) refById.TryAdd(r.Id, r);

        // 1. Same id, same object: nothing to do. This is every object of an untouched map opened fresh, whose
        //    file-order ids are the reference's own.
        var rest = new List<Formats.Con.StaticObject>();
        foreach (var o in todo)
        {
            if (refById.TryGetValue(o.Id, out var r) && Signature(r) == Signature(o) && taken.Add(o.Id)) continue;
            rest.Add(o);
        }

        // 2. The same object under a different id - a file saved before ids existed, whose numbering slid when
        //    something above it was deleted. Matched by what it is.
        var pool = new Dictionary<string, Queue<string>>(StringComparer.Ordinal);
        foreach (var r in reference.Objects)
        {
            if (taken.Contains(r.Id)) continue;
            var k = Signature(r);
            if (!pool.TryGetValue(k, out var q)) pool[k] = q = new Queue<string>();
            q.Enqueue(r.Id);
        }
        int matched = 0;
        var unmatched = new List<Formats.Con.StaticObject>();
        foreach (var o in rest)
        {
            if (pool.TryGetValue(Signature(o), out var q))
            {
                while (q.Count > 0)
                {
                    var id = q.Dequeue();
                    if (!taken.Add(id)) continue;
                    if (o.Id != id) matched++;
                    o.Id = id;
                    goto next;
                }
            }
            unmatched.Add(o);
            next:;
        }

        // 3. What is left was moved, changed or added. It keeps its own id - a moved object stays itself - unless
        //    that id now belongs to another object, in which case it is new.
        foreach (var o in unmatched)
        {
            if (taken.Add(o.Id)) continue;
            o.Id = "n" + Guid.NewGuid().ToString("N")[..12];
            taken.Add(o.Id);
        }
        return matched;
    }

    // A centimetre and a hundredth of a degree: tighter than any hand edit, looser than float noise.
    private static string Signature(Formats.Con.StaticObject o)
        => string.Create(CultureInfo.InvariantCulture,
            $"{o.Template.ToLowerInvariant()}|{R(o.Position.X)}|{R(o.Position.Y)}|{R(o.Position.Z)}|{R(o.Rotation.X)}|{R(o.Rotation.Y)}|{R(o.Rotation.Z)}");

    private static float R(float v) => MathF.Round(v, 2) + 0f;   // + 0f folds -0 into 0
}
