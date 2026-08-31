namespace RefractorForge.Formats.Con;

/// <summary>Which gameplay handles belong to which GAME MODE.
///
/// A level keeps a separate set of gameplay .con files per mode - <c>Conquest/</c>, <c>Ctf/</c>, <c>TDM/</c>,
/// <c>SinglePlayer/</c>, <c>Coop/</c> - and an object exists in a mode only if that mode's files name it. Battlecraft
/// exposes this as its "Show All / CQ / CTF / TDM" dropdown (guide figure 21), so a mapper can see where the flags
/// and spawns sit in each mode without opening the level five times.
///
/// This is read-only membership by NAME. The editor still loads and edits ONE mode's gameplay, exactly as before;
/// this only answers "does this named handle also appear in mode X", which is what a display filter needs. Keeping it
/// separate is deliberate: making the editable layer multi-mode would change saving, undo and collaboration, whereas
/// a filter that misreports membership would quietly hide objects a mapper is looking for.</summary>
public sealed class GameplayModes
{
    /// <summary>The modes found, in the order discovered (e.g. Conquest, Ctf, TDM). Empty for a level with none.</summary>
    public IReadOnlyList<string> Modes { get; }

    // mode -> the names it declares, per kind. Names are matched case-insensitively, as the .con files are.
    private readonly Dictionary<string, HashSet<string>> _cp, _veh, _sol;

    private GameplayModes(List<string> modes,
                          Dictionary<string, HashSet<string>> cp,
                          Dictionary<string, HashSet<string>> veh,
                          Dictionary<string, HashSet<string>> sol)
    { Modes = modes; _cp = cp; _veh = veh; _sol = sol; }

    public static GameplayModes Empty { get; } = new(new List<string>(),
        new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase));

    /// <summary>The three kinds of handle a mode's files can declare.</summary>
    public enum Kind { ControlPoint, Vehicle, Soldier }

    /// <summary>Does <paramref name="name"/> appear in <paramref name="mode"/>? An unknown mode or a level with no
    /// per-mode files answers TRUE - a filter that cannot tell must not hide anything.</summary>
    public bool InMode(Kind kind, string name, string mode)
    {
        if (string.IsNullOrEmpty(mode) || string.IsNullOrEmpty(name)) return true;
        var map = kind switch { Kind.ControlPoint => _cp, Kind.Vehicle => _veh, _ => _sol };
        if (!map.TryGetValue(mode, out var names)) return true;
        return names.Contains(name);
    }

    /// <summary>The modes a named handle appears in - what the inspector shows for the selected object.</summary>
    public IEnumerable<string> ModesOf(Kind kind, string name)
    {
        var map = kind switch { Kind.ControlPoint => _cp, Kind.Vehicle => _veh, _ => _sol };
        foreach (var m in Modes)
            if (map.TryGetValue(m, out var names) && names.Contains(name)) yield return m;
    }

    /// <summary>Build from each mode's raw file contents. Pure, so it is unit-tested without a level on disk; the
    /// viewer feeds it either from a folder or straight out of the level's .rfa entries.</summary>
    public static GameplayModes Scan(IEnumerable<(string Mode, IEnumerable<string>? ControlPoints,
                                                  IEnumerable<string>? ObjectSpawns, IEnumerable<string>? SoldierSpawns)> perMode)
    {
        var modes = new List<string>();
        var cp = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var veh = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var sol = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (mode, c, v, s) in perMode)
        {
            if (string.IsNullOrWhiteSpace(mode)) continue;
            if (c is null && v is null && s is null) continue;      // not a game-mode folder at all
            if (!modes.Contains(mode, StringComparer.OrdinalIgnoreCase)) modes.Add(mode);
            if (c is not null) cp[mode] = GameplayObjects.ParseObjectNames(c);
            if (v is not null) veh[mode] = GameplayObjects.ParseObjectNames(v);
            if (s is not null) sol[mode] = GameplayObjects.ParseObjectNames(s);
        }
        return new GameplayModes(modes, cp, veh, sol);
    }

    /// <summary>Scan a level FOLDER: every sub-folder that carries gameplay files is a mode, named by the folder.</summary>
    public static GameplayModes FromFolder(string levelDir)
    {
        if (!Directory.Exists(levelDir)) return Empty;
        var per = new List<(string, IEnumerable<string>?, IEnumerable<string>?, IEnumerable<string>?)>();
        foreach (var dir in LevelSaver.GameModeDirs(levelDir))
        {
            IEnumerable<string>? Read(string n)
            {
                var p = Path.Combine(dir, n);
                try { return File.Exists(p) ? File.ReadAllLines(p) : null; } catch { return null; }
            }
            per.Add((new DirectoryInfo(dir).Name, Read("ControlPoints.con"), Read("ObjectSpawns.con"), Read("SoldierSpawns.con")));
        }
        return Scan(per);
    }
}
