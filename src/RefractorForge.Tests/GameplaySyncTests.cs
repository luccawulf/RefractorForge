using RefractorForge.Formats.Con;
using RefractorForge.Formats.Geometry;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The gameplay layer is synced as FULL STATE: any edit re-serialises every control point, vehicle spawner and
/// soldier spawn, and the relay echoes it back to the sender, who re-applies it. So a field the wire does not carry
/// is not merely invisible to other people — it is erased on everyone's copy, including the copy belonging to the
/// person who made the edit, and then written out by the next save.
///
/// That makes this round trip load-bearing for data the user cannot get back: per-team vehicle templates, spawn
/// teams, and the group id that ties a soldier spawn to its control point.
/// </summary>
public class GameplaySyncTests
{
    static EditableGameplay Layer()
    {
        var gp = new EditableGameplay(GameplayObjects.Empty);
        gp.Add(GpKind.ControlPoint, new ControlPointDef(
            "US_base", new Vec3(100, 5, 200), 30f, SpawnGroupId: 2,
            Team: 1, AreaValue: 25, ConversionTime: 90, ControlPointName: "OI_base1", ObjectSpawnerId: 7,
            PoleGeometry: "flagbase_m1", FlagGeometry1: "flagge_m1", FlagGeometry2: "flaguk_m1", FlagHeight: 8.2f,
            TimeToGetControl: 35, TimeToLoseControl: 45, DisableIfEnemyInside: 1, DisableWhenLosing: 1,
            LoseControlWhenEnemyClose: 0, LoseControlWhenNotClose: 1, UnableToChangeTeam: 1, OnlyTakableByTeam: 2,
            HasCollisionPhysics: 0));
        gp.Add(GpKind.Vehicle, new VehicleSpawnDef(
            "Spawner", new Vec3(120, 5, 210), new Vec3(90, 0, 0), "M48Patton", OsId: 3,
            Vehicle1: "Willy", Vehicle2: "M48Patton", Team: 2,
            MinSpawnDelay: 15, MaxSpawnDelay: 45, SpawnDelayAtStart: 5, TimeToLive: 300,
            Distance: 150, DamageWhenLost: 20, MaxNrOfObjectSpawned: 4));
        gp.Add(GpKind.Soldier, new SoldierSpawnDef(
            "sp1", new Vec3(105, 5, 205), new Vec3(45, 0, 0), Group: 2, SpawnId: 9, SpawnAsParaTrooper: 1));
        return gp;
    }

    static EditableGameplay RoundTrip(EditableGameplay src)
    {
        var dst = new EditableGameplay(GameplayObjects.Empty);
        GameplaySync.Apply(dst, GameplaySync.Serialize(src));
        return dst;
    }

    [Fact]
    public void A_vehicle_spawner_survives_the_wire_intact()
    {
        var a = Layer().VehicleSpawns[0];
        var b = RoundTrip(Layer()).VehicleSpawns[0];

        Assert.Equal(a.Name, b.Name);
        Assert.Equal(a.Position, b.Position);
        Assert.Equal(a.Rotation, b.Rotation);
        Assert.Equal(a.Vehicle, b.Vehicle);
        Assert.Equal(a.OsId, b.OsId);
        // The per-team templates and the team itself: losing these is what silently un-does team-correct spawns.
        Assert.Equal(a.Vehicle1, b.Vehicle1);
        Assert.Equal(a.Vehicle2, b.Vehicle2);
        Assert.Equal(a.Team, b.Team);
        // The spawn-template fields, all written out on save.
        Assert.Equal(a.MinSpawnDelay, b.MinSpawnDelay);
        Assert.Equal(a.MaxSpawnDelay, b.MaxSpawnDelay);
        Assert.Equal(a.SpawnDelayAtStart, b.SpawnDelayAtStart);
        Assert.Equal(a.TimeToLive, b.TimeToLive);
        Assert.Equal(a.Distance, b.Distance);
        Assert.Equal(a.DamageWhenLost, b.DamageWhenLost);
        Assert.Equal(a.MaxNrOfObjectSpawned, b.MaxNrOfObjectSpawned);
    }

    [Fact]
    public void A_soldier_spawn_keeps_the_group_that_ties_it_to_its_flag()
    {
        var a = Layer().SoldierSpawns[0];
        var b = RoundTrip(Layer()).SoldierSpawns[0];

        Assert.Equal(a.Position, b.Position);
        Assert.Equal(a.Rotation, b.Rotation);
        // Group is what matches a spawn to the control point's SpawnGroupId. Reset it and nobody spawns there.
        Assert.Equal(a.Group, b.Group);
        Assert.Equal(a.SpawnId, b.SpawnId);
        Assert.Equal(a.SpawnAsParaTrooper, b.SpawnAsParaTrooper);
    }

    [Fact]
    public void A_control_point_survives_the_wire_intact()
    {
        var a = Layer().ControlPoints[0];
        var b = RoundTrip(Layer()).ControlPoints[0];

        Assert.Equal(a.Name, b.Name);
        Assert.Equal(a.Position, b.Position);
        Assert.Equal(a.Radius, b.Radius);
        Assert.Equal(a.SpawnGroupId, b.SpawnGroupId);
        Assert.Equal(a.Team, b.Team);
        Assert.Equal(a.AreaValue, b.AreaValue);
        Assert.Equal(a.ConversionTime, b.ConversionTime);
        Assert.Equal(a.ControlPointName, b.ControlPointName);
        // ObjectSpawnerId owns the vehicle spawners at this flag - the vehicle equivalent of SpawnGroupId.
        Assert.Equal(a.ObjectSpawnerId, b.ObjectSpawnerId);
        Assert.Equal(a.PoleGeometry, b.PoleGeometry);
        Assert.Equal(a.FlagGeometry1, b.FlagGeometry1);
        Assert.Equal(a.FlagGeometry2, b.FlagGeometry2);
        Assert.Equal(a.FlagHeight, b.FlagHeight, 3);
        Assert.Equal(a.TimeToGetControl, b.TimeToGetControl);
        Assert.Equal(a.TimeToLoseControl, b.TimeToLoseControl);
        Assert.Equal(a.DisableIfEnemyInside, b.DisableIfEnemyInside);
        Assert.Equal(a.DisableWhenLosing, b.DisableWhenLosing);
        Assert.Equal(a.LoseControlWhenEnemyClose, b.LoseControlWhenEnemyClose);
        Assert.Equal(a.LoseControlWhenNotClose, b.LoseControlWhenNotClose);
        Assert.Equal(a.UnableToChangeTeam, b.UnableToChangeTeam);
        Assert.Equal(a.OnlyTakableByTeam, b.OnlyTakableByTeam);
        Assert.Equal(a.HasCollisionPhysics, b.HasCollisionPhysics);
    }

    [Fact]
    public void Repeated_round_trips_do_not_drift()
    {
        // Every edit re-serialises and the echo re-applies, so a level being worked on goes through this many
        // times over. Nothing may erode.
        var gp = Layer();
        string first = GameplaySync.Serialize(gp);
        for (int i = 0; i < 5; i++) gp = RoundTrip(gp);
        Assert.Equal(first, GameplaySync.Serialize(gp));
    }

    [Fact]
    public void Short_lines_from_an_older_peer_still_parse()
    {
        // Back-compat: a peer on an older build sends the original short form. It must load, not throw, and take
        // sensible defaults for what it did not send.
        var text = "CP US_base 100/5/200 30 2\nVS Spawner 120/5/210 90/0/0 M48Patton 3\nSS sp1 105/5/205 45/0/0\n";
        var gp = new EditableGameplay(GameplayObjects.Empty);
        GameplaySync.Apply(gp, text);

        Assert.Single(gp.ControlPoints);
        Assert.Single(gp.VehicleSpawns);
        Assert.Single(gp.SoldierSpawns);
        Assert.Equal("US_base", gp.ControlPoints[0].Name);
        Assert.Equal(new Vec3(100, 5, 200), gp.ControlPoints[0].Position);
        Assert.Equal("M48Patton", gp.VehicleSpawns[0].Vehicle);
        Assert.Equal(new Vec3(45, 0, 0), gp.SoldierSpawns[0].Rotation);
    }

    [Fact]
    public void A_malformed_line_is_skipped_without_taking_the_rest_with_it()
    {
        var good = GameplaySync.Serialize(Layer());
        var gp = new EditableGameplay(GameplayObjects.Empty);
        GameplaySync.Apply(gp, "CP\nnonsense here\n" + good);

        Assert.Single(gp.ControlPoints);
        Assert.Single(gp.VehicleSpawns);
        Assert.Single(gp.SoldierSpawns);
    }
}
