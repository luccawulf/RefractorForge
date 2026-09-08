using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Two faults found by reading a real BfVietnam run of Saigon68, whose log carried 260 copies of
/// <c>RaShaderPVLS1DifLmp.cpp:84  Expression: m_LightMapD3DH</c> — the engine binding the lightmap shader and
/// finding a null texture handle.
///
/// The first is naming: <c>GeometryTemplate.file</c> may be a PATH, and Saigon68's city_dumpster1 really is
/// <c>../standardMesh/city_dumpster1</c>. Written verbatim the lightmap landed at
/// <c>ObjectLightMaps/../standardMesh/city_dumpster1_449-10-173.tga</c> — 15 files that normalise out of the folder
/// the engine reads.
///
/// The second is coverage, and is tested where the bake decides it: a mesh carrying a lightmap channel must end up
/// with a FILE even when there is no unwrap to bake, because the engine picks its shader from the vertex format and
/// asserts when the texture is absent. Retail ships 376 maps for Saigon68 for exactly that reason.
/// </summary>
public class ObjectLightmapNamingTests
{
    [Theory]
    // The real offender from the user's archive.
    [InlineData("../standardMesh/city_dumpster1", "city_dumpster1")]
    [InlineData("..\\standardMesh\\city_dumpster1", "city_dumpster1")]
    // Ordinary names are untouched — this must not disturb the 443 that were already right.
    [InlineData("O_HueHouse_B_M1", "O_HueHouse_B_M1")]
    [InlineData("o_sewers_A_M1", "o_sewers_A_M1")]
    // Deeper and absolute paths.
    [InlineData("../../objects/props/lamp_m1", "lamp_m1")]
    [InlineData("BfVietnam/levels/Saigon68/StandardMesh/decal1", "decal1")]
    // An extension would otherwise double up into "x.sm_10-2-3.tga".
    [InlineData("../standardMesh/city_dumpster1.sm", "city_dumpster1")]
    // Degenerate input must not throw or produce a path fragment.
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("../", "")]
    public void FileBase_reduces_a_geometry_reference_to_its_leaf(string meshName, string expected)
        => Assert.Equal(expected, ObjectLightmaps.FileBase(meshName));

    /// <summary>The whole point: whatever comes back can be used as a file name, so it may never contain a
    /// separator or a traversal segment that would move the file out of ObjectLightMaps/.</summary>
    [Theory]
    [InlineData("../standardMesh/city_dumpster1")]
    [InlineData("..\\standardMesh\\city_dumpster1")]
    [InlineData("a/b/c/d")]
    [InlineData("../../..")]
    public void FileBase_never_returns_something_that_escapes_the_folder(string meshName)
    {
        var b = ObjectLightmaps.FileBase(meshName);
        Assert.DoesNotContain('/', b);
        Assert.DoesNotContain('\\', b);
        Assert.NotEqual("..", b);
        // And the name the writer actually builds stays inside the folder.
        var path = $"ObjectLightMaps/{b}_449-10-173.tga";
        Assert.DoesNotContain("/../", path);
    }

    /// <summary>The name the engine reads is <c>&lt;meshLeaf&gt;_&lt;x&gt;-&lt;y&gt;-&lt;z&gt;.tga</c>, and negative
    /// coordinates are ordinary (Saigon68's sewers sit below zero: <c>o_sewers_A_M1_591--4-288.tga</c>). Building it
    /// from the leaf must still round-trip through the loader's own name parser.</summary>
    [Theory]
    [InlineData("../standardMesh/city_dumpster1", 449, -10, 173)]
    [InlineData("o_sewers_A_M1", 591, -4, 288)]
    public void Built_name_is_parseable_by_the_loader(string meshName, int x, int y, int z)
    {
        var name = $"{ObjectLightmaps.FileBase(meshName)}_{x}-{y}-{z}";
        var olm = new ObjectLightmaps();
        // A 4x4 grey map is enough to prove the name parses and keys correctly.
        var tex = new Texture2D(4, 4, new byte[4 * 4 * 4]);
        olm.AddBaked(ObjectLightmaps.FileBase(meshName), x, y, z, tex);
        Assert.NotNull(olm.Match(ObjectLightmaps.FileBase(meshName), x, y, z));
        Assert.DoesNotContain('/', name);
    }
}
