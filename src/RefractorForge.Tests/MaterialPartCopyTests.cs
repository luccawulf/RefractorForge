using System.Numerics;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// A vehicle's mesh is ASSEMBLED from its parts, and the assembler used to rebuild each material by listing its
/// fields. Anything added to <see cref="MeshLibrary.MaterialPart"/> after that line was written was therefore
/// dropped on the way through: the assault Huey's canopy reached the renderer with no opacity and with depth
/// writing back on, so it stamped the depth buffer before the fuselage drew and the fuselage was culled - the
/// aircraft looked see-through. The same material on a static object, which skips assembly, was correct.
/// A <c>with</c> expression cannot lose a field, so the test guards the property rather than the field list.
/// </summary>
public class MaterialPartCopyTests
{
    private static MeshLibrary.MaterialPart Glass() => new(
        Indices: new[] { 0, 1, 2 },
        Color: new Vector3(0.5f, 0.6f, 0.7f),
        Texture: null,
        AlphaTest: false,
        Blend: true,
        TextureName: "texture/Ve_Hueyatk_win",
        AlphaRef: null,
        Foliage: false,
        Opacity: 0.2f,
        DepthWrite: false);

    [Fact]
    public void Rebasing_a_parts_indices_keeps_every_other_render_state()
    {
        var original = Glass();
        var rebased = original with { Indices = new[] { 10, 11, 12 } };

        Assert.Equal(new[] { 10, 11, 12 }, rebased.Indices);
        // The states the assembler used to lose.
        Assert.Equal(0.2f, rebased.Opacity!.Value, 3);
        Assert.False(rebased.DepthWrite);
        // ...and the ones it did carry, so the copy is genuinely whole.
        Assert.True(rebased.Blend);
        Assert.False(rebased.AlphaTest);
        Assert.False(rebased.Foliage);
        Assert.Equal("texture/Ve_Hueyatk_win", rebased.TextureName);
        Assert.Equal(original.Color, rebased.Color);
    }

    [Fact]
    public void An_ordinary_opaque_material_still_writes_depth()
    {
        // The default matters: a struct-like copy that forgot DepthWrite would turn depth writing OFF for the whole
        // scene, which is far worse than the bug it came from.
        var opaque = new MeshLibrary.MaterialPart(new[] { 0, 1, 2 }, Vector3.One, null, AlphaTest: false);
        Assert.True(opaque.DepthWrite);
        Assert.Null(opaque.Opacity);
        Assert.True((opaque with { Indices = new[] { 3, 4, 5 } }).DepthWrite);
    }
}
