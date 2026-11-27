using System.Numerics;
using RefractorForge.Formats.Rfa;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The palette BfVietnam reads its 8-bit lightmaps through (<see cref="LightmapPalette"/>), and the numbers the
/// calibration level measured in the game on 2026-11-27 that the editor's model now has to reproduce.
/// </summary>
public class LightmapPaletteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rf_lmpal_" + Guid.NewGuid().ToString("N"));
    public LightmapPaletteTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static void Near(float want, float got, float tol) => Assert.InRange(got, want - tol, want + tol);

    private static LightmapPalette Make(Func<int, (byte R, byte G, byte B)> entry)
    {
        var riff = TgaTexture.GreyPalettePal();
        for (int i = 0; i < 256; i++) { var (r, g, b) = entry(i); riff[24 + i * 4] = r; riff[25 + i * 4] = g; riff[26 + i * 4] = b; }
        return LightmapPalette.Parse(riff)!;
    }

    // The retail curve: a toe under the middle (index 64 is 47, 96 is 73), a plain ramp above it.
    private static readonly LightmapPalette Retail = Make(i =>
    {
        byte v = i >= 128 ? (byte)i : (byte)Math.Round(128 * Math.Pow(i / 128.0, 1.45));
        return (v, v, v);
    });
    // Battlecraft's: tinted, and running from about 25 to 185.
    private static readonly LightmapPalette Battlecraft = Make(i => ((byte)(25 + i * 0.63), (byte)(22 + i * 0.61), (byte)(24 + i * 0.6)));
    // Operation Irving's: black up to index 80.
    private static readonly LightmapPalette Irving = Make(i => { byte v = i <= 80 ? (byte)0 : (byte)Math.Round((i - 80) * 255.0 / 175.0); return (v, v, v); });

    private static Texture2D Ramp(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                byte v = (byte)(x * 255 / (w - 1));
                int o = (y * w + x) * 4; px[o] = px[o + 1] = px[o + 2] = v; px[o + 3] = 255;
            }
        return new Texture2D(w, h, px);
    }

    [Fact]
    public void A_byte_comes_back_from_a_565_texture_as_the_game_showed_it()
    {
        // The calibration boards: palette values 47, 128, 191 were drawn as 41, 132, 189 in red and blue, 44, 130, 190 in green.
        Assert.Equal(41, LightmapPalette.Through565(47));
        Assert.Equal(132, LightmapPalette.Through565(128));
        Assert.Equal(189, LightmapPalette.Through565(191));
        Assert.Equal(44, LightmapPalette.Through565(47, green: true));
        Assert.Equal(130, LightmapPalette.Through565(128, green: true));
        Assert.Equal(190, LightmapPalette.Through565(191, green: true));
        Assert.Equal(0, LightmapPalette.Through565(0));
        Assert.Equal(255, LightmapPalette.Through565(255));
    }

    [Fact]
    public void The_plain_ramp_is_what_the_editor_has_always_written_beside_the_maps()
    {
        Assert.True(LightmapPalette.Linear.IsLinear);
        Assert.True(LightmapPalette.Linear.Usable);
        Assert.True(LightmapPalette.Parse(TgaTexture.GreyPalettePal())!.IsLinear);
        Assert.Equal(TgaTexture.GreyPalettePal(), LightmapPalette.Linear.ToRiff());
        Assert.Null(LightmapPalette.Parse(new byte[] { 1, 2, 3 }));
        Assert.Null(LightmapPalette.Parse(null));
        var back = LightmapPalette.Parse(Retail.ToRiff())!;
        for (int i = 0; i < 256; i++) Assert.Equal(Retail[i], back[i]);
    }

    [Fact]
    public void Which_palettes_a_bake_can_say_what_it_means_with()
    {
        Assert.True(Retail.Usable);
        Assert.False(Retail.IsLinear);
        Assert.Equal(47, Retail[64].B);                                        // the toe the game showed
        Assert.True(Irving.Usable);                                            // crushed, but grey and reaching both ends
        Assert.False(Battlecraft.Usable);                                      // tinted, and it stops well short of full
        Assert.True(Battlecraft.Range.Max < 0.8f);
        Assert.True(Battlecraft.Range.Min > 0.05f);
    }

    [Theory]
    [InlineData(0)]   // the plain ramp
    [InlineData(1)]   // retail
    [InlineData(2)]   // Irving
    public void What_the_game_shows_is_what_was_meant(int which)
    {
        var pal = which switch { 0 => LightmapPalette.Linear, 1 => Retail, _ => Irving };
        var ramp = Ramp(256, 16);
        var file = pal.Encode(ramp);
        Assert.Equal((1, 1, 8, 0), (file[1], file[2], file[16], file[17]));    // the retail form: colour-mapped, 8-bit, bottom-left
        var shown = pal.Decode(file)!;
        // A 5-bit texture holds 32 levels, so a single texel is up to 8 off; the dither makes every 4 x 4 block's
        // mean right, which is what the game's bilinear filter and the eye see.
        double worst = 0;
        for (int bx = 0; bx + 4 <= 256; bx += 4)
        {
            double want = 0, got = 0;
            for (int y = 0; y < 4; y++)
                for (int x = bx; x < bx + 4; x++) { want += ramp.Rgba[(y * 256 + x) * 4]; got += shown.Rgba[(y * 256 + x) * 4 + 2]; }
            worst = Math.Max(worst, Math.Abs(want - got) / 16.0);
        }
        Assert.True(worst <= 2.6, $"worst 4x4 block is {worst:0.0} of 255 off");
        // And no texel is further off than one step of the texture.
        for (int i = 0; i < 256 * 16; i++) Assert.InRange(Math.Abs(shown.Rgba[i * 4 + 2] - ramp.Rgba[i * 4]), 0, 9);
    }

    [Fact]
    public void Index_equals_value_through_the_retail_palette_is_the_error_this_fixes()
    {
        // What the editor used to write: index = light. Through the retail palette the game showed light 64 as 41.
        var old = TgaTexture.EncodeGrayColormapped(Ramp(256, 4));
        var shownOld = Retail.Decode(old)!;
        Assert.Equal(41, shownOld.Rgba[64 * 4 + 2]);
        var shownNew = Retail.Decode(Retail.Encode(Ramp(256, 4)))!;
        double mean = 0; for (int y = 0; y < 4; y++) for (int x = 62; x < 66; x++) mean += shownNew.Rgba[(y * 256 + x) * 4 + 2];
        Near(64f, (float)(mean / 16.0), 3f);
    }

    [Fact]
    public void A_palette_that_cannot_reach_a_value_gives_its_nearest_and_never_wraps()
    {
        var file = Battlecraft.Encode(Ramp(256, 4));
        var shown = Battlecraft.Decode(file)!;
        byte lo = shown.Rgba[2], hi = shown.Rgba[255 * 4 + 2];
        Assert.True(lo < 40 && hi > 170);
        for (int x = 1; x < 256; x++) Assert.True(shown.Rgba[x * 4 + 2] + 9 >= shown.Rgba[(x - 1) * 4 + 2]);   // never darker going up, beyond dither
    }

    [Fact]
    public void The_file_carries_the_palette_it_was_written_for_and_other_kinds_are_left_to_their_own_readers()
    {
        var file = Retail.Encode(Ramp(64, 64));
        for (int i = 0; i < 256; i++)
            Assert.Equal((Retail[i].B, Retail[i].G, Retail[i].R), (file[18 + i * 3], file[19 + i * 3], file[20 + i * 3]));
        Assert.NotNull(TgaTexture.Decode(file));
        Assert.Null(Retail.Decode(TgaTexture.EncodeRgb24(Ramp(8, 8))));        // 24-bit: read as it is
        Assert.Null(Retail.Decode(new byte[10]));
    }

    [Fact]
    public void A_level_s_lightmaps_load_through_its_palette_only_when_asked()
    {
        // One map, every texel index 64, colour map inside the file a plain ramp - what the editor used to write.
        var px = new byte[32 * 32 * 4];
        for (int i = 0; i < 32 * 32; i++) { px[i * 4] = px[i * 4 + 1] = px[i * 4 + 2] = 64; px[i * 4 + 3] = 255; }
        var map = TgaTexture.EncodeGrayColormapped(new Texture2D(32, 32, px));
        var rfa = Path.Combine(_dir, "L.rfa");
        RefractorFlatArchive.WriteFile(rfa, new List<(string, byte[])>
        {
            ("BfVietnam/levels/L/Objectlightmaps/Palette.pal", Retail.ToRiff()),
            ("BfVietnam/levels/L/Objectlightmaps/house_m1_10-20-30.tga", map),
        }, compress: true, XPackId.Default);

        var plain = ObjectLightmaps.FromRfaPaths(new[] { rfa });
        Assert.Null(plain.Palette);
        Assert.Equal(64, Assert.Single(plain.Entries).Texture.Rgba[0]);

        var asGame = ObjectLightmaps.FromRfaPaths(new[] { rfa }, throughLevelPalette: true);
        Assert.NotNull(asGame.Palette);
        var tex = Assert.Single(asGame.Entries).Texture;
        Assert.Equal((41, 44, 41), (tex.Rgba[0], tex.Rgba[1], tex.Rgba[2]));   // palette 47, through 5:6:5
    }

    // ---- the formula, against what the game drew ------------------------------------------------------------

    private static readonly Vector3 Sun = Vector3.Normalize(new Vector3(-0.747988f, 0.592542f, 0.299011f));
    private static readonly EngineLight.Colours Cal = new(new Vector3(1.25f, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 0.5f), new Vector3(0, 0, 0.25f));
    private static Vector3 Rgb(Vector3 v) => new(MathF.Round(v.X * 255f), MathF.Round(v.Y * 255f), MathF.Round(v.Z * 255f));

    [Fact]
    public void A_tangent_frame_mesh_is_lit_as_the_game_drew_board_E()
    {
        var m = EngineLight.Material.Default;
        float p128 = 132f / 255f;                                              // lightmap 128 as the game held it
        Near(165f, Rgb(EngineLight.Lightmapped(p128, Sun, Sun, Cal, m)).X, 1f);                   // facing the sun: 165 0 64
        Near(64f, Rgb(EngineLight.Lightmapped(p128, Sun, Sun, Cal, m)).Z, 1f);
        Near(236f, Rgb(EngineLight.Lightmapped(189f / 255f, Sun, Sun, Cal, m)).X, 1f);            // lightmap 191
        Near(132f, Rgb(EngineLight.Lightmapped(p128, EngineLight.BackLight(Sun), Sun, Cal, m)).Y, 1f);
        var up = Rgb(EngineLight.Lightmapped(1f, Vector3.UnitY, Sun, Cal, m));                    // straight up, full: 188 152 64
        Near(188f, up.X, 2f); Near(152f, up.Y, 2f); Near(64f, up.Z, 1f);
        var half = EngineLight.Material.Default with { Diffuse = new Vector3(0.5f) };
        Near(159f, Rgb(EngineLight.Lightmapped(1f, Sun, Sun, Cal, half)).X, 1f);                  // half diffuse halves the lights...
        Near(64f, Rgb(EngineLight.Lightmapped(1f, -Vector3.UnitY, Sun, Cal, half)).Z, 1f);        // ...and leaves the ambient
    }

    [Fact]
    public void A_mesh_without_a_tangent_frame_clamps_the_lights_first_as_the_game_drew_board_A()
    {
        var v = EngineLight.Material.Default with { VertexLit = true };
        float p128 = 132f / 255f;
        Near(132f, Rgb(EngineLight.Lightmapped(p128, Sun, Sun, Cal, v)).X, 1f);                   // 132, where board E showed 165
        Near(255f, Rgb(EngineLight.Lightmapped(1f, Sun, Sun, Cal, v)).X, 1f);
        var up = Rgb(EngineLight.Lightmapped(1f, Vector3.UnitY, Sun, Cal, v));                    // under 1: the same as per pixel
        Near(189f, up.X, 2f); Near(151f, up.Y, 2f);
        // The solve knows it: to show a sun-facing wall at one half, a vertex-lit mesh needs more lightmap.
        float want = 0.5f;
        var target = new Vector3(want, 0f, 0.25f);
        float perPixel = EngineLight.SolvePrelight(target, Sun, Sun, Cal, EngineLight.Material.Default);
        float perVertex = EngineLight.SolvePrelight(target, Sun, Sun, Cal, v);
        Near(want / 1.25f, perPixel, 1e-3f);
        Near(want, perVertex, 1e-3f);
        Near(EngineLight.Luma(target), EngineLight.Luma(EngineLight.Lightmapped(perVertex, Sun, Sun, Cal, v)), 1e-3f);
    }

    [Fact]
    public void An_object_with_no_lightmap_keeps_the_share_of_the_lights_the_ground_under_it_shows()
    {
        // In the open - the second level's back row, over black, grey and white ground alike - all of it.
        Near(1f, EngineLight.LightShare(0.3f, EngineLight.TerrainSample(inShadow: false, Vector3.Zero)), 1e-6f);
        var v = EngineLight.Material.Default;
        Near(255f, Rgb(EngineLight.Dynamic(EngineLight.BackLight(Sun), Sun, Cal, v, 1f)).Y, 1f);
        Near(127f, Rgb(EngineLight.Dynamic(-Vector3.UnitY, Sun, Cal, v, 1f)).Z, 1f);

        // In terrain shadow over Hue's own dark ground (brightness 33 of 255), with Hue's minimum of 0.3: the first
        // level's boards, 124 in red facing the sun and 99 in green facing the back light, blue untouched.
        float share = EngineLight.LightShare(0.3f, EngineLight.TerrainSample(true, new Vector3(33f / 255f)));
        Near(0.3906f, share, 0.001f);
        Near(124f, Rgb(EngineLight.Dynamic(Sun, Sun, Cal, v, share)).X, 1.5f);
        Near(99f, Rgb(EngineLight.Dynamic(EngineLight.BackLight(Sun), Sun, Cal, v, share)).Y, 1.5f);
        Near(127f, Rgb(EngineLight.Dynamic(Sun, Sun, Cal, v, share)).Z, 1f);

        // Never under the minimum, never over 1, and white ground in shadow dims nothing.
        Near(0.3f, EngineLight.LightShare(0.3f, EngineLight.TerrainSample(true, Vector3.Zero)), 1e-6f);
        Near(1f, EngineLight.LightShare(0.3f, EngineLight.TerrainSample(true, Vector3.One)), 1e-6f);
        Near(EngineLight.DefaultMinIntensity, EngineLight.LightShare(EngineLight.DefaultMinIntensity, 0f), 1e-6f);
        // The weights are the exe's: 0.3, 0.59, 0.11.
        Near(0.59f, EngineLight.TerrainSample(true, new Vector3(0, 1, 0)), 1e-6f);
    }

    [Fact]
    public void A_static_object_samples_five_points_toward_the_sun_and_counts_bright_ground_as_lit()
    {
        var pos = new Vector3(372f, 43f, 536f);
        var pts = EngineLight.StaticSamplePoints(pos, 4.3f, Sun);
        Assert.Equal(5, pts.Length);
        var flat = Vector3.Normalize(new Vector3(Sun.X, 0f, Sun.Z));
        foreach (var p in pts)
        {
            Near(pos.Y, p.Y, 1e-5f);                                   // level with the object
            Near(1.1f * 4.3f, Vector3.Distance(p, pos), 1e-3f);        // 1.1 radii out
            Assert.True(Vector3.Dot(Vector3.Normalize(p - pos), flat) > 0.92f);   // within 22.5 degrees of the sun
        }
        // The arc is lopsided, -22.5 to +13.5: the middle sample is 4.5 degrees off the sun's bearing.
        Near(MathF.Cos(4.5f * MathF.PI / 180f), Vector3.Dot(Vector3.Normalize(pts[2] - pos), flat), 1e-4f);

        // The second level's board over grey ground: four samples in shadow near 124 of 255, one in the open -> 0.709.
        float[] grey = { 127f / 255f, 126f / 255f, 123f / 255f, 120f / 255f, 1f };
        Near(0.709f, EngineLight.LightShare(0.3f, EngineLight.StaticTerrain(grey)), 0.005f);
        // Ground brighter than 128 in shadow counts as lit: the board over white ground showed the whole light.
        float[] bright = { 0.6f, 0.9f, 0.51f, 1f, 0.7f };
        Near(1f, EngineLight.StaticTerrain(bright), 1e-6f);
        // The first level's board over Hue's dark ground: 0.39.
        float[] dark = { 38f / 255f, 36f / 255f, 33f / 255f, 31f / 255f, 29f / 255f };
        Near(0.389f, EngineLight.LightShare(0.3f, EngineLight.StaticTerrain(dark)), 0.005f);
    }

    [Fact]
    public void A_static_objects_radius_is_measured_from_the_mesh_origin_and_its_share_follows_the_ground()
    {
        // A calibration board: 6.6 wide, standing 3.6 tall on its origin. Half-diagonal 3.76; the engine uses 4.88.
        float r = EngineLight.StaticRadius(new Vector3(-3.3f, 0f, -0.05f), new Vector3(3.3f, 3.6f, 0.05f));
        Near(4.884f, r, 0.002f);

        var pos = new Vector3(100f, 10f, 100f);
        // Nothing in shadow: the whole light, whatever the ground.
        Near(1f, EngineLight.StaticShare(pos, r, Sun, 0.3f, _ => false, _ => Vector3.Zero), 1e-6f);
        // All in shadow over black ground: the minimum. Over ground of 51 / 255: 0.3 + 0.7 x 0.2.
        Near(0.3f, EngineLight.StaticShare(pos, r, Sun, 0.3f, _ => true, _ => Vector3.Zero), 1e-6f);
        Near(0.44f, EngineLight.StaticShare(pos, r, Sun, 0.3f, _ => true, _ => new Vector3(0.2f)), 1e-4f);
        // The shadow only matters where the samples land - toward the sun, never behind the object.
        var flat = Vector3.Normalize(new Vector3(Sun.X, 0f, Sun.Z));
        Near(1f, EngineLight.StaticShare(pos, r, Sun, 0.3f, p => Vector3.Dot(p - pos, flat) < 0f, _ => Vector3.Zero), 1e-6f);
        Near(0.3f, EngineLight.StaticShare(pos, r, Sun, 0.3f, p => Vector3.Dot(p - pos, flat) > 0f, _ => Vector3.Zero), 1e-6f);
    }

    [Fact]
    public void A_coloured_lightmap_colours_the_light_only_on_a_mesh_without_a_tangent_frame()
    {
        // Boards C and G: the map held (v, v/2, 3v/4). The fixed-function mesh used each channel; the per-pixel one, blue.
        var lm = new Vector3(1f, 127f / 255f, 191f / 255f);
        var v = EngineLight.Material.Default with { VertexLit = true };
        var back = EngineLight.BackLight(Sun);
        Near(255f, Rgb(EngineLight.Lightmapped(lm, Sun, Sun, Cal, v)).X, 1f);                     // C: sun ramp end 255
        Near(127f, Rgb(EngineLight.Lightmapped(lm, back, Sun, Cal, v)).Y, 1f);                    // C: back ramp end 127
        Near(239f, Rgb(EngineLight.Lightmapped(lm, Sun, Sun, Cal, EngineLight.Material.Default)).X, 1f);   // G: 239
        Near(191f, Rgb(EngineLight.Lightmapped(lm, back, Sun, Cal, EngineLight.Material.Default)).Y, 1f);  // G: 191
    }
}
