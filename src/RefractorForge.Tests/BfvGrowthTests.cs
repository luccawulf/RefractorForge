using System.Numerics;
using System.Text;
using RefractorForge.Formats.Rfa;
using RefractorForge.Formats.Terrain;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Gates on the growth generator reversed from the stock BfVietnam.exe (Formats/Terrain/BfvGrowth.cs,
/// docs/BFV_Growth_Placement.md). The synthetic tests pin each engine rule; the install-backed ones pin whole maps
/// against the reference implementation (tools/bfv_growth) and against a capture of the running game.
/// </summary>
public class BfvGrowthTests
{
    // ---- the RNG and the cell layout, against values from tools/bfv_growth/overgrowth_place.py ------------------

    [Fact]
    public void Random_is_the_engines_lehmer_generator()
    {
        var r = new GrowthRandom(23);
        Assert.Equal(new[] { 1613378486, 1268074238, 1791315292 }, new[] { r.Next(), r.Next(), r.Next() });
        var k = new GrowthRandom(4734);
        Assert.Equal(new[] { 9918204, 12688136, 5724708 }, new[] { k.K(), k.K(), k.K() });
    }

    [Fact]
    public void Seeds_follow_the_cell_indices()
    {
        Assert.Equal(23, GrowthRandom.SeedFor(0, 0));
        Assert.Equal(36, GrowthRandom.SeedFor(1, 0));
        Assert.Equal(4734, GrowthRandom.SeedFor(0, 1));
        Assert.Equal((int)((23L + 13 * 5 + 4711L * 9) & 0x7FFFFFFF), GrowthRandom.SeedFor(5, 9));
    }

    [Fact]
    public void Uniform_samples_round_like_the_engine()
    {
        var r = new GrowthRandom(36);
        float s = GrowthDistribution.Parse("CRDUniform/.8/1.2/false", GrowthDistribution.DefaultScale).Sample(ref r);
        Assert.Equal(1065578814u, BitConverter.SingleToUInt32Bits(s));
        var r2 = new GrowthRandom(36);
        float yaw = GrowthDistribution.DefaultRotation.Sample(ref r2);
        Assert.Equal(1103208904u, BitConverter.SingleToUInt32Bits(yaw));
    }

    [Fact]
    public void Cell_sizes_snap_to_the_map_and_to_the_colour_tile()
    {
        Assert.Equal(1125271845u, BitConverter.SingleToUInt32Bits(BfvGrowth.CellSize(true, 550f, 2048f)));   // Irving, 14 x 14
        Assert.Equal(1122209337u, BitConverter.SingleToUInt32Bits(BfvGrowth.CellSize(true, 450f, 2048f)));
        Assert.Equal(64f, BfvGrowth.CellSize(true, 250f, 1024f));
        Assert.Equal(1097920753u, BitConverter.SingleToUInt32Bits(BfvGrowth.CellSize(false, 60f, 2048f)));   // 256/17
        Assert.Equal(1100106021u, BitConverter.SingleToUInt32Bits(BfvGrowth.CellSize(false, 70f, 2048f)));   // 256/14
        // The Veg builds hard-code the seed: 12.5 m over (2048/163 after the snap), 17.5 m under (256/14).
        Assert.Equal(2048f / 163f, BfvGrowth.CellSize(true, 550f, 2048f, VegetationBuild.VegUltra));
        Assert.Equal(256f / 14f, BfvGrowth.CellSize(false, 60f, 2048f, VegetationBuild.VegUltra));
    }

    [Fact]
    public void Low_medium_high_builds_keep_a_quarter_half_three_quarters_of_the_cells()
    {
        int n = 20000, low = 0, med = 0, high = 0, ultra = 0;
        for (int i = 0; i < n; i++)
        {
            int seed = GrowthRandom.SeedFor(i % 163, i / 163);
            if (GrowthBuilds.Keeps(VegetationBuild.VegLow, seed)) low++;
            if (GrowthBuilds.Keeps(VegetationBuild.VegMedium, seed)) med++;
            if (GrowthBuilds.Keeps(VegetationBuild.VegHigh, seed)) high++;
            if (GrowthBuilds.Keeps(VegetationBuild.VegUltra, seed)) ultra++;
        }
        Assert.InRange(low / (double)n, 0.22, 0.28);
        Assert.InRange(med / (double)n, 0.47, 0.53);
        Assert.InRange(high / (double)n, 0.72, 0.78);
        Assert.Equal(n, ultra);
        Assert.True(GrowthBuilds.Keeps(VegetationBuild.Stock, 12345));
    }

    // ---- the .wst as the engine reads it ------------------------------------------------------------------------

    const string Wst = """
        <?xml version="1.0"?>
        <WRAPPER_TREE VERS = "1.1">
          <underGrowth materialMapSideSize = "4" viewdistance = "60">
            <materials>
              <renamedDefault><types><a geometryName = "g_a"/></types></renamedDefault>
              <water><types/></water>
              <dryGrass>
                <types>
                  <b geometryName = "g_b" probability = ".25" scale = "CRDUniform/.5/1.5/false" normalScale = ".2"
                     minRadiusDistToEquals = ".5" minRadiusDistToOthers = ".25"/>
                  <c geometryName = "g_c"/>
                </types>
              </dryGrass>
              <dryGrass><types><d geometryName = "g_d" probability = ".1"/></types></dryGrass>
            </materials>
          </underGrowth>
        </WRAPPER_TREE>
        """;

    [Fact]
    public void Definition_uses_the_engines_defaults_and_binds_materials_by_name()
    {
        var def = GrowthDefinition.Parse(Wst, over: false);
        Assert.Equal(60f, def.ViewDistance);                 // lower-case "viewdistance" still counts
        Assert.Equal(4, def.MaterialMapSideSize);
        Assert.Equal(1f, def.DensityScale);
        Assert.Equal(0.3f, def.FadeRange);
        Assert.Single(def.Materials[0]);                      // an unknown name falls back to its position (0)
        var dry = def.Materials[2];
        Assert.Equal(new[] { "g_b", "g_c", "g_d" }, dry.Select(t => t.GeometryName));   // a repeated name adds
        Assert.Equal(0.25f, dry[0].Probability);
        Assert.Equal(new GrowthDistribution(GrowthDistribution.Uniform, 0.5f, 1.5f, false), dry[0].Scale);
        // g_c sets nothing: probability 1, both spacing factors 1, normalScale 1, scale 1, yaw -180..180
        Assert.Equal(1f, dry[1].Probability);
        Assert.Equal(1f, dry[1].MinRadiusDistToEquals);
        Assert.Equal(1f, dry[1].MinRadiusDistToOthers);
        Assert.Equal(1f, dry[1].NormalScale);
        Assert.Equal(GrowthDistribution.DefaultScale, dry[1].Scale);
        Assert.Equal(GrowthDistribution.DefaultRotation, dry[1].Rotation);
        Assert.Empty(def.Materials[1]);
    }

    [Fact]
    public void Geometry_list_reads_every_geometry_name()
    {
        var set = GrowthDefinition.GeometryList("<WRAPPER_TREE><x><a geometryName = \"C02F_Trees_M1\"/><b><c geometryName=\"o_rocks01_m1\"/></b></x></WRAPPER_TREE>");
        Assert.Contains("c02f_trees_m1", set);   // compared case-insensitively
        Assert.Contains("O_Rocks01_M1", set);
        Assert.Equal(2, set.Count);
    }

    // ---- the loop's rules ---------------------------------------------------------------------------------------

    static GrowthMeshInfo Box(float half, int verts, int idx) => new(-half, 0, -half, half, 2, half, verts, idx);

    static IReadOnlyList<IReadOnlyList<GrowthType>> OneMaterial(params GrowthType[] types)
    {
        var mats = new List<IReadOnlyList<GrowthType>>();
        for (int i = 0; i < GrowthDefinition.MaterialNames.Length; i++) mats.Add(i == 2 ? types : Array.Empty<GrowthType>());
        return mats;
    }

    [Fact]
    public void Roulette_skips_an_unresolved_type_without_spending_its_probability()
    {
        var missing = new GrowthType { GeometryName = "gone", Probability = 0.5f };
        var real = new GrowthType { GeometryName = "real", Probability = 0.5f, MinRadiusDistToEquals = 0f, MinRadiusDistToOthers = 0f };
        var recs = new GrowthRecords(n => n == "real" ? Box(1, 4, 6) : null);
        missing.Record = recs.Resolve("gone"); real.Record = recs.Resolve("real");
        Assert.Equal(-1, missing.Record);
        var rng = new GrowthRandom(GrowthRandom.SeedFor(0, 0));
        var vec = new List<GrowthInstance>();
        GrowthLoop.Run(ref rng, 2048, 8192, 0, 0, 10, (x, z) => 2, OneMaterial(missing, real), recs.All, vec, 0, 0);
        // With the missing type skipped, a draw under 0.5 lands on it and moves on to 'real' with u unchanged, so
        // every draw under 1.0 places 'real': nothing is lost to the gap.
        Assert.NotEmpty(vec);
        Assert.All(vec, i => Assert.Equal("real", i.Geometry));
    }

    [Fact]
    public void Probabilities_are_not_normalised()
    {
        var t = new GrowthType { GeometryName = "g", Probability = 0.1f, MinRadiusDistToEquals = 0f, MinRadiusDistToOthers = 0f };
        var recs = new GrowthRecords(_ => Box(1, 1, 1));
        t.Record = recs.Resolve("g");
        var rng = new GrowthRandom(GrowthRandom.SeedFor(3, 4));
        var vec = new List<GrowthInstance>();
        var (_, _, stop, iters) = GrowthLoop.Run(ref rng, 2048, 8192, 0, 0, 100, (x, z) => 2, OneMaterial(t), recs.All, vec, 0, 0);
        // 256 iterations at 10 %: about 23 placements (each costs two iterations), never all of them.
        Assert.Equal(GrowthStop.Iterations, stop);
        Assert.InRange(vec.Count, 10, 40);
    }

    [Fact]
    public void A_type_that_no_longer_fits_ends_the_cell()
    {
        var big = new GrowthType { GeometryName = "big", Probability = 0.5f, MinRadiusDistToEquals = 0f, MinRadiusDistToOthers = 0f };
        var small = new GrowthType { GeometryName = "small", Probability = 0.5f, MinRadiusDistToEquals = 0f, MinRadiusDistToOthers = 0f };
        var recs = new GrowthRecords(n => n == "big" ? Box(1, 700, 10) : Box(1, 1, 1));
        big.Record = recs.Resolve("big"); small.Record = recs.Resolve("small");
        var rng = new GrowthRandom(GrowthRandom.SeedFor(7, 1));
        var vec = new List<GrowthInstance>();
        var (a, _, stop, _) = GrowthLoop.Run(ref rng, 2048, 8192, 0, 0, 100, (x, z) => 2, OneMaterial(big, small), recs.All, vec, 0, 0);
        Assert.Equal(GrowthStop.Budget, stop);
        Assert.True(a < 700, "the cell stopped when 'big' no longer fit");
        Assert.True(a >= 0);
        Assert.True(vec.Count(i => i.Geometry == "big") <= 2);
    }

    [Fact]
    public void Spacing_keeps_equals_apart_by_the_mesh_radius_and_scale()
    {
        var t = new GrowthType { GeometryName = "tree", Probability = 1f, MinRadiusDistToEquals = 1f, MinRadiusDistToOthers = 1f };
        var recs = new GrowthRecords(_ => Box(3, 1, 1));   // corner radius sqrt(3^2 + 2^2 + 3^2)
        t.Record = recs.Resolve("tree");
        var rng = new GrowthRandom(GrowthRandom.SeedFor(2, 2));
        var vec = new List<GrowthInstance>();
        GrowthLoop.Run(ref rng, 2048, 8192, 0, 0, 100, (x, z) => 2, OneMaterial(t), recs.All, vec, 0, 0);
        float r = recs.All[0].Radius;
        Assert.Equal(MathF.Sqrt(22f), r, 4);
        for (int i = 0; i < vec.Count; i++)
            for (int j = i + 1; j < vec.Count; j++)
            {
                float dx = vec[i].X - vec[j].X, dz = vec[i].Z - vec[j].Z;
                Assert.True(dx * dx + dz * dz >= (2 * r) * (2 * r) * 0.9999f, "two equals closer than their summed radii");
            }
    }

    [Fact]
    public void Material_lookup_is_row_z_column_x_low_four_bits()
    {
        var map = new byte[16];
        map[2 * 4 + 1] = 0x13;   // row (z) 2, column (x) 1, high nibble ignored
        Assert.Equal(3, GrowthLoop.MaterialAt(map, 4, 4f / 8f, 2.5f, 4.5f));
        Assert.Equal(0, GrowthLoop.MaterialAt(map, 4, 4f / 8f, 4.5f, 2.5f));
    }

    [Fact]
    public void Instance_matrix_is_yaw_about_up_then_scale_and_position()
    {
        var inst = new GrowthInstance { X = 10, Z = 20, Scale = 2, Yaw = 90, Generated = true };
        inst.Up = Vector3.UnitY;
        inst.Y = 5;
        var m = inst.World();
        var p = Vector3.Transform(new Vector3(0, 0, 1), m);   // local forward -> +X at yaw 90
        Assert.Equal(12f, p.X, 4);
        Assert.Equal(5f, p.Y, 4);
        Assert.Equal(20f, p.Z, 4);
    }

    // ---- whole maps ------------------------------------------------------------------------------------------------

    /// <summary>A retail BFV level read out of its archives, newest patch first.</summary>
    sealed class Level
    {
        readonly List<RefractorFlatArchive> _levels = new();
        readonly RefractorFlatArchive _meshes, _objects;
        readonly Dictionary<string, RefractorFlatArchiveEntry> _mesh = new(StringComparer.OrdinalIgnoreCase);
        public readonly string Name;

        public Level(string archives, string name)
        {
            Name = name;
            var dir = Path.Combine(archives, "BfVietnam", "levels");
            foreach (var p in new[] { name + "_001.rfa", name + ".rfa" })
                if (File.Exists(Path.Combine(dir, p))) _levels.Add(new RefractorFlatArchive(Path.Combine(dir, p)));
            _meshes = new RefractorFlatArchive(Path.Combine(archives, "standardMesh.rfa"));
            _objects = new RefractorFlatArchive(Path.Combine(archives, "objects.rfa"));
            foreach (var e in _meshes.Entries)
                if (e.Name.EndsWith(".sm", StringComparison.OrdinalIgnoreCase))
                    _mesh.TryAdd(Path.GetFileNameWithoutExtension(e.Name.Replace('\\', '/')), e);
        }

        public byte[] Bytes(string rel)
        {
            foreach (var a in _levels)
                foreach (var e in a.Entries)
                    if (e.Name.Replace('\\', '/').EndsWith("/" + Name + "/" + rel, StringComparison.OrdinalIgnoreCase)) return a.Read(e);
            throw new FileNotFoundException(rel);
        }
        public string Text(string rel) => Encoding.Latin1.GetString(Bytes(rel));

        public GrowthMeshInfo? Mesh(string name) =>
            _mesh.TryGetValue(name, out var e) ? GrowthMeshInfo.FromStandardMesh(_meshes.Read(e)) : null;

        public string MeshText(string leaf) =>
            Encoding.Latin1.GetString(_meshes.Read(_meshes.Entries.First(e => e.Name.EndsWith(leaf, StringComparison.OrdinalIgnoreCase))));

        public (TerrainConfig Cfg, GrowthTerrain Terrain) Terrain()
        {
            var lines = Text("Init/Terrain.con").Split('\n');
            var cfg = TerrainConfig.Parse(lines);
            var raw = Bytes("Heightmap.raw");
            int side = (int)Math.Round(Math.Sqrt(raw.Length / 2));
            var h = new ushort[side * side];
            Buffer.BlockCopy(raw, 0, h, 0, h.Length * 2);
            return (cfg, new GrowthTerrain(h, side, cfg.WorldSize, cfg.YScale));
        }

        public List<GrowthStatic> Statics()
        {
            var geo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in _objects.Entries)
            {
                if (!Path.GetFileName(e.Name.Replace('\\', '/')).Equals("objects.con", StringComparison.OrdinalIgnoreCase)) continue;
                string? cur = null;
                foreach (var line in Encoding.Latin1.GetString(_objects.Read(e)).Split('\n'))
                {
                    var p = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length >= 3 && p[0].Equals("ObjectTemplate.create", StringComparison.OrdinalIgnoreCase)) cur = p[2];
                    else if (p.Length >= 2 && p[0].Equals("ObjectTemplate.geometry", StringComparison.OrdinalIgnoreCase) && cur is not null) geo.TryAdd(cur, p[1]);
                }
            }
            var so = RefractorForge.Formats.Con.StaticObjectsFile.Parse(RefractorForge.Formats.Con.ConLines.Split(Text("StaticObjects.con")));
            var res = new List<GrowthStatic>();
            foreach (var o in so.Objects)
                res.Add(new GrowthStatic(res.Count, o.Template, geo.TryGetValue(o.Template, out var g) ? g : o.Template,
                    new Vector3(o.Position.X, o.Position.Y, o.Position.Z), new Vector3(o.Rotation.X, o.Rotation.Y, o.Rotation.Z),
                    new Vector3(o.EffectiveScale.X, o.EffectiveScale.Y, o.EffectiveScale.Z)));
            return res;
        }

        public OvergrowthResult Overgrowth(VegetationBuild build = VegetationBuild.Stock)
        {
            var (cfg, terrain) = Terrain();
            var def = GrowthDefinition.Parse(Text("Growth/overGrowth.wst"), true);
            var importable = GrowthDefinition.GeometryList(MeshText("overGrowthGeometries.wst"));
            return BfvGrowth.GenerateOvergrowth(def, Bytes("Growth/OverGrowthMap.raw"), 0, terrain, cfg.WorldSize, Mesh, importable, Statics(), build);
        }
    }

    [InstallFact(Installs.BfvOriginalArchives)]
    public void Irving_overgrowth_matches_the_reference_implementation()
    {
        var res = new Level(Installs.BfvOriginalArchives, "Operation_Irving").Overgrowth();
        Assert.Equal(14, res.GridW);
        var byType = res.Generated.GroupBy(i => i.Geometry).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(1449, res.Generated.Count());
        Assert.Equal(908, byType["c02f_trees_m2"]);
        Assert.Equal(370, byType["c08f_jungle_m2"]);
        Assert.Equal(139, byType["c01f_trees_m2"]);
        Assert.Equal(29, byType["o_rocks01_m2"]);
        Assert.Equal(3, byType["c04f_trees_m2"]);
        Assert.Equal(147, res.Imported.Count());
        Assert.Equal(26, res.Dropped.Count);
        // Around the US hangar (805, 502): the nearest generated tree is a palm 63 m west at (742.1, 502.2).
        var nearest = res.Generated.OrderBy(i => (i.X - 805) * (i.X - 805) + (i.Z - 502) * (i.Z - 502)).First();
        Assert.Equal("c02f_trees_m2", nearest.Geometry);
        Assert.Equal(742.1f, nearest.X, 0.1f);
        Assert.Equal(502.2f, nearest.Z, 0.1f);
    }

    [InstallFact(Installs.BfvOriginalArchives)]
    public void Irving_undergrowth_around_the_hangar_matches_the_reference_implementation()
    {
        var lvl = new Level(Installs.BfvOriginalArchives, "Operation_Irving");
        var (cfg, terrain) = lvl.Terrain();
        var def = GrowthDefinition.Parse(lvl.Text("Growth/underGrowth.wst"), false);
        var field = new UndergrowthField(def, lvl.Bytes("Growth/UnderGrowthMap.raw"), 0, terrain, cfg.WorldSize, lvl.Mesh);
        var cam = new Vector3(805f, (float)terrain.Height(805, 502) + 1.7f, 502f);
        var vis = field.Visible(cam);
        Assert.Equal(69, vis.Count);
        var all = vis.SelectMany(v => v.Patch.Instances).ToList();
        Assert.Equal(772, all.Count);
        var byType = all.GroupBy(i => i.Geometry).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(354, byType["F_undergrowthG08_m1"]);
        Assert.Equal(205, byType["F_undergrowthG07_m1"]);
        Assert.Equal(16, byType["F_undergrowthT02_m1"]);
        Assert.All(vis, v => Assert.True(v.Patch.UsedVertices <= 1024 && v.Patch.UsedIndices <= 4096 && v.Patch.Instances.Count <= 64));
    }

    /// <summary>The viewer's own path: meshes by exact name through <see cref="RefractorForge.Render.MeshLibrary"/>,
    /// the geometry list read out of its archives, each static's geometry from its template registry. It has to
    /// land on the same forest as the archive-level reader above.</summary>
    [InstallFact(Installs.BfvOriginalArchives)]
    public void Irving_overgrowth_through_the_mesh_library_matches()
    {
        var arch = Installs.BfvOriginalArchives;
        var lvl = new Level(arch, "Operation_Irving");
        var lib = RefractorForge.Render.MeshLibrary.Open(
            Path.Combine(arch, "BfVietnam", "levels", "Operation_Irving_001.rfa"), Path.Combine(arch, "BfVietnam", "levels", "Operation_Irving.rfa"),
            Path.Combine(arch, "standardMesh.rfa"), Path.Combine(arch, "objects.rfa"));
        Assert.True(lib.TryReadEntry("standardMesh/overGrowthGeometries.wst", out var gl));
        var importable = GrowthDefinition.GeometryList(Encoding.Latin1.GetString(gl));
        Assert.True(lib.TryGetExactMeshBytes("c02f_trees_m2", out _));
        Assert.False(lib.TryGetExactMeshBytes("c02f_trees", out _));       // no fuzzy _m1/_m2 or prefix matches
        Assert.Equal("C02F_Trees_M1", lib.GeometryTemplateOf("C02F_Trees_M1"), ignoreCase: true);

        GrowthMeshInfo? Mesh(string n) => lib.TryGetExactMeshBytes(n, out var b) ? GrowthMeshInfo.FromStandardMesh(b) : null;
        var (cfg, terrain) = lvl.Terrain();
        var so = RefractorForge.Formats.Con.StaticObjectsFile.Parse(RefractorForge.Formats.Con.ConLines.Split(lvl.Text("StaticObjects.con")));
        var statics = new List<GrowthStatic>();
        for (int i = 0; i < so.Objects.Count; i++)
        {
            var o = so.Objects[i]; var s = o.EffectiveScale;
            statics.Add(new GrowthStatic(i, o.Template, lib.GeometryTemplateOf(o.Template) ?? o.Template,
                new Vector3(o.Position.X, o.Position.Y, o.Position.Z), new Vector3(o.Rotation.X, o.Rotation.Y, o.Rotation.Z), new Vector3(s.X, s.Y, s.Z)));
        }
        var def = GrowthDefinition.Parse(lvl.Text("Growth/overGrowth.wst"), true);
        var res = BfvGrowth.GenerateOvergrowth(def, lvl.Bytes("Growth/OverGrowthMap.raw"), 0, terrain, cfg.WorldSize, Mesh, importable, statics);
        Assert.Equal(1449, res.Generated.Count());
        Assert.Equal(147, res.Imported.Count());
        Assert.Equal(26, res.Dropped.Count);
    }

    [Fact]
    public void Baked_rotation_reproduces_the_instance_matrix()
    {
        var inst = new GrowthInstance { X = 1, Z = 2, Scale = 1.3f, Yaw = 37f, Generated = true };
        inst.Up = Vector3.Normalize(new Vector3(0.12f, 1f, -0.07f));
        var r = inst.YawPitchRollDegrees();
        const float k = MathF.PI / 180f;
        var rebuilt = Matrix4x4.CreateFromYawPitchRoll(r.X * k, r.Y * k, r.Z * k);
        var m = inst.World();
        Vector3 Row(Matrix4x4 a, int i) => Vector3.Normalize(i == 0 ? new(a.M11, a.M12, a.M13) : i == 1 ? new(a.M21, a.M22, a.M23) : new(a.M31, a.M32, a.M33));
        for (int i = 0; i < 3; i++)
            Assert.True(Vector3.Distance(Row(m, i), Row(rebuilt, i)) < 1e-3f, $"row {i}: {Row(m, i)} vs {Row(rebuilt, i)}");
    }

    const string FlamingDartCapture = @"C:\Users\lucas\Downloads\bfvVegetationCaptureFlamingDart\tree_dump.bin";

    /// <summary>The strongest gate there is: a dump of the instances the RUNNING game generated on Operation Flaming
    /// Dart (BfVietnam_Veg_Ultra, 41,559 trees), read out of its OverGrowthManager. Every one must come out of the
    /// port at the same float position with the same scale.</summary>
    [InstallFact(FlamingDartCapture)]
    public void Flaming_dart_matches_a_capture_of_the_running_game_tree_for_tree()
    {
        if (!Directory.Exists(Installs.BfvOriginalArchives)) return;
        var res = new Level(Installs.BfvOriginalArchives, "Operation_Flaming_Dart").Overgrowth(VegetationBuild.VegUltra);
        var cap = OvergrowthCapture.LoadDump(File.ReadAllBytes(FlamingDartCapture));
        Assert.Equal(41559, cap.Count);
        var mine = res.All.GroupBy(i => (i.X, i.Z)).ToDictionary(g => g.Key, g => g.ToList());
        int matched = 0;
        foreach (var c in cap)
        {
            Assert.True(mine.TryGetValue((c.X, c.Z), out var l) && l.Count > 0, $"no instance at ({c.X}, {c.Z})");
            var m = l![0];
            l.RemoveAt(0);
            Assert.Equal(c.Scale, m.Scale);
            Assert.Equal(c.Y, m.Y, 0.001f);
            matched++;
        }
        Assert.Equal(cap.Count, matched);
        Assert.Equal(0, mine.Values.Sum(l => l.Count));
    }
}
