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
    /// <param name="SelfIllum">Brightness the surface carries on its own, before any light reaches it. This is the
    /// state that makes a material immune to a dark level: 1 1 1 draws the texture at full strength at midnight.</param>
    /// <param name="BlendSrc">/<param name="BlendDest">How the surface combines with what is already drawn.
    /// <c>sourcealpha</c> + <c>one</c> is ADDITIVE — the texture only ever brightens the pixels behind it, which is
    /// how the engine draws a muzzle flash, and the only way to put a pool of light on a surface in a game with no
    /// dynamic lights. Leave both null for ordinary alpha blending.</param></param>
    public sealed record Material(string Name, string? Texture, Vec3 Diffuse,
                                  bool Transparent = false, float? AlphaTestRef = null,
                                  bool TwoSided = false, bool Lighting = true,
                                  bool LightingSpecular = false, bool DepthWrite = true,
                                  Vec3? SelfIllum = null, Vec3? Specular = null, float? Opacity = null,
                                  bool SortedBlend = false, string? BlendSrc = null, string? BlendDest = null);

    /// <summary>
    /// The retail ADDITIVE-GLOW material, copied statement for statement from <c>e_MuzzAK47_m1.rs</c> (41 shipped
    /// materials use exactly these states). Unlit, self-illuminated and additively blended, so it brightens whatever
    /// it is laid over and never darkens with the level — which is what makes it usable as a pool of lamplight.
    /// The COLOUR lives in the texture, not here: <c>materialDiffuse</c> stays white so the picture comes through
    /// unchanged.
    /// </summary>
    public static Material Glow(string name, string? texture, bool twoSided = true) =>
        new(name, texture, new Vec3(1, 1, 1),
            Transparent: true, AlphaTestRef: 0f, TwoSided: twoSided, Lighting: false,
            LightingSpecular: false, DepthWrite: false,
            SelfIllum: new Vec3(1, 1, 1), Specular: new Vec3(0, 0, 0), Opacity: 1f,
            SortedBlend: true, BlendSrc: "sourcealpha", BlendDest: "one");

    /// <param name="textureFolder">Prepended to any texture name carrying no '/' of its own. Level-local objects
    /// use <c>texture</c>, which is where <c>textureManager.alternativePath</c> points them.</param>
    public static string Write(IEnumerable<Material> materials, string textureFolder = "texture")
    {
        var sb = new StringBuilder();
        foreach (var m in materials)
        {
            sb.Append("subshader \"").Append(m.Name).Append("\" \"StandardMesh/Default\"\r\n{\r\n");
            // Statement order follows a real shipped shader rather than our own taste, so a generated file diffs
            // cleanly against retail and nothing depends on us having guessed the parser's tolerance right.
            Stmt(sb, "lighting", m.Lighting ? "true" : "false");
            Stmt(sb, "lightingSpecular", m.LightingSpecular ? "true" : "false");
            if (m.Specular is { } sp) Stmt(sb, "materialSpecular", $"{Fmt(sp.X)} {Fmt(sp.Y)} {Fmt(sp.Z)}");
            Stmt(sb, "materialDiffuse", $"{Fmt(m.Diffuse.X)} {Fmt(m.Diffuse.Y)} {Fmt(m.Diffuse.Z)}");
            if (m.SelfIllum is { } si) Stmt(sb, "selfillum", $"{Fmt(si.X)} {Fmt(si.Y)} {Fmt(si.Z)}");
            if (m.Opacity is { } op) Stmt(sb, "opacity", Fmt(op));
            Stmt(sb, "transparent", m.Transparent ? "true" : "false");
            if (m.SortedBlend) Stmt(sb, "sortedBlend", "true");
            if (!string.IsNullOrWhiteSpace(m.BlendSrc)) Stmt(sb, "blendSrc", m.BlendSrc!);
            if (!string.IsNullOrWhiteSpace(m.BlendDest)) Stmt(sb, "blendDest", m.BlendDest!);
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

    /// <summary>
    /// Lift a shader out of the scene's shading so a picture stays readable where the sun does not reach.
    ///
    /// The engine shades a standard mesh as <c>saturate(2*(prelight*N.L + ambient)) * texture</c>, so a decal in a
    /// tunnel, an alley or any north-facing wall is multiplied down to its ambient - a sign you cannot read. The
    /// engine's own answer is <c>selfillum</c>, which adds a floor to that lighting term; at 1 the surface renders
    /// at its texture's own brightness everywhere, which is what a sign, a poster or a map board wants.
    ///
    /// This EDITS an existing <c>.rs</c> as text rather than regenerating it, so it works on shaders the editor did
    /// not write (an imported model's, a decal from an older build) and preserves every other state in the file.
    /// A statement already present is replaced in place; a missing one is inserted after <c>materialDiffuse</c>,
    /// which is where a shipped shader carries it.
    /// </summary>
    /// <param name="level">0 = leave the shading alone (any existing selfillum is removed), 1 = fully self-lit.</param>
    /// <param name="unlitAtFull">At level 1, also turn <c>lighting</c> off - the strongest form, and what retail
    /// uses for a surface that must never be shaded at all.</param>
    public static string Brighten(string rs, float level, bool unlitAtFull = false)
    {
        level = Math.Clamp(level, 0f, 1f);
        var lines = rs.Replace("\r\n", "\n").Split('\n').ToList();

        // Drop whatever selfillum is there now; we are about to state it (or deliberately not to).
        lines.RemoveAll(l => l.TrimStart().StartsWith("selfillum", StringComparison.OrdinalIgnoreCase));

        if (level > 0f)
        {
            string stmt = null!;
            for (int i = 0; i < lines.Count; i++)
            {
                var t = lines[i].TrimStart();
                if (!t.StartsWith("materialDiffuse", StringComparison.OrdinalIgnoreCase)) continue;
                string indent = lines[i][..(lines[i].Length - t.Length)];
                stmt = $"{indent}selfillum {Fmt(level)} {Fmt(level)} {Fmt(level)};";
                lines.Insert(i + 1, stmt);
                i++;                                    // skip the line we just inserted
            }
            // A shader with no materialDiffuse at all: put it just inside each subshader block instead.
            if (stmt is null)
                for (int i = 0; i < lines.Count; i++)
                    if (lines[i].Trim() == "{")
                        lines.Insert(++i, $"\tselfillum {Fmt(level)} {Fmt(level)} {Fmt(level)};");
        }

        // `lighting` only moves at full brightness, and only when asked: turning it off on a normal decal would
        // flatten it everywhere, not just in shade.
        if (unlitAtFull && level >= 1f)
            for (int i = 0; i < lines.Count; i++)
            {
                var t = lines[i].TrimStart();
                if (t.StartsWith("lighting ", StringComparison.OrdinalIgnoreCase) && !t.StartsWith("lightingSpecular", StringComparison.OrdinalIgnoreCase))
                    lines[i] = lines[i][..(lines[i].Length - t.Length)] + "lighting false;";
            }

        return string.Join("\r\n", lines);
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
