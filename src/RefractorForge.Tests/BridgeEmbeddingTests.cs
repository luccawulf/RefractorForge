using System.Text;
using RefractorBridge.Con;
using RefractorBridge.Level;
using RefractorBridge.Mesh;
using RefractorBridge.Oracle;
using RefractorForge.Formats.Rfa;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Gates on object embedding (RefractorBridge M3b) - lifting the objects a BF1942 level places out of a mod's
/// shared archives and shipping them inside the ported level.
///
/// Without this a ported level shows terrain and nothing else: vanilla Berlin keeps 1 of its 326 placements,
/// because every object it names lives in the mod's shared Objects.rfa. With it, 326 of 326.
/// </summary>
public class BridgeEmbeddingTests : IDisposable
{
    private readonly string _dir;

    public BridgeEmbeddingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"rbridge_embed_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static byte[] L(string s) => Encoding.Latin1.GetBytes(s.Replace("\n", "\r\n"));

    /// <summary>A miniature stand-in for a mod's shared object library.</summary>
    private AssetPool MakePool()
    {
        var entries = new List<(string, byte[])>
        {
            ("Objects/Props/Crate1/Geometries.con", L("""
                GeometryTemplate.create StandardMesh Crate1
                GeometryTemplate.file Crate1
                GeometryTemplate.billboard 1
                """)),
            ("Objects/Props/Crate1/Objects.con", L("""
                ObjectTemplate.create SimpleObject Crate1
                ObjectTemplate.geometry Crate1
                ObjectTemplate.addTemplate Crate1_Child
                """)),
            ("Objects/Props/Crate1Child/Objects.con", L("""
                ObjectTemplate.create SimpleObject Crate1_Child
                """)),
            ("StandardMesh/Crate1.sm", new byte[64]),
            ("StandardMesh/Crate1.rs", L("""
                subshader "Crate1_Material0" "StandardMesh/Default"
                {
                	texture "texture/CrateSkin";
                }
                """)),
            ("Texture/CrateSkin.dds", new byte[32]),
        };

        string path = Path.Combine(_dir, "pool.rfa");
        RefractorFlatArchive.WriteFile(path, entries, compress: true, XPackId.None);
        return AssetPool.Build(new[] { path });
    }

    private static PortedFile? Find(EmbedResult r, string suffix) =>
        r.Files.FirstOrDefault(f => f.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    // --- the pool ------------------------------------------------------------------------------------------

    [Fact]
    public void The_pool_indexes_templates_to_their_owning_folder()
    {
        var pool = MakePool();

        Assert.True(pool.DefinesTemplate("Crate1"));
        Assert.Equal("Objects/Props/Crate1", pool.FolderFor("Crate1"));
        Assert.Equal("Crate1", pool.GeometryFile("Crate1"));
        Assert.Equal("StandardMesh", pool.GeometryType("Crate1"));
        Assert.NotNull(pool.Mesh("Crate1"));
        Assert.NotNull(pool.Texture("CrateSkin"));
    }

    // --- the layout ----------------------------------------------------------------------------------------

    [Fact]
    public void Objects_are_emitted_in_the_folder_shape_the_engine_actually_loads()
    {
        // A flattened file of all templates does NOT load. One folder per object, chained, does.
        var r = ObjectEmbedder.Embed(new[] { "Crate1" }, MakePool(), "MyLevel");

        Assert.NotNull(Find(r, "Objects/Crate1/geometries.con"));
        Assert.NotNull(Find(r, "Objects/Crate1/objects.con"));

        string entry = Encoding.Latin1.GetString(Find(r, "Objects/Crate1/Crate1.con")!.Data);
        Assert.Contains("run geometries", entry, StringComparison.Ordinal);
        Assert.Contains("run objects", entry, StringComparison.Ordinal);

        string index = Encoding.Latin1.GetString(Find(r, "Objects/Objects.con")!.Data);
        Assert.Contains("run Crate1/Crate1", index, StringComparison.Ordinal);
    }

    [Fact]
    public void Geometry_is_retargeted_to_the_levels_own_mesh_folder()
    {
        // Source mods write bare names, folder-qualified paths, or ../standardMesh/. All but the last fail to
        // resolve in a flat level - and they fail on CONSTRUCT, not on load.
        var r = ObjectEmbedder.Embed(new[] { "Crate1" }, MakePool(), "MyLevel");
        string geo = Encoding.Latin1.GetString(Find(r, "Objects/Crate1/geometries.con")!.Data);

        Assert.Contains("GeometryTemplate.file ../standardMesh/Crate1", geo, StringComparison.Ordinal);
        Assert.DoesNotContain("billboard", geo, StringComparison.OrdinalIgnoreCase);   // BF1942-only
        Assert.NotNull(Find(r, "StandardMesh/Crate1.sm"));
    }

    [Fact]
    public void The_material_and_the_textures_it_names_come_with_the_mesh()
    {
        var r = ObjectEmbedder.Embed(new[] { "Crate1" }, MakePool(), "MyLevel");

        Assert.NotNull(Find(r, "StandardMesh/Crate1.rs"));
        Assert.NotNull(Find(r, "Texture/CrateSkin.dds"));
        Assert.Equal(1, r.TextureCount);
    }

    [Fact]
    public void Referenced_child_templates_are_pulled_in_transitively()
    {
        var r = ObjectEmbedder.Embed(new[] { "Crate1" }, MakePool(), "MyLevel");

        Assert.Contains("Crate1_Child", r.EmbeddedTemplates);
        Assert.NotNull(Find(r, "Objects/Crate1Child/objects.con"));
    }

    [Fact]
    public void Closure_stops_at_the_exclusion_list()
    {
        // Following every reference is correct in principle and ruinous in practice - on the hand port it
        // dragged in an AC-130 and 27 MB the map never spawns.
        var r = ObjectEmbedder.Embed(new[] { "Crate1" }, MakePool(), "MyLevel",
            new EmbedOptions { Exclude = new HashSet<string>(new[] { "Crate1_Child" }, StringComparer.OrdinalIgnoreCase) });

        Assert.DoesNotContain("Crate1_Child", r.EmbeddedTemplates);
    }

    [Fact]
    public void A_name_retail_vietnam_already_owns_is_left_to_retail()
    {
        // A level's scripts load AFTER stock, so defining a name stock uses REPLACES stock's version for the
        // whole game rather than making a private copy.
        var census = StockCensus.Build(new[]
        {
            new ConFile("stock.con", L("ObjectTemplate.create SimpleObject Crate1\n")),
        });

        var r = ObjectEmbedder.Embed(new[] { "Crate1" }, MakePool(), "MyLevel", new EmbedOptions { Census = census });

        Assert.Contains("Crate1", r.SkippedStockNames);
        Assert.Null(Find(r, "Objects/Crate1/objects.con"));
    }

    [Fact]
    public void A_template_the_pool_does_not_have_is_reported_missing()
    {
        var r = ObjectEmbedder.Embed(new[] { "NoSuchThing" }, MakePool(), "MyLevel");
        Assert.Contains(r.Missing, m => m.Contains("NoSuchThing", StringComparison.Ordinal));
    }

    [Fact]
    public void No_two_emitted_files_share_a_destination()
    {
        // The same folder can be indexed from a mod archive and its patch; both would land on one name.
        var r = ObjectEmbedder.Embed(new[] { "Crate1" }, MakePool(), "MyLevel");
        Assert.Equal(r.Files.Count, r.Files.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // --- texture capping -----------------------------------------------------------------------------------

    [Fact]
    public void An_already_small_texture_is_returned_untouched()
    {
        byte[] dds = SyntheticDxt1(256, 256, mips: 9);
        var capped = DdsCap.Cap(dds, 512);

        Assert.False(capped.Changed);
        Assert.Same(dds, capped.Data);
    }

    [Fact]
    public void An_oversized_texture_is_capped_by_dropping_top_mips()
    {
        // The real case: an install with an upscaled pack hands out 4096-square props. Berlin's 101 textures
        // came to 692 MB before capping and 23 MB after - and this is a byte slice, never a re-encode.
        byte[] dds = SyntheticDxt1(4096, 4096, mips: 13);
        var capped = DdsCap.Cap(dds, 512);

        Assert.True(capped.Changed);
        Assert.Equal(512, capped.Width);
        Assert.Equal(512, capped.Height);
        Assert.Equal(3, capped.LevelsDropped);
        Assert.True(capped.Data.Length < dds.Length / 10);

        // And the result must still be a texture the engine's own decoder reads.
        var decoded = DdsTexture.Decode(capped.Data);
        Assert.Equal(512, decoded.Width);
        Assert.Equal(512, decoded.Height);
    }

    [Fact]
    public void A_texture_with_no_mip_chain_is_left_alone_rather_than_re_encoded()
    {
        byte[] dds = SyntheticDxt1(2048, 2048, mips: 1);
        var capped = DdsCap.Cap(dds, 512);

        Assert.False(capped.Changed);
        Assert.Contains("re-encode", capped.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_real_game_texture_caps_and_still_decodes()
    {
        const string tex = @"D:\Games\EA GAMES\Battlefield 1942\Mods\bf1942\Archives\texture.rfa";
        if (!File.Exists(tex)) return;

        var archive = new RefractorFlatArchive(tex);
        var big = archive.Entries
            .Where(e => e.Name.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.UncompressedSize)
            .FirstOrDefault();
        if (big is null) return;

        byte[] raw = archive.Read(big);
        var capped = DdsCap.Cap(raw, 256);
        if (!capped.Changed) return;                       // already small, or a cube map

        Assert.True(capped.Width <= 256 && capped.Height <= 256);
        var decoded = DdsTexture.Decode(capped.Data);
        Assert.Equal(capped.Width, decoded.Width);
        Assert.Equal(capped.Height, decoded.Height);
    }

    /// <summary>A DXT1 DDS with a real mip chain - header plus correctly sized (zeroed) level data.</summary>
    private static byte[] SyntheticDxt1(int width, int height, int mips)
    {
        int payload = 0;
        for (int i = 0, w = width, h = height; i < mips; i++, w = Math.Max(1, w / 2), h = Math.Max(1, h / 2))
            payload += Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 8;

        var dds = new byte[128 + payload];
        void W(int at, uint v) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(at), v);

        W(0, 0x20534444);                       // "DDS "
        W(4, 124);                              // header size
        W(8, 0x1 | 0x2 | 0x4 | 0x1000 | 0x80000); // caps|height|width|pixelformat|linearsize
        W(12, (uint)height);
        W(16, (uint)width);
        W(20, (uint)(Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 8));
        W(28, (uint)mips);
        W(76, 32);                              // pixel format size
        W(80, 0x4);                             // DDPF_FOURCC
        W(84, 0x31545844);                      // 'DXT1'
        W(108, 0x1000 | 0x400000 | 0x8);        // texture | mipmap | complex
        return dds;
    }
}
