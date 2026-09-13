using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RefractorForge.Formats.Con;

/// <summary>
/// Turning an Object Library entry into the name written as <c>object.create</c>.
///
/// The library is built from MESH file names, grouped under a stem with the ".sm" and the LOD suffix dropped, so
/// <c>o_speakers_m1.sm</c> is listed as "o_speakers". That is a display name, not a template: BfVietnam declares
/// <c>ObjectTemplate.create SimpleObject o_speakers_m1</c> and nothing called "o_speakers". A drop used to write the
/// stem verbatim, and the game answered with "createObject failed, unknown objectTemplate", a parse error for each of
/// the position and rotation lines after it, and nothing drawn. Which name to write has to be settled against the
/// TEMPLATE registry, exactly - the mesh resolver deliberately tolerates _m1/_m2 and would have passed the stem.
/// </summary>
public static class LibraryTemplate
{
    private static readonly Regex LodSuffix = new(@"_(?:m\d+|l\d+|lod\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The library's grouping key for a mesh or template name: ".sm" and one trailing _mN/_lN/_lodN
    /// dropped. "o_speakers_m1.sm" -> "o_speakers".</summary>
    public static string DisplayName(string meshOrTemplate)
    {
        var s = meshOrTemplate.EndsWith(".sm", StringComparison.OrdinalIgnoreCase) ? meshOrTemplate[..^3] : meshOrTemplate;
        return LodSuffix.Replace(s, "");
    }

    /// <summary>The mesh files a library entry was listed from: every mesh whose display name is this entry.</summary>
    public static IEnumerable<string> MeshesListedAs(string libraryName, IEnumerable<string> meshBaseNames)
        => meshBaseNames.Where(m => DisplayName(m).Equals(libraryName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Every name a library entry could be declared as, best first: the entry itself (a template already, or a
    /// vehicle listed by its folder), then "_M1" on the end - the retail convention, a map places the _M1 object - and
    /// last the mesh files it was listed from, ".sm" dropped. "_M1" is only tried on a name with no LOD suffix of its
    /// own, so nothing ever asks for "x_M1_M1". Lazy, so a caller that stops at the first hit never walks the mesh list.
    /// </summary>
    public static IEnumerable<string> Candidates(string libraryName, IEnumerable<string>? meshBaseNames = null)
    {
        if (string.IsNullOrWhiteSpace(libraryName)) yield break;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { libraryName };
        yield return libraryName;
        if (DisplayName(libraryName).Equals(libraryName, StringComparison.OrdinalIgnoreCase) && seen.Add(libraryName + "_M1"))
            yield return libraryName + "_M1";
        if (meshBaseNames is null) yield break;
        foreach (var m in meshBaseNames)
        {
            var b = m.EndsWith(".sm", StringComparison.OrdinalIgnoreCase) ? m[..^3] : m;
            if (b.Length > 0 && seen.Add(b)) yield return b;
        }
    }

    /// <summary>The template to write, spelled the way its .con declares it, or null when nothing resolves - the
    /// caller refuses the placement rather than write a name the game does not know.</summary>
    /// <param name="declaredName">The registry lookup: a template's declared spelling, or null when nothing declares
    /// it. Must be an exact (case-insensitive) template lookup, never a mesh lookup.</param>
    /// <param name="templatesDrawing">Last resort: the templates whose <c>geometry</c> draws a mesh listed under this
    /// name. Some objects are named nothing like their mesh - retail's O_USAmmo_M1.sm is drawn by <c>USAmmobox</c> - and
    /// no suffix rule finds those. Used only when exactly ONE declared template draws it: a soldier's 1P arms are drawn
    /// by every kit, and picking one of them would be a guess.</param>
    public static string? Resolve(string libraryName, Func<string, string?> declaredName, IEnumerable<string>? meshBaseNames = null,
                                  Func<string, IEnumerable<string>>? templatesDrawing = null)
    {
        foreach (var c in Candidates(libraryName, meshBaseNames))
            if (declaredName(c) is { Length: > 0 } hit) return hit;
        if (templatesDrawing is null || string.IsNullOrWhiteSpace(libraryName)) return null;
        var drawn = templatesDrawing(libraryName).Select(declaredName).OfType<string>().Where(n => n.Length > 0)
                                                 .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
        return drawn.Count == 1 ? drawn[0] : null;
    }
}
