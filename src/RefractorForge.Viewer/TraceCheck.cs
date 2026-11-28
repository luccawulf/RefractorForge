using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using RefractorForge.Render;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace RefractorForge.Viewer;

/// <summary>
/// <c>RefractorForge.exe --trace-check &lt;ArchivesDir&gt; &lt;level&gt; &lt;outDir&gt; [width height]</c>: the live traced view
/// run end to end with no editor - a hidden window for an OpenGL context, a level's scene, the kernel's self-test,
/// timed frames at the given size (still, and with the view changing every frame as it does while editing) and the
/// picture written out. It is the editor's own executable, so it runs on the card and with the libraries the editor
/// will. Everything it finds goes to <c>trace-check.txt</c> in the output folder: a WinExe has no console.
/// Stops at once if OpenGL is not on a dedicated card.
/// </summary>
internal static unsafe class TraceCheck
{
    public static int Run(string[] a)
    {
        if (a.Length < 3) return 2;
        string arcDir = a[0], lvl = a[1], outDir = a[2];
        int w = a.Length > 4 ? int.Parse(a[3]) : 3840, h = a.Length > 4 ? int.Parse(a[4]) : 2224;
        Directory.CreateDirectory(outDir);
        var log = new StringBuilder();
        string logPath = Path.Combine(outDir, "trace-check.txt");
        void Say(string s) { log.AppendLine(s); File.WriteAllText(logPath, log.ToString()); }
        try
        {
            Silk.NET.Windowing.Glfw.GlfwWindowing.RegisterPlatform();
            var opts = WindowOptions.Default;
            opts.IsVisible = false; opts.Size = new Vector2D<int>(640, 360); opts.Title = "RefractorForge trace check";
            using var window = Window.Create(opts);
            window.Initialize();
            var gl = GL.GetApi(window);
            string renderer = gl.GetStringS(StringName.Renderer) ?? "";
            Say($"OpenGL on: {renderer}, version {gl.GetStringS(StringName.Version)}");
            if (!GpuAdapter.LooksDiscrete(renderer)) { Say("STOPPED: OpenGL is not on a dedicated graphics card."); return 3; }

            bool Is(string f, string stem) { var n = Path.GetFileNameWithoutExtension(f); return n.Equals(stem, StringComparison.OrdinalIgnoreCase) || (n.StartsWith(stem + "_", StringComparison.OrdinalIgnoreCase) && n[(stem.Length + 1)..].All(char.IsDigit)); }
            string game = Directory.Exists(Path.Combine(arcDir, "bf1942", "levels")) ? "bf1942" : "BfVietnam";
            var levelRfas = Directory.GetFiles(Path.Combine(arcDir, game, "levels"), "*.rfa").Where(f => Is(f, lvl)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
            var top = Directory.GetFiles(arcDir, "*.rfa").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
            var meshA = top.Where(f => Is(f, "standardMesh") || Is(f, "Objects") || Is(f, "treeMesh")).Concat(levelRfas).ToArray();
            var texA = top.Where(f => Is(f, "texture")).Concat(levelRfas).ToArray();
            var sw = Stopwatch.StartNew();
            var loaded = LevelArchive.FromRfa(levelRfas);
            var lib = MeshLibrary.Open(meshA);
            lib.AttachTextures(TextureLibrary.Open(texA));
            var envS = loaded.Environment;
            var toSun = envS is null ? Vector3.Normalize(new Vector3(-0.5f, 0.8f, -0.35f)) : Vector3.Normalize(new Vector3(envS.SunDirection.X, envS.SunDirection.Y, envS.SunDirection.Z));
            var day = new Daylight { ToSun = toSun }.Build();
            var built = LightScene.Build(loaded.Heightmap, loaded.Config, null, loaded.StaticObjects, lib);
            var scene = LiveTraceScene.From(built) ?? throw new InvalidOperationException("the level has no geometry");
            Say($"{lvl}: {scene.TriCount:N0} triangles ({scene.TerrainTris:N0} terrain), built in {sw.Elapsed.TotalSeconds:0.0} s");

            using var view = new LiveTraceView(gl);
            Say($"traced view: available {view.Available}, hardware ray tracing {view.Hardware} - {view.Status}" + (view.HardwareNote.Length > 0 ? " [" + view.HardwareNote + "]" : ""));
            if (!view.Available) return 4;
            sw.Restart();
            view.SetScene(scene, null);
            gl.Finish();
            Say($"scene on the card in {sw.Elapsed.TotalMilliseconds:0} ms");

            float ws = loaded.Config.WorldSize;
            var o0 = loaded.StaticObjects.Objects.Count > 0 ? loaded.StaticObjects.Objects[loaded.StaticObjects.Objects.Count / 2].Position : new RefractorForge.Formats.Geometry.Vec3(ws / 2, 40, ws / 2);
            var target = new Vector3(o0.X, o0.Y + 3f, o0.Z);
            var eye0 = target + new Vector3(-45f, 22f, -45f);
            Matrix4x4 ViewProj(Vector3 eye)
                => Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY) * Matrix4x4.CreatePerspectiveFieldOfView(60f * MathF.PI / 180f, w / (float)h, 0.3f, ws * 3f);
            LiveTraceKernel.Params P(Vector3 eye, int pw, int ph) => LiveTraceKernel.Params.From(day, null, ViewProj(eye), eye, pw, ph, null, ws, bounces: 2);

            bool pass = view.SelfTest(P(eye0, w, h));
            Say("self-test " + view.SelfTestReport);
            if (!pass) return 5;

            // Somewhere to draw: the hidden window's own framebuffer is neither this big nor promised to hold pixels.
            uint fbo = gl.GenFramebuffer(), colour = gl.GenTexture(), depth = gl.GenRenderbuffer();
            gl.BindTexture(TextureTarget.Texture2D, colour);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
            gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depth);
            gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, (uint)w, (uint)h);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, colour, 0);
            gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, depth);
            if (gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete) throw new InvalidOperationException("the check's framebuffer is incomplete");
            view.TargetFramebuffer = fbo;

            foreach (int div in new[] { 1, 2 })
            {
                int tw = w / div, th = h / div;
                double Run1(bool moving, int frames)
                {
                    double total = 0; int counted = 0;
                    for (int i = 0; i < frames; i++)
                    {
                        // Moving: the view changes every frame, so every frame starts the picture again - what
                        // flying the camera or dragging an object does.
                        var eye = moving ? eye0 + new Vector3(i * 0.05f, 0f, 0f) : eye0;
                        if (moving) view.Reset();
                        var p = P(eye, tw, th);
                        long t0 = Stopwatch.GetTimestamp();
                        gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
                        gl.Viewport(0, 0, (uint)w, (uint)h);
                        view.Frame(p, ViewProj(eye), w, h, null, _ => { });
                        gl.Finish();
                        double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                        if (i >= 5) { total += ms; counted++; }      // the first few pay for new textures and settle the sample count
                    }
                    return total / Math.Max(counted, 1);
                }
                view.Reset();
                double still = Run1(false, 40);
                int sppStill = view.SamplesPerFrame; double traceStill = view.TraceMs;
                double moving = Run1(true, 40);
                Say($"window {w} x {h}, traced at {tw} x {th}: still {still:0.0} ms a frame ({1000.0 / still:0} fps, {sppStill} sample(s) a frame, card {traceStill:0.0} ms); "
                    + $"moving {moving:0.0} ms a frame ({1000.0 / moving:0} fps, {view.SamplesPerFrame} sample(s) a frame, card {view.TraceMs:0.0} ms)");
            }

            // The picture, after it has had a moment to clear up.
            view.Reset();
            var pf = P(eye0, w, h);
            for (int i = 0; i < 24; i++)
            {
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
                gl.Viewport(0, 0, (uint)w, (uint)h);
                view.Frame(pf, ViewProj(eye0), w, h, null, _ => { });
            }
            gl.Finish();
            var px = new byte[w * h * 4];
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            gl.PixelStore(PixelStoreParameter.PackAlignment, 4);
            fixed (byte* dst = px) gl.ReadPixels(0, 0, (uint)w, (uint)h, PixelFormat.Rgba, PixelType.UnsignedByte, dst);
            string png = Path.Combine(outDir, lvl + "_trace-check.png");
            SavePng(png, px, w, h);
            Say($"picture ({view.Samples} samples) -> {png}");
            var err = gl.GetError();
            Say(err == GLEnum.NoError ? "no OpenGL error" : "OpenGL error at the end: " + err);
            Say("DONE");
            return 0;
        }
        catch (Exception ex)
        {
            Say("FAILED: " + ex);
            return 1;
        }
    }

    // OpenGL's rows run bottom to top, a PNG's top to bottom.
    private static void SavePng(string path, byte[] rgba, int w, int h)
    {
        using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var rect = new System.Drawing.Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var raw = new byte[data.Stride * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int s = ((h - 1 - y) * w + x) * 4, d = y * data.Stride + x * 4;
                raw[d] = rgba[s + 2]; raw[d + 1] = rgba[s + 1]; raw[d + 2] = rgba[s]; raw[d + 3] = 255;
            }
        Marshal.Copy(raw, 0, data.Scan0, raw.Length);
        bmp.UnlockBits(data);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}
