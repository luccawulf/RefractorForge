using System.Security.Cryptography;
using System.Text;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Mesh;
using RefractorForge.Formats.Rfa;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>ModelObject writes an imported model as a level-local object (<see cref="ModelObject.Build"/>) or as a
/// MOD-level one (<see cref="ModelObject.BuildForMod"/>). The level form's bytes are pinned so the mod form could be
/// added without changing a single byte of what the editor already writes.</summary>
public class ModelObjectModLevelTests
{
    private const string TwoMaterialBox =
        "o Box\nv -1 0 -1\nv 1 0 -1\nv 1 2 -1\nv -1 2 -1\nv -1 0 1\nv 1 0 1\nv 1 2 1\nv -1 2 1\n" +
        "vt 0 0\nvt 1 0\nvt 1 1\nvt 0 1\n" +
        "usemtl wood\nf 1/1 2/2 3/3 4/4\nf 5/1 8/4 7/3 6/2\nf 1/1 5/2 6/3 2/4\n" +
        "usemtl metal\nf 4/1 3/2 7/3 8/4\nf 2/1 6/2 7/3 3/4\nf 1/1 4/2 8/3 5/4\n";

    private static ObjMesh Box() => ObjMesh.Parse(TwoMaterialBox);

    private static ModelObject.Built LevelBuild()
        => ModelObject.Build("Test_Map", "My Crate", Box(),
            new[] { new ModelObject.Material("wood", "crate_wood", new Vec3(1, 1, 1)), new ModelObject.Material("metal", null, new Vec3(0.5f, 0.5f, 0.5f)) },
            new[] { new ModelObject.Texture("crate_wood", new byte[] { 1, 2, 3, 4 }) },
            collision: true, baseSub: "BfVietnam", maxDrawDistance: 400f, extraLods: new[] { Box() }, shadow: Box());

    private static string Digest(IEnumerable<(string RelPath, byte[] Bytes)> files)
        => string.Join("\n", files.Select(f => f.RelPath + " " + Convert.ToHexString(SHA256.HashData(f.Bytes))[..16]));

    [Fact]
    public void Level_build_output_is_unchanged()
    {
        // Captured from the implementation before BuildForMod existed.
        Assert.Equal(LevelGolden, Digest(LevelBuild().Files));
    }

    [Fact]
    public void Mod_level_object_has_bare_file_and_no_run_chain()
    {
        var b = ModelObject.BuildForMod("mm_Crate", Box(), "Buildings/MyMod",
            new[] { new ModelObject.Material("wood", "mm_crate_wood", new Vec3(1, 1, 1)) },
            new[] { new ModelObject.Texture("mm_crate_wood", new byte[] { 9 }) }, collision: true);
        var paths = b.Files.Select(f => f.RelPath).ToList();
        Assert.Equal(new[]
        {
            "standardMesh/mm_Crate.sm", "standardMesh/mm_Crate.rs", "texture/mm_crate_wood.dds",
            "objects/Buildings/MyMod/mm_Crate/Geometries.con", "objects/Buildings/MyMod/mm_Crate/Objects.con",
        }, paths);
        Assert.DoesNotContain(paths, p => p.EndsWith("/mm_Crate.con"));                 // the engine auto-runs objects/
        Assert.Equal("", b.RunLine);
        var geo = Encoding.UTF8.GetString(b.Files.Single(f => f.RelPath.EndsWith("Geometries.con")).Bytes);
        Assert.Contains("GeometryTemplate.file mm_Crate\r\n", geo);                       // bare name, flat namespace
        Assert.DoesNotContain("levels/", geo);
        Assert.Contains("HasCollisionPhysics 1", Encoding.UTF8.GetString(b.Files.Single(f => f.RelPath.EndsWith("Objects.con")).Bytes));
        Assert.Throws<ArgumentException>(() => ModelObject.BuildForMod("x", Box(), "Stuff/ai/Things"));
    }

    [Fact]
    public void Collision_faces_take_the_material_of_their_source_material()
    {
        var b = ModelObject.BuildForMod("mm_Box", Box(), collision: true, collisionMaterial: 88,
            faceMaterial: new Dictionary<string, int> { ["wood"] = 81 });
        var sm = StandardMesh.Parse(b.Files.Single(f => f.RelPath.EndsWith(".sm")).Bytes);
        Assert.True(StandardMesh.TryParseCollisionFull(sm.CollisionSections[0], out var col));
        var faceMats = Enumerable.Range(0, col.TriangleCount).Select(t => (int)col.Tris[t * 4 + 3]).ToList();
        Assert.Equal(new[] { 81, 88 }, faceMats.Distinct().OrderBy(x => x).ToArray());
        // 3 quads of wood then 3 of metal -> 6 wood triangles first.
        Assert.All(faceMats.Take(6), m => Assert.Equal(81, m));
    }

    /// <summary>Retail armour: rebuild the vertex and triangle blocks of multi-material hull collision from their own
    /// vertices, faces and per-face materials, and require the bytes to match what DICE shipped.</summary>
    [InstallFact(Installs.Bf1942CleanArchives, Installs.BfvOriginalArchives)]
    public void Multi_material_retail_sections_rebuild_their_vertex_and_triangle_blocks_byte_exact()
    {
        int checkedSections = 0;
        foreach (var (archive, meshes) in new[]
                 {
                     (Path.Combine(Installs.Bf1942CleanArchives, "standardMesh.rfa"), new[] { "Sherman_Hull_M1", "Tiger_Hull_M1", "Willy_Hul_M1" }),
                     (Path.Combine(Installs.BfvOriginalArchives, "standardMesh.rfa"), new[] { "ve_t54_body_m1" }),
                 })
        {
            if (!File.Exists(archive)) continue;
            var a = new RefractorFlatArchive(archive);
            foreach (var mesh in meshes)
            {
                var e = a.Entries.FirstOrDefault(x => x.Name.EndsWith("/" + mesh + ".sm", StringComparison.OrdinalIgnoreCase));
                if (e is null) continue;
                var sm = StandardMesh.Parse(a.Read(e));
                foreach (var sec in sm.CollisionSections)
                {
                    Assert.True(StandardMesh.TryParseCollisionFull(sec, out var col));
                    var verts = Enumerable.Range(0, col.VertexCount).Select(i => new Vec3(col.Verts[i * 4], col.Verts[i * 4 + 1], col.Verts[i * 4 + 2])).ToList();
                    var tris = Enumerable.Range(0, col.TriangleCount).Select(t => ((int)col.Tris[t * 4], (int)col.Tris[t * 4 + 1], (int)col.Tris[t * 4 + 2])).ToList();
                    var mats = Enumerable.Range(0, col.TriangleCount).Select(t => (int)col.Tris[t * 4 + 3]).ToList();
                    var rebuilt = StandardMeshWriter.BuildCollisionSection(verts, tris, mats);
                    int blocks = 12 + 16 * col.VertexCount + 4 + 8 * col.TriangleCount;
                    Assert.True(rebuilt.AsSpan(0, blocks).SequenceEqual(sec.AsSpan(0, blocks)), $"{mesh}: vertex/triangle blocks differ");
                    checkedSections++;
                }
            }
        }
        Assert.True(checkedSections >= 4, $"only {checkedSections} sections checked");
    }

    private const string LevelGolden =
        "StandardMesh/My_Crate.sm 17B37DE999F37070\n" +
        "StandardMesh/My_Crate.rs F0D4A04BB9BF2299\n" +
        "Texture/crate_wood.dds 9F64A747E1B97F13\n" +
        "Objects/My_Crate/Geometries.con F2BFED1C727D3ED3\n" +
        "Objects/My_Crate/Objects.con 2DA00E23B5683ECC\n" +
        "Objects/My_Crate/My_Crate.con 13C83D09AD599BBF";
}
