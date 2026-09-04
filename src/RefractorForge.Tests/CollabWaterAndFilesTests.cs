using RefractorForge.Formats.Con;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Two things a collaborator could not see. The WATER op carried a single number - the water LEVEL - so every colour
/// on both bodies, and the shader's reflectivity and opacity, stayed on the machine that set them; since those are
/// map data written into Init.con and levelWater.rs on save, whoever saved last silently overwrote the other. And a
/// decal or a placed sound generates its own .con / .ssc / .wav / .dds, which existed nowhere but the machine that
/// made them - the peer got the placement and nothing to resolve it with.
/// </summary>
public class CollabWaterAndFilesTests
{
    private const string FullWater =
        "WATER 30 c=0.1/0.22/0.3 s=0.1/0.22/0.3 d=0.16/0.35/0.55 a=0.6 rf=0.82 op=0.6 " +
        "bon=1 bl=6.3 bc=0.3/0.19/0.08 bs=0.2/0.14/0.06 bd=0.12/0.08/0.03 ba=0.45 bad=0.4 bcd=7.5 brf=0.1 bop=0.85";

    [Fact]
    public void The_whole_water_op_survives_to_a_late_joiner()
    {
        var w = new CollabWorldState();
        Assert.True(w.ApplyOp(FullWater));
        // verbatim, so the payload can grow again without the relay needing to understand it
        Assert.Contains(FullWater, w.SnapshotOps());
    }

    [Fact]
    public void A_later_water_edit_replaces_the_earlier_one()
    {
        var w = new CollabWorldState();
        w.ApplyOp(FullWater);
        w.ApplyOp("WATER 31 c=0.9/0.1/0.1");
        var ops = w.SnapshotOps().Where(o => o.StartsWith("WATER")).ToList();
        Assert.Single(ops);
        Assert.Contains("c=0.9/0.1/0.1", ops[0]);
    }

    [Fact]
    public void A_session_saved_before_the_op_grew_still_loads()
    {
        // Older relays persisted water.txt as a bare number. It has to come back as the level-only op it was, not
        // be dropped - a resumed session would otherwise lose the water level entirely.
        var dir = Path.Combine(Path.GetTempPath(), "rf_collab_water_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "water.txt"), "42.5");
            var loaded = CollabWorldState.Load(dir);
            Assert.NotNull(loaded);
            Assert.Equal("WATER 42.5", loaded!.Water);
            Assert.Contains("WATER 42.5", loaded.SnapshotOps());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void A_full_water_op_round_trips_through_a_saved_session()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rf_collab_water_" + Guid.NewGuid().ToString("N"));
        try
        {
            var w = new CollabWorldState();
            w.ApplyOp(FullWater);
            w.Save(dir);

            var back = CollabWorldState.Load(dir);
            Assert.NotNull(back);
            Assert.Equal(FullWater, back!.Water);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---- a level-local object's files ---------------------------------------------------------------------------

    private static string LvlFile(string template, string tag) =>
        $"LVLFILE {template} {Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(tag))}";

    [Fact]
    public void A_placed_sounds_files_reach_a_late_joiner()
    {
        var w = new CollabWorldState();
        Assert.True(w.ApplyOp(LvlFile("Snd_river", "wav-bytes")));
        Assert.Contains(LvlFile("Snd_river", "wav-bytes"), w.SnapshotOps());
    }

    [Fact]
    public void Re_importing_a_template_replaces_its_files_rather_than_stacking_them()
    {
        var w = new CollabWorldState();
        w.ApplyOp(LvlFile("Snd_river", "first"));
        w.ApplyOp(LvlFile("Snd_river", "second"));
        w.ApplyOp(LvlFile("Decal_sign", "other"));
        var ops = w.SnapshotOps().Where(o => o.StartsWith("LVLFILE")).ToList();
        Assert.Equal(2, ops.Count);
        Assert.Contains(ops, o => o == LvlFile("Snd_river", "second"));
    }

    [Fact]
    public void Files_arrive_before_anything_that_places_them()
    {
        // The joiner replays these in order; a placement whose files have not landed yet resolves to nothing.
        var w = new CollabWorldState();
        w.ApplyOp(LvlFile("Snd_river", "wav"));
        w.ApplyOp("OBJMESH Decal_sign " + Convert.ToBase64String(new byte[] { 1, 2, 3 }));
        var ops = w.SnapshotOps().ToList();
        Assert.True(ops.FindIndex(o => o.StartsWith("LVLFILE")) < ops.FindIndex(o => o.StartsWith("OBJMESH")));
    }

    [Fact]
    public void Level_files_round_trip_through_a_saved_session()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rf_collab_files_" + Guid.NewGuid().ToString("N"));
        try
        {
            var w = new CollabWorldState();
            w.ApplyOp(LvlFile("Snd_river", "wav-bytes"));
            w.Save(dir);

            var back = CollabWorldState.Load(dir);
            Assert.NotNull(back);
            Assert.Contains(LvlFile("Snd_river", "wav-bytes"), back!.SnapshotOps());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void A_relay_holding_only_these_is_still_worth_seeding_from()
    {
        // Any gates whether the relay bothers persisting/replaying at all; a session whose only edits were water or
        // an imported sound would otherwise look empty and seed a joiner with nothing.
        var water = new CollabWorldState(); water.ApplyOp(FullWater);
        Assert.True(water.Any);
        var files = new CollabWorldState(); files.ApplyOp(LvlFile("Snd_river", "wav"));
        Assert.True(files.Any);
    }
}
