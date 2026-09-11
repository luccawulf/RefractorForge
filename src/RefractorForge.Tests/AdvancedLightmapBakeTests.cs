using System.Numerics;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The advanced bake, measured where it counts: in the TEXELS, not in the sampler.
///
/// <para>The contract that matters most is the first test - with no options the bake is byte-for-byte what it
/// always was. Everything else here is opt-in, so a map that looked right yesterday still does.</para>
/// </summary>
public class AdvancedLightmapBakeTests
{
    /// <summary>A flat floor, 16 m square, filling the whole lightmap.</summary>
    private static MeshLibrary.Mesh Floor()
    {
        var pos = new[] { new Vector3(0, 0, 0), new Vector3(16, 0, 0), new Vector3(16, 0, 16), new Vector3(0, 0, 16) };
        var lm = new[] { new Vector2(0.001f, 0.001f), new Vector2(0.999f, 0.001f),
                         new Vector2(0.999f, 0.999f), new Vector2(0.001f, 0.999f) };
        // Wound so the OUTWARD normal points up. Refractor is clockwise-from-outside and the baker takes
        // fn = -cross(b-a, c-a), so {0,2,1} would face this floor DOWNWARD - the hemisphere would then sample
        // below the ground, see nothing, and every sky term would come back fully open.
        var part = new MeshLibrary.MaterialPart(new[] { 0, 1, 2, 0, 2, 3 }, Vector3.One, null, false);
        return new MeshLibrary.Mesh(pos, new Vector2[4], new[] { part }) { LightmapUvs = lm };
    }

    private static (Heightmap, TerrainConfig) FlatGround()
        => (new Heightmap(16, 16), new TerrainConfig { MaterialSize = 16, WorldSize = 256, YScale = 1f });

    /// <summary>A thin wall standing across the middle of the floor, at z = 8, 6 m tall. It casts a shadow but
    /// blocks very little sky, which is what separates the sun term from the sky term.</summary>
    private static RayScene Wall() => RayScene.Build(new List<(Vector3, Vector3, Vector3)>
    {
        (new Vector3(-4, 0, 8), new Vector3(20, 0, 8), new Vector3(20, 6, 8)),
        (new Vector3(-4, 0, 8), new Vector3(20, 6, 8), new Vector3(-4, 6, 8)),
    })!;

    private static readonly Vec3 Sun = new(0f, 0.7071f, -0.7071f);

    private static Texture2D Bake(MeshLibrary.Mesh mesh, ObjectLightmapBaker.Advanced? adv, int size = 64)
    {
        var (hm, cfg) = FlatGround();
        return ObjectLightmapBaker.Bake(mesh, Matrix4x4.Identity, hm, cfg, Sun, size,
                                        ambient: 0f, samples: 1, advanced: adv)!;
    }

    private static int[] Histogram(Texture2D t)
    {
        var h = new int[256];
        for (int i = 0; i < t.Width * t.Height; i++) h[t.Rgba[i * 4]]++;
        return h;
    }

    /// <summary>How many texels are neither black nor white - the size of the soft band.</summary>
    private static int Intermediate(Texture2D t)
    {
        int n = 0;
        var h = Histogram(t);
        for (int v = 12; v < 244; v++) n += h[v];
        return n;
    }

    /// <summary>THE COMPATIBILITY CONTRACT: with no options, the bake is unchanged, byte for byte.</summary>
    [Fact]
    public void Without_options_the_bake_is_byte_identical_to_the_plain_one()
    {
        var (hm, cfg) = FlatGround();
        var mesh = Floor();
        var plain = ObjectLightmapBaker.Bake(mesh, Matrix4x4.Identity, hm, cfg, Sun, 64, ambient: 0f, samples: 2)!;
        var viaAdvanced = ObjectLightmapBaker.Bake(mesh, Matrix4x4.Identity, hm, cfg, Sun, 64, ambient: 0f,
                                                   samples: 2, advanced: null)!;
        Assert.Equal(plain.Rgba, viaAdvanced.Rgba);
    }

    /// <summary>A hard sun writes only black and white. An area sun writes the gradient between them - which is
    /// the entire visible difference between a stencil and a render.</summary>
    [Fact]
    public void An_area_sun_puts_a_gradient_where_the_hard_sun_had_a_step()
    {
        var wall = Wall();
        var hard = Bake(Floor(), new ObjectLightmapBaker.Advanced(Scene: wall));
        var soft = Bake(Floor(), new ObjectLightmapBaker.Advanced(
            Scene: wall, SunAngularDiameterDeg: 6f, SunSamples: 48));

        int hardBand = Intermediate(hard), softBand = Intermediate(soft);
        Assert.True(hardBand < 40, $"a point sun should be essentially binary, got {hardBand} intermediate texels");
        Assert.True(softBand > hardBand * 4,
            $"an area sun should produce a real penumbra: hard={hardBand}, soft={softBand}");

        // Both must still agree about the two extremes - the penumbra replaces the edge, not the shadow.
        Assert.True(Histogram(soft)[0] > 400, "the deep shadow must still be black");
        Assert.True(Histogram(soft)[255] > 400, "open ground must still be fully lit");
    }

    /// <summary>Sky fill is what makes occlusion visible in SHADOW. The engine gives the map no ambient channel
    /// to modulate, so a shadowed texel is black however open it is - unless the map itself carries a small
    /// sky-weighted value. With it on, the wall's shadow lifts off black while staying below the lit ground.</summary>
    [Fact]
    public void Sky_fill_lifts_an_open_shadow_off_black()
    {
        var wall = Wall();
        var flat = Bake(Floor(), new ObjectLightmapBaker.Advanced(Scene: wall));
        var filled = Bake(Floor(), new ObjectLightmapBaker.Advanced(
            Scene: wall, SkySamples: 32, SkyFill: 0.35f));

        Assert.True(Histogram(flat)[0] > 800, "without fill the shadow is pure black");
        Assert.True(Histogram(filled)[0] < Histogram(flat)[0] / 4,
            "with fill, most of the shadow should no longer be pure black");

        // ...but it must stay clearly darker than sunlit ground, or the shadow has just been erased.
        double mean = 0;
        for (int i = 0; i < filled.Width * filled.Height; i++) mean += filled.Rgba[i * 4];
        mean /= filled.Width * filled.Height;
        Assert.True(mean < 235, $"sky fill should not wash the shadow out, mean={mean:0.0}");
        Assert.True(Histogram(filled)[255] > 400, "sunlit ground must be unaffected");
    }

    /// <summary>Ambient occlusion darkens the sunlit side near an obstruction - contact darkening. Bounded to a
    /// short radius it is the "dirt" pass, and it must not touch open ground far from anything.</summary>
    [Fact]
    public void Ambient_occlusion_darkens_ground_near_a_wall()
    {
        var wall = Wall();
        var plain = Bake(Floor(), new ObjectLightmapBaker.Advanced(Scene: wall));
        var ao = Bake(Floor(), new ObjectLightmapBaker.Advanced(
            Scene: wall, SkySamples: 48, AoRadius: 3f, AoStrength: 0.9f));

        // The floor spans z 0..16 over v 0..1 and the wall stands at z = 8. The sun points toward -z, so the
        // LIT side is z < 8: v 0.44 is a metre in front of the wall, v 0.05 is seven metres clear of it.
        double NearWall(Texture2D t) => Row(t, 0.44f);
        double FarFromWall(Texture2D t) => Row(t, 0.05f);

        Assert.True(NearWall(ao) < NearWall(plain) - 10,
            $"AO should darken the lit ground beside the wall: {NearWall(plain):0.0} -> {NearWall(ao):0.0}");
        Assert.True(System.Math.Abs(FarFromWall(ao) - FarFromWall(plain)) < 8,
            $"a 3 m radius must leave open ground alone: {FarFromWall(plain):0.0} -> {FarFromWall(ao):0.0}");
    }

    private static double Row(Texture2D t, float v)
    {
        int y = System.Math.Clamp((int)(v * t.Height), 0, t.Height - 1);
        double sum = 0; int n = 0;
        for (int x = 4; x < t.Width - 4; x++) { sum += t.Rgba[(y * t.Width + x) * 4]; n++; }
        return sum / n;
    }

    /// <summary>Two runs of the same advanced bake must produce the same file.</summary>
    [Fact]
    public void An_advanced_bake_is_reproducible()
    {
        var wall = Wall();
        var opts = new ObjectLightmapBaker.Advanced(Scene: wall, SunAngularDiameterDeg: 5f, SunSamples: 24,
                                                    SkySamples: 24, AoRadius: 2f, AoStrength: 0.6f, SkyFill: 0.2f);
        Assert.Equal(Bake(Floor(), opts).Rgba, Bake(Floor(), opts).Rgba);
    }
}
