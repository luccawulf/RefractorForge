using System.Drawing.Drawing2D;

namespace RefractorForge.Archive;

/// <summary>
/// The look: a dark, low-contrast surface with one accent, so a wall of file names reads as a list rather than
/// a spreadsheet, and the preview - the thing you actually came to see - is the brightest area on screen.
///
/// WinForms has no theme system, so this is applied by walking the control tree and by owner-drawing what
/// will not recolour on its own (menus, headers, the list). Everything colour-related lives here so the rest of
/// the program never names a colour.
/// </summary>
public static class Theme
{
    public static readonly Color Bg        = Color.FromArgb(24, 26, 30);      // window
    public static readonly Color Surface   = Color.FromArgb(31, 34, 39);      // panels, list
    public static readonly Color Raised    = Color.FromArgb(40, 44, 50);      // toolbars, headers
    public static readonly Color Border    = Color.FromArgb(52, 57, 64);
    public static readonly Color Text      = Color.FromArgb(222, 226, 232);
    public static readonly Color TextDim   = Color.FromArgb(140, 148, 160);
    public static readonly Color TextFaint = Color.FromArgb(92, 99, 110);
    public static readonly Color Accent    = Color.FromArgb(86, 156, 214);    // selection, links, the one colour
    public static readonly Color AccentDim = Color.FromArgb(44, 74, 104);
    public static readonly Color Selection = Color.FromArgb(48, 78, 112);
    public static readonly Color Hover     = Color.FromArgb(38, 43, 50);
    public static readonly Color Good      = Color.FromArgb(110, 190, 120);
    public static readonly Color Warn      = Color.FromArgb(230, 180, 80);
    public static readonly Color Bad       = Color.FromArgb(225, 95, 90);
    public static readonly Color Folder    = Color.FromArgb(232, 190, 96);
    public static readonly Color Stripe    = Color.FromArgb(28, 31, 36);      // alternate rows

    // Entry states, as drawn in the Status column and the row tint.
    public static readonly Color Added    = Color.FromArgb(110, 190, 120);
    public static readonly Color Replaced = Color.FromArgb(230, 180, 80);
    public static readonly Color Deleted  = Color.FromArgb(225, 95, 90);
    public static readonly Color Overridden = Color.FromArgb(150, 130, 220); // workspace: this copy shadows another

    // .con syntax.
    public static readonly Color SynComment = Color.FromArgb(106, 118, 130);
    public static readonly Color SynKeyword = Color.FromArgb(197, 134, 192);
    public static readonly Color SynObject  = Color.FromArgb(86, 156, 214);
    public static readonly Color SynProp    = Color.FromArgb(156, 220, 254);
    public static readonly Color SynString  = Color.FromArgb(206, 145, 120);
    public static readonly Color SynNumber  = Color.FromArgb(181, 206, 168);
    public static readonly Color SynRun     = Color.FromArgb(220, 220, 170);

    public static readonly Font UiFont = new("Segoe UI", 9.5f);
    public static readonly Font UiBold = new("Segoe UI", 9.5f, FontStyle.Bold);
    public static readonly Font Mono = new("Cascadia Mono", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
    public static readonly Font Small = new("Segoe UI", 8.25f);
    public static readonly Font Title = new("Segoe UI Semibold", 11f);

    /// <summary>Pixels at 96 DPI -> pixels on this display. Fonts already scale; layout has to be told.</summary>
    public static int Dp(int v)
    {
        float dpi;
        try { using var g = Graphics.FromHwnd(IntPtr.Zero); dpi = g.DpiX; } catch { dpi = 96f; }
        return (int)Math.Round(v * dpi / 96f);
    }
    public static Size Dp(Control c, int w, int h) => new(Dp(w), Dp(h));

    /// <summary>Recolour a control and everything under it.</summary>
    public static void Apply(Control root)
    {
        void Walk(Control c)
        {
            switch (c)
            {
                case Form f:
                    f.BackColor = Bg; f.ForeColor = Text; f.Font = UiFont; break;
                case MenuStrip m:
                    m.BackColor = Raised; m.ForeColor = Text; m.Renderer = new DarkRenderer(); break;
                case StatusStrip s:
                    s.BackColor = Raised; s.ForeColor = TextDim; s.Renderer = new DarkRenderer(); s.SizingGrip = false; break;
                case ToolStrip t:
                    t.BackColor = Raised; t.ForeColor = Text; t.Renderer = new DarkRenderer(); t.GripStyle = ToolStripGripStyle.Hidden; break;
                case ListView lv:
                    lv.BackColor = Surface; lv.ForeColor = Text; break;
                case TreeView tv:
                    tv.BackColor = Surface; tv.ForeColor = Text; tv.LineColor = Border; break;
                case TextBox tb:
                    tb.BackColor = Surface; tb.ForeColor = Text; tb.BorderStyle = BorderStyle.FixedSingle; break;
                case RichTextBox rtb:
                    rtb.BackColor = Surface; rtb.ForeColor = Text; rtb.BorderStyle = BorderStyle.None; break;
                case ComboBox cb:
                    cb.BackColor = Surface; cb.ForeColor = Text; cb.FlatStyle = FlatStyle.Flat; break;
                case Button b:
                    StyleButton(b); break;
                case CheckBox ch:
                    ch.ForeColor = Text; ch.BackColor = Color.Transparent; break;
                case RadioButton rb:
                    rb.ForeColor = Text; rb.BackColor = Color.Transparent; break;
                case Label l:
                    l.ForeColor = l.ForeColor == SystemColors.ControlText || l.ForeColor == Color.Black ? Text : l.ForeColor;
                    l.BackColor = Color.Transparent; break;
                case SplitContainer sc:
                    sc.BackColor = Border; sc.Panel1.BackColor = Bg; sc.Panel2.BackColor = Bg; break;
                case TabControl tc:
                    tc.BackColor = Bg; break;
                case Panel p:
                    if (p.BackColor == SystemColors.Control || p.BackColor == SystemColors.ControlDark) p.BackColor = Bg;
                    break;
                case GroupBox g:
                    g.ForeColor = TextDim; g.BackColor = Bg; break;
                case ProgressBar pb:
                    pb.BackColor = Surface; pb.ForeColor = Accent; break;
                case NumericUpDown n:
                    n.BackColor = Surface; n.ForeColor = Text; break;
                default:
                    if (c.BackColor == SystemColors.Control) c.BackColor = Bg;
                    break;
            }
            foreach (Control child in c.Controls) Walk(child);
        }
        Walk(root);
    }

    public static void StyleButton(Button b, bool primary = false)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = primary ? Accent : Border;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.MouseOverBackColor = primary ? Accent : Hover;
        b.FlatAppearance.MouseDownBackColor = primary ? AccentDim : Raised;
        b.BackColor = primary ? AccentDim : Raised;
        b.ForeColor = Text;
        b.Cursor = Cursors.Hand;
        b.UseVisualStyleBackColor = false;
    }

    /// <summary>Colour for an entry state, or null for unchanged.</summary>
    public static Color? StateColor(ArchiveModel.EntryState s) => s switch
    {
        ArchiveModel.EntryState.Added => Added,
        ArchiveModel.EntryState.Replaced => Replaced,
        ArchiveModel.EntryState.Deleted => Deleted,
        _ => null,
    };

    /// <summary>Menus, toolbars and status bars use their own renderer; this one paints them in the palette.</summary>
    public sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var b = new SolidBrush(Raised);
            e.Graphics.FillRectangle(b, e.AffectedBounds);
        }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (e.ToolStrip is MenuStrip or StatusStrip)
            {
                using var p = new Pen(Border);
                int y = e.ToolStrip is MenuStrip ? e.AffectedBounds.Bottom - 1 : 0;
                e.Graphics.DrawLine(p, 0, y, e.AffectedBounds.Width, y);
            }
        }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var r = new Rectangle(Point.Empty, e.Item.Size);
            if (e.Item.Selected || e.Item.Pressed)
            {
                using var b = new SolidBrush(e.Item.Enabled ? Selection : Hover);
                e.Graphics.FillRectangle(b, r);
            }
        }
        protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
        {
            var r = new Rectangle(Point.Empty, e.Item.Size);
            if (e.Item is ToolStripButton { Checked: true } || e.Item.Pressed)
            {
                using var b = new SolidBrush(AccentDim);
                e.Graphics.FillRectangle(b, r);
            }
            else if (e.Item.Selected)
            {
                using var b = new SolidBrush(Hover);
                e.Graphics.FillRectangle(b, r);
            }
        }
        protected override void OnRenderDropDownButtonBackground(ToolStripItemRenderEventArgs e) => OnRenderButtonBackground(e);
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Text : TextFaint;
            base.OnRenderItemText(e);
        }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var p = new Pen(Border);
            if (e.Vertical)
            {
                int x = e.Item.Width / 2;
                e.Graphics.DrawLine(p, x, 4, x, e.Item.Height - 4);
            }
            else
            {
                int y = e.Item.Height / 2;
                e.Graphics.DrawLine(p, 28, y, e.Item.Width - 4, y);
            }
        }
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            using var b = new SolidBrush(Raised);
            e.Graphics.FillRectangle(b, e.AffectedBounds);
        }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Text;
            base.OnRenderArrow(e);
        }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            using var p = new Pen(Accent, 2f);
            var r = e.ImageRectangle;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.DrawLines(p, new[] { new Point(r.Left + 3, r.Top + r.Height / 2), new Point(r.Left + r.Width / 2 - 1, r.Bottom - 4), new Point(r.Right - 3, r.Top + 3) });
        }
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Selection;
        public override Color MenuItemSelected => Selection;
        public override Color MenuItemSelectedGradientBegin => Selection;
        public override Color MenuItemSelectedGradientEnd => Selection;
        public override Color MenuItemPressedGradientBegin => Raised;
        public override Color MenuItemPressedGradientEnd => Raised;
        public override Color MenuStripGradientBegin => Raised;
        public override Color MenuStripGradientEnd => Raised;
        public override Color ToolStripDropDownBackground => Raised;
        public override Color ImageMarginGradientBegin => Raised;
        public override Color ImageMarginGradientMiddle => Raised;
        public override Color ImageMarginGradientEnd => Raised;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
        public override Color ToolStripBorder => Border;
        public override Color StatusStripGradientBegin => Raised;
        public override Color StatusStripGradientEnd => Raised;
    }

    /// <summary>A flat vector glyph for the toolbar, drawn rather than shipped as a bitmap so it scales and takes
    /// the palette. Kept to a handful of simple shapes.</summary>
    public static Bitmap Glyph(string kind, int size, Color? color = null)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var c = color ?? Text;
        using var pen = new Pen(c, Math.Max(1.5f, size / 11f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(c);
        float s = size, m = s * 0.18f, w = s - 2 * m;
        switch (kind)
        {
            case "open":      // a folder
                g.DrawPath(pen, FolderPath(m, m + w * 0.15f, w, w * 0.7f));
                break;
            case "mod":       // stacked layers
                for (int i = 0; i < 3; i++)
                {
                    float y = m + i * w * 0.28f;
                    g.DrawPolygon(pen, new[] { new PointF(m, y + w * 0.2f), new PointF(m + w / 2, y), new PointF(m + w, y + w * 0.2f), new PointF(m + w / 2, y + w * 0.4f) });
                }
                break;
            case "save":      // a floppy-ish square with a notch
                g.DrawRectangle(pen, m, m, w, w);
                g.FillRectangle(brush, m + w * 0.25f, m, w * 0.5f, w * 0.3f);
                break;
            case "extract":   // arrow out of a tray
                g.DrawLine(pen, s / 2, m, s / 2, m + w * 0.6f);
                g.DrawLines(pen, new[] { new PointF(s / 2 - w * 0.25f, m + w * 0.35f), new PointF(s / 2, m + w * 0.6f), new PointF(s / 2 + w * 0.25f, m + w * 0.35f) });
                g.DrawLines(pen, new[] { new PointF(m, m + w * 0.65f), new PointF(m, m + w), new PointF(m + w, m + w), new PointF(m + w, m + w * 0.65f) });
                break;
            case "add":
                g.DrawLine(pen, s / 2, m, s / 2, m + w);
                g.DrawLine(pen, m, s / 2, m + w, s / 2);
                break;
            case "search":
                float r = w * 0.32f;
                g.DrawEllipse(pen, m, m, r * 2, r * 2);
                g.DrawLine(pen, m + r * 1.7f, m + r * 1.7f, m + w, m + w);
                break;
            case "diff":      // two columns with a tilde
                g.DrawRectangle(pen, m, m, w * 0.4f, w);
                g.DrawRectangle(pen, m + w * 0.6f, m, w * 0.4f, w);
                break;
            case "refs":      // a node with spokes
                g.FillEllipse(brush, s / 2 - w * 0.12f, s / 2 - w * 0.12f, w * 0.24f, w * 0.24f);
                for (int i = 0; i < 4; i++)
                {
                    double a = i * Math.PI / 2 + Math.PI / 4;
                    g.DrawLine(pen, s / 2, s / 2, s / 2 + (float)Math.Cos(a) * w / 2, s / 2 + (float)Math.Sin(a) * w / 2);
                }
                break;
            case "broom":     // unused-asset sweep: a slanted stroke with bristles
                g.DrawLine(pen, m + w * 0.15f, m, m + w * 0.65f, m + w * 0.55f);
                g.DrawLines(pen, new[] { new PointF(m + w * 0.45f, m + w * 0.75f), new PointF(m + w * 0.65f, m + w * 0.55f), new PointF(m + w, m + w * 0.9f), new PointF(m + w * 0.55f, m + w) });
                break;
            case "server":    // a rack
                g.DrawRectangle(pen, m, m, w, w * 0.4f);
                g.DrawRectangle(pen, m, m + w * 0.6f, w, w * 0.4f);
                g.FillEllipse(brush, m + w * 0.12f, m + w * 0.13f, w * 0.14f, w * 0.14f);
                g.FillEllipse(brush, m + w * 0.12f, m + w * 0.73f, w * 0.14f, w * 0.14f);
                break;
            case "wand":      // new mod
                g.DrawLine(pen, m, m + w, m + w * 0.7f, m + w * 0.3f);
                g.FillEllipse(brush, m + w * 0.68f, m + w * 0.02f, w * 0.3f, w * 0.3f);
                break;
            case "clone":
                g.DrawRectangle(pen, m, m + w * 0.3f, w * 0.7f, w * 0.7f);
                g.DrawLines(pen, new[] { new PointF(m + w * 0.3f, m + w * 0.3f), new PointF(m + w * 0.3f, m), new PointF(m + w, m), new PointF(m + w, m + w * 0.7f), new PointF(m + w * 0.7f, m + w * 0.7f) });
                break;
            case "check":
                g.DrawLines(pen, new[] { new PointF(m, s / 2), new PointF(m + w * 0.38f, m + w * 0.85f), new PointF(m + w, m + w * 0.15f) });
                break;
            case "folder":
                g.FillPath(brush, FolderPath(m, m + w * 0.15f, w, w * 0.7f));
                break;
            case "file":
                g.DrawLines(pen, new[] { new PointF(m + w * 0.15f, m), new PointF(m + w * 0.65f, m), new PointF(m + w * 0.9f, m + w * 0.25f), new PointF(m + w * 0.9f, m + w), new PointF(m + w * 0.15f, m + w), new PointF(m + w * 0.15f, m) });
                break;
        }
        return bmp;
    }

    private static GraphicsPath FolderPath(float x, float y, float w, float h)
    {
        var p = new GraphicsPath();
        p.AddLines(new[]
        {
            new PointF(x, y), new PointF(x + w * 0.38f, y), new PointF(x + w * 0.48f, y + h * 0.18f),
            new PointF(x + w, y + h * 0.18f), new PointF(x + w, y + h), new PointF(x, y + h),
        });
        p.CloseFigure();
        return p;
    }
}
