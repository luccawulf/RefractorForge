using System.Text;
using RefractorForge.Formats;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Rfa;

namespace RefractorForge.Archive;

/// <summary>Shared bits for the tool windows: dark list views and labelled rows, so each dialog is short.</summary>
internal static class Ui
{
    public static ListView List(params (string Title, int Width)[] cols)
    {
        var lv = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false,
            BorderStyle = BorderStyle.None, MultiSelect = true,
        };
        foreach (var (t, w) in cols) lv.Columns.Add(t, Theme.Dp(w));
        DarkHeader(lv);
        lv.Resize += (_, _) =>
        {
            if (lv.Columns.Count == 0) return;
            int others = 0;
            for (int i = 0; i < lv.Columns.Count - 1; i++) others += lv.Columns[i].Width;
            int w = lv.ClientSize.Width - others - Theme.Dp(4);
            if (w > Theme.Dp(80)) lv.Columns[^1].Width = w;
        };
        return lv;
    }

    /// <summary>The column header is the one part of a ListView that ignores BackColor; paint it ourselves.</summary>
    public static void DarkHeader(ListView lv)
    {
        lv.OwnerDraw = true;
        lv.DrawColumnHeader += (_, e) =>
        {
            using (var bg = new SolidBrush(Theme.Raised)) e.Graphics.FillRectangle(bg, e.Bounds);
            using (var pen = new Pen(Theme.Border))
                e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            using var fmt = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            using var br = new SolidBrush(Theme.TextDim);
            var r = new Rectangle(e.Bounds.Left + Theme.Dp(6), e.Bounds.Top, Math.Max(e.Bounds.Width - Theme.Dp(12), 4), e.Bounds.Height);
            e.Graphics.DrawString(e.Header?.Text ?? "", Theme.Small, br, r, fmt);
        };
        lv.DrawItem += (_, e) => e.DrawDefault = true;
        lv.DrawSubItem += (_, e) => e.DrawDefault = true;
    }

    public static TableLayoutPanel Grid(int rows)
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, RowCount = rows, AutoSize = true, Padding = new Padding(Theme.Dp(12), Theme.Dp(10), Theme.Dp(12), Theme.Dp(6)) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.Dp(130)));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return t;
    }

    public static void Row(TableLayoutPanel t, int row, string label, Control field, Control? button = null)
    {
        var l = new Label { Text = label, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill, ForeColor = Theme.TextDim };
        field.Dock = DockStyle.Fill; field.Margin = new Padding(0, Theme.Dp(3), Theme.Dp(6), Theme.Dp(3));
        t.Controls.Add(l, 0, row); t.Controls.Add(field, 1, row);
        if (button is not null) { button.Margin = new Padding(0, Theme.Dp(3), 0, Theme.Dp(3)); t.Controls.Add(button, 2, row); }
    }

    public static Button Btn(string text, EventHandler onClick, bool primary = false, int width = 110)
    {
        var b = new Button { Text = text, Width = Theme.Dp(width), Height = Theme.Dp(28) };
        b.Click += onClick;
        Theme.StyleButton(b, primary);
        return b;
    }

    public static Panel ButtonBar(params Button[] buttons)
    {
        var p = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = Theme.Dp(44), Padding = new Padding(Theme.Dp(8)) };
        foreach (var b in buttons) { b.Margin = new Padding(6, 0, 0, 0); p.Controls.Add(b); }
        return p;
    }

    public static string? PickFile(IWin32Window owner, string title, string filter)
    {
        using var d = new OpenFileDialog { Title = title, Filter = filter };
        return d.ShowDialog(owner) == DialogResult.OK ? d.FileName : null;
    }

    public static string? PickFolder(IWin32Window owner, string title)
    {
        using var d = new FolderBrowserDialog { Description = title, UseDescriptionForTitle = true };
        return d.ShowDialog(owner) == DialogResult.OK ? d.SelectedPath : null;
    }

    public static string Human(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.##} GiB"
        : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.##} MiB"
        : bytes >= 1L << 10 ? $"{bytes / (double)(1L << 10):0.##} KiB"
        : $"{bytes} B";
}

/// <summary>What changed between two archives - a level and its patch, two versions of a mod's objects.</summary>
public sealed class DiffForm : Form
{
    private readonly TextBox _a = new(), _b = new();
    private readonly ListView _list = Ui.List(("File", 460), ("Change", 90), ("Size A", 90), ("Size B", 90));
    private readonly CheckBox _showSame = new() { Text = "Show identical files too", AutoSize = true };
    private readonly Label _summary = new() { Dock = DockStyle.Bottom, Height = Theme.Dp(24), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(Theme.Dp(12), 0, 0, 0) };
    private ArchiveDiff.Result? _result;
    private readonly Action<string> _goTo;

    public DiffForm(string? pathA, string? pathB, Action<string> goTo)
    {
        _goTo = goTo;
        Text = "Compare Archives"; Size = Theme.Dp(this, 900, 600); MinimumSize = Theme.Dp(this, 640, 400);
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        _a.Text = pathA ?? ""; _b.Text = pathB ?? "";

        var g = Ui.Grid(3);
        Ui.Row(g, 0, "Archive A", _a, Ui.Btn("Browse...", (_, _) => { if (Ui.PickFile(this, "Archive A", "RFA|*.rfa") is { } p) _a.Text = p; }, width: 90));
        Ui.Row(g, 1, "Archive B", _b, Ui.Btn("Browse...", (_, _) => { if (Ui.PickFile(this, "Archive B", "RFA|*.rfa") is { } p) _b.Text = p; }, width: 90));
        Ui.Row(g, 2, "", _showSame);
        _showSame.CheckedChanged += (_, _) => Fill();

        _list.DoubleClick += (_, _) => { if (_list.SelectedItems.Count > 0) _goTo(_list.SelectedItems[0].Text); };
        Controls.Add(_list); Controls.Add(_summary);
        var compare = Ui.Btn("Compare", (_, _) => Run(), primary: true);
        Controls.Add(Ui.ButtonBar(Ui.Btn("Close", (_, _) => Close()), Ui.Btn("Copy report", (_, _) => { if (_result is not null) Clipboard.SetText(_result.ToReport()); }), compare));
        AcceptButton = compare;
        Controls.Add(g);
        _list.BringToFront();
        Theme.Apply(this);
        _summary.ForeColor = Theme.TextDim;
        if (pathA is not null && pathB is not null) Shown += (_, _) => Run();
    }

    private void Run()
    {
        if (!File.Exists(_a.Text) || !File.Exists(_b.Text)) { _summary.Text = "Pick two archives."; return; }
        try { Cursor = Cursors.WaitCursor; _result = ArchiveDiff.Compare(_a.Text, _b.Text); }
        catch (Exception ex) { _summary.Text = ex.Message; return; }
        finally { Cursor = Cursors.Default; }
        Fill();
    }

    private void Fill()
    {
        _list.BeginUpdate(); _list.Items.Clear();
        if (_result is not null)
        {
            foreach (var l in _result.Lines)
            {
                if (l.Kind == ArchiveDiff.Kind.Same && !_showSame.Checked) continue;
                var (label, color) = l.Kind switch
                {
                    ArchiveDiff.Kind.OnlyInA => ("only in A", Theme.Deleted),
                    ArchiveDiff.Kind.OnlyInB => ("only in B", Theme.Added),
                    ArchiveDiff.Kind.Changed => ("changed", Theme.Replaced),
                    _ => ("same", Theme.TextFaint),
                };
                _list.Items.Add(new ListViewItem(new[] { l.Name, label, l.SizeA == 0 ? "" : l.SizeA.ToString("N0"), l.SizeB == 0 ? "" : l.SizeB.ToString("N0") }) { ForeColor = color });
            }
            _summary.Text = _result.Identical ? "Identical." :
                $"{_result.OnlyInA} only in A   -   {_result.OnlyInB} only in B   -   {_result.Changed} changed   -   {_result.Same} identical";
        }
        _list.EndUpdate();
    }
}

/// <summary>A list of files with a purpose - references to an asset, or the members of some result.</summary>
public sealed class FileListForm : Form
{
    public FileListForm(string title, string summary, IEnumerable<(string Name, string Note)> rows, Action<string> goTo)
    {
        Text = title; Size = Theme.Dp(this, 760, 480); MinimumSize = Theme.Dp(this, 480, 300);
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        var list = Ui.List(("File", 480), ("Note", 220));
        foreach (var (n, note) in rows) list.Items.Add(new ListViewItem(new[] { n, note }));
        list.DoubleClick += (_, _) => { if (list.SelectedItems.Count > 0) goTo(list.SelectedItems[0].Text); };
        var lbl = new Label { Text = summary, Dock = DockStyle.Top, Height = Theme.Dp(34), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(Theme.Dp(12), 0, 0, 0) };
        Controls.Add(list); Controls.Add(Ui.ButtonBar(Ui.Btn("Close", (_, _) => Close()),
            Ui.Btn("Copy list", (_, _) => Clipboard.SetText(string.Join("\r\n", list.Items.Cast<ListViewItem>().Select(i => i.Text))))));
        Controls.Add(lbl); list.BringToFront();
        Theme.Apply(this);
        lbl.ForeColor = Theme.TextDim;
    }
}

/// <summary>The Mod Optimizer: textures and sounds nothing references. Scans the open archive or whole mod.</summary>
public sealed class UnusedAssetsForm : Form
{
    private readonly ArchiveModel _model;
    private readonly Action<IEnumerable<string>> _stageDelete;
    private readonly ListView _list = Ui.List(("File", 440), ("Size", 100), ("Archive", 150));
    private readonly CheckBox _tex = new() { Text = "Textures", AutoSize = true, Checked = true };
    private readonly CheckBox _snd = new() { Text = "Sounds", AutoSize = true, Checked = true };
    private readonly Label _summary = new() { Dock = DockStyle.Bottom, Height = Theme.Dp(24), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(Theme.Dp(12), 0, 0, 0) };
    private readonly Button _stage;

    public UnusedAssetsForm(ArchiveModel model, Action<IEnumerable<string>> stageDelete, Action<string> goTo)
    {
        _model = model; _stageDelete = stageDelete;
        Text = "Unused Assets"; Size = Theme.Dp(this, 860, 600); MinimumSize = Theme.Dp(this, 600, 400);
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;

        var intro = new Label
        {
            Dock = DockStyle.Top, Height = Theme.Dp(66), Padding = new Padding(Theme.Dp(12), Theme.Dp(8), Theme.Dp(12), Theme.Dp(0)),
            Text = "Every .con, .rs, .ssc and mesh in what is open is read for the names it mentions; textures and sounds " +
                   "nothing mentions are listed, largest first. Files the engine loads by convention - terrain tiles, menu art, " +
                   "lightmaps, sky - are never listed. Review before removing anything: a file can be used in a way no script names.",
        };
        var opts = new FlowLayoutPanel { Dock = DockStyle.Top, Height = Theme.Dp(32), Padding = new Padding(Theme.Dp(10), Theme.Dp(4), Theme.Dp(0), Theme.Dp(0)) };
        opts.Controls.Add(_tex); opts.Controls.Add(_snd);
        _list.DoubleClick += (_, _) => { if (_list.SelectedItems.Count > 0) goTo(_list.SelectedItems[0].Text); };
        _stage = Ui.Btn("Mark selected for deletion", (_, _) => Stage(), width: 190);
        _stage.Enabled = !_model.IsWorkspace;
        Controls.Add(_list); Controls.Add(_summary);
        var scan = Ui.Btn("Scan", (_, _) => Run(), primary: true);
        Controls.Add(Ui.ButtonBar(Ui.Btn("Close", (_, _) => Close()), _stage,
            Ui.Btn("Copy list", (_, _) => Clipboard.SetText(string.Join("\r\n", _list.Items.Cast<ListViewItem>().Select(i => i.Text)))), scan));
        AcceptButton = scan;
        Controls.Add(opts); Controls.Add(intro);
        _list.BringToFront();
        Theme.Apply(this);
        intro.ForeColor = Theme.TextDim; _summary.ForeColor = Theme.TextDim;
    }

    private async void Run()
    {
        _list.Items.Clear(); _summary.Text = "Scanning...";
        bool tex = _tex.Checked, snd = _snd.Checked;
        List<AssetReferences.Unused> unused = new();
        int scanned = 0;
        await Task.Run(() =>
        {
            IEnumerable<RefractorFlatArchive> refArchives;
            IEnumerable<(string, RefractorFlatArchive)> assetArchives;
            if (_model.Workspace is { } ws)
            {
                refArchives = ws.Layers.Select(l => l.Archive);
                assetArchives = ws.Layers.Where((l, i) => !_model.HiddenLayers.Contains(i)).Select(l => (l.Label, l.Archive));
            }
            else if (_model.Archive is { } a)
            {
                refArchives = new[] { a };
                assetArchives = new[] { (Path.GetFileName(_model.Path ?? ""), a) };
            }
            else return;
            var refs = AssetReferences.Build(refArchives, _ => scanned++);
            unused = refs.UnusedAssets(assetArchives, tex, snd);
        });
        _list.BeginUpdate();
        foreach (var u in unused) _list.Items.Add(new ListViewItem(new[] { u.Name, u.Size.ToString("N0"), u.Archive }));
        _list.EndUpdate();
        _summary.Text = $"{unused.Count:N0} unreferenced file(s), {Ui.Human(unused.Sum(u => (long)u.Size))}   -   {scanned:N0} script/mesh file(s) read";
    }

    private void Stage()
    {
        var names = _list.SelectedItems.Cast<ListViewItem>().Select(i => i.Text).ToList();
        if (names.Count == 0) return;
        _stageDelete(names);
        _summary.Text = $"{names.Count} file(s) marked for deletion in the open archive - nothing is written until you save.";
    }
}

/// <summary>Server-side copies: one archive, or a whole folder of level archives, with client content stripped.</summary>
public sealed class StripForm : Form
{
    private readonly TextBox _src = new(), _dst = new();
    private readonly CheckBox _dry = new() { Text = "Dry run - report only, write nothing", AutoSize = true, Checked = true };
    private readonly ListView _list = Ui.List(("Archive", 260), ("Entries", 110), ("Before", 100), ("After", 100), ("Saved", 90));
    private readonly Label _summary = new() { Dock = DockStyle.Bottom, Height = Theme.Dp(24), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(Theme.Dp(12), 0, 0, 0) };

    public StripForm(string? levelsDir)
    {
        Text = "Server-Side Archives"; Size = Theme.Dp(this, 820, 560); MinimumSize = Theme.Dp(this, 600, 400);
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        _src.Text = levelsDir ?? "";
        if (levelsDir is not null) _dst.Text = Path.Combine(levelsDir, "server-side");
        var intro = new Label
        {
            Dock = DockStyle.Top, Height = Theme.Dp(54), Padding = new Padding(Theme.Dp(12), Theme.Dp(8), Theme.Dp(12), Theme.Dp(0)),
            Text = "A dedicated server needs a map's scripts and terrain, not its textures, sounds, movies or baked light. " +
                   "Every .rfa in the folder is written stripped into the output folder; the originals are never touched.",
        };
        var g = Ui.Grid(3);
        Ui.Row(g, 0, "Level archives in", _src, Ui.Btn("Browse...", (_, _) => { if (Ui.PickFolder(this, "Folder of level .rfa files") is { } p) { _src.Text = p; if (_dst.Text.Length == 0) _dst.Text = Path.Combine(p, "server-side"); } }, width: 90));
        Ui.Row(g, 1, "Write stripped to", _dst, Ui.Btn("Browse...", (_, _) => { if (Ui.PickFolder(this, "Output folder") is { } p) _dst.Text = p; }, width: 90));
        Ui.Row(g, 2, "", _dry);
        Controls.Add(_list); Controls.Add(_summary);
        var run = Ui.Btn("Run", (_, _) => Run(), primary: true);
        Controls.Add(Ui.ButtonBar(Ui.Btn("Close", (_, _) => Close()), run));
        AcceptButton = run;
        Controls.Add(g); Controls.Add(intro);
        _list.BringToFront();
        Theme.Apply(this);
        intro.ForeColor = Theme.TextDim; _summary.ForeColor = Theme.TextDim;
    }

    private async void Run()
    {
        if (!Directory.Exists(_src.Text)) { _summary.Text = "Pick a folder of level archives."; return; }
        if (!_dry.Checked && _dst.Text.Length == 0) { _summary.Text = "Pick an output folder."; return; }
        if (!_dry.Checked && PathSafety.ProtectedReason(_dst.Text) is { } why) { _summary.Text = "Refusing that output folder: " + why; return; }
        _list.Items.Clear(); _summary.Text = "Working...";
        bool dry = _dry.Checked; string src = _src.Text, dst = _dst.Text;
        List<ServerSide.Outcome> res = new();
        await Task.Run(() => res = ServerSide.StripFolder(src, dst, dry));
        _list.BeginUpdate();
        foreach (var r in res)
            _list.Items.Add(new ListViewItem(new[] { Path.GetFileName(r.Source), $"{r.EntriesBefore} -> {r.EntriesAfter}", Ui.Human(r.BytesBefore), Ui.Human(r.BytesAfter),
                                                     r.BytesBefore > 0 ? $"{100.0 * (r.BytesBefore - r.BytesAfter) / r.BytesBefore:0}%" : "" }));
        _list.EndUpdate();
        long saved = res.Sum(r => r.BytesBefore - r.BytesAfter);
        _summary.Text = $"{res.Count} archive(s), {Ui.Human(saved)} smaller in total" + (dry ? "  -  dry run, nothing written" : $"  -  written to {dst}");
    }
}

/// <summary>ModWizard: a new, empty mod the game will list.</summary>
public sealed class ModWizardForm : Form
{
    private readonly TextBox _root = new(), _name = new(), _display = new(), _version = new() { Text = "1.0" }, _url = new(), _bases = new() { Text = "bf1942" };
    private readonly CheckBox _vietnam = new() { Text = "Battlefield Vietnam", AutoSize = true };
    private readonly Label _summary = new() { Dock = DockStyle.Bottom, Height = Theme.Dp(40), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(Theme.Dp(12), 0, Theme.Dp(12), 0) };
    public string? CreatedModDir { get; private set; }

    public ModWizardForm(string? gameRoot)
    {
        Text = "New Mod"; Size = Theme.Dp(this, 640, 400); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        _root.Text = gameRoot ?? "";
        var g = Ui.Grid(7);
        Ui.Row(g, 0, "Game folder", _root, Ui.Btn("Browse...", (_, _) => { if (Ui.PickFolder(this, "The game's install folder (holds Mods\\)") is { } p) _root.Text = p; }, width: 90));
        Ui.Row(g, 1, "Folder name", _name);
        Ui.Row(g, 2, "Display name", _display);
        Ui.Row(g, 3, "Version", _version);
        Ui.Row(g, 4, "Website", _url);
        Ui.Row(g, 5, "Mounts (in order)", _bases);
        Ui.Row(g, 6, "", _vietnam);
        _vietnam.CheckedChanged += (_, _) => { if (_bases.Text is "bf1942" or "bfvietnam") _bases.Text = _vietnam.Checked ? "bfvietnam" : "bf1942"; };
        Controls.Add(_summary);
        Controls.Add(Ui.ButtonBar(Ui.Btn("Cancel", (_, _) => Close()), Ui.Btn("Create", (_, _) => Create(), primary: true)));
        Controls.Add(g);
        Theme.Apply(this);
        _summary.ForeColor = Theme.TextDim;
        _summary.Text = "Creates Mods\\<name>\\ with an init.con in the retail shape and the Archives folders. Mounts are the mods this one layers over, comma-separated, nearest first.";
    }

    private void Create()
    {
        try
        {
            var bases = _bases.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var made = ModScaffold.Create(_root.Text, new ModScaffold.Spec(_name.Text, _display.Text, _version.Text, _url.Text, _vietnam.Checked, bases));
            CreatedModDir = made[0];
            MessageBox.Show(this, $"Created {made.Count} item(s):\r\n\r\n{string.Join("\r\n", made)}", "Mod created", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK; Close();
        }
        catch (Exception ex) { _summary.Text = ex.Message; _summary.ForeColor = Theme.Bad; }
    }
}

/// <summary>The Object Generator: duplicate an object's .con set under a new name, templates renamed to match.</summary>
public sealed class CloneObjectForm : Form
{
    private readonly ArchiveModel _model;
    private readonly string _folder;
    private readonly TextBox _old = new(), _new = new();
    private readonly CheckBox _geom = new() { Text = "Rename geometry templates too (keeps pointing at the original mesh files)", AutoSize = true };
    private readonly ListView _preview = Ui.List(("Template / file", 340), ("Becomes", 340));
    private readonly Label _summary = new() { Dock = DockStyle.Bottom, Height = Theme.Dp(24), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(Theme.Dp(12), 0, 0, 0) };
    private ObjectCloner.Plan? _plan;

    public CloneObjectForm(ArchiveModel model, string folder)
    {
        _model = model; _folder = folder.TrimEnd('/');
        Text = "Clone Object"; Size = Theme.Dp(this, 820, 600); MinimumSize = Theme.Dp(this, 600, 400);
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        _old.Text = _folder[(_folder.LastIndexOf('/') + 1)..];
        var intro = new Label
        {
            Dock = DockStyle.Top, Height = Theme.Dp(54), Padding = new Padding(Theme.Dp(12), Theme.Dp(8), Theme.Dp(12), Theme.Dp(0)),
            Text = $"Copies every file under  {_folder}/  with the object renamed: each template whose name contains the old name is renamed, " +
                   "and every reference to it rewritten. Meshes are left alone, so the clone draws as the original until you give it its own.",
        };
        var g = Ui.Grid(3);
        Ui.Row(g, 0, "Object name", _old);
        Ui.Row(g, 1, "New name", _new);
        Ui.Row(g, 2, "", _geom);
        _old.TextChanged += (_, _) => Preview(); _new.TextChanged += (_, _) => Preview(); _geom.CheckedChanged += (_, _) => Preview();
        Controls.Add(_preview); Controls.Add(_summary);
        Controls.Add(Ui.ButtonBar(Ui.Btn("Cancel", (_, _) => Close()), Ui.Btn("Add to archive", (_, _) => Apply(), primary: true, width: 130)));
        Controls.Add(g); Controls.Add(intro);
        _preview.BringToFront();
        Theme.Apply(this);
        intro.ForeColor = Theme.TextDim; _summary.ForeColor = Theme.TextDim;
        Preview();
    }

    private IEnumerable<(string Path, string Text)> SourceFiles()
    {
        string prefix = _folder + "/";
        foreach (var it in _model.Items)
        {
            if (it.State == ArchiveModel.EntryState.Deleted) continue;
            if (!it.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!ConSyntax.Handles(it.Name)) continue;
            yield return (it.Name, Encoding.Latin1.GetString(_model.Read(it)));
        }
    }

    private void Preview()
    {
        _preview.Items.Clear(); _plan = null;
        if (_old.Text.Trim().Length == 0 || _new.Text.Trim().Length == 0) { _summary.Text = "Enter the new name."; return; }
        try { _plan = ObjectCloner.Build(_old.Text.Trim(), _new.Text.Trim(), SourceFiles(), _geom.Checked); }
        catch (Exception ex) { _summary.Text = ex.Message; return; }
        _preview.BeginUpdate();
        foreach (var kv in _plan.Templates) _preview.Items.Add(new ListViewItem(new[] { kv.Key, kv.Value }) { ForeColor = Theme.SynObject });
        foreach (var f in _plan.Files) _preview.Items.Add(new ListViewItem(new[] { f.OldPath, f.NewPath }));
        _preview.EndUpdate();
        _summary.Text = $"{_plan.Templates.Count} template rename(s), {_plan.Files.Count} file(s)" +
                        (ObjectCloner.RunLine(_plan) is { } rl ? $"   -   add to objects.con:  {rl}" : "");
    }

    private void Apply()
    {
        if (_plan is null || _plan.Files.Count == 0) return;
        if (_model.IsWorkspace) { _summary.Text = "Open the object's own archive to add the clone."; return; }
        foreach (var f in _plan.Files) _model.Add(f.NewPath, Encoding.Latin1.GetBytes(f.Text));
        DialogResult = DialogResult.OK; Close();
    }
}
