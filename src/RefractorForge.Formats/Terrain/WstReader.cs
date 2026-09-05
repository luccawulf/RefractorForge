using System;
using System.Collections.Generic;

namespace RefractorForge.Formats.Terrain;

/// <summary>
/// A forgiving reader for the tag soup in <c>.wst</c> growth palettes.
/// <para>
/// These files look like XML but four retail BFVietnam levels ship ones that no strict parser will accept, and the
/// game loads them anyway: <c>Khe_Sahn</c> and <c>Lang_Vei</c> open <c>&lt;wetDirt&gt;</c> and close it with
/// <c>&lt;/juicyGrass&gt;</c>, <c>Operation_Cedar_Falls</c> closes <c>&lt;c03f_trees_m2&gt;</c> with
/// <c>&lt;/c05f_trees_m2&gt;</c>, and <c>Dogs_of_War</c> has an element whose name starts with "(". Feeding those to
/// <c>XDocument</c> throws, and a level whose palette failed to load shows no trees at all - so the editor has to be
/// as tolerant as the engine.
/// </para>
/// So: scan tags, keep the NESTING, and let any close tag close the innermost open element whatever name it claims.
/// Element names are never validated, because the name that matters is the <c>geometryName</c> attribute.
/// </summary>
public sealed class WstNode
{
    public string Name { get; init; } = "";
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<WstNode> Children { get; } = new();

    public WstNode? Child(string localName)
    {
        foreach (var c in Children)
            if (string.Equals(c.Name, localName, StringComparison.OrdinalIgnoreCase)) return c;
        return null;
    }

    public string? Attr(string name) => Attributes.TryGetValue(name, out var v) ? v : null;

    /// <summary>Parse tag soup into a tree. Never throws on malformed input; the worst case is a shallow tree.</summary>
    public static WstNode Parse(string text)
    {
        var root = new WstNode { Name = "#document" };
        var open = new List<WstNode> { root };          // used as a stack; index 0 is the document
        int i = 0;

        while (true)
        {
            int lt = text.IndexOf('<', i);
            if (lt < 0) break;

            // <?xml ... ?>, <!-- ... -->, <!DOCTYPE ...>: skip wholesale.
            if (lt + 1 < text.Length && (text[lt + 1] == '?' || text[lt + 1] == '!'))
            {
                int skip = text.IndexOf('>', lt);
                if (skip < 0) break;
                i = skip + 1;
                continue;
            }

            int gt = TagEnd(text, lt);
            if (gt < 0) break;
            var inner = text.Substring(lt + 1, gt - lt - 1).Trim();
            i = gt + 1;
            if (inner.Length == 0) continue;

            if (inner[0] == '/')
            {
                // Whatever it names, it ends the element we are inside. That is the whole point of this reader.
                if (open.Count > 1) open.RemoveAt(open.Count - 1);
                continue;
            }

            bool selfClosing = inner[^1] == '/';
            if (selfClosing) inner = inner[..^1].TrimEnd();

            var node = ReadTag(inner);
            open[^1].Children.Add(node);
            if (!selfClosing) open.Add(node);
        }
        return root;
    }

    /// <summary>Index of the '&gt;' that ends the tag opening at <paramref name="lt"/>, ignoring quoted values.</summary>
    private static int TagEnd(string text, int lt)
    {
        char quote = '\0';
        for (int i = lt + 1; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c == '"' || c == '\'') { quote = c; continue; }
            if (c == '>') return i;
        }
        return -1;
    }

    // "name attr = "value" attr2='v'" -> node. Unquoted values are accepted too; a bare word is a valueless flag.
    private static WstNode ReadTag(string inner)
    {
        int n = 0;
        while (n < inner.Length && !char.IsWhiteSpace(inner[n])) n++;
        var node = new WstNode { Name = inner[..n] };

        int i = n;
        while (i < inner.Length)
        {
            while (i < inner.Length && (char.IsWhiteSpace(inner[i]) || inner[i] == '=')) i++;
            if (i >= inner.Length) break;

            int nameStart = i;
            while (i < inner.Length && !char.IsWhiteSpace(inner[i]) && inner[i] != '=') i++;
            var key = inner[nameStart..i];
            if (key.Length == 0) break;

            while (i < inner.Length && char.IsWhiteSpace(inner[i])) i++;
            if (i >= inner.Length || inner[i] != '=') { node.Attributes[key] = ""; continue; }
            i++;                                                    // the '='
            while (i < inner.Length && char.IsWhiteSpace(inner[i])) i++;
            if (i >= inner.Length) { node.Attributes[key] = ""; break; }

            string value;
            if (inner[i] == '"' || inner[i] == '\'')
            {
                char q = inner[i++];
                int valueStart = i;
                while (i < inner.Length && inner[i] != q) i++;
                value = inner[valueStart..Math.Min(i, inner.Length)];
                if (i < inner.Length) i++;                          // the closing quote
            }
            else
            {
                int valueStart = i;
                while (i < inner.Length && !char.IsWhiteSpace(inner[i])) i++;
                value = inner[valueStart..i];
            }
            node.Attributes[key] = value.Trim();
        }
        return node;
    }
}
