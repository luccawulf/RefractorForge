using System.Numerics;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// See-through parts in the software renderer: drawn after the solid ones with an opacity below 1, they are blended over
/// what is already there and leave the depth alone - as the game draws a blended shader (glass) - so what is behind
/// them shows through and a solid part drawn later is not hidden by them.
/// </summary>
public class SoftwareRendererOpacityTests
{
    private static (ImageBuffer Img, Camera Cam) Scene()
    {
        var img = new ImageBuffer(32, 32);
        img.Clear(Vector3.Zero);
        var cam = new Camera { Aspect = 1f, Near = 0.1f, Far = 100f, Position = new Vector3(0f, 0f, 5f) };
        cam.LookAt(Vector3.Zero);
        return (img, cam);
    }

    private static readonly Vector3 Light = Vector3.Normalize(new Vector3(0f, 0f, 1f));

    private static ModelInstance Cube(float size, float z, Vector3 color)
        => new(Matrix4x4.CreateScale(size) * Matrix4x4.CreateTranslation(0f, 0f, z), color);

    /// <summary>One pane facing the camera (a cube would be two layers of glass: the renderer draws both sides).</summary>
    private static readonly Vector3[] Pane = { new(-0.6f, -0.6f, 1.5f), new(0.6f, -0.6f, 1.5f), new(0.6f, 0.6f, 1.5f), new(-0.6f, 0.6f, 1.5f) };
    private static readonly int[] PaneIndices = { 0, 1, 2, 0, 2, 3 };
    private static readonly ModelInstance[] Red = { new(Matrix4x4.Identity, new Vector3(1f, 0f, 0f)) };

    private static (byte R, byte G, byte B) Centre(ImageBuffer img)
    {
        int p = (16 * 32 + 16) * 3;
        return (img.Rgb[p], img.Rgb[p + 1], img.Rgb[p + 2]);
    }

    [Fact]
    public void A_see_through_part_blends_over_what_is_behind_it_and_writes_no_depth()
    {
        var (img, cam) = Scene();
        SoftwareRenderer.DrawModels(img, cam, Light, SoftwareRenderer.CubePositions, SoftwareRenderer.CubeIndices, new[] { Cube(1f, 0f, Vector3.One) });
        var white = Centre(img);
        float depth = img.Depth[16 * 32 + 16];

        // Red glass in front, half solid: the white behind shows through it.
        SoftwareRenderer.DrawModels(img, cam, Light, Pane, PaneIndices, Red, opacity: 0.5f);
        var mixed = Centre(img);
        Assert.InRange(mixed.G, white.G / 2 - 3, white.G / 2 + 3);
        Assert.True(mixed.R > mixed.G + 40, $"{mixed}: the glass is red");
        Assert.Equal(depth, img.Depth[16 * 32 + 16]);                     // the glass left the depth as it was

        // Fully solid, the same part hides what is behind it.
        var (img2, cam2) = Scene();
        SoftwareRenderer.DrawModels(img2, cam2, Light, SoftwareRenderer.CubePositions, SoftwareRenderer.CubeIndices, new[] { Cube(1f, 0f, Vector3.One) });
        SoftwareRenderer.DrawModels(img2, cam2, Light, Pane, PaneIndices, Red);
        Assert.Equal(0, Centre(img2).G);
    }

    [Fact]
    public void A_see_through_textured_part_blends_its_texels()
    {
        var (img, cam) = Scene();
        SoftwareRenderer.DrawModels(img, cam, Light, SoftwareRenderer.CubePositions, SoftwareRenderer.CubeIndices, new[] { Cube(1f, 0f, Vector3.One) });
        var white = Centre(img);
        var blue = new byte[4 * 4 * 4];
        for (int i = 0; i < blue.Length; i += 4) { blue[i + 2] = 255; blue[i + 3] = 255; }
        var uvs = Pane.Select(p => new Vector2(p.X + 0.5f, p.Y + 0.5f)).ToArray();
        SoftwareRenderer.DrawModelsTextured(img, cam, Light, Pane, uvs, PaneIndices, new Texture2D(4, 4, blue), false,
                                            new[] { new ModelInstance(Matrix4x4.Identity, Vector3.One) }, opacity: 0.25f);
        var mixed = Centre(img);
        Assert.InRange(mixed.R, white.R * 3 / 4 - 3, white.R * 3 / 4 + 3);   // three quarters of the white still shows
        Assert.True(mixed.B > mixed.R, $"{mixed}: tinted blue");
    }
}
