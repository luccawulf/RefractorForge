using System.Globalization;

namespace RefractorForge.Formats.Menu;

/// <summary>What a record is.</summary>
public enum MemeRecordKind
{
    /// <summary>A decoded object: a class this game registers, whose payload fits the layout exactly.</summary>
    Object,
    /// <summary>Kept as raw payload bytes, so nothing is lost: a class the game does not register (or a class string
    /// the engine cannot resolve), or a payload that does not fit the layout (a damaged record).</summary>
    Opaque,
    /// <summary>Class index 0, name index 0: a null pointer.</summary>
    Null,
    /// <summary>Class index 0 with a name: a by-name reference. The engine looks the name up in its global registry
    /// and, when it is not registered, loads the menu FILE of that name (System::loadFile, BFV 0x00874DC0) - this is
    /// how screens chain together.</summary>
    Ref,
}

/// <summary>
/// One record of a Meme 2.0 file (<c>u32 size; u16 name index; u16 class index; payload[size - 8]</c>), or the root,
/// which has a class and a payload but no size and no name. Fields are positional, in the class's serialize order.
/// <para>A NAMED record is registered in the menu system's global registry under its name, and the engine keeps the
/// FIRST definition it meets: a later record of the same name is resolved to the registered object and its payload
/// skipped (findObject BFV 0x00868A70, BF1942 0x007EB740), across files too. Data objects are named with their variable
/// path (<c>Ammo/PrimaryAmmo</c>), and DICE's writer inlines a full copy of a shared object every time it is referenced
/// (21,735 BF1942 and 22,480 BFV retail repeats, all byte-identical) - so same-named records are ONE object, and an
/// edit belongs on every copy (<see cref="MemeEdits.SetEverywhere"/>).</para>
/// </summary>
public sealed class MemeRecord
{
    private string? _class;
    private string _name = "";

    /// <summary>The class name exactly as stored (<c>dice::meme::BfButtonNode</c>); null for a null pointer or a
    /// by-name reference. Setting it resolves <see cref="Class"/> again in the same game's schema.</summary>
    public string? ClassName
    {
        get => _class;
        set
        {
            _class = string.IsNullOrEmpty(value) ? null : value;
            if (Class is { } c && c.FullName != _class && c.Name != _class) Class = _class is null ? null : c.Schema.Find(_class);
        }
    }

    /// <summary>The object name ("" for none). The root has none.</summary>
    public string Name { get => _name; set => _name = value ?? ""; }

    /// <summary>The schema class this record was decoded or created as; null for null, reference and unknown-class
    /// records. The writer checks the class name against the file's game itself and does not trust this.</summary>
    public MemeClass? Class { get; set; }

    /// <summary>The decoded fields in token order; null for an opaque, null or reference record.</summary>
    public List<MemeField>? Fields { get; set; }

    /// <summary>An opaque record's payload bytes, or the dead bytes of a null / reference record longer than its header
    /// (the engine resolves the pointer from the header and seeks to start + size, so they are never read). Kept
    /// verbatim.</summary>
    public byte[]? Raw { get; set; }

    /// <summary>Why a record is opaque.</summary>
    public string? Note { get; set; }

    /// <summary>Where the record was read (informational): its offset and size (for the root: payload start and
    /// payload length).</summary>
    public int? Offset { get; internal set; }
    public int? Size { get; internal set; }

    /// <summary>The string-table indices the header was read with. A table can hold the same string twice, so writing
    /// the file back byte-exact needs the index it used, not just the string; the writer uses it whenever the table it
    /// writes still holds that string there.</summary>
    public int? NameIndex { get; set; }
    public int? ClassIndex { get; set; }

    public MemeRecordKind Kind => _class is null
        ? (_name.Length > 0 ? MemeRecordKind.Ref : MemeRecordKind.Null)
        : (Fields is null ? MemeRecordKind.Opaque : MemeRecordKind.Object);

    /// <summary>The class name after its last ':' (<c>BfButtonNode</c>), as the engine's second lookup pass reads it.</summary>
    public string? Short => Class?.Name ?? (_class is null ? null : _class[(_class.LastIndexOf(':') + 1)..]);

    // ---- construction ------------------------------------------------------------------------------------------

    public MemeRecord() { }

    public MemeRecord(string? className, string name = "", List<MemeField>? fields = null, byte[]? raw = null)
    {
        ClassName = className; Name = name; Fields = fields; Raw = raw;
    }

    /// <summary>A null pointer.</summary>
    public static MemeRecord Null() => new();

    /// <summary>A by-name reference to a registered object or a menu file.</summary>
    public static MemeRecord Ref(string name)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("a reference needs a name; use Null() for a null pointer", nameof(name));
        return new MemeRecord(null, name);
    }

    /// <summary>A fresh object of a class of <paramref name="game"/> with default values - null records, 0, "", an
    /// empty list, no tree - except a field the engine constrains, which gets its required value
    /// (BfVerticalScrollExNode Version = 1). <paramref name="cls"/> is resolved as the engine resolves class strings
    /// (<see cref="MemeSchema.Find"/>), and the full registered name is stored.</summary>
    public static MemeRecord New(MemeGame game, string cls, string name = "", params (string Label, object? Value)[] values)
    {
        var schema = MemeSchema.For(game);
        var c = schema.Find(cls) ?? throw new ArgumentException($"'{cls}' is not a {game} menu class", nameof(cls));
        var fields = new List<MemeField>(c.Fields.Count);
        foreach (var d in c.Fields) fields.Add(new MemeField(d, DefaultValue(d)));
        var r = new MemeRecord(c.FullName, name, fields) { Class = c };
        foreach (var (label, value) in values) r.Set(label, value);
        return r;
    }

    internal static object? DefaultValue(MemeFieldDef d)
    {
        if (d.MustEqual is int eq) return eq;
        return d.Token switch
        {
            MemeToken.O => Null(),
            MemeToken.List => new List<MemeRecord>(),
            MemeToken.F32 => 0f,
            MemeToken.I32 => 0,
            MemeToken.U8 or MemeToken.Bool => (byte)0,
            MemeToken.S8 or MemeToken.Str32 or MemeToken.WStr32 => "",
            MemeToken.Tree => null,
            MemeToken.U16 => (ushort)0,
            MemeToken.U32As16 => 0u,
            _ => null,
        };
    }

    /// <summary>A deep copy: fields, lists, tree nodes and raw bytes are copied; the string-index hints are kept (the
    /// writer only uses one where the table still holds that string), the read position is not. A subtree shared by
    /// two slots becomes two copies. Throws <see cref="InvalidOperationException"/> for a record that is its own
    /// descendant.</summary>
    public MemeRecord DeepClone() => DeepStack.Run(() => CloneRec(this, new HashSet<MemeRecord>(ReferenceEqualityComparer.Instance)));

    private static MemeRecord CloneRec(MemeRecord r, HashSet<MemeRecord> path)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        if (!path.Add(r)) throw new InvalidOperationException($"cycle: {r} is its own descendant; a menu file is a tree");
        var c = new MemeRecord(r.ClassName, r.Name)
        {
            Class = r.Class, Raw = r.Raw is null ? null : (byte[])r.Raw.Clone(), Note = r.Note,
            NameIndex = r.NameIndex, ClassIndex = r.ClassIndex,
            Fields = r.Fields is null ? null : CloneFields(r.Fields, path),
        };
        path.Remove(r);
        return c;
    }

    private static List<MemeField> CloneFields(List<MemeField> fields, HashSet<MemeRecord> path)
    {
        var list = new List<MemeField>(fields.Count);
        foreach (var f in fields)
        {
            object? v = f.Value switch
            {
                MemeRecord rec => CloneRec(rec, path),
                List<MemeRecord> items => items.Select(x => x is null ? null! : CloneRec(x, path)).ToList(),
                MemeTreeNode n => CloneTree(n, path),
                var other => other,
            };
            list.Add(new MemeField(f.Token, f.Label, v, f.Def) { ListEnd = f.ListEnd is null ? null : CloneRec(f.ListEnd, path) });
        }
        return list;
    }

    private static MemeTreeNode CloneTree(MemeTreeNode n, HashSet<MemeRecord> path) => new()
    {
        Flag = n.Flag,
        Fields = CloneFields(n.Fields, path),
        Child = n.Child is null ? null : CloneTree(n.Child, path),
        Next = n.Next is null ? null : CloneTree(n.Next, path),
        Prev = n.Prev is null ? null : CloneTree(n.Prev, path),
    };

    // ---- fields ------------------------------------------------------------------------------------------------

    /// <summary>A field by its engine label (BfListBoxData's BF1942 and BFV spellings both work).</summary>
    public MemeField Field(string label) => TryGetField(label, out var f) ? f
        : throw new KeyNotFoundException($"{this} has no field '{label}'" +
                                         (Fields is null ? " (it has no decoded fields)" : $"; its fields: {string.Join(", ", Fields.Select(x => x.Label))}"));

    public bool TryGetField(string label, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out MemeField field)
    {
        if (Fields is not null)
        {
            foreach (var f in Fields) if (f.Label == label) { field = f; return true; }
            foreach (var f in Fields) if (MemeLabels.Same(f.Label, label)) { field = f; return true; }
        }
        field = null;
        return false;
    }

    /// <summary>A field's value by label; setting goes through <see cref="Set"/>, which checks the type.</summary>
    public object? this[string label]
    {
        get => Field(label).Value;
        set => Field(label).Set(value);
    }

    /// <summary>A field's value as <typeparamref name="T"/>; numeric values convert (a u8 read as int, an f32 as
    /// double). Throws <see cref="InvalidCastException"/> naming the field when it is something else.</summary>
    public T Get<T>(string label) => Field(label).Get<T>();

    /// <summary>Set a field, checking the value fits its token (<see cref="MemeField.Set"/>).</summary>
    public void Set(string label, object? value) => Field(label).Set(value);

    /// <summary>The "Next node" field: the node's continuation (a sibling, drawn with the same parent rect), or null
    /// for a record that has none.</summary>
    public MemeField? NextField
    {
        get
        {
            if (Fields is null) return null;
            foreach (var f in Fields)
                if (f.Def is { } d ? d.IsNextNode : f.Token == MemeToken.O && f.Label == "Next node") return f;
            return null;
        }
    }

    /// <summary>The "Next node" record (a null record when the chain ends here); null when this record has no such
    /// field. Setting null writes a null pointer.</summary>
    public MemeRecord? Next
    {
        get => NextField?.Value as MemeRecord;
        set => (NextField ?? throw new InvalidOperationException($"{this} has no Next node")).Value = value ?? Null();
    }

    /// <summary>The nested records in file order: O slots, LIST items and a non-canonical list terminator, and tree
    /// node slots. An O slot or list item holding C# null (the writer's shorthand for a null pointer) is yielded as a
    /// fresh null record.</summary>
    public IEnumerable<MemeRecord> Children()
    {
        if (Fields is null) yield break;
        foreach (var f in Fields)
        {
            switch (f.Token)
            {
                case MemeToken.O:
                    yield return f.Value as MemeRecord ?? Null();
                    break;
                case MemeToken.List:
                    if (f.Value is IEnumerable<MemeRecord?> items)
                        foreach (var r in items) yield return r ?? Null();
                    if (f.ListEnd is not null) yield return f.ListEnd;
                    break;
                case MemeToken.Tree:
                    if (f.Value is MemeTreeNode n)
                        foreach (var r in n.Records()) yield return r;
                    break;
            }
        }
    }

    public override string ToString() => Kind switch
    {
        MemeRecordKind.Null => "null",
        MemeRecordKind.Ref => $"-> '{Name}'",
        MemeRecordKind.Opaque => $"{Short} '{Name}' (opaque)",
        _ => Name.Length > 0 ? $"{Short} '{Name}'" : Short ?? "?",
    };
}

/// <summary>
/// One field of a record: its token, engine label and value. <see cref="Value"/> holds, by token: <c>O</c> a
/// <see cref="MemeRecord"/> (in a decoded file never C# null - a null pointer is a record of kind Null, so its dead bytes
/// survive; the writer also takes C# null as a null pointer); <c>LIST</c> a <c>List&lt;MemeRecord&gt;</c> (and
/// <see cref="ListEnd"/> the non-canonical terminator, if one was read); <c>f32</c> float (NaN payloads kept);
/// <c>i32</c> int; <c>u8</c>/<c>bool</c> byte (a bool of 2..255 is kept as read); <c>s8</c>/<c>str32</c> a latin1
/// string; <c>wstr32</c> a string of UTF-16 code units (a lone surrogate is kept); <c>TREE</c> a
/// <see cref="MemeTreeNode"/> or null; <c>u16</c> ushort; <c>u32as16</c> uint.
/// <para>Assigning <see cref="Value"/> directly stores what it is given, and the writer refuses what cannot be written;
/// <see cref="Set"/> checks and converts at once.</para>
/// </summary>
public sealed class MemeField
{
    public MemeField(MemeToken token, string label, object? value, MemeFieldDef? def = null)
    {
        Token = token; Label = label; Value = value; Def = def;
    }

    public MemeField(MemeFieldDef def, object? value) : this(def.Token, def.Label, value, def) { }

    public MemeToken Token { get; }
    public string Label { get; }
    /// <summary>The schema definition of this field (expects, handle, enum names, constraint); null for a field built
    /// by hand.</summary>
    public MemeFieldDef? Def { get; }
    public object? Value { get; set; }
    /// <summary>LIST only: the terminating null record when it was not the canonical 8 bytes (it carried dead bytes);
    /// null for the plain terminator. No corpus list has one.</summary>
    public MemeRecord? ListEnd { get; set; }

    /// <summary>The O slot's record (null when it holds C# null or this is not an O field).</summary>
    public MemeRecord? Record => Value as MemeRecord;
    /// <summary>The LIST's items.</summary>
    public List<MemeRecord>? Items => Value as List<MemeRecord>;
    public MemeTreeNode? TreeRoot => Value as MemeTreeNode;

    /// <summary>The value as <typeparamref name="T"/>, converting between numeric types.</summary>
    public T Get<T>()
    {
        if (Value is T t) return t;
        if (Value is IConvertible && typeof(T).IsPrimitive && Value is not string)
        {
            try { return (T)Convert.ChangeType(Value, typeof(T), CultureInfo.InvariantCulture); }
            catch (Exception e) when (e is InvalidCastException or OverflowException) { }
        }
        throw new InvalidCastException($"'{Label}' ({Token}) holds {Value?.GetType().Name ?? "null"}, not {typeof(T).Name}");
    }

    /// <summary>Set the value, checking it fits the token and converting it to the stored type: a u8/bool takes an
    /// integer 0..255 (or a C# bool), an i32 an integer in -2^31..2^32-1 (2^31 and up are the same 32 bits, read back
    /// negative), an f32 any number that fits a float, an O a record or null (a null pointer), a LIST records none of
    /// which is null, a string field a string (s8/str32 latin1, s8 at most 255 bytes) without NUL - the engine keeps
    /// every string as a C string, so the text would end there. Throws <see cref="ArgumentException"/>.</summary>
    public void Set(object? value) => Value = Normalize(Token, Label, value);

    internal static object? Normalize(MemeToken token, string label, object? value)
    {
        string Bad(string why) => $"'{label}' ({token}): {why}";
        switch (token)
        {
            case MemeToken.O:
                return value switch
                {
                    null => MemeRecord.Null(),
                    MemeRecord r => r,
                    _ => throw new ArgumentException(Bad($"an O slot holds a record, not {value.GetType().Name}")),
                };
            case MemeToken.List:
            {
                if (value is not IEnumerable<MemeRecord?> items) throw new ArgumentException(Bad("a LIST holds records"));
                var list = value as List<MemeRecord> ?? items.ToList()!;
                foreach (var r in list)
                    if (r is null || r.Kind == MemeRecordKind.Null)
                        throw new ArgumentException(Bad("a null record cannot be a LIST item: the reader ends the list there and every later field would shift"));
                return list;
            }
            case MemeToken.F32:
                return MemeValues.TryF32(value, out var f, out var why) ? f : throw new ArgumentException(Bad(why!));
            case MemeToken.I32:
                return MemeValues.TryI32(value, out var i) ? i : throw new ArgumentException(Bad($"{Show(value)} is not an integer in -2^31..2^32-1"));
            case MemeToken.U8:
            case MemeToken.Bool:
                return MemeValues.TryByte(value, out var b) ? b : throw new ArgumentException(Bad($"{Show(value)} is not an integer 0..255"));
            case MemeToken.U16:
                return MemeValues.TryInteger(value, out var u) && u is >= 0 and <= ushort.MaxValue ? (ushort)u
                    : throw new ArgumentException(Bad($"{Show(value)} is not an integer 0..65535"));
            case MemeToken.U32As16:
                return MemeValues.TryInteger(value, out var w) && w is >= 0 and <= uint.MaxValue ? (uint)w
                    : throw new ArgumentException(Bad($"{Show(value)} is not an integer 0..2^32-1"));
            case MemeToken.S8:
            case MemeToken.Str32:
            case MemeToken.WStr32:
            {
                if (value is not string s) throw new ArgumentException(Bad($"{Show(value)} is not a string"));
                var problem = MemeValues.StringProblem(token, s);
                return problem is null ? s : throw new ArgumentException(Bad(problem));
            }
            case MemeToken.Tree:
                return value is null or MemeTreeNode ? value : throw new ArgumentException(Bad("a TREE holds a MemeTreeNode or null"));
            default:
                throw new ArgumentException(Bad("unknown token"));
        }
    }

    private static string Show(object? v) => v is null ? "null" : $"{v} ({v.GetType().Name})";

    public override string ToString() => $"{Label} = {Value}";
}

/// <summary>
/// A node of a Tree / NavigateTree field. The grammar (BFV 0x0087B190 -> 0x0087AEB0 / 0x0087AAD0 / 0x0087AF70,
/// BF1942 0x007F8DD0 -> 0x007F8AD0), hand-decoded independently by two angles:
/// <code>
/// TREE0 := u8 flag ; if flag: NODE TREE0(child) TREE1(next) TREE2(prev)
/// TREE1 := u8 flag ; if flag: NODE TREE0(child) TREE1(next)
/// TREE2 := u8 flag ; if flag: NODE TREE0(child) TREE2(prev)
/// </code>
/// so a next-node has no prev and a prev-node no next; the writer refuses such a node rather than drop it. No corpus
/// file holds either class; a synthetic file round-trips for both games.
/// </summary>
public sealed class MemeTreeNode
{
    /// <summary>The byte that says "a node follows"; any non-zero value is kept, and 0 is written as 1 (0 would end the
    /// tree here).</summary>
    public byte Flag { get; set; } = 1;
    /// <summary>The node class's fields (TreeObject: none; NavigateTreeObject: O Description, O Action).</summary>
    public List<MemeField> Fields { get; set; } = new();
    public MemeTreeNode? Child { get; set; }
    public MemeTreeNode? Next { get; set; }
    public MemeTreeNode? Prev { get; set; }

    /// <summary>A node with the default fields of <paramref name="layout"/> (<see cref="MemeClass.TreeNodeFields"/>).</summary>
    public static MemeTreeNode New(IReadOnlyList<MemeFieldDef> layout) =>
        new() { Fields = layout.Select(d => new MemeField(d, MemeRecord.DefaultValue(d))).ToList() };

    /// <summary>This node's O slots (C# null as a fresh null record), then those of its child, next and prev
    /// subtrees.</summary>
    public IEnumerable<MemeRecord> Records()
    {
        var stack = new Stack<MemeTreeNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            foreach (var f in n.Fields)
                if (f.Token == MemeToken.O) yield return f.Value as MemeRecord ?? MemeRecord.Null();
            if (n.Prev is not null) stack.Push(n.Prev);
            if (n.Next is not null) stack.Push(n.Next);
            if (n.Child is not null) stack.Push(n.Child);
        }
    }

    public MemeField Field(string label) => Fields.FirstOrDefault(f => MemeLabels.Same(f.Label, label))
                                            ?? throw new KeyNotFoundException($"the tree node has no field '{label}'");

    public object? this[string label]
    {
        get => Field(label).Value;
        set => Field(label).Set(value);
    }
}
