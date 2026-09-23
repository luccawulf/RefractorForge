using System.Text;

namespace RefractorBridge.Oracle;

/// <summary>
/// The most reliable answer to "does this engine implement this console command".
///
/// Refractor stores every console object's property names as a contiguous NUL-separated table inside the
/// executable, so the two games' command sets can be diffed straight out of their binaries. That beats comparing
/// shipped .con files, which only show what the retail maps happened to use. This oracle is what found the
/// TreeMesh removal and the whole BF1942-only command list.
///
/// IMPORTANT, and the thing that is easy to get wrong: the table holds BARE PROPERTY NAMES
/// (<c>setTextureParam</c>), not dotted <c>Class.property</c> strings. Searching for the dotted form finds
/// nothing at all. Dotted strings do exist, but they are serialization forms such as <c>"Object.name "</c>, so
/// they are kept here only as a secondary signal. Verified against the real binaries: <c>setTextureParam</c>
/// BF1942=1/BFV=0, <c>hasResponsePhysics</c> 3/0, <c>setActiveCombatArea</c> 1/1 - the same counts the porting
/// notes recorded.
///
/// TWO CAVEATS, both learned the hard way, and both the reason <see cref="Con.ConDialect"/> consults its own
/// rule tables BEFORE this one:
///  * ABSENCE is the only strong signal, and even then only as "present in BF1942, absent from BFV". Presence
///    can be an accident: <c>normalMap</c>, <c>specularEnable</c> and <c>lightDirection</c> all appear in
///    BfVietnam.exe as D3D render-state and shader-constant names while being no part of BFV's water console
///    object. A naive leaf lookup would call those portable; they are not.
///  * The table never says WHICH CLASS exposes a name. Refractor registers properties per class, so the exe can
///    be actively misleading: BfVietnam.exe does carry <c>setTorque</c>, yet on the Engine class retail content
///    uses <c>torque</c> and the set- form zero times. When the exe and per-class retail usage disagree, retail
///    usage wins - which is why the setX-to-X family is reported for review and never rewritten on the exe's word.
/// </summary>
public sealed class ExeSymbolTable
{
    private readonly HashSet<string> _exact;        // whole printable runs - a NUL-delimited table entry is one
    private readonly HashSet<string> _identifiers;  // identifiers appearing inside a run
    private readonly HashSet<string> _dotted;       // "class.property" serialization forms

    public string Path { get; }

    /// <summary>Distinct NUL-delimited strings. The property table's entries are among these.</summary>
    public int StringCount => _exact.Count;

    /// <summary>Distinct identifiers - the pool a property name is looked up in.</summary>
    public int IdentifierCount => _identifiers.Count;

    /// <summary>Distinct dotted tokens, e.g. the <c>Object.name</c> serialization form.</summary>
    public int DottedCount => _dotted.Count;

    private ExeSymbolTable(string path, HashSet<string> exact, HashSet<string> identifiers, HashSet<string> dotted)
    {
        Path = path;
        _exact = exact;
        _identifiers = identifiers;
        _dotted = dotted;
    }

    private static readonly Dictionary<string, ExeSymbolTable> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ExeSymbolTable Load(string exePath)
    {
        string full = System.IO.Path.GetFullPath(exePath);
        lock (Cache)
        {
            if (Cache.TryGetValue(full, out var hit)) return hit;
            var table = Scan(full, File.ReadAllBytes(full));
            Cache[full] = table;
            return table;
        }
    }

    /// <summary>Is this property name registered anywhere in the executable?</summary>
    public bool HasProperty(string property)
    {
        string leaf = Leaf(property).ToLowerInvariant();
        return leaf.Length > 0 && (_exact.Contains(leaf) || _identifiers.Contains(leaf));
    }

    /// <summary>Does the executable carry this exact dotted token (a serialization form)?</summary>
    public bool HasDotted(string classProperty) => _dotted.Contains(classProperty.ToLowerInvariant());

    /// <summary>
    /// Does this engine plausibly implement the command? True when the property name, its set-stripped or
    /// set-prefixed twin, or the dotted serialization form is present. Treat a TRUE as weak and a FALSE as
    /// strong - see the class remarks.
    /// </summary>
    public bool HasCommand(string classProperty)
    {
        if (HasDotted(classProperty)) return true;
        string leaf = Leaf(classProperty);
        if (leaf.Length == 0) return false;
        if (HasProperty(leaf)) return true;
        string twin = SetTwinLeaf(leaf);
        return twin.Length > 0 && HasProperty(twin);
    }

    /// <summary>The part after the dot, or the whole string when there is no dot.</summary>
    public static string Leaf(string classProperty)
    {
        int dot = classProperty.LastIndexOf('.');
        return dot < 0 ? classProperty : classProperty[(dot + 1)..];
    }

    /// <summary>The set-stripped / set-prefixed twin of a bare property name.</summary>
    public static string SetTwinLeaf(string leaf)
    {
        if (leaf.Length == 0) return "";
        if (leaf.StartsWith("set", StringComparison.OrdinalIgnoreCase) && leaf.Length > 3)
        {
            string stripped = leaf[3..];
            return char.ToLowerInvariant(stripped[0]) + stripped[1..];
        }
        return "set" + char.ToUpperInvariant(leaf[0]) + leaf[1..];
    }

    /// <summary>The set-twin of a full <c>Class.property</c>, keeping the class.</summary>
    public static string SetTwin(string classProperty)
    {
        int dot = classProperty.IndexOf('.');
        if (dot <= 0 || dot >= classProperty.Length - 1) return "";
        string twin = SetTwinLeaf(classProperty[(dot + 1)..]);
        return twin.Length == 0 ? "" : $"{classProperty[..dot]}.{twin}";
    }

    // --- the scanner -------------------------------------------------------------------------------------------

    private static ExeSymbolTable Scan(string path, byte[] data)
    {
        var exact = new HashSet<string>(StringComparer.Ordinal);
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        var dotted = new HashSet<string>(StringComparer.Ordinal);
        int runStart = -1;

        for (int i = 0; i <= data.Length; i++)
        {
            bool printable = i < data.Length && data[i] >= 0x20 && data[i] <= 0x7e;
            if (printable)
            {
                if (runStart < 0) runStart = i;
                continue;
            }
            if (runStart >= 0)
            {
                Harvest(Encoding.Latin1.GetString(data, runStart, i - runStart), exact, identifiers, dotted);
                runStart = -1;
            }
        }

        return new ExeSymbolTable(path, exact, identifiers, dotted);
    }

    private static void Harvest(string run, HashSet<string> exact, HashSet<string> identifiers, HashSet<string> dotted)
    {
        exact.Add(run.ToLowerInvariant());

        // Every identifier inside the run. A NUL-delimited property entry is the whole run, but tables are also
        // packed with names inside longer strings, so both are collected.
        for (int i = 0; i < run.Length;)
        {
            if (!IsAlphaOrUnderscore(run[i])) { i++; continue; }
            int start = i;
            while (i < run.Length && IsWord(run[i])) i++;
            identifiers.Add(run[start..i].ToLowerInvariant());

            // ... and the dotted form, when one identifier is followed by '.' and another. The right-hand side
            // is a property name in its own right - miss it and 'setTorque' inside "ObjectTemplate.setTorque"
            // reads as absent from an engine that plainly carries it.
            if (i + 1 < run.Length && run[i] == '.' && IsAlphaOrUnderscore(run[i + 1]))
            {
                int afterDot = i + 1;
                int j = afterDot;
                while (j < run.Length && IsWord(run[j])) j++;
                dotted.Add(run[start..j].ToLowerInvariant());
                identifiers.Add(run[afterDot..j].ToLowerInvariant());
                i = j;
            }
        }
    }

    private static bool IsWord(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
    private static bool IsAlphaOrUnderscore(char c) => char.IsAsciiLetter(c) || c == '_';
}
