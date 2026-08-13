using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace RefractorForge.Viewer;

/// <summary>
/// A borderless launch splash (the bundled refractorforgesplash.png + the "RefractorForge" title and
/// "developed by LuccaWulf" credit overlaid - the in-app credit uses the handle the BF modding community knows,
/// while README/USER_GUIDE carry the full legal name). Runs on its own STA thread with a message loop so it keeps
/// painting while the main (MTA) thread does the heavy level/GL load, then is closed once the editor is ready.
/// </summary>
internal static class SplashScreen
{
    private static Form? _form;
    private static Thread? _thread;
    private static int _shownTick;   // Environment.TickCount when the splash first painted (for WaitVisibleFor)

    public static void Show()
    {
        try
        {
            string img = Path.Combine(AppContext.BaseDirectory, "refractorforgesplash.png");
            if (!File.Exists(img)) return;
            var ready = new ManualResetEventSlim(false);
            _thread = new Thread(() =>
            {
                try
                {
                    _form = Build(img);
                    _form.Shown += (_, _) => { _shownTick = Environment.TickCount; ready.Set(); };
                    Application.Run(_form);
                }
                catch { ready.Set(); }
            }) { IsBackground = true, Name = "splash" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait(2000);   // don't block startup forever if the form fails to show
        }
        catch { }
    }

    /// <summary>Block the caller until the splash has been visible for at least <paramref name="ms"/> ms (so it gets
    /// its own on-screen moment before the level picker opens). No-op if the splash never showed.</summary>
    public static void WaitVisibleFor(int ms)
    {
        if (_form is null || _shownTick == 0) return;
        int waited = Environment.TickCount - _shownTick;
        if (waited < ms) Thread.Sleep(ms - waited);
    }

    /// <summary>Close the splash (called once the editor window is up). Safe to call if it never showed.</summary>
    public static void Close()
    {
        try
        {
            var f = _form;
            if (f is not null && !f.IsDisposed)
                f.BeginInvoke(new Action(() => { try { f.Close(); } catch { } }));
        }
        catch { }
    }

    private static Form Build(string imgPath)
    {
        var src = Image.FromFile(imgPath);
        // Scale to a tidy splash size (max 820 wide) preserving aspect.
        int w = Math.Min(820, src.Width);
        int h = (int)(src.Height * (w / (float)src.Width));

        var form = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.CenterScreen,
            ShowInTaskbar = false,
            TopMost = true,
            Width = w,
            Height = h,
            BackColor = Color.Black,
        };
        try { var ico = Path.Combine(AppContext.BaseDirectory, "RefractorForge.ico"); if (File.Exists(ico)) form.Icon = new Icon(ico); } catch { }

        form.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.DrawImage(src, new Rectangle(0, 0, w, h));

            // Dark gradient band along the bottom so the title/credit read over any image.
            int band = Math.Max(96, h / 4);
            // Inset the brush rectangle by 1px top/bottom and set TileFlipXY so GDI+ doesn't leave its 1px hard-edge
            // artefact (a stray dark line) at the top of the gradient band.
            using (var grad = new LinearGradientBrush(new Rectangle(0, h - band - 1, w, band + 2),
                       Color.FromArgb(0, 0, 0, 0), Color.FromArgb(205, 0, 0, 0), LinearGradientMode.Vertical))
            {
                grad.WrapMode = WrapMode.TileFlipXY;
                g.FillRectangle(grad, new Rectangle(0, h - band, w, band));
            }

            // Title + credit (with a soft shadow), bottom-left.
            void Text(string s, Font font, Brush brush, float x, float y)
            {
                using var shadow = new SolidBrush(Color.FromArgb(190, 0, 0, 0));
                g.DrawString(s, font, shadow, x + 1.5f, y + 1.5f);
                g.DrawString(s, font, brush, x, y);
            }
            using var title = new Font("Segoe UI", 30f, FontStyle.Bold, GraphicsUnit.Point);
            using var credit = new Font("Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Point);
            float ty = h - band + band * 0.28f;
            Text("RefractorForge", title, Brushes.White, 22f, ty);
            Text("developed by LuccaWulf", credit, new SolidBrush(Color.FromArgb(220, 220, 230, 240)), 25f, ty + 48f);

            using var border = new Pen(Color.FromArgb(70, 255, 255, 255), 1f);
            g.DrawRectangle(border, 0, 0, w - 1, h - 1);
        };
        // Safety net: never let the splash linger if the editor fails to call Close() (e.g. a load error).
        var safety = new System.Windows.Forms.Timer { Interval = 12000 };
        safety.Tick += (_, _) => { safety.Stop(); try { form.Close(); } catch { } };
        safety.Start();
        form.FormClosed += (_, _) => { try { safety.Dispose(); } catch { } try { src.Dispose(); } catch { } };
        return form;
    }
}
