using RefractorForge.Formats.Rfa;

namespace RefractorForge.Archive;

/// <summary>
/// The archive window, laid out the way BGA lays it out: ONE list holding folders and files together, with
/// columns for size, packed size, ratio and offset, and the search box along the bottom.
///
/// That single-list shape is deliberate rather than incidental. A folder tree beside a separate file pane makes
/// you click twice to see anything and hides how an archive is actually arranged; Refractor archives are broad
/// and shallow (bf1942/levels/&lt;map&gt;/...), and seeing a folder together with its contents in one column is
/// how you find your way around one.
///
/// The list is a virtual owner-drawn ListView rather than a real tree control: it has to hold tens of thousands
/// of rows, and only a virtual list asks for them by index as it scrolls. The hierarchy is drawn into the first
/// column - indent, expander, icon - over a flattened row array from <see cref="TreeModel"/>.
/// </summary>
public sealed class MainForm : Form
{
    private readonly ArchiveModel _model = new();
    private readonly TreeModel _tree = new();
    private readonly AudioPreview _audio = new();
    private readonly ShellIcons _icons = new();

    private readonly ListView _list = new();
    private readonly ToolStripStatusLabel _status = new();
    private readonly ToolStripStatusLabel _statusRight = new();
    private readonly TextBox _search = new();

    private readonly Panel _previewHost = new();
    private readonly PictureBox _picture = new();
    private readonly TextBox _text = new();
    private readonly Panel _audioPanel = new();
    private readonly Button _audioPlay = new();
    private readonly Label _audioInfo = new();
    private readonly Label _previewCaption = new();

    private byte[]? _currentBytes;
    private ArchiveModel.Item? _current;

    private float _meshYaw = 35f, _meshPitch = 20f, _meshZoom = 1f;
    private bool _meshDragging;
    private Point _meshLast;

    // Column 0 geometry: indent per level, then the expander, then the icon, then the name.
    private const int IndentPerLevel = 19;
    private const int GlyphWidth = 16;
    private const int IconWidth = 18;

    public MainForm(string? openPath)
    {
        Text = "RefractorForge Archive";
        Width = 1180;
        Height = 800;
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;
        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += OnDragDrop;

        BuildUi();
        UpdateEnabled();

        if (!string.IsNullOrEmpty(openPath) && File.Exists(openPath))
            OpenArchive(openPath);
    }

    // ── UI ───────────────────────────────────────────────────────────────────

    private ToolStripMenuItem _miSave = null!, _miSaveAs = null!, _miClose = null!;
    private ToolStripMenuItem _miReplace = null!, _miAdd = null!, _miDelete = null!, _miRevert = null!;
    private ToolStripMenuItem _miExtractSel = null!, _miExtractAll = null!, _miValidate = null!;

    private void BuildUi()
    {
        var menu = new MenuStrip();

        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(new ToolStripMenuItem("&Open archive...", null, (_, _) => PickAndOpen())
            { ShortcutKeys = Keys.Control | Keys.O });
        file.DropDownItems.Add(new ToolStripMenuItem("&Pack folder into a new archive...", null, (_, _) => PackFolder()));
        file.DropDownItems.Add(new ToolStripSeparator());
        _miSave = new ToolStripMenuItem("&Save", null, (_, _) => Save(null)) { ShortcutKeys = Keys.Control | Keys.S };
        _miSaveAs = new ToolStripMenuItem("Save &as...", null, (_, _) => SaveAs());
        _miClose = new ToolStripMenuItem("&Close archive", null, (_, _) => CloseArchive());
        file.DropDownItems.AddRange(new ToolStripItem[]
            { _miSave, _miSaveAs, new ToolStripSeparator(), _miClose,
              new ToolStripMenuItem("E&xit", null, (_, _) => Close()) });

        var edit = new ToolStripMenuItem("&Edit");
        _miReplace = new ToolStripMenuItem("&Replace selected file...", null, (_, _) => ReplaceSelected());
        _miAdd = new ToolStripMenuItem("&Add files...", null, (_, _) => AddFiles());
        _miDelete = new ToolStripMenuItem("&Delete selected", null, (_, _) => DeleteSelected())
            { ShortcutKeys = Keys.Delete };
        _miRevert = new ToolStripMenuItem("Re&vert selected", null, (_, _) => RevertSelected());
        edit.DropDownItems.AddRange(new ToolStripItem[] { _miReplace, _miAdd, _miDelete, _miRevert });

        var view = new ToolStripMenuItem("&View");
        view.DropDownItems.Add(new ToolStripMenuItem("&Expand all", null, (_, _) =>
            { _tree.ExpandAll(_model.Items); Refill(); }) { ShortcutKeys = Keys.Control | Keys.E });
        view.DropDownItems.Add(new ToolStripMenuItem("&Collapse all", null, (_, _) =>
            { _tree.CollapseAll(); Refill(); }) { ShortcutKeys = Keys.Control | Keys.W });

        var tools = new ToolStripMenuItem("&Tools");
        _miExtractSel = new ToolStripMenuItem("&Extract selected...", null, (_, _) => Extract(false));
        _miExtractAll = new ToolStripMenuItem("Extract &all...", null, (_, _) => Extract(true));
        _miValidate = new ToolStripMenuItem("&Validate archive", null, (_, _) => ValidateArchive());
        tools.DropDownItems.AddRange(new ToolStripItem[]
            { _miExtractSel, _miExtractAll, new ToolStripSeparator(), _miValidate });

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(new ToolStripMenuItem("&About", null, (_, _) => ShowAbout()));

        menu.Items.AddRange(new ToolStripItem[] { file, edit, view, tools, help });

        // The list, with BGA's own columns and widths.
        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = true;
        _list.HideSelection = false;
        _list.VirtualMode = true;
        _list.OwnerDraw = true;
        _list.BorderStyle = BorderStyle.None;
        _list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _list.RetrieveVirtualItem += OnRetrieveVirtualItem;
        _list.DrawColumnHeader += (_, e) => e.DrawDefault = true;
        _list.DrawItem += (_, e) => e.DrawDefault = false;       // painted per sub-item instead
        _list.DrawSubItem += OnDrawSubItem;
        _list.SelectedIndexChanged += (_, _) => OnSelectionChanged();
        _list.MouseDown += OnListMouseDown;
        _list.MouseDoubleClick += OnListDoubleClick;
        _list.KeyDown += OnListKeyDown;
        _list.Columns.Add("Filename", 350);
        _list.Columns.Add("Size", 90, HorizontalAlignment.Right);
        _list.Columns.Add("Compressed", 90, HorizontalAlignment.Right);
        _list.Columns.Add("Ratio", 73, HorizontalAlignment.Right);
        _list.Columns.Add("Offset", 110, HorizontalAlignment.Right);
        _list.Columns.Add("Status", 80);

        // Search along the BOTTOM of the list, where BGA keeps it.
        var searchBar = new Panel { Dock = DockStyle.Bottom, Height = 28, Padding = new Padding(4, 3, 4, 3) };
        var searchLabel = new Label
            { Text = "Search:", Dock = DockStyle.Left, Width = 52, TextAlign = ContentAlignment.MiddleLeft };
        _search.Dock = DockStyle.Fill;
        _search.TextChanged += (_, _) => Refill();
        var searchClear = new Button { Text = "Clear", Dock = DockStyle.Right, Width = 60 };
        searchClear.Click += (_, _) => _search.Text = string.Empty;
        searchBar.Controls.Add(_search);
        searchBar.Controls.Add(searchLabel);
        searchBar.Controls.Add(searchClear);

        var listPanel = new Panel { Dock = DockStyle.Fill };
        listPanel.Controls.Add(_list);
        listPanel.Controls.Add(searchBar);

        BuildPreviewHost();

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        split.Panel1.Controls.Add(listPanel);
        split.Panel2.Controls.Add(_previewHost);

        var strip = new StatusStrip();
        _status.Spring = true;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        strip.Items.Add(_status);
        strip.Items.Add(_statusRight);

        Controls.Add(split);
        Controls.Add(menu);
        Controls.Add(strip);
        MainMenuStrip = menu;
        split.BringToFront();
        Shown += (_, _) => split.SplitterDistance = (int)(split.Height * 0.55);
    }

    private void BuildPreviewHost()
    {
        _previewHost.Dock = DockStyle.Fill;
        _previewHost.BackColor = SystemColors.ControlDark;

        _previewCaption.Dock = DockStyle.Top;
        _previewCaption.Height = 22;
        _previewCaption.TextAlign = ContentAlignment.MiddleLeft;
        _previewCaption.BackColor = SystemColors.Control;
        _previewCaption.Padding = new Padding(6, 0, 0, 0);

        _picture.Dock = DockStyle.Fill;
        _picture.SizeMode = PictureBoxSizeMode.Zoom;
        _picture.BackColor = Color.FromArgb(48, 48, 48);
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
        _text.Multiline = true;
        _text.ReadOnly = true;
        _text.ScrollBars = ScrollBars.Both;
        _text.WordWrap = false;
        _text.Font = new Font(FontFamily.GenericMonospace, 9f);
        _text.Visible = false;

        _audioPanel.Dock = DockStyle.Fill;
        _audioPanel.Visible = false;
        _audioPlay.Text = "Play";
        _audioPlay.SetBounds(12, 12, 90, 30);
        _audioPlay.Click += (_, _) => ToggleAudio();
        _audioInfo.SetBounds(112, 18, 600, 20);
        _audioPanel.Controls.Add(_audioPlay);
        _audioPanel.Controls.Add(_audioInfo);
        _audio.Stopped += (_, _) => BeginInvoke(() => _audioPlay.Text = "Play");

        _previewHost.Controls.Add(_picture);
        _previewHost.Controls.Add(_text);
        _previewHost.Controls.Add(_audioPanel);
        _previewHost.Controls.Add(_previewCaption);
    }

    // ── Drawing the tree into the list ───────────────────────────────────────

    private void OnRetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        // Owner-draw paints the text, but the control still needs an item per row for selection and hit
        // testing, and one sub-item per column so DrawSubItem is raised for each.
        var lvi = new ListViewItem(string.Empty);
        for (int i = 1; i < _list.Columns.Count; i++) lvi.SubItems.Add(string.Empty);
        e.Item = lvi;
    }

    private TreeModel.Row? RowAt(int index) =>
        index >= 0 && index < _tree.Rows.Count ? _tree.Rows[index] : null;

    private void OnDrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        var row = RowAt(e.ItemIndex);
        if (row is null) return;

        bool selected = e.Item?.Selected == true;
        bool focused = _list.Focused;
        var backColor = selected
            ? (focused ? SystemColors.Highlight : SystemColors.ControlLight)
            : _list.BackColor;
        using (var bg = new SolidBrush(backColor)) e.Graphics.FillRectangle(bg, e.Bounds);

        Color fore = selected && focused ? SystemColors.HighlightText : _list.ForeColor;
        if (!selected)
        {
            // Pending edits are the one thing worth colouring: green for content that will be written,
            // grey for a folder's aggregate row so real files read as the foreground.
            if (row.Item?.State is ArchiveModel.EntryState.Added or ArchiveModel.EntryState.Replaced)
                fore = Color.FromArgb(0, 110, 0);
            else if (row.IsFolder)
                fore = Color.FromArgb(70, 70, 70);
        }

        using var fmt = new StringFormat
        {
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        using var brush = new SolidBrush(fore);

        if (e.ColumnIndex == 0)
        {
            int x = e.Bounds.Left + 2 + row.Depth * IndentPerLevel;

            if (row.IsFolder)
                DrawGlyph(e.Graphics, new Rectangle(x, e.Bounds.Top, GlyphWidth, e.Bounds.Height),
                    _tree.IsExpanded(row.Path));
            x += GlyphWidth;

            int iconIdx = row.IsFolder
                ? (_tree.IsExpanded(row.Path) ? ShellIcons.FolderOpen : ShellIcons.FolderClosed)
                : _icons.ForFile(row.Display);
            if (iconIdx >= 0 && iconIdx < _icons.Images.Images.Count)
                _icons.Images.Draw(e.Graphics, x, e.Bounds.Top + (e.Bounds.Height - 16) / 2, iconIdx);
            x += IconWidth;

            fmt.Alignment = StringAlignment.Near;
            var textRect = new Rectangle(x, e.Bounds.Top, Math.Max(e.Bounds.Right - x - 2, 4), e.Bounds.Height);
            e.Graphics.DrawString(row.Display, _list.Font, brush, textRect, fmt);
            return;
        }

        string text = ColumnText(row, e.ColumnIndex);
        if (text.Length == 0) return;

        fmt.Alignment = _list.Columns[e.ColumnIndex].TextAlign == HorizontalAlignment.Right
            ? StringAlignment.Far : StringAlignment.Near;
        var r = new Rectangle(e.Bounds.Left + 3, e.Bounds.Top, Math.Max(e.Bounds.Width - 6, 4), e.Bounds.Height);
        e.Graphics.DrawString(text, _list.Font, brush, r, fmt);
    }

    private string ColumnText(TreeModel.Row row, int col)
    {
        if (row.IsFolder)
        {
            // A folder reports what it contains, so a collapsed branch still tells you something.
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
                _ => it.IsCompressed ? "packed" : "stored",
            },
            _ => "",
        };
    }

    /// <summary>The expander. Uses the themed triangle so it matches Explorer, falling back to a drawn +/-
    /// box on a machine with visual styles turned off.</summary>
    private static void DrawGlyph(Graphics g, Rectangle bounds, bool expanded)
    {
        var box = new Rectangle(bounds.Left + 2, bounds.Top + (bounds.Height - 10) / 2, 10, 10);
        try
        {
            var element = expanded
                ? System.Windows.Forms.VisualStyles.VisualStyleElement.TreeView.Glyph.Opened
                : System.Windows.Forms.VisualStyles.VisualStyleElement.TreeView.Glyph.Closed;
            if (System.Windows.Forms.VisualStyles.VisualStyleRenderer.IsElementDefined(element))
            {
                new System.Windows.Forms.VisualStyles.VisualStyleRenderer(element).DrawBackground(g, box);
                return;
            }
        }
        catch { /* fall through to the drawn box */ }

        using var pen = new Pen(Color.Gray);
        g.DrawRectangle(pen, box);
        g.DrawLine(pen, box.Left + 2, box.Top + box.Height / 2, box.Right - 2, box.Top + box.Height / 2);
        if (!expanded)
            g.DrawLine(pen, box.Left + box.Width / 2, box.Top + 2, box.Left + box.Width / 2, box.Bottom - 2);
    }

    /// <summary>Was the click on a folder's expander rather than on the row itself?</summary>
    private static bool HitGlyph(TreeModel.Row row, int x)
    {
        if (!row.IsFolder) return false;
        int left = 2 + row.Depth * IndentPerLevel;
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
    }

    /// <summary>Left and right collapse and expand, as in any tree.</summary>
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

    /// <summary>Rebuild the flattened rows and hand the new count to the virtual list.</summary>
    private void Refill(string? keepSelectionPath = null)
    {
        _tree.Build(_model.Items, _search.Text.Trim());
        _list.VirtualListSize = _tree.Rows.Count;
        _list.Invalidate();

        if (keepSelectionPath is not null)
        {
            int idx = -1;
            for (int i = 0; i < _tree.Rows.Count; i++)
                if (_tree.Rows[i].Path == keepSelectionPath) { idx = i; break; }
            if (idx >= 0)
            {
                _list.SelectedIndices.Clear();
                _list.SelectedIndices.Add(idx);
                _list.EnsureVisible(idx);
            }
        }
        UpdateStatus();
    }

    // ── Opening / closing ────────────────────────────────────────────────────

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            if (Directory.Exists(paths[0])) { PackFolder(paths[0]); return; }
            OpenArchive(paths[0]);
        }
    }

    private void PickAndOpen()
    {
        using var d = new OpenFileDialog
        {
            Title = "Open a Refractor Flat Archive",
            Filter = "Refractor archives (*.rfa)|*.rfa|All files (*.*)|*.*",
        };
        if (d.ShowDialog(this) == DialogResult.OK) OpenArchive(d.FileName);
    }

    private void OpenArchive(string path)
    {
        if (!ConfirmDiscard()) return;
        try
        {
            Cursor = Cursors.WaitCursor;
            _model.Open(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open this archive.\r\n\r\n{ex.Message}", "Open failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        finally { Cursor = Cursors.Default; }

        Text = $"RefractorForge Archive - {Path.GetFileName(path)}";
        _search.Text = string.Empty;
        _tree.ExpandTopLevel(_model.Items);        // show the shape, not a wall of rows
        Refill();
        UpdateEnabled();
    }

    private void CloseArchive()
    {
        if (!ConfirmDiscard()) return;
        _audio.Stop();
        _model.Close();
        _tree.CollapseAll();
        Refill();
        ShowPreview(PreviewKind.None, null, null);
        Text = "RefractorForge Archive";
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
        else { _audio.Dispose(); _model.Dispose(); _icons.Dispose(); }
        base.OnFormClosing(e);
    }

    // ── Preview ──────────────────────────────────────────────────────────────

    private void OnSelectionChanged()
    {
        UpdateEnabled();
        var rows = SelectedRows().Take(2).ToList();
        if (rows.Count != 1 || rows[0].Item is null) { ShowPreview(PreviewKind.None, null, null); return; }
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
                    ShowPreview(PreviewKind.Text, caption + "   (not a square 8- or 16-bit map)",
                        Preview.ToHexDump(_currentBytes));
                    return;
                }
                _picture.Image?.Dispose();
                _picture.Image = raw;
                ShowPreview(PreviewKind.Image,
                    $"{caption}   -   {rinfo.Side} x {rinfo.Side} " +
                    $"{(rinfo.SixteenBit ? "16-bit heightmap" : "8-bit index map")}, range {rinfo.Min}-{rinfo.Max}", null);
                break;
            }

            case PreviewKind.Image:
            {
                var bmp = Preview.ToBitmap(item.Name, _currentBytes);
                if (bmp is null)
                {
                    ShowPreview(PreviewKind.Text, caption + "   (image could not be decoded)",
                        Preview.ToHexDump(_currentBytes));
                    return;
                }
                _picture.Image?.Dispose();
                _picture.Image = bmp;
                ShowPreview(PreviewKind.Image, $"{caption}   -   {bmp.Width} x {bmp.Height}", null);
                break;
            }

            case PreviewKind.Text:
                ShowPreview(PreviewKind.Text, caption, Preview.ToText(_currentBytes));
                break;

            case PreviewKind.Audio:
                _audioInfo.Text = $"{item.FileName}  -  {_currentBytes.Length:N0} bytes";
                ShowPreview(PreviewKind.Audio, caption, null);
                break;

            default:
                ShowPreview(PreviewKind.Text, caption, Preview.ToHexDump(_currentBytes));
                break;
        }
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
            ShowPreview(PreviewKind.Text, $"{_current.Name}   (not a readable StandardMesh)",
                Preview.ToHexDump(_currentBytes));
            return;
        }

        _picture.Image?.Dispose();
        _picture.Image = bmp;
        string info = mi is null ? string.Empty
            : $"   -   LOD 0 of {mi.Lods}, {mi.Materials} material(s), {mi.Vertices:N0} verts, {mi.Triangles:N0} tris" +
              $"   -   {mi.Size.X:0.#} x {mi.Size.Y:0.#} x {mi.Size.Z:0.#}   -   drag to orbit, wheel to zoom";
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

    private void ShowPreview(PreviewKind kind, string? caption, string? text)
    {
        _previewCaption.Text = caption ?? string.Empty;
        bool onPicture = kind is PreviewKind.Image or PreviewKind.Mesh;
        _picture.SizeMode = kind == PreviewKind.Mesh ? PictureBoxSizeMode.CenterImage : PictureBoxSizeMode.Zoom;
        _picture.Visible = onPicture;
        _audioPanel.Visible = kind == PreviewKind.Audio;
        _text.Visible = kind == PreviewKind.Text;
        if (text is not null) _text.Text = text;
        if (!onPicture && _picture.Image is not null) { _picture.Image.Dispose(); _picture.Image = null; }
    }

    private void ToggleAudio()
    {
        if (_audio.IsPlaying) { _audio.Stop(); _audioPlay.Text = "Play"; return; }
        if (_currentBytes is null) return;
        try { _audio.Play(_currentBytes); _audioPlay.Text = "Stop"; }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not play this sound.\r\n\r\n{ex.Message}", "Playback failed",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    /// <summary>Selecting a folder means everything under it, which is what a tree implies.</summary>
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
            foreach (var i in _model.Items)
                if (i.State != ArchiveModel.EntryState.Deleted &&
                    i.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && seen.Add(i.Name))
                    result.Add(i);
        }
        return result;
    }

    private void Extract(bool all)
    {
        var items = all
            ? _model.Items.Where(i => i.State != ArchiveModel.EntryState.Deleted).ToList()
            : SelectedItemsDeep();
        if (items.Count == 0) return;

        using var d = new FolderBrowserDialog { Description = "Extract to which folder?", UseDescriptionForTitle = true };
        if (d.ShowDialog(this) != DialogResult.OK) return;

        int done = 0, failed = 0;
        var errors = new List<string>();
        Cursor = Cursors.WaitCursor;
        try
        {
            string rootFull = Path.GetFullPath(d.SelectedPath);
            foreach (var it in items)
            {
                // Entry paths come out of the archive, so treat them as untrusted: anything that would land
                // outside the chosen folder is refused rather than written.
                string dest = Path.GetFullPath(Path.Combine(rootFull, it.Name.Replace('/', Path.DirectorySeparatorChar)));
                if (!dest.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                {
                    failed++; errors.Add($"{it.Name}: path escapes the target folder");
                    continue;
                }
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

        string msg = $"Extracted {done:N0} file(s) to\r\n{d.SelectedPath}";
        if (failed > 0) msg += $"\r\n\r\n{failed:N0} failed:\r\n" + string.Join("\r\n", errors);
        MessageBox.Show(this, msg, failed > 0 ? "Extracted with errors" : "Extracted",
            MessageBoxButtons.OK, failed > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }

    private void ReplaceSelected()
    {
        var it = Selected().FirstOrDefault();
        if (it is null) return;
        using var d = new OpenFileDialog { Title = $"Replace {it.FileName} with...", FileName = it.FileName };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            _model.Replace(it, File.ReadAllBytes(d.FileName));
            Refill(keepSelectionPath: it.Name);
            ShowFor(it);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Replace failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void AddFiles()
    {
        if (!_model.IsOpen) return;
        using var d = new OpenFileDialog { Title = "Add files to the archive", Multiselect = true };
        if (d.ShowDialog(this) != DialogResult.OK) return;

        // New files land in the folder of whatever is selected, which is what "add here" means in a tree.
        var sel = SelectedRows().FirstOrDefault();
        string folder = sel is null ? string.Empty : (sel.IsFolder ? sel.Path : sel.Item!.Folder);

        foreach (var f in d.FileNames)
        {
            string name = folder.Length == 0 ? Path.GetFileName(f) : folder + "/" + Path.GetFileName(f);
            try { _model.Add(name, File.ReadAllBytes(f)); }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"{f}\r\n\r\n{ex.Message}", "Add failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        if (folder.Length > 0) _tree.RevealPath(folder + "/x");
        Refill();
    }

    private void DeleteSelected()
    {
        var items = SelectedItemsDeep();
        if (items.Count == 0) return;
        if (MessageBox.Show(this,
                $"Remove {items.Count:N0} file(s) from the archive?\r\n\r\nNothing is written until you save.",
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
        if (!_model.IsOpen) return;
        path ??= _model.Path!;
        try { Cursor = Cursors.WaitCursor; _model.Save(path); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"The archive was NOT changed.\r\n\r\n{ex.Message}", "Save failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        finally { Cursor = Cursors.Default; }

        // Saving is the moment corruption would be introduced, so check the result now rather than letting
        // the game be the one to find out.
        string? problem = RefractorFlatArchive.Validate(path);
        Refill();
        UpdateEnabled();

        if (problem is not null)
            MessageBox.Show(this,
                $"Saved, but the archive did not pass verification:\r\n\r\n{problem}\r\n\r\n" +
                "Please keep a copy and report this - it should not happen.",
                "Verification failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        else
            _statusRight.Text = $"Saved and verified  {DateTime.Now:HH:mm:ss}";
    }

    private void SaveAs()
    {
        if (!_model.IsOpen) return;
        using var d = new SaveFileDialog
        {
            Title = "Save archive as",
            Filter = "Refractor archives (*.rfa)|*.rfa|All files (*.*)|*.*",
            FileName = Path.GetFileName(_model.Path ?? "archive.rfa"),
        };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        Save(d.FileName);
        Text = $"RefractorForge Archive - {Path.GetFileName(d.FileName)}";
    }

    private void PackFolder(string? folder = null)
    {
        if (folder is null)
        {
            using var fb = new FolderBrowserDialog { Description = "Pack which folder?", UseDescriptionForTitle = true };
            if (fb.ShowDialog(this) != DialogResult.OK) return;
            folder = fb.SelectedPath;
        }
        using var d = new SaveFileDialog
        {
            Title = "Write the new archive to",
            Filter = "Refractor archives (*.rfa)|*.rfa",
            FileName = new DirectoryInfo(folder).Name + ".rfa",
        };
        if (d.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            Cursor = Cursors.WaitCursor;
            ArchiveModel.PackFolder(folder, d.FileName, compress: true, XPackId.Default);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Pack failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        finally { Cursor = Cursors.Default; }

        string? problem = RefractorFlatArchive.Validate(d.FileName);
        if (problem is not null)
        {
            MessageBox.Show(this, $"The archive was written but failed verification:\r\n\r\n{problem}",
                "Verification failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (MessageBox.Show(this, "Packed and verified. Open it now?", "Done",
                MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            OpenArchive(d.FileName);
    }

    private void ValidateArchive()
    {
        if (_model.Path is null) return;
        if (_model.IsDirty &&
            MessageBox.Show(this, "This checks the archive ON DISK, so unsaved changes are not included. Continue?",
                "Validate", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;

        string? problem;
        try { Cursor = Cursors.WaitCursor; problem = RefractorFlatArchive.Validate(_model.Path); }
        finally { Cursor = Cursors.Default; }

        if (problem is null)
            MessageBox.Show(this, $"{_model.Items.Count:N0} entries checked. Every block decoded cleanly.",
                "Archive is sound", MessageBoxButtons.OK, MessageBoxIcon.Information);
        else
            MessageBox.Show(this, problem, "Archive has a problem", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void ShowAbout() =>
        MessageBox.Show(this,
            "RefractorForge Archive\r\n\r\n" +
            "Browser, extractor and packer for Refractor Flat Archives (.rfa) - Battlefield 1942 and " +
            "Battlefield Vietnam.\r\n\r\n" +
            "It uses RefractorForge's RFA implementation, which round-trips retail archives byte-exactly and " +
            "verifies every block it writes with an independent, engine-validated LZO decoder.",
            "About", MessageBoxButtons.OK, MessageBoxIcon.Information);

    // ── Chrome ───────────────────────────────────────────────────────────────

    private void UpdateEnabled()
    {
        bool open = _model.IsOpen;
        bool sel = _list.SelectedIndices.Count > 0;
        _miSave.Enabled = open && _model.IsDirty;
        _miSaveAs.Enabled = open;
        _miClose.Enabled = open;
        _miReplace.Enabled = Selected().Take(2).Count() == 1;
        _miAdd.Enabled = open;
        _miDelete.Enabled = sel;
        _miRevert.Enabled = sel;
        _miExtractSel.Enabled = sel;
        _miExtractAll.Enabled = open;
        _miValidate.Enabled = open;
    }

    private void UpdateStatus()
    {
        if (!_model.IsOpen) { _status.Text = "No archive open."; _statusRight.Text = string.Empty; return; }

        var live = _model.Items.Where(i => i.State != ArchiveModel.EntryState.Deleted).ToList();
        long unc = live.Sum(i => (long)i.UncompressedSize);
        long packed = live.Sum(i => (long)i.BlockSize);
        int changed = _model.Items.Count(i => i.State != ArchiveModel.EntryState.Unchanged);

        _status.Text =
            $"{live.Count:N0} files  |  {Human(unc)} -> {Human(packed)}  |  " +
            $"{(_model.IsV11Format ? "Refractor2 v1.1" : "v1.0")}, " +
            $"{(_model.IsCompressed ? "compressed" : "uncompressed")}  |  {_tree.Rows.Count:N0} rows shown" +
            (changed > 0 ? $"  |  {changed:N0} unsaved change(s)" : string.Empty);

        UpdateEnabled();
    }

    private static string Human(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.##} GiB"
        : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.##} MiB"
        : bytes >= 1L << 10 ? $"{bytes / (double)(1L << 10):0.##} KiB"
        : $"{bytes} B";
}
