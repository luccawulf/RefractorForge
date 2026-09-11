using System;
using System.Collections.Generic;
using System.Numerics;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;

namespace RefractorForge.Render;

/// <summary>
/// A small, fixed scene for checking a GPU lightmap shader against the CPU on the machine it will actually run on.
///
/// <para>The kernel's bookkeeping is proven bit-for-bit on the CPU by the test suite, but the arithmetic is the
/// GPU's own - its sin, cos, tan and sqrt are not .NET's, and drivers fuse multiply-adds - so GPU output is close
/// to the CPU's rather than identical. How close has to be MEASURED, per card, before a map is baked with it. This
/// scene exercises every term: a room with a holed roof on real hills (so the terrain march walks), self-shadowing,
/// two coloured neighbours to bounce off, a soft sun, bounded sky visibility and one bounce.</para>
/// </summary>
public static class LightmapGpuSelfTest
{
    public sealed record Scene(MeshLibrary.Mesh Mesh, Matrix4x4 World, Heightmap Hm, TerrainConfig Cfg, Vec3 Sun,
                               ObjectLightmapBaker.Advanced Advanced, int Size, int SubSamples);

    /// <summary>How far apart two bakes are. <see cref="Passed"/> is the bar a GPU must clear to be trusted.</summary>
    public sealed record Comparison(double MeanAbs, int Max, double FractionOver24, int Texels)
    {
        /// <summary>
        /// Close enough to bake with. A single shadow ray landing the other way changes one sample by 1/N of its
        /// sun term, which is a few levels once the sub-samples average; what must NOT happen is a systematic
        /// shift (the mean) or whole regions coming out different (many texels far apart). The limits are loose
        /// enough for honest floating-point differences and far too tight for a real bug to slip under.
        /// </summary>
        public bool Passed => MeanAbs < 2.0 && FractionOver24 < 0.01;
    }

    public static Scene Build()
    {
        var pos = new List<Vector3>(); var lm = new List<Vector2>(); var idx = new List<int>();
        int cell = 0; const int grid = 4;
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int b0 = pos.Count;
            pos.Add(a); pos.Add(b); pos.Add(c); pos.Add(d);
            float cx = (cell % grid) / (float)grid, cy = (cell / grid) / (float)grid, w = 1f / grid, m = 0.02f;
            lm.Add(new(cx + m, cy + m)); lm.Add(new(cx + w - m, cy + m));
            lm.Add(new(cx + w - m, cy + w - m)); lm.Add(new(cx + m, cy + w - m));
            idx.AddRange(new[] { b0, b0 + 1, b0 + 2, b0, b0 + 2, b0 + 3 });
            cell++;
        }
        float s = 10f, h = 4f;
        Quad(new(0, 0, 0), new(s, 0, 0), new(s, 0, s), new(0, 0, s));
        Quad(new(0, 0, 0), new(0, h, 0), new(s, h, 0), new(s, 0, 0));
        Quad(new(s, 0, 0), new(s, h, 0), new(s, h, s), new(s, 0, s));
        Quad(new(s, 0, s), new(s, h, s), new(0, h, s), new(0, 0, s));
        Quad(new(0, 0, s), new(0, h, s), new(0, h, 0), new(0, 0, 0));
        Quad(new(0, h, 0), new(s, h, 0), new(s, h, 3.5f), new(0, h, 3.5f));
        Quad(new(0, h, 6.5f), new(s, h, 6.5f), new(s, h, s), new(0, h, s));
        var part = new MeshLibrary.MaterialPart(idx.ToArray(), Vector3.One, null, false);
        var mesh = new MeshLibrary.Mesh(pos.ToArray(), new Vector2[pos.Count], new[] { part }) { LightmapUvs = lm.ToArray() };
        var world = Matrix4x4.CreateTranslation(95f, 20f, 95f);

        var hm = new Heightmap(64, 64);
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
                hm[x, y] = (ushort)Math.Clamp(400 + 300 * Math.Sin(x * 0.21) * Math.Cos(y * 0.17) + 250 * Math.Sin((x + y) * 0.09), 0, 1000);
        var cfg = new TerrainConfig { MaterialSize = 64, WorldSize = 256, YScale = 16f };

        var tris = new List<(Vector3, Vector3, Vector3)>();
        var alb = new List<Vector3>();
        foreach (var p in mesh.Parts)
            for (int i = 0; i + 2 < p.Indices.Length; i += 3)
            {
                tris.Add((Vector3.Transform(mesh.Positions[p.Indices[i]], world),
                          Vector3.Transform(mesh.Positions[p.Indices[i + 1]], world),
                          Vector3.Transform(mesh.Positions[p.Indices[i + 2]], world)));
                alb.Add(new Vector3(0.7f, 0.6f, 0.5f));
            }
        void Box(Vector3 lo, Vector3 hi, Vector3 colour)
        {
            Vector3 P(float x, float y, float z) => new(x, y, z);
            var f = new (Vector3, Vector3, Vector3)[]
            {
                (P(lo.X, lo.Y, hi.Z), P(hi.X, lo.Y, hi.Z), P(hi.X, hi.Y, hi.Z)), (P(lo.X, lo.Y, hi.Z), P(hi.X, hi.Y, hi.Z), P(lo.X, hi.Y, hi.Z)),
                (P(lo.X, lo.Y, lo.Z), P(lo.X, hi.Y, lo.Z), P(hi.X, hi.Y, lo.Z)), (P(lo.X, lo.Y, lo.Z), P(hi.X, hi.Y, lo.Z), P(hi.X, lo.Y, lo.Z)),
                (P(hi.X, lo.Y, lo.Z), P(hi.X, hi.Y, lo.Z), P(hi.X, hi.Y, hi.Z)), (P(hi.X, lo.Y, lo.Z), P(hi.X, hi.Y, hi.Z), P(hi.X, lo.Y, hi.Z)),
                (P(lo.X, lo.Y, lo.Z), P(lo.X, lo.Y, hi.Z), P(lo.X, hi.Y, hi.Z)), (P(lo.X, lo.Y, lo.Z), P(lo.X, hi.Y, hi.Z), P(lo.X, hi.Y, lo.Z)),
                (P(lo.X, hi.Y, lo.Z), P(lo.X, hi.Y, hi.Z), P(hi.X, hi.Y, hi.Z)), (P(lo.X, hi.Y, lo.Z), P(hi.X, hi.Y, hi.Z), P(hi.X, hi.Y, lo.Z)),
            };
            foreach (var t in f) { tris.Add(t); alb.Add(colour); }
        }
        Box(new(108f, 20f, 92f), new(114f, 29f, 112f), new(0.8f, 0.15f, 0.1f));
        Box(new(90f, 20f, 108f), new(106f, 26f, 114f), new(0.1f, 0.3f, 0.8f));
        var level = RayScene.Build(tris, alb)!;

        var adv = new ObjectLightmapBaker.Advanced(
            Scene: level, SunAngularDiameterDeg: 2.5f, SunSamples: 32, SkySamples: 48, AoRadius: 2.5f,
            AoStrength: 0.6f, SkyFill: 0.12f, BounceSamples: 24, BounceDepth: 1, BounceDistance: 30f,
            SunColour: new Vector3(0.9f, 0.85f, 0.7f), DenoiseIterations: 0);
        return new Scene(mesh, world, hm, cfg, new Vec3(0.35f, 0.55f, -0.76f), adv, Size: 128, SubSamples: 3);
    }

    /// <summary>Bake the scene with a given shader - null is the CPU.</summary>
    public static Texture2D? Bake(Scene s, ILightmapShader? shader, System.Threading.CancellationToken cancel = default)
        => ObjectLightmapBaker.Bake(s.Mesh, s.World, s.Hm, s.Cfg, s.Sun, s.Size, ambient: 0f, samples: s.SubSamples,
                                    advanced: s.Advanced, shader: shader, cancel: cancel);

    public static Comparison Compare(Texture2D a, Texture2D b)
    {
        long sum = 0; int max = 0, over = 0, n = 0;
        for (int i = 0; i < a.Width * a.Height; i++)
        {
            int d = Math.Abs(a.Rgba[i * 4] - b.Rgba[i * 4]);
            sum += d; n++;
            if (d > max) max = d;
            if (d > 24) over++;
        }
        return new Comparison(n == 0 ? 0 : sum / (double)n, max, n == 0 ? 0 : over / (double)n, n);
    }
}
