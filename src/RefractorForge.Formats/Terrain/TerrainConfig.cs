using System.Globalization;

namespace RefractorForge.Formats.Terrain;

/// <summary>
/// The authoritative terrain parameters from a level's <c>Init/Terrain.con</c>
/// (the <c>GeometryTemplate.*</c> block). This is where the real map dimensions live.
///
/// Confirmed against the retail map Operation_Irving:
///   materialSize 512, worldSize 2048 (exactly 4:1), yScale 0.35, waterLevel 30,
///   Heightmap.raw = 512x512 16-bit (grid side == materialSize).
/// </summary>
public sealed class TerrainConfig
{
    public int MaterialSize { get; set; } = 256;
    public int WorldSize { get; set; } = 1024;
    public float YScale { get; set; } = 1f;
    public float WaterLevel { get; set; }
    public float SeaFloorLevel { get; set; }
    public float WaveHeight { get; set; } = 1f;

    // BfVietnam 1.2's SECOND water body, for tunnel maps. With drawWaterBelowTerrain on, any point under the
    // terrain surface (or on a hole cell) uses this level instead of waterLevel - Saigon68 floods its sewers to
    // -7.1 m under a 7.5 m river. Null = the level never declares it. WriteWaterBelow marks an edit so the
    // patcher writes (or removes) the two lines.
    public bool DrawWaterBelowTerrain { get; set; }
    public float? WaterBelowLevel { get; set; }
    public bool WriteWaterBelow { get; set; }
    public int TexOffsetX { get; set; }
    public int TexOffsetY { get; set; }
    public int TargetTriCount { get; set; }
    public string? HeightmapRef { get; set; }
    public string? MaterialMapRef { get; set; }

    /// <summary>Horizontal distance in meters between adjacent heightmap samples.</summary>
    public float HorizontalSpacing => (float)WorldSize / MaterialSize;

    /// <summary>
    /// Convert a raw 16-bit sample to height in meters. The 16-bit value behaves as
    /// 8.8 fixed point scaled by yScale: <c>meters = raw * yScale / 256</c>. Validated
    /// on Operation_Irving (range 19.3–89.6 m against a 30 m water level).
    /// </summary>
    public float HeightToMeters(ushort raw) => raw * YScale / 256f;

    public ushort MetersToRaw(float meters) =>
        (ushort)Math.Clamp(MathF.Round(meters * 256f / YScale), 0, ushort.MaxValue);

    public static TerrainConfig Load(string path) => Parse(File.ReadLines(path));

    public static TerrainConfig Parse(IEnumerable<string> lines)
    {
        var t = new TerrainConfig();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            int sp = line.IndexOf(' ');
            if (sp < 0) continue;
            var key = line[..sp].ToLowerInvariant();
            var val = line[(sp + 1)..].Trim();

            switch (key)
            {
                case "geometrytemplate.materialsize":  t.MaterialSize   = I(val); break;
                case "geometrytemplate.worldsize":     t.WorldSize      = I(val); break;
                case "console.worldsize":              t.WorldSize      = I(val); break;
                case "geometrytemplate.yscale":        t.YScale         = F(val); break;
                case "geometrytemplate.waterlevel":    t.WaterLevel     = F(val); break;
                case "geometrytemplate.seafloorlevel": t.SeaFloorLevel  = F(val); break;
                case "geometrytemplate.drawwaterbelowterrain": t.DrawWaterBelowTerrain = val.StartsWith("1"); break;
                case "geometrytemplate.waterbelowlevel": t.WaterBelowLevel = F(val); break;
                case "geometrytemplate.waveheight":    t.WaveHeight     = F(val); break;
                case "geometrytemplate.texoffsetx":    t.TexOffsetX     = I(val); break;
                case "geometrytemplate.texoffsety":    t.TexOffsetY     = I(val); break;
                case "geometrytemplate.targettricount":t.TargetTriCount = I(val); break;
                case "geometrytemplate.file":          t.HeightmapRef   = val;    break;
                case "geometrytemplate.materialmap":   t.MaterialMapRef = val;    break;
            }
        }
        return t;
    }

    private static int I(string s) => int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
    private static float F(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static string W(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>
    /// Render the <c>GeometryTemplate</c> block + terrain object for a fresh <c>Init/Terrain.con</c>,
    /// the inverse of <see cref="Parse"/>. <paramref name="enginePath"/> is the game's VFS base for the
    /// level (e.g. <c>BfVietnam\levels\MyMap</c>); the heightmap/materialmap/texture refs hang off it,
    /// matching retail Operation_Irving. Shadow settings live in the environment, not here — append
    /// <see cref="EnvironmentSettings.ToTerrainShadowLines"/> after these.
    /// </summary>
    /// <summary>
    /// Patch an existing <c>Terrain.con</c>'s editable <c>GeometryTemplate</c> scalar lines (waterLevel,
    /// seaFloorLevel, waveHeight) to this config's current values, preserving EVERY other line verbatim. Used to
    /// save a live water-level edit without rewriting the whole file (no map-mangling). Only lines already present
    /// are replaced — nothing is added.
    /// </summary>
    public IEnumerable<string> PatchConLines(IEnumerable<string> lines)
    {
        var outLines = new List<string>();
        int waterAt = -1; bool sawDraw = false, sawBelow = false;
        foreach (var raw in lines)
        {
            var t = raw.TrimStart();
            int sp = t.IndexOf(' ');
            var key = (sp < 0 ? t : t[..sp]).ToLowerInvariant();
            string indent = raw[..(raw.Length - t.Length)];
            // The tunnel water pair is REWRITTEN as a pair: dropped when the edit switched it off, replaced in place
            // when it is on. Everything else passes through untouched.
            if (WriteWaterBelow && key is "geometrytemplate.drawwaterbelowterrain" or "geometrytemplate.waterbelowlevel")
            {
                if (!DrawWaterBelowTerrain) continue;
                if (key == "geometrytemplate.drawwaterbelowterrain") { sawDraw = true; outLines.Add($"{indent}GeometryTemplate.drawWaterBelowTerrain 1"); }
                else { sawBelow = true; outLines.Add($"{indent}GeometryTemplate.waterBelowLevel {W(WaterBelowLevel ?? WaterLevel)}"); }
                continue;
            }
            outLines.Add(key switch
            {
                "geometrytemplate.waterlevel"    => $"{indent}GeometryTemplate.waterLevel {W(WaterLevel)}",
                "geometrytemplate.seafloorlevel" => $"{indent}GeometryTemplate.seaFloorLevel {W(SeaFloorLevel)}",
                "geometrytemplate.waveheight"    => $"{indent}GeometryTemplate.waveHeight {W(WaveHeight)}",
                _ => raw,
            });
            if (key == "geometrytemplate.waterlevel") waterAt = outLines.Count - 1;
        }
        if (WriteWaterBelow && DrawWaterBelowTerrain)
        {
            // Saigon68's order: the switch just above waterLevel, the level just below it.
            var add = new List<string>();
            if (!sawDraw) add.Add("GeometryTemplate.drawWaterBelowTerrain 1");
            if (!sawBelow) add.Add($"GeometryTemplate.waterBelowLevel {W(WaterBelowLevel ?? WaterLevel)}");
            if (add.Count > 0) outLines.InsertRange(waterAt >= 0 ? waterAt + 1 : outLines.Count, add);
        }
        return outLines;
    }

    public IEnumerable<string> ToTerrainConLines(string enginePath)
    {
        yield return "rem ";
        yield return "rem **** Initialize Terrain *****";
        yield return "rem";
        yield return "GeometryTemplate.create patchTerrain terrainGeometry";
        yield return $@"GeometryTemplate.file {enginePath}\Heightmap";
        yield return $@"GeometryTemplate.materialMap {enginePath}\Materialmap";
        yield return $"GeometryTemplate.materialSize {MaterialSize}";
        yield return $"GeometryTemplate.targetTriCount {(TargetTriCount > 0 ? TargetTriCount : 5000)}";
        yield return $"GeometryTemplate.worldSize {WorldSize}";
        yield return $"GeometryTemplate.yScale {W(YScale)}";
        yield return $@"GeometryTemplate.texBaseName {enginePath}\Textures\Tx";
        yield return $"GeometryTemplate.texOffsetX {TexOffsetX}";
        yield return $"GeometryTemplate.texOffsetY {TexOffsetY}";
        yield return $@"GeometryTemplate.detailTexName {enginePath}\Textures\Detail";
        if (DrawWaterBelowTerrain) yield return "GeometryTemplate.drawWaterBelowTerrain 1";
        yield return $"GeometryTemplate.waterLevel {W(WaterLevel)}";
        if (DrawWaterBelowTerrain) yield return $"GeometryTemplate.waterBelowLevel {W(WaterBelowLevel ?? WaterLevel)}";
        yield return $"GeometryTemplate.seaFloorLevel {W(SeaFloorLevel)}";
        yield return $"GeometryTemplate.waveHeight {W(WaveHeight)}";
        yield return "";
        yield return "";
        yield return "ObjectTemplate.create SimpleObject terrainObject";
        yield return "ObjectTemplate.geometry terrainGeometry";
        yield return "objectTemplate.createNotInGrid 1";
        yield return "";
        yield return "Object.create terrainObject";
        yield return "Object.name track";
        yield return "Object.absolutePosition 0/0/0";
        yield return "Object.rotation 0/0/0";
        yield return "";
        yield return $"Console.worldSize {WorldSize}";
    }
}
