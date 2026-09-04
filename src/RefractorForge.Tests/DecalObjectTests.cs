using System.Text;
using RefractorForge.Formats;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Rfa;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// A decal is a level-local object the game has to take entirely on trust: a mesh, a shader, a texture and four
/// scripts we generate, registered from Init.con. These pin the shape of every file to what retail levels ship -
/// Easter Island's Sign_Credits for the scripts, BfVietnam's standardMesh.rfa for the mesh - and drive the same
/// patch-save call the editor makes, so the files land where the engine reads them.
/// </summary>
public class DecalObjectTests : IDisposable
{
    private readonly string _dir;
    public DecalObjectTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "rfdecal_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static byte[] FakeDds() => new byte[128 + 4 * 4 * 4];   // header-sized stub; the mesh is what is under test

    private static DecalObject.Built Build(string baseSub = "BfVietnam")
        => DecalObject.Build("Test_Map", "poster", 2f, 1f, "decal_poster", FakeDds(), flat: false, doubleSided: true, baseSub: baseSub);

    private static byte[] Bytes(DecalObject.Built b, string rel) => b.Files.First(f => f.RelPath.Equals(rel, StringComparison.OrdinalIgnoreCase)).Bytes;
    private static string Text(DecalObject.Built b, string rel) => Encoding.Latin1.GetString(Bytes(b, rel));

    /// <summary>
    /// Every one of the 1,997 meshes in BfVietnam's standardMesh.rfa ends with a trailing section (u32 flag,
    /// u32 size, then size bytes; 699 of them the empty form). Ours used to stop dead after the geometry, and a
    /// loader that reads that section unconditionally runs off the end of the file.
    /// </summary>
    [Fact]
    public void The_mesh_ends_with_the_trailing_section_every_shipped_mesh_has()
    {
        var sm = Bytes(Build(), "StandardMesh/poster.sm");
        var parsed = StandardMesh.Parse(sm);
        Assert.Equal(8, parsed.Total - parsed.Consumed);
        Assert.Equal(new byte[8], sm[^8..]);
        Assert.Equal(10u, parsed.Version);
        Assert.Equal(1, parsed.NumLods);
        var mat = Assert.Single(parsed.Lods[0]);
        Assert.Equal("poster_Material0", mat.Name);
        Assert.Equal(1041u, mat.VertexFormat);   // pos/normal/uv, the format 1,647 retail material sections use
        Assert.Equal(32u, mat.VertexByteSize);
        Assert.Equal(8, mat.NumVertices);        // two quads: front and back
        Assert.Equal(12, mat.NumFaceValues);
    }

    [Fact]
    public void The_bounding_box_has_thickness_on_every_axis()
    {
        foreach (var flat in new[] { false, true })
        {
            var b = DecalObject.Build("L", "d", 2f, 1f, "t", FakeDds(), flat: flat);
            var bb = b.Mesh.BoundingBox;
            Assert.True(bb[3] - bb[0] > 0.01f, "x");
            Assert.True(bb[4] - bb[1] > 0.01f, "y");
            Assert.True(bb[5] - bb[2] > 0.01f, "z");
            var parsed = StandardMesh.Parse(Bytes(b, "StandardMesh/d.sm"));
            Assert.Equal(bb, parsed.BoundingBox);
        }
    }

    /// <summary>The scripts, shaped like Easter Island's Sign_Credits, which is the level-local object the engine
    /// is known to load. The mount root differs per game and nothing else does.</summary>
    [Fact]
    public void The_scripts_follow_the_retail_level_local_recipe()
    {
        foreach (var (sub, root) in new[] { ("BfVietnam", "BfVietnam"), ("bf1942", "bf1942") })
        {
            var b = Build(sub);
            Assert.Equal(new[] { "StandardMesh/poster.sm", "StandardMesh/poster.rs", "Texture/decal_poster.dds",
                                 "Objects/poster/Geometries.con", "Objects/poster/Objects.con", "Objects/poster/poster.con" },
                         b.Files.Select(f => f.RelPath));
            Assert.Equal("run poster/poster", b.RunLine);

            var geom = Text(b, "Objects/poster/Geometries.con");
            Assert.Contains("GeometryTemplate.create StandardMesh poster", geom);
            Assert.Contains($"GeometryTemplate.file ../{root}/levels/Test_Map/StandardMesh/poster", geom);
            Assert.Contains("GeometryTemplate.setLodDistance 5 ", geom);      // the far end of the LOD ramp = cull distance

            var obj = Text(b, "Objects/poster/Objects.con");
            Assert.Contains("ObjectTemplate.create SimpleObject poster", obj);
            Assert.Contains("ObjectTemplate.geometry poster", obj);
            Assert.Contains("ObjectTemplate.setHasCollisionPhysics 0", obj);

            Assert.Equal("run Objects\r\nrun Geometries\r\n", Text(b, "Objects/poster/poster.con"));

            // The shader grammar is strict: every statement takes a value and ends in ';', the texture path is
            // folder-qualified, and the material name is what the .sm binds to.
            var rs = Text(b, "StandardMesh/poster.rs");
            Assert.StartsWith("subshader \"poster_Material0\" \"StandardMesh/Default\"", rs);
            Assert.Contains("texture \"texture/decal_poster\";", rs);
            foreach (var line in rs.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && l != "{" && l != "}" && !l.StartsWith("subshader")))
                Assert.EndsWith(";", line);
        }
    }

    [Fact]
    public void Init_con_gains_the_run_line_and_texture_path_once()
    {
        const string init = "renderer.fogstart 50\r\nrun Init/Terrain\r\n";
        var once = DecalObject.PatchInitCon(init, "Test_Map", "BfVietnam");
        Assert.Contains("run Objects/Objects", once);
        Assert.Contains("textureManager.alternativePath BfVietnam/levels/Test_Map/Texture", once);
        Assert.StartsWith(init, once);                                    // nothing above it touched
        Assert.Equal(once, DecalObject.PatchInitCon(once, "Test_Map", "BfVietnam"));   // idempotent

        var oc = DecalObject.PatchObjectsCon(null, "run poster/poster");
        Assert.Equal("run poster/poster\r\n", oc);
        Assert.Equal("run poster/poster\r\nrun sign/sign\r\n", DecalObject.PatchObjectsCon(oc, "run sign/sign"));
        Assert.Equal("run poster/poster\r\nrun sign/sign\r\n", DecalObject.PatchObjectsCon("run poster/poster\r\nrun sign/sign\r\n", "run poster/poster"));
    }

    /// <summary>
    /// What Ctrl+S does for a .rfa level: the archive the user opened is rewritten IN PLACE, with the decal's
    /// files added under the level's own prefix and everything else carried over untouched. This is the path a
    /// decal has to survive - the files and the Init.con that registers them have to end up in the archive the
    /// game actually mounts, not in a separate patch beside it.
    /// </summary>
    [Fact]
    public void Saving_in_place_writes_the_decal_into_the_archive_the_user_opened()
    {
        const string prefix = "BfVietnam/levels/Test_Map/";
        string path = Path.Combine(_dir, "Test_Map.rfa");
        var initText = "renderer.fogstart 50\r\nrun Init/Terrain\r\n";
        var heightmap = new byte[64 * 64 * 2];
        for (int i = 0; i < heightmap.Length; i++) heightmap[i] = (byte)(i * 7);
        RefractorFlatArchive.WriteFile(path, new List<(string, byte[])>
        {
            (prefix + "Init.con", Encoding.Latin1.GetBytes(initText)),
            (prefix + "StaticObjects.con", Encoding.Latin1.GetBytes("rem empty\r\n")),
            (prefix + "Init/Terrain.con", Encoding.Latin1.GetBytes("GeometryTemplate.worldSize 1024\r\n")),
            (prefix + "Textures/tx00x00.dds", heightmap),
        }, compress: true, xPackId: XPackId.Default);
        var before = new RefractorFlatArchive(path);
        int entriesBefore = before.Entries.Count;

        var built = Build();
        var newEntries = new List<(string RelPath, byte[] Bytes)>(built.Files)
        {
            ("Objects/objects.con", Encoding.Latin1.GetBytes(DecalObject.PatchObjectsCon(null, built.RunLine))),
            ("Init.con", Encoding.Latin1.GetBytes(DecalObject.PatchInitCon(initText, "Test_Map", "BfVietnam"))),
        };
        // Source and destination are the SAME file, which is what makes this an in-place save.
        LevelSaver.RepackToRfa(path, path, null, null, null, null, newEntries: newEntries);
        Assert.Null(RefractorFlatArchive.Validate(path));

        var after = new RefractorFlatArchive(path);
        var names = after.Entries.Select(e => e.Name.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in built.Files) Assert.Contains(prefix + f.RelPath, names);
        Assert.Contains(prefix + "Objects/objects.con", names);
        Assert.Equal(entriesBefore + built.Files.Count + 1, after.Entries.Count);   // +objects.con; Init.con replaced

        // The decal registration is in the Init.con the engine reads, and the untouched entries came through
        // byte for byte.
        var savedInit = Encoding.Latin1.GetString(after.Read(after.Entries.First(e => e.Name.EndsWith("Test_Map/Init.con", StringComparison.OrdinalIgnoreCase))));
        Assert.Contains("run Objects/Objects", savedInit);
        Assert.Contains("textureManager.alternativePath BfVietnam/levels/Test_Map/Texture", savedInit);
        Assert.Equal(heightmap, after.Read(after.Entries.First(e => e.Name.EndsWith("tx00x00.dds", StringComparison.OrdinalIgnoreCase))));
        Assert.Equal(Bytes(built, "StandardMesh/poster.sm"), after.Read(after.Entries.First(e => e.Name.EndsWith("poster.sm", StringComparison.OrdinalIgnoreCase))));
    }

    /// <summary>
    /// The save the editor performs: the decal's files go into a patch archive as NEW entries under the level's
    /// own prefix, alongside the patched Init.con and Objects/objects.con - and the result validates, which is
    /// the gate every save passes before it reports success.
    /// </summary>
    [Fact]
    public void A_patch_save_adds_every_file_under_the_level_prefix()
    {
        const string prefix = "BfVietnam/levels/Test_Map/";
        string basePath = Path.Combine(_dir, "Test_Map.rfa");
        var initText = "renderer.fogstart 50\r\nrun Init/Terrain\r\n";
        RefractorFlatArchive.WriteFile(basePath, new List<(string, byte[])>
        {
            (prefix + "Init.con", Encoding.Latin1.GetBytes(initText)),
            (prefix + "StaticObjects.con", Encoding.Latin1.GetBytes("rem empty\r\n")),
            (prefix + "Init/Terrain.con", Encoding.Latin1.GetBytes("GeometryTemplate.worldSize 1024\r\n")),
        }, compress: true, xPackId: XPackId.Default);

        var built = Build();
        var newEntries = new List<(string RelPath, byte[] Bytes)>(built.Files)
        {
            ("Objects/objects.con", Encoding.Latin1.GetBytes(DecalObject.PatchObjectsCon(null, built.RunLine))),
            ("Init.con", Encoding.Latin1.GetBytes(DecalObject.PatchInitCon(initText, "Test_Map", "BfVietnam"))),
        };
        string patchPath = Path.Combine(_dir, "Test_Map_001.rfa");
        var names = LevelSaver.WritePatchRfa(basePath, patchPath, null, null, null, null, newEntries: newEntries);
        Assert.Null(RefractorFlatArchive.Validate(patchPath));

        var patch = new RefractorFlatArchive(patchPath);
        var entryNames = patch.Entries.Select(e => e.Name.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in built.Files) Assert.Contains(prefix + f.RelPath, entryNames);
        Assert.Contains(prefix + "Objects/objects.con", entryNames);
        Assert.Contains(prefix + "Init.con", entryNames);
        Assert.Equal(8, names.Count);

        // The existing Init.con was REPLACED (matched by path), not duplicated beside the base's copy.
        Assert.Single(patch.Entries, e => e.Name.EndsWith("Test_Map/Init.con", StringComparison.OrdinalIgnoreCase));
        var savedInit = Encoding.Latin1.GetString(patch.Read(patch.Entries.First(e => e.Name.EndsWith("Test_Map/Init.con", StringComparison.OrdinalIgnoreCase))));
        Assert.Contains("run Objects/Objects", savedInit);
        Assert.Contains("run Init/Terrain", savedInit);
        var savedSm = patch.Read(patch.Entries.First(e => e.Name.EndsWith("poster.sm", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(Bytes(built, "StandardMesh/poster.sm"), savedSm);
    }
}
