using System.Text;
using RefractorForge.Formats.Con;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Teams are a per-LEVEL property, not a per-game one. Labelling them "Axis / Allies" for BF1942 and "NVA / US" for
/// Vietnam was wrong on everything but a stock WWII map - stock Wake is Japan vs US Marines and Interstate's
/// Akina_Mountain is British vs US. A level states it in Init.con via <c>game.setTeamSkin</c>, and the soldier skin
/// IS the nationality, so no lookup table is needed and any mod's own skins work.
/// </summary>
public class TeamNameTests
{
    private static byte[] Con(string s) => Encoding.Latin1.GetBytes(s);

    [Fact]
    public void ReadsTheSkinsStockWakeDeclares()
    {
        var t = TeamNames.Parse(new[] { Con("game.setTeamSkin 1 JapaneseSoldier\ngame.setTeamSkin 2 USMarineSoldier\n") }, vietnam: false);
        Assert.Equal("Japanese", t.Team1);
        Assert.Equal("US Marine", t.Team2);
        Assert.Equal("Neutral", t.Neutral);
    }

    [Fact]
    public void ReadsAModsOwnSkins()
    {
        var t = TeamNames.Parse(new[] { Con("game.setTeamSkin 1 BritishSoldier\ngame.setTeamSkin 2 USSoldier\n") }, vietnam: false);
        Assert.Equal("British", t.Team1);
        Assert.Equal("US", t.Team2);
    }

    /// <summary>No skins named -> the flag models say the same thing.</summary>
    [Fact]
    public void FallsBackToTheFlagModels()
    {
        var t = TeamNames.Parse(new[] { Con("ObjectTemplate.setTeamGeometry 1 flagJp_m1\nObjectTemplate.setTeamGeometry 2 flagus_m1\n") }, vietnam: false);
        Assert.Equal("Japanese", t.Team1);
        Assert.Equal("US", t.Team2);
    }

    /// <summary>A level that says nothing keeps the old game-based defaults rather than inventing a nationality.</summary>
    [Fact]
    public void SilentLevelKeepsTheGameDefaults()
    {
        var bf = TeamNames.Parse(new[] { Con("rem nothing here\n") }, vietnam: false);
        Assert.Equal("Axis", bf.Team1);
        Assert.Equal("Allies", bf.Team2);
        var bfv = TeamNames.Parse(System.Array.Empty<byte[]>(), vietnam: true);
        Assert.Equal("Vietcong / NVA", bfv.Team1);
        Assert.Equal("US Army", bfv.Team2);
    }

    /// <summary>Commented-out lines are not settings.</summary>
    [Fact]
    public void CommentsAreIgnored()
    {
        var t = TeamNames.Parse(new[] { Con("rem game.setTeamSkin 1 GermanSoldier\ngame.setTeamSkin 1 JapaneseSoldier\n") }, vietnam: false);
        Assert.Equal("Japanese", t.Team1);
    }

    /// <summary>The first file to name a team wins, so a patch archive's copy overrides the base's.</summary>
    [Fact]
    public void FirstFileToNameATeamWins()
    {
        var t = TeamNames.Parse(new[]
        {
            Con("game.setTeamSkin 1 GermanSoldier\n"),
            Con("game.setTeamSkin 1 JapaneseSoldier\ngame.setTeamSkin 2 USSoldier\n"),
        }, vietnam: false);
        Assert.Equal("German", t.Team1);   // from the first file
        Assert.Equal("US", t.Team2);       // team 2 only appeared in the second
    }

    [Theory]
    [InlineData("USMarineSoldier", "US Marine")]
    [InlineData("JapaneseSoldier", "Japanese")]
    [InlineData("RedArmySoldier", "Red Army")]
    [InlineData("NVA_Soldier", "NVA")]
    [InlineData("USSoldier", "US")]
    public void SkinNamesBecomeReadableLabels(string skin, string expected)
        => Assert.Equal(expected, TeamNames.FriendlyFromSkin(skin));

    [Fact]
    public void LabelledIncludesTheIndexTheDialogsShow()
    {
        var t = new TeamNames("Neutral", "Japanese", "US Marine");
        Assert.Equal("Neutral (0)", t.Labelled(0));
        Assert.Equal("Japanese (1)", t.Labelled(1));
        Assert.Equal("US Marine (2)", t.Labelled(2));
    }
}
