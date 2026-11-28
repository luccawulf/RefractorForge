using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using RefractorForge.Formats.Terrain;

namespace RefractorForge.Render;

/// <summary>
/// Fills a <see cref="LightVolume"/> from the traced light: where the air in the box is dark the lattice is dense,
/// where a lamp or a doorway lights it the lattice is clear (a dark volume); or the lattice carries the lamps' own
/// light, added, where they reach (a glow volume).
///
/// <para>The light "in the air" at a point is the mean of what six faces of a small cube there would receive - sun,
/// sky, bounce and placed lamps, shadowed by the level - so a point under a lamp, beside a lit doorway and under an
/// open roof all read as lit. It is traced on a coarse grid through the box and interpolated onto the planes: light
/// in air is smooth, and a plane's tile is only 32 texels across.</para>
/// </summary>
public static class LightVolumeBaker
{
    /// <param name="World">The box's own space to the world: the placed object's transform. The box's origin is
    /// the middle of its floor; it may be turned about the vertical.</param>
    /// <param name="Size">x across, y up, z deep, in the box's own axes.</param>
    /// <param name="Spacing">Metres between planes.</param>
    /// <param name="Kind">What the planes carry (<see cref="Dark"/>, <see cref="Glow"/>, <see cref="Haze"/>).</param>
    /// <param name="Strength">Dark: how fast unlit air swallows what is behind it, per metre (0.12 leaves a third
    /// after nine metres). Glow: how strongly lamp light shows in the air.</param>
    /// <param name="LitLevel">Dark only: the brightness (0..1, as on screen) at which air counts as fully lit and
    /// the volume is clear.</param>
    /// <param name="Fade">Metres over which the volume fades out toward its sides and top.</param>
    public sealed record Settings(Matrix4x4 World, Vector3 Size, float Spacing, int Kind, float Strength, float LitLevel, float Fade)
    {
        /// <summary>A haze volume's colour.</summary>
        public Vector3 HazeColour { get; init; } = new(0.72f, 0.76f, 0.80f);
    }

    /// <summary>Darkness where the air is unlit.</summary>
    public const int Dark = 0;
    /// <summary>The lamps' own light, added where it reaches.</summary>
    public const int Glow = 1;
    /// <summary>An even mist of one colour, whatever the light: <c>Strength</c> is its density per metre.</summary>
    public const int Haze = 2;

    /// <summary>Planes a metre apart suit a hangar, half a metre a room: the spacing that keeps a box of this size
    /// to a few hundred planes without showing its lattice more than it must.</summary>
    public static float SpacingFor(Vector3 size)
    {
        float longest = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
        return longest <= 14f ? 0.5f : longest <= 45f ? 1f : 2f;
    }

    /// <summary>The most a glow plane adds: a volume twenty planes deep at this is already a bright shaft.</summary>
    public const float MaxGlowAlpha = 0.05f;

    private static readonly Vector3[] Faces = { Vector3.UnitY, -Vector3.UnitY, Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ };

    /// <summary>The light in the air at <paramref name="p"/>, as the screen would show a white card there: the
    /// daylight and the lamps together, and the lamps alone.</summary>
    public static (Vector3 All, Vector3 Lamps) AirLight(RayScene? scene, DaylightModel day, LightRig? rig, Vector3 p, uint seed)
    {
        var budget = new LightTracer.Budget(1, 6, 1);
        var e = Vector3.Zero; var lamps = Vector3.Zero;
        for (int f = 0; f < Faces.Length; f++)
        {
            uint s = LightTracer.Mix(seed, (uint)(f * 7919 + 13));
            e += LightTracer.Gather(scene, day, p, Faces[f], budget, s, rig).Irradiance(day, Faces[f]);
            if (rig is not null) lamps += day.FromDisplay(LampLight.Display(scene, rig, p, Faces[f], 1, null, s, 0f));
        }
        e /= Faces.Length; lamps /= Faces.Length;
        return (day.ToDisplay(e + lamps), day.ToDisplay(lamps));
    }

    /// <summary>Bake one volume. Returns null if cancelled.</summary>
    public static LightVolume.Built? Bake(string meshName, Settings s, RayScene? scene, DaylightModel day, LightRig? rig,
                                          CancellationToken cancel = default)
    {
        if (rig is { Lights.Count: 0 }) rig = null;
        var size = Vector3.Max(s.Size, new Vector3(1f));
        float spacing = Math.Clamp(s.Spacing, 0.25f, 4f);
        // The grid the light is traced on: no finer than the planes, and no more than about forty thousand points.
        float g = MathF.Max(spacing, MathF.Cbrt(size.X * size.Y * size.Z / 40000f));
        int nx = Math.Max(2, (int)MathF.Ceiling(size.X / g) + 1), ny = Math.Max(2, (int)MathF.Ceiling(size.Y / g) + 1),
            nz = Math.Max(2, (int)MathF.Ceiling(size.Z / g) + 1);
        var grid = new Vector4[nx * ny * nz];
        float lit = MathF.Max(s.LitLevel, 0.02f);
        try
        {
            Parallel.For(0, grid.Length, new ParallelOptions { CancellationToken = cancel }, i =>
            {
                int ix = i % nx, iy = i / nx % ny, iz = i / (nx * ny);
                var local = new Vector3(-size.X * 0.5f + size.X * ix / (nx - 1), size.Y * iy / (ny - 1), -size.Z * 0.5f + size.Z * iz / (nz - 1));
                // The floor row is traced a hand above the ground, not in it.
                var p = Vector3.Transform(local + new Vector3(0f, iy == 0 ? 0.15f : 0f, 0f), s.World);
                if (s.Kind == Haze)
                {
                    grid[i] = new Vector4(s.HazeColour, LightVolume.PlaneAlpha(MathF.Max(s.Strength, 0f), spacing, 0.12f));
                    return;
                }
                var (all, lamps) = AirLight(scene, day, rig, p, LightTracer.Mix((uint)i, 0x7F4A7C15u));
                if (s.Kind == Glow)
                {
                    float peak = MathF.Max(lamps.X, MathF.Max(lamps.Y, lamps.Z));
                    var colour = peak > 1e-4f ? lamps / peak : Vector3.One;
                    grid[i] = new Vector4(colour, MathF.Min(MaxGlowAlpha, MathF.Max(s.Strength, 0f) * peak * spacing));
                }
                else
                {
                    float t = Math.Clamp(EngineLight.Luma(Vector3.Min(all, Vector3.One)) / lit, 0f, 1f);
                    grid[i] = new Vector4(0f, 0f, 0f, LightVolume.PlaneAlpha(MathF.Max(s.Strength, 0f) * (1f - t), spacing));
                }
            });
        }
        catch (OperationCanceledException) { return null; }
        if (cancel.IsCancellationRequested) return null;

        Vector4 At(Vector3 local)
        {
            float fx = Math.Clamp((local.X + size.X * 0.5f) / size.X, 0f, 1f) * (nx - 1);
            float fy = Math.Clamp(local.Y / size.Y, 0f, 1f) * (ny - 1);
            float fz = Math.Clamp((local.Z + size.Z * 0.5f) / size.Z, 0f, 1f) * (nz - 1);
            int x0 = Math.Min((int)fx, nx - 2), y0 = Math.Min((int)fy, ny - 2), z0 = Math.Min((int)fz, nz - 2);
            float tx = fx - x0, ty = fy - y0, tz = fz - z0;
            Vector4 G(int x, int y, int z) => grid[(z * ny + y) * nx + x];
            var a = Vector4.Lerp(Vector4.Lerp(G(x0, y0, z0), G(x0 + 1, y0, z0), tx), Vector4.Lerp(G(x0, y0 + 1, z0), G(x0 + 1, y0 + 1, z0), tx), ty);
            var b = Vector4.Lerp(Vector4.Lerp(G(x0, y0, z0 + 1), G(x0 + 1, y0, z0 + 1), tx), Vector4.Lerp(G(x0, y0 + 1, z0 + 1), G(x0 + 1, y0 + 1, z0 + 1), tx), ty);
            var v = Vector4.Lerp(a, b, tz);
            v.W *= LightVolume.EdgeFade(local, size, s.Fade);
            return v;
        }
        return LightVolume.Build(meshName, size, spacing, At);
    }
}
