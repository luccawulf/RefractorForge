using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Terrain;

namespace RefractorForge.Render;

/// <summary>
/// A ray-traced still of the level from a camera: the traced light on the level's own textures.
///
/// <para>This is the picture a realistic bake aims for, made in seconds and without baking anything - so the sun,
/// the sky and the lamps can be tuned by looking. It is NOT what the game will draw: the game has no ray tracer, and
/// draws whatever was baked through its own formula (the viewport shows that, live). What the two have in common is
/// the light; the bake's job is to get the engine as close to this picture as the engine can go.</para>
/// </summary>
public static class LightPreview
{
    /// <summary>Where the picture is taken from.</summary>
    /// <param name="FovYDeg">Vertical field of view, degrees.</param>
    public sealed record Camera(Vector3 Position, Vector3 Forward, Vector3 Up, float FovYDeg, int Width, int Height);

    /// <summary>
    /// Everything a render reads, resolved up front. Build it on the thread that owns the mesh library - its cache
    /// is not thread-safe - and then render from any thread.
    /// </summary>
    public sealed class Prepared
    {
        public RayScene Scene { get; init; } = null!;
        public IReadOnlyList<LightScene.Source> Sources { get; init; } = Array.Empty<LightScene.Source>();
        public MeshLibrary.Mesh?[] MeshOf { get; init; } = Array.Empty<MeshLibrary.Mesh?>();
        public Matrix4x4[] WorldOf { get; init; } = Array.Empty<Matrix4x4>();
        /// <summary>The ground's picture, laid over the whole world.</summary>
        public Texture2D? Ground { get; init; }
        public float WorldSize { get; init; }
        public float? WaterLevel { get; init; }
    }

    /// <summary>Resolve a built scene's objects to their meshes and placements.</summary>
    /// <param name="built">A scene built with <c>keepSources: true</c>.</param>
    public static Prepared? Prepare(LightScene.Built built, StaticObjectsFile? objs, MeshLibrary? meshes,
                                    Texture2D? ground, TerrainConfig cfg, float? waterLevel = null)
    {
        if (built.Scene is null || built.Sources is null) return null;
        int n = objs?.Objects.Count ?? 0;
        var meshOf = new MeshLibrary.Mesh?[n];
        var worldOf = new Matrix4x4[n];
        for (int i = 0; i < n; i++)
        {
            var o = objs!.Objects[i];
            worldOf[i] = LevelScene.MeshWorld(o);
            var name = meshes?.LodGeometryNames(o.Template).FirstOrDefault();
            if (name is not null && meshes!.TryGet(name, out var m)) meshOf[i] = m;
        }
        return new Prepared
        {
            Scene = built.Scene, Sources = built.Sources, MeshOf = meshOf, WorldOf = worldOf,
            Ground = ground, WorldSize = cfg.WorldSize, WaterLevel = waterLevel,
        };
    }

    /// <summary>
    /// Render into <paramref name="rgba"/> (width x height x 4).
    /// </summary>
    /// <param name="step">Trace one ray per <paramref name="step"/> x <paramref name="step"/> block and fill the
    /// block with it. 4 gives a rough picture in a sixteenth of the time; 1 is the full one.</param>
    /// <param name="lamps">Placed lamps: direct, shadowed and bounced.</param>
    /// <param name="rowDone">Called as each row of blocks finishes, from worker threads.</param>
    public static void Render(Prepared s, DaylightModel day, Camera cam, byte[] rgba, LightTracer.Budget budget,
                              int step = 1, LightRig? lamps = null, Action? rowDone = null, CancellationToken cancel = default)
    {
        int w = cam.Width, h = cam.Height;
        if (rgba.Length < w * h * 4) throw new ArgumentException("the picture buffer is too small", nameof(rgba));
        step = Math.Max(1, step);
        if (lamps is { Lights.Count: 0 }) lamps = null;
        var fwd = Vector3.Normalize(cam.Forward);
        var right = Vector3.Normalize(Vector3.Cross(fwd, cam.Up));
        var up = Vector3.Cross(right, fwd);
        float tanFov = MathF.Tan(cam.FovYDeg * MathF.PI / 360f), aspect = w / (float)h;
        int rows = (h + step - 1) / step;
        try
        {
            Parallel.For(0, rows, new ParallelOptions { CancellationToken = cancel }, ry =>
            {
                int y = ry * step;
                for (int x = 0; x < w; x += step)
                {
                    float px = MathF.Min(x + step * 0.5f, w - 0.5f), py = MathF.Min(y + step * 0.5f, h - 0.5f);
                    float sx = (2f * px / w - 1f) * tanFov * aspect, sy = (1f - 2f * py / h) * tanFov;
                    var dir = Vector3.Normalize(fwd + right * sx + up * sy);
                    var c = Shade(s, day, cam.Position, dir, budget, lamps, LightTracer.Mix((uint)x, (uint)y));
                    byte r = ToByte(c.X), g = ToByte(c.Y), b = ToByte(c.Z);
                    for (int yy = y; yy < Math.Min(y + step, h); yy++)
                        for (int xx = x; xx < Math.Min(x + step, w); xx++)
                        {
                            int o = (yy * w + xx) * 4;
                            rgba[o] = r; rgba[o + 1] = g; rgba[o + 2] = b; rgba[o + 3] = 255;
                        }
                }
                rowDone?.Invoke();
            });
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>The colour seen along one ray, on screen (0..1, may exceed 1 before clamping).</summary>
    public static Vector3 Shade(Prepared s, DaylightModel day, Vector3 origin, Vector3 dir, LightTracer.Budget budget,
                                LightRig? lamps, uint seed)
    {
        bool hitSurface = s.Scene.Trace(origin, dir, out var hit, skip: 0f);

        // Water: a flat sheet at the level's water line. Not light transport - just enough that a lake reads as one.
        if (s.WaterLevel is { } wl && dir.Y < -1e-4f && origin.Y > wl)
        {
            float t = (wl - origin.Y) / dir.Y;
            if (t > 0f && (!hitSurface || t < hit.Distance))
            {
                var under = hitSurface ? Surface(s, day, hit, budget, lamps, seed) : new Vector3(0.05f, 0.08f, 0.09f);
                var mirror = Sky(day, new Vector3(dir.X, -dir.Y, dir.Z));
                float fres = 0.08f + 0.92f * MathF.Pow(1f - MathF.Min(1f, -dir.Y), 4f);
                return Vector3.Lerp(under * new Vector3(0.45f, 0.6f, 0.6f), mirror, Math.Clamp(fres, 0f, 0.85f));
            }
        }
        return hitSurface ? Surface(s, day, hit, budget, lamps, seed) : Sky(day, dir);
    }

    // The sky as a backdrop. Radiance is far below the sunlit ground's on the linear scale and a camera would let it
    // burn; a fixed lift stands in for that so it reads as sky rather than as dusk.
    private static Vector3 Sky(DaylightModel day, Vector3 dir)
        => Vector3.Min(day.ToDisplay(day.Environment(new Vector3(dir.X, MathF.Max(dir.Y, 0.001f), dir.Z)) * 2.4f), Vector3.One);

    private static Vector3 Surface(Prepared s, DaylightModel day, RayScene.Hit hit, LightTracer.Budget budget,
                                   LightRig? lamps, uint seed)
    {
        var src = s.Sources[hit.Triangle];
        var n = hit.Normal;                                   // faces the camera, whichever way the mesh is wound
        var p = hit.Point;
        Vector3 tex;
        if (src.Object < 0)
            tex = s.Ground is null ? new Vector3(0.45f, 0.42f, 0.33f) : Sample(s.Ground, p.X / s.WorldSize, p.Z / s.WorldSize);
        else
        {
            var m = s.MeshOf[src.Object];
            tex = new Vector3(0.5f);
            if (m is not null && src.Part < m.Parts.Length)
            {
                var part = m.Parts[src.Part];
                tex = part.Color;
                if (part.Texture is { } t && m.Uvs is { Length: > 0 } uvs
                    && src.A < uvs.Length && src.B < uvs.Length && src.C < uvs.Length)
                {
                    var world = s.WorldOf[src.Object];
                    Bary(p, Vector3.Transform(m.Positions[src.A], world), Vector3.Transform(m.Positions[src.B], world),
                         Vector3.Transform(m.Positions[src.C], world), out float b0, out float b1, out float b2);
                    var uv = uvs[src.A] * b0 + uvs[src.B] * b1 + uvs[src.C] * b2;
                    tex = Sample(t, uv.X - MathF.Floor(uv.X), uv.Y - MathF.Floor(uv.Y));
                }
            }
        }
        var start = p + n * 0.02f;
        var l = LightTracer.Gather(s.Scene, day, start, n, budget, seed, lamps);
        var e = l.Irradiance(day, n);
        if (lamps is not null)
            e += day.FromDisplay(LampLight.Display(s.Scene, lamps, start, n, Math.Max(1, budget.SunSamples / 2), src.Object < 0, seed));
        return tex * day.ToDisplay(e);
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);

    private static Vector3 Sample(Texture2D t, float u, float v)
    {
        float fx = Math.Clamp(u, 0f, 1f) * t.Width - 0.5f, fy = Math.Clamp(v, 0f, 1f) * t.Height - 0.5f;
        int x0 = Math.Clamp((int)MathF.Floor(fx), 0, t.Width - 1), y0 = Math.Clamp((int)MathF.Floor(fy), 0, t.Height - 1);
        int x1 = Math.Min(x0 + 1, t.Width - 1), y1 = Math.Min(y0 + 1, t.Height - 1);
        float tx = Math.Clamp(fx - x0, 0f, 1f), ty = Math.Clamp(fy - y0, 0f, 1f);
        var px = t.Rgba; int w = t.Width;
        Vector3 P(int x, int y) { int i = (y * w + x) * 4; return new Vector3(px[i], px[i + 1], px[i + 2]) * (1f / 255f); }
        return Vector3.Lerp(Vector3.Lerp(P(x0, y0), P(x1, y0), tx), Vector3.Lerp(P(x0, y1), P(x1, y1), tx), ty);
    }

    private static void Bary(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out float u, out float v, out float w)
    {
        Vector3 v0 = b - a, v1 = c - a, v2 = p - a;
        float d00 = Vector3.Dot(v0, v0), d01 = Vector3.Dot(v0, v1), d11 = Vector3.Dot(v1, v1), d20 = Vector3.Dot(v2, v0), d21 = Vector3.Dot(v2, v1);
        float den = d00 * d11 - d01 * d01;
        if (MathF.Abs(den) < 1e-12f) { u = 1f; v = 0f; w = 0f; return; }
        v = (d11 * d20 - d01 * d21) / den; w = (d00 * d21 - d01 * d20) / den; u = 1f - v - w;
    }
}
