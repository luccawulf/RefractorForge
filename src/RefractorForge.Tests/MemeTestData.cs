using System.Buffers.Binary;
using System.Text;
using RefractorForge.Formats.Menu;
using RefractorForge.Formats.Rfa;

namespace RefractorForge.Tests;

/// <summary>Menu files for the Meme tests: the retail and community archives read in place, and small synthetic files
/// built from the schema (no install needed).</summary>
internal static class MemeTestData
{
    /// <summary>Every Meme entry of an archive, sniffed by magic (the files have no extension). An entry the archive
    /// cannot read is skipped and counted.</summary>
    public static IEnumerable<(string Entry, byte[] Data)> MemeEntries(RefractorFlatArchive a, Action<string>? unreadable = null)
    {
        foreach (var e in a.Entries)
        {
            byte[] head;
            try { head = a.ReadHead(e, 13); }
            catch (InvalidDataException ex) { unreadable?.Invoke($"{e.Name}: {ex.Message}"); continue; }
            if (MemeFile.IsMeme(head)) yield return (e.Name, a.Read(e));
        }
    }

    /// <summary>The same tree: class, name, kind and every value, compared through the dump (floats in their exact
    /// round-trip form).</summary>
    public static bool SameTree(MemeFile a, MemeFile b) => MemeDump.ToText(a) == MemeDump.ToText(b);

    /// <summary>
    /// A small HUD-like file with a bit of everything the format has: a Next-node chain, a TransformNode holding a
    /// PictureNode, a CullNode on a named BoolData, a TextNode with a style, a CullEventActionNode running an action
    /// list (a named IntData, a toggle of the same BoolData - a second, identical copy - and a SetPathAction whose path
    /// node is a by-name reference to a menu file), a ButtonEvent with its enums, and a WstringData.
    /// </summary>
    public static MemeFile Hud(MemeGame game)
    {
        MemeRecord N(string cls, string name = "", params (string, object?)[] v) => MemeRecord.New(game, cls, name, v);
        MemeRecord Show() => N("BoolData", "Game/ShowHud", ("Value <do not edit>", 1));

        var root = N("NameNode");
        var t = N("TransformNode", "", ("X", 390f), ("Y", 290f), ("Width", 20f), ("Height", 20f),
                  ("Transformed node", N("PictureNode", "", ("Picture", "menu/crosshair.tga"))));
        var cull = N("CullNode", "", ("Data", Show()), ("In time", 0.25f));
        var text = N("TextNode", "",
                     ("String", N("StringData", "Hud/Title", ("String <do not edit>", "Hello\r\nworld"))),
                     ("Style", N("CenterAlignedStyle", "", ("Font handle", "Font/Arial"))));
        var list = N("ActionListAction");
        var items = list.Field("Action list").Items!;
        items.Add(N("SetVariableAction", "", ("Variable", N("IntData", "Hud/Mode", ("Value <do not edit>", -1))),
                    ("Data", N("IntData", "", ("Value <do not edit>", 3)))));
        items.Add(N("ToggleVariableAction", "", ("Variable", Show())));
        items.Add(N("SetPathAction", "", ("Path node", MemeRecord.Ref("MainNavigationLayer")), ("In time", 0.5f)));
        var act = N("CullEventActionNode", "", ("Action", list),
                    ("Event", N("ButtonEvent", "", ("Event type", 1), ("Button type", 18))));
        var text2 = N("TextNode", "", ("String", N("WstringData", "Locale/Title", ("Wstring <do not edit>", "Caf\u00e9 \u4e2d\u6587"))));
        root.Next = t;
        t.Next = cull;
        cull.Next = text;
        text.Next = act;
        act.Next = text2;
        return new MemeFile(game, root);
    }

    /// <summary><see cref="Hud"/> written (a fresh first-use table) and read back.</summary>
    public static (MemeFile File, byte[] Bytes) HudRead(MemeGame game)
    {
        var bytes = Hud(game).ToBytes();
        return (MemeFile.Parse(bytes, game), bytes);
    }

    // ---- hand-built bytes ----------------------------------------------------------------------------------------

    public static byte[] Table(params string[] strings)
    {
        var b = new List<byte>();
        foreach (var s in strings.Prepend(MemeFile.Magic))
        {
            b.Add((byte)s.Length);
            b.AddRange(Encoding.Latin1.GetBytes(s));
        }
        b.Add(0);
        return b.ToArray();
    }

    public static byte[] Rec(int size, int ni, int ci)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)size);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), (ushort)ni);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), (ushort)ci);
        return b;
    }

    public static byte[] U16(int v) => new[] { (byte)v, (byte)(v >> 8) };

    public static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}
