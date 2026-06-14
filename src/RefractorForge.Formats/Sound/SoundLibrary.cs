using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RefractorForge.Formats.Sound;

/// <summary>One placeable sound emitter: the object template name placed in StaticObjects.con (e.g. "Frogs"),
/// the <c>.ssc</c> script it loads, and the parsed/editable script itself.</summary>
public sealed class SoundEmitter
{
    public string Template { get; init; } = "";
    public string SscName { get; init; } = "";        // e.g. "Frogs.ssc"
    public string? SscPath { get; set; }              // full path on disk (folder levels); null for archive-loaded
    public SoundScript? Script { get; set; }          // null if the .ssc couldn't be found
    public bool Dirty { get; set; }                   // edited in the editor -> needs save

    /// <summary>Audible radius hint for the editor (the script's minDistance, with a sane floor).</summary>
    public float MinDistance => Script is null ? 10f : System.Math.Max(1f, Script.MinDistance);
}

/// <summary>
/// The sound layer of a level: scans <c>Sounds/*.con</c> for <c>SimpleObject</c> templates that
/// <c>loadSoundScript</c> a <c>.ssc</c>, loads each script, and maps placement template name -&gt; emitter.
/// Lets the editor recognise which placed objects are sound emitters, show their audible radius, and edit
/// their <c>.ssc</c> properties.
/// </summary>
public sealed class SoundLibrary
{
    private readonly Dictionary<string, SoundEmitter> _byTemplate = new(System.StringComparer.OrdinalIgnoreCase);

    public static SoundLibrary Empty { get; } = new();

    public IReadOnlyCollection<SoundEmitter> Emitters => _byTemplate.Values;
    public IReadOnlyList<string> TemplateNames => _byTemplate.Keys.OrderBy(k => k, System.StringComparer.OrdinalIgnoreCase).ToList();
    public int Count => _byTemplate.Count;
    public bool IsSound(string template) => template is not null && _byTemplate.ContainsKey(template);
    public SoundEmitter? Get(string template) => template is not null && _byTemplate.TryGetValue(template, out var e) ? e : null;
    public bool AnyDirty => _byTemplate.Values.Any(e => e.Dirty);

    /// <summary>Build from already-read text: each Sounds/*.con body, plus a map of .ssc filename -&gt; bytes.
    /// Loader-agnostic so both the folder loader and the .rfa loader can use it.</summary>
    public static SoundLibrary FromTexts(IEnumerable<string> conTexts, IReadOnlyDictionary<string, byte[]> sscByName)
    {
        var lib = new SoundLibrary();
        foreach (var con in conTexts)
            foreach (var (template, sscName) in ParseTemplateMap(con))
            {
                if (lib._byTemplate.ContainsKey(template)) continue;
                SoundScript? script = null;
                if (TryGetIgnoreCase(sscByName, sscName, out var bytes)) script = SoundScript.Parse(bytes);
                lib._byTemplate[template] = new SoundEmitter { Template = template, SscName = sscName, Script = script };
            }
        return lib;
    }

    /// <summary>Scan an extracted level folder's <c>Sounds/</c> directory.</summary>
    public static SoundLibrary LoadFolder(string levelDir)
    {
        var soundsDir = Directory.Exists(Path.Combine(levelDir, "Sounds"))
            ? Path.Combine(levelDir, "Sounds")
            : Directory.EnumerateDirectories(levelDir, "Sounds", SearchOption.AllDirectories).FirstOrDefault();
        if (soundsDir is null) return Empty;

        var conTexts = Directory.EnumerateFiles(soundsDir, "*.con").Select(File.ReadAllText).ToList();
        var sscPaths = Directory.EnumerateFiles(soundsDir, "*.ssc")
            .ToDictionary(p => Path.GetFileName(p), p => p, System.StringComparer.OrdinalIgnoreCase);
        var sscBytes = sscPaths.ToDictionary(kv => kv.Key, kv => File.ReadAllBytes(kv.Value), System.StringComparer.OrdinalIgnoreCase);

        var lib = FromTexts(conTexts, sscBytes);
        foreach (var e in lib._byTemplate.Values)
            if (sscPaths.TryGetValue(e.SscName, out var p)) e.SscPath = p;   // remember disk path for saving
        return lib;
    }

    /// <summary>Write every edited script back to its <see cref="SoundEmitter.SscPath"/>. Returns paths written.</summary>
    public List<string> SaveDirty()
    {
        var written = new List<string>();
        foreach (var e in _byTemplate.Values)
            if (e.Dirty && e.Script is not null && e.SscPath is not null)
            { File.WriteAllBytes(e.SscPath, e.Script.ToBytes()); e.Dirty = false; written.Add(e.SscPath); }
        return written;
    }

    /// <summary>Edited (.ssc name -&gt; bytes) pairs, for injecting into a repack/patch .rfa save.</summary>
    public List<(string Name, byte[] Bytes)> DirtyScripts()
    {
        var list = new List<(string, byte[])>();
        foreach (var e in _byTemplate.Values)
            if (e.Dirty && e.Script is not null) list.Add((e.SscName, e.Script.ToBytes()));
        return list;
    }

    /// <summary>Clear the dirty flag on every emitter (after their scripts were written via a repack/patch).</summary>
    public void MarkAllSaved() { foreach (var e in _byTemplate.Values) e.Dirty = false; }

    // Walk a Sounds/*.con: every "ObjectTemplate.create <type> <name>" arms <name>; a following
    // "ObjectTemplate.loadSoundScript <file>" makes <name> a sound emitter loading <file>. rem-aware.
    private static IEnumerable<(string Template, string SscName)> ParseTemplateMap(string conText)
    {
        string? cur = null; int remDepth = 0;
        foreach (var raw in conText.Split('\n'))
        {
            var line = raw.Replace("\r", "").Trim();
            if (line.Length == 0) continue;
            var low = line.ToLowerInvariant();
            if (low == "beginrem") { remDepth++; continue; }
            if (low == "endrem") { if (remDepth > 0) remDepth--; continue; }
            if (remDepth > 0 || low.StartsWith("rem")) continue;
            var sp = line.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (sp.Length < 2) continue;
            var key = sp[0].ToLowerInvariant();
            if (key == "objecttemplate.create" && sp.Length >= 3) cur = sp[2];
            else if (key == "objecttemplate.loadsoundscript" && cur is not null && sp.Length >= 2)
                yield return (cur, sp[1]);
        }
    }

    private static bool TryGetIgnoreCase(IReadOnlyDictionary<string, byte[]> map, string name, out byte[] bytes)
    {
        if (map.TryGetValue(name, out var b)) { bytes = b; return true; }
        foreach (var kv in map) if (string.Equals(kv.Key, name, System.StringComparison.OrdinalIgnoreCase)) { bytes = kv.Value; return true; }
        bytes = System.Array.Empty<byte>(); return false;
    }
}
