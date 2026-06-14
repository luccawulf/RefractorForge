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
public enum MsgType { Join, SyncBegin, SyncObj, SyncEnd, Op, Presence, Leave, Error, SeedRequest, Auth }

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

    public static Message Presence(string clientId, string name, string selId, Vec3 cursor, float heading = 0f)
        => new() { Type = MsgType.Presence, Args = new[] { clientId, name, selId, cursor.ToString(), heading.ToString("0.####", CultureInfo.InvariantCulture) }, Payload = "" };

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

    public string Encode()
    {
        return Type switch
        {
            MsgType.Join      => $"JOIN {Args[0]} {Args[1]}",
            MsgType.SyncBegin => $"SYNCBEGIN {Args[0]}",
            MsgType.SyncObj   => $"SYNCOBJ {Payload}",
            MsgType.SyncEnd   => "SYNCEND",
            MsgType.Op        => $"OP {Args[0]} {Args[1]} {Args[2]} {Payload}",
            MsgType.Presence  => $"PRESENCE {Args[0]} {Args[1]} {Args[2]} {Args[3]} {Args[4]}",
            MsgType.Leave     => $"LEAVE {Args[0]}",
            MsgType.Error     => $"ERROR {Payload}",
            MsgType.SeedRequest => "SEEDREQ",
            MsgType.Auth      => $"AUTH {Payload}",
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
                var p = rest.Split(' ', 5);
                float heading = p.Length > 4 && float.TryParse(p[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var h) ? h : 0f;
                return Presence(p[0], p[1], p[2], Vec3.Parse(p[3]), heading);
            }
            case "LEAVE":  return Leave(rest);
            case "ERROR":  return Error(rest);
            case "SEEDREQ": return SeedRequest();
            case "AUTH":   return Auth(rest);
            default: throw new FormatException($"Unknown message '{type}'");
        }
    }
}
