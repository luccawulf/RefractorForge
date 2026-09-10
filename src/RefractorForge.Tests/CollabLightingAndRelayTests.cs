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
    public void RF_PASS_supplies_the_password_when_no_flag_is_given()
    {
        // systemd expands ${RF_PASS} in ExecStart before exec, so a --pass in the unit still lands in argv and
        // `ps` shows the join password to every user on the box. Reading the environment is what avoids that.
        var prev = Environment.GetEnvironmentVariable("RF_PASS");
        try
        {
            Environment.SetEnvironmentVariable("RF_PASS", "from-the-unit-file");
            var o = RelayOptions.Parse(Array.Empty<string>(), out var err);
            Assert.Null(err);
            Assert.Equal("from-the-unit-file", o.Password);

            // An explicit flag still wins, so every command line that worked before behaves the same.
            var f = RelayOptions.Parse(new[] { "--pass", "typed-here" }, out _);
            Assert.Equal("typed-here", f.Password);

            Environment.SetEnvironmentVariable("RF_PASS", "");
            Assert.Null(RelayOptions.Parse(Array.Empty<string>(), out _).Password);
        }
        finally { Environment.SetEnvironmentVariable("RF_PASS", prev); }
    }

    [Fact]
    public void Backup_cadence_has_defaults_and_is_settable()
    {
        var d = RelayOptions.Parse(Array.Empty<string>(), out _);
        Assert.Equal(5, d.BackupMinutes);
        Assert.Equal(12, d.KeepBackups);

        // A team spread across timezones wants days of history, not an hour: 30 min x 336 is a week of activity.
        var o = RelayOptions.Parse(new[] { "--backup-every", "30", "--keep-backups", "336" }, out var err);
        Assert.Null(err);
        Assert.Equal(30, o.BackupMinutes);
        Assert.Equal(336, o.KeepBackups);
    }

    [Theory]
    [InlineData("--backup-every 0")]        // an interval of zero would back up on every tick
    [InlineData("--backup-every 4000")]     // longer than a day is a typo, not an intention
    [InlineData("--backup-every soon")]
    [InlineData("--keep-backups 0")]        // keeping none would delete the snapshot it just took
    [InlineData("--keep-backups lots")]
    public void Nonsense_backup_settings_are_errors(string line)
    {
        RelayOptions.Parse(line.Split(' '), out var err);
        Assert.NotNull(err);
    }

    [Fact]
    public void Backups_also_prune_on_total_size_and_never_drop_the_last_one()
    {
        // The count alone is not a bound on disk. A session holding only objects is a couple of hundred KB; one
        // that has had terrain and material maps synced in is megabytes, and the same retention is then GB.
        string dir = Path.Combine(Path.GetTempPath(), "rf_backup_size_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "Heightmap.raw"), new byte[400 * 1024]);
            var backups = Path.Combine(dir, "_backups");
            Directory.CreateDirectory(backups);
            for (int i = 1; i <= 5; i++)
            {
                var b = Path.Combine(backups, $"20260101_00000{i}");
                Directory.CreateDirectory(b);
                File.WriteAllBytes(Path.Combine(b, "Heightmap.raw"), new byte[400 * 1024]);
            }

            // Six snapshots of 400 KB is ~2.4 MB; a 1 MB budget must cut it down, though the count allows 50.
            RelayHost.BackupState(dir, keep: 50, maxMb: 1);

            var left = Directory.EnumerateDirectories(backups).ToList();
            long total = left.Sum(d => new DirectoryInfo(d).EnumerateFiles().Sum(f => f.Length));
            Assert.True(total <= 1024 * 1024, $"backups still {total} bytes");
            Assert.NotEmpty(left);

            // A budget smaller than ONE snapshot still leaves exactly one standing: no backup at all is worse
            // than being over budget. Grow the state past the cap so the snapshot it is about to take exceeds it.
            File.WriteAllBytes(Path.Combine(dir, "Heightmap.raw"), new byte[2 * 1024 * 1024]);
            RelayHost.BackupState(dir, keep: 50, maxMb: 1);
            Assert.Single(Directory.EnumerateDirectories(backups));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Backups_prune_to_the_retention_and_keep_the_newest()
    {
        // BackupState stamps folders yyyyMMdd_HHmmss and prunes by ordinal sort, so what survives must be the
        // most recent ones. Pre-made stamps stand in for real runs, which would need a clock to separate.
        string dir = Path.Combine(Path.GetTempPath(), "rf_backup_prune_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "StaticObjects.con"), "rem state");
            var backups = Path.Combine(dir, "_backups");
            Directory.CreateDirectory(backups);
            for (int i = 1; i <= 6; i++) Directory.CreateDirectory(Path.Combine(backups, $"20260101_00000{i}"));

            RelayHost.BackupState(dir, keep: 3);

            var left = Directory.EnumerateDirectories(backups).Select(Path.GetFileName)
                                .OrderBy(x => x, StringComparer.Ordinal).ToList();
            Assert.Equal(3, left.Count);
            Assert.Contains("20260101_000006", left);           // newest of the pre-made ones survives
            Assert.DoesNotContain("20260101_000001", left);     // oldest is gone
            // The snapshot just taken is one of the three, and it carries the state file.
            var newest = Path.Combine(backups, left[^1]);
            Assert.True(File.Exists(Path.Combine(newest, "StaticObjects.con")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
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
