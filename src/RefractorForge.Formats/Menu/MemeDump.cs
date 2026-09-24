using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace RefractorForge.Formats.Menu;

/// <summary>A readable tree of a menu file, like the reference's <c>meme.py dump</c>: one line per record and value,
/// indented by nesting, enum values with their names.</summary>
public static class MemeDump
{
    public static string ToText(MemeFile file, bool values = true) => DeepStack.Run(() =>
    {
        var sb = new StringBuilder();
        Rec(sb, file.Root, 0, "root", values);
        return sb.ToString();
    });

    /// <summary>Beyond this many levels a line shows its depth instead of more indentation: a sibling chain nests one
    /// level per node, and indenting a very long one would make the text grow with the square of its length.</summary>
    private const int MaxIndent = 200;

    private static void Line(StringBuilder sb, int depth, string text)
    {
        if (depth <= MaxIndent) sb.Append(' ', 2 * depth);
        else sb.Append(' ', 2 * MaxIndent).Append('[').Append(depth).Append("] ");
        sb.Append(text).Append('\n');
    }

    private static void Rec(StringBuilder sb, MemeRecord? r, int depth, string label, bool values)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        r ??= MemeRecord.Null();
        switch (r.Kind)
        {
            case MemeRecordKind.Null:
                Line(sb, depth, $"{label}: null" + (r.Raw is { Length: > 0 } ? $"  [{r.Raw.Length} dead bytes]" : ""));
                return;
            case MemeRecordKind.Ref:
                Line(sb, depth, $"{label}: -> '{r.Name}'" + (r.Raw is { Length: > 0 } ? $"  [{r.Raw.Length} dead bytes]" : ""));
                return;
        }
        var head = $"{label}: {r.Short}" + (r.Name.Length > 0 ? $" \"{r.Name}\"" : "");
        if (r.Kind == MemeRecordKind.Opaque)
        {
            Line(sb, depth, $"{head}  [OPAQUE {r.Raw?.Length ?? 0} bytes: {r.Note}]");
            return;
        }
        Line(sb, depth, head);
        Fields(sb, r.Fields!, depth + 1, values);
    }

    private static void Fields(StringBuilder sb, List<MemeField> fields, int depth, bool values)
    {
        foreach (var f in fields)
        {
            switch (f.Token)
            {
                case MemeToken.O:
                    Rec(sb, f.Value as MemeRecord, depth, f.Label, values);
                    break;
                case MemeToken.List:
                {
                    var items = f.Value as IList<MemeRecord> ?? Array.Empty<MemeRecord>();
                    Line(sb, depth, $"{f.Label}: LIST[{items.Count}]");
                    for (int i = 0; i < items.Count; i++) Rec(sb, items[i], depth + 1, $"[{i}]", values);
                    if (f.ListEnd is not null) Rec(sb, f.ListEnd, depth + 1, "[end]", values);
                    break;
                }
                case MemeToken.Tree:
                    if (f.Value is MemeTreeNode n)
                    {
                        Line(sb, depth, $"{f.Label}: TREE");
                        Tree(sb, n, depth + 1, "node", values);
                    }
                    else Line(sb, depth, $"{f.Label}: TREE empty");
                    break;
                default:
                    if (values) Line(sb, depth, $"{f.Label} = {Value(f)}  ({MemeFormat.Token(f.Token)})");
                    break;
            }
        }
    }

    private static void Tree(StringBuilder sb, MemeTreeNode n, int depth, string label, bool values)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        Line(sb, depth, n.Flag == 1 ? $"{label}:" : $"{label} (flag {n.Flag}):");
        Fields(sb, n.Fields, depth + 1, values);
        if (n.Child is not null) Tree(sb, n.Child, depth + 1, "child", values);
        if (n.Next is not null) Tree(sb, n.Next, depth + 1, "next", values);
        if (n.Prev is not null) Tree(sb, n.Prev, depth + 1, "prev", values);
    }

    /// <summary>A value as the dump shows it: strings quoted with control characters escaped, floats in their shortest
    /// round-trip form, enum values with their name.</summary>
    public static string Value(MemeField f)
    {
        switch (f.Value)
        {
            case string s: return Quote(s);
            case float x: return MemeFormat.Float(x);
            case null: return "null";
        }
        var text = Convert.ToString(f.Value, CultureInfo.InvariantCulture) ?? "";
        if (f.Def?.EnumNames is { } names && MemeValues.TryInteger(f.Value, out var v) && v is >= int.MinValue and <= int.MaxValue
            && names.TryGetValue((int)v, out var name))
            return $"{text} ({name})";
        return text;
    }

    private static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('\'');
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\'': sb.Append("\\'"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsSurrogate(c)) sb.Append($"\\u{(int)c:x4}");
                    else if (c < ' ' || c is >= '\x7F' and <= '\x9F') sb.Append($"\\x{(int)c:x2}");
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('\'').ToString();
    }
}
