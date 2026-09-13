using System.Globalization;
using System.Text;
using System.Text.Json;
using RefractorForge.Formats.Geometry;

namespace RefractorForge.Formats.Terrain;

/// <summary>
/// A placed light. Position, colour, reach and brightness — the things you set on a lamp.
///
/// IMPORTANT about what these are. Refractor does NOT render dynamic point lights: a capture of a running
/// BfVietnam frame shows the engine setting 6,826 DIRECTIONAL lights and exactly zero point or spot lights, with
/// fixed-function lighting off for most draws because everything goes through shaders. What the game calls a
/// "streetlight" is an EffectBundle of additive glow sprites that emit nothing.
///
/// So a light here is AUTHORING data. It lights the editor viewport live so you can aim it, and it is baked into
/// the lightmaps the engine really does read — the terrain <c>.lsb</c> and the per-object lightmaps. That is how
/// night maps were actually lit, and it means a light placed here shows up in the game once baked.
/// </summary>
public sealed class PointLight
{
    public string Name { get; set; } = "Light";
    public Vec3 Position { get; set; }

    /// <summary>Metres at which the light has fallen to nothing.</summary>
    public float Radius { get; set; } = 20f;

    /// <summary>Brightness at the source. 1 is "about as bright as full sun".</summary>
    public float Intensity { get; set; } = 1f;

    public float ColorR { get; set; } = 1f;
    public float ColorG { get; set; } = 0.86f;
    public float ColorB { get; set; } = 0.65f;   // a warm bulb, the common case

    /// <summary>Off keeps the light in the level without contributing, for A/B comparisons.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Whether the bake traces terrain occlusion for this light. Off is much faster and is right for
    /// a fill light that is only there to lift the ambient.</summary>
    public bool CastsShadows { get; set; } = true;

    /// <summary>
    /// Falloff exponent. 2 is physically correct inverse-square; lower is flatter and easier to light a scene
    /// with, which is why every game lighting tool exposes it.
    /// </summary>
    public float Falloff { get; set; } = 2f;

    // ---- What a lamp is, beyond a point ------------------------------------------------------------------------
    // Everything below is optional in the sidecar: a rig written before these existed loads with the defaults, which
    // reproduce the old behaviour exactly (an omnidirectional point, a hard shadow, a glow of the light's own colour).

    /// <summary>0 = point (a bulb, shines everywhere), 1 = spot (a cone - a street lamp head, a floodlight).</summary>
    public int Kind { get; set; }

    /// <summary>Where a spot points, as compass yaw (degrees, 0 = +Z north, 90 = +X east) and pitch (degrees,
    /// -90 = straight down, 0 = level). A street lamp is pitch -90; a floodlight on a wall is about -30.</summary>
    public float SpotYawDeg { get; set; }
    public float SpotPitchDeg { get; set; } = -90f;

    /// <summary>Full cone angle of a spot, in degrees. Nothing outside it is lit.</summary>
    public float ConeDeg { get; set; } = 90f;

    /// <summary>How much of the cone is a soft edge, 0..1: 0 is a hard-edged circle, 1 fades from the axis out.</summary>
    public float ConeSoft { get; set; } = 0.5f;

    /// <summary>
    /// The physical size of the emitter in metres - the radius of the bulb, tube or fixture. This is what gives a
    /// shadow a PENUMBRA: the bake samples the light across this disc, so a wall's shadow on the ground goes from
    /// sharp at its base to soft further out, the way real lamp shadows do. 0 is a mathematical point and a
    /// razor-edged shadow; 0.3 is a bulb; 1.0 is a big fixture or a fire.
    /// </summary>
    public float SourceSize { get; set; } = 0.3f;

    /// <summary>Put an additive glow sprite at the bulb when the rig is baked, so the lamp itself reads as lit.</summary>
    public bool Glow { get; set; } = true;

    /// <summary>Diameter of the glow sprite, metres. Retail street lamps use 1.5-4.</summary>
    public float GlowSize { get; set; } = 2f;

    /// <summary>Scales the glow's colour; past ~1.5 the centre burns to white, which suits a bare bulb.</summary>
    public float GlowBrightness { get; set; } = 1f;

    /// <summary>Whether this light is baked into the ground texture (its pool on the floor).</summary>
    public bool OnGround { get; set; } = true;

    /// <summary>Whether this light is baked into the per-object lightmaps (its light on walls and props).</summary>
    public bool OnObjects { get; set; } = true;

    public bool IsSpot => Kind == 1;

    public PointLight Clone() => (PointLight)MemberwiseClone();

    /// <summary>Unit vector a spot shines along (from yaw/pitch). Meaningless for a point light.</summary>
    public Vec3 Direction()
    {
        float yaw = SpotYawDeg * MathF.PI / 180f, pitch = SpotPitchDeg * MathF.PI / 180f;
        float c = MathF.Cos(pitch);
        return new Vec3(MathF.Sin(yaw) * c, MathF.Sin(pitch), MathF.Cos(yaw) * c);
    }

    /// <summary>
    /// The cone term of a spot at a point, 0..1 (1 everywhere for a point light): 1 inside the inner cone, 0 outside
    /// the outer one, a smooth ramp between - so a spot's edge on the ground is a soft ring, not a stencil.
    /// </summary>
    public float Cone(float wx, float wy, float wz)
    {
        if (!IsSpot) return 1f;
        float dx = wx - Position.X, dy = wy - Position.Y, dz = wz - Position.Z;
        float len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        if (len < 1e-4f) return 1f;
        var d = Direction();
        float cosA = (dx * d.X + dy * d.Y + dz * d.Z) / len;
        float outer = MathF.Cos(Math.Clamp(ConeDeg, 1f, 179f) * 0.5f * MathF.PI / 180f);
        float inner = MathF.Cos(Math.Clamp(ConeDeg, 1f, 179f) * 0.5f * (1f - Math.Clamp(ConeSoft, 0f, 1f)) * MathF.PI / 180f);
        if (cosA <= outer) return 0f;
        if (cosA >= inner || inner <= outer) return 1f;
        float t = (cosA - outer) / (inner - outer);
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// How much this light delivers to a point, ignoring occlusion. Zero past the radius (and outside a spot's
    /// cone), so the bake can skip whole regions cheaply.
    /// </summary>
    public float Attenuation(float wx, float wy, float wz)
    {
        if (!Enabled || Intensity <= 0f || Radius <= 0f) return 0f;
        float dx = wx - Position.X, dy = wy - Position.Y, dz = wz - Position.Z;
        float d2 = dx * dx + dy * dy + dz * dz;
        float r2 = Radius * Radius;
        if (d2 >= r2) return 0f;

        // Normalised distance, then a windowed falloff: the exponent gives the shape and the (1 - t^2)^2
        // window pulls it cleanly to zero at the radius. Without the window a light visibly stops at a circle.
        float t = MathF.Sqrt(d2) / Radius;
        float window = 1f - t * t;
        window *= window;
        float shape = MathF.Pow(1f - t, MathF.Max(Falloff, 0.1f));
        float a = Intensity * shape * window;
        return IsSpot ? a * Cone(wx, wy, wz) : a;
    }
}

/// <summary>
/// The lamps people actually place, as starting points. Each is a complete light; the user tunes from there.
/// Colours are the ones the real fixtures have (sodium is orange, halide is cold, a fire is deep amber), because a
/// night street lit by one uniform "warm white" is the first thing that looks like a game rather than a place.
/// </summary>
public sealed record LightPreset(string Name, float R, float G, float B, float Intensity, float Radius, float Falloff,
                                 int Kind, float ConeDeg, float ConeSoft, float SourceSize, float GlowSize, float Height)
{
    public static readonly LightPreset StreetSodium = new("Street lamp - sodium", 1.00f, 0.72f, 0.36f, 1.4f, 26f, 1.6f, 1, 120f, 0.6f, 0.35f, 2.2f, 7f);
    public static readonly LightPreset StreetWhite  = new("Street lamp - white",  0.95f, 0.95f, 1.00f, 1.3f, 26f, 1.6f, 1, 120f, 0.6f, 0.35f, 2.0f, 7f);
    public static readonly LightPreset WallLamp     = new("Wall lamp",            1.00f, 0.84f, 0.60f, 0.9f, 12f, 1.8f, 0, 90f, 0.5f, 0.15f, 1.0f, 3f);
    public static readonly LightPreset Floodlight   = new("Floodlight",           0.92f, 0.96f, 1.00f, 2.2f, 60f, 1.2f, 1, 50f, 0.35f, 0.5f, 2.5f, 8f);
    public static readonly LightPreset Fire         = new("Fire / torch",         1.00f, 0.55f, 0.18f, 1.2f, 14f, 2.2f, 0, 90f, 0.5f, 0.8f, 1.6f, 1f);
    public static readonly LightPreset WindowGlow   = new("Window glow",          1.00f, 0.88f, 0.62f, 0.5f, 9f, 2.4f, 1, 140f, 0.9f, 0.6f, 0f, 1.8f);
    public static readonly LightPreset NeonRed      = new("Neon - red",           1.00f, 0.15f, 0.20f, 0.8f, 10f, 2.0f, 0, 90f, 0.5f, 0.3f, 1.2f, 3f);
    public static readonly LightPreset NeonCyan     = new("Neon - cyan",          0.20f, 0.90f, 1.00f, 0.8f, 10f, 2.0f, 0, 90f, 0.5f, 0.3f, 1.2f, 3f);
    public static readonly LightPreset Moonpool     = new("Cool fill",            0.55f, 0.65f, 1.00f, 0.4f, 40f, 1.2f, 0, 90f, 0.5f, 2.0f, 0f, 12f);

    public static IReadOnlyList<LightPreset> All { get; } =
        new[] { StreetSodium, StreetWhite, WallLamp, Floodlight, Fire, WindowGlow, NeonRed, NeonCyan, Moonpool };

    /// <summary>Copy the preset's look onto a light, keeping its name and position.</summary>
    public void ApplyTo(PointLight l)
    {
        l.ColorR = R; l.ColorG = G; l.ColorB = B;
        l.Intensity = Intensity; l.Radius = Radius; l.Falloff = Falloff;
        l.Kind = Kind; l.ConeDeg = ConeDeg; l.ConeSoft = ConeSoft; l.SpotPitchDeg = -90f;
        l.SourceSize = SourceSize;
        l.Glow = GlowSize > 0f; l.GlowSize = MathF.Max(GlowSize, 0.5f);
    }
}

/// <summary>
/// The lights placed on one level, and their night-preview setting.
///
/// Stored as a sidecar in the level folder rather than in a <c>.con</c>: the engine has no concept of these, and
/// writing an unknown command into a file it parses is how you get console errors on load. The file name is
/// registered with <c>LevelSaver.IsEditorOnlyFile</c>, so it stays in the working folder and never reaches a
/// packed <c>.rfa</c>.
/// </summary>
public sealed class LightRig
{
    public const string FileName = "RefractorForgeLights.json";

    public List<PointLight> Lights { get; set; } = new();

    /// <summary>How far down the sun and ambient are pulled in the editor's night preview. 0 = daylight,
    /// 1 = the sun contributes nothing and only placed lights remain.</summary>
    public float NightAmount { get; set; }

    /// <summary>Colour the remaining ambient takes at full night — moonlight is blue, not grey.</summary>
    public float NightR { get; set; } = 0.10f;
    public float NightG { get; set; } = 0.13f;
    public float NightB { get; set; } = 0.22f;

    /// <summary>Where the rig lives for this level. A folder level keeps it in the folder; a packed level keeps
    /// it beside the archive, since you cannot write a file inside a .rfa - see <see cref="LevelSidecar"/>.</summary>
    public static string PathFor(string levelDir) => LevelSidecar.PathFor(levelDir, FileName);

    public static LightRig Load(string levelDir)
    {
        try
        {
            var p = PathFor(levelDir);
            if (File.Exists(p))
                return JsonSerializer.Deserialize<LightRig>(File.ReadAllText(p)) ?? new LightRig();
        }
        catch { /* a damaged sidecar must not stop a level opening */ }
        return new LightRig();
    }

    /// <summary>The rig as JSON text - what <see cref="Save"/> writes, and what a collaborator receives. Full-state,
    /// like the gameplay layer: two peers can never hold two different lists.</summary>
    public string ToJson() => JsonSerializer.Serialize(this);

    public static LightRig FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<LightRig>(json) ?? new LightRig(); }
        catch { return new LightRig(); }
    }

    public void Save(string levelDir)
    {
        var p = PathFor(levelDir);
        if (Lights.Count == 0 && NightAmount <= 0f)
        {
            // Nothing to remember: do not litter the level folder with an empty file.
            try { if (File.Exists(p)) File.Delete(p); } catch { }
            return;
        }
        LevelSidecar.Write(p, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Total light delivered to a world point by every enabled light, as a colour.
    ///
    /// <paramref name="visible"/> is asked only for lights that cast shadows and only when they actually reach
    /// the point, because a terrain ray-march is by far the most expensive thing in a bake and most points are
    /// out of range of most lights.
    /// </summary>
    public (float R, float G, float B) Illuminate(
        float wx, float wy, float wz, Func<PointLight, bool>? visible = null)
    {
        float r = 0f, g = 0f, b = 0f;
        foreach (var l in Lights)
        {
            float a = l.Attenuation(wx, wy, wz);
            if (a <= 0f) continue;
            if (l.CastsShadows && visible is not null && !visible(l)) continue;
            r += l.ColorR * a;
            g += l.ColorG * a;
            b += l.ColorB * a;
        }
        return (r, g, b);
    }

    /// <summary>The lights that can reach a point at all, nearest first — what the viewport uploads when the
    /// shader has room for only a handful.</summary>
    public List<PointLight> Nearest(float wx, float wy, float wz, int max)
    {
        return Lights
            .Where(l => l.Enabled && l.Intensity > 0f && l.Radius > 0f)
            .Select(l =>
            {
                float dx = wx - l.Position.X, dy = wy - l.Position.Y, dz = wz - l.Position.Z;
                // Distance to the light's REACH, not to its centre: a big lamp far away can still matter more
                // than a small one nearby, and sorting by centre distance would drop it first.
                return (l, d: MathF.Sqrt(dx * dx + dy * dy + dz * dz) - l.Radius);
            })
            .OrderBy(x => x.d)
            .Take(max)
            .Select(x => x.l)
            .ToList();
    }

    /// <summary>
    /// A human-readable dump, so a rig can be pasted into a forum post or diffed. Not the storage format.
    /// </summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Lights.Count} light(s), night {NightAmount:0.00}");
        foreach (var l in Lights)
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  {0,-16} ({1,8:0.0},{2,7:0.0},{3,8:0.0})  r={4,6:0.0}  i={5,4:0.00}  rgb=({6:0.00},{7:0.00},{8:0.00}){9}",
                l.Name, l.Position.X, l.Position.Y, l.Position.Z, l.Radius, l.Intensity,
                l.ColorR, l.ColorG, l.ColorB, l.Enabled ? "" : "  [off]"));
        return sb.ToString();
    }
}
