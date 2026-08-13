using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using RefractorForge.Formats.Geometry;

namespace RefractorForge.Formats.Con;

/// <summary>
/// The level support files Battlecraft maintains on save but RefractorForge historically never wrote, so an edited
/// map kept whatever the original author shipped: <c>cullRadius.con</c>, <c>PreCache.con</c> and
/// <c>ai/StrategicAreas.con</c>.
///
/// All three are ADDITIVE here. They are hand-tunable files (a mapper may have set a deliberate cull scale, or
/// authored strategic areas around terrain features rather than flags), so an existing file is parsed, kept
/// verbatim, and only the entries it is MISSING are appended. That fixes the real failure - objects you add in the
/// editor never reaching these lists - without overwriting anyone's tuning.
/// </summary>
public static class LevelSupportFiles
{
    private const string NL = "\r\n";   // Refractor .con files are CRLF
    private static string F(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>Names already declared by <c>Objecttemplate.active</c> / <c>Object.create</c> lines.</summary>
    private static HashSet<string> DeclaredNames(IEnumerable<string> lines, string verb)
    {
        var found = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var raw in lines)
        {
            var sp = raw.Trim().Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (sp.Length >= 2 && string.Equals(sp[0], verb, System.StringComparison.OrdinalIgnoreCase))
                found.Add(sp[1]);
        }
        return found;
    }

    // ---- cullRadius.con -----------------------------------------------------------------------------------

    /// <summary>The engine's per-template cull scale. Every entry in the retail files examined uses 5.</summary>
    public const int DefaultCullRadiusScale = 5;

    /// <summary>Append <c>cullRadiusScale</c> entries for static templates the file doesn't mention yet.
    /// Returns null when nothing is missing, so callers can skip the write entirely.</summary>
    public static string? AppendMissingCullRadius(IEnumerable<string>? existingLines, IEnumerable<string> templates,
                                                  int scale = DefaultCullRadiusScale)
    {
        var lines = existingLines?.ToList() ?? new List<string>();
        var have = DeclaredNames(lines, "Objecttemplate.active");
        var missing = templates.Where(t => !string.IsNullOrWhiteSpace(t) && have.Add(t)).ToList();
        if (missing.Count == 0) return null;

        var sb = new StringBuilder();
        foreach (var l in lines) sb.Append(l.Replace("\r", "").Replace("\n", "")).Append(NL);
        if (lines.Count == 0) sb.Append("REM *** Buildings & Objects ***").Append(NL);
        sb.Append(NL).Append("REM *** Added by RefractorForge ***").Append(NL).Append(NL);
        foreach (var t in missing)
            sb.Append("Objecttemplate.active ").Append(t).Append(NL)
              .Append("objectTemplate.cullRadiusScale ").Append(scale.ToString(CultureInfo.InvariantCulture))
              .Append(NL).Append(NL);
        return sb.ToString();
    }

    // ---- PreCache.con -------------------------------------------------------------------------------------

    /// <summary>Append <c>Object.create</c>/<c>Object.delete</c> pairs for templates the precache list is missing.
    /// The engine walks this at load to force each template's meshes and textures into memory, so a vehicle added
    /// in the editor and left out here loads late (the stutter when it first spawns).</summary>
    public static string? AppendMissingPreCache(IEnumerable<string>? existingLines, IEnumerable<string> templates)
    {
        var lines = existingLines?.ToList() ?? new List<string>();
        var have = DeclaredNames(lines, "Object.create");
        var missing = templates.Where(t => !string.IsNullOrWhiteSpace(t) && have.Add(t)).ToList();
        if (missing.Count == 0) return null;

        var sb = new StringBuilder();
        if (lines.Count == 0)
            sb.Append("Rem").Append(NL).Append("Rem PreCache Objects").Append(NL).Append("Rem").Append(NL)
              .Append(NL).Append("Object.active __BF_NONE__").Append(NL);
        else
            foreach (var l in lines) sb.Append(l.Replace("\r", "").Replace("\n", "")).Append(NL);

        sb.Append(NL).Append("Rem *** Added by RefractorForge ***").Append(NL);
        foreach (var t in missing)
            sb.Append("Object.create ").Append(t).Append(NL).Append("Object.delete").Append(NL);
        return sb.ToString();
    }

    // ---- ai/StrategicAreas.con ----------------------------------------------------------------------------

    /// <summary>
    /// A strategic area for the commander AI, laid out the way the retail files do:
    /// <c>aiStrategicArea.create &lt;name&gt; x1/z1 x2/z2 &lt;value&gt;</c> followed by neighbours, object-type
    /// flags, per-vehicle order positions, side and a vehicle search radius.
    /// </summary>
    public sealed record StrategicArea(string Name, float X, float Z, float HalfSize, int Value, int Side);

    /// <summary>Derive strategic areas from the control points: one area per flag, boxed around it, each linked to
    /// its <paramref name="neighbours"/> nearest siblings. This is the layer the bots reason about ABOVE the
    /// pathfinding grid - it decides which flag to attack next - so a map with none (or one whose flags have moved)
    /// leaves the commander AI working from the wrong picture.</summary>
    public static string BuildStrategicAreas(IReadOnlyList<ControlPointDef> cps, int neighbours = 2)
    {
        var areas = new List<StrategicArea>();
        foreach (var c in cps)
        {
            // The retail boxes are roughly the flag's capture area; value scales with how contested it is.
            float half = c.Radius > 1f ? c.Radius : 20f;
            int value = c.AreaValue > 0 ? c.AreaValue : 100;
            areas.Add(new StrategicArea(SafeName(c.Name), c.Position.X, c.Position.Z, half, value, c.Team));
        }

        var sb = new StringBuilder();
        sb.Append("rem *** Create strategic areas ***").Append(NL);
        sb.Append("rem *** Generated by RefractorForge from the level's control points ***").Append(NL).Append(NL);
        foreach (var a in areas)
            sb.Append("aiStrategicArea.create ").Append(a.Name).Append(' ')
              .Append(F(a.X - a.HalfSize)).Append('/').Append(F(a.Z - a.HalfSize)).Append(' ')
              .Append(F(a.X + a.HalfSize)).Append('/').Append(F(a.Z + a.HalfSize)).Append(' ')
              .Append(a.Value.ToString(CultureInfo.InvariantCulture)).Append(NL).Append(NL);

        foreach (var a in areas)
        {
            sb.Append("aiStrategicArea.setActive ").Append(a.Name).Append(NL);
            foreach (var n in areas.Where(o => o.Name != a.Name)
                                   .OrderBy(o => (o.X - a.X) * (o.X - a.X) + (o.Z - a.Z) * (o.Z - a.Z))
                                   .Take(neighbours))
                sb.Append("AIStrategicArea.addNeighbour ").Append(n.Name).Append(NL);
            sb.Append("aiStrategicArea.addObjectTypeFlag Base").Append(NL);
            sb.Append("AIStrategicArea.setOrderPosition Tank ").Append(F(a.X)).Append('/').Append(F(a.Z)).Append(NL);
            sb.Append("AIStrategicArea.setOrderPosition Infantry ").Append(F(a.X)).Append('/').Append(F(a.Z)).Append(NL);
            sb.Append("aiStrategicArea.setSide ").Append(a.Side.ToString(CultureInfo.InvariantCulture)).Append(NL);
            sb.Append("aiStrategicArea.vehicleSearchRadius ").Append(F(a.HalfSize * 2.4f)).Append(NL).Append(NL);
        }
        return sb.ToString();
    }

    /// <summary>Area names become .con identifiers, so strip anything that would break the parser.</summary>
    private static string SafeName(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name) sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        var s = sb.ToString().Trim('_');
        return s.Length == 0 ? "Area" : s;
    }
}
