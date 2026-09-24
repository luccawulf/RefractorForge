using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace RefractorForge.Formats.Menu;

/// <summary>How <see cref="MemeFile.ToBytes"/> writes the string table. The engine never cares about the order - it only
/// indexes into the table - but a byte-exact writer must.</summary>
public enum MemeTableMode
{
    /// <summary><see cref="Keep"/> when every string the tree needs is already in the table, so an unchanged file (or
    /// one whose edits only touch values) is byte-exact whatever its order. Otherwise a file that was first-use
    /// ordered gets <see cref="Rebuild"/> (what DICE's writer would produce after the edit) and a tool-ordered one
    /// Keep + append (MenuEditor's behaviour); a table over 511 strings is rebuilt if it can be, refused if not.</summary>
    Auto,
    /// <summary>The table as read - its order, duplicates and unused entries - with any new string appended. Byte-exact
    /// for every unchanged file (1,693 / 1,693); what DICE's MDT MenuEditor 1.30 does ("unused names are left").</summary>
    Keep,
    /// <summary>A fresh table in first-use order: the root class, then per record in file order its object name and
    /// its class name, each at first use - the rule all 186 retail files follow. Refused for a tree with opaque
    /// payloads, whose nested headers hold indices that cannot be remapped.</summary>
    Rebuild,
}

public enum MemeWarningKind
{
    /// <summary>A bool not 0/1, a float not finite or outside 1e-6..1e7, a control character in a string, a lone
    /// surrogate.</summary>
    Implausible,
    /// <summary>A child whose class does not derive from what its slot asks for (the loader does not enforce it).</summary>
    Type,
    /// <summary>A null or reference record longer than its header: bytes the engine never reads.</summary>
    ClasslessSize,
    /// <summary>Bytes after the root payload.</summary>
    Trailing,
}

/// <summary>A plausibility note from <see cref="MemeFile.Parse"/>; never fatal.</summary>
public sealed record MemeWarning(MemeWarningKind Kind, int Offset, string Text)
{
    /// <summary>The reference implementation's name for the kind (IMPLAUSIBLE, TYPE, CLASSLESS_SIZE, TRAILING).</summary>
    public string Code => Kind switch
    {
        MemeWarningKind.Implausible => "IMPLAUSIBLE", MemeWarningKind.Type => "TYPE",
        MemeWarningKind.ClasslessSize => "CLASSLESS_SIZE", _ => "TRAILING",
    };

    public override string ToString() => $"{Code} at 0x{Offset:X}: {Text}";
}

public enum MemeLintKind
{
    /// <summary>A later copy of a named record differs from the first, which is the only one the engine uses.</summary>
    DuplicateDiffers,
    /// <summary>A by-name reference to a name this file only defines later: the engine then tries to load a menu FILE
    /// of that name.</summary>
    RefBeforeDef,
    /// <summary>Two names equal up to / versus \ - one registry path in game.</summary>
    PathAlias,
    /// <summary>A bool byte other than 0/1; the engine reads it as 1.</summary>
    BoolNot01,
    /// <summary>Records nested deeper than the limit: a stack-overflow risk in game.</summary>
    Depth,
}

/// <summary>An editor-level finding (<see cref="MemeFile.Lint"/>): the file is well-formed, but the engine reads it
/// differently from what an editor shows.</summary>
public sealed record MemeLintFinding(MemeLintKind Kind, MemeRecord Record, string Text)
{
    /// <summary>The reference implementation's name (DUPLICATE_DIFFERS, REF_BEFORE_DEF, PATH_ALIAS, BOOL_NOT_01, DEPTH).</summary>
    public string Code => Kind switch
    {
        MemeLintKind.DuplicateDiffers => "DUPLICATE_DIFFERS", MemeLintKind.RefBeforeDef => "REF_BEFORE_DEF",
        MemeLintKind.PathAlias => "PATH_ALIAS", MemeLintKind.BoolNot01 => "BOOL_NOT_01", _ => "DEPTH",
    };

    public override string ToString() => $"{Code}: {Text}";
}

/// <summary>What <see cref="MemeFile.Parse"/> found, counted as the reference implementation counts (a record that
/// turned out opaque keeps the counts of what was read inside it first).</summary>
public sealed class MemeStats
{
    public int Records { get; internal set; }
    public int Objects { get; internal set; }
    public int Nulls { get; internal set; }
    public int Refs { get; internal set; }
    public int Opaque { get; internal set; }
    public int OpaqueBytes { get; internal set; }
    /// <summary>Bytes of null / reference records beyond their header, which the engine skips.</summary>
    public int DeadBytes { get; internal set; }
    /// <summary>Values checked for plausibility.</summary>
    public int Values { get; internal set; }
    /// <summary>Decoded objects per short class name.</summary>
    public Dictionary<string, int> Classes { get; } = new(StringComparer.Ordinal);

    internal void Count(string cls) => Classes[cls] = Classes.TryGetValue(cls, out var n) ? n + 1 : 1;
}

/// <summary>
/// A Refractor "Meme 2.0" menu file: an entry of <c>menu.rfa</c> under <c>menu/</c>, with no extension
/// (<c>menu/InGame</c> is the HUD, <c>menu/MainMenu</c>, <c>menu/ButtonNavigationLayer</c>) - the whole menu and HUD
/// system of BF1942 and BF Vietnam: screens, buttons, gauges, the variables that drive them, the actions buttons run.
/// The spec is <c>docs/Meme_Format.md</c>; this is a faithful port of its reference reader and writer,
/// <c>tools/meme/meme.py</c>.
/// <code>
/// file         := string_table  u16 root_class_index  root_payload
/// string_table := { u8 len (1..255) ; len bytes latin1 }*  0x00      strs[0] = "MemeFile 2.0" (byte 0 is 0x0C)
/// record       := u32 size ; u16 name_index ; u16 class_index ; payload[size - 8]
/// </code>
/// A u16 index <c>i</c> names <c>strs[i]</c>, so 0 means none; the root is not a record (a class index, then its
/// payload to EOF). Every retail file (94 BF1942 including both expansions, 92 BFV) parses with every byte accounted for
/// and is written back byte-exact, and so is every one of 1,505 community files - all but two (damaged data, not open
/// layouts) fully decoded.
/// </summary>
public sealed class MemeFile
{
    public const string Magic = "MemeFile 2.0";

    /// <summary>At most 511 strings after the magic: loadFile's ClassIStream holds <c>table[512]</c> with no bounds check
    /// in its fill loop (BFV 0x0087A1E7, BF1942 0x007F7E47), and a 512th string puts the terminator on the return
    /// address. The largest file of the whole corpus has 465 (BFV InGame).</summary>
    public const int MaxStrings = 511;

    private List<MemeWarning> _warnings = new();
    private MemeStats _stats = new();

    /// <summary>A file with this root (a record with a class; its name is not written). Its table is built on the first
    /// write.</summary>
    public MemeFile(MemeGame game, MemeRecord root)
    {
        Game = game;
        Root = root ?? throw new ArgumentNullException(nameof(root));
    }

    internal MemeFile(MemeGame game, MemeRecord root, IReadOnlyList<string>? strings, bool oldFormat) : this(game, root)
    {
        Strings = strings;
        IsOldFormat = oldFormat;
    }

    public MemeGame Game { get; }
    public MemeSchema Schema => MemeSchema.For(Game);

    /// <summary>The root object: a class and its payload, no name and no size. <c>NameNode</c> (1,371 corpus files) or
    /// <c>PathNode</c> (322).</summary>
    public MemeRecord Root { get; set; }

    /// <summary>The string table as read, the magic at index 0 - its order, duplicates and unused entries included;
    /// null for a new file or one in the old format.</summary>
    public IReadOnlyList<string>? Strings { get; }

    /// <summary>How the table as read was ordered, classified as the reference classifies it: <c>first-use</c> (every
    /// retail file), <c>first-use + N unused at the end</c>, <c>other order</c> (a third-party tool's), <c>invalid</c>,
    /// <c>unknown (opaque payloads)</c>; null for a new or old-format file. <see cref="MemeTableMode.Auto"/> decides by it.</summary>
    public string? TableRule { get; internal set; }

    /// <summary>The engine's fallback format, tried when the magic is missing: no table, and every name an inline u8-length
    /// string (root class, then per record <c>u32 size; name; class</c>). Engine-proven; no sample exists.</summary>
    public bool IsOldFormat { get; }

    /// <summary>Bytes after the root payload (never seen; kept for exactness).</summary>
    public byte[] Trailing { get; set; } = Array.Empty<byte>();

    /// <summary>Plausibility notes from the read; never fatal.</summary>
    public IReadOnlyList<MemeWarning> Warnings => _warnings;

    /// <summary>What the read found (as read; edits do not update it).</summary>
    public MemeStats Stats => _stats;

    /// <summary>Every byte of the file was decoded as read: no opaque record, no dead bytes, nothing trailing.</summary>
    public bool IsFullyDecoded => _stats.Opaque == 0 && _stats.DeadBytes == 0 && Trailing.Length == 0;

    internal void SetRead(List<MemeWarning> warnings, MemeStats stats)
    {
        _warnings = warnings;
        _stats = stats;
        TableRule = IsOldFormat ? null : ComputeTableRule();
    }

    // ---- read / write --------------------------------------------------------------------------------------------

    /// <summary>Byte 0 is 0x0C and bytes 1..12 are <c>MemeFile 2.0</c>.</summary>
    public static bool IsMeme(ReadOnlySpan<byte> data) =>
        data.Length >= 13 && data[0] == 12 && data.Slice(1, 12).SequenceEqual("MemeFile 2.0"u8);

    /// <summary>
    /// Read a menu file. Never throws for a damaged record: it is kept opaque, with its raw payload, and reading goes on
    /// after it, as the engine seeks past a record whose read differs from its size. Throws
    /// <see cref="InvalidDataException"/> only when the bytes are not a menu file at all (no magic and not the old
    /// format, a string table running past the end, no root class). <paramref name="plausible"/> adds the
    /// <see cref="Warnings"/>.
    /// </summary>
    public static MemeFile Parse(byte[] bytes, MemeGame game, bool plausible = true)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return DeepStack.Run(() => MemeReader.Parse(bytes, game, plausible));
    }

    /// <summary>A new file whose root is a fresh <paramref name="rootClass"/> (NameNode or PathNode in every corpus file).</summary>
    public static MemeFile Create(MemeGame game, string rootClass = "NameNode") => new(game, MemeRecord.New(game, rootClass));

    /// <summary>
    /// The file's bytes; record sizes are always recomputed from the content. An unchanged file comes back byte-exact
    /// in <see cref="MemeTableMode.Auto"/> and <see cref="MemeTableMode.Keep"/>, and a first-use file (all retail) in
    /// <see cref="MemeTableMode.Rebuild"/> too.
    /// <para>Throws <see cref="InvalidOperationException"/>, naming the record's path, for what the engine would read as
    /// something else: more than 511 strings; a name of 128+ bytes or a class name of 64+ (the root's too); a NUL in a
    /// name, an s8, a str32 or a wstr32; a null record inside a LIST; a u8/bool outside 0..255 or not an integer, an
    /// i32 outside -2^31..2^32-1, a float that does not fit f32; a record that is its own descendant; a class with
    /// neither fields nor raw payload, or fields without a class; BfVerticalScrollExNode Version other than 1; a TREE
    /// node the grammar cannot hold; a decoded object whose class this game does not register or cannot resolve, or
    /// whose fields differ from the layout. Opaque records are written verbatim.</para>
    /// </summary>
    public byte[] ToBytes(MemeTableMode mode = MemeTableMode.Auto) => DeepStack.Run(() => MemeWriter.Serialize(this, mode));

    /// <summary>A copy (sharing the tree) that writes in the engine's old, table-less format. Refused when the tree
    /// holds opaque payloads: their nested headers carry u16 table indices the old format has no table for.</summary>
    public MemeFile ToOldFormat()
    {
        if (!IsOldFormat && HasOpaque())
            throw new InvalidOperationException("cannot convert to the old format: the tree has opaque payloads");
        return new MemeFile(Game, Root, null, oldFormat: true) { Trailing = Trailing };
    }

    /// <summary>A copy (sharing the tree) that writes in Meme 2.0 with a fresh first-use table.</summary>
    public MemeFile ToMeme2Format() => new(Game, Root, null, oldFormat: false) { Trailing = Trailing };

    // ---- traversal -----------------------------------------------------------------------------------------------

    /// <summary>Every record, depth first in file order, the root first - null pointers, references, list terminators
    /// and tree slots included. A record shared by several slots (a DAG) is visited once per slot, as the writer writes
    /// it; a record that is its own descendant (a cycle, which no file can hold) throws
    /// <see cref="InvalidOperationException"/> instead of looping forever.</summary>
    public IEnumerable<MemeRecord> Walk()
    {
        yield return Root;
        var path = new HashSet<MemeRecord>(ReferenceEqualityComparer.Instance) { Root };
        var stack = new Stack<(MemeRecord Rec, IEnumerator<MemeRecord> It)>();
        stack.Push((Root, Root.Children().ToList().GetEnumerator()));
        while (stack.Count > 0)
        {
            var (r, it) = stack.Peek();
            if (!it.MoveNext())
            {
                stack.Pop();
                path.Remove(r);
                continue;
            }
            var c = it.Current;
            if (path.Contains(c))
                throw new InvalidOperationException($"cycle: {c} is its own descendant (via {r}); a menu file is a tree");
            yield return c;
            path.Add(c);
            stack.Push((c, c.Children().ToList().GetEnumerator()));
        }
    }

    /// <summary>Every object (decoded or opaque) named <paramref name="name"/>, in file order, each once. Names are
    /// identity: the engine registers the first and skips the payload of every later one, so an edit belongs on all of
    /// them (<see cref="MemeEdits.SetEverywhere"/>).</summary>
    public IReadOnlyList<MemeRecord> CopiesOf(string name)
    {
        var seen = new HashSet<MemeRecord>(ReferenceEqualityComparer.Instance);
        var list = new List<MemeRecord>();
        if (string.IsNullOrEmpty(name)) return list;
        foreach (var r in Walk())
            if (r.Kind is MemeRecordKind.Object or MemeRecordKind.Opaque && r.Name == name && r != Root && seen.Add(r))
                list.Add(r);
        return list;
    }

    internal bool HasOpaque()
    {
        bool any = false;
        foreach (var r in Walk())       // the whole walk: a cycle throws even after an opaque record
            if (r.Kind == MemeRecordKind.Opaque || (r.Raw is { Length: > 0 } && r.Kind is MemeRecordKind.Null or MemeRecordKind.Ref))
                any = true;
        return any;
    }

    /// <summary>The strings in the order a depth-first write first uses them: the root class, then per record header
    /// its object name and then its class name (pre-order, file order). Opaque payloads are not looked into. This is
    /// the table DICE's writer emits and <see cref="MemeTableMode.Rebuild"/> writes.</summary>
    public IReadOnlyList<string> FirstUseStrings() => DeepStack.Run(() =>
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<string>();
        void Use(string? s)
        {
            if (!string.IsNullOrEmpty(s) && seen.Add(s)) order.Add(s);
        }
        void Fields(List<MemeField>? fields)
        {
            if (fields is null) return;
            foreach (var f in fields)
            {
                switch (f.Token)
                {
                    case MemeToken.O: Rec(f.Value as MemeRecord); break;
                    case MemeToken.List:
                        if (f.Value is IEnumerable<MemeRecord?> items) foreach (var r in items) Rec(r);
                        if (f.ListEnd is not null) Rec(f.ListEnd);
                        break;
                    case MemeToken.Tree: Tree(f.Value as MemeTreeNode); break;
                }
            }
        }
        void Tree(MemeTreeNode? n)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            if (n is null) return;
            Fields(n.Fields);
            Tree(n.Child);
            Tree(n.Next);
            Tree(n.Prev);
        }
        void Rec(MemeRecord? r)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            if (r is null) return;          // C# null in an O slot = a null pointer
            Use(r.Name);
            Use(r.ClassName);
            Fields(r.Fields);
        }
        Use(Root.ClassName);
        Fields(Root.Fields);
        return (IReadOnlyList<string>)order;
    });

    private string? ComputeTableRule()
    {
        if (Strings is null) return null;
        if (HasOpaque()) return "unknown (opaque payloads)";
        var fu = FirstUseStrings();
        var t = Strings.Skip(1).ToList();
        if (t.SequenceEqual(fu)) return "first-use";
        if (t.Count >= fu.Count && t.Take(fu.Count).SequenceEqual(fu)) return $"first-use + {t.Count - fu.Count} unused at the end";
        if (fu.All(new HashSet<string>(t, StringComparer.Ordinal).Contains))
            return "other order" + (t.Count > fu.Count ? $" (+{t.Count - fu.Count} unused)" : "");
        return "invalid";
    }

    // ---- lint ----------------------------------------------------------------------------------------------------

    /// <summary>Retail files never nest deeper than 278 levels.</summary>
    public const int LintMaxDepth = 1000;

    /// <summary>
    /// Editor-level checks of a file the writer can write but the engine may read differently from what an editor
    /// shows (nothing here is refused by <see cref="ToBytes"/>):
    /// <list type="bullet">
    /// <item><b>DUPLICATE_DIFFERS</b>: a named record whose content differs from the FIRST record of that name in file
    /// order. The engine registers names globally, uses the first definition and skips every later payload (findObject
    /// then seek, BFV 0x00874FBA / BF1942 0x007ED59A), so the edit is lost in game. Retail: 0; community: 967.</item>
    /// <item><b>REF_BEFORE_DEF</b>: a by-name reference to a name this file only defines later; at that point
    /// findObject fails and the engine tries loadFile(name) instead (BFV 0x0087508A).</item>
    /// <item><b>PATH_ALIAS</b>: two names that differ only in / versus \. The registry splits a name into folder and
    /// leaf at its last / or \ (BFV registerObject 0x00869020), so A/B and A\B are one object in game (how the folder
    /// part compares was not traced: deeper paths may be over-reported).</item>
    /// <item><b>BOOL_NOT_01</b>: a bool byte other than 0/1; the engine reads any non-zero byte as true (BFV 0x00878E00).</item>
    /// <item><b>DEPTH</b>: records nested deeper than <paramref name="maxDepth"/>. The engine reads nesting recursively
    /// (roughly 0x180 bytes of its 1 MB main stack per level, inferred) and a Next-node chain nests one level per
    /// node.</item>
    /// </list>
    /// </summary>
    public IReadOnlyList<MemeLintFinding> Lint(int maxDepth = LintMaxDepth) => DeepStack.Run(() => LintCore(maxDepth));

    private List<MemeLintFinding> LintCore(int maxDepth)
    {
        var findings = new List<MemeLintFinding>();
        var order = Walk().ToList();     // throws on a cycle
        var sig = new Signatures();
        var definedAt = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < order.Count; i++)
        {
            var r = order[i];
            if (r.Kind is MemeRecordKind.Object or MemeRecordKind.Opaque && r.Name.Length > 0 && r != Root)
                definedAt.TryAdd(r.Name, i);
        }
        var first = new Dictionary<string, MemeRecord>(StringComparer.Ordinal);
        var alias = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < order.Count; i++)
        {
            var r = order[i];
            if (r.Kind is MemeRecordKind.Object or MemeRecordKind.Opaque && r.Name.Length > 0 && r != Root)
            {
                if (first.TryGetValue(r.Name, out var f0))
                {
                    if (f0 != r && (f0.ClassName != r.ClassName || !sig.Of(f0).AsSpan().SequenceEqual(sig.Of(r))))
                        findings.Add(new MemeLintFinding(MemeLintKind.DuplicateDiffers, r,
                            $"'{r.Name}': this {r.Short} differs from the first definition ({f0.Short}); the engine uses the first"));
                }
                else first[r.Name] = r;
                var a = r.Name.Replace('\\', '/');
                if (alias.TryGetValue(a, out var other) && other != r.Name)
                    findings.Add(new MemeLintFinding(MemeLintKind.PathAlias, r, $"'{other}' and '{r.Name}' are the same registry path in game"));
                alias.TryAdd(a, r.Name);
            }
            else if (r.Kind == MemeRecordKind.Ref && definedAt.TryGetValue(r.Name, out var at) && at > i)
                findings.Add(new MemeLintFinding(MemeLintKind.RefBeforeDef, r,
                    $"reference to '{r.Name}' before its definition in this file: the engine looks for a menu file of that name instead"));
            if (r.Fields is not null)
                foreach (var f in r.Fields)
                    if (f.Token == MemeToken.Bool && !(MemeValues.TryInteger(f.Value, out var b) && b is 0 or 1))
                        findings.Add(new MemeLintFinding(MemeLintKind.BoolNot01, r, $"{r.Short}.{f.Label} = {f.Value} (the engine reads it as 1)"));
        }
        // nesting depth (the root is level 0)
        var stack = new Stack<(MemeRecord, int)>();
        stack.Push((Root, 0));
        (int Depth, MemeRecord Rec) deepest = (0, Root);
        while (stack.Count > 0)
        {
            var (r, d) = stack.Pop();
            if (d > deepest.Depth) deepest = (d, r);
            foreach (var c in r.Children()) stack.Push((c, d + 1));
        }
        if (deepest.Depth > maxDepth)
            findings.Add(new MemeLintFinding(MemeLintKind.Depth, deepest.Rec,
                $"records nest {deepest.Depth} levels deep (retail at most 278; limit here {maxDepth})"));
        return findings;
    }

    /// <summary>A content hash per record, memoised by identity: class, name, kind, then the raw payload or every field
    /// (child records by their own hash). Two copies of a named object are the same object exactly when these agree.</summary>
    private sealed class Signatures
    {
        private readonly Dictionary<MemeRecord, byte[]> _memo = new(ReferenceEqualityComparer.Instance);
        private static readonly byte[] NullSig = Hash(new MemeRecord());

        public byte[] Of(MemeRecord? r)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            if (r is null) return NullSig;
            if (_memo.TryGetValue(r, out var s)) return s;
            var ms = new MemoryStream();
            Header(ms, r);
            if (r.Fields is null) Blob(ms, r.Raw ?? Array.Empty<byte>());
            else Fields(ms, r.Fields);
            s = SHA256.HashData(ms.GetBuffer().AsSpan(0, (int)ms.Length));
            _memo[r] = s;
            return s;
        }

        private static byte[] Hash(MemeRecord r)
        {
            var ms = new MemoryStream();
            Header(ms, r);
            Blob(ms, r.Raw ?? Array.Empty<byte>());
            return SHA256.HashData(ms.ToArray());
        }

        private static void Header(MemoryStream ms, MemeRecord r)
        {
            Text(ms, r.ClassName ?? "\0none");
            Text(ms, r.Name);
            ms.WriteByte((byte)r.Kind);
        }

        private void Fields(MemoryStream ms, List<MemeField> fields)
        {
            foreach (var f in fields)
            {
                ms.WriteByte((byte)f.Token);
                switch (f.Value)
                {
                    case MemeRecord rec: ms.Write(Of(rec)); break;
                    case null when f.Token == MemeToken.O: ms.Write(NullSig); break;
                    case IEnumerable<MemeRecord?> items when f.Token == MemeToken.List:
                        foreach (var x in items) ms.Write(Of(x));
                        ms.WriteByte((byte)'|');
                        ms.Write(Of(f.ListEnd));
                        break;
                    case MemeTreeNode n: Tree(ms, n); break;
                    case float fl: Span4(ms, BitConverter.SingleToInt32Bits(fl)); break;
                    case string str: Text(ms, str); break;
                    default: Text(ms, f.Value is null ? "\0null" : Convert.ToString(f.Value, System.Globalization.CultureInfo.InvariantCulture) ?? ""); break;
                }
            }
        }

        private void Tree(MemoryStream ms, MemeTreeNode? n)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            if (n is null) { ms.WriteByte(0); return; }
            ms.WriteByte(n.Flag == 0 ? (byte)1 : n.Flag);
            Fields(ms, n.Fields);
            Tree(ms, n.Child);
            Tree(ms, n.Next);
            Tree(ms, n.Prev);
        }

        private static void Span4(MemoryStream ms, int v)
        {
            Span<byte> b = stackalloc byte[4];
            BitConverter.TryWriteBytes(b, v);
            ms.Write(b);
        }

        private static void Text(MemoryStream ms, string s)
        {
            Span4(ms, s.Length);
            ms.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(s.AsSpan()));
        }

        private static void Blob(MemoryStream ms, byte[] b)
        {
            Span4(ms, b.Length);
            ms.Write(b);
        }
    }

    public override string ToString() =>
        $"{Game} {(IsOldFormat ? "old-format " : "")}menu file, root {Root.Short}, {Strings?.Count - 1 ?? 0} strings";
}
