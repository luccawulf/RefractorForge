using System.Security.Cryptography;
using System.Text;
using RefractorForge.Formats.Rfa;
using RefractorForge.Render;

// RFA tool / clean-room-codec verifier.
//   RfaTool <archive.rfa>                       decode-all stats
//   RfaTool <archive.rfa> list                  list entries
//   RfaTool <archive.rfa> grep <substr>         filter entries
//   RfaTool <archive.rfa> extract <name> <out>  extract one file
//   RfaTool <archive.rfa> verify <ref.tsv>      decode every entry and compare SHA-256 to the oracle manifest
//   RfaTool <archive.rfa> smverify <geom.tsv>   parse every .sm and compare geometry fields to the manifest
if (args.Length < 1)
{
    Console.WriteLine("usage: RfaTool <archive.rfa> [list | grep <substr> | extract <name> <out> | verify <ref.tsv>]");
    return 1;
}

var rfa = RfaArchive.Open(args[0]);
string mode = args.Length > 1 ? args[1] : "stats";

static string Sha256Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

switch (mode)
{
    case "list":
        foreach (var e in rfa.Entries) Console.WriteLine($"  {e.Name}  [{e.UncompressedSize}B]");
        break;

    case "grep" when args.Length > 2:
        foreach (var e in rfa.Entries)
            if (e.Name.Contains(args[2], StringComparison.OrdinalIgnoreCase))
                Console.WriteLine($"  {e.Name}  [{e.UncompressedSize}B]");
        break;

    case "extract" when args.Length > 3:
    {
        var e = rfa.Entries.FirstOrDefault(x => x.Name.Equals(args[2], StringComparison.OrdinalIgnoreCase))
                ?? rfa.Entries.FirstOrDefault(x => x.Name.EndsWith(args[2], StringComparison.OrdinalIgnoreCase));
        if (e is null) { Console.WriteLine($"not found: {args[2]}"); return 1; }
        File.WriteAllBytes(args[3], rfa.Read(e));
        Console.WriteLine($"extracted {e.Name} -> {args[3]} ({e.UncompressedSize} B)");
        break;
    }

    case "verify" when args.Length > 2:
    {
        // Reference manifest rows: index \t offset \t blockSize \t unc \t decoded_len \t sha256 \t name
        var refRows = File.ReadAllLines(args[2]);
        var byName = rfa.Entries.ToDictionary(e => e.Name, e => e);
        int match = 0, mismatch = 0, error = 0, missing = 0;
        var firstProblems = new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var line in refRows)
        {
            if (line.Length == 0) continue;
            var f = line.Split('\t');
            int expUnc = int.Parse(f[3]);
            string expSha = f[5];
            string name = f[6];
            if (!byName.TryGetValue(name, out var e)) { missing++; if (firstProblems.Count < 10) firstProblems.Add($"MISSING  {name}"); continue; }
            try
            {
                var data = rfa.Read(e);
                if (data.Length != expUnc) { mismatch++; if (firstProblems.Count < 10) firstProblems.Add($"LEN  {name}: {data.Length} != {expUnc}"); continue; }
                var sha = Sha256Hex(data);
                if (sha == expSha) match++;
                else { mismatch++; if (firstProblems.Count < 10) firstProblems.Add($"SHA  {name}: {sha[..12]}.. != {expSha[..12]}.."); }
            }
            catch (Exception ex) { error++; if (firstProblems.Count < 10) firstProblems.Add($"ERR  {name}: {ex.Message}"); }
        }
        sw.Stop();
        Console.WriteLine($"Archive : {args[0]}");
        Console.WriteLine($"Manifest: {args[2]}  ({refRows.Length} rows)");
        Console.WriteLine($"SHA-256 match : {match}/{refRows.Length}");
        Console.WriteLine($"mismatch={mismatch}  error={error}  missing={missing}   ({sw.ElapsedMilliseconds} ms)");
        if (firstProblems.Count > 0) { Console.WriteLine("first problems:"); foreach (var p in firstProblems) Console.WriteLine("  " + p); }
        bool perfect = match == refRows.Length && mismatch == 0 && error == 0 && missing == 0;
        Console.WriteLine(perfect
            ? ">>> 100% BYTE-EXACT vs liblzo2 oracle. Clean-room LZO1X verified."
            : ">>> NOT byte-exact yet.");
        return perfect ? 0 : 2;
    }

    case "smverify" when args.Length > 2:
    {
        // Geometry manifest rows: name \t version \t numLods \t nMat(LOD0) \t nVerts(LOD0) \t nFaces(LOD0) \t consumed \t total
        var refRows = File.ReadAllLines(args[2]);
        var byName = rfa.Entries.ToDictionary(e => e.Name, e => e);
        int match = 0, bad = 0, error = 0, missing = 0;
        var firstProblems = new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var line in refRows)
        {
            if (line.Length == 0) continue;
            var f = line.Split('\t');
            string name = f[0];
            uint eVer = uint.Parse(f[1]); int eLods = int.Parse(f[2]);
            int eMat = int.Parse(f[3]), eV = int.Parse(f[4]), eF = int.Parse(f[5]);
            int eConsumed = int.Parse(f[6]), eTotal = int.Parse(f[7]);
            if (!byName.TryGetValue(name, out var e)) { missing++; if (firstProblems.Count < 10) firstProblems.Add($"MISSING  {name}"); continue; }
            try
            {
                var sm = StandardMesh.Parse(rfa.Read(e));
                var (nm, nv, nf) = sm.Lod0Counts();
                bool ok = sm.Version == eVer && sm.NumLods == eLods && nm == eMat && nv == eV
                          && nf == eF && sm.Consumed == eConsumed && sm.Total == eTotal;
                if (ok) match++;
                else { bad++; if (firstProblems.Count < 10) firstProblems.Add(
                    $"FIELD {name}: v{sm.Version}/{eVer} lod{sm.NumLods}/{eLods} m{nm}/{eMat} v{nv}/{eV} f{nf}/{eF} c{sm.Consumed}/{eConsumed} t{sm.Total}/{eTotal}"); }
            }
            catch (Exception ex) { error++; if (firstProblems.Count < 10) firstProblems.Add($"ERR  {name}: {ex.Message}"); }
        }
        sw.Stop();
        Console.WriteLine($"Archive : {args[0]}");
        Console.WriteLine($"Geometry manifest: {args[2]}  ({refRows.Length} rows)");
        Console.WriteLine($".sm geometry match : {match}/{refRows.Length}");
        Console.WriteLine($"fieldmismatch={bad}  error={error}  missing={missing}   ({sw.ElapsedMilliseconds} ms)");
        if (firstProblems.Count > 0) { Console.WriteLine("first problems:"); foreach (var p in firstProblems) Console.WriteLine("  " + p); }
        bool perfect = match == refRows.Length && bad == 0 && error == 0 && missing == 0;
        Console.WriteLine(perfect
            ? ">>> 100% geometry parse parity (version, LODs, material/vertex/face counts, byte cursor)."
            : ">>> NOT full parity yet.");
        return perfect ? 0 : 2;
    }

    case "render" when args.Length > 3:
    {
        var e = rfa.Entries.FirstOrDefault(x => x.Name.Equals(args[2], StringComparison.OrdinalIgnoreCase))
                ?? rfa.Entries.FirstOrDefault(x => x.Name.EndsWith(args[2], StringComparison.OrdinalIgnoreCase))
                ?? rfa.Entries.FirstOrDefault(x => x.Name.Contains(args[2], StringComparison.OrdinalIgnoreCase));
        if (e is null) { Console.WriteLine($"not found: {args[2]}"); return 1; }
        if (!e.Name.EndsWith(".sm", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine($"not a .sm mesh: {e.Name}"); return 1; }

        var sm = RefractorForge.Formats.Rfa.StandardMesh.Parse(rfa.Read(e));
        if (sm.Lods.Count == 0 || sm.Lods[0].Count == 0) { Console.WriteLine("mesh has no LOD0 geometry"); return 1; }

        // Flatten LOD0 (all materials) into one triangle soup.
        // Optional debug filter: pass "mat=N" as any arg to render only material N of LOD0.
        int onlyMat = -1;
        foreach (var a in args) if (a.StartsWith("mat=") && int.TryParse(a.AsSpan(4), out var mm)) onlyMat = mm;
        var pos = new List<System.Numerics.Vector3>();
        var idx = new List<int>();
        int mi = -1;
        foreach (var m in sm.Lods[0])
        {
            mi++;
            if (onlyMat >= 0 && mi != onlyMat) continue;
            int @base = pos.Count;
            int vcount = m.Vertices.Length;
            foreach (var v in m.Vertices) pos.Add(new System.Numerics.Vector3(v.X, v.Y, v.Z));
            foreach (var (fa, fb, fc) in m.Faces)
            {
                // Triangle strips carry 0xFFFF restart sentinels and zero-area stitch triangles;
                // drop anything that isn't a real, in-range triangle before handing it to the rasterizer.
                if ((uint)fa >= (uint)vcount || (uint)fb >= (uint)vcount || (uint)fc >= (uint)vcount) continue;
                if (fa == fb || fb == fc || fa == fc) continue;
                idx.Add(@base + fa); idx.Add(@base + fb); idx.Add(@base + fc);
            }
        }
        var modelPos = pos.ToArray();
        var modelIdx = idx.ToArray();
        var (cnt, nvv, nff) = sm.Lod0Counts();
        Console.WriteLine($"{e.Name}: v{sm.Version}, LOD0 {cnt} material(s), {nvv} verts, {nff} tris");

        // Bounding box of the mesh.
        var lo = new System.Numerics.Vector3(float.MaxValue);
        var hi = new System.Numerics.Vector3(float.MinValue);
        foreach (var v in modelPos) { lo = System.Numerics.Vector3.Min(lo, v); hi = System.Numerics.Vector3.Max(hi, v); }
        var center = (lo + hi) * 0.5f;
        var size = hi - lo;
        float radius = MathF.Max(0.01f, size.Length() * 0.5f);

        int W = args.Length > 4 && int.TryParse(args[4], out var w) ? w : 1000;
        int H = args.Length > 5 && int.TryParse(args[5], out var h) ? h : 750;
        const int SS = 2;                       // supersampling factor (anti-aliasing)
        int RW = W * SS, RH = H * SS;

        // Orbit: look across the longest horizontal axis so long meshes span the screen width.
        float az = (size.Z >= size.X ? 120f : 210f) * MathF.PI / 180f;
        float el = 24f * MathF.PI / 180f;
        float fovY = MathF.PI / 3f;
        var dir = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(
            MathF.Cos(el) * MathF.Sin(az), MathF.Sin(el), MathF.Cos(el) * MathF.Cos(az)));

        // Auto-fit: start from the bounding sphere, then refine distance so the projected
        // 8 corners fill ~88% of the frame (works for compact and long-thin meshes alike).
        float dist = radius / MathF.Sin(fovY * 0.5f) * 1.2f;
        var corners = new System.Numerics.Vector3[8];
        for (int ci = 0; ci < 8; ci++)
            corners[ci] = new System.Numerics.Vector3(
                (ci & 1) == 0 ? lo.X : hi.X, (ci & 2) == 0 ? lo.Y : hi.Y, (ci & 4) == 0 ? lo.Z : hi.Z);
        Camera cam = default!;
        for (int iter = 0; iter < 4; iter++)
        {
            var camPos0 = center + dir * dist;
            var fwd0 = System.Numerics.Vector3.Normalize(center - camPos0);
            cam = new Camera
            {
                Position = camPos0,
                Pitch = MathF.Asin(Math.Clamp(fwd0.Y, -1f, 1f)),
                Yaw = MathF.Atan2(fwd0.X, fwd0.Z),
                FovY = fovY, Aspect = (float)RW / RH,
                Near = MathF.Max(0.05f, radius * 0.02f), Far = dist + radius * 8f,
            };
            var vp0 = cam.ViewProjection;
            float maxNdc = 0.01f;
            foreach (var cc in corners)
            {
                var pc = System.Numerics.Vector4.Transform(new System.Numerics.Vector4(cc, 1f), vp0);
                if (pc.W <= 1e-4f) { maxNdc = 1f; continue; }
                maxNdc = MathF.Max(maxNdc, MathF.Max(MathF.Abs(pc.X / pc.W), MathF.Abs(pc.Y / pc.W)));
            }
            dist *= maxNdc / 0.88f;             // scale to fit
        }

        var big = new ImageBuffer(RW, RH);
        big.ClearGradient(new System.Numerics.Vector3(0.22f, 0.24f, 0.28f),
                          new System.Numerics.Vector3(0.10f, 0.11f, 0.13f));
        var key = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(-0.45f, 0.8f, -0.4f));
        int cull = args.Length > 6 && int.TryParse(args[6], out var cu) ? cu : 0;
        SoftwareRenderer.DrawMeshSmooth(big, cam, key, modelPos, modelIdx, new System.Numerics.Vector3(0.82f, 0.83f, 0.86f), cull);
        var img = big.DownsampleBy(SS);
        img.SaveBmp(args[3]);
        Console.WriteLine($"rendered -> {args[3]} ({W}x{H}, {SS}x SSAA), bbox=({lo.X:F1},{lo.Y:F1},{lo.Z:F1})..({hi.X:F1},{hi.Y:F1},{hi.Z:F1}) r={radius:F1}m");
        break;
    }

    default: // stats
    {
        int ok = 0, fail = 0;
        var byExt = new Dictionary<string, int>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var e in rfa.Entries)
        {
            if (rfa.TryRead(e, out var b) && b.Length == e.UncompressedSize)
            {
                ok++;
                string ext = e.Name.Contains('.') ? e.Name[(e.Name.LastIndexOf('.') + 1)..].ToLowerInvariant() : "(none)";
                byExt[ext] = byExt.GetValueOrDefault(ext) + 1;
            }
            else fail++;
        }
        sw.Stop();
        Console.WriteLine($"Archive: {args[0]}");
        Console.WriteLine($"Entries: {rfa.Entries.Count}");
        Console.WriteLine($"Decoded (exact length): {ok}/{rfa.Entries.Count}   failed: {fail}   ({sw.ElapsedMilliseconds} ms)");
        Console.WriteLine("by extension: " + string.Join(", ", byExt.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")));
        break;
    }
}
return 0;
