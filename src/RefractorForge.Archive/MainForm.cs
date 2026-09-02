using System.Text;
using RefractorForge.Formats;
using RefractorForge.Formats.Rfa;

namespace RefractorForge.Archive;

/// <summary>
/// The archive window. One list holds folders and files together - Refractor archives are broad and shallow,
/// and seeing a folder with its contents in one column is how you find your way around one - with the preview
/// below and the file's properties beside it.
///
/// Two things it does that a single-archive tool cannot. It opens a whole MOD: every archive the mod mounts,
/// merged the way the game merges them, with each file's provider named and the copies it overrides listed.
/// And it answers questions across all of that - which file mentions this texture, which sounds does nothing
/// use, what changed between this patch and its base - which is where the MDT's separate utilities came in.
///
/// The list is a virtual owner-drawn ListView: it has to hold tens of thousands of rows, and only a virtual
/// list asks for them by index as it scrolls. The hierarchy is drawn into the first column over a flattened
/// row array from <see cref="TreeModel"/>.
/// </summary>
public sealed class MainForm : Form
{
    private readonly ArchiveModel _model = new();
    private readonly TreeModel _tree = new();
    private readonly AudioPreview _audio = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly ShellIcons _icons;
    private readonly ExternalEdit _edit;

    private readonly ListView _list = new();
    private readonly ToolStripStatusLabel _status = new();
    private readonly ToolStripStatusLabel _statusRight = new();
    private readonly TextBox _search = new();

    // Header strip.
    private readonly Label _title = new();
    private readonly Label _subtitle = new();
    private readonly Label _chips = new();

    // Workspace layers.
    private readonly Panel _layersHost = new();
    private readonly CheckedListBox _layers = new();
    private bool _fillingLayers;
    private SplitContainer _outer = null!;

    // Preview + properties.
    private readonly Panel _previewHost = new();
    private readonly PictureBox _picture = new();
    private readonly RichTextBox _text = new();
    private readonly Panel _audioPanel = new();
    private readonly Button _audioPlay = new();
    private readonly Label _audioInfo = new();
    private readonly Label _previewCaption = new();
    private readonly ListView _props = new();
    private SplitContainer _previewSplit = null!;

    private byte[]? _currentBytes;
    private ArchiveModel.Item? _current;
    private AssetReferences? _refs;          // built lazily for Find References

    private float _meshYaw = 35f, _meshPitch = 20f, _meshZoom = 1f;
    private bool _meshDragging;
    private Point _meshLast;

    // Fonts follow the display DPI on their own; pixel sizes do not. Everything laid out in pixels is scaled
    // through here so a 150% or 200% display gets the same proportions as a 100% one.
    private int Dp(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    private int GlyphWidth => Dp(16);
    private int IconWidth => _icons.Size + Dp(4);
    private int IndentPerLevel => GlyphWidth + Dp(3);

    private ToolStripMenuItem _miRecent = null!, _miEditOs = null!, _miEditWith = null!;
    private ToolStripMenuItem _miSave = null!, _miSaveAs = null!, _miSaveServer = null!, _miClose = null!;
    private ToolStripMenuItem _miReplace = null!, _miAdd = null!, _miDelete = null!, _miRevert = null!;
    private ToolStripMenuItem _miExtractSel = null!, _miExtractAll = null!, _miValidate = null!;
    private ToolStripMenuItem _miRefs = null!, _miOpenSource = null!, _miClone = null!, _miUnused = null!;
    private ToolStripButton _tbSave = null!, _tbExtract = null!, _tbAdd = null!, _tbRefs = null!, _tbUnused = null!, _tbClone = null!;
    private ColumnHeader _sourceColumn = null!;

    public MainForm(string? openPath)
    {
        _icons = new ShellIcons(_settings.IconSize);
        _edit = new ExternalEdit { Sync = this };
        _edit.Changed += OnExternalEditChanged;

        Text = "RefractorForge Archive";
        AutoScaleMode = AutoScaleMode.None;
        Width = Dp(1280);
        Height = Dp(860);
        Font = Theme.UiFont;
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;
        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += OnDragDrop;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        BuildUi();
        Theme.Apply(this);
        StyleAfterTheme();
        UpdateEnabled();
        SetHeader(null, null);

        if (!string.IsNullOrEmpty(openPath))
        {
            if (File.Exists(openPath)) OpenArchive(openPath);
            else if (Directory.Exists(openPath)) OpenMod(openPath);
        }
    }

    // ── UI ───────────────────────────────────────────────────────────────────

    private void BuildUi()
    {
        var menu = new MenuStrip { Padding = new Padding(Dp(6), Dp(2), 0, Dp(2)) };

        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(new ToolStripMenuItem("&Open archive...", null, (_, _) => PickAndOpen()) { ShortcutKeys = Keys.Control | Keys.O });
        file.DropDownItems.Add(new ToolStripMenuItem("Open &mod (every archive it mounts)...", null, (_, _) => OpenMod(null)) { ShortcutKeys = Keys.Control | Keys.Shift | Keys.O });
        file.DropDownItems.Add(new ToolStripMenuItem("&Pack folder into a new archive...", null, (_, _) => PackFolder()));
        file.DropDownItems.Add(new ToolStripSeparator());
        _miSave = new ToolStripMenuItem("&Save", null, (_, _) => Save(null)) { ShortcutKeys = Keys.Control | Keys.S };
        _miSaveAs = new ToolStripMenuItem("Save &as...", null, (_, _) => SaveAs());
        _miSaveServer = new ToolStripMenuItem("Save a server-side cop&y...", null, (_, _) => SaveServerSide());
        _miClose = new ToolStripMenuItem("&Close", null, (_, _) => CloseArchive()) { ShortcutKeys = Keys.Control | Keys.W };
        _miRecent = new ToolStripMenuItem("&Recent");
        file.DropDownItems.AddRange(new ToolStripItem[]
            { _miSave, _miSaveAs, _miSaveServer, new ToolStripSeparator(), _miRecent, new ToolStripSeparator(), _miClose,
              new ToolStripMenuItem("E&xit", null, (_, _) => Close()) });
        RebuildRecentMenu();

        var edit = new ToolStripMenuItem("&Edit");
        _miEditOs = new ToolStripMenuItem("&Open in the associated program", null, (_, _) => EditExternally(null)) { ShortcutKeys = Keys.Control | Keys.Return };
        _miEditWith = new ToolStripMenuItem("Open &with...", null, (_, _) => EditWithChosenProgram());
        _miReplace = new ToolStripMenuItem("&Replace selected file...", null, (_, _) => ReplaceSelected());
        _miAdd = new ToolStripMenuItem("&Add files...", null, (_, _) => AddFiles()) { ShortcutKeys = Keys.Control | Keys.I };
        _miDelete = new ToolStripMenuItem("&Delete selected", null, (_, _) => DeleteSelected()) { ShortcutKeys = Keys.Delete };
        _miRevert = new ToolStripMenuItem("Re&vert selected", null, (_, _) => RevertSelected());
        _miRefs = new ToolStripMenuItem("Find re&ferences to this file", null, (_, _) => FindReferences()) { ShortcutKeys = Keys.Control | Keys.R };
        _miOpenSource = new ToolStripMenuItem("Open this file's own arc&hive", null, (_, _) => OpenSourceArchive());
        edit.DropDownItems.AddRange(new ToolStripItem[]
            { _miEditOs, _miEditWith, new ToolStripSeparator(), _miReplace, _miAdd, _miDelete, _miRevert,
              new ToolStripSeparator(), _miRefs, _miOpenSource });

        var view = new ToolStripMenuItem("&View");
        view.DropDownItems.Add(new ToolStripMenuItem("&Expand all", null, (_, _) => { _tree.ExpandAll(_model.Items); Refill(); }) { ShortcutKeys = Keys.Control | Keys.E });
        view.DropDownItems.Add(new ToolStripMenuItem("&Collapse all", null, (_, _) => { _tree.CollapseAll(); Refill(); }) { ShortcutKeys = Keys.Control | Keys.Shift | Keys.E });
        view.DropDownItems.Add(new ToolStripSeparator());
        var miProps = new ToolStripMenuItem("&Properties panel") { Checked = true, CheckOnClick = true };
        miProps.Click += (_, _) => _previewSplit.Panel2Collapsed = !miProps.Checked;
        view.DropDownItems.Add(miProps);

        var tools = new ToolStripMenuItem("&Tools");
        tools.DropDownItems.Add(new ToolStripMenuItem("&Search files and contents...", null, (_, _) => OpenSearch()) { ShortcutKeys = Keys.Control | Keys.F });
        tools.DropDownItems.Add(new ToolStripMenuItem("&Compare two archives...", null, (_, _) => OpenDiff()) { ShortcutKeys = Keys.Control | Keys.D });
        _miUnused = new ToolStripMenuItem("&Unused textures and sounds...", null, (_, _) => OpenUnused());
        tools.DropDownItems.Add(_miUnused);
        tools.DropDownItems.Add(new ToolStripMenuItem("Server-side copies of a &level folder...", null, (_, _) => OpenStrip()));
        tools.DropDownItems.Add(new ToolStripSeparator());
        tools.DropDownItems.Add(new ToolStripMenuItem("&New mod...", null, (_, _) => OpenModWizard()));
        _miClone = new ToolStripMenuItem("Clone &object under a new name...", null, (_, _) => OpenClone());
        tools.DropDownItems.Add(_miClone);
        tools.DropDownItems.Add(new ToolStripSeparator());
        _miExtractSel = new ToolStripMenuItem("&Extract selected...", null, (_, _) => Extract(false));
        _miExtractAll = new ToolStripMenuItem("Extract &all...", null, (_, _) => Extract(true));
        _miValidate = new ToolStripMenuItem("&Validate archive", null, (_, _) => ValidateArchive());
        tools.DropDownItems.AddRange(new ToolStripItem[] { _miExtractSel, _miExtractAll, _miValidate });

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(new ToolStripMenuItem("&About", null, (_, _) => ShowAbout()));
        menu.Items.AddRange(new ToolStripItem[] { file, edit, view, tools, help });

        // Toolbar: the actions you reach for, as drawn glyphs that take the palette.
        var bar = new ToolStrip { ImageScalingSize = new Size(Dp(20), Dp(20)), Padding = new Padding(Dp(6), Dp(3), 0, Dp(3)), AutoSize = false, Height = Dp(38) };
        ToolStripButton Tb(string glyph, string text, Action act, Color? c = null)
        {
            var b = new ToolStripButton(text, Theme.Glyph(glyph, Dp(20), c), (_, _) => act()) { DisplayStyle = ToolStripItemDisplayStyle.ImageAndText, ToolTipText = text, Padding = new Padding(Dp(4), 0, Dp(4), 0) };
            bar.Items.Add(b); return b;
        }
        Tb("open", "Open", PickAndOpen);
        Tb("mod", "Open Mod", () => OpenMod(null), Theme.Folder);
        bar.Items.Add(new ToolStripSeparator());
        _tbSave = Tb("save", "Save", () => Save(null));
        bar.Items.Add(new ToolStripSeparator());
        _tbExtract = Tb("extract", "Extract", () => Extract(false));
        _tbAdd = Tb("add", "Add", AddFiles);
        bar.Items.Add(new ToolStripSeparator());
        Tb("search", "Search", OpenSearch, Theme.Accent);
        Tb("diff", "Compare", OpenDiff);
        _tbRefs = Tb("refs", "References", FindReferences);
        _tbUnused = Tb("broom", "Unused", OpenUnused);
        Tb("server", "Server-side", OpenStrip);
        bar.Items.Add(new ToolStripSeparator());
        Tb("wand", "New Mod", OpenModWizard, Theme.Folder);
        _tbClone = Tb("clone", "Clone", OpenClone);

        // Header: what is open, where it lives, and a few numbers.
        var header = new Panel { Dock = DockStyle.Top, Height = Dp(56), Padding = new Padding(Dp(14), Dp(6), Dp(14), Dp(6)), BackColor = Theme.Raised };
        _title.Font = Theme.Title; _title.AutoSize = false; _title.Dock = DockStyle.Top; _title.Height = Dp(26); _title.TextAlign = ContentAlignment.MiddleLeft;
        _subtitle.Font = Theme.Small; _subtitle.AutoSize = false; _subtitle.Dock = DockStyle.Fill; _subtitle.TextAlign = ContentAlignment.MiddleLeft; _subtitle.ForeColor = Theme.TextDim;
        _chips.Font = Theme.Small; _chips.AutoSize = false; _chips.Dock = DockStyle.Right; _chips.Width = Dp(460); _chips.TextAlign = ContentAlignment.MiddleRight; _chips.ForeColor = Theme.TextDim;
        header.Controls.Add(_subtitle); header.Controls.Add(_chips); header.Controls.Add(_title);

        // The list.
        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = true;
        _list.HideSelection = false;
        _list.VirtualMode = true;
        _list.OwnerDraw = true;
        _list.BorderStyle = BorderStyle.None;
        _list.HeaderStyle = ColumnHeaderStyle.Clickable;
        _list.ColumnClick += OnColumnClick;
        _list.ItemDrag += OnItemDrag;
        _list.RetrieveVirtualItem += OnRetrieveVirtualItem;
        _list.DrawColumnHeader += OnDrawColumnHeader;
        _list.DrawItem += (_, e) => e.DrawDefault = false;
        _list.DrawSubItem += OnDrawSubItem;
        _list.SelectedIndexChanged += (_, _) => OnSelectionChanged();
        _list.MouseDown += OnListMouseDown;
        _list.MouseDoubleClick += OnListDoubleClick;
        _list.KeyDown += OnListKeyDown;
        _list.SmallImageList = _icons.Images;
        _list.Columns.Add("Name", Dp(380));
        _list.Columns.Add("Size", Dp(96), HorizontalAlignment.Right);
        _list.Columns.Add("Packed", Dp(96), HorizontalAlignment.Right);
        _list.Columns.Add("Ratio", Dp(64), HorizontalAlignment.Right);
        _list.Columns.Add("Offset", Dp(104), HorizontalAlignment.Right);
        _list.Columns.Add("Status", Dp(96));
        _sourceColumn = _list.Columns.Add("Archive", 0);
        // The name column takes whatever the others leave, so the list never shows a bare strip on the right.
        _list.Resize += (_, _) => FitNameColumn();

        var ctx = new ContextMenuStrip();
        ctx.Items.Add("Open in the associated program", null, (_, _) => EditExternally(null));
        ctx.Items.Add("Extract selected...", null, (_, _) => Extract(false));
        ctx.Items.Add(new ToolStripSeparator());
        ctx.Items.Add("Find references to this file", null, (_, _) => FindReferences());
        ctx.Items.Add("Open this file's own archive", null, (_, _) => OpenSourceArchive());
        ctx.Items.Add("Clone this object...", null, (_, _) => OpenClone());
        ctx.Items.Add(new ToolStripSeparator());
        ctx.Items.Add("Replace...", null, (_, _) => ReplaceSelected());
        ctx.Items.Add("Delete", null, (_, _) => DeleteSelected());
        ctx.Items.Add("Revert", null, (_, _) => RevertSelected());
        ctx.Items.Add(new ToolStripSeparator());
        ctx.Items.Add("Copy path", null, (_, _) => { if (Selected().FirstOrDefault() is { } it) Clipboard.SetText(it.Name); });
        ctx.Renderer = new Theme.DarkRenderer();
        _list.ContextMenuStrip = ctx;

        // Filter along the bottom of the list.
        var searchBar = new Panel { Dock = DockStyle.Bottom, Height = Dp(34), Padding = new Padding(Dp(8), Dp(4), Dp(8), Dp(4)), BackColor = Theme.Raised };
        var searchLabel = new Label { Text = "Filter", Dock = DockStyle.Left, Width = Dp(46), TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.TextDim };
        _search.Dock = DockStyle.Fill;
        _search.PlaceholderText = "type to filter by name  -  Ctrl+F searches inside files";
        _search.TextChanged += (_, _) => Refill();
        var searchClear = new Button { Text = "Clear", Dock = DockStyle.Right, Width = Dp(64) };
        searchClear.Click += (_, _) => _search.Text = string.Empty;
        searchBar.Controls.Add(_search); searchBar.Controls.Add(searchLabel); searchBar.Controls.Add(searchClear);

        var listPanel = new Panel { Dock = DockStyle.Fill };
        listPanel.Controls.Add(_list);
        listPanel.Controls.Add(searchBar);

        // Layers (only for a mod view).
        _layersHost.Dock = DockStyle.Fill;
        var layersTitle = new Label { Text = "MOUNTED ARCHIVES", Dock = DockStyle.Top, Height = Dp(26), Font = Theme.Small, ForeColor = Theme.TextDim, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(Dp(10), 0, 0, 0), BackColor = Theme.Raised };
        var layersHint = new Label { Text = "Top wins. Untick to hide a layer's files.", Dock = DockStyle.Bottom, Height = Dp(30), Font = Theme.Small, ForeColor = Theme.TextFaint, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(Dp(10), 0, 0, 0) };
        _layers.Dock = DockStyle.Fill; _layers.BorderStyle = BorderStyle.None; _layers.CheckOnClick = true; _layers.IntegralHeight = false;
        // ItemCheck fires for every Items.Add while the panel is being filled - 332 times for the base game -
        // and before the window has a handle when a mod is opened from the command line. Only a user's tick
        // counts, and the check state is not yet applied inside the event, hence the deferral.
        _layers.ItemCheck += (_, e) => { if (!_fillingLayers && IsHandleCreated) BeginInvoke(() => ApplyLayerVisibility()); };
        _layers.Font = Theme.Small;
        _layersHost.Controls.Add(_layers); _layersHost.Controls.Add(layersHint); _layersHost.Controls.Add(layersTitle);

        BuildPreviewHost();

        // Properties beside the preview.
        _props.Dock = DockStyle.Fill; _props.View = View.Details; _props.HeaderStyle = ColumnHeaderStyle.None;
        _props.BorderStyle = BorderStyle.None; _props.FullRowSelect = true; _props.Font = Theme.Small;
        _props.Columns.Add("k", Dp(118)); _props.Columns.Add("v", Dp(400));
        _props.Resize += (_, _) => { if (_props.Columns.Count == 2) _props.Columns[1].Width = Math.Max(Dp(120), _props.ClientSize.Width - _props.Columns[0].Width - Dp(4)); };
        var propsHost = new Panel { Dock = DockStyle.Fill };
        var propsTitle = new Label { Text = "PROPERTIES", Dock = DockStyle.Top, Height = Dp(26), Font = Theme.Small, ForeColor = Theme.TextDim, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(Dp(10), 0, 0, 0), BackColor = Theme.Raised };
        propsHost.Controls.Add(_props); propsHost.Controls.Add(propsTitle);

        _previewSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = Dp(4) };
        _previewSplit.Panel1.Controls.Add(_previewHost);
        _previewSplit.Panel2.Controls.Add(propsHost);

        var vertical = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = Dp(4) };
        vertical.Panel1.Controls.Add(listPanel);
        vertical.Panel2.Controls.Add(_previewSplit);

        _outer = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = Dp(4), Panel1Collapsed = true };
        _outer.Panel1.Controls.Add(_layersHost);
        _outer.Panel2.Controls.Add(vertical);

        var strip = new StatusStrip { Padding = new Padding(Dp(8), 0, Dp(8), 0) };
        _status.Spring = true; _status.TextAlign = ContentAlignment.MiddleLeft;
        strip.Items.Add(_status); strip.Items.Add(_statusRight);

        Controls.Add(_outer);
        Controls.Add(header);
        Controls.Add(bar);
        Controls.Add(menu);
        Controls.Add(strip);
        MainMenuStrip = menu;
        _outer.BringToFront();
        Shown += (_, _) =>
        {
            vertical.SplitterDistance = (int)(vertical.Height * 0.55);
            _previewSplit.SplitterDistance = Math.Max(Dp(200), _previewSplit.Width - Dp(360));
            _outer.SplitterDistance = Dp(270);
            FitNameColumn();
        };
    }

    private void BuildPreviewHost()
    {
        _previewHost.Dock = DockStyle.Fill;
        _previewHost.BackColor = Theme.Surface;

        _previewCaption.Dock = DockStyle.Top;
        _previewCaption.Height = Dp(26);
        _previewCaption.TextAlign = ContentAlignment.MiddleLeft;
        _previewCaption.BackColor = Theme.Raised;
        _previewCaption.ForeColor = Theme.TextDim;
        _previewCaption.Font = Theme.Small;
        _previewCaption.Padding = new Padding(Dp(10), 0, 0, 0);

        _picture.Dock = DockStyle.Fill;
        _picture.SizeMode = PictureBoxSizeMode.Zoom;
        _picture.BackColor = Color.FromArgb(18, 20, 24);
        _picture.Visible = false;
        _picture.MouseDown += (_, e) =>
        {
            if (Preview.KindOf(_current?.Name ?? "") == PreviewKind.Mesh) { _meshDragging = true; _meshLast = e.Location; }
        };
        _picture.MouseUp += (_, _) => _meshDragging = false;
        _picture.MouseMove += OnMeshDrag;
        _picture.MouseWheel += OnMeshWheel;
        _picture.MouseEnter += (_, _) => { if (_picture.Visible) _picture.Focus(); };

        _text.Dock = DockStyle.Fill;
        _text.ReadOnly = true;
        _text.WordWrap = false;
        _text.Font = Theme.Mono;
        _text.DetectUrls = false;
        _text.Visible = false;
        _text.MouseDoubleClick += (_, _) => JumpFromText();

        _audioPanel.Dock = DockStyle.Fill;
        _audioPanel.Visible = false;
        _audioPlay.Text = "Play";
        _audioPlay.SetBounds(Dp(14), Dp(14), Dp(100), Dp(32));
        _audioPlay.Click += (_, _) => ToggleAudio();
        _audioInfo.SetBounds(Dp(126), Dp(20), Dp(700), Dp(22));
        _audioPanel.Controls.Add(_audioPlay);
        _audioPanel.Controls.Add(_audioInfo);
        _audio.Stopped += (_, _) => BeginInvoke(() => _audioPlay.Text = "Play");

        _previewHost.Controls.Add(_picture);
        _previewHost.Controls.Add(_text);
        _previewHost.Controls.Add(_audioPanel);
        _previewHost.Controls.Add(_previewCaption);
    }

    private void FitNameColumn()
    {
        if (_list.Columns.Count == 0) return;
        int others = 0;
        for (int i = 1; i < _list.Columns.Count; i++) others += _list.Columns[i].Width;
        int w = _list.ClientSize.Width - others - Dp(4);
        if (w > Dp(160)) _list.Columns[0].Width = w;
    }

    /// <summary>What Theme.Apply cannot reach generically.</summary>
    private void StyleAfterTheme()
    {
        Theme.StyleButton(_audioPlay, primary: true);
        _layers.BackColor = Theme.Surface; _layers.ForeColor = Theme.Text;
        _props.BackColor = Theme.Surface; _props.ForeColor = Theme.Text;
        _list.BackColor = Theme.Surface;
        _text.BackColor = Theme.Surface;
    }

    private void SetHeader(string? title, string? subtitle)
    {
        _title.Text = title ?? "Nothing open";
        _subtitle.Text = subtitle ?? "Open an archive, or a whole mod, to begin.  Drop an .rfa or a folder here.";
        _title.ForeColor = title is null ? Theme.TextDim : Theme.Text;
    }

    // ── Drawing the tree into the list ───────────────────────────────────────

    private void OnRetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        var lvi = new ListViewItem(string.Empty);
        for (int i = 1; i < _list.Columns.Count; i++) lvi.SubItems.Add(string.Empty);
        e.Item = lvi;
    }

    private TreeModel.Row? RowAt(int index) =>
        index >= 0 && index < _tree.Rows.Count ? _tree.Rows[index] : null;

    private void OnDrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        using (var bg = new SolidBrush(Theme.Raised)) e.Graphics.FillRectangle(bg, e.Bounds);
        using (var pen = new Pen(Theme.Border))
        {
            e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            e.Graphics.DrawLine(pen, e.Bounds.Right - 1, e.Bounds.Top + 6, e.Bounds.Right - 1, e.Bounds.Bottom - 6);
        }
        using var fmt = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        fmt.Alignment = e.Header?.TextAlign == HorizontalAlignment.Right ? StringAlignment.Far : StringAlignment.Near;
        using var br = new SolidBrush(Theme.TextDim);
        string txt = e.Header?.Text ?? "";
        if (e.ColumnIndex == _sortColumn) txt += _sortDescending ? "  v" : "  ^";
        var r = new Rectangle(e.Bounds.Left + 6, e.Bounds.Top, Math.Max(e.Bounds.Width - 12, 4), e.Bounds.Height);
        e.Graphics.DrawString(txt, Theme.Small, br, r, fmt);
    }

    private void OnDrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        var row = RowAt(e.ItemIndex);
        if (row is null) return;

        bool selected = e.Item?.Selected == true;
        bool focused = _list.Focused;
        var backColor = selected ? (focused ? Theme.Selection : Theme.AccentDim) : (e.ItemIndex % 2 == 1 ? Theme.Stripe : Theme.Surface);
        using (var bg = new SolidBrush(backColor)) e.Graphics.FillRectangle(bg, e.Bounds);

        Color fore = Theme.Text;
        if (row.IsFolder) fore = Theme.Folder;
        else if (row.Item is not null && Theme.StateColor(row.Item.State) is { } sc) fore = sc;

        using var fmt = new StringFormat
        {
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        using var brush = new SolidBrush(fore);

        if (e.ColumnIndex == 0)
        {
            int x = e.Bounds.Left + Dp(4) + row.Depth * IndentPerLevel;
            if (row.IsFolder)
                DrawGlyph(e.Graphics, new Rectangle(x, e.Bounds.Top, GlyphWidth, e.Bounds.Height), _tree.IsExpanded(row.Path));
            x += GlyphWidth;

            int iconIdx = row.IsFolder
                ? (_tree.IsExpanded(row.Path) ? ShellIcons.FolderOpen : ShellIcons.FolderClosed)
                : _icons.ForFile(row.Display);
            if (iconIdx >= 0 && iconIdx < _icons.Images.Images.Count)
                _icons.Images.Draw(e.Graphics, x, e.Bounds.Top + (e.Bounds.Height - _icons.Size) / 2, iconIdx);
            x += IconWidth;

            fmt.Alignment = StringAlignment.Near;
            var textRect = new Rectangle(x, e.Bounds.Top, Math.Max(e.Bounds.Right - x - 2, 4), e.Bounds.Height);
            e.Graphics.DrawString(row.Display, row.IsFolder ? Theme.UiBold : _list.Font, brush, textRect, fmt);
            return;
        }

        string text = ColumnText(row, e.ColumnIndex);
        if (text.Length == 0) return;

        var col = e.ColumnIndex == 5 && row.Item is { Overrides: > 0 } ? Theme.Overridden
                : e.ColumnIndex >= 1 && !row.IsFolder && fore == Theme.Text ? Theme.TextDim : fore;
        using var dim = new SolidBrush(col);
        fmt.Alignment = _list.Columns[e.ColumnIndex].TextAlign == HorizontalAlignment.Right ? StringAlignment.Far : StringAlignment.Near;
        var r = new Rectangle(e.Bounds.Left + 4, e.Bounds.Top, Math.Max(e.Bounds.Width - 8, 4), e.Bounds.Height);
        e.Graphics.DrawString(text, _list.Font, dim, r, fmt);
    }

    private string ColumnText(TreeModel.Row row, int col)
    {
        if (row.IsFolder)
        {
            return col switch
            {
                1 => row.TotalSize.ToString("N0"),
                2 => row.TotalPacked.ToString("N0"),
                3 => row.TotalSize > 0 ? $"{100.0 * row.TotalPacked / row.TotalSize:0}%" : "",
                5 => $"{row.FileCount:N0} file(s)",
                _ => "",
            };
        }

        var it = row.Item!;
        return col switch
        {
            1 => it.UncompressedSize.ToString("N0"),
            2 => it.BlockSize.ToString("N0"),
            3 => it.UncompressedSize > 0 ? $"{100.0 * it.BlockSize / it.UncompressedSize:0}%" : "",
            4 => it.State == ArchiveModel.EntryState.Added ? "" : "0x" + it.Offset.ToString("X8"),
            5 => it.State switch
            {
                ArchiveModel.EntryState.Added => "added",
                ArchiveModel.EntryState.Replaced => "replaced",
                _ => it.Overrides > 0 ? $"shadows {it.Overrides}" : it.IsCompressed ? "packed" : "stored",
            },
            6 => it.Source ?? "",
            _ => "",
        };
    }

    private static void DrawGlyph(Graphics g, Rectangle bounds, bool expanded)
    {
        var c = new Point(bounds.Left + 6, bounds.Top + bounds.Height / 2);
        using var pen = new Pen(Theme.TextDim, 1.6f);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        if (expanded)
            g.DrawLines(pen, new[] { new Point(c.X - 3, c.Y - 2), new Point(c.X, c.Y + 2), new Point(c.X + 3, c.Y - 2) });
        else
            g.DrawLines(pen, new[] { new Point(c.X - 2, c.Y - 3), new Point(c.X + 2, c.Y), new Point(c.X - 2, c.Y + 3) });
    }

    private bool HitGlyph(TreeModel.Row row, int x)
    {
        if (!row.IsFolder) return false;
        int left = Dp(4) + row.Depth * IndentPerLevel;
        return x >= left && x < left + GlyphWidth;
    }

    private void OnListMouseDown(object? sender, MouseEventArgs e)
    {
        var hit = _list.HitTest(e.Location);
        var row = RowAt(hit.Item?.Index ?? -1);
        if (row is null) return;
        if (e.Button == MouseButtons.Left && HitGlyph(row, e.X))
        {
            _tree.Toggle(row.Path);
            Refill(keepSelectionPath: row.Path);
        }
    }

    private void OnListDoubleClick(object? sender, MouseEventArgs e)
    {
        var hit = _list.HitTest(e.Location);
        var row = RowAt(hit.Item?.Index ?? -1);
        if (row is { IsFolder: true })
        {
            _tree.Toggle(row.Path);
            Refill(keepSelectionPath: row.Path);
        }
        else if (row?.Item is not null) EditExternally(null);
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        var row = SelectedRows().FirstOrDefault();
        if (row is null || !row.IsFolder) return;
        if (e.KeyCode == Keys.Left && _tree.IsExpanded(row.Path))
        { _tree.SetExpanded(row.Path, false); Refill(keepSelectionPath: row.Path); e.Handled = true; }
        else if (e.KeyCode == Keys.Right && !_tree.IsExpanded(row.Path))
        { _tree.SetExpanded(row.Path, true); Refill(keepSelectionPath: row.Path); e.Handled = true; }
    }

    private IEnumerable<TreeModel.Row> SelectedRows()
    {
        foreach (int i in _list.SelectedIndices)
            if (RowAt(i) is { } r) yield return r;
    }

    private IEnumerable<ArchiveModel.Item> Selected() =>
        SelectedRows().Where(r => r.Item is not null).Select(r => r.Item!);

    private IEnumerable<ArchiveModel.Item> VisibleItems() =>
        _model.Items.Where(i => !_model.HiddenLayers.Contains(i.LayerIndex));

    private void Refill(string? keepSelectionPath = null)
    {
        _tree.Build(VisibleItems(), _search.Text.Trim());
        _list.VirtualListSize = _tree.Rows.Count;
        _list.Invalidate();

        if (keepSelectionPath is not null)
        {
            int idx = -1;
            for (int i = 0; i < _tree.Rows.Count; i++)
                if (string.Equals(_tree.Rows[i].Path, keepSelectionPath, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
            if (idx >= 0)
            {
                _list.SelectedIndices.Clear();
                _list.SelectedIndices.Add(idx);
                _list.EnsureVisible(idx);
            }
        }
        UpdateStatus();
    }

    /// <summary>Select and reveal one file by its archive path - the target of every "double-click to jump".</summary>
    public void GoTo(string name)
    {
        if (_model.Find(name) is not { } it) return;
        if (it.LayerIndex >= 0 && _model.HiddenLayers.Contains(it.LayerIndex))
        {
            _model.HiddenLayers.Remove(it.LayerIndex);
            for (int i = 0; i < _layers.Items.Count; i++) if (i == it.LayerIndex) _layers.SetItemChecked(i, true);
        }
        _search.Text = string.Empty;
        _tree.RevealPath(it.Name);
        Refill(keepSelectionPath: it.Name);
        _list.Focus();
        Activate();
    }

    // ── Opening / closing ────────────────────────────────────────────────────

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            if (Directory.Exists(paths[0]))
            {
                // A mod folder opens as a mod; any other folder is offered for packing.
                if (File.Exists(Path.Combine(paths[0], "init.con")) || Directory.Exists(Path.Combine(paths[0], "Archives"))) OpenMod(paths[0]);
                else PackFolder(paths[0]);
                return;
            }
            if (paths.Length > 1 && _model.IsOpen && !_model.IsWorkspace) { AddFilesFrom(paths); return; }
            OpenArchive(paths[0]);
        }
    }

    private void PickAndOpen()
    {
        using var d = new OpenFileDialog { Title = "Open a Refractor Flat Archive", Filter = "Refractor archives (*.rfa)|*.rfa|All files (*.*)|*.*" };
        if (d.ShowDialog(this) == DialogResult.OK) OpenArchive(d.FileName);
    }

    private void OpenArchive(string path, string? thenSelect = null)
    {
        if (!ConfirmDiscard()) return;
        try
        {
            Cursor = Cursors.WaitCursor;
            _model.Open(path);
            _refs = null;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open this archive.\r\n\r\n{ex.Message}", "Open failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        finally { Cursor = Cursors.Default; }

        Text = $"{Path.GetFileName(path)} - RefractorForge Archive";
        SetHeader(Path.GetFileName(path), path);
        _settings.AddRecent(path);
        RebuildRecentMenu();
        _search.Text = string.Empty;
        _outer.Panel1Collapsed = true;
        _sourceColumn.Width = 0;
        FitNameColumn();
        _tree.ExpandTopLevel(_model.Items);
        Refill();
        UpdateEnabled();
        _list.Select();
        if (thenSelect is not null) GoTo(thenSelect);
    }

    /// <summary>Open every archive a mod mounts, as one file system.</summary>
    private void OpenMod(string? modDir)
    {
        if (!ConfirmDiscard()) return;
        if (modDir is null)
        {
            modDir = Ui.PickFolder(this, "Pick a mod folder (Mods\\<name>, holds init.con) - or the game folder to see the base game");
            if (modDir is null) return;
        }
        string? gameRoot = ModChain.FindGameRoot(modDir);
        List<(string Path, string Mod)> layers;
        string label;
        try
        {
            Cursor = Cursors.WaitCursor;
            if (gameRoot is not null && File.Exists(Path.Combine(modDir, "init.con")))
            {
                var chain = ModChain.Resolve(gameRoot, modDir);
                layers = ModWorkspace.LayersForChain(chain);
                label = new DirectoryInfo(modDir).Name;
            }
            else
            {
                layers = ModWorkspace.LayersFor(modDir).Select(p => (p, new DirectoryInfo(modDir).Name)).ToList();
                label = new DirectoryInfo(modDir).Name;
            }
            if (layers.Count == 0)
            {
                MessageBox.Show(this, "No .rfa archives were found under that folder.", "Nothing to open", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var ws = ModWorkspace.Open(layers);
            _model.OpenWorkspace(ws, label);
            _refs = null;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open the mod.\r\n\r\n{ex.Message}", "Open failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        finally { Cursor = Cursors.Default; }

        var wsp = _model.Workspace!;
        Text = $"{label} (mod) - RefractorForge Archive";
        SetHeader($"{label}  -  {wsp.Layers.Count} archives", modDir);
        _fillingLayers = true;
        _layers.Items.Clear();
        foreach (var l in wsp.Layers)
            _layers.Items.Add($"{l.Mod}  /  {l.Label}   ({l.Archive.Entries.Count:N0})", true);
        _fillingLayers = false;
        _outer.Panel1Collapsed = false;
        _sourceColumn.Width = Dp(150);
        FitNameColumn();
        _search.Text = string.Empty;
        _tree.ExpandTopLevel(_model.Items);
        Refill();
        UpdateEnabled();
        _list.Select();
    }

    private void ApplyLayerVisibility()
    {
        _model.HiddenLayers.Clear();
        for (int i = 0; i < _layers.Items.Count; i++)
            if (!_layers.GetItemChecked(i)) _model.HiddenLayers.Add(i);
        Refill();
    }

    /// <summary>From the mod view to the single archive that owns the selected file, with it selected.</summary>
    private void OpenSourceArchive()
    {
        var it = Selected().FirstOrDefault();
        if (it is null || it.LayerIndex < 0 || _model.Workspace is null) return;
        var path = _model.Workspace.Layers[it.LayerIndex].Path;
        OpenArchive(path, thenSelect: it.Name);
    }

    private void CloseArchive()
    {
        if (!ConfirmDiscard()) return;
        _audio.Stop();
        _model.Close();
        _refs = null;
        _tree.CollapseAll();
        _layers.Items.Clear();
        _outer.Panel1Collapsed = true;
        _sourceColumn.Width = 0;
        Refill();
        ShowPreview(PreviewKind.None, null, null);
        _props.Items.Clear();
        Text = "RefractorForge Archive";
        SetHeader(null, null);
        UpdateEnabled();
    }

    private bool ConfirmDiscard()
    {
        if (!_model.IsDirty) return true;
        return MessageBox.Show(this, "This archive has unsaved changes. Discard them?", "Unsaved changes",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!ConfirmDiscard()) e.Cancel = true;
        else { _audio.Dispose(); _edit.Dispose(); _model.Dispose(); _icons.Dispose(); }
        base.OnFormClosing(e);
    }

    // ── Preview + properties ─────────────────────────────────────────────────

    private void OnSelectionChanged()
    {
        UpdateEnabled();
        var rows = SelectedRows().Take(2).ToList();
        if (rows.Count != 1 || rows[0].Item is null)
        {
            ShowPreview(PreviewKind.None, null, null);
            if (rows.Count == 1 && rows[0].IsFolder) FillFolderProperties(rows[0]); else _props.Items.Clear();
            return;
        }
        ShowFor(rows[0].Item!);
    }

    private void ShowFor(ArchiveModel.Item item)
    {
        _audio.Stop();
        _audioPlay.Text = "Play";
        _current = item;
        try { _currentBytes = _model.Read(item); }
        catch (Exception ex)
        {
            _currentBytes = null;
            ShowPreview(PreviewKind.Text, $"{item.Name} - could not be read", ex.Message);
            return;
        }

        var kind = Preview.KindOf(item.Name);
        string caption = $"{item.Name}   -   {item.UncompressedSize:N0} bytes";

        switch (kind)
        {
            case PreviewKind.Mesh:
                _meshYaw = 35f; _meshPitch = 20f; _meshZoom = 1f;
                RenderMeshPreview();
                break;

            case PreviewKind.Image when Path.GetExtension(item.Name).Equals(".raw", StringComparison.OrdinalIgnoreCase):
            {
                var raw = MeshPreview.RenderRaw(_currentBytes, item.Name, 1024, out var rinfo);
                if (raw is null || rinfo is null)
                {
                    ShowPreview(PreviewKind.Text, caption + "   (not a square 8- or 16-bit map)", Preview.ToHexDump(_currentBytes));
                    break;
                }
                _picture.Image?.Dispose();
                _picture.Image = raw;
                ShowPreview(PreviewKind.Image, $"{caption}   -   {rinfo.Side} x {rinfo.Side} {(rinfo.SixteenBit ? "16-bit heightmap" : "8-bit index map")}, range {rinfo.Min}-{rinfo.Max}", null);
                break;
            }

            case PreviewKind.Image:
            {
                var bmp = Preview.ToBitmap(item.Name, _currentBytes);
                if (bmp is null)
                {
                    ShowPreview(PreviewKind.Text, caption + "   (image could not be decoded)", Preview.ToHexDump(_currentBytes));
                    break;
                }
                _picture.Image?.Dispose();
                _picture.Image = bmp;
                ShowPreview(PreviewKind.Image, $"{caption}   -   {bmp.Width} x {bmp.Height}", null);
                break;
            }

            case PreviewKind.Text:
                ShowPreview(PreviewKind.Text, caption + (ConSyntax.Handles(item.Name) ? "   -   double-click a template name to search for it" : ""), Preview.ToText(_currentBytes), item.Name);
                break;

            case PreviewKind.Audio:
                _audioInfo.Text = $"{item.FileName}  -  {ImageImport.DescribeWav(_currentBytes) ?? _currentBytes.Length.ToString("N0") + " bytes"}";
                ShowPreview(PreviewKind.Audio, caption, null);
                break;

            default:
                ShowPreview(PreviewKind.Text, caption, Preview.ToHexDump(_currentBytes));
                break;
        }
        FillProperties(item, _currentBytes);
    }

    private void FillProperties(ArchiveModel.Item it, byte[]? data)
    {
        _props.BeginUpdate();
        _props.Items.Clear();
        void P(string k, string v) => _props.Items.Add(new ListViewItem(new[] { k, v }));
        P("Name", it.FileName);
        P("Folder", it.Folder.Length == 0 ? "(root)" : it.Folder);
        P("Size", $"{it.UncompressedSize:N0} bytes  ({Ui.Human(it.UncompressedSize)})");
        if (it.State != ArchiveModel.EntryState.Added)
        {
            P("Packed", $"{it.BlockSize:N0} bytes" + (it.UncompressedSize > 0 ? $"  ({100.0 * it.BlockSize / it.UncompressedSize:0}%)" : ""));
            P("Stored", it.IsCompressed ? "LZO1X compressed" : "uncompressed");
            P("Offset", "0x" + it.Offset.ToString("X8"));
        }
        if (it.State != ArchiveModel.EntryState.Unchanged) P("Pending", it.State.ToString().ToLowerInvariant() + " - not yet saved");
        if (it.Source is not null)
        {
            P("Archive", it.Source + (it.SourceMod is not null ? $"  ({it.SourceMod})" : ""));
            if (it.Overrides > 0 && _model.Workspace is { } ws && ws.Find(it.Name) is { } f)
                P("Shadows", string.Join(", ", f.Overridden.Select(i => ws.Layers[i].Label)));
        }
        if (data is not null)
        {
            var ext = Path.GetExtension(it.Name).ToLowerInvariant();
            try
            {
                switch (ext)
                {
                    case ".dds": P("Texture", ImageImport.DescribeDds(data)); break;
                    case ".wav": if (ImageImport.DescribeWav(data) is { } w) P("Audio", w); break;
                    case ".sm":
                        if (StandardMesh.TryParse(data, out var sm) && sm is not null)
                        {
                            P("Mesh", $"version {sm.Version}, {sm.NumLods} LOD(s), {sm.NumCollisionMeshes} collision mesh(es)");
                            var lod0 = sm.Lods.Count > 0 ? sm.Lods[0] : Array.Empty<SmMaterial>();
                            P("LOD 0", $"{lod0.Count} material(s), {lod0.Sum(m => m.NumVertices):N0} verts, {lod0.Sum(m => m.Faces.Length):N0} tris");
                            if (lod0.Count > 0) P("Vertex format", $"{lod0[0].VertexFormat} / {lod0[0].VertexByteSize} B" + (lod0[0].HasLightmapUv ? "  (lightmap UVs)" : "  (no lightmap UVs)"));
                            foreach (var m in lod0.Take(8)) P("  material", m.Name);
                            var bb = sm.BoundingBox;
                            P("Bounds", $"{bb[3] - bb[0]:0.##} x {bb[4] - bb[1]:0.##} x {bb[5] - bb[2]:0.##} m");
                        }
                        break;
                    case ".con": case ".rs": case ".ssc": case ".inc":
                    {
                        var txt = Encoding.Latin1.GetString(data);
                        int lines = txt.Count(c => c == '\n') + 1;
                        var creates = System.Text.RegularExpressions.Regex.Matches(txt, @"(?im)^\s*ObjectTemplate\.create\s+(\S+)\s+(\S+)");
                        P("Script", $"{lines:N0} line(s), {creates.Count} template(s) declared");
                        foreach (System.Text.RegularExpressions.Match m in creates.Take(10)) P("  " + m.Groups[1].Value, m.Groups[2].Value);
                        var runs = System.Text.RegularExpressions.Regex.Matches(txt, @"(?im)^\s*run\s+(\S+)");
                        if (runs.Count > 0) P("Runs", string.Join(", ", runs.Cast<System.Text.RegularExpressions.Match>().Take(12).Select(m => m.Groups[1].Value)));
                        break;
                    }
                    case ".raw":
                    {
                        int side16 = (int)Math.Sqrt(data.Length / 2), side8 = (int)Math.Sqrt(data.Length);
                        if (side16 * side16 * 2 == data.Length) P("Map", $"{side16} x {side16}, 16-bit");
                        else if (side8 * side8 == data.Length) P("Map", $"{side8} x {side8}, 8-bit");
                        break;
                    }
                }
            }
            catch { }
        }
        _props.EndUpdate();
    }

    private void FillFolderProperties(TreeModel.Row row)
    {
        _props.BeginUpdate(); _props.Items.Clear();
        _props.Items.Add(new ListViewItem(new[] { "Folder", row.Path }));
        _props.Items.Add(new ListViewItem(new[] { "Files", row.FileCount.ToString("N0") }));
        _props.Items.Add(new ListViewItem(new[] { "Size", $"{Ui.Human(row.TotalSize)}  ->  {Ui.Human(row.TotalPacked)} packed" }));
        var byExt = VisibleItems().Where(i => i.Name.StartsWith(row.Path + "/", StringComparison.OrdinalIgnoreCase))
            .GroupBy(i => Path.GetExtension(i.Name).ToLowerInvariant()).OrderByDescending(g => g.Count()).Take(8);
        foreach (var g in byExt) _props.Items.Add(new ListViewItem(new[] { "  " + (g.Key.Length == 0 ? "(none)" : g.Key), $"{g.Count():N0}" }));
        _props.EndUpdate();
    }

    private void RenderMeshPreview()
    {
        if (_currentBytes is null || _current is null) return;
        int w = Math.Max(_previewHost.ClientSize.Width, 64);
        int h = Math.Max(_previewHost.ClientSize.Height - _previewCaption.Height, 64);

        Bitmap? bmp; MeshPreview.MeshInfo? mi;
        try { bmp = MeshPreview.RenderMesh(_currentBytes, w, h, _meshYaw, _meshPitch, _meshZoom, out mi); }
        catch { bmp = null; mi = null; }

        if (bmp is null)
        {
            ShowPreview(PreviewKind.Text, $"{_current.Name}   (not a readable StandardMesh)", Preview.ToHexDump(_currentBytes));
            return;
        }
        _picture.Image?.Dispose();
        _picture.Image = bmp;
        string info = mi is null ? string.Empty
            : $"   -   LOD 0 of {mi.Lods}, {mi.Materials} material(s), {mi.Vertices:N0} verts, {mi.Triangles:N0} tris   -   drag to orbit, wheel to zoom";
        ShowPreview(PreviewKind.Mesh, $"{_current.Name}   -   {_current.UncompressedSize:N0} bytes{info}", null);
    }

    private void OnMeshDrag(object? sender, MouseEventArgs e)
    {
        if (!_meshDragging || _current is null) return;
        _meshYaw -= (e.X - _meshLast.X) * 0.5f;
        _meshPitch = Math.Clamp(_meshPitch + (e.Y - _meshLast.Y) * 0.5f, -85f, 85f);
        _meshLast = e.Location;
        RenderMeshPreview();
    }

    private void OnMeshWheel(object? sender, MouseEventArgs e)
    {
        if (_current is null || Preview.KindOf(_current.Name) != PreviewKind.Mesh) return;
        _meshZoom = Math.Clamp(_meshZoom * (e.Delta > 0 ? 0.88f : 1.14f), 0.15f, 8f);
        RenderMeshPreview();
    }

    private void ShowPreview(PreviewKind kind, string? caption, string? text, string? nameForSyntax = null)
    {
        _previewCaption.Text = caption ?? string.Empty;
        bool onPicture = kind is PreviewKind.Image or PreviewKind.Mesh;
        _picture.SizeMode = kind == PreviewKind.Mesh ? PictureBoxSizeMode.CenterImage : PictureBoxSizeMode.Zoom;
        _picture.Visible = onPicture;
        _audioPanel.Visible = kind == PreviewKind.Audio;
        _text.Visible = kind == PreviewKind.Text;
        if (text is not null)
        {
            if (nameForSyntax is not null && ConSyntax.Handles(nameForSyntax) && text.Length < 600_000) ConSyntax.Colorize(_text, text);
            else { _text.Clear(); _text.Font = Theme.Mono; _text.ForeColor = Theme.Text; _text.Text = text; }
        }
        if (!onPicture && _picture.Image is not null) { _picture.Image.Dispose(); _picture.Image = null; }
    }

    /// <summary>Double-click a template name in a script: search everything open for it.</summary>
    private void JumpFromText()
    {
        if (_current is null || !ConSyntax.Handles(_current.Name)) return;
        int line = _text.GetLineFromCharIndex(_text.SelectionStart);
        if (line < 0 || line >= _text.Lines.Length) return;
        var name = ConSyntax.TemplateAt(_text.Lines[line]) ?? _text.SelectedText.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 60) return;
        var f = new SearchForm(_model, GoTo);
        f.Show(this);
        f.Prefill(null, name);
    }

    private void ToggleAudio()
    {
        if (_audio.IsPlaying) { _audio.Stop(); _audioPlay.Text = "Play"; return; }
        if (_currentBytes is null) return;
        try { _audio.Play(_currentBytes); _audioPlay.Text = "Stop"; }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not play this sound.\r\n\r\n{ex.Message}", "Playback failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ── Tools ────────────────────────────────────────────────────────────────

    private void OpenSearch()
    {
        if (!_model.IsOpen) return;
        new SearchForm(_model, GoTo).Show(this);
    }

    private void OpenDiff()
    {
        string? a = _model.IsWorkspace ? null : _model.Path;
        new DiffForm(a, null, GoTo).Show(this);
    }

    private void FindReferences()
    {
        var it = Selected().FirstOrDefault();
        if (it is null) return;
        try
        {
            if (_refs is null)
            {
                Cursor = Cursors.WaitCursor;
                _statusRight.Text = "Indexing references...";
                IEnumerable<RefractorFlatArchive> arcs = _model.Workspace is { } ws ? ws.Layers.Select(l => l.Archive)
                                                     : _model.Archive is { } a ? new[] { a } : Array.Empty<RefractorFlatArchive>();
                _refs = AssetReferences.Build(arcs);
            }
        }
        finally { Cursor = Cursors.Default; _statusRight.Text = ""; }
        var rows = _refs.ReferencesTo(it.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                        .Select(n => (n, _model.Find(n)?.Source ?? "")).ToList();
        string title = $"References to {it.FileName}";
        string summary = rows.Count == 0
            ? $"Nothing in {(_model.IsWorkspace ? "this mod" : "this archive")} names \"{AssetReferences.Key(it.Name)}\"." + (_model.IsWorkspace ? "" : "  Open the whole mod to search across every archive it mounts.")
            : $"{rows.Count} file(s) mention \"{AssetReferences.Key(it.Name)}\"  -  matched by base name, the way the engine resolves it. Double-click to jump.";
        new FileListForm(title, summary, rows, GoTo).Show(this);
    }

    private void OpenUnused()
    {
        if (!_model.IsOpen) return;
        new UnusedAssetsForm(_model, names =>
        {
            if (_model.IsWorkspace) return;
            foreach (var n in names) if (_model.Find(n) is { } it) _model.Delete(it);
            Refill();
        }, GoTo).Show(this);
    }

    private void OpenStrip()
    {
        string? guess = _model.Path is not null ? Path.GetDirectoryName(_model.Path) : null;
        if (guess is not null && !ModChain.IsLevelArchive(_model.Path!)) guess = null;
        new StripForm(guess).Show(this);
    }

    private void OpenModWizard()
    {
        string? root = _model.Path is not null ? ModChain.FindGameRoot(_model.Path) : null;
        using var f = new ModWizardForm(root);
        if (f.ShowDialog(this) == DialogResult.OK && f.CreatedModDir is not null
            && MessageBox.Show(this, "Open the new mod now?", "Mod created", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            OpenMod(f.CreatedModDir);
    }

    private void OpenClone()
    {
        var row = SelectedRows().FirstOrDefault();
        if (row is null) return;
        string folder = row.IsFolder ? row.Path : row.Item!.Folder;
        if (folder.Length == 0) { MessageBox.Show(this, "Select an object's folder (or a file inside it) first.", "Clone object", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        using var f = new CloneObjectForm(_model, folder);
        if (f.ShowDialog(this) == DialogResult.OK) { _tree.RevealPath(folder + "/x"); Refill(); }
    }

    private void SaveServerSide()
    {
        if (_model.Path is null || _model.IsWorkspace) return;
        using var d = new SaveFileDialog
        {
            Title = "Write the server-side copy to", Filter = "Refractor archives (*.rfa)|*.rfa",
            FileName = Path.GetFileName(_model.Path), InitialDirectory = Path.Combine(Path.GetDirectoryName(_model.Path)!, "server-side"),
        };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        if (string.Equals(d.FileName, _model.Path, StringComparison.OrdinalIgnoreCase))
        { MessageBox.Show(this, "Write it somewhere else - stripping the archive the game plays from would break it for clients.", "Server-side copy", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        try
        {
            Cursor = Cursors.WaitCursor;
            var o = ServerSide.Strip(_model.Path, d.FileName);
            MessageBox.Show(this, $"{o.EntriesBefore} -> {o.EntriesAfter} entries, {Ui.Human(o.BytesBefore)} -> {Ui.Human(o.BytesAfter)}.\r\n\r\n{d.FileName}", "Server-side copy written", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { Cursor = Cursors.Default; }
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    private List<ArchiveModel.Item> SelectedItemsDeep()
    {
        var result = new List<ArchiveModel.Item>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in SelectedRows())
        {
            if (row.Item is not null)
            {
                if (seen.Add(row.Item.Name)) result.Add(row.Item);
                continue;
            }
            string prefix = row.Path + "/";
            foreach (var i in VisibleItems())
                if (i.State != ArchiveModel.EntryState.Deleted &&
                    i.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && seen.Add(i.Name))
                    result.Add(i);
        }
        return result;
    }

    private void Extract(bool all)
    {
        var items = all
            ? VisibleItems().Where(i => i.State != ArchiveModel.EntryState.Deleted).ToList()
            : SelectedItemsDeep();
        if (items.Count == 0) return;

        var folder = Ui.PickFolder(this, "Extract to which folder?");
        if (folder is null) return;

        int done = 0, failed = 0;
        var errors = new List<string>();
        Cursor = Cursors.WaitCursor;
        try
        {
            string rootFull = Path.GetFullPath(folder);
            foreach (var it in items)
            {
                string dest = Path.GetFullPath(Path.Combine(rootFull, it.Name.Replace('/', Path.DirectorySeparatorChar)));
                if (!dest.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                { failed++; errors.Add($"{it.Name}: path escapes the target folder"); continue; }
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.WriteAllBytes(dest, _model.Read(it));
                    done++;
                }
                catch (Exception ex) { failed++; if (errors.Count < 10) errors.Add($"{it.Name}: {ex.Message}"); }
            }
        }
        finally { Cursor = Cursors.Default; }

        string msg = $"Extracted {done:N0} file(s) to\r\n{folder}";
        if (failed > 0) msg += $"\r\n\r\n{failed:N0} failed:\r\n" + string.Join("\r\n", errors);
        MessageBox.Show(this, msg, failed > 0 ? "Extracted with errors" : "Extracted", MessageBoxButtons.OK, failed > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }

    private void ReplaceSelected()
    {
        var it = Selected().FirstOrDefault();
        if (it is null || _model.IsWorkspace) return;
        using var d = new OpenFileDialog { Title = $"Replace {it.FileName} with...", FileName = it.FileName };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var bytes = File.ReadAllBytes(d.FileName);
            if (it.Name.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) && ImageImport.CanConvert(d.FileName)
                && MessageBox.Show(this, "Convert this picture to a DDS texture (power-of-two, with mipmaps) so the game can load it?", "Convert", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                bytes = ImageImport.ToDds(bytes, d.FileName).Dds;
            _model.Replace(it, bytes);
            Refill(keepSelectionPath: it.Name);
            ShowFor(it);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Replace failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void AddFiles()
    {
        if (!_model.IsOpen || _model.IsWorkspace) return;
        using var d = new OpenFileDialog { Title = "Add files to the archive", Multiselect = true };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        AddFilesFrom(d.FileNames);
    }

    private void AddFilesFrom(string[] files)
    {
        var sel = SelectedRows().FirstOrDefault();
        string folder = sel is null ? string.Empty : (sel.IsFolder ? sel.Path : sel.Item!.Folder);

        // Pictures become textures the engine can actually load, if the user wants that.
        bool? convert = null;
        int converted = 0;
        foreach (var f in files)
        {
            string leaf = Path.GetFileName(f);
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(f);
                if (ImageImport.CanConvert(f))
                {
                    convert ??= MessageBox.Show(this, "Convert pictures to DDS textures as they are added?\r\n\r\nSnapped to power-of-two with a mipmap chain - the engine silently drops any texture that is not.", "Convert pictures", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
                    if (convert == true)
                    {
                        var r = ImageImport.ToDds(bytes, f);
                        bytes = r.Dds; leaf = Path.ChangeExtension(leaf, ".dds"); converted++;
                    }
                }
                string name = folder.Length == 0 ? leaf : folder + "/" + leaf;
                _model.Add(name, bytes);
            }
            catch (Exception ex) { MessageBox.Show(this, $"{f}\r\n\r\n{ex.Message}", "Add failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
        if (folder.Length > 0) _tree.RevealPath(folder + "/x");
        Refill();
        if (converted > 0) _statusRight.Text = $"{converted} picture(s) converted to DDS";
    }

    private void DeleteSelected()
    {
        if (_model.IsWorkspace) return;
        var items = SelectedItemsDeep();
        if (items.Count == 0) return;
        if (MessageBox.Show(this, $"Remove {items.Count:N0} file(s) from the archive?\r\n\r\nNothing is written until you save.",
                "Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        foreach (var it in items) _model.Delete(it);
        Refill();
    }

    private void RevertSelected()
    {
        foreach (var it in SelectedItemsDeep()) _model.Revert(it);
        Refill();
    }

    private void Save(string? path)
    {
        if (!_model.IsOpen || _model.IsWorkspace) return;
        path ??= _model.Path!;
        try { Cursor = Cursors.WaitCursor; _model.Save(path); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"The archive was NOT changed.\r\n\r\n{ex.Message}", "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        finally { Cursor = Cursors.Default; }

        string? problem = RefractorFlatArchive.Validate(path);
        _refs = null;
        Refill();
        UpdateEnabled();
        if (problem is not null)
            MessageBox.Show(this, $"Saved, but the archive did not pass verification:\r\n\r\n{problem}\r\n\r\nPlease keep a copy and report this - it should not happen.",
                "Verification failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        else
            _statusRight.Text = $"Saved and verified  {DateTime.Now:HH:mm:ss}";
    }

    private void SaveAs()
    {
        if (!_model.IsOpen || _model.IsWorkspace) return;
        using var d = new SaveFileDialog { Title = "Save archive as", Filter = "Refractor archives (*.rfa)|*.rfa|All files (*.*)|*.*", FileName = Path.GetFileName(_model.Path ?? "archive.rfa") };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        Save(d.FileName);
        Text = $"{Path.GetFileName(d.FileName)} - RefractorForge Archive";
        SetHeader(Path.GetFileName(d.FileName), d.FileName);
    }

    private void PackFolder(string? folder = null)
    {
        folder ??= Ui.PickFolder(this, "Pack which folder?");
        if (folder is null) return;
        using var d = new SaveFileDialog { Title = "Write the new archive to", Filter = "Refractor archives (*.rfa)|*.rfa", FileName = new DirectoryInfo(folder).Name + ".rfa" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        try { Cursor = Cursors.WaitCursor; ArchiveModel.PackFolder(folder, d.FileName, compress: true, XPackId.Default); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Pack failed", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        finally { Cursor = Cursors.Default; }

        string? problem = RefractorFlatArchive.Validate(d.FileName);
        if (problem is not null)
        { MessageBox.Show(this, $"The archive was written but failed verification:\r\n\r\n{problem}", "Verification failed", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        if (MessageBox.Show(this, "Packed and verified. Open it now?", "Done", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            OpenArchive(d.FileName);
    }

    private void ValidateArchive()
    {
        if (_model.Path is null) return;
        if (_model.IsDirty && MessageBox.Show(this, "This checks the archive ON DISK, so unsaved changes are not included. Continue?", "Validate", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
        string? problem;
        try { Cursor = Cursors.WaitCursor; problem = RefractorFlatArchive.Validate(_model.Path); }
        finally { Cursor = Cursors.Default; }
        if (problem is null)
            MessageBox.Show(this, $"{_model.Items.Count:N0} entries checked. Every block decoded cleanly.", "Archive is sound", MessageBoxButtons.OK, MessageBoxIcon.Information);
        else
            MessageBox.Show(this, problem, "Archive has a problem", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void ShowAbout() =>
        MessageBox.Show(this,
            "RefractorForge Archive\r\n\r\n" +
            "Browser, extractor and packer for Refractor Flat Archives (.rfa) - Battlefield 1942 and Battlefield Vietnam.\r\n\r\n" +
            "Opens one archive or a whole mod as the game sees it, finds references and unused assets, compares archives, " +
            "writes server-side copies, scaffolds mods and clones objects - the MDT's utilities, on an RFA implementation " +
            "that round-trips retail archives byte-exactly and verifies every block it writes.",
            "About", MessageBoxButtons.OK, MessageBoxIcon.Information);

    // ── External editing, recent files, sorting, drag-out ────────────────────

    private void EditExternally(string? program)
    {
        var it = Selected().FirstOrDefault();
        if (it is null) return;
        try
        {
            program ??= _settings.Editors.TryGetValue(Path.GetExtension(it.Name).ToLowerInvariant(), out var p) && p.Length > 0 ? p : null;
            _edit.Open(it, _model.Read(it), program);
            _statusRight.Text = _model.IsWorkspace ? $"Opened {it.FileName} (read-only view - changes will not come back)" : $"Editing {it.FileName} externally - save there and it comes straight back";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not open this file.\r\n\r\n" + ex.Message + "\r\n\r\nIf nothing is associated with this type, use Open with... to pick a program.",
                "Open failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void EditWithChosenProgram()
    {
        var it = Selected().FirstOrDefault();
        if (it is null) return;
        using var d = new OpenFileDialog { Title = $"Open {it.FileName} with which program?", Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        _settings.Editors[Path.GetExtension(it.Name).ToLowerInvariant()] = d.FileName;
        _settings.Save();
        EditExternally(d.FileName);
    }

    private void OnExternalEditChanged(ArchiveModel.Item item, byte[] data)
    {
        if (_model.IsWorkspace || _model.Find(item.Name) is null) return;
        _model.Replace(item, data);
        Refill(keepSelectionPath: item.Name);
        if (_current == item) ShowFor(item);
        _statusRight.Text = $"{item.FileName} updated from the editor  {DateTime.Now:HH:mm:ss}";
    }

    private void RebuildRecentMenu()
    {
        _miRecent.DropDownItems.Clear();
        var gone = new List<string>();
        foreach (var path in _settings.Recent)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) { gone.Add(path); continue; }
            string captured = path;
            _miRecent.DropDownItems.Add(new ToolStripMenuItem(path, null, (_, _) => { if (Directory.Exists(captured)) OpenMod(captured); else OpenArchive(captured); }));
        }
        if (gone.Count > 0) { _settings.Recent.RemoveAll(gone.Contains); _settings.Save(); }
        _miRecent.Enabled = _miRecent.DropDownItems.Count > 0;
    }

    private int _sortColumn = -1;
    private bool _sortDescending;

    private void OnColumnClick(object? sender, ColumnClickEventArgs e)
    {
        if (e.Column == _sortColumn) _sortDescending = !_sortDescending;
        else { _sortColumn = e.Column; _sortDescending = e.Column is 1 or 2; }
        _tree.SetSort(_sortColumn, _sortDescending);
        Refill();
    }

    private void OnItemDrag(object? sender, ItemDragEventArgs e)
    {
        var items = SelectedItemsDeep();
        if (items.Count == 0) return;
        string stage = Path.Combine(Path.GetTempPath(), "RefractorForgeArchive", "drag-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new List<string>();
        try
        {
            foreach (var it in items)
            {
                string dest = Path.Combine(stage, it.Name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.WriteAllBytes(dest, _model.Read(it));
                paths.Add(dest);
            }
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not prepare the drag", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        DoDragDrop(new DataObject(DataFormats.FileDrop, paths.ToArray()), DragDropEffects.Copy);
    }

    // ── Chrome ───────────────────────────────────────────────────────────────

    private void UpdateEnabled()
    {
        bool open = _model.IsOpen, ws = _model.IsWorkspace, editable = open && !ws;
        bool sel = _list.SelectedIndices.Count > 0;
        bool single = Selected().Take(2).Count() == 1;
        _miSave.Enabled = editable && _model.IsDirty;
        _miSaveAs.Enabled = editable;
        _miSaveServer.Enabled = editable;
        _miClose.Enabled = open;
        _miReplace.Enabled = editable && single;
        _miEditOs.Enabled = single;
        _miEditWith.Enabled = single;
        _miAdd.Enabled = editable;
        _miDelete.Enabled = editable && sel;
        _miRevert.Enabled = editable && sel;
        _miRefs.Enabled = single;
        _miOpenSource.Enabled = ws && single;
        _miClone.Enabled = editable && sel;
        _miUnused.Enabled = open;
        _miExtractSel.Enabled = sel;
        _miExtractAll.Enabled = open;
        _miValidate.Enabled = editable;
        _tbSave.Enabled = _miSave.Enabled;
        _tbExtract.Enabled = sel;
        _tbAdd.Enabled = editable;
        _tbRefs.Enabled = single;
        _tbUnused.Enabled = open;
        _tbClone.Enabled = _miClone.Enabled;
    }

    private void UpdateStatus()
    {
        if (!_model.IsOpen) { _status.Text = "No archive open."; _statusRight.Text = string.Empty; _chips.Text = ""; return; }

        var live = VisibleItems().Where(i => i.State != ArchiveModel.EntryState.Deleted).ToList();
        long unc = live.Sum(i => (long)i.UncompressedSize);
        long packed = live.Sum(i => (long)i.BlockSize);
        int changed = _model.Items.Count(i => i.State != ArchiveModel.EntryState.Unchanged);

        _status.Text = $"{live.Count:N0} files   {Ui.Human(unc)} -> {Ui.Human(packed)}   {_tree.Rows.Count:N0} rows shown" +
                       (changed > 0 ? $"   {changed:N0} unsaved change(s)" : string.Empty);
        _chips.Text = _model.IsWorkspace
            ? $"{live.Count:N0} files   -   {Ui.Human(unc)}   -   {live.Count(i => i.Overrides > 0):N0} shadowing another   -   read-only"
            : $"{live.Count:N0} files   -   {Ui.Human(unc)} -> {Ui.Human(packed)}   -   {(_model.IsV11Format ? "v1.1" : "v1.0")} {(_model.IsCompressed ? "compressed" : "stored")}   -   {_model.XPackId}";
        UpdateEnabled();
    }
}
