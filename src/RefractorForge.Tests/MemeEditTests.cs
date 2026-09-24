using RefractorForge.Formats.Menu;
using Xunit;
using static RefractorForge.Tests.MemeTestData;

namespace RefractorForge.Tests;

/// <summary>
/// What a menu editor does with a <see cref="MemeFile"/>: edits that follow the format's two rules (same-named records
/// are one object; "Next node" is a sibling continuation, not a child), the editor-level checks the writer cannot make
/// (<see cref="MemeFile.Lint"/>), scripted edits through JSON, and the class table an editor offers
/// (<see cref="MemeSchema"/>).
/// </summary>
public class MemeEditTests
{
    /// <summary>Names are identity: the engine keeps the first definition of a name and skips every later copy's
    /// payload, so a copy edited alone is lost in game (DUPLICATE_DIFFERS); editing every copy is clean.</summary>
    [Fact]
    public void Same_named_copies_are_edited_together()
    {
        var (mf, bytes) = HudRead(MemeGame.Bfv);
        var copies = mf.CopiesOf("Game/ShowHud");
        Assert.Equal(2, copies.Count);
        Assert.Empty(mf.Lint());

        copies[1]["Value <do not edit>"] = 0;
        var lint = mf.Lint();
        Assert.Equal(MemeLintKind.DuplicateDiffers, lint.Single().Kind);
        Assert.Same(copies[1], lint[0].Record);
        Assert.Equal("DUPLICATE_DIFFERS", lint[0].Code);

        Assert.Equal(2, MemeEdits.SetEverywhere(mf, copies[0], "Value <do not edit>", 0));
        Assert.Empty(mf.Lint());
        var edited = mf.ToBytes();
        Assert.Equal(2, bytes.Zip(edited).Count(p => p.First != p.Second));    // one bool byte per copy
        Assert.All(MemeFile.Parse(edited, MemeGame.Bfv).CopiesOf("Game/ShowHud"), r => Assert.Equal((byte)0, r["Value <do not edit>"]));

        // a record value is copied into each copy, so the file stays a tree
        var withChild = mf.CopiesOf("Hud/Mode").Single();
        Assert.Equal(1, MemeEdits.SetEverywhere(mf, withChild, "Value <do not edit>", 5));
        Assert.Equal(2, MemeEdits.Rename(mf, copies[0], "Game/HudShown"));
        Assert.Empty(mf.CopiesOf("Game/ShowHud"));
        Assert.Equal(2, MemeFile.Parse(mf.ToBytes(), MemeGame.Bfv).CopiesOf("Game/HudShown").Count);
        Assert.Equal(1, MemeEdits.Rename(mf, copies[1], "Game/Other", everyCopy: false));
        Assert.Single(mf.CopiesOf("Game/HudShown"));
    }

    [Fact]
    public void Lint_finds_what_the_engine_reads_differently()
    {
        // a reference to a name this file defines only later: the engine looks for a menu FILE of that name. A
        // node's continuation is written before its other fields, so the LAST TextNode of the chain comes first in
        // the file, before the first TextNode's "Hud/Title"
        var (mf, _) = HudRead(MemeGame.Bf1942);
        mf.Walk().Last(r => r.Short == "TextNode")["String"] = MemeRecord.Ref("Hud/Title");
        Assert.Equal(new[] { MemeLintKind.RefBeforeDef }, mf.Lint().Select(f => f.Kind));

        // A/B and A\B are one registry path in game
        (mf, _) = HudRead(MemeGame.Bf1942);
        mf.Walk().Last(r => r.Short == "TextNode")["String"] =
            MemeRecord.New(MemeGame.Bf1942, "StringData", "Hud\\Title", ("String <do not edit>", "x"));
        Assert.Equal(new[] { MemeLintKind.PathAlias }, mf.Lint().Select(f => f.Kind));

        // nesting deeper than the limit: a long sibling chain is a stack-overflow risk in game
        (mf, _) = HudRead(MemeGame.Bf1942);
        Assert.Empty(mf.Lint());
        Assert.Equal(MemeLintKind.Depth, mf.Lint(maxDepth: 3).Single().Kind);
        var deep = MemeFile.Create(MemeGame.Bf1942);
        var at = deep.Root;
        for (int i = 0; i < 1001; i++)
        {
            var n = MemeRecord.New(MemeGame.Bf1942, "NameNode");
            at.Next = n;
            at = n;
        }
        var depth = deep.Lint().Single();
        Assert.Equal(MemeLintKind.Depth, depth.Kind);
        Assert.Contains("1002 levels", depth.Text);                          // 1001 nodes and the null that ends them
        var back = MemeFile.Parse(deep.ToBytes(), MemeGame.Bf1942);         // deep, but written and read
        Assert.Equal(1003, back.Stats.Records);
    }

    /// <summary>A node goes into a chain after another, drawn after it in the same parent rect; unlinking it keeps the
    /// rest of the chain where it was.</summary>
    [Fact]
    public void Nodes_are_spliced_into_and_out_of_their_chains()
    {
        var (mf, bytes) = HudRead(MemeGame.Bfv);
        var t = mf.Walk().First(r => r.Short == "TransformNode");
        var cull = t.Next!;

        var a = MemeRecord.New(MemeGame.Bfv, "PictureNode", "", ("Picture", "a.tga"));
        var b = MemeRecord.New(MemeGame.Bfv, "PictureNode", "", ("Picture", "b.tga"));
        a.Next = b;                                                          // a chain of two goes in whole
        MemeEdits.InsertAfter(t, a);
        Assert.Same(a, t.Next);
        Assert.Same(cull, b.Next);
        Assert.True(MemeEdits.Unlink(mf, a));
        Assert.Same(b, t.Next);
        Assert.Equal(MemeRecordKind.Null, a.Next!.Kind);
        Assert.True(MemeEdits.Unlink(mf, b));
        Assert.Equal(bytes, mf.ToBytes());
        Assert.False(MemeEdits.Unlink(mf, b));                               // no longer in the file

        MemeEdits.InsertFirst(t, "Transformed node", a);                     // at the head of the transformed chain
        Assert.Equal("menu/crosshair.tga", a.Next!["Picture"]);
        var slot = MemeEdits.FindSlot(mf, a)!;
        Assert.Same(t, slot.Parent);
        Assert.Equal("Transformed node", slot.Field.Label);
        Assert.True(MemeEdits.Unlink(mf, a));
        Assert.Equal(bytes, mf.ToBytes());

        Assert.Throws<InvalidOperationException>(() => MemeEdits.InsertAfter(t, t));
        Assert.Throws<InvalidOperationException>(() => MemeEdits.InsertAfter(t, MemeRecord.New(MemeGame.Bfv, "IntData")));
        Assert.Throws<InvalidOperationException>(() => MemeEdits.InsertAfter(MemeRecord.New(MemeGame.Bfv, "IntData"), a));
    }

    [Fact]
    public void List_items_and_slots_are_replaced_inserted_and_removed()
    {
        var (mf, bytes) = HudRead(MemeGame.Bf1942);
        var list = mf.Walk().First(r => r.Short == "ActionListAction");
        var extra = MemeRecord.New(MemeGame.Bf1942, "RemoveEventAction");
        MemeEdits.InsertListItem(list, "Action list", 1, extra);
        var back = MemeFile.Parse(mf.ToBytes(), MemeGame.Bf1942);
        Assert.Equal(new[] { "SetVariableAction", "RemoveEventAction", "ToggleVariableAction", "SetPathAction" },
                     back.Walk().First(r => r.Short == "ActionListAction").Field("Action list").Items!.Select(r => r.Short));
        Assert.Same(extra, MemeEdits.RemoveListItem(list, "Action list", 1));
        Assert.Equal(bytes, mf.ToBytes());

        var slot = MemeEdits.FindSlot(mf, list.Field("Action list").Items![2])!;
        Assert.True(slot.IsListItem);
        Assert.Equal(2, slot.Index);
        // an unlinked list item that continues nothing is removed from the list
        Assert.True(MemeEdits.Unlink(mf, list.Field("Action list").Items![2]));
        Assert.Equal(2, list.Field("Action list").Items!.Count);

        var cull = mf.Walk().First(r => r.Short == "CullNode");
        var old = MemeEdits.ReplaceSlot(cull, "Data", MemeRecord.Ref("Game/ShowHud"));
        Assert.Equal("Game/ShowHud", old.Name);
        Assert.Equal(MemeRecordKind.Ref, MemeFile.Parse(mf.ToBytes(), MemeGame.Bf1942).Walk().First(r => r.Short == "CullNode").Get<MemeRecord>("Data").Kind);
        Assert.Throws<ArgumentException>(() => MemeEdits.ReplaceSlot(cull, "In time", null));
        Assert.Throws<ArgumentException>(() => MemeEdits.InsertListItem(cull, "Data", 0, extra));
        Assert.Throws<KeyNotFoundException>(() => cull["No such field"]);
    }

    [Fact]
    public void A_deep_copy_is_a_separate_subtree_that_writes_the_same()
    {
        var (mf, _) = HudRead(MemeGame.Bfv);
        var act = mf.Walk().First(r => r.Short == "CullEventActionNode");
        var copy = MemeEdits.DeepCopy(act);
        Assert.NotSame(act.Get<MemeRecord>("Action"), copy.Get<MemeRecord>("Action"));
        copy.Get<MemeRecord>("Event")["Button type"] = 5;
        Assert.Equal(18, act.Get<MemeRecord>("Event")["Button type"]);
        copy.Next = null;
        MemeEdits.InsertAfter(act, copy);                                    // pasted right after the original
        var back = MemeFile.Parse(mf.ToBytes(), MemeGame.Bfv);
        Assert.Equal(2, back.Walk().Count(r => r.Short == "CullEventActionNode"));
        Assert.Equal(3, back.CopiesOf("Game/ShowHud").Count);                 // copies of named objects stay copies
        Assert.Empty(back.Lint());
    }

    /// <summary>BfListBoxData is the one class whose labels differ between the games ("Create new action" in BF1942,
    /// "Create dice_new action" in BFV): either spelling finds the field in either game.</summary>
    [Fact]
    public void The_list_box_data_labels_of_both_games_name_the_same_fields()
    {
        var bfv = MemeRecord.New(MemeGame.Bfv, "BfListBoxData");
        var bf42 = MemeRecord.New(MemeGame.Bf1942, "BfListBoxData");
        Assert.Equal("Create dice_new action", bfv.Fields![0].Label);
        Assert.Equal("Create new action", bf42.Fields![0].Label);
        foreach (var r in new[] { bfv, bf42 })
        {
            Assert.Same(r.Field("Create new action"), r.Field("Create dice_new action"));
            Assert.Same(r.Field("Create new string"), r.Field("Create dice_new string"));
            r["Create dice_new action"] = MemeRecord.New(MemeGame.Bfv, "RemoveEventAction");
            Assert.Equal("RemoveEventAction", r.Get<MemeRecord>("Create new action").Short);
        }
        Assert.Equal(bfv.Class!.Field("Create new string"), bfv.Class.Fields[1]);

        // a BF1942 BfListBoxData pasted into a BFV file is written (the bytes are the same), and JSON takes either label
        var mf = MemeFile.Create(MemeGame.Bfv);
        var list = MemeRecord.New(MemeGame.Bfv, "BfNewListBoxNode", "", ("Listbox data", bf42));
        mf.Root.Next = list;
        var back = MemeFile.Parse(mf.ToBytes(), MemeGame.Bfv);
        Assert.Equal("Create dice_new action", back.Walk().First(r => r.Short == "BfListBoxData").Fields![0].Label);
        var json = MemeJson.ToJson(back).Replace("Create dice_new string", "Create new string");
        Assert.Equal(mf.ToBytes(), MemeJson.FromJson(json).ToBytes());
    }

    [Fact]
    public void Json_carries_a_file_losslessly_and_takes_scripted_edits()
    {
        var (mf, bytes) = HudRead(MemeGame.Bfv);
        var json = MemeJson.ToJson(mf);
        Assert.Contains("\"X\": 390", json);
        Assert.Equal(bytes, MemeJson.FromJson(json, MemeGame.Bfv).ToBytes());
        Assert.Equal(bytes, MemeJson.FromJson(MemeJson.ToJson(mf, indented: false)).ToBytes());

        var edited = MemeJson.FromJson(json.Replace("\"X\": 390", "\"X\": 400.5").Replace("menu/crosshair.tga", "menu/dot.tga"));
        var back = MemeFile.Parse(edited.ToBytes(), MemeGame.Bfv);
        var t = back.Walk().First(r => r.Short == "TransformNode");
        Assert.Equal(400.5f, t["X"]);
        Assert.Equal("menu/dot.tga", t.Get<MemeRecord>("Transformed node")["Picture"]);
        Assert.Equal("first-use", back.TableRule);

        // a field left out gets its default; an unknown label, class or game is refused with the JSON path
        var partial = MemeJson.FromJson(json.Replace("\"In time\": 0.25,", ""));
        Assert.Equal(0f, partial.Walk().First(r => r.Short == "CullNode")["In time"]);
        var e = Assert.Throws<InvalidDataException>(() => MemeJson.FromJson(json.Replace("\"In time\"", "\"In tim\"")));
        Assert.Contains("root.Next node.Next node", e.Message);
        Assert.Throws<InvalidDataException>(() => MemeJson.FromJson(json.Replace("dice::meme::CullNode", "dice::meme::CullNod")));
        Assert.Throws<InvalidDataException>(() => MemeJson.FromJson(json, MemeGame.Bf1942));
        Assert.Throws<InvalidDataException>(() => MemeJson.FromJson(json.Replace("\"X\": 390", "\"X\": \"wide\"")));
        Assert.Throws<InvalidDataException>(() => MemeJson.FromJson(json.Replace("\"Picture\": \"menu/crosshair.tga\"", "\"Picture\": 7")));
        Assert.Throws<InvalidDataException>(() => MemeJson.FromJson(json.Replace("\"ci\": 1", "\"ci\": \"one\"")));
        Assert.Throws<InvalidDataException>(() => MemeJson.FromJson("{ not json"));
    }

    [Fact]
    public void The_schema_is_the_two_engines_class_tables()
    {
        var bf42 = MemeSchema.For(MemeGame.Bf1942);
        var bfv = MemeSchema.For(MemeGame.Bfv);
        Assert.Equal(194, bf42.Classes.Count);
        Assert.Equal(199, bfv.Classes.Count);
        Assert.Equal(new[] { "BfControlPointGauge", "BfLocalePictureButtonNode", "BfNewCreditsListNode", "BfPictureButtonNode", "BfTriangleButtonsNode" },
                     bfv.Classes.Select(c => c.Name).Except(bf42.Classes.Select(c => c.Name)).OrderBy(n => n, StringComparer.Ordinal));
        // 97 BF1942 and 98 BFV entries come from the engines alone; four BFV classes are proven by BF1942 files
        Assert.Equal(97, bf42.Classes.Count(c => c.ProvenByData));
        Assert.Equal(101, bfv.Classes.Count(c => c.ProvenByData));
        foreach (var s in new[] { bf42, bfv })
        {
            Assert.Equal(new[] { "CullActionNode", "SetVariableSinusAction", "ShowEffectNode" }, s.Classes.Where(c => c.Deprecated).Select(c => c.Name));
            Assert.Equal(new[] { "Ref2System", "System" }, s.Classes.Where(c => !c.IsFileObject).Select(c => c.Name));
            Assert.Equal("dice::ref2::menu::Ref2System", s["Ref2System"]!.FullName);
            Assert.Equal(1, s["BfVerticalScrollExNode"]!.Field("Version <do not edit>")!.MustEqual);
            Assert.Equal(MemeHandle.Texture, s["PictureNode"]!.Field("Picture")!.Handle);
            Assert.Equal(MemeHandle.Font, s["CenterAlignedStyle"]!.Field("Font handle")!.Handle);
            Assert.Equal(MemeHandle.Sound, s["SoundAction"]!.Field("Sound")!.Handle);
            Assert.True(s["TransformNode"]!.NextNode!.IsNextNode);
            Assert.Equal("Node", s["TransformNode"]!.Field("Transformed node")!.Expects);
            Assert.Null(s["BfGaugeNode"]!.NextNode);                            // its serialize does not chain to Node's
            Assert.True(s["BfNavigationButtonNode"]!.IsA("BfButtonNode"));
            Assert.True(s["BfNavigationButtonNode"]!.IsNode);
            Assert.False(s["IntData"]!.IsNode);
            Assert.True(s["IntData"]!.IsA("Data"));
            Assert.Equal("Enter", s["ButtonEvent"]!.Field("Button type")!.EnumNames![18]);
            Assert.Equal("PreShow", s["TypeEvent"]!.Field("Event type")!.EnumNames![13]);
            Assert.Equal(new[] { "Description", "Action" }, s["NavigateTree"]!.TreeNodeFields!.Select(f => f.Label));
            Assert.Empty(s["Tree"]!.TreeNodeFields!);
            Assert.Null(s["NavigateTree"]!.SerializeVa);
            Assert.All(s.Classes.Where(c => c.IsNode && c.Name != "BfGaugeNode" && c.Name != "Node"), c => Assert.NotNull(c.NextNode));
        }
        Assert.Equal(0x007EAF90u, bf42["PictureNode"]!.SerializeVa);
        Assert.Equal(0x008737E0u, bfv["PictureNode"]!.SerializeVa);
        Assert.Equal("Create new action", bf42["BfListBoxData"]!.Fields[0].Label);
    }
}
