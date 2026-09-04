using System.Globalization;
using System.Text.RegularExpressions;
using RefractorForge.Formats.Geometry;

namespace RefractorForge.Formats.Terrain;

/// <summary>How BfVietnam's water looks: the material properties of <c>standardMesh/levelWater.rs</c>.</summary>
/// <param name="Reflectivity">How much of the sky cubemap the surface mirrors. Retail: 0.18 (Fall of Saigon) to 0.3
/// (Ho Chi Minh Trail); the base archive ships 0.20.</param>
/// <param name="Opacity">Surface opacity. Base 0.35; the jungle rivers ship 0.5-0.75.</param>
/// <param name="Diffuse">The material tint (<c>materialDiffuse</c>). Base is a murky .281/.266/.205; retail levels
/// mostly reset it to white and let <c>water.color</c> do the colouring.</param>
/// <param name="ScrollSpeed">The third component of <c>uvSpeed</c>: how fast the ripple sequence drifts.</param>
/// <param name="WaterScale">Ripple tiling (<c>waterScale</c>).</param>
public sealed record WaterShaderSettings(float Reflectivity, float Opacity, Vec3 Diffuse, float ScrollSpeed, float WaterScale)
{
    public static readonly WaterShaderSettings RetailDefault = new(0.20f, 0.35f, new Vec3(0.281f, 0.266f, 0.205f), 1f, 25f);
}

/// <summary>
/// The knob for how much BfVietnam's water reflects the sky lives in a shader file, not in Init.con:
/// <c>standardMesh/levelWater.rs</c>, subshader <c>WaterSetting</c>. A level overrides it by shipping its own copy
/// under <c>StandardMesh/levelWater.rs</c> - Fall of Saigon, Defense of Con Thien and Ho Chi Minh Trail all do,
/// each with a different <c>reflectivity</c> and <c>opacity</c>. This reads those values out of the text and writes
/// them back in place, so an override keeps every line the level's author left in it.
/// </summary>
public static class WaterShader
{
    /// <summary>The base archive's <c>levelWater.rs</c>, byte for byte, for levels that ship none.</summary>
    public const string RetailText =
        "subshader \"WaterSetting\" \"StandardMesh/Default\"\r\n{\r\n" +
        "\tsequence \"texture/Waterseq/test\";\r\n\tcubemap \"texture/default_env.rcm\";\r\n" +
        "\tsequenceCycleTime 2;\r\n\tsequenceFrameCnt 30;\r\n\topacity 0.35;\r\n" +
        "\tmaterialDiffuse .281 .266 .205;\r\n\treflectivity 0.20;\r\n\tuvSpeed 0 0 1;\r\n" +
        "\twaterScale 25;\r\n\twaterFade 0;\r\n}\r\n";

    private static readonly Regex Reflect = new(@"(?im)^(\s*reflectivity\s+)([-+\d.]+)(\s*;)", RegexOptions.Compiled);
    private static readonly Regex Opac = new(@"(?im)^(\s*opacity\s+)([-+\d.]+)(\s*;)", RegexOptions.Compiled);
    private static readonly Regex Diff = new(@"(?im)^(\s*materialDiffuse\s+)([-+\d.]+)\s+([-+\d.]+)\s+([-+\d.]+)(\s*;)", RegexOptions.Compiled);
    private static readonly Regex Speed = new(@"(?im)^(\s*uvSpeed\s+)([-+\d.]+)\s+([-+\d.]+)\s+([-+\d.]+)(\s*;)", RegexOptions.Compiled);
    private static readonly Regex Scale = new(@"(?im)^(\s*waterScale\s+)([-+\d.]+)(\s*;)", RegexOptions.Compiled);

    public static WaterShaderSettings Parse(string? rsText)
    {
        var d = WaterShaderSettings.RetailDefault;
        if (string.IsNullOrEmpty(rsText)) return d;
        float refl = d.Reflectivity, op = d.Opacity, sp = d.ScrollSpeed, sc = d.WaterScale;
        var diff = d.Diffuse;
        if (Reflect.Match(rsText) is { Success: true } m1) refl = P(m1.Groups[2].Value, refl);
        if (Opac.Match(rsText) is { Success: true } m2) op = P(m2.Groups[2].Value, op);
        if (Diff.Match(rsText) is { Success: true } m3) diff = new Vec3(P(m3.Groups[2].Value, diff.X), P(m3.Groups[3].Value, diff.Y), P(m3.Groups[4].Value, diff.Z));
        if (Speed.Match(rsText) is { Success: true } m4) sp = P(m4.Groups[4].Value, sp);
        if (Scale.Match(rsText) is { Success: true } m5) sc = P(m5.Groups[2].Value, sc);
        return new WaterShaderSettings(refl, op, diff, sp, sc);
    }

    /// <summary>The text with the five values written in place. A property the text lacks is added before the
    /// closing brace, so a hand-trimmed override still comes out complete.</summary>
    public static string Patch(string? rsText, WaterShaderSettings s)
    {
        string t = string.IsNullOrWhiteSpace(rsText) ? RetailText : rsText!;
        t = Set(t, Reflect, m => m.Groups[1].Value + F(s.Reflectivity) + m.Groups[3].Value, "\treflectivity " + F(s.Reflectivity) + ";");
        t = Set(t, Opac, m => m.Groups[1].Value + F(s.Opacity) + m.Groups[3].Value, "\topacity " + F(s.Opacity) + ";");
        t = Set(t, Diff, m => m.Groups[1].Value + F(s.Diffuse.X) + " " + F(s.Diffuse.Y) + " " + F(s.Diffuse.Z) + m.Groups[5].Value,
                "\tmaterialDiffuse " + F(s.Diffuse.X) + " " + F(s.Diffuse.Y) + " " + F(s.Diffuse.Z) + ";");
        t = Set(t, Speed, m => m.Groups[1].Value + m.Groups[2].Value + " " + m.Groups[3].Value + " " + F(s.ScrollSpeed) + m.Groups[5].Value,
                "\tuvSpeed 0 0 " + F(s.ScrollSpeed) + ";");
        t = Set(t, Scale, m => m.Groups[1].Value + F(s.WaterScale) + m.Groups[3].Value, "\twaterScale " + F(s.WaterScale) + ";");
        return t;
    }

    private static string Set(string text, Regex rx, MatchEvaluator ev, string addLine)
    {
        if (rx.IsMatch(text)) return rx.Replace(text, ev, 1);
        int close = text.LastIndexOf('}');
        if (close < 0) return text + addLine + "\r\n";
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        // Keep the brace on its own line whatever precedes it.
        string head = text[..close].TrimEnd('\r', '\n', ' ', '\t');
        return head + nl + addLine + nl + text[close..];
    }

    private static float P(string s, float fallback) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
