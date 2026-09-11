using System.Numerics;
using System.Text;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// BfVietnam's buildings stretch a black "portal" quad across every window and door (<c>texture/portal_black</c>,
/// <c>texturefade true</c>) so an opening reads as a dark interior from afar and fades as the player walks up. 51 of
/// the 361 lightmapped meshes carry one - every BuildBig/BuildMed/BuildSma building, the fishing huts, the temples.
/// A building's bake traced its OWN portals as walls, so no sun came through a single window and the whole inside
/// baked black (O_BuildMedDemo01 on al_vietnas: 7,576 texels of interior light missing). These pin the fix.
/// </summary>
public class PortalShadowTests
{
    /// <summary>A floor, and a quad hovering over it. With the sun straight overhead the quad shades the floor -
    /// unless it is a portal, which the game draws as a fading plane and which must let the light through.</summary>
    private static MeshLibrary.Mesh FloorUnder(bool quadIsPortal)
    {
        var pos = new[]
        {
            new Vector3(0, 0, 0), new Vector3(4, 0, 0), new Vector3(4, 0, 4), new Vector3(0, 0, 4),    // floor, facing up
            new Vector3(-1, 2, -1), new Vector3(5, 2, -1), new Vector3(5, 2, 5), new Vector3(-1, 2, 5), // the quad above it
        };
        var lm = new[]
        {
            new Vector2(0.05f, 0.05f), new Vector2(0.45f, 0.05f), new Vector2(0.45f, 0.45f), new Vector2(0.05f, 0.45f),
            new Vector2(0.55f, 0.55f), new Vector2(0.95f, 0.55f), new Vector2(0.95f, 0.95f), new Vector2(0.55f, 0.95f),
        };
        var floor = new MeshLibrary.MaterialPart(new[] { 0, 2, 1, 0, 3, 2 }, Vector3.One, null, false);
        var quad = new MeshLibrary.MaterialPart(new[] { 4, 5, 6, 4, 6, 7 }, Vector3.One, null, AlphaTest: quadIsPortal,
                                                TextureName: "texture/portal_black", Foliage: quadIsPortal);
        return new MeshLibrary.Mesh(pos, new Vector2[pos.Length], new[] { floor, quad }) { LightmapUvs = lm };
    }

    private static double FloorBrightness(MeshLibrary.Mesh mesh)
    {
        var hm = new Heightmap(64, 64);
        var cfg = new TerrainConfig { MaterialSize = 64, WorldSize = 256, YScale = 1f };
        var world = Matrix4x4.CreateTranslation(100f, 10f, 100f);
        var t = ObjectLightmapBaker.Bake(mesh, world, hm, cfg, new Vec3(0f, 1f, 0f), size: 64, ambient: 0f, samples: 1)!;
        long sum = 0; int n = 0;
        for (int y = 8; y < 24; y++)          // well inside the floor's chart (UV 0.05..0.45 of 64 px)
            for (int x = 8; x < 24; x++) { sum += t.Rgba[(y * t.Width + x) * 4]; n++; }
        return sum / (double)n;
    }

    [Fact]
    public void A_portal_plane_lets_the_sun_through_to_the_floor_below()
    {
        double solid = FloorBrightness(FloorUnder(quadIsPortal: false));
        double portal = FloorBrightness(FloorUnder(quadIsPortal: true));
        Assert.True(solid < 20, $"a real roof must shade the floor (got {solid:0})");
        Assert.True(portal > 200, $"a portal plane must not (got {portal:0})");
    }

    /// <summary>The whole chain on a real shader: a <c>texturefade</c> material comes out of the mesh library as a
    /// part that does not cast - the flag the bake's rule reads - while a plain material still does.</summary>
    [Fact]
    public void A_texturefade_shader_makes_a_part_that_does_not_cast()
    {
        var rs = "subshader \"crate_Material0\" \"StandardMesh/Default\"\r\n{\r\n\tlighting true;\r\n\ttexturefade true;\r\n" +
                 "\tsortedblend true;\r\n\tcustomfloatvalue 90;\r\n\ttexture \"texture/portal_black\";\r\n}\r\n";
        var lib = MeshLibrary.Open(Path.Combine(Path.GetTempPath(), "rf_none_" + Guid.NewGuid().ToString("N")[..8]));
        Assert.True(lib.TryBuildMeshFromSm(LightmapReadyTestsAccess.Box(), rs, out var portal));
        Assert.False(LevelScene.CastsShadow(Assert.Single(portal.Parts)));

        // A plain wall material casts. (It needs a wall's texture as well: the library also reads "portal" in a
        // texture NAME as a cutout, so a portal_black part is excluded even without texturefade.)
        var wallRs = rs.Replace("\ttexturefade true;\r\n", "").Replace("texture/portal_black", "texture/O_BuildMedIntact01_A");
        Assert.True(lib.TryBuildMeshFromSm(LightmapReadyTestsAccess.Box(), wallRs, out var wall));
        Assert.True(LevelScene.CastsShadow(Assert.Single(wall.Parts)));
    }
}

/// <summary>The 32-byte test box, shared with <see cref="LightmapReadyTests"/>.</summary>
internal static class LightmapReadyTestsAccess
{
    public static byte[] Box()
    {
        var p = new List<Vector3>();
        var idx = new List<ushort>();
        void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int b0 = p.Count;
            p.Add(a); p.Add(b); p.Add(c); p.Add(d);
            foreach (var t in new[] { (0, 1, 2), (0, 2, 3) })
            { idx.Add((ushort)(b0 + t.Item3)); idx.Add((ushort)(b0 + t.Item2)); idx.Add((ushort)(b0 + t.Item1)); }
        }
        float x = 1.5f, y = 2f, z = 2.5f;
        Face(new(-x, -y, -z), new(x, -y, -z), new(x, y, -z), new(-x, y, -z));
        Face(new(x, -y, z), new(-x, -y, z), new(-x, y, z), new(x, y, z));
        Face(new(-x, -y, z), new(-x, -y, -z), new(-x, y, -z), new(-x, y, z));
        Face(new(x, -y, -z), new(x, -y, z), new(x, y, z), new(x, y, -z));
        Face(new(-x, y, -z), new(x, y, -z), new(x, y, z), new(-x, y, z));
        Face(new(-x, -y, z), new(x, -y, z), new(x, -y, -z), new(-x, -y, -z));

        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write((uint)10); w.Write(new byte[4]);
        w.Write(-x); w.Write(-y); w.Write(-z); w.Write(x); w.Write(y); w.Write(z);
        w.Write((byte)0);
        w.Write((uint)0);
        w.Write((uint)1); w.Write((uint)1);
        var nm = Encoding.Latin1.GetBytes("crate_Material0");
        w.Write((uint)nm.Length); w.Write(nm); w.Write(new byte[12]);
        w.Write(4u); w.Write((uint)1041); w.Write((uint)32);
        w.Write((uint)p.Count); w.Write((uint)idx.Count); w.Write((uint)0);
        foreach (var v in p)
        {
            w.Write(v.X); w.Write(v.Y); w.Write(v.Z);
            var n = Vector3.Normalize(v);
            w.Write(n.X); w.Write(n.Y); w.Write(n.Z);
            w.Write(0f); w.Write(0f);
        }
        foreach (var i in idx) w.Write(i);
        w.Write((uint)0); w.Write((uint)0);
        w.Flush();
        return ms.ToArray();
    }
}
