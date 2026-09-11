using RefractorForge.Formats.Con;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;
using RefractorForge.Formats.Validation;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// al_vietnas' tunnel spawns (2026-09-11): 14 soldier spawns 17-21 m under the sewers, every template carrying
/// <c>ObjectTemplate.allowSpawingBelowGround 1</c> - one letter short. The console ignores a command it does not know,
/// the flag stayed off, and BFSpawnPoint::fixPositionVsTerrain lifted every one of them to the grass above. The map
/// check used to call ANY underground spawn an error, tunnel spawns included; now it asks the question the engine asks.
/// </summary>
public class SpawnBelowGroundTests
{
    private static readonly string[] Templates =
    {
        "rem -----------------------------------------",
        "ObjectTemplate.create SpawnPoint Ruins_Spawn6",
        "ObjectTemplate.setSpawnId 100",
        "ObjectTemplate.setGroup 4",
        "ObjectTemplate.create SpawnPoint Sewers_Spawn1",
        "ObjectTemplate.setGroup 6",
        "ObjectTemplate.allowSpawingBelowGround 1",           // al_vietnas' spelling
        "ObjectTemplate.create SpawnPoint Tunnel_Spawn1",
        "ObjectTemplate.setGroup 5",
        "ObjectTemplate.allowSpawningBelowGround 1",          // Cedar Falls' / Saigon68's spelling
        "ObjectTemplate.create SpawnPoint Off_Spawn",
        "ObjectTemplate.allowSpawningBelowGround 1",
        "ObjectTemplate.allowSpawningBelowGround 0",          // switched back off
        "rem ObjectTemplate.allowSpawningBelowGround 1",
    };

    [Fact]
    public void The_flag_is_read_and_the_misspelling_is_named()
    {
        var f = SpawnBelowGround.Parse(Templates);
        Assert.Contains("Tunnel_Spawn1", f.Allowed);
        Assert.DoesNotContain("Sewers_Spawn1", f.Allowed);
        Assert.DoesNotContain("Off_Spawn", f.Allowed);
        Assert.DoesNotContain("Ruins_Spawn6", f.Allowed);
        Assert.Equal("allowSpawingBelowGround", f.Misspelled["Sewers_Spawn1"]);
        Assert.Equal(4, f.Defined.Count);
        Assert.False(SpawnBelowGround.IsMisspelling("allowSpawningBelowGround"));
        Assert.False(SpawnBelowGround.IsMisspelling("setSpawnId"));
        Assert.True(SpawnBelowGround.IsMisspelling("alowSpawningBelowGround"));
    }

    private static LevelReport Check(IReadOnlyList<(string, SpawnBelowGround.Flags)> modes)
    {
        var cfg = new TerrainConfig { WorldSize = 1024, MaterialSize = 64, YScale = 1f, WaterLevel = 7.5f };
        var hm = new Heightmap(64, 64);
        for (int z = 0; z < 64; z++) for (int x = 0; x < 64; x++) hm[x, z] = 3584;       // 14 m everywhere
        var gp = new EditableGameplay(new GameplayObjects(
            new List<ControlPointDef> { new("Sewers", new Vec3(608, -7.8f, 303), 15, 6), new("Tunnel", new Vec3(540, -6.7f, 204), 20, 5), new("Ruins", new Vec3(668, 14, 224), 20, 4) },
            new List<VehicleSpawnDef>(),
            new List<SoldierSpawnDef>
            {
                new("Sewers_Spawn1", new Vec3(600, -7.8f, 300), default, 6),
                new("Tunnel_Spawn1", new Vec3(540, -5.8f, 204), default, 5),
                new("Ruins_Spawn6", new Vec3(668, 14, 224), default, 4),
            }));
        return LevelValidator.Run(new LevelValidator.Inputs
        {
            Gameplay = gp, Heightmap = hm, Config = cfg, CombatArea = CombatArea.Whole(1024), SpawnBelowGroundByMode = modes,
        });
    }

    [Fact]
    public void A_flagged_tunnel_spawn_is_fine_and_a_misspelled_one_is_an_error_per_mode()
    {
        var f = SpawnBelowGround.Parse(Templates);
        var r = Check(new[] { ("Conquest", f), ("Ctf", f) });
        var spawn = r.Issues.Where(i => i.Category == "Soldier spawn").ToList();
        Assert.DoesNotContain(spawn, i => i.Message.StartsWith("'Tunnel_Spawn1'"));             // the game keeps it under
        Assert.DoesNotContain(spawn, i => i.Message.StartsWith("'Ruins_Spawn6'"));              // on the ground
        var sewers = spawn.Where(i => i.Message.StartsWith("'Sewers_Spawn1'")).ToList();
        Assert.Equal(2, sewers.Count);                                                          // once per mode
        Assert.All(sewers, i => Assert.Equal(IssueSeverity.Error, i.Severity));
        Assert.All(sewers, i => Assert.Contains("allowSpawingBelowGround", i.Message));
        Assert.Contains(sewers, i => i.Message.Contains("Ctf template"));
    }

    [Fact]
    public void The_corrected_spelling_clears_it_and_a_mode_that_lacks_the_spawn_says_nothing()
    {
        var fixedLines = Templates.Select(l => l.Replace("allowSpawingBelowGround", "allowSpawningBelowGround")).ToArray();
        var r = Check(new[] { ("Conquest", SpawnBelowGround.Parse(fixedLines)), ("TDM", SpawnBelowGround.Parse(new[] { "ObjectTemplate.create SpawnPoint Other" })) });
        Assert.DoesNotContain(r.Issues, i => i.Category == "Soldier spawn" && i.Message.Contains("under the ground"));
    }

    [Fact]
    public void Without_the_templates_an_underground_spawn_is_still_reported()
    {
        var r = Check(Array.Empty<(string, SpawnBelowGround.Flags)>());
        Assert.Contains(r.Issues, i => i.Message.StartsWith("'Sewers_Spawn1'") && i.Message.Contains("under the ground"));
    }
}
