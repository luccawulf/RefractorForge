using System.Collections.Concurrent;
using RefractorForge.Formats.Menu;
using RefractorForge.Formats.Rfa;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Meme 2.0 menu files (<see cref="MemeFile"/>) against every one the two games and their mods ship, read straight out
/// of the menu archives. Retail (94 BF1942 files - 91 bf1942, 1 XPack1, 2 XPack2 - and 92 BFV) decodes with every byte
/// accounted for, without a single warning, and comes back byte-exact whichever way the string table is written and
/// through JSON. Every community file of both main installs (1,214 BF1942 from 91 archives, 291 BFV from 8) comes back
/// byte-exact, and all but two are fully decoded - both damaged data, not open layouts: FW1918's hand-edited
/// <c>testi</c> (one opaque 21-byte record) and wasteland2042's <c>SingleplayerNavigation</c> (a null record of size
/// 86, 78 bytes the engine never reads) - the same two the reference <c>meme.py</c> finds.
/// </summary>
public class MemeCorpusTests
{
    private const string Bf1942Mods = Installs.Bf1942Clean + @"\Mods";
    private const string BfvMods = Installs.BfvOriginal + @"\Mods";
    private const string Bf1942Main = @"D:\Games\EA GAMES\Battlefield 1942\Mods";
    private const string BfvMain = @"D:\Games\EA GAMES\Battlefield Vietnam\Mods";
    private const string MdtTemplates = @"D:\Games\EA GAMES\Battlefield Mod Development Toolkit\Menu Editor\templates";

    private static readonly HashSet<string> RetailMods = new(StringComparer.OrdinalIgnoreCase) { "bf1942", "XPack1", "XPack2", "BfVietnam" };

    /// <summary>The menu archives of the retail mods, with the Meme files each holds (XPack1's menu_001.rfa has none).</summary>
    private static readonly (string Archive, MemeGame Game, int Files)[] RetailArchives =
    {
        (Bf1942Mods + @"\bf1942\Archives\menu.rfa", MemeGame.Bf1942, 91),
        (Bf1942Mods + @"\XPack1\Archives\Menu.rfa", MemeGame.Bf1942, 1),
        (Bf1942Mods + @"\XPack1\Archives\menu_001.rfa", MemeGame.Bf1942, 0),
        (Bf1942Mods + @"\XPack2\Archives\Menu.rfa", MemeGame.Bf1942, 2),
        (BfvMods + @"\BfVietnam\Archives\menu.rfa", MemeGame.Bfv, 92),
    };

    [InstallFact(Installs.Bf1942Clean, Installs.BfvOriginal)]
    public void Every_retail_menu_file_decodes_whole_and_comes_back_byte_exact()
    {
        var failures = new ConcurrentBag<string>();
        var counts = new ConcurrentDictionary<MemeGame, int>();
        Parallel.ForEach(RetailArchives, arc =>
        {
            if (!File.Exists(arc.Archive)) { failures.Add($"{arc.Archive}: missing"); return; }
            int n = 0;
            foreach (var (entry, data) in MemeTestData.MemeEntries(new RefractorFlatArchive(arc.Archive)))
            {
                n++;
                string at = $"{Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(arc.Archive)))}|{entry}";
                var mf = MemeFile.Parse(data, arc.Game);
                if (!mf.IsFullyDecoded) failures.Add($"{at}: not fully decoded (opaque {mf.Stats.Opaque}, dead {mf.Stats.DeadBytes})");
                if (mf.Warnings.Count > 0) failures.Add($"{at}: {mf.Warnings.Count} warnings, first {mf.Warnings[0]}");
                if (mf.TableRule != "first-use") failures.Add($"{at}: table rule {mf.TableRule}");
                if (mf.Lint().Count > 0) failures.Add($"{at}: lint findings");
                foreach (var mode in new[] { MemeTableMode.Auto, MemeTableMode.Keep, MemeTableMode.Rebuild })
                    if (!mf.ToBytes(mode).AsSpan().SequenceEqual(data)) failures.Add($"{at}: not byte-exact with the table {mode}");
                var back = MemeJson.FromJson(MemeJson.ToJson(mf), arc.Game);
                if (!back.ToBytes().AsSpan().SequenceEqual(data)) failures.Add($"{at}: JSON round trip not byte-exact");
                // the engine's old (table-less) format: written, read back, the same tree and the same bytes again
                var old = mf.ToOldFormat().ToBytes();
                var mo = MemeFile.Parse(old, arc.Game);
                if (!mo.IsOldFormat || !mo.IsFullyDecoded || !mo.ToMeme2Format().ToBytes().AsSpan().SequenceEqual(data))
                    failures.Add($"{at}: old-format round trip");
            }
            if (n != arc.Files) failures.Add($"{arc.Archive}: {n} menu files, expected {arc.Files}");
            counts.AddOrUpdate(arc.Game, n, (_, v) => v + n);
        });
        Assert.True(failures.IsEmpty, string.Join("\n", failures.Take(20)));
        Assert.Equal(94, counts[MemeGame.Bf1942]);
        Assert.Equal(92, counts[MemeGame.Bfv]);
    }

    /// <summary>Every menu archive of every non-retail mod of both main installs. An archive that is not an RFA
    /// (MC1942_FX1's 1,388-byte MENU.rfa) is skipped and counted, as is an entry the archive cannot read.</summary>
    [InstallFact(Bf1942Main, BfvMain)]
    public void Every_community_menu_file_comes_back_byte_exact()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var (root, game, archives, notRfa, files, notFull) in new[]
                 {
                     (Bf1942Main, MemeGame.Bf1942, 163, 1, 1214, new[] { @"FW1918|menu/testi", @"wasteland2042|menu/SingleplayerNavigation" }),
                     (BfvMain, MemeGame.Bfv, 12, 0, 291, Array.Empty<string>()),
                 })
        {
            if (!Directory.Exists(root)) continue;
            var paths = Directory.GetDirectories(root)
                                 .Where(m => !RetailMods.Contains(Path.GetFileName(m)) && Directory.Exists(Path.Combine(m, "Archives")))
                                 .SelectMany(m => Directory.GetFiles(Path.Combine(m, "Archives"), "menu*.rfa"))
                                 .ToList();
            var failures = new ConcurrentBag<string>();
            var partial = new ConcurrentBag<string>();
            var unreadable = new ConcurrentBag<string>();
            int badArchives = 0, n = 0;
            Parallel.ForEach(paths, path =>
            {
                var mod = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path)));
                RefractorFlatArchive a;
                try { a = new RefractorFlatArchive(path); }
                catch (InvalidDataException) { Interlocked.Increment(ref badArchives); return; }
                foreach (var (entry, data) in MemeTestData.MemeEntries(a, e => unreadable.Add($"{mod}|{e}")))
                {
                    Interlocked.Increment(ref n);
                    try
                    {
                        var mf = MemeFile.Parse(data, game);
                        if (!mf.IsFullyDecoded) partial.Add($"{mod}|{entry}");
                        if (!mf.ToBytes().AsSpan().SequenceEqual(data)) failures.Add($"{mod}|{entry}: not byte-exact");
                    }
                    catch (Exception e) { failures.Add($"{mod}|{entry}: {e.GetType().Name} {e.Message}"); }
                }
            });
            Assert.True(failures.IsEmpty, string.Join("\n", failures.Take(20)));
            Assert.Equal(archives, paths.Count);
            Assert.Equal(notRfa, badArchives);
            Assert.Equal(files, n);
            Assert.Equal(notFull.OrderBy(x => x), partial.OrderBy(x => x));
        }
        Assert.True(sw.Elapsed.TotalSeconds < 120, $"the community corpus took {sw.Elapsed.TotalSeconds:0} s");
    }

    /// <summary>The DICE MDT's own two templates: byte-exact, and the Default template carries the one slot-type
    /// mismatch of the whole corpus - a StringData in a TransformNode's Next node - which is a warning, never an error
    /// (the loader does not enforce slot types).</summary>
    [InstallFact(MdtTemplates)]
    public void The_mdt_templates_round_trip_and_the_one_type_mismatch_is_a_warning()
    {
        var files = Directory.GetFiles(MdtTemplates).Where(f => MemeFile.IsMeme(File.ReadAllBytes(f))).ToList();
        Assert.Equal(2, files.Count);
        int typeWarnings = 0;
        foreach (var f in files)
        {
            var data = File.ReadAllBytes(f);
            var mf = MemeFile.Parse(data, MemeGame.Bf1942);
            Assert.True(mf.IsFullyDecoded, f);
            Assert.Equal(data, mf.ToBytes());
            typeWarnings += mf.Warnings.Count(w => w.Kind == MemeWarningKind.Type);
        }
        Assert.Equal(1, typeWarnings);
    }

    private static byte[] RetailFile(string archive, string entry) =>
        MemeTestData.MemeEntries(new RefractorFlatArchive(archive)).First(e => e.Entry.EndsWith(entry, StringComparison.OrdinalIgnoreCase)).Data;

    /// <summary>The reference's edit tests on the real BFV HUD (menu/InGame, the largest file: 465 strings): a value
    /// edit changes only its 4 bytes, a rename to a new string gives a first-use table (or, kept, the string appended),
    /// an inserted TransformNode adds exactly 32 bytes, a deleted subtree keeps the table (Rebuild compacts it), every
    /// edit undone is byte-exact again, and a table past 511 strings is refused.</summary>
    [InstallFact(Installs.BfvOriginalArchives)]
    public void Edits_on_the_retail_bfv_hud_behave_as_the_reference_says()
    {
        var data = RetailFile(Installs.BfvOriginalArchives + @"\menu.rfa", "menu/InGame");
        var mf = MemeFile.Parse(data, MemeGame.Bfv);
        Assert.Equal(465, mf.Strings!.Count - 1);

        var tn = mf.Walk().First(r => r.Short == "TransformNode");
        float x = tn.Get<float>("X");
        tn["X"] = x + 12.5f;
        var edited = mf.ToBytes();
        Assert.Equal(data.Length, edited.Length);
        Assert.InRange(data.Zip(edited).Count(p => p.First != p.Second), 1, 4);
        Assert.Equal(x + 12.5f, MemeFile.Parse(edited, MemeGame.Bfv).Walk().First(r => r.Short == "TransformNode").Get<float>("X"));
        tn["X"] = x;
        Assert.Equal(data, mf.ToBytes());

        var named = mf.Walk().First(r => r.Kind == MemeRecordKind.Object && r.Name.Length > 0 && r != mf.Root);
        var oldName = named.Name;
        named.Name = "RDK/TestVariable";
        var renamed = MemeFile.Parse(mf.ToBytes(), MemeGame.Bfv);
        Assert.Equal("first-use", renamed.TableRule);
        Assert.True(MemeTestData.SameTree(mf, renamed));
        var kept = MemeFile.Parse(mf.ToBytes(MemeTableMode.Keep), MemeGame.Bfv);
        Assert.Equal("RDK/TestVariable", kept.Strings![^1]);
        Assert.Equal(mf.Strings, kept.Strings.Take(kept.Strings.Count - 1));
        named.Name = oldName;

        var newt = MemeRecord.New(MemeGame.Bfv, "TransformNode", "", ("X", 10f), ("Y", 20f), ("Width", 100f), ("Height", 50f));
        MemeEdits.InsertAfter(tn, newt);
        var inserted = mf.ToBytes();
        Assert.Equal(data.Length + 8 + 16 + 8, inserted.Length);      // header, four floats, a null Transformed node
        Assert.True(MemeTestData.SameTree(mf, MemeFile.Parse(inserted, MemeGame.Bfv)));
        Assert.True(MemeEdits.Unlink(mf, newt));
        Assert.Equal(data, mf.ToBytes());

        var slot = mf.Walk().Where(r => r.Fields is not null).SelectMany(r => r.Fields!)
                     .First(f => f.Token == MemeToken.O && f.Record is { Kind: MemeRecordKind.Object, Short: "CullNode" });
        var saved = slot.Value;
        slot.Value = MemeRecord.Null();
        var auto = MemeFile.Parse(mf.ToBytes(), MemeGame.Bfv);
        var rebuilt = MemeFile.Parse(mf.ToBytes(MemeTableMode.Rebuild), MemeGame.Bfv);
        Assert.Equal(mf.Strings, auto.Strings);
        Assert.Equal("first-use", rebuilt.TableRule);
        Assert.True(rebuilt.Strings!.Count < auto.Strings!.Count);
        slot.Value = saved;
        Assert.Equal(data, mf.ToBytes());

        var many = MemeFile.Parse(data, MemeGame.Bfv);
        int i = 0;
        foreach (var r in many.Walk().Where(r => r.Kind == MemeRecordKind.Object && r != many.Root).Take(600)) r.Name = $"X{i++}";
        var e = Assert.Throws<InvalidOperationException>(() => many.ToBytes());
        Assert.Contains("511", e.Message);
    }
}
