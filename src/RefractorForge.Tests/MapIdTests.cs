using RefractorForge.Formats.Con;
using Xunit;

namespace RefractorForge.Tests;

/// <summary><see cref="MapId"/>: the Menu/init.con line without which Battlefield 1942 loads no mod objects.</summary>
public class MapIdTests
{
    private const string Old = "Game.setLocalized 1\r\n\r\nrem ** Multiplayer Briefing **\r\ngame.setLoadPicture Load/Desert.tga";

    [Fact]
    public void A_level_from_before_the_patch_has_no_map_id()
    {
        Assert.False(MapId.IsSet(Old));
        Assert.False(MapId.IsSet(null));
        Assert.False(MapId.IsSet("rem game.setMapId \"BF1942\"\r\n"));       // commented out
        Assert.False(MapId.IsSet("game.setMapId \r\n"));                      // no value
        Assert.False(MapId.IsSet("game.setMapIdentity x\r\n"));               // another command
    }

    [Fact]
    public void Retail_spellings_count_as_set()
    {
        Assert.True(MapId.IsSet("game.setMapId \"BF1942\"\r\n"));
        Assert.True(MapId.IsSet(Old + "\r\nGame.SetMapId DesertCombat\n"));
        Assert.True(MapId.IsSet("\tgame.setMapId\t\"XPack1\""));
    }

    [Fact]
    public void Adding_it_appends_one_line_and_changes_nothing_else()
    {
        string fixedText = MapId.With(Old, "BF1942");
        Assert.Equal(Old + "\r\ngame.setMapId \"BF1942\"\r\n", fixedText);
        Assert.True(MapId.IsSet(fixedText));
        Assert.Equal(fixedText, MapId.With(fixedText, "Other"));              // already set: left alone
        Assert.Equal("game.setMapId \"BF1942\"\r\n", MapId.With("", "BF1942"));
    }

    [Fact]
    public void The_id_follows_the_mod_folder()
    {
        Assert.Equal("BF1942", MapId.ForMod("bf1942"));
        Assert.Equal("BF1942", MapId.ForMod(null));
        Assert.Equal("DesertCombat", MapId.ForMod("DesertCombat"));
    }
}
