using System.Text.Json;
using RefractorForge.Formats;

namespace RefractorForge.Viewer;

/// <summary>One entry in the Recent Projects list shown on the startup screen.</summary>
public sealed record RecentProject(string ProjectPath, string Name, string Game, string LastOpened);

/// <summary>The recent-projects list, persisted at <c>%APPDATA%\RefractorForge\recent.json</c> (most-recent first).
/// Read by <see cref="StartupWindow"/>; touched whenever a project is opened or saved.</summary>
public static class RecentProjects
{
    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RefractorForge");
    private static string FilePath => Path.Combine(Dir, "recent.json");

    public static List<RecentProject> Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<RecentProject>>(File.ReadAllText(FilePath)) ?? new()
                : new();
        }
        catch { return new(); }
    }

    /// <summary>Drop one project from the list. This forgets it, it does NOT touch the project on disk - removing a
    /// stale entry and deleting someone's level are very different acts and must not share a code path.</summary>
    public static void Forget(string projectPath)
    {
        try
        {
            var list = Load().Where(r => !string.Equals(r.ProjectPath, projectPath, StringComparison.OrdinalIgnoreCase)).ToList();
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>Record a project as most-recently-opened (de-duped by path, capped at 12, missing files dropped).</summary>
    public static void Touch(RfProject p)
    {
        try
        {
            var entry = new RecentProject(p.FilePath, p.Name, p.Game, DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            var list = Load().Where(r => !string.Equals(r.ProjectPath, p.FilePath, StringComparison.OrdinalIgnoreCase)).ToList();
            list.Insert(0, entry);
            list = list.Where(r => string.Equals(r.ProjectPath, entry.ProjectPath, StringComparison.OrdinalIgnoreCase) || File.Exists(r.ProjectPath)).Take(12).ToList();
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
