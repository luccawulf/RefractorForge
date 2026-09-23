using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using RefractorBridge.Con;

namespace RefractorBridge.Oracle;

/// <summary>What the census decided about one <c>setX</c> spelling on one class.</summary>
public enum SetFormDecision
{
    /// <summary>Retail uses the short form on this class and never the set- form. Safe to rewrite.</summary>
    RenameToShortForm,
    /// <summary>Retail uses the set- form on this class. Renaming it would break working content.</summary>
    KeepSetForm,
    /// <summary>Retail says nothing either way. Leave it alone and report it.</summary>
    NoEvidence,
}

public sealed record SetFormVerdict(
    SetFormDecision Decision,
    string ShortForm,
    int SetFormUses,
    int ShortFormUses,
    string Reason,
    /// <summary>True when only WHOLE-CORPUS evidence supported this - retail never uses the class at all.
    /// Weaker than per-class evidence, so the caller must confirm it against the executable.</summary>
    bool FromWholeCorpus = false);

/// <summary>The observed range of a numeric property on a class - for the "magnitudes, not just names" audits.</summary>
public sealed record ValueRange(double Min, double Max, int Samples);

/// <summary>
/// A per-class census of what RETAIL Battlefield Vietnam content actually does.
///
/// This is the oracle the executables cannot be. Refractor registers properties PER CLASS, and the exe string
/// table never says which class exposes a name - so it can be actively misleading: BfVietnam.exe carries
/// <c>setTorque</c>, yet on the <c>Engine</c> class retail content uses <c>torque</c> and the set- form zero
/// times. When the exe and per-class retail usage disagree, retail usage wins.
///
/// That distinction is not academic. The <c>setX</c> family is the single biggest pile in the BF1942 mod
/// corpus - FHSW alone has 151,010 such lines, bg42 69,223, Forgotten Hope 39,031 - and getting one wrong
/// fails SILENTLY: an unregistered property is discarded without a warning and the object keeps its default.
/// On one ported vehicle that produced four separate bugs days apart - no replication (<c>setNetworkableInfo</c>),
/// no suspension (<c>setStrength</c>), no throttle (<c>setTorque</c>), no trigger (<c>setInputFire</c>).
///
/// And there is no general "strip set" rule: BFV keeps the set- form for plenty of properties
/// (<c>setEntryRadius</c> 36 retail uses, <c>GeometryTemplate.setLodDistance</c> 7,589, <c>setMinimapIcon</c> 88),
/// so a blanket rewrite breaks working content in the other direction. Only a census decides.
/// </summary>
public sealed class StockCensus
{
    /// <summary>Numeric properties whose RANGE matters, not just their presence - see the audits in the plan.</summary>
    private static readonly HashSet<string> Watchlist = new(StringComparer.OrdinalIgnoreCase)
    {
        "cullRadiusScale", "hasDynamicShadow", "mass", "gearUp", "gearDown", "hasCollisionPhysics",
        "maxSpeed", "torque", "strength", "damping", "lodDistance", "setLodDistance",
    };

    /// <summary>class -> property -> how many times retail sets it on that class.</summary>
    public IReadOnlyDictionary<string, Dictionary<string, int>> Classes { get; }

    /// <summary>Every template name retail defines. Redefining one REPLACES it for the whole game.</summary>
    public IReadOnlySet<string> TemplateNames { get; }

    /// <summary>"Class.property" -> the numeric range retail uses, for the watchlist properties.</summary>
    public IReadOnlyDictionary<string, ValueRange> Ranges { get; }

    public int FilesScanned { get; }
    public int CommandsScanned { get; }

    private StockCensus(
        Dictionary<string, Dictionary<string, int>> classes,
        HashSet<string> templateNames,
        Dictionary<string, ValueRange> ranges,
        int filesScanned,
        int commandsScanned)
    {
        Classes = classes;
        TemplateNames = templateNames;
        Ranges = ranges;
        FilesScanned = filesScanned;
        CommandsScanned = commandsScanned;
    }

    // --- building ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Walk retail content and attribute every property to the class of the template it is being set on.
    ///
    /// The attribution is what makes this useful: a <c>.con</c> reads as a stream of
    /// <c>ObjectTemplate.create &lt;Class&gt; &lt;Name&gt;</c> followed by the properties set on it, so the
    /// "current class" has to be tracked as the file is walked. <c>activeSafe</c> re-opens an existing template
    /// and counts too - retail uses it constantly.
    /// </summary>
    public static StockCensus Build(IEnumerable<ConFile> files)
    {
        var classes = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sums = new Dictionary<string, (double Min, double Max, int N)>(StringComparer.OrdinalIgnoreCase);
        int fileCount = 0, commandCount = 0;

        foreach (var file in files)
        {
            fileCount++;
            // Per prefix ("ObjectTemplate", "GeometryTemplate", ...) the class currently being defined.
            var context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var cmd in ConDialect.Commands(file.Name, file.Bytes))
            {
                commandCount++;
                int dot = cmd.Command.IndexOf('.');
                if (dot <= 0) continue;

                string prefix = cmd.Command[..dot];
                string property = cmd.Command[(dot + 1)..];
                string[] args = ConDialect.Args(cmd.Rest);

                // A create/activeSafe on ObjectTemplate or GeometryTemplate names the class for what follows.
                if (IsClassOpener(prefix, property) && args.Length >= 2)
                {
                    context[prefix] = args[0];
                    names.Add(args[1]);
                    continue;
                }

                // Everything else names its own template but carries no class of its own.
                if (property.StartsWith("create", StringComparison.OrdinalIgnoreCase) && args.Length >= 1)
                {
                    names.Add(args[^1]);
                    context[prefix] = prefix;
                    continue;
                }

                string cls = context.TryGetValue(prefix, out string? c) ? c : prefix;

                if (!classes.TryGetValue(cls, out var props))
                    classes[cls] = props = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                props[property] = props.GetValueOrDefault(property) + 1;

                if (Watchlist.Contains(property) && args.Length >= 1 &&
                    double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                {
                    string key = $"{cls}.{property}";
                    if (sums.TryGetValue(key, out var r))
                        sums[key] = (Math.Min(r.Min, v), Math.Max(r.Max, v), r.N + 1);
                    else
                        sums[key] = (v, v, 1);
                }
            }
        }

        var ranges = sums.ToDictionary(k => k.Key, k => new ValueRange(k.Value.Min, k.Value.Max, k.Value.N),
            StringComparer.OrdinalIgnoreCase);

        return new StockCensus(classes, names, ranges, fileCount, commandCount);
    }

    private static bool IsClassOpener(string prefix, string property) =>
        (prefix.Equals("ObjectTemplate", StringComparison.OrdinalIgnoreCase) ||
         prefix.Equals("GeometryTemplate", StringComparison.OrdinalIgnoreCase)) &&
        (property.Equals("create", StringComparison.OrdinalIgnoreCase) ||
         property.Equals("activeSafe", StringComparison.OrdinalIgnoreCase));

    // --- asking ------------------------------------------------------------------------------------------------

    /// <summary>How many times retail sets this property on this class.</summary>
    public int Count(string className, string property) =>
        Classes.TryGetValue(className, out var props) ? props.GetValueOrDefault(property) : 0;

    /// <summary>How many times retail sets this property on ANY class.</summary>
    public int CountAnywhere(string property)
    {
        int total = 0;
        foreach (var props in Classes.Values) total += props.GetValueOrDefault(property);
        return total;
    }

    /// <summary>Which classes retail sets this property on, commonest first. The reverse lookup.</summary>
    public IEnumerable<(string Class, int Count)> ClassesUsing(string property) =>
        Classes.Where(c => c.Value.ContainsKey(property))
               .Select(c => (c.Key, c.Value[property]))
               .OrderByDescending(x => x.Item2);

    /// <summary>Does retail define a template by this name? Redefining one replaces it for the whole game.</summary>
    public bool DefinesTemplate(string name) => TemplateNames.Contains(name);

    /// <summary>
    /// The decision the whole census exists for. The rule, straight from the porting notes: rewrite only when
    /// retail uses your spelling ZERO times on this class and the alias many times. Anything else is left alone -
    /// a guess in either direction breaks something, and the failure is silent.
    /// </summary>
    public SetFormVerdict JudgeSetForm(string className, string setProperty, int minEvidence = 3)
    {
        if (!setProperty.StartsWith("set", StringComparison.OrdinalIgnoreCase) || setProperty.Length <= 3)
            return new SetFormVerdict(SetFormDecision.NoEvidence, setProperty, 0, 0, "not a set- spelling");

        string shortForm = ExeSymbolTable.SetTwinLeaf(setProperty);
        int setUses = Count(className, setProperty);
        int shortUses = Count(className, shortForm);

        // TIER 1 - per-class evidence. The strongest signal, and the one the porting notes settled on.
        if (setUses > 0)
            return new SetFormVerdict(SetFormDecision.KeepSetForm, shortForm, setUses, shortUses,
                $"retail uses '{setProperty}' {setUses}x on {className} - renaming it would break working content");

        if (shortUses >= minEvidence)
            return new SetFormVerdict(SetFormDecision.RenameToShortForm, shortForm, 0, shortUses,
                $"retail uses '{shortForm}' {shortUses}x on {className} and '{setProperty}' never");

        // TIER 2 - whole-corpus evidence, for a class retail simply never uses. Without this the census misses
        // renames that demonstrably matter: retail never sets either spelling of networkableInfo on
        // PlayerControlObject, yet it sets 'networkableInfo' 1,182x across six other classes and
        // 'setNetworkableInfo' ZERO times anywhere - and a template with no NetworkableInfo never replicates,
        // so the vehicle is built by the server and no client is ever told it exists.
        //
        // The guard that makes this safe is the same evidence read the other way: if retail uses the set- form
        // ANYWHERE, it is a spelling BFV genuinely keeps, and a class with no local evidence must not be
        // rewritten. That is what protects setLodDistance (7,395 retail uses), setEntryRadius and setMinimapIcon.
        int setAnywhere = CountAnywhere(setProperty);
        int shortAnywhere = CountAnywhere(shortForm);

        if (setAnywhere == 0 && shortAnywhere >= minEvidence)
            return new SetFormVerdict(SetFormDecision.RenameToShortForm, shortForm, 0, shortUses,
                $"retail never sets either spelling on {className}, but across all of retail it uses " +
                $"'{shortForm}' {shortAnywhere}x and '{setProperty}' never", FromWholeCorpus: true);

        if (setAnywhere > 0)
            return new SetFormVerdict(SetFormDecision.NoEvidence, shortForm, setUses, shortUses,
                $"retail never sets either spelling on {className}, and it DOES use '{setProperty}' " +
                $"{setAnywhere}x elsewhere - so the set- form is a spelling BFV keeps. Left alone");

        return new SetFormVerdict(SetFormDecision.NoEvidence, shortForm, setUses, shortUses,
            shortAnywhere == 0
                ? $"retail never sets either spelling of '{shortForm}' anywhere"
                : $"only {shortAnywhere} retail use(s) of '{shortForm}' anywhere - below the evidence bar");
    }

    // --- persistence -------------------------------------------------------------------------------------------

    private sealed record Dto(
        [property: JsonPropertyName("filesScanned")] int FilesScanned,
        [property: JsonPropertyName("commandsScanned")] int CommandsScanned,
        [property: JsonPropertyName("classes")] Dictionary<string, Dictionary<string, int>> Classes,
        [property: JsonPropertyName("templateNames")] List<string> TemplateNames,
        [property: JsonPropertyName("ranges")] Dictionary<string, ValueRange> Ranges);

    public void Save(string path)
    {
        var dto = new Dto(FilesScanned, CommandsScanned,
            Classes.ToDictionary(k => k.Key, v => v.Value),
            TemplateNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
            Ranges.ToDictionary(k => k.Key, v => v.Value));

        File.WriteAllText(path, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static StockCensus Load(string path)
    {
        var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(path))
                  ?? throw new InvalidDataException($"{path} is not a census");

        return new StockCensus(
            new Dictionary<string, Dictionary<string, int>>(dto.Classes, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(dto.TemplateNames, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, ValueRange>(dto.Ranges, StringComparer.OrdinalIgnoreCase),
            dto.FilesScanned, dto.CommandsScanned);
    }
}
