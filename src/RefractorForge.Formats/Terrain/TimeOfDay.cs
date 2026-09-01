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

    public static readonly TimeOfDayPreset Night = new("Night",
        200f, 30f,
        new(0.080f, 0.082f, 0.085f), new(0.080f, 0.082f, 0.085f), new(0.18f, 0.20f, 0.22f), new(0.4f, 0.5f, 0.6f),
        true, new(0.09f, 0.10f, 0.11f), 85f, 130f, 130f,
        new(0.13f, 0.15f, 0.20f), 0.85f);

    public static readonly TimeOfDayPreset Overcast = new("Overcast",
        150f, 45f,
        new(0.30f, 0.31f, 0.33f), new(0.26f, 0.27f, 0.29f), new(0.55f, 0.57f, 0.60f), new(0.3f, 0.3f, 0.3f),
        true, new(0.66f, 0.69f, 0.72f), 80f, 380f, 420f,
        new(0.72f, 0.74f, 0.78f), 0.1f);

    public static IReadOnlyList<TimeOfDayPreset> All { get; } = new[] { Dawn, Noon, Dusk, Night, Overcast };

    /// <summary>The sun direction vector the engine's <c>sky.sunLightDirectionVec</c> wants, from az/el.</summary>
    public Vec3 SunDirection()
    {
        float az = SunAzimuthDeg * MathF.PI / 180f, el = SunElevationDeg * MathF.PI / 180f;
        return new Vec3(MathF.Cos(el) * MathF.Sin(az), MathF.Sin(el), MathF.Cos(el) * MathF.Cos(az));
    }
}
