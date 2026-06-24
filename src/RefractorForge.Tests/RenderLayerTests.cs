using RefractorForge.Formats.Con;
using RefractorForge.Formats.Terrain;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

public class RenderLayerTests
{
    static bool Near(float a, float b, float eps = 0.01f) => MathF.Abs(a - b) <= eps;

    [Fact]
    public void RoadSpline_catmullrom_arc_monotone_and_uv()
    {
        var cp = new (float X, float Y, float Z, float HalfW)[]
        {
            (0f,   0f, 0f,   4f),
            (100f, 0f, 0f,   4f),
            (100f, 0f, 100f, 4f),
            (0f,   0f, 100f, 4f),
        };

        var pts = RoadSpline.Resample(cp, 2f);
        Assert.True(pts.Count > 4, $"resampled to multiple samples ({pts.Count})");

        float prevArc = 0f;
        foreach (var s in pts)
        {
            Assert.True(s.ArcLen >= prevArc - 1e-3f, $"arc monotonically non-decreasing ({prevArc:0.00} → {s.ArcLen:0.00})");
            prevArc = s.ArcLen;
        }

        float totalLen = pts[^1].ArcLen;
        Assert.True(totalLen > 200f && totalLen < 450f, $"arc length ~300 for 3 sides of a 100×100 square (got {totalLen:0})");

        foreach (var s in pts)
        {
            Assert.True(s.HalfWidth >= 3.5f && s.HalfWidth <= 4.5f, $"half-width stays ~4 (got {s.HalfWidth:0.0})");
            Assert.True(s.X >= -15f && s.X <= 115f && s.Z >= -15f && s.Z <= 115f, $"sample within bounds ({s.X:0},{s.Z:0})");
        }

        bool smoothEnough = true;
        for (int i = 1; i < pts.Count - 1 && smoothEnough; i++)
        {
            float ax = pts[i].X - pts[i - 1].X, az = pts[i].Z - pts[i - 1].Z;
            float bx = pts[i + 1].X - pts[i].X, bz = pts[i + 1].Z - pts[i].Z;
            float dot = ax * bx + az * bz;
            float lenA = MathF.Sqrt(ax * ax + az * az), lenB = MathF.Sqrt(bx * bx + bz * bz);
            if (lenA > 0.01f && lenB > 0.01f && dot / (lenA * lenB) < -0.5f) smoothEnough = false;
        }
        Assert.True(smoothEnough, "no sudden U-turns in Catmull-Rom resampling");

        var straight = new (float X, float Y, float Z, float HalfW)[] { (0f, 0f, 0f, 3f), (0f, 0f, 100f, 3f) };
        var spts = RoadSpline.Resample(straight, 5f);
        Assert.True(spts.Count >= 2, "two-point straight produces samples");
        float sLen = spts[^1].ArcLen;
        Assert.True(Near(sLen, 100f, 5f), $"two-point straight arc ~100 m (got {sLen:0.0})");
        Assert.True(spts.All(s => Near(s.X, 0f, 1f)), "two-point straight stays on X=0 line");
    }

    [Fact]
    public void TextureLayer_bake_blend_noise_determinism()
    {
        int ms = 32, ws = 128;
        var cfg = new TerrainConfig { MaterialSize = ms, WorldSize = ws, YScale = 1f, WaterLevel = 10f };
        var hm = new Heightmap(ms, ms);
        for (int row = 0; row < ms; row++) for (int col = 0; col < ms; col++)
            hm[col, row] = cfg.MetersToRaw(col * (130f / (ms - 1)));

        var texA = new Texture2D(4, 4, Enumerable.Repeat((byte)0, 4 * 4 * 4).Select((b, i) => i % 4 == 1 ? (byte)200 : b).ToArray());
        var texB = new Texture2D(4, 4, Enumerable.Repeat((byte)0, 4 * 4 * 4).Select((b, i) => i % 4 == 0 ? (byte)200 : b).ToArray());
        int atlasW = ms, atlasH = ms;
        var atlas = new Texture2D(atlasW, atlasH, new byte[atlasW * atlasH * 4]);

        var spec = new TextureLayerSpec
        {
            Selector = LayerSelector.Height,
            ThresholdLow = 60f,
            ThresholdHigh = 90f,
            NoiseOn = false,
        };
        TerrainTextureLayer.BakeLayerToAtlas(atlas, hm, cfg, texA, texB, spec);
        bool hasGreen = atlas.Rgba.Where((b, i) => i % 4 == 1).Any(b => b > 100);
        bool hasRed   = atlas.Rgba.Where((b, i) => i % 4 == 0).Any(b => b > 100);
        Assert.True(hasGreen, "low-altitude area samples A (green)");
        Assert.True(hasRed,   "high-altitude area samples B (red)");

        var atlasDet1 = new Texture2D(atlasW, atlasH, new byte[atlasW * atlasH * 4]);
        var atlasDet2 = new Texture2D(atlasW, atlasH, new byte[atlasW * atlasH * 4]);
        var specNoise = new TextureLayerSpec { Selector = LayerSelector.Height, ThresholdLow = 60f, ThresholdHigh = 90f, NoiseOn = true, Seed = 42 };
        TerrainTextureLayer.BakeLayerToAtlas(atlasDet1, hm, cfg, texA, texB, specNoise);
        TerrainTextureLayer.BakeLayerToAtlas(atlasDet2, hm, cfg, texA, texB, specNoise);
        Assert.True(atlasDet1.Rgba.SequenceEqual(atlasDet2.Rgba), "bake is deterministic for fixed Seed");

        var atlasDiff = new Texture2D(atlasW, atlasH, new byte[atlasW * atlasH * 4]);
        var specNoise2 = new TextureLayerSpec { Selector = LayerSelector.Height, ThresholdLow = 60f, ThresholdHigh = 90f, NoiseOn = true, Seed = 99 };
        TerrainTextureLayer.BakeLayerToAtlas(atlasDiff, hm, cfg, texA, texB, specNoise2);
        bool differs = false;
        for (int i = 0; i < atlasDet1.Rgba.Length; i++) if (atlasDet1.Rgba[i] != atlasDiff.Rgba[i]) { differs = true; break; }
        Assert.True(differs, "different Seed produces different bake");

        var atlasNoNoise = new Texture2D(atlasW, atlasH, new byte[atlasW * atlasH * 4]);
        var specNoNoise = new TextureLayerSpec { Selector = LayerSelector.Height, ThresholdLow = 60f, ThresholdHigh = 90f, NoiseOn = false };
        TerrainTextureLayer.BakeLayerToAtlas(atlasNoNoise, hm, cfg, texA, texB, specNoNoise);
        int diffCount = 0;
        for (int i = 0; i < atlasNoNoise.Rgba.Length; i++) if (atlasNoNoise.Rgba[i] != atlasDet1.Rgba[i]) diffCount++;
        Assert.True(diffCount > atlasNoNoise.Rgba.Length / 10, $"noise irregularity changes many bytes (got {diffCount})");

        var specSlope = new TextureLayerSpec { Selector = LayerSelector.Slope, ThresholdLow = 10f, ThresholdHigh = 40f, NoiseOn = false };
        var atlasSlope = new Texture2D(atlasW, atlasH, new byte[atlasW * atlasH * 4]);
        TerrainTextureLayer.BakeLayerToAtlas(atlasSlope, hm, cfg, texA, texB, specSlope);
        bool hasAnySlope = atlasSlope.Rgba.Any(b => b > 0);
        Assert.True(hasAnySlope, "slope selector produces non-empty result");

        float fv = TerrainTextureLayer.Fractal(0.5f, 0.5f, specNoNoise);
        Assert.True(fv >= -1f && fv <= 1f, $"Fractal returns [-1,1] value ({fv:0.000})");

        var preview = TerrainTextureLayer.ProofPreview(32, texA, texB, spec);
        Assert.True(preview.Width == 32 && preview.Height == 32, "ProofPreview has correct size");
        Assert.True(preview.Rgba.Any(b => b > 0), "ProofPreview not all black");
    }

    [Fact]
    public void WeatherGen_effects_con_textures_and_bundle_names()
    {
        Assert.True(WeatherEffect.SafeIntensity(WeatherType.Rain, 10000) < 10000, "SafeIntensity clamps rain (short-lived, large cap ok)");
        Assert.True(WeatherEffect.SafeIntensity(WeatherType.DustStorm, 10000) < 10000, "SafeIntensity clamps DustStorm (long-lived)");
        Assert.True(WeatherEffect.SafeIntensity(WeatherType.Snow, 1) == 1, "SafeIntensity preserves minimum=1");

        string snowCon = WeatherEffect.BuildEffectsCon(WeatherType.Snow, 50, 0.5f, 512f);
        Assert.True(snowCon.Contains("ObjectTemplate.create SpriteParticle") || snowCon.Contains("EffectBundle"), $"effects.con has template (len {snowCon.Length})");
        Assert.True(snowCon.Contains("Snow") || snowCon.Contains("snow"), "snow mentioned in effects.con");

        string rainCon = WeatherEffect.BuildEffectsCon(WeatherType.Rain, 100, 2f, 512f);
        Assert.True(rainCon != snowCon, "rain and snow produce different .con text");
        Assert.True(rainCon.Contains("Rain"), "rain con mentions Rain");

        string tmpl = WeatherEffect.BuildTemplatesCon(new[] { WeatherType.Snow, WeatherType.Rain }, 50, 1f, 512f);
        Assert.True(tmpl.Contains("Snow") && tmpl.Contains("Rain"), "templates.con has both Snow and Rain");

        foreach (var wt in new[] { WeatherType.Snow, WeatherType.Rain, WeatherType.Dust, WeatherType.DustStorm })
        {
            var rgba = WeatherEffect.BuildParticleRgba(wt, 32);
            Assert.True(rgba.Length == 32 * 32 * 4, $"{wt}: particle texture correct byte count");
            Assert.True(rgba.Any(b => b > 0), $"{wt}: particle texture not all black");

            var name = WeatherEffect.BundleName(wt);
            Assert.True(!string.IsNullOrEmpty(name), $"{wt}: BundleName non-empty");
            var parsed = WeatherEffect.TypeOfBundle(name);
            Assert.True(parsed == wt, $"{wt}: TypeOfBundle round-trips BundleName (got {parsed})");

            string texName = WeatherEffect.TextureName(wt);
            Assert.True(!string.IsNullOrEmpty(texName), $"{wt}: TextureName non-empty");
        }

        var snowRgba = WeatherEffect.BuildParticleRgba(WeatherType.Snow, 32);
        var rainRgba = WeatherEffect.BuildParticleRgba(WeatherType.Rain, 32);
        bool snowRainDiffer = false;
        for (int i = 0; i < snowRgba.Length; i++) if (snowRgba[i] != rainRgba[i]) { snowRainDiffer = true; break; }
        Assert.True(snowRainDiffer, "snow and rain particle textures differ");

        Assert.True(WeatherEffect.TypeOfBundle("e_RF_WeatherSnow") == WeatherType.Snow, "TypeOfBundle('e_RF_WeatherSnow') == Snow");
        Assert.True(WeatherEffect.TypeOfBundle("not_weather") is null, "TypeOfBundle('not_weather') == null");
        Assert.True(WeatherEffect.TypeOfBundle("") is null, "TypeOfBundle('') == null");
    }

    [Fact]
    public void CloudCon_emit_parse_patch_and_remove()
    {
        var env = new EnvironmentSettings();
        Assert.True(env.Clouds.Count == 0, "fresh EnvironmentSettings has no clouds");

        var cloud = new EnvironmentSettings.CloudLayer
        {
            Name = "cloud_0",
            SpeedX = -0.03f, SpeedY = 0.015f,
            Height = 3500f,
            TexScale = 8f,
        };
        env.Clouds.Add(cloud);
        env.CloudOfsHeight = 2500f;

        var emitted = env.CloudConLines().ToList();
        Assert.True(emitted.Any(l => l.Contains("Sky.addCloud")), "CloudConLines emits Sky.addCloud");
        Assert.True(emitted.Any(l => l.Contains("cloud.setName") || l.Contains("cloud_0")), "CloudConLines includes cloud name");

        var parsed = EnvironmentSettings.Parse(emitted, null, null);
        Assert.True(parsed.Clouds.Count == 1, $"parsed 1 cloud (got {parsed.Clouds.Count})");
        Assert.True(parsed.Clouds[0].Name == "cloud_0", $"name round-trips (got '{parsed.Clouds[0].Name}')");
        Assert.True(Near(parsed.Clouds[0].SpeedX, -0.03f, 1e-4f), $"SpeedX round-trips ({parsed.Clouds[0].SpeedX})");
        Assert.True(Near(parsed.Clouds[0].Height, 3500f, 1f), $"Height round-trips ({parsed.Clouds[0].Height})");

        string baseLines = "Sky.sunLightDirectionVec -0.5/0.8/-0.35\r\nSky.setRotAngle 0\r\n";
        var patched = env.PatchSkyAndSunConLines(baseLines.Split('\n'));
        Assert.True(patched.Any(l => l.Contains("Sky.sunLightDirectionVec")), "sun direction preserved");
        Assert.True(patched.Any(l => l.Contains("Sky.addCloud")), "cloud layer injected");

        var reparsed = EnvironmentSettings.Parse(patched, null, null);
        Assert.True(reparsed.Clouds.Count == 1, $"reparsed cloud count == 1 ({reparsed.Clouds.Count})");

        var cloud2 = new EnvironmentSettings.CloudLayer { Name = "cloud_1", Height = 5000f, SpeedX = 0.02f, SpeedY = 0f };
        var env2 = new EnvironmentSettings();
        env2.Clouds.Add(cloud2);
        var rePatch = env2.PatchSkyAndSunConLines(patched);
        var reParsed = EnvironmentSettings.Parse(rePatch, null, null);
        Assert.True(reParsed.Clouds.Count == 1, $"idempotent re-patch doesn't duplicate clouds ({reParsed.Clouds.Count})");
        Assert.True(reParsed.Clouds[0].Name == "cloud_1", $"re-patch updates to new cloud (got '{reParsed.Clouds[0].Name}')");
        Assert.True(Near(reParsed.Clouds[0].Height, 5000f, 1f), $"new cloud height set ({reParsed.Clouds[0].Height})");

        var envNoClouds = new EnvironmentSettings();
        var noClouds = envNoClouds.PatchSkyAndSunConLines(rePatch);
        var noParsed = EnvironmentSettings.Parse(noClouds, null, null);
        Assert.True(noParsed.Clouds.Count == 0, $"passing empty cloud list removes clouds ({noParsed.Clouds.Count})");
        Assert.True(noClouds.Any(l => l.Contains("Sky.sunLightDirectionVec")), "sun direction survives cloud removal");
    }
}
