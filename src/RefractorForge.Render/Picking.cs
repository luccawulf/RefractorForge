using System.Numerics;

namespace RefractorForge.Render;

public readonly record struct Ray(Vector3 Origin, Vector3 Dir);

/// <summary>
/// Selection math: turn a screen click into a world ray (inverse of the camera projection),
/// then find the nearest object. Pure and verified against the same projection the renderer uses.
/// </summary>
public static class Picking
{
    public static Ray ScreenToRay(Camera cam, float px, float py, int width, int height)
    {
        Matrix4x4.Invert(cam.ViewProjection, out var inv);
        float ndcX = 2f * px / width - 1f;
        float ndcY = 1f - 2f * py / height;

        Vector3 Unproject(float ndcZ)
        {
            var p = Vector4.Transform(new Vector4(ndcX, ndcY, ndcZ, 1f), inv); // row-vector * inv(VP)
            return new Vector3(p.X, p.Y, p.Z) / p.W;
        }

        var near = Unproject(0f);
        var far = Unproject(1f);
        return new Ray(near, Vector3.Normalize(far - near));
    }

    /// <summary>
    /// The world point drawn at pixel (px, py) whose depth-buffer value is <paramref name="depth"/> (0..1). The
    /// projection puts z in 0..1 and GL maps its NDC -1..1 onto the depth buffer, so NDC z = 2d - 1 is exactly the
    /// value the matrix produced; the rest is the same inverse <see cref="ScreenToRay"/> uses. Placement reads the
    /// pick buffer's depth under the cursor and turns it back into the surface point with this.
    /// </summary>
    public static Vector3 UnprojectDepth(Matrix4x4 viewProj, float px, float py, int width, int height, float depth)
    {
        Matrix4x4.Invert(viewProj, out var inv);
        float ndcX = 2f * px / width - 1f;
        float ndcY = 1f - 2f * py / height;
        var p = Vector4.Transform(new Vector4(ndcX, ndcY, 2f * depth - 1f, 1f), inv);
        return new Vector3(p.X, p.Y, p.Z) / p.W;
    }

    /// <summary>Index of the nearest object whose pick-sphere the ray hits, or -1.</summary>
    public static int PickNearest(Ray ray, IReadOnlyList<Vector3> points, float radius)
    {
        int best = -1;
        float bestT = float.MaxValue;
        float r2 = radius * radius;
        for (int i = 0; i < points.Count; i++)
        {
            var oc = ray.Origin - points[i];
            float b = Vector3.Dot(oc, ray.Dir);
            float c = Vector3.Dot(oc, oc) - r2;
            float disc = b * b - c;
            if (disc < 0f) continue;
            float sq = MathF.Sqrt(disc);
            float t = -b - sq;
            if (t < 0f) t = -b + sq;
            if (t < 0f) continue;
            if (t < bestT) { bestT = t; best = i; }
        }
        return best;
    }

    /// <summary>
    /// Pick the object whose screen projection is closest to the cursor within <paramref name="pixelRadius"/>.
    /// This makes selection feel natural at any zoom (a fixed world-space pick sphere is tiny when far and
    /// smaller than a big building when near). Ties break toward the camera (front object wins).
    /// </summary>
    public static int PickNearestScreen(Camera cam, Vector2 cursorPx, int width, int height,
                                        IReadOnlyList<Vector3> points, float pixelRadius)
    {
        var vp = cam.ViewProjection;
        int best = -1;
        float bestPix = pixelRadius;
        float bestDepth = float.MaxValue;
        for (int i = 0; i < points.Count; i++)
        {
            var clip = Vector4.Transform(new Vector4(points[i], 1f), vp);
            if (clip.W <= 1e-4f) continue;                 // behind the camera
            float sx = (clip.X / clip.W * 0.5f + 0.5f) * width;
            float sy = (1f - (clip.Y / clip.W * 0.5f + 0.5f)) * height;
            float d = Vector2.Distance(new Vector2(sx, sy), cursorPx);
            float depth = clip.W;                          // distance along view (positive in front)
            // Within the pixel threshold: prefer the closer-to-cursor, then the nearer-to-camera.
            if (d <= pixelRadius && (d < bestPix - 4f || (d <= bestPix + 4f && depth < bestDepth)))
            { bestPix = Math.Min(bestPix, d); bestDepth = depth; best = i; }
        }
        return best;
    }
}
