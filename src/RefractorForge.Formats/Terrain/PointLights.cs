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

    public PointLight Clone() => (PointLight)MemberwiseClone();

    /// <summary>
    /// How much this light delivers to a point, ignoring occlusion. Zero past the radius, so the bake can skip
    /// whole regions cheaply.
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
        return Intensity * shape * window;
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

    public static string PathFor(string levelDir) => System.IO.Path.Combine(levelDir, FileName);

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
        Directory.CreateDirectory(levelDir);
        File.WriteAllText(p, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
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
