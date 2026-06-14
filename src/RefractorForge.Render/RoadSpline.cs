namespace RefractorForge.Render;

/// <summary>One densified point on a road centerline: world position (Y = the graded road height), the road
/// half-width at this point (per-point widths interpolate along the curve), and the accumulated arc length in
/// metres from the road start (the along-road "v" coordinate for oriented texturing).</summary>
public readonly record struct RoadSample(float X, float Y, float Z, float HalfWidth, float ArcLen);

/// <summary>
/// Centripetal Catmull-Rom spline through clicked road control points — the curve passes exactly through every
/// point, bends smoothly between them, and (being centripetal) never loops or cusps at tight turns. Heights ride
/// the same spline so the road grades smoothly; widths lerp linearly per segment (no overshoot). Pure math, no
/// engine types, so the Demo harness gates it headlessly.
/// </summary>
public static class RoadSpline
{
    /// <summary>Densify the control polyline into samples spaced ~<paramref name="stepMeters"/> apart by arc
    /// length. Two control points degenerate to a straight segment; near-duplicate points are skipped.</summary>
    public static List<RoadSample> Resample(IReadOnlyList<(float X, float Y, float Z, float HalfW)> ctrl, float stepMeters)
    {
        var pts = new List<(float X, float Y, float Z, float HalfW)>();
        foreach (var p in ctrl)   // drop near-duplicates (a double-click would put a cusp in the parameterization)
        {
            if (pts.Count > 0)
            {
                var q = pts[^1];
                float dx = p.X - q.X, dz = p.Z - q.Z;
                if (dx * dx + dz * dz < 0.01f) continue;
            }
            pts.Add(p);
        }
        var outPts = new List<RoadSample>();
        if (pts.Count == 0) return outPts;
        float step = MathF.Max(stepMeters, 0.05f);
        if (pts.Count == 1) { outPts.Add(new RoadSample(pts[0].X, pts[0].Y, pts[0].Z, pts[0].HalfW, 0f)); return outPts; }

        float arc = 0f;
        (float X, float Y, float Z)? last = null;
        float sinceEmit = float.MaxValue;   // force-emit the very first sample

        void Emit(float x, float y, float z, float halfW)
        {
            if (last is { } lp)
            {
                float dx = x - lp.X, dz = z - lp.Z;
                float d = MathF.Sqrt(dx * dx + dz * dz);
                arc += d; sinceEmit += d;
            }
            last = (x, y, z);
            if (sinceEmit >= step)
            {
                outPts.Add(new RoadSample(x, y, z, halfW, arc));
                sinceEmit = 0f;
            }
        }

        for (int i = 0; i + 1 < pts.Count; i++)
        {
            // Neighbour points for this segment (ends clamp to themselves — the standard endpoint treatment).
            var p0 = pts[Math.Max(i - 1, 0)];
            var p1 = pts[i];
            var p2 = pts[i + 1];
            var p3 = pts[Math.Min(i + 2, pts.Count - 1)];

            // Centripetal parameterization: knot spacing = sqrt of chord length (alpha = 0.5).
            static float Knot(float t, (float X, float Y, float Z, float HalfW) a, (float X, float Y, float Z, float HalfW) b)
            {
                float dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
                return t + MathF.Pow(MathF.Max(dx * dx + dy * dy + dz * dz, 1e-8f), 0.25f);
            }
            float t0 = 0f, t1 = Knot(t0, p0, p1), t2 = Knot(t1, p1, p2), t3 = Knot(t2, p2, p3);

            float chord = MathF.Sqrt((p2.X - p1.X) * (p2.X - p1.X) + (p2.Z - p1.Z) * (p2.Z - p1.Z));
            int n = Math.Max(2, (int)MathF.Ceiling(chord / step) * 2);   // fine sub-steps; Emit spaces them by arc
            int jStart = i == 0 ? 0 : 1;                                  // segment start == previous segment end
            for (int j = jStart; j <= n; j++)
            {
                float t = t1 + (t2 - t1) * (j / (float)n);
                // Barry–Goldman pyramid for one point on the centripetal Catmull-Rom segment.
                float w01 = (t1 - t0) < 1e-6f ? 0f : (t - t0) / (t1 - t0);
                float w12 = (t2 - t1) < 1e-6f ? 0f : (t - t1) / (t2 - t1);
                float w23 = (t3 - t2) < 1e-6f ? 0f : (t - t2) / (t3 - t2);
                float a1x = p0.X + (p1.X - p0.X) * w01, a1y = p0.Y + (p1.Y - p0.Y) * w01, a1z = p0.Z + (p1.Z - p0.Z) * w01;
                float a2x = p1.X + (p2.X - p1.X) * w12, a2y = p1.Y + (p2.Y - p1.Y) * w12, a2z = p1.Z + (p2.Z - p1.Z) * w12;
                float a3x = p2.X + (p3.X - p2.X) * w23, a3y = p2.Y + (p3.Y - p2.Y) * w23, a3z = p2.Z + (p3.Z - p2.Z) * w23;
                float wb1 = (t2 - t0) < 1e-6f ? 0f : (t - t0) / (t2 - t0);
                float wb2 = (t3 - t1) < 1e-6f ? 0f : (t - t1) / (t3 - t1);
                float b1x = a1x + (a2x - a1x) * wb1, b1y = a1y + (a2y - a1y) * wb1, b1z = a1z + (a2z - a1z) * wb1;
                float b2x = a2x + (a3x - a2x) * wb2, b2y = a2y + (a3y - a2y) * wb2, b2z = a2z + (a3z - a2z) * wb2;
                float cx = b1x + (b2x - b1x) * w12, cy = b1y + (b2y - b1y) * w12, cz = b1z + (b2z - b1z) * w12;
                // Width lerps linearly along the segment (Catmull-Rom on width could overshoot to negative).
                float hw = p1.HalfW + (p2.HalfW - p1.HalfW) * (j / (float)n);
                Emit(cx, cy, cz, hw);
            }
        }
        // Always include the exact final control point (Emit may have skipped it if it fell between steps).
        var lastCtrl = pts[^1];
        if (outPts.Count == 0 || MathF.Abs(outPts[^1].X - lastCtrl.X) > 0.01f || MathF.Abs(outPts[^1].Z - lastCtrl.Z) > 0.01f)
        {
            float dx = lastCtrl.X - (last?.X ?? lastCtrl.X), dz = lastCtrl.Z - (last?.Z ?? lastCtrl.Z);
            outPts.Add(new RoadSample(lastCtrl.X, lastCtrl.Y, lastCtrl.Z, lastCtrl.HalfW, arc + MathF.Sqrt(dx * dx + dz * dz)));
        }
        return outPts;
    }
}
