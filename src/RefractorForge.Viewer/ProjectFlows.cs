using System.Text.RegularExpressions;
using RefractorForge.Formats;
using RefractorForge.Formats.Terrain;

namespace RefractorForge.Viewer;

/// <summary>The startup screen's follow-up flows: turn the user's choice (open project / open RFA / open folder /
/// new map) into a saved, active, recent <see cref="RfProject"/> the load path can consume. Native (WinForms)
/// pickers + <see cref="LevelSaver.ExtractToFolder"/> + <see cref="LevelSaver.CreateNewLevel"/>. Runs BEFORE the GL
/// window (like the old first-run flow), so it's pure path/file work.</summary>
internal static class ProjectFlows
{
    /// <summary>What the startup screen resolved to: a project to load, a request to run the Open Mod flow (which
    /// is archive/Settings-based rather than a project, so it has no <see cref="RfProject"/>), or a cancel.</summary>
    internal sealed record StartupResult(RfProject? Project = null, bool OpenMod = false, bool Cancelled = false);

    /// <summary>Show the startup screen and run the chosen flow. The project flows are finalized here (saved + set
    /// active + added to recents); Open Mod is handed back to the caller because it loads a mod's archive chain in
    /// place instead of creating a project.</summary>
    public static StartupResult RunStartup()
    {
        var choice = StartupWindow.Show();
        return choice.Action switch
        {
            StartupAction.OpenProject => new StartupResult(OpenProjectFlow(choice.RecentPath)),
            StartupAction.OpenMod => new StartupResult(OpenMod: true),
            StartupAction.OpenRfa => new StartupResult(OpenRfaFlow()),
            StartupAction.OpenFolder => new StartupResult(OpenFolderFlow()),
            StartupAction.NewMap => new StartupResult(NewMapFlow()),
            _ => new StartupResult(Cancelled: true),
        };
    }

    // Per-flow public entries (used by the startup screen AND the in-editor File ▸ Project menu). Each runs its flow
    // then finalizes (save the .rfproj + set active + add to recents), so the caller just relaunches.
    public static RfProject? OpenProjectFlow(string? recentPath = null) => Finalize(OpenProject(recentPath));
    public static RfProject? OpenRfaFlow() => Finalize(OpenRfa());
    public static RfProject? OpenFolderFlow() => Finalize(OpenFolder());
    public static RfProject? NewMapFlow() => Finalize(NewMap());

    private static RfProject? Finalize(RfProject? proj)
    {
        if (proj is not null)
        {
            try { proj.Save(); } catch { }
            ActiveProject.Set(proj.FilePath);
            RecentProjects.Touch(proj);
        }
        return proj;
    }

    private static RfProject? OpenProject(string? recentPath)
    {
        var path = recentPath ?? Picker.File("Open Project (.rfproj)", "RefractorForge project (*.rfproj)|*.rfproj|All files (*.*)|*.*", null);
        if (path is null || !File.Exists(path)) return null;
        try { return RfProject.Load(path); }
        catch (Exception ex) { Picker.Error("Failed to open project:\n" + ex.Message); return null; }
    }

    private static RfProject? OpenRfa()
    {
        var rfas = Picker.Files("Select the map .rfa  (base + any patch, Ctrl/Shift-click)", "RFA archives (*.rfa)|*.rfa|All files (*.*)|*.*", null);
        if (rfas.Length == 0) return null;
        var (game, mod, gameRoot, mapName) = InferFromPath(rfas[0]);
        var dest = Picker.Folder($"Choose an EMPTY folder to extract '{mapName}' into (this becomes the project folder)", null);
        if (dest is null) return null;
        try { LevelSaver.ExtractToFolder(rfas, dest); }
        catch (Exception ex) { Picker.Error("Extract failed:\n" + ex.Message); return null; }

        var p = new RfProject { Name = mapName, Game = game, Mod = mod, GameRoot = gameRoot, Mode = RfMode.Default, ProjectFolder = dest };
        // The .rfa sat inside a game install, so its mod (and therefore its whole dependency chain) is known.
        // Otherwise ask which mod this map targets; only fall back to the last-used archives if the user skips.
        if (gameRoot is null && !AskModTarget(p)) FallbackCustom(p, rfas);
        foreach (var r in rfas) if (File.Exists(r) && !p.LevelArchives.Contains(r, StringComparer.OrdinalIgnoreCase)) p.LevelArchives.Add(r);
        return p;
    }

    private static RfProject? OpenFolder()
    {
        var dir = Picker.Folder("Select the extracted level folder (the project folder)", null);
        if (dir is null) return null;
        var existing = Directory.EnumerateFiles(dir, "*.rfproj").FirstOrDefault();
        if (existing is not null) { try { return RfProject.Load(existing); } catch { } }

        var name = new DirectoryInfo(dir.TrimEnd('\\', '/')).Name;
        var p = new RfProject { Name = name, Game = DetectGame(dir), Mode = RfMode.Custom, ProjectFolder = dir };
        // If the folder lives inside a game install we can infer the mod; otherwise ask which mod it targets.
        if (ModChain.FindGameRoot(dir) is string gr)
        {
            var modDir = new DirectoryInfo(dir);
            while (modDir?.Parent is not null && !modDir.Parent.Name.Equals("Mods", StringComparison.OrdinalIgnoreCase)) modDir = modDir.Parent;
            if (modDir is not null) { p.GameRoot = gr; p.Mod = modDir.Name; p.Mode = RfMode.Default; }
        }
        if (string.IsNullOrEmpty(p.GameRoot) && !AskModTarget(p)) FallbackCustom(p, Array.Empty<string>());
        return p;
    }

    private static RfProject? NewMap()
    {
        var spec = NewMapDialog.Show();
        if (spec is null) return null;
        var parent = Picker.Folder("Choose where to create the new map's project folder", null);
        if (parent is null) return null;
        var dir = Path.Combine(parent, spec.Name);
        try
        {
            Directory.CreateDirectory(dir);
            var cfg = new TerrainConfig { MaterialSize = spec.MaterialSize, WorldSize = spec.WorldSize, YScale = 1f, WaterLevel = 30f, SeaFloorLevel = 0f, WaveHeight = 1f };
            var hm = HeightmapGenerator.Flat(spec.MaterialSize, cfg.MetersToRaw(35f));
            LevelSaver.CreateNewLevel(dir, spec.Name, cfg, hm, new EnvironmentSettings(), null, playable: true);
            try { File.WriteAllText(Path.Combine(dir, "refractorforge.game"), spec.Game == "BF1942" ? "1942" : "vietnam"); } catch { }
        }
        catch (Exception ex) { Picker.Error("New map failed:\n" + ex.Message); return null; }

        var p = new RfProject { Name = spec.Name, Game = spec.Game, Mode = RfMode.Custom, ProjectFolder = dir };
        // Ask which mod the new map is FOR, so its whole dependency chain (e.g. FHSW -> FH -> bf1942) is mounted
        // and the mapper can place that mod's objects. Skipping falls back to the last-used archives.
        if (!AskModTarget(p)) FallbackCustom(p, Array.Empty<string>());
        return p;
    }

    /// <summary>Ask which mod a project targets and record it (Default mode resolves that mod's full mount chain).
    /// Returns false if the user skipped. Seeds the dialog from the project, else the last-used game install.</summary>
    private static bool AskModTarget(RfProject p)
    {
        string? guessRoot = p.GameRoot;
        if (string.IsNullOrEmpty(guessRoot))
        {
            var saved = Settings.Load();
            foreach (var hint in new[] { saved?.Level, saved?.MeshArchives?.FirstOrDefault(), saved?.Textures?.FirstOrDefault() })
                if (!string.IsNullOrEmpty(hint) && ModChain.FindGameRoot(hint!) is string g) { guessRoot = g; break; }
        }
        var target = ModPickerDialog.Show(guessRoot, string.IsNullOrWhiteSpace(p.Mod) ? null : p.Mod, p.IncludeInheritedMods);
        if (target is null) return false;
        p.GameRoot = target.GameRoot;
        p.Mod = target.Mod;
        p.IncludeInheritedMods = target.IncludeInherited;
        if (p.Mode == RfMode.Custom && p.MeshArchives.Count == 0 && p.TextureArchives.Count == 0) p.Mode = RfMode.Default;
        if (target.GameRoot.ToLowerInvariant().Contains("vietnam")) p.Game = "BFVietnam";
        return true;
    }

    /// <summary>Fill a Custom project's mesh/texture libraries from the last-used archives (so a new/extracted map
    /// still has models + textures to work with), and record any base level archives for later export diffing.</summary>
    private static void FallbackCustom(RfProject p, string[] baseRfas)
    {
        p.Mode = RfMode.Custom;
        var saved = Settings.Load();
        if (saved?.MeshArchives is { Length: > 0 } m) p.MeshArchives.AddRange(m.Where(File.Exists));
        if (saved?.Textures is { Length: > 0 } t) p.TextureArchives.AddRange(t.Where(File.Exists));
        foreach (var r in baseRfas) if (File.Exists(r)) p.LevelArchives.Add(r);
    }

    /// <summary>Infer game / mod / game-install-root / map name from a level .rfa path like
    /// <c>…\Mods\&lt;Mod&gt;\Archives\&lt;base&gt;\levels\&lt;Map&gt;.rfa</c>.</summary>
    private static (string game, string mod, string? gameRoot, string mapName) InferFromPath(string rfaPath)
    {
        var mapName = Path.GetFileNameWithoutExtension(rfaPath);
        var mm = Regex.Match(mapName, @"^(.*?)_\d{3}$");
        if (mm.Success) mapName = mm.Groups[1].Value;

        string mod = "bf1942"; string? gameRoot = null;
        for (var d = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(rfaPath)) ?? "."); d?.Parent is not null; d = d.Parent)
            if (d.Parent.Name.Equals("Mods", StringComparison.OrdinalIgnoreCase)) { mod = d.Name; gameRoot = d.Parent.Parent?.FullName; break; }

        var lower = rfaPath.Replace('\\', '/').ToLowerInvariant();
        string game = (lower.Contains("/bfvietnam/") || (gameRoot?.ToLowerInvariant().Contains("vietnam") ?? false)) ? "BFVietnam" : "BF1942";
        return (game, mod, gameRoot, mapName);
    }

    /// <summary>Best-effort game detection for an already-extracted folder (the New-Map sidecar, else default).</summary>
    private static string DetectGame(string dir)
    {
        try
        {
            var sidecar = Path.Combine(dir, "refractorforge.game");
            if (File.Exists(sidecar)) return File.ReadAllText(sidecar).Trim().Contains("vietnam", StringComparison.OrdinalIgnoreCase) ? "BFVietnam" : "BF1942";
        }
        catch { }
        return "BF1942";
    }
}
