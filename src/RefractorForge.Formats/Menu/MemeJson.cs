using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace RefractorForge.Formats.Menu;

/// <summary>
/// A lossless JSON form of a menu file, for scripted edits: <c>FromJson(ToJson(f)).ToBytes() == f.ToBytes()</c> for
/// every corpus file. It carries what a byte-exact write needs besides the tree: the string table as read (order,
/// duplicates, unused entries), the table rule as read (which <see cref="MemeTableMode.Auto"/> decides by), each
/// header's string indices, opaque payloads and dead bytes (hex), a non-canonical list terminator, and the exact bits
/// of every float that its shortest round-trip text would not give back (NaN, infinities).
/// <code>
/// { "meme": 1, "game": "Bfv", "format": "meme2", "tableRule": "first-use", "strings": ["MemeFile 2.0", ...],
///   "root": { "class": "dice::meme::NameNode", "ci": 1, "fields": { "Next node": { ... } } } }
/// </code>
/// A record is <c>null</c> (a null pointer), <c>{"ref": name}</c>, <c>{"class", "name", "fields": {label: value}}</c>
/// (a decoded object, fields by engine label) or <c>{"class", "name", "raw": hex, "note"}</c> (opaque); <c>ni</c> /
/// <c>ci</c> are the string indices it was read with, <c>raw</c> on a null or reference its dead bytes. A LIST is an array
/// of records (<c>{"items": [...], "end": record}</c> when its terminator carried dead bytes); a TREE node is
/// <c>{"flag", "fields", "child", "next", "prev"}</c>. An f32 is a number, or <c>{"bits": "7FC00000"}</c>; a wstr32 with
/// a lone surrogate is <c>{"utf16": hex}</c>. On the way back a field left out gets its default (as
/// <see cref="MemeRecord.New"/> gives it), an unknown label is refused, and the warnings and counts of a read are not
/// carried.
/// </summary>
public static class MemeJson
{
    /// <summary>The file as JSON; indented unless records nest deeper than <see cref="MemeFile.LintMaxDepth"/> (retail
    /// never passes 278), where indentation would make the text grow with the square of the depth.</summary>
    public static string ToJson(MemeFile file, bool indented = true) => DeepStack.Run(() =>
    {
        indented &= Depth(file.Root) <= MemeFile.LintMaxDepth;
        var buf = new ArrayBufferWriter<byte>(1 << 16);
        using (var w = new Utf8JsonWriter(buf, new JsonWriterOptions
               {
                   Indented = indented, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = int.MaxValue,
               }))
        {
            w.WriteStartObject();
            w.WriteNumber("meme", 1);
            w.WriteString("game", file.Game.ToString());
            w.WriteString("format", file.IsOldFormat ? "old" : "meme2");
            if (file.TableRule is not null) w.WriteString("tableRule", file.TableRule);
            if (file.Strings is not null)
            {
                w.WriteStartArray("strings");
                foreach (var s in file.Strings) w.WriteStringValue(s);
                w.WriteEndArray();
            }
            if (file.Trailing.Length > 0) w.WriteString("trailing", Convert.ToHexString(file.Trailing));
            w.WritePropertyName("root");
            Record(w, file.Root);
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buf.WrittenSpan);
    });

    private static int Depth(MemeRecord root)
    {
        int deepest = 0;
        var stack = new Stack<(MemeRecord, int)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (r, d) = stack.Pop();
            deepest = Math.Max(deepest, d);
            if (d > MemeFile.LintMaxDepth) break;
            foreach (var c in r.Children()) stack.Push((c, d + 1));
        }
        return deepest;
    }

    private static void Record(Utf8JsonWriter w, MemeRecord? r)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (r is null || (r.Kind == MemeRecordKind.Null && r.Raw is null or { Length: 0 }))
        {
            w.WriteNullValue();
            return;
        }
        w.WriteStartObject();
        switch (r.Kind)
        {
            case MemeRecordKind.Null:
                w.WriteBoolean("null", true);
                break;
            case MemeRecordKind.Ref:
                w.WriteString("ref", r.Name);
                break;
            default:
                w.WriteString("class", r.ClassName);
                if (r.Name.Length > 0) w.WriteString("name", r.Name);
                break;
        }
        if (r.NameIndex is int ni && r.Kind != MemeRecordKind.Null) w.WriteNumber("ni", ni);
        if (r.ClassIndex is int ci && r.ClassName is not null) w.WriteNumber("ci", ci);
        if (r.Fields is not null)
        {
            w.WriteStartObject("fields");
            Fields(w, r.Fields);
            w.WriteEndObject();
        }
        if (r.Raw is not null) w.WriteString("raw", Convert.ToHexString(r.Raw));
        if (r.Note is not null && r.Kind == MemeRecordKind.Opaque) w.WriteString("note", r.Note);
        w.WriteEndObject();
    }

    private static void Fields(Utf8JsonWriter w, List<MemeField> fields)
    {
        foreach (var f in fields)
        {
            w.WritePropertyName(f.Label);
            Value(w, f);
        }
    }

    private static void Value(Utf8JsonWriter w, MemeField f)
    {
        var v = f.Value;
        switch (f.Token)
        {
            case MemeToken.O:
                Record(w, v as MemeRecord);
                return;
            case MemeToken.List:
            {
                var items = v as IEnumerable<MemeRecord?> ?? Array.Empty<MemeRecord>();
                if (f.ListEnd is not null)
                {
                    w.WriteStartObject();
                    w.WritePropertyName("items");
                }
                w.WriteStartArray();
                foreach (var r in items) Record(w, r);
                w.WriteEndArray();
                if (f.ListEnd is not null)
                {
                    w.WritePropertyName("end");
                    Record(w, f.ListEnd);
                    w.WriteEndObject();
                }
                return;
            }
            case MemeToken.Tree:
                Tree(w, v as MemeTreeNode);
                return;
            case MemeToken.F32 when v is float x:
            {
                var s = MemeFormat.Float(x);
                if (float.IsFinite(x) && BitConverter.SingleToInt32Bits(float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture))
                    == BitConverter.SingleToInt32Bits(x))
                    w.WriteRawValue(s);
                else
                {
                    w.WriteStartObject();
                    w.WriteString("bits", BitConverter.SingleToUInt32Bits(x).ToString("X8", CultureInfo.InvariantCulture));
                    w.WriteEndObject();
                }
                return;
            }
            case MemeToken.WStr32 when v is string s && MemeValues.HasLoneSurrogate(s):
                w.WriteStartObject();
                w.WriteString("utf16", Convert.ToHexString(MemoryMarshal.AsBytes(s.AsSpan())));
                w.WriteEndObject();
                return;
        }
        switch (v)
        {
            case null: w.WriteNullValue(); break;
            case string s: w.WriteStringValue(s); break;
            case float x: w.WriteRawValue(MemeFormat.Float(x)); break;
            case double d: w.WriteNumberValue(d); break;
            default:
                if (MemeValues.TryInteger(v, out var n)) w.WriteNumberValue(n);
                else throw new InvalidOperationException($"'{f.Label}' ({f.Token}) holds {v.GetType().Name}, which has no JSON form");
                break;
        }
    }

    private static void Tree(Utf8JsonWriter w, MemeTreeNode? n)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (n is null) { w.WriteNullValue(); return; }
        w.WriteStartObject();
        if (n.Flag != 1) w.WriteNumber("flag", n.Flag);
        w.WriteStartObject("fields");
        Fields(w, n.Fields);
        w.WriteEndObject();
        if (n.Child is not null) { w.WritePropertyName("child"); Tree(w, n.Child); }
        if (n.Next is not null) { w.WritePropertyName("next"); Tree(w, n.Next); }
        if (n.Prev is not null) { w.WritePropertyName("prev"); Tree(w, n.Prev); }
        w.WriteEndObject();
    }

    // ---- back ----------------------------------------------------------------------------------------------------

    /// <summary>The file a <see cref="ToJson"/> text describes, for the game it names.</summary>
    public static MemeFile FromJson(string json) => FromJson(json, null);

    /// <summary>The file a <see cref="ToJson"/> text describes. Throws <see cref="InvalidDataException"/> naming the
    /// JSON path when it is not one (an unknown label or class, a value of the wrong kind), or when it names a game other
    /// than <paramref name="game"/>.</summary>
    public static MemeFile FromJson(string json, MemeGame? game)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = int.MaxValue }); }
        catch (JsonException e) { throw new InvalidDataException($"not JSON: {e.Message}", e); }
        using (doc) return DeepStack.Run(() => new Reader(doc.RootElement).File(game));
    }

    private sealed class Reader(JsonElement root)
    {
        private readonly List<string> _path = new();
        private MemeSchema _schema = null!;

        private InvalidDataException Bad(string why) => new($"{(_path.Count == 0 ? "$" : string.Join(".", _path))}: {why}");

        public MemeFile File(MemeGame? want)
        {
            if (root.ValueKind != JsonValueKind.Object) throw Bad("a menu file's JSON is an object");
            MemeGame game;
            if (root.TryGetProperty("game", out var g))
            {
                if (!Enum.TryParse(g.GetString(), ignoreCase: true, out game)) throw Bad($"unknown game '{g}'");
                if (want is { } w && w != game) throw Bad($"the JSON is a {game} file, not {w}");
            }
            else game = want ?? throw Bad("no game");
            _schema = MemeSchema.For(game);
            bool old = root.TryGetProperty("format", out var fmt) && fmt.GetString() == "old";
            List<string>? strings = null;
            if (root.TryGetProperty("strings", out var st) && st.ValueKind == JsonValueKind.Array)
                strings = st.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
            _path.Add("root");
            MemeRecord rootRec;
            // a value of the wrong JSON kind where a string or number belongs: the element's own exception, with the path
            try { rootRec = Record(root.TryGetProperty("root", out var r) ? r : throw Bad("no root")); }
            catch (Exception e) when (e is InvalidOperationException or FormatException or OverflowException) { throw Bad(e.Message); }
            _path.Clear();
            var file = new MemeFile(game, rootRec, old ? null : strings, old);
            if (root.TryGetProperty("trailing", out var tr)) file.Trailing = Hex(tr);
            file.TableRule = old ? null
                : root.TryGetProperty("tableRule", out var rule) && rule.ValueKind == JsonValueKind.String ? rule.GetString()
                : null;
            return file;
        }

        private MemeRecord Record(JsonElement e)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            if (e.ValueKind == JsonValueKind.Null) return MemeRecord.Null();
            if (e.ValueKind != JsonValueKind.Object) throw Bad($"a record is null or an object, not {e.ValueKind}");
            var r = new MemeRecord();
            if (e.TryGetProperty("ref", out var refName)) r.Name = refName.GetString() ?? "";
            if (e.TryGetProperty("class", out var cls)) r.ClassName = cls.GetString();
            if (e.TryGetProperty("name", out var name)) r.Name = name.GetString() ?? "";
            if (e.TryGetProperty("ni", out var ni)) r.NameIndex = ni.GetInt32();
            if (e.TryGetProperty("ci", out var ci)) r.ClassIndex = ci.GetInt32();
            if (e.TryGetProperty("raw", out var raw)) r.Raw = Hex(raw);
            if (e.TryGetProperty("note", out var note)) r.Note = note.GetString();
            if (e.TryGetProperty("fields", out var fields))
            {
                if (r.ClassName is null) throw Bad("a record with fields needs a class");
                var c = _schema.Find(r.ClassName) ?? throw Bad($"'{r.ClassName}' is not a {_schema.Game} menu class");
                r.Class = c;
                r.Fields = Fields(fields, c.Fields, c.Name);
            }
            else if (r.ClassName is not null) r.Class = _schema.Find(r.ClassName);
            return r;
        }

        private List<MemeField> Fields(JsonElement e, IReadOnlyList<MemeFieldDef> defs, string owner)
        {
            if (e.ValueKind != JsonValueKind.Object) throw Bad("fields are an object of label: value");
            var given = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var p in e.EnumerateObject())
            {
                var def = defs.FirstOrDefault(d => d.Label == p.Name) ?? defs.FirstOrDefault(d => MemeLabels.Same(d.Label, p.Name))
                          ?? throw Bad($"{owner} has no field '{p.Name}' (its fields: {string.Join(", ", defs.Select(d => d.Label))})");
                given[def.Label] = p.Value;
            }
            var list = new List<MemeField>(defs.Count);
            foreach (var d in defs)
            {
                if (!given.TryGetValue(d.Label, out var v))
                {
                    list.Add(new MemeField(d, MemeRecord.DefaultValue(d)));
                    continue;
                }
                _path.Add(d.Label);
                list.Add(Field(d, v));
                _path.RemoveAt(_path.Count - 1);
            }
            return list;
        }

        private MemeField Field(MemeFieldDef d, JsonElement v)
        {
            switch (d.Token)
            {
                case MemeToken.O:
                    return new MemeField(d, Record(v));
                case MemeToken.List:
                {
                    MemeRecord? end = null;
                    var arr = v;
                    if (v.ValueKind == JsonValueKind.Object)
                    {
                        arr = v.TryGetProperty("items", out var items) ? items : throw Bad("a LIST object has 'items'");
                        if (v.TryGetProperty("end", out var e)) { _path.Add("end"); end = Record(e); _path.RemoveAt(_path.Count - 1); }
                    }
                    if (arr.ValueKind != JsonValueKind.Array) throw Bad("a LIST is an array of records");
                    var list = new List<MemeRecord>();
                    int i = 0;
                    foreach (var x in arr.EnumerateArray())
                    {
                        _path.Add($"[{i++}]");
                        list.Add(Record(x));
                        _path.RemoveAt(_path.Count - 1);
                    }
                    return new MemeField(d, list) { ListEnd = end };
                }
                case MemeToken.Tree:
                    return new MemeField(d, Tree(v, MemeSchema.TreeNodeLayout(d.TreeNode)));
                case MemeToken.F32:
                    if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("bits", out var bits))
                        return new MemeField(d, BitConverter.UInt32BitsToSingle(uint.Parse(bits.GetString() ?? "", NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
                    if (v.ValueKind != JsonValueKind.Number) throw Bad("an f32 is a number or {\"bits\": hex}");
                    return new MemeField(d, float.Parse(v.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture));
                case MemeToken.WStr32 when v.ValueKind == JsonValueKind.Object && v.TryGetProperty("utf16", out var hex):
                    return new MemeField(d, new string(MemoryMarshal.Cast<byte, char>(Hex(hex))));
                case MemeToken.S8:
                case MemeToken.Str32:
                case MemeToken.WStr32:
                    if (v.ValueKind != JsonValueKind.String) throw Bad($"a {MemeFormat.Token(d.Token)} is a string");
                    return new MemeField(d, v.GetString());
                default:
                {
                    if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out var n)) throw Bad($"a {MemeFormat.Token(d.Token)} is an integer");
                    try { return new MemeField(d, MemeField.Normalize(d.Token, d.Label, n)); }
                    catch (ArgumentException ex) { throw Bad(ex.Message); }
                }
            }
        }

        private MemeTreeNode? Tree(JsonElement e, IReadOnlyList<MemeFieldDef> layout)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            if (e.ValueKind == JsonValueKind.Null) return null;
            if (e.ValueKind != JsonValueKind.Object) throw Bad("a tree node is null or an object");
            var n = new MemeTreeNode
            {
                Flag = e.TryGetProperty("flag", out var flag) ? flag.GetByte() : (byte)1,
                Fields = e.TryGetProperty("fields", out var f) ? Fields(f, layout, "the tree node") : MemeTreeNode.New(layout).Fields,
            };
            foreach (var (key, set) in new (string, Action<MemeTreeNode?>)[] { ("child", x => n.Child = x), ("next", x => n.Next = x), ("prev", x => n.Prev = x) })
            {
                if (!e.TryGetProperty(key, out var sub)) continue;
                _path.Add(key);
                set(Tree(sub, layout));
                _path.RemoveAt(_path.Count - 1);
            }
            return n;
        }

        private byte[] Hex(JsonElement e)
        {
            try { return Convert.FromHexString(e.GetString() ?? ""); }
            catch (FormatException) { throw Bad("not hex"); }
        }
    }
}
