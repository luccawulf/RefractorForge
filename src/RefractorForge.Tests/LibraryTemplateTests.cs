using System;
using System.Collections.Generic;
using System.Linq;
using RefractorForge.Formats.Con;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Dropping an object from the Object Library writes <c>object.create &lt;name&gt;</c>. The library lists MESH stems -
/// o_speakers_m1.sm shows as "o_speakers" - and the drop used to write that stem, a template no .con declares: the
/// game logged "createObject failed, unknown objectTemplate", a parse error for each position/rotation line after it,
/// and drew nothing. The name written has to come from the TEMPLATE registry, exactly.
/// </summary>
public class LibraryTemplateTests
{
    // A registry as MeshLibrary.DeclaredTemplateName sees it: case-insensitive lookup, declared spelling back.
    private static Func<string, string?> Registry(params string[] declared)
    {
        var d = declared.ToDictionary(n => n, n => n, StringComparer.OrdinalIgnoreCase);
        return n => d.TryGetValue(n, out var hit) ? hit : null;
    }

    // What base BfVietnam's objects.rfa holds for the speakers: one template, the LOD meshes it draws.
    private static readonly string[] SpeakerMeshes = { "o_speakers_m1.sm", "o_speakers_m2.sm" };

    [Theory]
    [InlineData("o_speakers_m1.sm", "o_speakers")]
    [InlineData("o_speakers_m2.sm", "o_speakers")]
    [InlineData("C05F_Trees_L1.sm", "C05F_Trees")]
    [InlineData("bunker_lod2", "bunker")]
    [InlineData("Sheridan", "Sheridan")]
    public void The_library_lists_a_mesh_under_its_stem(string mesh, string display)
        => Assert.Equal(display, LibraryTemplate.DisplayName(mesh));

    [Fact]
    public void A_stem_resolves_to_the_M1_template_the_archive_declares()
    {
        var reg = Registry("o_speakers_m1");
        Assert.Equal("o_speakers_m1", LibraryTemplate.Resolve("o_speakers", reg, SpeakerMeshes));
        // Without the mesh list too: "_M1" is the retail convention, and the declared spelling comes back.
        Assert.Equal("o_speakers_m1", LibraryTemplate.Resolve("o_speakers", reg));
    }

    [Theory]
    [InlineData("o_speakers_m1")]
    [InlineData("O_SPEAKERS_M1")]
    public void A_name_that_is_already_a_template_is_unchanged(string name)
        => Assert.Equal("o_speakers_m1", LibraryTemplate.Resolve(name, Registry("o_speakers_m1"), SpeakerMeshes));

    [Fact]
    public void A_template_named_like_the_stem_wins_over_the_M1()
    {
        // interstate-style content: the template AND the mesh are the plain name.
        Assert.Equal("city_building2", LibraryTemplate.Resolve("city_building2", Registry("city_building2", "city_building2_M1")));
        // A vehicle is listed by its folder, which is its template.
        Assert.Equal("Sheridan", LibraryTemplate.Resolve("Sheridan", Registry("Sheridan")));
    }

    [Fact]
    public void Falls_back_to_the_mesh_the_entry_was_listed_from()
    {
        // Only an _m2 is declared; neither the stem nor "_M1" names anything.
        Assert.Equal("c05f_trees_m2",
            LibraryTemplate.Resolve("c05f_trees", Registry("c05f_trees_m2"), new[] { "c05f_trees_m2.sm" }));
    }

    // Retail's ammo box: mesh O_USAmmo_M1.sm, listed as "O_USAmmo", drawn by a template called USAmmobox.
    private static IEnumerable<string> Drawing(string stem) => stem.ToLowerInvariant() switch
    {
        "o_usammo" => new[] { "USAmmobox" },
        "ch_1pvietcongarms" => new[] { "VietcongA1_1pBody", "VietcongA2_1pBody" },
        "o_speakers" => new[] { "o_speakers_m1" },
        _ => Array.Empty<string>(),
    };

    [Fact]
    public void An_object_named_nothing_like_its_mesh_resolves_through_its_geometry()
        => Assert.Equal("USAmmobox",
               LibraryTemplate.Resolve("O_USAmmo", Registry("USAmmobox"), new[] { "O_USAmmo_M1.sm" }, Drawing));

    [Fact]
    public void A_mesh_drawn_by_several_templates_is_a_guess_and_is_refused()
        => Assert.Null(LibraryTemplate.Resolve("Ch_1PVietCongArms", Registry("VietcongA1_1pBody", "VietcongA2_1pBody"),
                                               new[] { "Ch_1PVietCongArms.sm" }, Drawing));

    [Fact]
    public void The_geometry_fallback_needs_the_drawing_template_declared_and_never_outranks_a_name()
    {
        // Drawn by USAmmobox, but nothing declares it: still refused.
        Assert.Null(LibraryTemplate.Resolve("O_USAmmo", Registry("o_speakers_m1"), new[] { "O_USAmmo_M1.sm" }, Drawing));
        // A direct hit wins before the geometry index is ever asked.
        Assert.Equal("o_speakers_m1", LibraryTemplate.Resolve("o_speakers", Registry("o_speakers_m1"), SpeakerMeshes,
                                                              _ => throw new InvalidOperationException("asked the geometry index")));
    }

    [Fact]
    public void An_unresolvable_name_is_rejected()
    {
        // The exact defect: a registry that knows o_speakers_m1 must not be satisfied by anything else, and a stem
        // with no declared template anywhere gives null - the editor refuses the drop instead of writing it.
        Assert.Null(LibraryTemplate.Resolve("o_nothing", Registry("o_speakers_m1"), new[] { "o_nothing_m1.sm" }, Drawing));
        Assert.Null(LibraryTemplate.Resolve("o_speakers", Registry()));
        Assert.Null(LibraryTemplate.Resolve("", Registry("o_speakers_m1"), null, Drawing));
    }

    [Fact]
    public void A_suffixed_name_is_never_stripped_to_match()
    {
        // The mesh resolver tolerates _m1; the template check must not. "o_speakers_m1" is not "o_speakers".
        Assert.Null(LibraryTemplate.Resolve("o_speakers_m1", Registry("o_speakers")));
        Assert.DoesNotContain("o_speakers_m1_M1", LibraryTemplate.Candidates("o_speakers_m1"), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Candidates_lead_with_the_entry_then_M1_then_the_meshes_and_never_repeat()
    {
        var c = LibraryTemplate.Candidates("o_speakers", SpeakerMeshes).ToList();
        Assert.Equal(new[] { "o_speakers", "o_speakers_M1", "o_speakers_m2" }, c);   // o_speakers_m1 == o_speakers_M1
        Assert.Empty(LibraryTemplate.Candidates(" "));
    }

    [Fact]
    public void The_mesh_list_is_only_walked_when_the_first_guesses_miss()
    {
        IEnumerable<string> exploding = new[] { "o_speakers_m1.sm" }.Select<string, string>(_ => throw new InvalidOperationException("walked the mesh list"));
        Assert.Equal("o_speakers_m1", LibraryTemplate.Resolve("o_speakers", Registry("o_speakers_m1"), exploding));
    }

    [Fact]
    public void Meshes_listed_as_an_entry_are_matched_by_stem()
    {
        var all = new[] { "o_speakers_m1.sm", "o_speakers_m2.sm", "o_speakersbig_m1.sm", "Sky_HCMT2_m1.sm" };
        Assert.Equal(SpeakerMeshes, LibraryTemplate.MeshesListedAs("O_Speakers", all).ToArray());
    }
}
