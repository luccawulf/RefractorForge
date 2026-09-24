using System.Numerics;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The shader flags a picture needs to draw a part as the game does: <c>twosided</c> (the engine culls a face seen
/// from behind otherwise), <c>lighting false</c> (full bright: a lamp's lens) and <c>blendDest one</c> (added on: a glow) -
/// the interstate mod's tail light shader word for word; and a glow drawn over what is behind it.
/// </summary>
public class RsShaderFlagsTests
{
    [Fact]
    public void Two_sided_unlit_and_additive_are_read()
    {
        var set = RsShaderSet.Parse(
            "subshader \"Taillights_Material13\" \"StandardMesh/Default\"\r\n{\r\n\tlighting false;\r\n\tlightingSpecular false;\r\n" +
            "\tmaterialDiffuse 0.588235 0.588235 0.588235;\r\n\tblendDest one;\r\n\talphaTestRef 0.7;\r\n\ttexture \"texture/red\";\r\n}\r\n" +
            "subshader \"Glass\" \"StandardMesh/Default\"\r\n{\r\n\tlighting true;\r\n\ttransparent true;\r\n\ttwosided true;\r\n\tblendDest invSourceAlpha;\r\n}\r\n" +
            "subshader \"Plain\" \"StandardMesh/Default\"\r\n{\r\n\tlighting true;\r\n\tlightingSpecular true;\r\n\ttwosided false;\r\n}\r\n");
        var glow = set.Materials["Taillights_Material13"];
        Assert.True(glow.Unlit);
        Assert.True(glow.Additive);
        Assert.False(glow.TwoSided);
        var glass = set.Materials["Glass"];
        Assert.True(glass.TwoSided);
        Assert.False(glass.Unlit);                  // lightingSpecular is not lighting
        Assert.False(glass.Additive);
        var plain = set.Materials["Plain"];
        Assert.False(plain.Unlit || plain.Additive || plain.TwoSided);
    }

    [Fact]
    public void A_glow_adds_onto_what_is_behind_it_and_black_adds_nothing()
    {
        var img = new ImageBuffer(16, 16);
        img.Clear(new Vector3(0.2f, 0.2f, 0.2f));
        var cam = new Camera { Aspect = 1f, Near = 0.1f, Far = 100f, Position = new Vector3(0f, 0f, 5f) };
        cam.LookAt(Vector3.Zero);
        var pane = new[] { new Vector3(-1f, -1f, 0f), new Vector3(1f, -1f, 0f), new Vector3(1f, 1f, 0f), new Vector3(-1f, 1f, 0f) };
        var idx = new[] { 0, 1, 2, 0, 2, 3 };
        SoftwareRenderer.DrawModels(img, cam, new Vector3(0f, 1f, 0f), pane, idx, new[] { new ModelInstance(Matrix4x4.Identity, new Vector3(0.5f, 0f, 0f)) },
                                    additive: true, unlit: true);
        int p = (8 * 16 + 8) * 3;
        Assert.InRange(img.Rgb[p], 175, 185);        // 0.2 + 0.5, unlit: whatever way it faces the light
        Assert.InRange(img.Rgb[p + 1], 50, 52);      // green untouched
        Assert.Equal(float.MaxValue, img.Depth[8 * 16 + 8]);   // no depth written
    }
}
