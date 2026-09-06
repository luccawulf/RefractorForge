using System.Buffers.Binary;
using RefractorForge.Formats.Rfa;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Two things a skin tool needs from the shared libraries. A DXT5 encoder, because every object texture whose alpha
/// matters ships in that format and a replacement has to come back in the same one; and a way to learn WHICH
/// archive entry a shader's texture reference resolves to, because a patch archive must carry the replacement under
/// exactly that name or the game never sees it.
/// </summary>
public class Dxt5AndTextureEntryTests
{
    private static Texture2D Alpha(int n)
    {
        // colour gradient across, alpha gradient down, so both halves of every block carry information
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int o = (y * n + x) * 4;
                px[o] = (byte)(x * 255 / (n - 1)); px[o + 1] = 90; px[o + 2] = (byte)(255 - x * 255 / (n - 1));
                px[o + 3] = (byte)(y * 255 / (n - 1));
            }
        return new Texture2D(n, n, px);
    }

    [Fact]
    public void Dxt5_writes_the_header_the_games_alpha_textures_carry()
    {
        var dds = DxtEncoder.EncodeDxt5Mipped(Alpha(64));
        // 64x64 DXT5 top = 16 blocks x 16 blocks x 16 B = 4096; the chain to 1x1 adds 1024+256+64+16+16+16
        Assert.Equal(128 + 4096 + 1024 + 256 + 64 + 16 + 16 + 16, dds.Length);
        Assert.Equal("DXT5", System.Text.Encoding.ASCII.GetString(dds, 84, 4));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(28)));           // 64,32,16,8,4,2,1
        Assert.Equal(4096u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(20)));
        Assert.Equal(0x401008u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(108)));
    }

    [Fact]
    public void Dxt5_round_trips_alpha_and_colour_through_the_decoder()
    {
        var src = Alpha(64);
        var back = DdsTexture.Decode(DxtEncoder.EncodeDxt5Mipped(src));
        Assert.Equal(64, back.Width);
        int worstA = 0, worstRgb = 0;
        for (int i = 0; i < src.Rgba.Length; i += 4)
        {
            worstA = Math.Max(worstA, Math.Abs(src.Rgba[i + 3] - back.Rgba[i + 3]));
            for (int c = 0; c < 3; c++) worstRgb = Math.Max(worstRgb, Math.Abs(src.Rgba[i + c] - back.Rgba[i + c]));
        }
        // A 4x4 block spans 4 rows of a 64-step gradient, so the 8-value alpha palette covers it within a couple of
        // levels; the 565 colour palette is what DXT1 already delivers.
        Assert.True(worstA <= 4, $"alpha drifted by {worstA}");
        Assert.True(worstRgb <= 24, $"colour drifted by {worstRgb}");
    }

    [Fact]
    public void A_block_that_is_one_alpha_everywhere_stays_exactly_that()
    {
        var px = new byte[16 * 16 * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = 200; px[i + 1] = 30; px[i + 2] = 30; px[i + 3] = 137; }
        var back = DdsTexture.Decode(DxtEncoder.EncodeDxt5Mipped(new Texture2D(16, 16, px)));
        for (int i = 3; i < back.Rgba.Length; i += 4) Assert.Equal(137, back.Rgba[i]);
    }

    [Fact]
    public void The_texture_library_can_say_which_entry_a_shader_name_resolves_to()
    {
        var root = Path.Combine(Path.GetTempPath(), "rf_texent_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var rfa = Path.Combine(root, "texture.rfa");
        try
        {
            var dds = DxtEncoder.EncodeDxt1Flat(Alpha(8));
            RefractorFlatArchive.WriteFile(rfa, new[] { ("texture/Sheridan_Hull.dds", dds) }, compress: true, xPackId: XPackId.Default);
            var lib = TextureLibrary.Open(rfa);

            // every spelling a .rs might use lands on the one entry, with its archive-side name intact
            foreach (var spelling in new[] { "texture/Sheridan_Hull", "Sheridan_Hull", "texture\\sheridan_hull", "Sheridan_Hull.dds" })
            {
                var hit = lib.ResolveRaw(spelling);
                Assert.NotNull(hit);
                Assert.Equal("texture/Sheridan_Hull.dds", hit!.Value.EntryName);
                Assert.Equal(dds, hit.Value.Bytes);
            }
            Assert.Null(lib.ResolveRaw("texture/NoSuchThing"));
            Assert.Null(lib.ResolveRaw(null));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
