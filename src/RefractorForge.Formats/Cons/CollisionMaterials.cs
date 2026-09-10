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
        new(90,  "90 - hard surface: vehicle hulls, boxes, huts"),
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
