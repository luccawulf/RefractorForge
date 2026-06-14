using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

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

    public static FoliagePalette Parse(string xml)
    {
        // Some shipped .wst files have a stray leading space/BOM before the XML declaration.
        string trimmed = xml.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        var doc = XDocument.Parse(trimmed);
        var root = doc.Root ?? throw new InvalidDataException("empty .wst");
        // root = <WRAPPER_TREE>; its single child is <underGrowth> or <overGrowth>.
        var growth = root.Elements().FirstOrDefault()
                     ?? throw new InvalidDataException(".wst has no growth element");
        bool isOver = growth.Name.LocalName.IndexOf("over", StringComparison.OrdinalIgnoreCase) >= 0;

        var slots = new List<FoliageMaterialSlot>();
        var materials = FirstLocal(growth, "materials");
        if (materials is not null)
        {
            foreach (var mat in materials.Elements())
            {
                var slot = new FoliageMaterialSlot { Name = mat.Name.LocalName };
                var types = FirstLocal(mat, "types");
                if (types is not null)
                    foreach (var t in types.Elements())
                        slot.Types.Add(new FoliageType(
                            AttrStr(t, "geometryName") ?? t.Name.LocalName,
                            AttrFloat(t, "probability", 0f),
                            AttrFloat(t, "normalScale", 1f),
                            AttrFloat(t, "minRadiusDistToEquals", 0f),
                            AttrFloat(t, "minRadiusDistToOthers", 0f),
                            AttrStr(t, "scale") ?? ""));
                slots.Add(slot);
            }
        }

        return new FoliagePalette
        {
            IsOver = isOver,
            MaterialMapSideSize = AttrInt(growth, "materialMapSideSize", 0),
            ViewDistance = AttrFloat(growth, "viewdistance", AttrFloat(growth, "viewDistance", 0f)),
            ImportSceneObjects = string.Equals(AttrStr(growth, "importSceneObjects"), "true", StringComparison.OrdinalIgnoreCase),
            Materials = slots,
            RawXml = xml,
        };
    }

    private static XElement? FirstLocal(XElement parent, string local) =>
        parent.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, local, StringComparison.OrdinalIgnoreCase));

    private static string? AttrStr(XElement e, string name) =>
        e.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value?.Trim();

    private static int AttrInt(XElement e, string name, int def) =>
        int.TryParse(AttrStr(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;

    private static float AttrFloat(XElement e, string name, float def) =>
        float.TryParse(AttrStr(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;
}
