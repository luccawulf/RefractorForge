using RefractorForge.Formats.Geometry;

namespace RefractorForge.Formats.Terrain;

/// <summary>
/// Whole-scene lighting presets: sun, ambient, fog and sky moved together.
///
/// Each one is a set of the same Init.con renderer values a real level declares, so applying a preset is not a
/// viewport effect - it is what the game will show once saved. Night is DC_Basrah_Nights' recipe verbatim; the
/// others are built to the same shape, with the sun angle chosen to match the hour.
/// </summary>
public sealed record TimeOfDayPreset(
    string Name,
    float SunAzimuthDeg, float SunElevationDeg,
    Vec3 GlobalAmbient, Vec3 Ambient, Vec3 Diffuse, Vec3 Specular,
    bool Fog, Vec3 FogColor, float FogStart, float FogEnd, float ViewDistance,
    Vec3 SkyTint, float NightAmount)
{
    public static readonly TimeOfDayPreset Dawn = new("Dawn",
        95f, 8f,
        new(0.22f, 0.20f, 0.26f), new(0.20f, 0.17f, 0.20f), new(0.95f, 0.72f, 0.55f), new(0.9f, 0.7f, 0.5f),
        true, new(0.86f, 0.66f, 0.55f), 120f, 520f, 600f,
        new(1.0f, 0.78f, 0.62f), 0.15f);

    public static readonly TimeOfDayPreset Noon = new("Noon",
        135f, 62f,
        new(0.16f, 0.15f, 0.17f), new(0.12f, 0.10f, 0.08f), new(0.975f, 1.0f, 0.95f), new(0.9f, 0.9f, 0.7f),
        false, new(0.72f, 0.83f, 0.83f), 250f, 900f, 900f,
        new(1f, 1f, 1f), 0f);

    public static readonly TimeOfDayPreset Dusk = new("Dusk",
        255f, 6f,
        new(0.20f, 0.15f, 0.20f), new(0.18f, 0.12f, 0.14f), new(0.92f, 0.55f, 0.35f), new(0.95f, 0.6f, 0.4f),
        true, new(0.62f, 0.40f, 0.36f), 100f, 420f, 480f,
        new(1.0f, 0.62f, 0.45f), 0.25f);

    // Moon at 55, not 30: a lamp baked into a lightmap shows only on faces the sun direction reaches (lm * sun * N.L
    // in both games), and at 30 degrees most floors got nothing from their lamps.
    public static readonly TimeOfDayPreset Night = new("Night",
        200f, 55f,
        new(0.080f, 0.082f, 0.085f), new(0.080f, 0.082f, 0.085f), new(0.18f, 0.20f, 0.22f), new(0.4f, 0.5f, 0.6f),
        true, new(0.09f, 0.10f, 0.11f), 85f, 130f, 130f,
        new(0.13f, 0.15f, 0.20f), 0.85f);

    public static readonly TimeOfDayPreset Overcast = new("Overcast",
        150f, 45f,
        new(0.30f, 0.31f, 0.33f), new(0.26f, 0.27f, 0.29f), new(0.55f, 0.57f, 0.60f), new(0.3f, 0.3f, 0.3f),
        true, new(0.66f, 0.69f, 0.72f), 80f, 380f, 420f,
        new(0.72f, 0.74f, 0.78f), 0.1f);

    // ---- The nights the reference maps actually ship ----------------------------------------------------------
    // Measured off the two night maps the user pointed at, with one deliberate change: the "moon" is HIGH. Both
    // games apply a lightmap as `lm * sun * N.L`, so a lamp baked into a lightmap can only show on faces the sun
    // direction reaches; Bespin's near-horizontal sun (-10/0/-1.5) leaves every floor at N.L = 0. At 55 degrees up,
    // floors, roofs and most walls all carry lamp light.

    /// <summary>Battlefield 1942 - Dystopia City's TDM night: warm-grey, a readable 0.4 moon, mid fog.</summary>
    public static readonly TimeOfDayPreset NightBf1942 = new("Night (BF1942)",
        215f, 55f,
        new(0.10f, 0.10f, 0.11f), new(0.05f, 0.05f, 0.06f), new(0.40f, 0.40f, 0.42f), new(0.08f, 0.08f, 0.08f),
        true, new(0.293f, 0.266f, 0.195f), 368f, 512f, 1024f,
        new(0.20f, 0.19f, 0.17f), 0.85f);

    /// <summary>Battlefield 1942 - Bespin Night's look: deep blue ambient, warm dim moon, blue fog to the horizon.</summary>
    public static readonly TimeOfDayPreset NightBlue = new("Night - blue (Bespin)",
        215f, 55f,
        new(0.00f, 0.086f, 0.161f), new(0.00f, 0.00f, 0.00f), new(0.247f, 0.233f, 0.161f), new(0.05f, 0.046f, 0.032f),
        true, new(0.00f, 0.086f, 0.161f), 900f, 950f, 1000f,
        new(0.05f, 0.10f, 0.20f), 0.9f);

    /// <summary>Battlefield Vietnam - DC_Basrah_Nights' recipe (BfVietnam ignores globalAmbientColor; it is kept
    /// equal to ambient so the editor's preview does not read brighter than the game).</summary>
    public static readonly TimeOfDayPreset NightBfv = new("Night (BFV)",
        215f, 55f,
        new(0.080f, 0.082f, 0.085f), new(0.080f, 0.082f, 0.085f), new(0.18f, 0.20f, 0.22f), new(0.4f, 0.5f, 0.6f),
        true, new(0.09f, 0.10f, 0.11f), 85f, 130f, 130f,
        new(0.13f, 0.15f, 0.20f), 0.85f);

    public static IReadOnlyList<TimeOfDayPreset> All { get; } = new[] { Dawn, Noon, Dusk, Night, Overcast };

    /// <summary>The night looks, per game - what the Night Lighting window offers first.</summary>
    public static IReadOnlyList<TimeOfDayPreset> Nights { get; } = new[] { NightBf1942, NightBlue, NightBfv, Night };

    /// <summary>The sun direction vector the engine's <c>sky.sunLightDirectionVec</c> wants, from az/el.</summary>
    public Vec3 SunDirection()
    {
        float az = SunAzimuthDeg * MathF.PI / 180f, el = SunElevationDeg * MathF.PI / 180f;
        return new Vec3(MathF.Cos(el) * MathF.Sin(az), MathF.Sin(el), MathF.Cos(el) * MathF.Cos(az));
    }
}
