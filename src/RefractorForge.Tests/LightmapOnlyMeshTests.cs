using System.Numerics;
using System.Text;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Mesh;
using RefractorForge.Formats.Rfa;
using RefractorForge.Render;
using Xunit;
using LC = RefractorForge.Render.LightCalibration;

namespace RefractorForge.Tests;

/// <summary>
/// A mesh the engine lights from its lightmap alone: every LOD on the 40-byte fixed-function vertex, one normal on
/// every vertex (<see cref="LightmapUnwrapper.LightmapOnlyPlans"/>, <see cref="StandardMeshRewriter"/>), and the
/// third calibration level that asks the game whether that works.
/// </summary>
public class LightmapOnlyMeshTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rf_lmonly_" + Guid.NewGuid().ToString("N"));
    public LightmapOnlyMeshTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // A box with a different normal, texture UV and lightmap UV on every vertex, in the given layout.
    private static byte[] Box(StandardMeshWriter.LitLayout layout, string material = "box_Material0")
    {
        var p = new List<Vec3>(); var n = new List<Vec3>(); var uv = new List<(float, float)>(); var lm = new List<(float, float)>();
        var f = new List<(int, int, int)>();
        void Face(Vector3 nn, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int b0 = p.Count;
            foreach (var v in new[] { a, b, c, d })
            {
                p.Add(new Vec3(v.X, v.Y, v.Z)); n.Add(new Vec3(nn.X, nn.Y, nn.Z));
                uv.Add((p.Count * 0.01f, p.Count * 0.02f)); lm.Add((p.Count * 0.03f, 1f - p.Count * 0.03f));
            }
            f.Add((b0, b0 + 1, b0 + 2)); f.Add((b0, b0 + 2, b0 + 3));
        }
        float x = 1f, y = 1.5f, z = 2f;
        Face(-Vector3.UnitZ, new(-x, -y, -z), new(x, -y, -z), new(x, y, -z), new(-x, y, -z));
        Face(Vector3.UnitZ, new(x, -y, z), new(-x, -y, z), new(-x, y, z), new(x, y, z));
        Face(-Vector3.UnitX, new(-x, -y, z), new(-x, -y, -z), new(-x, y, -z), new(-x, y, z));
        Face(Vector3.UnitX, new(x, -y, -z), new(x, -y, z), new(x, y, z), new(x, y, -z));
        Face(Vector3.UnitY, new(-x, y, -z), new(x, y, -z), new(x, y, z), new(-x, y, z));
        Face(-Vector3.UnitY, new(-x, -y, z), new(x, -y, z), new(x, -y, -z), new(-x, -y, -z));
        var sec = new StandardMeshWriter.LitSection(material, p, n, uv, lm, f);
        return StandardMeshWriter.WriteLit(new[] { sec }, layout, layout != StandardMeshWriter.LitLayout.Plain32,
                                           new[] { -x, -y, -z, x, y, z });
    }

    [Fact]
    public void A_tangent_frame_mesh_drops_to_40_bytes_keeping_everything_but_the_frame()
    {
        var bytes = Box(StandardMeshWriter.LitLayout.Tangent64);
        var sm = StandardMesh.Parse(bytes);
        var src = sm.Lods[0][0];
        Assert.Equal(64u, src.VertexByteSize);

        var kept = StandardMesh.Parse(StandardMeshRewriter.Write(bytes, sm, LightmapUnwrapper.LightmapOnlyPlans(sm, null, null),
                                                                 new StandardMeshRewriter.Options(SetQFlag: true)));
        var m = kept.Lods[0][0];
        Assert.Equal(40u, m.VertexByteSize);
        Assert.Equal(9233u, m.VertexFormat);
        Assert.Equal(1, kept.QFlag);
        Assert.Equal(src.NumVertices, m.NumVertices);
        Assert.Equal(src.Vertices, m.Vertices);
        Assert.Equal(src.Normals, m.Normals);                         // its own normals when none is given
        Assert.Equal(src.Uvs, m.Uvs);
        Assert.Equal(src.LightmapUvs, m.LightmapUvs);                 // the unwrap it already had
        Assert.Equal(src.RawIndices, m.RawIndices);

        // With a normal: that one on every vertex, and nothing else different.
        var up = StandardMesh.Parse(StandardMeshRewriter.Write(bytes, sm, LightmapUnwrapper.LightmapOnlyPlans(sm, null, (0f, 1f, 0f)),
                                                               new StandardMeshRewriter.Options(SetQFlag: true)));
        var u = up.Lods[0][0];
        Assert.All(u.Normals, nn => Assert.Equal(new Vec3(0f, 1f, 0f), nn));
        Assert.Equal(src.Vertices, u.Vertices);
        Assert.Equal(src.LightmapUvs, u.LightmapUvs);
    }

    [Fact]
    public void A_mesh_with_no_unwrap_gets_one_and_the_same_normal_on_every_vertex()
    {
        var bytes = Box(StandardMeshWriter.LitLayout.Plain32);
        var sm = StandardMesh.Parse(bytes);
        var r = LightmapUnwrapper.Unwrap(sm);
        Assert.Equal(LightmapUnwrapper.UnwrapStatus.Ok, r.Status);
        var plans = LightmapUnwrapper.LightmapOnlyPlans(sm, LightmapUnwrapper.AllLodPlans(sm, r.Lod0Plan!), (0f, 1f, 0f));
        var outSm = StandardMesh.Parse(StandardMeshRewriter.Write(bytes, sm, plans, new StandardMeshRewriter.Options(SetQFlag: true)));
        var m = outSm.Lods[0][0];
        Assert.Equal(40u, m.VertexByteSize);
        Assert.All(m.Normals, nn => Assert.Equal(new Vec3(0f, 1f, 0f), nn));
        Assert.Contains(m.LightmapUvs, t => t.U != 0f || t.V != 0f);
        // A 40-byte mesh that already has an unwrap is only given the normal.
        var b40 = Box(StandardMeshWriter.LitLayout.Lightmap40);
        var s40 = StandardMesh.Parse(b40);
        var o40 = StandardMesh.Parse(StandardMeshRewriter.Write(b40, s40, LightmapUnwrapper.LightmapOnlyPlans(s40, null, (0f, 1f, 0f))));
        Assert.Equal(s40.Lods[0][0].LightmapUvs, o40.Lods[0][0].LightmapUvs);
        Assert.All(o40.Lods[0][0].Normals, nn => Assert.Equal(new Vec3(0f, 1f, 0f), nn));
    }

    [Fact]
    public void The_third_level_stands_six_steered_boards_over_rewritten_retail_meshes()
    {
        var fake = new LightCalibrationTests();
        try
        {
            var outDir = Path.Combine(_dir, "steer");
            Directory.CreateDirectory(outDir);
            var o = new LC.Options(fake.FakeLevelPath(), "rf_cal3", outDir, Full: false, new LC.Site(128, 160, 126))
            {
                Steer = true,
                Sun = new Vector3(3f, 3f, 3f), Back = new Vector3(3f, 3f, 3f),            // clamp whatever the fake sun's height
                Ambient = new Vector3(0.15f, 0.15f, 0.3f), LmAmbient = new Vector3(0.1f),
                RealMeshes = new[]
                {
                    new LC.RealMesh("rfcal_bunk_s_m1", Box(StandardMeshWriter.LitLayout.Tangent64, "bunk_Material0"), null, LC.ShaderStyle.Plain, true, "64 -> 40, up"),
                    new LC.RealMesh("rfcal_bush_t_m1", Box(StandardMeshWriter.LitLayout.Plain32, "bush_Material0"), "C06F_Tree", LC.ShaderStyle.Vegetation, true, "32 -> 40, up"),
                },
            };
            var m = LC.BuildLevel(o);
            var arc = new RefractorFlatArchive(Path.Combine(outDir, "rf_cal3.rfa"));
            const string p = "BfVietnam/levels/rf_cal3/";
            byte[] Read(string rel) => arc.TryGetEntry(p + rel, out var e) ? arc.Read(e) : throw new FileNotFoundException(rel);
            string S(string rel) => Encoding.Latin1.GetString(Read(rel));

            Assert.Equal(new[] { "A", "B", "C", "D", "E", "F" }, m.Boards.Select(b => b.Id));
            Assert.All(m.Boards.Take(5), b => Assert.True(b.Steered));
            Assert.Equal(180, m.Boards[5].Yaw);
            Assert.False(m.Boards[5].Steered);
            Assert.Equal(new[] { "Plain", "Plain", "RetailLike", "Vegetation", "PalmAsIs", "Plain" }, m.Boards.Select(b => b.Style));
            Assert.Equal(new[] { "Grey24", "Rgb24", "Grey24", "Grey24", "Grey24", "Grey24" }, m.Boards.Select(b => b.Lightmap));

            // The steered boards' meshes: every patch normal straight up; the turned board's are not.
            foreach (var mesh in new[] { "rfcal_up_m1", "rfcal_upr_m1", "rfcal_upv_m1", "rfcal_upp_m1" })
            {
                var sm = StandardMesh.Parse(Read($"StandardMesh/{mesh}.sm"));
                Assert.All(sm.Lods[0][0].Normals, nn => Assert.Equal(new Vec3(0f, 1f, 0f), nn));
                Assert.All(sm.Lods[0], x => Assert.Equal(40u, x.VertexByteSize));
            }
            Assert.Contains(StandardMesh.Parse(Read("StandardMesh/rfcal_pv_m1.sm")).Lods[0][0].Normals, nn => nn.Y < 0.5f);
            Assert.Contains("selfillum .3 .3 .3", S("StandardMesh/rfcal_upp_m1.rs"));
            Assert.Contains("alphatestref 0.45", S("StandardMesh/rfcal_upv_m1.rs"));
            Assert.Contains("normalmap \"texture/rfcal_gloss_n\"", S("StandardMesh/rfcal_upr_m1.rs"));

            // The turned board is placed turned; lightmaps are 24-bit files keyed by mesh and position.
            var statics = S("StaticObjects.con");
            Assert.Contains("180", statics.Split('\n').First(l => l.TrimStart().StartsWith("object.rotation", StringComparison.OrdinalIgnoreCase) && l.Contains("180")));
            foreach (var b in m.Boards)
            {
                var tga = Read($"ObjectLightmaps/{b.Mesh}_{b.X}-{b.Y}-{b.Z}.tga");
                Assert.Equal(2, tga[2]); Assert.Equal(24, tga[16]);
            }

            // The retail meshes: 40 bytes in every LOD, normals up, a shader of their own and a flat lightmap each.
            Assert.Equal(2, m.Objects.Count);
            foreach (var ro in m.Objects)
            {
                var sm = StandardMesh.Parse(Read($"StandardMesh/{ro.Mesh}.sm"));
                Assert.All(sm.Lods.SelectMany(l => l), x => { Assert.Equal(40u, x.VertexByteSize); Assert.All(x.Normals, nn => Assert.Equal(new Vec3(0f, 1f, 0f), nn)); });
                Assert.Equal(1, sm.QFlag);
                var tga = Read($"ObjectLightmaps/{ro.Mesh}_{ro.X}-{ro.Y}-{ro.Z}.tga");
                Assert.Equal(24, tga[16]);
                Assert.True(ro.Y < m.Boards[0].Y);                                        // on the ground, under the boards
                Assert.Contains($"GeometryTemplate.file ../BfVietnam/levels/rf_cal3/StandardMesh/{ro.Mesh}", S($"Objects/{ro.Mesh}/Geometries.con"));
            }
            Assert.Contains("texture \"texture/C06F_Tree\"", S("StandardMesh/rfcal_bush_t_m1.rs"));
            Assert.Contains("subshader \"bunk_Material0\"", S("StandardMesh/rfcal_bunk_s_m1.rs"));

            // What the editor expects: on a steered board with a plain shader, the lightmap plus LMambient - whichever
            // way the board's patches used to face - and the coloured map channel by channel.
            var cells = LC.Cells(m.ToSunVector);
            foreach (var c in cells.Where(c => !c.Empty && !c.HalfMaterial))
            {
                float v = LC.Levels[c.Level] / 255f;
                var a = LC.Predict(m, m.Boards[0], c);
                Assert.InRange(a.X, MathF.Min(1f, v + 0.1f) - 1e-4f, MathF.Min(1f, v + 0.1f) + 1e-4f);
                var rgb = LC.Predict(m, m.Boards[1], c);
                Assert.InRange(rgb.Y, MathF.Min(1f, (LC.Levels[c.Level] / 2) / 255f + 0.1f) - 1e-4f, MathF.Min(1f, (LC.Levels[c.Level] / 2) / 255f + 0.1f) + 1e-4f);
            }
            // The turned board's normals turn with it: "to sun" faces the back light's side once turned half round.
            var toSun = cells.First(c => c.Label == "to sun");
            var turned = Vector3.Transform(toSun.Normal, Matrix4x4.CreateRotationY(MathF.PI));
            Assert.True(Vector3.Dot(turned, EngineLight.BackLight(m.ToSunVector)) > 0.99f);
        }
        finally { fake.Dispose(); }
    }
}
