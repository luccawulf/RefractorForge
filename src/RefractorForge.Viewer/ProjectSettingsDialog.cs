using System.Drawing;
using System.Windows.Forms;
using RefractorForge.Formats;

namespace RefractorForge.Viewer;

/// <summary>Edit the current project's manifest fields (name / game / mod / patch number / mode / game install /
/// test dir). Returns true if the user saved (the <see cref="RfProject"/> is mutated in place). Full archive-list
/// editing is a later addition; this covers the scalars Test/Export need (patch number, game test dir).</summary>
internal static class ProjectSettingsDialog
{
    public static bool Show(RfProject p)
    {
        bool ok = false;
        var t = new Thread(() =>
        {
            using var f = new Form
            {
                Text = "Project Settings", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterScreen,
                MaximizeBox = false, MinimizeBox = false, ClientSize = new Size(440, 320),
                BackColor = Color.FromArgb(32, 34, 38), ForeColor = Color.Gainsboro,
            };
            const int dy = 34; int y0 = 16;
            Label L(string s, int row) => new() { Text = s, Left = 16, Top = y0 + row * dy, Width = 110, Height = 24, TextAlign = ContentAlignment.MiddleLeft };
            TextBox T(string val, int row) => new() { Left = 132, Top = y0 + row * dy, Width = 288, Text = val };
            var name = T(p.Name, 0);
            var game = new ComboBox { Left = 132, Top = y0 + dy, Width = 288, DropDownStyle = ComboBoxStyle.DropDownList };
            game.Items.AddRange(new object[] { "BF1942", "BFVietnam" }); game.SelectedItem = p.Game.Equals("BFVietnam", StringComparison.OrdinalIgnoreCase) ? "BFVietnam" : "BF1942";
            var mod = T(p.Mod, 2);
            var patch = T(p.PatchNumber ?? "", 3);
            var mode = new ComboBox { Left = 132, Top = y0 + dy * 4, Width = 288, DropDownStyle = ComboBoxStyle.DropDownList };
            mode.Items.AddRange(new object[] { "Default", "Custom" }); mode.SelectedItem = p.Mode == RfMode.Custom ? "Custom" : "Default";
            var gameRoot = T(p.GameRoot ?? "", 5);
            var testDir = T(p.GameTestDir ?? "", 6);
            var save = new Button { Text = "Save", Left = 256, Top = 274, Width = 76, DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 114, 178), ForeColor = Color.White };
            var cancel = new Button { Text = "Cancel", Left = 340, Top = 274, Width = 76, DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat };
            f.Controls.AddRange(new Control[] { L("Name", 0), name, L("Game", 1), game, L("Mod", 2), mod, L("Patch number", 3), patch, L("Mode", 4), mode, L("Game install", 5), gameRoot, L("Game test dir", 6), testDir, save, cancel });
            f.AcceptButton = save; f.CancelButton = cancel;
            if (f.ShowDialog() == DialogResult.OK)
            {
                if (name.Text.Trim().Length > 0) p.Name = name.Text.Trim();
                p.Game = (string)game.SelectedItem!;
                if (mod.Text.Trim().Length > 0) p.Mod = mod.Text.Trim();
                p.PatchNumber = patch.Text.Trim().Length > 0 ? patch.Text.Trim() : null;
                p.Mode = (string)mode.SelectedItem! == "Custom" ? RfMode.Custom : RfMode.Default;
                p.GameRoot = gameRoot.Text.Trim().Length > 0 ? gameRoot.Text.Trim() : null;
                p.GameTestDir = testDir.Text.Trim().Length > 0 ? testDir.Text.Trim() : null;
                ok = true;
            }
        }) { Name = "projsettings" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return ok;
    }
}
