using System.Numerics;

namespace RefractorForge.Render;

/// <summary>
/// The underground minimap BfVietnam 1.2 shows while a soldier is inside a below-ground object.
///
/// The engine binds one texture to one object template over a world rectangle -
/// <c>mapManager.addObjectMap o_tunnelsA TunnelsAMap 886/871/328/327</c> (x, z, width, height, metres) - and
/// draws it in place of the surface map when the player is in that object. Retail maps are hand-painted
/// parchments (Cedar Falls' TunnelsAMap.dds); Battlecraft's "Generate Underground Map" rendered the tunnel
/// meshes top-down instead, and this does the same: a floor plan of every below-ground mesh in the rectangle,
/// floors light and walls dark, north up and east right like the surface map.
/// </summary>
public static class TunnelMap
{
    /// <summary>The world-space footprint of a placed mesh, as the engine wants it: x, z, width, height.</summary>
    public static (float X, float Z, float W, float H) WorldRect(MeshLibrary.Mesh mesh, Matrix4x4 world)
    {
        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        foreach (var p in mesh.Positions)
        {
            var w = Vector3.Transform(p, world);
            if (w.X < minX) minX = w.X; if (w.X > maxX) maxX = w.X;
            if (w.Z < minZ) minZ = w.Z; if (w.Z > maxZ) maxZ = w.Z;
        }
        if (minX > maxX) return (0, 0, 0, 0);
        return (minX, minZ, maxX - minX, maxZ - minZ);
    }

    /// <summary>The union of several footprints. One template gets one map, so several instances share it.</summary>
    public static (float X, float Z, float W, float H) Union(IEnumerable<(float X, float Z, float W, float H)> rects)
    {
        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        foreach (var r in rects)
        {
            if (r.W <= 0 || r.H <= 0) continue;
            minX = MathF.Min(minX, r.X); minZ = MathF.Min(minZ, r.Z);
            maxX = MathF.Max(maxX, r.X + r.W); maxZ = MathF.Max(maxZ, r.Z + r.H);
        }
        if (minX > maxX) return (0, 0, 0, 0);
        // A little margin so the outermost wall is not cut by the texture edge.
        float m = MathF.Max(2f, 0.02f * MathF.Max(maxX - minX, maxZ - minZ));
        return (minX - m, minZ - m, maxX - minX + 2 * m, maxZ - minZ + 2 * m);
    }

    /// <summary>The rectangle grown to a square about its centre. The map texture is square, and Cedar Falls'
    /// retail rectangle (328 x 327 m over a 193 x 292 m mesh) shows DICE kept the two in step so the drawing is
    /// not stretched in one direction.</summary>
    public static (float X, float Z, float W, float H) Squared((float X, float Z, float W, float H) r)
    {
        float side = MathF.Max(r.W, r.H);
        if (side <= 0) return r;
        return (r.X + r.W * 0.5f - side * 0.5f, r.Z + r.H * 0.5f - side * 0.5f, side, side);
    }

    /// <summary>
    /// Render the meshes top-down into a square texture covering <paramref name="rect"/>. Texel (0,0) is the
    /// north-west corner (x = rect.X, z = rect.Z + rect.H); each pixel keeps its LOWEST surface, so the floor
    /// of a corridor wins over the roof above it and the result reads as a floor plan.
    /// </summary>
    public static Texture2D Render(IEnumerable<(MeshLibrary.Mesh Mesh, Matrix4x4 World)> objects,
                                   (float X, float Z, float W, float H) rect, int size = 512)
    {
        if (size < 16) size = 16;
        var rgba = new byte[size * size * 4];
        var depth = new float[size * size];
        Array.Fill(depth, float.MaxValue);

        // Parchment ground, dark - the area the tunnel complex sits under. The retail maps are a warm brown.
        for (int i = 0; i < size * size; i++)
        {
            rgba[i * 4 + 0] = 62; rgba[i * 4 + 1] = 50; rgba[i * 4 + 2] = 36; rgba[i * 4 + 3] = 255;
        }
        if (rect.W <= 0 || rect.H <= 0) return new Texture2D(size, size, rgba);

        float sx = size / rect.W, sz = size / rect.H;
        Vector2 Proj(Vector3 w) => new((w.X - rect.X) * sx, (rect.Z + rect.H - w.Z) * sz);

        foreach (var (mesh, world) in objects)
        {
            var wp = new Vector3[mesh.Positions.Length];
            for (int i = 0; i < wp.Length; i++) wp[i] = Vector3.Transform(mesh.Positions[i], world);
            foreach (var part in mesh.Parts)
            {
                var idx = part.Indices;
                for (int t = 0; t + 2 < idx.Length; t += 3)
                {
                    var a = wp[idx[t]]; var b = wp[idx[t + 1]]; var c = wp[idx[t + 2]];
                    var n = Vector3.Cross(b - a, c - a);
                    float len = n.Length();
                    if (len < 1e-8f) continue;
                    n /= len;
                    // Floors face up and read light; walls and ceilings darker. Both sides of a face count as
                    // the same surface - a floor's winding is whatever the modeller left it.
                    float up = MathF.Abs(n.Y);
                    byte shade = (byte)(90 + 120 * up);
                    var col = (R: (byte)Math.Min(255, shade + 40), G: (byte)Math.Min(255, shade + 20), B: shade);
                    Fill(Proj(a), a.Y, Proj(b), b.Y, Proj(c), c.Y, col, rgba, depth, size);
                }
            }
        }
        return new Texture2D(size, size, rgba);
    }

    /// <summary>Scan-convert one triangle, keeping the lowest Y per pixel.</summary>
    private static void Fill(Vector2 p0, float y0, Vector2 p1, float y1, Vector2 p2, float y2,
                             (byte R, byte G, byte B) col, byte[] rgba, float[] depth, int size)
    {
        int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(p0.X, MathF.Min(p1.X, p2.X))));
        int maxX = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(p0.X, MathF.Max(p1.X, p2.X))));
        int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(p0.Y, MathF.Min(p1.Y, p2.Y))));
        int maxY = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(p0.Y, MathF.Max(p1.Y, p2.Y))));
        if (minX > maxX || minY > maxY) return;
        float area = (p1.X - p0.X) * (p2.Y - p0.Y) - (p2.X - p0.X) * (p1.Y - p0.Y);
        if (MathF.Abs(area) < 1e-6f)
        {
            // Edge-on (a wall seen from above): draw its line so thin walls still show.
            Line(p0, p1, y0, col, rgba, depth, size); Line(p1, p2, y1, col, rgba, depth, size);
            return;
        }
        float inv = 1f / area;
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                float w0 = ((p1.X - px) * (p2.Y - py) - (p2.X - px) * (p1.Y - py)) * inv;
                float w1 = ((p2.X - px) * (p0.Y - py) - (p0.X - px) * (p2.Y - py)) * inv;
                float w2 = 1f - w0 - w1;
                if (w0 < -1e-4f || w1 < -1e-4f || w2 < -1e-4f) continue;
                float h = w0 * y0 + w1 * y1 + w2 * y2;
                int i = y * size + x;
                if (h >= depth[i]) continue;
                depth[i] = h;
                rgba[i * 4 + 0] = col.R; rgba[i * 4 + 1] = col.G; rgba[i * 4 + 2] = col.B;
            }
    }

    private static void Line(Vector2 a, Vector2 b, float h, (byte R, byte G, byte B) col, byte[] rgba, float[] depth, int size)
    {
        int steps = (int)MathF.Ceiling(MathF.Max(MathF.Abs(b.X - a.X), MathF.Abs(b.Y - a.Y)));
        for (int s = 0; s <= steps; s++)
        {
            float t = steps == 0 ? 0f : s / (float)steps;
            int x = (int)(a.X + (b.X - a.X) * t), y = (int)(a.Y + (b.Y - a.Y) * t);
            if (x < 0 || y < 0 || x >= size || y >= size) continue;
            int i = y * size + x;
            if (h > depth[i]) continue;
            depth[i] = h;
            rgba[i * 4 + 0] = col.R; rgba[i * 4 + 1] = col.G; rgba[i * 4 + 2] = col.B;
        }
    }
}
