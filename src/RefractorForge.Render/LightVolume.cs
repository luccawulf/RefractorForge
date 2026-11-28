using System;
using System.Collections.Generic;
using System.Numerics;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Mesh;
using RefractorForge.Formats.Rfa;

namespace RefractorForge.Render;

/// <summary>
/// A volume of darkness, haze or light for a game that has none: a lattice of nearly transparent planes.
///
/// <para>The trick is a Battlefield 1942 modder's. Fill a box with flat planes on all three axes, each at a few
/// percent opacity; a line of sight through the box crosses many of them and their tints add up with distance, as
/// fog does. Whatever stands inside - a soldier, a vehicle - is behind some of the planes and is tinted with the
/// rest, which no lightmap can do for a moving thing.</para>
///
/// <para>Measured in BfVietnam (the fifth calibration level, 2026-11-28): unlit, blended, depth-write-off planes at
/// 3 % each left 0.51 of a known board seen through about twenty of them, on every grey level - plain
/// <c>(1 - alpha)^n</c>; an alpha that varies across the texture shapes the volume; an added (glow) material draws
/// too; and a soldier inside went dark with it. A box of uniform planes shows its lattice on its faces and ends on
/// a hard edge, so a volume here always fades to nothing toward its boundary.</para>
///
/// <para><b>The field is three-dimensional</b>, so each plane gets its own tile of one atlas: the tile holds the
/// field sampled across that plane. Tiles are a power of two on a power-of-two grid, so the mip chain averages a
/// tile with itself for as many levels as the tile has; the plane's UVs stop half a texel inside the tile, so
/// bilinear filtering never reaches a neighbour.</para>
/// </summary>
public static class LightVolume
{
    /// <summary>Texels along a tile's side.</summary>
    public const int Tile = 32;

    /// <param name="Sm">The mesh: one quad per plane, 32-byte vertices. Its shader must draw both sides.</param>
    /// <param name="Atlas">Colour and per-plane opacity, one tile per plane.</param>
    /// <param name="Planes">How many planes the lattice has.</param>
    public sealed record Built(byte[] Sm, Texture2D Atlas, int Planes);

    /// <summary>
    /// Build a volume's lattice.
    /// </summary>
    /// <param name="size">The box: x across, y up, z deep. The mesh's origin is the middle of its floor.</param>
    /// <param name="spacing">Metres between planes.</param>
    /// <param name="field">Colour (rgb, 0..1) and ONE plane's opacity (a, 0..1) at a point of the box, in the
    /// mesh's own coordinates: x in [-size.X/2, size.X/2], y in [0, size.Y], z in [-size.Z/2, size.Z/2].</param>
    public static Built Build(string meshName, Vector3 size, float spacing, Func<Vector3, Vector4> field)
    {
        spacing = MathF.Max(spacing, 0.1f);
        float hx = size.X * 0.5f, hz = size.Z * 0.5f;
        // Each plane: its corner, and the two edges its tile runs along (u then v).
        var planes = new List<(Vector3 Origin, Vector3 U, Vector3 V)>();
        foreach (float z in Steps(-hz, hz, spacing)) planes.Add((new(-hx, 0, z), new(size.X, 0, 0), new(0, size.Y, 0)));
        foreach (float x in Steps(-hx, hx, spacing)) planes.Add((new(x, 0, -hz), new(0, 0, size.Z), new(0, size.Y, 0)));
        // No plane on the floor itself: it would lie in the terrain.
        foreach (float y in Steps(0f, size.Y, spacing)) if (y > 1e-3f) planes.Add((new(-hx, y, -hz), new(size.X, 0, 0), new(0, 0, size.Z)));

        int cols = (int)MathF.Ceiling(MathF.Sqrt(planes.Count)), rows = (planes.Count + cols - 1) / cols;
        int w = Pow2(cols * Tile), h = Pow2(rows * Tile);
        var px = new byte[w * h * 4];
        var P = new List<Vec3>(); var N = new List<Vec3>(); var Uv = new List<(float, float)>(); var Lm = new List<(float, float)>();
        var F = new List<(int, int, int)>();
        for (int k = 0; k < planes.Count; k++)
        {
            var (o, eu, ev) = planes[k];
            int tx = k % cols * Tile, ty = k / cols * Tile;
            for (int j = 0; j < Tile; j++)
                for (int i = 0; i < Tile; i++)
                {
                    var c = field(o + eu * (i / (float)(Tile - 1)) + ev * (j / (float)(Tile - 1)));
                    int at = ((ty + j) * w + tx + i) * 4;
                    px[at] = B(c.X); px[at + 1] = B(c.Y); px[at + 2] = B(c.Z); px[at + 3] = B(c.W);
                }
            float u0 = (tx + 0.5f) / w, u1 = (tx + Tile - 0.5f) / w, v0 = (ty + 0.5f) / h, v1 = (ty + Tile - 0.5f) / h;
            int b0 = P.Count;
            void Vert(Vector3 p, float u, float v) { P.Add(new Vec3(p.X, p.Y, p.Z)); N.Add(new Vec3(0, 1, 0)); Uv.Add((u, v)); Lm.Add((0f, 0f)); }
            Vert(o, u0, v0); Vert(o + eu, u1, v0); Vert(o + eu + ev, u1, v1); Vert(o + ev, u0, v1);
            F.Add((b0, b0 + 1, b0 + 2)); F.Add((b0, b0 + 2, b0 + 3));
        }
        var sections = new[] { new StandardMeshWriter.LitSection(meshName + "_Material0", P, N, Uv, Lm, F) };
        var sm = StandardMeshWriter.WriteLit(sections, StandardMeshWriter.LitLayout.Plain32, false,
                                             new[] { -hx, 0f, -hz, hx, size.Y, hz });
        return new Built(sm, new Texture2D(w, h, px), planes.Count);
    }

    /// <summary>The volume's shader: unlit, both sides, blended or added, no depth write - the states measured to draw.</summary>
    public static string Shader(string meshName, string texture, bool additive)
        => RsWriter.Write(new[]
        {
            additive
                ? RsWriter.Glow(meshName + "_Material0", texture)
                : new RsWriter.Material(meshName + "_Material0", texture, new Vec3(1, 1, 1), Transparent: true, AlphaTestRef: 0f,
                                        TwoSided: true, Lighting: false, LightingSpecular: false, DepthWrite: false, SortedBlend: true),
        });

    /// <summary>The geometry script. All six LOD-distance lines: the first fog level, which had one, drew nothing.</summary>
    public static string GeometryScript(string meshName, string levelName, string baseSub)
        => $"GeometryTemplate.create StandardMesh {meshName}\r\nGeometryTemplate.file ../{baseSub}/levels/{levelName}/StandardMesh/{meshName}\r\n" +
           "GeometryTemplate.setLodDistance 0 0\r\nGeometryTemplate.setLodDistance 1 50\r\nGeometryTemplate.setLodDistance 2 100\r\n" +
           "GeometryTemplate.setLodDistance 3 200\r\nGeometryTemplate.setLodDistance 4 400\r\nGeometryTemplate.setLodDistance 5 800\r\n\r\n";

    /// <summary>The object script: a thing to place that nothing collides with.</summary>
    public static string ObjectScript(string meshName)
        => $"ObjectTemplate.create SimpleObject {meshName}\r\nObjectTemplate.geometry {meshName}\r\nObjectTemplate.HasCollisionPhysics 0\r\n\r\n";

    /// <summary>
    /// How much of a volume remains at a point, 0 at the box's faces rising to 1 over <paramref name="fade"/> metres
    /// inside them - what keeps a volume from ending on a visible wall. The floor does not fade: a volume stands on
    /// the ground.
    /// </summary>
    public static float EdgeFade(Vector3 p, Vector3 size, float fade)
    {
        if (fade <= 1e-4f) return 1f;
        float d = MathF.Min(MathF.Min(size.X * 0.5f - MathF.Abs(p.X), size.Z * 0.5f - MathF.Abs(p.Z)), size.Y - p.Y);
        float t = Math.Clamp(d / fade, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>One plane's opacity for a medium that should leave <c>exp(-density x metres)</c> of what is behind
    /// it: <c>1 - exp(-density x spacing)</c>, held to <paramref name="max"/>.</summary>
    public static float PlaneAlpha(float density, float spacing, float max = 0.25f)
        => Math.Clamp(1f - MathF.Exp(-MathF.Max(density, 0f) * spacing), 0f, max);

    private static IEnumerable<float> Steps(float from, float to, float step)
    {
        int n = Math.Max(1, (int)MathF.Round((to - from) / step));
        for (int i = 0; i <= n; i++) yield return from + (to - from) * i / n;
    }

    private static int Pow2(int v) { int p = 1; while (p < v) p <<= 1; return p; }
    private static byte B(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
}
