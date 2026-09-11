using RefractorForge.Formats;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Rfa;

namespace RefractorForge.Collab;

/// <summary>
/// Turning a live session back into a level archive.
///
/// The relay keeps a map as an immutable base archive plus a small delta of edits, and that split is what makes
/// it work: the delta is a couple of hundred KB so it saves on every edit and 192 backups cost 35 MB, while the
/// base is stable so everyone can be pinned to it and told when they have the wrong one. Rewriting the archive
/// per edit would trade two days of history for about an hour and make every client's pin go stale constantly.
///
/// But a delta is no use to a person who just wants the map. Opening a level in the editor, or putting it on a
/// game server, means one file. So the split stays and the archive is rebuilt on demand: take the base, replace
/// the entries the session has changed, repack. A full repack of a 383 MB archive measures at about a second,
/// because the writer streams compressed blocks through rather than recompressing them, so this is cheap enough
/// to do whenever anyone asks.
/// </summary>
public static class MapExport
{
    /// <summary>Build a current archive for a room. Returns its identity, or null when the map has no base
    /// archive to build from - a session that has never been pinned is a pile of edits with no ground under it,
    /// and there is nothing honest to hand someone.</summary>
    public static LevelBase.Id? Build(MapLibrary.Room room, BaseArchiveStore store, string outPath)
    {
        var pin = room.Relay.BasePin;
        if (pin is null || !store.Has(pin.Fingerprint)) return null;

        string basePath = Path.Combine(store.Directory_, pin.Fingerprint.ToLowerInvariant() + ".rfa");
        if (!File.Exists(basePath)) return null;

        // One consistent moment: the version, the objects, the world and the file list are read together, so the
        // record written into the archive describes exactly what is in it.
        long seq = room.Relay.Sequence;
        var world = room.Relay.SnapshotWorld();
        var objects = room.Relay.SnapshotDoc();
        objects.PersistIds = true;                       // the archive carries its ids; that is what syncing needs
        var files = room.Relay.FileList();

        // Gameplay travels as GameplaySync text, which is a diff language, not a file format. It is applied onto
        // the base archive's own gameplay so anything the session never touched survives untouched.
        EditableGameplay? gameplay = null;
        try
        {
            var baseLevel = RefractorForge.Render.LevelArchive.FromRfa(basePath);
            if (!string.IsNullOrEmpty(world?.Gameplay))
            {
                gameplay = new EditableGameplay(baseLevel.Gameplay);
                GameplaySync.Apply(gameplay, world!.Gameplay!);
            }
        }
        catch { /* a base we cannot re-read still exports its objects and terrain */ }

        var dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = outPath + ".building";
        if (File.Exists(tmp)) File.Delete(tmp);

        // Every level file people sent - tiles, bakes, sounds, decals, Init.con - goes in by its level path. Without
        // them the archive held objects whose templates were nowhere in it, and none of the lighting or paint.
        var newEntries = new List<(string RelPath, byte[] Bytes)>();
        foreach (var (path, _, _, _) in files)
            if (room.Relay.ReadFile(path) is { } bytes) newEntries.Add((path, bytes));

        // And the record, so a mapper who opens this file is already "in step at version N" rather than a copy the
        // server has never heard of. Hashes are of exactly what the archive holds: every file of the archive it
        // was built on, with the ones people sent laid over them.
        var allFiles = BaseManifest(store, pin, basePath);
        foreach (var f in files) allFiles[f.Path] = f.Hash;
        var record = new SyncRecord
        {
            Map = room.Name, Epoch = room.Relay.Epoch, Seq = seq, Base = pin,
            Hashes = SyncKeys.Hashes(objects, world, allFiles),
        };
        newEntries.Add((SyncRecord.EntryLeaf, record.ToBytes()));

        LevelSaver.RepackToRfa(basePath, tmp, objects, world?.Height, world?.Material, gameplay,
                               growth: world is null ? null : new GrowthMapsView(world).ToGrowthMaps(),
                               newEntries: newEntries);

        if (File.Exists(outPath)) File.Delete(outPath);
        File.Move(tmp, outPath);
        return LevelBase.Identify(outPath);
    }

    /// <summary>
    /// The map as it was when the session began - its pinned archive, piece by piece - for an editor whose copy has
    /// never been synced and so has no record of its own to compare against. Objects come with their content, not
    /// just a hash: such a copy has no ids either, and has to line its objects up with these by what they are.
    /// The archive's objects are first lined up with the server's own, so an object nobody moved has the same id in
    /// all three copies. Deflated and base64'd "key TAB value" lines, cached beside the archive.
    /// </summary>
    public static string? Baseline(MapLibrary.Room room, BaseArchiveStore store)
    {
        var pin = room.Relay.BasePin;
        if (pin is null || !store.Has(pin.Fingerprint)) return null;
        string basePath = Path.Combine(store.Directory_, pin.Fingerprint.ToLowerInvariant() + ".rfa");
        if (!File.Exists(basePath)) return null;

        var (objects, world) = RelayHost.LoadFullLevel(basePath);
        objects ??= new StaticObjectsFile();
        SyncObjects.Rekey(objects, room.Relay.SnapshotDoc(), _ => true);
        var files = BaseManifest(store, pin, basePath);

        var sb = new System.Text.StringBuilder();
        foreach (var o in objects.Objects) sb.Append("o:").Append(o.Id).Append("\t=").Append(SyncKeys.ObjectCanon(o)).Append('\n');
        foreach (var kv in SyncKeys.Hashes(null, world, files)) sb.Append(kv.Key).Append('\t').Append(kv.Value).Append('\n');
        return Deflate(sb.ToString());
    }

    /// <summary>A base archive's files (everything but the structured entries), by level path, with the hash of
    /// each. Every entry has to be decompressed for it, so it is computed once per archive and kept beside it.</summary>
    public static Dictionary<string, string> BaseManifest(BaseArchiveStore store, LevelBase.Id pin, string basePath)
    {
        var cache = Path.Combine(store.Directory_, pin.Fingerprint.ToLowerInvariant() + ".manifest");
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(cache))
        {
            foreach (var line in File.ReadLines(cache))
            {
                int t = line.IndexOf('\t');
                if (t > 0) files[line[(t + 1)..]] = line[..t];
            }
            return files;
        }
        var arch = new Formats.Rfa.RefractorFlatArchive(basePath);
        string prefix = LevelSaver.ArchivePrefix(arch);
        foreach (var e in LevelBase.Manifest(basePath))
        {
            var rel = SyncKeys.LevelRelative(e.Name, prefix);
            if (rel is null || SyncKeys.IsStructuredEntry(rel)) continue;
            files[rel] = e.Hash;
        }
        try { File.WriteAllLines(cache, files.Select(kv => $"{kv.Value}\t{kv.Key}")); } catch { }
        return files;
    }

    public static string Deflate(string text)
    {
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            var b = System.Text.Encoding.UTF8.GetBytes(text);
            z.Write(b, 0, b.Length);
        }
        return Convert.ToBase64String(ms.ToArray());
    }

    public static string Inflate(string b64)
    {
        using var ms = new MemoryStream(Convert.FromBase64String(b64));
        using var z = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Decompress);
        using var sr = new StreamReader(z, System.Text.Encoding.UTF8);
        return sr.ReadToEnd();
    }

    /// <summary>The world state holds the two growth maps loose; the saver wants them as a pair.</summary>
    private sealed class GrowthMapsView
    {
        private readonly CollabWorldState _w;
        public GrowthMapsView(CollabWorldState w) => _w = w;
        public Formats.Terrain.GrowthMaps? ToGrowthMaps()
            => _w.Under is null && _w.Over is null
                ? null
                : new Formats.Terrain.GrowthMaps { Under = _w.Under, Over = _w.Over };
    }
}
