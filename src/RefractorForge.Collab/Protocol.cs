using System.Globalization;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Editing;
using RefractorForge.Formats.Geometry;

namespace RefractorForge.Collab;

/// <summary>
/// The collaboration wire protocol. It is a thin, line-oriented framing layer over the
/// editor's existing <see cref="EditWire"/> command strings. One message per line; the first
/// token is the message type. Object-edit payloads ("MOVE id x/y/z", "ADD id tmpl ...") are
/// reused verbatim so the protocol inherits the editor's lossless command semantics.
///
/// Design choice that makes correctness easy to reason about: every object-edit is an
/// ABSOLUTE set (MOVE/ROT/SCALE write a value, not a delta) or an idempotent structural op
/// (ADD checks existence, DEL checks existence). The relay assigns every op a single global
/// sequence number and rebroadcasts in that order; because all clients apply the identical
/// totally-ordered stream, their documents are byte-identical by construction (last-writer-wins
/// per field, with "last" defined by the relay's order — the same on every client).
/// </summary>
public enum MsgType
{
    Join, SyncBegin, SyncObj, SyncEnd, Op, Presence, Leave, Error, SeedRequest, Auth,
    // The base level archive: the ground the ops are applied to. See the Base* factories below.
    Base, BaseOk, BaseDiff, BaseNeed, BaseGet, BasePut, BaseData, BaseDone,
    // Choosing which map to work on, when the relay hosts several. See the Map* factories below.
    MapList, MapInfo, MapEnd, PickMap, MapOk,
    // Pulling the CURRENT map back out as one archive. See the Export factories below.
    Export, ExportReady, ExportFailed,
    // Working on a map at different times: the map's version, its history, the ground it started from, and the
    // level files it carries. See the factories under "Working at different times".
    Version, History, Changes, Baseline, BaselineData, BaselineFail, FileInfo, FileGet, FileData,
}

public readonly struct Message
{
    public MsgType Type { get; init; }
    /// <summary>Tokens after the type keyword (for fixed-arity messages).</summary>
    public string[] Args { get; init; }
    /// <summary>For OP/SYNCOBJ: the trailing EditWire command string (may contain spaces).</summary>
    public string Payload { get; init; }

    public static Message Join(string clientId, string name)
        => new() { Type = MsgType.Join, Args = new[] { clientId, name }, Payload = "" };

    public static Message SyncBegin(long seq)
        => new() { Type = MsgType.SyncBegin, Args = new[] { seq.ToString(CultureInfo.InvariantCulture) }, Payload = "" };

    public static Message SyncObj(string editWireAdd)
        => new() { Type = MsgType.SyncObj, Args = Array.Empty<string>(), Payload = editWireAdd };

    public static Message SyncEnd()
        => new() { Type = MsgType.SyncEnd, Args = Array.Empty<string>(), Payload = "" };

    public static Message Op(long seq, string clientId, long localOpId, string editWire)
        => new() { Type = MsgType.Op, Args = new[] { seq.ToString(CultureInfo.InvariantCulture), clientId, localOpId.ToString(CultureInfo.InvariantCulture) }, Payload = editWire };

    /// <summary>Ephemeral presence: where a peer's camera is, and where it is pointing. Pitch is a SIXTH field
    /// appended after heading, which older relays and clients simply ignore - a relay forwards the PRESENCE line
    /// verbatim, and a client that only reads five fields still gets the position and the compass bearing.</summary>
    public static Message Presence(string clientId, string name, string selId, Vec3 cursor, float heading = 0f, float pitch = 0f)
        => new() { Type = MsgType.Presence, Args = new[] { clientId, name, selId, cursor.ToString(), heading.ToString("0.####", CultureInfo.InvariantCulture), pitch.ToString("0.####", CultureInfo.InvariantCulture) }, Payload = "" };

    public static Message Leave(string clientId)
        => new() { Type = MsgType.Leave, Args = new[] { clientId }, Payload = "" };

    public static Message Error(string text)
        => new() { Type = MsgType.Error, Args = Array.Empty<string>(), Payload = text };

    /// <summary>Server -> first client of an EMPTY relay: "please upload your document to seed me". So a fresh
    /// central server everyone joins gets its canonical state from the first joiner instead of from a host.</summary>
    public static Message SeedRequest()
        => new() { Type = MsgType.SeedRequest, Args = Array.Empty<string>(), Payload = "" };

    /// <summary>Client -> server, sent BEFORE Join when the relay is password-protected. The password is the
    /// trailing payload (so it may contain spaces). A wrong/absent password gets an Error + disconnect.</summary>
    public static Message Auth(string password)
        => new() { Type = MsgType.Auth, Args = Array.Empty<string>(), Payload = password };

    // ---------------------------------------------------------------------------------------------------
    // The base level archive.
    //
    // Everything above this line syncs EDITS. None of it syncs the ground those edits land on - the level's
    // own .rfa. Two people whose archives differ apply the identical ordered op stream to different worlds
    // and neither is told, which is the one silent corruption this protocol had left. So a client announces
    // what it is standing on the moment it joins, and the relay, which pins the session to one archive, either
    // agrees, or says what it has and whether it can hand it over.
    //
    // The bytes travel in base64 chunks over this same line protocol rather than a second socket: it keeps the
    // one outbound connection that makes the relay work behind home routers, and a chunk is small enough that
    // an editing session carries on between them.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>Client -> server, right after Join: "this is the archive I have open". A client with no level
    /// open sends a fingerprint of "-".</summary>
    public static Message Base(string fingerprint, long bytes, string levelName)
        => new() { Type = MsgType.Base, Args = new[] { fingerprint, bytes.ToString(CultureInfo.InvariantCulture), levelName }, Payload = "" };

    /// <summary>Server -> client: you are on the session's archive. Also the answer to the first client, whose
    /// archive becomes the pin.</summary>
    public static Message BaseOk(string fingerprint)
        => new() { Type = MsgType.BaseOk, Args = new[] { fingerprint }, Payload = "" };

    /// <summary>Server -> client: the session is pinned to a different archive. <paramref name="available"/> is
    /// whether the relay actually holds the bytes and can serve them, because knowing you are wrong is only
    /// half an answer.</summary>
    public static Message BaseDiff(string fingerprint, long bytes, string levelName, bool available)
        => new()
        {
            Type = MsgType.BaseDiff,
            Args = new[] { fingerprint, bytes.ToString(CultureInfo.InvariantCulture), levelName, available ? "1" : "0" },
            Payload = "",
        };

    /// <summary>Server -> client: you match the pin but the relay has no copy of it. Upload yours so the next
    /// person to join can be given it.</summary>
    public static Message BaseNeed(string fingerprint)
        => new() { Type = MsgType.BaseNeed, Args = new[] { fingerprint }, Payload = "" };

    /// <summary>Client -> server: send me the session's archive.</summary>
    public static Message BaseGet(string fingerprint)
        => new() { Type = MsgType.BaseGet, Args = new[] { fingerprint }, Payload = "" };

    /// <summary>Client -> server: I am about to upload this archive.</summary>
    public static Message BasePut(string fingerprint, long bytes, string levelName)
        => new() { Type = MsgType.BasePut, Args = new[] { fingerprint, bytes.ToString(CultureInfo.InvariantCulture), levelName }, Payload = "" };

    /// <summary>Either direction: one chunk of archive bytes, base64 in the payload. The index is there so a
    /// receiver can reject an out-of-order stream instead of writing a corrupt file.</summary>
    public static Message BaseData(string fingerprint, int index, string base64)
        => new() { Type = MsgType.BaseData, Args = new[] { fingerprint, index.ToString(CultureInfo.InvariantCulture) }, Payload = base64 };

    /// <summary>Either direction: that was the last chunk. The receiver verifies the fingerprint before it
    /// accepts the file, so a truncated or corrupted transfer is discarded rather than installed.</summary>
    public static Message BaseDone(string fingerprint)
        => new() { Type = MsgType.BaseDone, Args = new[] { fingerprint }, Payload = "" };

    // -----------------------------------------------------------------------------------------------------
    // Choosing a map.
    //
    // A relay hosting several maps sends its list the moment a client is authenticated, and waits: the client is
    // registered nowhere and sees no document until it has picked one. That ordering is the point. Registering
    // first and switching later would mean streaming a whole level to somebody who is about to ask for a
    // different one, and would give every client a window in which its edits could land in the wrong map.
    // -----------------------------------------------------------------------------------------------------

    /// <summary>Server -> client: here comes the list of maps on this relay.</summary>
    public static Message MapList() => new() { Type = MsgType.MapList, Args = Array.Empty<string>(), Payload = "" };

    /// <summary>Server -> client: one map. Objects and whether the relay holds its archive are there so a person
    /// can tell an established map from an empty one before entering it.</summary>
    public static Message MapInfo(string name, int objects, bool hasBase, long updatedUnix, int clients)
        => new()
        {
            Type = MsgType.MapInfo,
            Args = new[]
            {
                name,
                objects.ToString(CultureInfo.InvariantCulture),
                hasBase ? "1" : "0",
                updatedUnix.ToString(CultureInfo.InvariantCulture),
                clients.ToString(CultureInfo.InvariantCulture),
            },
            Payload = "",
        };

    /// <summary>Server -> client: that was the whole list.</summary>
    public static Message MapEnd() => new() { Type = MsgType.MapEnd, Args = Array.Empty<string>(), Payload = "" };

    /// <summary>Client -> server: put me in this map. A name the relay does not know creates it, which is how a
    /// new map is started without touching the server.</summary>
    public static Message PickMap(string name)
        => new() { Type = MsgType.PickMap, Args = new[] { name }, Payload = "" };

    /// <summary>Server -> client: you are in. The document follows immediately.</summary>
    public static Message MapOk(string name)
        => new() { Type = MsgType.MapOk, Args = new[] { name }, Payload = "" };

    /// <summary>Client -> server: build the map as it stands now and send it to me as one archive. The relay
    /// keeps a map as an immutable base plus a delta, which is right for editing and for history but no use to
    /// somebody who just wants the level - opening it in the editor, or putting it on a game server, means one
    /// file. This is how that file is produced, on demand, without the storage model changing.</summary>
    public static Message Export() => new() { Type = MsgType.Export, Args = Array.Empty<string>(), Payload = "" };

    /// <summary>Server -> client: built. The bytes follow as ordinary BASEDATA chunks under this fingerprint.</summary>
    public static Message ExportReady(string fingerprint, long bytes, string mapName)
        => new() { Type = MsgType.ExportReady, Args = new[] { fingerprint, bytes.ToString(CultureInfo.InvariantCulture), mapName }, Payload = "" };

    /// <summary>Server -> client: could not build it, and why in words meant for a person.</summary>
    public static Message ExportFailed(string reason)
        => new() { Type = MsgType.ExportFailed, Args = Array.Empty<string>(), Payload = reason };

    // -----------------------------------------------------------------------------------------------------
    // Working at different times.
    //
    // A map on the server has a VERSION - a number that only goes up, one per edit, kept across restarts - and an
    // EPOCH that names this incarnation of the map, so a copy synced with a map that was since deleted and made
    // again is not mistaken for one synced with this. An editor keeps the version it last synced to inside the
    // .rfa, which is how "what changed while I was away" and "what did I change offline" both become answerable.
    // -----------------------------------------------------------------------------------------------------

    /// <summary>Server -> client, just before the document: where the map stands and who touched it last.</summary>
    public static Message Version(long seq, string epoch, long lastUnix, string lastAuthor)
        => new()
        {
            Type = MsgType.Version,
            Args = new[] { seq.ToString(CultureInfo.InvariantCulture), epoch, lastUnix.ToString(CultureInfo.InvariantCulture) },
            Payload = lastAuthor,
        };

    /// <summary>Client -> server: who changed what since version <paramref name="sinceSeq"/>?</summary>
    public static Message History(long sinceSeq)
        => new() { Type = MsgType.History, Args = new[] { sinceSeq.ToString(CultureInfo.InvariantCulture) }, Payload = "" };

    /// <summary>Server -> client: the answer, as base64 text - one "count TAB lastUnix TAB kinds TAB author" line per
    /// person. <paramref name="complete"/> is false when the history no longer reaches back that far.</summary>
    public static Message Changes(long sinceSeq, long nowSeq, bool complete, string payloadB64)
        => new()
        {
            Type = MsgType.Changes,
            Args = new[] { sinceSeq.ToString(CultureInfo.InvariantCulture), nowSeq.ToString(CultureInfo.InvariantCulture), complete ? "1" : "0" },
            Payload = payloadB64,
        };

    /// <summary>Client -> server: send me the map as it was when the session began - the pinned archive's own
    /// content, key by key. A copy that has never been synced compares itself against this.</summary>
    public static Message Baseline() => new() { Type = MsgType.Baseline, Args = Array.Empty<string>(), Payload = "" };

    /// <summary>Server -> client: the baseline, deflated and base64'd "key TAB hash" lines.</summary>
    public static Message BaselineData(string payloadB64)
        => new() { Type = MsgType.BaselineData, Args = Array.Empty<string>(), Payload = payloadB64 };

    /// <summary>Server -> client: there is no baseline to give, and why.</summary>
    public static Message BaselineFail(string reason)
        => new() { Type = MsgType.BaselineFail, Args = Array.Empty<string>(), Payload = reason };

    /// <summary>Server -> client, in the document: one level file the server holds - its version, hash and size,
    /// not its bytes. A client asks for the ones it does not have with <see cref="FileGet"/>.</summary>
    public static Message FileInfo(long seq, string hash, long size, string escapedPath)
        => new()
        {
            Type = MsgType.FileInfo,
            Args = new[] { seq.ToString(CultureInfo.InvariantCulture), hash, size.ToString(CultureInfo.InvariantCulture), escapedPath },
            Payload = "",
        };

    /// <summary>Client -> server: send me this level file.</summary>
    public static Message FileGet(string escapedPath)
        => new() { Type = MsgType.FileGet, Args = new[] { escapedPath }, Payload = "" };

    /// <summary>Server -> client: a requested file's bytes (base64), or "-" when the server does not have it.</summary>
    public static Message FileData(string escapedPath, string base64OrDash)
        => new() { Type = MsgType.FileData, Args = new[] { escapedPath }, Payload = base64OrDash };

    public string Encode()
    {
        return Type switch
        {
            MsgType.Join      => $"JOIN {Args[0]} {Args[1]}",
            MsgType.SyncBegin => $"SYNCBEGIN {Args[0]}",
            MsgType.SyncObj   => $"SYNCOBJ {Payload}",
            MsgType.SyncEnd   => "SYNCEND",
            MsgType.Op        => $"OP {Args[0]} {Args[1]} {Args[2]} {Payload}",
            MsgType.Presence  => $"PRESENCE {Args[0]} {Args[1]} {Args[2]} {Args[3]} {Args[4]} {(Args.Length > 5 ? Args[5] : "0")}",
            MsgType.Leave     => $"LEAVE {Args[0]}",
            MsgType.Error     => $"ERROR {Payload}",
            MsgType.SeedRequest => "SEEDREQ",
            MsgType.Auth      => $"AUTH {Payload}",
            MsgType.Base      => $"BASE {Args[0]} {Args[1]} {Args[2]}",
            MsgType.BaseOk    => $"BASEOK {Args[0]}",
            MsgType.BaseDiff  => $"BASEDIFF {Args[0]} {Args[1]} {Args[2]} {Args[3]}",
            MsgType.BaseNeed  => $"BASENEED {Args[0]}",
            MsgType.BaseGet   => $"BASEGET {Args[0]}",
            MsgType.BasePut   => $"BASEPUT {Args[0]} {Args[1]} {Args[2]}",
            MsgType.BaseData  => $"BASEDATA {Args[0]} {Args[1]} {Payload}",
            MsgType.BaseDone  => $"BASEDONE {Args[0]}",
            MsgType.MapList   => "MAPLIST",
            MsgType.MapInfo   => $"MAPINFO {Args[0]} {Args[1]} {Args[2]} {Args[3]} {Args[4]}",
            MsgType.MapEnd    => "MAPEND",
            MsgType.PickMap   => $"PICKMAP {Args[0]}",
            MsgType.MapOk     => $"MAPOK {Args[0]}",
            MsgType.Export       => "EXPORT",
            MsgType.ExportReady  => $"EXPORTREADY {Args[0]} {Args[1]} {Args[2]}",
            MsgType.ExportFailed => $"EXPORTFAILED {Payload}",
            MsgType.Version      => $"VERSION {Args[0]} {Args[1]} {Args[2]} {Payload}",
            MsgType.History      => $"HISTORY {Args[0]}",
            MsgType.Changes      => $"CHANGES {Args[0]} {Args[1]} {Args[2]} {Payload}",
            MsgType.Baseline     => "BASELINE",
            MsgType.BaselineData => $"BASELINEDATA {Payload}",
            MsgType.BaselineFail => $"BASELINEFAIL {Payload}",
            MsgType.FileInfo     => $"FILEINFO {Args[0]} {Args[1]} {Args[2]} {Args[3]}",
            MsgType.FileGet      => $"FILEGET {Args[0]}",
            MsgType.FileData     => $"FILEDATA {Args[0]} {Payload}",
            _ => throw new InvalidOperationException(),
        };
    }

    public static Message Decode(string line)
    {
        line = line.TrimEnd('\r', '\n');
        int sp = line.IndexOf(' ');
        string type = sp < 0 ? line : line[..sp];
        string rest = sp < 0 ? "" : line[(sp + 1)..];

        switch (type)
        {
            case "JOIN":
            {
                var p = rest.Split(' ', 2);
                return Join(p[0], p.Length > 1 ? p[1] : p[0]);
            }
            case "SYNCBEGIN": return SyncBegin(long.Parse(rest, CultureInfo.InvariantCulture));
            case "SYNCOBJ":   return SyncObj(rest);
            case "SYNCEND":   return SyncEnd();
            case "OP":
            {
                // OP <seq> <clientId> <localOpId> <editwire...>
                var p = rest.Split(' ', 4);
                return Op(long.Parse(p[0], CultureInfo.InvariantCulture), p[1],
                          long.Parse(p[2], CultureInfo.InvariantCulture), p[3]);
            }
            case "PRESENCE":
            {
                var p = rest.Split(' ', 6);
                float heading = p.Length > 4 && float.TryParse(p[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var h) ? h : 0f;
                float pitch = p.Length > 5 && float.TryParse(p[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var pi) ? pi : 0f;
                return Presence(p[0], p[1], p[2], Vec3.Parse(p[3]), heading, pitch);
            }
            case "LEAVE":  return Leave(rest);
            case "ERROR":  return Error(rest);
            case "SEEDREQ": return SeedRequest();
            case "AUTH":   return Auth(rest);
            case "BASE":
            {
                var p = rest.Split(' ', 3);
                return Base(p[0], long.Parse(p[1], CultureInfo.InvariantCulture), p.Length > 2 ? p[2] : "level");
            }
            case "BASEOK":   return BaseOk(rest.Trim());
            case "BASEDIFF":
            {
                var p = rest.Split(' ', 4);
                return BaseDiff(p[0], long.Parse(p[1], CultureInfo.InvariantCulture), p[2], p.Length > 3 && p[3] == "1");
            }
            case "BASENEED": return BaseNeed(rest.Trim());
            case "BASEGET":  return BaseGet(rest.Trim());
            case "BASEPUT":
            {
                var p = rest.Split(' ', 3);
                return BasePut(p[0], long.Parse(p[1], CultureInfo.InvariantCulture), p.Length > 2 ? p[2] : "level");
            }
            case "BASEDATA":
            {
                var p = rest.Split(' ', 3);
                return BaseData(p[0], int.Parse(p[1], CultureInfo.InvariantCulture), p.Length > 2 ? p[2] : "");
            }
            case "BASEDONE": return BaseDone(rest.Trim());
            case "MAPLIST":  return MapList();
            case "MAPINFO":
            {
                var p = rest.Split(' ', 5);
                return MapInfo(p[0], int.Parse(p[1], CultureInfo.InvariantCulture), p[2] == "1",
                               long.Parse(p[3], CultureInfo.InvariantCulture),
                               p.Length > 4 ? int.Parse(p[4], CultureInfo.InvariantCulture) : 0);
            }
            case "MAPEND":   return MapEnd();
            case "PICKMAP":  return PickMap(rest.Trim());
            case "MAPOK":    return MapOk(rest.Trim());
            case "EXPORT":   return Export();
            case "EXPORTREADY":
            {
                var p = rest.Split(' ', 3);
                return ExportReady(p[0], long.Parse(p[1], CultureInfo.InvariantCulture), p.Length > 2 ? p[2] : "map");
            }
            case "EXPORTFAILED": return ExportFailed(rest);
            case "VERSION":
            {
                var p = rest.Split(' ', 4);
                return Version(long.Parse(p[0], CultureInfo.InvariantCulture), p[1],
                               long.Parse(p[2], CultureInfo.InvariantCulture), p.Length > 3 ? p[3] : "");
            }
            case "HISTORY": return History(long.Parse(rest.Trim(), CultureInfo.InvariantCulture));
            case "CHANGES":
            {
                var p = rest.Split(' ', 4);
                return Changes(long.Parse(p[0], CultureInfo.InvariantCulture), long.Parse(p[1], CultureInfo.InvariantCulture),
                               p[2] == "1", p.Length > 3 ? p[3] : "");
            }
            case "BASELINE":     return Baseline();
            case "BASELINEDATA": return BaselineData(rest);
            case "BASELINEFAIL": return BaselineFail(rest);
            case "FILEINFO":
            {
                var p = rest.Split(' ', 4);
                return FileInfo(long.Parse(p[0], CultureInfo.InvariantCulture), p[1],
                                long.Parse(p[2], CultureInfo.InvariantCulture), p[3]);
            }
            case "FILEGET":  return FileGet(rest.Trim());
            case "FILEDATA":
            {
                var p = rest.Split(' ', 2);
                return FileData(p[0], p.Length > 1 ? p[1] : "-");
            }
            default: throw new FormatException($"Unknown message '{type}'");
        }
    }
}
