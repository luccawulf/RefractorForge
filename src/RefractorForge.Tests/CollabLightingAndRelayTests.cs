using RefractorForge.Collab;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Lighting over the wire, and the relay as its own program.
///
/// Placed lights were never synced, yet every bake reads them and they are saved as a sidecar - so a peer baked a
/// different map and the next save from the other machine dropped the lights. A bake itself travels as a trigger:
/// its output for a map runs to tens of MB, but every input is synced, so the peer re-runs the same bake instead.
/// </summary>
public class CollabLightingAndRelayTests
{
    private static LightRig Rig()
    {
        var r = new LightRig { NightAmount = 0.4f };
        r.Lights.Add(new PointLight { Name = "Porch", Position = new Vec3(10.5f, 3f, -20.25f), Radius = 18f, Intensity = 1.5f, ColorR = 1f, ColorG = 0.8f, ColorB = 0.5f, Falloff = 2f, CastsShadows = true });
        r.Lights.Add(new PointLight { Name = "Off", Position = new Vec3(0f, 0f, 0f), Enabled = false });
        return r;
    }

    [Fact]
    public void A_rig_survives_the_trip_as_text()
    {
        var back = LightRig.FromJson(Rig().ToJson());
        Assert.Equal(2, back.Lights.Count);
        Assert.Equal(0.4f, back.NightAmount, 4);
        var l = back.Lights[0];
        Assert.Equal("Porch", l.Name);
        Assert.Equal(10.5f, l.Position.X, 4); Assert.Equal(-20.25f, l.Position.Z, 4);
        Assert.Equal(18f, l.Radius, 4); Assert.Equal(1.5f, l.Intensity, 4);
        Assert.Equal(0.8f, l.ColorG, 4); Assert.True(l.CastsShadows);
        Assert.False(back.Lights[1].Enabled);
    }

    [Fact]
    public void The_same_rig_serialises_to_the_same_text()
    {
        // The editor sends the rig when its text changes, and records the text it received. If a received rig
        // re-serialised differently the receiver would bounce it straight back, and two peers would volley forever.
        var once = Rig().ToJson();
        Assert.Equal(once, LightRig.FromJson(once).ToJson());
    }

    [Fact]
    public void Damaged_rig_text_gives_an_empty_rig_not_a_crash()
    {
        Assert.Empty(LightRig.FromJson("{ not json").Lights);
        Assert.Empty(LightRig.FromJson("").Lights);
    }

    // ---- what the relay keeps ------------------------------------------------------------------------------------

    private static string RigOp() => "LIGHTRIG " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Rig().ToJson()));
    private const string BakeOp = "LIGHTBAKE 1 0 1 1 1 1.25";

    [Fact]
    public void Lights_and_the_bake_reach_a_late_joiner_in_the_right_order()
    {
        var w = new CollabWorldState();
        Assert.True(w.ApplyOp(BakeOp));
        Assert.True(w.ApplyOp(RigOp()));
        Assert.True(w.ApplyOp("LIGHT 45 30 1 0.2 0.2 0.2 0.3 0.3 0.3 1 1 1 0.5 0.5 0.5"));
        var ops = w.SnapshotOps().ToList();
        int light = ops.FindIndex(o => o.StartsWith("LIGHT ")), rig = ops.FindIndex(o => o.StartsWith("LIGHTRIG ")), bake = ops.FindIndex(o => o.StartsWith("LIGHTBAKE "));
        Assert.True(light >= 0 && rig >= 0 && bake >= 0);
        // the joiner re-runs the bake, so it must arrive after every input it reads - the sun and the lights included
        Assert.True(light < bake && rig < bake, $"order was light={light} rig={rig} bake={bake}");
        Assert.Equal(ops.Count - 1, bake);
    }

    [Fact]
    public void A_later_bake_replaces_the_earlier_one()
    {
        var w = new CollabWorldState();
        w.ApplyOp("LIGHTBAKE 1 0 0 0 0 1");
        w.ApplyOp(BakeOp);
        Assert.Single(w.SnapshotOps().Where(o => o.StartsWith("LIGHTBAKE")));
        Assert.Contains(BakeOp, w.SnapshotOps());
    }

    [Fact]
    public void Lights_and_bake_round_trip_through_a_saved_session()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rf_collab_lighting_" + Guid.NewGuid().ToString("N"));
        try
        {
            var w = new CollabWorldState();
            w.ApplyOp(RigOp()); w.ApplyOp(BakeOp);
            w.Save(dir);
            var back = CollabWorldState.Load(dir);
            Assert.NotNull(back);
            Assert.Equal(RigOp(), back!.LightRig);
            Assert.Equal(BakeOp, back.LightBake);
            Assert.True(back.Any);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---- the server's command line -------------------------------------------------------------------------------

    [Fact]
    public void Defaults_when_started_bare()
    {
        var o = RelayOptions.Parse(Array.Empty<string>(), out var err);
        Assert.Null(err);
        Assert.Equal(7777, o.Port); Assert.Null(o.SeedPath); Assert.Null(o.SavePath); Assert.Null(o.Password);
    }

    [Fact]
    public void The_editors_old_relay_form_still_parses()
    {
        // RefractorForge.exe --relay 7800 D:\Levels\Hue --save C:\state --pass hunter2  - what the docs have said.
        var o = RelayOptions.Parse(new[] { "7800", @"D:\Levels\Hue", "--save", @"C:\state", "--pass", "hunter2" }, out var err);
        Assert.Null(err);
        Assert.Equal(7800, o.Port); Assert.Equal(@"D:\Levels\Hue", o.SeedPath);
        Assert.Equal(@"C:\state", o.SavePath); Assert.Equal("hunter2", o.Password);
    }

    [Fact]
    public void A_seed_alone_is_a_seed_not_a_port()
    {
        var o = RelayOptions.Parse(new[] { "/srv/levels/al_vietnas.rfa" }, out var err);
        Assert.Null(err);
        Assert.Equal(7777, o.Port); Assert.Equal("/srv/levels/al_vietnas.rfa", o.SeedPath);
    }

    [Fact]
    public void Named_port_and_bind_address()
    {
        var o = RelayOptions.Parse(new[] { "--port", "9000", "--bind", "10.0.0.5" }, out var err);
        Assert.Null(err);
        Assert.Equal(9000, o.Port); Assert.Equal(System.Net.IPAddress.Parse("10.0.0.5"), o.Bind);
    }

    [Theory]
    [InlineData("--save")]                 // a flag with nothing after it
    [InlineData("--bogus")]                // a typo must not start an open, unpersisted server
    [InlineData("70000")]                  // not a port
    [InlineData("--bind not-an-address")]
    [InlineData("7777 seed extra")]
    public void Mistakes_are_errors_not_silent_defaults(string line)
    {
        RelayOptions.Parse(line.Split(' '), out var err);
        Assert.NotNull(err);
    }

    [Fact]
    public void Help_is_help()
    {
        Assert.True(RelayOptions.Parse(new[] { "--help" }, out _).Help);
        Assert.Contains("--save", RelayOptions.Usage);
    }
}
