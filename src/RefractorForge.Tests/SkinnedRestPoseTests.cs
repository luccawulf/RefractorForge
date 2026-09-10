using System.Text;
using RefractorForge.Formats.Mesh;
using RefractorForge.Formats.Rfa;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// A skinned mesh's <c>.sm</c> holds the SKIN'S BIND POSE, not the shape the engine draws. The engine puts every
/// vertex through its skeleton, so an <c>AnimatedMesh</c> rendered straight from the file is wrong in both
/// orientation and anchor.
///
/// The Tango is what made this visible. Its <c>Objects.con</c> hangs an <c>AnimatedUsFlag</c> off the mast with a
/// plain <c>setPosition 0.991/8.768/-0.41</c>, and the bind pose of <c>o_USflag_m1</c> is upside down (the flag
/// texture's top edge sits at the mesh's LOWEST vertices) and straddles its own origin. Drawn raw, the boat flew an
/// inverted flag centred on its mast. Posed through <c>flag.ske</c> the cloth hangs the right way up and entirely
/// to one side, its hoist edge at the origin - which is exactly why one setPosition is all the .con needs.
///
/// Measured on the real archives: the flag part's world box goes from y 8.13..9.80 / z -1.51..0.61 (straddling the
/// mount, extending ABOVE it) to y 7.13..8.74 / z -2.57..-0.43 (hanging from it, on one side). Every other part of
/// every retail vehicle is byte-identical either way - nothing else in the corpus is skinned.
/// </summary>
public class SkinnedRestPoseTests
{
    private static string NewLevelDir(string name)
    {
        var d = Path.Combine(Path.GetTempPath(), "rf_skin_" + Guid.NewGuid().ToString("N")[..8], name);
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Write(string dir, string rel, byte[] bytes)
    {
        var p = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, bytes);
    }

    private static void Write(string dir, string rel, string text) => Write(dir, rel, Encoding.ASCII.GetBytes(text));

    /// <summary>A length-prefixed, NUL-terminated name: u16 length INCLUDING the NUL, then the bytes.</summary>
    private static void Name(BinaryWriter w, string s)
    {
        var b = Encoding.ASCII.GetBytes(s);
        w.Write((ushort)(b.Length + 1));
        w.Write(b);
        w.Write((byte)0);
    }

    /// <summary>One root bone whose local matrix is the given rotation ROWS + translation - the on-disk layout
    /// (f[0..2]/f[4..6]/f[8..10] are the rows, f[3]/f[7]/f[11] the translation).</summary>
    private static byte[] SkeletonOneBone(string bone, float[] rows3x3, float tx, float ty, float tz)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(1);                       // version
        w.Write(1);                       // boneCount
        Name(w, bone);
        w.Write((ushort)0xFFFF);          // root
        float[] f =
        {
            rows3x3[0], rows3x3[1], rows3x3[2], tx,
            rows3x3[3], rows3x3[4], rows3x3[5], ty,
            rows3x3[6], rows3x3[7], rows3x3[8], tz,
        };
        foreach (var v in f) w.Write(v);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>A skin binding every vertex fully to bone 0, with bindPosLocal = the vertex's own position, so the
    /// rest pose is exactly <c>boneWorld * bindPos</c> and the expected answer is arithmetic, not a fixture.</summary>
    private static byte[] SkinAllOnOneBone(string bone, System.Numerics.Vector3[] verts)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(1);                       // version
        w.Write(verts.Length);
        foreach (var v in verts)
        {
            w.Write(v.X); w.Write(v.Y); w.Write(v.Z);
            w.Write((byte)1);             // one influence
            w.Write((ushort)0);           // local bone index
            w.Write(1f);                  // weight
            w.Write(v.X); w.Write(v.Y); w.Write(v.Z);
        }
        w.Write((ushort)1);
        Name(w, bone);
        w.Flush();
        return ms.ToArray();
    }

    private const string Obj = """
o cloth
v 1 0 0
v -1 0 0
v -1 1 0
vt 0 0
vt 1 0
vt 1 1
usemtl cloth
f 1/1 2/2 3/3
""";

    private static readonly System.Numerics.Vector3[] Verts =
    {
        new(1f, 0f, 0f), new(-1f, 0f, 0f), new(-1f, 1f, 0f),
    };

    /// <summary>Build a level folder holding one skinned AnimatedBundle: mesh + skin + skeleton + its cons.</summary>
    private static string BuildSkinnedLevel(float[] rows, float tx, float ty, float tz)
    {
        var dir = NewLevelDir("SkinMap");
        Write(dir, "standardmesh/cloth.sm", StandardMeshWriter.Write(ObjMesh.Parse(Obj)));
        Write(dir, "animations/cloth.skn", SkinAllOnOneBone("Bone01", Verts));
        Write(dir, "animations/banner.ske", SkeletonOneBone("Bone01", rows, tx, ty, tz));
        Write(dir, "objects/Banner/Geometries.con",
              "GeometryTemplate.create AnimatedMesh cloth\nGeometryTemplate.setSkin animations/cloth.skn\nGeometryTemplate.file cloth\n");
        Write(dir, "objects/Banner/Objects.con",
              "ObjectTemplate.create AnimatedBundle Banner\nObjectTemplate.geometry cloth\nObjectTemplate.createSkeleton animations/banner.ske\n");
        return dir;
    }

    /// <summary>The correction itself: the mesh comes back through its skeleton, not as it sits in the file.
    /// A 180 degree roll is the Tango flag's own case - it is what turns the cloth right way up.</summary>
    [Fact]
    public void A_skinned_template_renders_in_its_skeletons_rest_pose()
    {
        float[] roll180 = { -1, 0, 0, 0, -1, 0, 0, 0, 1 };
        var dir = BuildSkinnedLevel(roll180, 1.1f, -0.6f, 0f);
        try
        {
            var lib = MeshLibrary.Open(dir);
            Assert.True(lib.TryGetSkinnedRest("Banner", out var posed), "the skinned template should resolve to a posed mesh");

            for (int i = 0; i < Verts.Length; i++)
            {
                var p = Verts[i];
                var want = new System.Numerics.Vector3(-p.X + 1.1f, -p.Y - 0.6f, p.Z);
                Assert.Equal(want.X, posed.Positions[i].X, 4);
                Assert.Equal(want.Y, posed.Positions[i].Y, 4);
                Assert.Equal(want.Z, posed.Positions[i].Z, 4);
            }

            // ...and the raw mesh lookup is untouched, so nothing that asks for the .sm gets a surprise.
            Assert.True(lib.TryGet("cloth", out var raw));
            Assert.Equal(Verts[0].X, raw.Positions[0].X, 4);
            Assert.Equal(Verts[0].Y, raw.Positions[0].Y, 4);
        }
        finally { Directory.Delete(Path.GetDirectoryName(dir)!, true); }
    }

    /// <summary>The hierarchy walk applies it too - which is the path the Tango's flag actually takes, since the
    /// flag is a CHILD of the boat rather than something placed on its own.</summary>
    [Fact]
    public void A_skinned_child_of_an_assembly_is_posed_where_its_parent_hangs_it()
    {
        float[] roll180 = { -1, 0, 0, 0, -1, 0, 0, 0, 1 };
        var dir = BuildSkinnedLevel(roll180, 1.1f, -0.6f, 0f);
        try
        {
            Write(dir, "objects/Vehicles/Sea/Boat/Objects.con",
                  "ObjectTemplate.create PlayerControlObject Boat\n"
                + "ObjectTemplate.addTemplate Banner\n"
                + "ObjectTemplate.setPosition 0/10/0\n");
            var lib = MeshLibrary.Open(dir);
            Assert.True(lib.TryAssembleVehicle("Boat", out var parts));
            var part = Assert.Single(parts);

            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var v in part.Mesh.Positions)
            {
                var w = System.Numerics.Vector3.Transform(v, part.Local);
                lo = MathF.Min(lo, w.Y); hi = MathF.Max(hi, w.Y);
            }
            // Bind Y runs 0..1, so unposed the cloth would sit at 10..11 - ABOVE its mount. Posed it hangs from
            // it: -0.6 down to -1.6 relative to the mount, i.e. 8.4..9.4.
            Assert.Equal(8.4f, lo, 3);
            Assert.Equal(9.4f, hi, 3);
        }
        finally { Directory.Delete(Path.GetDirectoryName(dir)!, true); }
    }

    /// <summary>Nothing else changes. A template with no skeleton, or a mesh with no skin beside it, keeps the
    /// exact bind pose it always had - the correction only fires where the data to make it is present.</summary>
    [Fact]
    public void An_unskinned_template_is_left_exactly_as_the_file_has_it()
    {
        var dir = NewLevelDir("PlainMap");
        try
        {
            Write(dir, "standardmesh/cloth.sm", StandardMeshWriter.Write(ObjMesh.Parse(Obj)));
            Write(dir, "objects/Prop/Geometries.con", "GeometryTemplate.create StandardMesh cloth\nGeometryTemplate.file cloth\n");
            Write(dir, "objects/Prop/Objects.con", "ObjectTemplate.create SimpleObject Prop\nObjectTemplate.geometry cloth\n");
            var lib = MeshLibrary.Open(dir);
            Assert.False(lib.TryGetSkinnedRest("Prop", out _), "no skeleton -> no correction");
            Assert.True(lib.TryGetRenderMesh("Prop", out var m));
            Assert.Equal(Verts[0].X, m.Positions[0].X, 4);
            Assert.Equal(Verts[2].Y, m.Positions[2].Y, 4);
        }
        finally { Directory.Delete(Path.GetDirectoryName(dir)!, true); }
    }
}
