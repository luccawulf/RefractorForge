using System;
using System.IO;
using System.Linq;

namespace RefractorForge.Formats.Terrain;

/// <summary>
/// The two foliage layers of a level: <b>undergrowth</b> (ground sprites / grass) and <b>overgrowth</b>
/// (trees / large vegetation). Each is an 8-bit-per-cell index map — <c>UnderGrowthMap.raw</c> /
/// <c>OverGrowthMap.raw</c> — at the side size declared by its <c>.wst</c> palette, which is frequently a
/// different resolution than the material map (Operation_Irving: undergrowth 1024², overgrowth 512², on a
/// 512 material map). Painted exactly like the material map (a discrete index stamped in a brush radius);
/// the <c>.wst</c> palette is preserved unchanged.
/// </summary>
public sealed class GrowthMaps
{
    public MaterialMap? Under { get; set; }
    public int UnderSide { get; set; }
    public FoliagePalette? UnderPalette { get; set; }

    public MaterialMap? Over { get; set; }
    public int OverSide { get; set; }
    public FoliagePalette? OverPalette { get; set; }

    public bool HasUnder => Under is not null;
    public bool HasOver => Over is not null;
    public bool Any => HasUnder || HasOver;

    public static GrowthMaps Empty => new();

    /// <summary>Load the Growth/ layer from a level folder. Tolerant: any missing file leaves that layer null.</summary>
    public static GrowthMaps LoadFolder(string levelDir)
    {
        var g = new GrowthMaps();
        string? Find(string name) => Directory.EnumerateFiles(levelDir, name, SearchOption.AllDirectories).FirstOrDefault();

        var uw = Find("underGrowth.wst");
        if (uw is not null) { try { g.UnderPalette = FoliagePalette.Parse(File.ReadAllText(uw)); } catch { /* keep null */ } }
        var ow = Find("overGrowth.wst");
        if (ow is not null) { try { g.OverPalette = FoliagePalette.Parse(File.ReadAllText(ow)); } catch { /* keep null */ } }

        var um = Find("UnderGrowthMap.raw");
        if (um is not null) (g.Under, g.UnderSide) = LoadMap(File.ReadAllBytes(um), g.UnderPalette?.MaterialMapSideSize ?? 0);
        var om = Find("OverGrowthMap.raw");
        if (om is not null) (g.Over, g.OverSide) = LoadMap(File.ReadAllBytes(om), g.OverPalette?.MaterialMapSideSize ?? 0);
        return g;
    }

    /// <summary>Build a square index map from raw bytes, using the palette side if it fits, else inferring it.</summary>
    public static (MaterialMap?, int) LoadMap(byte[] bytes, int paletteSide)
    {
        int side = paletteSide;
        if (side <= 0 || (long)side * side > bytes.Length) side = InferSide(bytes.Length);
        if (side <= 0) return (null, 0);
        return (MaterialMap.FromBytes(bytes, side, side), side);
    }

    /// <summary>Square side from a raw byte count (maps are side×side, 1 byte/cell); 0 if not a perfect square.</summary>
    public static int InferSide(int byteCount)
    {
        if (byteCount <= 0) return 0;
        int s = (int)Math.Round(Math.Sqrt(byteCount));
        return (long)s * s == byteCount ? s : 0;
    }
}
