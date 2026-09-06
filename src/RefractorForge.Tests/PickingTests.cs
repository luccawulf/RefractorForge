using System.Numerics;
using RefractorForge.Render;
using RefractorForge.Viewer;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Clicking an object in the viewport. Two things have to hold and neither needs a GPU to check:
/// the screen-to-world math must agree with the world-to-screen math the renderer uses, and the box test must
/// pick what is under the cursor rather than whatever happens to surround the camera.
/// </summary>
public class PickingTests
{
    /// <summary>Where the renderer puts a world point, in pixels — the same mapping <c>Gizmo.Project</c> and the
    /// GPU both use (a System.Numerics matrix uploaded untransposed is read column-major by GL, which turns the
    /// row-vector multiply into the column-vector one the shader writes).</summary>
    static Vector2 Drawn(Vector3 world, Camera cam, int w, int h) => Gizmo.Project(world, cam.ViewProjection, w, h);

    static Camera Cam(int w, int h, bool mirror = true) => new()
    {
        Position = new Vector3(470f, 90f, 120f),
        Yaw = 0f,
        Pitch = -0.45f,
        Aspect = w / (float)h,
        MirrorX = mirror,
    };

    /// <summary>
    /// A click where an object is DRAWN must produce a ray that passes through it. This is the invariant that a
    /// wrong aspect, a stale viewport, a mismatched mirror or a mouse fed in the wrong coordinate space would all
    /// break — each of which shows up as "I have to click to one side of things to select them".
    ///
    /// Checked across the display sizes people actually use, because the editor sizes its picks from the
    /// framebuffer and a bug here would appear on some monitors and not others.
    /// </summary>
    [Theory]
    [InlineData(1280, 800)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(3440, 1440)]     // ultrawide: the aspect is far from 16:9
    [InlineData(3840, 2160)]
    [InlineData(3840, 2224)]     // the framebuffer a maximised window gets on a 4K laptop
    public void A_click_where_an_object_draws_makes_a_ray_through_it(int w, int h)
    {
        foreach (bool mirror in new[] { false, true })
        {
            var cam = Cam(w, h, mirror);
            float worst = 0f;
            for (int gz = 0; gz < 7; gz++)
                for (int gx = 0; gx < 7; gx++)
                {
                    var p = new Vector3(470f - 180f + gx * 60f, 30f, 200f + gz * 60f);
                    var px = Drawn(p, cam, w, h);
                    if (float.IsNaN(px.X) || px.X < 0 || px.X >= w || px.Y < 0 || px.Y >= h) continue;
                    var ray = Picking.ScreenToRay(cam, px.X, px.Y, w, h);
                    // Closest approach of the ray to the point, expressed back in pixels so the tolerance means
                    // something to a human: a click is right if it lands within a pixel of the object.
                    var v = p - ray.Origin;
                    var closest = ray.Origin + ray.Dir * Vector3.Dot(v, ray.Dir);
                    var px2 = Drawn(closest, cam, w, h);
                    worst = MathF.Max(worst, Vector2.Distance(px, px2));
                }
            Assert.True(worst < 1f, $"{w}x{h} mirror={mirror}: pick disagrees with the render by {worst:0.##} px");
        }
    }

    static GlObjects.PickBox Box(int index, Vector3 at, Vector3 half, float scale = 1f, float yawDeg = 0f) =>
        new(Matrix4x4.CreateScale(scale)
            * Matrix4x4.CreateFromYawPitchRoll(yawDeg * MathF.PI / 180f, 0f, 0f)
            * Matrix4x4.CreateTranslation(at),
            new Vector3(-half.X, 0f, -half.Z), new Vector3(half.X, half.Y * 2f, half.Z), index);

    /// <summary>
    /// The bug that made selection feel broken on a dense map: a slab test started at t = 0 reports a hit at zero
    /// distance for any box that CONTAINS the ray's origin, so the moment the camera drifted inside some large
    /// object's bounding box that object won every click in the level — including clicks on empty sky.
    /// </summary>
    [Fact]
    public void A_box_around_the_camera_does_not_swallow_every_click()
    {
        // A hangar the camera is standing inside, and a hut out in front of it.
        var hangar = Box(0, new Vector3(470f, 20f, 150f), new Vector3(30f, 20f, 30f));
        var hut = Box(1, new Vector3(500f, 30f, 220f), new Vector3(3f, 2.5f, 3f));
        var boxes = new[] { hangar, hut };
        var origin = new Vector3(470f, 35f, 150f);           // inside the hangar's box

        var toHut = Vector3.Normalize(new Vector3(500f, 32f, 220f) - origin);
        Assert.Equal(1, GlObjects.RaycastBoxes(boxes, origin, toHut));       // the hut, not the box around us

        // Pointing at nothing must not select the enclosing box either... except as a last resort, which is what
        // being inside a box legitimately means when there is nothing else to choose.
        var atSky = Vector3.Normalize(new Vector3(-1f, 0.9f, -1f));
        Assert.Equal(0, GlObjects.RaycastBoxes(new[] { hangar }, origin, atSky));
        Assert.Equal(-1, GlObjects.RaycastBoxes(new[] { hut }, origin, atSky));

        // Among boxes that all contain the camera, the tightest one is the one you are really in.
        var shed = Box(2, new Vector3(470f, 30f, 150f), new Vector3(4f, 3f, 4f));
        Assert.Equal(2, GlObjects.RaycastBoxes(new[] { hangar, shed }, origin, atSky));
    }

    /// <summary>Ordinary selection still works: the nearest box the ray actually enters wins, whatever its scale.</summary>
    [Fact]
    public void The_nearest_box_the_ray_enters_wins_at_any_scale()
    {
        // Level with the boxes' waist (they stand from y=38), looking straight down +Z.
        var origin = new Vector3(470f, 40f, 100f);
        var dir = Vector3.UnitZ;

        var near = Box(0, new Vector3(470f, 38f, 200f), new Vector3(4f, 3f, 4f));
        var far = Box(1, new Vector3(470f, 38f, 400f), new Vector3(10f, 8f, 10f));
        Assert.Equal(0, GlObjects.RaycastBoxes(new[] { near, far }, origin, dir));
        Assert.Equal(0, GlObjects.RaycastBoxes(new[] { far, near }, origin, dir));   // order must not matter

        // A scaled placement: t stays in world units through the inverse transform, so the near one still wins.
        var farBig = Box(1, new Vector3(470f, 38f, 400f), new Vector3(4f, 3f, 4f), scale: 3f);
        Assert.Equal(0, GlObjects.RaycastBoxes(new[] { near, farBig }, origin, dir));
        var nearBig = Box(0, new Vector3(470f, 38f, 200f), new Vector3(2f, 2f, 2f), scale: 2.5f);
        Assert.Equal(0, GlObjects.RaycastBoxes(new[] { nearBig, far }, origin, dir));

        // A rotated placement is tested in its own space, so a click on its turned face still lands.
        var turned = Box(0, new Vector3(470f, 38f, 200f), new Vector3(8f, 3f, 1f), yawDeg: 90f);
        Assert.Equal(0, GlObjects.RaycastBoxes(new[] { turned }, origin, dir));

        // Nothing in the way: nothing selected. A click on empty ground must clear the selection.
        Assert.Equal(-1, GlObjects.RaycastBoxes(new[] { near, far }, origin, Vector3.Normalize(new Vector3(1f, 0.8f, 0f))));
    }
}
