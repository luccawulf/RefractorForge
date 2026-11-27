using System;
using System.Numerics;

namespace RefractorForge.Render;

/// <summary>
/// The light Battlefield Vietnam's object shaders are actually fed, as plain functions - the one place the editor's
/// viewport, its bakes and its tests agree on what the game will draw.
///
/// <para><b>Where it comes from.</b> The shaders are <c>effects/RaShaderPPLSTs1DifLmp.fx</c> (lightmapped) and
/// <c>RaShaderPPLSTs1Dif.fx</c> (not), both ending in <c>saturate(2 * (...)) * texture</c>. What is uploaded into them
/// was read off <c>BfVietnam.exe</c> (the block at 0xa4b5a0 and the two ambient uploads at 0xa6d2f0 / 0xa6dc90):</para>
/// <list type="bullet">
/// <item><c>LightMaterialColor</c> = sun colour x material diffuse x <b>0.5</b>;</item>
/// <item><c>secondaryLightColor</c> = <c>renderer.SecondaryDiffuseColor</c> x material diffuse x <b>0.5</b>;</item>
/// <item><c>LightAmbient</c> = (material ambient x renderer ambient + material self-illumination) x <b>0.5</b>, where
/// the renderer ambient is <c>LMambientColor</c> for a lightmapped surface and <c>ambientColor</c> otherwise;</item>
/// <item><c>invXformLightDir2</c>, the "BackLight", is the sun's direction with x and z negated and y kept.</item>
/// </list>
/// <para>So the halves cancel the shader's doubling, and what multiplies the texture is</para>
/// <code>saturate( P * matDiffuse * (sun * sat(N.L) + back * sat(N.B)) + matAmbient * ambient + selfIllum )</code>
/// <para>with <c>P</c> the lightmap's blue channel (1 where there is no lightmap shader) and <c>B</c> the sun mirrored
/// to the opposite compass bearing at the same height. Reading the shader alone, without the upload, gives a formula
/// twice as bright - one that would clip every retail wall - and no back light direction at all.</para>
///
/// <para><b>Measured.</b> The calibration level (<see cref="LightCalibration"/>, 2026-11-27) put that formula in front
/// of the game: on a tangent-frame mesh with a 24-bit lightmap all 49 patches came out within 0.3 of 255 of it, the
/// back light 0.3 degrees from the mirrored sun, a sun colour of 1.25 carried through to the final clamp, and the
/// material's diffuse scaling the two lights but not the ambient. Two things it added:</para>
/// <list type="bullet">
/// <item>a mesh WITHOUT a tangent frame (32- or 40-byte vertices: every imported model, every mesh widened to hold a
/// lightmap) is drawn fixed-function, and there the two lights are summed per vertex and <b>clamped to 1 before</b> the
/// lightmap multiplies them, and the lightmap multiplies them <b>per channel</b> - a coloured lightmap colours the
/// light on those meshes (<see cref="Material.VertexLit"/>);</item>
/// <item>an object with NO lightmap is lit exactly as <see cref="Dynamic(Vector3, Vector3, in Colours, in Material)"/>
/// says where the terrain shadow map (<c>LightmapShadowBits.lsb</c>) is clear. Where it says shadow, the two lights -
/// not the ambient - are scaled by <see cref="LightShare"/>: the engine samples the terrain round the object, and in
/// shadow a sample is the brightness of the ground TEXTURE there (<see cref="TerrainSample"/>). Statics take five
/// samples once (<see cref="StaticSamplePoints"/>, <see cref="StaticTerrain"/>), moving things four per frame. Read
/// from the exe (0x673570, 0x6722a0, 0x691860, 0x7243a0, 0x74a050); the static rule fits the fourteen boards
/// measured to 0.005. Lightmapped objects never get
/// there: the routine returns at once for a mesh that has a lightmap.</item>
/// </list>
///
/// <para><b>What follows from it.</b> Every wall is reached by the sun or by the back light, so a lightmap can shade
/// every wall; only a face that looks DOWN (a ceiling, the underside of an awning) has neither and shows the ambient
/// alone, whatever its lightmap says. And the lightmap is one number per texel: it scales the two lights but cannot
/// change their colour, so the colour of shade is the ambient's colour, for the whole level at once.</para>
/// </summary>
public static class EngineLight
{
    /// <summary>The four light colours a level's Init.con sets.</summary>
    /// <param name="Sun"><c>renderer.diffuseColor</c>.</param>
    /// <param name="Back"><c>renderer.SecondaryDiffuseColor</c>.</param>
    /// <param name="Ambient"><c>renderer.ambientColor</c> - what an object WITHOUT a lightmap adds.</param>
    /// <param name="LmAmbient"><c>renderer.LMambientColor</c> - what a lightmapped object adds.</param>
    public readonly record struct Colours(Vector3 Sun, Vector3 Back, Vector3 Ambient, Vector3 LmAmbient);

    /// <summary>A material's own colours from its <c>.rs</c> file: <c>materialDiffuse</c>, <c>materialAmbient</c> and
    /// <c>selfillum</c>. Nearly every retail material is the default.</summary>
    public readonly record struct Material(Vector3 Diffuse, Vector3 Ambient, Vector3 SelfIllum)
    {
        public static readonly Material Default = new(Vector3.One, Vector3.One, Vector3.Zero);

        /// <summary>The mesh has no tangent frame, so the engine lights it per vertex through the fixed-function
        /// pipeline: the two lights' sum is clamped to 1 before the lightmap scales it. With both lights at or under
        /// 1 and only one of them reaching a face this changes nothing; it bites on a sun colour above 1 and on
        /// faces both lights reach.</summary>
        public bool VertexLit { get; init; }
    }

    /// <summary>Rec. 709 luma - how bright a display colour reads.</summary>
    public static float Luma(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

    /// <summary>The direction TOWARD the engine's second light, given the direction toward the sun: the opposite
    /// compass bearing, the same height. No .con command aims it.</summary>
    public static Vector3 BackLight(Vector3 toSun) => new(-toSun.X, toSun.Y, -toSun.Z);

    /// <summary>The two directional lights on a surface facing <paramref name="n"/>, before the lightmap and the
    /// ambient: <c>sun * sat(N.L) + back * sat(N.B)</c>.</summary>
    public static Vector3 Direct(Vector3 n, Vector3 toSun, in Colours c)
    {
        float a = MathF.Max(0f, Vector3.Dot(n, toSun));
        float b = MathF.Max(0f, Vector3.Dot(n, BackLight(toSun)));
        return c.Sun * a + c.Back * b;
    }

    /// <summary>What the game multiplies a LIGHTMAPPED surface's texture by.</summary>
    /// <param name="prelight">The lightmap's value at this point, 0..1.</param>
    public static Vector3 Lightmapped(float prelight, Vector3 n, Vector3 toSun, in Colours c, in Material m)
        => Saturate(prelight * Lights(n, toSun, c, m) + m.Ambient * c.LmAmbient + m.SelfIllum);

    /// <summary>The same for a lightmap that carries colour. Only a mesh drawn fixed-function
    /// (<see cref="Material.VertexLit"/>) shows it; the per-pixel shader reads the blue channel alone.</summary>
    public static Vector3 Lightmapped(Vector3 lightmap, Vector3 n, Vector3 toSun, in Colours c, in Material m)
        => Saturate((m.VertexLit ? lightmap : new Vector3(lightmap.Z)) * Lights(n, toSun, c, m) + m.Ambient * c.LmAmbient + m.SelfIllum);

    /// <summary>The two lights as the lightmap meets them: times the material's diffuse, and on a vertex-lit mesh
    /// clamped to 1.</summary>
    public static Vector3 Lights(Vector3 n, Vector3 toSun, in Colours c, in Material m)
    {
        var d = m.Diffuse * Direct(n, toSun, c);
        return m.VertexLit ? Saturate(d) : d;
    }

    /// <summary>The same with the default material.</summary>
    public static Vector3 Lightmapped(float prelight, Vector3 n, Vector3 toSun, in Colours c)
        => Lightmapped(prelight, n, toSun, c, Material.Default);

    /// <summary>What the game multiplies the texture of a surface with NO lightmap by - a prop, a vehicle.</summary>
    public static Vector3 Dynamic(Vector3 n, Vector3 toSun, in Colours c, in Material m)
        => Saturate(m.Diffuse * Direct(n, toSun, c) + m.Ambient * c.Ambient + m.SelfIllum);

    /// <summary>The same with the default material.</summary>
    public static Vector3 Dynamic(Vector3 n, Vector3 toSun, in Colours c) => Dynamic(n, toSun, c, Material.Default);

    /// <summary>An object with no lightmap standing in terrain shadow: the two lights reach it at
    /// <paramref name="lightShare"/> (<see cref="LightShare"/>; 1 in the open), the ambient whole.</summary>
    public static Vector3 Dynamic(Vector3 n, Vector3 toSun, in Colours c, in Material m, float lightShare)
        => Saturate(lightShare * m.Diffuse * Direct(n, toSun, c) + m.Ambient * c.Ambient + m.SelfIllum);

    /// <summary><c>renderer.standardMeshMinIntensity</c> when a level does not set it (the exe's own default).</summary>
    public const float DefaultMinIntensity = 0.2f;

    /// <summary>
    /// One of the engine's terrain samples for an unlightmapped object (exe 0x7243a0), 0..1: 1 where the point is
    /// not in terrain shadow; in shadow, the mean colour of the terrain tile's 4 x 4 texture block under it,
    /// weighed 0.3 / 0.59 / 0.11 - the ground's own brightness, shadow and paint alike.
    /// </summary>
    public static float TerrainSample(bool inShadow, Vector3 groundTexel)
        => inShadow ? Math.Clamp(0.3f * groundTexel.X + 0.59f * groundTexel.Y + 0.11f * groundTexel.Z, 0f, 1f) : 1f;

    /// <summary>
    /// The share of the sun and back light an unlightmapped object keeps (exe 0x74a050):
    /// <c>min + (1 - min) x terrain</c>, where <paramref name="terrain"/> is the mean of the object's four
    /// <see cref="TerrainSample"/>s and <paramref name="minIntensity"/> is <c>renderer.standardMeshMinIntensity</c>.
    /// In the open it is 1; Hue's first calibration boards, over ground of brightness 33/255 with a minimum of 0.3,
    /// showed 0.39.
    /// </summary>
    public static float LightShare(float minIntensity, float terrain)
    {
        float m = Math.Clamp(minIntensity, 0f, 1f);
        return m + (1f - m) * Math.Clamp(terrain, 0f, 1f);
    }

    /// <summary>The angles, in degrees about the vertical, of the five terrain samples a static object takes.</summary>
    public static readonly float[] StaticSampleAngles = { -22.5f, -13.5f, -4.5f, 4.5f, 13.5f };

    /// <summary>
    /// Where a STATIC object with no lightmap samples the terrain (exe 0x6722a0, run once over the level's objects by
    /// 0x673700 and not per frame): five points on an arc 1.1 radii from the object's position, toward the sun on
    /// the level - the sun vector flattened and normalised, then turned by each of <see cref="StaticSampleAngles"/>.
    /// The height is the object's own. Moving things (soldiers, vehicles) take four points instead, per frame.
    /// </summary>
    public static Vector3[] StaticSamplePoints(Vector3 position, float radius, Vector3 toSun)
    {
        var flat = new Vector3(toSun.X, 0f, toSun.Z);
        flat = flat.LengthSquared() > 1e-12f ? Vector3.Normalize(flat) : Vector3.UnitZ;
        var pts = new Vector3[StaticSampleAngles.Length];
        for (int i = 0; i < pts.Length; i++)
        {
            float t = StaticSampleAngles[i] * MathF.PI / 180f, c = MathF.Cos(t), sn = MathF.Sin(t);
            var dir = new Vector3(c * flat.X + sn * flat.Z, 0f, -sn * flat.X + c * flat.Z);
            pts[i] = position + dir * (1.1f * radius);
        }
        return pts;
    }

    /// <summary>
    /// The terrain value of a static object from its <see cref="TerrainSample"/>s (exe 0x673570): their mean, with
    /// any sample brighter than 128 / 255 counted as fully lit (a flat 128 grey tile is not: the board over one
    /// showed it). Feed it to <see cref="LightShare"/>. With the board
    /// radius the one free number, this lands within 0.012 of all fourteen boards measured (rms 0.0045).
    /// </summary>
    public static float StaticTerrain(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0) return 1f;
        float sum = 0f;
        foreach (float v in samples) sum += v * 255f > 128.001f ? 1f : Math.Clamp(v, 0f, 1f);
        return sum / samples.Length;
    }

    /// <summary>
    /// A terrain tile colour as the engine's sampler reads it (exe 0x7216d0): the DXT1 block's 5:6:5 colours
    /// widened by shifting alone, so white is 248 / 252 / 248 and never 255. Pass this to <see cref="TerrainSample"/>.
    /// </summary>
    public static Vector3 TileColour(byte r, byte g, byte b)
        => new Vector3((r >> 3) << 3, (g >> 2) << 2, (b >> 3) << 3) / 255f;

    /// <summary>
    /// The radius the engine samples with (exe 0x6852d0, read by the mesh as its own radius): the distance from the
    /// mesh ORIGIN to the farthest corner of its bounding box - not the box's half-diagonal. An object with children
    /// takes the larger of this and each child's reach (0x684e60); that part is not modelled here.
    /// </summary>
    public static float StaticRadius(Vector3 boxMin, Vector3 boxMax)
    {
        float x = MathF.Max(MathF.Abs(boxMin.X), MathF.Abs(boxMax.X));
        float y = MathF.Max(MathF.Abs(boxMin.Y), MathF.Abs(boxMax.Y));
        float z = MathF.Max(MathF.Abs(boxMin.Z), MathF.Abs(boxMax.Z));
        return MathF.Sqrt(x * x + y * y + z * z);
    }

    /// <summary>
    /// The share of the sun and back light a static object with no lightmap keeps: <see cref="StaticSamplePoints"/>,
    /// each read through <paramref name="inShadow"/> (the terrain shadow bits) and <paramref name="ground"/> (the
    /// terrain texture colour there, 0..1), then <see cref="StaticTerrain"/> and <see cref="LightShare"/>.
    /// </summary>
    public static float StaticShare(Vector3 position, float radius, Vector3 toSun, float minIntensity,
                                    Func<Vector3, bool> inShadow, Func<Vector3, Vector3> ground)
    {
        var pts = StaticSamplePoints(position, radius, toSun);
        Span<float> v = stackalloc float[pts.Length];
        for (int i = 0; i < pts.Length; i++)
        {
            bool sh = inShadow(pts[i]);
            v[i] = TerrainSample(sh, sh ? ground(pts[i]) : Vector3.One);
        }
        return LightShare(minIntensity, StaticTerrain(v));
    }

    /// <summary>
    /// The lightmap value that makes the game draw <paramref name="target"/> on this surface - the inverse of
    /// <see cref="Lightmapped(float, Vector3, Vector3, in Colours, in Material)"/>.
    ///
    /// <para>One number cannot match three channels, so it matches BRIGHTNESS: the luma of what the game draws equals
    /// the luma of the target wherever that is possible. It is not possible in two cases, and both clamp rather than
    /// fail: a target darker than the ambient (the ambient is added after the lightmap, so nothing goes below it),
    /// and a target brighter than both lights at full.</para>
    ///
    /// <para>On a face neither light reaches the value changes nothing in the game. It is still solved - against a
    /// small floor - so that it stays continuous with its neighbours across a bilinear tap, and so a normal map that
    /// tilts part of that face into the light finds a sensible value there.</para>
    /// </summary>
    /// <param name="target">What should multiply the texture, in the same display units the engine's colours are in.</param>
    public static float SolvePrelight(Vector3 target, Vector3 n, Vector3 toSun, in Colours c, in Material m)
    {
        float want = Luma(target) - Luma(m.Ambient * c.LmAmbient + m.SelfIllum);
        if (want <= 0f) return 0f;
        float lights = Luma(Lights(n, toSun, c, m));
        return Math.Clamp(want / MathF.Max(lights, MinLights), 0f, 1f);
    }

    /// <summary>The same with the default material.</summary>
    public static float SolvePrelight(Vector3 target, Vector3 n, Vector3 toSun, in Colours c)
        => SolvePrelight(target, n, toSun, c, Material.Default);

    /// <summary>Below this much light from the two lamps a face is treated as unlit by them, and the solve divides by
    /// this instead of by nearly nothing.</summary>
    public const float MinLights = 0.02f;

    /// <summary>How far short of <paramref name="target"/> the game will fall on this surface however the lightmap is
    /// set, in luma: positive when the target is out of reach above (both lights at full are not enough), negative
    /// when it is out of reach below (the ambient alone is already brighter). 0 when a lightmap value reaches it.</summary>
    public static float OutOfReach(Vector3 target, Vector3 n, Vector3 toSun, in Colours c, in Material m)
    {
        float floor = Luma(m.Ambient * c.LmAmbient + m.SelfIllum);
        float ceil = MathF.Min(1f, floor + Luma(Lights(n, toSun, c, m)));
        float t = MathF.Min(1f, Luma(target));
        return t > ceil ? t - ceil : t < floor ? t - floor : 0f;
    }

    private static Vector3 Saturate(Vector3 v) => Vector3.Clamp(v, Vector3.Zero, Vector3.One);
}
