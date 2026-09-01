using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace RefractorForge.Archive;

/// <summary>
/// Icons for the file list, taken from the Windows shell.
///
/// Asking the shell means a .wav, a .txt or a .dds gets whatever icon the user's own machine shows for it in
/// Explorer, so the list looks native and stays right when someone installs a different image editor. It also
/// avoids shipping an icon set: BGA drew its icons from a large bundled pack, which is a licensing question
/// this program does not need to have.
///
/// Nothing is extracted from the archive to do it. SHGFI_USEFILEATTRIBUTES tells the shell to answer from the
/// extension alone, so it never touches the disk and works for paths that only exist inside an .rfa.
/// </summary>
public sealed class ShellIcons : IDisposable
{
    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>The ImageList the list draws from. Index 0 is a closed folder, 1 an open one.</summary>
    public ImageList Images { get; }

    /// <summary>Edge length of every icon, in pixels.</summary>
    public int Size { get; }

    public const int FolderClosed = 0;
    public const int FolderOpen = 1;

    private readonly Dictionary<string, int> _byExt = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="size">Icon edge in pixels. Above 16 the source is the shell's 32px icon scaled DOWN,
    /// never the 16px one scaled up — enlarging a 16px icon is exactly what makes a file list look blurry.</param>
    public ShellIcons(int size = 24)
    {
        Size = Math.Clamp(size, 16, 48);
        Images = new ImageList { ImageSize = new Size(Size, Size), ColorDepth = ColorDepth.Depth32Bit };
        Images.Images.Add(Scale(Folder(open: false)));
        Images.Images.Add(Scale(Folder(open: true)));
    }

    /// <summary>Icon index for a file name, added to the list on first sight of each extension.</summary>
    public int ForFile(string name)
    {
        string ext = Path.GetExtension(name);
        if (ext.Length == 0) ext = ".";
        if (_byExt.TryGetValue(ext, out int idx)) return idx;

        Icon? ico = null;
        try
        {
            var info = new SHFILEINFO();
            // "x" + ext is a path that need not exist: USEFILEATTRIBUTES makes the shell answer from the
            // extension alone rather than going to the filesystem.
            var h = SHGetFileInfo("x" + ext, FILE_ATTRIBUTE_NORMAL, ref info,
                (uint)Marshal.SizeOf<SHFILEINFO>(),
                SHGFI_ICON | SizeFlag | SHGFI_USEFILEATTRIBUTES);
            if (h != IntPtr.Zero && info.hIcon != IntPtr.Zero)
            {
                ico = (Icon)Icon.FromHandle(info.hIcon).Clone();
                DestroyIcon(info.hIcon);
            }
        }
        catch { ico = null; }

        idx = Images.Images.Count;
        Images.Images.Add(Scale(ico));
        _byExt[ext] = idx;
        return idx;
    }

    private uint SizeFlag => Size > 16 ? SHGFI_LARGEICON : SHGFI_SMALLICON;

    private Icon? Folder(bool open)
    {
        try
        {
            var info = new SHFILEINFO();
            // A directory path that need not exist, for the same reason as above.
            var h = SHGetFileInfo(open ? Path.GetTempPath() : "x", FILE_ATTRIBUTE_DIRECTORY, ref info,
                (uint)Marshal.SizeOf<SHFILEINFO>(),
                SHGFI_ICON | SizeFlag | SHGFI_USEFILEATTRIBUTES);
            if (h == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
            var ico = (Icon)Icon.FromHandle(info.hIcon).Clone();
            DestroyIcon(info.hIcon);
            return ico;
        }
        catch { return null; }
    }

    /// <summary>Rasterise at exactly the list's size with a high-quality filter. A null icon still produces a
    /// transparent bitmap, so a missing one occupies its slot and every later index stays aligned.</summary>
    private Bitmap Scale(Icon? ico)
    {
        var bmp = new Bitmap(Size, Size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        if (ico is null) return bmp;
        using (ico)
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            using var src = ico.ToBitmap();
            g.DrawImage(src, new Rectangle(0, 0, Size, Size));
        }
        return bmp;
    }

    public void Dispose() => Images.Dispose();
}
