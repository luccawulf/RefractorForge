using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace RefractorForge.Formats.Terrain;

/// <summary>
/// One foliage entry inside a .wst growth palette: the sprite/geometry scattered for a given material,
/// with the placement parameters the engine uses when generating the actual instances at load.
/// </summary>
public sealed record FoliageType(
    string GeometryName,
    float Probability,
    float NormalScale,
    float MinRadiusToEquals,
    float MinRadiusToOthers,
    string Scale);

/// <summary>One material slot in a growth palette (e.g. "dryGrass") and the foliage types it grows.</summary>
public sealed class FoliageMaterialSlot
{
    public required string Name;
    public List<FoliageType> Types { get; } = new();
}

/// <summary>
/// Parsed BF1942/BFV growth palette (<c>underGrowth.wst</c> / <c>overGrowth.wst</c>). It declares, per
/// terrain material, which foliage geometries get scattered and how densely. The painted index maps
/// (<c>UnderGrowthMap.raw</c> / <c>OverGrowthMap.raw</c>) select per cell from these per-material lists;
/// the palette itself is never changed by painting, so <see cref="RawXml"/> is preserved verbatim and
/// written back unchanged on save.
/// </summary>
public sealed class FoliagePalette
{
    public bool IsOver { get; init; }
    public int MaterialMapSideSize { get; init; }
    public float ViewDistance { get; init; }
    public bool ImportSceneObjects { get; init; }
    public IReadOnlyList<FoliageMaterialSlot> Materials { get; init; } = Array.Empty<FoliageMaterialSlot>();

    /// <summary>The original .wst text, preserved byte-for-byte so it can be written back unchanged.</summary>
    public string RawXml { get; init; } = "";

    /// <summary>Distinct foliage geometry names across every material slot, in first-seen order (for UI).</summary>
    public IReadOnlyList<string> DistinctGeometries
    {
        get
        {
            var seen = new List<string>();
            foreach (var m in Materials)
                foreach (var t in m.Types)
                    if (!seen.Contains(t.GeometryName)) seen.Add(t.GeometryName);
            return seen;
        }
    }

    public int TypeCount => Materials.Sum(m => m.Types.Count);

    /// <summary>The engine's terrain materials, in the order a growth map's cell values index them. Verified against
    /// every retail BFVietnam level: 79 of 82 list exactly these 16 names in exactly this order.</summary>
    public static readonly string[] MaterialNames =
    {
        "default", "water", "dryGrass", "juicyGrass", "dryDirt", "wetDirt", "mud", "deathMaterial",
        "gravel", "muddyWater", "drySand", "wetSand", "rock", "sandRoad", "dirtRoad", "pavelRoad",
    };

    /// <summary>Which slot a growth-map cell value selects. Matched by NAME rather than by position, because
    /// Khe_Sahn and Lang_Vei duplicate their <c>dryDirt</c> block - a 17th slot that shifts every material after it,
    /// so counting down the list would grow the wrong species from index 6 on. Falls back to position for a palette
    /// whose names are not the engine's (Operation_Flaming_Dart renames slot 0).</summary>
    public FoliageMaterialSlot? SlotForIndex(int index)
    {
        if (index < 0) return null;
        if (index < MaterialNames.Length)
            foreach (var m in Materials)
                if (string.Equals(m.Name, MaterialNames[index], StringComparison.OrdinalIgnoreCase)) return m;
        return index < Materials.Count ? Materials[index] : null;
    }

    public static FoliagePalette Parse(string xml)
    {
        // Read with the forgiving scanner rather than an XML parser: four retail levels ship .wst files that are not
        // well-formed, the game loads them, and a palette that failed to load means a map with no trees. See WstNode.
        var doc = WstNode.Parse(xml);
        var root = doc.Children.FirstOrDefault()
                   ?? throw new InvalidDataException("empty .wst");
        // root = <WRAPPER_TREE>; its single child is <underGrowth> or <overGrowth>.
        var growth = root.Children.FirstOrDefault()
                     ?? throw new InvalidDataException(".wst has no growth element");
        bool isOver = growth.Name.IndexOf("over", StringComparison.OrdinalIgnoreCase) >= 0;

        var slots = new List<FoliageMaterialSlot>();
        var materials = growth.Child("materials");
        if (materials is not null)
        {
            foreach (var mat in materials.Children)
            {
                var slot = new FoliageMaterialSlot { Name = mat.Name };
                var types = mat.Child("types");
                if (types is not null)
                    foreach (var t in types.Children)
                        slot.Types.Add(new FoliageType(
                            NonEmpty(t.Attr("geometryName")) ?? t.Name,
                            AttrFloat(t, "probability", 0f),
                            AttrFloat(t, "normalScale", 1f),
                            AttrFloat(t, "minRadiusDistToEquals", 0f),
                            AttrFloat(t, "minRadiusDistToOthers", 0f),
                            t.Attr("scale") ?? ""));
                slots.Add(slot);
            }
        }

        return new FoliagePalette
        {
            IsOver = isOver,
            MaterialMapSideSize = AttrInt(growth, "materialMapSideSize", 0),
            ViewDistance = AttrFloat(growth, "viewDistance", 0f),
            ImportSceneObjects = string.Equals(growth.Attr("importSceneObjects"), "true", StringComparison.OrdinalIgnoreCase),
            Materials = slots,
            RawXml = xml,
        };
    }

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static int AttrInt(WstNode e, string name, int def) =>
        int.TryParse(e.Attr(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;

    private static float AttrFloat(WstNode e, string name, float def) =>
        float.TryParse(e.Attr(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;
}
