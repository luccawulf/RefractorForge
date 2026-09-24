using System.Collections.Generic;
using System.Linq;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Mesh;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// What of a model can be seen from outside: a part closed in by another is hidden, see-through parts let the view in,
/// a floor-less model standing on the ground hides what is under its skin; and the simplifier spends a hidden part's
/// triangles first when told how much each part counts.
/// </summary>
public class MeshVisibilityTests
{
    /// <summary>A box as a submesh, its faces wound outward; <paramref name="skip"/> leaves faces out (0-5: -X +X -Y +Y -Z +Z).</summary>
    private static ObjSubMesh Box(string material, Vec3 a, Vec3 b, params int[] skip)
    {
        var s = new ObjSubMesh { Material = material, Object = material };
        void Quad(int side, Vec3 p0, Vec3 p1, Vec3 p2, Vec3 p3)
        {
            if (skip.Contains(side)) return;
            int o = s.Positions.Count;
            s.Positions.AddRange(new[] { p0, p1, p2, p3 });
            for (int i = 0; i < 4; i++) { s.Normals.Add(new Vec3(0, 1, 0)); s.Uvs.Add((0f, 0f)); }
            s.Faces.Add((o, o + 1, o + 2));
            s.Faces.Add((o, o + 2, o + 3));
        }
        Quad(0, new(a.X, a.Y, a.Z), new(a.X, a.Y, b.Z), new(a.X, b.Y, b.Z), new(a.X, b.Y, a.Z));
        Quad(1, new(b.X, a.Y, b.Z), new(b.X, a.Y, a.Z), new(b.X, b.Y, a.Z), new(b.X, b.Y, b.Z));
        Quad(2, new(a.X, a.Y, a.Z), new(b.X, a.Y, a.Z), new(b.X, a.Y, b.Z), new(a.X, a.Y, b.Z));
        Quad(3, new(a.X, b.Y, b.Z), new(b.X, b.Y, b.Z), new(b.X, b.Y, a.Z), new(a.X, b.Y, a.Z));
        Quad(4, new(b.X, a.Y, a.Z), new(a.X, a.Y, a.Z), new(a.X, b.Y, a.Z), new(b.X, b.Y, a.Z));
        Quad(5, new(a.X, a.Y, b.Z), new(b.X, a.Y, b.Z), new(b.X, b.Y, b.Z), new(a.X, b.Y, b.Z));
        return s;
    }

    private static ObjMesh Mesh(params ObjSubMesh[] subs)
    {
        var m = new ObjMesh();
        m.SubMeshes.AddRange(subs);
        m.RecomputeBounds();
        return m;
    }

    [Fact]
    public void A_part_closed_in_is_hidden_and_glass_lets_the_view_in()
    {
        var shell = Box("shell", new Vec3(-2, 0, -2), new Vec3(2, 2, 2));
        var engine = Box("engine", new Vec3(-0.5f, 0.5f, -0.5f), new Vec3(0.5f, 1.5f, 0.5f));
        var e = MeshVisibility.Exposure(Mesh(shell, engine));
        Assert.Equal(1f, e[0]);
        Assert.Equal(0f, e[1]);

        // The shell's +X side is a window: the view reaches the engine through it.
        var body = Box("body", new Vec3(-2, 0, -2), new Vec3(2, 2, 2), 1);
        var glass = Box("glass", new Vec3(2, 0, -2), new Vec3(2, 2, 2), 0, 2, 3, 4, 5);
        var seen = MeshVisibility.Exposure(Mesh(body, glass, engine), s => s.Material == "glass");
        Assert.True(seen[2] > 0.3f, $"the engine behind glass is {seen[2]:P0} exposed");

        // What is hidden goes; what is seen stays.
        var mesh = Mesh(shell, engine);
        var visible = MeshVisibility.WithoutHidden(mesh, MeshVisibility.ExposedFaces(mesh), out int removed);
        Assert.Equal(12, removed);
        Assert.Equal(new[] { "shell" }, visible.SubMeshes.Select(s => s.Material));
    }

    /// <summary>A body with no floor (most downloads) standing on the ground: what is inside is open to the air
    /// underneath, and hidden once the ground seals it.</summary>
    [Fact]
    public void The_ground_seals_a_floorless_body()
    {
        var body = Box("body", new Vec3(-2, 0.3f, -2), new Vec3(2, 2, 2), 2);       // no bottom
        var engine = Box("engine", new Vec3(-0.5f, 0.8f, -0.5f), new Vec3(0.5f, 1.5f, 0.5f));
        var wheel = Box("wheel", new Vec3(-2.2f, 0, -1.6f), new Vec3(-1.8f, 0.6f, -1.2f));
        var mesh = Mesh(body, engine, wheel);
        Assert.True(MeshVisibility.Exposure(mesh)[1] > 0.5f);
        Assert.Equal(0f, MeshVisibility.Exposure(mesh, ground: 0.2f)[1]);
    }

    /// <summary>Two identical grids, one counting a tenth as much: the simplifier takes the triangles it needs from it.</summary>
    [Fact]
    public void A_part_that_counts_less_gives_its_triangles_up_first()
    {
        ObjSubMesh Grid(string material, float x0)
        {
            var s = new ObjSubMesh { Material = material, Object = material };
            const int n = 20;
            for (int j = 0; j <= n; j++)
                for (int i = 0; i <= n; i++)
                {
                    float x = x0 + i / (float)n, z = j / (float)n;
                    s.Positions.Add(new Vec3(x, 0.05f * System.MathF.Sin(x * 9f) * System.MathF.Cos(z * 7f), z));
                    s.Normals.Add(new Vec3(0, 1, 0));
                    s.Uvs.Add((0f, 0f));
                }
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    int a = j * (n + 1) + i;
                    s.Faces.Add((a, a + n + 1, a + 1));
                    s.Faces.Add((a + 1, a + n + 1, a + n + 2));
                }
            return s;
        }
        var mesh = Mesh(Grid("seen", 0f), Grid("hidden", 3f));
        var result = MeshDecimator.Decimate(mesh, 500, 1e-5f, true, s => s.Material == "hidden" ? 0.1f : 1f);
        int seen = result.SubMeshes.Single(s => s.Material == "seen").Faces.Count, hidden = result.SubMeshes.Single(s => s.Material == "hidden").Faces.Count;
        Assert.True(hidden < seen / 2, $"seen kept {seen}, hidden kept {hidden}");
    }
}
