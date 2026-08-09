using System.Drawing;
using System.Windows.Forms;
using RefractorForge.Formats;

namespace RefractorForge.Viewer;

/// <summary>The mod a project targets: the game install + mod folder name, and whether inherited dependencies
/// are mounted.</summary>
public sealed record ModTarget(string GameRoot, string Mod, bool IncludeInherited);

/// <summary>
/// "Which mod is this map for?" — pick the game install and the target mod, and SEE the resolved mount chain
/// before committing (e.g. <c>FHSW -> FH -> bf1942</c>, with how many object/texture archives that mounts and
/// any dependency that is named but not installed). This is what makes authoring a map for a mini-mod work:
/// the project records the mod, and every load resolves that mod's whole dependency stack.
/// </summary>
internal static class ModPickerDialog
{
    public static ModTarget? Show(string? initialGameRoot, string? initialMod, bool initialInherit = true)
    {
        ModTarget? result = null;
        var t = new Thread(() =>
        {
            using var f = new Form
            {
                Text = Loc.T("Target mod (dependencies)"), FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterScreen, MaximizeBox = false, MinimizeBox = false,
                ClientSize = new Size(560, 430), BackColor = Color.FromArgb(32, 34, 38), ForeColor = Color.Gainsboro,
            };

            var lblRoot = new Label { Text = Loc.T("Game install"), Left = 14, Top = 16, Width = 90, Height = 24, TextAlign = ContentAlignment.MiddleLeft };
            var root = new TextBox { Left = 108, Top = 14, Width = 348, Text = initialGameRoot ?? "" };
            var browse = new Button { Text = Loc.T("Browse..."), Left = 462, Top = 13, Width = 84, FlatStyle = FlatStyle.Flat };

            var lblMod = new Label { Text = Loc.T("Mod"), Left = 14, Top = 52, Width = 90, Height = 24, TextAlign = ContentAlignment.MiddleLeft };
            var mods = new ListBox { Left = 108, Top = 50, Width = 438, Height = 190, BackColor = Color.FromArgb(28, 30, 34), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };

            var inherit = new CheckBox { Text = Loc.T("Also mount dependencies the mod's init.con doesn't list (recommended)"), Left = 108, Top = 246, Width = 438, Height = 22, Checked = initialInherit, ForeColor = Color.Gainsboro };

            var lblChain = new Label { Text = Loc.T("Mount chain"), Left = 14, Top = 274, Width = 90, Height = 24, TextAlign = ContentAlignment.MiddleLeft };
            var chain = new TextBox { Left = 108, Top = 272, Width = 438, Height = 96, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(24, 26, 30), ForeColor = Color.FromArgb(150, 200, 150), BorderStyle = BorderStyle.FixedSingle };

            var ok = new Button { Text = Loc.T("Use this mod"), Left = 372, Top = 384, Width = 104, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 114, 178), ForeColor = Color.White, DialogResult = DialogResult.OK };
            var skip = new Button { Text = Loc.T("Skip"), Left = 482, Top = 384, Width = 64, FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.Cancel };

            void FillMods()
            {
                mods.Items.Clear();
                var dir = Path.Combine(root.Text.Trim(), "Mods");
                if (!Directory.Exists(dir)) { chain.Text = Loc.T("No Mods\\ folder under that path."); return; }
                foreach (var d in Directory.EnumerateDirectories(dir).OrderBy(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase))
                    mods.Items.Add(Path.GetFileName(d));
                if (initialMod is { Length: > 0 })
                {
                    int i = mods.Items.IndexOf(initialMod);
                    if (i < 0) for (int k = 0; k < mods.Items.Count; k++) if (string.Equals((string)mods.Items[k]!, initialMod, StringComparison.OrdinalIgnoreCase)) { i = k; break; }
                    if (i >= 0) mods.SelectedIndex = i;
                }
            }

            void Preview()
            {
                if (mods.SelectedItem is not string mod || !Directory.Exists(root.Text.Trim())) { chain.Text = ""; return; }
                try
                {
                    var r = ModChain.ResolveByName(root.Text.Trim(), mod, inherit.Checked);
                    var (me, te) = ModChain.CollectArchives(r);
                    var lines = new List<string> { r.Describe(), "", $"{r.Mounts.Count} mod(s) mounted -> {me.Length} object + {te.Length} texture archive(s)" };
                    if (r.Mounts.Any(m => !m.Listed))
                        lines.Add("(+ marks a dependency inherited from another mod's init.con)");
                    if (r.Missing.Count > 0)
                        lines.Add($"WARNING: not installed -> {string.Join(", ", r.Missing)}");
                    chain.Text = string.Join(Environment.NewLine, lines);
                }
                catch (Exception ex) { chain.Text = Loc.T("Could not resolve: ") + ex.Message; }
            }

            browse.Click += (_, _) =>
            {
                using var d = new FolderBrowserDialog { Description = Loc.T("Select the game install folder (the one containing Mods\\)"), UseDescriptionForTitle = true };
                if (Directory.Exists(root.Text.Trim())) d.SelectedPath = root.Text.Trim();
                if (d.ShowDialog() == DialogResult.OK) { root.Text = d.SelectedPath; FillMods(); Preview(); }
            };
            mods.SelectedIndexChanged += (_, _) => Preview();
            inherit.CheckedChanged += (_, _) => Preview();
            root.Leave += (_, _) => { FillMods(); Preview(); };

            f.Controls.AddRange(new Control[] { lblRoot, root, browse, lblMod, mods, inherit, lblChain, chain, ok, skip });
            f.AcceptButton = ok; f.CancelButton = skip;
            FillMods(); Preview();

            if (f.ShowDialog() == DialogResult.OK && mods.SelectedItem is string chosen && Directory.Exists(root.Text.Trim()))
                result = new ModTarget(root.Text.Trim(), chosen, inherit.Checked);
        }) { Name = "modpicker" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return result;
    }
}
