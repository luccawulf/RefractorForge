using System.Drawing;
using System.Windows.Forms;

namespace RefractorForge.Viewer;

/// <summary>Result of the startup New Map dialog.</summary>
public sealed record NewMapSpec(string Name, string Game, int MaterialSize, int WorldSize);

/// <summary>A compact WinForms New Map dialog (name / game / size) for the startup flow. The rich terrain generator
/// (fractal types, heightmap import, …) stays available in-editor via the Terrain menu after the map opens.</summary>
internal static class NewMapDialog
{
    public static NewMapSpec? Show()
    {
        NewMapSpec? result = null;
        var t = new Thread(() =>
        {
            using var f = new Form
            {
                Text = Loc.T("New Map"), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterScreen,
                MaximizeBox = false, MinimizeBox = false, ClientSize = new Size(360, 200),
                BackColor = Color.FromArgb(32, 34, 38), ForeColor = Color.Gainsboro,
            };
            Label L(string s, int y) => new() { Text = s, Left = 16, Top = y, Width = 92, Height = 24, TextAlign = ContentAlignment.MiddleLeft };
            var name = new TextBox { Left = 116, Top = 16, Width = 224, Text = "MyMap" };
            var game = new ComboBox { Left = 116, Top = 52, Width = 224, DropDownStyle = ComboBoxStyle.DropDownList };
            game.Items.AddRange(new object[] { "BF1942", "BFVietnam" }); game.SelectedIndex = 0;
            var size = new ComboBox { Left = 116, Top = 88, Width = 224, DropDownStyle = ComboBoxStyle.DropDownList };
            size.Items.AddRange(new object[] { Loc.T("Small (1024 m)"), Loc.T("Medium (2048 m)"), Loc.T("Large (4096 m)") }); size.SelectedIndex = 1;
            var ok = new Button { Text = Loc.T("Create"), Left = 180, Top = 150, Width = 76, DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 158, 115), ForeColor = Color.White };
            var cancel = new Button { Text = Loc.T("Cancel"), Left = 264, Top = 150, Width = 76, DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat };
            f.Controls.AddRange(new Control[] { L(Loc.T("Name"), 18), name, L(Loc.T("Game"), 54), game, L(Loc.T("Size"), 90), size, ok, cancel });
            f.AcceptButton = ok; f.CancelButton = cancel;
            if (f.ShowDialog() == DialogResult.OK && name.Text.Trim().Length > 0)
            {
                var (ms, ws) = size.SelectedIndex switch { 0 => (256, 1024), 2 => (1024, 4096), _ => (512, 2048) };
                result = new NewMapSpec(name.Text.Trim(), (string)game.SelectedItem!, ms, ws);
            }
        }) { Name = "newmap" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return result;
    }
}
