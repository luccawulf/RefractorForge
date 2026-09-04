using System.Numerics;
using RefractorForge.Formats.Terrain;

namespace RefractorForge.Render;

/// <summary>
/// Builds a renderable triangle mesh from a heightmap, in world space:
/// x = column * spacing, y = height in meters, z = row * spacing.
/// <paramref name="stride"/> decimates for LOD (1 = full res). Engine-agnostic —
/// consumed by both the software rasterizer and the GPU viewer.
/// </summary>
public sealed class TerrainMesh
{
    public required Vector3[] Positions;
    public required Vector3[] Normals;
    public required int[] Indices;
    public int GridW;
    public int GridH;

    /// <param name="holes">Leave out every triangle that touches a raw-0 sample. That is how BfVietnam's tunnel
    /// system cuts the terrain: the entrances of Cedar Falls and Saigon68 sit on cells whose sample is exactly
    /// 0, and with <c>Game.isTunnelMap 1</c> the engine draws (and collides) nothing there. Off by default,
    /// because a plain map can legitimately hold 0 at its sea floor or edges.</param>
    public static TerrainMesh FromHeightmap(Heightmap hm, TerrainConfig cfg, int stride = 1, bool holes = false)
    {
        if (stride < 1) stride = 1;
        int gw = (hm.Width - 1) / stride + 1;
        int gh = (hm.Height - 1) / stride + 1;
        float sp = cfg.HorizontalSpacing * stride;

        var pos = new Vector3[gw * gh];
        for (int gy = 0; gy < gh; gy++)
            for (int gx = 0; gx < gw; gx++)
            {
                int sx = Math.Min(gx * stride, hm.Width - 1);
                int sy = Math.Min(gy * stride, hm.Height - 1);
                pos[gy * gw + gx] = new Vector3(gx * sp, cfg.HeightToMeters(hm[sx, sy]), gy * sp);
            }

        var idx = new int[(gw - 1) * (gh - 1) * 6];
        int k = 0;
        bool Hole(int gx, int gy) => hm[Math.Min(gx * stride, hm.Width - 1), Math.Min(gy * stride, hm.Height - 1)] == 0;
        for (int gy = 0; gy < gh - 1; gy++)
            for (int gx = 0; gx < gw - 1; gx++)
            {
                int a = gy * gw + gx, b = a + 1, c = a + gw, d = c + 1;
                if (holes)
                {
                    bool ha = Hole(gx, gy), hb = Hole(gx + 1, gy), hc = Hole(gx, gy + 1), hd = Hole(gx + 1, gy + 1);
                    if (!(ha || hb || hc)) { idx[k++] = a; idx[k++] = c; idx[k++] = b; }
                    if (!(hb || hc || hd)) { idx[k++] = b; idx[k++] = c; idx[k++] = d; }
                    continue;
                }
                idx[k++] = a; idx[k++] = c; idx[k++] = b;
                idx[k++] = b; idx[k++] = c; idx[k++] = d;
            }
        if (k < idx.Length) Array.Resize(ref idx, k);

        var nrm = new Vector3[pos.Length];
        for (int i = 0; i < idx.Length; i += 3)
        {
            int i0 = idx[i], i1 = idx[i + 1], i2 = idx[i + 2];
            var n = Vector3.Cross(pos[i1] - pos[i0], pos[i2] - pos[i0]);
            nrm[i0] += n; nrm[i1] += n; nrm[i2] += n;
        }
        for (int i = 0; i < nrm.Length; i++)
            nrm[i] = nrm[i].LengthSquared() > 1e-12f ? Vector3.Normalize(nrm[i]) : Vector3.UnitY;

        return new TerrainMesh { Positions = pos, Normals = nrm, Indices = idx, GridW = gw, GridH = gh };
    }
}
