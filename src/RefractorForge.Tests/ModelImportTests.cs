using System.Text;
using RefractorForge.Formats;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Mesh;
using RefractorForge.Formats.Rfa;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The 3D-model import path: source mesh -> fitted -> <c>.sm</c> + <c>.rs</c> + textures -> a level-local object.
///
/// The assertions worth understanding are the <c>.rs</c> ones. Our reader is deliberately lenient — it will parse a
/// shader with no semicolons and a bare texture name quite happily — so a writer/reader round-trip proves nothing
/// about whether the engine would accept the file. These tests check the grammar directly instead, because that is
/// the class of bug that made every generated shader inert while every test passed.
/// </summary>
public class ModelImportTests
{
    static bool Near(float a, float b, float eps = 1e-4f) => MathF.Abs(a - b) < eps;

    /// <summary>A unit cube, 2 m on a side, centred on the origin, with a UV per corner.</summary>
    static ObjMesh Cube(string material = "wood", float half = 1f)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 8; i++)
            sb.Append($"v {((i & 1) != 0 ? half : -half)} {((i & 2) != 0 ? half : -half)} {((i & 4) != 0 ? half : -half)}\n");
        sb.Append("vt 0 0\nvt 1 0\nvt 1 1\nvt 0 1\n");
        sb.Append($"usemtl {material}\n");
        int[,] faces = { { 0, 1, 3, 2 }, { 4, 6, 7, 5 }, { 0, 4, 5, 1 }, { 2, 3, 7, 6 }, { 0, 2, 6, 4 }, { 1, 5, 7, 3 } };
        for (int f = 0; f < 6; f++)
            sb.Append($"f {faces[f, 0] + 1}/1 {faces[f, 1] + 1}/2 {faces[f, 2] + 1}/3 {faces[f, 3] + 1}/4\n");
        return ObjMesh.Parse(sb.ToString());
    }

    [Fact]
    public void Rs_writer_emits_the_grammar_the_engine_requires()
    {
        var rs = RsWriter.Write(new[] {
            new RsWriter.Material("Prop_Material0", "oak", new Vec3(0.6f, 0.4f, 0.2f)),
            new RsWriter.Material("Prop_Material1", "leaf.png", new Vec3(1, 1, 1), AlphaTestRef: 0.5f, TwoSided: true),
            new RsWriter.Material("Prop_Material2", null, new Vec3(1, 1, 1), Transparent: true, DepthWrite: false),
        });

        // Rule 1: EVERY statement ends in ';'. A single miss and the engine's parser throws and drops the subshader.
        foreach (var line in rs.Split('\n').Select(l => l.Trim())
                              .Where(l => l.Length > 0 && l != "{" && l != "}" && !l.StartsWith("subshader")))
            Assert.EndsWith(";", line);

        // Rule 2: the texture is folder-qualified, and its extension is dropped (the engine appends its own).
        Assert.Contains("texture \"texture/oak\";", rs);
        Assert.Contains("texture \"texture/leaf\";", rs);

        // An opaque material must NOT carry an alphaTestRef: its presence beside `transparent` is exactly what
        // means "cut out, don't blend", so writing one on a solid surface mislabels it for engine and editor alike.
        var blocks = rs.Split("subshader ", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, blocks.Length);
        Assert.DoesNotContain("alphaTestRef", blocks[0]);
        Assert.Contains("alphaTestRef 0.5;", blocks[1]);
        Assert.Contains("twosided true;", blocks[1]);
        Assert.Contains("twosided false;", blocks[0]);
        Assert.Contains("transparent true;", blocks[2]);
        Assert.Contains("depthWrite false;", blocks[2]);       // blended glass must not stamp depth
        Assert.DoesNotContain("texture", blocks[2]);           // untextured material writes no binding

        // A caller-supplied path (a mod folder, a Bink movie) is written verbatim, not re-qualified.
        Assert.Contains("texture \"Mods/bfv/Movies/screen.bik\";",
            RsWriter.Write(new[] { new RsWriter.Material("m", "Mods/bfv/Movies/screen.bik", new Vec3(1, 1, 1)) }));

        Assert.Equal("texture/oak", RsWriter.QualifyTexture("oak"));
        Assert.Equal("texture/oak", RsWriter.QualifyTexture("oak.dds"));
        Assert.Equal("mymod/oak", RsWriter.QualifyTexture("oak", "mymod/"));
        Assert.Equal("a/b/oak", RsWriter.QualifyTexture("a\\b\\oak"));   // an authored Windows path is still a path
    }

    [Fact]
    public void Fit_puts_a_z_up_model_upright_at_the_right_size_on_the_ground()
    {
        // 2 m cube, Z-up, sitting off in space: the shape a Blender FBX arrives in.
        var mesh = Cube();
        mesh.Transform(1f, new Vec3(10, 20, 30));

        var r = MeshFit.Apply(mesh, new MeshFitOptions {
            Up = UpAxis.Z, Fit = FitMode.Height, TargetMeters = 6f, Origin = OriginMode.Base });

        Assert.True(Near(r.Scale, 3f), $"2 m tall -> 6 m is a 3x scale (got {r.Scale})");
        Assert.True(Near(mesh.BoundingBox[1], 0f), $"lowest point sits on the ground (got {mesh.BoundingBox[1]})");
        Assert.True(Near(mesh.BoundingBox[0], -3f) && Near(mesh.BoundingBox[3], 3f), "centred horizontally");
        Assert.True(Near(mesh.BoundingBox[4], 6f), "6 m tall");

        // The axis swap is a proper rotation, so normals come with it and stay unit length.
        foreach (var n in mesh.SubMeshes[0].Normals)
            Assert.True(Near(MathF.Sqrt(n.X * n.X + n.Y * n.Y + n.Z * n.Z), 1f), "normals stay unit length");

        // Z-up (x,y,z) -> Y-up (x,z,-y): the source's +Y (its "forward") becomes -Z, Refractor's forward.
        var probe = ObjMesh.Parse("v 0 1 0\nv 1 1 0\nv 0 1 1\nf 1 2 3\n");
        MeshFit.Apply(probe, new MeshFitOptions { Up = UpAxis.Z, Origin = OriginMode.Keep });
        Assert.True(Near(probe.SubMeshes[0].Positions[0].Z, -1f) && Near(probe.SubMeshes[0].Positions[0].Y, 0f),
                    "+Y in the source becomes -Z");

        // LongestSide sizes a vehicle by its length; Center is for something that hangs or spins.
        var car = Cube(half: 0.5f);
        car.Transform(1f, new Vec3(0, 0, 0));
        car.SubMeshes[0].Positions[0] = new Vec3(-4f, -0.5f, -0.5f);   // stretch it along X
        var r2 = MeshFit.Apply(car, new MeshFitOptions { Fit = FitMode.LongestSide, TargetMeters = 10f, Origin = OriginMode.Center });
        Assert.True(Near(car.BoundingBox[3] - car.BoundingBox[0], 10f), "longest side becomes 10 m");
        Assert.True(Near((car.BoundingBox[1] + car.BoundingBox[4]) * 0.5f, 0f), "centred vertically");
        Assert.True(r2.Scale > 1f, "reports the scale it applied");
    }

    /// <summary>A displaced N x N grid: one material, one continuous UV map, a pinned border and a large
    /// collapsible interior — the shape a decimator should be able to hit its target on.</summary>
    static ObjMesh Grid(int n, string material = "ground")
    {
        var sb = new StringBuilder();
        for (int y = 0; y <= n; y++)
            for (int x = 0; x <= n; x++)
                sb.Append($"v {x} {MathF.Sin(x * 0.4f) * MathF.Cos(y * 0.4f) * 2f} {y}\n");
        for (int y = 0; y <= n; y++)
            for (int x = 0; x <= n; x++)
                sb.Append($"vt {(float)x / n} {(float)y / n}\n");
        sb.Append($"usemtl {material}\n");
        int At(int x, int y) => y * (n + 1) + x + 1;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                sb.Append($"f {At(x, y)}/{At(x, y)} {At(x + 1, y)}/{At(x + 1, y)} {At(x + 1, y + 1)}/{At(x + 1, y + 1)}\n");
                sb.Append($"f {At(x, y)}/{At(x, y)} {At(x + 1, y + 1)}/{At(x + 1, y + 1)} {At(x, y + 1)}/{At(x, y + 1)}\n");
            }
        return ObjMesh.Parse(sb.ToString());
    }

    static void AssertWellFormed(ObjMesh m, string what)
    {
        foreach (var s in m.SubMeshes)
        {
            Assert.True(s.Positions.Count == s.Normals.Count && s.Positions.Count == s.Uvs.Count,
                        what + ": parallel vertex arrays stay the same length");
            foreach (var (a, b, c) in s.Faces)
            {
                Assert.True(a >= 0 && b >= 0 && c >= 0 && a < s.Positions.Count && b < s.Positions.Count && c < s.Positions.Count,
                            what + ": every index is in range");
                Assert.True(a != b && b != c && a != c, what + ": no degenerate triangle");
            }
            foreach (var p in s.Positions)
                Assert.True(!float.IsNaN(p.X) && !float.IsNaN(p.Y) && !float.IsNaN(p.Z), what + ": no NaN positions");
        }
    }

    [Fact]
    public void Decimation_hits_its_target_and_keeps_the_silhouette()
    {
        var src = Grid(40);                                   // 3,200 triangles
        Assert.Equal(3200, src.TotalFaces);
        var box = (float[])src.BoundingBox.Clone();

        var lod = MeshDecimator.Decimate(src, 800, out var r);
        AssertWellFormed(lod, "decimated grid");
        Assert.Equal(3200, src.TotalFaces);                   // the source is not touched
        Assert.True(r.Triangles <= 900 && r.Triangles >= 600,
                    $"asked for 800 triangles, got {r.Triangles} (from {r.SourceTriangles})");
        Assert.True(r.Collapses > 0, "it actually collapsed something");

        // The silhouette is what a distant LOD is for, so the box must barely move. Half-edge collapses only ever
        // remove vertices, so the box can shrink a little and can never grow.
        for (int i = 0; i < 3; i++)
            Assert.True(lod.BoundingBox[i] >= box[i] - 1e-3f && lod.BoundingBox[i + 3] <= box[i + 3] + 1e-3f,
                        "the decimated box stays inside the original");
        Assert.True(MathF.Abs(lod.BoundingBox[3] - box[3]) < 0.5f && MathF.Abs(lod.BoundingBox[0] - box[0]) < 0.5f,
                    "the footprint is essentially unchanged");

        // Every surviving vertex is one the author placed - nothing is invented, so UVs and positions stay exact.
        var srcPositions = src.SubMeshes[0].Positions.Select(p => (p.X, p.Y, p.Z)).ToHashSet();
        foreach (var p in lod.SubMeshes[0].Positions)
            Assert.Contains((p.X, p.Y, p.Z), srcPositions);

        // And it still writes as a mesh.
        var sm = StandardMesh.Parse(StandardMeshWriter.Write(lod));
        Assert.Equal(r.Triangles, sm.Lods[0].Sum(m => m.Faces.Length));
        Assert.True(sm.Total - sm.Consumed == 8, "the decimated mesh accounts for every byte");

        // A target at or above the source is a no-op rather than a rebuild.
        var same = MeshDecimator.Decimate(src, 99999, out var r2);
        Assert.Equal(src.TotalFaces, r2.Triangles);
        Assert.Equal(0, r2.Collapses);
    }

    [Fact]
    public void Decimation_pins_seams_and_material_boundaries()
    {
        // Two materials meeting along a shared edge: collapsing across that join opens a gap between them.
        var a = Grid(12, "left");
        var b = Grid(12, "right");
        foreach (var s in b.SubMeshes) { s.Material = "right"; for (int i = 0; i < s.Positions.Count; i++) s.Positions[i] = new Vec3(s.Positions[i].X + 12f, s.Positions[i].Y, s.Positions[i].Z); }
        var joined = new ObjMesh();
        joined.SubMeshes.Add(a.SubMeshes[0]);
        joined.SubMeshes.Add(b.SubMeshes[0]);

        var lod = MeshDecimator.Decimate(joined, 100, out var r);
        AssertWellFormed(lod, "two-material decimation");
        Assert.Equal(2, lod.SubMeshes.Count);                              // neither material reduced to nothing
        Assert.All(lod.SubMeshes, s => Assert.True(s.Faces.Count >= 4, "each material keeps geometry"));
        Assert.True(r.Triangles < joined.TotalFaces, "it did simplify");

        // The seam between the two materials is at x = 12; every vertex the two share must survive in both, or the
        // materials pull apart. Compare the sets of positions along that line.
        var leftSeam = lod.SubMeshes[0].Positions.Where(p => MathF.Abs(p.X - 12f) < 1e-4f).Select(p => MathF.Round(p.Z, 3)).ToHashSet();
        var rightSeam = lod.SubMeshes[1].Positions.Where(p => MathF.Abs(p.X - 12f) < 1e-4f).Select(p => MathF.Round(p.Z, 3)).ToHashSet();
        Assert.True(leftSeam.Count > 0 && leftSeam.SetEquals(rightSeam),
                    $"the shared edge matches on both sides ({leftSeam.Count} vs {rightSeam.Count} vertices)");

        // A closed box whose faces meet at UV seams: nothing should tear, and the box must stay a box.
        var cube = Cube(half: 2f);
        var small = MeshDecimator.Decimate(cube, 4, out _);
        AssertWellFormed(small, "cube decimation");
        Assert.True(MathF.Abs(small.BoundingBox[3] - 2f) < 1e-3f, "the cube keeps its extent");
    }

    [Fact]
    public void A_mesh_can_carry_several_lods()
    {
        var l0 = Grid(30);
        var l1 = MeshDecimator.Decimate(l0, 400);
        var l2 = MeshDecimator.Decimate(l0, 100);

        var sm = StandardMesh.Parse(StandardMeshWriter.Write(new[] { l0, l1, l2 }));
        Assert.Equal(3, sm.NumLods);
        Assert.Equal(l0.TotalFaces, sm.Lods[0].Sum(m => m.Faces.Length));
        Assert.Equal(l1.TotalFaces, sm.Lods[1].Sum(m => m.Faces.Length));
        Assert.Equal(l2.TotalFaces, sm.Lods[2].Sum(m => m.Faces.Length));
        Assert.True(sm.Lods[2].Sum(m => m.Faces.Length) < sm.Lods[0].Sum(m => m.Faces.Length), "they do get coarser");
        Assert.True(sm.Total - sm.Consumed == 8, "a multi-LOD mesh accounts for every byte");

        // The box is LOD 0's: a coarser copy's own box is slightly smaller, and using it would cull the whole
        // object early.
        Assert.True(Near(sm.BoundingBox[3], l0.BoundingBox[3], 1e-3f), "the bounding box is LOD 0's");
    }

    [Fact]
    public void Oversized_sections_are_split_rather_than_refused()
    {
        // One material with more vertices than a .sm section's u16 indices can address. The writer used to throw.
        const int tris = 30000;                       // 3 unshared verts each = 90,000
        var sb = new StringBuilder("usemtl dense\n");
        for (int i = 0; i < tris; i++)
            sb.Append($"v {i} 0 0\nv {i} 1 0\nv {i} 0 1\n").Append($"f {i * 3 + 1} {i * 3 + 2} {i * 3 + 3}\n");
        var mesh = ObjMesh.Parse(sb.ToString());
        Assert.Single(mesh.SubMeshes);
        Assert.Equal(tris * 3, mesh.TotalVertices);

        int splits = MeshFit.SplitOversizedSections(mesh);
        Assert.True(splits >= 1, "the oversized section was split");
        Assert.All(mesh.SubMeshes, s => Assert.True(s.Positions.Count <= 65535, "every piece is inside the u16 limit"));
        Assert.Equal(tris, mesh.TotalFaces);                                      // no triangle lost
        Assert.All(mesh.SubMeshes, s => Assert.Equal("dense", s.Material));       // the pieces still share one shader

        var sm = StandardMesh.Parse(StandardMeshWriter.Write(mesh));
        Assert.Equal(mesh.SubMeshes.Count, sm.Lods[0].Count);
        Assert.Equal(tris, sm.Lods[0].Sum(m => m.Faces.Length));
        Assert.True(sm.Total - sm.Consumed == 8, "the split mesh still accounts for every byte");
    }

    [Fact]
    public void ModelObject_builds_a_level_local_object_that_reads_back()
    {
        // Two materials, so the <Mesh>_MaterialN renaming and the per-material shader both get exercised.
        var mesh = ObjMesh.Parse(
            "v 0 0 0\nv 1 0 0\nv 0 1 0\nv 2 0 0\nv 3 0 0\nv 2 1 0\nvt 0 0\nvt 1 0\nvt 1 1\n" +
            "usemtl hull\nf 1/1 2/2 3/3\nusemtl glass\nf 4/1 5/2 6/3\n");
        var dds = new byte[128]; dds[0] = (byte)'D';

        var b = ModelObject.Build("Test_Map", "my prop!", mesh,
            materials: new[] {
                new ModelObject.Material("hull", "prop_hull", new Vec3(1, 1, 1)),
                new ModelObject.Material("glass", null, new Vec3(0.5f, 0.6f, 0.9f), Transparent: true),
            },
            textures: new[] { new ModelObject.Texture("prop_hull", dds) },
            collision: true, baseSub: "BfVietnam");

        Assert.Equal("my_prop", b.Template);                       // sanitized the way a template name must be
        Assert.Equal("run my_prop/my_prop", b.RunLine);
        Assert.True(b.HasCollision);

        string Text(string p) => Encoding.UTF8.GetString(b.Files.First(f => f.RelPath == p).Bytes);
        byte[] Bytes(string p) => b.Files.First(f => f.RelPath == p).Bytes;

        // The six files of the recipe, and nothing loose.
        foreach (var p in new[] { "StandardMesh/my_prop.sm", "StandardMesh/my_prop.rs", "Texture/prop_hull.dds",
                                  "Objects/my_prop/Geometries.con", "Objects/my_prop/Objects.con",
                                  "Objects/my_prop/my_prop.con" })
            Assert.Contains(b.Files, f => f.RelPath == p);
        Assert.Equal(6, b.Files.Count);

        // Materials renamed to the global-registry-safe convention. "hull" and "glass" would collide with any mod's.
        Assert.Equal(new[] { "my_prop_Material0", "my_prop_Material1" }, b.MaterialNames.ToArray());
        var rs = Text("StandardMesh/my_prop.rs");
        Assert.Contains("subshader \"my_prop_Material0\" \"StandardMesh/Default\"", rs);
        Assert.Contains("texture \"texture/prop_hull\";", rs);
        Assert.Contains("transparent true;", rs);
        foreach (var line in rs.Split('\n').Select(l => l.Trim())
                              .Where(l => l.Length > 0 && l != "{" && l != "}" && !l.StartsWith("subshader")))
            Assert.EndsWith(";", line);

        // The .sm reads back with its geometry, its renamed materials and its collision intact.
        var sm = StandardMesh.Parse(Bytes("StandardMesh/my_prop.sm"));
        Assert.True(sm.Version == 10 && sm.Lods[0].Count == 2, "two material sections");
        Assert.Equal("my_prop_Material0", sm.Lods[0][0].Name);
        Assert.Equal("my_prop_Material1", sm.Lods[0][1].Name);
        Assert.True(Near(sm.Lods[0][0].Uvs[2].U, 1f) && Near(sm.Lods[0][0].Uvs[2].V, 1f), "uvs survive the trip");
        Assert.True(sm.Total - sm.Consumed == 8, "the reader accounts for every byte but the trailing section");
        Assert.Equal(1, sm.NumCollisionMeshes);
        Assert.True(StandardMesh.TryParseCollision(sm.CollisionSections[0], out _, out var ci) && ci.Length == 6,
                    "collision covers both triangles");

        // The .con files point where the engine will look. A BF1942 path resolves to nothing in Vietnam.
        Assert.Contains("GeometryTemplate.file ../BfVietnam/levels/Test_Map/StandardMesh/my_prop",
                        Text("Objects/my_prop/Geometries.con"));
        Assert.Contains("GeometryTemplate.setLodDistance 5 1000", Text("Objects/my_prop/Geometries.con"));
        var obj = Text("Objects/my_prop/Objects.con");
        Assert.Contains("ObjectTemplate.create SimpleObject my_prop", obj);
        Assert.Contains("ObjectTemplate.geometry my_prop", obj);
        Assert.Contains("ObjectTemplate.HasCollisionPhysics 1", obj);
        Assert.Equal("run Objects\r\nrun Geometries\r\n", Text("Objects/my_prop/my_prop.con"));

        // The level-side patches are shared with the decal path and stay idempotent.
        var oc = DecalObject.PatchObjectsCon(null, b.RunLine);
        Assert.Equal(oc, DecalObject.PatchObjectsCon(oc, b.RunLine));
        var init = DecalObject.PatchInitCon("run Init/Terrain\r\n", "Test_Map", "BfVietnam");
        Assert.Contains("textureManager.alternativePath BfVietnam/levels/Test_Map/Texture", init);

        // Without collision the flag goes to 0 rather than promising solidity the .sm cannot deliver.
        var soft = ModelObject.Build("Test_Map", "soft", ObjMesh.Parse("v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n"));
        Assert.False(soft.HasCollision);
        Assert.Contains("ObjectTemplate.HasCollisionPhysics 0",
                        Encoding.UTF8.GetString(soft.Files.First(f => f.RelPath == "Objects/soft/Objects.con").Bytes));
    }

    /// <summary>
    /// What Ctrl+S does for a <c>.rfa</c> level, with a real model in it: the archive the user opened is rewritten
    /// in place with the object's files added under the level's prefix. The <c>.sm</c> is the part worth proving
    /// separately from the decal's — it is large, binary and LZO-compressed on the way in, so "it round-trips as
    /// bytes" and "it still parses as a mesh after the archive had it" are two different claims.
    /// </summary>
    [Fact]
    public void A_saved_archive_carries_the_model_back_out_intact()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rfmodel_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            const string prefix = "bf1942/levels/Test_Map/";
            string path = Path.Combine(dir, "Test_Map.rfa");
            const string initText = "renderer.fogstart 50\r\nrun Init/Terrain\r\n";
            RefractorFlatArchive.WriteFile(path, new List<(string, byte[])>
            {
                (prefix + "Init.con", Encoding.Latin1.GetBytes(initText)),
                (prefix + "StaticObjects.con", Encoding.Latin1.GetBytes("rem empty\r\n")),
            }, compress: true, xPackId: XPackId.Default);

            var mesh = Cube();
            MeshFit.Apply(mesh, new MeshFitOptions { Up = UpAxis.Z, Fit = FitMode.Height, TargetMeters = 3f });
            var built = ModelObject.Build("Test_Map", "crate", mesh,
                new[] { new ModelObject.Material("wood", "crate_wood", new Vec3(1, 1, 1)) },
                new[] { new ModelObject.Texture("crate_wood", new byte[128 + 64 * 4]) },
                collision: true);

            var newEntries = new List<(string RelPath, byte[] Bytes)>(built.Files)
            {
                ("Objects/objects.con", Encoding.Latin1.GetBytes(DecalObject.PatchObjectsCon(null, built.RunLine))),
                ("Init.con", Encoding.Latin1.GetBytes(DecalObject.PatchInitCon(initText, "Test_Map"))),
            };
            LevelSaver.RepackToRfa(path, path, null, null, null, null, newEntries: newEntries);
            Assert.Null(RefractorFlatArchive.Validate(path));

            var after = new RefractorFlatArchive(path);
            var names = after.Entries.Select(e => e.Name.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in built.Files) Assert.Contains(prefix + f.RelPath, names);

            // Out of the archive and straight back through the mesh reader: same geometry, same materials, same
            // collision, and every byte accounted for.
            var smBytes = after.Read(after.Entries.First(e => e.Name.EndsWith("crate.sm", StringComparison.OrdinalIgnoreCase)));
            Assert.Equal(built.Files.First(f => f.RelPath == "StandardMesh/crate.sm").Bytes, smBytes);
            var sm = StandardMesh.Parse(smBytes);
            Assert.Equal("crate_Material0", sm.Lods[0][0].Name);
            Assert.Equal(12, sm.Lods[0].Sum(m => m.Faces.Length));            // a cube is 6 quads = 12 triangles
            Assert.Equal(1, sm.NumCollisionMeshes);
            Assert.True(sm.Total - sm.Consumed == 8, "every byte accounted for after the round trip");
            Assert.True(Near(sm.BoundingBox[1], 0f, 1e-3f), "still sitting on the ground");
            Assert.True(Near(sm.BoundingBox[4], 3f, 1e-3f), "still 3 m tall");

            var savedInit = Encoding.Latin1.GetString(after.Read(after.Entries.First(e => e.Name.EndsWith("Test_Map/Init.con", StringComparison.OrdinalIgnoreCase))));
            Assert.Contains("run Objects/Objects", savedInit);
            Assert.Contains("textureManager.alternativePath bf1942/levels/Test_Map/Texture", savedInit);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
