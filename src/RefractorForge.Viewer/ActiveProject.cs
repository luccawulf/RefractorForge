namespace RefractorForge.Viewer;

/// <summary>The `.rfproj` the editor should load on next launch. Persisted at
/// <c>%APPDATA%\RefractorForge\active.txt</c>. Opening/creating a project sets it; switching projects while running
/// sets it then relaunches; "Close Project" clears it so the next launch shows the startup screen.</summary>
internal static class ActiveProject
{
    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RefractorForge");
    private static string FilePath => Path.Combine(Dir, "active.txt");

    public static string? Get()
    {
        try { return File.Exists(FilePath) ? File.ReadAllText(FilePath).Trim() : null; }
        catch { return null; }
    }

    public static void Set(string rfprojPath)
    {
        try { Directory.CreateDirectory(Dir); File.WriteAllText(FilePath, rfprojPath); } catch { }
    }

    public static void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
    }
}
