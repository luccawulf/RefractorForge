using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace RefractorForge.Formats.Menu;

/// <summary>
/// The reader, a port of the reference <c>meme.py</c> reader: a damaged record never fails the file - it is kept
/// opaque (its raw payload) and reading goes on at start + size, exactly as the engine's record reader seeks to
/// start + size after a read that differs from the saved size (BFV 0x00874F10, BF1942 0x007ED4F0). Only a broken frame
/// (string table, root class) is fatal. Counts and warnings are kept the way the reference keeps them, including those
/// of the part of a record read before it turned out opaque.
/// </summary>
internal sealed class MemeReader
{
    private readonly byte[] _d;
    private readonly MemeSchema _s;
    private readonly bool _plausible;
    private string[] _strs = Array.Empty<string>();
    private bool _old;

    public readonly List<MemeWarning> Warnings = new();
    public readonly MemeStats Stats = new();

    private MemeReader(byte[] data, MemeSchema schema, bool plausible)
    {
        _d = data; _s = schema; _plausible = plausible;
    }

    private sealed class ParseError(string message) : Exception(message);

    public static MemeFile Parse(byte[] data, MemeGame game, bool plausible)
    {
        var r = new MemeReader(data, MemeSchema.For(game), plausible);
        try { return r.File(game); }
        catch (ParseError e) { throw new InvalidDataException(e.Message); }
    }

    private MemeFile File(MemeGame game)
    {
        var D = _d;
        string rcls;
        int q;
        int? rci;
        if (MemeFile.IsMeme(D))
        {
            var strs = new List<string>();
            int p = 0;
            while (true)
            {
                if (p >= D.Length) throw new ParseError("string table runs past EOF");
                int n = D[p];
                if (n == 0) break;
                if (p + 1 + n > D.Length) throw new ParseError("string table entry overruns EOF");
                strs.Add(Encoding.Latin1.GetString(D, p + 1, n));
                p += 1 + n;
            }
            _strs = strs.ToArray();
            int body = p + 1;
            if (body + 2 > D.Length) throw new ParseError("no root class index");
            rci = BinaryPrimitives.ReadUInt16LittleEndian(D.AsSpan(body));
            rcls = S(rci.Value);
            if (rcls.Length == 0) throw new ParseError("root class index 0");
            q = body + 2;
        }
        else
        {
            // the engine's fallback when the magic check fails ("Trying old format"): no table, inline u8-length names
            _old = true;
            (rcls, q) = InlineName(0, D.Length);
            if (!rcls.StartsWith("dice::", StringComparison.Ordinal))
                throw new ParseError($"neither a MemeFile 2.0 nor an old-format file (root class '{MemeValues.Clip(rcls)}')");
            rci = null;
        }
        var root = new MemeRecord(rcls) { Offset = q, ClassIndex = rci };
        var file = new MemeFile(game, root, _old ? null : _strs, _old);
        Stats.Records++;
        var cls = _s.Find(rcls);
        if (cls is null)
        {
            root.Raw = D[q..];
            root.Note = $"root class {rcls} is not registered in {game}";
            Stats.Opaque++;
            Stats.OpaqueBytes += D.Length - q;
        }
        else
        {
            root.Class = cls;
            try
            {
                int end = q;
                var fields = Payload(cls.Name, cls, cls.Fields, ref end, D.Length);
                root.Fields = fields;
                file.Trailing = D[end..];
                Stats.Objects++;
                Stats.Count(cls.Name);
                if (file.Trailing.Length > 0)
                    Warnings.Add(new MemeWarning(MemeWarningKind.Trailing, end, $"{file.Trailing.Length} bytes after the root payload"));
            }
            catch (ParseError e)
            {
                root.Raw = D[q..];
                root.Note = e.Message;
                Stats.Opaque++;
                Stats.OpaqueBytes += D.Length - q;
            }
        }
        root.Size = D.Length - q;
        file.SetRead(Warnings, Stats);
        return file;
    }

    private string S(int i)
    {
        if (i == 0) return "";
        if (i >= _strs.Length) throw new ParseError($"string index {i} beyond the table ({_strs.Length} strings)");
        return _strs[i];
    }

    private (string, int) InlineName(int q, int limit)
    {
        if (q >= limit) throw new ParseError($"old-format name overruns at 0x{q:X}");
        int n = _d[q];
        if (q + 1 + n > limit) throw new ParseError($"old-format name overruns at 0x{q:X}");
        return (Encoding.Latin1.GetString(_d, q + 1, n), q + 1 + n);
    }

    private MemeRecord Record(ref int pos, int limit)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        var D = _d;
        int start = pos;
        uint size;
        int hdr;
        string name, cname;
        int? ni, ci;
        if (_old)
        {
            if (start + 4 > limit) throw new ParseError($"record header overruns at 0x{start:X}");
            size = BinaryPrimitives.ReadUInt32LittleEndian(D.AsSpan(start));
            int q;
            (name, q) = InlineName(start + 4, limit);
            (cname, q) = InlineName(q, limit);
            hdr = q - start;
            ni = ci = null;
        }
        else
        {
            if (start + 8 > limit) throw new ParseError($"record header at 0x{start:X} overruns 0x{limit:X}");
            size = BinaryPrimitives.ReadUInt32LittleEndian(D.AsSpan(start));
            int n = BinaryPrimitives.ReadUInt16LittleEndian(D.AsSpan(start + 4));
            int c = BinaryPrimitives.ReadUInt16LittleEndian(D.AsSpan(start + 6));
            hdr = 8;
            name = S(n);
            cname = S(c);
            ni = n; ci = c;
        }
        if (size < hdr || start + (long)size > limit)
            throw new ParseError($"record at 0x{start:X}: size {size} outside 8..{limit - start}");
        int end = start + (int)size;
        var r = new MemeRecord(cname, name) { Offset = start, Size = (int)size, NameIndex = ni, ClassIndex = ci };
        pos = end;
        Stats.Records++;
        if (cname.Length == 0)
        {
            if (name.Length > 0) Stats.Refs++; else Stats.Nulls++;
            if (size != hdr)
            {
                r.Raw = D[(start + hdr)..end];
                Stats.DeadBytes += (int)size - hdr;
                Warnings.Add(new MemeWarning(MemeWarningKind.ClasslessSize, start,
                    $"{(name.Length > 0 ? "reference" : "null")} record of size {size}: {size - hdr} bytes the engine skips"));
            }
            return r;
        }
        var cls = _s.Find(cname);
        if (cls is null)
        {
            r.Raw = D[(start + hdr)..end];
            r.Note = $"class {cname} is not registered in {_s.Game}";
            Stats.Opaque++;
            Stats.OpaqueBytes += (int)size - hdr;
            return r;
        }
        r.Class = cls;
        try
        {
            int q = start + hdr;
            var fields = Payload(cls.Name, cls, cls.Fields, ref q, end);
            if (q != end)
                throw new ParseError($"{cls.Name} at 0x{start:X} consumed {q - start - hdr} of {size - hdr} payload bytes");
            r.Fields = fields;
        }
        catch (ParseError e)
        {
            r.Raw = D[(start + hdr)..end];
            r.Note = e.Message;
            Stats.Opaque++;
            Stats.OpaqueBytes += (int)size - hdr;
            return r;
        }
        Stats.Objects++;
        Stats.Count(cls.Name);
        return r;
    }

    /// <summary>The fields of <paramref name="defs"/> from <paramref name="q"/>. <paramref name="owner"/> is the class
    /// whose O slots are type-checked (null inside a tree node, as in the reference).</summary>
    private List<MemeField> Payload(string cls, MemeClass? owner, IReadOnlyList<MemeFieldDef> defs, ref int q, int limit)
    {
        var D = _d;
        var fields = new List<MemeField>(defs.Count);
        foreach (var def in defs)
        {
            int at = q;
            object? v;
            switch (def.Token)
            {
                case MemeToken.O:
                {
                    var rec = Record(ref q, limit);
                    if (_plausible && owner is not null) CheckType(owner, def, rec);
                    fields.Add(new MemeField(def, rec));
                    continue;
                }
                case MemeToken.List:
                {
                    var items = new List<MemeRecord>();
                    MemeRecord? endRec = null;
                    while (true)
                    {
                        var k = Record(ref q, limit);
                        if (k.ClassName is null && k.Name.Length == 0)
                        {
                            if (k.Raw is { Length: > 0 }) endRec = k;
                            break;
                        }
                        items.Add(k);
                    }
                    fields.Add(new MemeField(def, items) { ListEnd = endRec });
                    continue;
                }
                case MemeToken.Tree:
                {
                    var layout = MemeSchema.TreeNodeLayout(def.TreeNode);
                    fields.Add(new MemeField(def, Tree(cls, layout, ref q, limit, 0)));
                    continue;
                }
                case MemeToken.U8:
                case MemeToken.Bool:
                    Need(q, 1, limit, cls, def.Label);
                    v = D[q];
                    q += 1;
                    break;
                case MemeToken.U16:
                    Need(q, 2, limit, cls, def.Label);
                    v = BinaryPrimitives.ReadUInt16LittleEndian(D.AsSpan(q));
                    q += 2;
                    break;
                case MemeToken.I32:
                    Need(q, 4, limit, cls, def.Label);
                    v = BinaryPrimitives.ReadInt32LittleEndian(D.AsSpan(q));
                    q += 4;
                    break;
                case MemeToken.U32As16:
                    Need(q, 4, limit, cls, def.Label);
                    v = BinaryPrimitives.ReadUInt32LittleEndian(D.AsSpan(q));
                    q += 4;
                    break;
                case MemeToken.F32:
                    Need(q, 4, limit, cls, def.Label);
                    v = BinaryPrimitives.ReadSingleLittleEndian(D.AsSpan(q));    // NaN payload bits kept as they are
                    q += 4;
                    break;
                case MemeToken.S8:
                {
                    Need(q, 1, limit, cls, def.Label);
                    int n = D[q];
                    Need(q + 1, n, limit, cls, def.Label);
                    v = Encoding.Latin1.GetString(D, q + 1, n);
                    q += 1 + n;
                    break;
                }
                case MemeToken.Str32:
                {
                    Need(q, 4, limit, cls, def.Label);
                    long n = BinaryPrimitives.ReadUInt32LittleEndian(D.AsSpan(q));
                    Need(q + 4, n, limit, cls, def.Label);
                    v = Encoding.Latin1.GetString(D, q + 4, (int)n);
                    q += 4 + (int)n;
                    break;
                }
                case MemeToken.WStr32:
                {
                    Need(q, 4, limit, cls, def.Label);
                    long n = BinaryPrimitives.ReadUInt32LittleEndian(D.AsSpan(q));
                    Need(q + 4, 2 * n, limit, cls, def.Label);
                    // the code units as they are: a decoder would replace a lone surrogate and the file would not come back
                    v = new string(MemoryMarshal.Cast<byte, char>(D.AsSpan(q + 4, 2 * (int)n)));
                    q += 4 + 2 * (int)n;
                    break;
                }
                default:
                    throw new ParseError($"unknown token {def.Token} in the schema of {cls}");
            }
            if (_plausible) CheckValue(cls, def, v, at);
            fields.Add(new MemeField(def, v));
        }
        return fields;
    }

    private static void Need(int q, long n, int limit, string cls, string label)
    {
        if (q + n > limit)
            throw new ParseError($"{cls}.{label}: {n} bytes at 0x{q:X} overrun the record end 0x{limit:X}");
    }

    /// <summary>kind 0 = TREE0 (child, next, prev), 1 = TREE1 (child, next), 2 = TREE2 (child, prev).</summary>
    private MemeTreeNode? Tree(string cls, IReadOnlyList<MemeFieldDef> layout, ref int q, int limit, int kind)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        Need(q, 1, limit, cls, "tree flag");
        byte flag = _d[q];
        q += 1;
        if (flag == 0) return null;
        var node = new MemeTreeNode { Flag = flag, Fields = Payload(cls + "<node>", null, layout, ref q, limit) };
        node.Child = Tree(cls, layout, ref q, limit, 0);
        if (kind is 0 or 1) node.Next = Tree(cls, layout, ref q, limit, 1);
        if (kind is 0 or 2) node.Prev = Tree(cls, layout, ref q, limit, 2);
        return node;
    }

    // ---- plausibility (warnings only) ----------------------------------------------------------------------------

    private void CheckValue(string cls, MemeFieldDef def, object v, int at)
    {
        string? bad = null;
        switch (def.Token)
        {
            case MemeToken.Bool when (byte)v > 1:
                bad = $"bool = {v}";
                break;
            case MemeToken.F32:
            {
                float f = (float)v;
                double a = Math.Abs((double)f);
                if (float.IsNaN(f) || float.IsInfinity(f) || (f != 0 && !(1e-6 <= a && a <= 1e7)))
                    bad = "float = " + MemeFormat.Float(f);
                break;
            }
            case MemeToken.S8:
            case MemeToken.Str32:
            {
                var s = (string)v;
                foreach (var c in s)
                    if (c < ' ' && c != '\t' && c != '\r' && c != '\n') { bad = $"control char in '{MemeValues.Clip(s)}'"; break; }
                break;
            }
            case MemeToken.WStr32:
                if (MemeValues.HasLoneSurrogate((string)v)) bad = "lone surrogate";
                break;
        }
        Stats.Values++;
        if (bad is not null)
            Warnings.Add(new MemeWarning(MemeWarningKind.Implausible, at, $"{cls}.{def.Label} {MemeFormat.Token(def.Token)}: {bad}"));
    }

    /// <summary>A decoded child whose class does not derive from what the slot asks for. The loader does not enforce
    /// the slot type (the record reader only checks isKindOf(Object), BFV 0x008752D0), so it is a note, never an error;
    /// all 1.46 M corpus records respect it but one (the MDT Default template's StringData in a Next node).</summary>
    private void CheckType(MemeClass owner, MemeFieldDef def, MemeRecord rec)
    {
        if (rec.Kind != MemeRecordKind.Object) return;
        var want = def.Expects;
        if (want is null || want == "Object") return;
        if (rec.Class!.IsA(want)) return;
        Warnings.Add(new MemeWarning(MemeWarningKind.Type, rec.Offset ?? 0,
                                     $"{owner.Name}.{def.Label} expects {want}, holds {rec.Short}"));
    }
}

/// <summary>Formatting shared by messages, the dump and JSON.</summary>
internal static class MemeFormat
{
    public static string Float(float f) => f.ToString("R", CultureInfo.InvariantCulture);

    public static string Token(MemeToken t) => t switch
    {
        MemeToken.O => "O", MemeToken.List => "LIST", MemeToken.F32 => "f32", MemeToken.I32 => "i32",
        MemeToken.U8 => "u8", MemeToken.Bool => "bool", MemeToken.S8 => "s8", MemeToken.Str32 => "str32",
        MemeToken.WStr32 => "wstr32", MemeToken.Tree => "TREE", MemeToken.U16 => "u16", MemeToken.U32As16 => "u32as16",
        _ => t.ToString(),
    };
}
