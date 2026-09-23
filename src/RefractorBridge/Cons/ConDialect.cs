using System.Text;
using RefractorBridge.Oracle;

namespace RefractorBridge.Con;

/// <summary>What the converter decided about one command.</summary>
public enum CommandVerdict
{
    /// <summary>BfVietnam.exe carries it. Ports unchanged.</summary>
    PresentInBfv,
    /// <summary>In BF1942.exe, absent from BfVietnam.exe. The line is silently discarded by BFV, so it is removed.</summary>
    DropBf1942Only,
    /// <summary>Absent from BOTH exes - it never did anything, in either game. Removed while cleaning.</summary>
    DropDeadInBoth,
    /// <summary>BFV spells it differently. Rewritten, arguments untouched.</summary>
    Rename,
    /// <summary>Same name, different argument shape. The value is rewritten.</summary>
    ValueFix,
    /// <summary>An engine-level command BfVietnam.exe does not carry, and the converter has no rule for it.</summary>
    MissingEngineLevel,
    /// <summary>
    /// An ObjectTemplate/Object/GeometryTemplate property. These are generated per class and are not all present
    /// as literals, so absence from the exe is INCONCLUSIVE - see <see cref="ExeSymbolTable"/>.
    /// </summary>
    SoftTableProperty,
}

/// <summary>One rewrite the converter made, for the change log.</summary>
public sealed record ConChange(string File, int Line, CommandVerdict Verdict, string Before, string? After, string Reason);

/// <summary>The kind of decision a review is asking for - typed so a corpus census can count them.</summary>
public enum ReviewKind
{
    /// <summary>A TreeMesh geometry. BFV has no such type, so the object cannot load at all.</summary>
    TreeMesh,
    /// <summary>Some other geometry type BFV does not register.</summary>
    UnknownGeometryType,
    /// <summary>`game.setTeamSkin` in BF1942's two-argument form; BFV needs an index.</summary>
    TeamSkinArity,
    /// <summary>`game.setKit` - same arity, but BFV's kit names and index range differ.</summary>
    KitNames,
    /// <summary>A member of the setX-to-X family; only a per-class census of retail content can settle it.</summary>
    PerClassRename,
}

/// <summary>Something a human has to decide. The converter never guesses these.</summary>
public sealed record ConReview(string File, int Line, ReviewKind Kind, string Text, string Note);

/// <summary>The rewritten bytes plus every change made to get them.</summary>
public sealed record ConRewriteResult(byte[] Bytes, IReadOnlyList<ConChange> Changes, IReadOnlyList<ConReview> Reviews)
{
    public bool Changed => Changes.Count > 0;
}

/// <summary>One command as used by the analysed files.</summary>
public sealed record CommandRow(string Command, int Count, CommandVerdict Verdict, string Note, string ExampleFile);

/// <summary>The whole analysis of a file, tree or archive.</summary>
public sealed record ConAnalysis(
    IReadOnlyList<CommandRow> Rows,
    IReadOnlyList<ConReview> Reviews,
    IReadOnlyList<string> MissingRequired,
    int FilesScanned);

public sealed record ConDialectOptions
{
    /// <summary>
    /// Map <c>hasResponsePhysics</c> onto BFV's <c>hasMobilePhysics</c> instead of dropping it. Off by default:
    /// the property is unregistered in BFV so the line does nothing there either way, and mapping it ADDS physics
    /// behaviour the source never asked BFV for. Dropping is the change that cannot be wrong.
    /// </summary>
    public bool MapResponsePhysics { get; init; }

    /// <summary>
    /// A census of retail Battlefield Vietnam content. Supply one and the setX-to-X family stops being a pile
    /// of review notes and becomes evidence-backed rewrites - which matters, because that pile is the biggest
    /// thing in the corpus (FHSW alone has 151,010 such lines). Without a census those lines are reported and
    /// left exactly as they are; the converter never guesses a rename.
    /// </summary>
    public Oracle.StockCensus? Census { get; init; }

    /// <summary>How many retail uses of the short form it takes before a rename is considered proven.</summary>
    public int CensusMinEvidence { get; init; } = 3;

    /// <summary>
    /// BfVietnam.exe's own table. Required to confirm a WHOLE-CORPUS rename, because retail content saying
    /// nothing about a class is not the same as the engine not implementing the command. Retail BFV ships no
    /// cloud blocks at all, so the corpus alone "proved" a rename of <c>Cloud.setName</c> to <c>Cloud.name</c>
    /// - and BfVietnam.exe carries <c>setName</c>, so that rename was simply wrong.
    /// </summary>
    public Oracle.ExeSymbolTable? Bfv { get; init; }
}

/// <summary>
/// The BF1942 -> Battlefield Vietnam .con dialect: what to drop, what to rename, what to hand back to a human.
///
/// Every rule here is *proven* - established by diffing the two executables' string tables and cross-checking
/// against retail BFV levels (see docs/BF1942_to_BFV_Converter_Plan.md and the Al Nas porting notes it cites).
/// Where the evidence only supports a judgement call - the setX-to-X family, team-skin arity, kit names - the
/// converter reports it for review rather than guessing, because each of those has a documented way to be
/// silently wrong: an unregistered property is discarded without a warning and the object is just quietly broken.
/// </summary>
public static class ConDialect
{
    // --- rules -------------------------------------------------------------------------------------------------

    /// <summary>Commands BFV does not implement. The key is a lowercased "class.property".</summary>
    private static readonly Dictionary<string, (CommandVerdict Verdict, string Reason)> DropRules = new(StringComparer.Ordinal)
    {
        ["shadermanager.settextureparam"] = (CommandVerdict.DropBf1942Only, "BFV's shaderManager has no texture-param setter; BFV takes its cube map from a .rs 'cubemap' line + Texture/env_default_0N.dds"),
        ["renderer.globalambientcolor"]   = (CommandVerdict.DropBf1942Only, "BF1942-only. It added ambient ON TOP of diffuse, so inheriting BF1942's tuned-down values renders a BFV level far too dark - raise renderer.diffuseColor to the retail 0.85-1.0 band instead"),
        ["renderer.animatedmeshambientcolor"] = (CommandVerdict.DropBf1942Only, "BF1942-only. BFV's nearest lever is renderer.standardmeshminintensity (retail 0.3-0.6)"),
        ["renderer.beginglobalcluster"]   = (CommandVerdict.DropBf1942Only, "BF1942-only"),
        ["renderer.endglobalcluster"]     = (CommandVerdict.DropBf1942Only, "BF1942-only"),

        // BF1942's layered water shader. BFV's water exposes only colour/alpha/depth.
        ["water.texlayer1"]               = (CommandVerdict.DropBf1942Only, "BF1942 layered water; BFV water is colour/alpha/depth only"),
        ["water.texlayer2"]               = (CommandVerdict.DropBf1942Only, "BF1942 layered water; BFV water is colour/alpha/depth only"),
        ["water.normalmap"]               = (CommandVerdict.DropBf1942Only, "BF1942 layered water; BFV water is colour/alpha/depth only"),
        ["water.scrolldirectionnormalmap"] = (CommandVerdict.DropBf1942Only, "BF1942 layered water"),
        ["water.scrolldirection1"]        = (CommandVerdict.DropBf1942Only, "BF1942 layered water"),
        ["water.scrolldirection2"]        = (CommandVerdict.DropBf1942Only, "BF1942 layered water"),
        ["water.scrolllayer1"]            = (CommandVerdict.DropBf1942Only, "BF1942 layered water"),
        ["water.scrolllayer2"]            = (CommandVerdict.DropBf1942Only, "BF1942 layered water"),
        ["water.scrollnormalmap"]         = (CommandVerdict.DropBf1942Only, "BF1942 layered water"),
        ["water.tilelayer1"]              = (CommandVerdict.DropBf1942Only, "BF1942 layered water"),
        ["water.tilelayer2"]              = (CommandVerdict.DropBf1942Only, "BF1942 layered water"),
        ["water.tilenormalmap"]           = (CommandVerdict.DropBf1942Only, "BF1942 layered water"),
        ["water.specularstreakfactor"]    = (CommandVerdict.DropBf1942Only, "BF1942 layered water"),
        ["water.specularenable"]          = (CommandVerdict.DropBf1942Only, "BF1942 layered water"),
        ["water.specularcolor"]           = (CommandVerdict.DropBf1942Only, "BF1942 layered water"),
        ["water.lightdirection"]          = (CommandVerdict.DropBf1942Only, "BF1942 layered water"),
        ["water.addblendenable"]          = (CommandVerdict.DropBf1942Only, "BF1942 layered water"),
        ["water.envmapenable"]            = (CommandVerdict.DropBf1942Only, "BF1942 layered water"),
        ["water.envmapcolor"]             = (CommandVerdict.DropBf1942Only, "BF1942 layered water"),

        ["objecttemplate.unabletochangeteam"] = (CommandVerdict.DropBf1942Only, "BF1942-only; BFV's ControlPoint has no such property"),

        // Found by censusing 20 BF1942 mods (~40,000 .con files) and then asking both executables. Each was
        // unruled in the first pass; the oracle settled it.
        ["game.addlanguageruntimedirectory"] = (CommandVerdict.DropBf1942Only, "BF1942-only (in BF1942.exe, absent from BfVietnam.exe). Seen in 19 of 20 mods censused - BFV resolves localisation its own way"),
        ["render.beginglobalcluster"]       = (CommandVerdict.DropBf1942Only, "BF1942-only; the 'render.' spelling of beginGlobalCluster, used by Desert Combat and GC_Redux"),
        ["render.endglobalcluster"]         = (CommandVerdict.DropBf1942Only, "BF1942-only; the 'render.' spelling of endGlobalCluster"),
        ["kittemplate.allowedallied"]       = (CommandVerdict.DropBf1942Only, "BF1942-only kit-AI property (in BF1942.exe, absent from BfVietnam.exe)"),
        ["kittemplate.allowedaxis"]         = (CommandVerdict.DropBf1942Only, "BF1942-only kit-AI property (in BF1942.exe, absent from BfVietnam.exe)"),

        // The billboard/imposter system BF1942 pairs with TreeMesh. BFV registers neither, so a converted
        // tree keeps its geometry and loses the distance card it never had a shader for.
        ["geometrytemplate.billboard"]         = (CommandVerdict.DropBf1942Only, "BF1942-only; part of the billboard-imposter system BFV does not have (see the TreeMesh removal)"),
        ["geometrytemplate.billboarddistance"] = (CommandVerdict.DropBf1942Only, "BF1942-only; part of the billboard-imposter system BFV does not have"),

        // Found by the debug executable's own log on the first in-game test.
        ["geometrytemplate.setlodpercent"] = (CommandVerdict.DropDeadInBoth, "absent from BOTH exes; BfVietnam reports 'unknown function setLodPercent'"),
        ["game.assaultteam"] = (CommandVerdict.DropDeadInBoth, "absent from BOTH exes; BfVietnam's console reports 'unknown function game' for it"),

        // The BF1942 TSun lens-flare block. Every one of these is absent from BOTH executables, and its
        // textures (ring3/4/5, sunflare7/9) exist in no BF1942 or BFV archive. No retail BFV level defines
        // a LensFlare at all.
        ["objecttemplate.setbackflarecount"]    = (CommandVerdict.DropDeadInBoth, "BF1942 TSun lens-flare block; absent from both exes"),
        ["objecttemplate.setvisibilityangledeg"] = (CommandVerdict.DropDeadInBoth, "BF1942 TSun lens-flare block; absent from both exes"),
        ["objecttemplate.setflaresrcblend"]     = (CommandVerdict.DropDeadInBoth, "BF1942 TSun lens-flare block; absent from both exes"),
        ["objecttemplate.setflaredestblend"]    = (CommandVerdict.DropDeadInBoth, "BF1942 TSun lens-flare block; absent from both exes"),
        ["objecttemplate.setflarescale"]        = (CommandVerdict.DropDeadInBoth, "BF1942 TSun lens-flare block; absent from both exes"),
        ["objecttemplate.setflaredistfadescale"] = (CommandVerdict.DropDeadInBoth, "BF1942 TSun lens-flare block; absent from both exes"),
        ["objecttemplate.setflarefadeall"]      = (CommandVerdict.DropDeadInBoth, "BF1942 TSun lens-flare block; absent from both exes"),
        ["objecttemplate.setcoronasrcblend"]    = (CommandVerdict.DropDeadInBoth, "BF1942 TSun lens-flare block; absent from both exes"),
        ["objecttemplate.setcoronadestblend"]   = (CommandVerdict.DropDeadInBoth, "BF1942 TSun lens-flare block; absent from both exes"),
        ["objecttemplate.setcoronascale"]       = (CommandVerdict.DropDeadInBoth, "BF1942 TSun lens-flare block; absent from both exes"),
        ["objecttemplate.setcoronafadeall"]     = (CommandVerdict.DropDeadInBoth, "BF1942 TSun lens-flare block; absent from both exes"),

        ["networkableinfo.sethasorientation"] = (CommandVerdict.DropDeadInBoth, "absent from BOTH exes - a no-op in BF1942 too. Seen in 17 of 20 mods censused"),
        ["networkableinfo.setiscontrolledby"] = (CommandVerdict.DropDeadInBoth, "absent from BOTH exes - a no-op in BF1942 too"),

        // Dead in BOTH engines - they were no-ops in BF1942 too.
        ["geometrytemplate.loddistance"]  = (CommandVerdict.DropDeadInBoth, "not in the PatchTerrain table of EITHER engine (the lone lodDistance string belongs to the particle emitter). Note GeometryTemplate.setLodDistance and ObjectTemplate.lodDistance are both real and are left alone"),
        ["game.defaultstartpos"]          = (CommandVerdict.DropDeadInBoth, "absent from both exes"),
        ["game.startpos"]                 = (CommandVerdict.DropDeadInBoth, "absent from both exes"),
        ["spawnpointmanager.groupstatus"] = (CommandVerdict.DropDeadInBoth, "absent from both exes"),
        ["objecttemplate.exittimer"]      = (CommandVerdict.DropDeadInBoth, "absent from both exes"),
        ["objecttemplate.damagewhenlost"] = (CommandVerdict.DropDeadInBoth, "dead/unused"),
    };

    /// <summary>Commands BFV spells differently. Value is the replacement, in BFV's own casing.</summary>
    private static readonly Dictionary<string, (string Replacement, string Reason)> RenameRules = new(StringComparer.Ordinal)
    {
        ["renderer.foglinearstart"] = ("renderer.fogstart", "foglinear* is dead in BOTH engines; every retail BFV level uses fogstart/fogend, so the source's fog was never working"),
        ["renderer.foglinearend"]   = ("renderer.fogend",   "foglinear* is dead in BOTH engines; every retail BFV level uses fogstart/fogend"),
        ["game.setviewdistance"]    = ("Game.ViewDistance", "BFV registers both, but 12/12 retail levels use Game.ViewDistance"),
        ["object.setname"]          = ("Object.Name",       "BFV's Object serialization string is 'Object.name'; 11/11 retail levels write Object.Name"),
    };

    /// <summary>
    /// The confirmed setX-to-X family. Reported for REVIEW only - never rewritten here. Refractor registers
    /// properties per class and BfVietnam.exe lists BOTH spellings, so the exe cannot settle these; only a
    /// per-class census of retail content can, and that is a later milestone. Getting it wrong is silent: an
    /// unregistered property is discarded and the object keeps its default (no replication, no suspension,
    /// no throttle, no trigger - all four were real bugs on one ported vehicle).
    /// </summary>
    private static readonly HashSet<string> PerClassRenameSeeds = new(StringComparer.OrdinalIgnoreCase)
    {
        "setNetworkableInfo", "setAcceleration", "setMinRotation", "setMaxRotation", "setMaxSpeed",
        "setAutomaticReset", "setInputToRoll", "setInputToYaw", "setInputToPitch", "setAttachToListener",
        "setPivotPosition", "setStrength", "setDamping", "setTorque", "setEngineType", "setDifferential",
        "setNumberOfGears", "setGearUp", "setGearDown", "setGearChangeTime", "setNoPropellerEffectAtSpeed",
        "setGearDownHeight", "setGearDownEngineInput", "setGearUpEngineInput", "setPcoId", "setInputFire",
        "setVehicleIcon", "setVehicleIconPos", "setNumberOfWeaponIcons", "setAsynchronyFire",
        // the amphibious group, which a rename map derived from one land vehicle completely missed
        "setHullHeight", "setFloatMaxLift", "setFloatMinLift", "setWingLift", "setFlapLift", "setPositionOffset",
        "setHasCollisionPhysics", "setHasTurretIcon", "setAutomaticYawStabilization", "setAutomaticPitchStabilization",
    };

    /// <summary>
    /// Properties where BFV KEEPS the set- form. Renaming these breaks working content, so they are never
    /// flagged: setEntryRadius has 36 retail uses, GeometryTemplate.setLodDistance 7589, setMinimapIcon 88.
    /// </summary>
    private static readonly HashSet<string> KeepSetForm = new(StringComparer.OrdinalIgnoreCase)
    {
        "setEntryRadius", "setLodDistance", "addToCollisionGroup", "setStartOnEffects",
        "setCrossHairType", "setToolTipType", "setPrimaryAmmoBar", "setPredictionMode", "setObjectTemplate",
        "setTeamGeometry", "setHasCollisionPhysics2",
    };

    /// <summary>The geometry types BfVietnam.exe registers. Anything else cannot load - TreeMesh above all.</summary>
    private static readonly HashSet<string> BfvGeometryTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AnimatedMesh", "ParticleSystemTemplate", "PatchTerrain", "RoamTerrain", "SimpleGeom",
        "SkeletonCollisionMesh", "StandardMesh",
    };

    /// <summary>Classes whose properties are generated per class, so exe absence is inconclusive.</summary>
    private static readonly HashSet<string> SoftClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "objecttemplate", "object", "geometrytemplate",
    };

    /// <summary>
    /// Commands essentially every retail BFV level carries and BF1942 levels do not. Reported as "missing"
    /// so the level porter (a later milestone) knows what to add; not injected by the line rewriter.
    /// </summary>
    public static readonly IReadOnlyList<string> RequiredBfvCommands = new[]
    {
        "Game.setLoadMusicFilename", "renderer.SecondaryDiffuseColor", "renderer.LMambientColor",
        "renderer.standardmeshminintensity", "renderer.fogstart", "renderer.fogend", "Game.ViewDistance",
        "game.setTeamInsignia", "game.setTeamInsigniaName", "GeometryTemplate.waveHeight",
    };

    /// <summary>BFV's four-component shadow colour, as used by 12/12 retail levels.</summary>
    private const string BfvShadowColor = "0/0/.075/.5";

    // --- rewriting ---------------------------------------------------------------------------------------------

    public static ConRewriteResult Rewrite(byte[] latin1Bytes, string fileName = "", ConDialectOptions? options = null)
        => Rewrite(Encoding.Latin1.GetString(latin1Bytes), fileName, options);

    /// <summary>
    /// Apply the high-confidence dialect rules. Comments are never touched (Desert Combat writes
    /// "rem GeometryTemplate.file ...", and an unanchored rewrite would happily edit inside it), line endings are
    /// reproduced exactly, and a file with nothing to change comes back byte-identical.
    /// </summary>
    public static ConRewriteResult Rewrite(string text, string fileName = "", ConDialectOptions? options = null)
    {
        options ??= new ConDialectOptions();
        var lines = ConText.Split(text);
        var kept = new List<ConLine>(lines.Count);
        var changes = new List<ConChange>();
        var reviews = new List<ConReview>();
        int remDepth = 0;
        // Per prefix, the template class currently being defined - the census is keyed by it.
        var classContext = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            int lineNo = i + 1;
            string first = FirstWord(line.Content);

            if (first.Equals("endRem", StringComparison.OrdinalIgnoreCase))
            {
                if (remDepth > 0) remDepth--;
                kept.Add(line);
                continue;
            }
            if (remDepth > 0) { kept.Add(line); continue; }                       // inert: leave verbatim
            if (first.Equals("beginRem", StringComparison.OrdinalIgnoreCase)) { remDepth++; kept.Add(line); continue; }
            if (first.Equals("rem", StringComparison.OrdinalIgnoreCase)) { kept.Add(line); continue; }

            if (!TryParseCommand(line.Content, out string indent, out string command, out string rest))
            {
                kept.Add(line);
                continue;
            }

            string key = command.ToLowerInvariant();
            TrackClassContext(command, rest, classContext);

            // With a census in hand the setX family is decided rather than deferred. Done before the review
            // pass so a proven rename is applied instead of being reported.
            if (options.Census is not null)
            {
                var verdict = JudgeWithCensus(command, rest, classContext, options, out string cls);

                // Whole-corpus evidence is the weaker tier: confirm it against the executable before acting.
                if (verdict is { Decision: SetFormDecision.RenameToShortForm, FromWholeCorpus: true }
                    && options.Bfv?.HasProperty(command[(command.IndexOf('.') + 1)..]) == true)
                    verdict = verdict with
                    {
                        Decision = SetFormDecision.NoEvidence,
                        Reason = $"retail says nothing about {cls}, but BfVietnam.exe carries this spelling - left alone",
                    };

                if (verdict is { Decision: SetFormDecision.RenameToShortForm })
                {
                    string after = indent + command[..(command.IndexOf('.') + 1)] + verdict.ShortForm + rest;
                    changes.Add(new ConChange(fileName, lineNo, CommandVerdict.Rename, line.Content, after,
                        $"on class {cls}: {verdict.Reason}"));
                    kept.Add(line with { Content = after });
                    continue;
                }

                // Only a candidate the census could NOT settle is worth a human's time; a set- form retail
                // itself uses is simply correct and is not reported at all.
                if (verdict is { Decision: SetFormDecision.NoEvidence } && IsRenameCandidate(command))
                {
                    reviews.Add(new ConReview(fileName, lineNo, ReviewKind.PerClassRename, line.Content.Trim(),
                        $"On class {cls} the retail census cannot settle this: {verdict.Reason}. Left as written."));
                    kept.Add(line);
                    continue;
                }
            }

            CollectLineReviews(fileName, lineNo, line.Content, key, rest, reviews);

            // hasResponsePhysics: drop by default, map only when explicitly asked (see ConDialectOptions).
            if (key is "objecttemplate.hasresponsephysics" or "objecttemplate.sethasresponsephysics")
            {
                if (options.MapResponsePhysics)
                {
                    string after = indent + "ObjectTemplate.hasMobilePhysics" + rest;
                    changes.Add(new ConChange(fileName, lineNo, CommandVerdict.Rename, line.Content, after,
                        "mapped onto BFV's hasMobilePhysics at your request"));
                    kept.Add(line with { Content = after });
                }
                else
                {
                    changes.Add(new ConChange(fileName, lineNo, CommandVerdict.DropBf1942Only, line.Content, null,
                        "hasResponsePhysics is unregistered in BFV, so the line does nothing there; dropped (use --map-response-physics to map it onto hasMobilePhysics instead)"));
                }
                continue;
            }

            if (DropRules.TryGetValue(key, out var drop))
            {
                changes.Add(new ConChange(fileName, lineNo, drop.Verdict, line.Content, null, drop.Reason));
                continue;                                                         // the line is removed entirely
            }

            if (RenameRules.TryGetValue(key, out var ren))
            {
                string after = indent + ren.Replacement + rest;
                changes.Add(new ConChange(fileName, lineNo, CommandVerdict.Rename, line.Content, after, ren.Reason));
                kept.Add(line with { Content = after });
                continue;
            }

            // shadow.shadowColor took one float in BF1942 and takes four in BFV.
            if (key == "shadow.shadowcolor" && IsScalarArgument(rest))
            {
                string after = $"{indent}shadow.shadowColor {BfvShadowColor}";
                changes.Add(new ConChange(fileName, lineNo, CommandVerdict.ValueFix, line.Content, after,
                    "BFV's shadow.shadowColor takes four components (r/g/b/a); 12/12 retail levels use " + BfvShadowColor));
                kept.Add(line with { Content = after });
                continue;
            }

            kept.Add(line);
        }

        return new ConRewriteResult(ConText.JoinBytes(kept), changes, reviews);
    }

    // --- walking ---------------------------------------------------------------------------------------------

    /// <summary>One live command line: everything the walker already worked out about it.</summary>
    public readonly record struct ConCommand(string File, int Line, string Content, string Command, string Rest);

    /// <summary>
    /// Every command a script actually executes, with comments skipped. Shared by the analyser and the stock
    /// census so the two can never disagree about what counts as live: Desert Combat writes
    /// <c>rem GeometryTemplate.file ...</c>, and a scanner that reads inside comments reports work that is not
    /// there - or, worse, edits it.
    /// </summary>
    public static IEnumerable<ConCommand> Commands(string fileName, byte[] bytes)
    {
        var lines = ConText.Split(bytes);
        int remDepth = 0;

        for (int i = 0; i < lines.Count; i++)
        {
            string content = lines[i].Content;
            string first = FirstWord(content);

            if (first.Equals("endRem", StringComparison.OrdinalIgnoreCase)) { if (remDepth > 0) remDepth--; continue; }
            if (remDepth > 0) continue;
            if (first.Equals("beginRem", StringComparison.OrdinalIgnoreCase)) { remDepth++; continue; }
            if (first.Equals("rem", StringComparison.OrdinalIgnoreCase)) continue;
            if (!TryParseCommand(content, out _, out string command, out string rest)) continue;

            yield return new ConCommand(fileName, i + 1, content, command, rest);
        }
    }

    /// <summary>Split a command's arguments the way the engine does - on whitespace.</summary>
    public static string[] Args(string rest) =>
        rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // --- analysis ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Classify every command the given files use against both executables. This is the offline gate's core
    /// check - "does anything BF1942-only survive?" - and the thing to run first on a mod nobody has ported yet.
    /// </summary>
    public static ConAnalysis Analyze(IEnumerable<ConFile> files, ExeSymbolTable bfv, ExeSymbolTable bf42)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var example = new Dictionary<string, string>(StringComparer.Ordinal);
        var cased = new Dictionary<string, string>(StringComparer.Ordinal);
        var reviews = new List<ConReview>();
        int scanned = 0;

        foreach (var file in files)
        {
            scanned++;
            foreach (var c in Commands(file.Name, file.Bytes))
            {
                string key = c.Command.ToLowerInvariant();
                counts[key] = counts.GetValueOrDefault(key) + 1;
                if (!example.ContainsKey(key)) { example[key] = file.Name; cased[key] = c.Command; }
                CollectLineReviews(file.Name, c.Line, c.Content, key, c.Rest, reviews);
            }
        }

        var rows = new List<CommandRow>();
        foreach ((string key, int count) in counts)
        {
            var (verdict, note) = Classify(key, bfv, bf42);
            rows.Add(new CommandRow(cased[key], count, verdict, note, example[key]));
        }

        rows.Sort((a, b) => a.Verdict != b.Verdict
            ? a.Verdict.CompareTo(b.Verdict)
            : string.Compare(a.Command, b.Command, StringComparison.OrdinalIgnoreCase));

        var missing = RequiredBfvCommands
            .Where(r => !counts.ContainsKey(r.ToLowerInvariant()))
            .ToList();

        return new ConAnalysis(rows, reviews, missing, scanned);
    }

    /// <summary>Where one command stands, by the rules first and the two exes second.</summary>
    public static (CommandVerdict Verdict, string Note) Classify(string command, ExeSymbolTable bfv, ExeSymbolTable bf42)
    {
        string key = command.ToLowerInvariant();

        if (key is "objecttemplate.hasresponsephysics" or "objecttemplate.sethasresponsephysics")
            return (CommandVerdict.DropBf1942Only, "unregistered in BFV; the line is silently discarded there");
        if (DropRules.TryGetValue(key, out var drop)) return (drop.Verdict, drop.Reason);
        if (RenameRules.TryGetValue(key, out var ren)) return (CommandVerdict.Rename, $"-> {ren.Replacement}: {ren.Reason}");
        if (key == "shadow.shadowcolor") return (CommandVerdict.ValueFix, "BFV takes four components (r/g/b/a); a single float is rewritten");
        if (bfv.HasCommand(key)) return (CommandVerdict.PresentInBfv, "");

        string cls = key.Split('.', 2)[0];
        string twin = ExeSymbolTable.SetTwin(key);

        if (SoftClasses.Contains(cls))
        {
            string note = bf42.HasCommand(key)
                ? "generated per class, so exe absence is inconclusive - present in BF1942.exe"
                : "generated per class, so exe absence is inconclusive - absent from BF1942.exe too";
            if (twin.Length > 0 && bfv.HasCommand(twin)) note += $"; BFV does carry '{twin}'";
            return (CommandVerdict.SoftTableProperty, note);
        }

        // Absent from BOTH engines is a different thing from "BFV dropped it": the line never did anything in
        // BF1942 either, so it is a no-op the port inherits rather than a problem the port has to solve. In the
        // 20-mod census these were almost all typos in the mod's own content - Pirates ships
        // 'setPBlueictionMode', which is setPredictionMode after a global Red->Blue search-and-replace.
        if (!bf42.HasCommand(key))
            return (CommandVerdict.DropDeadInBoth,
                "absent from BOTH exes - a no-op in BF1942 too (often a typo in the source). Safe to leave or clean");

        return (CommandVerdict.MissingEngineLevel, "in BF1942.exe, ABSENT from BfVietnam.exe - must be rewritten or dropped");
    }

    // --- line-level review flags -------------------------------------------------------------------------------

    /// <summary>
    /// Follow which template class the script is currently defining, so a property can be attributed to it.
    /// <c>activeSafe</c> re-opens an existing template and counts exactly like <c>create</c>.
    /// </summary>
    private static void TrackClassContext(string command, string rest, Dictionary<string, string> context)
    {
        int dot = command.IndexOf('.');
        if (dot <= 0) return;

        string prefix = command[..dot], property = command[(dot + 1)..];
        bool opener = (prefix.Equals("ObjectTemplate", StringComparison.OrdinalIgnoreCase) ||
                       prefix.Equals("GeometryTemplate", StringComparison.OrdinalIgnoreCase)) &&
                      (property.Equals("create", StringComparison.OrdinalIgnoreCase) ||
                       property.Equals("activeSafe", StringComparison.OrdinalIgnoreCase));
        if (!opener) return;

        var args = Args(rest);
        if (args.Length >= 2) context[prefix] = args[0];
    }

    /// <summary>
    /// Ask the retail census about a <c>setX</c> line, in the class the script is currently defining.
    /// Returns null when the line is not a setX candidate at all.
    /// </summary>
    private static SetFormVerdict? JudgeWithCensus(
        string command, string rest, Dictionary<string, string> context, ConDialectOptions options, out string cls)
    {
        cls = "";
        int dot = command.IndexOf('.');
        if (dot <= 0) return null;

        string prefix = command[..dot], property = command[(dot + 1)..];
        if (!property.StartsWith("set", StringComparison.OrdinalIgnoreCase) || property.Length <= 3) return null;
        if (KeepSetForm.Contains(property)) return null;

        // A property with no argument is not a value assignment, so there is nothing to carry over.
        if (Args(rest).Length == 0) return null;

        cls = context.TryGetValue(prefix, out string? c) ? c : prefix;
        var verdict = options.Census!.JudgeSetForm(cls, property, options.CensusMinEvidence);
        if (verdict.Decision != SetFormDecision.NoEvidence || options.Bfv is null) return verdict;

        // TIER 3 - the executable, for a property retail content simply never exercised. If BfVietnam.exe
        // carries no such identifier at all but does carry the short form, the set- spelling cannot be a
        // registered property there. The engine says as much out loud at runtime: "Don't use 'set' on
        // properties any longer, instead use: ObjectTemplate.minimapIcon".
        //
        // Guarded by retail usage so a spelling BFV demonstrably keeps is never touched: setLodDistance has
        // 7,395 retail uses, and the exe alone would happily have renamed it.
        string shortForm = ExeSymbolTable.SetTwinLeaf(property);
        if (shortForm.Length > 0
            && options.Census.CountAnywhere(property) == 0
            && !options.Bfv.HasProperty(property)
            && options.Bfv.HasProperty(shortForm))
        {
            return verdict with
            {
                Decision = SetFormDecision.RenameToShortForm,
                ShortForm = shortForm,
                Reason = $"BfVietnam.exe carries no '{property}' at all but does carry '{shortForm}', " +
                         "and retail content never uses the set- spelling anywhere",
                FromWholeCorpus = false,
            };
        }

        return verdict;
    }

    /// <summary>A setX spelling the porting work has already seen go wrong - worth a human's attention.</summary>
    private static bool IsRenameCandidate(string command)
    {
        int dot = command.IndexOf('.');
        return dot > 0 && PerClassRenameSeeds.Contains(command[(dot + 1)..]);
    }

    private static void CollectLineReviews(string file, int lineNo, string content, string key, string rest,
        List<ConReview> into)
    {
        if (key == "geometrytemplate.create")
        {
            string type = FirstWord(rest);
            if (type.Length > 0 && !BfvGeometryTypes.Contains(type))
            {
                bool tree = type.Equals("TreeMesh", StringComparison.OrdinalIgnoreCase);
                into.Add(new ConReview(file, lineNo,
                    tree ? ReviewKind.TreeMesh : ReviewKind.UnknownGeometryType,
                    content.Trim(),
                    tree
                        ? "BFV registers no TreeMesh geometry type, so this object cannot load. Convert the .tm to a StandardMesh (.sm + .rs), substitute BFV vegetation, or drop it."
                        : $"'{type}' is not one of BFV's registered geometry types ({string.Join(", ", BfvGeometryTypes)})."));
            }
            return;
        }

        if (key == "game.setteamskin" && CountArgs(rest) == 2)
        {
            into.Add(new ConReview(file, lineNo, ReviewKind.TeamSkinArity, content.Trim(),
                "BF1942 takes <team> <skin>; BFV takes <team> <index> <skin> with index 1..4. The index is a choice, so it is not guessed here. (Retail BFV levels only ever set teams 1 and 2.)"));
            return;
        }

        if (key == "game.setkit")
        {
            into.Add(new ConReview(file, lineNo, ReviewKind.KitNames, content.Trim(),
                "Same arity, but BFV defines 8 kits per team (4 base + 4 _Alt, index 0..7) and the kit NAMES are game-specific."));
            return;
        }

        int dot = key.IndexOf('.');
        if (dot > 0)
        {
            string leaf = key[(dot + 1)..];
            if (leaf.StartsWith("set", StringComparison.Ordinal) && leaf.Length > 3)
            {
                string original = FirstWord(content).Split('.', 2) is [_, var l] ? l : leaf;
                if (!KeepSetForm.Contains(original) && PerClassRenameSeeds.Contains(original))
                {
                    into.Add(new ConReview(file, lineNo, ReviewKind.PerClassRename, content.Trim(),
                        $"Known per-class rename candidate: retail BFV uses '{ExeSymbolTable.SetTwin(key).Split('.', 2)[^1]}' on this class and the set- form zero times. NOT rewritten automatically - BfVietnam.exe registers both spellings, so only a per-class census of retail content can settle it. Getting this wrong fails silently (the property keeps its default)."));
                }
            }
        }
    }

    // --- small helpers -----------------------------------------------------------------------------------------

    /// <summary>Split "  Class.property args..." into its indent, the command, and everything after it.</summary>
    public static bool TryParseCommand(string content, out string indent, out string command, out string rest)
    {
        indent = command = rest = "";
        int i = 0;
        while (i < content.Length && (content[i] == ' ' || content[i] == '\t')) i++;
        indent = content[..i];

        int start = i;
        if (i >= content.Length || !(char.IsAsciiLetter(content[i]) || content[i] == '_')) return false;
        while (i < content.Length && (char.IsAsciiLetterOrDigit(content[i]) || content[i] == '_')) i++;
        if (i >= content.Length || content[i] != '.') return false;
        i++;
        if (i >= content.Length || !(char.IsAsciiLetter(content[i]) || content[i] == '_')) return false;
        while (i < content.Length && (char.IsAsciiLetterOrDigit(content[i]) || content[i] == '_')) i++;

        command = content[start..i];
        rest = content[i..];
        return true;
    }

    private static string FirstWord(string s)
    {
        int i = 0;
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
        int start = i;
        while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
        return s[start..i];
    }

    private static int CountArgs(string rest) =>
        rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

    /// <summary>True for a single non-vector argument, i.e. BF1942's one-float shadow colour.</summary>
    private static bool IsScalarArgument(string rest)
    {
        var args = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return args.Length == 1 && !args[0].Contains('/');
    }
}
