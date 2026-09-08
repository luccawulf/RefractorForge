using System;
using System.Collections.Generic;
using RefractorForge.Formats.Geometry;

namespace RefractorForge.Formats.Con;

/// <summary>
/// A pool of lamplight you can actually see in the game.
///
/// WHY THIS EXISTS, and why the obvious answer does not work. Refractor renders no dynamic lights at all — a frame
/// capture of BfVietnam shows the engine setting 24 directional lights and exactly zero point or spot lights, with
/// fixed-function lighting off on every draw. The other obvious channel, the per-object lightmap, cannot help
/// either: <c>effects/RaShaderPPLSTs1DifLmp.fx</c> reads it as
/// <c>Prelight = tex2D(LightMapSampler, uv).b</c> — one scalar, which then MULTIPLIES the sun:
/// <c>FinalDiffuseLight = Prelight * LightDOT3 + LightAmbient</c>. A lightmap can therefore only ever take light
/// AWAY. It can never be warmer than the sun, brighter than the sun, or present where the sun is not — which is
/// exactly why a lamp hung in a tunnel lights nothing beneath it however the lightmap is painted.
///
/// What DOES put light on a surface is geometry. 41 shipped materials — every muzzle flash, <c>e_MuzzAK47_m1.rs</c>
/// among them — use an unlit, self-illuminated, ADDITIVELY blended quad:
/// <c>lighting false; selfillum 1 1 1; transparent true; sortedBlend true; blendSrc sourcealpha; blendDest one;
/// depthWrite false;</c>. Additive means the quad can only brighten what is behind it, so laid on a floor it reads
/// as a pool of light and laid at a bulb it reads as a glow — in a pitch-black tunnel or in daylight, on terrain or
/// on a mesh, at any detail setting, in both games. That is the recipe here.
///
/// The object is the same six-file level-local recipe as <see cref="DecalObject"/> (which this delegates to), so a
/// pool packs, saves and loads by a path that is already proven.
/// </summary>
public static class LightPool
{
    /// <summary>How a pool is shaped.</summary>
    public enum Shape
    {
        /// <summary>Lying flat, for the light a lamp throws on the floor beneath it.</summary>
        Floor,
        /// <summary>Standing upright, for the halo at the bulb itself.</summary>
        Glow,
    }

    /// <summary>
    /// Build the level-local object for one pool of light.
    /// </summary>
    /// <param name="levelName">Level folder name under &lt;baseSub&gt;/levels/.</param>
    /// <param name="name">Template name.</param>
    /// <param name="diameterMetres">How wide the pool is on the ground. A ceiling bulb throws roughly its own
    /// height in diameter; 6-10 m is a street lamp.</param>
    /// <param name="colour">Lamp colour, 0..1. Retail's own bulb glow is 248/238/205 — a warm off-white.</param>
    /// <param name="brightness">Scales the texture. Because the blend is additive this is literally how much light
    /// the pool adds, so past about 1.5 it starts to blow out to white.</param>
    /// <param name="softness">0 is a hard-edged disc, 1 fades from the very centre. Around 0.6 looks like a lamp.</param>
    /// <param name="baseSub">"bf1942" or "BfVietnam" — the archive mount root; the two games share no namespace.</param>
    public static DecalObject.Built Build(string levelName, string name, float diameterMetres, Vec3 colour,
                                          float brightness = 1f, float softness = 0.6f, Shape shape = Shape.Floor,
                                          string baseSub = "bf1942", int textureSize = 128,
                                          Func<byte[], byte[]>? encodeDds = null)
    {
        name = DecalObject.Sanitize(name);
        string texName = DecalObject.Sanitize("lightpool_" + name);
        float d = Math.Clamp(diameterMetres, 0.25f, 400f);

        var rgba = Gradient(textureSize, colour, brightness, softness);
        // The caller owns DDS encoding (it lives in the Render layer, which Formats does not reference). Without an
        // encoder the object is still complete and correct — it simply ships no picture, which is a caller error
        // worth failing loudly on rather than writing a pool that draws nothing.
        byte[]? dds = encodeDds?.Invoke(rgba);

        // A pool must never take a bullet or block a step, and `flat` lays the quad down for a floor pool while a
        // glow stands upright like a sign. Two-sided either way: seen from below, a floor pool should still be there.
        return DecalObject.Build(levelName, name, d, d, texName, dds,
                                 flat: shape == Shape.Floor, doubleSided: true, baseSub: baseSub,
                                 additive: true);
    }

    /// <summary>
    /// The picture: a radial falloff in RGBA (4 bytes per texel, row-major from the top), sized
    /// <paramref name="size"/> square. Colour is constant across the disc and the FALLOFF IS IN THE ALPHA, because
    /// the blend is <c>sourcealpha</c>/<c>one</c> — alpha is what decides how much light each texel adds, so putting
    /// the shape there (rather than dimming the colour) keeps the hue of the lamp constant from the hot centre out
    /// to the rim, the way a real pool of light behaves.
    /// </summary>
    public static byte[] Gradient(int size, Vec3 colour, float brightness = 1f, float softness = 0.6f)
    {
        size = Math.Clamp(size, 8, 512);
        softness = Math.Clamp(softness, 0f, 1f);
        brightness = Math.Clamp(brightness, 0f, 4f);

        byte R = Q(colour.X), G = Q(colour.Y), B = Q(colour.Z);
        var px = new byte[size * size * 4];
        float half = size * 0.5f;

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            // Distance from the centre as a fraction of the radius, sampling texel CENTRES so the disc is
            // symmetrical and does not sit half a texel off.
            float dx = (x + 0.5f - half) / half, dy = (y + 0.5f - half) / half;
            float t = MathF.Sqrt(dx * dx + dy * dy);

            // A smoothstep down from the centre, raised to a power. NO flat core: a plateau of full alpha is what
            // makes an additive pool read as a white disc with a rim rather than as light — measured against three
            // other curves, a hot core of even 30% of the radius blows out. Softness is the EXPONENT, so it moves
            // light from the rim towards the centre: 0 is a broad hard-edged disc, 1 is a tight point with a long
            // tail, and the peak is only ever at the exact middle.
            float a = t >= 1f ? 0f
                    : MathF.Pow(1f - t * t * (3f - 2f * t), 0.6f + softness * 2.4f);

            float v = a * brightness;
            int i = (y * size + x) * 4;
            px[i + 0] = R; px[i + 1] = G; px[i + 2] = B;
            px[i + 3] = Q(v);
        }
        return px;
    }

    private static byte Q(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

    /// <summary>
    /// Template names the retail games hang a light effect on — the objects a mapper means when they say "lamp".
    /// Taken from the game's own data: these are the templates whose Objects.con carries an <c>e_lightbulb*</c>,
    /// <c>e_streetlight</c> or torch EffectBundle, plus the obvious name matches. Used to offer "put a pool under
    /// every lamp" rather than making the user find them all by eye.
    /// </summary>
    public static bool LooksLikeLamp(string template)
    {
        if (string.IsNullOrWhiteSpace(template)) return false;
        var t = template.Trim();
        foreach (var k in LampWords)
            if (t.Contains(k, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static readonly string[] LampWords =
    {
        "lamp", "light", "lantern", "torch", "candle", "bulb", "streetlight", "chandelier",
    };

    /// <summary>Every placed object that looks like a lamp, as (template, position). The editor turns each into a
    /// light and a pool.</summary>
    public static List<(string Template, Vec3 Position)> FindLamps(IEnumerable<(string Template, Vec3 Position)> placed)
    {
        var hits = new List<(string, Vec3)>();
        foreach (var o in placed)
            if (LooksLikeLamp(o.Template)) hits.Add((o.Template, o.Position));
        return hits;
    }
}
