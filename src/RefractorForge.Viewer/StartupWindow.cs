using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace RefractorForge.Viewer;

public enum StartupAction { OpenProject, OpenRfa, OpenMod, OpenFolder, NewMap, Cancel }

/// <summary>The user's choice on the startup screen. <see cref="RecentPath"/> is set only when a Recent Projects
/// row was double-clicked (an Open-Project shortcut to that <c>.rfproj</c>).</summary>
public sealed record StartupChoice(StartupAction Action, string? RecentPath = null);

/// <summary>
/// The interactive launch screen (Recent Projects list + four actions), matching the contributor's mockup. A
/// WinForms window shown BEFORE the GL window (like the splash), on its own STA thread; it returns the user's
/// choice and the caller runs the follow-up flow. Button hues use the Okabe-Ito colorblind-safe qualitative palette
/// (blue / orange / reddish-purple / bluish-green) so no two are confusable under red/green colorblindness, and text
/// colour is chosen for contrast.
/// </summary>
internal static class StartupWindow
{
    public static StartupChoice Show()
    {
        StartupChoice result = new(StartupAction.Cancel);
        var t = new Thread(() =>
        {
            try
            {
                // The language buttons close and rebuild the window rather than re-labelling it in place: the fonts,
                // the button widths and the painted header all depend on the script, and a rebuild is instant here.
                bool rebuild;
                do
                {
                    rebuild = false;
                    using var f = Build(c => result = c, () => rebuild = true);
                    Application.Run(f);
                } while (rebuild);
            }
            catch { }
        }) { Name = "startup" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return result;
    }

    private static Color TextOn(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) > 150 ? Color.FromArgb(20, 20, 20) : Color.White;

    /// <summary>A font family that can actually draw the active language. This window paints with GDI+
    /// (<c>Graphics.DrawString</c>), which does NOT font-link the way GDI does, so Segoe UI would render every
    /// Japanese character as an empty box. Probe the installed families rather than trusting a name - WinForms
    /// silently substitutes a missing font, which would look like a styling bug rather than a missing face.</summary>
    private static string _face = "";
    private static string Face
    {
        get
        {
            if (_face.Length > 0) return _face;
            var want = Loc.NeedsWideFont
                ? new[] { "Yu Gothic UI", "Meiryo UI", "Meiryo", "MS UI Gothic", "Yu Gothic", "Segoe UI" }
                : new[] { "Segoe UI" };
            try
            {
                using var installed = new InstalledFontCollection();
                var have = installed.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                _face = want.FirstOrDefault(have.Contains) ?? "Segoe UI";
            }
            catch { _face = "Segoe UI"; }
            return _face;
        }
    }

    private static Font Ui(float size, FontStyle style = FontStyle.Regular) => new(Face, size, style);

    private static Form Build(Action<StartupChoice> choose, Action rebuild)
    {
        const int W = 900, H = 560;
        var form = new Form
        {
            Text = "RefractorForge",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MaximizeBox = false,
            MinimizeBox = false,
            ClientSize = new Size(W, H),
            BackColor = Color.FromArgb(24, 26, 30),
        };
        try { var ico = Path.Combine(AppContext.BaseDirectory, "RefractorForge.ico"); if (File.Exists(ico)) form.Icon = new Icon(ico); } catch { }

        Image? bg = null;
        try { var img = Path.Combine(AppContext.BaseDirectory, "refractorforgesplash.png"); if (File.Exists(img)) bg = Image.FromFile(img); } catch { }

        void Choose(StartupChoice c) { choose(c); form.Close(); }

        form.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            if (bg is not null) g.DrawImage(bg, new Rectangle(0, 0, W, (int)(bg.Height * (W / (float)bg.Width))));
            using (var ov = new SolidBrush(Color.FromArgb(206, 20, 22, 26))) g.FillRectangle(ov, 0, 0, W, H);
            using var title = Ui(28f, FontStyle.Bold);
            using var sub = Ui(11f);
            using var credit = Ui(10f);
            using (var gold = new SolidBrush(Color.FromArgb(240, 200, 90))) g.DrawString("RefractorForge", title, gold, 28, 20);
            g.DrawString(Loc.T("BF1942 / BFVietnam Map Editor"), sub, Brushes.Gainsboro, 32, 72);
            var cs = "developed by LuccaWulf"; var csz = g.MeasureString(cs, credit);
            using (var cb = new SolidBrush(Color.FromArgb(215, 215, 225))) g.DrawString(cs, credit, cb, W - csz.Width - 24, 30);
            using (var pen = new Pen(Color.FromArgb(60, 255, 255, 255))) g.DrawLine(pen, 24, 104, W - 24, 104);
            using (var hb = new SolidBrush(Color.FromArgb(180, 190, 200))) g.DrawString(Loc.T("Recent Projects"), sub, hb, 28, 112);
        };

        // Language toggle, sitting on the header rule. Every option is written in its OWN language, so a Japanese
        // speaker can find it without reading any English - the same reason the first-run prompt is bilingual.
        // English stays the DEFAULT: Loc.Current is "en" until somebody chooses otherwise, and nothing here infers a
        // language from the machine's locale.
        int lx = W - 24;
        foreach (var lang in new[] { new { Code = "ja", Label = "日本語" }, new { Code = "en", Label = "English" } })
        {
            bool active = string.Equals(Loc.Current, lang.Code, StringComparison.OrdinalIgnoreCase);
            var col = active ? Color.FromArgb(0, 114, 178) : Color.FromArgb(44, 48, 55);
            int bw = lang.Code == "en" ? 68 : 62;
            lx -= bw;
            var b = new Button
            {
                Text = lang.Label, Left = lx, Top = 108, Width = bw, Height = 26,
                FlatStyle = FlatStyle.Flat, BackColor = col,
                ForeColor = active ? Color.White : Color.FromArgb(170, 175, 185),
                Font = new Font(lang.Code == "ja" ? "Yu Gothic UI" : "Segoe UI", 9f, active ? FontStyle.Bold : FontStyle.Regular),
                Cursor = Cursors.Hand, TabStop = false,
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(col, 0.25f);
            var code = lang.Code;
            b.Click += (_, _) =>
            {
                if (string.Equals(Loc.Current, code, StringComparison.OrdinalIgnoreCase)) return;
                Loc.SetLanguage(code);
                _face = "";        // re-probe: the face that can draw the new script may differ
                rebuild();
                form.Close();      // Show()'s loop rebuilds the window in the language just chosen
            };
            form.Controls.Add(b);
            b.BringToFront();
            lx -= 6;
        }

        var recents = RecentProjects.Load();
        var list = new ListBox
        {
            Left = 24, Top = 140, Width = W - 48, Height = H - 140 - 92,
            BackColor = Color.FromArgb(30, 33, 38), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.None,
            DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 46, IntegralHeight = false,
        };
        foreach (var r in recents) list.Items.Add(r);
        list.DrawItem += (_, e) =>
        {
            if (e.Index < 0 || e.Index >= recents.Count) return;
            var r = recents[e.Index]; var g = e.Graphics;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(sel ? Color.FromArgb(48, 74, 110) : Color.FromArgb(34, 37, 43))) g.FillRectangle(b, e.Bounds);
            using var name = Ui(12f, FontStyle.Bold);
            using var path = Ui(8.5f);
            g.DrawString(r.Name, name, Brushes.White, e.Bounds.Left + 10, e.Bounds.Top + 5);
            using (var pb = new SolidBrush(Color.FromArgb(150, 155, 165))) g.DrawString(r.ProjectPath, path, pb, e.Bounds.Left + 12, e.Bounds.Top + 26);
            using var gb = new SolidBrush(Color.FromArgb(120, 170, 220));
            var gsz = g.MeasureString(r.Game, name); g.DrawString(r.Game, name, gb, e.Bounds.Right - gsz.Width - 12, e.Bounds.Top + 12);
        };
        list.DoubleClick += (_, _) => { if (list.SelectedIndex >= 0 && list.SelectedIndex < recents.Count) Choose(new StartupChoice(StartupAction.OpenProject, recents[list.SelectedIndex].ProjectPath)); };
        form.Controls.Add(list);
        if (recents.Count == 0)
        {
            var hint = new Label { Text = Loc.T("No recent projects — open a level or create a new map to get started."), AutoSize = false, Left = 28, Top = 150, Width = W - 56, Height = 40, ForeColor = Color.FromArgb(150, 155, 165), BackColor = Color.Transparent, Font = Ui(10.5f) };
            form.Controls.Add(hint); hint.BringToFront();
        }

        Button Btn(string text, Color col, int idx, StartupAction act)
        {
            const int n = 4, pad = 24, gap = 12;
            int bw = (W - pad * 2 - gap * (n - 1)) / n;
            var b = new Button
            {
                Text = Loc.T(text), Left = pad + idx * (bw + gap), Top = H - 68, Width = bw, Height = 46,
                FlatStyle = FlatStyle.Flat, BackColor = col, ForeColor = TextOn(col),
                Font = Ui(10.5f, FontStyle.Bold), Cursor = Cursors.Hand, TabStop = false,
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(col, 0.18f);
            b.Click += (_, _) => Choose(new StartupChoice(act));
            return b;
        }
        // Okabe-Ito colorblind-safe hues (no two confusable under red/green CB).
        form.Controls.Add(Btn("Open Project  (.rfproj)", Color.FromArgb(0, 114, 178), 0, StartupAction.OpenProject));   // blue
        // Open MOD rather than a bare map .rfa: a level opened on its own has no object or texture library behind
        // it, so half a mod's map shows up empty. Picking the mod first resolves its init.con mount chain, and the
        // map is then chosen from that mod's own levels folder.
        form.Controls.Add(Btn("Open Mod", Color.FromArgb(230, 159, 0), 1, StartupAction.OpenMod));                     // orange
        form.Controls.Add(Btn("Open Level Folder", Color.FromArgb(204, 121, 167), 2, StartupAction.OpenFolder));       // reddish-purple
        form.Controls.Add(Btn("New Map", Color.FromArgb(0, 158, 115), 3, StartupAction.NewMap));                        // bluish-green

        form.FormClosed += (_, _) => { try { bg?.Dispose(); } catch { } };
        return form;
    }
}
