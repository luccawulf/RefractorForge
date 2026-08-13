using RefractorForge.Formats.Rfa;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Refractor overloads one keyword: <c>transparent true</c> ALONE means alpha blending (glass, canopies, gunsights),
/// but paired with an <c>alphaTestRef</c> it means an alpha TEST at that threshold (grilles, decals, painted
/// markings, ropes). Reading every <c>transparent</c> as a blend is what made the Willys jeep's engine grill
/// see-through in the editor - its sheet is 71% low-alpha, so blending it left almost nothing on screen where the
/// engine punches a clean grille out of it.
/// </summary>
public class RsShaderTests
{
    private const string WillysHull = """
        subshader "Willy_Hul_M1_Material1" "StandardMesh/Default"
        {
        	lighting true;
        	materialDiffuse 1 1 1;
        	texture "texture/Willy1_Z";
        }

        subshader "Willy_Hul_M1_Material2" "StandardMesh/Default"
        {
        	lighting true;
        	materialDiffuse 1 1 1;
        	transparent true;
        	twosided true;
        	depthWrite false;
        	alphaTestRef 0.7;
        	texture "texture/Willy3_Z";
        }
        """;

    // Verbatim from the stock 1p_Willy_Hul_M1.rs - real glass, and pointedly NO alphaTestRef.
    private const string WillysGlass = """
        subshader "1p_Willy_Hul_M1_Material1" "StandardMesh/Default"
        {
        	lighting true;
        	materialDiffuse 1 1 1;
        	transparent true;
        	depthWrite false;
        	envmap true;
        	texture "texture/katy_window_I";
        }
        """;

    [Fact]
    public void GrillIsACutoutAtItsAuthoredThreshold()
    {
        var set = RsShaderSet.Parse(WillysHull);
        var grill = set.Materials["Willy_Hul_M1_Material2"];
        Assert.True(grill.Transparent);                  // the keyword IS there...
        Assert.Equal(0.7f, grill.AlphaTestRef!.Value, 3);  // ...but the ref is what decides how to render it
    }

    [Fact]
    public void PlainMaterialCarriesNoThreshold()
    {
        var set = RsShaderSet.Parse(WillysHull);
        var body = set.Materials["Willy_Hul_M1_Material1"];
        Assert.False(body.Transparent);
        Assert.Null(body.AlphaTestRef);
    }

    [Fact]
    public void GlassBlendsBecauseItDeclaresNoRef()
    {
        var set = RsShaderSet.Parse(WillysGlass);
        var glass = set.Materials["1p_Willy_Hul_M1_Material1"];
        Assert.True(glass.Transparent);
        Assert.Null(glass.AlphaTestRef);
    }

    /// <summary>A material resets between blocks: an alphaTestRef must not leak from one subshader into the next,
    /// which would silently turn the glass that follows a grille into a cutout.</summary>
    [Fact]
    public void ThresholdDoesNotLeakIntoTheNextSubshader()
    {
        var set = RsShaderSet.Parse(WillysHull + "\n" + WillysGlass);
        Assert.Equal(0.7f, set.Materials["Willy_Hul_M1_Material2"].AlphaTestRef!.Value, 3);
        Assert.Null(set.Materials["1p_Willy_Hul_M1_Material1"].AlphaTestRef);
    }

    /// <summary>The split against the real game: stock BF1942 has far more blends than cutouts, and every
    /// alphaTestRef sits on a material that also says `transparent`. If that ever stopped holding, the rule above
    /// would be the wrong rule.</summary>
    [Fact]
    public void RealArchivesSplitBlendFromCutout()
    {
        string? archive = null;
        foreach (var root in new[] { @"D:\Games\EA GAMES\Battlefield 1942", @"D:\Games\EA GAMES\Battlefield Vietnam" })
        {
            if (!Directory.Exists(root)) continue;
            try { archive = Directory.EnumerateFiles(root, "StandardMesh*.rfa", SearchOption.AllDirectories).FirstOrDefault(); }
            catch { }
            if (archive is not null) break;
        }
        if (archive is null) return;   // no game install on this machine - the unit gates above still apply

        var arc = new RefractorFlatArchive(archive);
        int blend = 0, cutout = 0;
        foreach (var e in arc.Entries.Where(x => x.Name.EndsWith(".rs", StringComparison.OrdinalIgnoreCase)))
        {
            RsShaderSet set;
            try { set = RsShaderSet.Parse(System.Text.Encoding.Latin1.GetString(arc.Read(e))); }
            catch { continue; }
            foreach (var m in set.Materials.Values)
            {
                if (m.AlphaTestRef is { } r) { cutout++; Assert.InRange(r, 0f, 1f); }
                else if (m.Transparent) blend++;
            }
        }
        Assert.True(blend > 0 && cutout > 0, $"expected both kinds in {Path.GetFileName(archive)}, got {blend} blends / {cutout} cutouts");
    }
}
