using System;
using System.Runtime.InteropServices;
using System.Text;
using ImGuiNET;

namespace RefractorForge.Viewer;

/// <summary>
/// Wires Dear ImGui's clipboard callbacks to the Windows clipboard so Ctrl+C / Ctrl+X / Ctrl+V work in every
/// ImGui text box (e.g. pasting a collab host IP). Silk.NET 2.23 doesn't expose a clipboard API and ImGui's
/// built-in fallback is app-local only, so without this you can't paste text from outside the editor. Uses the
/// Win32 clipboard directly (callable from the GL thread — unlike WinForms' Clipboard, which needs STA).
/// </summary>
internal static unsafe class ClipboardBridge
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte* GetTextDelegate(IntPtr userData);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetTextDelegate(IntPtr userData, byte* text);

    // Kept alive as static fields so the GC never collects the delegates ImGui holds as function pointers.
    private static GetTextDelegate? _get;
    private static SetTextDelegate? _set;
    private static IntPtr _utf8Buf = IntPtr.Zero;   // persists between GetText calls (ImGui reads it after the call returns)

    /// <summary>Install the clipboard callbacks on the current ImGui IO. Call once, after the ImGui context exists.</summary>
    public static void Install()
    {
        _get = GetText; _set = SetText;
        var io = ImGui.GetIO();
        io.GetClipboardTextFn = Marshal.GetFunctionPointerForDelegate(_get);
        io.SetClipboardTextFn = Marshal.GetFunctionPointerForDelegate(_set);
    }

    private static byte* GetText(IntPtr userData)
    {
        string s = Win32Clipboard.GetText() ?? "";
        var bytes = Encoding.UTF8.GetBytes(s + "\0");
        if (_utf8Buf != IntPtr.Zero) Marshal.FreeHGlobal(_utf8Buf);
        _utf8Buf = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, _utf8Buf, bytes.Length);
        return (byte*)_utf8Buf;
    }

    private static void SetText(IntPtr userData, byte* text)
    {
        try { Win32Clipboard.SetText(Marshal.PtrToStringUTF8((IntPtr)text) ?? ""); } catch { }
    }
}

/// <summary>Minimal Win32 CF_UNICODETEXT clipboard get/set (thread-agnostic, unlike WinForms).</summary>
internal static class Win32Clipboard
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr hMem);

    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    public static string? GetText()
    {
        if (!OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            var h = GetClipboardData(CF_UNICODETEXT);
            if (h == IntPtr.Zero) return null;
            var p = GlobalLock(h);
            if (p == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(p); }
            finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    }

    public static void SetText(string s)
    {
        if (!OpenClipboard(IntPtr.Zero)) return;
        try
        {
            EmptyClipboard();
            var data = Encoding.Unicode.GetBytes(s + "\0");
            var hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
            if (hMem == IntPtr.Zero) return;
            var p = GlobalLock(hMem);
            if (p == IntPtr.Zero) return;
            try { Marshal.Copy(data, 0, p, data.Length); }
            finally { GlobalUnlock(hMem); }
            SetClipboardData(CF_UNICODETEXT, hMem);   // clipboard takes ownership of hMem
        }
        finally { CloseClipboard(); }
    }
}
