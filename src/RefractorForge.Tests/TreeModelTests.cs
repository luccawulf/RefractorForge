using RefractorForge.Archive;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The folders-and-files-in-one-list model behind the archive window.
///
/// It is flattened to a row array because the control that draws it is virtual and asks for rows by index, so
/// the row order, the depths and the collapsed state have to be right in the array itself — there is no control
/// holding the hierarchy to fall back on.
/// </summary>
public class TreeModelTests
{
    private static ArchiveModel.Item F(string name, int size = 100, int packed = 40) =>
        new() { Name = name, UncompressedSize = size, BlockSize = packed };

    private static List<ArchiveModel.Item> Sample() => new()
    {
        F("bf1942/levels/Berlin/Init.con", 500, 200),
        F("bf1942/levels/Berlin/Heightmap.raw", 1000, 400),
        F("bf1942/levels/Kharkov/Init.con", 300, 150),
        F("texture/wall.dds", 2000, 900),
        F("readme.txt", 50, 50),
    };

    [Fact]
    public void Every_folder_appears_once_with_its_files_nested_under_it()
    {
        var t = new TreeModel();
        t.ExpandAll(Sample());
        t.Build(Sample(), "");

        var rows = t.Rows;
        var berlin = rows.Single(r => r.IsFolder && r.Path == "bf1942/levels/Berlin");
        Assert.Equal("Berlin", berlin.Display);
        Assert.Equal(2, berlin.Depth);          // bf1942 = 0, levels = 1, Berlin = 2

        // Its two files follow it, one level deeper.
        int at = rows.ToList().FindIndex(r => r.Path == "bf1942/levels/Berlin");
        var under = rows.Skip(at + 1).TakeWhile(r => r.Depth > berlin.Depth).ToList();
        Assert.Equal(2, under.Count);
        Assert.All(under, r => Assert.Equal(3, r.Depth));
        Assert.Contains(under, r => r.Display == "Init.con");
        Assert.Contains(under, r => r.Display == "Heightmap.raw");

        // A file at the archive root sits at depth 0 alongside the top-level folders.
        var readme = rows.Single(r => r.Path == "readme.txt");
        Assert.False(readme.IsFolder);
        Assert.Equal(0, readme.Depth);
    }

    [Fact]
    public void A_collapsed_folder_hides_everything_beneath_it()
    {
        var items = Sample();
        var t = new TreeModel();
        t.ExpandAll(items);
        t.Build(items, "");
        int expanded = t.Rows.Count;

        t.SetExpanded("bf1942/levels", false);
        t.Build(items, "");

        Assert.True(t.Rows.Count < expanded);
        Assert.Contains(t.Rows, r => r.Path == "bf1942/levels");                 // the folder itself remains
        Assert.DoesNotContain(t.Rows, r => r.Path.StartsWith("bf1942/levels/")); // its contents do not
    }

    [Fact]
    public void A_folder_reports_what_is_inside_it_even_while_collapsed()
    {
        // The point of the aggregate: a collapsed branch still has to say how much is in there.
        var items = Sample();
        var t = new TreeModel();
        t.Build(items, "");                       // nothing expanded

        var bf = t.Rows.Single(r => r.Path == "bf1942");
        Assert.Equal(3, bf.FileCount);            // two Berlin files + one Kharkov
        Assert.Equal(1800, bf.TotalSize);         // 500 + 1000 + 300
        Assert.Equal(750, bf.TotalPacked);        // 200 + 400 + 150
    }

    [Fact]
    public void Searching_switches_to_a_flat_list_of_matches()
    {
        var items = Sample();
        var t = new TreeModel();
        t.ExpandAll(items);
        t.Build(items, "init");

        // While searching, folders are noise - you want the matches, with enough path to tell them apart.
        Assert.All(t.Rows, r => Assert.False(r.IsFolder));
        Assert.Equal(2, t.Rows.Count);
        Assert.All(t.Rows, r => Assert.Equal(0, r.Depth));
        Assert.Contains(t.Rows, r => r.Display == "bf1942/levels/Berlin/Init.con");
        Assert.Contains(t.Rows, r => r.Display == "bf1942/levels/Kharkov/Init.con");
    }

    [Fact]
    public void Opening_shows_the_top_level_only()
    {
        var items = Sample();
        var t = new TreeModel();
        t.ExpandTopLevel(items);
        t.Build(items, "");

        Assert.True(t.IsExpanded("bf1942"));
        Assert.False(t.IsExpanded("bf1942/levels"));
        Assert.Contains(t.Rows, r => r.Path == "bf1942/levels");
        Assert.DoesNotContain(t.Rows, r => r.Path == "bf1942/levels/Berlin");
    }

    [Fact]
    public void Deleted_entries_leave_the_list_before_it_is_built()
    {
        var items = Sample();
        items[0].State = ArchiveModel.EntryState.Deleted;

        var t = new TreeModel();
        t.ExpandAll(items);
        t.Build(items, "");

        Assert.DoesNotContain(t.Rows, r => r.Path == "bf1942/levels/Berlin/Init.con");
        var berlin = t.Rows.Single(r => r.Path == "bf1942/levels/Berlin");
        Assert.Equal(1, berlin.FileCount);
    }

    [Fact]
    public void Sorting_orders_files_within_their_folder_and_leaves_the_hierarchy_alone()
    {
        // The whole point of the list is the structure, so a sort must not flatten it: files reorder inside
        // their own folder, and the folders stay where they are.
        var items = new List<ArchiveModel.Item>
        {
            F("a/small.con", 10, 5),
            F("a/huge.con", 9000, 4000),
            F("a/medium.con", 500, 250),
            F("b/only.con", 77, 30),
        };
        var t = new TreeModel();
        t.ExpandAll(items);

        t.SetSort(1, descending: true);          // column 1 = Size
        t.Build(items, "");

        var rows = t.Rows.ToList();
        int aAt = rows.FindIndex(r => r.Path == "a");
        int bAt = rows.FindIndex(r => r.Path == "b");
        Assert.True(aAt < bAt, "folder order is untouched by a file sort");

        var inA = rows.Skip(aAt + 1).TakeWhile(r => !r.IsFolder).Select(r => r.Display).ToList();
        Assert.Equal(new[] { "huge.con", "medium.con", "small.con" }, inA);

        // b's single file must still sit under b, not get pulled in among a's.
        Assert.Equal("only.con", rows[bAt + 1].Display);

        t.SetSort(1, descending: false);
        t.Build(items, "");
        rows = t.Rows.ToList();
        aAt = rows.FindIndex(r => r.Path == "a");
        inA = rows.Skip(aAt + 1).TakeWhile(r => !r.IsFolder).Select(r => r.Display).ToList();
        Assert.Equal(new[] { "small.con", "medium.con", "huge.con" }, inA);
    }

    [Fact]
    public void Subfolders_come_before_loose_files_at_the_same_level()
    {
        // The order every file manager uses; without it a folder can end up buried among its siblings' files.
        var items = new List<ArchiveModel.Item>
        {
            F("mod/zzz.con"),
            F("mod/aaa/inner.con"),
        };
        var t = new TreeModel();
        t.ExpandAll(items);
        t.Build(items, "");

        var rows = t.Rows.ToList();
        int folder = rows.FindIndex(r => r.Path == "mod/aaa");
        int loose = rows.FindIndex(r => r.Path == "mod/zzz.con");
        Assert.True(folder < loose, "the subfolder should be listed before the loose file");
    }
}
