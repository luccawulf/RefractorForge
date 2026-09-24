using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace RefractorForge.Formats.Menu;

/// <summary>
/// The writer, a port of the reference <c>meme.py</c> serialize: sizes are recomputed from the content, the string
/// table is kept, rebuilt or kept-and-appended (<see cref="MemeTableMode"/>), and every model the engine would read as
/// something else is refused with an <see cref="InvalidOperationException"/> naming the record's path - none of those
/// occurs in any corpus file, so the corpus stays byte-exact (docs/Meme_Format.md section 8).
/// </summary>
internal sealed class MemeWriter
{
    private readonly bool _old;
    private readonly MemeGame _game;
    private readonly MemeSchema _schema;
    private readonly List<string> _table;
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
    private readonly bool _hints;
    private readonly List<string> _path = new();
    private byte[] _buf = new byte[4096];
    private int _n;

    private MemeWriter(List<string> table, bool old, MemeGame game, bool hints)
    {
        _table = table; _old = old; _game = game; _schema = MemeSchema.For(game); _hints = hints;
        for (int i = 1; i < table.Count; i++) _index.TryAdd(table[i], i);
    }

    public static byte[] Serialize(MemeFile mf, MemeTableMode table)
    {
        if (!Enum.IsDefined(table)) throw new ArgumentOutOfRangeException(nameof(table));
        bool opaque = mf.HasOpaque();          // walks the whole tree first: a cycle throws here, before any write
        if (mf.Root.ClassName is null) throw new InvalidOperationException("root: the root has no class");
        if (mf.IsOldFormat)
        {
            var w = new MemeWriter(new List<string>(), old: true, mf.Game, hints: false);
            w._path.Add("root");
            w.Check(mf.Root);
            w.Name(mf.Root.ClassName, null);
            w.Body(mf);
            return w._buf.AsSpan(0, w._n).ToArray();
        }
        var fu = mf.FirstUseStrings();
        var orig = mf.Strings;
        var mode = table;
        if (mode == MemeTableMode.Auto)
        {
            bool wasFirstUse = mf.TableRule?.StartsWith("first-use", StringComparison.Ordinal) == true;   // as read
            if (orig is { Count: > 0 } && fu.All(new HashSet<string>(orig.Skip(1), StringComparer.Ordinal).Contains))
                mode = MemeTableMode.Keep;        // unchanged strings: byte-exact whatever the order was
            else if (orig is { Count: > 0 } && (opaque || !wasFirstUse))
                mode = MemeTableMode.Keep;        // a tool-written order: keep it and append (what MenuEditor does)
            else
                mode = MemeTableMode.Rebuild;     // a new file, or a DICE-ordered one: write it the way DICE does
        }
        if (mode == MemeTableMode.Rebuild && opaque)
            throw new InvalidOperationException("cannot rebuild the string table: the tree has opaque payloads, whose nested headers hold indices into the old table");
        List<string> baseTable = mode == MemeTableMode.Rebuild
            ? new List<string> { MemeFile.Magic }.Concat(fu).ToList()
            : orig is { Count: > 0 } ? orig.ToList() : new List<string> { MemeFile.Magic };
        var wr = new MemeWriter(baseTable, old: false, mf.Game, hints: mode == MemeTableMode.Keep);
        wr._path.Add("root");
        wr.Check(mf.Root);
        wr.Name(mf.Root.ClassName, mode == MemeTableMode.Keep ? mf.Root.ClassIndex : null);
        wr.Body(mf);
        if (wr._table.Count - 1 > MemeFile.MaxStrings)
        {
            if (mode == MemeTableMode.Keep && !opaque && table == MemeTableMode.Auto)
                return Serialize(mf, MemeTableMode.Rebuild);
            throw new InvalidOperationException(
                $"{wr._table.Count - 1} strings after the magic; the engine holds at most {MemeFile.MaxStrings} (ClassIStream table[512] with no bounds check, BFV 0x0087A1E7)");
        }
        var tab = new List<byte>(wr._table.Sum(s => s.Length + 1) + 1);
        foreach (var s in wr._table)
        {
            if (s.Length is 0 or > 255)
                throw new InvalidOperationException($"string table entry of {s.Length} bytes: '{MemeValues.Clip(s)}'");
            tab.Add((byte)s.Length);
            foreach (var c in s)
            {
                if (c > 0xFF) throw new InvalidOperationException($"string table entry '{MemeValues.Clip(s)}' is not latin1");
                tab.Add((byte)c);
            }
        }
        tab.Add(0);
        var outBytes = new byte[tab.Count + wr._n];
        CollectionsMarshal.AsSpan(tab).CopyTo(outBytes);
        wr._buf.AsSpan(0, wr._n).CopyTo(outBytes.AsSpan(tab.Count));
        return outBytes;
    }

    private void Body(MemeFile mf)
    {
        if (mf.Root.Fields is not null) Fields(mf.Root.Fields, mf.Root);
        else if (mf.Root.Raw is not null) Bytes(mf.Root.Raw);
        Bytes(mf.Trailing);
    }

    // ---- strings -------------------------------------------------------------------------------------------------

    private int Sid(string s, int? hint)
    {
        if (s.Length == 0) return 0;
        if (_hints && hint is int h && h > 0 && h < _table.Count && _table[h] == s) return h;
        if (!_index.TryGetValue(s, out var i))
        {
            i = _table.Count;
            _index[s] = i;
            _table.Add(s);
        }
        return i;
    }

    private void Name(string? s, int? hint)
    {
        s ??= "";
        if (_old)
        {
            if (s.Length > 255) throw Error($"name longer than 255 bytes: '{MemeValues.Clip(s)}'");
            U8((byte)s.Length);
            foreach (var c in s) U8((byte)c);
            return;
        }
        int i = Sid(s, hint);
        if (i > ushort.MaxValue) throw Error($"string index {i} does not fit in 16 bits");
        U16((ushort)i);
    }

    // ---- checks --------------------------------------------------------------------------------------------------

    private string Where(MemeRecord? r = null) =>
        string.Join(" > ", _path) + (r is null || r.Kind is MemeRecordKind.Null ? "" : $" ({r})");

    private InvalidOperationException Error(string problem, MemeRecord? r = null) => new($"{Where(r)}: {problem}");

    /// <summary>Refuse a record the engine would read differently from the model.</summary>
    private void Check(MemeRecord r)
    {
        // the record reader strcpy's the names from the table into stack buffers of 0x80 / 0x40 bytes (BFV
        // 0x00874F6D/0x00874F8B via ClassIStream 0x0087A290, BF1942 0x007ED54D/0x007ED56B; the root class also into
        // 0x40, BFV 0x00874C4C) with no bound; retail maxima 67 / 42
        if (MemeValues.NameProblem(r.Name, 0x80, "object name") is { } n) throw Error(n, r);
        if (MemeValues.NameProblem(r.ClassName ?? "", 0x40, "class name") is { } c) throw Error(c, r);
        if (r.ClassName is null && r.Fields is not null)
            throw Error($"record '{r.Name}' has fields but no class: it would go out as a null pointer or a by-name reference followed by bytes the engine skips", r);
        if (r.ClassName is not null && r.Fields is null && r.Raw is null)
            throw Error($"record {r.ClassName} '{r.Name}' has neither fields nor a raw payload; build it with MemeRecord.New", r);
        if (r.ClassName is not null && r.Fields is not null)
        {
            // a decoded object must be a class of this game, spelled so the engine resolves it, with exactly its
            // schema layout: a class the exe does not register is read as a null pointer and skipped (a BFV-only node
            // pasted into a BF1942 file), and a hand-edited field list would be read back as other values
            var cls = _schema.Find(r.ClassName);
            if (cls is null)
                throw Error($"class '{r.ClassName}' is not a {_game} menu class; the engine would read a null pointer and skip it", r);
            if (!SameLayout(r.Fields, cls.Fields))
                throw Error($"its fields [{Layout(r.Fields.Select(f => (f.Token, f.Label)))}] do not match the {_game} layout of {cls.Name} [{Layout(cls.Fields.Select(f => (f.Token, f.Label)))}]", r);
            foreach (var d in cls.Fields)
            {
                if (d.MustEqual is not int eq) continue;
                var v = r.Fields[d.Index].Value;
                if (!MemeValues.TryInteger(v, out var x) || x != eq)
                    throw Error($"{cls.Name}.{d.Label} = {v}; the engine fails the read unless it is {eq} ({d.MustEqualWhy})", r);
            }
        }
    }

    private static bool SameLayout(List<MemeField> fields, IReadOnlyList<MemeFieldDef> defs)
    {
        if (fields.Count != defs.Count) return false;
        for (int i = 0; i < defs.Count; i++)
            if (fields[i].Token != defs[i].Token || !MemeLabels.Same(fields[i].Label, defs[i].Label)) return false;
        return true;
    }

    private static string Layout(IEnumerable<(MemeToken Token, string Label)> f) =>
        string.Join(", ", f.Take(8).Select(x => $"{MemeFormat.Token(x.Token)} {x.Label}"));

    // ---- records -------------------------------------------------------------------------------------------------

    private void Record(MemeRecord r)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        Check(r);
        int at = _n;
        U32(0);
        Name(r.Name, r.NameIndex);
        Name(r.ClassName, r.ClassIndex);
        if (r.Fields is not null) Fields(r.Fields, r);
        else if (r.Raw is not null) Bytes(r.Raw);
        BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(at), (uint)(_n - at));
    }

    private void Fields(List<MemeField> fields, MemeRecord owner)
    {
        foreach (var f in fields)
        {
            var v = f.Value;
            switch (f.Token)
            {
                case MemeToken.O:
                    _path.Add(f.Label);
                    if (v is not null and not MemeRecord) throw Error($"an O slot holds {v.GetType().Name}, not a record");
                    Record(v as MemeRecord ?? MemeRecord.Null());
                    _path.RemoveAt(_path.Count - 1);
                    break;
                case MemeToken.List:
                {
                    if (v is not IEnumerable<MemeRecord?> items) { _path.Add(f.Label); throw Error("a LIST holds records"); }
                    int i = 0;
                    foreach (var r in items)
                    {
                        _path.Add($"{f.Label}[{i++}]");
                        // the reader (engine 0x0087A6F0 and the reference) ends the list at the first null record, so
                        // a null item would cut the list short and shift every field after it
                        if (r is null || r.Kind == MemeRecordKind.Null)
                            throw Error("a null record cannot be a LIST item (it would end the list)");
                        Record(r);
                        _path.RemoveAt(_path.Count - 1);
                    }
                    _path.Add($"{f.Label}[end]");
                    if (f.ListEnd is { } end && end.Kind != MemeRecordKind.Null)
                        throw Error($"a LIST terminator must be a null record, not {end}");
                    Record(f.ListEnd ?? MemeRecord.Null());
                    _path.RemoveAt(_path.Count - 1);
                    break;
                }
                case MemeToken.U8:
                case MemeToken.Bool:
                    if (!MemeValues.TryByte(v, out var b))
                        throw Error($"{f.Label}: {MemeFormat.Token(f.Token)} value {v ?? "null"} is not an integer 0..255", owner);
                    U8(b);
                    break;
                case MemeToken.U16:
                    if (!MemeValues.TryInteger(v, out var u16) || u16 is < 0 or > ushort.MaxValue)
                        throw Error($"{f.Label}: u16 value {v ?? "null"} is not an integer 0..65535", owner);
                    U16((ushort)u16);
                    break;
                case MemeToken.I32:
                    // signed; 2^31..2^32-1 is accepted as the same 32 bits (it reads back as the negative value)
                    if (!MemeValues.TryI32(v, out var i32))
                        throw Error($"{f.Label}: i32 value {v ?? "null"} is not an integer that fits in 32 bits", owner);
                    U32(unchecked((uint)i32));
                    break;
                case MemeToken.U32As16:
                    if (!MemeValues.TryInteger(v, out var u32) || u32 is < 0 or > uint.MaxValue)
                        throw Error($"{f.Label}: u32as16 value {v ?? "null"} is not an integer 0..2^32-1", owner);
                    U32((uint)u32);
                    break;
                case MemeToken.F32:
                    if (!MemeValues.TryF32(v, out var fl, out var why)) throw Error($"{f.Label}: f32 value: {why}", owner);
                    Ensure(4);
                    BinaryPrimitives.WriteSingleLittleEndian(_buf.AsSpan(_n), fl);
                    _n += 4;
                    break;
                case MemeToken.S8:
                case MemeToken.Str32:
                case MemeToken.WStr32:
                {
                    if (v is not string s) throw Error($"{f.Label} ({MemeFormat.Token(f.Token)}): {v ?? "null"} is not a string", owner);
                    if (MemeValues.StringProblem(f.Token, s) is { } p) throw Error($"{f.Label} ({MemeFormat.Token(f.Token)}) {p}", owner);
                    if (f.Token == MemeToken.S8) U8((byte)s.Length);
                    else U32((uint)s.Length);
                    if (f.Token == MemeToken.WStr32) Bytes(MemoryMarshal.AsBytes(s.AsSpan()));
                    else Latin1(s);
                    break;
                }
                case MemeToken.Tree:
                    _path.Add(f.Label);
                    if (v is not null and not MemeTreeNode) throw Error($"a TREE holds a MemeTreeNode, not {v.GetType().Name}");
                    var layout = (f.Def ?? owner.Class?.Field(f.Label))?.TreeNode;
                    Tree(v as MemeTreeNode, 0, MemeSchema.TreeNodeLayout(layout), owner);
                    _path.RemoveAt(_path.Count - 1);
                    break;
                default:
                    throw Error($"{f.Label}: unknown token {f.Token}", owner);
            }
        }
    }

    private void Tree(MemeTreeNode? n, int kind, IReadOnlyList<MemeFieldDef> layout, MemeRecord owner)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (n is null) { U8(0); return; }
        // the grammar has no prev under a next-node and no next under a prev-node: refuse, never drop
        if (kind == 1 && n.Prev is not null) throw Error("a TREE next-node cannot hold a prev node (the grammar has no slot for it)");
        if (kind == 2 && n.Next is not null) throw Error("a TREE prev-node cannot hold a next node (the grammar has no slot for it)");
        if (!SameLayout(n.Fields, layout))
            throw Error($"a tree node's fields [{Layout(n.Fields.Select(f => (f.Token, f.Label)))}] do not match its node layout [{Layout(layout.Select(f => (f.Token, f.Label)))}]");
        U8(n.Flag == 0 ? (byte)1 : n.Flag);     // the flag byte says 'a node follows': 0 would end the tree here
        Fields(n.Fields, owner);
        _path.Add("child"); Tree(n.Child, 0, layout, owner); _path.RemoveAt(_path.Count - 1);
        if (kind is 0 or 1) { _path.Add("next"); Tree(n.Next, 1, layout, owner); _path.RemoveAt(_path.Count - 1); }
        if (kind is 0 or 2) { _path.Add("prev"); Tree(n.Prev, 2, layout, owner); _path.RemoveAt(_path.Count - 1); }
    }

    // ---- bytes ---------------------------------------------------------------------------------------------------

    private void Ensure(int more)
    {
        if (_n + more <= _buf.Length) return;
        long want = Math.Max((long)_buf.Length * 2, (long)_n + more);
        if (want > Array.MaxLength) throw new InvalidOperationException("the file would exceed 2 GB");
        Array.Resize(ref _buf, (int)want);
    }

    private void U8(byte b) { Ensure(1); _buf[_n++] = b; }
    private void U16(ushort v) { Ensure(2); BinaryPrimitives.WriteUInt16LittleEndian(_buf.AsSpan(_n), v); _n += 2; }
    private void U32(uint v) { Ensure(4); BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(_n), v); _n += 4; }

    private void Bytes(ReadOnlySpan<byte> b)
    {
        Ensure(b.Length);
        b.CopyTo(_buf.AsSpan(_n));
        _n += b.Length;
    }

    private void Latin1(string s)
    {
        Ensure(s.Length);
        _n += Encoding.Latin1.GetBytes(s, _buf.AsSpan(_n));
    }
}
