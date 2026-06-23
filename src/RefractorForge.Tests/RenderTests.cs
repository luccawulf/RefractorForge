using System.Numerics;
using RefractorForge.Formats;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Rfa;
using RefractorForge.Formats.Terrain;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

public class RenderTests
{
    [Fact]
    public void Tga_decode_uncompressed_and_rle_with_origin_bit()
    {
        static byte[] MakeTga(bool rle, bool topOrigin, int w, int h, byte[] pixels)
        {
            byte imageType = rle ? (byte)10 : (byte)2;
            byte descriptor = topOrigin ? (byte)0x20 : (byte)0x00;
            var hdr = new byte[] { 0, 0, imageType, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                (byte)(w & 0xFF), (byte)(w >> 8), (byte)(h & 0xFF), (byte)(h >> 8), 24, descriptor };
            if (!rle) return hdr.Concat(pixels).ToArray();
            var enc = new List<byte>();
            for (int i = 0; i < pixels.Length; i += 3)
            {
                int runLen = 1;
                while (i + runLen * 3 + 3 <= pixels.Length && pixels[i] == pixels[i + runLen * 3] &&
                       pixels[i + 1] == pixels[i + runLen * 3 + 1] && pixels[i + 2] == pixels[i + runLen * 3 + 2] && runLen < 128)
                    runLen++;
                enc.Add((byte)(0x80 | (runLen - 1)));
                enc.Add(pixels[i]); enc.Add(pixels[i + 1]); enc.Add(pixels[i + 2]);
                i += (runLen - 1) * 3;
            }
            return hdr.Concat(enc).ToArray();
        }

        int w = 4, h = 4;
        var bgr = new byte[w * h * 3];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 3;
            bgr[i] = (byte)(x * 40); bgr[i + 1] = (byte)(y * 40); bgr[i + 2] = 128;
        }

        var raw = TgaTexture.Decode(MakeTga(false, false, w, h, bgr));
        Assert.True(raw.Width == w && raw.Height == h, $"uncompressed TGA size {raw.Width}x{raw.Height}");
        Assert.True(raw.Rgba.Length == w * h * 4, "RGBA output length");
        Assert.True(raw.Rgba.Any(b => b > 0), "decoded TGA has non-zero pixels");
        Assert.True(raw.Rgba.Where((b, i) => i % 4 == 3).All(a => a == 255), "alpha channel always 255 for 24-bit TGA");

        var rleData = MakeTga(true, false, w, h, bgr);
        var rleDecoded = TgaTexture.Decode(rleData);
        Assert.True(rleDecoded.Rgba.SequenceEqual(raw.Rgba), "RLE decodes to same pixels as uncompressed");

        var topRaw = TgaTexture.Decode(MakeTga(false, true, w, h, bgr));
        var botRaw = TgaTexture.Decode(MakeTga(false, false, w, h, bgr));
        bool differs = false;
        for (int i = 0; i < topRaw.Rgba.Length; i++) if (topRaw.Rgba[i] != botRaw.Rgba[i]) { differs = true; break; }
        Assert.True(differs, "origin bit flips scanline order");
        Assert.True(topRaw.Rgba[0] == botRaw.Rgba[(h - 1) * w * 4], "top-left in top-origin == bottom-left in bottom-origin");

        var rleTop = TgaTexture.Decode(MakeTga(true, true, w, h, bgr));
        Assert.True(rleTop.Rgba.SequenceEqual(topRaw.Rgba), "RLE + top-origin matches uncompressed top-origin");

        var mono = new byte[w * h * 3]; for (int i = 0; i < mono.Length; i += 3) { mono[i] = 77; mono[i + 1] = 77; mono[i + 2] = 77; }
        var monoRle = MakeTga(true, false, w, h, mono);
        var monoDec = TgaTexture.Decode(monoRle);
        Assert.True(monoDec.Rgba.Length == w * h * 4, "mono RLE decodes");
        for (int i = 0; i < monoDec.Rgba.Length; i += 4) Assert.True(monoDec.Rgba[i] == 77, "mono pixel value preserved");

        var enc32 = TgaTexture.EncodeGrayColormapped(raw);
        var dec32 = TgaTexture.Decode(enc32);
        Assert.True(dec32.Width == w && dec32.Height == h, "gray colormapped encode/decode preserves size");
    }

    [Fact]
    public void BadArchive_survives_corrupt_files()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "rf_bad_" + Guid.NewGuid().ToString("N")[..6] + ".rfa");
        try
        {
            File.WriteAllBytes(tmp, new byte[] { 0x52, 0x46, 0x41, 0x00, 0xFF, 0xFE, 0xFD, 0x00 });
            Exception? ex = null;
            try { MeshLibrary.Open(tmp); } catch (Exception e) { ex = e; }
            Assert.True(ex == null || ex is not StackOverflowException, "MeshLibrary.Open does not stack-overflow on corrupt file");

            ex = null;
            try { TextureLibrary.Open(tmp); } catch (Exception e) { ex = e; }
            Assert.True(ex == null || ex is not StackOverflowException, "TextureLibrary.Open does not stack-overflow on corrupt file");

            ex = null;
            try { LevelArchive.FromRfa(tmp); } catch (Exception e) { ex = e; }
            Assert.True(ex == null || ex is not StackOverflowException, "LevelArchive.FromRfa does not stack-overflow on corrupt file");

            string zero = Path.Combine(Path.GetTempPath(), "rf_bad_zero_" + Guid.NewGuid().ToString("N")[..6] + ".rfa");
            File.WriteAllBytes(zero, Array.Empty<byte>());
            try
            {
                ex = null; try { MeshLibrary.Open(zero); } catch (Exception e) { ex = e; }
                Assert.True(ex == null || ex is not StackOverflowException, "MeshLibrary.Open 0-byte");
                ex = null; try { TextureLibrary.Open(zero); } catch (Exception e) { ex = e; }
                Assert.True(ex == null || ex is not StackOverflowException, "TextureLibrary.Open 0-byte");
                ex = null; try { LevelArchive.FromRfa(zero); } catch (Exception e) { ex = e; }
                Assert.True(ex == null || ex is not StackOverflowException, "LevelArchive.FromRfa 0-byte");
            }
            finally { try { File.Delete(zero); } catch { } }
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void ObjectLightmap_bake_variation_and_tga_roundtrip()
    {
        int size = 16;
        var positions = new Vector3[]
        {
            new(0, 0, 0), new(4, 0, 0), new(2, 2, 0),
            new(4, 0, 0), new(4, 0, 4), new(2, 2, 2),
            new(4, 0, 4), new(0, 0, 4), new(2, 2, 4),
            new(0, 0, 4), new(0, 0, 0), new(2, 2, 2),
        };
        var uvs = new Vector2[]
        {
            new(0, 0), new(1, 0), new(0.5f, 1f),
            new(0, 0), new(1, 0), new(0.5f, 1f),
            new(0, 0), new(1, 0), new(0.5f, 1f),
            new(0, 0), new(1, 0), new(0.5f, 1f),
        };
        var lmUvs = new Vector2[]
        {
            new(0f, 0f), new(0.5f, 0f), new(0.25f, 0.5f),
            new(0.5f, 0f), new(1f, 0f), new(0.75f, 0.5f),
            new(0f, 0.5f), new(0.5f, 0.5f), new(0.25f, 1f),
            new(0.5f, 0.5f), new(1f, 0.5f), new(0.75f, 1f),
        };
        var indices = new int[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
        var part = new MeshLibrary.MaterialPart(indices, Vector3.One, null, false);
        var mesh = new MeshLibrary.Mesh(positions, uvs, new[] { part }) { LightmapUvs = lmUvs };

        var hm = new Heightmap(16, 16);
        for (int i = 0; i < hm.Samples.Length; i++) hm.Samples[i] = 10000;
        var cfg = new TerrainConfig { MaterialSize = 16, WorldSize = 64, YScale = 1f };
        var sunDir = new Vec3(0.7f, 0.7f, 0f);

        var baked = ObjectLightmapBaker.Bake(mesh, Matrix4x4.Identity, hm, cfg, sunDir, size);
        Assert.True(baked is not null, "Bake returned non-null with lightmap UVs");
        Assert.True(baked!.Width == size && baked.Height == size, $"lightmap is {size}x{size}");
        Assert.True(baked.Rgba.Any(b => b > 0), "at least some texels lit");

        byte maxLit = 0;
        for (int i = 0; i < baked.Rgba.Length; i += 4) if (baked.Rgba[i] > maxLit) maxLit = baked.Rgba[i];
        Assert.True(maxLit > 50, $"brightest texel has some lit value (got {maxLit})");

        var noLm = new MeshLibrary.Mesh(positions, uvs, new[] { part });
        var bakedNone = ObjectLightmapBaker.Bake(noLm, Matrix4x4.Identity, hm, cfg, sunDir, size);
        Assert.True(bakedNone is null, "Bake returns null when no lightmap UVs");

        var enc = TgaTexture.EncodeGrayColormapped(baked);
        var dec = TgaTexture.Decode(enc);
        Assert.True(dec.Width == size && dec.Height == size, "TGA encode/decode preserves size");
        Assert.True(dec.Rgba.Any(b => b > 0), "decoded TGA not all black");

        bool closeEnough = true;
        for (int i = 0; i < baked.Rgba.Length; i += 4)
            if (Math.Abs(baked.Rgba[i] - dec.Rgba[i]) > 2) { closeEnough = false; break; }
        Assert.True(closeEnough, "TGA encode/decode preserves lightmap brightness within ±2");
    }
}
