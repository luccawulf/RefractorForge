using RefractorForge.Formats.Rfa;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// <c>transparent true</c> and <c>alphaTestRef</c> are INDEPENDENT render states: the first enables alpha blending,
/// the second enables an alpha test at that threshold. A material may declare both, and 39 of the 100 that name a
/// ref also name a <c>blendDest</c> - a blend destination factor, which is meaningless without blending.
///
/// Getting this wrong broke things in both directions. Blending a material without applying its ref made the Willys
/// jeep's engine grill see-through (its sheet is 71% low-alpha, so the texels the engine discards were smeared
/// across the panel instead). Then treating the pair as "cutout, NOT blend" made Interstate's headlight glows and
/// some trees disappear entirely. The rule is: honour both flags, separately.
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

    // Verbatim from Interstate's dbs70lightfront.rs - a headlight glow. Blended AND alpha-tested: the blendDest
    // proves the blending, so rendering it as a pure cutout made the headlights vanish.
    private const string HeadlightGlow = """
        subshader "dbs70lightfront_Material0" "StandardMesh/Default"
        {
        	lighting false;
        	materialDiffuse 0.588235 0.588235 0.588235;
        	blendDest one;
        	transparent true;
        	alphaTestRef 0.7;
        	depthWrite false;
        	twosided true;
        	texture "texture/phareglowblanc";
        }
        """;

    [Fact]
    public void GrillCarriesBothFlags()
    {
        var set = RsShaderSet.Parse(WillysHull);
        var grill = set.Materials["Willy_Hul_M1_Material2"];
        Assert.True(grill.Transparent);                     // blends...
        Assert.Equal(0.7f, grill.AlphaTestRef!.Value, 3);   // ...AND discards below 0.7. Both, not either.
    }

    [Fact]
    public void HeadlightGlowBlendsAndAlphaTests()
    {
        var glow = RsShaderSet.Parse(HeadlightGlow).Materials["dbs70lightfront_Material0"];
        Assert.True(glow.Transparent);                      // must stay blended or the light disappears
        Assert.Equal(0.7f, glow.AlphaTestRef!.Value, 3);
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
