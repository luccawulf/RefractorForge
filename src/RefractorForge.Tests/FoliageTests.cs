using RefractorForge.Formats.Con;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;
using Xunit;

namespace RefractorForge.Tests;

public class FoliageTests
{
    static string MakeWst(float density = 2.0f) =>
        $"""
        <WRAPPER_TREE version="1">
        <overGrowth viewdistance="200" materialMapSideSize="16">
        <materials>
        <dryGrass>
        <types>
        <type geometryName="foliage/oak_m1.staticmesh" probability="0.6" normalScale="1" minRadiusDistToEquals="0" minRadiusDistToOthers="0" scale="0.8 1.4"/>
        <type geometryName="foliage/fern_m1.staticmesh" probability="0.4" normalScale="1" minRadiusDistToEquals="0" minRadiusDistToOthers="0" scale="1.0 1.2"/>
        </types>
        </dryGrass>
        <water>
        <types/>
        </water>
        </materials>
        </overGrowth>
        </WRAPPER_TREE>
        """;

    [Fact]
    public void FoliagePalette_parse_and_wst_xml_roundtrip()
    {
        var pal = FoliagePalette.Parse(MakeWst());
        Assert.True(pal.IsOver, "overGrowth detected as IsOver");
        Assert.True(pal.MaterialMapSideSize == 16, $"materialMapSideSize=16 (got {pal.MaterialMapSideSize})");
        Assert.True(Math.Abs(pal.ViewDistance - 200f) < 1f, $"viewdistance=200 (got {pal.ViewDistance})");
        Assert.True(pal.Materials.Count == 2, $"2 material slots (got {pal.Materials.Count})");
        var slot = pal.Materials[0];
        Assert.True(slot.Name == "dryGrass", $"first slot name 'dryGrass' (got '{slot.Name}')");
        Assert.True(slot.Types.Count == 2, $"2 types in dryGrass (got {slot.Types.Count})");
        Assert.True(slot.Types[0].GeometryName == "foliage/oak_m1.staticmesh", "first geometry name");
        Assert.True(MathF.Abs(slot.Types[0].Probability - 0.6f) < 1e-4f, $"probability=0.6 (got {slot.Types[0].Probability})");
        Assert.True(slot.Types[1].GeometryName == "foliage/fern_m1.staticmesh", "second geometry name");
        var water = pal.Materials[1];
        Assert.True(water.Name == "water" && water.Types.Count == 0, "water slot has no types");
        var geoms = pal.DistinctGeometries;
        Assert.True(geoms.Count == 2 && geoms[0] == "foliage/oak_m1.staticmesh", "DistinctGeometries has 2 entries");
        Assert.True(pal.TypeCount == 2, $"TypeCount=2 (got {pal.TypeCount})");
    }

    [Fact]
    public void Overgrowth_scatter_density_species_determinism()
    {
        var pal = FoliagePalette.Parse(MakeWst());
        int side = pal.MaterialMapSideSize;
        var cfg = new TerrainConfig { MaterialSize = side, WorldSize = 64, YScale = 1f, WaterLevel = 5f };

        var overMap = new MaterialMap(side, side);
        for (int y = 0; y < side; y++) for (int x = 0; x < side; x++) overMap[x, y] = 0;
        var maps = new GrowthMaps { Over = overMap, OverSide = side, OverPalette = pal };

        float patchMeters = 4f;
        var placed = OvergrowthFoliage.Scatter(maps, cfg, patchMeters);
        Assert.True(placed.Count > 0, $"scatter produced objects (got {placed.Count})");

        float ws = cfg.WorldSize;
        Assert.True(placed.All(p => p.WorldX >= 0f && p.WorldX <= ws && p.WorldZ >= 0f && p.WorldZ <= ws),
            "all instances within world bounds");
        Assert.True(placed.All(p => p.YawDeg >= 0f && p.YawDeg < 360f), "yaw in [0,360)");
        Assert.True(placed.All(p => p.Scale > 0f), "scale > 0");
        Assert.True(placed.All(p => p.Geometry == "foliage/oak_m1.staticmesh" || p.Geometry == "foliage/fern_m1.staticmesh"),
            "all geometries come from declared types");
        bool hasBoth = placed.Any(p => p.Geometry == "foliage/oak_m1.staticmesh") && placed.Any(p => p.Geometry == "foliage/fern_m1.staticmesh");
        Assert.True(hasBoth || placed.Count < 3, "both species appear when sample count is large enough");

        var again = OvergrowthFoliage.Scatter(maps, cfg, patchMeters);
        Assert.True(again.Count == placed.Count, "scatter is deterministic (same count)");
        Assert.True(again.Count == 0 || (again[0].WorldX == placed[0].WorldX && again[0].Geometry == placed[0].Geometry),
            "deterministic: same first element");

        var half = OvergrowthFoliage.Scatter(maps, cfg, patchMeters, densityScale: 0.5f);
        Assert.True(half.Count < placed.Count || placed.Count < 2, $"half density → fewer objects ({half.Count} < {placed.Count})");

        var waterMap = new MaterialMap(side, side);
        for (int y = 0; y < side; y++) for (int x = 0; x < side; x++) waterMap[x, y] = 1;
        var waterMaps = new GrowthMaps { Over = waterMap, OverSide = side, OverPalette = pal };
        var noPlace = OvergrowthFoliage.Scatter(waterMaps, cfg, patchMeters);
        Assert.True(noPlace.Count == 0, "water-only material map produces no scatter");
    }

    [Fact]
    public void Overgrowth_bake_to_staticobjects_and_roundtrip()
    {
        var pal = FoliagePalette.Parse(MakeWst());
        int side = pal.MaterialMapSideSize;
        var cfg = new TerrainConfig { MaterialSize = side, WorldSize = 64, YScale = 1f, WaterLevel = 5f };
        var overMap = new MaterialMap(side, side);
        for (int y = 0; y < side; y++) for (int x = 0; x < side; x++) overMap[x, y] = 0;
        var maps = new GrowthMaps { Over = overMap, OverSide = side, OverPalette = pal };

        var instances = OvergrowthFoliage.Scatter(maps, cfg, 4f);
        Assert.True(instances.Count > 0, "needs objects to test baking");

        var obj = new StaticObjectsFile();
        foreach (var fi in instances)
        {
            var so = new StaticObject(fi.Geometry)
            {
                Id = $"fg_{obj.Objects.Count}",
                Position = new Vec3(fi.WorldX, 0f, fi.WorldZ),
                Rotation = new Vec3(fi.YawDeg, 0, 0),
                Scale = MathF.Abs(fi.Scale - 1f) > 0.01f ? fi.Scale : null,
            };
            obj.Objects.Add(so);
        }
        Assert.True(obj.Objects.Count == instances.Count, "StaticObjectsFile has same count as instances");

        string tmp = Path.GetTempFileName();
        try
        {
            obj.Save(tmp);
            var loaded = StaticObjectsFile.Load(tmp);
            Assert.True(loaded.Objects.Count == instances.Count,
                $"saved/loaded count matches ({loaded.Objects.Count} == {instances.Count})");
            Assert.True(loaded.Objects.All(o => o.Template == "foliage/oak_m1.staticmesh" || o.Template == "foliage/fern_m1.staticmesh"),
                "all loaded objects use declared templates");
            for (int i = 0; i < Math.Min(5, instances.Count); i++)
            {
                var f = instances[i]; var o = loaded.Objects[i];
                bool posOk = MathF.Abs(o.Position.X - f.WorldX) < 0.05f && MathF.Abs(o.Position.Z - f.WorldZ) < 0.05f;
                Assert.True(posOk, $"position[{i}] round-trips (X {o.Position.X:0.0} vs {f.WorldX:0.0})");
                bool yawOk = MathF.Abs(o.Rotation.X - f.YawDeg) < 0.5f || MathF.Abs(MathF.Abs(o.Rotation.X - f.YawDeg) - 360f) < 0.5f;
                Assert.True(yawOk, $"yaw[{i}] round-trips ({o.Rotation.X:0.0} vs {f.YawDeg:0.0})");
            }
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

}
