using System.Text;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Rfa;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// A ship's built-in soldier spawns belong to a spawn GROUP whose team the game fixes globally
/// (game/GlobalSpawnGroups.con) - so a ship given to the other team fills with the wrong soldiers. These gate the
/// check that finds it and the per-team copy that fixes it (Formats/Cons/SpawnGroups.cs).
/// </summary>
public class SpawnGroupTeamTests
{
    const string Global = """
        rem *** Ship ***
        spawnPointManager.group 73
        spawnPointManager.groupTeam 1
        spawnPointManager.groupIcon test2.tga
        rem spawnPointManager.groupStatus 0

        spawnPointManager.group 72
        spawnPointManager.groupTeam 2
        spawnPointManager.groupIcon test2.tga
        spawnPointManager.onlyForHuman 1
        """;

    const string Objects = """
        rem *** Ship ***
        ObjectTemplate.create PlayerControlObject Ship
        ObjectTemplate.hasMobilePhysics 1
        ObjectTemplate.addTemplate lodShip
        ObjectTemplate.setPosition 0/3.5/0
        ObjectTemplate.addTemplate ShipSoldierSpawn
        ObjectTemplate.setPosition 2/10/26
        ObjectTemplate.addTemplate ShipSoldierSpawn
        ObjectTemplate.setPosition 2/9.4/-35
        ObjectTemplate.addTemplate ShipBoatSpawner
        ObjectTemplate.setPosition -8/6/-23

        ObjectTemplate.create LodObject lodShip
        ObjectTemplate.addTemplate ShipHull

        ObjectTemplate.create SimpleObject ShipHull
        ObjectTemplate.geometry ship_hull_m1

        ObjectTemplate.create SpawnPoint ShipSoldierSpawn
        ObjectTemplate.setSpawnRotation 0/0/0
        ObjectTemplate.setGroup 73
        ObjectTemplate.setAIEnterOnSpawn 1

        ObjectTemplate.create ObjectSpawner ShipBoatSpawner
        ObjectTemplate.setObjectTemplate 1 Boat
        ObjectTemplate.setObjectTemplate 2 Boat
        ObjectTemplate.team 1

        GeometryTemplate.create StandardMesh ship_hull_m1
        GeometryTemplate.file ship_hull_m1
        """;

    static SpawnGroupTable Table() => new SpawnGroupTable().Apply(Global, "game/GlobalSpawnGroups.con");
    static ObjectTemplateIndex Index(params string[] extra) =>
        ObjectTemplateIndex.Build(new[] { ("objects.con", Objects) }.Concat(extra.Select((t, i) => ($"x{i}.con", t))));

    [Fact]
    public void Group_teams_come_from_the_declarations_and_later_ones_win()
    {
        var t = Table();
        Assert.Equal(1, t.TeamOf(73));
        Assert.Equal(2, t.TeamOf(72));
        Assert.Null(t.TeamOf(5));
        Assert.Contains("spawnPointManager.onlyForHuman 1", t.Groups[72].OtherLines);
        t.Apply("spawnPointManager.group 73\nspawnPointManager.groupTeam 2\n", "Conquest/SpawnpointManagerSettings.con");
        Assert.Equal(2, t.TeamOf(73));
    }

    [Fact]
    public void A_ship_given_to_the_other_team_keeps_its_old_teams_spawns()
    {
        var idx = Index();
        var m = VehicleSpawnTeams.Check(idx, Table(), "Ship", 2);
        var sp = Assert.Single(m, x => !x.IsSpawner);
        Assert.Equal(("ShipSoldierSpawn", 2, 73, 1), (sp.Template, sp.Count, sp.Group, sp.GroupTeam));
        var boats = Assert.Single(m, x => x.IsSpawner);
        Assert.Equal(("ShipBoatSpawner", 1), (boats.Template, boats.GroupTeam));
        Assert.Empty(VehicleSpawnTeams.Check(idx, Table(), "Ship", 1));   // for its own team, nothing is wrong
    }

    [Fact]
    public void The_team_copy_moves_its_spawns_to_a_new_group_of_that_team_and_shares_everything_else()
    {
        var idx = Index();
        var used = new HashSet<int> { 1, 2, 3, 72, 73 };
        var r = TeamVehicleCopy.Build(idx, Table(), "Ship", 2, used);
        Assert.NotNull(r);
        Assert.Equal("Ship_T2", r!.CopyName);
        Assert.Equal(200, r.GroupMap[73]);
        Assert.Contains("ObjectTemplate.create PlayerControlObject Ship_T2", r.TemplatesText);
        Assert.Contains("ObjectTemplate.create SpawnPoint ShipSoldierSpawn_T2", r.TemplatesText);
        Assert.Contains("ObjectTemplate.setGroup 200", r.TemplatesText);
        Assert.Contains("ObjectTemplate.create ObjectSpawner ShipBoatSpawner_T2", r.TemplatesText);
        Assert.Contains("ObjectTemplate.team 2", r.TemplatesText);
        Assert.Contains("ObjectTemplate.addTemplate lodShip", r.TemplatesText);          // the hull is shared, not copied
        Assert.DoesNotContain("create LodObject lodShip_T2", r.TemplatesText);
        Assert.Equal(2, r.TemplatesText.Split('\n').Count(l => l.Trim() == "ObjectTemplate.addTemplate ShipSoldierSpawn_T2"));
        Assert.Contains("spawnPointManager.group 200", r.GroupsText);
        Assert.Contains("spawnPointManager.groupTeam 2", r.GroupsText);
        Assert.Contains("spawnPointManager.groupIcon test2.tga", r.GroupsText);

        // With the copy and its groups loaded, the copy is clean for team 2 and still walks to the shared hull.
        var idx2 = Index(r.TemplatesText);
        var t2 = Table().Apply(r.GroupsText, "level");
        Assert.Empty(VehicleSpawnTeams.Check(idx2, t2, "Ship_T2", 2));
        Assert.Contains(idx2.Walk("Ship_T2"), w => w.T.Name == "ShipHull");
        Assert.Equal(1, t2.TeamOf(73));   // the original ship is untouched
    }

    [Fact]
    public void Additions_are_appended_once()
    {
        var a = new GameplayAddition("ObjectSpawnTemplates.con", TeamVehicleCopy.MarkerFor("Ship_T2"), "ObjectTemplate.create PlayerControlObject Ship_T2\r\n");
        var once = TeamVehicleCopy.AppendAdditions("ObjectTemplate.create ObjectSpawner X\r\n", new[] { a }, "ObjectSpawnTemplates.con");
        var twice = TeamVehicleCopy.AppendAdditions(once, new[] { a }, "ObjectSpawnTemplates.con");
        Assert.Equal(once, twice);
        Assert.Contains("rem RefractorForge team copy: Ship_T2", once);
        Assert.Equal(once.Length, TeamVehicleCopy.AppendAdditions(once, new[] { a }, "SpawnpointManagerSettings.con").Length);
    }

    const string Bf1918 = @"D:\Games\EA GAMES\Battlefield 1942\Mods\bf1918\Archives";
    const string Bf1942 = @"D:\Games\EA GAMES\Battlefield 1942\Mods\bf1942\Archives";

    /// <summary>The user report this answers: an SMS Dresden spawned for the Allies fills with Axis soldiers. BF1918
    /// declares its group 73 for team 1 in game/GlobalSpawnGroups.con.</summary>
    [InstallFact(Bf1918)]
    public void Bf1918_dresden_spawns_are_axis_whoever_owns_the_ship()
    {
        string Read(string rfa, string suffix)
        {
            var a = new RefractorFlatArchive(rfa);
            var e = a.Entries.First(x => x.Name.Replace('\\', '/').EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            return Encoding.Latin1.GetString(a.Read(e));
        }
        var table = new SpawnGroupTable().Apply(Read(Path.Combine(Bf1918, "bf1942", "game.rfa"), "game/GlobalSpawnGroups.con"), "bf1918");
        Assert.Equal(1, table.TeamOf(73));
        var scripts = new List<(string, string)>();
        foreach (var path in new[] { Path.Combine(Bf1918, "objects.rfa"), Path.Combine(Bf1942, "Objects.rfa") })
        {
            if (!File.Exists(path)) continue;
            var a = new RefractorFlatArchive(path);
            foreach (var e in a.Entries)
                if (e.Name.EndsWith(".con", StringComparison.OrdinalIgnoreCase))
                    scripts.Add((e.Name, Encoding.Latin1.GetString(a.Read(e))));
        }
        var idx = ObjectTemplateIndex.Build(scripts);
        var m = VehicleSpawnTeams.Check(idx, table, "SMSDresden", 2);
        var spawns = Assert.Single(m, x => !x.IsSpawner);
        Assert.Equal(3, spawns.Count);
        Assert.Equal(73, spawns.Group);
        Assert.Contains(m, x => x.IsSpawner && x.Template == "SMSDresdenBarkasseSpawner" && x.Count == 2);

        var r = TeamVehicleCopy.Build(idx, table, "SMSDresden", 2, new HashSet<int>(table.Groups.Keys));
        Assert.NotNull(r);
        var idx2 = ObjectTemplateIndex.Build(scripts.Append(("copy", r!.TemplatesText)));
        Assert.Empty(VehicleSpawnTeams.Check(idx2, new SpawnGroupTable().Apply(Read(Path.Combine(Bf1918, "bf1942", "game.rfa"), "game/GlobalSpawnGroups.con"), "bf1918")
                                                     .Apply(r.GroupsText, "level"), "SMSDresden_T2", 2));
        // The copy is the root and its spawns - the ship itself (lods, guns, hull) stays shared.
        Assert.True(r.TemplatesText.Split('\n').Count(l => l.TrimStart().StartsWith("ObjectTemplate.create")) <= 3);
    }
}
