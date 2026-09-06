using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RefractorForge.Formats.Geometry;

namespace RefractorForge.Formats.Mesh;

/// <summary>
/// Writes Refractor <c>.rs</c> "render shader" files — the file that binds each StandardMesh material to a
/// texture and its render states. One <c>.rs</c> sits beside each <c>.sm</c>.
///
/// The grammar the ENGINE accepts is stricter than the one our reader accepts, and the difference is the whole
/// reason this class exists:
///
///   * **Every statement ends in a semicolon.** Miss one and the parser throws, the subshader is never registered,
///     and the material falls back to untextured — silently, from the player's point of view.
///   * **The texture value is folder-qualified** (<c>texture "texture/foo"</c>). All 4,406 references across the
///     shipped shaders are; a bare name resolves at the archive root instead of the object's texture folder.
///
/// Both facts come from <see cref="Con.DecalObject"/>, whose hand-written shader string is the shape that is known
/// to load. This is that string, generalised — so there is one place that knows the grammar rather than two.
/// </summary>
public static class RsWriter
{
    /// <summary>One material binding to write.</summary>
    /// <param name="Texture">Texture reference: a bare base name (qualified with the write's texture folder), or a
    /// path of your own containing '/' — a mod folder, or a Bink movie — which is written verbatim.</param>
    /// <param name="AlphaTestRef">Set ONLY for a cut-out material — a grille, a foliage sheet, a painted marking —
    /// where texels below this alpha are discarded. Leave null for a solid surface: Refractor overloads
    /// <c>transparent</c>, and it is the presence of an alphaTestRef beside it that means "test, don't blend", so
    /// emitting one on an opaque material mislabels it for both the engine and our own viewer.</param>
    /// <param name="TwoSided">Draw back faces too. A flat sheet (sign, leaf card) needs it; a solid object does not,
    /// and turning it on there doubles the fill for nothing.</param>
    public sealed record Material(string Name, string? Texture, Vec3 Diffuse,
                                  bool Transparent = false, float? AlphaTestRef = null,
                                  bool TwoSided = false, bool Lighting = true,
                                  bool LightingSpecular = false, bool DepthWrite = true);

    /// <param name="textureFolder">Prepended to any texture name carrying no '/' of its own. Level-local objects
    /// use <c>texture</c>, which is where <c>textureManager.alternativePath</c> points them.</param>
    public static string Write(IEnumerable<Material> materials, string textureFolder = "texture")
    {
        var sb = new StringBuilder();
        foreach (var m in materials)
        {
            sb.Append("subshader \"").Append(m.Name).Append("\" \"StandardMesh/Default\"\r\n{\r\n");
            Stmt(sb, "lighting", m.Lighting ? "true" : "false");
            Stmt(sb, "lightingSpecular", m.LightingSpecular ? "true" : "false");
            Stmt(sb, "materialDiffuse", $"{Fmt(m.Diffuse.X)} {Fmt(m.Diffuse.Y)} {Fmt(m.Diffuse.Z)}");
            Stmt(sb, "transparent", m.Transparent ? "true" : "false");
            if (m.AlphaTestRef is { } ar) Stmt(sb, "alphaTestRef", Fmt(ar));
            // A blended surface that also writes depth hides whatever is drawn behind it afterwards — retail glass
            // always turns this off, so honour it rather than leaving the caller to remember.
            if (!m.DepthWrite) Stmt(sb, "depthWrite", "false");
            Stmt(sb, "twosided", m.TwoSided ? "true" : "false");
            if (!string.IsNullOrWhiteSpace(m.Texture))
                Stmt(sb, "texture", "\"" + QualifyTexture(m.Texture!, textureFolder) + "\"");
            sb.Append("}\r\n\r\n");
        }
        return sb.ToString();
    }

    private static void Stmt(StringBuilder sb, string name, string value) =>
        sb.Append('\t').Append(name).Append(' ').Append(value).Append(";\r\n");

    /// <summary>Folder-qualify a texture reference. One that already carries a path — including a Bink movie —
    /// is left exactly as written; a bare name loses any file extension (the engine appends its own) and gains
    /// the folder.</summary>
    public static string QualifyTexture(string texture, string folder = "texture")
    {
        var t = texture.Replace('\\', '/').Trim();
        if (t.Length == 0) return t;
        if (t.Contains('/')) return t;
        int dot = t.LastIndexOf('.');
        if (dot > 0 && t.Length - dot <= 5) t = t[..dot];
        folder = folder.Replace('\\', '/').Trim().Trim('/');
        return folder.Length == 0 ? t : folder + "/" + t;
    }

    private static string Fmt(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}
