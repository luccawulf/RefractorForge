using System.Text;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Sound;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// What was learned from a video screen running in the game:
/// <list type="bullet">
/// <item><c>autoPlaySound 1</c> is what makes an object's sound an AMBIENT. Every retail object that hums or
/// broadcasts has it (o_gen_sound_m1, the Hue speakers, the PA system). Without it the engine plays the sound while
/// the object is DRAWN - it snapped on at full volume when looked at, from any distance.</item>
/// <item>The engine decodes Bink frames into a POWER-OF-TWO texture, so a 1920x800 movie fills 1920/2048 across and
/// 800/1024 down. A quad mapped 0..1 over that showed the padding as black bands along two edges.</item>
/// </list>
/// </summary>
public class VideoDecalSoundTests
{
    private static string ObjConOf(DecalObject.Built b)
        => Encoding.Latin1.GetString(b.Files.First(f => f.RelPath.EndsWith("Objects.con")).Bytes);
    private static string GeomConOf(DecalObject.Built b)
        => Encoding.Latin1.GetString(b.Files.First(f => f.RelPath.EndsWith("Geometries.con")).Bytes);

    [Fact]
    public void An_ambient_screen_carries_autoPlaySound_the_way_the_games_own_speakers_do()
    {
        var obj = ObjConOf(DecalObject.Build("L", "screen", 4f, 3f, "t", null, textureRef: "Movies/x.bik",
                                             soundScript: "screen.ssc", soundAutoPlay: true));
        Assert.Contains("ObjectTemplate.autoPlaySound 1", obj);
        Assert.Contains("ObjectTemplate.loadSoundScript screen.ssc", obj);
    }

    [Fact]
    public void The_look_at_kind_leaves_autoPlaySound_out_on_purpose()
    {
        var obj = ObjConOf(DecalObject.Build("L", "screen", 4f, 3f, "t", null, textureRef: "Movies/x.bik",
                                             soundScript: "screen.ssc", soundAutoPlay: false));
        Assert.DoesNotContain("autoPlaySound", obj);
        Assert.Contains("ObjectTemplate.loadSoundScript screen.ssc", obj);
    }

    [Fact]
    public void A_silent_decal_gains_no_sound_lines_at_all()
    {
        var obj = ObjConOf(DecalObject.Build("L", "poster", 2f, 1f, "t", new byte[64]));
        Assert.DoesNotContain("autoPlaySound", obj);
        Assert.DoesNotContain("loadSoundScript", obj);
    }

    [Fact]
    public void Capping_the_draw_distance_caps_the_whole_lod_ramp()
    {
        // In the look-at mode the sound lasts as long as the object is drawn, so the far LOD distance is its range.
        var geom = GeomConOf(DecalObject.Build("L", "screen", 4f, 3f, "t", null, textureRef: "Movies/x.bik",
                                               soundScript: "screen.ssc", soundAutoPlay: false, maxDrawDistance: 120f));
        Assert.Contains("GeometryTemplate.setLodDistance 0 0", geom);
        Assert.Contains("GeometryTemplate.setLodDistance 5 120", geom);
        Assert.Contains("GeometryTemplate.setLodDistance 1 12", geom);
        Assert.DoesNotContain("setLodDistance 5 1000", geom);

        // Left alone, it keeps the ramp every shipped Geometries.con writes.
        var dflt = GeomConOf(DecalObject.Build("L", "poster", 2f, 1f, "t", new byte[64]));
        Assert.Contains("GeometryTemplate.setLodDistance 5 1000", dflt);
    }

    [Fact]
    public void A_videos_uvs_stop_where_the_picture_stops()
    {
        // 1920x800 in a 2048x1024 texture: 0.9375 across, 0.78125 down.
        var b = DecalObject.Build("L", "screen", 4f, 3f, "t", null, textureRef: "Movies/x.bik",
                                  uMax: 1920f / 2048f, vMax: 800f / 1024f);
        var uvs = b.Mesh.SubMeshes[0].Uvs;
        Assert.Contains(uvs, uv => Math.Abs(uv.Item1 - 0.9375f) < 1e-4 && Math.Abs(uv.Item2 - 0.78125f) < 1e-4);
        Assert.Contains(uvs, uv => uv.Item1 == 0f && uv.Item2 == 0f);
        Assert.All(uvs, uv => Assert.True(uv.Item1 <= 0.9376f && uv.Item2 <= 0.78126f));

        // A still image is resized to a power of two instead, so it uses the whole texture.
        var pic = DecalObject.Build("L", "poster", 2f, 1f, "t", new byte[64]);
        Assert.Contains(pic.Mesh.SubMeshes[0].Uvs, uv => uv.Item1 == 1f && uv.Item2 == 1f);
    }

    [Fact]
    public void Padding_to_a_power_of_two_reports_the_fraction_the_picture_fills()
    {
        var src = new Texture2D(1920, 800, new byte[1920 * 800 * 4]);
        var padded = DdsTexture.PadToPowerOfTwo(src, out var u, out var v);
        Assert.Equal(2048, padded.Width);
        Assert.Equal(1024, padded.Height);
        Assert.Equal(1920f / 2048f, u, 5);
        Assert.Equal(800f / 1024f, v, 5);

        // Already a power of two: untouched, and it fills all of it.
        var pow2 = new Texture2D(256, 512, new byte[256 * 512 * 4]);
        Assert.Same(pow2, DdsTexture.PadToPowerOfTwo(pow2, out var u2, out var v2));
        Assert.Equal(1f, u2, 5);
        Assert.Equal(1f, v2, 5);
    }

    [Fact]
    public void The_padding_keeps_the_picture_in_the_top_left_and_blackens_the_rest()
    {
        var px = new byte[2 * 2 * 4];
        for (int i = 0; i < px.Length; i++) px[i] = 200;
        var padded = DdsTexture.PadToPowerOfTwo(new Texture2D(2, 3, new byte[2 * 3 * 4].Select((_, i) => (byte)200).ToArray()), out _, out _);
        Assert.Equal(2, padded.Width);
        Assert.Equal(4, padded.Height);
        Assert.Equal(200, padded.Rgba[0]);                                   // the picture
        Assert.Equal(0, padded.Rgba[(3 * 2 + 0) * 4]);                       // the padded row is black...
        Assert.Equal(255, padded.Rgba[(3 * 2 + 0) * 4 + 3]);                 // ...and opaque
    }

    [Fact]
    public void A_stereo_sample_is_declared_and_a_mono_one_is_not()
    {
        var wav = new byte[128];
        var st = Encoding.Latin1.GetString(SoundObject.Build("s", wav, stereo: true).Files.First(f => f.RelPath.EndsWith(".ssc")).Bytes);
        Assert.Contains("\r\nstereo\r\n", st);
        var mono = Encoding.Latin1.GetString(SoundObject.Build("s", wav).Files.First(f => f.RelPath.EndsWith(".ssc")).Bytes);
        Assert.DoesNotContain("stereo", mono);
    }

    [Fact]
    public void The_editor_can_read_back_where_a_script_falls_silent()
    {
        // The Distance->Volume ramp's second distance - what the outer ring in the viewport is drawn at.
        var ssc = Encoding.Latin1.GetString(SoundObject.Build("s", new byte[128], minDistance: 8f, maxDistance: 60f)
                                                       .Files.First(f => f.RelPath.EndsWith(".ssc")).Bytes);
        var script = SoundScript.Parse(ssc);
        Assert.Equal(8f, script.MinDistance, 3);
        Assert.Equal(60f, script.MaxDistance!.Value, 3);

        // A script with no such effect says so, rather than inventing a distance.
        Assert.Null(SoundScript.Parse("newPatch\r\nload x.wav\r\nminDistance 5\r\nvolume 1\r\n").MaxDistance);
    }
}
