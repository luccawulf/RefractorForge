using System.Drawing;
using System.Windows.Forms;

namespace RefractorForge.Viewer;

/// <summary>
/// First-run language picker. Shown once, before anything else draws, when no language has ever been chosen.
///
/// It is deliberately BILINGUAL and label-free: at this point we do not know which language the user reads, so
/// asking the question in English only would be exactly the barrier the Japanese translation exists to remove.
/// Each option is written in its own language, the way an installer does it.
///
/// This is WinForms rather than ImGui because it has to run before the GL window and the ImGui font atlas exist -
/// the atlas is built once from a CJK font only when a non-English language is active, so the choice must be made
/// first. Same reason the language switch later in the session restarts the editor.
/// </summary>
internal static class LanguagePrompt
{
    /// <summary>Ask which language to use. Returns the chosen code ("en" / "ja"); defaults to English if the
    /// dialog is closed without a choice.</summary>
    public static string Ask()
    {
        string chosen = "en";

        using var form = new Form
        {
            Text = "RefractorForge",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MaximizeBox = false,
            MinimizeBox = false,
            ClientSize = new Size(430, 210),
            BackColor = Color.FromArgb(32, 34, 38),
            ForeColor = Color.Gainsboro,
            TopMost = true,
        };

        form.Controls.Add(new Label
        {
            Text = "Choose your language",
            Left = 24, Top = 22, Width = 382, Height = 26,
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            ForeColor = Color.White,
            TextAlign = ContentAlignment.MiddleCenter,
        });
        form.Controls.Add(new Label
        {
            Text = "言語を選択してください",
            Left = 24, Top = 48, Width = 382, Height = 26,
            Font = new Font("Yu Gothic UI", 11f),
            ForeColor = Color.FromArgb(190, 195, 205),
            TextAlign = ContentAlignment.MiddleCenter,
        });

        Button Choice(string text, string code, Color colour, int left, Font font)
        {
            var b = new Button
            {
                Text = text,
                Left = left, Top = 96, Width = 178, Height = 52,
                FlatStyle = FlatStyle.Flat,
                BackColor = colour,
                ForeColor = Color.White,
                Font = font,
                Cursor = Cursors.Hand,
            };
            b.FlatAppearance.BorderSize = 0;
            b.Click += (_, _) => { chosen = code; form.DialogResult = DialogResult.OK; form.Close(); };
            return b;
        }

        // Okabe-Ito blue / vermillion: distinguishable under every common form of colour blindness.
        form.Controls.Add(Choice("English", "en", Color.FromArgb(0, 114, 178), 24, new Font("Segoe UI", 12f, FontStyle.Bold)));
        form.Controls.Add(Choice("日本語", "ja", Color.FromArgb(213, 94, 0), 228, new Font("Yu Gothic UI", 12f, FontStyle.Bold)));

        form.Controls.Add(new Label
        {
            Text = "You can change this later in View ▸ Language.\nこの設定は後から View ▸ Language で変更できます。",
            Left = 24, Top = 158, Width = 382, Height = 40,
            Font = new Font("Yu Gothic UI", 8.5f),
            ForeColor = Color.FromArgb(150, 155, 165),
            TextAlign = ContentAlignment.MiddleCenter,
        });

        try { form.ShowDialog(); } catch { /* never block startup on a dialog failure */ }
        return chosen;
    }
}
