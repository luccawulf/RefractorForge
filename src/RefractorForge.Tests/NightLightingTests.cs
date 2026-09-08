using System;
using System.Linq;
using System.Numerics;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Night lighting is BAKED - neither engine has a runtime point light - so what these check is the bake: that a spot
/// is a cone, that a lamp's shadow stops at the lamp and softens with its size, that a lamp's colour survives into a
/// 24-bit lightmap for BF1942, and that the glow object is the additive recipe the retail lamps use.
/// </summary>
public class NightLightingTests
{
    private static (Heightmap Hm, TerrainConfig Cfg) Flat(int side = 64)
    {
        var cfg = new TerrainConfig { WorldSize = side, MaterialSize = side, YScale = 1f, WaterLevel = -1000f };
        return (new Heightmap(side, side), cfg);
    }

    [Fact]
    public void A_spot_lights_inside_its_cone_and_nothing_outside()
    {
        var l = new PointLight { Position = new Vec3(0, 10, 0), Radius = 40f, Intensity = 1f, Kind = 1, SpotPitchDeg = -90f, ConeDeg = 60f, ConeSoft = 0f };
        Assert.True(l.Attenuation(0f, 0f, 0f) > 0.3f, "straight below the spot is lit");
        Assert.Equal(0f, l.Attenuation(20f, 0f, 0f));            // 63 degrees off axis, outside a 30-degree half-cone
        l.Kind = 0;
        Assert.True(l.Attenuation(20f, 0f, 0f) > 0f, "the same light as a point reaches it");
    }

    [Fact]
    public void A_sidecar_from_before_spots_existed_still_loads()
    {
        // The fields added for spots, glows and soft shadows are all optional: an old rig gets the old behaviour.
        var rig = LightRig.FromJson("{\"Lights\":[{\"Name\":\"old\",\"Position\":{\"X\":1,\"Y\":2,\"Z\":3},\"Radius\":20,\"Intensity\":1}],\"NightAmount\":0.5}");
        var l = Assert.Single(rig.Lights);
        Assert.Equal(0, l.Kind);
        Assert.True(l.Glow);
        Assert.True(l.OnGround && l.OnObjects);
        Assert.Equal(0.3f, l.SourceSize, 3);
        Assert.Equal(-90f, l.SpotPitchDeg, 3);
    }

    // A wall: a vertical quad at x = wallX spanning z 26..38 and y 0..6, both windings so it blocks from either side.
    private static System.Collections.Generic.List<(Vector3, Vector3, Vector3)> Wall(float wallX)
    {
        Vector3 a = new(wallX, 0, 26), b = new(wallX, 0, 38), c = new(wallX, 6, 38), d = new(wallX, 6, 26);
        return new() { (a, b, c), (a, c, d), (a, c, b), (a, d, c) };
    }

    [Fact]
    public void Segment_occlusion_stops_at_the_light()
    {
        var occ = MeshOccluder.Build(Wall(5f))!;
        var cur = occ.NewCursor();
        var from = new Vector3(0, 1, 32);
        Assert.True(occ.Occluded(from, Vector3.UnitX, 10f, cur), "a wall between the point and a light 10 m away");
        Assert.False(occ.Occluded(from, Vector3.UnitX, 3f, cur), "the same wall BEYOND a light 3 m away is not in the way");
    }

    [Fact]
    public void Ground_bake_puts_a_shadow_behind_a_wall_and_softens_it_with_source_size()
    {
        var (hm, cfg) = Flat();
        // Lamp at (32,4,32); wall at x = 36 spanning z 26..38. Seen from the lamp, the wall's shadow at x = 44.5 runs
        // from z = 32 - 1.5 * 12.5 = 13.25 to z = 50.75; (44, 32) is deep inside it, (44, 6) is well clear, and
        // (44, 13) sits a quarter of a metre inside the edge.
        var lamp = new PointLight { Position = new Vec3(32, 4, 32), Radius = 60f, Intensity = 1f, Falloff = 1f, ColorR = 1, ColorG = 1, ColorB = 1 };
        var rig = new LightRig { Lights = { lamp } };
        var scene = NightBake.Build(hm, cfg, Wall(36f));

        lamp.SourceSize = 0f;
        var hard = NightBake.BakeGround(scene, rig, 64, 1);
        float Lit(Texture2D t, int x, int z) => t.Rgba[(z * 64 + x) * 4] / 255f;
        Assert.True(Lit(hard, 28, 32) > 0.3f, $"in front of the wall is lit ({Lit(hard, 28, 32):0.00})");
        Assert.Equal(0f, Lit(hard, 44, 32));                       // behind the wall, level with the lamp: black
        Assert.True(Lit(hard, 44, 6) > 0.05f, $"past the wall's end the ground is lit again ({Lit(hard, 44, 6):0.00})");
        Assert.Equal(0f, Lit(hard, 44, 13));                       // a hard shadow: a quarter metre in is fully dark

        // A real source turns the hard edge into a penumbra: the same texel just inside the shadow gets SOME light,
        // while the deep shadow and the clear ground are unchanged.
        lamp.SourceSize = 1.5f;
        var soft = NightBake.BakeGround(scene, rig, 64, 8);
        float edge = Lit(soft, 44, 13);
        Assert.True(edge > 0f && edge < Lit(soft, 44, 6), $"the shadow edge is partial with a wide source ({edge:0.00} vs clear {Lit(soft, 44, 6):0.00})");
        Assert.Equal(0f, Lit(soft, 44, 32));                       // deep in the shadow it is still black
    }

    [Fact]
    public void Rgb_lightmap_round_trips_and_carries_the_Bespin_header()
    {
        var px = new byte[4 * 4 * 4];
        for (int i = 0; i < 16; i++) { px[i * 4] = (byte)(i * 16); px[i * 4 + 1] = (byte)(255 - i * 16); px[i * 4 + 2] = (byte)(i * 7); px[i * 4 + 3] = 255; }
        var tex = new Texture2D(4, 4, px);
        var tga = TgaTexture.EncodeRgb24(tex);
        // Measured off GC_Bespin_Night's night maps: type 2, no colour map, 24 bpp, descriptor 0.
        Assert.Equal(2, tga[2]); Assert.Equal(0, tga[1]); Assert.Equal(24, tga[16]); Assert.Equal(0, tga[17]);
        Assert.Equal(18 + 4 * 4 * 3, tga.Length);
        var back = TgaTexture.Decode(tga)!;
        for (int i = 0; i < 16; i++)
        {
            Assert.Equal(px[i * 4], back.Rgba[i * 4]);
            Assert.Equal(px[i * 4 + 1], back.Rgba[i * 4 + 1]);
            Assert.Equal(px[i * 4 + 2], back.Rgba[i * 4 + 2]);
        }
    }

    // A floor quad at y = 0 (left half of the lightmap) under a roof at y = 5 (right half), both facing up. The roof
    // keeps the sun off the floor, so under it a lamp is the only light there is.
    private static MeshLibrary.Mesh RoofOverFloor()
    {
        var pos = new Vector3[]
        {
            new(0, 0, 0), new(8, 0, 0), new(8, 0, 8), new(0, 0, 8),
            new(0, 5, 0), new(8, 5, 0), new(8, 5, 8), new(0, 5, 8),
        };
        var uv = new Vector2[8];
        var lm = new Vector2[] { new(0.02f, 0.02f), new(0.48f, 0.02f), new(0.48f, 0.98f), new(0.02f, 0.98f),
                                 new(0.52f, 0.02f), new(0.98f, 0.02f), new(0.98f, 0.98f), new(0.52f, 0.98f) };
        int[] idx = { 0, 2, 1, 0, 3, 2, 4, 6, 5, 4, 7, 6 };
        var part = new MeshLibrary.MaterialPart(idx, Vector3.One, null, false);
        return new MeshLibrary.Mesh(pos, uv, new[] { part }) { LightmapUvs = lm };
    }

    [Fact]
    public void Object_lightmap_keeps_a_lamps_colour_only_when_asked()
    {
        var (hm, cfg) = Flat(16);
        var mesh = RoofOverFloor();
        var red = new PointLight { Position = new Vec3(4, 3, 4), Radius = 12f, Intensity = 1f, ColorR = 1f, ColorG = 0.1f, ColorB = 0.1f, SourceSize = 0f };
        var rig = new LightRig { Lights = { red } };
        var scene = NightBake.Build(hm, cfg, null);
        var sunOverhead = new Vec3(0, 1, 0);
        var colour = ObjectLightmapBaker.Bake(mesh, Matrix4x4.Identity, hm, cfg, sunOverhead, 32, ambient: 0f, rig: rig, night: scene, colour: true)!;
        var grey = ObjectLightmapBaker.Bake(mesh, Matrix4x4.Identity, hm, cfg, sunOverhead, 32, ambient: 0f, rig: rig, night: scene, colour: false)!;
        int c = (16 * 32 + 8) * 4;    // the floor's middle texel (left half of the atlas), right under the lamp
        int roof = (16 * 32 + 24) * 4;
        Assert.Equal(255, colour.Rgba[roof]);                       // the roof top sees the sun: full, white
        Assert.Equal(255, colour.Rgba[roof + 2]);
        Assert.True(colour.Rgba[c] > 100, $"red channel lit ({colour.Rgba[c]})");
        Assert.True(colour.Rgba[c] > colour.Rgba[c + 1] * 3, "a red lamp stays red in a 24-bit map");
        Assert.Equal(grey.Rgba[c], grey.Rgba[c + 1]);
        Assert.Equal(grey.Rgba[c], grey.Rgba[c + 2]);
        Assert.True(grey.Rgba[c] > 0 && grey.Rgba[c] < colour.Rgba[c], "grey is the luma of the colour, so a red lamp is dimmer in it");
    }

    [Fact]
    public void Glow_object_is_the_additive_recipe_with_a_stable_name()
    {
        var colour = new Vec3(1f, 0.72f, 0.36f);
        var built = LampGlow.Build("Test_Level", colour, 2f, 1f, "bf1942", rgba => new byte[] { 1, 2, 3 });
        Assert.StartsWith(LampGlow.Prefix, built.Template);
        Assert.Equal(built.Template, LampGlow.TemplateName(colour, 2f, 1f));
        Assert.True(LampGlow.IsGlow(built.Template));
        var names = built.Files.Select(f => f.RelPath).ToList();
        Assert.Contains($"StandardMesh/{built.Template}.sm", names);
        Assert.Contains($"StandardMesh/{built.Template}.rs", names);
        Assert.Contains($"Texture/{built.Template}_tex.dds", names);
        Assert.Contains($"Objects/{built.Template}/Objects.con", names);
        string rs = System.Text.Encoding.UTF8.GetString(built.Files.First(f => f.RelPath.EndsWith(".rs")).Bytes);
        Assert.Contains("lighting false", rs);
        Assert.Contains("blendDest one", rs);
        Assert.Contains("selfillum", rs);
        // Three crossed quads, each two-sided: 24 vertices, 12 triangles.
        Assert.Equal(24, built.Mesh.SubMeshes[0].Positions.Count);
        Assert.Equal(12, built.Mesh.SubMeshes[0].Faces.Count);
        // And a different size is a different template, so a street of small lamps and one big floodlight coexist.
        Assert.NotEqual(built.Template, LampGlow.TemplateName(colour, 4f, 1f));
    }

    [Fact]
    public void Glow_texture_is_bright_in_the_middle_and_gone_at_the_rim()
    {
        var px = LampGlow.Texture(64, new Vec3(1f, 0.8f, 0.5f), 1f);
        int centre = (32 * 64 + 32) * 4, rim = (32 * 64 + 0) * 4;   // the outermost texel is fully transparent
        Assert.True(px[centre + 3] > 200, "opaque core");
        Assert.Equal(0, px[rim + 3]);
        Assert.Equal(0, px[rim]);
    }

    [Fact]
    public void Night_presets_keep_the_moon_high_enough_to_light_floors()
    {
        // Both games show a lamp's lightmap only where the sun direction reaches (lm * sun * N.L), so a low moon
        // would leave every floor without lamp light however bright the lamp. Every night preset stays above 45.
        foreach (var p in TimeOfDayPreset.Nights)
            Assert.True(p.SunElevationDeg >= 45f, $"{p.Name} moon at {p.SunElevationDeg} degrees");
        Assert.Contains(TimeOfDayPreset.Nights, p => p.Name.Contains("BF1942"));
        Assert.Contains(TimeOfDayPreset.Nights, p => p.Name.Contains("BFV"));
    }
}
