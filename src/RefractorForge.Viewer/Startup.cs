using System.Text.Json;
using System.Windows.Forms;

namespace RefractorForge.Viewer;

/// <summary>The level folder + mesh/texture archives the user last opened (persisted next to the exe).
/// StdMesh/Objects are legacy single-archive fields (still read for back-compat); MeshArchives / LevelArchives
/// are the unlimited lists (base + any patch .rfa).</summary>
public record LevelPaths(string? Level, string? StdMesh, string? Objects, string[]? Textures = null,
                         string[]? MeshArchives = null, string[]? LevelArchives = null);

/// <summary>
/// Native Windows folder/file pickers. WinForms dialogs are modal and run on a dedicated STA thread,
/// so they work from the ordinary (MTA) program thread without an [STAThread] Main or a message loop.
/// On .NET these use the modern Vista-style dialogs by default.
/// </summary>
public static class Picker
{
    public static string? Folder(string title, string? startAt)
        => RunSta(() =>
        {
            using var d = new FolderBrowserDialog
            {
                Description = Loc.T(title),
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false,
            };
            if (!string.IsNullOrEmpty(startAt) && Directory.Exists(startAt)) d.SelectedPath = startAt;
            return d.ShowDialog() == DialogResult.OK ? d.SelectedPath : null;
        });

    public static string? File(string title, string filter, string? startNear)
        => RunSta(() =>
        {
            using var d = new OpenFileDialog { Title = Loc.T(title), Filter = filter, CheckFileExists = true };
            if (!string.IsNullOrEmpty(startNear))
            {
                var dir = Directory.Exists(startNear) ? startNear : Path.GetDirectoryName(startNear);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) d.InitialDirectory = dir;
            }
            return d.ShowDialog() == DialogResult.OK ? d.FileName : null;
        });

    /// <summary>Multi-select file picker (Ctrl/Shift-click). Returns the chosen paths, or empty if cancelled.</summary>
    public static string[] Files(string title, string filter, string? startNear)
    {
        string[] result = Array.Empty<string>();
        var t = new Thread(() =>
        {
            using var d = new OpenFileDialog { Title = Loc.T(title), Filter = filter, CheckFileExists = true, Multiselect = true };
            if (!string.IsNullOrEmpty(startNear))
            {
                var dir = Directory.Exists(startNear) ? startNear : Path.GetDirectoryName(startNear);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) d.InitialDirectory = dir;
            }
            if (d.ShowDialog() == DialogResult.OK) result = d.FileNames;
        });
        t.IsBackground = true;   // never let a dialog thread keep the editor resident
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return result;
    }

    /// <summary>Native save-as picker. Returns the chosen path (overwrite already confirmed), or null if cancelled.</summary>
    public static string? Save(string title, string filter, string? defaultName, string? startNear)
        => RunSta(() =>
        {
            using var d = new SaveFileDialog { Title = title, Filter = filter, OverwritePrompt = true, AddExtension = true };
            if (!string.IsNullOrEmpty(defaultName)) d.FileName = defaultName;
            if (!string.IsNullOrEmpty(startNear))
            {
                var dir = Directory.Exists(startNear) ? startNear : Path.GetDirectoryName(startNear);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) d.InitialDirectory = dir;
            }
            return d.ShowDialog() == DialogResult.OK ? d.FileName : null;
        });

    /// <summary>Modal warning dialog on its own STA thread (safe to call from the MTA program thread).
    /// Used to report a failed level load instead of letting the app hard-crash on startup.</summary>
    public static void Error(string message, string title = "RefractorForge")
    {
        var t = new Thread(() => MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning));
        t.IsBackground = true;   // never let a dialog thread keep the editor resident
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
    }

    /// <summary>Modal yes/no on its own STA thread, defaulting to NO. For the choices that can lose files.</summary>
    public static bool Confirm(string message, string title = "RefractorForge")
    {
        bool yes = false;
        var t = new Thread(() => yes = MessageBox.Show(message, title, MessageBoxButtons.YesNo,
                                                      MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes);
        t.IsBackground = true;   // never let a dialog thread keep the editor resident
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return yes;
    }

    private static string? RunSta(Func<string?> show)
    {
        string? result = null;
        var t = new Thread(() => result = show());
        t.IsBackground = true;   // never let a dialog thread keep the editor resident
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return result;
    }
}

/// <summary>Remembers the last-opened paths in refractorforge.json beside the executable.</summary>
public static class Settings
{
    private static string FilePath => Path.Combine(AppContext.BaseDirectory, "refractorforge.json");

    public static LevelPaths? Load()
    {
        try
        {
            return System.IO.File.Exists(FilePath)
                ? JsonSerializer.Deserialize<LevelPaths>(System.IO.File.ReadAllText(FilePath))
                : null;
        }
        catch { return null; }
    }

    public static void Save(LevelPaths paths)
    {
        try { System.IO.File.WriteAllText(FilePath, JsonSerializer.Serialize(paths, new JsonSerializerOptions { WriteIndented = true })); }
        catch { /* non-fatal: just means we re-ask next time */ }
    }
}
