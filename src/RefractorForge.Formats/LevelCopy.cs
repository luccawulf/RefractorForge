using System.Text;
using System.Text.RegularExpressions;
using RefractorForge.Formats.Rfa;

namespace RefractorForge.Formats;

/// <summary>
/// Battlecraft's "Copy Level To...": the same level under another name. Battlecraft cannot rename a level in place
/// either - its manual says so and points at Copy Level To - because a level's name is not only its archive's file
/// name. It is the folder every entry sits under (<c>bfvietnam/levels/Foo/...</c>), and the level's own files
/// spell that folder out: <c>Init/Terrain.con</c>'s heightmap, material map, texture and detail paths, the growth
/// <c>.wst</c>s' <c>materialMapFilename</c>, an <c>Init.con</c> <c>textureManager.alternativePath</c> or
/// <c>ShaderManager.setTextureParam</c> into its own Textures, a <c>game.setLoadPicture</c> that reaches back into
/// its own Menu folder. Measured over 105 levels (84 in a BFV install's BfVietnam mod, 21 of a clean BF1942), those are the only places.
///
/// So a copy moves every entry to the new folder and re-points exactly those paths: any <c>levels/OldName</c>
/// segment (either slash, any case) followed by a separator, quote, space or the end of the line. File names are
/// left alone - <c>env_Marshall_island_01.dds</c> is a name the level's .con asks for, not a path - and so are
/// mentions that are not paths (comments, lexicon keys, a load picture that lives in the menu archive). Nothing is
/// ever overwritten: an existing destination is refused.
/// </summary>
public static class LevelCopy
{
    public sealed record Result(string Destination, string OldName, string NewName, int Files, int FilesRewritten,
                                int References, int OtherMentions, IReadOnlyList<string> Notes);

    // The level files that are text and may carry paths. Everything else (raw maps, textures, meshes, sounds) is
    // copied byte for byte.
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".con", ".inc", ".wst", ".ssc", ".txt", ".desc", ".xml", ".ini", ".cfg", ".lst", ".rs", ".tweak", ".ai" };

    private static readonly Encoding Latin1 = Encoding.Latin1;   // byte <-> char one to one: nothing else changes

    /// <summary>Null when <paramref name="name"/> can be a level name; otherwise why not. The engine reads a level
    /// path as one word in a .con line, so no spaces - letters, digits, '_' and '-' only.</summary>
    public static string? NameProblem(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Type a name for the copy.";
        if (name.Length > 64) return "Keep the name under 64 characters.";
        foreach (char c in name)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-'))
                return "Use only letters, digits, _ and - (no spaces): the game reads a level path as one word.";
        return null;
    }

    private static Regex LevelPath(string name) => new(
        @"(levels[\\/]+)" + Regex.Escape(name) + @"(?=[\\/""'\s]|$)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>Re-point every <c>levels/old</c> path segment in a text file to <c>levels/new</c>. Returns the same
    /// array when nothing matched, so an untouched file is copied as it was.</summary>
    public static (byte[] Bytes, int Count) Retarget(byte[] data, IEnumerable<string> oldNames, string newName)
    {
        var text = Latin1.GetString(data);
        int n = 0;
        foreach (var old in oldNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (old.Equals(newName, StringComparison.Ordinal)) continue;
            text = LevelPath(old).Replace(text, m => { n++; return m.Groups[1].Value + newName; });
        }
        return n == 0 ? (data, 0) : (Latin1.GetBytes(text), n);
    }

    /// <summary>The level name its own files use: the folder <c>Init/Terrain.con</c> (else <c>Init.con</c>, else a
    /// growth .wst) loads its heightmap from. Null when none says.</summary>
    public static string? NameInFiles(Func<string, byte[]?> readRel)
    {
        var rx = new Regex(@"levels[\\/]+([A-Za-z0-9_\-]+)[\\/]", RegexOptions.IgnoreCase);
        foreach (var rel in new[] { "Init/Terrain.con", "Init.con", "Growth/overGrowth.wst", "Growth/underGrowth.wst" })
        {
            var b = readRel(rel);
            if (b is null) continue;
            var m = rx.Match(Latin1.GetString(b));
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }

    private static int CountMentions(byte[] data, string name)
        => Regex.Matches(Latin1.GetString(data), @"(?<![A-Za-z0-9_])" + Regex.Escape(name) + @"(?![A-Za-z0-9_])",
                         RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Count;

    /// <summary>Copy a PACKED level - its .rfa plus every numeric patch beside it (<c>Foo_001.rfa</c>...), merged
    /// last-wins as the game layers them - to <c>&lt;destDir&gt;/&lt;newName&gt;.rfa</c> (default: beside the
    /// original), entries moved to the new level folder and its paths re-pointed. The editor's sidecars beside the
    /// archive (<c>Foo.*.json</c>: lights, groups, notes, growth view) are copied too, under the new name.</summary>
    public static Result CopyArchive(string baseRfa, string newName, string? destDir = null)
    {
        if (NameProblem(newName) is { } why) throw new ArgumentException(why, nameof(newName));
        var archives = LevelSaver.WithPatchArchives(new[] { baseRfa });
        var opened = archives.Select(p => new RefractorFlatArchive(p)).ToList();
        var baseArc = opened[0];
        string prefix = LevelSaver.ArchivePrefix(baseArc);   // e.g. "BfVietnam/levels/Foo/"
        if (prefix.Length == 0)
            throw new InvalidDataException($"{Path.GetFileName(baseRfa)} does not look like a level (no Init.con, StaticObjects.con or Heightmap.raw).");
        var parts = prefix.TrimEnd('/').Split('/');
        string folderName = parts[^1];
        string parent = string.Join('/', parts[..^1]);
        string newPrefix = (parent.Length > 0 ? parent + "/" : "") + newName + "/";

        var dir = destDir ?? Path.GetDirectoryName(Path.GetFullPath(baseRfa))!;
        string dest = Path.Combine(dir, newName + ".rfa");
        if (File.Exists(dest)) throw new IOException($"{dest} already exists - pick another name.");
        var notes = new List<string>();
        foreach (var stray in Directory.Exists(dir) ? Directory.EnumerateFiles(dir, newName + "_*.rfa") : Enumerable.Empty<string>())
            if (Regex.IsMatch(Path.GetFileNameWithoutExtension(stray), "^" + Regex.Escape(newName) + @"_\d+$", RegexOptions.IgnoreCase))
                throw new IOException($"{Path.GetFileName(stray)} is already there - the game would layer it over the copy as a patch. Move it or pick another name.");

        // Merge base + patches by name, later archives winning, keeping first-seen order.
        var merged = new Dictionary<string, (RefractorFlatArchive A, RefractorFlatArchiveEntry E)>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var a in opened)
            foreach (var e in a.Entries)
            {
                var key = e.Name.Replace('\\', '/');
                if (!merged.ContainsKey(key)) order.Add(key);
                merged[key] = (a, e);
            }

        byte[]? ReadRel(string rel)
        {
            var key = prefix + rel;
            return merged.TryGetValue(key, out var hit) ? hit.A.Read(hit.E) : null;
        }
        string? inFiles = NameInFiles(ReadRel);
        var oldNames = new List<string> { folderName };
        if (inFiles is not null && !inFiles.Equals(folderName, StringComparison.OrdinalIgnoreCase))
        {
            oldNames.Add(inFiles);
            notes.Add($"Its files pointed at levels/{inFiles} although it is packed as {folderName}; those paths now point at {newName} too.");
        }

        var items = new List<(RefractorFlatArchive, RefractorFlatArchiveEntry, string)>(order.Count);
        var replace = new Dictionary<string, byte[]>(RefractorFlatArchive.NameComparer);
        int rewritten = 0, refs = 0, others = 0, outside = 0;
        foreach (var key in order)
        {
            var (a, e) = merged[key];
            string name;
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) name = newPrefix + key[prefix.Length..];
            else { name = key; outside++; }
            items.Add((a, e, name));
            if (!TextExtensions.Contains(Path.GetExtension(key))) continue;
            var original = a.Read(e);
            var (bytes, n) = Retarget(original, oldNames, newName);
            if (n > 0) { replace[name] = bytes; rewritten++; refs += n; }
            foreach (var old in oldNames) others += CountMentions(bytes, old);
        }
        if (outside > 0) notes.Add($"{outside} entr{(outside == 1 ? "y" : "ies")} outside the level folder were copied under their old names.");

        RefractorFlatArchive.WriteRenamedCopy(dest, items, replace, baseArc.IsCompressed, baseArc.XPackId);

        // The editor's own files beside a packed level: <stem>.<name> -> <newName>.<name>.
        var stem = Path.GetFileNameWithoutExtension(baseRfa);
        int sidecars = 0;
        if (Directory.Exists(Path.GetDirectoryName(Path.GetFullPath(baseRfa))))
            foreach (var f in Directory.EnumerateFiles(Path.GetDirectoryName(Path.GetFullPath(baseRfa))!, stem + ".*"))
            {
                var leaf = Path.GetFileName(f);
                var ext = Path.GetExtension(leaf);
                if (ext.Equals(".rfa", StringComparison.OrdinalIgnoreCase) || ext.Equals(".rfatmp", StringComparison.OrdinalIgnoreCase)) continue;
                var to = Path.Combine(dir, newName + leaf[stem.Length..]);
                if (File.Exists(to)) continue;
                DurableFile.Copy(f, to);
                sidecars++;
            }
        if (sidecars > 0) notes.Add($"Copied {sidecars} editor file(s) kept beside the archive (lights, groups, notes, view settings).");
        if (archives.Length > 1) notes.Add($"Merged {archives.Length - 1} patch archive(s) into the copy.");

        return new Result(dest, folderName, newName, items.Count, rewritten, refs, others, notes);
    }

    /// <summary>Copy a level FOLDER to a sibling folder named <paramref name="newName"/> (or one under
    /// <paramref name="destParent"/>), its paths re-pointed. The editor's files inside it come along; its Backups
    /// folder and a project manifest (.rfproj, which names the old folder) do not. Written to a temporary folder
    /// first and renamed when complete, so a failure leaves nothing half-copied.</summary>
    public static Result CopyFolder(string levelDir, string newName, string? destParent = null)
    {
        if (NameProblem(newName) is { } why) throw new ArgumentException(why, nameof(newName));
        var src = Path.GetFullPath(levelDir).TrimEnd('\\', '/');
        if (!Directory.Exists(src)) throw new DirectoryNotFoundException(src);
        string folderName = Path.GetFileName(src);
        var parentDir = destParent ?? Path.GetDirectoryName(src)!;
        string dest = Path.Combine(parentDir, newName);
        if (Directory.Exists(dest) || File.Exists(dest)) throw new IOException($"{dest} already exists - pick another name.");
        if (Path.GetFullPath(dest).StartsWith(src + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The copy cannot go inside the level it copies.");

        byte[]? ReadRel(string rel)
        {
            var p = Path.Combine(src, rel.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(p) ? File.ReadAllBytes(p) : null;
        }
        var notes = new List<string>();
        string? inFiles = NameInFiles(ReadRel);
        var oldNames = new List<string>();
        if (inFiles is not null) oldNames.Add(inFiles);
        if (NameProblem(folderName) is null) oldNames.Add(folderName);
        string oldName = inFiles ?? folderName;

        string tmp = dest + ".rfcopy";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        int files = 0, rewritten = 0, refs = 0, others = 0, skipped = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(src, f).Replace('\\', '/');
                var leaf = Path.GetFileName(rel);
                if (rel.StartsWith("Backups/", StringComparison.OrdinalIgnoreCase) || rel.Contains("/Backups/", StringComparison.OrdinalIgnoreCase)
                    || leaf.EndsWith(".rfproj", StringComparison.OrdinalIgnoreCase) || leaf.EndsWith(".rfatmp", StringComparison.OrdinalIgnoreCase)
                    || leaf.StartsWith("~", StringComparison.Ordinal))
                { skipped++; continue; }
                var to = Path.Combine(tmp, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                if (TextExtensions.Contains(Path.GetExtension(leaf)) && oldNames.Count > 0)
                {
                    var (bytes, n) = Retarget(File.ReadAllBytes(f), oldNames, newName);
                    if (n > 0) { rewritten++; refs += n; }
                    foreach (var old in oldNames) others += CountMentions(bytes, old);
                    File.WriteAllBytes(to, bytes);
                }
                else File.Copy(f, to);
                files++;
            }
            Directory.Move(tmp, dest);
        }
        catch
        {
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); } catch { }
            throw;
        }
        if (skipped > 0) notes.Add($"Left out {skipped} file(s): backups and the project manifest, which belong to the original.");
        return new Result(dest, oldName, newName, files, rewritten, refs, others, notes);
    }
}
