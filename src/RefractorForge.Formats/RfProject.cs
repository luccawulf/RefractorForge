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

    /// <summary>Follow each dependency's own init.con to mount mods the author didn't list (e.g. a mini-mod that
    /// names FHSW but not FH). Inherited mounts are appended at the lowest precedence. Default on.</summary>
    public bool IncludeInheritedMods = true;

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
            IncludeInheritedMods = !string.Equals(S("IncludeInheritedMods"), "false", StringComparison.OrdinalIgnoreCase),
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
        // Written for BOTH modes: the game install + mod chain settings also drive Test-launch and target-mod
        // resolution, which a Custom project needs just as much as a Default one.
        root.Add(new XElement("GameRoot", GameRoot ?? ""));
        root.Add(new XElement("RunTestPacked", RunTestPacked ? "true" : "false"));
        root.Add(new XElement("IncludeInheritedMods", IncludeInheritedMods ? "true" : "false"));
        if (Mode == RfMode.Custom)
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
        var mesh = new List<string>();
        var tex = new List<string>();

        // The MAP'S OWN archive first. A level can carry objects and textures that exist in no mod - Interstate's
        // Akina_Mountain ships 37 custom vehicles inside its .rfa - and extracting the level to a project folder
        // leaves those as loose files the mesh/texture libraries (which read .rfa) never see. Opening the same map
        // via Open Mod worked precisely because the level archive WAS in that list, which is why map-side objects
        // appeared there and not in a project. First = highest precedence: a map's own asset overrides the mod's,
        // exactly as the engine resolves it.
        mesh.AddRange(LevelArchives.Where(File.Exists));
        tex.AddRange(LevelArchives.Where(File.Exists));

        // Custom mode: the explicitly listed archives come next (the libraries are first-wins).
        if (Mode == RfMode.Custom)
        {
            foreach (var p in MeshArchives.Where(File.Exists)) if (!mesh.Contains(p, StringComparer.OrdinalIgnoreCase)) mesh.Add(p);
            foreach (var p in TextureArchives.Where(File.Exists)) if (!tex.Contains(p, StringComparer.OrdinalIgnoreCase)) tex.Add(p);
        }

        // TARGET MOD (both modes): append the mod's full resolved mount chain - the mod, its dependencies
        // (transitively, so FHSW brings FH) and the base game. Appending means a Custom project's explicit picks
        // still win, while a map authored for a mini-mod automatically gets that mod's whole asset stack.
        if (!string.IsNullOrWhiteSpace(GameRoot) && !string.IsNullOrWhiteSpace(Mod))
        {
            var (cm, ct) = CollectModArchives(GameRoot!, Mod, IncludeInheritedMods);
            foreach (var p in cm) if (!mesh.Contains(p, StringComparer.OrdinalIgnoreCase)) mesh.Add(p);
            foreach (var p in ct) if (!tex.Contains(p, StringComparer.OrdinalIgnoreCase)) tex.Add(p);
        }
        return (ProjectFolder, mesh.ToArray(), tex.ToArray());
    }

    /// <summary>Resolve this project's target mod to its full mount chain (see <see cref="ModChain"/>): the mod, its
    /// dependencies (transitively — the FHSW case), and the base game, in precedence order. Null when the project
    /// has no game install recorded.</summary>
    public ModChainResult? ResolveChain()
        => string.IsNullOrEmpty(GameRoot) ? null : ModChain.ResolveByName(GameRoot!, Mod, IncludeInheritedMods);

    /// <summary>Collect a mod's mesh + texture archives across its FULL mount chain, in precedence order.
    /// Delegates to <see cref="ModChain"/> so the Viewer and the project system share one resolver.</summary>
    public static (string[] mesh, string[] tex) CollectModArchives(string gameRoot, string mod, bool includeInherited = true)
        => ModChain.CollectArchives(ModChain.ResolveByName(gameRoot, mod, includeInherited));
}
