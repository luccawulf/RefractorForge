using System.Buffers.Binary;
using RefractorForge.Formats.Menu;
using Xunit;
using static RefractorForge.Tests.MemeTestData;

namespace RefractorForge.Tests;

/// <summary>
/// The Meme 2.0 reader and writer (<see cref="MemeFile"/>) on files built here, no install needed: what a byte-exact
/// writer must keep (the string table as read, each header's string index, dead bytes, NaN payloads, lone surrogates),
/// what a damaged file does (kept opaque, never a failure), the engine's two less common grammars (the Tree node
/// grammar, the old table-less format), and every model the writer refuses because the engine would read it as
/// something else (docs/Meme_Format.md section 8) - each refusal an <see cref="InvalidOperationException"/> naming the
/// record's path, each ported from the reference's own tests.
/// </summary>
public class MemeFileTests
{
    [Theory]
    [InlineData(MemeGame.Bf1942)]
    [InlineData(MemeGame.Bfv)]
    public void A_new_file_is_written_first_use_and_reads_back_the_same(MemeGame game)
    {
        var (mf, bytes) = HudRead(game);
        Assert.Equal("first-use", mf.TableRule);
        Assert.True(mf.IsFullyDecoded);
        Assert.Empty(mf.Warnings);
        Assert.True(SameTree(Hud(game), mf));
        Assert.Equal(bytes, mf.ToBytes());
        Assert.Equal(bytes, mf.ToBytes(MemeTableMode.Keep));
        Assert.Equal(bytes, mf.ToBytes(MemeTableMode.Rebuild));
        Assert.Equal("dice::meme::NameNode", mf.Strings![1]);                 // the root class first
        Assert.Equal(mf.FirstUseStrings(), mf.Strings.Skip(1));

        var created = MemeFile.Create(game);
        var t = MemeRecord.New(game, "TransformNode", "", ("X", 390f), ("Y", 290f), ("Width", 20f), ("Height", 20f));
        t["Transformed node"] = MemeRecord.New(game, "PictureNode", "", ("Picture", "menu/a.tga"));
        created.Root.Next = t;
        var back = MemeFile.Parse(created.ToBytes(), game);
        Assert.Equal("first-use", back.TableRule);
        Assert.True(back.IsFullyDecoded);
        Assert.True(SameTree(created, back));
        Assert.Equal("NameNode", back.Root.Short);
        Assert.Equal("", back.Root.Name);
    }

    [Fact]
    public void A_value_edit_changes_only_its_bytes_and_undoing_it_is_byte_exact()
    {
        var (mf, bytes) = HudRead(MemeGame.Bfv);
        var t = mf.Walk().First(r => r.Short == "TransformNode");
        t["X"] = 402.5f;
        var edited = mf.ToBytes();
        Assert.Equal(bytes.Length, edited.Length);
        Assert.InRange(bytes.Zip(edited).Count(p => p.First != p.Second), 1, 4);
        Assert.Equal(402.5f, MemeFile.Parse(edited, MemeGame.Bfv).Walk().First(r => r.Short == "TransformNode").Get<float>("X"));
        t["X"] = 390f;
        Assert.Equal(bytes, mf.ToBytes());
    }

    [Fact]
    public void A_rename_to_a_new_string_writes_a_first_use_table_or_appends_it_when_kept()
    {
        var (mf, bytes) = HudRead(MemeGame.Bf1942);
        var title = mf.CopiesOf("Hud/Title").Single();
        title.Name = "Hud/Heading";
        var auto = MemeFile.Parse(mf.ToBytes(), MemeGame.Bf1942);
        Assert.Equal("first-use", auto.TableRule);
        Assert.DoesNotContain("Hud/Title", auto.Strings!);
        Assert.True(SameTree(mf, auto));
        var kept = MemeFile.Parse(mf.ToBytes(MemeTableMode.Keep), MemeGame.Bf1942);
        Assert.Equal("Hud/Heading", kept.Strings![^1]);
        Assert.Equal(mf.Strings, kept.Strings.Take(kept.Strings.Count - 1));
        Assert.StartsWith("other order", kept.TableRule);                    // the old name stays, unused, as MenuEditor leaves it
        Assert.True(SameTree(mf, kept));
        title.Name = "Hud/Title";
        Assert.Equal(bytes, mf.ToBytes());
    }

    [Fact]
    public void An_inserted_transform_node_adds_exactly_32_bytes_and_a_deleted_subtree_keeps_the_table()
    {
        var (mf, bytes) = HudRead(MemeGame.Bfv);
        var t = mf.Walk().First(r => r.Short == "TransformNode");
        var added = MemeRecord.New(MemeGame.Bfv, "TransformNode", "", ("X", 10f), ("Y", 20f), ("Width", 100f), ("Height", 50f));
        MemeEdits.InsertAfter(t, added);
        var inserted = mf.ToBytes();
        Assert.Equal(bytes.Length + 8 + 16 + 8, inserted.Length);           // header, four floats, a null Transformed node
        var back = MemeFile.Parse(inserted, MemeGame.Bfv);
        Assert.True(SameTree(mf, back));
        Assert.Equal("TransformNode", back.Walk().First(r => r.Short == "TransformNode").Next!.Short);
        Assert.True(MemeEdits.Unlink(mf, added));
        Assert.Equal(bytes, mf.ToBytes());

        var act = mf.Walk().First(r => r.Short == "CullEventActionNode");
        var saved = MemeEdits.ReplaceSlot(act, "Action", null);
        var auto = MemeFile.Parse(mf.ToBytes(), MemeGame.Bfv);
        var rebuilt = MemeFile.Parse(mf.ToBytes(MemeTableMode.Rebuild), MemeGame.Bfv);
        Assert.Equal(mf.Strings, auto.Strings);                            // every string it needs is there: kept
        Assert.Equal("first-use", rebuilt.TableRule);
        Assert.DoesNotContain("Hud/Mode", rebuilt.Strings!);
        Assert.True(rebuilt.Strings!.Count < auto.Strings!.Count);
        MemeEdits.ReplaceSlot(act, "Action", saved);
        Assert.Equal(bytes, mf.ToBytes());
    }

    /// <summary>ClassIStream's <c>table[512]</c>: 511 strings after the magic is the most a file may hold.</summary>
    [Fact]
    public void The_string_table_holds_511_strings_and_not_512()
    {
        static MemeFile Chain(int named)
        {
            var mf = MemeFile.Create(MemeGame.Bfv);
            var at = mf.Root;
            for (int i = 0; i < named; i++)
            {
                var n = MemeRecord.New(MemeGame.Bfv, "NameNode", $"n{i}");
                at.Next = n;
                at = n;
            }
            return mf;
        }
        var ok = MemeFile.Parse(Chain(510).ToBytes(), MemeGame.Bfv);        // the class + 510 names
        Assert.Equal(511, ok.Strings!.Count - 1);
        Assert.True(ok.IsFullyDecoded);
        var e = Assert.Throws<InvalidOperationException>(() => Chain(511).ToBytes());
        Assert.Contains("511", e.Message);
        // a kept table that would pass 511 is rebuilt when that fits, refused when it does not
        ok.Walk().Last(r => r.Name.Length > 0).Name = "renamed";
        Assert.Equal(511, MemeFile.Parse(ok.ToBytes(), MemeGame.Bfv).Strings!.Count - 1);
        Assert.Throws<InvalidOperationException>(() => ok.ToBytes(MemeTableMode.Keep));
    }

    [Fact]
    public void Names_are_refused_where_the_engine_buffers_end()
    {
        var (mf, _) = HudRead(MemeGame.Bfv);
        var title = mf.CopiesOf("Hud/Title").Single();
        title.Name = new string('n', 127);
        Assert.Equal(title.Name, MemeFile.Parse(mf.ToBytes(), MemeGame.Bfv).CopiesOf(title.Name).Single().Name);
        title.Name = new string('n', 128);                                   // the reader's 0x80-byte buffer
        var e = Assert.Throws<InvalidOperationException>(() => mf.ToBytes());
        Assert.Contains("127", e.Message);
        Assert.Contains("root > Next node", e.Message);                      // the path to the record
        title.Name = "Hud/Title";

        mf.Root.ClassName = "dice::meme::" + new string('N', 60);           // the root class too: a 0x40-byte buffer
        Assert.Contains("63", Assert.Throws<InvalidOperationException>(() => mf.ToBytes()).Message);
        mf.Root.ClassName = "dice::meme::NameNode";

        title.Name = "Hud/\u0100";                                           // not latin1
        Assert.Throws<InvalidOperationException>(() => mf.ToBytes());
        Assert.Throws<ArgumentException>(() => MemeEdits.Rename(mf, title, new string('x', 128)));
    }

    [Fact]
    public void A_nul_is_refused_in_every_string_the_engine_keeps_as_a_c_string()
    {
        var (mf, _) = HudRead(MemeGame.Bfv);
        var pic = mf.Walk().First(r => r.Short == "PictureNode");
        var str = mf.CopiesOf("Hud/Title").Single();
        var wstr = mf.CopiesOf("Locale/Title").Single();
        Assert.Throws<ArgumentException>(() => pic["Picture"] = "menu/a\0b.tga");
        Assert.Throws<ArgumentException>(() => str["String <do not edit>"] = "a\0b");
        Assert.Throws<ArgumentException>(() => wstr["Wstring <do not edit>"] = "a\0b");
        foreach (var (rec, label) in new[] { (pic, "Picture"), (str, "String <do not edit>"), (wstr, "Wstring <do not edit>") })
        {
            var f = rec.Field(label);
            var was = f.Value;
            f.Value = "a\0b";                                                // assigned raw: the writer refuses it
            Assert.Contains("NUL", Assert.Throws<InvalidOperationException>(() => mf.ToBytes()).Message);
            f.Value = was;
        }
        str.Name = "a\0b";
        Assert.Contains("NUL", Assert.Throws<InvalidOperationException>(() => mf.ToBytes()).Message);
    }

    /// <summary>The reader ends a LIST at the first null record (engine 0x0087A6F0), so a null item would end the list
    /// early and shift every later field.</summary>
    [Fact]
    public void A_null_record_inside_a_list_is_refused()
    {
        var (mf, _) = HudRead(MemeGame.Bf1942);
        var list = mf.Walk().First(r => r.Short == "ActionListAction");
        var items = list.Field("Action list").Items!;
        items.Insert(1, MemeRecord.Null());
        var e = Assert.Throws<InvalidOperationException>(() => mf.ToBytes());
        Assert.Contains("Action list[1]", e.Message);
        items.RemoveAt(1);
        items.Insert(1, null!);
        Assert.Throws<InvalidOperationException>(() => mf.ToBytes());
        items.RemoveAt(1);
        Assert.Throws<ArgumentException>(() => MemeEdits.InsertListItem(list, "Action list", 0, MemeRecord.Null()));
        Assert.Throws<ArgumentException>(() => list["Action list"] = new List<MemeRecord> { MemeRecord.Null() });
    }

    [Fact]
    public void Byte_and_int_fields_take_what_fits_and_nothing_else()
    {
        var (mf, _) = HudRead(MemeGame.Bfv);
        var ev = mf.Walk().First(r => r.Short == "ButtonEvent");
        var index = ev.Field("Input index (0 == all indexes)");
        Assert.Throws<ArgumentException>(() => index.Set(256));
        Assert.Throws<ArgumentException>(() => index.Set(-1));
        Assert.Throws<ArgumentException>(() => index.Set(1.7));             // it used to become 1
        foreach (var bad in new object[] { 256, -1, 1.7, "1" })
        {
            index.Value = bad;
            Assert.Throws<InvalidOperationException>(() => mf.ToBytes());
        }
        index.Set(255);
        Assert.Equal((byte)255, MemeFile.Parse(mf.ToBytes(), MemeGame.Bfv).Walk().First(r => r.Short == "ButtonEvent")["Input index (0 == all indexes)"]);
        index.Set(0);

        var type = ev.Field("Button type");
        type.Set(0x80000000L);                                               // 2^31..2^32-1: the same 32 bits, read back negative
        Assert.Equal(int.MinValue, MemeFile.Parse(mf.ToBytes(), MemeGame.Bfv).Walk().First(r => r.Short == "ButtonEvent").Get<int>("Button type"));
        Assert.Throws<ArgumentException>(() => type.Set(0x100000000L));
        type.Value = 0x100000000L;
        Assert.Throws<InvalidOperationException>(() => mf.ToBytes());
        type.Set(18);

        var t = mf.Walk().First(r => r.Short == "TransformNode");
        Assert.Throws<ArgumentException>(() => t.Set("X", 1e39));            // does not fit an f32
        Assert.Throws<ArgumentException>(() => t.Set("X", "390"));
        t.Set("X", 390);                                                     // an int is a number
        Assert.Equal(390f, t["X"]);
    }

    /// <summary>A bool is one byte and the engine reads any non-zero byte as true (BFV 0x00878E00): a 2..255 is kept as
    /// read, written as is, and reported - by the reader as implausible, by lint as BOOL_NOT_01.</summary>
    [Fact]
    public void A_bool_of_2_to_255_round_trips_and_is_reported()
    {
        var (mf, _) = HudRead(MemeGame.Bf1942);
        MemeEdits.SetEverywhere(mf, mf.CopiesOf("Game/ShowHud")[0], "Value <do not edit>", 7);
        var bytes = mf.ToBytes();
        var back = MemeFile.Parse(bytes, MemeGame.Bf1942);
        Assert.All(back.CopiesOf("Game/ShowHud"), r => Assert.Equal((byte)7, r["Value <do not edit>"]));
        Assert.Equal(2, back.Warnings.Count(w => w.Kind == MemeWarningKind.Implausible));
        Assert.Equal(new[] { MemeLintKind.BoolNot01, MemeLintKind.BoolNot01 }, back.Lint().Select(f => f.Kind));
        Assert.Equal(bytes, back.ToBytes());
    }

    [Fact]
    public void A_record_that_is_its_own_descendant_is_refused_not_looped_on()
    {
        var (mf, _) = HudRead(MemeGame.Bfv);
        var t = mf.Walk().First(r => r.Short == "TransformNode");
        var next = t.Next;
        t.Next = t;
        Assert.Contains("cycle", Assert.Throws<InvalidOperationException>(() => mf.ToBytes()).Message);
        Assert.Throws<InvalidOperationException>(() => mf.Walk().ToList());
        Assert.Throws<InvalidOperationException>(() => mf.Lint());
        Assert.Throws<InvalidOperationException>(() => t.DeepClone());
        t.Next = next;
        // a record in two slots (a DAG) is fine: written as two inline copies, as DICE writes a shared object
        var pic = t["Transformed node"] as MemeRecord;
        mf.Walk().First(r => r.Short == "CullNode").Next!.Next = pic;
        var back = MemeFile.Parse(mf.ToBytes(), MemeGame.Bfv);
        Assert.Equal(2, back.Walk().Count(r => r.Short == "PictureNode"));
    }

    [Fact]
    public void Hand_built_records_the_engine_would_misread_are_refused()
    {
        var (mf, _) = HudRead(MemeGame.Bfv);
        var t = mf.Walk().First(r => r.Short == "TransformNode");

        t["Transformed node"] = new MemeRecord("dice::meme::PictureNode", "x");  // a header with no payload
        Assert.Contains("neither fields nor a raw payload", Assert.Throws<InvalidOperationException>(() => mf.ToBytes()).Message);

        var classless = MemeRecord.New(MemeGame.Bfv, "PictureNode", "x");
        classless.ClassName = null;                                          // it would become a reference + dead bytes
        t["Transformed node"] = classless;
        Assert.Contains("no class", Assert.Throws<InvalidOperationException>(() => mf.ToBytes()).Message);

        var retyped = MemeRecord.New(MemeGame.Bfv, "TransformNode");
        retyped.Fields![1] = new MemeField(MemeToken.S8, "X", "abc");         // would be read back as other values
        t["Transformed node"] = retyped;
        Assert.Contains("do not match", Assert.Throws<InvalidOperationException>(() => mf.ToBytes()).Message);

        var dropped = MemeRecord.New(MemeGame.Bfv, "TransformNode");
        dropped.Fields!.RemoveAt(2);
        t["Transformed node"] = dropped;
        Assert.Throws<InvalidOperationException>(() => mf.ToBytes());

        var foreign = MemeRecord.New(MemeGame.Bfv, "PictureNode");
        foreign.ClassName = "x::PictureNode";                                // the engine cannot resolve it
        t["Transformed node"] = foreign;
        Assert.Contains("not a Bfv menu class", Assert.Throws<InvalidOperationException>(() => mf.ToBytes()).Message);
    }

    /// <summary>BfVerticalScrollExNode's Version must be 1: any other value fails the read (BF1942 0x007D8532, BFV
    /// 0x0055A492). New() fills engine constraints, so a fresh one is right.</summary>
    [Theory]
    [InlineData(MemeGame.Bf1942)]
    [InlineData(MemeGame.Bfv)]
    public void A_vertical_scroll_ex_node_is_version_1_and_nothing_else(MemeGame game)
    {
        var (mf, _) = HudRead(game);
        var v = MemeRecord.New(game, "BfVerticalScrollExNode");
        Assert.Equal(1, v["Version <do not edit>"]);
        mf.Walk().First(r => r.Short == "TransformNode")["Transformed node"] = v;
        var back = MemeFile.Parse(mf.ToBytes(), game);
        Assert.Equal(1, back.Walk().First(r => r.Short == "BfVerticalScrollExNode")["Version <do not edit>"]);
        v["Version <do not edit>"] = 0;
        Assert.Contains("fails the read", Assert.Throws<InvalidOperationException>(() => mf.ToBytes()).Message);
    }

    [Fact]
    public void A_bfv_only_class_in_a_bf1942_file_is_refused()
    {
        var (mf, _) = HudRead(MemeGame.Bf1942);
        Assert.Throws<ArgumentException>(() => MemeRecord.New(MemeGame.Bf1942, "BfPictureButtonNode"));
        mf.Walk().First(r => r.Short == "TransformNode")["Transformed node"] = MemeRecord.New(MemeGame.Bfv, "BfPictureButtonNode");
        var e = Assert.Throws<InvalidOperationException>(() => mf.ToBytes());
        Assert.Contains("not a Bf1942 menu class", e.Message);
        Assert.Contains("root > Next node > Transformed node", e.Message);
    }

    /// <summary>The class lookup (BF1942 0x007EDEA0, BFV 0x008753F0) takes the full registered name, then the bare
    /// class name - but not a foreign prefix. The reader follows it: a bare name decodes, <c>x::PictureNode</c> stays
    /// opaque and is written back byte-exact.</summary>
    [Fact]
    public void Class_strings_resolve_the_way_the_engine_resolves_them()
    {
        var bfv = MemeSchema.For(MemeGame.Bfv);
        Assert.Equal("PictureNode", bfv.Find("dice::meme::PictureNode")?.Name);
        Assert.Equal("PictureNode", bfv.Find("PictureNode")?.Name);
        Assert.Null(bfv.Find("x::PictureNode"));
        Assert.Null(bfv.Find("meme::PictureNode"));
        Assert.Null(bfv.Find("dice::meme::Picturenode"));
        Assert.Null(bfv.Find(""));
        Assert.Equal("Ref2System", bfv.Find("dice::ref2::menu::Ref2System")?.Name);
        Assert.Null(bfv.Find("dice::meme::Ref2System"));
        Assert.NotNull(bfv.Find("BfPictureButtonNode"));
        Assert.Null(MemeSchema.For(MemeGame.Bf1942).Find("BfPictureButtonNode"));

        var root = MemeRecord.New(MemeGame.Bfv, "NameNode");
        var pic = MemeRecord.New(MemeGame.Bfv, "PictureNode", "", ("Picture", "menu/a.tga"));
        pic.ClassName = "PictureNode";
        root.Next = pic;
        var bytes = new MemeFile(MemeGame.Bfv, root).ToBytes();
        var back = MemeFile.Parse(bytes, MemeGame.Bfv);
        Assert.Equal(MemeRecordKind.Object, back.Root.Next!.Kind);
        Assert.Equal("PictureNode", back.Root.Next.ClassName);

        var odd = bytes.ToArray();
        int at = odd.AsSpan().IndexOf("\u000BPictureNode"u8);
        Assert.True(at > 0);
        var patched = Cat(odd[..at], new byte[] { 14 }, "x::PictureNode"u8.ToArray(), odd[(at + 12)..]);
        var opaque = MemeFile.Parse(patched, MemeGame.Bfv);
        Assert.Equal(MemeRecordKind.Opaque, opaque.Root.Next!.Kind);
        Assert.Equal(patched, opaque.ToBytes());
    }

    /// <summary>The two damaged community files, rebuilt in miniature: a record whose payload does not fit its layout is
    /// kept opaque, a null record longer than its header keeps its dead bytes; both come back byte-exact, and neither
    /// file can have its table rebuilt or be converted to the old format (their raw bytes hold table indices).</summary>
    [Fact]
    public void Damaged_records_are_kept_whole_and_written_back_byte_exact()
    {
        // a PictureNode whose s8 says 12 characters with only 4 left in its 21-byte record (FW1918's testi)
        var table = Table("dice::meme::NameNode", "dice::meme::PictureNode");
        var pic = Cat(Rec(21, 0, 2), Rec(8, 0, 0), new byte[] { 12 }, "menu"u8.ToArray());
        var bad = Cat(table, U16(1), pic);
        var mf = MemeFile.Parse(bad, MemeGame.Bf1942);
        Assert.Equal(MemeRecordKind.Opaque, mf.Root.Next!.Kind);
        Assert.Contains("overrun", mf.Root.Next.Note);
        Assert.Equal(13, mf.Root.Next.Raw!.Length);
        Assert.Equal((1, 13), (mf.Stats.Opaque, mf.Stats.OpaqueBytes));
        Assert.False(mf.IsFullyDecoded);
        Assert.Equal("unknown (opaque payloads)", mf.TableRule);
        Assert.Equal(bad, mf.ToBytes());
        Assert.Equal(bad, MemeJson.FromJson(MemeJson.ToJson(mf)).ToBytes());
        Assert.Throws<InvalidOperationException>(() => mf.ToBytes(MemeTableMode.Rebuild));
        Assert.Throws<InvalidOperationException>(() => mf.ToOldFormat());

        // the root's Next node a null record of size 16 (wasteland2042's SingleplayerNavigation: 86)
        var dead = Cat(Table("dice::meme::NameNode"), U16(1), Rec(16, 0, 0), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        var md = MemeFile.Parse(dead, MemeGame.Bfv);
        Assert.Equal(MemeRecordKind.Null, md.Root.Next!.Kind);
        Assert.Equal(8, md.Stats.DeadBytes);
        Assert.Equal(MemeWarningKind.ClasslessSize, md.Warnings.Single().Kind);
        Assert.Equal(dead, md.ToBytes());
        Assert.Equal(dead, MemeJson.FromJson(MemeJson.ToJson(md)).ToBytes());
        Assert.Throws<InvalidOperationException>(() => md.ToBytes(MemeTableMode.Rebuild));

        // a class this game does not register: opaque, verbatim
        var foreign = Cat(Table("dice::meme::NameNode", "dice::meme::BfTriangleButtonsNode"), U16(1), Rec(11, 0, 2), new byte[] { 9, 9, 9 });
        var mfor = MemeFile.Parse(foreign, MemeGame.Bf1942);
        Assert.Equal(MemeRecordKind.Opaque, mfor.Root.Next!.Kind);
        Assert.Contains("not registered in Bf1942", mfor.Root.Next.Note);
        Assert.Equal(foreign, mfor.ToBytes());

        // a file cut short: the root itself is kept opaque, nothing throws
        var (_, whole) = HudRead(MemeGame.Bfv);
        var cut = whole[..^40];
        var mc = MemeFile.Parse(cut, MemeGame.Bfv);
        Assert.Equal(MemeRecordKind.Opaque, mc.Root.Kind);
        Assert.Equal(cut, mc.ToBytes());
    }

    [Fact]
    public void What_is_not_a_menu_file_is_refused_as_invalid_data()
    {
        Assert.Throws<InvalidDataException>(() => MemeFile.Parse(Array.Empty<byte>(), MemeGame.Bfv));
        Assert.Throws<InvalidDataException>(() => MemeFile.Parse(new byte[] { 1, 2, 3 }, MemeGame.Bfv));
        Assert.Throws<InvalidDataException>(() => MemeFile.Parse("game.addModPath Mods/bf1942/\r\n"u8.ToArray(), MemeGame.Bf1942));
        Assert.Throws<InvalidDataException>(() => MemeFile.Parse(Table("dice::meme::NameNode")[..20], MemeGame.Bfv));   // table runs off the end
        Assert.Throws<InvalidDataException>(() => MemeFile.Parse(Table("dice::meme::NameNode"), MemeGame.Bfv));         // no root class index
        Assert.Throws<InvalidDataException>(() => MemeFile.Parse(Cat(Table("dice::meme::NameNode"), U16(0)), MemeGame.Bfv));
        Assert.Throws<InvalidDataException>(() => MemeFile.Parse(Cat(Table("dice::meme::NameNode"), U16(7)), MemeGame.Bfv));
        Assert.False(MemeFile.IsMeme("\u000CMemeFile 1.0"u8));
        Assert.True(MemeFile.IsMeme(Table()));
    }

    /// <summary>What a byte-exact writer must not normalise: NaN payload bits, lone surrogates in a wstr32, CR/LF in a
    /// str32, a list terminator that carried dead bytes, a string table with the same string twice.</summary>
    [Fact]
    public void Odd_values_come_back_bit_for_bit()
    {
        var (mf, _) = HudRead(MemeGame.Bfv);
        var t = mf.Walk().First(r => r.Short == "TransformNode");
        t["X"] = BitConverter.Int32BitsToSingle(0x7FC01234);
        t["Width"] = -0f;
        mf.CopiesOf("Locale/Title").Single()["Wstring <do not edit>"] = "half \uD800 a pair, then \U0001F600";
        var bytes = mf.ToBytes();
        // a signalling NaN in Y, put into the bytes: a float constant can be quieted before it reaches the code (the
        // JIT folds it through a double), a value read from a file cannot
        var read = MemeFile.Parse(bytes, MemeGame.Bfv).Walk().First(r => r.Short == "TransformNode");
        int y = read.Offset!.Value + 8 + read.Next!.Size!.Value + 4;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(y), 0x7F800001);
        var back = MemeFile.Parse(bytes, MemeGame.Bfv);
        var bt = back.Walk().First(r => r.Short == "TransformNode");
        Assert.Equal(0x7FC01234, BitConverter.SingleToInt32Bits(bt.Get<float>("X")));
        Assert.Equal(0x7F800001, BitConverter.SingleToInt32Bits(bt.Get<float>("Y")));
        Assert.Equal(int.MinValue, BitConverter.SingleToInt32Bits(bt.Get<float>("Width")));
        Assert.Equal("half \uD800 a pair, then \U0001F600", back.CopiesOf("Locale/Title").Single()["Wstring <do not edit>"]);
        Assert.Equal("Hello\r\nworld", back.CopiesOf("Hud/Title").Single()["String <do not edit>"]);
        Assert.Equal(new[] { "lone surrogate" }, back.Warnings.Where(w => w.Text.EndsWith("surrogate")).Select(w => w.Text[^14..]));
        Assert.Equal(2, back.Warnings.Count(w => w.Text.Contains("float = NaN")));
        Assert.Equal(bytes, back.ToBytes());
        Assert.Equal(bytes, MemeJson.FromJson(MemeJson.ToJson(back), MemeGame.Bfv).ToBytes());

        // a list whose terminator is a null record of 12 bytes, and a table holding "dice::meme::NameNode" twice
        var table = Table("dice::meme::ActionListAction", "dice::meme::RemoveEventAction", "dice::meme::RemoveEventAction");
        var list = Cat(table, U16(1), Rec(8, 0, 3), Rec(8, 0, 2), Rec(12, 0, 0), new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        var ml = MemeFile.Parse(list, MemeGame.Bf1942);
        var f = ml.Root.Field("Action list");
        Assert.Equal(2, f.Items!.Count);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, f.ListEnd!.Raw);
        Assert.Equal(3, f.Items[0].ClassIndex);                              // the second copy of the string, as read
        Assert.Equal(list, ml.ToBytes());
        Assert.Equal(list, ml.ToBytes(MemeTableMode.Keep));
        Assert.Equal(list, MemeJson.FromJson(MemeJson.ToJson(ml)).ToBytes());
        f.ListEnd = MemeRecord.Ref("x");
        Assert.Throws<InvalidOperationException>(() => ml.ToBytes());
    }

    /// <summary>The Tree / NavigateTree grammar (no corpus file has either class), from the reference's synthetic file:
    /// a NavigateTree whose root node has a child and a next sibling. Written back byte-exact for both games; a node
    /// flagged 0 is written as a node; a prev under a next-node, which the grammar cannot hold, is refused.</summary>
    [Theory]
    [InlineData(MemeGame.Bf1942)]
    [InlineData(MemeGame.Bfv)]
    public void The_tree_grammar_round_trips(MemeGame game)
    {
        var nul = Rec(8, 0, 0);
        var node = Cat(nul, nul);                                            // NavigateTreeObject: Description, Action
        var tree = Cat(new byte[] { 1 }, node, Cat(new byte[] { 1 }, node, new byte[] { 0, 0, 0 }),
                       Cat(new byte[] { 1 }, node, new byte[] { 0, 0 }), new byte[] { 0 });
        var navtree = Cat(Rec(8 + tree.Length, 0, 3), tree);
        var navact = Cat(Rec(8 + navtree.Length, 0, 2), navtree);
        var bytes = Cat(Table("dice::meme::NameNode", "dice::meme::NavigateAction", "dice::meme::NavigateTree"), U16(1), navact);
        var mf = MemeFile.Parse(bytes, game);
        Assert.True(mf.IsFullyDecoded);
        var t = mf.Root.Next!.Get<MemeRecord>("Action tree").Field("Tree").TreeRoot!;
        Assert.NotNull(t.Child);
        Assert.NotNull(t.Next);
        Assert.Null(t.Prev);
        Assert.Equal(new[] { "Description", "Action" }, t.Fields.Select(f => f.Label));
        Assert.Equal(bytes, mf.ToBytes());
        Assert.Equal(bytes, MemeJson.FromJson(MemeJson.ToJson(mf), game).ToBytes());
        Assert.Equal(6, mf.Walk().Count(r => r.Kind == MemeRecordKind.Null));

        var nav = MemeRecord.New(game, "NavigateTree");
        var layout = nav.Class!.TreeNodeFields!;
        var n0 = MemeTreeNode.New(layout);
        n0.Flag = 0;
        nav["Tree"] = n0;
        var button = MemeRecord.New(game, "BfButtonNode", "", ("Action", MemeRecord.New(game, "NavigateAction", "", ("Action tree", nav))));
        var nf = MemeFile.Create(game);
        nf.Root.Next = button;
        var back = MemeFile.Parse(nf.ToBytes(), game);
        Assert.True(back.IsFullyDecoded);
        Assert.Equal(1, back.Walk().Single(r => r.Short == "NavigateTree").Field("Tree").TreeRoot!.Flag);

        n0.Next = MemeTreeNode.New(layout);
        n0.Next.Prev = MemeTreeNode.New(layout);
        Assert.Contains("prev", Assert.Throws<InvalidOperationException>(() => nf.ToBytes()).Message);
        n0.Next.Prev = null;
        n0.Prev = MemeTreeNode.New(layout);
        n0.Prev.Next = MemeTreeNode.New(layout);
        Assert.Throws<InvalidOperationException>(() => nf.ToBytes());
    }

    /// <summary>The engine's fallback format (no magic, no table, inline names; engine-proven, no sample exists): a file
    /// converted to it reads back as the same tree, writes back byte-exact, and converts back to the same Meme 2.0 bytes.</summary>
    [Theory]
    [InlineData(MemeGame.Bf1942)]
    [InlineData(MemeGame.Bfv)]
    public void The_old_format_round_trips(MemeGame game)
    {
        var (mf, bytes) = HudRead(game);
        var old = mf.ToOldFormat().ToBytes();
        Assert.False(MemeFile.IsMeme(old));
        Assert.Equal((byte)"dice::meme::NameNode".Length, old[0]);
        var mo = MemeFile.Parse(old, game);
        Assert.True(mo.IsOldFormat);
        Assert.True(mo.IsFullyDecoded);
        Assert.Null(mo.Strings);
        Assert.True(SameTree(mf, mo));
        Assert.Equal(old, mo.ToBytes());
        Assert.Equal(bytes, mo.ToMeme2Format().ToBytes());
        Assert.Equal(old, MemeJson.FromJson(MemeJson.ToJson(mo), game).ToBytes());
    }

    [Fact]
    public void The_dump_reads_as_the_tree()
    {
        var (mf, _) = HudRead(MemeGame.Bfv);
        var text = MemeDump.ToText(mf);
        Assert.StartsWith("root: NameNode\n  Next node: TransformNode\n    Next node: CullNode\n", text);
        Assert.Contains("\n    X = 390  (f32)\n", text);
        Assert.Contains("Button type = 18 (Enter)  (i32)", text);
        Assert.Contains("Path node: -> 'MainNavigationLayer'", text);
        Assert.Contains("String <do not edit> = 'Hello\\r\\nworld'  (str32)", text);
        Assert.Contains("Action list: LIST[3]", text);
    }
}
