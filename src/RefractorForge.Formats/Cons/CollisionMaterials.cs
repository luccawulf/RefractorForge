using System;
using System.Linq;

namespace RefractorForge.Formats.Con;

/// <summary>
/// The engine's numbered materials a collision mesh can be made of - what a bullet sounds like on it, what
/// damage it takes, how it grips - offered by what they are where the game says, and by what uses them where
/// it does not.
/// <para>
/// The table is <c>game/materialManagerdefine.con</c>, the same in BF1942 and BfVietnam for these ids. The
/// terrain surfaces 0-15 and the stairs 96-98 are named there; the "Basic Materials" 79-95 are not - DICE left
/// them as bare numbers with no comment - so each is described by the retail meshes whose collision sections use
/// it (2,004 meshes in <c>standardMesh.rfa</c> + <c>objects.rfa</c> read for this). The order is by how many
/// faces carry the id: 88 is on 107,000, 81 on 78,000, 92 on 28,000 - and 92 is what Desert Combat's props, the
/// ones Saigon68 ships, are made of.
/// </para>
/// </summary>
public static class CollisionMaterials
{
    public readonly record struct Choice(int Id, string Name);

    /// <summary>The picker's rows. The last row is the "type an id" escape; its <see cref="Choice.Id"/> is -1.</summary>
    public static readonly Choice[] Choices =
    {
        new(88,  "88 - stone: temples, walls, boulders, ruins"),
        new(81,  "81 - wood: huts, fences, bridges, crates, bunkers"),
        new(92,  "92 - brick and plaster: town buildings, slums, stone fences"),
        new(85,  "85 - metal: wrecks, barbed wire, machinery"),
        new(94,  "94 - concrete: sewers, concrete rubble, houses"),
        new(93,  "93 - masonry: large building walls"),
        new(90,  "90 - hard surface: boxes, huts, small hull fittings"),
        new(82,  "82 - soft: tents, sandbags, planks, rubber trees"),
        new(80,  "80 - trees and poles"),
        new(84,  "84 - steel: guns, engines, wreck hulls"),
        new(83,  "83 - tunnels and sewers"),
        new(26,  "26 - furniture and interior props"),
        new(96,  "96 - stone stairs"),
        new(97,  "97 - wood stairs"),
        new(98,  "98 - iron stairs"),
        new(178, "178 - iron stairs (retail's collision-only boxes)"),
        new(0,   "0 - default (terrain)"),
        new(-1,  "Other (type the id)"),
    };

    /// <summary>
    /// The armour classes a VEHICLE's collision is made of - not the building materials above. The damage system
    /// (<c>game/damage_system</c>, the MDT damage tutorial) groups defensive materials in bands, and within a band a
    /// higher number is thicker armour; the retail hulls use them per face, harder on the front:
    /// Willy_Hul_M1 45, Sherman_Hull_M1 50/51/52, Tiger_Hull_M1 51/53/54, BFV ve_t54_body_m1 51/53/54.
    /// Wheels are 37 (Willy_WheR_M1) and wrecks 85 (Wreck_Willy_M1).
    /// </summary>
    public static readonly Choice[] VehicleArmour =
    {
        new(45, "45 - light vehicle: jeeps, cars, trucks (Willy)"),
        new(43, "43 - light vehicle, thinnest"),
        new(47, "47 - light vehicle, heaviest (light armoured cars)"),
        new(50, "50 - tank, thinnest (Sherman sides)"),
        new(51, "51 - tank (Sherman / Tiger / T-54 hull)"),
        new(52, "52 - tank, thicker (Sherman front)"),
        new(53, "53 - tank, heavy (Tiger / T-54)"),
        new(54, "54 - tank, heaviest (Tiger / T-54 front)"),
        new(55, "55 - ship, thinnest (landing craft, patrol boats)"),
        new(57, "57 - ship"),
        new(59, "59 - ship, heaviest (battleships)"),
        new(60, "60 - aircraft, thinnest"),
        new(61, "61 - aircraft"),
        new(62, "62 - aircraft, heaviest (bombers)"),
        new(66, "66 - carrier and battleship wooden decks"),
        new(37, "37 - wheels"),
        new(85, "85 - wreck"),
        new(-1, "Other (type the id)"),
    };

    /// <summary>The armour class a vehicle of this retail category starts from.</summary>
    public static int DefaultArmourFor(string vehicleCategory) => vehicleCategory.ToUpperInvariant() switch
    {
        "VCSEA" => 55,
        "VCAIR" => 61,
        _ => 45,
    };

    /// <summary>The rows as one NUL-separated string, the shape an ImGui combo takes.</summary>
    public static string ComboZ { get; } = string.Join("\0", Choices.Select(c => c.Name)) + "\0";

    /// <summary>The row for an id, or the "Other" row when the id is not listed.</summary>
    public static int IndexOf(int id)
    {
        int i = Array.FindIndex(Choices, c => c.Id == id);
        return i >= 0 ? i : Choices.Length - 1;
    }

    public static string NameOf(int id)
    {
        int i = Array.FindIndex(Choices, c => c.Id == id);
        return i >= 0 ? Choices[i].Name : id.ToString();
    }
}
