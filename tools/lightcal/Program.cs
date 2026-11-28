// lightcal build <base.rfa> <outDir> <name> [--safe | --unlit | --steer | --glow]  build a calibration level + <name>.calibration.json
// calibtool synth <manifest.json> <out.png> [w h fov pitch back] the picture the editor's model expects
// calibtool read  <screenshot> <manifest.json> [--out annotated.png] [--threshold n] [--mirror]
// calibtool check <level.rfa>                                    parse the level's new files back
using RefractorForge.Formats.Mesh;
using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using RefractorForge.Formats.Rfa;
using RefractorForge.Render;
using LC = RefractorForge.Render.LightCalibration;

static string Fwd(string s) => s.Replace((char)92, '/');
var json = new JsonSerializerOptions { WriteIndented = true };

static (byte[] Rgba, int W, int H) LoadImage(string path)
{
    if (path.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
    {
        var t = TgaTexture.Decode(File.ReadAllBytes(path)) ?? throw new InvalidDataException("Unreadable TGA.");
        return (t.Rgba, t.Width, t.Height);
    }
    using var bmp = new Bitmap(path);
    var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
    using var conv = bmp.Clone(rect, PixelFormat.Format32bppArgb);
    var data = conv.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    var raw = new byte[data.Stride * data.Height];
    Marshal.Copy(data.Scan0, raw, 0, raw.Length);
    conv.UnlockBits(data);
    var rgba = new byte[bmp.Width * bmp.Height * 4];
    for (int y = 0; y < bmp.Height; y++)
        for (int x = 0; x < bmp.Width; x++)
        {
            int s = y * data.Stride + x * 4, d = (y * bmp.Width + x) * 4;
            rgba[d] = raw[s + 2]; rgba[d + 1] = raw[s + 1]; rgba[d + 2] = raw[s]; rgba[d + 3] = 255;
        }
    return (rgba, bmp.Width, bmp.Height);
}

static void SavePng(string path, byte[] rgba, int w, int h)
{
    using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
    var rect = new Rectangle(0, 0, w, h);
    var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
    var raw = new byte[data.Stride * h];
    for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int s = (y * w + x) * 4, d = y * data.Stride + x * 4;
            raw[d] = rgba[s + 2]; raw[d + 1] = rgba[s + 1]; raw[d + 2] = rgba[s]; raw[d + 3] = 255;
        }
    Marshal.Copy(raw, 0, data.Scan0, raw.Length);
    bmp.UnlockBits(data);
    bmp.Save(path, ImageFormat.Png);
}

string cmd = args[0];
if (cmd == "build")
{
    string baseRfa = args[1], outDir = args[2], name = args[3];
    bool safe = args.Contains("--safe");
    bool unlit = args.Contains("--unlit");
    Directory.CreateDirectory(outDir);
    var dest = Path.Combine(outDir, name + ".rfa");
    if (File.Exists(dest)) File.Delete(dest);
    bool glow = args.Contains("--glow");
    bool fog = args.Contains("--fog");
    bool steer = args.Contains("--steer") || glow || fog;
    LC.Options opts;
    if (args.Contains("--bf1942"))
    {
        // Battlefield 1942: the sun red, ambientColor green, globalAmbientColor blue, each low enough that nothing
        // clips even if the game doubles its lights as BfVietnam does.
        opts = new LC.Options(baseRfa, name, outDir, Full: false, LC.Site.ElAlamein)
        {
            Bf1942 = true, BaseSub = "bf1942",
            Sun = new Vector3(0.5f, 0f, 0f), Back = Vector3.Zero, LmAmbient = Vector3.Zero,
            Ambient = new Vector3(0f, 0.25f, 0f), GlobalAmbient = new Vector3(0f, 0f, 0.25f),
        };
        // --trees: three stock swaying trees with different lightmap files, under plain grey light so a tree that
        // took its lightmap (black, or red) stands out from one that did not.
        if (args.Contains("--trees"))
            opts = opts with { Trees = true, Sun = new Vector3(0.4f), Ambient = new Vector3(0.15f), GlobalAmbient = new Vector3(0.15f) };
    }
    else if (glow)
    {
        // The fourth level: shader levers under WEAK lights (a night's worth), ordinary normals everywhere.
        string archives = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(baseRfa))!, "..", ".."));
        var sm = new RefractorFlatArchive(Path.Combine(archives, "standardMesh.rfa"));
        byte[] Mesh(string leaf) => sm.Read(sm.Entries.First(e => Fwd(e.Name).EndsWith("/" + leaf + ".sm", StringComparison.OrdinalIgnoreCase)));
        var bunker = Mesh("O_Sbunker04_m1"); var bush = Mesh("F_bush11_M1");
        opts = new LC.Options(baseRfa, name, outDir, Full: false, LC.Site.HueUnlit)
        {
            Steer = true, Glow = true,
            Sun = new Vector3(0.3f, 0.2f, 0.2f), Back = new Vector3(0.1f, 0.2f, 0.2f),
            Ambient = new Vector3(0.15f, 0.15f, 0.3f), LmAmbient = new Vector3(0.1f, 0.1f, 0.1f),
            RealMeshes = new[]
            {
                new LC.RealMesh("rfcal_bunk_k_m1", bunker, null, LC.ShaderStyle.Glow, false, "a retail 64-byte bunker AS SHIPPED, selfillum 1, flat white", Keep: true),
                new LC.RealMesh("rfcal_bunk_g_m1", bunker, null, LC.ShaderStyle.Glow, false, "the same bunker rewritten to 40 bytes, its own normals, selfillum 1: one even grey?"),
                new LC.RealMesh("rfcal_bush_g_m1", bush, "C06F_Tree", LC.ShaderStyle.VegetationGlow, false, "a retail bush made lightmap-ready, its own normals, selfillum 1: evenly lit?"),
                new LC.RealMesh("rfcal_bush_n_m1", bush, "C06F_Tree", LC.ShaderStyle.Vegetation, false, "the same bush without selfillum: ordinary shading under the weak lights, for contrast"),
            },
        };
    }
    else if (steer)
    {
        // The third level. Its retail meshes come from standardMesh.rfa, three folders up from the base level
        // (<mod>/Archives/BfVietnam/levels/<level>.rfa).
        string archives = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(baseRfa))!, "..", ".."));
        var sm = new RefractorFlatArchive(Path.Combine(archives, "standardMesh.rfa"));
        byte[] Mesh(string leaf) => sm.Read(sm.Entries.First(e => Fwd(e.Name).EndsWith("/" + leaf + ".sm", StringComparison.OrdinalIgnoreCase)));
        var bunker = Mesh("O_Sbunker04_m1"); var bush = Mesh("F_bush11_M1");
        opts = new LC.Options(baseRfa, name, outDir, Full: false, LC.Site.HueUnlit)
        {
            Steer = true, Fog = fog, FogShaped = args.Contains("--shaped"),
            // Both lights reach 1 on a normal that points straight up (Hue's sun stands 36 degrees high: each light
            // gives 0.59 of itself there), in every channel; the red channel still tells the sun from the back light.
            Sun = new Vector3(1.25f, 0.9f, 0.9f), Back = new Vector3(0.5f, 0.9f, 0.9f),
            Ambient = new Vector3(0.15f, 0.15f, 0.3f), LmAmbient = new Vector3(0.1f, 0.1f, 0.1f),
            RealMeshes = new[]
            {
                new LC.RealMesh("rfcal_bunk_s_m1", bunker, null, LC.ShaderStyle.Plain, true, "a retail 64-byte bunker rewritten to 40 bytes, normals up, flat white: one even grey on every face?"),
                new LC.RealMesh("rfcal_bunk_n_m1", bunker, null, LC.ShaderStyle.Plain, false, "the same bunker at 40 bytes with its own normals: ordinary shading, for contrast"),
                new LC.RealMesh("rfcal_bush_w_m1", bush, null, LC.ShaderStyle.Vegetation, true, "a retail bush (six LODs) made lightmap-ready, normals up, flat white: one even grey?"),
                new LC.RealMesh("rfcal_bush_t_m1", bush, "C06F_Tree", LC.ShaderStyle.Vegetation, true, "the same bush with its own leaf texture: cut out correctly and evenly lit?"),
            },
        };
    }
    else opts = new LC.Options(baseRfa, name, outDir, Full: !safe && !unlit, unlit ? LC.Site.HueUnlit : LC.Site.Hue) { Unlit = unlit };
    var m = LC.BuildLevel(opts);
    File.WriteAllText(Path.Combine(outDir, name + ".calibration.json"), JsonSerializer.Serialize(m, json));
    Console.WriteLine($"{dest}  {new FileInfo(dest).Length:N0} bytes");
    Console.WriteLine($"to sun {m.ToSun[0]:0.###}/{m.ToSun[1]:0.###}/{m.ToSun[2]:0.###}; viewer {m.Viewer[0]:0.#}/{m.Viewer[1]:0.#}/{m.Viewer[2]:0.#}");
    foreach (var b in m.Boards) Console.WriteLine($"  {b.Id}  {b.Mesh,-14} {b.Lightmap,-13} at {b.X}/{b.Y}/{b.Z}  {b.What}");
    foreach (var ro in m.Objects) Console.WriteLine($"  {ro.Id}  {ro.Mesh,-16} at {ro.X}/{ro.Y}/{ro.Z}  {ro.What}");
    foreach (var v in m.Volumes) Console.WriteLine($"  fog {v.Id}  {v.Mesh,-14} at {v.X}/{v.Y}/{v.Z}  {v.PlanesThrough} planes through, expect x{v.Transmission:0.00}  {v.What}");
    foreach (var n in m.Notes) Console.WriteLine("  note: " + n);
    return;
}
if (cmd == "check")
{
    var arc = new RefractorFlatArchive(args[1]);
    Console.WriteLine($"{arc.Entries.Count} entries, compressed {arc.IsCompressed}");
    foreach (var e in arc.Entries)
    {
        var n = Fwd(e.Name);
        if (n.Contains("/rfcal") || n.Contains("/Objects/") || n.EndsWith("StaticObjects.con") || n.Contains("rfcal_"))
        {
            var b = arc.Read(e);
            string extra = "";
            if (n.EndsWith(".sm"))
            {
                var sm = StandardMesh.Parse(b);
                extra = $"  v{sm.Version} qflag {sm.QFlag} consumed {sm.Consumed}/{sm.Total} " + string.Join(" | ", sm.Lods[0].Select(x => $"{x.Name} vf{x.VertexFormat}/{x.VertexByteSize} nv{x.NumVertices} nf{x.NumFaceValues} lm{x.HasLightmapUv}"));
            }
            else if (n.EndsWith(".tga")) extra = $"  type {b[2]} cmap {b[1]} {BitConverter.ToUInt16(b, 12)}x{BitConverter.ToUInt16(b, 14)} bpp {b[16]} desc {b[17]}";
            else if (n.EndsWith(".dds")) extra = $"  {BitConverter.ToUInt32(b, 16)}x{BitConverter.ToUInt32(b, 12)} mips {BitConverter.ToUInt32(b, 28)} pf 0x{BitConverter.ToUInt32(b, 80):X}";
            Console.WriteLine($"{e.UncompressedSize,9}  {n}{extra}");
        }
    }
    foreach (var rel in new[] { "Init.con", "StaticObjects.con", "Objects/objects.con", "Conquest/SoldierSpawns.con", "Objects/rfcal_pv_m1/Geometries.con", "StandardMesh/rfcal_pv_m1.rs" })
    {
        var e = arc.Entries.FirstOrDefault(x => Fwd(x.Name).EndsWith("/" + rel, StringComparison.OrdinalIgnoreCase));
        if (e is null) { Console.WriteLine($"--- {rel}: MISSING"); continue; }
        var text = Encoding.Latin1.GetString(arc.Read(e));
        Console.WriteLine($"--- {rel} ({text.Length} chars)");
        var lines = text.Replace("\r\n", "\n").Split('\n');
        IEnumerable<string> show = rel == "Init.con" ? lines.Where(l => l.StartsWith("renderer.", StringComparison.OrdinalIgnoreCase) || l.Contains("Objects") || l.Contains("alternativePath") || l.StartsWith("rem Refractor"))
                                 : rel.Contains("SoldierSpawns") ? lines.Where(l => l.StartsWith("Object.")).Take(12)
                                 : lines.Take(14);
        foreach (var l in show) Console.WriteLine("    " + l);
    }
    return;
}

if (cmd == "ls")
{
    // ls <archive.rfa> [substring] : every entry, with its size; with a substring, only the matching ones.
    var arc = new RefractorFlatArchive(args[1]);
    foreach (var e in arc.Entries)
        if (args.Length < 3 || Fwd(e.Name).Contains(args[2], StringComparison.OrdinalIgnoreCase))
            Console.WriteLine($"{arc.Read(e).Length,10:N0}  {Fwd(e.Name)}");
    return;
}

if (cmd == "unlight")
{
    // unlight <level.rfa> <ground-from.rfa> [--write] : put another archive's ground tiles (Textures/tx*.dds) into the
    // level and take out every baked object lightmap (the palette stays). Nothing else is touched. A dated backup is
    // made beside the level first, and the result is read back.
    var path = args[1];
    var lvl = new RefractorFlatArchive(path);
    var from = new RefractorFlatArchive(args[2]);
    static bool IsTile(string n) { var leaf = n.Split('/').Last().ToLowerInvariant(); return n.Contains("/Textures/", StringComparison.OrdinalIgnoreCase) && leaf.StartsWith("tx") && leaf.EndsWith(".dds"); }
    var src = from.Entries.Where(e => IsTile(Fwd(e.Name))).ToDictionary(e => Fwd(e.Name).Split('/').Last().ToLowerInvariant(), e => e);
    var repl = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    int same = 0;
    foreach (var e in lvl.Entries.Where(e => IsTile(Fwd(e.Name))))
    {
        string leaf = Fwd(e.Name).Split('/').Last().ToLowerInvariant();
        if (!src.TryGetValue(leaf, out var se)) { Console.WriteLine($"NO SOURCE for {leaf} - stopping."); return; }
        var b = from.Read(se);
        if (lvl.Read(e).AsSpan().SequenceEqual(b)) { same++; continue; }
        repl[Fwd(e.Name)] = b;
    }
    if (src.Count != lvl.Entries.Count(e => IsTile(Fwd(e.Name)))) { Console.WriteLine($"Tile counts differ ({src.Count} in the source) - stopping."); return; }
    var gone = lvl.Entries.Select(e => Fwd(e.Name))
        .Where(n => n.Contains("/objectlightmaps/", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)).ToList();
    Console.WriteLine($"{repl.Count} ground tile(s) to replace ({same} already identical), {gone.Count} object lightmap(s) to remove ({gone.Sum(n => (long)lvl.Read(lvl.Entries.First(e => Fwd(e.Name) == n)).Length) / 1048576} MB).");
    if (!args.Contains("--write")) { Console.WriteLine("dry run - add --write"); return; }
    string bak = path + ".before_unlight_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
    File.Copy(path, bak);
    Console.WriteLine("backup: " + bak);
    RefractorFlatArchive.RepackToFile(path, lvl, repl, gone);
    var check = new RefractorFlatArchive(path);
    int bad = repl.Count(kv => !check.Read(check.Entries.First(x => RefractorFlatArchive.NameComparer.Equals(Fwd(x.Name), kv.Key))).AsSpan().SequenceEqual(kv.Value));
    int left = check.Entries.Count(e => Fwd(e.Name).Contains("/objectlightmaps/", StringComparison.OrdinalIgnoreCase) && Fwd(e.Name).EndsWith(".tga", StringComparison.OrdinalIgnoreCase));
    Console.WriteLine($"repacked: {check.Entries.Count} entries (was {lvl.Entries.Count}); {repl.Count - bad} of {repl.Count} tiles read back identical; {left} object lightmaps left; {new FileInfo(path).Length / 1048576} MB.");
    return;
}

if (cmd == "extract")
{
    // extract <archive.rfa> <path substring> <outDir> : write every matching entry out by its leaf name.
    var arc = new RefractorFlatArchive(args[1]);
    Directory.CreateDirectory(args[3]);
    foreach (var e in arc.Entries.Where(x => Fwd(x.Name).Contains(args[2], StringComparison.OrdinalIgnoreCase)))
        File.WriteAllBytes(Path.Combine(args[3], Fwd(e.Name).Split('/').Last().ToLowerInvariant()), arc.Read(e));
    return;
}

if (cmd == "hash")
{
    // hash <archive.rfa> <path substring> : size and SHA-1 of every matching entry, to compare archives.
    var arc = new RefractorFlatArchive(args[1]);
    foreach (var e in arc.Entries.Where(x => Fwd(x.Name).Contains(args[2], StringComparison.OrdinalIgnoreCase)).OrderBy(x => Fwd(x.Name).ToLowerInvariant()))
    {
        var b = arc.Read(e);
        Console.WriteLine($"{Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(b))[..12]} {b.Length,10} {Fwd(e.Name).Split('/').Last().ToLowerInvariant()}");
    }
    return;
}

if (cmd == "cat")
{
    // cat <archive.rfa> <path substring> : print the first matching text entry.
    var arc = new RefractorFlatArchive(args[1]);
    var e = arc.Entries.First(x => Fwd(x.Name).Contains(args[2], StringComparison.OrdinalIgnoreCase));
    Console.WriteLine("== " + Fwd(e.Name));
    Console.WriteLine(Encoding.Latin1.GetString(arc.Read(e)));
    return;
}

if (cmd == "grep")
{
    // grep <folder> <regex> : every .con / .rs line matching, across every .rfa under the folder.
    var rx = new System.Text.RegularExpressions.Regex(args[2], System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    foreach (var file in Directory.EnumerateFiles(args[1], "*.rfa", SearchOption.AllDirectories))
    {
        RefractorFlatArchive arc;
        try { arc = new RefractorFlatArchive(file); } catch { continue; }
        foreach (var e in arc.Entries)
        {
            var n = Fwd(e.Name);
            if (!n.EndsWith(".con", StringComparison.OrdinalIgnoreCase) && !n.EndsWith(".rs", StringComparison.OrdinalIgnoreCase)) continue;
            string text;
            try { text = Encoding.Latin1.GetString(arc.Read(e)); } catch { continue; }
            foreach (var line in text.Split('\n'))
                if (rx.IsMatch(line)) Console.WriteLine($"{Path.GetFileName(file)}: {n}: {line.Trim()}");
        }
    }
    return;
}

if (cmd == "smcheck")
{
    // smcheck <level.rfa> <archives folder> : every mesh the level carries against its .rs and against the original it
    // was copied from (name without "_lm"): LODs, materials, vertex layouts, the .rs subshader names, the textures.
    var lvl = new RefractorFlatArchive(args[1]);
    var globals = Directory.EnumerateFiles(args[2], "*.rfa", SearchOption.TopDirectoryOnly)
        .Where(f => Path.GetFileName(f).StartsWith("standardMesh", StringComparison.OrdinalIgnoreCase)
                 || Path.GetFileName(f).StartsWith("objects", StringComparison.OrdinalIgnoreCase)
                 || Path.GetFileName(f).StartsWith("texture", StringComparison.OrdinalIgnoreCase))
        .Select(f => new RefractorFlatArchive(f)).ToList();
    var all = globals.Append(lvl).ToList();
    RefractorFlatArchiveEntry? Find(string tail, out RefractorFlatArchive? owner)
    {
        owner = null;
        foreach (var a in Enumerable.Reverse(all))
            foreach (var e in a.Entries)
                if (Fwd(e.Name).EndsWith(tail, StringComparison.OrdinalIgnoreCase)) { owner = a; return e; }
        return null;
    }
    string Describe(StandardMesh m) => $"v{m.Version} q{m.QFlag} lods {m.NumLods}: " + string.Join(" | ", m.Lods.Select(l =>
        string.Join(",", l.Select(x => $"{x.Name}[vf{x.VertexFormat}/{x.VertexByteSize}B v{x.NumVertices} i{x.NumFaceValues} rt{x.RenderType}]"))));
    var rxSub = new System.Text.RegularExpressions.Regex("subshader\\s+\"([^\"]+)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    var rxTex = new System.Text.RegularExpressions.Regex("(texture|normalmap|glossmap|detailmap|envmap|lightmap)\\s+\"([^\"]+)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    foreach (var e in lvl.Entries.Where(x => Fwd(x.Name).EndsWith(".sm", StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.Name))
    {
        string name = Path.GetFileNameWithoutExtension(Fwd(e.Name));
        var problems = new List<string>();
        StandardMesh m;
        try { m = StandardMesh.Parse(lvl.Read(e)); }
        catch (Exception ex) { Console.WriteLine($"{name}: DOES NOT PARSE: {ex.Message}"); continue; }
        if (m.Consumed != m.Total) problems.Add($"parser stopped at {m.Consumed} of {m.Total} bytes");
        var rsE = lvl.Entries.FirstOrDefault(x => Fwd(x.Name).EndsWith("/" + name + ".rs", StringComparison.OrdinalIgnoreCase));
        if (rsE is null) problems.Add("no .rs in the level");
        else
        {
            var rs = Encoding.Latin1.GetString(lvl.Read(rsE));
            var subs = rxSub.Matches(rs).Select(x => x.Groups[1].Value).ToList();
            var mats = m.Lods.SelectMany(l => l.Select(x => x.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var missing = mats.Where(x => !subs.Contains(x, StringComparer.OrdinalIgnoreCase)).ToList();
            if (missing.Count > 0) problems.Add("materials with no subshader: " + string.Join(",", missing));
            foreach (System.Text.RegularExpressions.Match t in rxTex.Matches(rs))
            {
                var tex = t.Groups[2].Value.Replace('\\', '/');
                if (Find("/" + tex.TrimStart('/') + ".dds", out _) is null && Find("/" + tex.TrimStart('/') + ".tga", out _) is null)
                    problems.Add("texture not found: " + tex);
            }
        }
        // The original it was copied from.
        string orig = System.Text.RegularExpressions.Regex.Replace(name, "_lm(?=_|$)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        string od = "";
        if (!string.Equals(orig, name, StringComparison.OrdinalIgnoreCase))
        {
            var oe = Find("/" + orig + ".sm", out var oa);
            if (oe is null) problems.Add("original " + orig + ".sm not found");
            else
            {
                var om = StandardMesh.Parse(oa!.Read(oe));
                od = Describe(om);
                if (om.NumLods != m.NumLods) problems.Add($"LODs {om.NumLods} -> {m.NumLods}");
                for (int l = 0; l < Math.Min(om.NumLods, m.NumLods); l++)
                {
                    if (om.Lods[l].Count != m.Lods[l].Count) problems.Add($"LOD{l} materials {om.Lods[l].Count} -> {m.Lods[l].Count}");
                    for (int k = 0; k < Math.Min(om.Lods[l].Count, m.Lods[l].Count); k++)
                    {
                        var a = om.Lods[l][k]; var b = m.Lods[l][k];
                        if (a.NumVertices != b.NumVertices || a.NumFaceValues != b.NumFaceValues || a.RenderType != b.RenderType)
                            problems.Add($"LOD{l}.{k} v{a.NumVertices}/i{a.NumFaceValues}/rt{a.RenderType} -> v{b.NumVertices}/i{b.NumFaceValues}/rt{b.RenderType}");
                        if (b.NumVertices > 65535) problems.Add($"LOD{l}.{k} has {b.NumVertices} vertices (16-bit indices)");
                    }
                }
                if (om.CollisionSections.Count != m.CollisionSections.Count) problems.Add($"collision {om.CollisionSections.Count} -> {m.CollisionSections.Count}");
            }
        }
        Console.WriteLine($"{(problems.Count == 0 ? "ok     " : "PROBLEM")} {name}: {Describe(m)}");
        if (od.Length > 0 && args.Length > 3) Console.WriteLine($"        original {orig}: {od}");
        foreach (var p in problems) Console.WriteLine("        - " + p);
    }
    return;
}

if (cmd == "relod")
{
    // relod <level.rfa> <archives folder> [--write] : rebuild the level's lightmap-ready mesh copies with a lightmap
    // channel in EVERY LOD (LightmapUnwrapper.AllLodPlans). LOD0 must come out exactly as it is, or the copy is left
    // alone - the baked lightmaps are keyed to it. --write backs the archive up beside itself, then repacks it.
    var path = args[1];
    var lvl = new RefractorFlatArchive(path);
    var globals = new[] { "objects.rfa", "standardMesh.rfa" }.Select(f => Path.Combine(args[2], f)).Where(File.Exists)
                                                              .Select(f => new RefractorFlatArchive(f)).ToList();
    var objE = lvl.Entries.First(e => Fwd(e.Name).EndsWith("RF_LightmapReady/Objects.con", StringComparison.OrdinalIgnoreCase));
    var pairs = Encoding.Latin1.GetString(lvl.Read(objE)).Split('\n')
        .Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        .Where(t => t.Length == 5 && t[0] == "rem" && t[1] == "rf-lightmap-ready" && t[2] == "geometry")
        .Select(t => (Orig: t[3], Copy: t[4])).ToList();
    // Each original geometry's file, from the global archives' Geometries.con.
    var fileOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var a in globals)
        foreach (var e in a.Entries.Where(x => Fwd(x.Name).EndsWith(".con", StringComparison.OrdinalIgnoreCase)))
        {
            string? cur = null;
            foreach (var raw in Encoding.Latin1.GetString(a.Read(e)).Split('\n'))
            {
                var t = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (t.Length >= 3 && t[0].Equals("GeometryTemplate.create", StringComparison.OrdinalIgnoreCase)) cur = t[2];
                else if (t.Length >= 2 && cur is not null && t[0].Equals("GeometryTemplate.file", StringComparison.OrdinalIgnoreCase))
                    fileOf.TryAdd(cur, t[1].Replace('\\', '/'));
            }
        }
    var replacements = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    foreach (var (orig, copy) in pairs)
    {
        var copyE = lvl.Entries.FirstOrDefault(e => Fwd(e.Name).EndsWith("/StandardMesh/" + copy + ".sm", StringComparison.OrdinalIgnoreCase));
        if (copyE is null) { Console.WriteLine($"skip {copy}: no copy mesh in the level"); continue; }
        if (!fileOf.TryGetValue(orig, out var f)) { Console.WriteLine($"skip {copy}: original geometry {orig} not found"); continue; }
        string leaf = f.Split('/').Last();
        var oe = globals.SelectMany(a => a.Entries.Select(e => (a, e)))
                        .FirstOrDefault(x => Fwd(x.e.Name).EndsWith("/" + leaf + ".sm", StringComparison.OrdinalIgnoreCase));
        if (oe.e is null) { Console.WriteLine($"skip {copy}: original mesh {leaf}.sm not found"); continue; }
        var origBytes = oe.a.Read(oe.e);
        var sm = StandardMesh.Parse(origBytes);
        var cur = StandardMesh.Parse(lvl.Read(copyE));
        if (cur.Lods.All(l => l.All(m => m.HasLightmapUv)) ) { Console.WriteLine($"ok   {copy}: every LOD already has the channel"); continue; }
        var r = LightmapUnwrapper.Unwrap(sm);
        if (r.Status != LightmapUnwrapper.UnwrapStatus.Ok || r.Lod0Plan is null) { Console.WriteLine($"skip {copy}: unwrap {r.Status}"); continue; }
        var bytes = StandardMeshRewriter.Write(origBytes, sm, LightmapUnwrapper.AllLodPlans(sm, r.Lod0Plan), new StandardMeshRewriter.Options(SetQFlag: true));
        var outSm = StandardMesh.Parse(bytes);
        // LOD0 exactly as the lightmaps were baked against.
        bool same = outSm.Lods[0].Count == cur.Lods[0].Count;
        for (int m = 0; same && m < cur.Lods[0].Count; m++)
        {
            var a = cur.Lods[0][m]; var b = outSm.Lods[0][m];
            same = a.NumVertices == b.NumVertices && a.VertexByteSize == b.VertexByteSize && a.RawIndices.AsSpan().SequenceEqual(b.RawIndices)
                   && a.LightmapUvs.AsSpan().SequenceEqual(b.LightmapUvs) && a.Vertices.AsSpan().SequenceEqual(b.Vertices);
        }
        if (!same) { Console.WriteLine($"SKIP {copy}: LOD0 would change - left alone"); continue; }
        Console.WriteLine($"fix  {copy}: {cur.NumLods} LODs, " + string.Join(" ", outSm.Lods.Select(l => string.Join(",", l.Select(m => m.VertexByteSize)))));
        replacements[Fwd(copyE.Name)] = bytes;
    }
    Console.WriteLine($"{replacements.Count} mesh(es) to rebuild.");
    if (args.Contains("--write") && replacements.Count > 0)
    {
        string bak = path + ".before_relod_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
        File.Copy(path, bak);
        Console.WriteLine("backup: " + bak);
        RefractorFlatArchive.RepackToFile(path, lvl, replacements);
        var check = new RefractorFlatArchive(path);
        int bad = 0;
        foreach (var (n, b) in replacements)
        {
            var e = check.Entries.First(x => RefractorFlatArchive.NameComparer.Equals(Fwd(x.Name), n));
            if (!check.Read(e).AsSpan().SequenceEqual(b)) bad++;
        }
        Console.WriteLine($"repacked: {check.Entries.Count} entries (was {lvl.Entries.Count}), {replacements.Count - bad} of {replacements.Count} rebuilt meshes read back identical.");
    }
    return;
}

if (cmd == "lmsound")
{
    // lmsound <level.rfa> <archives folder> [--write] : give the level's lightmap-ready copies the sound scripts their
    // originals load from beside their own .con (LightmapReady.Emit does this now; this mends a level made before).
    var path = args[1];
    var lvl = new RefractorFlatArchive(path);
    var objects = new RefractorFlatArchive(Path.Combine(args[2], "objects.rfa"));
    var objE = lvl.Entries.First(e => Fwd(e.Name).EndsWith("RF_LightmapReady/Objects.con", StringComparison.OrdinalIgnoreCase));
    string objPath = Fwd(objE.Name), folder = objPath[..objPath.LastIndexOf('/')];
    var text = Encoding.Latin1.GetString(lvl.Read(objE));
    // copy -> original, from the manifest lines.
    var origOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var l in text.Split('\n'))
    {
        var t = l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (t.Length == 5 && t[0] == "rem" && t[1] == "rf-lightmap-ready" && (t[2] == "placed" || t[2] == "copy")) origOf[t[4]] = t[3];
    }
    // original -> the .con that creates it.
    var srcOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var e in objects.Entries.Where(x => Fwd(x.Name).EndsWith(".con", StringComparison.OrdinalIgnoreCase)))
        foreach (var l in Encoding.Latin1.GetString(objects.Read(e)).Split('\n'))
        {
            var t = l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (t.Length >= 3 && t[0].Equals("ObjectTemplate.create", StringComparison.OrdinalIgnoreCase)) srcOf.TryAdd(t[2], Fwd(e.Name));
        }
    var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
    var add = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    string? cur = null; int changed = 0;
    for (int i = 0; i < lines.Count; i++)
    {
        var t = lines[i].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (t.Length >= 3 && t[0].Equals("ObjectTemplate.create", StringComparison.OrdinalIgnoreCase)) { cur = t[2]; continue; }
        if (t.Length < 2 || !t[0].EndsWith(".loadSoundScript", StringComparison.OrdinalIgnoreCase) || cur is null) continue;
        if (!origOf.TryGetValue(cur, out var orig) || !srcOf.TryGetValue(orig, out var src)) { Console.WriteLine($"?? {cur}: original not found"); continue; }
        if (t[1].StartsWith("Sounds/" + orig + "/", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine($"ok {cur}: already mended"); continue; }
        var p = RefractorForge.Formats.Con.LightmapReady.ResolveFromScript(src, t[1]);
        var se = p is null ? null : objects.Entries.FirstOrDefault(x => Fwd(x.Name).Equals(p, StringComparison.OrdinalIgnoreCase));
        string prefix = t[0][..t[0].IndexOf('.')];
        if (se is null)
        {
            lines[i] = $"rem RefractorForge: the original's sound script {p} was not found, so this copy has no sound";
            Console.WriteLine($"drop {cur}: {p} not found - line commented out");
        }
        else
        {
            string leaf = p![(p.LastIndexOf('/') + 1)..], rel = $"Sounds/{orig}/{leaf}";
            add[$"{folder}/{rel}"] = objects.Read(se);
            lines[i] = $"{prefix}.loadSoundScript {rel}";
            Console.WriteLine($"fix {cur}: {t[1]} -> {rel} (copied {p}, {add[$"{folder}/{rel}"].Length} bytes)");
        }
        changed++;
    }
    Console.WriteLine($"{changed} line(s) to mend.");
    if (args.Contains("--write") && changed > 0)
    {
        add[objPath] = Encoding.Latin1.GetBytes(string.Join("\r\n", lines));
        string bak = path + ".before_lmsound_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
        File.Copy(path, bak);
        Console.WriteLine("backup: " + bak);
        RefractorFlatArchive.RepackToFile(path, lvl, add);
        var check = new RefractorFlatArchive(path);
        int ok = add.Count(kv => check.Entries.FirstOrDefault(x => Fwd(x.Name).Equals(kv.Key, StringComparison.OrdinalIgnoreCase)) is { } e && check.Read(e).AsSpan().SequenceEqual(kv.Value));
        Console.WriteLine($"repacked: {check.Entries.Count} entries (was {lvl.Entries.Count}), {ok} of {add.Count} written entries read back identical.");
    }
    return;
}

if (cmd == "sminfo")
{
    // sminfo <archive.rfa> <mesh leaf> : bounding box and, per material, the box of its vertices and its normals' spread.
    var arc = new RefractorFlatArchive(args[1]);
    var e = arc.Entries.First(x => Fwd(x.Name).EndsWith("/" + args[2] + ".sm", StringComparison.OrdinalIgnoreCase));
    var m = StandardMesh.Parse(arc.Read(e));
    Console.WriteLine($"{Fwd(e.Name)}: box {string.Join(" ", m.BoundingBox.Select(f => f.ToString("0.0")))}, lods {m.NumLods}");
    foreach (var mat in m.Lods[0])
    {
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        int up = 0, down = 0, side = 0;
        for (int i = 0; i < mat.Vertices.Length; i++)
        {
            var v = new Vector3(mat.Vertices[i].X, mat.Vertices[i].Y, mat.Vertices[i].Z);
            lo = Vector3.Min(lo, v); hi = Vector3.Max(hi, v);
            float ny = i < mat.Normals.Length ? mat.Normals[i].Y : 0;
            if (ny > 0.5f) up++; else if (ny < -0.5f) down++; else side++;
        }
        Console.WriteLine($"  {mat.Name}: {mat.NumVertices} verts {mat.VertexByteSize}B, box ({lo.X:0.0},{lo.Y:0.0},{lo.Z:0.0})-({hi.X:0.0},{hi.Y:0.0},{hi.Z:0.0}), normals up {up} down {down} side {side}");
    }
    return;
}

if (cmd == "vegscan")
{
    // vegscan <standardMesh.rfa> [maxSize] : small meshes whose .rs is a vegetation material (alphatestref + selfillum).
    var arc = new RefractorFlatArchive(args[1]);
    float max = args.Length > 2 ? float.Parse(args[2]) : 5f;
    var rsOf = arc.Entries.Where(x => Fwd(x.Name).EndsWith(".rs", StringComparison.OrdinalIgnoreCase))
                          .ToDictionary(x => Path.GetFileNameWithoutExtension(Fwd(x.Name)), x => x, StringComparer.OrdinalIgnoreCase);
    foreach (var e in arc.Entries.Where(x => Fwd(x.Name).EndsWith(".sm", StringComparison.OrdinalIgnoreCase)))
    {
        string name = Path.GetFileNameWithoutExtension(Fwd(e.Name));
        if (!rsOf.TryGetValue(name, out var re)) continue;
        string rs = Encoding.Latin1.GetString(arc.Read(re));
        if (!rs.Contains("alphatestref", StringComparison.OrdinalIgnoreCase) || !rs.Contains("selfillum", StringComparison.OrdinalIgnoreCase)) continue;
        StandardMesh m;
        try { m = StandardMesh.Parse(arc.Read(e)); } catch { continue; }
        var b = m.BoundingBox;
        float sx = b[3] - b[0], sy = b[4] - b[1], sz = b[5] - b[2];
        if (MathF.Max(sx, MathF.Max(sy, sz)) > max || sy < 1.2f) continue;
        Console.WriteLine($"{name,-28} {sx:0.0} x {sy:0.0} x {sz:0.0}  lods {m.NumLods}  mats {m.Lods[0].Count}  {string.Join(",", m.Lods.Select(l => l.Count == 0 ? "-" : l[0].VertexByteSize.ToString()))}  verts {m.Lods[0].Sum(x => x.NumVertices)}");
    }
    return;
}

if (cmd == "smscan")
{
    // smscan <archive.rfa>... : how retail meshes combine vertex layouts across LODs.
    var tally = new Dictionary<string, (int N, string Example)>();
    foreach (var file in args.Skip(1))
    {
        var arc = new RefractorFlatArchive(file);
        foreach (var e in arc.Entries.Where(x => Fwd(x.Name).EndsWith(".sm", StringComparison.OrdinalIgnoreCase)))
        {
            StandardMesh m;
            try { m = StandardMesh.Parse(arc.Read(e)); } catch { continue; }
            if (m.NumLods < 2) continue;
            var key = string.Join(" | ", m.Lods.Select(l => string.Join(",", l.Select(x => x.VertexByteSize).Distinct())));
            bool lm0 = m.Lods[0].Any(x => x.VertexByteSize != 32), lmRest = m.Lods.Skip(1).Any(l => l.Any(x => x.VertexByteSize != 32));
            string cls = lm0 && !lmRest ? "LOD0 wide, rest 32" : lm0 && lmRest ? "several LODs wide" : !lm0 && lmRest ? "LOD0 32, a later LOD wide" : "all 32";
            tally[cls] = tally.TryGetValue(cls, out var t) ? (t.N + 1, t.Example) : (1, Path.GetFileName(Fwd(e.Name)) + " " + key);
        }
    }
    foreach (var (k, v) in tally.OrderByDescending(x => x.Value.N)) Console.WriteLine($"{v.N,6}  {k}   e.g. {v.Example}");
    return;
}

if (cmd == "onlyscan")
{
    // onlyscan <archive.rfa>... : run every mesh through the lightmap-only rewrite (every LOD to the 40-byte vertex,
    // normals up; unwrapped first when it has no lightmap UVs) and read the result back.
    int ok = 0, total = 0;
    var bad = new Dictionary<string, (int N, string Example)>();
    void Bad(string why, string name) => bad[why] = bad.TryGetValue(why, out var t) ? (t.N + 1, t.Example) : (1, name);
    foreach (var file in args.Skip(1))
    {
        var arc = new RefractorFlatArchive(file);
        foreach (var e in arc.Entries.Where(x => Fwd(x.Name).EndsWith(".sm", StringComparison.OrdinalIgnoreCase)))
        {
            string name = Path.GetFileName(Fwd(e.Name));
            byte[] raw; StandardMesh m;
            try { raw = arc.Read(e); m = StandardMesh.Parse(raw); } catch { continue; }
            if (m.Lods.Count == 0 || m.Lods[0].Count == 0) continue;
            total++;
            bool full = m.Lods[0].All(x => x.HasLightmapUv && x.LightmapUvs.Any(uv => MathF.Abs(uv.U) > 1e-6f || MathF.Abs(uv.V) > 1e-6f));
            IReadOnlyList<IReadOnlyList<StandardMeshRewriter.MaterialPlan?>>? plans = null;
            if (!full)
            {
                var r = LightmapUnwrapper.Unwrap(m);
                if (r.Status != LightmapUnwrapper.UnwrapStatus.Ok || r.Lod0Plan is null) { Bad("unwrap: " + r.Status, name); continue; }
                try { plans = LightmapUnwrapper.AllLodPlans(m, r.Lod0Plan); } catch (Exception ex) { Bad("all-LOD plans: " + ex.Message.Split('.')[0], name); continue; }
            }
            try
            {
                var only = LightmapUnwrapper.LightmapOnlyPlans(m, plans, (0f, 1f, 0f));
                var bytes = StandardMeshRewriter.Write(raw, m, only, new StandardMeshRewriter.Options(SetQFlag: true));
                var back = StandardMesh.Parse(bytes);
                if (back.Lods.Count != m.Lods.Count) { Bad("LOD count changed", name); continue; }
                bool all40 = back.Lods.All(l => l.All(x => x.VertexByteSize == 40));
                bool up = back.Lods.All(l => l.All(x => x.Normals.All(n => MathF.Abs(n.X) < 1e-6f && MathF.Abs(n.Y - 1f) < 1e-6f && MathF.Abs(n.Z) < 1e-6f)));
                bool tris = back.Lods.Zip(m.Lods).All(z => z.First.Zip(z.Second).All(y => y.First.Faces.Length == y.Second.Faces.Length));
                bool uvKept = !full || back.Lods[0].Zip(m.Lods[0]).All(y => y.First.LightmapUvs.Length == y.Second.LightmapUvs.Length
                              && y.First.LightmapUvs.Zip(y.Second.LightmapUvs).All(q => q.First.U == q.Second.U && q.First.V == q.Second.V));
                if (!all40) Bad("a LOD is not 40-byte after the rewrite (" + string.Join(",", back.Lods.SelectMany(l => l.Select(x => x.VertexByteSize)).Distinct()) + ")", name);
                else if (!up) Bad("a normal is not up", name);
                else if (!tris) Bad("triangle count changed", name);
                else if (!uvKept)
                {
                    Bad("an existing unwrap changed", name);
                    if (args.Contains("--why"))
                        for (int mi = 0; mi < m.Lods[0].Count; mi++)
                        {
                            var o = m.Lods[0][mi].LightmapUvs; var nn = back.Lods[0][mi].LightmapUvs;
                            int diff = 0, nan = 0; string first = "";
                            for (int q = 0; q < Math.Min(o.Length, nn.Length); q++)
                            {
                                if (float.IsNaN(o[q].U) || float.IsNaN(o[q].V)) { nan++; continue; }
                                if (o[q].U != nn[q].U || o[q].V != nn[q].V) { if (diff++ == 0) first = $"#{q} {o[q].U:0.####},{o[q].V:0.####} -> {nn[q].U:0.####},{nn[q].V:0.####}"; }
                            }
                            Console.WriteLine($"    {name} mat{mi} {m.Lods[0][mi].VertexByteSize}B rt{m.Lods[0][mi].RenderType}: {o.Length} -> {nn.Length} uvs, {diff} differ, {nan} NaN  {first}");
                        }
                }
                else ok++;
            }
            catch (Exception ex) { Bad("rewrite: " + ex.Message.Split('.')[0], name); }
        }
    }
    Console.WriteLine($"{ok} of {total} meshes rewrite to the lightmap-only form and read back");
    foreach (var (k, v) in bad.OrderByDescending(x => x.Value.N)) Console.WriteLine($"{v.N,6}  {k}   e.g. {v.Example}");
    return;
}

if (cmd == "lmstat")
{
    // lmstat <level.rfa> <lightmap name substring> [out.png] : decode an object lightmap as the game does (through the
    // level's Palette.pal, 5:6:5) and print its range; optionally write it as a picture.
    var arc = new RefractorFlatArchive(args[1]);
    var palE = arc.Entries.FirstOrDefault(x => Fwd(x.Name).EndsWith("/Palette.pal", StringComparison.OrdinalIgnoreCase)
                                             && Fwd(x.Name).Contains("lightmap", StringComparison.OrdinalIgnoreCase));
    var pal = palE is null ? null : LightmapPalette.Parse(arc.Read(palE));
    Console.WriteLine(palE is null ? "no Palette.pal in the archive" : $"palette {Fwd(palE.Name)}: linear {pal!.IsLinear}, usable {pal.Usable}");
    foreach (var e in arc.Entries.Where(x => Fwd(x.Name).Contains(args[2], StringComparison.OrdinalIgnoreCase)))
    {
        var raw = arc.Read(e);
        var t = pal?.Decode(raw) ?? TgaTexture.Decode(raw);
        if (t is null) { Console.WriteLine($"{Fwd(e.Name)}: not decodable"); continue; }
        int n = t.Width * t.Height; var hist = new int[8]; double sum = 0; int mx = 0;
        for (int i = 0; i < n; i++) { int b = t.Rgba[i * 4 + 2]; sum += b; mx = Math.Max(mx, b); hist[b / 32]++; }
        Console.WriteLine($"{Fwd(e.Name)}: {t.Width}x{t.Height}, type {raw[2]}, bpp {raw[16]}, blue mean {sum / n:0.0}, max {mx}, histogram by 32s: {string.Join(" ", hist)}");
        if (args.Length > 3) SavePng(args[3], t.Rgba, t.Width, t.Height);
    }
    return;
}

if (cmd == "shares")
{
    // shares <level.rfa> <minIntensity> <radius> <sunX> <sunY> <sunZ> x z [x z ...] : the share of the two lights the
    // engine's static rule (EngineLight.StaticShare) gives an unlightmapped object at each point, from the level's own
    // shadow bits and terrain tiles - each sample the mean of the 1 m texture block (4 x 4 texels) it falls in.
    var arc = new RefractorFlatArchive(args[1]);
    var lsb = RefractorForge.Formats.Terrain.LightmapShadowBits.Decode(arc.Read(arc.Entries.First(x => Fwd(x.Name).EndsWith("/LightmapShadowBits.lsb", StringComparison.OrdinalIgnoreCase))));
    var mask = lsb.ToShadowMask(out int side);
    var tiles = arc.Entries.Where(e => { var n = Fwd(e.Name); return n.Contains("/Textures/tx", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) && n.Split('/').Length == 5; })
                           .Select(e => (e.Name, arc.Read(e))).ToList();
    var terrain = Encoding.Latin1.GetString(arc.Read(arc.Entries.First(e => Fwd(e.Name).EndsWith("/Init/Terrain.con", StringComparison.OrdinalIgnoreCase))));
    var cfg = RefractorForge.Formats.Terrain.TerrainConfig.Parse(terrain.Replace("\r\n", "\n").Split('\n'));
    var tt = TerrainTexture.FromTileBytes(tiles, cfg.WorldSize)!;
    float minI = float.Parse(args[2]), radius = float.Parse(args[3]);
    var sun = new Vector3(float.Parse(args[4]), float.Parse(args[5]), float.Parse(args[6]));
    bool InShadow(Vector3 p)
    {
        int px = (int)(p.X / cfg.WorldSize * side), pz = (int)(p.Z / cfg.WorldSize * side);
        return px >= 0 && pz >= 0 && px < side && pz < side && mask[pz * side + px] != 0;
    }
    Vector3 Ground(Vector3 p)
    {
        float bx = MathF.Floor(p.X), bz = MathF.Floor(p.Z); Vector3 sum = Vector3.Zero;
        for (int j = 0; j < 4; j++) for (int i = 0; i < 4; i++) { var c = tt.Sample(bx + (i + 0.5f) / 4f, bz + (j + 0.5f) / 4f) * 255f; sum += EngineLight.TileColour((byte)MathF.Round(c.X), (byte)MathF.Round(c.Y), (byte)MathF.Round(c.Z)); }
        return sum / 16f;
    }
    for (int i = 7; i + 1 < args.Length; i += 2)
    {
        var pos = new Vector3(float.Parse(args[i]), 0f, float.Parse(args[i + 1]));
        var sb = new StringBuilder();
        foreach (var q in EngineLight.StaticSamplePoints(pos, radius, sun))
            sb.Append(InShadow(q) ? $" {EngineLight.TerrainSample(true, Ground(q)) * 255f,5:0.0}" : "   lit");
        Console.WriteLine($"{pos.X,5:0},{pos.Z,5:0}  r {radius:0.###}  share {EngineLight.StaticShare(pos, radius, sun, minI, InShadow, Ground):0.0000}   samples{sb}");
    }
    return;
}

if (cmd == "ground")
{
    // ground <level.rfa> x z [x z ...] : the terrain tiles' colour under each point, over growing windows.
    var arc = new RefractorFlatArchive(args[1]);
    var tiles = arc.Entries.Where(e => { var n = Fwd(e.Name); return n.Contains("/Textures/tx", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) && n.Split('/').Length == 5; })
                           .Select(e => (e.Name, arc.Read(e))).ToList();
    var terrain = Encoding.Latin1.GetString(arc.Read(arc.Entries.First(e => Fwd(e.Name).EndsWith("/Init/Terrain.con", StringComparison.OrdinalIgnoreCase))));
    var cfg = RefractorForge.Formats.Terrain.TerrainConfig.Parse(terrain.Replace("\r\n", "\n").Split('\n'));
    var tt = TerrainTexture.FromTileBytes(tiles, cfg.WorldSize)!;
    for (int i = 2; i + 1 < args.Length; i += 2)
    {
        float x = float.Parse(args[i]), z = float.Parse(args[i + 1]);
        Console.Write($"{x,5:0.#},{z,5:0.#}:");
        foreach (float half in new[] { 0f, 1f, 2f, 4f, 8f, 16f })
        {
            Vector3 sum = Vector3.Zero; int n = 0;
            for (float dz = -half; dz <= half; dz += 0.25f)
                for (float dx = -half; dx <= half; dx += 0.25f) { sum += tt.Sample(x + dx, z + dz); n++; }
            var c = sum / n * 255f;
            Console.Write($"   +-{half,2:0}m {c.X,5:0.0} {c.Y,5:0.0} {c.Z,5:0.0}");
        }
        Console.WriteLine();
    }
    return;
}

if (cmd == "lsb")
{
    // lsb <level.rfa> x z [x z ...] : the terrain shadow map's lit share overall and round each point.
    var arc = new RefractorFlatArchive(args[1]);
    var e = arc.Entries.First(x => Fwd(x.Name).EndsWith("/LightmapShadowBits.lsb", StringComparison.OrdinalIgnoreCase));
    var raw = arc.Read(e);
    var lsb = RefractorForge.Formats.Terrain.LightmapShadowBits.Decode(raw);
    var vis = lsb.ToShadowMask(out int side);
    long lit = 0; foreach (var b in vis) if (b != 0) lit++;
    Console.WriteLine($"{raw.Length:N0} bytes, grid {lsb.GridDim} x {lsb.TilePixels} px = {side}, lit {100.0 * lit / vis.Length:0.0}%, empty tiles {lsb.Tiles.Count(t => t.IsEmpty)}/{lsb.Tiles.Count}");
    float world = float.Parse(args[2]);
    for (int i = 3; i + 1 < args.Length; i += 2)
    {
        float x = float.Parse(args[i]), z = float.Parse(args[i + 1]);
        Console.WriteLine($"round {x},{z} (rows north to south, 1 m steps, # = lit):");
        foreach (bool flip in new[] { false, true })
        {
            Console.WriteLine(flip ? "  if row 0 is NORTH:" : "  if row 0 is SOUTH:");
            for (int dz = 6; dz >= -6; dz--)
            {
                var sb = new StringBuilder("    ");
                for (int dx = -8; dx <= 8; dx++)
                {
                    int px = (int)((x + dx) / world * side), pz = (int)((z + dz) / world * side);
                    if (flip) pz = side - 1 - pz;
                    sb.Append(vis[pz * side + px] != 0 ? '#' : '.');
                }
                Console.WriteLine(sb);
            }
        }
    }
    return;
}

if (cmd == "groundcrop")
{
    // groundcrop <level.rfa> x0 z0 x1 z1 <out.raw> : the terrain tiles over a rectangle at 4 samples a metre, RGB bytes, row 0 = south.
    var arc = new RefractorFlatArchive(args[1]);
    var tiles = arc.Entries.Where(e => { var n = Fwd(e.Name); return n.Contains("/Textures/tx", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) && n.Split('/').Length == 5; })
                           .Select(e => (e.Name, arc.Read(e))).ToList();
    var terrain = Encoding.Latin1.GetString(arc.Read(arc.Entries.First(e => Fwd(e.Name).EndsWith("/Init/Terrain.con", StringComparison.OrdinalIgnoreCase))));
    var cfg = RefractorForge.Formats.Terrain.TerrainConfig.Parse(terrain.Replace("\r\n", "\n").Split('\n'));
    var tt = TerrainTexture.FromTileBytes(tiles, cfg.WorldSize)!;
    float x0 = float.Parse(args[2]), z0 = float.Parse(args[3]), x1 = float.Parse(args[4]), z1 = float.Parse(args[5]);
    int w = (int)((x1 - x0) * 4), h = (int)((z1 - z0) * 4);
    var raw = new byte[w * h * 3];
    for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            var c = tt.Sample(x0 + (x + 0.5f) / 4f, z0 + (y + 0.5f) / 4f) * 255f;
            raw[(y * w + x) * 3] = (byte)c.X; raw[(y * w + x) * 3 + 1] = (byte)c.Y; raw[(y * w + x) * 3 + 2] = (byte)c.Z;
        }
    File.WriteAllBytes(args[6], raw);
    Console.WriteLine($"{w} x {h}");
    return;
}

if (cmd == "lsbtex")
{
    // lsbtex <level.rfa> : is ground under a SET shadow-map flag darker or brighter than ground under a clear one?
    // Retail ground textures have their shadows painted in, so the darker group is the one the level's makers
    // called shadow. Reported for each way the raster's rows could run.
    var arc = new RefractorFlatArchive(args[1]);
    var le = arc.Entries.FirstOrDefault(x => Fwd(x.Name).EndsWith("/LightmapShadowBits.lsb", StringComparison.OrdinalIgnoreCase));
    if (le is null) { Console.WriteLine("no shadow map"); return; }
    var lsb = RefractorForge.Formats.Terrain.LightmapShadowBits.Decode(arc.Read(le));
    int side = lsb.GridDim * lsb.TilePixels;
    // The raw flags, tile by tile, without going through the codec's idea of what a flag means.
    var flag = new bool[side * side];
    int empty = 0;
    for (int gy = 0; gy < lsb.GridDim; gy++)
        for (int gx = 0; gx < lsb.GridDim; gx++)
        {
            var tile = lsb.Tiles[gy * lsb.GridDim + gx];
            if (tile.IsEmpty) { empty++; continue; }
            for (int r = 0; r < tile.Rows.Length; r++)
            {
                int x = 0;
                foreach (ushort t in tile.Rows[r].Tokens)
                {
                    bool set = (t & 0x2000) != 0; int run = t & 0x1FFF;
                    for (int i = 0; i < run && x < lsb.TilePixels; i++, x++) flag[(gy * lsb.TilePixels + r) * side + gx * lsb.TilePixels + x] = set;
                }
            }
        }
    var tiles = arc.Entries.Where(e => { var n = Fwd(e.Name); return n.Contains("/Textures/tx", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) && n.Split('/').Length == 5; })
                           .Select(e => (e.Name, arc.Read(e))).ToList();
    var terrain = Encoding.Latin1.GetString(arc.Read(arc.Entries.First(e => Fwd(e.Name).EndsWith("/Init/Terrain.con", StringComparison.OrdinalIgnoreCase))));
    var cfg = RefractorForge.Formats.Terrain.TerrainConfig.Parse(terrain.Replace("\r\n", "\n").Split('\n'));
    var tt = TerrainTexture.FromTileBytes(tiles, cfg.WorldSize)!;
    var mat = arc.Entries.FirstOrDefault(e => Fwd(e.Name).EndsWith("/MaterialMap.raw", StringComparison.OrdinalIgnoreCase));
    var mm = mat is null ? null : arc.Read(mat);
    long setCount = flag.LongCount(f => f);
    Console.WriteLine($"{Path.GetFileName(args[1])}: grid {lsb.GridDim} x {lsb.TilePixels}, {empty} of {lsb.Tiles.Count} tiles empty, flag set on {100.0 * setCount / flag.Length:0.0}% of the map");
    foreach (bool flipX in new[] { false, true })
    foreach (bool flipZ in new[] { false, true })
    {
        double sSet = 0, sClear = 0; long nSet = 0, nClear = 0;
        const int step = 4;
        for (int pz = 0; pz < side; pz += step)
            for (int px = 0; px < side; px += step)
            {
                float wx = (px + 0.5f) / side * cfg.WorldSize, wz = (pz + 0.5f) / side * cfg.WorldSize;
                if (mm is not null && mm.Length == cfg.MaterialSize * cfg.MaterialSize
                    && mm[(int)(wz / cfg.WorldSize * cfg.MaterialSize) * cfg.MaterialSize + (int)(wx / cfg.WorldSize * cfg.MaterialSize)] == 7) continue;   // out of bounds: not lit by anyone
                var c = tt.Sample(wx, wz);
                float luma = 0.299f * c.X + 0.587f * c.Y + 0.114f * c.Z;
                bool f = flag[(flipZ ? side - 1 - pz : pz) * side + (flipX ? side - 1 - px : px)];
                if (f) { sSet += luma; nSet++; } else { sClear += luma; nClear++; }
            }
        Console.WriteLine($"   rows {(flipZ ? "north first" : "south first")}, columns {(flipX ? "east first" : "west first")}: in bounds {100.0 * nSet / Math.Max(1, nSet + nClear):0.0}% set; mean ground under SET {255 * sSet / Math.Max(1, nSet):0.0}, under CLEAR {255 * sClear / Math.Max(1, nClear):0.0}");
    }
    return;
}

LC.Manifest LoadManifest(string path) => JsonSerializer.Deserialize<LC.Manifest>(File.ReadAllText(path))!;

if (cmd == "variant")
{
    // variant <src.rfa> <newName> <destDir> [--drop <substring>]... [--take <entry suffix>=<other.rfa>]...
    // A renamed copy of a level for bisecting a load failure: entries whose path contains a --drop substring are left
    // out, and a --take entry is replaced by the one of that name from another archive of the SAME level (its paths
    // re-pointed to the new name). The source is never written to.
    string srcRfa = args[1], newName = args[2], destDir = args[3];
    Directory.CreateDirectory(destDir);
    var drops = new List<string>(); var takes = new List<(string Suffix, string From)>(); var strips = new List<(string Suffix, string Rx)>(); var prepends = new List<(string Suffix, string Line)>(); var crlfs = new List<string>(); var appends = new List<(string Suffix, string Line)>();   // --crlf <entry suffix> x: rewrite with CR LF line ends   // --strip <entry suffix>=<regex>: leave out the lines that match
    for (int i = 4; i + 1 < args.Length; i += 2)
    {
        if (args[i] == "--drop") drops.Add(args[i + 1]);
        else if (args[i] == "--prepend") { int eq = args[i + 1].IndexOf('='); prepends.Add((args[i + 1][..eq], args[i + 1][(eq + 1)..])); }
        else if (args[i] == "--append") { int eq = args[i + 1].IndexOf('='); appends.Add((args[i + 1][..eq], args[i + 1][(eq + 1)..])); }
        else if (args[i] == "--crlf") { crlfs.Add(args[i + 1]); }
        else if (args[i] == "--strip") { int eq = args[i + 1].IndexOf('='); strips.Add((args[i + 1][..eq], args[i + 1][(eq + 1)..])); }
        else if (args[i] == "--take") { int eq = args[i + 1].IndexOf('='); takes.Add((args[i + 1][..eq], args[i + 1][(eq + 1)..])); }
    }
    var res = RefractorForge.Formats.LevelCopy.CopyArchive(srcRfa, newName, destDir);
    var arc = new RefractorFlatArchive(res.Destination);
    var repl = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    foreach (var (suffix, from) in takes)
    {
        // "<other.rfa>::<suffix there>" takes a differently named entry (another level's file).
        string otherSuffix = suffix, fromPath = from;
        int sep = from.IndexOf("::", StringComparison.Ordinal);
        if (sep > 0) { fromPath = from[..sep]; otherSuffix = from[(sep + 2)..]; }
        var other = new RefractorFlatArchive(fromPath);
        var oe = other.Entries.First(e => Fwd(e.Name).EndsWith("/" + otherSuffix, StringComparison.OrdinalIgnoreCase));
        var mine = arc.Entries.First(e => Fwd(e.Name).EndsWith("/" + suffix, StringComparison.OrdinalIgnoreCase));
        repl[Fwd(mine.Name)] = RefractorForge.Formats.LevelCopy.Retarget(other.Read(oe), new[] { res.OldName }, newName).Bytes;
    }
    foreach (var (suffix, rxs) in strips)
    {
        var mine = arc.Entries.First(e => Fwd(e.Name).EndsWith("/" + suffix, StringComparison.OrdinalIgnoreCase));
        var rx = new System.Text.RegularExpressions.Regex(rxs, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var lines = Encoding.Latin1.GetString(arc.Read(mine)).Split((char)10);
        int before = lines.Length;
        lines = lines.Where(l => !rx.IsMatch(l)).ToArray();
        Console.WriteLine($"  {suffix}: {before - lines.Length} line(s) stripped");
        repl[Fwd(mine.Name)] = Encoding.Latin1.GetBytes(string.Join((char)10, lines));
    }
    foreach (var (suffix, line) in prepends)
    {
        var mine = arc.Entries.First(e => Fwd(e.Name).EndsWith("/" + suffix, StringComparison.OrdinalIgnoreCase));
        var cur = repl.TryGetValue(Fwd(mine.Name), out var had) ? had : arc.Read(mine);
        repl[Fwd(mine.Name)] = Encoding.Latin1.GetBytes(line + "\r\n").Concat(cur).ToArray();
    }
    foreach (var (suffix, line) in appends)
    {
        var mine = arc.Entries.First(e => Fwd(e.Name).EndsWith("/" + suffix, StringComparison.OrdinalIgnoreCase));
        var cur = repl.TryGetValue(Fwd(mine.Name), out var had) ? had : arc.Read(mine);
        repl[Fwd(mine.Name)] = cur.Concat(new byte[] { 13, 10 }).Concat(Encoding.Latin1.GetBytes(line)).Concat(new byte[] { 13, 10 }).ToArray();
    }
    foreach (var suffix in crlfs)
    {
        var mine = arc.Entries.First(e => Fwd(e.Name).EndsWith("/" + suffix, StringComparison.OrdinalIgnoreCase));
        var cur = repl.TryGetValue(Fwd(mine.Name), out var had) ? had : arc.Read(mine);
        var text = Encoding.Latin1.GetString(cur).Replace("\r\n", "\n").Replace("\n", "\r\n");
        Console.WriteLine($"  {suffix}: {cur.Count(b => b == 10)} line(s), {cur.Count(b => b == 13)} had CR");
        repl[Fwd(mine.Name)] = Encoding.Latin1.GetBytes(text);
    }
    int dropped = 0;
    string tmp = res.Destination + ".tmp";
    File.Copy(res.Destination, tmp, true);
    var tmpArc = new RefractorFlatArchive(tmp);
    RefractorFlatArchive.RepackToFile(res.Destination, tmpArc, repl,
        n => { bool d = drops.Any(x => Fwd(n).Contains(x, StringComparison.OrdinalIgnoreCase)); if (d) dropped++; return d; });
    File.Delete(tmp);
    Console.WriteLine($"{newName}: {new RefractorFlatArchive(res.Destination).Entries.Count} entries ({dropped} dropped, {repl.Count} taken from elsewhere) -> {res.Destination}");
    return;
}

if (cmd == "vktrace")
{
    // vktrace <ArchivesDir> <levelName> <outDir> [eyeX eyeY eyeZ targetX targetY targetZ] : the Vulkan traced view on a
    // real level, headless. Checks the card's kernel against the C# reference on a small view, times frames at
    // several sizes, and writes the picture. Runs only on a discrete card with hardware ray tracing.
    string arcDir = args[1], lvl = args[2], outD = args[3];
    Directory.CreateDirectory(outD);
    bool Is(string f, string stem) { var n = Path.GetFileNameWithoutExtension(f); return n.Equals(stem, StringComparison.OrdinalIgnoreCase) || (n.StartsWith(stem + "_", StringComparison.OrdinalIgnoreCase) && n[(stem.Length + 1)..].All(char.IsDigit)); }
    string game = Directory.Exists(Path.Combine(arcDir, "bf1942", "levels")) ? "bf1942" : "BfVietnam";
    var levelRfas = Directory.GetFiles(Path.Combine(arcDir, game, "levels"), "*.rfa").Where(f => Is(f, lvl)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
    var top = Directory.GetFiles(arcDir, "*.rfa").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
    var meshA = top.Where(f => Is(f, "standardMesh") || Is(f, "Objects") || Is(f, "treeMesh")).Concat(levelRfas).ToArray();
    var texA = top.Where(f => Is(f, "texture")).Concat(levelRfas).ToArray();
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var loaded = LevelArchive.FromRfa(levelRfas);
    var lib = MeshLibrary.Open(meshA);
    lib.AttachTextures(TextureLibrary.Open(texA));
    var envS = loaded.Environment;
    var toSun = envS is null ? Vector3.Normalize(new Vector3(-0.5f, 0.8f, -0.35f)) : Vector3.Normalize(new Vector3(envS.SunDirection.X, envS.SunDirection.Y, envS.SunDirection.Z));
    var day = new Daylight { ToSun = toSun }.Build();
    var built = LightScene.Build(loaded.Heightmap, loaded.Config, null, loaded.StaticObjects, lib);
    var scene = LiveTraceScene.From(built) ?? throw new InvalidOperationException("no geometry");
    Console.WriteLine($"{lvl}: {scene.TriCount:N0} triangles ({scene.TerrainTris:N0} terrain), scene built in {sw.Elapsed.TotalSeconds:0.0} s");

    float F(int i, float d) => args.Length > i ? float.Parse(args[i], System.Globalization.CultureInfo.InvariantCulture) : d;
    float ws = loaded.Config.WorldSize;
    var eye = new Vector3(F(4, ws * 0.5f), F(5, 0f), F(6, ws * 0.5f - 60f));
    var target = new Vector3(F(7, ws * 0.5f), F(8, 0f), F(9, ws * 0.5f));
    if (args.Length <= 5)
    {
        // No view given: stand 25 m above the ground near the first object and look at it.
        var o0 = loaded.StaticObjects.Objects.Count > 0 ? loaded.StaticObjects.Objects[loaded.StaticObjects.Objects.Count / 2].Position : new RefractorForge.Formats.Geometry.Vec3(ws / 2, 40, ws / 2);
        target = new Vector3(o0.X, o0.Y + 3f, o0.Z);
        eye = target + new Vector3(-45f, 22f, -45f);
    }
    Matrix4x4 ViewProj(int w, int h)
    {
        var view = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(60f * MathF.PI / 180f, w / (float)h, 0.3f, ws * 3f);
        return view * proj;
    }
    LiveTraceKernel.Params P(int w, int h) => LiveTraceKernel.Params.From(day, null, ViewProj(w, h), eye, w, h, null, ws, bounces: 2);

    using var vk = new RefractorForge.VkTrace.VulkanTracer();
    if (!vk.Available) { Console.WriteLine("NOT AVAILABLE: " + vk.Status); return; }
    Console.WriteLine("card: " + vk.DeviceName);
    sw.Restart();
    vk.SetScene(scene, null);
    Console.WriteLine($"scene on the card (acceleration structure built) in {sw.Elapsed.TotalMilliseconds:0} ms");

    // ---- 1. the card against the C# kernel ----
    {
        int w = 96, h = 64;
        var p = P(w, h);
        vk.Reset();
        vk.Trace(p, 1);
        var acc = vk.ReadAccum();
        int surface = 0, hitMismatch = 0, over = 0; double sum = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var refS = LiveTraceKernel.Shade(scene, p, x, y, 0u, null);
                int o = (y * w + x) * 4;
                var gpu = new Vector3(acc[o], acc[o + 1], acc[o + 2]);
                bool refHit = refS.Distance >= 0f;
                if (!refHit) { if (gpu.LengthSquared() > 1e-6f) hitMismatch++; continue; }
                surface++;
                var a = LiveTraceKernel.ToDisplay(p, refS.Light); var b = LiveTraceKernel.ToDisplay(p, gpu);
                float d = MathF.Max(MathF.Abs(a.X - b.X), MathF.Max(MathF.Abs(a.Y - b.Y), MathF.Abs(a.Z - b.Z))) * 255f;
                sum += d; if (d > 24f) over++;
            }
        Console.WriteLine($"self-test {w}x{h}: {surface} surface pixels, {hitMismatch} sky/surface mismatches, mean difference {sum / Math.Max(surface, 1):0.00} of 255, {over * 100.0 / Math.Max(surface, 1):0.0}% over 24");
    }

    // ---- 2. timings ----
    foreach (var (w, h) in new[] { (960, 556), (1920, 1112), (3840, 2224) })
    {
        var p = P(w, h);
        vk.Reset();
        vk.Trace(p, 1);                                    // the first frame pays for the new targets
        double total = 0; const int frames = 20;
        for (int i = 0; i < frames; i++) { vk.Trace(p, 1); total += vk.LastGpuMs; }
        double four = 0;
        for (int i = 0; i < 5; i++) { vk.Trace(p, 4); four += vk.LastGpuMs; }
        Console.WriteLine($"{w} x {h}: {total / frames:0.0} ms a frame at 1 sample ({1000.0 / (total / frames):0} fps), {four / 5:0.0} ms at 4 samples");
    }

    // ---- 3. the picture ----
    unsafe
    {
        int w = 1920, h = 1112;
        var p = P(w, h);
        vk.Reset();
        RefractorForge.VkTrace.VulkanTracer.Frame fr = default;
        for (int i = 0; i < 16; i++) fr = vk.Trace(p, 4);
        var rgba = new byte[w * h * 4];
        Half* light = (Half*)fr.Light; byte* alb = (byte*)fr.Albedo;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int s = (y * w + x) * 4, d = ((h - 1 - y) * w + x) * 4;      // row 0 is the bottom row
                float dist = (float)light[s + 3];
                for (int c = 0; c < 3; c++)
                {
                    float v = dist < 0f ? new[] { 0.55f, 0.68f, 0.9f }[c] : (alb[s + c] / 255f) * (float)light[s + c];
                    rgba[d + c] = (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);
                }
                rgba[d + 3] = 255;
            }
        string png = Path.Combine(outD, lvl + "_vktrace.png");
        SavePng(png, rgba, w, h);
        Console.WriteLine($"picture ({vk.Samples} samples) -> {png}");
    }
    return;
}

if (cmd == "bake42")
{
    // bake42 <ArchivesDir> <levelName> <outDir> [maxMaps] : the Battlefield 1942 traced bake on a real level, without
    // the editor. Bakes the first lightmap-carrying objects, writes each map as the game will SHOW it (x 2) beside
    // the retail map for the same object read the same way, and prints the colours the level would be given.
    string arcDir = args[1], lvl = args[2], outD = args[3];
    int max = args.Length > 4 ? int.Parse(args[4]) : 6;
    Directory.CreateDirectory(outD);
    bool Is(string f, string stem) { var n = Path.GetFileNameWithoutExtension(f); return n.Equals(stem, StringComparison.OrdinalIgnoreCase) || (n.StartsWith(stem + "_", StringComparison.OrdinalIgnoreCase) && n[(stem.Length + 1)..].All(char.IsDigit)); }
    var levelRfas = Directory.GetFiles(Path.Combine(arcDir, "bf1942", "levels"), "*.rfa").Where(f => Is(f, lvl)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
    var top = Directory.GetFiles(arcDir, "*.rfa").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
    var meshA = top.Where(f => Is(f, "standardMesh") || Is(f, "Objects") || Is(f, "treeMesh")).Concat(levelRfas).ToArray();
    var texA = top.Where(f => Is(f, "texture")).Concat(levelRfas).ToArray();
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var loaded = LevelArchive.FromRfa(levelRfas);
    var lib = MeshLibrary.Open(meshA);
    lib.AttachTextures(TextureLibrary.Open(texA));
    var envS = loaded.Environment;
    var toSun = envS is null ? Vector3.Normalize(new Vector3(-0.5f, 0.8f, -0.35f)) : Vector3.Normalize(new Vector3(envS.SunDirection.X, envS.SunDirection.Y, envS.SunDirection.Z));
    var day = new Daylight { ToSun = toSun }.Build();
    var built = LightScene.Build(loaded.Heightmap, loaded.Config, null, loaded.StaticObjects, lib);
    Console.WriteLine($"{lvl}: {levelRfas.Length} archive(s), {loaded.StaticObjects.Objects.Count} static object(s); scene of {built.Stats.TerrainTriangles:N0} terrain + {built.Stats.ObjectTriangles:N0} object triangle(s) in {sw.Elapsed.TotalSeconds:0.0} s; sun toward {toSun.X:0.00}/{toSun.Y:0.00}/{toSun.Z:0.00}");
    var (fd, fa) = day.FitBf1942();
    Console.WriteLine($"for objects with no lightmap: diffuse {fd.X:0.00}/{fd.Y:0.00}/{fd.Z:0.00}, ambient + global ambient {fa.X:0.00}/{fa.Y:0.00}/{fa.Z:0.00}"
                      + (envS is null ? "" : $"   (the level ships diffuse {envS.DiffuseColor.X:0.00}/{envS.DiffuseColor.Y:0.00}/{envS.DiffuseColor.Z:0.00}, ambient {envS.AmbientColor.X:0.00}/{envS.AmbientColor.Y:0.00}/{envS.AmbientColor.Z:0.00} + global {envS.GlobalAmbientColor.X:0.00}/{envS.GlobalAmbientColor.Y:0.00}/{envS.GlobalAmbientColor.Z:0.00})"));
    var retail = ObjectLightmaps.FromRfaPaths(levelRfas, throughLevelPalette: true);
    var ph = new ObjectLightmapBaker.Physical(built.Scene!, day, default, new LightTracer.Budget(8, 32, 2)) { Bf1942 = true };
    int withUv = 0, without = 0, done = 0;
    static (double R, double G, double B) Shown(Texture2D t)
    {
        double r = 0, g = 0, b = 0; int n = t.Width * t.Height;
        for (int i = 0; i < n; i++) { r += Math.Min(255, 2 * t.Rgba[i * 4]); g += Math.Min(255, 2 * t.Rgba[i * 4 + 1]); b += Math.Min(255, 2 * t.Rgba[i * 4 + 2]); }
        return (r / n, g / n, b / n);
    }
    static byte[] Doubled(Texture2D t)
    {
        var o = (byte[])t.Rgba.Clone();
        for (int i = 0; i < o.Length; i++) if (i % 4 != 3) o[i] = (byte)Math.Min(255, 2 * o[i]);
        return o;
    }
    foreach (var o in loaded.StaticObjects.Objects)
    {
        MeshLibrary.Mesh? mesh = null; string? meshName = null;
        foreach (var mn in lib.LodGeometryNames(o.Template))
            if (lib.TryGet(mn, out var m) && m.LightmapUvs is not null) { mesh = m; meshName = mn; break; }
        if (mesh is null) { without++; continue; }
        withUv++;
        if (done >= max) continue;
        sw.Restart();
        var pm = ObjectLightmapBaker.BakePhysical(mesh, LevelScene.MeshWorld(o), 128, 2, ph);
        if (pm is null) { Console.WriteLine($"  {meshName}: no unwrap in its lightmap channel"); continue; }
        string stem = $"{ObjectLightmaps.FileBase(meshName!)}_{(int)o.Position.X}-{(int)o.Position.Y}-{(int)o.Position.Z}";
        SavePng(Path.Combine(outD, stem + "_traced_x2.png"), Doubled(pm.Map), pm.Map.Width, pm.Map.Height);
        var s2 = Shown(pm.Map);
        string line = $"  {stem}: traced shows {s2.R:0}/{s2.G:0}/{s2.B:0} mean, {pm.AtCeiling:P0} clipped, {sw.Elapsed.TotalSeconds:0.0} s";
        if (retail.Match(o.Template, o.Position.X, o.Position.Y, o.Position.Z) is { } rt)
        {
            SavePng(Path.Combine(outD, stem + "_retail_x2.png"), Doubled(rt), rt.Width, rt.Height);
            var r2 = Shown(rt);
            line += $"; retail shows {r2.R:0}/{r2.G:0}/{r2.B:0}";
        }
        Console.WriteLine(line);
        done++;
    }
    Console.WriteLine($"{withUv} object(s) carry a lightmap unwrap, {without} do not (those are lit by the level's sun and ambient).");
    return;
}

if (cmd == "synth")
{
    var m = LoadManifest(args[1]);
    int w = args.Length > 3 ? int.Parse(args[3]) : 1920, h = args.Length > 4 ? int.Parse(args[4]) : 1080;
    float fov = args.Length > 5 ? float.Parse(args[5]) : 55f, pitch = args.Length > 6 ? float.Parse(args[6]) : 6f, back = args.Length > 7 ? float.Parse(args[7]) : 0f;
    SavePng(args[2], LC.RenderExpected(m, w, h, fov, pitch, back: back), w, h);
    Console.WriteLine($"wrote {args[2]}");
    return;
}

if (cmd == "read")
{
    var (rgba, w, h) = LoadImage(args[1]);
    var m = LoadManifest(args[2]);
    int thr = 28; string? outPng = null; bool mirror = args.Contains("--mirror");
    // --board A:x,y,x,y,x,y,x,y  gives a board's corners by hand (top-left, top-right, bottom-right, bottom-left)
    // for a plate the finder could not separate - one standing in front of black ground, say.
    var given = new Dictionary<string, Vector2[]>(StringComparer.OrdinalIgnoreCase);
    for (int i = 3; i < args.Length; i++)
    {
        if (args[i] == "--threshold") thr = int.Parse(args[++i]);
        else if (args[i] == "--out") outPng = args[++i];
        else if (args[i] == "--board")
        {
            var parts = args[++i].Split(':');
            var n = parts[1].Split(',').Select(float.Parse).ToArray();
            given[parts[0]] = new[] { new Vector2(n[0], n[1]), new Vector2(n[2], n[3]), new Vector2(n[4], n[5]), new Vector2(n[6], n[7]) };
        }
    }
    var quads = LC.FindBlackQuads(rgba, w, h, thr);
    Console.WriteLine($"{w} x {h}; {quads.Count} black plate(s) found");
    var cells = LC.Cells(m.ToSunVector);
    var toSun = m.ToSunVector; var predictedBack = LC.PredictedBack(toSun);

    // Tell the plates from anything else that is black in the picture (HUD frames, a gun barrel): a board is about
    // 1.8 times as wide as tall and has lit patches on it; the ground mat is a wide strip of five swatches with
    // black between them.
    static float Aspect(LC.Quad q) => (Vector2.Distance(q.Corners[0], q.Corners[1]) + Vector2.Distance(q.Corners[3], q.Corners[2]))
                                    / MathF.Max(1f, Vector2.Distance(q.Corners[0], q.Corners[3]) + Vector2.Distance(q.Corners[1], q.Corners[2]));
    static float Peak(Vector3 v) => float.IsNaN(v.X) ? 0f : MathF.Max(v.X, MathF.Max(v.Y, v.Z));
    LC.Quad? mat = null;
    var boardQuads = new List<LC.Quad>();
    foreach (var q in quads)
    {
        float aspect = Aspect(q);
        if (aspect is >= 1.3f and <= 2.6f && LC.IsBoard(rgba, w, h, q.Corners, mirror)) { boardQuads.Add(q); continue; }
        // The mat is whatever shape the camera's height and pitch make it: test it by what is on it, not by its outline.
        if (m.Swatches.Count > 0)
        {
            int lit = Enumerable.Range(0, m.Swatches.Count).Count(i => Peak(LC.SampleAt(rgba, w, h, q.Corners, LC.GroundSwatchCentre(i), 0.03f, 0.12f)) > 0.12f);
            int dark = Enumerable.Range(0, m.Swatches.Count - 1).Count(i => Peak(LC.SampleAt(rgba, w, h, q.Corners, LC.GroundGapCentre(i), 0.006f, 0.12f)) < 0.12f);
            if (lit >= m.Swatches.Count - 1 && dark >= m.Swatches.Count - 2 && (mat is null || q.Area > mat.Area)) { mat = q; continue; }
        }
        Console.WriteLine($"  ignoring a black shape at {q.Centre.X:0},{q.Centre.Y:0} (width/height {aspect:0.00})");
    }
    // Rows: split at the largest gap in centre height when there are two rows in the level.
    bool twoRows = m.Boards.Select(b => b.Y).Distinct().Count() > 1;
    var ordered = boardQuads.OrderBy(q => q.Centre.Y).ToList();
    var top = new List<LC.Quad>(); var bottom = new List<LC.Quad>(ordered);
    if (twoRows && ordered.Count > 1)
    {
        int cut = 1; float gap = -1;
        for (int i = 1; i < ordered.Count; i++) { float g = ordered[i].Centre.Y - ordered[i - 1].Centre.Y; if (g > gap) { gap = g; cut = i; } }
        top = ordered.Take(cut).ToList(); bottom = ordered.Skip(cut).ToList();
    }
    var lowRow = m.Boards.Where(b => b.Y == m.Boards.Min(x => x.Y)).OrderBy(b => b.X).ToList();
    var highRow = m.Boards.Where(b => b.Y != m.Boards.Min(x => x.Y)).OrderBy(b => b.X).ToList();
    var pairs = new List<(LC.Board Board, LC.Quad Quad)>();
    void Pair(List<LC.Board> row, List<LC.Quad> found, string which)
    {
        foreach (var b in row.Where(b => given.ContainsKey(b.Id))) pairs.Add((b, new LC.Quad(given[b.Id], 0)));
        row = row.Where(b => !given.ContainsKey(b.Id)).ToList();
        // A plate found by the finder that a hand-given board already covers is that board, not another one.
        found = found.Where(q => !given.Values.Any(c => Vector2.Distance((c[0] + c[1] + c[2] + c[3]) * 0.25f, q.Centre) < 40f)).ToList();
        var f = found.OrderBy(q => q.Centre.X).ToList();
        if (f.Count != row.Count) Console.WriteLine($"  WARNING: {which} row has {f.Count} plate(s) in the picture, the level has {row.Count} - pairing left to right");
        for (int i = 0; i < Math.Min(f.Count, row.Count); i++) pairs.Add((row[i], f[i]));
    }
    Pair(lowRow, bottom, "bottom"); Pair(highRow, top, "top");

    var overlay = (byte[])rgba.Clone();
    void Dot(Vector2 p, byte r, byte g, byte b, int rad = 2)
    {
        for (int dy = -rad; dy <= rad; dy++)
            for (int dx = -rad; dx <= rad; dx++)
            {
                int x = (int)p.X + dx, y = (int)p.Y + dy;
                if (x < 0 || y < 0 || x >= w || y >= h) continue;
                int o = (y * w + x) * 4; overlay[o] = r; overlay[o + 1] = g; overlay[o + 2] = b;
            }
    }
    void Line(Vector2 a, Vector2 b2, byte r, byte g, byte b)
    {
        int n = (int)Vector2.Distance(a, b2) + 1;
        for (int i = 0; i <= n; i++) Dot(Vector2.Lerp(a, b2, i / (float)n), r, g, b, 0);
    }
    string Rgb(Vector3 v) => float.IsNaN(v.X) ? "  -   -   -" : $"{v.X * 255,3:0} {v.Y * 255,3:0} {v.Z * 255,3:0}";
    float AngleDeg(Vector3 a, Vector3 b) => MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1f, 1f)) * 180f / MathF.PI;
    string Vec(Vector3 v) => $"{v.X,6:0.000}/{v.Y,6:0.000}/{v.Z,6:0.000}";

    var dump = new Dictionary<string, object>();
    foreach (var (board, quad) in pairs.OrderBy(p => p.Board.Id))
    {
        bool flip = mirror ^ (board.Yaw == 180);          // a board turned half round shows its back: columns right to left
        var got = LC.SampleBoard(rgba, w, h, quad.Corners, flip);
        for (int i = 0; i < 4; i++) Line(quad.Corners[i], quad.Corners[(i + 1) % 4], 255, 255, 0);
        foreach (var c in cells) { var at = LC.CellCentre(c.Column, c.Row); if (flip) at.X = 1 - at.X; Dot(LC.Project(quad.Corners, at.X, at.Y), 255, 0, 255); }
        Console.WriteLine();
        Console.WriteLine($"=== Board {board.Id}: {board.What}  [{board.Layout}; lightmap {board.Lightmap}]");
        var mark = got[0];
        Console.WriteLine($"    corners {string.Join("  ", quad.Corners.Select(c => $"{c.X:0},{c.Y:0}"))}   mark cell {Rgb(mark)} {(MathF.Max(mark.X, MathF.Max(mark.Y, mark.Z)) < 0.16f ? "(dark: right way round)" : "(NOT dark - wrong way round, or not this board)")}");
        Console.WriteLine("    measured R G B per cell:");
        for (int r = 0; r < LC.Rows; r++)
            Console.WriteLine("      " + string.Join(" | ", Enumerable.Range(0, LC.Columns).Select(c => Rgb(got[r * LC.Columns + c]))));
        dump[board.Id] = got.Select(v => new[] { v.X, v.Y, v.Z }).ToArray();

        // The direction fan: everything at lightmap level 2 on the full material.
        var fan = cells.Where(c => !c.Empty && !c.HalfMaterial && c.Level == 2).ToList();
        Vector3 V(LC.Cell c) => got[c.Row * LC.Columns + c.Column];
        float rMin = fan.Min(c => V(c).X), gMin = fan.Min(c => V(c).Y);
        var (sunDir, sunStr, sunRms) = LC.SolveLight(fan.Select(c => (c.Normal, V(c).X - rMin)).ToList());
        var (backDir, backStr, backRms) = LC.SolveLight(fan.Select(c => (c.Normal, V(c).Y - gMin)).ToList());
        Console.WriteLine($"    RED   light: toward {Vec(sunDir)}  strength {sunStr:0.000}  (fit rms {sunRms:0.000}); level's sun vector {Vec(toSun)} -> {(sunStr > 0 ? AngleDeg(sunDir, toSun).ToString("0.0") + " deg off" : "none seen")}");
        Console.WriteLine($"    GREEN light: toward {Vec(backDir)}  strength {backStr:0.000}  (fit rms {backRms:0.000}); predicted back    {Vec(predictedBack)} -> {(backStr > 0 ? AngleDeg(backDir, predictedBack).ToString("0.0") + " deg off" : "none seen")}");
        Console.WriteLine($"    unlit floor over the fan: R {rMin * 255:0}  G {gMin * 255:0};  BLUE over the fan: min {fan.Min(c => V(c).Z) * 255:0} max {fan.Max(c => V(c).Z) * 255:0} mean {fan.Average(c => V(c).Z) * 255:0.0}");

        LC.Cell At(string label) => cells.First(c => c.Label == label);
        Console.WriteLine("    ramps (lightmap 0, 64, 128, 191, 255):");
        Console.WriteLine("      facing the sun       R G B: " + string.Join(" | ", Enumerable.Range(0, 5).Select(k => Rgb(V(At($"to sun P{k}"))))));
        Console.WriteLine("      facing the back dir  R G B: " + string.Join(" | ", Enumerable.Range(0, 5).Select(k => Rgb(V(At($"to back P{k}"))))));
        Console.WriteLine("      facing down (0,128,255)   : " + string.Join(" | ", new[] { "down P0", "down P2", "down P4" }.Select(l => Rgb(V(At(l))))));
        Console.WriteLine("      facing up   (0,255)       : " + string.Join(" | ", new[] { "up P0", "up P4" }.Select(l => Rgb(V(At(l))))));
        Console.WriteLine($"    half-diffuse material (full -> half): to sun R {V(At("to sun P4")).X * 255:0} -> {V(At("half to sun P4")).X * 255:0};  to back G {V(At("to back P4")).Y * 255:0} -> {V(At("half to back P4")).Y * 255:0};  down B {V(At("down P4")).Z * 255:0} -> {V(At("half down P4")).Z * 255:0}");

        // Against the editor's model.
        double sq = 0; int n = 0; var worst = new List<(float Err, string Text)>();
        foreach (var c in cells)
        {
            if (c.Empty) continue;
            var want = LC.Predict(m, board, c); var have = V(c);
            if (float.IsNaN(have.X)) continue;
            var d = have - want; float e = MathF.Sqrt(Vector3.Dot(d, d) / 3f);
            sq += Vector3.Dot(d, d) / 3f; n++;
            worst.Add((e, $"{c.Label,-16} got {Rgb(have)}  model {Rgb(want)}"));
        }
        Console.WriteLine($"    editor's model: rms error {Math.Sqrt(sq / Math.Max(1, n)) * 255:0.0} of 255 over {n} cells; furthest:");
        foreach (var x in worst.OrderByDescending(x => x.Err).Take(5)) Console.WriteLine($"      {x.Text}");
    }

    if (mat is not null)
    {
        Console.WriteLine();
        Console.WriteLine("=== Ground chart (terrain tiles; the terrain shader has no live light)");
        for (int i = 0; i < 4; i++) Line(mat.Corners[i], mat.Corners[(i + 1) % 4], 0, 255, 255);
        var sw = new List<float[]>();
        for (int i = 0; i < m.Swatches.Count; i++)
        {
            var at = LC.GroundSwatchCentre(i);
            Dot(LC.Project(mat.Corners, at.X, at.Y), 255, 0, 255);
            var v = LC.SampleAt(rgba, w, h, mat.Corners, at, 0.03f, 0.12f);
            var s = m.Swatches[i];
            Console.WriteLine($"    tile {s.Tile[0] * 255,3:0} {s.Tile[1] * 255,3:0} {s.Tile[2] * 255,3:0}   on screen {Rgb(v)}   expected (tile x detail x 2) {s.Expected[0] * 255,3:0} {s.Expected[1] * 255,3:0} {s.Expected[2] * 255,3:0}");
            sw.Add(new[] { v.X, v.Y, v.Z });
        }
        dump["ground"] = sw;
    }
    else Console.WriteLine("\n(no ground chart found in the picture)");

    if (outPng is not null)
    {
        SavePng(outPng, overlay, w, h);
        File.WriteAllText(Path.ChangeExtension(outPng, ".json"), JsonSerializer.Serialize(dump, json));
        Console.WriteLine($"\nannotated picture: {outPng}");
    }
    return;
}
Console.WriteLine("unknown command");
