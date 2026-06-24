using RefractorForge.Formats.Con;
using RefractorForge.Formats.Editing;
using RefractorForge.Formats.Geometry;
using Xunit;

namespace RefractorForge.Tests;

public class GameplayTests
{
    [Fact]
    public void ControlPoint_edit_and_gameplay_sync_roundtrip()
    {
        string tmpl =
            "ObjectTemplate.create ControlPoint us_base\r\n" +
            "ObjectTemplate.controlPointName OI_base1\r\n" +
            "ObjectTemplate.radius 40\r\n" +
            "ObjectTemplate.team 2\r\n" +
            "ObjectTemplate.spawnGroupId 1\r\n" +
            "ObjectTemplate.areaValue 25\r\n" +
            "ObjectTemplate.conversionTime 90\r\n" +
            "ObjectTemplate.geometry USflagbase_m1\r\n";
        string pts = "Object.create us_base\r\nObject.absolutePosition 841.57/35.14/528.64\r\n";

        var cps = GameplayObjects.ParseControlPoints(pts.Split('\n'), tmpl.Split('\n'));
        var cp = cps[0];
        Assert.True(cp.Team == 2 && cp.AreaValue == 25 && cp.ConversionTime == 90 && cp.ControlPointName == "OI_base1", "parsed CP fields");

        var edited = cp with { Team = 1, AreaValue = 50, ConversionTime = 120, Radius = 35f, ControlPointName = "OI_alt" };
        var patched = GameplayWriter.PatchControlPointRadii(tmpl.Split('\n'), new[] { edited });
        Assert.True(patched.Contains("ObjectTemplate.team 1"), "patched team");
        Assert.True(patched.Contains("ObjectTemplate.areaValue 50"), "patched areaValue");
        Assert.True(patched.Contains("ObjectTemplate.conversionTime 120"), "patched conversionTime");
        Assert.True(patched.Contains("ObjectTemplate.radius 35"), "patched radius");
        Assert.True(patched.Contains("ObjectTemplate.controlPointName OI_alt"), "patched controlPointName");
        Assert.True(patched.Contains("ObjectTemplate.geometry USflagbase_m1"), "geometry preserved verbatim");
        Assert.True(!patched.Contains("ObjectTemplate.team 2"), "old team value replaced");

        var gp = new EditableGameplay(new GameplayObjects(
            new[] { edited }, Array.Empty<VehicleSpawnDef>(), Array.Empty<SoldierSpawnDef>()));
        var (rt, _, _) = GameplaySync.Parse(GameplaySync.Serialize(gp));
        Assert.True(rt.Count == 1 && rt[0].Team == 1 && rt[0].AreaValue == 50 && rt[0].ConversionTime == 120 && rt[0].ControlPointName == "OI_alt", "GameplaySync round-trips CP fields");
    }

    [Fact]
    public void Prefab_fromobjects_save_load()
    {
        bool Near(float a, float b) => MathF.Abs(a - b) < 1e-3f;
        var src = StaticObjectsFile.Parse(new[]
        {
            "object.create o_tent",   "object.absolutePosition 100/10/200", "object.rotation 90/0/0",
            "object.create o_wall",   "object.absolutePosition 110/12/205", "object.rotation 0/0/0",
            "object.create o_bunker", "object.absolutePosition 90/14/195",  "object.rotation 180/0/0",
        });
        Assert.True(src.Objects.Count == 3, $"3 source objects ({src.Objects.Count})");

        var pf = Prefab.FromObjects("Test Camp", src.Objects);
        Assert.True(pf.Members.Count == 3, $"3 members ({pf.Members.Count})");
        Assert.True(Near(pf.Members[0].Offset.X, 0f) && Near(pf.Members[0].Offset.Z, 0f), "tent at XZ centroid");
        Assert.True(Near(pf.Members[0].Offset.Y, 0f), "lowest object Y offset 0");
        Assert.True(Near(pf.Members[1].Offset.Y, 2f), $"wall Y offset +2 (got {pf.Members[1].Offset.Y})");

        var tmp = Path.Combine(Path.GetTempPath(), "rf_prefab_test.rfprefab");
        try
        {
            pf.Save(tmp);
            var rl = Prefab.Load(tmp);
            Assert.True(rl.Name == "Test Camp", $"name round-trip ({rl.Name})");
            bool same = rl.Members.Count == 3;
            for (int i = 0; i < rl.Members.Count && same; i++)
            {
                var a = pf.Members[i]; var b = rl.Members[i];
                if (a.Template != b.Template || !Near(a.Offset.X, b.Offset.X) || !Near(a.Offset.Y, b.Offset.Y)
                    || !Near(a.Offset.Z, b.Offset.Z) || !Near(a.Rotation.X, b.Rotation.X)) same = false;
            }
            Assert.True(same, "member transforms round-trip");
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void Gameplay_sync_roundtrip_and_replace()
    {
        bool Near(float a, float b) => MathF.Abs(a - b) < 1e-3f;
        var gp = new EditableGameplay(GameplayObjects.Empty);
        gp.ControlPoints.Add(new ControlPointDef("US_base", new Vec3(100, 5, 200), 30f, 1));
        gp.ControlPoints.Add(new ControlPointDef("NVA_base", new Vec3(800, 6, 900), 25f, 2));
        gp.VehicleSpawns.Add(new VehicleSpawnDef("Spawner", new Vec3(120, 5, 210), new Vec3(90, 0, 0), "M48Patton", 1));
        gp.SoldierSpawns.Add(new SoldierSpawnDef("sp1", new Vec3(105, 5, 205), new Vec3(45, 0, 0)));

        var wire = GameplaySync.Serialize(gp);
        var gp2 = new EditableGameplay(GameplayObjects.Empty);
        GameplaySync.Apply(gp2, wire);

        Assert.True(gp2.ControlPoints.Count == 2, $"2 control points ({gp2.ControlPoints.Count})");
        Assert.True(gp2.VehicleSpawns.Count == 1 && gp2.VehicleSpawns[0].Vehicle == "M48Patton", "vehicle spawn round-trips");
        Assert.True(gp2.SoldierSpawns.Count == 1, $"1 soldier spawn ({gp2.SoldierSpawns.Count})");
        Assert.True(Near(gp2.ControlPoints[1].Radius, 25f) && gp2.ControlPoints[1].Name == "NVA_base", "CP radius + name round-trip");
        Assert.True(Near(gp2.VehicleSpawns[0].Rotation.X, 90f), "vehicle spawn rotation round-trip");

        gp2.ControlPoints.Add(new ControlPointDef("stale", Vec3.Zero, 1f, 0));
        GameplaySync.Apply(gp2, wire);
        Assert.True(gp2.ControlPoints.Count == 2, $"re-apply replaces (no leftover stale) ({gp2.ControlPoints.Count})");
    }
}
