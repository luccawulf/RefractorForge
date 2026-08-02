using System.Text.Json;

namespace RefractorForge.Viewer;

/// <summary>
/// Editor preferences that change HOW A LEVEL IS ASSEMBLED, kept in <c>%APPDATA%\RefractorForge\prefs.json</c>.
///
/// Both options below are conveniences that pull in content the opened <c>.rfa</c> does not itself contain. They are
/// usually what you want — they are how the game assembles the same map — but when you are authoring, seeing the
/// borrowed content mixed with your own can be confusing, so each can be switched off.
/// </summary>
public static class AppPrefs
{
    /// <summary>Follow each dependency's own <c>init.con</c> so a mod inherits mounts its author didn't list
    /// (a mini-mod naming FHSW also gets FH). Off = mount only what the mod's own init.con names, exactly like the
    /// game does.</summary>
    public static bool ResolveInheritedMods { get; set; } = true;

    /// <summary>When the opened <c>.rfa</c> has NO terrain of its own (an add-on/patch map such as FHSWEurope's
    /// <c>coral_sea.rfa</c>, which ships only ObjectiveMode configs plus custom ships), look through the mod chain
    /// for the same-named base map and layer it underneath so the map has ground and its original objects.
    /// Off = show only what the opened archive actually contains.</summary>
    public static bool LayerBaseMap { get; set; } = true;

    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RefractorForge");
    private static string FilePath => Path.Combine(Dir, "prefs.json");

    private sealed record Data(bool? ResolveInheritedMods, bool? LayerBaseMap);

    /// <summary>Load persisted preferences. Call once at startup, BEFORE the level load block reads them.</summary>
    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            if (JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) is not { } d) return;
            if (d.ResolveInheritedMods is bool a) ResolveInheritedMods = a;
            if (d.LayerBaseMap is bool b) LayerBaseMap = b;
        }
        catch { /* a corrupt prefs file must never stop the editor starting */ }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Data(ResolveInheritedMods, LayerBaseMap),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
