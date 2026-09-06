using System.Numerics;
using ImGuiNET;
using RefractorForge.Viewer;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The editor's look, driven through a Dear ImGui context with no renderer behind it. ImGui is arithmetic: give
/// it a display size, a built font atlas and a frame time, and NewFrame / Render run the whole thing, producing
/// draw lists nobody draws. That is enough to prove the two things a theme can get wrong without anyone noticing
/// until a panel is visibly broken: a helper that pushes a colour or a font and never pops it, and a font hook
/// that throws inside the controller's constructor.
/// </summary>
[Collection("imgui")]
public class ThemeTests : IDisposable
{
    private readonly IntPtr _ctx;

    public ThemeTests()
    {
        _ctx = ImGui.CreateContext();
        ImGui.SetCurrentContext(_ctx);
        var io = ImGui.GetIO();
        io.DisplaySize = new Vector2(1600, 900);
        io.DeltaTime = 1f / 60f;
        unsafe { io.NativePtr->IniFilename = null; }
        Theme.ClearToasts();   // the toast list is static, so one test's cards would otherwise draw in the next
    }

    public void Dispose() { ImGui.DestroyContext(_ctx); }

    private static void BuildAtlas()
    {
        var io = ImGui.GetIO();
        io.Fonts.GetTexDataAsRGBA32(out IntPtr _, out int _, out int _, out int _);
        io.Fonts.SetTexID((IntPtr)1);
        io.Fonts.ClearTexData();
    }

    private static bool Has(ImFontPtr f) { unsafe { return f.NativePtr != null; } }

    [Fact]
    public void Fonts_load_and_the_hook_never_throws()
    {
        // The real faces on this machine, at the editor's scale.
        Theme.LoadFonts(ImGui.GetIO(), 1f);
        BuildAtlas();
        Assert.True(Has(Theme.FontRegular) && Has(Theme.FontSemibold) && Has(Theme.FontSmall) && Has(Theme.FontTitle),
                    "every slot is filled");
        Assert.True(Theme.FontSmall.FontSize < Theme.FontRegular.FontSize, "the small face is smaller");

        // A CJK face that does not exist must not throw out of the controller's constructor (which would leave a
        // half-made ImGui context behind); it falls through to the Latin faces, and failing those, the built-in.
        ImGui.GetIO().Fonts.Clear();
        var ex = Record.Exception(() => Theme.LoadFonts(ImGui.GetIO(), 1f, @"C:\no\such\font.ttc"));
        Assert.Null(ex);
        BuildAtlas();
        Assert.True(Has(Theme.FontRegular), "a font stands in");
    }

    [Fact]
    public void Every_helper_leaves_the_style_and_font_stacks_balanced()
    {
        Theme.LoadFonts(ImGui.GetIO(), 1f);
        BuildAtlas();
        Theme.Apply(1f);
        var style = ImGui.GetStyle();
        var button = style.Colors[(int)ImGuiCol.Button];
        var text = style.Colors[(int)ImGuiCol.Text];
        Assert.Equal(Theme.Bg2, button);
        Assert.Equal(Theme.Text, text);

        // PushStyleColor writes straight into style.Colors and PopStyleColor restores it, so after a balanced frame
        // the entries read exactly what Apply set. An unpopped push would leave one of them changed.
        for (int frame = 0; frame < 3; frame++)
        {
            ImGui.NewFrame();
            ImGui.SetNextWindowPos(Vector2.Zero); ImGui.SetNextWindowSize(new Vector2(400, 700));
            if (ImGui.Begin("panel"))
            {
                Theme.PanelTitle("Inspector");
                Theme.Section("Layers");
                Theme.Heading("Object Mapper");
                Theme.Muted("muted line");
                Theme.Small("small line");
                Theme.Small("coloured small line", Theme.Warn);
                Theme.PushOn(); ImGui.Button("on"); Theme.PopOn();
                ImGui.SameLine(); Theme.VSep();
                Theme.Segment("A", true); ImGui.SameLine(); Theme.Segment("B", false);
                Theme.AccentButton("Primary");
                Theme.Tip("a tooltip that is not showing because nothing is hovered");
                ImGui.Button("hover me");
                Theme.Tip("and one more");
            }
            ImGui.End();
            Theme.Toast("saved");
            Theme.Toast("something failed", error: true);
            Theme.DrawToasts(new Vector2(300, 60), new Vector2(1300, 870));
            ImGui.Render();
            var dd = ImGui.GetDrawData();
            Assert.True(dd.TotalVtxCount > 0, "the frame drew something");
        }
        Assert.Equal(button, style.Colors[(int)ImGuiCol.Button]);
        Assert.Equal(text, style.Colors[(int)ImGuiCol.Text]);
        Assert.Equal(Theme.Bg0, style.Colors[(int)ImGuiCol.MenuBarBg]);
        Assert.Equal(Theme.Accent, style.Colors[(int)ImGuiCol.CheckMark]);
    }

    [Fact]
    public void Toasts_stack_dedupe_and_expire()
    {
        Theme.LoadFonts(ImGui.GetIO(), 1f);
        BuildAtlas();
        Theme.Apply(1f);
        Theme.Toast("one", seconds: 0.05);
        Theme.Toast("one", seconds: 0.05);          // the same message again replaces, it does not stack
        Theme.Toast("two", seconds: 0.05);

        ImGui.NewFrame();
        Theme.DrawToasts(new Vector2(0, 0), new Vector2(800, 600));
        ImGui.Render();
        int withToasts = ImGui.GetDrawData().TotalVtxCount;
        Assert.True(withToasts > 0, "two cards drew");

        Thread.Sleep(120);
        ImGui.NewFrame();
        Theme.DrawToasts(new Vector2(0, 0), new Vector2(800, 600));
        ImGui.Render();
        Assert.Equal(0, ImGui.GetDrawData().TotalVtxCount);   // both gone; nothing else in the frame
    }
}
