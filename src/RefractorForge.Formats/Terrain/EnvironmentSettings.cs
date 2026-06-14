using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using RefractorForge.Formats.Geometry;

namespace RefractorForge.Formats.Terrain;

/// <summary>
/// The level's lighting/sky environment, parsed from <c>Init/SkyAndSun.con</c> (plus a couple of
/// <c>Terrain.con</c> shadow settings). The piece the renderer needs most is the sun light direction,
/// which drives both terrain/object shading and the water specular highlight. The skybox itself is a
/// StandardMesh (<see cref="SkyBoxMesh"/>, e.g. <c>Sky_OI_m1</c>) with cubemap-style env textures —
/// recorded here for a future skybox pass; for now the renderer draws a gradient sky.
/// </summary>
public sealed class EnvironmentSettings
{
    /// <summary>Sun/key-light direction (points toward the sun), as written in the .con. Not normalized.</summary>
    public Vec3 SunDirection { get; set; } = new(-0.5f, 0.8f, -0.35f);
    public float SkyRotationAngle { get; set; }
    /// <summary>Ambient/shadow colour 0..255 per channel (Terrain.ShadowAmbient); default mid-grey.</summary>
    public Vec3 ShadowAmbient { get; set; } = new(80f, 80f, 80f);
    /// <summary>Skybox StandardMesh name from SkyAndSun.con (GeometryTemplate.file), if present.</summary>
    public string? SkyBoxMesh { get; set; }

    // ---- Animated clouds (Refractor's dormant Cloud system: a UV-scrolling cloud layer above the sky). Parsed from
    // and re-emitted into SkyAndSun.con AFTER Sky.initSky. The cloud StandardMesh ("cloud") was stripped from public
    // game files, so a level needs one supplied for clouds to show in-game; the EDITOR renders its own cloud layer. ----
    public List<CloudLayer> Clouds { get; } = new();
    public string CloudMeshFile { get; set; } = "cloud";   // GeometryTemplate.file for the shared "Cloud" StandardMesh
    public float CloudOfsHeight { get; set; } = 2500f;     // Sky.changeOfsCloudHeight
    public float CloudOfsDist { get; set; } = 333f;        // Sky.changeOfsCloudDist
    public bool CloudFog { get; set; } = false;            // Sky.setCloudFog

    /// <summary>One scrolling cloud layer (a Sky.addCloud block).</summary>
    public sealed class CloudLayer
    {
        public string Name = "cloud_0";
        public float SpeedX = -0.03f, SpeedY = 0.015f;     // Cloud.setSpeed (Vec2 UV scroll velocity)
        public float Height = 3500f;                        // Cloud.setHeight
        public float TexScale = 8f;                         // Cloud.setTexScale
        public string SrcBlend = "BMSourceAlpha";          // Cloud.setSrcBlend
        public string DstBlend = "BMInvSourceAlpha";       // Cloud.setDstBlend
    }

    // Distance fog (from Init.con: renderer.vertexFogEnable / fogColorVec / fogstart / fogend).
    /// <summary>Whether vertex/distance fog is enabled (renderer.vertexFogEnable).</summary>
    public bool FogEnabled { get; set; } = false;
    /// <summary>Fog colour, RGB 0..1 (renderer.fogColorVec).</summary>
    public Vec3 FogColor { get; set; } = new(0.72f, 0.83f, 0.83f);
    /// <summary>World distance where fog begins (renderer.fogstart), metres.</summary>
    public float FogStart { get; set; } = 100f;
    /// <summary>World distance where fog reaches full density (renderer.fogend), metres.</summary>
    public float FogEnd { get; set; } = 450f;
    /// <summary>Game.ViewDistance (far clip-ish), metres; informational.</summary>
    public float ViewDistance { get; set; } = 550f;

    // Water surface look (from Init.con: water.color / water.waterShallowAlpha).
    /// <summary>Water surface colour, RGB 0..1 (water.color).</summary>
    public Vec3 WaterColor { get; set; } = new(0.10f, 0.22f, 0.30f);
    /// <summary>Deep-water colour, RGB 0..1 (water.deepcolor); the submerged-terrain tint. Defaults to a blue.</summary>
    public Vec3 DeepColor { get; set; } = new(0.16f, 0.35f, 0.55f);
    /// <summary>Water surface transparency 0..1 (water.waterShallowAlpha); lower = more see-through.</summary>
    public float WaterAlpha { get; set; } = 0.6f;

    // BF1942 TEXTURED water: two scrolling diffuse layers + a scrolling normal map, tiled + tinted by water.color
    // (Init.con water.texLayer1/2, water.normalMap, water.scroll*/tile*; Terrain.con Water.baseTex). When these resolve
    // to real textures the editor renders scrolling textured water instead of flat colour. BFV maps set only colour/alpha.
    /// <summary>water.texLayer1 — first scrolling diffuse layer texture name (e.g. "texture/water07"). Null = none.</summary>
    public string? WaterTexLayer1 { get; set; }
    /// <summary>water.texLayer2 — second scrolling diffuse layer.</summary>
    public string? WaterTexLayer2 { get; set; }
    /// <summary>water.normalMap — scrolling ripple normal map.</summary>
    public string? WaterNormalMap { get; set; }
    /// <summary>Water.baseTex (Terrain.con) — the base water texture; used if no texLayer1.</summary>
    public string? WaterBaseTex { get; set; }
    public float ScrollDir1X { get; set; } = 1f;   // water.scrollDirection1
    public float ScrollDir1Y { get; set; } = 0f;
    public float ScrollDir2X { get; set; } = 0f;   // water.scrollDirection2
    public float ScrollDir2Y { get; set; } = 1f;
    public float ScrollDirNX { get; set; } = 1f;   // water.scrollDirectionNormalmap
    public float ScrollDirNY { get; set; } = 1f;
    public float ScrollSpeed1 { get; set; } = 0.03f;
    public float ScrollSpeed2 { get; set; } = 0.03f;
    public float ScrollSpeedN { get; set; } = 0.01f;
    public float TileLayer1 { get; set; } = 0.5f;
    public float TileLayer2 { get; set; } = 0.5f;
    public float TileNormal { get; set; } = 0.5f;
    /// <summary>water.specularColor — sun-glint tint on the textured water.</summary>
    public Vec3 WaterSpecularColor { get; set; } = new(0.75f, 0.7f, 0.65f);
    public bool WaterSpecularEnable { get; set; }
    /// <summary>True when the level configured at least one water texture (so the textured path applies).</summary>
    public bool HasWaterTextures => !string.IsNullOrEmpty(WaterTexLayer1) || !string.IsNullOrEmpty(WaterNormalMap) || !string.IsNullOrEmpty(WaterBaseTex);

    public static EnvironmentSettings LoadFolder(string levelDir)
    {
        string? Find(string n) => Directory.EnumerateFiles(levelDir, n, SearchOption.AllDirectories).FirstOrDefault();
        var sky = Find("SkyAndSun.con");
        var terr = Find("Terrain.con");
        var init = Find("Init.con");
        return Parse(sky is null ? null : File.ReadLines(sky),
                     terr is null ? null : File.ReadLines(terr),
                     init is null ? null : File.ReadLines(init));
    }

    public static EnvironmentSettings Parse(IEnumerable<string>? skyAndSunLines, IEnumerable<string>? terrainLines,
                                            IEnumerable<string>? initLines = null)
    {
        var e = new EnvironmentSettings();

        if (skyAndSunLines is not null)
        {
            string curGeo = "";            // current GeometryTemplate.create name (to tell the skybox mesh from the cloud mesh)
            CloudLayer? curCloud = null;   // current Sky.addCloud layer being configured
            foreach (var raw in skyAndSunLines)
            {
                var line = raw.Trim();
                int sp = line.IndexOf(' ');
                var key = (sp < 0 ? line : line[..sp]).ToLowerInvariant();
                var val = sp < 0 ? "" : line[(sp + 1)..].Trim();
                switch (key)
                {
                    case "geometrytemplate.create":
                        // "StandardMesh <name>" -> remember the name so the next .file lands on skybox vs cloud.
                        var ct = val.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        curGeo = ct.Length > 1 ? ct[1] : (ct.Length > 0 ? ct[0] : "");
                        break;
                    case "geometrytemplate.file":
                        if (curGeo.Equals("Cloud", StringComparison.OrdinalIgnoreCase)) e.CloudMeshFile = val;
                        else e.SkyBoxMesh = val;   // in SkyAndSun.con this is the skybox mesh
                        break;
                    case "sky.sunlightdirectionvec": if (TryVec(val, out var d)) e.SunDirection = d; break;
                    case "sky.setrotangle": if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)) e.SkyRotationAngle = a; break;
                    case "sky.addcloud": curCloud = new CloudLayer { Name = $"cloud_{e.Clouds.Count}" }; e.Clouds.Add(curCloud); break;
                    case "cloud.setname": if (curCloud is not null) curCloud.Name = val; break;
                    case "cloud.setspeed":
                        if (curCloud is not null)
                        {
                            var s2 = val.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                            if (s2.Length >= 2 && float.TryParse(s2[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var sx)
                                               && float.TryParse(s2[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var sy))
                            { curCloud.SpeedX = sx; curCloud.SpeedY = sy; }
                        }
                        break;
                    case "cloud.setheight": if (curCloud is not null && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var ch)) curCloud.Height = ch; break;
                    case "cloud.settexscale": if (curCloud is not null && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var ts)) curCloud.TexScale = ts; break;
                    case "cloud.setsrcblend": if (curCloud is not null) curCloud.SrcBlend = val; break;
                    case "cloud.setdstblend": if (curCloud is not null) curCloud.DstBlend = val; break;
                    case "sky.changeofscloudheight": if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var coh)) e.CloudOfsHeight = coh; break;
                    case "sky.changeofsclouddist": if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var cod)) e.CloudOfsDist = cod; break;
                    case "sky.setcloudfog": e.CloudFog = val.StartsWith("1"); break;
                }
            }
        }

        if (terrainLines is not null)
            foreach (var raw in terrainLines)
            {
                var line = raw.Trim();
                int sp = line.IndexOf(' ');
                if (sp < 0) continue;
                var tk = line[..sp].ToLowerInvariant();
                var tv = line[(sp + 1)..].Trim();
                if (tk == "terrain.shadowambient" && TryVec(tv, out var amb)) e.ShadowAmbient = amb;
                else if (tk == "water.basetex") e.WaterBaseTex = tv;   // Terrain.con: base water texture
            }

        if (initLines is not null)
            foreach (var raw in initLines)
            {
                var line = raw.Trim();
                int sp = line.IndexOf(' ');
                if (sp < 0) continue;
                var key = line[..sp].ToLowerInvariant();
                var val = line[(sp + 1)..].Trim();
                switch (key)
                {
                    case "renderer.vertexfogenable": e.FogEnabled = val.StartsWith("1"); break;
                    case "renderer.fogcolorvec": if (TryVec(val, out var fc)) e.FogColor = fc; break;
                    case "renderer.fogstart": if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var fs)) e.FogStart = fs; break;
                    case "renderer.fogend": if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var fe)) e.FogEnd = fe; break;
                    case "game.viewdistance":
                    case "game.setviewdistance": if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var vd)) e.ViewDistance = vd; break;
                    case "water.color": if (TryVec(val, out var wcl)) e.WaterColor = wcl; break;
                    case "water.deepcolor": if (TryVec(val, out var wdc)) e.DeepColor = wdc; break;
                    case "water.watershallowalpha": if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var wal)) e.WaterAlpha = Math.Clamp(wal, 0.08f, 1f); break;
                    // BF1942 textured-water layers + scroll/tile/specular.
                    case "water.texlayer1": e.WaterTexLayer1 = val; break;
                    case "water.texlayer2": e.WaterTexLayer2 = val; break;
                    case "water.normalmap": e.WaterNormalMap = val; break;
                    case "water.scrolldirection1": if (TryVec2(val, out var s1x, out var s1y)) { e.ScrollDir1X = s1x; e.ScrollDir1Y = s1y; } break;
                    case "water.scrolldirection2": if (TryVec2(val, out var s2x, out var s2y)) { e.ScrollDir2X = s2x; e.ScrollDir2Y = s2y; } break;
                    case "water.scrolldirectionnormalmap": if (TryVec2(val, out var snx, out var sny)) { e.ScrollDirNX = snx; e.ScrollDirNY = sny; } break;
                    case "water.scrolllayer1": if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var sl1)) e.ScrollSpeed1 = sl1; break;
                    case "water.scrolllayer2": if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var sl2)) e.ScrollSpeed2 = sl2; break;
                    case "water.scrollnormalmap": if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var sln)) e.ScrollSpeedN = sln; break;
                    case "water.tilelayer1": if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var tl1)) e.TileLayer1 = tl1; break;
                    case "water.tilelayer2": if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var tl2)) e.TileLayer2 = tl2; break;
                    case "water.tilenormalmap": if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var tln)) e.TileNormal = tln; break;
                    case "water.specularcolor": if (TryVec(val, out var wsc)) e.WaterSpecularColor = wsc; break;
                    case "water.specularenable": e.WaterSpecularEnable = val.StartsWith("1"); break;
                }
            }

        return e;
    }

    private static string F(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);
    private static string V(Vec3 v) => $"{F(v.X)}/{F(v.Y)}/{F(v.Z)}";

    /// <summary>The <c>Terrain.ShadowAmbient</c> (+ companion) lines that belong in <c>Init/Terrain.con</c>.
    /// Inverse of the terrain-line parsing in <see cref="Parse"/>.</summary>
    public IEnumerable<string> ToTerrainShadowLines()
    {
        yield return $"Terrain.ShadowAmbient {V(ShadowAmbient)}";
        yield return "Terrain.ShadowBorderFadeTime 0.075";
        yield return "Terrain.ShadowSamplingCullY 3";
    }

    /// <summary>Render a fresh <c>Init/SkyAndSun.con</c> (skybox mesh + sun direction). Inverse of the
    /// SkyAndSun parsing in <see cref="Parse"/>; round-trips SunDirection / SkyRotationAngle / SkyBoxMesh.</summary>
    public IEnumerable<string> ToSkyAndSunConLines()
    {
        yield return "TextureManager.mipmaps 0";
        yield return "";
        yield return "rem ************************";
        yield return "rem *** Sky ***";
        yield return "rem ************************";
        yield return "GeometryTemplate.create StandardMesh SkyBox";
        yield return $"GeometryTemplate.file {(string.IsNullOrEmpty(SkyBoxMesh) ? "Sky_OI_m1" : SkyBoxMesh)}";
        yield return "Sky.initSky";
        yield return "";
        yield return "TextureManager.mipmaps 1";
        yield return $"Sky.setRotAngle {F(SkyRotationAngle)}";
        yield return $"sky.sunLightDirectionVec {V(SunDirection)}";
        foreach (var l in CloudConLines()) yield return l;
    }

    /// <summary>The Cloud-system block (after Sky.initSky): the shared cloud StandardMesh + per-layer Sky.addCloud
    /// configs + the global cloud offsets. Empty when there are no cloud layers.</summary>
    public IEnumerable<string> CloudConLines()
    {
        if (Clouds.Count == 0) yield break;
        yield return "";
        yield return "rem *** Clouds (RefractorForge) - needs a 'cloud' StandardMesh in the level to show in-game ***";
        yield return "GeometryTemplate.create StandardMesh Cloud";
        yield return $"GeometryTemplate.file {(string.IsNullOrEmpty(CloudMeshFile) ? "cloud" : CloudMeshFile)}";
        yield return $"Sky.changeOfsCloudHeight {F(CloudOfsHeight)}";
        yield return $"Sky.changeOfsCloudDist {F(CloudOfsDist)}";
        yield return $"Sky.setCloudFog {(CloudFog ? 1 : 0)}";
        foreach (var c in Clouds)
        {
            yield return "Sky.addCloud";
            yield return $"Cloud.setName {c.Name}";
            yield return $"Cloud.setSrcBlend {c.SrcBlend}";
            yield return $"Cloud.setDstBlend {c.DstBlend}";
            yield return $"Cloud.setTexScale {F(c.TexScale)}";
            yield return $"Cloud.setSpeed {F(c.SpeedX)} {F(c.SpeedY)}";
            yield return $"Cloud.setHeight {F(c.Height)}";
        }
    }

    /// <summary>True if a SkyAndSun.con line is part of a Cloud-system block (so a patch can strip the old block
    /// before re-emitting). The cloud GeometryTemplate create/file pair is matched via the <paramref name="dropGeoFile"/>
    /// state carried by the caller.</summary>
    private static bool IsCloudLine(string trimmed, ref bool dropGeoFile)
    {
        var low = trimmed.ToLowerInvariant();
        if (dropGeoFile && low.StartsWith("geometrytemplate.file")) { dropGeoFile = false; return true; }
        if (low.StartsWith("geometrytemplate.create") && low.Contains(" cloud")) { dropGeoFile = true; return true; }
        return low.StartsWith("sky.addcloud") || low.StartsWith("cloud.") || low.StartsWith("sky.changeofscloud")
            || low.StartsWith("sky.setcloudfog")
            || (low.StartsWith("rem") && low.Contains("clouds (refractorforge)"));
    }

    /// <summary>Patch an existing SkyAndSun.con: strip any current Cloud block, preserve everything else (skybox,
    /// sun, etc.), and append the fresh cloud block at the end (after Sky.initSky). Inverse of the cloud parsing.</summary>
    public List<string> PatchSkyAndSunConLines(IEnumerable<string> existing)
    {
        var outLines = new List<string>();
        bool dropGeoFile = false;
        foreach (var raw in existing)
        {
            var t = raw.Trim();
            if (IsCloudLine(t, ref dropGeoFile)) continue;
            outLines.Add(raw);
        }
        while (outLines.Count > 0 && outLines[^1].Trim().Length == 0) outLines.RemoveAt(outLines.Count - 1);   // trim trailing blanks
        outLines.AddRange(CloudConLines());
        return outLines;
    }

    /// <summary>Render a minimal, editor- and engine-loadable <c>Init.con</c>: rendering/fog settings,
    /// the SkyAndSun + Terrain runs, and a basic water block. Inverse of the Init-line parsing in
    /// <see cref="Parse"/> for the fog/view-distance fields. Full MP gameplay (flags/kits/spawns) is
    /// deliberately out of scope for a blank map.</summary>
    public IEnumerable<string> ToInitConLines()
    {
        yield return "rem";
        yield return "rem **** level-specific rendering settings ****";
        yield return "rem";
        yield return "renderer.diffuseColor .975/1/.95";
        yield return "renderer.ambientColor .1/.1/.1";
        yield return "renderer.specularColor .9/.9/.7";
        yield return "";
        yield return $"Game.ViewDistance {F(ViewDistance)}";
        yield return "";
        yield return $"renderer.vertexFogEnable {(FogEnabled ? 1 : 0)}";
        yield return $"renderer.fogColorVec {V(FogColor)}";
        yield return $"renderer.fogstart {F(FogStart)}";
        yield return $"renderer.fogend {F(FogEnd)}";
        yield return "";
        yield return "if v_arg1 == host";
        yield return "Game.spawnPlayers 1";
        yield return "endIf";
        yield return "";
        yield return "run Init/SkyAndSun";
        yield return "run Init/Terrain";
        yield return "";
        yield return "water.shallowcolor .281/.266/.205";
        yield return "water.color .281/.266/.205";
        yield return "water.deepcolor .281/.266/.205";
        yield return "water.waterAlphaDepth 20";
        yield return "water.waterColorDepth 20";
        yield return "water.waterShallowAlpha .25";
    }

    /// <summary>Parse an "x/y/z" triple (the .con vector form).</summary>
    private static bool TryVec2(string s, out float x, out float y)
    {
        x = y = 0f;
        var parts = s.Split('/');
        if (parts.Length < 2) return false;
        return float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
             & float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y);
    }

    private static bool TryVec(string s, out Vec3 v)
    {
        v = default;
        var parts = s.Split('/');
        if (parts.Length < 3) return false;
        if (float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
        { v = new Vec3(x, y, z); return true; }
        return false;
    }
}
