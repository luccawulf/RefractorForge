using System.Globalization;
using System.Text.Json;

namespace RefractorForge.Formats.Menu;

/// <summary>Which executable's class table a menu file is read against. BfVietnam.exe registers the same 190 file
/// classes as BF1942.exe, with identical payloads, plus five of its own (BfControlPointGauge,
/// BfLocalePictureButtonNode, BfNewCreditsListNode, BfPictureButtonNode, BfTriangleButtonsNode).</summary>
public enum MemeGame { Bf1942, Bfv }

/// <summary>A payload token: how one field sits on disk. Properties carry no names, tags or type codes - each class's
/// <c>serialize()</c> reads a fixed positional sequence through virtual IStream slots, so the token list IS the class
/// layout (docs/Meme_Format.md section 4).</summary>
public enum MemeToken
{
    /// <summary>A nested record: <c>u32 size; u16 name index; u16 class index; payload</c> (IStream +0x60..+0x88).</summary>
    O,
    /// <summary>Records until a null record, which is written and ends the list (IStream +0x58).</summary>
    List,
    /// <summary>4-byte IEEE float, little endian (IStream +0x34).</summary>
    F32,
    /// <summary>4-byte signed int (IStream +0x3C Int, and +0x5C enum: a binary stream stores the value).</summary>
    I32,
    /// <summary>1 byte (IStream +0x1C / +0x20).</summary>
    U8,
    /// <summary>1 byte, 0/1 - the engine reads any non-zero byte as true (BFV 0x00878E00) (IStream +0x38).</summary>
    Bool,
    /// <summary><c>u8</c> length + latin1 bytes, no NUL: a texture (+0x4C), font (+0x50) or sound (+0x54) handle name.</summary>
    S8,
    /// <summary><c>u32</c> length + latin1 bytes (IStream +0x40); StringData, CR/LF allowed.</summary>
    Str32,
    /// <summary><c>u32</c> length in characters + 2*length bytes UTF-16LE (IStream +0x44); WstringData.</summary>
    WStr32,
    /// <summary>The recursive, bool-prefixed node grammar of Tree and NavigateTree (section 5.3).</summary>
    Tree,
    /// <summary>2 bytes (IStream +0x24/+0x28). No class uses it; listed for completeness.</summary>
    U16,
    /// <summary>4 bytes on disk, stored as u16 (IStream +0x48, BF1942 0x007F5AF0). No class uses it.</summary>
    U32As16,
}

/// <summary>What an <see cref="MemeToken.S8"/> string names: the IStream slot it is read through decides it.</summary>
public enum MemeHandle { None, Texture, Font, Sound }

/// <summary>One field of a class layout, in serialize order.</summary>
public sealed class MemeFieldDef
{
    internal MemeFieldDef(int index, MemeToken token, string label, string? expects, MemeHandle handle, bool isNextNode,
                          IReadOnlyDictionary<int, string>? enumNames, string? enumSource, int? mustEqual,
                          string? mustEqualWhy, string? slot, string? treeNode)
    {
        Index = index; Token = token; Label = label; Expects = expects; Handle = handle; IsNextNode = isNextNode;
        EnumNames = enumNames; EnumSource = enumSource; MustEqual = mustEqual; MustEqualWhy = mustEqualWhy; Slot = slot;
        TreeNode = treeNode;
    }

    /// <summary>Position in the class layout.</summary>
    public int Index { get; }
    public MemeToken Token { get; }
    /// <summary>The engine's own debug label for the field (e.g. <c>Next node</c>, <c>Value &lt;do not edit&gt;</c>).</summary>
    public string Label { get; }
    /// <summary>The base class the slot asks for (Node, Data, Action, Event, Effect, Style, Function, Tree, Object) -
    /// read from which typed IStream slot the engine uses. The loader does not enforce it (it only checks
    /// isKindOf(Object), BFV 0x008752D0), so a mismatch is a <see cref="MemeWarningKind.Type"/> warning, never an
    /// error. Null for a value field.</summary>
    public string? Expects { get; }
    public MemeHandle Handle { get; }
    /// <summary>The first O of every Node subclass: its continuation, drawn after the node with the SAME parent rect -
    /// a sibling, not a child (Node::draw BFV 0x00873DF0). A walk that treats it as a child degenerates into one long
    /// chain.</summary>
    public bool IsNextNode { get; }
    /// <summary>The names of an enum field's values (the file stores the value). From BfVietnam.exe's tables; BF1942.exe
    /// holds the same strings but its tables were not traced.</summary>
    public IReadOnlyDictionary<int, string>? EnumNames { get; }
    public string? EnumSource { get; }
    /// <summary>A value the engine requires: BfVerticalScrollExNode "Version &lt;do not edit&gt;" must be 1, any other
    /// value fails the read (BF1942 0x007D8532, BFV 0x0055A492).</summary>
    public int? MustEqual { get; }
    public string? MustEqualWhy { get; }
    /// <summary>The IStream vtable slot the engine reads the field through, e.g. <c>+0x88</c>.</summary>
    public string? Slot { get; }
    /// <summary>For a <see cref="MemeToken.Tree"/> field, the node class whose layout each tree node carries.</summary>
    public string? TreeNode { get; }

    public override string ToString() => $"{Token} {Label}";
}

/// <summary>One registered menu class of one game.</summary>
public sealed class MemeClass
{
    internal MemeClass(MemeSchema schema, string name, string fullName, string? baseName, IReadOnlyList<MemeFieldDef> fields,
                       string status, bool proven, uint? serializeVa, string engineSource, string? deprecationNote,
                       string? notAFileObject)
    {
        Schema = schema; Name = name; FullName = fullName; Base = baseName; Fields = fields; Status = status;
        ProvenByData = proven; SerializeVa = serializeVa; EngineSource = engineSource; DeprecationNote = deprecationNote;
        NotAFileObject = notAFileObject;
    }

    public MemeSchema Schema { get; }
    public MemeGame Game => Schema.Game;
    /// <summary>The short class name, e.g. <c>BfButtonNode</c>.</summary>
    public string Name { get; }
    /// <summary>The registered name, as every corpus file spells it: <c>dice::meme::BfButtonNode</c> (and
    /// <c>dice::ref2::menu::Ref2System</c>, the only other namespace).</summary>
    public string FullName { get; }
    /// <summary>The base class (from BFV RTTI; BF1942 has none and is assumed identical for the shared classes).</summary>
    public string? Base { get; }
    public IReadOnlyList<MemeFieldDef> Fields { get; }
    /// <summary>Real files prove the layout: every instance consumes exactly its payload with plausible values (for
    /// four BFV classes, BF1942 files with the identical layout do). Otherwise it comes from the engines alone - three
    /// tracers agree, but no file has tested it.</summary>
    public bool ProvenByData { get; }
    /// <summary>The evidence, in words (instance counts, or "engine-only").</summary>
    public string Status { get; }
    /// <summary>The engine's serialize routine (BF1942.exe or BfVietnam.exe, ImageBase 0x00400000); null for Tree and
    /// NavigateTree, whose loader is custom.</summary>
    public uint? SerializeVa { get; }
    /// <summary>Where the layout was read: the exe, serialize, vtable and slot, or the custom tree reader.</summary>
    public string EngineSource { get; }
    /// <summary>CullActionNode, SetVariableSinusAction and ShowEffectNode: the engine logs a deprecation warning when it
    /// loads one. An editor should not offer them for new objects.</summary>
    public bool Deprecated => DeprecationNote is not null;
    public string? DeprecationNote { get; }
    /// <summary>System and Ref2System are runtime roots, registered but never written into a file.</summary>
    public bool IsFileObject => NotAFileObject is null;
    public string? NotAFileObject { get; }

    /// <summary>Whether this class is <paramref name="baseShort"/> or derives from it (the base chain, at most 40 steps).</summary>
    public bool IsA(string baseShort)
    {
        var c = this;
        for (int i = 0; c is not null && i < 40; i++)
        {
            if (c.Name == baseShort) return true;
            if (c.Base is null) return false;
            if (Schema[c.Base] is not { } b) return c.Base == baseShort;
            c = b;
        }
        return false;
    }

    /// <summary>Drawn things and containers: everything whose serialize starts with a "Next node" (except BfGaugeNode,
    /// which does not chain to Node's and appears in no file).</summary>
    public bool IsNode => IsA("Node");

    public MemeClass? BaseClass => Base is null ? null : Schema[Base];

    /// <summary>The "Next node" field, or null for a class that has none.</summary>
    public MemeFieldDef? NextNode => Fields.Count > 0 && Fields[0].IsNextNode ? Fields[0] : null;

    /// <summary>A field by its engine label; BfListBoxData's BF1942 and BFV spellings are the same field.</summary>
    public MemeFieldDef? Field(string label)
    {
        foreach (var f in Fields) if (MemeLabels.Same(f.Label, label)) return f;
        return null;
    }

    /// <summary>For Tree and NavigateTree: the fields each tree node carries (none for Tree; Description and Action for
    /// NavigateTree).</summary>
    public IReadOnlyList<MemeFieldDef>? TreeNodeFields =>
        Fields.Count == 1 && Fields[0].Token == MemeToken.Tree ? MemeSchema.TreeNodeLayout(Fields[0].TreeNode) : null;

    public override string ToString() => Name;
}

/// <summary>
/// The class table of one game's menu system: every class BF1942.exe (194 entries) or BfVietnam.exe (199) registers,
/// with its payload layout, read from <c>MemeSchema.json</c>, a resource generated by <c>tools/meme/make_resource.py</c>
/// from the full table (<c>tools/meme/meme_schema.json</c>, which keeps each angle's provenance). Four independent
/// sources agree on every layout they share; the spec is <c>docs/Meme_Format.md</c>.
/// </summary>
public sealed class MemeSchema
{
    private static readonly Lazy<Loaded> _loaded = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Dictionary<string, MemeClass> _byShort = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MemeClass> _byFull = new(StringComparer.Ordinal);
    private readonly List<MemeClass> _classes = new();

    private MemeSchema(MemeGame game, string exe, string? enumNote)
    {
        Game = game; Exe = exe; EnumNote = enumNote;
    }

    public static MemeSchema For(MemeGame game) => game switch
    {
        MemeGame.Bf1942 => _loaded.Value.Bf1942,
        MemeGame.Bfv => _loaded.Value.Bfv,
        _ => throw new ArgumentOutOfRangeException(nameof(game)),
    };

    public MemeGame Game { get; }
    /// <summary>BF1942.exe or BfVietnam.exe.</summary>
    public string Exe { get; }
    /// <summary>Where this game's enum names come from (BF1942's are taken from BfVietnam.exe).</summary>
    public string? EnumNote { get; }
    /// <summary>Every class, by short name.</summary>
    public IReadOnlyList<MemeClass> Classes => _classes;

    /// <summary>A class by its SHORT name (<c>PictureNode</c>), or null.</summary>
    public MemeClass? this[string shortName] => _byShort.TryGetValue(shortName, out var c) ? c : null;

    /// <summary>
    /// A class string resolved the way the engine resolves it (BF1942 0x007EDEA0, BFV 0x008753F0): first the full
    /// registered name (<c>dice::meme::PictureNode</c>), then, in a second pass, the part of each registered name
    /// after its last ':' (the ClassInfo keeps that offset, <c>strrchr(name, ':') + 1</c>, BF1942 0x007EDF60), so a
    /// BARE <c>PictureNode</c> resolves too - but a foreign prefix such as <c>x::PictureNode</c> does not: the engine
    /// logs an unknown class, reads a null pointer and skips the record. Every corpus file spells the full name.
    /// </summary>
    public MemeClass? Find(string classNameOrShort)
    {
        if (string.IsNullOrEmpty(classNameOrShort)) return null;
        if (_byFull.TryGetValue(classNameOrShort, out var c)) return c;
        return _byShort.TryGetValue(classNameOrShort, out c) ? c : null;
    }

    private static readonly Dictionary<string, IReadOnlyList<MemeFieldDef>> _treeNodes = new(StringComparer.Ordinal);

    /// <summary>The layout of a tree node class (TreeObject: none; NavigateTreeObject: O Description, O Action).</summary>
    public static IReadOnlyList<MemeFieldDef> TreeNodeLayout(string? nodeClass)
    {
        _ = _loaded.Value;
        return nodeClass is not null && _treeNodes.TryGetValue(nodeClass, out var l) ? l : Array.Empty<MemeFieldDef>();
    }

    public override string ToString() => $"{Game} ({_classes.Count} classes)";

    // ---- loading -----------------------------------------------------------------------------------------------

    private sealed record Loaded(MemeSchema Bf1942, MemeSchema Bfv);

    private const string ResourceName = "RefractorForge.Formats.Menu.MemeSchema.json";

    private static Loaded Load()
    {
        using var s = typeof(MemeSchema).Assembly.GetManifestResourceStream(ResourceName)
                      ?? throw new InvalidOperationException($"the embedded resource {ResourceName} is missing");
        using var doc = JsonDocument.Parse(s);
        var root = doc.RootElement;
        var enums = new Dictionary<string, (IReadOnlyDictionary<int, string> Names, string From)>(StringComparer.Ordinal);
        foreach (var e in root.GetProperty("enums").EnumerateObject())
        {
            var names = new Dictionary<int, string>();
            foreach (var n in e.Value.GetProperty("names").EnumerateObject())
                names[int.Parse(n.Name, CultureInfo.InvariantCulture)] = n.Value.GetString()!;
            enums[e.Name] = (names, e.Value.GetProperty("from").GetString()!);
        }
        foreach (var t in root.GetProperty("treeNodes").EnumerateObject())
            _treeNodes[t.Name] = t.Value.EnumerateArray().Select((f, i) => ReadField(f, i, enums)).ToArray();
        var games = root.GetProperty("games");
        return new Loaded(ReadGame(MemeGame.Bf1942, games.GetProperty("Bf1942"), enums),
                          ReadGame(MemeGame.Bfv, games.GetProperty("Bfv"), enums));
    }

    private static MemeSchema ReadGame(MemeGame game, JsonElement g,
                                       Dictionary<string, (IReadOnlyDictionary<int, string> Names, string From)> enums)
    {
        var schema = new MemeSchema(game, g.GetProperty("exe").GetString()!, Str(g, "enumNote"));
        foreach (var c in g.GetProperty("classes").EnumerateArray())
        {
            var fields = c.GetProperty("fields").EnumerateArray().Select((f, i) => ReadField(f, i, enums)).ToArray();
            string? va = Str(c, "va");
            var cls = new MemeClass(schema, c.GetProperty("name").GetString()!, c.GetProperty("full").GetString()!,
                                    Str(c, "base"), fields, c.GetProperty("status").GetString()!,
                                    c.GetProperty("proven").GetBoolean(),
                                    va is null ? null : uint.Parse(va.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                                    Str(c, "engine") ?? "", Str(c, "deprecated"), Str(c, "notAFileObject"));
            schema._classes.Add(cls);
            schema._byShort.Add(cls.Name, cls);
            schema._byFull.Add(cls.FullName, cls);
        }
        return schema;
    }

    private static MemeFieldDef ReadField(JsonElement f, int index,
                                          Dictionary<string, (IReadOnlyDictionary<int, string> Names, string From)> enums)
    {
        var tok = f.GetProperty("tok").GetString() switch
        {
            "O" => MemeToken.O, "LIST" => MemeToken.List, "f32" => MemeToken.F32, "i32" => MemeToken.I32,
            "u8" => MemeToken.U8, "bool" => MemeToken.Bool, "s8" => MemeToken.S8, "str32" => MemeToken.Str32,
            "wstr32" => MemeToken.WStr32, "TREE" => MemeToken.Tree, "u16" => MemeToken.U16,
            "u32as16" => MemeToken.U32As16,
            var t => throw new InvalidDataException($"unknown token '{t}' in {ResourceName}"),
        };
        var handle = Str(f, "handle") switch
        {
            "texture" => MemeHandle.Texture, "font" => MemeHandle.Font, "sound" => MemeHandle.Sound, _ => MemeHandle.None,
        };
        var enumKey = Str(f, "enum");
        (IReadOnlyDictionary<int, string> Names, string From)? en = enumKey is null ? null : enums[enumKey];
        int? eq = f.TryGetProperty("mustEqual", out var m) ? m.GetInt32() : null;
        return new MemeFieldDef(index, tok, f.GetProperty("label").GetString()!, Str(f, "expects"), handle,
                                f.TryGetProperty("next", out var n) && n.GetBoolean(), en?.Names, en?.From, eq,
                                Str(f, "why"), Str(f, "slot"), Str(f, "treeNode"));
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>The one label that differs between the games: BfListBoxData's "Create new action/string" in BF1942 reads
/// "Create dice_new action/string" in BFV. Both spellings name the same field everywhere a label is looked up.</summary>
internal static class MemeLabels
{
    private const string Bfv = "Create dice_new ";
    private const string Bf1942 = "Create new ";

    public static string Canonical(string label) =>
        label.StartsWith(Bfv, StringComparison.Ordinal) ? Bf1942 + label[Bfv.Length..] : label;

    public static bool Same(string a, string b) => a == b || Canonical(a) == Canonical(b);
}
