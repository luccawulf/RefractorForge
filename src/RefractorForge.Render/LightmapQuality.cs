using System;

namespace RefractorForge.Render;

/// <summary>
/// The lightmap bake's quality tiers, as data rather than as a <c>switch</c> buried in the UI.
///
/// <para>It lives here so it can be TESTED. The tier table was previously inline in the viewer next to a
/// <c>Math.Clamp(tier, 0, 2)</c>, and when a fourth tier was added the clamp was not widened with it - so
/// choosing Ultra snapped straight back to High, the option could not be selected at all, and every bake quietly
/// ran at the old settings while appearing to offer new ones. Nothing caught that, because a clamp in a UI
/// handler is not reachable from a test. A pure function is.</para>
/// </summary>
/// <param name="SubSamples">Sub-samples per texel AXIS - what smooths a shadow edge within a texel.</param>
/// <param name="TexelsPerMetre">How much of the atlas a surface gets.</param>
/// <param name="MaxSize">Per-map ceiling. 1024 is the ENGINE's limit; 2048 crashes the game.</param>
/// <param name="Advanced">Whether the offline terms below are on at all.</param>
public sealed record LightmapQuality(
    int Tier, string Name, int SubSamples, float TexelsPerMetre, int MaxSize,
    bool Advanced, int SunSamples, int SkySamples, int BounceSamples, int DenoiseIterations)
{
    public const int Draft = 0;
    public const int Good = 1;
    public const int High = 2;
    /// <summary>The offline tier: soft sun, ambient occlusion, sky fill, indirect bounce, denoised.</summary>
    public const int Ultra = 3;
    /// <summary>How many tiers there are. Clamp against this, never against a literal.</summary>
    public const int Count = 4;

    /// <summary>1024 is a hard engine ceiling - 2048 crashes the game. No tier may exceed it.</summary>
    public const int EngineMaxSize = 1024;

    private static readonly LightmapQuality[] Tiers =
    {
        // The first three are exactly what they have always been, and must stay that way: they produced the
        // reference bake the user confirmed looks right AND loads, so changing them would change every map that
        // has ever been baked with this editor.
        new(Draft, "Draft",  1, 16f,  256, false, 1,  0,  0, 0),
        new(Good,  "Good",   2, 24f,  512, false, 1,  0,  0, 0),
        new(High,  "High",   3, 40f, 1024, false, 1,  0,  0, 0),
        // Minutes rather than seconds on a big map. The bake already runs on a worker behind a progress bar with
        // a Cancel, so the cost is wall-clock rather than a frozen editor.
        new(Ultra, "Ultra",  3, 40f, 1024, true, 32, 48, 24, 2),
    };

    /// <summary>The settings for a tier. Out-of-range values clamp into range rather than throwing - a bad
    /// setting restored from disk should give a working editor, not a crash.</summary>
    public static LightmapQuality For(int tier) => Tiers[Math.Clamp(tier, 0, Count - 1)];
}
