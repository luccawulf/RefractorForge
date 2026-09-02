using System.Drawing.Imaging;
using RefractorForge.Render;

namespace RefractorForge.Archive;

/// <summary>
/// Turn an ordinary picture into a texture the engine will load. The MDT shipped nvdxt in a batch file for
/// this; here it happens as the file is added. The rules come from the engine itself: dimensions must be a
/// power of two or the texture manager drops the file, and an object texture wants a mip chain or it shimmers.
/// Uncompressed 32-bit is what is written - the loader chooses its format from the bit count alone and reads it
/// fine, and it needs no DXT encoder.
/// </summary>
public static class ImageImport
{
    private static readonly HashSet<string> Convertible = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".tga" };

    public static bool CanConvert(string fileName) => Convertible.Contains(Path.GetExtension(fileName));

    public sealed record Result(byte[] Dds, int SourceW, int SourceH, int W, int H, int Mips);

    /// <summary>Decode with GDI+ (or the project's own TGA reader), snap to power-of-two, encode with mips.</summary>
    public static Result ToDds(byte[] data, string fileName, int maxSide = 1024)
    {
        Texture2D tex;
        if (Path.GetExtension(fileName).Equals(".tga", StringComparison.OrdinalIgnoreCase))
            tex = TgaTexture.Decode(data) ?? throw new InvalidDataException("Not a readable TGA.");
        else
        {
            using var ms = new MemoryStream(data);
            using var img = Image.FromStream(ms);
            using var bmp = new Bitmap(img);
            tex = FromBitmap(bmp);
        }
        var pow2 = DdsTexture.ToPowerOfTwo(tex, 4, maxSide);
        var dds = DdsTexture.EncodeUncompressedMipped(pow2);
        int mips = 1; for (int s = Math.Max(pow2.Width, pow2.Height); s > 1; s >>= 1) mips++;
        return new Result(dds, tex.Width, tex.Height, pow2.Width, pow2.Height, mips);
    }

    private static Texture2D FromBitmap(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var bits = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rgba = new byte[bmp.Width * bmp.Height * 4];
            var row = new byte[bmp.Width * 4];
            for (int y = 0; y < bmp.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(bits.Scan0 + y * bits.Stride, row, 0, row.Length);
                int d = y * bmp.Width * 4;
                for (int x = 0; x < bmp.Width; x++)
                {
                    int s = x * 4;
                    rgba[d + s + 0] = row[s + 2];   // R
                    rgba[d + s + 1] = row[s + 1];   // G
                    rgba[d + s + 2] = row[s + 0];   // B
                    rgba[d + s + 3] = row[s + 3];   // A
                }
            }
            return new Texture2D(bmp.Width, bmp.Height, rgba);
        }
        finally { bmp.UnlockBits(bits); }
    }

    /// <summary>What a DDS header says, for the properties panel.</summary>
    public static string DescribeDds(byte[] d)
    {
        if (d.Length < 128 || d[0] != 'D' || d[1] != 'D' || d[2] != 'S') return "not a DDS";
        int h = BitConverter.ToInt32(d, 12), w = BitConverter.ToInt32(d, 16), mips = BitConverter.ToInt32(d, 28);
        uint pfFlags = BitConverter.ToUInt32(d, 80);
        string fourcc = System.Text.Encoding.ASCII.GetString(d, 84, 4);
        int bits = BitConverter.ToInt32(d, 88);
        string fmt = (pfFlags & 0x4) != 0 ? fourcc.Trim('\0') : $"{bits}-bit {((pfFlags & 0x1) != 0 ? "ARGB" : "RGB")}";
        return $"{w} x {h}, {fmt}, {Math.Max(mips, 1)} mip level(s)";
    }

    /// <summary>What a RIFF WAVE header says.</summary>
    public static string? DescribeWav(byte[] d)
    {
        if (d.Length < 44 || d[0] != 'R' || d[1] != 'I' || d[2] != 'F' || d[3] != 'F') return null;
        int pos = 12;
        int channels = 0, rate = 0, bits = 0; long dataLen = 0;
        while (pos + 8 <= d.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(d, pos, 4);
            int len = BitConverter.ToInt32(d, pos + 4);
            if (id == "fmt " && pos + 24 <= d.Length)
            {
                channels = BitConverter.ToInt16(d, pos + 10);
                rate = BitConverter.ToInt32(d, pos + 12);
                bits = BitConverter.ToInt16(d, pos + 22);
            }
            else if (id == "data") { dataLen = len; break; }
            pos += 8 + len + (len & 1);
        }
        if (rate == 0 || channels == 0 || bits == 0) return null;
        double secs = dataLen / (double)(rate * channels * bits / 8);
        return $"{(channels == 1 ? "mono" : channels + " ch")}, {rate:N0} Hz, {bits}-bit, {secs:0.00} s";
    }
}
