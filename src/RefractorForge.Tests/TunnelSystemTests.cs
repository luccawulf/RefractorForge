using System.Numerics;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// BfVietnam 1.2's tunnel system, as reverse-engineered from Operation Cedar Falls and Saigon68 and from the
/// strings Battlecraft Vietnam and BfVietnam.exe carry:
///
///   * a HOLE in the terrain is a heightmap sample of exactly 0 (Cedar Falls: one cell under each hut, hole
///     and bunker entrance; Saigon68: pairs of cells under each sewer entrance), drawn and collided by the
///     engine only while <c>Game.isTunnelMap 1</c>;
///   * the tunnel mesh is a normal placed object whose template says <c>isBelowGround 1</c>, the entrances say
///     <c>isEntryPoint 1</c> (or carry an <c>entrance</c> child that does), and <c>Game.entryPointRadius</c> is
///     how close a soldier has to be to pass through;
///   * <c>mapManager.addObjectMap &lt;template&gt; &lt;MapName&gt; x/z/w/h</c> binds Textures/&lt;MapName&gt;.dds as the
///     minimap over that world rectangle while the player is inside the object.
///
/// Battlecraft wrote <c>game.isTunnelMap 0</c> and two dummy object maps into EVERY custom map, which is why
/// tunnels built with it never worked; these pin that the editor writes the working form.
/// </summary>
public class TunnelSystemTests
{
    private static readonly string[] CedarFallsInit =
    {
        "renderer.fogstart 50",
        "renderer.fogend 300",
        "",
        "Game.ViewDistance 350",
        "Game.isTunnelMap 1",
        "Game.useBelowGroundCulling 1",
        "Game.entryPointRadius 3.5",
        "",
        "mapManager.addObjectMap o_tunnelsA TunnelsAMap 886/871/328/327",
        "run Init/Terrain",
    };

    [Fact]
    public void Cedar_Falls_tunnel_settings_parse()
    {
        var e = EnvironmentSettings.Parse(null, null, CedarFallsInit);
        Assert.True(e.IsTunnelMap);
        Assert.True(e.UseBelowGroundCulling);
        Assert.Equal(3.5f, e.EntryPointRadius);
        var m = Assert.Single(e.ObjectMaps);
        Assert.Equal("o_tunnelsA", m.Template);
        Assert.Equal("TunnelsAMap", m.MapName);
        Assert.Equal((886f, 871f, 328f, 327f), (m.X, m.Z, m.Width, m.Height));
        Assert.Equal("mapManager.addObjectMap o_tunnelsA TunnelsAMap 886/871/328/327", m.ToConLine());
    }

    [Fact]
    public void Untouched_settings_leave_Init_con_exactly_as_it_was()
    {
        var e = EnvironmentSettings.Parse(null, null, CedarFallsInit);
        Assert.Equal(CedarFallsInit, e.PatchInitConLines(CedarFallsInit));
    }

    /// <summary>The Battlecraft shape every custom map carries, turned into the working shape.</summary>
    [Fact]
    public void Battlecrafts_disabled_lines_are_rewritten_into_a_working_tunnel_map()
    {
        string[] bcv =
        {
            "renderer.fogend 100",
            "game.isTunnelMap 0",
            "game.useBelowGroundCulling 0",
            "mapManager.addObjectMap o_sewers_A_M1 tunnelmap 0/0/256/256",
            "mapManager.addObjectMap o_tunnelsa tunnelmap 0/0/256/256",
            "run Init/Terrain",
        };
        var e = EnvironmentSettings.Parse(null, null, bcv);
        Assert.False(e.IsTunnelMap);
        Assert.Equal(2, e.ObjectMaps.Count);

        e.IsTunnelMap = true; e.UseBelowGroundCulling = true; e.EntryPointRadius = 3.5f; e.WriteTunnel = true;
        e.ObjectMaps.Clear();
        e.ObjectMaps.Add(new EnvironmentSettings.ObjectMap("o_tunnelsA", "o_tunnelsAMap", 100.5f, 200f, 150f, 160f));
        var outLines = e.PatchInitConLines(bcv);

        Assert.Equal(new[]
        {
            "renderer.fogend 100",
            "Game.entryPointRadius 3.5",
            "Game.isTunnelMap 1",
            "mapManager.addObjectMap o_tunnelsA o_tunnelsAMap 100.5/200/150/160",
            "Game.useBelowGroundCulling 1",
            "run Init/Terrain",
        }, outLines);

        // Reading our own output back gives the same settings: the round trip is closed.
        var again = EnvironmentSettings.Parse(null, null, outLines);
        Assert.True(again.IsTunnelMap && again.UseBelowGroundCulling);
        Assert.Equal(3.5f, again.EntryPointRadius);
        Assert.Equal(e.ObjectMaps, again.ObjectMaps);
    }

    [Fact]
    public void A_level_without_the_lines_gets_them_added_once()
    {
        string[] plain = { "renderer.fogstart 50", "run Init/Terrain" };
        var e = EnvironmentSettings.Parse(null, null, plain);
        e.IsTunnelMap = true; e.UseBelowGroundCulling = true; e.WriteTunnel = true;
        e.ObjectMaps.Add(new EnvironmentSettings.ObjectMap("o_sewers_A_M1", "SewersAMap", 1, 2, 3, 4));
        var once = e.PatchInitConLines(plain);
        Assert.Contains("Game.isTunnelMap 1", once);
        Assert.Contains("Game.useBelowGroundCulling 1", once);
        Assert.Contains("Game.entryPointRadius 3.5", once);
        Assert.Single(once, l => l.StartsWith("mapManager.addObjectMap"));
        Assert.Equal(once, e.PatchInitConLines(once));       // idempotent
        Assert.Equal("run Init/Terrain", once[^1]);
    }

    [Fact]
    public void Switching_the_system_off_drops_the_object_maps_but_keeps_the_flags_explicit()
    {
        var e = EnvironmentSettings.Parse(null, null, CedarFallsInit);
        e.IsTunnelMap = false; e.WriteTunnel = true;
        var outLines = e.PatchInitConLines(CedarFallsInit);
        Assert.Contains("Game.isTunnelMap 0", outLines);
        Assert.DoesNotContain(outLines, l => l.StartsWith("mapManager.addObjectMap"));
        Assert.DoesNotContain(outLines, l => l.StartsWith("Game.entryPointRadius"));
    }

    // ---- Holes in the terrain ----

    private static (Heightmap, TerrainConfig) World(int side = 16, float metres = 20f)
    {
        var cfg = new TerrainConfig { MaterialSize = side, WorldSize = side * 4, YScale = 1f };
        var hm = new Heightmap(side, side);
        ushort raw = cfg.MetersToRaw(metres);
        for (int i = 0; i < hm.Samples.Length; i++) hm.Samples[i] = raw;
        return (hm, cfg);
    }

    [Fact]
    public void A_zero_sample_removes_every_triangle_that_touches_it_only_when_asked()
    {
        var (hm, cfg) = World();
        hm[5, 5] = 0;
        var plain = TerrainMesh.FromHeightmap(hm, cfg, 1);
        var holed = TerrainMesh.FromHeightmap(hm, cfg, 1, holes: true);
        Assert.Equal(15 * 15 * 6, plain.Indices.Length);
        // The sample is a corner of four cells; the two diagonal triangles that avoid it survive in the
        // diagonal-neighbouring cells, so six of the eight triangles around it go.
        Assert.Equal(15 * 15 * 6 - 6 * 3, holed.Indices.Length);
        int v = 5 * holed.GridW + 5;
        Assert.DoesNotContain(v, holed.Indices);
        Assert.Contains(v, plain.Indices);
    }

    [Fact]
    public void The_hole_brush_punches_and_the_fill_brush_closes_from_the_rim()
    {
        var (hm, cfg) = World();
        var ed = new TerrainEditor(hm, cfg);
        var stroke = ed.BeginStroke();
        var brush = new TerrainBrush(BrushMode.Hole, 5f, 1f, BrushFalloff.Smooth, null, null, Square: false);
        stroke.Dab(32f, 32f, brush);          // grid (8,8), 1.25 cells across
        var edit = stroke.Finish();
        Assert.NotNull(edit);
        Assert.Equal(0, hm[8, 8]);
        Assert.NotEqual(0, hm[8, 12]);         // hard edge: outside the radius nothing changed
        int holes = hm.Samples.Count(s => s == 0);
        Assert.InRange(holes, 1, 9);

        // Undo restores every sample; redo punches them again.
        edit!.Undo(hm);
        Assert.Equal(0, hm.Samples.Count(s => s == 0));
        edit.Redo(hm);
        Assert.Equal(holes, hm.Samples.Count(s => s == 0));

        var fill = ed.BeginStroke();
        fill.Dab(32f, 32f, brush with { Mode = BrushMode.FillHole, RadiusMeters = 12f });
        Assert.NotNull(fill.Finish());
        Assert.Equal(0, hm.Samples.Count(s => s == 0));
        Assert.Equal(cfg.MetersToRaw(20f), hm[8, 8]);      // borrowed from the flat ground around it
    }

    [Fact]
    public void SetHole_is_one_cell_and_is_recorded_for_undo()
    {
        var (hm, cfg) = World();
        var ed = new TerrainEditor(hm, cfg);
        var s = ed.BeginStroke();
        s.SetHole(3, 4, true);
        var e = s.Finish();
        Assert.NotNull(e);
        Assert.Equal(0, hm[3, 4]);
        Assert.Equal(1, hm.Samples.Count(v => v == 0));
        e!.Undo(hm);
        Assert.Equal(0, hm.Samples.Count(v => v == 0));
    }

    // ---- The underground map ----

    private static MeshLibrary.Mesh Box(float w, float h, float d)
    {
        // A corridor: a box with its floor at y = -h, open ends left out (four faces are plenty for a plan).
        var p = new[]
        {
            new Vector3(-w, -h, -d), new Vector3(w, -h, -d), new Vector3(w, -h, d), new Vector3(-w, -h, d),   // floor
            new Vector3(-w,  0, -d), new Vector3(w,  0, -d), new Vector3(w,  0, d), new Vector3(-w,  0, d),   // roof
        };
        var idx = new[] { 0, 1, 2, 0, 2, 3, 4, 6, 5, 4, 7, 6 };
        return new MeshLibrary.Mesh(p, new Vector2[p.Length], new[] { new MeshLibrary.MaterialPart(idx, Vector3.One, null, false) });
    }

    [Fact]
    public void The_world_rectangle_follows_the_placed_mesh_through_its_rotation()
    {
        var mesh = Box(10f, 3f, 2f);
        var world = Matrix4x4.CreateFromYawPitchRoll(MathF.PI / 2f, 0, 0) * Matrix4x4.CreateTranslation(100f, 20f, 200f);
        var r = TunnelMap.WorldRect(mesh, world);
        // Yawed 90 degrees, the 20 m length now runs along Z.
        Assert.Equal(98f, r.X, 2); Assert.Equal(190f, r.Z, 2);
        Assert.Equal(4f, r.W, 2); Assert.Equal(20f, r.H, 2);
    }

    [Fact]
    public void The_map_shows_the_floor_light_on_a_dark_ground_north_up()
    {
        var mesh = Box(10f, 3f, 2f);
        var world = Matrix4x4.CreateTranslation(100f, 20f, 200f);
        var rect = TunnelMap.Union(new[] { TunnelMap.WorldRect(mesh, world) });
        var tex = TunnelMap.Render(new[] { (mesh, world) }, rect, 64);
        Assert.Equal(64, tex.Width);

        byte At(float wx, float wz, int c)
        {
            int px = (int)((wx - rect.X) / rect.W * 64), py = (int)((rect.Z + rect.H - wz) / rect.H * 64);
            return tex.Rgba[(py * 64 + px) * 4 + c];
        }
        // Inside the corridor the FLOOR (lowest surface) is drawn, and it is far lighter than the ground around.
        Assert.True(At(100f, 200f, 0) > 150, "floor should be light");
        Assert.True(At(rect.X + 0.5f, rect.Z + 0.5f, 0) < 80, "outside the mesh is the dark parchment");
        // North (+Z) is at the TOP of the image: the far-north edge of the corridor lands above its south edge.
        int rowNorth = (int)((rect.Z + rect.H - (200f + 1.5f)) / rect.H * 64);
        int rowSouth = (int)((rect.Z + rect.H - (200f - 1.5f)) / rect.H * 64);
        Assert.True(rowNorth < rowSouth);
    }

    // ---- The ground light bake, done as the game will show it ----

    [Fact]
    public void The_pool_is_a_ratio_against_the_scene_light_not_an_addition()
    {
        var atlas = new Texture2D(4, 4, new byte[4 * 4 * 4]);
        for (int i = 0; i < 16; i++) { atlas.Rgba[i * 4] = 100; atlas.Rgba[i * 4 + 1] = 60; atlas.Rgba[i * 4 + 2] = 20; atlas.Rgba[i * 4 + 3] = 255; }
        var light = new Texture2D(4, 4, new byte[4 * 4 * 4]);
        for (int i = 0; i < 16; i++) { light.Rgba[i * 4] = 51; light.Rgba[i * 4 + 1] = 51; light.Rgba[i * 4 + 2] = 51; light.Rgba[i * 4 + 3] = 255; }   // 0.2 white
        var scene = new float[4 * 4 * 3];
        Array.Fill(scene, 0.2f);                                  // a dim night scene

        LightBake.MultiplyIntoAtlas(atlas, light, scene, 4, 1f);

        // 0.2 of light over a 0.2 scene doubles the texture: the ground keeps its own colour, just brighter.
        Assert.Equal((byte)200, atlas.Rgba[0]);
        Assert.Equal((byte)120, atlas.Rgba[1]);
        Assert.Equal((byte)40, atlas.Rgba[2]);

        // The same lamp under full daylight barely registers - which is what the game would show.
        var day = new Texture2D(4, 4, new byte[4 * 4 * 4]);
        for (int i = 0; i < 16; i++) { day.Rgba[i * 4] = 100; day.Rgba[i * 4 + 3] = 255; }
        Array.Fill(scene, 1.0f);
        LightBake.MultiplyIntoAtlas(day, light, scene, 4, 1f);
        Assert.Equal((byte)120, day.Rgba[0]);
    }

    [Fact]
    public void Scene_light_is_ambient_plus_diffuse_against_the_slope()
    {
        var (hm, cfg) = World(16, 20f);
        var flat = LightBake.SceneLight(hm, cfg, 8, new Vec3(0.1f, 0.1f, 0.1f), new Vec3(0.5f, 0.5f, 0.5f), new Vec3(0, 1, 0));
        Assert.Equal(0.6f, flat[0], 3);
        // Sun on the horizon lights a flat floor not at all: ambient only.
        var grazing = LightBake.SceneLight(hm, cfg, 8, new Vec3(0.1f, 0.1f, 0.1f), new Vec3(0.5f, 0.5f, 0.5f), new Vec3(1, 0, 0));
        Assert.Equal(0.1f, grazing[0], 3);
    }
}
