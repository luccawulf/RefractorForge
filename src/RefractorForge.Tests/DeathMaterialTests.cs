using RefractorForge.Formats.Con;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;
using RefractorForge.Formats.Validation;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Saigon68's Huey blew up on spawn and its palace flag said OUT OF COMBAT AREA, through every combat-area setting
/// and with none at all. The cause was never a coordinate: Game::isOutsideWorld also returns "outside" for any
/// position whose material cell is 7 - the engine's own deathMaterial, which retail paints around every island as
/// the void. The map had been built across that void without repainting it: 68 spawns stood on 7. These pin the
/// engine's cell lookup, the check that should have said so, and the repair.
/// </summary>
public class DeathMaterialTests
{
    private static TerrainConfig Cfg() => new() { WorldSize = 1024, MaterialSize = 64, YScale = 1f, WaterLevel = 7.5f };

    // A 64-cell map: an island of dry grass (2) with a paved road (15) down the middle, in a sea of deathMaterial.
    private static MaterialMap Island()
    {
        var m = new MaterialMap(64, 64);
        for (int z = 0; z < 64; z++)
            for (int x = 0; x < 64; x++)
                m[x, z] = (byte)(x >= 20 && x < 44 && z >= 20 && z < 44 ? (x == 32 ? 15 : 2) : 7);
        return m;
    }

    [Fact]
    public void Lookup_uses_the_engines_cell_arithmetic()
    {
        // cell = floor(x * cells / worldSize): 16 m per cell here, so 319.9 is cell 19 (void) and 320 is cell 20.
        var cfg = Cfg(); var m = Island();
        Assert.True(DeathMaterial.IsDeath(m, cfg, 319.9f, 500f));
        Assert.False(DeathMaterial.IsDeath(m, cfg, 320f, 500f));
        Assert.Equal(15, DeathMaterial.At(m, cfg, 32 * 16 + 1f, 500f));
    }

    [Fact]
    public void The_map_check_names_every_spawn_on_it()
    {
        var cfg = Cfg(); var m = Island();
        var gp = new EditableGameplay(new GameplayObjects(
            new List<ControlPointDef> { new("Palace", new Vec3(100, 10, 100), 20, 1), new("Island", new Vec3(500, 10, 500), 20, 2) },
            new List<VehicleSpawnDef> { new("AttackChopper", new Vec3(120, 30, 120), default, "uh1assault", 1), new("SlickChopper", new Vec3(520, 30, 520), default, "uh1transport", 2) },
            new List<SoldierSpawnDef> { new("US_Palace_Spawn1", new Vec3(110, 10, 110), default, 1) }));
        var r = LevelValidator.Run(new LevelValidator.Inputs { Gameplay = gp, Config = cfg, Material = m, CombatArea = CombatArea.Whole(1024) });
        var death = r.Issues.Where(i => i.Category == "On deathMaterial").ToList();
        Assert.Contains(death, i => i.Message.StartsWith("'Palace'") && i.Severity == IssueSeverity.Error);
        Assert.Contains(death, i => i.Message.StartsWith("'AttackChopper'") && i.Message.Contains("destroyed as it spawns"));
        Assert.Contains(death, i => i.Message.StartsWith("'US_Palace_Spawn1'"));
        Assert.DoesNotContain(death, i => i.Message.StartsWith("'Island'") || i.Message.StartsWith("'SlickChopper'"));
        Assert.Contains(death, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("inside the combat area"));
    }

    [Fact]
    public void Without_a_material_map_the_check_stays_quiet()
    {
        var r = LevelValidator.Run(new LevelValidator.Inputs { Config = Cfg(), CombatArea = CombatArea.Whole(1024) });
        Assert.DoesNotContain(r.Issues, i => i.Category == "On deathMaterial");
    }

    [Fact]
    public void Repaint_clears_the_rectangle_and_leaves_the_boundary()
    {
        var cfg = Cfg(); var m = Island();
        var area = new CombatArea(0, 0, 768, 768);              // cells 0..47: the island plus the void west/south of it
        var edit = DeathMaterial.Repaint(m, null, cfg, area);
        Assert.NotNull(edit);
        var (deathIn, _) = DeathMaterial.CountInside(m, cfg, area);
        Assert.Equal(0, deathIn);
        Assert.Equal(7, m[60, 60]);                             // outside the rectangle: still the boundary
        Assert.Equal(7, m[10, 60]);
        // and the fill grew out of what was painted - grass beside grass, road beside road - not one flat colour
        Assert.Equal(2, m[10, 30]);
        Assert.Equal(15, m[32, 10]);
        Assert.Equal(15, m[32, 46]);
    }

    [Fact]
    public void Repaint_puts_water_where_the_ground_is_under_the_water_line()
    {
        var cfg = Cfg(); var m = Island();
        var hm = new Heightmap(64, 64);
        for (int z = 0; z < 64; z++)
            for (int x = 0; x < 64; x++)
                hm[x, z] = (ushort)(z < 10 ? 0 : 5000);             // raw 5000 * 1 / 256 = 19.5 m; the south strip is at 0 m
        DeathMaterial.Repaint(m, hm, cfg, CombatArea.Whole(1024));
        Assert.Equal(DeathMaterial.Water, m[30, 5]);
        Assert.Equal(2, m[30, 15]);
        Assert.Equal(0, DeathMaterial.CountInside(m, cfg, CombatArea.Whole(1024)).Death);
    }

    [Fact]
    public void Repaint_is_undoable_and_idempotent()
    {
        var cfg = Cfg(); var m = Island();
        var before = m.Samples.ToArray();
        var edit = DeathMaterial.Repaint(m, null, cfg, CombatArea.Whole(1024))!;
        Assert.NotEqual(before, m.Samples);
        Assert.Null(DeathMaterial.Repaint(m, null, cfg, CombatArea.Whole(1024)));   // nothing left to do
        edit.Undo(m);
        Assert.Equal(before, m.Samples);
    }
}
