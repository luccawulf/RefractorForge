using System.Numerics;

namespace RefractorForge.Render;

/// <summary>
/// Math for a world-space translate gizmo: pick the axis handle under a screen ray, and project ray
/// motion onto an axis to get the slide distance. Pure and GPU-free, so it can be unit-tested against
/// known geometry. The viewer reuses the marker shader to draw the handles as coloured GL_LINES.
/// </summary>
public static class Gizmo
{
    public static Vector3 Axis(int i) => i == 1 ? Vector3.UnitY : i == 2 ? Vector3.UnitZ : Vector3.UnitX;

    /// <summary>Parameter t along the infinite axis line (origin + dir*t) of the point closest to the ray.</summary>
    public static float ClosestAxisParam(Ray ray, Vector3 origin, Vector3 axisDir)
    {
        var d = Vector3.Normalize(ray.Dir);
        var a = Vector3.Normalize(axisDir);
        var r = ray.Origin - origin;
        float b = Vector3.Dot(d, a);
        float denom = 1f - b * b;                       // d.d = a.a = 1
        if (denom < 1e-6f) return Vector3.Dot(r, a);    // ray ~parallel to axis: project the ray origin
        float dd = Vector3.Dot(d, r);
        float ee = Vector3.Dot(a, r);
        return (ee - b * dd) / denom;                   // t on axis at the lines' closest approach
    }

    /// <summary>Shortest distance between the (forward) ray and a finite segment [p0,p1].</summary>
    public static float RaySegmentDistance(Ray ray, Vector3 p0, Vector3 p1)
    {
        var d = Vector3.Normalize(ray.Dir);
        var seg = p1 - p0;
        float segLen = seg.Length();
        if (segLen < 1e-6f)
        {
            float s0 = MathF.Max(0f, Vector3.Dot(p0 - ray.Origin, d));
            return Vector3.Distance(ray.Origin + d * s0, p0);
        }
        var a = seg / segLen;
        var r = ray.Origin - p0;
        float b = Vector3.Dot(d, a);
        float denom = 1f - b * b;
        float sRay, tSeg;
        if (denom < 1e-6f) { sRay = MathF.Max(0f, -Vector3.Dot(r, d)); tSeg = 0f; }
        else
        {
            float dd = Vector3.Dot(d, r);
            float ee = Vector3.Dot(a, r);
            sRay = (b * ee - dd) / denom;
            tSeg = (ee - b * dd) / denom;
        }
        if (sRay < 0f) sRay = 0f;                        // ray cannot extend behind its origin
        tSeg = Math.Clamp(tSeg, 0f, segLen);
        return Vector3.Distance(ray.Origin + d * sRay, p0 + a * tSeg);
    }

    /// <summary>Nearest axis (0=X,1=Y,2=Z) whose handle the ray passes within <paramref name="threshold"/> of, else -1.</summary>
    public static int PickAxis(Ray ray, Vector3 origin, float length, float threshold)
    {
        int best = -1; float bestD = threshold;
        for (int i = 0; i < 3; i++)
        {
            float dist = RaySegmentDistance(ray, origin, origin + Axis(i) * length);
            if (dist < bestD) { bestD = dist; best = i; }
        }
        return best;
    }

    // ---- rotate gizmo --------------------------------------------------------
    // Rotation is BFV Euler (CreateFromYawPitchRoll(X,Y,Z)): X=yaw about world Y, Y=pitch about world X,
    // Z=roll about world Z. Each ring edits one Euler channel and lies in the plane perpendicular to that
    // channel's world axis. RingFrame(c) returns (worldAxis, u, v) where (u,v) span the ring's plane.
    public static (Vector3 axis, Vector3 u, Vector3 v) RingFrame(int channel) => channel switch
    {
        0 => (Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ),   // yaw  (Rotation.X) — plane XZ
        1 => (Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ),   // pitch(Rotation.Y) — plane YZ
        _ => (Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY),   // roll (Rotation.Z) — plane XY
    };

    /// <summary>Intersect the forward ray with the plane through <paramref name="planePoint"/> with the given normal.</summary>
    public static bool RayPlaneHit(Ray ray, Vector3 planePoint, Vector3 normal, out Vector3 hit, out float t)
    {
        hit = default; t = 0f;
        var d = Vector3.Normalize(ray.Dir);
        float denom = Vector3.Dot(normal, d);
        if (MathF.Abs(denom) < 1e-5f) return false;          // ray parallel to plane
        t = Vector3.Dot(normal, planePoint - ray.Origin) / denom;
        if (t < 0f) return false;
        hit = ray.Origin + d * t;
        return true;
    }

    /// <summary>Angle (radians) of a hit point around ring <paramref name="channel"/>, in its (u,v) basis.</summary>
    public static float RingAngle(Vector3 hit, Vector3 origin, int channel)
    {
        var (_, u, v) = RingFrame(channel);
        var r = hit - origin;
        return MathF.Atan2(Vector3.Dot(r, v), Vector3.Dot(r, u));
    }

    /// <summary>Pick the ring (channel 0/1/2) the ray grazes near <paramref name="radius"/> (within band), else -1.</summary>
    public static int PickRing(Ray ray, Vector3 origin, float radius, float band)
    {
        int best = -1; float bestT = float.MaxValue;
        var d = Vector3.Normalize(ray.Dir);
        for (int c = 0; c < 3; c++)
        {
            var (axis, _, _) = RingFrame(c);
            if (MathF.Abs(Vector3.Dot(d, axis)) < 0.08f) continue;        // ring seen edge-on
            if (!RayPlaneHit(ray, origin, axis, out var hit, out float t)) continue;
            if (MathF.Abs(Vector3.Distance(hit, origin) - radius) <= band && t < bestT) { bestT = t; best = c; }
        }
        return best;
    }

    // ---- uniform scale gizmo -------------------------------------------------
    /// <summary>Project a world point to pixel coordinates (matching Picking.ScreenToRay's convention).</summary>
    public static Vector2 Project(Vector3 world, Matrix4x4 viewProj, int w, int h)
    {
        var c = Vector4.Transform(new Vector4(world, 1f), viewProj);
        if (c.W <= 1e-4f) return new Vector2(float.NaN, float.NaN);
        return new Vector2((c.X / c.W * 0.5f + 0.5f) * w, (1f - (c.Y / c.W * 0.5f + 0.5f)) * h);
    }
}
