using System.Globalization;
using System.Text;
using System.Text.Json;
using RefractorForge.Formats.Geometry;

namespace RefractorForge.Formats.Editing;

/// <summary>A note pinned to a place in the world - "needs cover here", "this wall is see-through".</summary>
public sealed class Annotation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public Vec3 Position { get; set; }
    public string Text { get; set; } = "";
    public string Author { get; set; } = "";
    public bool Resolved { get; set; }
    public float ColorR { get; set; } = 1f;
    public float ColorG { get; set; } = 0.85f;
    public float ColorB { get; set; } = 0.25f;
    public DateTime Created { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Notes for reviewing someone else's map, synced over collaboration.
///
/// Stored as a sidecar the packer never ships, and carried on the wire as FULL STATE (one ANNOT op holds every
/// note), the same choice the gameplay layer makes: notes are small, and a full-state op can never leave two
/// peers holding different lists.
/// </summary>
public sealed class Annotations
{
    public const string FileName = "RefractorForgeNotes.json";
    public const string Verb = "ANNOT";

    public List<Annotation> Notes { get; set; } = new();

    public static string PathFor(string levelDir) => Path.Combine(levelDir, FileName);

    public static Annotations Load(string levelDir)
    {
        try
        {
            var p = PathFor(levelDir);
            if (File.Exists(p)) return JsonSerializer.Deserialize<Annotations>(File.ReadAllText(p)) ?? new Annotations();
        }
        catch { }
        return new Annotations();
    }

    public void Save(string levelDir)
    {
        var p = PathFor(levelDir);
        if (Notes.Count == 0) { try { if (File.Exists(p)) File.Delete(p); } catch { } return; }
        Directory.CreateDirectory(levelDir);
        File.WriteAllText(p, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    // ---- wire ----

    public string Serialize() => JsonSerializer.Serialize(Notes);

    public void ApplyText(string json)
    {
        try
        {
            var list = JsonSerializer.Deserialize<List<Annotation>>(json);
            if (list is not null) Notes = list;
        }
        catch { }
    }

    /// <summary><c>ANNOT &lt;base64 json&gt;</c> - the op the relay stores and replays to late joiners.</summary>
    public string ToWire() => Verb + " " + Convert.ToBase64String(Encoding.UTF8.GetBytes(Serialize()));

    public static bool TryParseWire(string payload, out string json)
    {
        json = "";
        if (!payload.StartsWith(Verb + " ", StringComparison.Ordinal)) return false;
        try { json = Encoding.UTF8.GetString(Convert.FromBase64String(payload[(Verb.Length + 1)..].Trim())); return true; }
        catch { return false; }
    }

    public Annotation Add(Vec3 pos, string text, string author)
    {
        var a = new Annotation { Position = pos, Text = text, Author = author };
        Notes.Add(a);
        return a;
    }

    public int Open => Notes.Count(n => !n.Resolved);

    public string Describe()
    {
        var sb = new StringBuilder();
        foreach (var n in Notes)
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "[{0}] ({1:0},{2:0},{3:0}) {4}: {5}",
                n.Resolved ? "x" : " ", n.Position.X, n.Position.Y, n.Position.Z, n.Author, n.Text));
        return sb.ToString();
    }
}
