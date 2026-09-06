using System.Numerics;
using ImGuiNET;

namespace RefractorForge.Viewer;

/// <summary>
/// The look of the editor: deep charcoal panels, one warm amber accent, a real UI typeface, generous spacing.
/// One place decides every colour and every font so the tool reads as one thing — before this, the accent blue
/// was typed out as a Vector4 literal in thirty places and a section title was whatever TextDisabled happened to
/// draw. Sizes go through <see cref="S"/> so the whole interface follows the monitor's scale.
///
/// The same design as RefractorForge Livery, so the two tools look like siblings.
/// </summary>
public static class Theme
{
    public static float Scale { get; private set; } = 1f;
    public static float S(float px) => px * Scale;

    public static ImFontPtr FontRegular, FontSemibold, FontTitle, FontSmall;
    /// <summary>True when a real typeface was loaded (Segoe UI, or the CJK face a non-English UI needs). False
    /// means ImGui's built-in 13 px bitmap font, which is what the editor used to draw everything in.</summary>
    public static bool HasFonts;
    public static string FontReport = "built-in";

    static Vector4 C(int r, int g, int b, float a = 1f) => new(r / 255f, g / 255f, b / 255f, a);

    public static readonly Vector4 Bg0 = C(21, 23, 27);          // the bars: menu, toolbar, status
    public static readonly Vector4 Bg1 = C(29, 32, 38);          // panels
    public static readonly Vector4 Bg2 = C(38, 42, 49);          // frames, inputs, list rows
    public static readonly Vector4 Bg3 = C(47, 52, 60);          // hover
    public static readonly Vector4 Bg4 = C(58, 64, 74);          // active / a toggle that is on
    public static readonly Vector4 Border = C(13, 15, 18);
    public static readonly Vector4 BorderSoft = C(52, 57, 66);
    public static readonly Vector4 Text = C(232, 234, 237);
    public static readonly Vector4 TextMuted = C(154, 163, 174);
    public static readonly Vector4 TextDim = C(104, 112, 122);
    public static readonly Vector4 Accent = C(240, 162, 75);
    public static readonly Vector4 AccentHover = C(255, 184, 102);
    public static readonly Vector4 AccentActive = C(214, 138, 54);
    public static readonly Vector4 AccentDim = C(240, 162, 75, 0.22f);
    public static readonly Vector4 AccentText = C(20, 16, 10);
    public static readonly Vector4 Danger = C(229, 98, 106);
    public static readonly Vector4 Warn = C(240, 180, 90);
    public static readonly Vector4 Ok = C(111, 207, 151);
    public static readonly Vector4 Info = C(120, 178, 232);

    public static uint U(Vector4 c) => ImGui.GetColorU32(c);
    public static uint U(Vector4 c, float alpha) => ImGui.GetColorU32(new Vector4(c.X, c.Y, c.Z, c.W * alpha));

    // ---- fonts ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs inside the ImGui controller's IO hook — after the context exists and before the atlas is built, which
    /// is the only moment faces can join it. <paramref name="wideFont"/> is the CJK face a non-English UI needs;
    /// when it is given it is used for EVERY slot (a Latin face beside it would draw Japanese as boxes), at two
    /// sizes only, because a CJK range at four sizes makes an atlas some GPUs refuse.
    /// </summary>
    public static void LoadFonts(ImGuiIOPtr io, float scale, string? wideFont = null)
    {
        Scale = scale;
        try
        {
            if (wideFont is not null && File.Exists(wideFont))
            {
                var ranges = io.Fonts.GetGlyphRangesJapanese();
                FontRegular = io.Fonts.AddFontFromFileTTF(wideFont, MathF.Round(15f * scale), default, ranges);
                FontSmall = io.Fonts.AddFontFromFileTTF(wideFont, MathF.Round(13f * scale), default, ranges);
                FontSemibold = FontRegular; FontTitle = FontRegular;
                HasFonts = true; FontReport = Path.GetFileName(wideFont) + " (Japanese glyph ranges)";
            }
            else
            {
                var fonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
                string regular = Path.Combine(fonts, "segoeui.ttf"), semibold = Path.Combine(fonts, "seguisb.ttf");
                if (!File.Exists(semibold)) semibold = Path.Combine(fonts, "segoeuib.ttf");
                if (!File.Exists(regular)) throw new FileNotFoundException(regular);
                bool sb = File.Exists(semibold);
                FontRegular = io.Fonts.AddFontFromFileTTF(regular, MathF.Round(15f * scale));
                FontSemibold = sb ? io.Fonts.AddFontFromFileTTF(semibold, MathF.Round(15f * scale)) : FontRegular;
                FontTitle = io.Fonts.AddFontFromFileTTF(sb ? semibold : regular, MathF.Round(20f * scale));
                FontSmall = io.Fonts.AddFontFromFileTTF(regular, MathF.Round(13f * scale));
                HasFonts = true; FontReport = "Segoe UI" + (sb ? " + Semibold" : "");
            }
            // ImGui.NET exposes FontDefault read-only on the wrapper; the field itself is plain to set.
            unsafe { io.NativePtr->FontDefault = FontRegular.NativePtr; }
        }
        catch (Exception ex)
        {
            // Never let the hook throw: it runs inside the controller's constructor, and a half-made context is
            // worse than the bitmap font.
            io.Fonts.Clear();
            FontRegular = FontSemibold = FontTitle = FontSmall = io.Fonts.AddFontDefault();
            HasFonts = false; FontReport = "built-in (" + ex.Message + ")";
        }
    }

    // ---- style ------------------------------------------------------------------------------------------------

    public static void Apply(float scale)
    {
        Scale = scale;
        ImGui.StyleColorsDark();
        var s = ImGui.GetStyle();
        s.WindowRounding = 0f; s.ChildRounding = 6f; s.FrameRounding = 5f; s.PopupRounding = 8f;
        s.GrabRounding = 5f; s.TabRounding = 5f; s.ScrollbarRounding = 6f;
        s.WindowBorderSize = 0f; s.FrameBorderSize = 0f; s.PopupBorderSize = 1f; s.ChildBorderSize = 0f;
        s.WindowPadding = new Vector2(12, 10);
        s.FramePadding = new Vector2(9, 5);
        s.ItemSpacing = new Vector2(8, 6);
        s.ItemInnerSpacing = new Vector2(6, 4);
        s.ScrollbarSize = 11f;
        s.GrabMinSize = 10f;
        s.IndentSpacing = 16f;
        s.WindowMenuButtonPosition = ImGuiDir.None;
        // Tooltips wait for the mouse to settle instead of flashing under every pass of the cursor.
        s.HoverStationaryDelay = 0.10f;
        s.HoverDelayShort = 0.10f;
        s.HoverDelayNormal = 0.25f;
        var c = s.Colors;
        c[(int)ImGuiCol.WindowBg] = Bg1;
        c[(int)ImGuiCol.ChildBg] = new Vector4(0, 0, 0, 0);
        c[(int)ImGuiCol.PopupBg] = Bg1;
        c[(int)ImGuiCol.Border] = BorderSoft;
        c[(int)ImGuiCol.MenuBarBg] = Bg0;
        c[(int)ImGuiCol.TitleBg] = Bg0; c[(int)ImGuiCol.TitleBgActive] = Bg0; c[(int)ImGuiCol.TitleBgCollapsed] = Bg0;
        c[(int)ImGuiCol.FrameBg] = Bg2; c[(int)ImGuiCol.FrameBgHovered] = Bg3; c[(int)ImGuiCol.FrameBgActive] = Bg4;
        c[(int)ImGuiCol.Button] = Bg2; c[(int)ImGuiCol.ButtonHovered] = Bg3; c[(int)ImGuiCol.ButtonActive] = Bg4;
        c[(int)ImGuiCol.Header] = AccentDim;
        c[(int)ImGuiCol.HeaderHovered] = new Vector4(Accent.X, Accent.Y, Accent.Z, 0.32f);
        c[(int)ImGuiCol.HeaderActive] = new Vector4(Accent.X, Accent.Y, Accent.Z, 0.42f);
        c[(int)ImGuiCol.CheckMark] = Accent;
        c[(int)ImGuiCol.SliderGrab] = Accent; c[(int)ImGuiCol.SliderGrabActive] = AccentHover;
        c[(int)ImGuiCol.Text] = Text; c[(int)ImGuiCol.TextDisabled] = TextDim;
        c[(int)ImGuiCol.Separator] = BorderSoft; c[(int)ImGuiCol.SeparatorHovered] = Accent; c[(int)ImGuiCol.SeparatorActive] = Accent;
        c[(int)ImGuiCol.ScrollbarBg] = new Vector4(0, 0, 0, 0);
        c[(int)ImGuiCol.ScrollbarGrab] = Bg3; c[(int)ImGuiCol.ScrollbarGrabHovered] = Bg4; c[(int)ImGuiCol.ScrollbarGrabActive] = Accent;
        c[(int)ImGuiCol.Tab] = Bg2; c[(int)ImGuiCol.TabHovered] = Bg3; c[(int)ImGuiCol.TabActive] = Bg3;
        c[(int)ImGuiCol.ResizeGrip] = new Vector4(0, 0, 0, 0);
        c[(int)ImGuiCol.ResizeGripHovered] = AccentDim; c[(int)ImGuiCol.ResizeGripActive] = Accent;
        c[(int)ImGuiCol.NavHighlight] = Accent;
        c[(int)ImGuiCol.ModalWindowDimBg] = new Vector4(0, 0, 0, 0.55f);
        c[(int)ImGuiCol.DragDropTarget] = Accent;
        c[(int)ImGuiCol.TextSelectedBg] = AccentDim;
        c[(int)ImGuiCol.PlotHistogram] = Accent; c[(int)ImGuiCol.PlotLines] = Accent;
        s.ScaleAllSizes(Scale);
    }

    // ---- small helpers used everywhere in the UI --------------------------------------------------------------

    /// <summary>A section title inside a panel: small caps in the semibold face, muted, a hairline under it, air
    /// above it. Replaces the TextDisabled("LAYERS") + Separator() pairs.</summary>
    public static void Section(string text)
    {
        ImGui.Dummy(new Vector2(0, S(6)));
        ImGui.PushFont(FontSemibold);
        ImGui.PushStyleColor(ImGuiCol.Text, TextMuted);
        ImGui.TextUnformatted(text.ToUpperInvariant());
        ImGui.PopStyleColor();
        ImGui.PopFont();
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        float w = ImGui.GetContentRegionAvail().X;
        dl.AddLine(new Vector2(p.X, p.Y - S(2)), new Vector2(p.X + w, p.Y - S(2)), U(BorderSoft), 1f);
        ImGui.Dummy(new Vector2(0, S(3)));
    }

    /// <summary>A heading line: the semibold face in the text colour. What a panel says it is about.</summary>
    public static void Heading(string text)
    {
        ImGui.PushFont(FontSemibold);
        ImGui.TextUnformatted(text);
        ImGui.PopFont();
    }

    /// <summary>The name across the top of a pinned panel, where a title bar used to be.</summary>
    public static void PanelTitle(string text)
    {
        ImGui.PushFont(FontSemibold);
        ImGui.PushStyleColor(ImGuiCol.Text, TextMuted);
        ImGui.TextUnformatted(text.ToUpperInvariant());
        ImGui.PopStyleColor();
        ImGui.PopFont();
        ImGui.Dummy(new Vector2(0, S(2)));
    }

    /// <summary>Push the colours of a toggle that is ON — a filled button with accent text. Pair with
    /// <see cref="PopOn"/>. Replaces the hand-typed accent-blue PushStyleColor scattered through the toolbars.</summary>
    public static void PushOn()
    {
        ImGui.PushStyleColor(ImGuiCol.Button, Bg4);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Bg4);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Bg4);
        ImGui.PushStyleColor(ImGuiCol.Text, Accent);
    }
    public static void PopOn() => ImGui.PopStyleColor(4);

    /// <summary>The accent-filled primary button — the one thing a dialog is for.</summary>
    public static bool AccentButton(string label, Vector2 size = default)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, Accent);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, AccentHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, AccentActive);
        ImGui.PushStyleColor(ImGuiCol.Text, AccentText);
        ImGui.PushFont(FontSemibold);
        bool r = ImGui.Button(label, size);
        ImGui.PopFont();
        ImGui.PopStyleColor(4);
        return r;
    }

    /// <summary>A button that reads as one segment of a segmented control: filled when on, quiet when off.</summary>
    public static bool Segment(string label, bool on, Vector2 size = default)
    {
        if (on) PushOn();
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0, 0, 0, 0));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Bg3);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, Bg4);
            ImGui.PushStyleColor(ImGuiCol.Text, TextMuted);
        }
        bool r = ImGui.Button(label, size);
        ImGui.PopStyleColor(4);
        return r;
    }

    /// <summary>A tooltip for the last item, wrapped at a readable width and shown once the mouse settles.
    /// ImGui's SetTooltip never wraps, so a sentence of help became one very long line.</summary>
    public static void Tip(string text)
    {
        if (!ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip)) return;
        ImGui.PushFont(FontRegular);
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(S(380));
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
        ImGui.PopFont();
    }

    public static void Muted(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, TextMuted);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    public static void Small(string text, Vector4? color = null)
    {
        ImGui.PushFont(FontSmall);
        ImGui.PushStyleColor(ImGuiCol.Text, color ?? TextMuted);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
        ImGui.PopFont();
    }

    /// <summary>A thin vertical rule between groups on a bar, in place of a "|" drawn as text.</summary>
    public static void VSep()
    {
        ImGui.SameLine();
        var p = ImGui.GetCursorScreenPos();
        float h = ImGui.GetFrameHeight();
        ImGui.GetWindowDrawList().AddLine(new Vector2(p.X + S(4), p.Y + S(4)), new Vector2(p.X + S(4), p.Y + h - S(4)), U(BorderSoft), 1f);
        ImGui.Dummy(new Vector2(S(9), h));
        ImGui.SameLine();
    }

    // ---- toasts ------------------------------------------------------------------------------------------------

    private static readonly List<(string Text, double Until, bool Error)> _toasts = new();

    /// <summary>Show a message for a few seconds, stacked over the bottom of the viewport. It used to be a line
    /// squeezed into the status bar, where anything longer than the bar was cut off.</summary>
    public static void Toast(string text, bool error = false, double seconds = 4.5)
    {
        _toasts.RemoveAll(t => t.Text == text);
        _toasts.Add((text, Environment.TickCount64 / 1000.0 + seconds, error));
        if (_toasts.Count > 4) _toasts.RemoveAt(0);
    }

    public static void ClearToasts() => _toasts.Clear();

    public static void DrawToasts(Vector2 min, Vector2 max)
    {
        double now = Environment.TickCount64 / 1000.0;
        _toasts.RemoveAll(t => t.Until <= now);
        if (_toasts.Count == 0) return;
        var dl = ImGui.GetForegroundDrawList();
        float maxW = MathF.Min(S(640), (max.X - min.X) - S(40));
        float y = max.Y - S(40);
        for (int i = _toasts.Count - 1; i >= 0; i--)
        {
            var (text, until, error) = _toasts[i];
            float a = (float)Math.Clamp((until - now) / 0.4, 0, 1);
            var ts = ImGui.CalcTextSize(text, false, maxW - S(28));
            var size = ts + new Vector2(S(28), S(16));
            var p0 = new Vector2((min.X + max.X) * 0.5f - size.X * 0.5f, y - size.Y);
            dl.AddRectFilled(p0, p0 + size, U(error ? Danger : Bg0, 0.95f * a), S(8));
            dl.AddRect(p0, p0 + size, U(error ? Danger : BorderSoft, a), S(8));
            dl.AddText(FontRegular, FontRegular.FontSize, p0 + new Vector2(S(14), S(8)), U(Text, a), text, maxW - S(28));
            y -= size.Y + S(8);
        }
    }
}
