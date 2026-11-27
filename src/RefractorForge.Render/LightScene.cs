using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Terrain;

namespace RefractorForge.Render;

/// <summary>
/// The whole level as ONE surface for light to travel in: the terrain, every placed object, and the holes in
/// leaves, fences and grilles.
///
/// <para>The earlier bakes kept the terrain apart from the objects - a height-field march asked "is the ground in
/// the way" and a triangle scene asked the same of the buildings. That answers visibility and nothing else. Light
/// that has bounced needs to know what it LANDED on, and outdoors the answer is nearly always the ground: the
/// underside of an awning, the shaded foot of a wall and the ceiling of a porch are lit by the sunlit earth in front
/// of them. With the terrain as triangles in the same tree as everything else, a ray simply finds the nearest
/// surface, and that surface has a colour.</para>
/// </summary>
public static class LightScene
{
    /// <summary>What a scene was built from, for the log and for tests.</summary>
    public sealed record Stats(int TerrainTriangles, int ObjectTriangles, int CutoutTriangles, int Masks, int TerrainStride);

    /// <summary>The scene and the terrain's shape as the scene has it.</summary>
    /// <param name="Scene">Null only when there is neither terrain nor objects.</param>
    /// <param name="TerrainStride">Heightmap samples per terrain cell edge in the scene. Ground sample points must be
    /// placed on THIS surface (see <see cref="Ground"/>), or they sit a little above or below it and shadow themselves.</param>
    /// <param name="Sources">Where each of the scene's triangles came from, in the scene's own triangle order - only
    /// when asked for. A preview render uses it to find the texture and the lightmap under a ray's hit.</param>
    public sealed record Built(RayScene? Scene, Stats Stats, int TerrainStride, IReadOnlyList<Source>? Sources = null);

    /// <summary>A scene triangle's origin: the placed object (its index in the level's object list, -1 for terrain),
    /// the material part of its mesh, and its three vertices in that mesh.</summary>
    public readonly record struct Source(int Object, int Part, int A, int B, int C);

    /// <summary>Terrain triangles the scene carries at most. A 1024-sample map is two million; past this the
    /// terrain is taken at every second sample, which no bake at a few texels per metre can tell apart.</summary>
    public const int MaxTerrainTriangles = 4_200_000;

    /// <summary>
    /// Build the level's scene.
    /// </summary>
    /// <param name="ground">The ground's colour as painted, BEFORE any light was baked into it - the terrain's
    /// reflectance. Laid over the whole world, texel (x, y) at world (x / width * worldSize, y / height * worldSize),
    /// as the terrain atlas is. Null gives the terrain a neutral earth colour.</param>
    /// <param name="holes">Leave out terrain that touches a raw-0 height sample: on a tunnel map the engine draws
    /// nothing there, and light falls through into the tunnel.</param>
    /// <param name="keepSources">Also record where every triangle came from (<see cref="Built.Sources"/>).</param>
    public static Built Build(Heightmap? hm, TerrainConfig? cfg, Texture2D? ground,
                              StaticObjectsFile? objs, MeshLibrary? meshes, bool holes = false, bool keepSources = false)
    {
        var tris = new List<(Vector3 A, Vector3 B, Vector3 C)>();
        var albedo = new List<Vector3>();
        var cut = new List<RayScene.Cutout?>();
        var sources = keepSources ? new List<Source>() : null;
        int stride = 1, terrainTris = 0;
        if (hm is not null && cfg is not null)
        {
            stride = TerrainStrideFor(hm);
            AddTerrain(tris, albedo, cut, hm, cfg, ground, holes, stride);
            terrainTris = tris.Count;
            if (sources is not null) for (int i = 0; i < terrainTris; i++) sources.Add(new Source(-1, 0, 0, 0, 0));
        }
        int masks = 0, cutTris = 0;
        if (objs is not null && meshes is not null)
            (masks, cutTris) = AddObjects(tris, albedo, cut, objs, meshes, sources);
        var scene = RayScene.Build(tris, albedo, cutTris > 0 ? cut : null);
        return new Built(scene, new Stats(terrainTris, tris.Count - terrainTris, cutTris, masks, stride), stride, sources);
    }

    /// <summary>The coarsest-necessary step through the heightmap that keeps the terrain inside its triangle budget.</summary>
    public static int TerrainStrideFor(Heightmap hm)
    {
        int stride = 1;
        while ((long)((hm.Width - 1) / stride) * ((hm.Height - 1) / stride) * 2 > MaxTerrainTriangles) stride *= 2;
        return stride;
    }

    // ---- terrain -----------------------------------------------------------------------------------------------

    /// <summary>The terrain's default reflectance, linear, when there is no ground picture to read it from.</summary>
    public static readonly Vector3 DefaultGround = new(0.16f, 0.15f, 0.11f);

    private static void AddTerrain(List<(Vector3, Vector3, Vector3)> tris, List<Vector3> albedo, List<RayScene.Cutout?> cut,
                                   Heightmap hm, TerrainConfig cfg, Texture2D? ground, bool holes, int stride)
    {
        int gw = (hm.Width - 1) / stride + 1, gh = (hm.Height - 1) / stride + 1;
        float sp = cfg.HorizontalSpacing * stride;
        var cellAlbedo = ground is not null ? GroundAlbedo(ground, gw - 1, gh - 1, sp * (gw - 1), sp * (gh - 1), cfg.WorldSize) : null;

        Vector3 P(int gx, int gy)
        {
            int sx = Math.Min(gx * stride, hm.Width - 1), sy = Math.Min(gy * stride, hm.Height - 1);
            return new Vector3(gx * sp, cfg.HeightToMeters(hm[sx, sy]), gy * sp);
        }
        bool Hole(int gx, int gy) => hm[Math.Min(gx * stride, hm.Width - 1), Math.Min(gy * stride, hm.Height - 1)] == 0;

        for (int gy = 0; gy < gh - 1; gy++)
            for (int gx = 0; gx < gw - 1; gx++)
            {
                // The same split TerrainMesh draws - the diagonal runs b-c - but wound the way Refractor's meshes
                // are, clockwise seen from above, so "back face" means the same thing on the ground as on a wall.
                Vector3 a = P(gx, gy), b = P(gx + 1, gy), c = P(gx, gy + 1), d = P(gx + 1, gy + 1);
                var col = cellAlbedo is null ? DefaultGround : cellAlbedo[gy * (gw - 1) + gx];
                bool ha = false, hb = false, hc = false, hd = false;
                if (holes) { ha = Hole(gx, gy); hb = Hole(gx + 1, gy); hc = Hole(gx, gy + 1); hd = Hole(gx + 1, gy + 1); }
                if (!(ha || hb || hc)) { tris.Add((a, b, c)); albedo.Add(col); cut.Add(null); }
                if (!(hb || hc || hd)) { tris.Add((b, d, c)); albedo.Add(col); cut.Add(null); }
            }
    }

    /// <summary>The mean linear reflectance of each terrain cell, read from the ground picture.</summary>
    private static Vector3[] GroundAlbedo(Texture2D ground, int cellsX, int cellsY, float spanX, float spanZ, float worldSize)
    {
        var sum = new Vector3[cellsX * cellsY];
        var n = new int[cellsX * cellsY];
        int w = ground.Width, h = ground.Height;
        // A few texels per cell settle a mean; reading all of a 4096 picture for a 512-cell terrain is 64 a cell.
        int step = Math.Max(1, Math.Min(w / Math.Max(cellsX * 2, 1), h / Math.Max(cellsY * 2, 1)));
        for (int y = 0; y < h; y += step)
        {
            float wz = (y + 0.5f) / h * worldSize;
            int cy = (int)(wz / spanZ * cellsY);
            if ((uint)cy >= (uint)cellsY) continue;
            for (int x = 0; x < w; x += step)
            {
                float wx = (x + 0.5f) / w * worldSize;
                int cx = (int)(wx / spanX * cellsX);
                if ((uint)cx >= (uint)cellsX) continue;
                int i = (y * w + x) * 4;
                sum[cy * cellsX + cx] += new Vector3(SrgbToLinear(ground.Rgba[i]), SrgbToLinear(ground.Rgba[i + 1]), SrgbToLinear(ground.Rgba[i + 2]));
                n[cy * cellsX + cx]++;
            }
        }
        for (int i = 0; i < sum.Length; i++)
        {
            if (n[i] == 0)
            {
                // A picture coarser than the terrain leaves cells no texel centre fell in: read the one under the
                // cell's own centre.
                int cx = i % cellsX, cy = i / cellsX;
                int x = Math.Clamp((int)((cx + 0.5f) / cellsX * spanX / worldSize * w), 0, w - 1);
                int y = Math.Clamp((int)((cy + 0.5f) / cellsY * spanZ / worldSize * h), 0, h - 1);
                int o = (y * w + x) * 4;
                sum[i] = new Vector3(SrgbToLinear(ground.Rgba[o]), SrgbToLinear(ground.Rgba[o + 1]), SrgbToLinear(ground.Rgba[o + 2]));
                n[i] = 1;
            }
            sum[i] = Vector3.Min(sum[i] / n[i], new Vector3(0.9f));
        }
        return sum;
    }

    /// <summary>
    /// The ground under a world position AS THE SCENE HAS IT: the height and the upward normal of the terrain
    /// triangle there. A bake that puts a sample on the bilinear height-field instead is a few centimetres off the
    /// triangle, and half of those samples are underneath it.
    /// </summary>
    public static (float Height, Vector3 Normal) Ground(Heightmap hm, TerrainConfig cfg, float wx, float wz, int stride = 1)
    {
        int gw = (hm.Width - 1) / stride + 1, gh = (hm.Height - 1) / stride + 1;
        float sp = cfg.HorizontalSpacing * stride;
        float fx = Math.Clamp(wx / sp, 0f, gw - 1.0001f), fz = Math.Clamp(wz / sp, 0f, gh - 1.0001f);
        int gx = (int)fx, gy = (int)fz;
        float u = fx - gx, v = fz - gy;
        float H(int x, int y) => cfg.HeightToMeters(hm[Math.Min(x * stride, hm.Width - 1), Math.Min(y * stride, hm.Height - 1)]);
        float ha = H(gx, gy), hb = H(gx + 1, gy), hc = H(gx, gy + 1), hd = H(gx + 1, gy + 1);
        float height; Vector3 n;
        if (u + v <= 1f)
        {
            height = ha + u * (hb - ha) + v * (hc - ha);
            n = new Vector3(-(hb - ha), sp, -(hc - ha));
        }
        else
        {
            height = hd + (1f - u) * (hc - hd) + (1f - v) * (hb - hd);
            n = new Vector3(-(hd - hc), sp, -(hd - hb));
        }
        return (height, Vector3.Normalize(n));
    }

    // ---- objects -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Does a material part take part in light transport, and how?
    /// <list type="bullet">
    /// <item><b>Blended glass</b> lets light through: left out.</item>
    /// <item><b>Portal planes</b> - the black quads BfVietnam stretches across a building's windows and doors so the
    /// inside reads dark from afar - are a render trick, not a wall: left out. Counted as solid they shut every
    /// window and the inside of the building bakes black.</item>
    /// <item><b>Cut-outs</b> - leaves, fences, grilles, barbed wire - are there only where their texture is.</item>
    /// <item>Everything else is solid.</item>
    /// </list>
    /// </summary>
    public enum Surface { None, Solid, Cutout }

    /// <summary>See <see cref="Surface"/>.</summary>
    public static Surface SurfaceOf(MeshLibrary.MaterialPart part)
    {
        if (part.Blend || part.Additive) return Surface.None;
        if (part.TextureName is { } tn && tn.Contains("portal", StringComparison.OrdinalIgnoreCase)) return Surface.None;
        if (!part.AlphaTest) return Surface.Solid;
        // A cut-out with nothing to cut it out by: a fade plane, or a texture that did not resolve. Tracing it solid
        // is the slab of shadow this class exists to get rid of.
        return part.Texture is null ? Surface.None : Surface.Cutout;
    }

    private static (int Masks, int CutTris) AddObjects(List<(Vector3, Vector3, Vector3)> tris, List<Vector3> albedo,
                                                       List<RayScene.Cutout?> cut, StaticObjectsFile objs, MeshLibrary meshes,
                                                       List<Source>? sources = null)
    {
        var meanCache = new Dictionary<MeshLibrary.MaterialPart, Vector3>();
        var maskCache = new Dictionary<MeshLibrary.MaterialPart, RayScene.AlphaMask?>();
        int cutTris = 0;
        for (int oi = 0; oi < objs.Objects.Count; oi++)
        {
            var o = objs.Objects[oi];
            var meshName = meshes.LodGeometryNames(o.Template).FirstOrDefault();
            if (meshName is null || !meshes.TryGet(meshName, out var m)) continue;
            var world = LevelScene.MeshWorld(o);
            var pos = m.Positions; var uvs = m.Uvs;
            for (int pi = 0; pi < m.Parts.Length; pi++)
            {
                var part = m.Parts[pi];
                var kind = SurfaceOf(part);
                if (kind == Surface.None) continue;
                if (!meanCache.TryGetValue(part, out var col)) meanCache[part] = col = LevelScene.MeanAlbedo(part);
                RayScene.AlphaMask? mask = null;
                if (kind == Surface.Cutout)
                {
                    if (!maskCache.TryGetValue(part, out mask))
                    {
                        var tex = part.Texture!;
                        maskCache[part] = mask = RayScene.AlphaMask.From(tex.Width, tex.Height, tex.Rgba, part.AlphaRef ?? 0.5f);
                    }
                    // No hole anywhere in the texture: solid after all, and it needs no test per ray.
                }
                bool haveUv = uvs is not null && uvs.Length >= pos.Length;
                var idx = part.Indices;
                for (int t = 0; t + 2 < idx.Length; t += 3)
                {
                    int a = idx[t], b = idx[t + 1], c = idx[t + 2];
                    if ((uint)a >= (uint)pos.Length || (uint)b >= (uint)pos.Length || (uint)c >= (uint)pos.Length) continue;
                    tris.Add((Vector3.Transform(pos[a], world), Vector3.Transform(pos[b], world), Vector3.Transform(pos[c], world)));
                    albedo.Add(col);
                    sources?.Add(new Source(oi, pi, a, b, c));
                    if (mask is not null && haveUv) { cut.Add(new RayScene.Cutout(mask, uvs![a], uvs[b], uvs[c])); cutTris++; }
                    else cut.Add(null);
                }
            }
        }
        return (maskCache.Values.Count(v => v is not null), cutTris);
    }

    internal static float SrgbToLinear(byte v)
    {
        float c = v / 255f;
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }
}
