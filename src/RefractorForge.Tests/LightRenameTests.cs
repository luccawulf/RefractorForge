using System.Collections.Generic;
using RefractorForge.Formats.Terrain;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Renaming several lights at once.
///
/// The name is the only thing a light shows in the list, so stamping one name on a whole street would make the list
/// useless - but leaving the name on a single light while every other control edits the selection was the wrong
/// half-measure. A numbered rename is the answer, and the fiddly part is that the number must come OFF again before
/// the next rename puts one on.
/// </summary>
public class LightRenameTests
{
    private static List<PointLight> Lamps(int n, string name = "Light")
    {
        var l = new List<PointLight>();
        for (int i = 0; i < n; i++) l.Add(new PointLight { Name = name });
        return l;
    }

    [Theory]
    [InlineData("Street lamp 12", "Street lamp")]
    [InlineData("Street lamp", "Street lamp")]
    [InlineData("Sodium 3", "Sodium")]
    [InlineData("Lamp   7", "Lamp")]
    [InlineData("M16", "M16")]              // the number is part of the word, not an index
    [InlineData("7", "7")]                  // nothing but digits: there is no stem to keep
    [InlineData("", "")]
    [InlineData(null, "")]
    public void The_stem_drops_an_index_but_not_a_name(string? input, string expected)
        => Assert.Equal(expected, LightRig.NameStem(input));

    [Fact]
    public void A_selection_is_renamed_in_order_and_numbered()
    {
        var lamps = Lamps(4);
        LightRig.RenameNumbered(lamps, "Street lamp");

        Assert.Equal(new[] { "Street lamp 1", "Street lamp 2", "Street lamp 3", "Street lamp 4" },
                     lamps.ConvertAll(x => x.Name).ToArray());
    }

    [Fact]
    public void Renaming_twice_does_not_stack_indices()
    {
        // The bug this guards: "Street lamp" -> "Street lamp 1", then typing again over the same selection giving
        // "Street lamp 1 1". The box seeds from the stem and the rename strips one too, so it cannot happen.
        var lamps = Lamps(3);
        LightRig.RenameNumbered(lamps, "Street lamp");
        LightRig.RenameNumbered(lamps, lamps[0].Name);          // exactly what the panel re-seeds with

        Assert.Equal(new[] { "Street lamp 1", "Street lamp 2", "Street lamp 3" },
                     lamps.ConvertAll(x => x.Name).ToArray());
    }

    [Fact]
    public void One_light_keeps_exactly_what_was_typed()
    {
        // A single light needs no index - it would be noise, and it is the case the panel had before any of this.
        var one = Lamps(1);
        LightRig.RenameNumbered(one, "  Porch bulb  ");
        Assert.Equal("Porch bulb", one[0].Name);
    }

    [Fact]
    public void A_blank_box_is_not_a_rename()
    {
        // Clearing the field mid-edit must not wipe forty names.
        var lamps = Lamps(3, "Sodium");
        LightRig.RenameNumbered(lamps, "   ");
        Assert.All(lamps, x => Assert.Equal("Sodium", x.Name));

        LightRig.RenameNumbered(lamps, null);
        Assert.All(lamps, x => Assert.Equal("Sodium", x.Name));
    }

    [Fact]
    public void Renaming_nothing_is_harmless()
        => LightRig.RenameNumbered(new List<PointLight>(), "Street lamp");
}
