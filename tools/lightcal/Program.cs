// lightcal build <base.rfa> <outDir> <name> [--safe | --unlit]  build a calibration level + <name>.calibration.json
// calibtool synth <manifest.json> <out.png> [w h fov pitch back] the picture the editor's model expects
// calibtool read  <screenshot> <manifest.json> [--out annotated.png] [--threshold n] [--mirror]
// calibtool check <level.rfa>                                    parse the level's new files back
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
    var m = LC.BuildLevel(new LC.Options(baseRfa, name, outDir, Full: !safe && !unlit, unlit ? LC.Site.HueUnlit : LC.Site.Hue) { Unlit = unlit });
    File.WriteAllText(Path.Combine(outDir, name + ".calibration.json"), JsonSerializer.Serialize(m, json));
    Console.WriteLine($"{dest}  {new FileInfo(dest).Length:N0} bytes");
    Console.WriteLine($"to sun {m.ToSun[0]:0.###}/{m.ToSun[1]:0.###}/{m.ToSun[2]:0.###}; viewer {m.Viewer[0]:0.#}/{m.Viewer[1]:0.#}/{m.Viewer[2]:0.#}");
    foreach (var b in m.Boards) Console.WriteLine($"  {b.Id}  {b.Mesh,-14} {b.Lightmap,-13} at {b.X}/{b.Y}/{b.Z}  {b.What}");
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
        var got = LC.SampleBoard(rgba, w, h, quad.Corners, mirror);
        for (int i = 0; i < 4; i++) Line(quad.Corners[i], quad.Corners[(i + 1) % 4], 255, 255, 0);
        foreach (var c in cells) { var at = LC.CellCentre(c.Column, c.Row); if (mirror) at.X = 1 - at.X; Dot(LC.Project(quad.Corners, at.X, at.Y), 255, 0, 255); }
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
