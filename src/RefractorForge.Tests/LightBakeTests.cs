using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Baking a light rig into the two places the engine reads lighting from.
///
/// The split is not a design preference, it is what the formats allow. The ground texture is full RGB, so a
/// light's COLOUR can live there — the same trick DC_Basrah_Nights uses with a level-local Textures override.
/// Per-object lightmaps cannot carry colour: all 201 shipped lightmaps checked across retail BF1942 levels are
/// colour-mapped TGAs with a GREY palette, so those get brightness only.
/// </summary>
public class LightBakeTests
{
    private static (Heightmap, TerrainConfig) FlatWorld(int side = 64, float worldSize = 256f, float metres = 10f)
    {
        var cfg = new TerrainConfig { MaterialSize = side, WorldSize = (int)worldSize, YScale = 1f };
        var hm = new Heightmap(side, side);
        ushort raw = cfg.MetersToRaw(metres);
        for (int i = 0; i < hm.Samples.Length; i++) hm.Samples[i] = raw;
        return (hm, cfg);
    }

    private static LightRig OneLight(float x, float y, float z, float radius, float r, float g, float b)
    {
        var rig = new LightRig();
        rig.Lights.Add(new PointLight
        {
            Position = new Vec3(x, y, z), Radius = radius, Intensity = 1f,
            ColorR = r, ColorG = g, ColorB = b,
        });
        return rig;
    }

    private static (byte R, byte G, byte B) Px(Texture2D t, int x, int y)
    {
        int o = (y * t.Width + x) * 4;
        return (t.Rgba[o], t.Rgba[o + 1], t.Rgba[o + 2]);
    }

    [Fact]
    public void The_ground_bake_puts_a_coloured_pool_under_the_light_and_nothing_far_away()
    {
        var (hm, cfg) = FlatWorld();
        // Centre of a 256 m world, 6 m up, reaching 40 m.
        var rig = OneLight(128f, 16f, 128f, radius: 40f, r: 1f, g: 0.5f, b: 0.1f);

        var ground = LightBake.BakeGround(hm, cfg, rig, 64);

        // Texel (x,y) is world (x/size*worldSize, ...), the same mapping the atlas uses - so the centre of a
        // 256 m world at 64 texels is texel 32.
        var under = Px(ground, 32, 32);
        Assert.True(under.R > 0, "the ground under the light should be lit");
        Assert.True(under.R > under.G && under.G > under.B, "the pool should carry the lamp's colour");

        var corner = Px(ground, 1, 1);
        Assert.Equal((byte)0, corner.R);
        Assert.Equal((byte)0, corner.G);
        Assert.Equal((byte)0, corner.B);
    }

    [Fact]
    public void The_pool_fades_outward_from_the_light()
    {
        var (hm, cfg) = FlatWorld();
        var rig = OneLight(128f, 16f, 128f, radius: 60f, r: 1f, g: 1f, b: 1f);
        var ground = LightBake.BakeGround(hm, cfg, rig, 64);

        // Along +X from the centre: 4 m per texel, so this walks out to ~48 m.
        byte prev = 255;
        for (int x = 32; x <= 44; x++)
        {
            byte v = Px(ground, x, 32).R;
            Assert.True(v <= prev, $"brightness should not rise with distance (texel {x})");
            prev = v;
        }
        Assert.True(Px(ground, 32, 32).R > Px(ground, 40, 32).R, "the centre should be clearly brighter than the edge");
    }

    [Fact]
    public void Terrain_between_the_light_and_the_ground_blocks_it()
    {
        // A wall of high ground down the middle: the far side must stay dark even though it is within reach.
        int side = 64;
        var cfg = new TerrainConfig { MaterialSize = side, WorldSize = 256, YScale = 1f };
        var hm = new Heightmap(side, side);
        ushort low = cfg.MetersToRaw(0f), high = cfg.MetersToRaw(60f);
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
                hm[x, y] = (x == side / 2) ? high : low;

        // Light low down on the left, reaching well past the wall.
        var rig = OneLight(96f, 4f, 128f, radius: 120f, r: 1f, g: 1f, b: 1f);
        var ground = LightBake.BakeGround(hm, cfg, rig, 64);

        Assert.True(Px(ground, 20, 32).R > 0, "the near side of the wall is lit");
        Assert.Equal((byte)0, Px(ground, 45, 32).R);      // behind the wall
    }

    [Fact]
    public void Shadow_tracing_is_skipped_for_a_light_that_says_it_casts_none()
    {
        int side = 64;
        var cfg = new TerrainConfig { MaterialSize = side, WorldSize = 256, YScale = 1f };
        var hm = new Heightmap(side, side);
        ushort low = cfg.MetersToRaw(0f), high = cfg.MetersToRaw(60f);
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
                hm[x, y] = (x == side / 2) ? high : low;

        var rig = OneLight(96f, 4f, 128f, radius: 120f, r: 1f, g: 1f, b: 1f);
        rig.Lights[0].CastsShadows = false;

        var ground = LightBake.BakeGround(hm, cfg, rig, 64);
        Assert.True(Px(ground, 45, 32).R > 0, "a fill light should reach through, by definition");
    }

    [Fact]
    public void Burning_into_the_atlas_adds_light_and_leaves_unlit_ground_untouched()
    {
        var atlas = new Texture2D(8, 8, new byte[8 * 8 * 4]);
        for (int i = 0; i < 8 * 8; i++)
        {
            atlas.Rgba[i * 4 + 0] = 40; atlas.Rgba[i * 4 + 1] = 40;
            atlas.Rgba[i * 4 + 2] = 40; atlas.Rgba[i * 4 + 3] = 255;
        }

        // A light map lighting exactly one texel.
        var light = new Texture2D(8, 8, new byte[8 * 8 * 4]);
        int o = (3 * 8 + 3) * 4;
        light.Rgba[o] = 100; light.Rgba[o + 1] = 60; light.Rgba[o + 2] = 20; light.Rgba[o + 3] = 255;

        LightBake.BurnIntoAtlas(atlas, light, 1f);

        Assert.Equal((byte)140, atlas.Rgba[(3 * 8 + 3) * 4 + 0]);   // added, not replaced
        Assert.Equal((byte)100, atlas.Rgba[(3 * 8 + 3) * 4 + 1]);
        Assert.Equal((byte)60, atlas.Rgba[(3 * 8 + 3) * 4 + 2]);
        Assert.Equal((byte)40, atlas.Rgba[(2 * 8 + 2) * 4 + 0]);    // neighbours untouched
    }

    [Fact]
    public void Burning_saturates_rather_than_wrapping_around_to_black()
    {
        // Adding into a byte without clamping is how a bright pool turns into a dark hole.
        var atlas = new Texture2D(2, 2, new byte[2 * 2 * 4]);
        for (int i = 0; i < 4; i++)
        {
            atlas.Rgba[i * 4 + 0] = 250; atlas.Rgba[i * 4 + 1] = 250;
            atlas.Rgba[i * 4 + 2] = 250; atlas.Rgba[i * 4 + 3] = 255;
        }
        var light = new Texture2D(2, 2, new byte[2 * 2 * 4]);
        for (int i = 0; i < 4; i++)
        {
            light.Rgba[i * 4 + 0] = 200; light.Rgba[i * 4 + 1] = 200;
            light.Rgba[i * 4 + 2] = 200; light.Rgba[i * 4 + 3] = 255;
        }

        LightBake.BurnIntoAtlas(atlas, light, 1f);
        for (int i = 0; i < 4; i++) Assert.Equal((byte)255, atlas.Rgba[i * 4]);
    }

    [Fact]
    public void Strength_scales_the_whole_rig_so_it_can_be_dialled_without_rebaking()
    {
        Texture2D Make(byte v)
        {
            var t = new Texture2D(1, 1, new byte[4]);
            t.Rgba[0] = v; t.Rgba[1] = v; t.Rgba[2] = v; t.Rgba[3] = 255;
            return t;
        }
        var half = Make(0); half.Rgba[0] = 10; half.Rgba[1] = 10; half.Rgba[2] = 10;
        var atlas = Make(0);
        LightBake.BurnIntoAtlas(atlas, half, 0.5f);
        Assert.Equal((byte)5, atlas.Rgba[0]);

        var atlas2 = Make(0);
        LightBake.BurnIntoAtlas(atlas2, half, 0f);       // off contributes nothing at all
        Assert.Equal((byte)0, atlas2.Rgba[0]);
    }

    [Fact]
    public void The_atlas_and_the_light_map_need_not_be_the_same_size()
    {
        // The bake is capped below the atlas resolution on big maps, so the two routinely differ.
        var atlas = new Texture2D(16, 16, new byte[16 * 16 * 4]);
        for (int i = 0; i < 16 * 16; i++) atlas.Rgba[i * 4 + 3] = 255;

        var light = new Texture2D(4, 4, new byte[4 * 4 * 4]);
        for (int i = 0; i < 4 * 4; i++)
        {
            light.Rgba[i * 4 + 0] = 80; light.Rgba[i * 4 + 1] = 80;
            light.Rgba[i * 4 + 2] = 80; light.Rgba[i * 4 + 3] = 255;
        }

        LightBake.BurnIntoAtlas(atlas, light, 1f);
        Assert.Equal((byte)80, atlas.Rgba[0]);
        Assert.Equal((byte)80, atlas.Rgba[(15 * 16 + 15) * 4]);
    }

    [Fact]
    public void Object_intensity_is_luma_so_two_lamps_of_equal_brightness_land_equally()
    {
        // Object lightmaps carry brightness only, so a blue lamp and a yellow one of the same perceived
        // brightness must not bake at different strengths just because of their hue.
        var (hm, cfg) = FlatWorld();
        var blue = OneLight(128f, 16f, 128f, 40f, 0f, 0f, 1f);
        var green = OneLight(128f, 16f, 128f, 40f, 0f, 1f, 0f);

        float ib = LightBake.Intensity(128f, 10f, 128f, blue, hm, cfg);
        float ig = LightBake.Intensity(128f, 10f, 128f, green, hm, cfg);

        Assert.True(ig > ib, "green should read brighter than blue at equal intensity (Rec. 709 luma)");
        Assert.True(ib > 0f, "and blue still contributes something");
    }
}
