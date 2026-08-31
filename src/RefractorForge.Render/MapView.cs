using System.Numerics;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;

namespace RefractorForge.Render;

/// <summary>
/// A top-down picture of a level, meant to be LOOKED at rather than shipped to the game.
///
/// <see cref="Minimap"/> already renders the in-game map, and this reuses it for the ground so both agree. What it
/// adds is the things a person (or an assistant) needs in order to decide where to build: relief that reads as
/// relief, where the objects actually are, and a coordinate grid — because "the clearing east of the bridge" is
/// useless without knowing what X and Z that is.
/// </summary>
public static class MapView
{
    /// <summary>Render the level top-down: ground, hill shading, object dots and a labelled grid.</summary>
    /// <param name="size">Output edge in pixels.</param>
    /// <param name="highlight">Objects drawn in a second colour (e.g. the ones just placed).</param>
    public static Texture2D Render(int size, Heightmap hm, TerrainConfig cfg, TerrainTexture? tex,
                                   MaterialMap? material, IEnumerable<StaticObject>? objects,
                                   IEnumerable<Vec3>? highlight = null, bool grid = true)
    {
        if (size < 64) size = 64;
        var img = Minimap.Render(size, hm, cfg, tex, material);
        var px = img.Rgba;
        float ws = cfg.WorldSize <= 0 ? 1f : cfg.WorldSize;

        // Minimap is north-up: +Z is at the TOP, so a world Z maps to (1 - Z/ws) down the image.
        (int X, int Y) ToPixel(float wx, float wz)
            => (Math.Clamp((int)(wx / ws * size), 0, size - 1),
                Math.Clamp((int)((1f - wz / ws) * size), 0, size - 1));

        if (grid)
        {
            // A line every 256 m, brighter every 512 m. Without this the picture is pretty and unusable, because
            // nothing in it can be turned back into a coordinate.
            for (int i = 0; i * 256f <= ws; i++)
            {
                float w = i * 256f;
                bool major = i % 2 == 0;
                byte a = (byte)(major ? 90 : 45);
                var (gx, _) = ToPixel(w, 0);
                var (_, gy) = ToPixel(0, w);
                for (int p = 0; p < size; p++)
                {
                    Blend(px, size, gx, p, 255, 255, 255, a);
                    Blend(px, size, p, gy, 255, 255, 255, a);
                }
            }
        }

        if (objects is not null)
            foreach (var o in objects)
            {
                var (x, y) = ToPixel(o.Position.X, o.Position.Z);
                Dot(px, size, x, y, 1, 30, 30, 34, 200);          // a dark core so dots read on pale ground
                Dot(px, size, x, y, 0, 255, 232, 120, 255);
            }

        if (highlight is not null)
            foreach (var h in highlight)
            {
                var (x, y) = ToPixel(h.X, h.Z);
                Dot(px, size, x, y, 2, 20, 20, 20, 210);
                Dot(px, size, x, y, 1, 255, 90, 70, 255);
            }

        return img;
    }

    private static void Dot(byte[] px, int size, int cx, int cy, int r, byte cr, byte cg, byte cb, byte a)
    {
        for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
                if (dx * dx + dy * dy <= r * r) Blend(px, size, cx + dx, cy + dy, cr, cg, cb, a);
    }

    private static void Blend(byte[] px, int size, int x, int y, byte r, byte g, byte b, byte a)
    {
        if (x < 0 || y < 0 || x >= size || y >= size) return;
        int o = (y * size + x) * 4;
        float t = a / 255f;
        px[o] = (byte)(px[o] * (1 - t) + r * t);
        px[o + 1] = (byte)(px[o + 1] * (1 - t) + g * t);
        px[o + 2] = (byte)(px[o + 2] * (1 - t) + b * t);
        px[o + 3] = 255;
    }
}
