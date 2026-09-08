using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace RefractorForge.Render;

/// <summary>
/// Small solid shapes the editor draws where an object has no geometry of its own. They go through the normal
/// object shader, so they are interleaved position(3) + normal(3) + uv(2), the layout <c>MakeMesh</c> expects.
/// <para>
/// These live here rather than inline in the viewer so their winding and volume can be asserted headlessly - a
/// marker with a flipped face reads as a hole in the shape, and that is not something you want to discover by
/// launching the GPU.
/// </para>
/// </summary>
public static class MarkerMeshes
{
    /// <summary>
    /// A quaver: an extruded note head, stem and flag. Roughly 2 units tall and 1.5 wide, centred on the origin,
    /// facing +Z. Drawn at a sound area, which is an <c>AreaObject</c> - a pure trigger volume with no mesh at
    /// all - so this marker IS the object as far as the editor is concerned. It should say what it is rather than
    /// wear the same "mesh not found" diamond as a genuinely broken template.
    /// </summary>
    public static (float[] Verts, uint[] Indices) MusicNote()
    {
        var v = new List<float>();
        var f = new List<uint>();
        const float z = 0.10f;

        // Head: a tilted ellipse, the way a printed note head is drawn.
        var head = new (float X, float Y)[20];
        for (int i = 0; i < head.Length; i++)
        {
            float t = i / (float)head.Length * MathF.PI * 2f;
            float ex = MathF.Cos(t) * 0.42f, ey = MathF.Sin(t) * 0.29f;
            float ca = MathF.Cos(-0.38f), sa = MathF.Sin(-0.38f);
            head[i] = (-0.22f + ex * ca - ey * sa, -0.62f + ex * sa + ey * ca);
        }
        Extrude(v, f, head, -z, z);
        Extrude(v, f, new (float, float)[] { (0.14f, -0.62f), (0.28f, -0.62f), (0.28f, 0.95f), (0.14f, 0.95f) }, -z, z);
        Extrude(v, f, new (float, float)[] { (0.28f, 0.95f), (0.74f, 0.60f), (0.66f, 0.18f), (0.28f, 0.50f) }, -z, z);
        return (v.ToArray(), f.ToArray());
    }

    /// <summary>
    /// Extrude one CONVEX outline between two Z planes: two fan caps plus a wall per edge. Convex because the caps
    /// are fans; the winding is normalised here, so a caller may give its points either way round.
    /// The walls carry their own vertices, which is what keeps the silhouette edge sharp instead of rounding it off
    /// against the cap normal.
    /// </summary>
    private static void Extrude(List<float> v, List<uint> f, (float X, float Y)[] poly, float z0, float z1)
    {
        double area = 0;
        for (int i = 0; i < poly.Length; i++)
        {
            var a = poly[i]; var c = poly[(i + 1) % poly.Length];
            area += a.X * c.Y - c.X * a.Y;
        }
        if (area < 0) poly = poly.Reverse().ToArray();     // CCW seen from +Z, so the normals below face outward

        void Push(float x, float y, float zz, Vector3 n)
        { v.Add(x); v.Add(y); v.Add(zz); v.Add(n.X); v.Add(n.Y); v.Add(n.Z); v.Add(0f); v.Add(0f); }

        int n0 = poly.Length;
        uint b = (uint)(v.Count / 8);
        foreach (var q in poly) Push(q.X, q.Y, z1, new Vector3(0f, 0f, 1f));
        foreach (var q in poly) Push(q.X, q.Y, z0, new Vector3(0f, 0f, -1f));
        for (int i = 1; i + 1 < n0; i++) { f.Add(b); f.Add(b + (uint)i); f.Add(b + (uint)i + 1); }
        for (int i = 1; i + 1 < n0; i++)
        { f.Add(b + (uint)n0); f.Add(b + (uint)(n0 + i + 1)); f.Add(b + (uint)(n0 + i)); }

        for (int i = 0; i < n0; i++)
        {
            var a = poly[i]; var c = poly[(i + 1) % n0];
            var nrm = Vector3.Normalize(new Vector3(c.Y - a.Y, a.X - c.X, 0f));
            uint w = (uint)(v.Count / 8);
            Push(a.X, a.Y, z1, nrm); Push(c.X, c.Y, z1, nrm); Push(c.X, c.Y, z0, nrm); Push(a.X, a.Y, z0, nrm);
            // Wound so the FACE agrees with `nrm`: going front-edge -> back-edge round the outline puts the
            // geometric normal at -(dy, -dx), the opposite of the outward one, and the wall culls away.
            f.Add(w); f.Add(w + 2); f.Add(w + 1); f.Add(w); f.Add(w + 3); f.Add(w + 2);
        }
    }

    /// <summary>
    /// Six times the signed volume of a closed triangle mesh (the divergence-theorem sum). Positive means every
    /// face winds outward, which is the one thing that can silently go wrong when a shape is built from extruded
    /// outlines - a flipped cap or wall reads as a hole once the shader backface-culls it.
    /// </summary>
    public static double SignedVolume6(float[] verts, uint[] indices)
    {
        double sum = 0;
        Vector3 At(uint i) => new(verts[i * 8], verts[i * 8 + 1], verts[i * 8 + 2]);
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            var a = At(indices[t]); var b = At(indices[t + 1]); var c = At(indices[t + 2]);
            sum += Vector3.Dot(a, Vector3.Cross(b - a, c - a));
        }
        return sum;
    }
}
