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

    /// <summary>renderer.globalAmbientColor - the scene-wide ambient the engine adds to everything (Init.con).</summary>
    public Vec3 GlobalAmbientColor { get; set; } = new(0.16f, 0.15f, 0.17f);
    /// <summary>renderer.ambientColor - the key light's own ambient term.</summary>
    public Vec3 AmbientColor { get; set; } = new(0.12f, 0.10f, 0.08f);
    /// <summary>renderer.diffuseColor - the key (sun) light colour. This is what tints a level warm or cold.</summary>
    public Vec3 DiffuseColor { get; set; } = new(0.975f, 1f, 0.95f);
    /// <summary>renderer.specularColor - highlight colour on shiny surfaces.</summary>
    public Vec3 SpecularColor { get; set; } = new(0.9f, 0.9f, 0.7f);
    // Which of the four the level actually declared. Only declared keys are written back on a patch save, so a map
    // that deliberately leaves one out - Operation_Irving ships globalAmbientColor commented out - does not silently
    // gain one just because the editor has a default for it. Setting a flag is how the UI says "the user chose this".
    /// <summary><c>renderer.LMambientColor</c>: what the engine ADDS to every lightmapped surface. Retail object
    /// lightmaps go all the way to 0 in shadow (Cedar Falls' means sit at 32-85 of 255), so this is the only thing
    /// keeping a shadowed wall from being black in the game - and it has to be added in the editor the same way.
    /// Cedar Falls .25, Saigon68 .2. Accepts the single-float form as well as a triple.</summary>
    public Vec3 LMAmbientColor { get; set; } = new(0.25f, 0.25f, 0.25f);
    public bool HasLMAmbient { get; set; }

    /// <summary>The colour block of the second, below-terrain water (<c>waterBelowTerrain.*</c>, the console object
    /// BfVietnam registers beside <c>water</c>). Saigon68 copies its surface water's block wholesale, so that is what
    /// an edit writes: the level's own <c>water.*</c> colour lines, mirrored. <c>WriteWaterBelow</c> marks the edit;
    /// <c>WaterBelowEnabled</c> false removes the block.</summary>
    public bool WriteWaterBelow { get; set; }
    public bool WaterBelowEnabled { get; set; }
    public bool HasGlobalAmbient { get; set; }
    public bool HasAmbient { get; set; }
    public bool HasDiffuse { get; set; }
    public bool HasSpecular { get; set; }
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

    /// <summary><c>game.setActiveCombatArea</c>, when the level declares one. Offsets then sizes - see
    /// <see cref="Validation.CombatArea"/> for why the order matters.</summary>
    public Validation.CombatArea? CombatArea { get; set; }
    public bool HasCombatArea => CombatArea is not null;

    // ---- The BfVietnam 1.2 tunnel system, as Init.con declares it ----
    //
    // Operation Cedar Falls is the reference:  Game.isTunnelMap 1 / Game.useBelowGroundCulling 1 /
    // Game.entryPointRadius 3.5 / mapManager.addObjectMap o_tunnelsA TunnelsAMap 886/871/328/327.
    // isTunnelMap switches the system on (Battlecraft wrote it as 0 into every custom map, which is why tunnels
    // "never worked" in them); the object map binds an underground minimap texture (Textures/<MapName>.dds) to
    // a below-ground object template over a world rectangle (x, z, width, height in metres); entryPointRadius
    // is how close a soldier has to be to an entry point to pass through the terrain.

    /// <summary><c>Game.isTunnelMap</c>: the level uses holes in the heightmap and below-ground objects.</summary>
    public bool IsTunnelMap { get; set; }
    /// <summary><c>Game.useBelowGroundCulling</c>: do not draw the surface world while the camera is underground.</summary>
    public bool UseBelowGroundCulling { get; set; }
    /// <summary><c>Game.entryPointRadius</c>, metres. Retail uses 3.5 (Cedar Falls) and 5 (Saigon68).</summary>
    public float EntryPointRadius { get; set; } = 3.5f;
    /// <summary>Every <c>mapManager.addObjectMap</c> line: template, map texture name, world rectangle.</summary>
    public List<ObjectMap> ObjectMaps { get; } = new();
    /// <summary>Set when the tunnel settings were edited, so the patcher rewrites them.</summary>
    public bool WriteTunnel { get; set; }

    public sealed record ObjectMap(string Template, string MapName, float X, float Z, float Width, float Height)
    {
        public string ToConLine() => string.Format(CultureInfo.InvariantCulture,
            "mapManager.addObjectMap {0} {1} {2:0.###}/{3:0.###}/{4:0.###}/{5:0.###}", Template, MapName, X, Z, Width, Height);

        public static ObjectMap? Parse(string val)
        {
            var parts = val.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return null;
            var r = parts[2].Split('/');
            if (r.Length < 4) return null;
            if (!float.TryParse(r[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                !float.TryParse(r[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var z) ||
                !float.TryParse(r[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ||
                !float.TryParse(r[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var h)) return null;
            return new ObjectMap(parts[0], parts[1], x, z, w, h);
        }
    }

    // Water surface look (from Init.con: water.color / water.waterShallowAlpha).
    /// <summary>Water surface colour, RGB 0..1 (water.color).</summary>
    public Vec3 WaterColor { get; set; } = new(0.10f, 0.22f, 0.30f);
    /// <summary>water.shallowColor - what the water looks like where it is SHALLOW, which on a river or a flooded
    /// tunnel is most of what you see. It is a separate setting from the colour, and the editor used not to show it
    /// at all: a level whose shallowColor disagreed with its colour rendered nothing like the viewport, which is how
    /// a brown tunnel came out orange in game. Seeded from the body's colour when the level does not set it.</summary>
    public Vec3 ShallowColor { get; set; } = new(0.10f, 0.22f, 0.30f);
    public bool HasShallowColor { get; set; }
    /// <summary>Deep-water colour, RGB 0..1 (water.deepcolor); the submerged-terrain tint. Defaults to a blue.</summary>
    public Vec3 DeepColor { get; set; } = new(0.16f, 0.35f, 0.55f);
    /// <summary>Water surface transparency 0..1 (water.waterShallowAlpha); lower = more see-through.</summary>
    public float WaterAlpha { get; set; } = 0.6f;

    // The SECOND water body of a tunnel map (waterBelowTerrain.*) has the same properties as the surface and is
    // edited separately: a flooded sewer is not the same colour as the river above it. Saigon68 is the only retail
    // level that ships the block. When a level turns tunnel water on for the first time these are seeded from the
    // surface (what Saigon68 effectively did), then diverge as the author edits them.
    /// <summary>waterBelowTerrain.color.</summary>
    public Vec3 BelowColor { get; set; } = new(0.10f, 0.22f, 0.30f);
    /// <summary>waterBelowTerrain.shallowColor - the same thing for the second body. Saigon68 sets it to its own
    /// value rather than following its colour, so this is a real setting, not a derived one.</summary>
    public Vec3 BelowShallowColor { get; set; } = new(0.10f, 0.22f, 0.30f);
    public bool HasBelowShallowColor { get; set; }
    /// <summary>waterBelowTerrain.deepColor.</summary>
    public Vec3 BelowDeepColor { get; set; } = new(0.16f, 0.35f, 0.55f);
    /// <summary>waterBelowTerrain.waterShallowAlpha.</summary>
    public float BelowAlpha { get; set; } = 0.6f;
    /// <summary>The level's file already declared waterBelowTerrain colours (so they are the author's, not seeds).</summary>
    public bool HasBelowColors { get; set; }
    /// <summary>waterBelowTerrain.waterAlphaDepth / waterColorDepth - how quickly the second body reaches full
    /// opacity and full colour with depth. Saigon68's values, which is the only shipped example.</summary>
    public const float DefaultBelowAlphaDepth = 0.4f;
    public const float DefaultBelowColorDepth = 7.5f;
    public float BelowAlphaDepth { get; set; } = DefaultBelowAlphaDepth;
    public float BelowColorDepth { get; set; } = DefaultBelowColorDepth;

    /// <summary>Start the second body from the surface's look - used the first time tunnel water is switched on.</summary>
    public void SeedBelowWaterFromSurface()
    {
        if (HasBelowColors) return;
        BelowColor = WaterColor; BelowDeepColor = DeepColor; BelowAlpha = WaterAlpha; BelowShallowColor = ShallowColor;
    }

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

        float? surfAlphaDepth = null, surfColorDepth = null;
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
                    case "renderer.globalambientcolor": if (TryVec(val, out var gac)) { e.GlobalAmbientColor = gac; e.HasGlobalAmbient = true; } break;
                    case "renderer.ambientcolor": if (TryVec(val, out var amc)) { e.AmbientColor = amc; e.HasAmbient = true; } break;
                    case "renderer.lmambientcolor":
                        if (TryVec(val, out var lma)) { e.LMAmbientColor = lma; e.HasLMAmbient = true; }
                        else if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var lm1)) { e.LMAmbientColor = new Vec3(lm1, lm1, lm1); e.HasLMAmbient = true; }
                        break;
                    // The second water body. Any of its keys means the level HAS one; the three the editor edits
                    // are also read, so an author's colours come back instead of being re-seeded from the surface.
                    case "waterbelowterrain.color": case "waterbelowterrain.shallowcolor": case "waterbelowterrain.deepcolor":
                    case "waterbelowterrain.wateralphadepth": case "waterbelowterrain.watershallowalpha": case "waterbelowterrain.watercolordepth":
                        e.WaterBelowEnabled = true;
                        if (key == "waterbelowterrain.color" && TryVec(val, out var bcl)) { e.BelowColor = bcl; e.HasBelowColors = true; }
                        else if (key == "waterbelowterrain.shallowcolor" && TryVec(val, out var bsc)) { e.BelowShallowColor = bsc; e.HasBelowShallowColor = true; }
                        else if (key == "waterbelowterrain.deepcolor" && TryVec(val, out var bdc)) { e.BelowDeepColor = bdc; e.HasBelowColors = true; }
                        else if (key == "waterbelowterrain.watershallowalpha" && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var bal)) { e.BelowAlpha = Math.Clamp(bal, 0.08f, 1f); e.HasBelowColors = true; }
                        else if (key == "waterbelowterrain.wateralphadepth" && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var bad)) e.BelowAlphaDepth = bad;
                        else if (key == "waterbelowterrain.watercolordepth" && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var bcd)) e.BelowColorDepth = bcd;
                        break;
                    case "water.wateralphadepth": if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var sad)) surfAlphaDepth = sad; break;
                    case "water.watercolordepth": if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var scd)) surfColorDepth = scd; break;
                    case "renderer.diffusecolor": if (TryVec(val, out var dfc)) { e.DiffuseColor = dfc; e.HasDiffuse = true; } break;
                    case "renderer.specularcolor": if (TryVec(val, out var spc)) { e.SpecularColor = spc; e.HasSpecular = true; } break;
                    case "renderer.vertexfogenable": e.FogEnabled = val.StartsWith("1"); break;
                    case "renderer.fogcolorvec": if (TryVec(val, out var fc)) e.FogColor = fc; break;
                    case "renderer.fogstart": if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var fs)) e.FogStart = fs; break;
                    case "renderer.fogend": if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var fe)) e.FogEnd = fe; break;
                    case "game.viewdistance":
                    case "game.setviewdistance": if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var vd)) e.ViewDistance = vd; break;
                    case "game.setactivecombatarea": if (Validation.CombatArea.TryParse(line, out var caA)) e.CombatArea = caA; break;
                    case "game.istunnelmap": e.IsTunnelMap = val.StartsWith("1"); break;
                    case "game.usebelowgroundculling": e.UseBelowGroundCulling = val.StartsWith("1"); break;
                    case "game.entrypointradius": if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var epr)) e.EntryPointRadius = epr; break;
                    case "mapmanager.addobjectmap": if (ObjectMap.Parse(val) is { } om) e.ObjectMaps.Add(om); break;
                    case "water.color": if (TryVec(val, out var wcl)) e.WaterColor = wcl; break;
                    case "water.deepcolor": if (TryVec(val, out var wdc)) e.DeepColor = wdc; break;
                    case "water.shallowcolor": if (TryVec(val, out var wsh)) { e.ShallowColor = wsh; e.HasShallowColor = true; } break;
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

        // Saigon68 mirrors its surface's shallowColor and depth scales onto its tunnel water exactly, and sets a
        // below colour that differs from its own shallowColor - so neither "identical to the surface" nor "different
        // from its colour" says anything about whether a level meant it. Nothing is inferred here; the editor shows
        // these values instead of guessing at them. A body that names no shallowColor simply takes its own colour.
        if (!e.HasShallowColor) e.ShallowColor = e.WaterColor;
        if (!e.HasBelowShallowColor) e.BelowShallowColor = e.BelowColor;
        _ = surfAlphaDepth; _ = surfColorDepth;

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
        bool wroteSun = false;
        foreach (var raw in existing)
        {
            var t = raw.Trim();
            if (IsCloudLine(t, ref dropGeoFile)) continue;
            // The sun's direction is rewritten in place, keeping the file's own indentation and everything else in it
            // (skybox mesh, rotation, fog...) exactly where the author left it.
            if (WriteSun && t.StartsWith("sky.sunLightDirectionVec", StringComparison.OrdinalIgnoreCase))
            {
                if (wroteSun) continue;                      // a duplicate line would fight the first
                wroteSun = true;
                outLines.Add(raw[..(raw.Length - raw.TrimStart().Length)] + $"sky.sunLightDirectionVec {V(SunDirection)}");
                continue;
            }
            outLines.Add(raw);
        }
        while (outLines.Count > 0 && outLines[^1].Trim().Length == 0) outLines.RemoveAt(outLines.Count - 1);   // trim trailing blanks
        // A level that never declared one still needs the line once the editor has aimed the sun.
        if (WriteSun && !wroteSun) outLines.Add($"sky.sunLightDirectionVec {V(SunDirection)}");
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
        yield return $"renderer.diffuseColor {V(DiffuseColor)}";
        yield return $"renderer.ambientColor {V(AmbientColor)}";
        yield return $"renderer.specularColor {V(SpecularColor)}";
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

    /// <summary>Patch the lighting lines into an existing <c>Init.con</c>, rewriting the keys in place and leaving
    /// every other line untouched. Init.con carries real gameplay (kits, spawns, the run chain), so this rewrites
    /// rather than regenerates. Keys the level never declared are only added once the editor has a value for them,
    /// and go in right after the last existing <c>renderer.</c> line so they keep the file's own grouping.</summary>
    /// <summary>Set by a time-of-day preset: the fog and view-distance lines are written too, so the preset
    /// is what the game shows and not only what the editor shows. Off by default so an ordinary lighting edit
    /// does not start rewriting fog it never touched.</summary>
    public bool WriteFog { get; set; }
    public bool WriteViewDistance { get; set; }
    /// <summary>The surface water's colours were edited, so <c>water.color</c> / <c>deepColor</c> /
    /// <c>waterShallowAlpha</c> go into Init.con. Without this the editor's water colour was a viewport-only
    /// setting that never reached the game.</summary>
    public bool WriteWater { get; set; }
    /// <summary>The sun was aimed in the editor, so <c>sky.sunLightDirectionVec</c> goes into SkyAndSun.con. Without
    /// this the manual sun was a viewport-only setting: the bakes used it, the game did not, and baked shadows then
    /// disagreed with the game's own lighting.</summary>
    public bool WriteSun { get; set; }

    public List<string> PatchInitConLines(IEnumerable<string> existing)
    {
        var wanted = new (string Key, string Line, bool Want)[]
        {
            ("renderer.globalambientcolor", $"renderer.globalAmbientColor {V(GlobalAmbientColor)}", HasGlobalAmbient),
            ("renderer.ambientcolor",       $"renderer.ambientColor {V(AmbientColor)}",             HasAmbient),
            ("renderer.diffusecolor",       $"renderer.diffuseColor {V(DiffuseColor)}",             HasDiffuse),
            ("renderer.specularcolor",      $"renderer.specularColor {V(SpecularColor)}",           HasSpecular),
            ("renderer.vertexfogenable",    $"renderer.vertexFogEnable {(FogEnabled ? 1 : 0)}",     WriteFog),
            ("renderer.fogcolorvec",        $"renderer.fogColorVec {V(FogColor)}",                  WriteFog),
            ("renderer.fogstart",           $"renderer.fogstart {F(FogStart)}",                     WriteFog),
            ("renderer.fogend",             $"renderer.fogend {F(FogEnd)}",                         WriteFog),
            ("game.setviewdistance",        $"Game.setViewDistance {F(ViewDistance)}",              WriteViewDistance),
            ("water.color",                 $"water.color {V(WaterColor)}",                         WriteWater),
            ("water.shallowcolor",          $"water.shallowColor {V(ShallowColor)}",                WriteWater),
            ("water.deepcolor",             $"water.deepColor {V(DeepColor)}",                      WriteWater),
            ("water.watershallowalpha",     $"water.waterShallowAlpha {F(WaterAlpha)}",             WriteWater),
            ("game.setactivecombatarea",    CombatArea?.ToConLine() ?? "",                          HasCombatArea),
            ("game.istunnelmap",            $"Game.isTunnelMap {(IsTunnelMap ? 1 : 0)}",             WriteTunnel),
            ("game.usebelowgroundculling",  $"Game.useBelowGroundCulling {(UseBelowGroundCulling ? 1 : 0)}", WriteTunnel),
            ("game.entrypointradius",       $"Game.entryPointRadius {F(EntryPointRadius)}",         WriteTunnel && IsTunnelMap),
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var outLines = new List<string>();
        int lastRenderer = -1, tunnelAt;
        foreach (var rawLine in existing)
        {
            var t = rawLine.Trim();
            int sp = t.IndexOf(' ');
            var key = (sp < 0 ? t : t[..sp]).ToLowerInvariant();
            // The object maps are rewritten as a set (below), so the old ones go - including the pair of dummy
            // lines Battlecraft put in every map, which bind textures that do not exist.
            if (WriteTunnel && key == "mapmanager.addobjectmap") continue;
            if (WriteTunnel && !IsTunnelMap && key == "game.entrypointradius") continue;   // meaningless with the system off
            int w = Array.FindIndex(wanted, x => x.Key == key);
            // A rem'd line reads as key "rem", so a commented-out setting is left exactly as it is.
            if (w >= 0 && wanted[w].Want) { outLines.Add(wanted[w].Line); seen.Add(key); }
            else outLines.Add(rawLine);
            if (key.StartsWith("renderer.", StringComparison.Ordinal)) lastRenderer = outLines.Count - 1;
        }
        var missing = wanted.Where(x => x.Want && !seen.Contains(x.Key)).Select(x => x.Line).ToList();
        if (missing.Count > 0) outLines.InsertRange(lastRenderer >= 0 ? lastRenderer + 1 : 0, missing);
        if (WriteTunnel && IsTunnelMap && ObjectMaps.Count > 0)
        {
            // Directly after the isTunnelMap line, wherever that ended up, so the block reads as one setting.
            tunnelAt = outLines.FindIndex(l => l.TrimStart().StartsWith("Game.isTunnelMap", StringComparison.OrdinalIgnoreCase));
            outLines.InsertRange(tunnelAt >= 0 ? tunnelAt + 1 : outLines.Count, ObjectMaps.Select(m => m.ToConLine()));
        }
        if (WriteWaterBelow)
        {
            outLines.RemoveAll(l => l.TrimStart().StartsWith("waterBelowTerrain.", StringComparison.OrdinalIgnoreCase));
            if (WaterBelowEnabled)
            {
                // The second body's OWN complete block. It must not inherit anything from the surface: mirroring the
                // surface's shallowColor and depth scales let a bright surface colour override the tunnel water
                // entirely - a brown, half-transparent sewer came out opaque orange - which is the opposite of the
                // two bodies being edited apart. Only colour keys belong here; a BF1942 level's texLayer / scroll
                // lines have no meaning on the second body.
                var block = new List<string>
                {
                    $"waterBelowTerrain.color {V(BelowColor)}",
                    $"waterBelowTerrain.shallowColor {V(BelowShallowColor)}",
                    $"waterBelowTerrain.deepColor {V(BelowDeepColor)}",
                    $"waterBelowTerrain.waterShallowAlpha {F(BelowAlpha)}",
                    $"waterBelowTerrain.waterAlphaDepth {F(BelowAlphaDepth)}",
                    $"waterBelowTerrain.waterColorDepth {F(BelowColorDepth)}",
                };
                int lastWater = -1;
                for (int i = 0; i < outLines.Count; i++)
                    if (outLines[i].TrimStart().StartsWith("water.", StringComparison.OrdinalIgnoreCase)) lastWater = i;
                outLines.InsertRange(lastWater >= 0 ? lastWater + 1 : outLines.Count, block);
            }
        }
        return outLines;
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
