using System.Numerics;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// A face that runs from in front of the camera to behind it - a floor under the camera, a headlight beam pointed past
/// it, anything big once a preview is zoomed in - is cut at the near plane and its front part drawn. It used to be left
/// out whole, so long faces vanished from the picture as soon as one corner passed the camera.
/// </summary>
public class SoftwareRendererNearClipTests
{
    /// <summary>A 32 x 32 picture from 5 m up the Z axis looking at the origin, 60 degrees high.</summary>
    private static (ImageBuffer Img, Camera Cam) Scene()
    {
        var img = new ImageBuffer(32, 32);
        img.Clear(Vector3.Zero);
        var cam = new Camera { Aspect = 1f, Near = 0.1f, Far = 100f, Position = new Vector3(0f, 0f, 5f) };
        cam.LookAt(new Vector3(0f, 0f, 0f));
        return (img, cam);
    }

    /// <summary>A floor a metre below the camera from 10 m ahead of it to 5 m behind it.</summary>
    private static readonly Vector3[] Floor = { new(-2f, -1f, -5f), new(2f, -1f, -5f), new(2f, -1f, 10f), new(-2f, -1f, 10f) };
    private static readonly int[] FloorIndices = { 0, 1, 2, 0, 2, 3 };

    private static (byte R, byte G, byte B) At(ImageBuffer img, int x, int y)
    {
        int p = (y * img.W + x) * 3;
        return (img.Rgb[p], img.Rgb[p + 1], img.Rgb[p + 2]);
    }

    [Fact]
    public void A_face_running_past_the_camera_draws_its_part_in_front()
    {
        var (img, cam) = Scene();
        SoftwareRenderer.DrawModels(img, cam, Vector3.UnitY, Floor, FloorIndices,
                                    new[] { new ModelInstance(Matrix4x4.Identity, Vector3.One) }, unlit: true);
        Assert.Equal((255, 255, 255), At(img, 16, 31));     // the floor at the camera's feet
        Assert.Equal((255, 255, 255), At(img, 16, 21));     // and out towards its far edge
        Assert.Equal((0, 0, 0), At(img, 16, 10));           // above the horizon: nothing
        Assert.InRange(img.Depth[31 * 32 + 16], 0f, 1f);    // depth in the near..far range

        // Wholly behind the camera: nothing.
        var (img2, cam2) = Scene();
        var behind = Floor.Select(p => p with { Z = p.Z + 16f }).ToArray();
        SoftwareRenderer.DrawModels(img2, cam2, Vector3.UnitY, behind, FloorIndices,
                                    new[] { new ModelInstance(Matrix4x4.Identity, Vector3.One) }, unlit: true);
        Assert.All(img2.Rgb, b => Assert.Equal(0, b));
    }

    [Fact]
    public void A_cut_textured_face_keeps_its_texture_where_it_was()
    {
        // Red for the first half of V (the far end), green for the second (under and behind the camera). V runs
        // 0 -> 1 from 10 m ahead to 5 m behind, so it is 0.5 at 2.5 m ahead: pixel row 27.6. Blending UVs on the
        // picture instead of in clip space would put the change elsewhere.
        var rgba = new byte[1 * 8 * 4];
        for (int row = 0; row < 8; row++)
        {
            rgba[row * 4] = (byte)(row < 4 ? 255 : 0);
            rgba[row * 4 + 1] = (byte)(row < 4 ? 0 : 255);
            rgba[row * 4 + 3] = 255;
        }
        var uvs = Floor.Select(p => new Vector2(0.5f, (p.Z + 5f) / 15f)).ToArray();
        var (img, cam) = Scene();
        SoftwareRenderer.DrawModelsTextured(img, cam, Vector3.UnitY, Floor, uvs, FloorIndices, new Texture2D(1, 8, rgba), false,
                                            new[] { new ModelInstance(Matrix4x4.Identity, Vector3.One) }, unlit: true);
        var near = At(img, 16, 31);
        var far = At(img, 16, 23);
        Assert.True(near.G > 200 && near.R < 60, $"{near}: green under the camera");
        Assert.True(far.R > 200 && far.G < 60, $"{far}: red further out");
    }
}
