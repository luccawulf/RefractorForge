using System.Text;

namespace RefractorBridge.Level;

/// <summary>
/// Battlefield Vietnam's foliage ("growth") files, generated empty.
///
/// These are effectively mandatory: all 83 retail levels <c>run</c> both growth scripts, and a BF1942 level has
/// no equivalent to carry over. A map with no foliage still needs the pair to exist - zero-filled index maps and
/// a palette declaring all sixteen material slots with empty <c>&lt;types&gt;</c>.
///
/// The one detail that bites: <c>materialMapFilename</c> is an ENGINE property holding the level's own path, so
/// a <c>.wst</c> copied from another level - or from the level's old name - silently makes the game read THAT
/// level's growth map. It has to be rewritten to the ported level's path, which is why these are generated from
/// the level name rather than copied.
///
/// Shapes and slot order are taken from retail (Hue): overgrowth 256 square, undergrowth 1024 square.
/// </summary>
public static class GrowthStubs
{
    /// <summary>The sixteen terrain material slots, in the order the engine's own table uses.</summary>
    public static readonly string[] MaterialSlots =
    {
        "default", "water", "dryGrass", "juicyGrass", "dryDirt", "wetDirt", "mud", "deathMaterial",
        "gravel", "muddyWater", "drySand", "wetSand", "rock", "sandRoad", "dirtRoad", "pavelRoad",
    };

    public const int OverGrowthSide = 256;
    public const int UnderGrowthSide = 1024;

    /// <summary>A zero-filled index map. These are discrete INDEX maps (0-14), not 0-255 densities.</summary>
    public static byte[] Map(int side) => new byte[side * side];

    public static byte[] OverGrowthMap() => Map(OverGrowthSide);
    public static byte[] UnderGrowthMap() => Map(UnderGrowthSide);

    public static string OverGrowthWst(string levelName) => Wst(
        "overGrowth", levelName, "overGrowthMap", OverGrowthSide,
        "        viewDistance = \"200\"\r\n\tclosePercentage=\"0.4\"\r\n\timportSceneObjects = \"true\"");

    public static string UnderGrowthWst(string levelName) => Wst(
        "underGrowth", levelName, "underGrowthMap", UnderGrowthSide,
        "        viewdistance=\"35\"");

    private static string Wst(string root, string levelName, string mapName, int side, string extraAttributes)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\"?>\r\n");
        sb.Append("<WRAPPER_TREE\r\n    VERS = \"1.1\">\r\n");
        sb.Append($"    <{root}\r\n");
        // Backslashes and the BfVietnam\levels\<name> shape, exactly as retail writes it.
        sb.Append($"    \tmaterialMapFilename = \"BfVietnam\\levels\\{levelName}\\growth\\{mapName}\"\r\n");
        sb.Append($"    \tmaterialMapSideSize = \"{side}\"\r\n");
        sb.Append(extraAttributes).Append("\r\n        >\r\n");
        sb.Append("        <materials>\r\n");

        foreach (string slot in MaterialSlots)
        {
            sb.Append($"            <{slot}>\r\n");
            sb.Append("                <types>\r\n");
            sb.Append("                </types>\r\n");
            sb.Append($"            </{slot}>\r\n");
        }

        sb.Append("        </materials>\r\n");
        sb.Append($"    </{root}>\r\n");
        sb.Append("</WRAPPER_TREE>\r\n");
        return sb.ToString();
    }
}
