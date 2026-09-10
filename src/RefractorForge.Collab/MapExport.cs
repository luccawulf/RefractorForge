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

        var world = room.Relay.SnapshotWorld();
        var objects = room.Relay.SnapshotDoc();

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

        LevelSaver.RepackToRfa(basePath, tmp, objects, world?.Height, world?.Material, gameplay,
                               growth: world is null ? null : new GrowthMapsView(world).ToGrowthMaps());

        if (File.Exists(outPath)) File.Delete(outPath);
        File.Move(tmp, outPath);
        return LevelBase.Identify(outPath);
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
