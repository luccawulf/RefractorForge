using System.Numerics;
using System.Text;
using RefractorForge.Formats.Rfa;
using RefractorForge.Formats.Terrain;
using RefractorForge.Render;
using Xunit;
using LC = RefractorForge.Render.LightCalibration;

namespace RefractorForge.Tests;

/// <summary>
/// The lighting calibration level (<see cref="LightCalibration"/>): the test board and its mesh in each vertex
/// layout, the lightmap files, the level built from a base level, and the reader that turns a screenshot of it
/// back into light directions and colours.
/// </summary>
public class LightCalibrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rf_lightcal_" + Guid.NewGuid().ToString("N"));
    public LightCalibrationTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static readonly Vector3 Sun = Vector3.Normalize(new Vector3(-0.747988f, 0.592542f, 0.299011f));
    private static void Near(float want, float got, float tol) => Assert.InRange(got, want - tol, want + tol);
    private static byte[] T(string s) => Encoding.Latin1.GetBytes(s);

    // ---- the mesh -------------------------------------------------------------------------------------------

    [Fact]
    public void The_40_byte_board_is_byte_for_byte_what_the_lightmap_promotion_writes()
    {
        // The editor already ships 40-byte meshes: a 32-byte one widened by StandardMeshRewriter. The board written
        // directly must be that same file, so it rides on a layout that is known to load.
        var direct = LC.BuildMesh("rfcal_pv_m1", Sun, StandardMeshWriter.LitLayout.Lightmap40, withUnwrap: true);
        var plain = LC.BuildMesh("rfcal_pv_m1", Sun, StandardMeshWriter.LitLayout.Plain32, withUnwrap: false);
        var want = StandardMesh.Parse(direct);
        var src = StandardMesh.Parse(plain);
        var plan = src.Lods[0].Select((m, i) => (StandardMeshRewriter.MaterialPlan?)new StandardMeshRewriter.MaterialPlan(
            want.Lods[0][i].LightmapUvs, Array.Empty<int>(), null, StandardMeshRewriter.StridePromotion.To40)).ToList();
        var promoted = StandardMeshRewriter.Write(plain, src, new[] { plan }, new StandardMeshRewriter.Options(SetQFlag: true));
        Assert.Equal(promoted, direct);
    }

    [Fact]
    public void The_tangent_board_reads_back_with_its_normals_its_unwrap_and_a_frame_round_each_normal()
    {
        var bytes = LC.BuildMesh("rfcal_pp_m1", Sun, StandardMeshWriter.LitLayout.Tangent64, withUnwrap: true);
        var sm = StandardMesh.Parse(bytes);
        Assert.Equal(1, sm.QFlag);
        Assert.Equal(3, sm.Lods[0].Count);
        var cells = LC.Cells(Sun);
        foreach (var mat in sm.Lods[0])
        {
            Assert.Equal(1041u, mat.VertexFormat);
            Assert.Equal(64u, mat.VertexByteSize);
            Assert.True(mat.HasLightmapUv);
            Assert.NotNull(mat.PlanarExtra);
            for (int v = 0; v < mat.NumVertices; v++)
            {
                var n = new Vector3(mat.Normals[v].X, mat.Normals[v].Y, mat.Normals[v].Z);
                var t = new Vector3(mat.PlanarExtra![v * 8 + 2], mat.PlanarExtra[v * 8 + 3], mat.PlanarExtra[v * 8 + 4]);
                var b = new Vector3(mat.PlanarExtra[v * 8 + 5], mat.PlanarExtra[v * 8 + 6], mat.PlanarExtra[v * 8 + 7]);
                Near(1f, n.Length(), 1e-4f);
                Near(0f, Vector3.Distance(Vector3.Cross(t, b), n), 1e-4f);    // tangent x binormal = normal, as retail
            }
        }
        // The first patch material holds 46 patches (49 less the three half-diffuse ones), each written both ways round.
        Assert.Equal(46 * 8, sm.Lods[0][0].NumVertices);
        Assert.Equal(3 * 8, sm.Lods[0][1].NumVertices);
        Assert.Equal(8, sm.Lods[0][2].NumVertices);
        // Every vertex normal is one of the board's authored normals (or the plate's own).
        var allowed = cells.Select(c => c.Normal).Append(new Vector3(0, 0, -1)).ToList();
        foreach (var mat in sm.Lods[0])
            foreach (var nn in mat.Normals)
                Assert.Contains(allowed, a => Vector3.Distance(a, new Vector3(nn.X, nn.Y, nn.Z)) < 1e-4f);
    }

    [Fact]
    public void The_retail_wall_frame_comes_out_of_the_frame_builder()
    {
        // O_HueHouse_B_M1's +X wall: tangent +Z, binormal -Y.
        var (t, b) = StandardMeshWriter.TangentFrame(new RefractorForge.Formats.Geometry.Vec3(1, 0, 0));
        Near(0f, Vector3.Distance(new Vector3(t.X, t.Y, t.Z), new Vector3(0, 0, 1)), 1e-6f);
        Near(0f, Vector3.Distance(new Vector3(b.X, b.Y, b.Z), new Vector3(0, -1, 0)), 1e-6f);
        // Straight up and straight down have no level tangent; they still get a frame with tangent x binormal = normal.
        foreach (var n in new[] { new Vector3(0, 1, 0), new Vector3(0, -1, 0) })
        {
            var (t2, b2) = StandardMeshWriter.TangentFrame(new RefractorForge.Formats.Geometry.Vec3(n.X, n.Y, n.Z));
            Near(0f, Vector3.Distance(Vector3.Cross(new Vector3(t2.X, t2.Y, t2.Z), new Vector3(b2.X, b2.Y, b2.Z)), n), 1e-6f);
        }
    }

    [Fact]
    public void A_board_without_an_unwrap_says_so_and_a_32_byte_one_cannot_claim_one()
    {
        var empty = StandardMesh.Parse(LC.BuildMesh("rfcal_ppq0_m1", Sun, StandardMeshWriter.LitLayout.Tangent64, withUnwrap: false));
        Assert.Equal(0, empty.QFlag);
        Assert.All(empty.Lods[0], m => Assert.All(m.LightmapUvs, uv => Assert.Equal((0f, 0f), uv)));

        var plain = StandardMesh.Parse(LC.BuildMesh("rfcal_p32_m1", Sun, StandardMeshWriter.LitLayout.Plain32, withUnwrap: true));
        Assert.Equal(0, plain.QFlag);
        Assert.All(plain.Lods[0], m => Assert.Equal(32u, m.VertexByteSize));

        var section = new StandardMeshWriter.LitSection("m",
            new[] { new RefractorForge.Formats.Geometry.Vec3(0, 0, 0), new RefractorForge.Formats.Geometry.Vec3(1, 0, 0), new RefractorForge.Formats.Geometry.Vec3(0, 1, 0) },
            Enumerable.Repeat(new RefractorForge.Formats.Geometry.Vec3(0, 0, -1), 3).ToArray(),
            new[] { (0f, 0f), (1f, 0f), (0f, 1f) }, Array.Empty<(float, float)>(), new[] { (0, 1, 2) });
        Assert.Throws<InvalidDataException>(() => StandardMeshWriter.WriteLit(new[] { section }, StandardMeshWriter.LitLayout.Plain32, true, new float[6]));
    }

    // ---- the board and its lightmap -------------------------------------------------------------------------

    [Fact]
    public void The_board_has_fifty_cells_one_gap_and_a_patch_for_each_light()
    {
        var cells = LC.Cells(Sun);
        Assert.Equal(LC.Columns * LC.Rows, cells.Length);
        Assert.Equal(cells.Length, cells.Select(c => (c.Column, c.Row)).Distinct().Count());
        var gap = Assert.Single(cells, c => c.Empty);
        Assert.Equal((0, 0), (gap.Column, gap.Row));
        Assert.All(cells, c => Near(1f, c.Normal.Length(), 1e-5f));

        Near(0f, Vector3.Distance(cells.Single(c => c.Label == "to sun").Normal, Sun), 1e-5f);
        var back = cells.Single(c => c.Label == "to back").Normal;
        Near(0f, Vector3.Distance(back, new Vector3(-Sun.X, Sun.Y, -Sun.Z)), 1e-5f);
        // The "side" patch is level with the ground and square to both lights: neither reaches it.
        var side = cells.Single(c => c.Label == "side").Normal;
        Near(0f, Vector3.Dot(side, Sun), 1e-5f);
        Near(0f, Vector3.Dot(side, back), 1e-5f);
        Near(0f, side.Y, 1e-6f);
        // The fan covers the sphere well enough to place a light anywhere: every direction has a patch within 50 degrees.
        var fan = cells.Where(c => !c.Empty && c.Level == 2 && !c.HalfMaterial).Select(c => c.Normal).ToList();
        for (int az = 0; az < 360; az += 15)
            for (int el = -90; el <= 90; el += 15)
                Assert.True(fan.Max(n => Vector3.Dot(n, LC.Direction(az, el))) > MathF.Cos(50f * MathF.PI / 180f), $"nothing near az {az} el {el}");
    }

    [Fact]
    public void The_lightmap_reads_the_same_whichever_corner_is_the_origin()
    {
        var v = LC.LightmapValues();
        int side = (int)Math.Sqrt(v.Length);
        Assert.Equal(side * side, v.Length);
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                Assert.Equal(v[y * side + x], v[y * side + (side - 1 - x)]);
                Assert.Equal(v[y * side + x], v[(side - 1 - y) * side + x]);
            }
        // Each level's value is in the file - and the mesh's lightmap uvs land on them (checked through the parsed mesh).
        var sm = StandardMesh.Parse(LC.BuildMesh("b", Sun, StandardMeshWriter.LitLayout.Lightmap40, true));
        var seen = new HashSet<byte>();
        foreach (var mat in sm.Lods[0])
            foreach (var (u, w) in mat.LightmapUvs)
                seen.Add(v[(int)(w * side) * side + (int)(u * side)]);
        Assert.Equal(LC.Levels.OrderBy(x => x), seen.OrderBy(x => x));
    }

    [Fact]
    public void Lightmap_files_are_the_kinds_they_claim()
    {
        Assert.Null(LC.EncodeLightmap(LC.LightmapKind.None));

        var grey = LC.EncodeLightmap(LC.LightmapKind.Grey)!;
        Assert.Equal((1, 1, 8), (grey[1], grey[2], grey[16]));                 // colour-mapped, 8-bit: the retail form
        for (int i = 0; i < 256; i++) Assert.Equal((i, i, i), ((int)grey[18 + i * 3], (int)grey[19 + i * 3], (int)grey[20 + i * 3]));
        Assert.True(ObjectLightmaps.HasDetail(grey));

        var flat = LC.EncodeLightmap(LC.LightmapKind.Uniform)!;
        Assert.All(flat.Skip(18 + 768), b => Assert.Equal(128, b));

        var pal = LC.EncodeLightmap(LC.LightmapKind.ColourPalette)!;
        Assert.Equal(grey.Skip(18 + 768), pal.Skip(18 + 768));                 // same indices, another palette
        Assert.Equal((150, 100, 200), ((int)pal[18 + 200 * 3], (int)pal[19 + 200 * 3], (int)pal[20 + 200 * 3]));   // B, G, R

        var rgb = LC.EncodeLightmap(LC.LightmapKind.Rgb24)!;
        Assert.Equal((0, 2, 24), (rgb[1], rgb[2], rgb[16]));
        var decoded = TgaTexture.Decode(rgb)!;
        var px = Enumerable.Range(0, decoded.Width * decoded.Height).Select(i => (decoded.Rgba[i * 4], decoded.Rgba[i * 4 + 1], decoded.Rgba[i * 4 + 2])).Distinct().ToList();
        Assert.Contains(((byte)255, (byte)127, (byte)191), px);
        Assert.Contains(((byte)0, (byte)0, (byte)0), px);

        Near(0.75f, LC.LightmapRgb(LC.LightmapKind.Rgb24, 4).Z, 0.005f);
        Near(0.5f, LC.LightmapRgb(LC.LightmapKind.Rgb24, 4).Y, 0.005f);
    }

    // ---- reading a picture ----------------------------------------------------------------------------------

    private static LC.Manifest FlatManifest(bool twoRows = true)
    {
        var boards = new List<LC.Board>
        {
            new("A", "rfcal_pv_m1", "", nameof(LC.LightmapKind.Grey), true, 116, 43, 160, ""),
            new("B", "rfcal_pv_m1", "", nameof(LC.LightmapKind.None), true, 124, 43, 160, ""),
            new("C", "rfcal_pv_m1", "", nameof(LC.LightmapKind.Rgb24), true, 132, 43, 160, ""),
            new("D", "rfcal_p32_m1", "", nameof(LC.LightmapKind.Uniform), false, 140, 43, 160, ""),
        };
        if (twoRows)
            boards.Add(new("E", "rfcal_pp_m1", "", nameof(LC.LightmapKind.Grey), true, 116, 47, 160, ""));
        return new LC.Manifest
        {
            Level = "t", ToSun = new[] { Sun.X, Sun.Y, Sun.Z },
            Sun = new[] { 1.25f, 0f, 0f }, Back = new[] { 0f, 1f, 0f }, Ambient = new[] { 0f, 0f, 0.5f }, LmAmbient = new[] { 0f, 0f, 0.25f },
            Detail = new[] { 0.5f, 0.5f, 0.5f }, Boards = boards,
            Swatches = LC.GroundSwatches.Select(c => new LC.Swatch(new[] { c.R / 255f, c.G / 255f, c.B / 255f }, new[] { c.R / 255f, c.G / 255f, c.B / 255f })).ToList(),
            Viewer = new[] { 128f, 41.5f, 136f }, CentreX = 128, BoardsZ = 160, ViewerZ = 136,
        };
    }

    [Fact]
    public void A_drawn_level_reads_back_as_the_model_that_drew_it()
    {
        var m = FlatManifest();
        const int w = 1600, h = 900;
        var px = LC.RenderExpected(m, w, h, fovYDeg: 55f, pitchUpDeg: 5f);
        var quads = LC.FindBlackQuads(px, w, h);
        Assert.Equal(m.Boards.Count + 1, quads.Count);                         // every board, and the ground mat

        var mat = quads.OrderByDescending(q => q.Centre.Y).First();
        var boards = quads.Where(q => q != mat).ToList();
        var bottom = boards.OrderByDescending(q => q.Centre.Y).Take(4).OrderBy(q => q.Centre.X).ToList();
        var cells = LC.Cells(Sun);
        for (int i = 0; i < 4; i++)
        {
            var got = LC.SampleBoard(px, w, h, bottom[i].Corners);
            foreach (var c in cells)
            {
                var want = LC.Predict(m, m.Boards[i], c);
                Near(0f, Vector3.Distance(want, got[c.Row * LC.Columns + c.Column]), 0.012f);
            }
        }

        // The fan of board E (the tangent-frame one, alone in the top row), with nothing told to the solver about
        // where the lights are. Board A's would do for the directions, but its mesh has no tangent frame, so its sun
        // clips at 1 and its strength reads low - which is what the game showed.
        var e = boards.Except(bottom).Single();
        var a = LC.SampleBoard(px, w, h, e.Corners);
        var fan = cells.Where(c => !c.Empty && !c.HalfMaterial && c.Level == 2).ToList();
        var (sunDir, sunStrength, _) = LC.SolveLight(fan.Select(c => (c.Normal, a[c.Row * LC.Columns + c.Column].X)).ToList());
        var (backDir, backStrength, _) = LC.SolveLight(fan.Select(c => (c.Normal, a[c.Row * LC.Columns + c.Column].Y)).ToList());
        Assert.True(Vector3.Dot(sunDir, Sun) > MathF.Cos(1f * MathF.PI / 180f));
        Assert.True(Vector3.Dot(backDir, LC.PredictedBack(Sun)) > MathF.Cos(1f * MathF.PI / 180f));
        Near(1.25f * 128f / 255f, sunStrength, 0.01f);                         // sun colour x the fan's lightmap value
        Near(1.0f * 128f / 255f, backStrength, 0.01f);
        var lowA = LC.SampleBoard(px, w, h, bottom[0].Corners);
        var (sunDirA, sunStrengthA, _) = LC.SolveLight(fan.Select(c => (c.Normal, lowA[c.Row * LC.Columns + c.Column].X)).ToList());
        Assert.True(Vector3.Dot(sunDirA, Sun) > MathF.Cos(1.5f * MathF.PI / 180f));
        Assert.InRange(sunStrengthA, 0.5f, 0.6f);                              // clipped at 1 x the lightmap value

        // The ground swatches come back in order, west to east.
        for (int i = 0; i < m.Swatches.Count; i++)
        {
            var v = LC.SampleAt(px, w, h, mat.Corners, LC.GroundSwatchCentre(i), 0.03f, 0.12f);
            Near(0f, Vector3.Distance(v, new Vector3(m.Swatches[i].Expected[0], m.Swatches[i].Expected[1], m.Swatches[i].Expected[2])), 0.012f);
        }
    }

    [Fact]
    public void A_black_panel_with_bright_things_on_it_is_not_taken_for_a_board()
    {
        // The game's HUD: a black panel of a board's proportions with icons and numbers on it.
        var m = FlatManifest(twoRows: false);
        const int w = 1600, h = 900;
        var px = LC.RenderExpected(m, w, h, fovYDeg: 55f, pitchUpDeg: 5f);
        var rng = new Random(7);
        for (int y = 60; y < 170; y++)
            for (int x = 40; x < 240; x++)
            {
                int o = (y * w + x) * 4;
                px[o] = px[o + 1] = px[o + 2] = 0;
            }
        for (int k = 0; k < 40; k++)
        {
            int bx = 50 + rng.Next(170), by = 68 + rng.Next(85), bw = 6 + rng.Next(22), bh = 4 + rng.Next(12);
            for (int y = by; y < Math.Min(165, by + bh); y++)
                for (int x = bx; x < Math.Min(235, bx + bw); x++)
                {
                    int o = (y * w + x) * 4;
                    px[o] = 230; px[o + 1] = 200; px[o + 2] = 40;
                }
        }
        var quads = LC.FindBlackQuads(px, w, h);
        Assert.Equal(m.Boards.Count + 2, quads.Count);                         // the boards, the mat and the panel
        var boards = quads.Where(q => LC.IsBoard(px, w, h, q.Corners)).ToList();
        Assert.Equal(m.Boards.Count, boards.Count);
        Assert.DoesNotContain(boards, q => q.Centre.Y < 200);
    }

    [Fact]
    public void The_reader_follows_another_formula_when_the_picture_shows_one()
    {
        // Draw the level with a different engine: the back light straight overhead at a third of the strength, and
        // a 0.5 floor under everything (what the fixed-function shader's texture factor would do). The reader has
        // to report THAT, not the editor's model.
        var m = FlatManifest(twoRows: false);
        var overhead = Vector3.UnitY;
        Vector3 Other(LC.Board b, LC.Cell c)
            => c.Empty ? Vector3.Zero
             : Vector3.Min(Vector3.One, new Vector3(0.5f) + c.Prelight * new Vector3(
                   0.8f * MathF.Max(0f, Vector3.Dot(c.Normal, Sun)), 0.33f * MathF.Max(0f, Vector3.Dot(c.Normal, overhead)), 0.1f));
        const int w = 1280, h = 960;
        var px = LC.RenderExpected(m, w, h, fovYDeg: 60f, pitchUpDeg: 4f, colour: Other);
        var quads = LC.FindBlackQuads(px, w, h);
        var board = quads.OrderByDescending(q => q.Centre.Y).Skip(1).OrderBy(q => q.Centre.X).First();
        var got = LC.SampleBoard(px, w, h, board.Corners);
        var fan = LC.Cells(Sun).Where(c => !c.Empty && !c.HalfMaterial && c.Level == 2).ToList();
        float floor = fan.Min(c => got[c.Row * LC.Columns + c.Column].Y);
        Near(0.5f, floor, 0.01f);
        var (dir, strength, rms) = LC.SolveLight(fan.Select(c => (c.Normal, got[c.Row * LC.Columns + c.Column].Y - floor)).ToList());
        Assert.True(Vector3.Dot(dir, overhead) > MathF.Cos(1.5f * MathF.PI / 180f), $"found {dir}");
        Near(0.33f * 128f / 255f, strength, 0.01f);
        Assert.True(rms < 0.01f);
    }

    [Fact]
    public void A_light_is_found_from_the_patches_it_reaches_and_nothing_else()
    {
        var light = Vector3.Normalize(new Vector3(0.3f, -0.2f, 0.9f)) * 0.7f;
        var samples = new List<(Vector3, float)>();
        for (int az = 0; az < 360; az += 45)
            foreach (int el in new[] { -45, 0, 45 })
            {
                var n = LC.Direction(az, el);
                samples.Add((n, MathF.Max(0f, Vector3.Dot(n, light))));
            }
        var (dir, strength, rms) = LC.SolveLight(samples);
        Near(0f, Vector3.Distance(dir * strength, light), 1e-3f);
        Near(0f, rms, 1e-3f);

        // No light at all is reported as none, not as noise.
        var (_, none, _) = LC.SolveLight(samples.Select(s => (s.Item1, 0f)).ToList());
        Assert.Equal(0f, none);
    }

    // ---- the level ------------------------------------------------------------------------------------------

    /// <summary>The fake base level, for the third level's tests in another class.</summary>
    internal string FakeLevelPath() => FakeLevel();

    private string FakeLevel()
    {
        const string p = "BfVietnam/levels/Foo/";
        const int ms = 64;
        var height = new byte[ms * ms * 2];
        const ushort raw = 30720;                                          // 42 m exactly at yScale 0.35
        for (int i = 0; i < ms * ms; i++) { height[i * 2] = (byte)(raw & 0xFF); height[i * 2 + 1] = (byte)(raw >> 8); }
        var tilePx = new byte[256 * 256 * 4];
        for (int i = 0; i < 256 * 256; i++) { tilePx[i * 4] = 90; tilePx[i * 4 + 1] = 110; tilePx[i * 4 + 2] = 70; tilePx[i * 4 + 3] = 255; }
        var tile = DxtEncoder.EncodeDxt1Mipped(new Texture2D(256, 256, tilePx));
        var allShadow = new byte[256 * 256]; Array.Fill(allShadow, (byte)255);
        var shadow = LightmapShadowBits.FromShadowMask(allShadow, 256, 1, 256).Encode();             // the base level: all in shadow

        var files = new List<(string, byte[])>
        {
            (p + "Init.con", T("renderer.diffuseColor .85/.8/.74\r\nrenderer.ambientColor .175\r\nrenderer.SecondaryDiffuseColor .317/.317/.325\r\n" +
                               "renderer.LMambientColor .2\r\nrenderer.fogstart 10\r\nrenderer.fogend 150\r\nGame.ViewDistance 170\r\n\r\nrun Init/SkyAndSun\r\nrun Init/Terrain\r\n")),
            (p + "Init/Terrain.con", T("GeometryTemplate.create patchTerrain terrainGeometry\r\nGeometryTemplate.file BfVietnam\\levels\\Foo\\Heightmap\r\n" +
                                       "GeometryTemplate.materialMap BfVietnam\\levels\\Foo\\Materialmap\r\nGeometryTemplate.materialSize 64\r\n" +
                                       "GeometryTemplate.worldSize 256\r\nGeometryTemplate.yScale 0.35\r\n" +
                                       "GeometryTemplate.texBaseName BfVietnam\\levels\\Foo\\Textures\\Tx\r\n" +
                                       "GeometryTemplate.detailTexName BfVietnam\\levels\\Foo\\Textures\\Detail\r\nGeometryTemplate.waterLevel 10\r\n")),
            (p + "Init/SkyAndSun.con", T("Sky.setRotAngle -68\r\nsky.sunLightDirectionVec -0.747988/0.592542/0.299011\r\n")),
            (p + "Heightmap.raw", height),
            (p + "MaterialMap.raw", new byte[ms * ms]),
            (p + "StaticObjects.con", T("Object.create O_HueHouse_B\r\nObject.absolutePosition 130/41/150\r\nObject.rotation 0/0/0\r\n")),
            (p + "objectlightmaps/O_HueHouse_B_M1_130-41-150.tga", LC.EncodeLightmap(LC.LightmapKind.Grey)!),
            (p + "Conquest/ControlPointTemplates.con", T(
                "ObjectTemplate.create ControlPoint NVA\r\nObjectTemplate.team 1\r\nObjectTemplate.spawnGroupId 1\r\n\r\n" +
                "ObjectTemplate.create ControlPoint US\r\nObjectTemplate.team 2\r\nObjectTemplate.spawnGroupId 2\r\n\r\n" +
                "ObjectTemplate.create ControlPoint mid\r\nObjectTemplate.team 0\r\nObjectTemplate.spawnGroupId 3\r\n")),
            (p + "Conquest/SoldierSpawnTemplates.con", T(
                "ObjectTemplate.create SpawnPoint NVA1\r\nObjectTemplate.setSpawnId 1\r\nObjectTemplate.setGroup 1\r\n" +
                "ObjectTemplate.create SpawnPoint US1\r\nObjectTemplate.setSpawnId 2\r\nObjectTemplate.setGroup 2\r\n" +
                "ObjectTemplate.create SpawnPoint US2\r\nObjectTemplate.setSpawnId 3\r\nObjectTemplate.setGroup 2\r\n" +
                "ObjectTemplate.create SpawnPoint mid1\r\nObjectTemplate.setSpawnId 4\r\nObjectTemplate.setGroup 3\r\n")),
            (p + "Conquest/SoldierSpawns.con", T(
                "Object.create NVA1\r\nObject.absolutePosition 20/60/20\r\nObject.rotation -90/0/0\r\n\r\n" +
                "Object.create US1\r\nObject.absolutePosition 200/60/200\r\nObject.rotation 45/0/0\r\n\r\n" +
                "Object.create US2\r\nObject.absolutePosition 202/60/200\r\n\r\n" +
                "Object.create mid1\r\nObject.absolutePosition 99/41/99\r\nObject.rotation 180/0/0\r\n")),
            (p + "Textures/tx00x00.dds", tile),
            (p + "Textures/detail.dds", tile),
            (p + "Textures/LightmapShadowBits.lsb", shadow),
        };
        var rfa = Path.Combine(_dir, "Foo.rfa");
        RefractorFlatArchive.WriteFile(rfa, files, compress: true, XPackId.Default);
        return rfa;
    }

    private static readonly LC.Site FakeSite = new(128, 160, 136);

    [Fact]
    public void The_level_is_the_base_level_with_boards_where_its_buildings_were()
    {
        var baseRfa = FakeLevel();
        var before = File.ReadAllBytes(baseRfa);
        var outDir = Path.Combine(_dir, "out");
        Directory.CreateDirectory(outDir);
        var m = LC.BuildLevel(new LC.Options(baseRfa, "rf_cal", outDir, Full: true, FakeSite));
        Assert.Equal(before, File.ReadAllBytes(baseRfa));                      // the base is only read

        var arc = new RefractorFlatArchive(Path.Combine(outDir, "rf_cal.rfa"));
        const string p = "BfVietnam/levels/rf_cal/";
        Assert.All(arc.Entries, e => Assert.StartsWith(p, e.Name.Replace('\\', '/')));
        byte[] Read(string rel) => arc.TryGetEntry(p + rel, out var e) ? arc.Read(e) : throw new FileNotFoundException(rel);
        bool Has(string rel) => arc.TryGetEntry(p + rel, out _);
        string S(string rel) => Encoding.Latin1.GetString(Read(rel));

        // Eight boards and nothing else placed.
        Assert.Equal(8, m.Boards.Count);
        var statics = S("StaticObjects.con");
        Assert.DoesNotContain("O_HueHouse_B", statics);
        Assert.Equal(8, statics.Split('\n').Count(l => l.TrimStart().StartsWith("object.create", StringComparison.OrdinalIgnoreCase)));
        Near(42f, m.Viewer[1], 0.001f);
        Assert.All(m.Boards, b => Assert.True(b.Y >= 43));                     // clear of the ground, on whole metres

        // Each mesh, its shader and its object, and the run chain that registers them.
        foreach (var (mesh, stride, qflag, normalMap) in new[] { ("rfcal_pv_m1", 40u, 1, false), ("rfcal_p32_m1", 32u, 0, false), ("rfcal_pp_m1", 64u, 1, true), ("rfcal_ppq0_m1", 64u, 0, true) })
        {
            var sm = StandardMesh.Parse(Read($"StandardMesh/{mesh}.sm"));
            Assert.Equal(qflag, sm.QFlag);
            Assert.All(sm.Lods[0], x => Assert.Equal(stride, x.VertexByteSize));
            var rs = S($"StandardMesh/{mesh}.rs");
            foreach (var mat in sm.Lods[0]) Assert.Contains($"subshader \"{mat.Name}\" \"StandardMesh/Default\"", rs);
            Assert.Equal(normalMap, rs.Contains("normalmap \"texture/rfcal_flat_n\";"));
            Assert.Contains($"GeometryTemplate.file ../BfVietnam/levels/rf_cal/StandardMesh/{mesh}", S($"Objects/{mesh}/Geometries.con"));
            Assert.Contains($"ObjectTemplate.geometry {mesh}", S($"Objects/{mesh}/Objects.con"));
            Assert.Contains($"run {mesh}/{mesh}", S("Objects/objects.con"));
        }
        Assert.True(Has("Texture/rfcal_white.dds") && Has("Texture/rfcal_black.dds") && Has("Texture/rfcal_flat_n.dds"));
        var white = DdsTexture.Decode(Read("Texture/rfcal_white.dds"));
        Assert.Equal((255, 255, 255, 255), (white.Rgba[0], white.Rgba[1], white.Rgba[2], white.Rgba[3]));
        var flat = DdsTexture.Decode(Read("Texture/rfcal_flat_n.dds"));
        Assert.Equal((128, 128, 255, 0), (flat.Rgba[0], flat.Rgba[1], flat.Rgba[2], flat.Rgba[3]));

        // Lightmaps: in the folder as the level spells it, keyed by mesh and whole-metre position, and only where meant.
        foreach (var b in m.Boards)
        {
            string rel = $"objectlightmaps/{b.Mesh}_{b.X}-{b.Y}-{b.Z}.tga";
            Assert.Equal(b.Lightmap != nameof(LC.LightmapKind.None), Has(rel));
            if (b.Lightmap == nameof(LC.LightmapKind.Rgb24)) Assert.Equal(2, Read(rel)[2]);
            if (b.Lightmap == nameof(LC.LightmapKind.Uniform)) Assert.False(ObjectLightmaps.HasDetail(Read(rel)));
        }
        Assert.DoesNotContain(arc.Entries, e => e.Name.Replace('\\', '/').StartsWith(p + "ObjectLightmaps/", StringComparison.Ordinal));

        // The light: one channel per term, fog past the boards, the objects registered after everything else.
        var init = S("Init.con");
        Assert.Contains("renderer.diffuseColor 1.25/0/0", init);
        Assert.Contains("renderer.ambientColor 0/0/0.5", init);
        Assert.Contains("renderer.SecondaryDiffuseColor 0/1/0", init);
        Assert.Contains("renderer.LMambientColor 0/0/0.25", init);
        Assert.Contains("renderer.fogstart 155", init);
        Assert.Contains("renderer.fogend 170", init);
        Assert.Contains("textureManager.alternativePath BfVietnam/levels/rf_cal/Texture", init);
        Assert.True(init.IndexOf("run Objects/Objects", StringComparison.Ordinal) > init.IndexOf("run Init/Terrain", StringComparison.Ordinal));

        // Both teams' main-base spawns stand on the viewing line facing north; the neutral point's is left alone.
        var spawns = S("Conquest/SoldierSpawns.con").Replace("\r\n", "\n");
        Assert.Contains("Object.create NVA1\nObject.absolutePosition 128/42.1/133\nObject.rotation 0/0/0", spawns);
        Assert.Contains("Object.create US1\nObject.absolutePosition 126.75/42.1/136\nObject.rotation 0/0/0", spawns);
        Assert.Contains("Object.create US2\nObject.absolutePosition 129.25/42.1/136\nObject.rotation 0/0/0", spawns);
        Assert.Contains("Object.create mid1\nObject.absolutePosition 99/41/99\nObject.rotation 180/0/0", spawns);

        // The ground chart is in the tile, the detail texture is flat, and nothing is left in terrain shadow.
        var tileNow = DdsTexture.Decode(Read("Textures/tx00x00.dds"));
        Assert.Equal(256, tileNow.Width);
        var (mx0, mx1, mz0, mz1) = LC.GroundMat(FakeSite);
        int cx = (int)((mx0 + 0.2f) * 256f / 256f), cz = (int)((mz0 + 0.5f) * 256f / 256f);
        Assert.True(tileNow.Rgba[(cz * 256 + cx) * 4 + 1] < 30, "the mat's rim is black");
        Assert.Equal(110, tileNow.Rgba[(20 * 256 + 20) * 4 + 1], 6f);          // far from the chart: as it was
        var detail = DdsTexture.Decode(Read("Textures/detail.dds"));
        Assert.Equal(512, detail.Width);
        Near(m.Detail[0] * 255f, detail.Rgba[0], 0.5f);
        Assert.InRange(detail.Rgba[1], 124, 134);
        var vis = LightmapShadowBits.Decode(Read("Textures/LightmapShadowBits.lsb")).ToShadowMask(out _);
        Assert.All(vis, b => Assert.Equal(0, b));
        Assert.Equal(5, m.Swatches.Count);
    }

    [Fact]
    public void The_second_level_crosses_ground_colour_with_the_shadow_map()
    {
        var outDir = Path.Combine(_dir, "unlit");
        Directory.CreateDirectory(outDir);
        var site = new LC.Site(128, 160, 126);
        var m = LC.BuildLevel(new LC.Options(FakeLevel(), "rf_cal2", outDir, Full: false, site) { Unlit = true });
        var arc = new RefractorFlatArchive(Path.Combine(outDir, "rf_cal2.rfa"));
        const string p = "BfVietnam/levels/rf_cal2/";
        byte[] Read(string rel) => arc.TryGetEntry(p + rel, out var e) ? arc.Read(e) : throw new FileNotFoundException(rel);

        // Twelve boards: six columns, the front row low and near, the back row five metres behind and lifted clear.
        Assert.Equal(12, m.Boards.Count);
        var front = m.Boards.Take(6).ToList(); var back = m.Boards.Skip(6).ToList();
        Assert.All(front, b => Assert.Equal(160, b.Z));
        Assert.All(back, b => Assert.Equal(165, b.Z));
        Assert.True(back[0].Y - front[0].Y >= 5);
        Assert.Equal(new[] { 108, 116, 124, 132, 140, 148 }, front.Select(b => b.X));
        Assert.Equal(new[] { "None", "None", "None", "None", "Uniform", "Grey" }, front.Select(b => b.Lightmap));
        Assert.Equal(new[] { 0f, 64f, 128f, 255f, 128f, 128f }, front.Select(b => MathF.Round(b.Ground![0] * 255f)));

        // The shadow map: in shadow under the front row, clear under the back row.
        var lsb = LightmapShadowBits.Decode(Read("Textures/LightmapShadowBits.lsb"));
        var mask = lsb.ToShadowMask(out int side);
        byte At(float x, float z) => mask[(int)(z / 256f * side) * side + (int)(x / 256f * side)];
        Assert.All(front, b => Assert.Equal(255, At(b.X, b.Z)));
        Assert.All(back, b => Assert.Equal(0, At(b.X, b.Z)));
        Assert.Equal(255, At(20, 20));                                         // and in shadow everywhere else
        Assert.All(front, b => Assert.Contains("in shadow", b.ShadowMap));
        Assert.All(back, b => Assert.Contains("sunlit", b.ShadowMap));

        // The shadow ambient has a different value in each channel; the ground under each column is its colour.
        Assert.Contains("Terrain.ShadowAmbient 60/120/200", Encoding.Latin1.GetString(Read("Init/Terrain.con")));
        var tile = DdsTexture.Decode(Read("Textures/tx00x00.dds"));
        byte Ground(float x, float z) => tile.Rgba[((int)z * 256 + (int)x) * 4 + 1];
        Assert.InRange(Ground(108, 162), 0, 6);
        Assert.InRange(Ground(124, 162), 122, 136);
        Assert.InRange(Ground(132, 162), 248, 255);
        Assert.Empty(m.Swatches);
    }

    [Fact]
    public void The_safe_level_keeps_to_file_forms_the_editor_already_ships()
    {
        var outDir = Path.Combine(_dir, "safe");
        Directory.CreateDirectory(outDir);
        var m = LC.BuildLevel(new LC.Options(FakeLevel(), "rf_cal_safe", outDir, Full: false, FakeSite));
        var arc = new RefractorFlatArchive(Path.Combine(outDir, "rf_cal_safe.rfa"));
        Assert.Equal(new[] { "A", "B", "C", "D" }, m.Boards.Select(b => b.Id));
        Assert.DoesNotContain(arc.Entries, e => e.Name.Contains("rfcal_pp", StringComparison.OrdinalIgnoreCase) || e.Name.Contains("rfcal_flat_n", StringComparison.OrdinalIgnoreCase));
        foreach (var e in arc.Entries.Where(e => e.Name.EndsWith(".tga", StringComparison.OrdinalIgnoreCase) && e.Name.Contains("rfcal_")))
            Assert.Equal(1, arc.Read(e)[2]);                                   // every lightmap is the retail 8-bit form
        Assert.Equal(nameof(LC.LightmapKind.ColourPalette), m.Boards.Single(b => b.Id == "C").Lightmap);
        foreach (var e in arc.Entries.Where(e => e.Name.EndsWith(".sm", StringComparison.OrdinalIgnoreCase)))
            Assert.All(StandardMesh.Parse(arc.Read(e)).Lods[0], x => Assert.True(x.VertexByteSize is 32 or 40));
    }

    [Fact]
    public void A_site_that_does_not_fit_the_map_is_refused_and_nothing_is_left_half_built()
    {
        var outDir = Path.Combine(_dir, "bad");
        Directory.CreateDirectory(outDir);
        var ex = Assert.Throws<InvalidDataException>(() => LC.BuildLevel(new LC.Options(FakeLevel(), "rf_bad", outDir, Full: false, new LC.Site(128, 400, 376))));
        Assert.Contains("does not fit the map", ex.Message);
        Assert.False(File.Exists(Path.Combine(outDir, "rf_bad.rfa")));
        Assert.Throws<InvalidDataException>(() => LC.BuildLevel(new LC.Options(Path.Combine(_dir, "Foo.rfa"), "rf_bad2", outDir, Full: false, new LC.Site(128, 130, 150))));
    }
}
