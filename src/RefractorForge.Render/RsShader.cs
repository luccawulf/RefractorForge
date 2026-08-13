using System.Globalization;
using System.Numerics;

namespace RefractorForge.Render;

/// <summary>
/// Parses Refractor <c>.rs</c> "render shader" files, which bind each StandardMesh material to a
/// texture, diffuse/specular colors and (optionally) a normal map. One <c>.rs</c> sits next to each
/// <c>.sm</c> in the archives. We use the bindings to color materials correctly per-surface; when the
/// game's texture archive is available, the same <see cref="MaterialShader.Texture"/> names feed real
/// texture sampling.
/// </summary>
public sealed class RsShaderSet
{
    /// <summary><paramref name="AlphaTestRef"/> is the engine's cutoff for a material that CUTS OUT rather than
    /// blends. Refractor overloads <c>transparent true</c>: on its own it means real alpha blending (glass, canopies,
    /// gunsights), but paired with an <c>alphaTestRef</c> it means an alpha TEST at that threshold - grilles, decals,
    /// ropes, painted markings. Across the stock BF1942 shaders that split is 178 blends vs 59 cutouts, and getting
    /// it wrong is what made the Willys' engine grill see-through: its sheet is 71% low-alpha, so blending it left
    /// almost nothing on screen where the engine punches a clean grille out of it.</summary>
    public sealed record MaterialShader(string Name, string? Texture, Vector3 Diffuse, bool TextureFade,
                                        bool Transparent = false, float? AlphaTestRef = null);

    private readonly Dictionary<string, MaterialShader> _byName = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, MaterialShader> Materials => _byName;

    public static RsShaderSet Parse(string text)
    {
        var set = new RsShaderSet();
        string? curName = null, curTex = null;
        Vector3 diffuse = Vector3.One; bool fade = false; bool transp = false; bool inBlock = false;
        float? alphaRef = null;

        foreach (var rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim().TrimEnd('\r');
            if (line.StartsWith("subshader", StringComparison.OrdinalIgnoreCase))
            {
                // subshader "Name" "StandardMesh/Default"
                var q = SplitQuoted(line);
                curName = q.Count > 0 ? q[0] : null;
                curTex = null; diffuse = Vector3.One; fade = false; transp = false; alphaRef = null;
            }
            else if (line.StartsWith("{")) inBlock = true;
            else if (line.StartsWith("}"))
            {
                if (curName is not null)
                    set._byName[curName] = new MaterialShader(curName, curTex, diffuse, fade, transp, alphaRef);
                inBlock = false; curName = null;
            }
            else if (inBlock && curName is not null)
            {
                if (line.StartsWith("texture", StringComparison.OrdinalIgnoreCase))
                {
                    var q = SplitQuoted(line);
                    if (q.Count > 0) curTex = q[0];
                }
                else if (line.StartsWith("materialDiffuse", StringComparison.OrdinalIgnoreCase))
                {
                    var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length >= 4 &&
                        float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var r) &&
                        float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var g) &&
                        float.TryParse(p[3].TrimEnd(';'), NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
                        diffuse = new Vector3(r, g, b);
                }
                else if (line.StartsWith("textureFade", StringComparison.OrdinalIgnoreCase))
                    fade = line.Contains("true", StringComparison.OrdinalIgnoreCase);
                else if (line.StartsWith("transparent", StringComparison.OrdinalIgnoreCase))
                    transp = line.Contains("true", StringComparison.OrdinalIgnoreCase);   // BF1942 glass/canopy: alpha-blended material
                else if (line.StartsWith("alphaTestRef", StringComparison.OrdinalIgnoreCase))
                {
                    // "alphaTestRef 0.7" - texels below this alpha are DISCARDED outright. Its presence is what
                    // distinguishes a cutout from a blend, so record the value even when it is malformed-but-present.
                    var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    alphaRef = p.Length >= 2 && float.TryParse(p[1].TrimEnd(';'), NumberStyles.Float, CultureInfo.InvariantCulture, out var ar)
                        ? Math.Clamp(ar, 0f, 1f) : 0.5f;
                }
            }
        }
        return set;
    }

    private static List<string> SplitQuoted(string s)
    {
        var outp = new List<string>(); int i = 0;
        while (i < s.Length)
        {
            int a = s.IndexOf('"', i); if (a < 0) break;
            int b = s.IndexOf('"', a + 1); if (b < 0) break;
            outp.Add(s.Substring(a + 1, b - a - 1)); i = b + 1;
        }
        return outp;
    }

    /// <summary>A representative surface color for a material, derived from its shader. Uses the texture
    /// name (the most descriptive signal — e.g. <c>O_fishinghut_bamboo</c>, <c>palmLeaf</c>,
    /// <c>concrete</c>) tinted by the authored diffuse. This is a stand-in until the real texture bitmaps
    /// are available; the binding itself is exact.</summary>
    public static Vector3 ColorFor(MaterialShader? m)
    {
        if (m is null) return new Vector3(0.72f, 0.68f, 0.60f);
        string t = (m.Texture ?? "").ToLowerInvariant();
        Vector3 baseColor;
        if (m.TextureFade || t.Contains("portal") || t.Contains("black"))
            baseColor = new Vector3(0.20f, 0.22f, 0.24f);                 // alpha/cutout/glow billboards
        else if (Has(t, "leaf", "palm", "jungle", "tree", "bush", "grass", "fern", "vine", "plant", "foliage", "ivy", "branch"))
            baseColor = new Vector3(0.27f, 0.45f, 0.24f);                 // foliage green
        else if (Has(t, "bamboo", "wood", "straw", "thatch", "log", "plank", "timber"))
            baseColor = new Vector3(0.66f, 0.52f, 0.34f);                 // wood/bamboo tan
        else if (Has(t, "roof", "tile", "clay", "shingle"))
            baseColor = new Vector3(0.50f, 0.33f, 0.27f);                 // roof terracotta
        else if (Has(t, "metal", "steel", "iron", "tin", "chrome", "gun", "barrel"))
            baseColor = new Vector3(0.48f, 0.50f, 0.53f);                 // metal grey
        else if (Has(t, "concrete", "cement", "stone", "rock", "plaster", "wall", "brick", "sand"))
            baseColor = new Vector3(0.66f, 0.63f, 0.56f);                 // masonry/sand
        else if (Has(t, "cloth", "canvas", "tarp", "sandbag", "bag", "tent", "fabric"))
            baseColor = new Vector3(0.60f, 0.56f, 0.40f);                 // canvas khaki
        else if (Has(t, "water"))
            baseColor = new Vector3(0.30f, 0.44f, 0.52f);
        else if (Has(t, "dirt", "mud", "ground", "earth"))
            baseColor = new Vector3(0.48f, 0.40f, 0.30f);
        else
            baseColor = new Vector3(0.70f, 0.66f, 0.58f);                 // default light stone
        // tint by authored diffuse (kept gentle so near-white diffuse leaves the base intact)
        var d = Vector3.Lerp(Vector3.One, m.Diffuse, 0.5f);
        return baseColor * d;
    }

    private static bool Has(string s, params string[] keys)
    {
        foreach (var k in keys) if (s.Contains(k)) return true;
        return false;
    }

    /// <summary>Write a <c>.rs</c> shader set from material bindings — the inverse of <see cref="Parse"/>. Each
    /// material becomes a subshader block binding its texture (basename, no extension) + diffuse colour, so an
    /// exported standard mesh is textured in-game. Round-trips through <see cref="Parse"/>.</summary>
    public static string Write(IEnumerable<(string Material, string? Texture, Vector3 Diffuse)> materials)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (mat, tex, dif) in materials)
        {
            sb.Append("subshader \"").Append(mat).Append("\" \"StandardMesh/Default\"\r\n{\r\n");
            if (!string.IsNullOrEmpty(tex)) sb.Append("\ttexture \"").Append(tex).Append("\"\r\n");
            sb.Append("\tmaterialDiffuse ").Append(Fmt(dif.X)).Append(' ').Append(Fmt(dif.Y)).Append(' ').Append(Fmt(dif.Z)).Append("\r\n");
            sb.Append("}\r\n\r\n");
        }
        return sb.ToString();
    }

    private static string Fmt(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}
