using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Tree meshes (.tm) carry no .rs at all, so the alpha channel is the only evidence for how to draw them. The old
/// test demanded a CLEANLY SEPARATED alpha ("almost nothing between transparent and solid") and so rejected every
/// antialiased leaf sheet: BF1942's palm leaf `pahile_c` is 61% transparent and 20% solid with 18.6% in between, and
/// Wake's palms rendered as opaque white cards. Only the palms whose texture happened to be NAMED palmleaf_T
/// survived, via the foliage word list - which is why some trees looked right and most did not.
/// </summary>
public class AlphaProfileTests
{
    /// <summary>Build a texture with a chosen alpha distribution; colour is irrelevant to the profile.</summary>
    private static Texture2D WithAlpha(params (byte Alpha, int Count)[] runs)
    {
        int n = runs.Sum(r => r.Count);
        var rgba = new byte[n * 4];
        int i = 0;
        foreach (var (alpha, count) in runs)
            for (int k = 0; k < count; k++, i++) { rgba[i * 4] = 128; rgba[i * 4 + 1] = 128; rgba[i * 4 + 2] = 128; rgba[i * 4 + 3] = alpha; }
        return new Texture2D(n, 1, rgba);
    }

    /// <summary>The real palm leaf sheet's distribution. Antialiasing is not a reason to call something opaque.</summary>
    [Fact]
    public void AntialiasedLeafSheetIsACutout()
    {
        // 61% transparent, 20% solid, 19% spread across the middle - pahile_c to the nearest percent.
        var runs = new List<(byte, int)> { ((byte)0, 610), ((byte)255, 200) };
        for (byte a = 40; a < 230; a += 10) runs.Add((a, 10));
        var prof = MeshLibrary.ProfileAlpha(WithAlpha(runs.ToArray()));
        Assert.True(prof.Cutout);
        Assert.InRange(prof.Ref, 0.20f, 0.80f);
    }

    /// <summary>Trunk and bark sheets are fully opaque and must stay solid - cutting them would punch holes.</summary>
    [Fact]
    public void FullyOpaqueTextureIsNotACutout()
    {
        Assert.False(MeshLibrary.ProfileAlpha(WithAlpha(((byte)255, 1000))).Cutout);
    }

    /// <summary>An all-but-empty alpha channel is unused/garbage; treating it as a cutout would erase the mesh.</summary>
    [Fact]
    public void FullyTransparentTextureIsNotACutout()
    {
        Assert.False(MeshLibrary.ProfileAlpha(WithAlpha(((byte)0, 1000))).Cutout);
    }

    /// <summary>A clean binary mask still works, and its threshold lands between the two populations.</summary>
    [Fact]
    public void BinaryMaskSplitsBetweenTheTwoPopulations()
    {
        var prof = MeshLibrary.ProfileAlpha(WithAlpha(((byte)0, 500), ((byte)255, 500)));
        Assert.True(prof.Cutout);
        Assert.InRange(prof.Ref, 0.20f, 0.80f);
    }

    /// <summary>The threshold tracks where the gap actually is, rather than a fixed 0.33 for every texture. Both
    /// masks here are genuine cutouts (real transparent AND real solid regions); only the valley between them
    /// moves.</summary>
    [Fact]
    public void ThresholdFollowsTheAlphaDistribution()
    {
        var low = MeshLibrary.ProfileAlpha(WithAlpha(((byte)0, 500), ((byte)255, 500)));
        var high = MeshLibrary.ProfileAlpha(WithAlpha(((byte)60, 500), ((byte)255, 500)));
        Assert.True(low.Cutout && high.Cutout);
        Assert.True(high.Ref > low.Ref, $"expected a higher cut where the transparent side is brighter: low={low.Ref} high={high.Ref}");
    }

    /// <summary>A texture with no transparent texels at all is opaque, whatever its mid-tones do - there is no
    /// hole to cut. This is what keeps trunks, bark and painted bodywork solid.</summary>
    [Fact]
    public void TextureWithNoTransparentRegionIsNotACutout()
    {
        Assert.False(MeshLibrary.ProfileAlpha(WithAlpha(((byte)160, 500), ((byte)255, 500))).Cutout);
    }

    [Fact]
    public void MissingTextureIsHandled()
    {
        var prof = MeshLibrary.ProfileAlpha(null);
        Assert.False(prof.Cutout);
    }
}
