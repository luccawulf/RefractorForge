using System;
using System.Collections.Generic;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Mesh;

namespace RefractorForge.Formats.Con;

/// <summary>
/// The bulb: a small additive glow standing at the light itself, so a lamp reads as switched on and not only as the
/// pool it throws. This is what every retail night map does for its lamps - BfVietnam's <c>e_Streetlight</c> and
/// Dystopia City's <c>e_CityLampLight</c> are both an additively blended sprite of 1.5-4 m in the bulb's colour
/// (245/243/197 there) - because an additive quad brightens whatever is behind it regardless of the scene's lighting,
/// which is the one thing a lightmap cannot do.
///
/// Built as a mesh rather than a particle emitter: three quads crossed on the three axes, each two-sided, with the
/// glow material the muzzle flashes use (<c>RsWriter.Glow</c>). Seen from any direction at least two of them face
/// you, so it reads as a soft ball of light without needing to be a billboard, and it goes through the level-local
/// object recipe that is proven to load in both games - no dependence on how each game's effect system resolves a
/// texture. One template serves every light that shares a colour and size, so a street of lamps is one object type.
/// </summary>
public static class LampGlow
{
    /// <summary>A stable template name for a colour/size pair, so re-baking finds the glow it made last time.</summary>
    public static string TemplateName(Vec3 colour, float diameterMetres, float brightness)
    {
        int r = (int)Math.Round(Math.Clamp(colour.X, 0f, 1f) * 15f), g = (int)Math.Round(Math.Clamp(colour.Y, 0f, 1f) * 15f),
            b = (int)Math.Round(Math.Clamp(colour.Z, 0f, 1f) * 15f);
        int s = (int)Math.Round(Math.Clamp(diameterMetres, 0.25f, 60f) * 4f);
        int i = (int)Math.Round(Math.Clamp(brightness, 0.05f, 3f) * 10f);
        return $"rf_glow_{r:x}{g:x}{b:x}_{s}_{i}";
    }

    /// <summary>Every glow this recipe has ever made starts with this, which is how a re-bake sweeps the old ones.</summary>
    public const string Prefix = "rf_glow_";

    public static bool IsGlow(string template) => template is not null && template.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Build the glow object.
    /// </summary>
    /// <param name="levelName">Level folder name under &lt;baseSub&gt;/levels/.</param>
    /// <param name="colour">The lamp's colour, 0..1.</param>
    /// <param name="diameterMetres">Size of the ball of light.</param>
    /// <param name="brightness">Scales the texture; additive, so past ~1.5 the centre goes white like a bare bulb.</param>
    /// <param name="baseSub">"bf1942" or "BfVietnam".</param>
    /// <param name="encodeDds">RGBA (row-major from the top) -> DDS bytes; the encoder lives in the Render layer.</param>
    public static DecalObject.Built Build(string levelName, Vec3 colour, float diameterMetres, float brightness,
                                          string baseSub, Func<byte[], byte[]>? encodeDds, int textureSize = 128)
    {
        string name = TemplateName(colour, diameterMetres, brightness);
        string texName = name + "_tex";
        float d = Math.Clamp(diameterMetres, 0.25f, 60f);

        var rgba = Texture(textureSize, colour, brightness);
        byte[]? dds = encodeDds?.Invoke(rgba);
        var mesh = Cross(d, name + "_Material0");
        return DecalObject.Emit(levelName, name, mesh, texName, dds, baseSub: baseSub, additive: true);
    }

    /// <summary>
    /// The glow picture: a soft ball with a hot core. The falloff goes into BOTH colour and alpha - the blend is
    /// <c>sourcealpha / one</c>, so alpha decides how much is added and the colour decides of what; putting the
    /// shape in both keeps the rim from adding a faint full-colour disc. The core is pushed past the lamp colour
    /// toward white by <paramref name="brightness"/>, which is what a bulb looks like against a night sky.
    /// </summary>
    public static byte[] Texture(int size, Vec3 colour, float brightness)
    {
        var px = new byte[size * size * 4];
        float half = size * 0.5f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f - half) / half, dy = (y + 0.5f - half) / half;
                float r = MathF.Sqrt(dx * dx + dy * dy) / 0.97f;              // a transparent border, so the quad's edge never shows
                float f = r >= 1f ? 0f : MathF.Pow(1f - r, 1.6f);          // soft shoulder, reaching exactly zero at the rim
                float core = r >= 0.25f ? 0f : (1f - r / 0.25f);               // the bulb itself
                float k = MathF.Min(1f, f * brightness + core * core * 0.6f);
                int o = (y * size + x) * 4;
                px[o]     = (byte)Math.Clamp((int)(255f * MathF.Min(1f, colour.X * f * brightness + core * core)), 0, 255);
                px[o + 1] = (byte)Math.Clamp((int)(255f * MathF.Min(1f, colour.Y * f * brightness + core * core)), 0, 255);
                px[o + 2] = (byte)Math.Clamp((int)(255f * MathF.Min(1f, colour.Z * f * brightness + core * core)), 0, 255);
                px[o + 3] = (byte)Math.Clamp((int)(255f * k), 0, 255);
            }
        return px;
    }

    /// <summary>Three double-sided quads of side <paramref name="d"/>, crossed on the three axes, centred on the origin.</summary>
    private static ObjMesh Cross(float d, string material)
    {
        var mesh = new ObjMesh();
        var sub = new ObjSubMesh { Material = material };
        float h = d * 0.5f;
        (float, float)[] uv = { (0f, 1f), (1f, 1f), (1f, 0f), (0f, 0f) };

        void Quad(Vec3[] pos, Vec3 n)
        {
            for (int side = 0; side < 2; side++)
            {
                bool reverse = side == 1;
                int b = sub.Positions.Count;
                for (int i = 0; i < 4; i++)
                {
                    sub.Positions.Add(pos[i]);
                    sub.Normals.Add(reverse ? new Vec3(-n.X, -n.Y, -n.Z) : n);
                    sub.Uvs.Add(uv[i]);
                }
                if (!reverse) { sub.Faces.Add((b, b + 1, b + 2)); sub.Faces.Add((b, b + 2, b + 3)); }
                else { sub.Faces.Add((b, b + 2, b + 1)); sub.Faces.Add((b, b + 3, b + 2)); }
            }
        }
        Quad(new[] { new Vec3(-h, -h, 0), new Vec3(h, -h, 0), new Vec3(h, h, 0), new Vec3(-h, h, 0) }, new Vec3(0, 0, -1));
        Quad(new[] { new Vec3(0, -h, -h), new Vec3(0, -h, h), new Vec3(0, h, h), new Vec3(0, h, -h) }, new Vec3(1, 0, 0));
        Quad(new[] { new Vec3(-h, 0, -h), new Vec3(h, 0, -h), new Vec3(h, 0, h), new Vec3(-h, 0, h) }, new Vec3(0, 1, 0));

        mesh.SubMeshes.Add(sub);
        mesh.BoundingBox[0] = -h; mesh.BoundingBox[1] = -h; mesh.BoundingBox[2] = -h;
        mesh.BoundingBox[3] = h; mesh.BoundingBox[4] = h; mesh.BoundingBox[5] = h;
        return mesh;
    }
}
