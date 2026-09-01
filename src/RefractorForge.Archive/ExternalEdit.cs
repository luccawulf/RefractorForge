using System.Text.Json;

namespace RefractorForge.Archive;

/// <summary>
/// "Open this entry in a real editor and pick the changes back up" — BGA's most useful feature, and the reason
/// modders put up with it: you cannot sensibly edit a .con by extracting it, finding it, editing it, and
/// importing it again every time.
///
/// The entry is written to a temp file, the chosen program is launched on it, and a watcher waits for that file
/// to change. The change is staged as a normal pending edit, so it still goes through Save and still gets
/// verified — editing externally is not a way around any of that.
/// </summary>
public sealed class ExternalEdit : IDisposable
{
    public sealed class Session : IDisposable
    {
        public required ArchiveModel.Item Item { get; init; }
        public required string TempPath { get; init; }
        public required FileSystemWatcher Watcher { get; init; }
        public DateTime LastSeen { get; set; }

        public void Dispose()
        {
            try { Watcher.EnableRaisingEvents = false; Watcher.Dispose(); } catch { }
            try { if (File.Exists(TempPath)) File.Delete(TempPath); } catch { }
        }
    }

    private readonly Dictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _dir;

    /// <summary>Raised on the UI thread when an externally-edited file has come back with new content.</summary>
    public event Action<ArchiveModel.Item, byte[]>? Changed;

    /// <summary>Marshals watcher callbacks onto the UI thread.</summary>
    public required Control Sync { private get; init; }

    public ExternalEdit()
    {
        _dir = Path.Combine(Path.GetTempPath(), "RefractorForgeArchive");
        Directory.CreateDirectory(_dir);
    }

    /// <summary>
    /// Write the entry out and open it. <paramref name="program"/> null means "whatever the OS associates".
    /// </summary>
    public void Open(ArchiveModel.Item item, byte[] data, string? program)
    {
        // Keep the real file name so the editor gets its syntax highlighting and the title bar is meaningful.
        // A per-entry subfolder keeps two files of the same name from colliding.
        string sub = Path.Combine(_dir, Math.Abs(item.Name.GetHashCode()).ToString("x8"));
        Directory.CreateDirectory(sub);
        string temp = Path.Combine(sub, item.FileName);
        File.WriteAllBytes(temp, data);

        if (_sessions.TryGetValue(item.Name, out var old)) { old.Dispose(); _sessions.Remove(item.Name); }

        var watcher = new FileSystemWatcher(sub, item.FileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        var session = new Session
        {
            Item = item, TempPath = temp, Watcher = watcher, LastSeen = File.GetLastWriteTimeUtc(temp),
        };
        watcher.Changed += (_, _) => OnChanged(session);
        _sessions[item.Name] = session;

        try
        {
            var psi = program is null
                ? new System.Diagnostics.ProcessStartInfo(temp) { UseShellExecute = true }
                : new System.Diagnostics.ProcessStartInfo(program, $"\"{temp}\"") { UseShellExecute = true };
            System.Diagnostics.Process.Start(psi);
        }
        catch
        {
            session.Dispose();
            _sessions.Remove(item.Name);
            throw;
        }
    }

    private void OnChanged(Session s)
    {
        // Editors commonly write in several steps (truncate, then write, or save-to-temp then rename), so a
        // single change event can arrive while the file is still locked or half-written. Settle briefly, then
        // read, and give up quietly rather than importing a torn file.
        byte[]? data = null;
        for (int attempt = 0; attempt < 12 && data is null; attempt++)
        {
            Thread.Sleep(60);
            try
            {
                var stamp = File.GetLastWriteTimeUtc(s.TempPath);
                if (stamp == s.LastSeen) continue;
                using var fs = new FileStream(s.TempPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var buf = new byte[fs.Length];
                fs.ReadExactly(buf);
                s.LastSeen = stamp;
                data = buf;
            }
            catch { /* still being written; try again */ }
        }
        if (data is null) return;

        try { Sync.BeginInvoke(() => Changed?.Invoke(s.Item, data)); } catch { }
    }

    public bool IsEditing(string entryName) => _sessions.ContainsKey(entryName);

    public void Dispose()
    {
        foreach (var s in _sessions.Values) s.Dispose();
        _sessions.Clear();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }
}

/// <summary>
/// Window settings that should outlive a run: the recent-archive list and the per-extension editor choices.
/// Kept beside the editor's own settings under %APPDATA%\RefractorForge.
/// </summary>
public sealed class AppSettings
{
    public List<string> Recent { get; set; } = new();

    /// <summary>Extension (with dot, lowercase) -> program to open it with. Empty means "ask the OS".</summary>
    public Dictionary<string, string> Editors { get; set; } = new();

    public int IconSize { get; set; } = 24;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RefractorForge", "archive-tool.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* a corrupt settings file is not worth refusing to start over */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* settings are a convenience; never let them break the app */ }
    }

    public void AddRecent(string path)
    {
        Recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        Recent.Insert(0, path);
        if (Recent.Count > 12) Recent.RemoveRange(12, Recent.Count - 12);
        Save();
    }
}
