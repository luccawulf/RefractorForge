using System.Drawing.Imaging;
using System.Text;
using RefractorForge.Render;

namespace RefractorForge.Archive;

/// <summary>What kind of preview an entry gets, decided by extension.</summary>
public enum PreviewKind { None, Image, Text, Audio, Mesh, Binary }

public static class Preview
{
    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
        { ".dds", ".tga", ".bmp", ".jpg", ".jpeg", ".png", ".gif" };

    // Everything Refractor keeps as plain text. .con and .inc are the script files; .rs is a shader;
    // .ssc/.tweak/.lst/.dif turn up in mods.
    private static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
        { ".con", ".inc", ".txt", ".ini", ".init", ".rs", ".ssc", ".tweak", ".lst", ".dif", ".log", ".xml", ".wst", ".csv" };

    private static readonly HashSet<string> AudioExt = new(StringComparer.OrdinalIgnoreCase)
        { ".wav" };

    public static PreviewKind KindOf(string name)
    {
        string ext = Path.GetExtension(name);
        // .sm is orbitable, so it gets its own kind. A .raw renders to a still image like any texture.
        if (ext.Equals(".sm", StringComparison.OrdinalIgnoreCase)) return PreviewKind.Mesh;
        if (ext.Equals(".raw", StringComparison.OrdinalIgnoreCase)) return PreviewKind.Image;
        if (ImageExt.Contains(ext)) return PreviewKind.Image;
        if (TextExt.Contains(ext)) return PreviewKind.Text;
        if (AudioExt.Contains(ext)) return PreviewKind.Audio;
        return PreviewKind.Binary;
    }

    /// <summary>
    /// Decode an entry to a Bitmap for display. DDS and TGA go through the project's own decoders (the same ones
    /// the editor renders maps with); the rest go through GDI+.
    /// </summary>
    public static Bitmap? ToBitmap(string name, byte[] data)
    {
        string ext = Path.GetExtension(name);
        try
        {
            if (ext.Equals(".dds", StringComparison.OrdinalIgnoreCase))
                return FromTexture(DdsTexture.Decode(data));
            if (ext.Equals(".tga", StringComparison.OrdinalIgnoreCase))
            {
                var t = TgaTexture.Decode(data);
                return t is null ? null : FromTexture(t);
            }
            using var ms = new MemoryStream(data);
            using var img = Image.FromStream(ms);
            return new Bitmap(img);   // copy: the source stream must not outlive the image
        }
        catch { return null; }
    }

    /// <summary>Texture2D is RGBA; GDI+ wants BGRA. Copy row by row so stride padding is respected.</summary>
    private static Bitmap FromTexture(Texture2D t)
    {
        var bmp = new Bitmap(t.Width, t.Height, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, t.Width, t.Height);
        var bits = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[t.Width * 4];
            for (int y = 0; y < t.Height; y++)
            {
                int src = y * t.Width * 4;
                for (int x = 0; x < t.Width; x++)
                {
                    int s = src + x * 4, d = x * 4;
                    row[d + 0] = t.Rgba[s + 2];   // B
                    row[d + 1] = t.Rgba[s + 1];   // G
                    row[d + 2] = t.Rgba[s + 0];   // R
                    row[d + 3] = t.Rgba[s + 3];   // A
                }
                System.Runtime.InteropServices.Marshal.Copy(row, 0, bits.Scan0 + y * bits.Stride, row.Length);
            }
        }
        finally { bmp.UnlockBits(bits); }
        return bmp;
    }

    /// <summary>Decode as text. Refractor scripts are Latin-1, but a UTF-8 BOM is honoured if present.</summary>
    public static string ToText(byte[] data, int maxBytes = 4 * 1024 * 1024)
    {
        bool truncated = data.Length > maxBytes;
        var slice = truncated ? data.AsSpan(0, maxBytes).ToArray() : data;
        string text = slice.Length >= 3 && slice[0] == 0xEF && slice[1] == 0xBB && slice[2] == 0xBF
            ? Encoding.UTF8.GetString(slice, 3, slice.Length - 3)
            : Encoding.Latin1.GetString(slice);
        if (truncated)
            text += $"\r\n\r\n--- truncated at {maxBytes:N0} bytes of {data.Length:N0} ---";
        return text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");   // normalise for TextBox
    }

    /// <summary>A hex dump of the first bytes, for anything with no better preview.</summary>
    public static string ToHexDump(byte[] data, int maxBytes = 4096)
    {
        int n = Math.Min(data.Length, maxBytes);
        var sb = new StringBuilder(n * 4 + 128);
        for (int off = 0; off < n; off += 16)
        {
            sb.Append(off.ToString("x8")).Append("  ");
            for (int i = 0; i < 16; i++)
            {
                if (off + i < n) sb.Append(data[off + i].ToString("x2")).Append(' ');
                else sb.Append("   ");
                if (i == 7) sb.Append(' ');
            }
            sb.Append(' ');
            for (int i = 0; i < 16 && off + i < n; i++)
            {
                byte b = data[off + i];
                sb.Append(b >= 32 && b < 127 ? (char)b : '.');
            }
            sb.Append("\r\n");
        }
        if (data.Length > n) sb.Append($"\r\n--- {data.Length - n:N0} more bytes ---");
        return sb.ToString();
    }
}

/// <summary>
/// Plays a .wav entry.
///
/// This deliberately uses NAudio's high-level <c>AudioFileReader</c> over a temp file, with one
/// <c>WaveOutEvent</c> per sound. A hand-rolled mixer / sample-provider pipeline in this codebase once turned
/// correctly-decoded audio into full-scale noise, so the rule is: no custom mixing, let the OS do it.
/// </summary>
public sealed class AudioPreview : IDisposable
{
    private NAudio.Wave.WaveOutEvent? _out;
    private NAudio.Wave.AudioFileReader? _reader;
    private string? _temp;

    public bool IsPlaying => _out?.PlaybackState == NAudio.Wave.PlaybackState.Playing;
    public TimeSpan Position => _reader?.CurrentTime ?? TimeSpan.Zero;
    public TimeSpan Duration => _reader?.TotalTime ?? TimeSpan.Zero;

    public event EventHandler? Stopped;

    public void Play(byte[] wav, float volume = 0.85f)
    {
        Stop();
        _temp = Path.Combine(Path.GetTempPath(), "rfarchive_" + Guid.NewGuid().ToString("N")[..8] + ".wav");
        File.WriteAllBytes(_temp, wav);

        _reader = new NAudio.Wave.AudioFileReader(_temp) { Volume = Math.Clamp(volume, 0f, 1f) };
        _out = new NAudio.Wave.WaveOutEvent { DesiredLatency = 150 };
        _out.PlaybackStopped += (_, _) => Stopped?.Invoke(this, EventArgs.Empty);
        _out.Init(_reader);
        _out.Play();
    }

    public void Stop()
    {
        try { _out?.Stop(); } catch { }
        try { _out?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        _out = null;
        _reader = null;
        if (_temp is not null) { try { File.Delete(_temp); } catch { } _temp = null; }
    }

    public void Dispose() => Stop();
}
