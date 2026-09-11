using System.Numerics;
using Silk.NET.OpenGL;

namespace RefractorForge.Viewer;

/// <summary>
/// Selection by reading the picture. On a click the objects are re-drawn into an offscreen buffer, each painted a
/// flat colour encoding its index, and the pixel under the cursor is read back. Whatever is drawn there IS the
/// answer — so selection cannot drift away from what you can see, which is the failure mode every ray-versus-box
/// scheme eventually has: the box is a coarse proxy for the mesh, and any disagreement between the pick's matrix
/// and the renderer's shows up as clicking beside things.
///
/// It is also exact where a box is hopeless. A lamp post, a railing, a wire or a palm frond is a few pixels of
/// actual geometry inside a large empty box; here it is clickable on precisely the pixels it covers, and the box
/// around it catches nothing.
///
/// Cost is one scene redraw per click into a small window around the cursor — the scissor keeps the fragment work
/// to that patch — which is nothing next to the frames drawn while you were deciding where to click.
/// </summary>
public sealed class GlPick : IDisposable
{
    private const string VertSrc = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=2) in vec2 aUv;
uniform mat4 uMVP; out vec2 vUv;
void main(){ gl_Position = uMVP * vec4(aPos,1.0); vUv = aUv; }";

    private const string FragSrc = @"#version 330 core
in vec2 vUv; out vec4 FragColor;
uniform sampler2D uTex; uniform int uUseTex; uniform float uAlphaRef; uniform vec4 uId;
void main(){
    // A cut-out part must drop the same texels it drops when drawn, or a fence is clickable on its holes.
    if (uUseTex == 1 && texture(uTex, vUv).a < uAlphaRef) discard;
    FragColor = uId;
}";

    private uint _prog, _fbo, _colorTex, _depthRbo;
    private int _uMvp = -1, _uId = -1, _uUseTex = -1, _uAlphaRef = -1;
    private int _w, _h;
    private bool _broken;

    /// <summary>True once the buffer and program exist. False means the caller should fall back to the ray test —
    /// an old driver with no framebuffer support is a bad reason to make selection stop working.</summary>
    public bool Ready => !_broken && _prog != 0 && _fbo != 0;

    public string? Error { get; private set; }

    private unsafe bool Ensure(GL gl, int w, int h)
    {
        if (_broken || w <= 0 || h <= 0) return false;
        if (_prog == 0)
        {
            uint vs = gl.CreateShader(ShaderType.VertexShader);
            gl.ShaderSource(vs, VertSrc); gl.CompileShader(vs);
            gl.GetShader(vs, ShaderParameterName.CompileStatus, out int vok);
            uint fs = gl.CreateShader(ShaderType.FragmentShader);
            gl.ShaderSource(fs, FragSrc); gl.CompileShader(fs);
            gl.GetShader(fs, ShaderParameterName.CompileStatus, out int fok);
            if (vok == 0 || fok == 0)
            {
                Error = "pick shader: " + gl.GetShaderInfoLog(vs) + gl.GetShaderInfoLog(fs);
                _broken = true; return false;
            }
            _prog = gl.CreateProgram();
            gl.AttachShader(_prog, vs); gl.AttachShader(_prog, fs); gl.LinkProgram(_prog);
            gl.GetProgram(_prog, ProgramPropertyARB.LinkStatus, out int lok);
            gl.DeleteShader(vs); gl.DeleteShader(fs);
            if (lok == 0) { Error = "pick program: " + gl.GetProgramInfoLog(_prog); _broken = true; return false; }
            _uMvp = gl.GetUniformLocation(_prog, "uMVP");
            _uId = gl.GetUniformLocation(_prog, "uId");
            _uUseTex = gl.GetUniformLocation(_prog, "uUseTex");
            _uAlphaRef = gl.GetUniformLocation(_prog, "uAlphaRef");
        }
        if (_fbo != 0 && _w == w && _h == h) return true;

        // Size follows the framebuffer so a pixel here is a pixel there — the whole point is that this pass sees
        // exactly what the colour pass sees.
        if (_fbo != 0) { gl.DeleteFramebuffer(_fbo); gl.DeleteTexture(_colorTex); gl.DeleteRenderbuffer(_depthRbo); }
        _colorTex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _colorTex);
        gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8, (uint)w, (uint)h, 0,
                      PixelFormat.Rgba, PixelType.UnsignedByte, (void*)0);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _depthRbo = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depthRbo);
        gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, (uint)w, (uint)h);
        _fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _colorTex, 0);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, _depthRbo);
        var status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        if (status != GLEnum.FramebufferComplete)
        {
            Error = "pick framebuffer incomplete: " + status;
            _broken = true; return false;
        }
        _w = w; _h = h;
        return true;
    }

    /// <summary>
    /// The object under <paramref name="cursorPx"/>, or -1. <paramref name="radiusPx"/> lets a click that lands
    /// just beside a thin object still find it: the patch around the cursor is searched outward and the nearest
    /// covered pixel wins, which is what makes clicking a railing or a lamp feel forgiving rather than exact.
    /// </summary>
    public unsafe int Pick(GL gl, GlObjects objects, Matrix4x4 viewProj, Vector2 cursorPx, int fbW, int fbH, float radiusPx)
    {
        if (!Ensure(gl, fbW, fbH)) return -1;
        int cx = (int)MathF.Round(cursorPx.X), cy = (int)MathF.Round(cursorPx.Y);
        if (cx < 0 || cy < 0 || cx >= fbW || cy >= fbH) return -1;
        int r = Math.Clamp((int)MathF.Round(radiusPx), 0, 64);

        // The patch to render and read, clamped to the buffer. GL's origin is bottom-left; the cursor is top-down.
        int x0 = Math.Max(0, cx - r), x1 = Math.Min(fbW - 1, cx + r);
        int glyTop = fbH - 1 - cy;
        int y0 = Math.Max(0, glyTop - r), y1 = Math.Min(fbH - 1, glyTop + r);
        int pw = x1 - x0 + 1, ph = y1 - y0 + 1;
        if (pw <= 0 || ph <= 0) return -1;

        var buf = new byte[pw * ph * 4];
        RenderPatch(gl, objects, viewProj, fbW, fbH, x0, y0, pw, ph, buf, null);

        // Nearest covered pixel to the cursor wins. Reading a patch rather than one pixel is what gives the click
        // a tolerance, and because it is measured on the drawn image it needs no per-object fudge factor.
        int best = -1; long bestD2 = long.MaxValue;
        int curPx = cx - x0, curPy = glyTop - y0;
        for (int y = 0; y < ph; y++)
            for (int x = 0; x < pw; x++)
            {
                int o = (y * pw + x) * 4;
                int code = buf[o] | (buf[o + 1] << 8) | (buf[o + 2] << 16);
                if (code == 0) continue;
                long dx = x - curPx, dy = y - curPy;
                long d2 = dx * dx + dy * dy;
                if (d2 < bestD2) { bestD2 = d2; best = code - 1; }
            }
        return best;
    }

    /// <summary>
    /// The object drawn EXACTLY under the cursor, and the depth-buffer value of its surface there (0..1, window
    /// depth), or false when no object covers that pixel. Placement uses it to set a new object down on top of the
    /// one you are pointing at: the depth, unprojected, is the visible surface itself - no tolerance, no proxy box.
    /// </summary>
    public bool PickSurface(GL gl, GlObjects objects, Matrix4x4 viewProj, Vector2 cursorPx, int fbW, int fbH,
                            out int id, out float depth)
    {
        id = -1; depth = 1f;
        if (!Ensure(gl, fbW, fbH)) return false;
        int cx = (int)MathF.Round(cursorPx.X), cy = (int)MathF.Round(cursorPx.Y);
        if (cx < 0 || cy < 0 || cx >= fbW || cy >= fbH) return false;
        var buf = new byte[4];
        var dep = new float[1];
        RenderPatch(gl, objects, viewProj, fbW, fbH, cx, fbH - 1 - cy, 1, 1, buf, dep);
        int code = buf[0] | (buf[1] << 8) | (buf[2] << 16);
        if (code == 0) return false;
        id = code - 1; depth = dep[0];
        return true;
    }

    // Draw every object's id into the patch (x0, y0, pw, ph) - GL coordinates, bottom-left origin - and read the ids
    // back, and the depth too when asked.
    private unsafe void RenderPatch(GL gl, GlObjects objects, Matrix4x4 viewProj, int fbW, int fbH,
                                    int x0, int y0, int pw, int ph, byte[] colors, float[]? depths)
    {
        // This runs from the mouse handler, OUTSIDE the render loop, so every piece of state it touches has to go
        // back exactly as it was. Leaving the scissor on or the draw framebuffer bound would blank the viewport.
        Span<int> prevViewport = stackalloc int[4];
        gl.GetInteger(GLEnum.Viewport, prevViewport);
        gl.GetInteger(GLEnum.FramebufferBinding, out int prevFbo);
        gl.GetInteger(GLEnum.CurrentProgram, out int prevProg);
        gl.GetInteger(GLEnum.VertexArrayBinding, out int prevVao);
        gl.GetInteger(GLEnum.DepthFunc, out int prevDepthFunc);
        gl.GetInteger(GLEnum.TextureBinding2D, out int prevTex);
        Span<float> prevClear = stackalloc float[4];
        gl.GetFloat(GLEnum.ColorClearValue, prevClear);
        bool prevScissor = gl.IsEnabled(EnableCap.ScissorTest);
        bool prevDepth = gl.IsEnabled(EnableCap.DepthTest);
        bool prevBlend = gl.IsEnabled(EnableCap.Blend);
        bool prevCull = gl.IsEnabled(EnableCap.CullFace);
        gl.GetBoolean(GLEnum.DepthWritemask, out bool prevDepthMask);
        Span<int> prevScissorBox = stackalloc int[4];
        gl.GetInteger(GLEnum.ScissorBox, prevScissorBox);

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        gl.Viewport(0, 0, (uint)fbW, (uint)fbH);       // full projection; the scissor limits what is shaded
        gl.Enable(EnableCap.ScissorTest);
        gl.Scissor(x0, y0, (uint)pw, (uint)ph);
        gl.ClearColor(0f, 0f, 0f, 1f);
        gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));
        gl.Enable(EnableCap.DepthTest);
        gl.DepthMask(true);
        gl.Disable(EnableCap.Blend);                   // an ID is not a colour: it must never be blended
        // Two-sided: a wall you are looking at from its back face is still a wall you clicked on.
        gl.Disable(EnableCap.CullFace);
        gl.DepthFunc(DepthFunction.Less);

        objects.DrawIds(gl, _prog, _uMvp, _uId, _uUseTex, _uAlphaRef, viewProj);

        gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
        fixed (byte* p = colors)
            gl.ReadPixels(x0, y0, (uint)pw, (uint)ph, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        if (depths is not null)
            fixed (float* d = depths)
                gl.ReadPixels(x0, y0, (uint)pw, (uint)ph, PixelFormat.DepthComponent, PixelType.Float, d);

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)prevFbo);
        gl.Viewport(prevViewport[0], prevViewport[1], (uint)prevViewport[2], (uint)prevViewport[3]);
        gl.Scissor(prevScissorBox[0], prevScissorBox[1], (uint)prevScissorBox[2], (uint)prevScissorBox[3]);
        if (prevScissor) gl.Enable(EnableCap.ScissorTest); else gl.Disable(EnableCap.ScissorTest);
        if (prevDepth) gl.Enable(EnableCap.DepthTest); else gl.Disable(EnableCap.DepthTest);
        if (prevBlend) gl.Enable(EnableCap.Blend); else gl.Disable(EnableCap.Blend);
        if (prevCull) gl.Enable(EnableCap.CullFace); else gl.Disable(EnableCap.CullFace);
        gl.DepthMask(prevDepthMask);
        gl.DepthFunc((DepthFunction)prevDepthFunc);
        gl.ClearColor(prevClear[0], prevClear[1], prevClear[2], prevClear[3]);
        gl.UseProgram((uint)prevProg);
        gl.BindVertexArray((uint)prevVao);
        gl.BindTexture(TextureTarget.Texture2D, (uint)prevTex);
    }

    public void Dispose() { }   // GL objects die with the context; the editor holds one for its lifetime
}
