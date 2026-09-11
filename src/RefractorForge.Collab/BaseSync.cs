using RefractorForge.Formats.Rfa;

namespace RefractorForge.Collab;

/// <summary>
/// The client half of the base-archive handshake: announce what level archive you have open, learn what the
/// session is pinned to, and move the bytes either way.
///
/// It is a state machine over lines rather than a socket, so the two very different clients in this codebase
/// can share it - <see cref="CollabClient"/>, which owns a document and is what the tests drive, and the
/// editor's own session, which drains a queue of raw lines on the render thread. One implementation means the
/// two cannot drift apart on a protocol where drifting apart is exactly the failure being fixed.
///
/// A download is written to a <c>.part</c> beside its destination and only moved into place once its bytes
/// hash to the fingerprint the relay claimed, so a dropped or corrupted transfer never leaves a half-written
/// map where a real one belongs.
/// </summary>
public sealed class BaseSync
{
    private readonly Action<string> _send;
    private readonly object _gate = new();

    private FileStream? _in;
    private string? _partPath, _finalPath;
    private long _got, _expected;
    private int _next;

    public BaseSync(Action<string> send) => _send = send;

    /// <summary>What this client told the relay it is standing on, if anything.</summary>
    public LevelBase.Id? Mine { get; private set; }
    /// <summary>What the session is pinned to, once the relay has said.</summary>
    public LevelBase.Id? Session { get; private set; }
    /// <summary>True once the relay has confirmed this client is on the session's archive.</summary>
    public bool Agreed { get; private set; }
    /// <summary>Whether the relay holds the pinned archive and can therefore serve it.</summary>
    public bool Available { get; private set; }
    /// <summary>True while a download is being received.</summary>
    public bool Downloading { get { lock (_gate) return _in is not null; } }

    /// <summary>Bytes moved so far and expected, for a progress bar. Both zero when nothing is running.</summary>
    public (long Done, long Total) Moved { get { lock (_gate) return (_got, _expected); } }

    private volatile bool _cancelled;

    /// <summary>Abandon whatever is in flight. An upload stops sending on its next chunk. A download closes and
    /// deletes its part file; chunks that are already on their way are then dropped on arrival, because a
    /// BASEDATA with no open file is ignored. Nothing half-written survives either way.</summary>
    public void Cancel()
    {
        _cancelled = true;
        lock (_gate) Close(deletePart: true);
    }

    /// <summary>Raised when the relay confirms this client is on the session's archive. Worth saying out loud:
    /// "you are on the same map as everyone else" is the state people actually want confirmed, and until it was
    /// raised the menu could only ever report problems.</summary>
    public Action<LevelBase.Id?>? OnAgreed;

    public Action<LevelBase.Id, bool>? OnMismatch;
    public Action<LevelBase.Id>? OnWanted;
    public Action<long, long>? OnProgress;
    public Action<string>? OnDownloaded;
    public Action<string>? OnFailed;

    /// <summary>Identify the archive this client has open and tell the relay. Reads the whole file to hash it,
    /// so call it off the UI thread when the archive is large. Pass null when no level is open.</summary>
    public void Announce(string? archivePath)
    {
        try { Mine = archivePath is not null && File.Exists(archivePath) ? LevelBase.Identify(archivePath) : null; }
        catch { Mine = null; }
        var id = Mine ?? new LevelBase.Id("-", 0, "-");
        _send(Message.Base(id.Fingerprint, id.Bytes, id.LevelName).Encode());
    }

    /// <summary>Announce an archive this client is DERIVED from rather than the bytes it has: a copy of a server
    /// map that has been saved since it was synced no longer hashes to the map's archive, but its record says which
    /// archive it grew from, and that is what it is let in on.</summary>
    public void AnnounceAs(LevelBase.Id id)
    {
        Mine = id;
        _send(Message.Base(id.Fingerprint, id.Bytes, id.LevelName).Encode());
    }

    /// <summary>Ask the server to build the map as it stands now and send it as one archive. Same transfer as
    /// a base download; the difference is that what arrives includes everyone's edits, so it is the file you
    /// open in the editor or drop on a game server.</summary>
    public void RequestExport(string destinationPath)
    {
        lock (_gate)
        {
            Close(deletePart: true);
            _exportTo = destinationPath;
        }
        _send(Message.Export().Encode());
    }

    private string? _exportTo;

    /// <summary>Ask for the session's archive, writing it to <paramref name="destinationPath"/>.</summary>
    public void Download(string destinationPath)
    {
        var pin = Session;
        if (pin is null) { OnFailed?.Invoke("the session has not said which archive it is using yet"); return; }
        lock (_gate)
        {
            Close(deletePart: true);
            try
            {
                var dir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                _finalPath = destinationPath;
                _partPath = destinationPath + ".part";
                if (File.Exists(_partPath)) File.Delete(_partPath);
                _in = new FileStream(_partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
                _got = 0; _next = 0; _expected = pin.Bytes; _cancelled = false;
            }
            catch (Exception ex) { Close(deletePart: true); OnFailed?.Invoke(ex.Message); return; }
        }
        _send(Message.BaseGet(pin.Fingerprint).Encode());
    }

    /// <summary>Send this client's archive up so the relay can serve it to the next joiner. Blocking: it walks
    /// the whole file, so run it on a worker.</summary>
    public void Upload(string archivePath)
    {
        var id = Mine;
        if (id is null) { OnFailed?.Invoke("this client has no level archive to upload"); return; }
        try
        {
            _cancelled = false;
            lock (_gate) { _got = 0; _expected = id.Bytes; }
            _send(Message.BasePut(id.Fingerprint, id.Bytes, id.LevelName).Encode());
            using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            var buf = new byte[BaseArchiveStore.ChunkBytes];
            int index = 0; long sent = 0;
            while (true)
            {
                if (_cancelled) { OnFailed?.Invoke("upload cancelled"); return; }
                int n = fs.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                _send(Message.BaseData(id.Fingerprint, index++, Convert.ToBase64String(buf, 0, n)).Encode());
                sent += n;
                lock (_gate) _got = sent;
                OnProgress?.Invoke(sent, id.Bytes);
            }
            _send(Message.BaseDone(id.Fingerprint).Encode());
        }
        catch (Exception ex) { OnFailed?.Invoke(ex.Message); }
    }

    /// <summary>Feed one inbound message. Returns true when it was a base-archive message and has been dealt
    /// with, so a caller's own switch can ignore those cases.</summary>
    public bool Handle(Message m)
    {
        switch (m.Type)
        {
            case MsgType.BaseOk:
                Agreed = true;
                Available = true;
                if (m.Args[0] != "-" && Mine is not null) Session = Mine;
                OnAgreed?.Invoke(Session);
                return true;

            case MsgType.BaseDiff:
                Agreed = false;
                Session = new LevelBase.Id(m.Args[0], long.Parse(m.Args[1]), m.Args[2]);
                Available = m.Args[3] == "1";
                OnMismatch?.Invoke(Session, Available);
                return true;

            case MsgType.ExportReady:
            {
                // The export is a different file from the pinned base, so the download verifies against ITS
                // fingerprint. Session is repointed for the duration; a base download afterwards re-announces.
                string? failed = null;
                lock (_gate)
                {
                    if (_exportTo is null) return true;
                    Session = new LevelBase.Id(m.Args[0], long.Parse(m.Args[1]), m.Args[2]);
                    try
                    {
                        var dir = Path.GetDirectoryName(_exportTo);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        _finalPath = _exportTo;
                        _partPath = _exportTo + ".part";
                        if (File.Exists(_partPath)) File.Delete(_partPath);
                        _in = new FileStream(_partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
                        _got = 0; _next = 0; _expected = Session.Bytes; _cancelled = false;
                    }
                    catch (Exception ex) { Close(deletePart: true); failed = ex.Message; }
                    _exportTo = null;
                }
                if (failed is not null) OnFailed?.Invoke(failed);
                return true;
            }

            case MsgType.ExportFailed:
                lock (_gate) { Close(deletePart: true); _exportTo = null; }
                OnFailed?.Invoke(m.Payload);
                return true;

            case MsgType.BaseNeed:
                if (Mine is not null) OnWanted?.Invoke(Mine);
                return true;

            case MsgType.BaseData:
            {
                (long Done, long Total)? progress = null;
                string? failed = null;
                lock (_gate)
                {
                    if (_in is null || Session is null
                        || !m.Args[0].Equals(Session.Fingerprint, StringComparison.OrdinalIgnoreCase)) return true;
                    int idx = int.Parse(m.Args[1]);
                    if (idx != _next) { Close(deletePart: true); failed = "the download arrived out of order"; }
                    else
                        try
                        {
                            var data = Convert.FromBase64String(m.Payload);
                            _in.Write(data, 0, data.Length);
                            _got += data.Length; _next++;
                            progress = (_got, _expected);
                        }
                        catch (Exception ex) { Close(deletePart: true); failed = ex.Message; }
                }
                if (progress is not null) OnProgress?.Invoke(progress.Value.Done, progress.Value.Total);
                if (failed is not null) OnFailed?.Invoke(failed);
                return true;
            }

            case MsgType.BaseDone:
            {
                string? landed = null, failed = null;
                lock (_gate)
                {
                    if (_in is null || _partPath is null || _finalPath is null) return true;
                    string part = _partPath, final = _finalPath;
                    try { _in.Flush(); _in.Dispose(); } catch { }
                    _in = null;
                    try
                    {
                        string actual = LevelBase.Fingerprint(part);
                        if (Session is not null && actual.Equals(Session.Fingerprint, StringComparison.OrdinalIgnoreCase))
                        {
                            if (File.Exists(final)) File.Delete(final);
                            File.Move(part, final);
                            landed = final;
                            Mine = LevelBase.Identify(final);
                            Agreed = true;
                        }
                        else
                        {
                            if (File.Exists(part)) File.Delete(part);
                            failed = "the downloaded archive did not match its fingerprint, so it was discarded";
                        }
                    }
                    catch (Exception ex) { failed = ex.Message; }
                    Close(deletePart: false);
                }
                if (landed is not null) OnDownloaded?.Invoke(landed);
                if (failed is not null) OnFailed?.Invoke(failed);
                return true;
            }
        }
        return false;
    }

    private void Close(bool deletePart)
    {
        try { _in?.Dispose(); } catch { }
        _in = null;
        if (deletePart && _partPath is not null)
        {
            try { if (File.Exists(_partPath)) File.Delete(_partPath); } catch { }
        }
        _partPath = null; _finalPath = null; _got = 0; _next = 0;
    }
}
