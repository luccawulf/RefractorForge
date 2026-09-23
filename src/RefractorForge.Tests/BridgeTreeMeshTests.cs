using System.Text;
using RefractorBridge.Mesh;
using RefractorForge.Formats.Rfa;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Gates on the BF1942 TreeMesh -> Battlefield Vietnam StandardMesh converter (RefractorBridge, M3 mesh piece).
///
/// BFV registers no TreeMesh geometry type, so every .tm in a BF1942 mod is an object that cannot load at all -
/// 190 of them in Forgotten Hope, 185 in FHSW, 112 in Pirates of the Reich. The conversion is a regrouping of
/// the same vertices and triangles into StandardMesh sections, so the counts are checkable exactly.
///
/// Runs against the real BF1942 tree archive when the game is installed and skips when it is not.
/// </summary>
public class BridgeTreeMeshTests
{
    private const string TreeArchive = @"D:\Games\EA GAMES\Battlefield 1942\Mods\bf1942\Archives\treeMesh.rfa";

    private static byte[]? Tm(string nameFragment)
    {
        if (!File.Exists(TreeArchive)) return null;
        var archive = new RefractorFlatArchive(TreeArchive);
        var hit = archive.Entries.FirstOrDefault(e =>
            e.Name.EndsWith(".tm", StringComparison.OrdinalIgnoreCase) &&
            e.Name.Contains(nameFragment, StringComparison.OrdinalIgnoreCase));
        return hit is null ? null : archive.Read(hit);
    }

    [Fact]
    public void A_real_tree_converts_to_a_standard_mesh_that_parses_back()
    {
        if (Tm("Pacific_Palm_1_M1") is not { } bytes) return;

        var tree = TreeMesh.Parse(bytes);
        var converted = TmToSm.Convert(bytes, "Pacific_Palm_1_M1");

        // The .sm must be readable by the same parser the engine's format is modelled on.
        var mesh = StandardMesh.Parse(converted.StandardMesh);
        Assert.NotEmpty(mesh.Lods);

        // A regrouping, not a resample: every non-sprite triangle survives, exactly.
        int expected = tree.Groups
            .Where((_, g) => g != 2)
            .SelectMany(g => g)
            .Sum(m => m.Count);
        Assert.Equal(expected, converted.Triangles);
        Assert.Equal(expected, mesh.Lods[0].Sum(m => m.Faces.Length));
        Assert.Equal(converted.MaterialNames.Count, mesh.Lods[0].Count);
    }

    [Fact]
    public void Every_material_the_mesh_declares_is_defined_by_the_material_script()
    {
        // A StandardMesh binds sections to materials BY NAME. A name the .rs omits is not an error - the
        // surface just renders untextured, which is how Desert Combat shipped two invisible interiors.
        if (Tm("Pacific_Palm_1_M1") is not { } bytes) return;

        var converted = TmToSm.Convert(bytes, "Pacific_Palm_1_M1");
        var mesh = StandardMesh.Parse(converted.StandardMesh);

        foreach (var section in mesh.Lods[0])
            Assert.Contains($"\"{section.Name}\"", converted.RsText, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_material_script_line_ends_in_a_semicolon()
    {
        // A missing semicolon loads fine on a dedicated server - which has no renderer and never parses
        // materials - and crashes the client partway through loading. Cheap to gate, expensive to find.
        if (Tm("Pacific_Palm_1_M1") is not { } bytes) return;

        string rs = TmToSm.Convert(bytes, "Pacific_Palm_1_M1").RsText;

        foreach (string raw in rs.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("subshader", StringComparison.Ordinal)
                || line == "{" || line == "}") continue;
            Assert.EndsWith(";", line);
        }
    }

    [Fact]
    public void The_material_script_follows_retail_vietnam_vegetation()
    {
        // Modelled on BFV's own C01F_Trees_M1.rs rather than invented. The combination that matters is
        // 'transparent false' WITH an alpha test - alpha-BLENDING a leaf card is what makes ported foliage
        // look like glass.
        if (Tm("Pacific_Palm_1_M1") is not { } bytes) return;

        string rs = TmToSm.Convert(bytes, "Pacific_Palm_1_M1").RsText;

        Assert.Contains("\"StandardMesh/Default\"", rs, StringComparison.Ordinal);
        Assert.Contains("transparent false;", rs, StringComparison.Ordinal);
        Assert.Contains("alphatestref 0.5;", rs, StringComparison.Ordinal);
        Assert.Contains("selfillum", rs, StringComparison.Ordinal);
        Assert.DoesNotContain("transparent true", rs, StringComparison.Ordinal);
    }

    [Fact]
    public void The_collision_hull_is_carried_across()
    {
        // Trees are solid in game and the hull is a separate block from the render geometry, so dropping it
        // turns every converted tree into something you walk through.
        if (Tm("Pacific_Palm_1_M1") is not { } bytes) return;

        var tree = TreeMesh.Parse(bytes);
        if (!tree.HasCollision || tree.CollisionVertices.Length == 0) return;

        var converted = TmToSm.Convert(bytes, "Pacific_Palm_1_M1");
        Assert.True(converted.CollisionCarried);

        var mesh = StandardMesh.Parse(converted.StandardMesh);
        Assert.NotEmpty(mesh.CollisionSections);
    }

    [Fact]
    public void Billboard_imposters_are_dropped_and_a_billboard_only_mesh_is_reported_as_such()
    {
        // BFV has no shader for BF1942's distance billboards; kept, they draw as flat untextured quads.
        // A mesh that is NOTHING but billboards therefore has nothing left to convert - jungle_bush_M1 and
        // Jungle_tree15c_M1 are exactly that, and both parse cleanly, so this is content, not a parse failure.
        if (Tm("jungle_bush_M1") is not { } spriteOnly) return;

        Assert.True(TmToSm.IsBillboardOnly(spriteOnly));
        Assert.Throws<InvalidDataException>(() => TmToSm.Convert(spriteOnly, "jungle_bush_M1"));

        if (Tm("Pacific_Palm_1_M1") is { } normal)
            Assert.False(TmToSm.IsBillboardOnly(normal));
    }

    [Fact]
    public void Texture_names_are_reduced_to_the_archive_root_form()
    {
        // A .tm names its texture 'texture/PAHILE_C'; the .rs wants texture "texture/PAHILE_C" - not
        // texture "texture/texture/PAHILE_C".
        if (Tm("Pacific_Palm_1_M1") is not { } bytes) return;

        var converted = TmToSm.Convert(bytes, "Pacific_Palm_1_M1");

        Assert.All(converted.Textures, t =>
        {
            Assert.DoesNotContain("/", t);
            Assert.DoesNotContain(".", t);
        });
        Assert.DoesNotContain("texture/texture/", converted.RsText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_whole_retail_tree_archive_converts()
    {
        // The real gate: every .tm BF1942 ships, converted and re-parsed. A format bug that only shows on one
        // odd mesh in a hundred is exactly the kind that reaches the game.
        if (!File.Exists(TreeArchive)) return;

        var archive = new RefractorFlatArchive(TreeArchive);
        int converted = 0, billboards = 0;

        foreach (var e in archive.Entries)
        {
            if (!e.Name.EndsWith(".tm", StringComparison.OrdinalIgnoreCase)) continue;
            byte[] bytes = archive.Read(e);
            string name = Path.GetFileNameWithoutExtension(e.Name);

            if (TmToSm.IsBillboardOnly(bytes)) { billboards++; continue; }

            var c = TmToSm.Convert(bytes, name);
            var mesh = StandardMesh.Parse(c.StandardMesh);            // must parse back
            Assert.Equal(c.MaterialNames.Count, mesh.Lods[0].Count);
            Assert.True(c.Triangles > 0, $"{name} converted to zero triangles");
            converted++;
        }

        Assert.True(converted > 100, $"only {converted} meshes converted");
        Assert.True(billboards <= 5, $"{billboards} billboard-only meshes is more than expected");
    }
}
