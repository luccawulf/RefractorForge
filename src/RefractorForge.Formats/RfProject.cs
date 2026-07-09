using System.Xml.Linq;

namespace RefractorForge.Formats;

public enum RfMode { Default, Custom }

/// <summary>
/// A RefractorForge project manifest (<c>.rfproj</c>, XML) — lives in the extracted level folder (next to
/// <c>init.con</c>). It records where the project's data comes from + the target game/mod/patch, so a map is
/// reopenable and shareable. Two flavours (per Workflow.md):
///  • <b>Default</b> — infer the mesh/texture/level archives from the game install (<see cref="GameRoot"/>) + the
///    <see cref="Mod"/> chain; the project folder holds only modified files.
///  • <b>Custom</b> — each data source is listed explicitly (mesh/texture/lexicon/level archives + a game test dir).
/// </summary>
public sealed class RfProject
{
    public string Name = "";
    public string Game = "BF1942";          // "BF1942" | "BFVietnam"
    public string Mod = "bf1942";
    public string? PatchNumber;             // e.g. "001"; null => no _NNN suffix on the exported .rfa
    public RfMode Mode = RfMode.Default;

    // Default mode:
    public string? GameRoot;                // the game install dir (holds BF1942.exe / BfVietnam.exe + Mods\)
    public bool RunTestPacked = true;

    // Custom mode:
    public List<string> MeshArchives = new();
    public List<string> TextureArchives = new();
    public List<string> LexiconFiles = new();
    public List<string> LevelArchives = new();   // base map .rfa(s) — the read/diff base; unmodified files stay here
    public string? MapName;                  // when the folder name != the map name
    public string? GameTestDir;              // pack the test .rfa under here when set

    /// <summary>The folder the <c>.rfproj</c> lives in (= the extracted level folder). Not serialized; set on load.</summary>
    public string ProjectFolder = "";

    /// <summary>The effective map name (Custom <see cref="MapName"/> if set, else the project folder name).</summary>
    public string EffectiveMapName =>
        !string.IsNullOrWhiteSpace(MapName) ? MapName! :
        (ProjectFolder.Length > 0 ? new DirectoryInfo(ProjectFolder.TrimEnd('\\', '/')).Name : Name);

    public string FilePath => Path.Combine(ProjectFolder, (Name.Length > 0 ? Name : "project") + ".rfproj");

    // ---- XML load / save ----

    public static RfProject Load(string rfprojPath)
    {
        var doc = XDocument.Load(rfprojPath);
        var r = doc.Root ?? throw new FormatException("empty .rfproj");
        string S(string n) => (string?)r.Element(n) ?? "";
        List<string> L(string n) => r.Element(n)?.Elements("Path").Select(e => (string?)e ?? "").Where(s => s.Length > 0).ToList() ?? new();
        var p = new RfProject
        {
            ProjectFolder = Path.GetDirectoryName(Path.GetFullPath(rfprojPath)) ?? "",
            Name = S("Name"),
            Game = S("Game") is { Length: > 0 } g ? g : "BF1942",
            Mod = S("Mod") is { Length: > 0 } m ? m : "bf1942",
            PatchNumber = S("PatchNumber") is { Length: > 0 } pn ? pn : null,
            Mode = string.Equals(S("Mode"), "Custom", StringComparison.OrdinalIgnoreCase) ? RfMode.Custom : RfMode.Default,
            GameRoot = S("GameRoot") is { Length: > 0 } gr ? gr : null,
            RunTestPacked = !string.Equals(S("RunTestPacked"), "false", StringComparison.OrdinalIgnoreCase),
            MeshArchives = L("MeshArchives"),
            TextureArchives = L("TextureArchives"),
            LexiconFiles = L("LexiconFiles"),
            LevelArchives = L("LevelArchives"),
            MapName = S("MapName") is { Length: > 0 } mn ? mn : null,
            GameTestDir = S("GameTestDir") is { Length: > 0 } gt ? gt : null,
        };
        if (p.Name.Length == 0) p.Name = p.EffectiveMapName;
        return p;
    }

    public void Save(string? path = null)
    {
        path ??= FilePath;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir != null) Directory.CreateDirectory(dir);
        XElement List(string n, List<string> items) => new(n, items.Select(s => new XElement("Path", s)));
        var root = new XElement("RfProject",
            new XElement("Name", Name),
            new XElement("Game", Game),
            new XElement("Mod", Mod),
            new XElement("PatchNumber", PatchNumber ?? ""),
            new XElement("Mode", Mode.ToString()));
        if (Mode == RfMode.Default)
        {
            root.Add(new XElement("GameRoot", GameRoot ?? ""));
            root.Add(new XElement("RunTestPacked", RunTestPacked ? "true" : "false"));
        }
        else
        {
            root.Add(List("MeshArchives", MeshArchives));
            root.Add(List("TextureArchives", TextureArchives));
            root.Add(List("LexiconFiles", LexiconFiles));
            root.Add(List("LevelArchives", LevelArchives));
            root.Add(new XElement("MapName", MapName ?? ""));
            root.Add(new XElement("GameTestDir", GameTestDir ?? ""));
        }
        new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(path);
    }

    // ---- Resolve to the concrete inputs the editor's load path consumes ----

    /// <summary>The level folder + mesh/texture archives the load block needs. The project is folder-based (the level
    /// data is extracted into <see cref="ProjectFolder"/>), so levelDir is the folder; the mesh/texture libraries are
    /// referenced (Custom: explicit; Default: derived from the game/mod chain like the Open-Mod flow).</summary>
    public (string levelDir, string[] meshArchives, string[] texArchives) Resolve()
    {
        if (Mode == RfMode.Custom)
            return (ProjectFolder,
                    MeshArchives.Where(File.Exists).ToArray(),
                    TextureArchives.Where(File.Exists).ToArray());

        // Default: derive the mesh/texture archives from GameRoot + Mod (non-interactive Open-Mod gathering).
        if (string.IsNullOrEmpty(GameRoot)) return (ProjectFolder, Array.Empty<string>(), Array.Empty<string>());
        var (mesh, tex) = CollectModArchives(GameRoot!, Mod);
        return (ProjectFolder, mesh, tex);
    }

    /// <summary>Collect a mod's mesh + texture archives (Archives\**\*.rfa across the init.con mount chain + the base
    /// game, split by name), matching Program.GatherModPaths but non-interactive. Level .rfa are excluded (the level
    /// is loaded from the project folder).</summary>
    public static (string[] mesh, string[] tex) CollectModArchives(string gameRoot, string mod)
    {
        var modDir = Path.Combine(gameRoot, "Mods", mod);
        var modPaths = new List<string>();
        var initCon = Path.Combine(modDir, "init.con");
        if (File.Exists(initCon))
            foreach (var raw in File.ReadAllLines(initCon))
            {
                var line = raw.Trim(); int sp = line.IndexOf(' ');
                if (sp < 0 || !line[..sp].Equals("game.addModPath", StringComparison.OrdinalIgnoreCase)) continue;
                var rel = line[(sp + 1)..].Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
                if (rel.Length == 0) continue;
                var abs = Path.GetFullPath(Path.Combine(gameRoot, rel));
                if (Directory.Exists(abs) && !modPaths.Any(x => x.Equals(abs, StringComparison.OrdinalIgnoreCase))) modPaths.Add(abs);
            }
        if (modPaths.Count == 0 && Directory.Exists(modDir)) modPaths.Add(modDir);
        var baseGuess = new[] { "BfVietnam", "bf1942", "bfvietnam" }.Select(b => Path.Combine(gameRoot, "Mods", b)).FirstOrDefault(Directory.Exists);
        if (baseGuess is not null && !modPaths.Any(x => x.Equals(baseGuess, StringComparison.OrdinalIgnoreCase))) modPaths.Add(baseGuess);

        static string[] AllRfa(string dir) => Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.rfa", SearchOption.AllDirectories).Where(f => !Path.GetFileName(f).StartsWith("~")).ToArray()
            : Array.Empty<string>();
        static bool IsLevelRfa(string p) => p.Replace('\\', '/').ToLowerInvariant().Contains("/levels/");
        static bool IsTex(string p) => Path.GetFileName(p).StartsWith("texture", StringComparison.OrdinalIgnoreCase);
        var all = new List<string>();
        foreach (var mp in modPaths) all.AddRange(AllRfa(Directory.Exists(Path.Combine(mp, "Archives")) ? Path.Combine(mp, "Archives") : mp));
        var mesh = all.Where(p => !IsTex(p) && !IsLevelRfa(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var tex = all.Where(p => IsTex(p) && !IsLevelRfa(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return (mesh, tex);
    }
}
