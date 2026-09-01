using RefractorForge.Formats.Rfa;

namespace RefractorForge.Archive;

/// <summary>
/// The archive browser. A folder tree on the left, the files of the selected folder in the middle, and a preview
/// underneath that renders textures, scripts and sounds without extracting anything by hand.
///
/// The list is virtual because a stock texture.rfa holds tens of thousands of entries and building that many
/// ListViewItems up front is the difference between instant and a visible stall.
/// </summary>
public sealed class MainForm : Form
{
    private readonly ArchiveModel _model = new();
    private readonly AudioPreview _audio = new();

    private readonly TreeView _tree = new();
    private readonly ListView _list = new();
    private readonly ToolStripStatusLabel _status = new();
    private readonly ToolStripStatusLabel _statusRight = new();
    private readonly ToolStripTextBox _search = new();

    // Preview surface: exactly one of these is visible at a time.
    private readonly Panel _previewHost = new();
    private readonly PictureBox _picture = new();
    private readonly TextBox _text = new();
    private readonly Panel _audioPanel = new();
    private readonly Button _audioPlay = new();
    private readonly Label _audioInfo = new();
    private readonly Label _previewCaption = new();

    private List<ArchiveModel.Item> _visible = new();   // what the virtual list is showing
    private string _folder = string.Empty;              // selected folder ("" = root, null-ish sentinel below)
    private bool _showAllFiles;                         // search mode ignores the folder selection
    private byte[]? _currentBytes;
    private ArchiveModel.Item? _current;

    // Mesh orbit. Kept on the form rather than inside the renderer so the angle survives re-renders while the
    // user drags, and reset per file so a new model always arrives framed the same way.
    private float _meshYaw = 35f, _meshPitch = 20f, _meshZoom = 1f;
    private bool _meshDragging;
    private Point _meshLast;

    public MainForm(string? openPath)
    {
        Text = "RefractorForge Archive";
        Width = 1280;
        Height = 820;
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

    // ── UI construction ──────────────────────────────────────────────────────

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
        file.DropDownItems.Add(_miSave);
        file.DropDownItems.Add(_miSaveAs);
        file.DropDownItems.Add(new ToolStripSeparator());
        _miClose = new ToolStripMenuItem("&Close archive", null, (_, _) => CloseArchive());
        file.DropDownItems.Add(_miClose);
        file.DropDownItems.Add(new ToolStripMenuItem("E&xit", null, (_, _) => Close()));

        var edit = new ToolStripMenuItem("&Edit");
        _miReplace = new ToolStripMenuItem("&Replace selected file...", null, (_, _) => ReplaceSelected());
        _miAdd = new ToolStripMenuItem("&Add files...", null, (_, _) => AddFiles());
        _miDelete = new ToolStripMenuItem("&Delete selected", null, (_, _) => DeleteSelected())
            { ShortcutKeys = Keys.Delete };
        _miRevert = new ToolStripMenuItem("Re&vert selected", null, (_, _) => RevertSelected());
        edit.DropDownItems.AddRange(new ToolStripItem[] { _miReplace, _miAdd, _miDelete, _miRevert });

        var tools = new ToolStripMenuItem("&Tools");
        _miExtractSel = new ToolStripMenuItem("&Extract selected...", null, (_, _) => Extract(false));
        _miExtractAll = new ToolStripMenuItem("Extract &all...", null, (_, _) => Extract(true));
        _miValidate = new ToolStripMenuItem("&Validate archive", null, (_, _) => ValidateArchive());
        tools.DropDownItems.AddRange(new ToolStripItem[] { _miExtractSel, _miExtractAll, new ToolStripSeparator(), _miValidate });

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(new ToolStripMenuItem("&About", null, (_, _) => ShowAbout()));

        menu.Items.AddRange(new ToolStripItem[] { file, edit, tools, help });

        var bar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
        bar.Items.Add(new ToolStripLabel("Search:"));
        _search.Width = 260;
        _search.TextChanged += (_, _) => ApplyFilter();
        bar.Items.Add(_search);
        bar.Items.Add(new ToolStripButton("Clear", null, (_, _) => _search.Text = string.Empty));

        // Left: folder tree. Right: file list over preview.
        var outer = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 320, Orientation = Orientation.Vertical };
        var inner = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };

        _tree.Dock = DockStyle.Fill;
        _tree.HideSelection = false;
        _tree.AfterSelect += (_, e) =>
        {
            _folder = (string)(e.Node?.Tag ?? string.Empty);
            _showAllFiles = false;
            ApplyFilter();
        };

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = true;
        _list.HideSelection = false;
        _list.VirtualMode = true;                       // tens of thousands of entries per archive
        _list.RetrieveVirtualItem += OnRetrieveVirtualItem;
        _list.SelectedIndexChanged += (_, _) => OnSelectionChanged();
        _list.Columns.Add("Name", 340);
        _list.Columns.Add("Size", 100, HorizontalAlignment.Right);
        _list.Columns.Add("Packed", 100, HorizontalAlignment.Right);
        _list.Columns.Add("Ratio", 70, HorizontalAlignment.Right);
        _list.Columns.Add("Status", 90);
        _list.Columns.Add("Path", 420);

        BuildPreviewHost();

        inner.Panel1.Controls.Add(_list);
        inner.Panel2.Controls.Add(_previewHost);
        outer.Panel1.Controls.Add(_tree);
        outer.Panel2.Controls.Add(inner);

        var strip = new StatusStrip();
        _status.Spring = true;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        strip.Items.Add(_status);
        strip.Items.Add(_statusRight);

        Controls.Add(outer);
        Controls.Add(bar);
        Controls.Add(menu);
        Controls.Add(strip);
        MainMenuStrip = menu;

        // Docking order: last added sits outermost, so add the fill control first.
        outer.BringToFront();
        inner.SplitterDistance = 330;
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
        _picture.SizeMode = PictureBoxSizeMode.Zoom;    // fit without distorting; textures are often non-square
        _picture.BackColor = Color.FromArgb(48, 48, 48);
        _picture.Visible = false;
        _picture.MouseDown += (_, e) => { if (Preview.KindOf(_current?.Name ?? "") == PreviewKind.Mesh) { _meshDragging = true; _meshLast = e.Location; } };
        _picture.MouseUp += (_, _) => _meshDragging = false;
        _picture.MouseMove += OnMeshDrag;
        _picture.MouseWheel += OnMeshWheel;
        // A PictureBox only receives the wheel once it has focus.
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

    private ToolStripMenuItem _miSave = null!, _miSaveAs = null!, _miClose = null!;
    private ToolStripMenuItem _miReplace = null!, _miAdd = null!, _miDelete = null!, _miRevert = null!;
    private ToolStripMenuItem _miExtractSel = null!, _miExtractAll = null!, _miValidate = null!;

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
        BuildTree();
        _folder = string.Empty;
        _showAllFiles = true;         // start by showing everything, so an archive is never a blank window
        ApplyFilter();
        UpdateEnabled();
        UpdateStatus();
    }

    private void CloseArchive()
    {
        if (!ConfirmDiscard()) return;
        _audio.Stop();
        _model.Close();
        _tree.Nodes.Clear();
        _visible = new List<ArchiveModel.Item>();
        _list.VirtualListSize = 0;
        ShowPreview(PreviewKind.None, null, null);
        Text = "RefractorForge Archive";
        UpdateEnabled();
        UpdateStatus();
    }

    private bool ConfirmDiscard()
    {
        if (!_model.IsDirty) return true;
        var r = MessageBox.Show(this,
            "This archive has unsaved changes. Discard them?", "Unsaved changes",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        return r == DialogResult.Yes;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!ConfirmDiscard()) e.Cancel = true;
        else { _audio.Dispose(); _model.Dispose(); }
        base.OnFormClosing(e);
    }

    // ── Tree + list ──────────────────────────────────────────────────────────

    private void BuildTree()
    {
        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        var root = new TreeNode(Path.GetFileName(_model.Path ?? "archive")) { Tag = string.Empty };
        var byPath = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase) { [string.Empty] = root };

        foreach (var item in _model.Items)
        {
            string folder = item.Folder;
            if (folder.Length == 0 || byPath.ContainsKey(folder)) continue;

            // Create every missing ancestor on the way down.
            var parts = folder.Split('/');
            string acc = string.Empty;
            var parent = root;
            foreach (var part in parts)
            {
                acc = acc.Length == 0 ? part : acc + "/" + part;
                if (!byPath.TryGetValue(acc, out var node))
                {
                    node = new TreeNode(part) { Tag = acc };
                    parent.Nodes.Add(node);
                    byPath[acc] = node;
                }
                parent = node;
            }
        }

        _tree.Nodes.Add(root);
        root.Expand();
        _tree.EndUpdate();
    }

    private void ApplyFilter()
    {
        string q = _search.Text.Trim();
        IEnumerable<ArchiveModel.Item> src = _model.Items.Where(i => i.State != ArchiveModel.EntryState.Deleted);

        if (q.Length > 0)
            src = src.Where(i => i.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
        else if (!_showAllFiles)
            // Files directly in the selected folder. Subfolders have their own nodes.
            src = src.Where(i => string.Equals(i.Folder, _folder, StringComparison.OrdinalIgnoreCase));

        _visible = src.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        _list.VirtualListSize = _visible.Count;
        _list.Invalidate();
        UpdateStatus();
    }

    private void OnRetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        if (e.ItemIndex < 0 || e.ItemIndex >= _visible.Count)
        {
            e.Item = new ListViewItem(string.Empty);
            return;
        }
        var it = _visible[e.ItemIndex];
        string ratio = it.UncompressedSize > 0 && it.IsCompressed
            ? $"{100.0 * it.BlockSize / it.UncompressedSize:0}%"
            : "-";
        string state = it.State switch
        {
            ArchiveModel.EntryState.Added => "added",
            ArchiveModel.EntryState.Replaced => "replaced",
            ArchiveModel.EntryState.Deleted => "deleted",
            _ => it.IsCompressed ? "packed" : "stored",
        };
        var lvi = new ListViewItem(new[]
        {
            it.FileName,
            it.UncompressedSize.ToString("N0"),
            it.BlockSize.ToString("N0"),
            ratio,
            state,
            it.Folder,
        });
        if (it.State is ArchiveModel.EntryState.Added or ArchiveModel.EntryState.Replaced)
            lvi.ForeColor = Color.FromArgb(0, 110, 0);
        e.Item = lvi;
    }

    private IEnumerable<ArchiveModel.Item> Selected()
    {
        foreach (int idx in _list.SelectedIndices)
            if (idx >= 0 && idx < _visible.Count) yield return _visible[idx];
    }

    private void OnSelectionChanged()
    {
        var sel = Selected().FirstOrDefault();
        UpdateEnabled();
        if (sel is null || _list.SelectedIndices.Count != 1) { ShowPreview(PreviewKind.None, null, null); return; }
        ShowFor(sel);
    }

    // ── Preview ──────────────────────────────────────────────────────────────

    private void ShowFor(ArchiveModel.Item item)
    {
        _audio.Stop();
        _audioPlay.Text = "Play";
        _current = item;
        try
        {
            _currentBytes = _model.Read(item);
        }
        catch (Exception ex)
        {
            _currentBytes = null;
            ShowPreview(PreviewKind.Text, $"{item.Name} - could not be read", $"{ex.Message}");
            return;
        }

        var kind = Preview.KindOf(item.Name);
        string caption = $"{item.Name}   -   {item.UncompressedSize:N0} bytes";

        switch (kind)
        {
            case PreviewKind.Mesh:
                _meshYaw = 35f; _meshPitch = 20f; _meshZoom = 1f;   // every model opens from the same angle
                RenderMeshPreview();
                break;

            case PreviewKind.Image when Path.GetExtension(item.Name).Equals(".raw", StringComparison.OrdinalIgnoreCase):
            {
                var raw = MeshPreview.RenderRaw(_currentBytes, item.Name, 1024, out var rinfo);
                if (raw is null || rinfo is null)
                {
                    ShowPreview(PreviewKind.Text,
                        caption + "   (not a square 8- or 16-bit map)", Preview.ToHexDump(_currentBytes));
                    return;
                }
                _picture.Image?.Dispose();
                _picture.Image = raw;
                ShowPreview(PreviewKind.Image,
                    $"{caption}   -   {rinfo.Side} x {rinfo.Side} " +
                    $"{(rinfo.SixteenBit ? "16-bit heightmap" : "8-bit index map")}, range {rinfo.Min}-{rinfo.Max}",
                    null);
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

    /// <summary>Re-draw the current .sm at the current orbit, sized to the preview pane.</summary>
    private void RenderMeshPreview()
    {
        if (_currentBytes is null || _current is null) return;
        int w = Math.Max(_previewHost.ClientSize.Width, 64);
        int h = Math.Max(_previewHost.ClientSize.Height - _previewCaption.Height, 64);

        Bitmap? bmp;
        MeshPreview.MeshInfo? mi;
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
        // A model is already drawn to fit the pane, so stretching it again would only soften it.
        _picture.SizeMode = kind == PreviewKind.Mesh ? PictureBoxSizeMode.CenterImage : PictureBoxSizeMode.Zoom;
        _picture.Visible = onPicture;
        _audioPanel.Visible = kind == PreviewKind.Audio;
        _text.Visible = kind == PreviewKind.Text;
        if (text is not null) _text.Text = text;
        if (!onPicture && _picture.Image is not null)
        {
            _picture.Image.Dispose();
            _picture.Image = null;
        }
    }

    private void ToggleAudio()
    {
        if (_audio.IsPlaying) { _audio.Stop(); _audioPlay.Text = "Play"; return; }
        if (_currentBytes is null) return;
        try
        {
            _audio.Play(_currentBytes);
            _audioPlay.Text = "Stop";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not play this sound.\r\n\r\n{ex.Message}", "Playback failed",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    private void Extract(bool all)
    {
        var items = (all ? _model.Items.Where(i => i.State != ArchiveModel.EntryState.Deleted) : Selected()).ToList();
        if (items.Count == 0) return;

        using var d = new FolderBrowserDialog { Description = "Extract to which folder?", UseDescriptionForTitle = true };
        if (d.ShowDialog(this) != DialogResult.OK) return;

        int done = 0, failed = 0;
        var errors = new List<string>();
        Cursor = Cursors.WaitCursor;
        try
        {
            foreach (var it in items)
            {
                // Entry paths are archive-relative and always forward-slashed. Reject anything that would
                // escape the chosen folder rather than trusting the archive's own strings.
                string rel = it.Name.Replace('/', Path.DirectorySeparatorChar);
                string dest = Path.GetFullPath(Path.Combine(d.SelectedPath, rel));
                if (!dest.StartsWith(Path.GetFullPath(d.SelectedPath), StringComparison.OrdinalIgnoreCase))
                {
                    failed++;
                    errors.Add($"{it.Name}: path escapes the target folder");
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
            ApplyFilter();
            ShowFor(it);
            UpdateStatus();
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

        // New entries land in the folder currently selected in the tree, which is what "add here" means.
        string folder = _folder;
        foreach (var f in d.FileNames)
        {
            string name = folder.Length == 0 ? Path.GetFileName(f) : folder + "/" + Path.GetFileName(f);
            try { _model.Add(name, File.ReadAllBytes(f)); }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"{f}\r\n\r\n{ex.Message}", "Add failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        BuildTree();
        ApplyFilter();
        UpdateStatus();
    }

    private void DeleteSelected()
    {
        var items = Selected().ToList();
        if (items.Count == 0) return;
        var r = MessageBox.Show(this,
            $"Remove {items.Count:N0} file(s) from the archive?\r\n\r\nNothing is written until you save.",
            "Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r != DialogResult.Yes) return;
        foreach (var it in items) _model.Delete(it);
        ApplyFilter();
        UpdateStatus();
    }

    private void RevertSelected()
    {
        foreach (var it in Selected().ToList()) _model.Revert(it);
        ApplyFilter();
        UpdateStatus();
    }

    private void Save(string? path)
    {
        if (!_model.IsOpen) return;
        path ??= _model.Path!;
        try
        {
            Cursor = Cursors.WaitCursor;
            _model.Save(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"The archive was NOT changed.\r\n\r\n{ex.Message}", "Save failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        finally { Cursor = Cursors.Default; }

        // Saving is the moment corruption would be introduced, so check the result immediately rather than
        // letting the game be the one to find out.
        string? problem = RefractorFlatArchive.Validate(path);
        BuildTree();
        ApplyFilter();
        UpdateEnabled();
        UpdateStatus();

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
            MessageBox.Show(this,
                "This checks the archive ON DISK, so unsaved changes are not included. Continue?",
                "Validate", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
            return;

        string? problem;
        try
        {
            Cursor = Cursors.WaitCursor;
            problem = RefractorFlatArchive.Validate(_model.Path);
        }
        finally { Cursor = Cursors.Default; }

        if (problem is null)
            MessageBox.Show(this,
                $"{_model.Items.Count:N0} entries checked. Every block decoded cleanly.",
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
        _miReplace.Enabled = _list.SelectedIndices.Count == 1;
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
        int changed = _model.Items.Count(i => i.State != ArchiveModel.EntryState.Unchanged);

        _status.Text =
            $"{live.Count:N0} entries  |  {Human(unc)} uncompressed  |  " +
            $"{(_model.IsV11Format ? "Refractor2 v1.1" : "v1.0")}, " +
            $"{(_model.IsCompressed ? "compressed" : "uncompressed")}  |  showing {_visible.Count:N0}" +
            (changed > 0 ? $"  |  {changed:N0} unsaved change(s)" : string.Empty);

        UpdateEnabled();
    }

    private static string Human(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.##} GiB"
        : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.##} MiB"
        : bytes >= 1L << 10 ? $"{bytes / (double)(1L << 10):0.##} KiB"
        : $"{bytes} B";
}
