using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The engine pairs an object with its AI mesh by NAME: a geometry name cut at its last '_' plus "_a1", looked up in
/// aiMeshes/ (BF1942.exe builds "AIMeshes/" + stem + "_a1.aim"). And a mesh can carry two collision sections, COL01
/// then COL02, which the collision overlay can now show apart. Checked on the clean BF1942 install.
/// </summary>
public class AiMeshAndCollisionTests
{
    static MeshLibrary Bf1942() => MeshLibrary.Open(
        Path.Combine(Installs.Bf1942CleanArchives, "Objects.rfa"),
        Path.Combine(Installs.Bf1942CleanArchives, "StandardMesh_001.rfa"),
        Path.Combine(Installs.Bf1942CleanArchives, "standardMesh.rfa"),
        Path.Combine(Installs.Bf1942CleanArchives, "aiMeshes.rfa"));

    [InstallFact(Installs.Bf1942CleanArchives)]
    public void A_building_finds_its_ai_mesh_by_geometry_name()
    {
        var lib = Bf1942();
        Assert.Equal(90, lib.AiMeshCount);
        // afr_house1_ste_m1 is a Bundle whose interior geometry is afr_house1_ste_m1 -> aiMeshes/afr_house1_ste_a1
        var ai = lib.AiMeshFor("afr_house1_ste_m1");
        Assert.NotNull(ai);
        Assert.Equal("afr_house1_ste_a1", ai!.Value.Name, ignoreCase: true);
        var shape = lib.AiMeshShape(ai.Value.Name);
        Assert.NotNull(shape);
        Assert.True(shape!.Indices.Length >= 3);
    }

    [InstallFact(Installs.Bf1942CleanArchives)]
    public void An_object_without_one_reports_none()
    {
        var lib = Bf1942();
        Assert.True(lib.KnowsTemplate("ammobox_m1") || lib.HasMeshEntry("ammobox_m1"));
        Assert.Null(lib.AiMeshFor("ammobox_m1"));
    }

    [InstallFact(Installs.Bf1942CleanArchives)]
    public void Collision_sections_are_listed_and_selectable()
    {
        var lib = Bf1942();
        // Find a mesh that carries two sections and check the overlay can decode each on its own.
        string? two = null;
        foreach (var name in lib.MeshBaseNames)
        {
            var stem = name.EndsWith(".sm", StringComparison.OrdinalIgnoreCase) ? name[..^3] : name;
            if (lib.CollisionSectionsOf(stem).Count >= 2) { two = stem; break; }
        }
        Assert.NotNull(two);
        var secs = lib.CollisionSectionsOf(two!);
        Assert.Equal(0, secs[0].Section);
        Assert.Equal(1, secs[1].Section);

        lib.CollisionSection = 0;
        Assert.True(lib.TryGetCollision(two!, out var col1));
        lib.CollisionSection = 1;
        Assert.True(lib.TryGetCollision(two!, out var col2));
        Assert.Equal(secs[0].Faces, col1.Indices.Length / 3);
        Assert.Equal(secs[1].Faces, col2.Indices.Length / 3);
        lib.CollisionSection = -1;
        Assert.True(lib.TryGetCollision(two!, out var first));
        Assert.Equal(col1.Indices.Length, first.Indices.Length);   // "first" is COL01 when there is one
    }
}
