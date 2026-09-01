using System.Runtime.InteropServices;

namespace RefractorForge.Archive;

/// <summary>
/// 16x16 icons for the file list, taken from the Windows shell.
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

    /// <summary>The ImageList the ListView draws from. Index 0 is a closed folder, 1 an open one.</summary>
    public ImageList Images { get; } = new()
    {
        ImageSize = new Size(16, 16),
        ColorDepth = ColorDepth.Depth32Bit,
    };

    public const int FolderClosed = 0;
    public const int FolderOpen = 1;

    private readonly Dictionary<string, int> _byExt = new(StringComparer.OrdinalIgnoreCase);

    public ShellIcons()
    {
        Images.Images.Add(Folder(open: false) ?? Blank());
        Images.Images.Add(Folder(open: true) ?? Blank());
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
                (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);
            if (h != IntPtr.Zero && info.hIcon != IntPtr.Zero)
            {
                ico = (Icon)Icon.FromHandle(info.hIcon).Clone();
                DestroyIcon(info.hIcon);
            }
        }
        catch { ico = null; }

        idx = Images.Images.Count;
        Images.Images.Add(ico ?? Blank());
        ico?.Dispose();
        _byExt[ext] = idx;
        return idx;
    }

    private static Icon? Folder(bool open)
    {
        try
        {
            var info = new SHFILEINFO();
            // A directory path that need not exist, for the same reason as above.
            var h = SHGetFileInfo(open ? Path.GetTempPath() : "x", FILE_ATTRIBUTE_DIRECTORY, ref info,
                (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);
            if (h == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
            var ico = (Icon)Icon.FromHandle(info.hIcon).Clone();
            DestroyIcon(info.hIcon);
            return ico;
        }
        catch { return null; }
    }

    /// <summary>A transparent 16x16, so a missing icon still occupies its slot and every index stays aligned.</summary>
    private static Icon Blank()
    {
        using var bmp = new Bitmap(16, 16);
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose() => Images.Dispose();
}
