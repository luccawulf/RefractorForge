using System.Text;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Mesh;
using RefractorForge.Formats.Rfa;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// A collision section the caller asked for is never silently left out. COL02 is the collision that blocks movement
/// (proven in game, gate G0): a dense model whose fine collision would not fit the section's 32,767-vertex limit used
/// to get a <c>rem ... left out</c> line and no COL02, and players walked through it while the import reported
/// success. Now the section is built from the next source that fits - the same shape with its shared corners
/// welded, then each lower level of detail, then the model itself, then boxes round the model's connected parts - and
/// the build says which. A <c>collision: true</c> request still gets what it always did while a LOD fits as exported.
/// </summary>
public class ModelObjectCollisionFallbackTests
{
    /// <summary>An <paramref name="n"/> x <paramref name="n"/> grid of quads, 1 m apart, lying at <paramref name="at"/>
    /// with a gentle wave in it. <paramref name="soup"/>: every triangle gets three vertices of its own, the way an
    /// exporter splits a vertex per UV and normal - same shape, three times the vertices.</summary>
    internal static ObjMesh Grid(int n, Vec3 at, string material = "ground", bool soup = false)
    {
        var s = new ObjSubMesh { Material = material };
        Vec3 P(int x, int z) => new(at.X + x, at.Y + MathF.Sin(x * 0.4f) * MathF.Cos(z * 0.4f) * 0.5f, at.Z + z);
        void Add(Vec3 p) { s.Positions.Add(p); s.Normals.Add(new Vec3(0, 1, 0)); s.Uvs.Add((p.X, p.Z)); }
        if (soup)
        {
            for (int z = 0; z < n; z++)
                for (int x = 0; x < n; x++)
                    foreach (var (a, b, c) in new[] { ((x, z), (x + 1, z), (x + 1, z + 1)), ((x, z), (x + 1, z + 1), (x, z + 1)) })
                    {
                        int i = s.Positions.Count;
                        Add(P(a.Item1, a.Item2)); Add(P(b.Item1, b.Item2)); Add(P(c.Item1, c.Item2));
                        s.Faces.Add((i, i + 1, i + 2));
                    }
        }
        else
        {
            for (int z = 0; z <= n; z++) for (int x = 0; x <= n; x++) Add(P(x, z));
            int At(int x, int z) => z * (n + 1) + x;
            for (int z = 0; z < n; z++)
                for (int x = 0; x < n; x++)
                {
                    s.Faces.Add((At(x, z), At(x + 1, z), At(x + 1, z + 1)));
                    s.Faces.Add((At(x, z), At(x + 1, z + 1), At(x, z + 1)));
                }
        }
        var m = ObjMesh.FromSubMeshes(new[] { s });
        m.RecomputeBounds();
        return m;
    }

    /// <summary>Three separate dense grids: 43,923 vertices together, past the limit however they are welded, while
    /// each part on its own is small.</summary>
    private static ObjMesh ThreeParts()
    {
        var parts = new[] { Grid(120, new Vec3(0, 0, 0), "a"), Grid(120, new Vec3(200, 3, 0), "b"), Grid(120, new Vec3(0, 10, 300), "a") };
        var m = ObjMesh.FromSubMeshes(parts.SelectMany(p => p.SubMeshes));
        m.RecomputeBounds();
        return m;
    }

    private static ObjMesh Box(float[] b, string material = "box")
        => ComponentBoxes.Build(ObjMesh.FromSubMeshes(new[] { new ObjSubMesh
        {
            Material = material,
            Positions = { new Vec3(b[0], b[1], b[2]), new Vec3(b[3], b[4], b[5]), new Vec3(b[0], b[4], b[2]) },
            Normals = { new Vec3(0, 1, 0), new Vec3(0, 1, 0), new Vec3(0, 1, 0) },
            Uvs = { (0f, 0f), (0f, 0f), (0f, 0f) },
            Faces = { (0, 1, 2) },
        } }));

    private static StandardMesh Sm(ModelObject.Built b) => StandardMesh.Parse(b.Files.First(f => f.RelPath.EndsWith(".sm")).Bytes);
    private static string ObjectsCon(ModelObject.Built b) => Encoding.UTF8.GetString(b.Files.First(f => f.RelPath.EndsWith("Objects.con")).Bytes);

    private static (Vec3[] V, int[] T) Col(StandardMesh sm, int section)
    {
        Assert.True(StandardMesh.TryParseCollision(sm.CollisionSections[section], out var v, out var t));
        return (v, t);
    }

    [Fact]
    public void A_col02_past_the_limit_is_built_from_the_first_lower_lod_that_fits()
    {
        var lod0 = Grid(200, Vec3.Zero);                       // 40,401 vertices, shared already: welding cannot help
        var lod1 = Grid(20, Vec3.Zero);
        var b = ModelObject.BuildForMod("dense", lod0, extraLods: new[] { lod1 },
            collisionMeshes: new[] { Box(lod0.BoundingBox), Grid(200, Vec3.Zero) });

        Assert.Equal(2, b.CollisionCount);
        var sm = Sm(b);
        Assert.Equal(2, sm.CollisionSections.Count);
        Assert.Equal(lod1.TotalVertices, Col(sm, 1).V.Length);

        var src = b.CollisionSources[1];
        Assert.Equal((2, ModelObject.CollisionOrigin.Lod, 1), (src.Section, src.Origin, src.Lod));
        Assert.True(src.Substitute);
        Assert.Equal(ModelObject.CollisionOrigin.Given, b.CollisionSources[0].Origin);
        Assert.False(b.CollisionSources[0].Substitute);

        var con = ObjectsCon(b);
        Assert.DoesNotContain("left out", con);
        Assert.Contains("rem COL02 has 40401 vertices", con);
        Assert.Contains("LOD 1", con);
        Assert.Contains("HasCollisionPhysics 1", con);
    }

    /// <summary>A dense model as an exporter writes it: 24,200 triangles with three vertices each (72,600, past the
    /// limit), 12,321 once welded - so only welding lets the model itself carry the collision.</summary>
    private static ObjMesh DenseSoup() => Grid(110, Vec3.Zero, soup: true);

    [Fact]
    public void Requested_collision_still_takes_the_first_lod_that_fits_as_exported()
    {
        // What collision: true always wrote (the Viewer's level-local importer asks for it): the model past the limit,
        // so LOD 1. Welding would let the model in, but at 24,200 faces - the stand-in for a light LOD 1 is not a
        // collision twenty-five times heavier.
        var lod1 = Grid(30, Vec3.Zero);
        var b = ModelObject.BuildForMod("dense", DenseSoup(), extraLods: new[] { lod1 }, collision: true);

        Assert.Equal(lod1.TotalVertices, Col(Sm(b), 0).V.Length);
        var src = b.CollisionSources.Single();
        Assert.Equal((ModelObject.CollisionOrigin.Lod, 1, false, false), (src.Origin, src.Lod, src.Welded, src.Substitute));
        Assert.DoesNotContain("rem Collision", ObjectsCon(b));
    }

    [Fact]
    public void Requested_collision_with_no_lod_that_fits_as_exported_welds_a_lower_lod_before_the_model()
    {
        var lod1 = Grid(75, Vec3.Zero, soup: true);             // 33,750 vertices as written, 5,776 welded
        Assert.True(lod1.TotalVertices > 32767);
        var b = ModelObject.BuildForMod("dense", DenseSoup(), extraLods: new[] { lod1 }, collision: true);

        Assert.Equal(76 * 76, Col(Sm(b), 0).V.Length);
        var src = b.CollisionSources.Single();
        Assert.Equal((ModelObject.CollisionOrigin.Lod, 1, true, false), (src.Origin, src.Lod, src.Welded, src.Substitute));
        Assert.Contains("rem Collision was requested but every LOD is past the 32767-vertex collision limit as exported; it was built from LOD 1 with its corners welded", ObjectsCon(b));
    }

    [Fact]
    public void A_stand_in_for_a_given_col02_is_a_lower_lod_before_the_model_itself()
    {
        // COL02 past the limit however it is welded. The model welded would fit, but the stand-in is the lower LOD:
        // the model is the densest shape there is, so it is tried last, just before boxes.
        var lod1 = Grid(30, Vec3.Zero);
        var model = DenseSoup();
        var b = ModelObject.BuildForMod("dense", model, extraLods: new[] { lod1 },
            collisionMeshes: new[] { Box(model.BoundingBox), Grid(200, Vec3.Zero) });

        Assert.Equal(lod1.TotalVertices, Col(Sm(b), 1).V.Length);
        var src = b.CollisionSources[1];
        Assert.Equal((ModelObject.CollisionOrigin.Lod, 1, false, true), (src.Origin, src.Lod, src.Welded, src.Substitute));
        Assert.Contains("built from LOD 1", ObjectsCon(b));
    }

    [Fact]
    public void Welding_its_split_corners_is_tried_first_and_changes_nothing_of_the_shape()
    {
        // 20,000 triangles with three vertices each: 60,000 as written, 10,201 once the corners are shared.
        var col02 = Grid(100, Vec3.Zero, soup: true);
        Assert.Equal(60000, col02.TotalVertices);
        var b = ModelObject.BuildForMod("split", Grid(10, Vec3.Zero), collisionMeshes: new[] { Box(col02.BoundingBox), col02 });

        var (v, t) = Col(Sm(b), 1);
        Assert.Equal(101 * 101, v.Length);
        Assert.Equal(20000 * 3, t.Length);
        // Triangle for triangle, corner for corner, the shape that was given.
        var s = col02.SubMeshes[0];
        for (int i = 0; i < s.Faces.Count; i++)
        {
            Assert.Equal(s.Positions[s.Faces[i].A], v[t[i * 3]]);
            Assert.Equal(s.Positions[s.Faces[i].B], v[t[i * 3 + 1]]);
            Assert.Equal(s.Positions[s.Faces[i].C], v[t[i * 3 + 2]]);
        }
        var src = b.CollisionSources[1];
        Assert.Equal(ModelObject.CollisionOrigin.Given, src.Origin);
        Assert.True(src.Welded);
        Assert.False(src.Substitute);
        Assert.DoesNotContain("rem COL02", ObjectsCon(b));
    }

    [Fact]
    public void With_no_lod_that_fits_col02_is_boxes_round_the_parts()
    {
        var model = ThreeParts();
        Assert.True(model.TotalVertices > 32767);
        var b = ModelObject.BuildForMod("parts", model, collisionMeshes: new[] { Box(model.BoundingBox), ThreeParts() },
                                        faceMaterial: new Dictionary<string, int> { ["a"] = 81, ["b"] = 88 });

        Assert.Equal(2, b.CollisionCount);
        var sm = Sm(b);
        var (v, t) = Col(sm, 1);
        Assert.Equal(3 * 8, v.Length);
        Assert.Equal(3 * 12 * 3, t.Length);
        // Each box keeps its part's collision material: two parts of "a" (81), one of "b" (88).
        Assert.True(StandardMesh.TryParseCollisionFull(sm.CollisionSections[1], out var col));
        var mats = Enumerable.Range(0, col.Tris.Length / 4).Select(i => (int)col.Tris[i * 4 + 3]).ToList();
        Assert.Equal(24, mats.Count(m => m == 81));
        Assert.Equal(12, mats.Count(m => m == 88));

        AssertBoxesOutwardAndCovering(v, t, model);

        var src = b.CollisionSources[1];
        Assert.Equal(ModelObject.CollisionOrigin.Boxes, src.Origin);
        Assert.Equal(3, src.Boxes);
        Assert.True(src.Substitute);
        Assert.Contains("3 boxes", ObjectsCon(b));
        Assert.DoesNotContain("left out", ObjectsCon(b));
    }

    [Fact]
    public void Requested_collision_on_a_mesh_no_lod_can_carry_is_still_solid()
    {
        var b = ModelObject.BuildForMod("parts", ThreeParts(), collision: true);
        Assert.True(b.HasCollision);
        Assert.Equal(1, b.CollisionCount);
        Assert.Equal(ModelObject.CollisionOrigin.Boxes, b.CollisionSources.Single().Origin);
        Assert.Equal(24, Col(Sm(b), 0).V.Length);
        Assert.Contains("HasCollisionPhysics 1", ObjectsCon(b));
        Assert.DoesNotContain("decimate it", ObjectsCon(b));
    }

    [Fact]
    public void A_col01_that_does_not_fit_keeps_col02_in_the_second_slot()
    {
        // Leaving COL01 out used to move COL02 up into the first slot, where it does not block movement.
        var col02 = Grid(8, Vec3.Zero);
        var b = ModelObject.BuildForMod("slots", Grid(4, Vec3.Zero), collisionMeshes: new[] { ThreeParts(), col02 });
        Assert.Equal(2, b.CollisionCount);
        var sm = Sm(b);
        Assert.Equal(25, Col(sm, 0).V.Length);                  // COL01 from the model itself, which fits
        Assert.Equal(col02.TotalVertices, Col(sm, 1).V.Length);
        Assert.Equal(new[] { 1, 2 }, b.CollisionSources.Select(s => s.Section));
        Assert.Equal((ModelObject.CollisionOrigin.Lod, 0), (b.CollisionSources[0].Origin, b.CollisionSources[0].Lod));
        Assert.Equal(ModelObject.CollisionOrigin.Given, b.CollisionSources[1].Origin);
    }

    [Fact]
    public void Collision_that_fits_is_written_as_before_and_reported_as_given()
    {
        var model = Grid(10, Vec3.Zero);
        var withMeshes = ModelObject.BuildForMod("fits", model, collisionMeshes: new[] { Box(model.BoundingBox), Grid(10, Vec3.Zero) });
        Assert.All(withMeshes.CollisionSources, s => Assert.Equal((ModelObject.CollisionOrigin.Given, false, false), (s.Origin, s.Welded, s.Substitute)));

        var generated = ModelObject.BuildForMod("fits", Grid(10, Vec3.Zero), collision: true);
        var g = generated.CollisionSources.Single();
        Assert.Equal((1, ModelObject.CollisionOrigin.Lod, 0, false), (g.Section, g.Origin, g.Lod, g.Substitute));
        Assert.Equal(model.TotalVertices, Col(Sm(generated), 0).V.Length);

        var none = ModelObject.BuildForMod("soft", Grid(10, Vec3.Zero));
        Assert.Empty(none.CollisionSources);
        Assert.Equal(0, none.CollisionCount);
    }

    [Fact]
    public void Boxes_are_capped_by_clustering_the_parts_and_still_cover_every_vertex()
    {
        // 2,000 loose triangles scattered over a 100 m square: one box each would be 16,000 vertices.
        var s = new ObjSubMesh { Material = "debris" };
        var rng = new Random(7);
        for (int i = 0; i < 2000; i++)
        {
            var c = new Vec3((float)rng.NextDouble() * 100, (float)rng.NextDouble() * 5, (float)rng.NextDouble() * 100);
            int k = s.Positions.Count;
            foreach (var p in new[] { c, new Vec3(c.X + 0.3f, c.Y, c.Z), new Vec3(c.X, c.Y + 0.2f, c.Z + 0.3f) })
            { s.Positions.Add(p); s.Normals.Add(new Vec3(0, 1, 0)); s.Uvs.Add((0f, 0f)); }
            s.Faces.Add((k, k + 1, k + 2));
        }
        var debris = ObjMesh.FromSubMeshes(new[] { s });
        var boxes = ComponentBoxes.Build(debris, maxBoxes: 64);
        Assert.InRange(boxes.TotalVertices / 8, 1, 64);
        Assert.Equal(boxes.TotalVertices / 8 * 12, boxes.TotalFaces);
        var verts = boxes.SubMeshes.SelectMany(m => m.Positions).ToArray();
        var tris = new List<int>();
        int bse = 0;
        foreach (var m in boxes.SubMeshes) { foreach (var (a, b, c) in m.Faces) tris.AddRange(new[] { bse + a, bse + b, bse + c }); bse += m.Positions.Count; }
        AssertBoxesOutwardAndCovering(verts, tris.ToArray(), debris);
    }

    [Fact]
    public void A_box_contained_in_another_adds_nothing_and_is_left_out()
    {
        var hull = Grid(4, Vec3.Zero);
        var bolt = Grid(1, new Vec3(1, 0, 1));                 // lies inside the hull's box
        var bolted = ObjMesh.FromSubMeshes(hull.SubMeshes.Concat(bolt.SubMeshes));
        Assert.Equal(8, ComponentBoxes.Build(bolted).TotalVertices);
        // A flat part still gets a box with depth: every side at least the minimum.
        var flat = ComponentBoxes.Build(ObjMesh.FromSubMeshes(new[] { new ObjSubMesh
        {
            Positions = { new Vec3(0, 2, 0), new Vec3(4, 2, 0), new Vec3(4, 2, 4) },
            Normals = { new Vec3(0, 1, 0), new Vec3(0, 1, 0), new Vec3(0, 1, 0) },
            Uvs = { (0f, 0f), (0f, 0f), (0f, 0f) },
            Faces = { (0, 1, 2) },
        } }));
        flat.RecomputeBounds();
        Assert.Equal(ComponentBoxes.MinimumSide, flat.BoundingBox[4] - flat.BoundingBox[1], 4);
        Assert.Equal(2f, (flat.BoundingBox[4] + flat.BoundingBox[1]) / 2, 4);
    }

    /// <summary>Every 8 consecutive vertices are one box; every triangle's engine normal (the LEFT-hand one,
    /// -(b-a)x(c-a): the engine reads a face clockwise from outside) points out of its box; and every vertex of
    /// <paramref name="source"/> lies inside some box.</summary>
    private static void AssertBoxesOutwardAndCovering(Vec3[] v, int[] t, ObjMesh source)
    {
        Assert.Equal(0, v.Length % 8);
        var boxes = Enumerable.Range(0, v.Length / 8).Select(i => v.Skip(i * 8).Take(8).ToArray()).ToArray();
        for (int i = 0; i < t.Length; i += 3)
        {
            int box = t[i] / 8;
            Assert.True(t[i + 1] / 8 == box && t[i + 2] / 8 == box, "a triangle spans two boxes");
            var c = Centre(boxes[box]);
            Vec3 a = v[t[i]], b = v[t[i + 1]], d = v[t[i + 2]];
            float ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z, wx = d.X - a.X, wy = d.Y - a.Y, wz = d.Z - a.Z;
            var n = new Vec3(-(uy * wz - uz * wy), -(uz * wx - ux * wz), -(ux * wy - uy * wx));
            var f = new Vec3((a.X + b.X + d.X) / 3 - c.X, (a.Y + b.Y + d.Y) / 3 - c.Y, (a.Z + b.Z + d.Z) / 3 - c.Z);
            Assert.True(n.X * f.X + n.Y * f.Y + n.Z * f.Z > 0f, $"triangle {i / 3} faces into its box");
        }
        var bounds = boxes.Select(bx => (Min: new Vec3(bx.Min(p => p.X), bx.Min(p => p.Y), bx.Min(p => p.Z)),
                                         Max: new Vec3(bx.Max(p => p.X), bx.Max(p => p.Y), bx.Max(p => p.Z)))).ToArray();
        foreach (var p in source.SubMeshes.SelectMany(m => m.Positions))
            Assert.Contains(bounds, bb => p.X >= bb.Min.X && p.X <= bb.Max.X && p.Y >= bb.Min.Y && p.Y <= bb.Max.Y && p.Z >= bb.Min.Z && p.Z <= bb.Max.Z);
    }

    private static Vec3 Centre(Vec3[] pts) => new(pts.Average(p => p.X), pts.Average(p => p.Y), pts.Average(p => p.Z));
}
