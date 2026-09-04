using RefractorForge.Formats.Con;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The black bands on a video screen come from the engine decoding Bink frames into a POWER-OF-TWO texture and
/// leaving the remainder black. The cure is to stop the quad's UVs where the picture stops - which has to be worked
/// out from each video's own size, not from the one video that first showed the problem.
/// </summary>
public class VideoAspectTests
{
    private static Texture2D Blank(int w, int h) => new(w, h, new byte[w * h * 4]);

    [Theory]
    // the reported case                            padded to    fills
    [InlineData(1920, 800, 2048, 1024)]          // 2.4:1 cinema
    [InlineData(1920, 1080, 2048, 2048)]         // 16:9 HD
    [InlineData(1280, 720, 2048, 1024)]
    [InlineData(640, 480, 1024, 512)]            // 4:3
    [InlineData(720, 576, 1024, 1024)]           // PAL
    [InlineData(512, 288, 512, 512)]             // what the converter makes by default
    [InlineData(500, 300, 512, 512)]             // nothing round about it
    [InlineData(3840, 2160, 4096, 4096)]         // 4K straight off a camera
    [InlineData(256, 256, 256, 256)]             // already a power of two: nothing to do
    [InlineData(1, 1, 1, 1)]
    public void Any_resolution_gets_the_window_that_fits_it(int w, int h, int padW, int padH)
    {
        var padded = DdsTexture.PadToPowerOfTwo(Blank(w, h), out float u, out float v);
        Assert.Equal(padW, padded.Width);
        Assert.Equal(padH, padded.Height);
        Assert.Equal(w / (float)padW, u, 5);
        Assert.Equal(h / (float)padH, v, 5);

        // ...and that window is what the decal's quad is built with, so the picture fills the screen exactly.
        var mesh = DecalObject.Build("L", "screen", 4f, 3f, "t", null, textureRef: "Movies/x.bik", uMax: u, vMax: v).Mesh;
        var uvs = mesh.SubMeshes[0].Uvs;
        Assert.Contains(uvs, uv => Math.Abs(uv.Item1 - u) < 1e-4 && Math.Abs(uv.Item2 - v) < 1e-4);
        Assert.All(uvs, uv => Assert.True(uv.Item1 <= u + 1e-4f && uv.Item2 <= v + 1e-4f));
    }

    [Fact]
    public void A_power_of_two_video_is_left_completely_alone()
    {
        var src = Blank(512, 256);
        Assert.Same(src, DdsTexture.PadToPowerOfTwo(src, out float u, out float v));
        Assert.Equal(1f, u, 5);
        Assert.Equal(1f, v, 5);
        // 1.0 / 1.0 is the plain full-texture quad.
        var uvs = DecalObject.Build("L", "s", 4f, 3f, "t", null, textureRef: "Movies/x.bik", uMax: u, vMax: v).Mesh.SubMeshes[0].Uvs;
        Assert.Contains(uvs, uv => uv.Item1 == 1f && uv.Item2 == 1f);
    }

    [Fact]
    public void An_unmeasurable_video_is_never_cropped_to_a_guess()
    {
        // With no FFmpeg the editor cannot read the frame size, so it passes 1/1 and leaves the mapping as it was:
        // the padding may show, but the picture is never cut off by a wrong window.
        var uvs = DecalObject.Build("L", "s", 4f, 3f, "t", null, textureRef: "Movies/x.bik").Mesh.SubMeshes[0].Uvs;
        Assert.Contains(uvs, uv => uv.Item1 == 1f && uv.Item2 == 1f);
    }

    [Fact]
    public void The_window_always_covers_more_than_half_the_texture()
    {
        // A sanity property of powers of two: padding can never waste half a dimension, so a correct window is
        // always > 0.5. Anything at or below that would mean the size was misread.
        foreach (var (w, h) in new[] { (1920, 800), (1280, 720), (640, 480), (3840, 2160), (17, 33), (1000, 1000) })
        {
            DdsTexture.PadToPowerOfTwo(Blank(w, h), out float u, out float v);
            Assert.InRange(u, 0.5001f, 1f);
            Assert.InRange(v, 0.5001f, 1f);
        }
    }
}
