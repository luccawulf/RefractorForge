using System;
using System.Globalization;
using System.Text;
using RefractorForge.Formats.Geometry;

namespace RefractorForge.Formats.Con;

/// <summary>The kind of map-wide weather particle effect to generate. DustStorm is a ground-hugging, sideways-drifting
/// dust sheet (modeled on BFV's stock e_dustWind), distinct from Dust (gently-falling motes).</summary>
public enum WeatherType { Snow, Rain, Dust, DustStorm }

/// <summary>
/// Generates a self-contained Refractor weather effect — a particle <c>EffectBundle</c> (→ <c>Emitter</c> →
/// <c>SpriteParticle</c>) plus its particle texture — for BF1942 / BFVietnam. Weather in Refractor is NOT a
/// dedicated subsystem; rain/snow/dust are the standard particle system. The bundle is instanced once (high over
/// the map, with a wide uniform spawn box and a large LOD distance) so it blankets the playable area. The engine
/// crashes above ~5000 on-screen sprites, so the emitter intensity is budgeted (intensity × avg lifetime) under that.
///
/// The generated .con and texture are dropped into the level's <c>Effects/</c> folder; the level's Init runs the
/// .con (so the templates exist) and <c>StaticObjects.con</c> instances the bundle. NOTE: in-game behaviour can't
/// be validated from the editor — treat generated weather as "test in-game" (mirrors the experimental .obj-collision
/// export). All values follow the documented MDT property names.
/// </summary>
public static class WeatherEffect
{
    public const string ConFileName = "RF_Weather.con";

    private static string Tag(WeatherType t) => t switch { WeatherType.Snow => "Snow", WeatherType.Rain => "Rain", WeatherType.DustStorm => "DustStorm", _ => "Dust" };
    public static string TextureName(WeatherType t) => "e_RF_" + Tag(t);
    public static string BundleName(WeatherType t) => "e_RF_Weather" + Tag(t);

    private static string F(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>~average particle lifetime (s) for the sprite-budget check, given the type's TTL range.</summary>
    private static (float min, float max) Ttl(WeatherType t) => t switch
    {
        WeatherType.Snow => (10f, 18f),
        WeatherType.Rain => (1.2f, 2.0f),
        WeatherType.DustStorm => (15f, 30f),  // long-lived sheets drifting along the ground (like stock e_dustWind)
        _ => (6f, 12f),                       // dust drifts a while
    };

    /// <summary>Clamp an emitter intensity (particles/sec) so intensity × avg-lifetime stays well under the
    /// engine's ~5000 on-screen sprite crash ceiling (cap at 4000 live sprites).</summary>
    public static int SafeIntensity(WeatherType t, int requested)
    {
        var (mn, mx) = Ttl(t);
        float avgLife = (mn + mx) * 0.5f;
        int cap = (int)(4000f / Math.Max(0.1f, avgLife));
        return Math.Clamp(requested, 1, cap);
    }

    /// <summary>
    /// Build the weather Effects .con text (SpriteParticle + Emitter + EffectBundle). <paramref name="intensity"/>
    /// is particles/sec (auto-clamped under the sprite cap). <paramref name="wind"/> is a horizontal drift speed
    /// (m/s). <paramref name="worldSize"/> sizes the emitter's spawn box so one bundle covers the map.
    /// </summary>
    public static string BuildEffectsCon(WeatherType type, int intensity, float wind, float worldSize, Vec3? instancePos = null)
    {
        var sb = new StringBuilder();
        Header(sb);
        AppendTemplate(sb, type, intensity, wind, worldSize);
        if (instancePos is Vec3 p)
        {
            sb.AppendLine("rem If the weather doesn't appear in-game, move these two lines into StaticObjects.con");
            sb.AppendLine("rem (some load orders only instance Object.create from the static-objects pass).");
            sb.AppendLine($"Object.create {BundleName(type)}");
            sb.AppendLine($"Object.absolutePosition {F(p.X)}/{F(p.Y)}/{F(p.Z)}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Build an Effects .con with the TEMPLATES for several weather types and NO instances — for the
    /// placeable-emitter workflow, where the bundle instances live in StaticObjects.con (the placed objects).</summary>
    public static string BuildTemplatesCon(IEnumerable<WeatherType> types, int intensity, float wind, float worldSize)
    {
        var sb = new StringBuilder();
        Header(sb);
        foreach (var t in types) AppendTemplate(sb, t, intensity, wind, worldSize);
        return sb.ToString();
    }

    private static void Header(StringBuilder sb)
    {
        sb.AppendLine("rem ============================================================");
        sb.AppendLine("rem  RefractorForge generated weather (particle system).");
        sb.AppendLine("rem  EffectBundle -> Emitter -> SpriteParticle. Intensity clamped under the ~5000-sprite limit.");
        sb.AppendLine("rem  TEST IN-GAME: in-game behaviour is not editor-verified.");
        sb.AppendLine("rem ============================================================");
        sb.AppendLine();
    }

    // Emit one weather type's SpriteParticle + Emitter + EffectBundle template blocks (no instance).
    private static void AppendTemplate(StringBuilder sb, WeatherType type, int intensity, float wind, float worldSize)
    {
        intensity = SafeIntensity(type, intensity);
        var (ttlMin, ttlMax) = Ttl(type);
        float half = Math.Max(64f, worldSize * 0.5f);
        float spawnHeight = 60f;       // metres above the emitter origin the particles start (high for falling weather)
        float groundDrift = 0f;        // horizontal ground drift speed (DustStorm) along Dof (matches stock e_dustWind)
        float sizeMin, sizeMax, gravMin, gravMax, drag, xyRatio; string blendSrc, blendDst;
        switch (type)
        {
            case WeatherType.Rain:
                sizeMin = 0.05f; sizeMax = 0.09f; gravMin = 9f; gravMax = 13f; drag = 0.02f; xyRatio = 6f;
                blendSrc = "BM_SRC_ALPHA"; blendDst = "BM_ONE";
                break;
            case WeatherType.Dust:
                sizeMin = 0.06f; sizeMax = 0.14f; gravMin = 0.15f; gravMax = 0.4f; drag = 0.25f; xyRatio = 1f;
                blendSrc = "BM_SRC_ALPHA"; blendDst = "BM_INV_SRC_ALPHA";
                break;
            case WeatherType.DustStorm:
                // Big, near-ground dust sheets blowing sideways along the ground (modeled on BFV's e_dustWind).
                sizeMin = 3f; sizeMax = 6f; gravMin = 0f; gravMax = 0.1f; drag = 0.05f; xyRatio = 1f;
                blendSrc = "BM_SRC_ALPHA"; blendDst = "BM_INV_SRC_ALPHA";
                spawnHeight = 4f; groundDrift = 6f;
                break;
            default: // Snow
                sizeMin = 0.08f; sizeMax = 0.16f; gravMin = 0.5f; gravMax = 1.1f; drag = 0.15f; xyRatio = 1f;
                blendSrc = "BM_SRC_ALPHA"; blendDst = "BM_INV_SRC_ALPHA";
                break;
        }
        string fx = "Fx_RF_" + Tag(type), em = "em_RF_" + Tag(type), bundle = BundleName(type);
        sb.AppendLine($"rem --- {Tag(type)} ---");
        sb.AppendLine($"ObjectTemplate.create SpriteParticle {fx}");
        sb.AppendLine($"ObjectTemplate.texture {TextureName(type)}");
        sb.AppendLine($"ObjectTemplate.setTimeToLive CRD_UNIFORM/{F(ttlMin)}/{F(ttlMax)}/0");
        sb.AppendLine($"ObjectTemplate.setSize CRD_UNIFORM/{F(sizeMin)}/{F(sizeMax)}/0");
        sb.AppendLine($"ObjectTemplate.setXYsizeRatio {F(xyRatio)}");
        sb.AppendLine($"ObjectTemplate.setGravityModifier CRD_UNIFORM/{F(gravMin)}/{F(gravMax)}/0");
        sb.AppendLine($"ObjectTemplate.setDrag {F(drag)}");
        sb.AppendLine($"ObjectTemplate.setSrcBlendMode {blendSrc}");
        sb.AppendLine($"ObjectTemplate.setDestBlendMode {blendDst}");
        // Tint dust types tan (the generated texture is white); snow/rain stay white.
        string colorRgba = (type == WeatherType.Dust || type == WeatherType.DustStorm) ? "0.8/0.72/0.5/1" : "1/1/1/1";
        sb.AppendLine($"ObjectTemplate.setColorRGBA {colorRgba}");
        sb.AppendLine();
        sb.AppendLine($"ObjectTemplate.create Emitter {em}");
        sb.AppendLine($"ObjectTemplate.setLodDistance {F(Math.Max(worldSize, 512f))}");
        sb.AppendLine($"ObjectTemplate.setIntensity CRD_NONE/{intensity}/0/0");
        sb.AppendLine($"ObjectTemplate.setLooping 1");
        sb.AppendLine($"ObjectTemplate.setStartAtCreation 1");
        sb.AppendLine($"ObjectTemplate.setRelativePositionInRight CRD_UNIFORM/{F(-half)}/{F(half)}/0");
        sb.AppendLine($"ObjectTemplate.setRelativePositionInDof CRD_UNIFORM/{F(-half)}/{F(half)}/0");
        sb.AppendLine($"ObjectTemplate.setRelativePositionInUp CRD_NONE/{F(spawnHeight)}/0/0");
        if (groundDrift != 0f)
            sb.AppendLine($"ObjectTemplate.setPositionalSpeedInDof CRD_NONE/{F(groundDrift)}/0/0");   // blow along the ground
        if (Math.Abs(wind) > 0.001f)
            sb.AppendLine($"ObjectTemplate.setPositionalSpeedInRight CRD_NONE/{F(wind)}/0/0");
        sb.AppendLine($"ObjectTemplate.addTemplate {fx}");
        sb.AppendLine();
        sb.AppendLine($"ObjectTemplate.create EffectBundle {bundle}");
        sb.AppendLine($"ObjectTemplate.addTemplate {em}");
        sb.AppendLine();
    }

    /// <summary>Map a placed bundle template name back to its weather type (null if it isn't a weather bundle).</summary>
    public static WeatherType? TypeOfBundle(string template)
    {
        if (string.IsNullOrEmpty(template) || !template.StartsWith("e_RF_Weather", StringComparison.OrdinalIgnoreCase)) return null;
        var tag = template.Substring("e_RF_Weather".Length);
        if (tag.Equals("Snow", StringComparison.OrdinalIgnoreCase)) return WeatherType.Snow;
        if (tag.Equals("Rain", StringComparison.OrdinalIgnoreCase)) return WeatherType.Rain;
        if (tag.Equals("DustStorm", StringComparison.OrdinalIgnoreCase)) return WeatherType.DustStorm;
        if (tag.Equals("Dust", StringComparison.OrdinalIgnoreCase)) return WeatherType.Dust;
        return null;
    }

    /// <summary>The <c>run</c> include line that loads the weather templates (placed in the level's Init so the
    /// templates exist before <c>StaticObjects.con</c> instances the bundle). Path is level-relative.</summary>
    public static string RunInclude() => $"run Effects/{ConFileName}";

    /// <summary>
    /// A procedural particle texture as 32-bit RGBA pixels (caller wraps in a Texture2D + saves as uncompressed DDS).
    /// Snow/Dust = a soft round dot; Rain = a vertical streak. Pure white with a radial/linear alpha falloff so it
    /// blends as a particle (white, so channel order is moot — only alpha carries the shape).
    /// </summary>
    public static byte[] BuildParticleRgba(WeatherType type, int size = 32)
    {
        var px = new byte[size * size * 4];
        float c = (size - 1) * 0.5f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float a;
                if (type == WeatherType.Rain)
                {
                    // Vertical streak: tight in X, soft along Y.
                    float dx = Math.Abs(x - c) / (size * 0.12f);
                    float dy = Math.Abs(y - c) / c;
                    a = Math.Clamp(1f - dx, 0f, 1f) * Math.Clamp(1f - dy * dy, 0f, 1f);
                }
                else
                {
                    // Soft round dot (snow/dust): radial falloff.
                    float d = MathF.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c;
                    a = Math.Clamp(1f - d, 0f, 1f);
                    a *= a;                                  // tighter core, soft edge
                }
                byte av = (byte)Math.Clamp((int)(a * 255f), 0, 255);
                int o = (y * size + x) * 4;
                px[o] = 255; px[o + 1] = 255; px[o + 2] = 255; px[o + 3] = av;   // RGBA, white, alpha = falloff
            }
        return px;
    }

    /// <summary>The bundle instance position for a map: centre of the world, high up (the emitter's own spawn box
    /// spreads particles outward from here). Y is the spawn altitude over the terrain peak estimate.</summary>
    public static Vec3 InstancePosition(float worldSize, float groundHeight)
        => new(worldSize * 0.5f, groundHeight + 80f, worldSize * 0.5f);
}
