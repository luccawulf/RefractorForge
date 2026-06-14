using System;
using System.Numerics;
using RefractorForge.Formats.Terrain;

namespace RefractorForge.Render;

/// <summary>
/// Renders a level's <c>ingamemap.dds</c> / menu thumbnail: a top-down image of the terrain shaded with
/// the real texture atlas (or material/height fallback), relief hill-shading from the heightmap, and a
/// water tint below the water level. Pure CPU (no GL) so it runs headlessly and is unit-testable; the
/// viewer calls the same code for its "Generate minimap" action.
/// </summary>
public static class Minimap
{
    // 16-colour material palette mirroring the terrain shader's matColor(), for the no-atlas fallback.
    private static readonly Vector3[] MatPalette =
    {
        new(0.85f,0.75f,0.45f), new(0.30f,0.70f,0.28f), new(0.50f,0.36f,0.22f), new(0.55f,0.55f,0.58f),
        new(0.20f,0.45f,0.68f), new(0.78f,0.58f,0.28f), new(0.42f,0.58f,0.30f), new(0.78f,0.30f,0.30f),
        new(0.28f,0.55f,0.58f), new(0.62f,0.62f,0.32f), new(0.52f,0.40f,0.62f), new(0.80f,0.68f,0.52f),
        new(0.32f,0.64f,0.48f), new(0.58f,0.46f,0.34f), new(0.72f,0.72f,0.74f), new(0.85f,0.40f,0.62f),
    };

    /// <param name="flipNorthUp">Put +Z (north) at the top of the image, matching the in-game map.</param>
    public static Texture2D Render(int size, Heightmap hm, TerrainConfig cfg,
                                   TerrainTexture? tex, MaterialMap? material = null, bool flipNorthUp = true)
    {
        if (size < 1) size = 1;
        var rgba = new byte[size * size * 4];
        var light = Vector3.Normalize(new Vector3(-0.6f, 1.0f, -0.5f));
        var water = new Vector3(0.20f, 0.40f, 0.60f);
        int hw = hm.Width, hh = hm.Height;
        float sp = cfg.HorizontalSpacing; if (sp <= 0f) sp = 1f;

        for (int py = 0; py < size; py++)
            for (int px = 0; px < size; px++)
            {
                float u = (px + 0.5f) / size;
                float v = (py + 0.5f) / size;
                if (flipNorthUp) v = 1f - v;   // image top -> max world Z

                // Base colour: real terrain atlas if available, else material palette, else flat.
                Vector3 col;
                if (tex is not null) col = tex.SampleUv(u, v);
                else if (material is not null)
                {
                    int mx = Math.Clamp((int)(u * material.Width), 0, material.Width - 1);
                    int my = Math.Clamp((int)(v * material.Height), 0, material.Height - 1);
                    col = MatPalette[material[mx, my] & 15];
                }
                else col = new Vector3(0.45f, 0.50f, 0.40f);

                // Relief hill-shade from the heightmap (central differences -> surface normal).
                int hx = Math.Clamp((int)(u * (hw - 1) + 0.5f), 0, hw - 1);
                int hy = Math.Clamp((int)(v * (hh - 1) + 0.5f), 0, hh - 1);
                float hL = cfg.HeightToMeters(hm[Math.Max(hx - 1, 0), hy]);
                float hR = cfg.HeightToMeters(hm[Math.Min(hx + 1, hw - 1), hy]);
                float hDn = cfg.HeightToMeters(hm[hx, Math.Max(hy - 1, 0)]);
                float hUp = cfg.HeightToMeters(hm[hx, Math.Min(hy + 1, hh - 1)]);
                var n = Vector3.Normalize(new Vector3(hL - hR, 2f * sp, hDn - hUp));
                col *= 0.5f + 0.5f * MathF.Max(0f, Vector3.Dot(n, light));

                // Water: tint below the water level, deeper = bluer.
                float hC = cfg.HeightToMeters(hm[hx, hy]);
                if (hC < cfg.WaterLevel)
                {
                    float depth = Math.Clamp((cfg.WaterLevel - hC) / 12f, 0f, 1f);
                    col = Vector3.Lerp(col, water, 0.45f + 0.4f * depth);
                }

                int i = (py * size + px) * 4;
                rgba[i] = Byte(col.X); rgba[i + 1] = Byte(col.Y); rgba[i + 2] = Byte(col.Z); rgba[i + 3] = 255;
            }
        return new Texture2D(size, size, rgba);
    }

    private static byte Byte(float c) => (byte)(Math.Clamp(c, 0f, 1f) * 255f + 0.5f);
}
