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
                MaximizeBox = false, MinimizeBox = false, ClientSize = new Size(440, 410),
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
            // Target mod + dependency chain: the mod supplies this map's objects, and its init.con chain is
            // resolved transitively (FHSW -> FH -> bf1942), so a map for a mini-mod sees every inherited asset.
            var inherit = new CheckBox { Text = "Mount inherited mod dependencies", Left = 132, Top = y0 + dy * 7, Width = 288, Height = 24, Checked = p.IncludeInheritedMods, ForeColor = Color.Gainsboro };
            var pick = new Button { Text = "Choose target mod / show dependency chain...", Left = 132, Top = y0 + dy * 8, Width = 288, Height = 28, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 114, 178), ForeColor = Color.White };
            var chainLbl = new Label { Left = 16, Top = y0 + dy * 9 + 4, Width = 404, Height = 34, ForeColor = Color.FromArgb(150, 200, 150), Font = new Font("Segoe UI", 8.5f) };

            void ShowChain()
            {
                try
                {
                    if (Directory.Exists(gameRoot.Text.Trim()) && mod.Text.Trim().Length > 0)
                    {
                        var r = ModChain.ResolveByName(gameRoot.Text.Trim(), mod.Text.Trim(), inherit.Checked);
                        chainLbl.Text = r.Mounts.Count > 0 ? "Chain: " + r.Describe() : "Chain: (mod not found)";
                        chainLbl.ForeColor = r.Missing.Count > 0 ? Color.FromArgb(230, 170, 90) : Color.FromArgb(150, 200, 150);
                        if (r.Missing.Count > 0) chainLbl.Text += $"   [missing: {string.Join(", ", r.Missing)}]";
                    }
                    else chainLbl.Text = "Chain: set a game install + mod to resolve dependencies.";
                }
                catch { chainLbl.Text = ""; }
            }
            pick.Click += (_, _) =>
            {
                var t = ModPickerDialog.Show(gameRoot.Text.Trim(), mod.Text.Trim(), inherit.Checked);
                if (t is null) return;
                gameRoot.Text = t.GameRoot; mod.Text = t.Mod; inherit.Checked = t.IncludeInherited;
                if (mode.Items.Contains("Default") && p.MeshArchives.Count == 0) mode.SelectedItem = "Default";
                ShowChain();
            };
            inherit.CheckedChanged += (_, _) => ShowChain();
            mod.Leave += (_, _) => ShowChain();
            gameRoot.Leave += (_, _) => ShowChain();

            var save = new Button { Text = "Save", Left = 256, Top = 364, Width = 76, DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 114, 178), ForeColor = Color.White };
            var cancel = new Button { Text = "Cancel", Left = 340, Top = 364, Width = 76, DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat };
            f.Controls.AddRange(new Control[] { L("Name", 0), name, L("Game", 1), game, L("Mod", 2), mod, L("Patch number", 3), patch, L("Mode", 4), mode, L("Game install", 5), gameRoot, L("Game test dir", 6), testDir, inherit, pick, chainLbl, save, cancel });
            ShowChain();
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
                p.IncludeInheritedMods = inherit.Checked;
                ok = true;
            }
        }) { Name = "projsettings" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return ok;
    }
}
