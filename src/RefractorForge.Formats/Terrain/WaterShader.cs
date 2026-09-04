using System.Globalization;
using System.Text.RegularExpressions;
using RefractorForge.Formats.Geometry;

namespace RefractorForge.Formats.Terrain;

/// <summary>How BfVietnam's water looks: the material properties of one subshader of <c>standardMesh/levelWater.rs</c>.</summary>
/// <param name="Reflectivity">How much of the sky cubemap the surface mirrors. Retail: 0.18 (Fall of Saigon) to 0.6
/// (Saigon68's canal); the base archive ships 0.20.</param>
/// <param name="Opacity">Surface opacity. Base 0.35; the jungle rivers ship 0.5-0.75.</param>
/// <param name="Diffuse">The material tint (<c>materialDiffuse</c>). Base is a murky .281/.266/.205; retail levels
/// mostly reset it to white and let <c>water.color</c> do the colouring.</param>
/// <param name="ScrollSpeed">The third component of <c>uvSpeed</c>: how fast the ripple sequence drifts.</param>
/// <param name="WaterScale">Ripple tiling (<c>waterScale</c>).</param>
/// <param name="Cubemap">The sky the water mirrors. There are two of these in the game and their names are one
/// letter apart: the base archive's own levelWater.rs says <c>texture/default_env.rcm</c>, but EVERY retail level
/// overrides it with <c>texture/env_default.rcm</c> - a different cubemap with different faces. Starting a level's
/// override from the base file therefore reflected the wrong sky, which only showed once reflectivity was raised.</param>
public sealed record WaterShaderSettings(float Reflectivity, float Opacity, Vec3 Diffuse, float ScrollSpeed, float WaterScale,
                                         string Cubemap = WaterShader.LevelCubemap)
{
    /// <summary>The base archive's own values (subshader <c>WaterSetting</c>).</summary>
    public static readonly WaterShaderSettings RetailDefault = new(0.20f, 0.35f, new Vec3(0.281f, 0.266f, 0.205f), 1f, 25f);

    /// <summary>Saigon68's <c>WaterSettingBelowTerrain</c>, the only shipped example: a still, barely reflective,
    /// nearly opaque body for a flooded sewer.</summary>
    public static readonly WaterShaderSettings BelowTerrainDefault = new(0.10f, 0.85f, new Vec3(1f, 1f, 1f), 0.05f, 30f);
}

/// <summary>
/// The look of BfVietnam's water lives in a shader file, not in Init.con: <c>standardMesh/levelWater.rs</c>. A level
/// overrides it by shipping its own copy under <c>StandardMesh/levelWater.rs</c> - most retail levels do.
///
/// The file holds one subshader per water body. <c>WaterSetting</c> is the surface, and a level whose terrain sets
/// <c>GeometryTemplate.drawWaterBelowTerrain 1</c> MUST also supply <c>WaterSettingBelowTerrain</c> for the second
/// body: the base archive has no such subshader, and the engine asserts and dies on level load without it
/// (<c>WaterPatch.cpp: Can't load or use: StandardMesh/levelWater/WaterSettingBelowTerrain.rs</c>). Saigon68 is the
/// one retail level that ships the pair.
///
/// This reads values out of the text and writes them back in place, so an override keeps every line its author left.
/// </summary>
public static class WaterShader
{
    /// <summary>What the base archive's own levelWater.rs reflects. No level uses it; see WaterShaderSettings.</summary>
    public const string BaseCubemap = "texture/default_env.rcm";
    /// <summary>What every retail level's levelWater.rs reflects.</summary>
    public const string LevelCubemap = "texture/env_default.rcm";

    public const string SurfaceSubshader = "WaterSetting";
    public const string BelowTerrainSubshader = "WaterSettingBelowTerrain";

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
    private static readonly Regex Cube = new(@"(?im)^(\s*cubemap\s+"")([^""]*)(""\s*;)", RegexOptions.Compiled);
    private static readonly Regex Scale = new(@"(?im)^(\s*waterScale\s+)([-+\d.]+)(\s*;)", RegexOptions.Compiled);

    /// <summary>The surface water's settings.</summary>
    public static WaterShaderSettings Parse(string? rsText) => Parse(rsText, SurfaceSubshader);

    /// <summary>One named subshader's settings; the matching defaults when the file has no such block.</summary>
    public static WaterShaderSettings Parse(string? rsText, string subshader)
    {
        var d = subshader.Equals(BelowTerrainSubshader, StringComparison.OrdinalIgnoreCase)
            ? WaterShaderSettings.BelowTerrainDefault : WaterShaderSettings.RetailDefault;
        var block = string.IsNullOrEmpty(rsText) ? null : BlockOf(rsText!, subshader);
        if (block is null) return d;
        string t = rsText![block.Value.Start..block.Value.End];
        float refl = d.Reflectivity, op = d.Opacity, sp = d.ScrollSpeed, sc = d.WaterScale;
        var diff = d.Diffuse;
        if (Reflect.Match(t) is { Success: true } m1) refl = P(m1.Groups[2].Value, refl);
        if (Opac.Match(t) is { Success: true } m2) op = P(m2.Groups[2].Value, op);
        if (Diff.Match(t) is { Success: true } m3) diff = new Vec3(P(m3.Groups[2].Value, diff.X), P(m3.Groups[3].Value, diff.Y), P(m3.Groups[4].Value, diff.Z));
        if (Speed.Match(t) is { Success: true } m4) sp = P(m4.Groups[4].Value, sp);
        if (Scale.Match(t) is { Success: true } m5) sc = P(m5.Groups[2].Value, sc);
        var cube = Cube.Match(t) is { Success: true } m6 ? m6.Groups[2].Value.Trim() : d.Cubemap;
        // The base archive's value is not a choice any level makes - all of them override it - so a level carrying it
        // inherited it, either from the base file or from a version of this editor that began the level's override by
        // copying it. Either way the sky it mirrors is the wrong one, so hand back the one the game's own levels use.
        if (cube.Equals(BaseCubemap, StringComparison.OrdinalIgnoreCase)) cube = LevelCubemap;
        return new WaterShaderSettings(refl, op, diff, sp, sc, cube);
    }

    /// <summary>
    /// The text with the surface subshader's five values written in place. Pass <paramref name="below"/> to also
    /// supply the below-terrain body a tunnel map needs - its block is patched if present and APPENDED if not, which
    /// is what keeps a <c>drawWaterBelowTerrain 1</c> level from asserting on load. Passing null leaves any existing
    /// below-terrain block untouched (it is never deleted: a level that has one may still want it).
    /// </summary>
    public static string Patch(string? rsText, WaterShaderSettings s, WaterShaderSettings? below = null)
    {
        string t = string.IsNullOrWhiteSpace(rsText) ? RetailText : rsText!;
        t = PatchBlock(t, SurfaceSubshader, s, addIfMissing: true);
        if (below is not null) t = PatchBlock(t, BelowTerrainSubshader, below, addIfMissing: true);
        return t;
    }

    /// <summary>The subshader's <c>sequence</c> - the stem of BfVietnam's animated water NORMAL maps
    /// (<c>texture/Waterseq/test</c> -> <c>test0000.dds</c>...). This is what makes the game's water ripple and
    /// catch the sky; a level that ships no override uses the base file's. Null when the block has no sequence.</summary>
    public static string? SequenceOf(string? rsText, string subshader = SurfaceSubshader)
    {
        string text = string.IsNullOrWhiteSpace(rsText) ? RetailText : rsText!;
        var span = BlockOf(text, subshader);
        if (span is null) return null;
        var m = Regex.Match(text[span.Value.Start..span.Value.End], @"(?im)^\s*sequence\s+""([^""]+)""\s*;");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>True when the text declares the second water body's subshader.</summary>
    public static bool HasBelowTerrain(string? rsText) => !string.IsNullOrEmpty(rsText) && BlockOf(rsText!, BelowTerrainSubshader) is not null;

    private static string PatchBlock(string text, string subshader, WaterShaderSettings s, bool addIfMissing)
    {
        var span = BlockOf(text, subshader);
        if (span is null)
        {
            if (!addIfMissing) return text;
            string nl = text.Contains("\r\n") ? "\r\n" : "\n";
            return text.TrimEnd('\r', '\n') + nl + nl + NewBlock(subshader, s, nl);
        }
        string b = text[span.Value.Start..span.Value.End];
        b = Set(b, Reflect, m => m.Groups[1].Value + F(s.Reflectivity) + m.Groups[3].Value, "\treflectivity " + F(s.Reflectivity) + ";");
        b = Set(b, Opac, m => m.Groups[1].Value + F(s.Opacity) + m.Groups[3].Value, "\topacity " + F(s.Opacity) + ";");
        b = Set(b, Diff, m => m.Groups[1].Value + F(s.Diffuse.X) + " " + F(s.Diffuse.Y) + " " + F(s.Diffuse.Z) + m.Groups[5].Value,
                "\tmaterialDiffuse " + F(s.Diffuse.X) + " " + F(s.Diffuse.Y) + " " + F(s.Diffuse.Z) + ";");
        b = Set(b, Speed, m => m.Groups[1].Value + m.Groups[2].Value + " " + m.Groups[3].Value + " " + F(s.ScrollSpeed) + m.Groups[5].Value,
                "\tuvSpeed 0 0 " + F(s.ScrollSpeed) + ";");
        b = Set(b, Scale, m => m.Groups[1].Value + F(s.WaterScale) + m.Groups[3].Value, "\twaterScale " + F(s.WaterScale) + ";");
        b = Set(b, Cube, m => m.Groups[1].Value + s.Cubemap + m.Groups[3].Value, "\tcubemap \"" + s.Cubemap + "\";");
        return text[..span.Value.Start] + b + text[span.Value.End..];
    }

    /// <summary>A whole subshader block, from its <c>subshader "name"</c> keyword to its closing brace. Matched on the
    /// QUOTED name, so <c>WaterSetting</c> never picks up <c>WaterSettingBelowTerrain</c>.</summary>
    private static (int Start, int End)? BlockOf(string text, string subshader)
    {
        var m = Regex.Match(text, @"(?im)^[ \t]*subshader\s+""" + Regex.Escape(subshader) + @"""");
        if (!m.Success) return null;
        int open = text.IndexOf('{', m.Index);
        if (open < 0) return null;
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return (m.Index, i + 1);
        }
        return null;
    }

    // Modelled on Saigon68's pair: the same sequence/cubemap lines, the caller's five values.
    private static string NewBlock(string subshader, WaterShaderSettings s, string nl) =>
        $"subshader \"{subshader}\" \"StandardMesh/Default\"{nl}{{{nl}" +
        $"\tsequence \"texture/Waterseq/test\";{nl}\tcubemap \"{s.Cubemap}\";{nl}" +
        $"\tsequenceCycleTime 2;{nl}\tsequenceFrameCnt 30;{nl}\topacity {F(s.Opacity)};{nl}" +
        $"\tmaterialDiffuse {F(s.Diffuse.X)} {F(s.Diffuse.Y)} {F(s.Diffuse.Z)};{nl}\treflectivity {F(s.Reflectivity)};{nl}" +
        $"\tuvSpeed 0 0 {F(s.ScrollSpeed)};{nl}\twaterScale {F(s.WaterScale)};{nl}\twaterFade 0;{nl}}}{nl}";

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
