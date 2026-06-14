using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Editing;
using RefractorForge.Formats.Geometry;
using RefractorForge.Render;

namespace RefractorForge.Studio;

/// <summary>
/// The interactive map editor window. It is a thin shell over the validated engine:
/// <see cref="LevelScene"/> loads + renders, <see cref="EditCommands"/> mutate the document, and
/// <see cref="EditHistory"/> gives undo/redo. The 3D view is the project's software rasterizer
/// blitted into a PictureBox — no GPU dependency.
/// </summary>
public sealed class MainForm : Form
{
    // ---- model ----
    private LevelScene? _scene;
    private EditHistory? _history;
    private string? _staticObjectsPath;   // where Save writes
    private string? _levelDir;
    private int _selected = -1;
    private bool _dirty;

    // ---- orbit camera (spherical around a target) ----
    private readonly Camera _cam = new();
    private Vector3 _target;
    private float _dist = 2000, _az = MathF.PI * 0.75f, _el = MathF.PI * 0.22f;

    // ---- ui ----
    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2, SplitterWidth = 6 };
    private readonly PictureBox _view = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(140, 174, 217), SizeMode = PictureBoxSizeMode.Normal, TabStop = true };
    private readonly Label _hint = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.White, Font = new Font("Segoe UI", 11), Text = "File ▸ Open Level Folder…  (Ctrl+O)\r\n\r\nPick the folder of a Battlefield Vietnam level\r\n(the one containing StaticObjects.con and Heightmap.raw)." };
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly Label _objCount = new() { Dock = DockStyle.Top, Height = 22, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 0, 0), Text = "No level loaded" };
    private readonly NumericUpDown _px = MakeNum(), _py = MakeNum(), _pz = MakeNum();
    private readonly NumericUpDown _rotY = MakeNum(-360, 360, 3, 1);
    private readonly NumericUpDown _scale = MakeNum(0.001m, 1000, 3, 0.1m);
    private readonly Label _selTemplate = new() { Dock = DockStyle.Top, Height = 22, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 0, 0), Text = "(nothing selected)" };
    private readonly TextBox _newTemplate = new() { Dock = DockStyle.Fill, Text = "container_box" };
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _hud = new() { TextAlign = ContentAlignment.MiddleRight };

    private bool _suppress;          // guard against control-change feedback loops
    private Point _downAt, _lastAt;  // mouse drag tracking
    private MouseButtons _dragBtn;

    public MainForm()
    {
        Text = "RefractorForge — Battlefield Vietnam Map Editor";
        Width = 1280; Height = 820;
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        MinimumSize = new Size(900, 600);

        BuildMenu();
        BuildSidePanel();

        // left = 3D view (with a centered hint shown until a level loads)
        var left = new Panel { Dock = DockStyle.Fill };
        left.Controls.Add(_view);
        left.Controls.Add(_hint);
        _view.Visible = false;
        _split.Panel1.Controls.Add(left);
        _split.Panel2MinSize = 250;
        Controls.Add(_split);

        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_status);
        statusStrip.Items.Add(_hud);
        Controls.Add(statusStrip);

        // events
        _view.MouseDown += View_MouseDown;
        _view.MouseMove += View_MouseMove;
        _view.MouseUp += View_MouseUp;
        _view.MouseWheel += View_MouseWheel;
        _view.Resize += (_, _) => RenderView();
        _list.SelectedIndexChanged += (_, _) => { if (!_suppress) SetSelected(_list.SelectedIndex); };

        _px.ValueChanged += (_, _) => CommitPosition();
        _py.ValueChanged += (_, _) => CommitPosition();
        _pz.ValueChanged += (_, _) => CommitPosition();
        _rotY.ValueChanged += (_, _) => CommitRotation();
        _scale.ValueChanged += (_, _) => CommitScale();

        // set initial split position after the form has a size
        Shown += (_, _) => { TrySetSplitter(); UpdateStatus(); };
        FormClosing += MainForm_FormClosing;

        SetEditingEnabled(false);
    }

    private void TrySetSplitter()
    {
        try
        {
            int want = ClientSize.Width - 320;
            int min = _split.Panel1MinSize;
            int max = _split.Width - _split.Panel2MinSize - _split.SplitterWidth;
            if (max > min) _split.SplitterDistance = Math.Clamp(want, min, max);
        }
        catch { /* window too small; ignore */ }
    }

    // ---------- UI construction ----------

    private void BuildMenu()
    {
        var menu = new MenuStrip();
        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(new ToolStripMenuItem("&Open Level Folder…", null, (_, _) => OpenLevel()) { ShortcutKeys = Keys.Control | Keys.O });
        file.DropDownItems.Add(new ToolStripMenuItem("&Save", null, (_, _) => Save()) { ShortcutKeys = Keys.Control | Keys.S });
        file.DropDownItems.Add(new ToolStripMenuItem("Save &As…", null, (_, _) => SaveAs()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("E&xit", null, (_, _) => Close()));

        var edit = new ToolStripMenuItem("&Edit");
        edit.DropDownItems.Add(new ToolStripMenuItem("&Undo", null, (_, _) => UndoRedo(undo: true)) { ShortcutKeys = Keys.Control | Keys.Z });
        edit.DropDownItems.Add(new ToolStripMenuItem("&Redo", null, (_, _) => UndoRedo(undo: false)) { ShortcutKeys = Keys.Control | Keys.Y });
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add(new ToolStripMenuItem("&Delete Selected", null, (_, _) => DeleteSelected()) { ShortcutKeys = Keys.Delete });

        var view = new ToolStripMenuItem("&View");
        view.DropDownItems.Add(new ToolStripMenuItem("&Reset Camera", null, (_, _) => { ResetCamera(); RenderView(); }) { ShortcutKeys = Keys.Control | Keys.R });
        view.DropDownItems.Add(new ToolStripMenuItem("&Frame Selected", null, (_, _) => FrameSelected()) { ShortcutKeys = Keys.F });

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(new ToolStripMenuItem("&About", null, (_, _) => ShowAbout()));

        menu.Items.AddRange(new ToolStripItem[] { file, edit, view, help });
        MainMenuStrip = menu;
        Controls.Add(menu);
    }

    private void BuildSidePanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6) };

        // selected-object editor (bottom)
        var props = new GroupBox { Text = "Selected object", Dock = DockStyle.Bottom, Height = 250 };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(6) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Row(string label, Control c) { grid.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }); grid.Controls.Add(c); }
        props.Controls.Add(grid);
        props.Controls.Add(_selTemplate);   // docked top inside group
        _selTemplate.Dock = DockStyle.Top;
        grid.Dock = DockStyle.Fill;
        Row("Pos X", _px); Row("Pos Y (height)", _py); Row("Pos Z", _pz);
        Row("Rot Y°", _rotY); Row("Scale", _scale);
        var del = new Button { Text = "Delete (Del)", Dock = DockStyle.Fill };
        del.Click += (_, _) => DeleteSelected();
        grid.Controls.Add(new Label()); grid.Controls.Add(del);

        // add-object box (middle)
        var addBox = new GroupBox { Text = "Add object (at view centre)", Dock = DockStyle.Bottom, Height = 80 };
        var addGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(6) };
        addGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        addGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        addGrid.Controls.Add(new Label { Text = "Template", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft });
        addGrid.Controls.Add(_newTemplate);
        var add = new Button { Text = "Add", Dock = DockStyle.Fill };
        add.Click += (_, _) => AddObjectAtCentre();
        addGrid.Controls.Add(new Label()); addGrid.Controls.Add(add);
        addBox.Controls.Add(addGrid);

        // object list (fills remaining space)
        panel.Controls.Add(_list);
        panel.Controls.Add(addBox);
        panel.Controls.Add(props);
        panel.Controls.Add(_objCount);   // top

        _split.Panel2.Controls.Add(panel);
    }

    private static NumericUpDown MakeNum(decimal min = -1_000_000, decimal max = 1_000_000, int dp = 3, decimal inc = 1)
        => new() { Dock = DockStyle.Fill, Minimum = min, Maximum = max, DecimalPlaces = dp, Increment = inc, ThousandsSeparator = false };

    // ---------- file ops ----------

    private void OpenLevel()
    {
        using var dlg = new FolderBrowserDialog { Description = "Select a Battlefield Vietnam level folder (containing StaticObjects.con + Heightmap.raw)", UseDescriptionForTitle = true };
        if (dlg.SelectedPath is { Length: > 0 } && Directory.Exists(dlg.SelectedPath)) dlg.InitialDirectory = dlg.SelectedPath;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        LoadLevel(dlg.SelectedPath);
    }

    private void LoadLevel(string dir)
    {
        try
        {
            var scene = LevelScene.Load(dir);
            _scene = scene;
            _levelDir = dir;
            _history = new EditHistory(scene.Objects);
            _staticObjectsPath = Directory.EnumerateFiles(dir, "StaticObjects.con", SearchOption.AllDirectories).FirstOrDefault();
            _selected = -1; _dirty = false;
            ResetCamera();
            RebuildList();
            ClearPropertyEditor();
            SetEditingEnabled(true);
            _hint.Visible = false; _view.Visible = true;
            RenderView();
            UpdateStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Could not load this folder as a level.\r\n\r\n{ex.Message}\r\n\r\n" +
                "Expected to find Terrain.con, Heightmap.raw and StaticObjects.con somewhere inside it.",
                "Open level", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void Save()
    {
        if (_scene is null) return;
        if (_staticObjectsPath is null) { SaveAs(); return; }
        try { _scene.Objects.Save(_staticObjectsPath); _dirty = false; SetStatus($"Saved {_scene.Objects.Objects.Count} objects to {_staticObjectsPath}"); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void SaveAs()
    {
        if (_scene is null) return;
        using var dlg = new SaveFileDialog { Filter = "StaticObjects (*.con)|*.con|All files (*.*)|*.*", FileName = "StaticObjects.con" };
        if (_staticObjectsPath is not null) dlg.InitialDirectory = Path.GetDirectoryName(_staticObjectsPath) ?? "";
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try { _scene.Objects.Save(dlg.FileName); _staticObjectsPath = dlg.FileName; _dirty = false; SetStatus($"Saved to {dlg.FileName}"); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_dirty) return;
        var r = MessageBox.Show(this, "You have unsaved changes. Save before closing?", "RefractorForge",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (r == DialogResult.Cancel) { e.Cancel = true; return; }
        if (r == DialogResult.Yes) Save();
    }

    // ---------- selection + list ----------

    private void RebuildList()
    {
        if (_scene is null) return;
        _suppress = true;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var o in _scene.Objects.Objects)
            _list.Items.Add($"{o.Template}   ({o.Position.X:0.#}, {o.Position.Z:0.#})");
        _list.EndUpdate();
        if ((uint)_selected < (uint)_list.Items.Count) _list.SelectedIndex = _selected;
        _objCount.Text = $"{_scene.Objects.Objects.Count} objects";
        _suppress = false;
    }

    private void SetSelected(int idx)
    {
        if (_scene is null) return;
        _selected = (uint)idx < (uint)_scene.Objects.Objects.Count ? idx : -1;
        _suppress = true;
        if (_selected >= 0 && _list.SelectedIndex != _selected) _list.SelectedIndex = _selected;
        _suppress = false;
        LoadPropertyEditor();
        RenderView();
        UpdateStatus();
    }

    private StaticObject? Current => _scene is not null && (uint)_selected < (uint)_scene.Objects.Objects.Count ? _scene.Objects.Objects[_selected] : null;

    private void LoadPropertyEditor()
    {
        var o = Current;
        _suppress = true;
        if (o is null)
        {
            _selTemplate.Text = "(nothing selected)";
            _px.Enabled = _py.Enabled = _pz.Enabled = _rotY.Enabled = _scale.Enabled = false;
        }
        else
        {
            _selTemplate.Text = $"{o.Template}   [{o.Id[..Math.Min(8, o.Id.Length)]}]";
            _px.Enabled = _py.Enabled = _pz.Enabled = _rotY.Enabled = _scale.Enabled = true;
            SetNum(_px, o.Position.X); SetNum(_py, o.Position.Y); SetNum(_pz, o.Position.Z);
            SetNum(_rotY, o.Rotation.Y); SetNum(_scale, o.Scale ?? 1f);
        }
        _suppress = false;
    }

    private void ClearPropertyEditor() { _selected = -1; LoadPropertyEditor(); }

    private static void SetNum(NumericUpDown n, float v)
    {
        decimal d;
        try { d = (decimal)v; } catch { d = 0m; }
        n.Value = Math.Clamp(d, n.Minimum, n.Maximum);
    }

    // ---------- edits (all go through EditHistory so Undo/Redo just work) ----------

    private void CommitPosition()
    {
        if (_suppress) return; var o = Current; if (o is null || _history is null) return;
        var to = new Vec3((float)_px.Value, (float)_py.Value, (float)_pz.Value);
        if (to == o.Position) return;
        _history.Do(new MoveObject(o.Id, to)); MarkDirty(); RebuildList(); RenderView();
    }

    private void CommitRotation()
    {
        if (_suppress) return; var o = Current; if (o is null || _history is null) return;
        var to = new Vec3(o.Rotation.X, (float)_rotY.Value, o.Rotation.Z);
        if (to == o.Rotation) return;
        _history.Do(new RotateObject(o.Id, to)); MarkDirty(); RenderView();
    }

    private void CommitScale()
    {
        if (_suppress) return; var o = Current; if (o is null || _history is null) return;
        float to = (float)_scale.Value;
        if (o.Scale.HasValue && Math.Abs(o.Scale.Value - to) < 1e-6f) return;
        _history.Do(new ScaleObject(o.Id, to)); MarkDirty(); RenderView();
    }

    private void AddObjectAtCentre()
    {
        if (_scene is null || _history is null) return;
        string tmpl = string.IsNullOrWhiteSpace(_newTemplate.Text) ? "object" : _newTemplate.Text.Trim();
        var pos = new Vec3(_target.X, _scene.MidHeight, _target.Z);
        string id = "ui-" + Guid.NewGuid().ToString("N")[..10];
        _history.Do(new AddObject(id, tmpl, pos, Vec3.Zero));
        MarkDirty();
        RebuildList();
        int idx = _scene.Objects.Objects.FindIndex(o => o.Id == id);
        SetSelected(idx);
        SetStatus($"Added '{tmpl}' at ({pos.X:0.#}, {pos.Z:0.#})");
    }

    private void DeleteSelected()
    {
        var o = Current; if (o is null || _history is null || _scene is null) return;
        _history.Do(new DeleteObject(o.Id));
        MarkDirty();
        _selected = -1;
        RebuildList();
        ClearPropertyEditor();
        RenderView();
        SetStatus($"Deleted '{o.Template}'");
    }

    private void UndoRedo(bool undo)
    {
        if (_history is null) return;
        bool changed = undo ? _history.Undo() : _history.Redo();
        if (!changed) return;
        MarkDirty();
        if (_scene is not null && _selected >= _scene.Objects.Objects.Count) _selected = -1;
        RebuildList();
        LoadPropertyEditor();
        RenderView();
        UpdateStatus();
    }

    private void MarkDirty() { _dirty = true; }

    // ---------- camera ----------

    private void ResetCamera()
    {
        if (_scene is null) return;
        _target = new Vector3(_scene.WorldSize / 2f, _scene.MidHeight, _scene.WorldSize / 2f);
        _dist = _scene.WorldSize * 0.95f;
        _az = MathF.PI * 0.75f; _el = MathF.PI * 0.22f;
    }

    private void FrameSelected()
    {
        var o = Current; if (o is null) return;
        _target = new Vector3(o.Position.X, o.Position.Y + LevelScene.ProxyHeight * 0.5f, o.Position.Z);
        _dist = 120f;
        RenderView();
    }

    private void UpdateCamera(int w, int h)
    {
        var dir = new Vector3(MathF.Cos(_el) * MathF.Sin(_az), MathF.Sin(_el), MathF.Cos(_el) * MathF.Cos(_az));
        _cam.Position = _target + dir * _dist;
        var look = Vector3.Normalize(_target - _cam.Position);
        _cam.Pitch = MathF.Asin(Math.Clamp(look.Y, -1f, 1f));
        _cam.Yaw = MathF.Atan2(look.X, look.Z);
        _cam.Aspect = h > 0 ? (float)w / h : 1f;
        _cam.Far = MathF.Max(_scene?.WorldSize * 4f ?? 60000f, _dist * 4f);
    }

    // ---------- mouse / render ----------

    private void View_MouseDown(object? sender, MouseEventArgs e)
    {
        _view.Focus();
        _downAt = _lastAt = e.Location;
        _dragBtn = e.Button;
    }

    private void View_MouseMove(object? sender, MouseEventArgs e)
    {
        if (_scene is null || _dragBtn == MouseButtons.None) return;
        int dx = e.X - _lastAt.X, dy = e.Y - _lastAt.Y;
        _lastAt = e.Location;
        bool pan = _dragBtn == MouseButtons.Right || (_dragBtn == MouseButtons.Left && (ModifierKeys & Keys.Shift) != 0) || _dragBtn == MouseButtons.Middle;
        if (pan)
        {
            // pan target across the view plane, scaled by distance so it feels 1:1
            float s = _dist * 0.0015f;
            var right = _cam.Right;
            var up = Vector3.Normalize(Vector3.Cross(right, _cam.Forward));
            _target += (-right * dx + up * dy) * s;
        }
        else // orbit
        {
            _az -= dx * 0.01f;
            _el = Math.Clamp(_el - dy * 0.01f, 0.05f, 1.55f);
        }
        RenderView();
    }

    private void View_MouseUp(object? sender, MouseEventArgs e)
    {
        if (_scene is null) { _dragBtn = MouseButtons.None; return; }
        int moved = Math.Abs(e.X - _downAt.X) + Math.Abs(e.Y - _downAt.Y);
        if (e.Button == MouseButtons.Left && moved < 4)   // a click, not a drag => pick
        {
            int w = Math.Max(1, _view.ClientSize.Width), h = Math.Max(1, _view.ClientSize.Height);
            UpdateCamera(w, h);
            var ray = Picking.ScreenToRay(_cam, e.X, e.Y, w, h);
            int idx = Picking.PickNearest(ray, _scene.PickPoints(), _scene.PickRadius);
            SetSelected(idx);
        }
        _dragBtn = MouseButtons.None;
    }

    private void View_MouseWheel(object? sender, MouseEventArgs e)
    {
        if (_scene is null) return;
        float factor = MathF.Pow(1.0015f, -e.Delta);   // wheel up = zoom in
        _dist = Math.Clamp(_dist * factor, 8f, _scene.WorldSize * 3f);
        RenderView();
    }

    private void RenderView()
    {
        if (_scene is null || !_view.Visible) return;
        int w = Math.Max(1, _view.ClientSize.Width), h = Math.Max(1, _view.ClientSize.Height);
        UpdateCamera(w, h);
        var buf = _scene.Render(_cam, w, h, _selected);
        var old = _view.Image;
        _view.Image = ToBitmap(buf);
        old?.Dispose();
        UpdateHud();
    }

    /// <summary>Copy the engine's RGB framebuffer into a 24bpp GDI bitmap (R/B swapped, top-down).</summary>
    private static Bitmap ToBitmap(ImageBuffer buf)
    {
        var bmp = new Bitmap(buf.W, buf.H, PixelFormat.Format24bppRgb);
        var data = bmp.LockBits(new Rectangle(0, 0, buf.W, buf.H), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            int stride = data.Stride;
            var row = new byte[stride];
            for (int y = 0; y < buf.H; y++)
            {
                int src = y * buf.W * 3;
                for (int x = 0; x < buf.W; x++)
                {
                    row[x * 3 + 0] = buf.Rgb[src + x * 3 + 2]; // B
                    row[x * 3 + 1] = buf.Rgb[src + x * 3 + 1]; // G
                    row[x * 3 + 2] = buf.Rgb[src + x * 3 + 0]; // R
                }
                Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * stride), stride);
            }
        }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }

    // ---------- status ----------

    private void SetEditingEnabled(bool on)
    {
        _list.Enabled = on; _newTemplate.Enabled = on;
        if (!on) { _px.Enabled = _py.Enabled = _pz.Enabled = _rotY.Enabled = _scale.Enabled = false; }
    }

    private void SetStatus(string msg) { _status.Text = msg; }

    private void UpdateStatus()
    {
        if (_scene is null) { _status.Text = "Ready — open a level to begin."; return; }
        string sel = Current is { } o ? $"   |   selected: {o.Template}" : "";
        string undo = _history is null ? "" : $"   |   undo {_history.UndoDepth} / redo {_history.RedoDepth}";
        _status.Text = $"{_levelDir}{sel}{undo}{(_dirty ? "   |   ● unsaved" : "")}";
        UpdateHud();
    }

    private void UpdateHud()
    {
        if (_scene is null) { _hud.Text = ""; return; }
        _hud.Text = $"world {_scene.WorldSize:0}m   |   dist {_dist:0}m   |   LMB orbit · wheel zoom · RMB/Shift pan · click select";
    }

    private void ShowAbout()
    {
        MessageBox.Show(this,
            "RefractorForge — a modern, open editor for Battlefield Vietnam maps.\r\n\r\n" +
            "• Loads real levels (terrain + StaticObjects.con) and round-trips them losslessly.\r\n" +
            "• Objects render as proxy boxes (real position/rotation/scale); real meshes are a future upgrade.\r\n" +
            "• Move/rotate/scale/add/delete with full undo-redo; built on the same command layer as live collaboration.\r\n\r\n" +
            "Software-rendered (no GPU required).",
            "About RefractorForge", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _view.Image?.Dispose();
        base.OnFormClosed(e);
    }
}
