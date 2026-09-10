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

    /// <summary>Battlecraft-style ground camera: WASD skims the map at a fixed height above the terrain instead of
    /// flying free. Off = the original fly camera. Remembered because it is a matter of taste, not of the level.</summary>
    public static bool GroundCamera { get; set; } = false;

    /// <summary>Largest object texture handed to the GPU, or 0 for the map's own resolution. Full is the default
    /// because a remastered map's art is the point of opening it in an editor - but object textures are by far the
    /// biggest thing the editor uploads, and on a GPU that shares system memory a couple of hundred 2048-4096
    /// textures can exhaust the driver mid-frame. Lower it when a map will not draw; the load log states the cost
    /// either way so the number is never a guess.</summary>
    public static int ObjectTextureCap { get; set; } = 0;

    /// <summary>The always-on relay this editor connects to: address, port, display name and join password.
    /// A central server is by definition the same one every time, so typing its address on every connect is
    /// pure friction - and a password nobody can remember is a password everybody works around.
    ///
    /// The password is stored in the clear alongside the rest. It guards a map-editing session on someone's
    /// hobby server, not an account, and the alternative in practice is that it gets pasted into a chat.
    /// It is not reused anywhere and the field says so.</summary>
    public static string CentralServerAddress { get; set; } = "";
    public static int CentralServerPort { get; set; } = 7777;
    public static string CentralServerPassword { get; set; } = "";
    public static string CentralServerName { get; set; } = "";

    /// <summary>Where a map downloaded from the relay is written. Empty means "beside the level archive that is
    /// already open", which is the mod's own Levels folder in the normal case and therefore the right answer
    /// without anybody configuring anything. It is a setting because the right answer is not always that: a
    /// second mod, a different drive, or simply somewhere the game is not looking yet.</summary>
    public static string MapDownloadDir { get; set; } = "";

    /// <summary>A map to rejoin on the next start. Switching level restarts the editor, and being dropped back
    /// at a disconnected desktop having to reconnect and pick the same map again is the friction that made
    /// downloading one feel like three separate chores. Set just before the relaunch, consumed and cleared on
    /// the way back up.</summary>
    public static string PendingRejoinMap { get; set; } = "";

    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RefractorForge");
    private static string FilePath => Path.Combine(Dir, "prefs.json");

    private sealed record Data(bool? ResolveInheritedMods, bool? LayerBaseMap, bool? GroundCamera = null,
                               int? ObjectTextureCap = null, string? CentralServerAddress = null,
                               int? CentralServerPort = null, string? CentralServerPassword = null,
                               string? CentralServerName = null, string? MapDownloadDir = null,
                               string? PendingRejoinMap = null);

    /// <summary>Load persisted preferences. Call once at startup, BEFORE the level load block reads them.</summary>
    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            if (JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) is not { } d) return;
            if (d.ResolveInheritedMods is bool a) ResolveInheritedMods = a;
            if (d.LayerBaseMap is bool b) LayerBaseMap = b;
            if (d.GroundCamera is bool c) GroundCamera = c;
            if (d.ObjectTextureCap is int t) ObjectTextureCap = t;
            if (d.CentralServerAddress is string ca) CentralServerAddress = ca;
            if (d.CentralServerPort is int cp && cp > 0 && cp <= 65535) CentralServerPort = cp;
            if (d.CentralServerPassword is string cw) CentralServerPassword = cw;
            if (d.CentralServerName is string cn) CentralServerName = cn;
            if (d.MapDownloadDir is string md) MapDownloadDir = md;
            if (d.PendingRejoinMap is string pr) PendingRejoinMap = pr;
        }
        catch { /* a corrupt prefs file must never stop the editor starting */ }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Data(ResolveInheritedMods, LayerBaseMap, GroundCamera, ObjectTextureCap,
                    CentralServerAddress, CentralServerPort, CentralServerPassword, CentralServerName, MapDownloadDir,
                    PendingRejoinMap),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
