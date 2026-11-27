using RefractorForge.Viewer;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The model browser's thumbnails: PNG and JPEG through System.Drawing, and anything else Windows has a codec for -
/// WebP, which Poly Pizza's thumbnails are - through WIC, called directly. Both must hand back RGBA in reading order.
/// </summary>
public class ModelBrowserImageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rf_mbimg_" + Guid.NewGuid().ToString("N")[..8]);
    public ModelBrowserImageTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private string Png()
    {
        var path = Path.Combine(_dir, "colours.png");
        using var bmp = new System.Drawing.Bitmap(3, 2, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        bmp.SetPixel(0, 0, System.Drawing.Color.FromArgb(255, 255, 0, 0));
        bmp.SetPixel(1, 0, System.Drawing.Color.FromArgb(255, 0, 255, 0));
        bmp.SetPixel(2, 0, System.Drawing.Color.FromArgb(255, 0, 0, 255));
        bmp.SetPixel(0, 1, System.Drawing.Color.FromArgb(128, 10, 20, 30));
        bmp.SetPixel(1, 1, System.Drawing.Color.FromArgb(255, 255, 255, 255));
        bmp.SetPixel(2, 1, System.Drawing.Color.FromArgb(255, 0, 0, 0));
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        return path;
    }

    private static void AssertColours(RefractorForge.Render.Texture2D? t)
    {
        Assert.NotNull(t);
        Assert.Equal((3, 2), (t!.Width, t.Height));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, t.Rgba[0..4]);          // red, first in the top row
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, t.Rgba[4..8]);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, t.Rgba[8..12]);
        Assert.Equal(128, t.Rgba[15]);                                       // alpha comes through
    }

    [Fact]
    public void Wic_decodes_into_rgba_in_reading_order() => AssertColours(WicImage.Decode(Png()));

    [Fact]
    public void System_drawing_decodes_the_same_way() => AssertColours(ModelBrowser.Decode(Png()));

    [Fact]
    public void A_thumbnail_is_shrunk_to_fit()
    {
        var big = new RefractorForge.Render.Texture2D(709, 418, new byte[709 * 418 * 4]);
        var s = ModelBrowser.Shrink(big, 256);
        Assert.True(s.Width <= 256 && s.Height <= 256);
        Assert.Equal(236, s.Width);   // 709 / 3
    }

    /// <summary>A real WebP (a Poly Pizza thumbnail) when RF_WEBP_SAMPLE names one: Windows ships the WebP codec, but a
    /// third-party image does not belong in the repository.</summary>
    [Fact]
    public void A_webp_decodes_through_windows_when_a_sample_is_given()
    {
        var sample = Environment.GetEnvironmentVariable("RF_WEBP_SAMPLE");
        if (string.IsNullOrEmpty(sample) || !File.Exists(sample)) return;
        var t = ModelBrowser.Decode(sample);
        Assert.NotNull(t);
        Assert.True(t!.Width > 0 && t.Height > 0);
        Assert.Contains(t.Rgba, b => b != 0);
    }
}
