using System.Numerics;
using ImGuiNET;
using RefractorForge.Viewer;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Copy and paste in the editor's text boxes. Dear ImGui implements Ctrl+C/X/V inside InputText itself, but only
/// through two callbacks the host has to supply — without them ImGui keeps its own in-process buffer and nothing
/// crosses the process boundary, so pasting a path or an IP from outside the editor does nothing at all.
///
/// The context here has no renderer behind it: ImGui is arithmetic, and the clipboard callbacks are plain function
/// pointers, so the round trip through the real Windows clipboard can be proven without a window.
/// </summary>
[Collection("imgui")]
public class ClipboardTests : IDisposable
{
    private readonly IntPtr _ctx;
    private readonly string? _saved;

    public ClipboardTests()
    {
        // Put back whatever the user had on their clipboard: a test has no business keeping it.
        _saved = SafeGet();
        _ctx = ImGui.CreateContext();
        ImGui.SetCurrentContext(_ctx);
        var io = ImGui.GetIO();
        io.DisplaySize = new Vector2(800, 600);
        io.DeltaTime = 1f / 60f;
        unsafe { io.NativePtr->IniFilename = null; }
    }

    public void Dispose()
    {
        ImGui.DestroyContext(_ctx);
        if (_saved is not null) { try { Win32Clipboard.SetText(_saved); } catch { } }
    }

    private static string? SafeGet() { try { return Win32Clipboard.GetText(); } catch { return null; } }

    [Fact]
    public void Imgui_copy_and_paste_reach_the_windows_clipboard()
    {
        ClipboardBridge.Install();

        // Copy out of ImGui (what Ctrl+C in a text box calls) must land on the OS clipboard, where another
        // application can read it.
        const string outward = "RefractorForge copy -> OS  \u00e9\u00fc\u65e5\u672c\u8a9e";
        ImGui.SetClipboardText(outward);
        Assert.Equal(outward, Win32Clipboard.GetText());

        // And paste INTO ImGui (Ctrl+V) must read what another application put there — the direction that
        // matters for pasting a level path or a collaboration host address into the editor.
        const string inward = "OS -> RefractorForge paste  C:\\Games\\BfVietnam\\Levels";
        Win32Clipboard.SetText(inward);
        Assert.Equal(inward, ImGui.GetClipboardText());

        // Round trip, twice over, to catch a buffer that is freed or reused between calls: the get callback hands
        // ImGui a pointer it reads AFTER the call returns, so the memory behind it has to outlive the call.
        ImGui.SetClipboardText("first");
        string a = ImGui.GetClipboardText();
        ImGui.SetClipboardText("second");
        string b = ImGui.GetClipboardText();
        Assert.Equal("first", a);
        Assert.Equal("second", b);

        // Empty is a legal clipboard value and must not throw or return null.
        ImGui.SetClipboardText("");
        Assert.Equal("", ImGui.GetClipboardText());
    }

    [Fact]
    public void The_callbacks_are_actually_installed_on_the_io()
    {
        var io = ImGui.GetIO();
        ClipboardBridge.Install();
        // ImGui.NET exposes these as raw function pointers; a zero here means the host never wired them and
        // ImGui falls back to its own process-local buffer, which looks like "copy and paste does nothing".
        Assert.NotEqual(IntPtr.Zero, io.GetClipboardTextFn);
        Assert.NotEqual(IntPtr.Zero, io.SetClipboardTextFn);
    }
}
