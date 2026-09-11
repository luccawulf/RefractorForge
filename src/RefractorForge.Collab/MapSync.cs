using System.Globalization;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Editing;

namespace RefractorForge.Collab;

/// <summary>
/// The client half of working on a server map at different times.
///
/// While connected it keeps a replica of the SERVER's map - built from the document the server streams on join and
/// kept current by every op after - separate from whatever the editor has open. Against a BASELINE (the version the
/// open copy last agreed with: the record inside the .rfa, or for a copy never synced, the map as it started on the
/// server) the replica says what others changed and the editor's own state says what was changed here. Nothing is
/// applied either way until a person says so: <see cref="DownloadOps"/> and <see cref="FilesFor"/> carry the
/// server's changes in, <see cref="UploadOps"/> carries local ones out.
///
/// It is a state machine over protocol lines, like <see cref="BaseSync"/>, so the editor and the tests drive the
/// same code.
/// </summary>
public sealed class MapSync
{
    private readonly Action<string> _send;

    public MapSync(Action<string> send) => _send = send;

    // ---- the server's map -------------------------------------------------------------------------------

    public StaticObjectsFile ServerObjects { get; private set; } = new();
    public CollabWorldState ServerWorld { get; private set; } = new();
    /// <summary>The level files the server holds, by level path as the server spells it.</summary>
    public Dictionary<string, (string Hash, long Size, long Seq)> ServerFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    public long ServerSeq { get; private set; }
    public string Epoch { get; private set; } = "";
    public long LastChangeUnix { get; private set; }
    public string LastChangeBy { get; private set; } = "";
    /// <summary>True once the server's whole document has arrived.</summary>
    public bool Synced { get; private set; }

    // ---- what this copy last agreed with ---------------------------------------------------------------

    /// <summary>Every key's hash at the version this copy last agreed with the server. Null until known.</summary>
    public Dictionary<string, string>? Baseline { get; private set; }
    /// <summary>The version the baseline is at; 0 when it is the map as it started.</summary>
    public long BaselineSeq { get; private set; }
    /// <summary>The baseline's objects by content, when it came from the server rather than from a record - a
    /// copy with no ids of its own lines its objects up with these.</summary>
    public StaticObjectsFile? BaselineObjects { get; private set; }
    public bool BaselineFromRecord { get; private set; }
    public string BaselineError { get; private set; } = "";

    /// <summary>Who changed what since the baseline, once the server has answered.</summary>
    public List<ChangeJournal.Contribution>? History { get; private set; }
    public bool HistoryComplete { get; private set; } = true;

    public Action? OnSynced;
    public Action? OnBaseline;
    public Action? OnHistory;
    /// <summary>A requested file arrived: its level path and bytes, or null bytes when the server lacks it.</summary>
    public Action<string, byte[]?>? OnFile;

    /// <summary>The replica's hashes just before the server re-sent the map (see SyncBegin), or null.</summary>
    public SyncRecord? PreviousAgreed { get; private set; }

    /// <summary>Give up waiting for the baseline - a server older than this protocol never answers the request.</summary>
    public void FailBaseline(string why) { if (Baseline is null) BaselineError = why; }

    /// <summary>Drop the baseline, for a comparison that will be made against something else.</summary>
    public void ForgetBaseline() { Baseline = null; BaselineObjects = null; BaselineFromRecord = false; BaselineError = ""; }

    public void UseRecord(SyncRecord r)
    {
        Baseline = new Dictionary<string, string>(r.Hashes, StringComparer.Ordinal);
        BaselineSeq = r.Seq;
        BaselineObjects = null;
        BaselineFromRecord = true;
        BaselineError = "";
    }

    /// <summary>Ask the server for the map as it started, for a copy that has never been synced.</summary>
    public void RequestBaseline() { BaselineError = ""; _send(Message.Baseline().Encode()); }

    public void RequestHistory(long since) => _send(Message.History(since).Encode());

    public void RequestFile(string relPath) => _send(Message.FileGet(SyncKeys.EscapePath(relPath)).Encode());

    /// <summary>Feed one inbound message. Returns true when it was one this class owns outright (the ones a
    /// document-holding client must not also act on are left to the caller to route).</summary>
    public bool Handle(Message m)
    {
        switch (m.Type)
        {
            case MsgType.Version:
                ServerSeq = long.Parse(m.Args[0], CultureInfo.InvariantCulture);
                Epoch = m.Args[1];
                LastChangeUnix = long.Parse(m.Args[2], CultureInfo.InvariantCulture);
                LastChangeBy = m.Payload;
                return true;

            case MsgType.SyncBegin:
                // The server sends the whole map again after a dropped connection. What the replica held just
                // before is the last point this copy and the server agreed on - kept, because the editor compares
                // against it rather than adopting the new stream over work done while the line was down.
                PreviousAgreed = Synced && Epoch.Length > 0 && Baseline is not null
                    ? new SyncRecord { Epoch = Epoch, Seq = ServerSeq, Hashes = ServerHashes() }
                    : null;
                ServerObjects = new StaticObjectsFile { PersistIds = true };
                ServerWorld = new CollabWorldState();
                ServerFiles.Clear();
                Synced = false;
                ServerSeq = Math.Max(ServerSeq, long.Parse(m.Args[0], CultureInfo.InvariantCulture));
                return false;

            case MsgType.SyncObj:
                ApplyToReplica(m.Payload);
                return false;

            case MsgType.FileInfo:
                ServerFiles[SyncKeys.UnescapePath(m.Args[3])] =
                    (m.Args[1], long.Parse(m.Args[2], CultureInfo.InvariantCulture), long.Parse(m.Args[0], CultureInfo.InvariantCulture));
                return true;

            case MsgType.SyncEnd:
                Synced = true;
                OnSynced?.Invoke();
                return false;

            case MsgType.Op:
                ServerSeq = Math.Max(ServerSeq, long.Parse(m.Args[0], CultureInfo.InvariantCulture));
                LastChangeUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                ApplyToReplica(m.Payload);
                return false;

            case MsgType.Changes:
                History = ChangeJournal.Decode(m.Payload);
                HistoryComplete = m.Args[2] == "1";
                OnHistory?.Invoke();
                return true;

            case MsgType.BaselineData:
                LoadBaseline(MapExport.Inflate(m.Payload));
                OnBaseline?.Invoke();
                return true;

            case MsgType.BaselineFail:
                BaselineError = m.Payload;
                OnBaseline?.Invoke();
                return true;

            case MsgType.FileData:
            {
                var rel = SyncKeys.UnescapePath(m.Args[0]);
                byte[]? bytes = null;
                if (m.Payload != "-") try { bytes = Convert.FromBase64String(m.Payload); } catch { }
                OnFile?.Invoke(rel, bytes);
                return true;
            }
        }
        return false;
    }

    /// <summary>Note a level file THIS client just sent. The server does not echo a FILE back to its sender -
    /// it can be a megabyte of tile - so without this the replica would forever think the server lacked it.</summary>
    public void NoteSentFile(string relPath, byte[] bytes)
        => ServerFiles[SyncKeys.NormPath(relPath)] = (SyncKeys.Hash(bytes), bytes.LongLength, ServerSeq);

    private void ApplyToReplica(string payload)
    {
        try
        {
            if (EditWire.IsObjectOp(payload)) { EditWire.Parse(payload).Apply(ServerObjects); return; }
            if (SyncKeys.TryParseFileOp(payload, out var rel, out var bytes)) { NoteSentFile(rel, bytes); return; }
            ServerWorld.ApplyOp(payload);
        }
        catch { }
    }

    private void LoadBaseline(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        var objs = new StaticObjectsFile();
        foreach (var line in text.Split('\n'))
        {
            int t = line.IndexOf('\t');
            if (t <= 0) continue;
            string key = line[..t], val = line[(t + 1)..];
            if (val.StartsWith('=') && key.StartsWith("o:", StringComparison.Ordinal))
            {
                var o = ParseCanon(key[2..], val[1..]);
                if (o is not null) { objs.Objects.Add(o); d[key] = SyncKeys.Hash(val[1..]); }
            }
            else d[key] = val;
        }
        Baseline = d;
        BaselineObjects = objs;
        BaselineSeq = 0;
        BaselineFromRecord = false;
        BaselineError = "";
    }

    private static StaticObject? ParseCanon(string id, string canon)
    {
        var p = canon.Split('|');
        if (p.Length < 4) return null;
        try
        {
            return new StaticObject(p[0])
            {
                Id = id, IdFromFile = true,
                Position = Formats.Geometry.Vec3.Parse(p[1]),
                Rotation = Formats.Geometry.Vec3.Parse(p[2]),
                Scale = float.Parse(p[3], CultureInfo.InvariantCulture),
            };
        }
        catch { return null; }
    }

    // ---- the comparison ---------------------------------------------------------------------------------

    /// <summary>Every key's hash on the server, as far as this client can tell. A file the server does not list is
    /// the one the map was built with, which is the baseline's; and a whole kind of content the server holds none
    /// of (a relay that never received terrain) is "unknown", not "deleted" - only objects can be deleted.</summary>
    public Dictionary<string, string> ServerHashes()
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in ServerFiles) files[kv.Key] = kv.Value.Hash;
        var d = SyncKeys.Hashes(ServerObjects, ServerWorld, files);
        FillUnknown(d);
        return d;
    }

    /// <summary>Complete <paramref name="d"/> with the baseline's value wherever it says nothing: files it does
    /// not list, and non-object content it has none of.</summary>
    public void FillUnknown(Dictionary<string, string> d)
    {
        if (Baseline is null) return;
        foreach (var kv in Baseline)
        {
            if (d.ContainsKey(kv.Key) || kv.Key.StartsWith("o:", StringComparison.Ordinal)) continue;
            d[kv.Key] = kv.Value;
        }
    }

    /// <summary>
    /// Compare the map here (<paramref name="localObjects"/>, <paramref name="localWorld"/> and its files by level
    /// path) with the server's, against the baseline. Objects here that have no id of their own are first lined up
    /// with the baseline's by what they are - that is what makes a copy saved before ids existed comparable at all;
    /// returns how many were re-keyed in <paramref name="rekeyed"/> so the caller can drop an undo history that
    /// still names the old ids.
    /// </summary>
    public SyncPlan Plan(StaticObjectsFile localObjects, CollabWorldState localWorld,
                         IReadOnlyDictionary<string, string> localFiles, out int rekeyed)
    {
        rekeyed = 0;
        if (Baseline is null) throw new InvalidOperationException("no baseline yet");
        // Only a copy measured against the map's STARTING point needs its objects lined up: a copy with a record
        // already carries the ids it synced with, and an object placed since is genuinely new.
        if (BaselineObjects is not null)
            rekeyed = SyncObjects.Rekey(localObjects, BaselineObjects, o => !o.IdFromFile);
        foreach (var o in localObjects.Objects) o.IdFromFile = true;   // from here on the id is the one to keep
        var local = SyncKeys.Hashes(localObjects, localWorld, localFiles);
        FillUnknown(local);
        return SyncPlan.Compute(Baseline, local, ServerHashes());
    }

    /// <summary>The ops that bring the server's value of each key into a copy (files excepted - see
    /// <see cref="FilesFor"/>).</summary>
    public List<string> DownloadOps(IEnumerable<string> keys)
    {
        var ops = new List<string>();
        foreach (var k in keys)
            if (!k.StartsWith("f:", StringComparison.Ordinal))
                ops.AddRange(SyncKeys.OpsFor(k, ServerObjects, ServerWorld));
        return ops;
    }

    /// <summary>The server's level paths for the file keys among <paramref name="keys"/> that the server holds.</summary>
    public List<string> FilesFor(IEnumerable<string> keys)
    {
        var byKey = ServerFiles.Keys.ToDictionary(SyncKeys.FileKey, p => p, StringComparer.Ordinal);
        return keys.Where(k => k.StartsWith("f:", StringComparison.Ordinal))
                   .Select(k => byKey.TryGetValue(k, out var p) ? p : null)
                   .Where(p => p is not null).Select(p => p!).ToList();
    }

    /// <summary>The ops that carry this copy's value of each key to the server. <paramref name="readFile"/> gives a
    /// level file's bytes by the key's path; a file it cannot produce is skipped.</summary>
    public static List<string> UploadOps(IEnumerable<string> keys, StaticObjectsFile localObjects, CollabWorldState localWorld,
                                         IReadOnlyDictionary<string, string> localFilePaths, Func<string, byte[]?> readFile)
    {
        var ops = new List<string>();
        var byKey = localFilePaths.Keys.ToDictionary(SyncKeys.FileKey, p => p, StringComparer.Ordinal);
        foreach (var k in keys)
        {
            if (k.StartsWith("f:", StringComparison.Ordinal))
            {
                if (byKey.TryGetValue(k, out var path) && readFile(path) is { } bytes) ops.Add(SyncKeys.FileOp(path, bytes));
                continue;
            }
            ops.AddRange(SyncKeys.OpsFor(k, localObjects, localWorld));
        }
        return ops;
    }

    /// <summary>The record to write into the archive once this copy agrees with the server: the server's hashes
    /// at its current version. Only true when nothing is pending either way - the caller checks.</summary>
    public SyncRecord RecordNow(string map, Formats.Rfa.LevelBase.Id? basePin)
        => new() { Map = map, Epoch = Epoch, Seq = ServerSeq, Base = basePin, Hashes = ServerHashes() };
}
