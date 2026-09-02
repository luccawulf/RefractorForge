using System.Text;
using System.Text.RegularExpressions;

namespace RefractorForge.Archive;

/// <summary>
/// Find files by name, or by what they contain, across everything that is open - one archive or a whole mod.
/// A name search is what BGA's filter box does; a content search is what it cannot do, and is how you answer
/// "which .con mentions this template" or "where is that texture referenced" without extracting a thing.
/// </summary>
public sealed class SearchForm : Form
{
    private readonly ArchiveModel _model;
    private readonly Action<string> _goTo;
    private readonly TextBox _name = new() { PlaceholderText = "file name  (wildcards: *.con  tank*  *Wake*)" };
    private readonly TextBox _content = new() { PlaceholderText = "text inside the file  (leave empty to search names only)" };
    private readonly CheckBox _regex = new() { Text = "Regular expression", AutoSize = true };
    private readonly CheckBox _case = new() { Text = "Match case", AutoSize = true };
    private readonly ListView _results = new();
    private readonly Label _summary = new() { AutoSize = false, Height = Theme.Dp(24), TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button _go = new() { Text = "Search" };
    private CancellationTokenSource? _cts;

    private static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
        { ".con", ".rs", ".ssc", ".inc", ".txt", ".lst", ".fnt", ".wst", ".sst", ".ini", ".xml", ".html", ".cfg" };

    public SearchForm(ArchiveModel model, Action<string> goTo)
    {
        _model = model; _goTo = goTo;
        Text = "Search";
        StartPosition = FormStartPosition.CenterParent;
        Size = Theme.Dp(this, 860, 560);
        MinimumSize = Theme.Dp(this, 560, 360);
        ShowInTaskbar = false;

        var top = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, RowCount = 3, AutoSize = true, Padding = new Padding(Theme.Dp(10), Theme.Dp(10), Theme.Dp(10), Theme.Dp(4)) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _name.Dock = DockStyle.Fill; _content.Dock = DockStyle.Fill;
        _go.Width = Theme.Dp(110); _go.Height = Theme.Dp(28); _go.Dock = DockStyle.Right;
        var opts = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Dock = DockStyle.Fill };
        opts.Controls.Add(_regex); opts.Controls.Add(_case);
        top.Controls.Add(_name, 0, 0); top.Controls.Add(_go, 1, 0);
        top.Controls.Add(_content, 0, 1);
        top.Controls.Add(opts, 0, 2);
        top.SetRowSpan(_go, 2);

        _results.Dock = DockStyle.Fill; _results.View = View.Details; _results.FullRowSelect = true;
        _results.BorderStyle = BorderStyle.None; _results.HideSelection = false;
        _results.Columns.Add("File", Theme.Dp(420)); _results.Columns.Add("Match", Theme.Dp(300)); _results.Columns.Add("Archive", Theme.Dp(110));
        Ui.DarkHeader(_results);
        _results.Resize += (_, _) =>
        {
            int w = _results.ClientSize.Width - _results.Columns[0].Width - _results.Columns[2].Width - Theme.Dp(4);
            if (w > Theme.Dp(120)) _results.Columns[1].Width = w;
        };
        _results.DoubleClick += (_, _) => { if (_results.SelectedItems.Count > 0) _goTo(_results.SelectedItems[0].Text); };
        _results.KeyDown += (_, e) => { if (e.KeyCode == Keys.Return && _results.SelectedItems.Count > 0) _goTo(_results.SelectedItems[0].Text); };

        _summary.Dock = DockStyle.Bottom; _summary.Padding = new Padding(Theme.Dp(10), 0, 0, 0);
        Controls.Add(_results); Controls.Add(_summary); Controls.Add(top);
        _results.BringToFront();

        _go.Click += (_, _) => Run();
        _name.KeyDown += (_, e) => { if (e.KeyCode == Keys.Return) { Run(); e.Handled = true; } };
        _content.KeyDown += (_, e) => { if (e.KeyCode == Keys.Return) { Run(); e.Handled = true; } };
        AcceptButton = _go;
        Theme.Apply(this);
        Theme.StyleButton(_go, primary: true);
        _summary.ForeColor = Theme.TextDim;
    }

    /// <summary>Seed the boxes and run - for "search for this template" from a script preview.</summary>
    public void Prefill(string? namePattern, string? content)
    {
        if (namePattern is not null) _name.Text = namePattern;
        if (content is not null) _content.Text = content;
        Run();
    }

    private static Regex Wildcard(string pattern, bool matchCase)
    {
        var rx = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return new Regex(rx, matchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
    }

    private async void Run()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _results.Items.Clear();
        _go.Enabled = false;
        string namePat = _name.Text.Trim(), content = _content.Text;
        bool useRegex = _regex.Checked, matchCase = _case.Checked;
        var items = _model.Items.Where(i => i.State != ArchiveModel.EntryState.Deleted && !_model.HiddenLayers.Contains(i.LayerIndex)).ToList();
        var opt = matchCase ? RegexOptions.None : RegexOptions.IgnoreCase;

        Regex? nameRx = null;
        try
        {
            if (namePat.Length > 0)
                nameRx = useRegex ? new Regex(namePat, opt) : (namePat.Contains('*') || namePat.Contains('?') ? Wildcard(namePat, matchCase) : null);
        }
        catch (Exception ex) { _summary.Text = "Bad pattern: " + ex.Message; _go.Enabled = true; return; }
        Regex? contentRx = null;
        try { if (content.Length > 0 && useRegex) contentRx = new Regex(content, opt); }
        catch (Exception ex) { _summary.Text = "Bad pattern: " + ex.Message; _go.Enabled = true; return; }

        var found = new List<(string Name, string Match, string Src)>();
        int scanned = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await Task.Run(() =>
        {
            foreach (var it in items)
            {
                if (cts.IsCancellationRequested) return;
                bool nameOk = nameRx is null
                    ? (namePat.Length == 0 || it.Name.Contains(namePat, matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                    : nameRx.IsMatch(it.FileName) || nameRx.IsMatch(it.Name);
                if (!nameOk) continue;

                if (content.Length == 0) { found.Add((it.Name, "", it.Source ?? "")); continue; }
                if (!TextExt.Contains(Path.GetExtension(it.Name))) continue;
                scanned++;
                string text;
                try { text = Encoding.Latin1.GetString(_model.Read(it)); } catch { continue; }
                int hit = -1; string line = "";
                if (contentRx is not null)
                {
                    var m = contentRx.Match(text);
                    if (m.Success) hit = m.Index;
                }
                else hit = text.IndexOf(content, matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
                if (hit < 0) continue;
                int ls = text.LastIndexOf('\n', Math.Max(0, hit - 1)) + 1;
                int le = text.IndexOf('\n', hit); if (le < 0) le = text.Length;
                line = text[ls..le].Trim();
                int lineNo = 1; for (int i = 0; i < ls; i++) if (text[i] == '\n') lineNo++;
                found.Add((it.Name, $"{lineNo}: {line}", it.Source ?? ""));
            }
        });
        if (cts.IsCancellationRequested) return;

        _results.BeginUpdate();
        foreach (var (n, m, s) in found.Take(5000))
            _results.Items.Add(new ListViewItem(new[] { n, m, s }));
        _results.EndUpdate();
        _summary.Text = $"{found.Count:N0} match(es)" + (content.Length > 0 ? $" in {scanned:N0} text file(s)" : "") +
                        $"  -  {sw.Elapsed.TotalSeconds:0.0} s" + (found.Count > 5000 ? "  (first 5,000 shown)" : "") +
                        "  -  double-click to jump";
        _go.Enabled = true;
    }

    protected override void OnFormClosing(FormClosingEventArgs e) { _cts?.Cancel(); base.OnFormClosing(e); }
}
