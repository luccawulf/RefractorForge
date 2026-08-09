using System.Text;
using RefractorForge.Formats;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Rfa;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Save-corruption hardening. Background: saved maps were reported corrupt in-game. The container/codec layer was
/// cleared by cross-validating MiniLZO against the clean-room <see cref="Lzo1x"/> decoder (itself validated against
/// retail archives with liblzo2 as oracle); the real hazard was workflow-level (saving into a base archive that
/// mounted _NNN patches keep overriding). These tests pin: codec cross-compatibility, the raw-entry fallback,
/// save-time block verification, patch-first path selection, SSM filtering, XPackId inheritance, editor-file
/// exclusion, and post-save validation.
/// </summary>
public class RfaSaveTests : IDisposable
{
    private readonly string _dir;

    public RfaSaveTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "rf_rfasave_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string P(string name) => Path.Combine(_dir, name);

    [Fact]
    public void MiniLzo_streams_decode_on_the_engine_validated_decoder()
    {
        // The referee decoder accepts exactly what the game accepts (validated against liblzo2 on retail data).
        // If MiniLZO ever emits a stream this decoder rejects, saved maps would corrupt in-game while still
        // round-tripping inside the editor — the worst failure mode there is.
        var rng = new Random(1942);
        foreach (int size in new[] { 0, 1, 3, 17, 255, 4096, 32767, 32768 })
            foreach (var mode in new[] { "random", "zeros", "text" })
            {
                var data = new byte[size];
                if (mode == "random") rng.NextBytes(data);
                else if (mode == "text") for (int i = 0; i < size; i++) data[i] = (byte)"object.absolutePosition 1024/35.5/1024\r\n"[i % 40];
                var comp = MiniLZO.MiniLZO.Compress(data);
                var back = Lzo1x.Decompress(comp, size);
                Assert.True(back.AsSpan().SequenceEqual(data), $"cross-decode mismatch ({mode}[{size}])");
            }
    }

    [Fact]
    public void Incompressible_entries_are_stored_raw_never_ambiguous()
    {
        // Random data doesn't compress; wrapping it would only add block headers. The writer must fall back to a
        // RAW entry (BlockSize == UncompressedSize — retail compressed archives contain 276 such entries, so the
        // engine provably accepts them). This also guarantees a wrapped region can never be confused for raw.
        var rng = new Random(7);
        var noise = new byte[70_000]; rng.NextBytes(noise);
        var path = P("raw.rfa");
        RefractorFlatArchive.WriteFile(path, new[] { ("a/noise.bin", noise) }, compress: true, xPackId: XPackId.Default);
        var a = new RefractorFlatArchive(path);
        var e = Assert.Single(a.Entries);
        Assert.Equal(e.UncompressedSize, e.BlockSize);          // stored raw
        Assert.True(a.Read(e).AsSpan().SequenceEqual(noise));   // and reads back identical
    }

    [Fact]
    public void Empty_entries_round_trip()
    {
        var path = P("empty.rfa");
        RefractorFlatArchive.WriteFile(path, new[] { ("a/empty.con", Array.Empty<byte>()), ("a/x.con", "rem x"u8.ToArray()) }, compress: true, xPackId: XPackId.Default);
        var a = new RefractorFlatArchive(path);
        Assert.Equal(2, a.Entries.Count);
        Assert.Empty(a.Read(a.Entries.First(x => x.Name.EndsWith("empty.con"))));
        Assert.Null(RefractorFlatArchive.Validate(path));
    }

    // ---- patch-first save ----------------------------------------------------------------------

    private string MakeBase(string name, XPackId xpack = XPackId.Default)
    {
        var so = "object.create house_m1\r\nobject.absolutePosition 10/20/30\r\n"u8.ToArray();
        var entries = new List<(string, byte[])>
        {
            ($"bf1942/levels/{name}/StaticObjects.con", so),
            ($"bf1942/levels/{name}/Init/Terrain.con", "GeometryTemplate.materialSize 64\r\nGeometryTemplate.worldSize 1024"u8.ToArray()),
            ($"bf1942/levels/{name}/Heightmap.raw", new byte[64 * 64 * 2]),
            ($"bf1942/levels/{name}/Textures/tx01x01.dds", new byte[256]),
            ($"bf1942/levels/{name}/Sound/amb.wav", new byte[128]),
        };
        var p = P(name + ".rfa");
        RefractorFlatArchive.WriteFile(p, entries, compress: true, xPackId: xpack);
        return p;
    }

    private static StaticObjectsFile EditedSo()
    {
        var so = new StaticObjectsFile();
        so.Objects.Add(new StaticObject("bunker_m1") { Id = "b1", Position = new RefractorForge.Formats.Geometry.Vec3(1, 2, 3) });
        return so;
    }

    [Fact]
    public void NextPatchPath_reuses_our_patch_but_never_foreign_ones()
    {
        var basePath = MakeBase("Alpha");
        // no patches yet -> _001
        Assert.EndsWith("Alpha_001.rfa", LevelSaver.NextPatchPath(basePath));

        // a FOREIGN _003 (not written by RefractorForge): overwrite the descriptor fingerprint
        var foreign = P("Alpha_003.rfa");
        File.Copy(basePath, foreign);
        using (var fs = new FileStream(foreign, FileMode.Open, FileAccess.ReadWrite))
        { fs.Seek(8, SeekOrigin.Begin); fs.Write("BattlecraftTool"u8); }
        Assert.False(RefractorFlatArchive.WasWrittenByRefractorForge(foreign));
        Assert.EndsWith("Alpha_004.rfa", LevelSaver.NextPatchPath(basePath));   // never touch the foreign patch

        // OUR patch at the top -> reuse it on every save (no _005 litter)
        var ours = P("Alpha_004.rfa");
        LevelSaver.WritePatchRfa(basePath, ours, EditedSo(), null, null, null);
        Assert.True(RefractorFlatArchive.WasWrittenByRefractorForge(ours));
        Assert.Equal(Path.GetFullPath(ours), Path.GetFullPath(LevelSaver.NextPatchPath(basePath)));
        // and asking from the PATCH path itself still resolves to the same target
        Assert.Equal(Path.GetFullPath(ours), Path.GetFullPath(LevelSaver.NextPatchPath(ours)));
    }

    [Fact]
    public void Ssm_patch_strips_client_only_content()
    {
        var basePath = MakeBase("Bravo");
        var extras = new List<(string Name, byte[] Bytes)> { ("tx01x01.dds", new byte[64]), ("amb.wav", new byte[32]) };

        var full = P("Bravo_full.rfa");
        LevelSaver.WritePatchRfa(basePath, full, EditedSo(), null, null, null, extraFiles: extras);
        var fa = new RefractorFlatArchive(full);
        Assert.Contains(fa.Entries, e => e.Name.EndsWith(".dds"));

        var ssm = P("Bravo_ssm.rfa");
        var names = LevelSaver.WritePatchRfa(basePath, ssm, EditedSo(), null, null, null, extraFiles: extras, serverSideOnly: true);
        var sa = new RefractorFlatArchive(ssm);
        Assert.DoesNotContain(sa.Entries, e => e.Name.EndsWith(".dds") || e.Name.EndsWith(".wav"));
        Assert.Contains(sa.Entries, e => e.Name.EndsWith("StaticObjects.con"));
        Assert.DoesNotContain(names, n => n.EndsWith(".dds", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Patch_inherits_the_base_archives_xpack_id()
    {
        var basePath = MakeBase("Charlie", XPackId.RoadToRome);
        var patch = P("Charlie_001.rfa");
        LevelSaver.WritePatchRfa(basePath, patch, EditedSo(), null, null, null);
        Assert.Equal(XPackId.RoadToRome, new RefractorFlatArchive(patch).XPackId);
    }

    [Fact]
    public void PackFolder_excludes_editor_only_files()
    {
        var dir = Path.Combine(_dir, "lvl");
        Directory.CreateDirectory(Path.Combine(dir, "Backups", "old"));
        File.WriteAllText(Path.Combine(dir, "StaticObjects.con"), "rem ok");
        File.WriteAllText(Path.Combine(dir, "map.rfproj"), "<RfProject/>");
        File.WriteAllText(Path.Combine(dir, "refractorforge.game"), "1942");
        File.WriteAllText(Path.Combine(dir, "Thumbs.db"), "x");
        File.WriteAllText(Path.Combine(dir, "Backups", "old", "StaticObjects.con"), "rem stale backup");

        var outRfa = P("packed.rfa");
        int n = LevelSaver.PackFolder(dir, outRfa, "bf1942/levels/lvl/");
        var a = new RefractorFlatArchive(outRfa);
        Assert.Equal(1, n);
        var e = Assert.Single(a.Entries);
        Assert.EndsWith("StaticObjects.con", e.Name);
        Assert.DoesNotContain("Backups", e.Name);
    }

    [Fact]
    public void Validate_passes_good_archives_and_catches_truncation()
    {
        var basePath = MakeBase("Delta");
        Assert.Null(RefractorFlatArchive.Validate(basePath));

        var broken = P("Delta_broken.rfa");
        var bytes = File.ReadAllBytes(basePath);
        File.WriteAllBytes(broken, bytes.AsSpan(0, bytes.Length - 40).ToArray());   // chop the tail (TOC/data)
        Assert.NotNull(RefractorFlatArchive.Validate(broken));
    }

    [Fact]
    public void Full_patch_cycle_survives_engine_validated_decode()
    {
        // End-to-end: base + patch, merged load has the edit, and every patch entry decodes with the referee.
        var basePath = MakeBase("Echo");
        var patch = LevelSaver.NextPatchPath(basePath);
        LevelSaver.WritePatchRfa(basePath, patch, EditedSo(), null, null, null);
        Assert.Null(RefractorFlatArchive.Validate(patch));

        var merged = RefractorForge.Render.LevelArchive.FromRfa(basePath, patch);
        Assert.Contains(merged.StaticObjects.Objects, o => o.Template == "bunker_m1");
    }
}
