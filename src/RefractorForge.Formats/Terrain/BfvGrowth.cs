using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using RefractorForge.Formats.Rfa;

namespace RefractorForge.Formats.Terrain;

// Battlefield Vietnam's growth generator, reimplemented from the stock BfVietnam.exe (ImageBase 0x400000).
//
// The full evidence - every step with its address and a verified/inferred tag - is docs/BFV_Overgrowth_RE.md and
// docs/BFV_Undergrowth_RE.md; docs/BFV_Growth_Placement.md is the summary, and tools/bfv_growth/*.py the reference
// implementation this port is checked against instance for instance. The arithmetic below rounds to float32 where
// the engine stores to a float, because a placement test that flips on a last bit puts a tree somewhere else and
// every later draw in that cell with it.
//
// Overgrowth: the whole map once at level load, in square cells of viewDistance/4 snapped to fit the map, each cell
// seeded (23 + 13i + 4711j) & 0x7fffffff, 256 candidates, a 2048-vertex / 8192-index budget per cell.
// Undergrowth: the same loop per small display patch around the camera, never stored.

/// <summary>The engine's growth random generator (<c>Random::next</c> 0x4e0990): Lehmer, modulus 2^31-1, multiplier
/// 630360016 applied as two Carta steps (x24112 then x26143). Every caller turns a draw into a 24-bit integer
/// <c>k = ((r | 0x80) &gt;&gt; 7) + 1</c> and that into (0, 1] as <c>k * 2^-24</c>.</summary>
public struct GrowthRandom
{
    public const int Modulus = 0x7FFFFFFF;
    /// <summary>2^-24, the float at 0xb5d414.</summary>
    public const float TwoM24 = 1f / 16777216f;

    public int State;
    public GrowthRandom(int seed) { State = seed; }

    public int Next()
    {
        long s = State;
        s = s * 24112 % Modulus;
        s = s * 26143 % Modulus;
        State = (int)s;
        return State;
    }

    /// <summary>The 24-bit draw, 1..2^24.</summary>
    public int K() => ((Next() | 0x80) >> 7) + 1;

    /// <summary>A uniform draw in (0, 1].</summary>
    public float U() => (float)K() * TwoM24;

    /// <summary>A cell's or patch's seed. Overgrowth cells and undergrowth patches use the same formula with their
    /// own indices.</summary>
    public static int SeedFor(int i, int j) => (int)((23L + 13L * i + 4711L * j) & 0x7FFFFFFF);
}

/// <summary>A <c>.wst</c> random distribution, written <c>type/a/b/randomSign</c> (RandomDistribution::sample 0x5eff00).
/// Retail levels only ever use <c>CRDUniform</c> (scale) and the default rotation.</summary>
public readonly record struct GrowthDistribution(int Kind, float A, float B, bool RandomSign)
{
    public const int None = 0, Uniform = 1, Exponential = 2, Normal = 3;

    /// <summary>A type's scale when the <c>.wst</c> gives none: <c>CRDNone</c> with a = 1, i.e. always 1.</summary>
    public static readonly GrowthDistribution DefaultScale = new(None, 1f, 0f, false);
    /// <summary>A type's yaw when the <c>.wst</c> gives none: uniform over -180..180 degrees.</summary>
    public static readonly GrowthDistribution DefaultRotation = new(Uniform, -180f, 180f, false);

    public static GrowthDistribution Parse(string? text, GrowthDistribution fallback)
    {
        if (text is null) return fallback;
        var p = text.Trim().Split('/');
        int kind = p[0].Trim().ToLowerInvariant() switch
        {
            "crdnone" => None, "crduniform" => Uniform, "crdexponential" => Exponential, "crdnormal" => Normal, _ => None,
        };
        static float Num(string[] p, int i, float fallback) =>
            p.Length > i && p[i].Trim().Length > 0 &&
            double.TryParse(p[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? (float)v : fallback;
        bool sign = p.Length > 3 && (p[3].Trim().Equals("true", StringComparison.OrdinalIgnoreCase) || p[3].Trim() == "1");
        return new GrowthDistribution(kind, Num(p, 1, fallback.A), Num(p, 2, fallback.B), sign);
    }

    /// <summary>One sample. A random sign costs one draw before the value; <c>CRDUniform</c> one draw
    /// (<c>Random::uniform</c> 0x4e0a40); <c>CRDNone</c> none.</summary>
    public float Sample(ref GrowthRandom rng)
    {
        float sign = 1f;
        if (RandomSign) sign = rng.U() > 0.5f ? 1f : -1f;
        float v;
        switch (Kind)
        {
            case Uniform:
            {
                float k = rng.K();
                float span = B - A;
                float t = span * k;
                t = t * GrowthRandom.TwoM24;
                v = t + A;
                break;
            }
            case Exponential:
                // Not reversed (0x5ef910); no retail level uses it. One draw, so later draws stay in step.
                v = -A * MathF.Log(rng.U());
                break;
            case Normal:
            {
                // Not reversed (0x5efd90); no retail level uses it. Two draws, Box-Muller.
                float u1 = rng.U(), u2 = rng.U();
                v = MathF.Sqrt(-2f * MathF.Log(u1)) * MathF.Cos(2f * MathF.PI * u2) * B + A;
                break;
            }
            default:
                v = A;
                break;
        }
        return v * sign;
    }
}

/// <summary>One foliage type of a growth material, with the engine's own defaults for anything the <c>.wst</c>
/// leaves out (0x75e410): probability 1, both spacing factors 1, normalScale 1, scale 1, yaw -180..180. These are
/// NOT <see cref="FoliageType"/>'s defaults, which is why the generator reads the <c>.wst</c> itself.</summary>
public sealed class GrowthType
{
    public string Key { get; init; } = "";
    public string GeometryName { get; init; } = "";
    public float Probability { get; init; } = 1f;
    public float MinRadiusDistToEquals { get; init; } = 1f;
    public float MinRadiusDistToOthers { get; init; } = 1f;
    public float NormalScale { get; init; } = 1f;
    public GrowthDistribution Scale { get; init; } = GrowthDistribution.DefaultScale;
    public GrowthDistribution Rotation { get; init; } = GrowthDistribution.DefaultRotation;
    /// <summary>Index into the generator's records, or -1 when the geometry does not resolve (the roulette then
    /// skips the type WITHOUT subtracting its probability, as 0x760a04 does).</summary>
    public int Record { get; internal set; } = -1;
}

/// <summary>A growth <c>.wst</c> as the engine reads it: the manager's root properties (names matched
/// case-insensitively, so Irving's lower-case <c>viewdistance</c> counts) and, per terrain material, its types in
/// file order.</summary>
public sealed class GrowthDefinition
{
    /// <summary>The engine's material table (GrowthManager ctor 0x7600e0). A growth map's 4-bit cell value indexes
    /// the first sixteen; <c>dummy</c> is where overgrowth keeps <c>overGrowthGeometries.wst</c>.</summary>
    public static readonly string[] MaterialNames =
    {
        "default", "water", "dryGrass", "juicyGrass", "dryDirt", "wetDirt", "mud", "deathMaterial",
        "gravel", "muddyWater", "drySand", "wetSand", "rock", "sandRoad", "dirtRoad", "pavelRoad", "dummy",
    };

    public bool IsOver { get; init; }
    public float ViewDistance { get; init; }
    public int MaterialMapSideSize { get; init; }
    /// <summary>Clamped to 0..1 as the setter does (0x75e1a0). It scales the vertex budget, not a count.</summary>
    public float DensityScale { get; init; } = 1f;
    public bool ImportSceneObjects { get; init; }
    public float FadeRange { get; init; } = 0.3f;
    public float CloseToGroundFadeHeight { get; init; } = 1f;
    public float CloseToGroundFadeAlpha { get; init; } = 0.4f;
    public float CloseToGroundFadeDist { get; init; } = 12f;

    /// <summary>Types per engine material, indexed like <see cref="MaterialNames"/>.</summary>
    public IReadOnlyList<IReadOnlyList<GrowthType>> Materials { get; init; } = Array.Empty<IReadOnlyList<GrowthType>>();

    public IEnumerable<GrowthType> AllTypes => Materials.SelectMany(m => m);

    /// <summary>Parse an <c>overGrowth.wst</c> (<paramref name="over"/>) or <c>underGrowth.wst</c>. Uses the
    /// forgiving scanner: four retail levels ship <c>.wst</c> files that are not well-formed XML.</summary>
    public static GrowthDefinition Parse(string wstText, bool over)
    {
        var doc = WstNode.Parse(wstText ?? "");
        string want = over ? "overGrowth" : "underGrowth";
        var node = Find(doc, want) ?? Find(doc, over ? "underGrowth" : "overGrowth");
        var attrs = node?.Attributes ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        float F(string key, float def) =>
            attrs.TryGetValue(key, out var s) && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? (float)v : def;
        int I(string key, int def) =>
            attrs.TryGetValue(key, out var s) && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? (int)v : def;
        bool B(string key) =>
            attrs.TryGetValue(key, out var s) && (s.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) || s.Trim() == "1");

        var mats = new List<GrowthType>[MaterialNames.Length];
        for (int i = 0; i < mats.Length; i++) mats[i] = new List<GrowthType>();
        var materials = node is null ? null : Find(node, "materials");
        if (materials is not null)
            for (int mi = 0; mi < materials.Children.Count; mi++)
            {
                var m = materials.Children[mi];
                // By NAME, as the engine binds them; a name it does not know falls back to its position (a .wst is free
                // to rename a slot, Operation Flaming Dart renames slot 0). A repeated name ADDS to that material.
                int idx = Array.FindIndex(MaterialNames, n => n.Equals(m.Name, StringComparison.OrdinalIgnoreCase));
                if (idx < 0) idx = mi;
                if (idx >= mats.Length) continue;
                var types = Find(m, "types");
                if (types is null) continue;
                foreach (var t in types.Children)
                    mats[idx].Add(ParseType(t));
            }

        return new GrowthDefinition
        {
            IsOver = over,
            ViewDistance = F("viewDistance", over ? 500f : 70f),
            MaterialMapSideSize = I("materialMapSideSize", 512),
            DensityScale = Math.Clamp(F("densityScale", 1f), 0f, 1f),
            ImportSceneObjects = over && B("importSceneObjects"),
            FadeRange = F("fadeRange", 0.3f),
            CloseToGroundFadeHeight = F("closeToGroundFadeHeight", 1f),
            CloseToGroundFadeAlpha = F("closeToGroundFadeAlpha", 0.4f),
            CloseToGroundFadeDist = F("closeToGroundFadeDist", 12f),
            Materials = mats,
        };
    }

    private static GrowthType ParseType(WstNode t)
    {
        float F(string key, float def) =>
            t.Attr(key) is { } s && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? (float)v : def;
        return new GrowthType
        {
            Key = t.Name,
            GeometryName = t.Attr("geometryName") ?? "",
            Probability = F("probability", 1f),
            MinRadiusDistToEquals = F("minRadiusDistToEquals", 1f),
            MinRadiusDistToOthers = F("minRadiusDistToOthers", 1f),
            NormalScale = F("normalScale", 1f),
            Scale = GrowthDistribution.Parse(t.Attr("scale"), GrowthDistribution.DefaultScale),
            Rotation = GrowthDistribution.Parse(t.Attr("rotation"), GrowthDistribution.DefaultRotation),
        };
    }

    private static WstNode? Find(WstNode n, string name)
    {
        foreach (var c in n.Children)
        {
            if (c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return c;
            if (Find(c, name) is { } r) return r;
        }
        return null;
    }

    /// <summary>Every <c>geometryName</c> in a geometry list such as <c>standardMesh/overGrowthGeometries.wst</c>:
    /// the static objects overgrowth may take over (importSceneObjects). Compared case-insensitively (0x71ef8f).</summary>
    public static HashSet<string> GeometryList(string wstText)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(WstNode n)
        {
            foreach (var c in n.Children)
            {
                if (c.Attr("geometryName") is { Length: > 0 } g) set.Add(g.Trim());
                Walk(c);
            }
        }
        Walk(WstNode.Parse(wstText ?? ""));
        return set;
    }
}

/// <summary>What the generator needs from a mesh: its bounding box (the 6 floats at <c>.sm</c> offset 8) and the
/// vertex and index counts of its first LOD's first material - what one instance costs a cell
/// (<c>StandardMeshTemplate+0xa0/+0xa4</c>).</summary>
public readonly record struct GrowthMeshInfo(float MinX, float MinY, float MinZ, float MaxX, float MaxY, float MaxZ,
                                             int Vertices, int Indices)
{
    public static GrowthMeshInfo? FromStandardMesh(byte[] sm)
    {
        try
        {
            var m = StandardMesh.Parse(sm);
            var bb = m.BoundingBox;
            int v = 0, i = 0;
            if (m.Lods.Count > 0 && m.Lods[0].Count > 0) { v = m.Lods[0][0].NumVertices; i = m.Lods[0][0].NumFaceValues; }
            return new GrowthMeshInfo(bb[0], bb[1], bb[2], bb[3], bb[4], bb[5], v, i);
        }
        catch { return null; }
    }

    /// <summary>The bounding sphere about the mesh ORIGIN: the farthest of the box's 8 corners
    /// (<c>Object_006852d0</c>), after the box is scaled per axis as <c>setScale</c> (0x748860) does.</summary>
    public float CornerRadius(double sx = 1, double sy = 1, double sz = 1)
    {
        float best = 0f;
        for (int c = 0; c < 8; c++)
        {
            float x = (float)(((c & 1) != 0 ? MaxX : MinX) * sx);
            float y = (float)(((c & 2) != 0 ? MaxY : MinY) * sy);
            float z = (float)(((c & 4) != 0 ? MaxZ : MinZ) * sz);
            float xx = x * x, yy = y * y, zz = z * z;
            float d = xx + yy;
            d = d + zz;
            if (d > best) best = d;
        }
        return (float)Math.Sqrt(best);
    }
}

/// <summary>One per-geometry record (GrowthManager::sub_00760460): the <c>_m2</c> mesh an instance is batched as
/// far away, its <c>_m1</c> twin (the real object drawn close up), the radius spacing is measured with, and the
/// budget one instance costs.</summary>
public sealed class GrowthRecord
{
    public string Name { get; init; } = "";
    public string M1Name { get; init; } = "";
    public float Radius { get; init; }
    public int CostA { get; init; }
    public int CostB { get; init; }
}

/// <summary>The growth records, created in material/type order and keyed by EXACT name (the engine compares with
/// <c>repe cmpsb</c>, so an imported <c>C02F_Trees_M2</c> is its own record beside a generated
/// <c>c02f_trees_m2</c>). A geometry whose mesh cannot be found gets no record.</summary>
public sealed class GrowthRecords
{
    private readonly Func<string, GrowthMeshInfo?> _mesh;
    private readonly Dictionary<string, GrowthMeshInfo?> _meshCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);
    private readonly List<GrowthRecord> _all = new();

    public GrowthRecords(Func<string, GrowthMeshInfo?> meshInfo) { _mesh = meshInfo; }

    public IReadOnlyList<GrowthRecord> All => _all;

    public GrowthMeshInfo? Mesh(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (!_meshCache.TryGetValue(name, out var m)) { m = _mesh(name); _meshCache[name] = m; }
        return m;
    }

    /// <summary>The engine's twin rule: the last character becomes <paramref name="digit"/> when the one before it
    /// is an <c>m</c> (<c>c02f_trees_m2</c> &lt;-&gt; <c>c02f_trees_m1</c>).</summary>
    public static string Twin(string name, char digit) =>
        name.Length > 2 && char.ToLowerInvariant(name[^2]) == 'm' ? name[..^1] + digit : name;

    public int Resolve(string name)
    {
        if (_byName.TryGetValue(name, out var idx)) return idx;
        var info = Mesh(name);
        if (info is null) return -1;
        string m1 = Twin(name, '1');
        var rec = new GrowthRecord
        {
            Name = name, M1Name = m1, Radius = info.Value.CornerRadius(), CostA = info.Value.Vertices, CostB = info.Value.Indices,
        };
        int i = _all.Count;
        _all.Add(rec);
        _byName[name] = i;
        _byName.TryAdd(m1, i);
        return i;
    }
}

/// <summary>The terrain as growth samples it (<c>PatchTerrain::vf23</c> 0x722c10): the raw heightmap times
/// yScale/256, triangle-interpolated over each height post's cell with the diagonal alternating like the terrain
/// mesh, and the face normal of that triangle. Checked against Irving's hand-placed trees: median error 0.00005 m.</summary>
public sealed class GrowthTerrain
{
    private readonly ushort[] _h;
    private readonly int _side, _mask;
    private readonly double _cell, _inv, _scale;

    public GrowthTerrain(ushort[] samples, int side, double worldSize, double yScale)
    {
        _h = samples; _side = side; _mask = side - 1;
        _cell = worldSize / side; _inv = 1.0 / _cell; _scale = yScale / 256.0;
    }

    public static GrowthTerrain From(Heightmap hm, TerrainConfig cfg) => new(hm.Samples, hm.Width, cfg.WorldSize, cfg.YScale);

    private double Raw(int x, int z) => _h[(z & _mask) * _side + (x & _mask)];

    public double Height(double x, double z) => HeightNormal(x, z).H;

    public (double H, double Nx, double Ny, double Nz) HeightNormal(double x, double z)
    {
        double px = x * _inv, pz = z * _inv;
        int ix = (int)px, iz = (int)pz;
        double fx = px - ix, fz = pz - iz;
        double h, ax, az;
        if (((ix + iz) & 1) != 0)
        {
            // anti-diagonal (ix+1, iz) - (ix, iz+1)
            if (1.0 - fx > fz)
            {
                double b = Raw(ix, iz);
                ax = Raw(ix + 1, iz) - b; az = Raw(ix, iz + 1) - b;
                h = b + fx * ax + fz * az;
            }
            else
            {
                double b = Raw(ix + 1, iz + 1);
                double dx = Raw(ix, iz + 1) - b, dz = Raw(ix + 1, iz) - b;
                h = b + (1 - fx) * dx + (1 - fz) * dz;
                ax = -dx; az = -dz;
            }
        }
        else
        {
            // main diagonal (ix, iz) - (ix+1, iz+1)
            if (fx <= fz)
            {
                double b = Raw(ix, iz + 1);
                double dz = Raw(ix, iz) - b, dx = Raw(ix + 1, iz + 1) - b;
                h = b + (1 - fz) * dz + fx * dx;
                ax = dx; az = -dz;
            }
            else
            {
                double b = Raw(ix + 1, iz);
                double dx = Raw(ix, iz) - b, dz = Raw(ix + 1, iz + 1) - b;
                h = b + (1 - fx) * dx + fz * dz;
                ax = -dx; az = dz;
            }
        }
        double gx = ax * _scale / _cell, gz = az * _scale / _cell;
        double len = Math.Sqrt(gx * gx + 1.0 + gz * gz);
        return (h * _scale, -gx / len, 1.0 / len, -gz / len);
    }
}

/// <summary>One growth instance: a generated plant, or (overgrowth only) a static object the manager took over.</summary>
public sealed class GrowthInstance
{
    public int Record { get; init; }
    /// <summary>The geometry it was chosen as - the <c>.wst</c> name for a generated instance (overgrowth's
    /// <c>_m2</c>), the static's own geometry for an imported one.</summary>
    public string Geometry { get; init; } = "";
    public bool Generated { get; init; } = true;
    public int Material { get; init; }
    public float X { get; init; }
    public float Y { get; internal set; }
    public float Z { get; init; }
    public float Scale { get; init; } = 1f;
    public float Yaw { get; internal set; }
    public float NormalScale { get; init; } = 1f;
    public float R { get; init; }
    public float REq { get; init; }
    public float ROth { get; init; }
    /// <summary>The tilted up axis: the terrain normal with its horizontal part scaled by normalScale, normalised.</summary>
    public Vector3 Up { get; internal set; } = Vector3.UnitY;
    public int CellI { get; init; }
    public int CellJ { get; init; }
    /// <summary>For an imported static: its index in the level's static objects, template, rotation and scale.</summary>
    public int StaticIndex { get; init; } = -1;
    public string? Template { get; init; }
    public Vector3 StaticRotation { get; init; }
    public Vector3 StaticScale { get; init; } = Vector3.One;

    /// <summary>The engine's instance matrix (<c>vf13</c> 0x75e750, <c>QuadTreeCuller_006a0940</c>): rows right, up,
    /// forward with forward = (sin yaw, 0, cos yaw), right = up x forward, forward = right x up (the last two are
    /// not re-normalised, exactly as the engine leaves them), scaled by the instance scale, then the position. Row
    /// vectors, as System.Numerics uses them.</summary>
    public Matrix4x4 World()
    {
        double t = Yaw * Math.PI / 180.0;
        double fx = Math.Sin(t), fz = Math.Cos(t);
        double ux = Up.X, uy = Up.Y, uz = Up.Z;
        double rx = uy * fz - uz * 0.0, ry = uz * fx - ux * fz, rz = ux * 0.0 - uy * fx;
        double gx = ry * uz - rz * uy, gy = rz * ux - rx * uz, gz = rx * uy - ry * ux;
        float s = Scale;
        return new Matrix4x4(
            (float)(rx * s), (float)(ry * s), (float)(rz * s), 0f,
            (float)(ux * s), (float)(uy * s), (float)(uz * s), 0f,
            (float)(gx * s), (float)(gy * s), (float)(gz * s), 0f,
            X, Y, Z, 1f);
    }

    /// <summary>The <c>Object.rotation</c> (yaw, pitch, roll in degrees, the order
    /// <c>Matrix4x4.CreateFromYawPitchRoll</c> and the editor's object transform use) that turns a mesh the way the
    /// game turned this instance - its yaw and its lean toward the terrain normal. What baking a generated tree into
    /// a static object needs to keep it where, and as, the game drew it.</summary>
    public Vector3 YawPitchRollDegrees()
    {
        var m = World();
        var right = Vector3.Normalize(new Vector3(m.M11, m.M12, m.M13));
        var up = Vector3.Normalize(new Vector3(m.M21, m.M22, m.M23));
        var fwd = Vector3.Normalize(new Vector3(m.M31, m.M32, m.M33));
        float pitch = MathF.Asin(Math.Clamp(-fwd.Y, -1f, 1f));
        float yaw = MathF.Atan2(fwd.X, fwd.Z);
        float roll = MathF.Atan2(right.Y, up.Y);
        const float k = 180f / MathF.PI;
        return new Vector3(yaw * k, pitch * k, roll * k);
    }
}

/// <summary>A static object as overgrowth's import sees it, in <c>StaticObjects.con</c> order.</summary>
public readonly record struct GrowthStatic(int Index, string Template, string Geometry, Vector3 Position, Vector3 Rotation, Vector3 Scale);

/// <summary>Which executable to reproduce. The <c>BfVietnam_Veg_*</c> builds replace the patch size with a hard-coded
/// 12.5 m (overgrowth) / 17.5 m (undergrowth) at 0x75e110, and Low/Medium/High also skip a cell or patch unless
/// <c>lowbias32(seed) &gt;&gt; 30</c> is below 1/2/3 (the hook at 0x856bc1). Ultra keeps every cell.</summary>
public static class GrowthBuilds
{
    public static float? PatchSeedSize(VegetationBuild build, bool over) =>
        build == VegetationBuild.Stock ? null : over ? 12.5f : 17.5f;

    /// <summary>Does this build generate the cell or patch with this seed?</summary>
    public static bool Keeps(VegetationBuild build, int seed)
    {
        int tier = build switch { VegetationBuild.VegLow => 1, VegetationBuild.VegMedium => 2, VegetationBuild.VegHigh => 3, _ => 4 };
        if (tier >= 4) return true;
        uint x = (uint)seed;
        x ^= x >> 16; x *= 0x7feb352d;
        x ^= x >> 15; x *= 0x846ca68b;
        x ^= x >> 16;
        return (x >> 30) < tier;
    }
}

/// <summary>Why a cell's or patch's loop ended.</summary>
public enum GrowthStop { Iterations, Budget, Skipped }

/// <summary>The engine's per-cell loop (<c>GrowthManager::vf14</c> 0x760950), shared by both layers.</summary>
public static class GrowthLoop
{
    /// <summary>Run the loop over one square of <paramref name="size"/> metres at (<paramref name="x0"/>,
    /// <paramref name="z0"/>), appending to <paramref name="vec"/> (which may already hold obstacles - overgrowth's
    /// imported statics). Returns the budget left and why it stopped.</summary>
    public static (int A, int B, GrowthStop Stop, int Iterations) Run(
        ref GrowthRandom rng, int budgetA, int budgetB, float x0, float z0, float size,
        Func<float, float, int> materialAt, IReadOnlyList<IReadOnlyList<GrowthType>> materials,
        IReadOnlyList<GrowthRecord> records, List<GrowthInstance> vec, int cellI, int cellJ)
    {
        int nIter = budgetA / 8;   // C division (truncates toward zero)
        int it = 0;
        var stop = GrowthStop.Iterations;
        while (it < nIter)
        {
            // 0x75e7f0: x, then z, uniform over the whole square.
            float dx = (float)rng.K() * size; dx = dx * GrowthRandom.TwoM24;
            float dz = (float)rng.K() * size; dz = dz * GrowthRandom.TwoM24;
            float x = dx + x0;
            float z = dz + z0;
            int mat = materialAt(x, z);

            // The roulette draw is ALWAYS taken, even on a material with no types.
            float u = rng.U();
            GrowthType? sel = null;
            var types = (uint)mat < (uint)materials.Count ? materials[mat] : Array.Empty<GrowthType>();
            foreach (var t in types)
            {
                if (u < t.Probability)
                {
                    if (t.Record < 0) continue;     // unresolved: next type, u unchanged
                    sel = t; break;
                }
                u = u - t.Probability;
            }
            if (sel is null) { it++; continue; }

            var rec = records[sel.Record];
            if (rec.CostA > budgetA || rec.CostB > budgetB)
            {
                // The first type that no longer fits ENDS the cell, even if a cheaper one would (0x760c61).
                it++;
                stop = GrowthStop.Budget;
                break;
            }
            float s = sel.Scale.Sample(ref rng);
            float R = rec.Radius;
            float r = R * s;
            float req = R * sel.MinRadiusDistToEquals; req = req * s;
            float roth = R * sel.MinRadiusDistToOthers; roth = roth * s;
            // vf13: the yaw is drawn BEFORE the spacing test, so a rejected candidate consumes it too.
            float yaw = sel.Rotation.Sample(ref rng);

            bool ok = true;
            foreach (var o in vec)
            {
                float ddx = x - o.X, ddz = z - o.Z;
                float a2 = ddz * ddz, b2 = ddx * ddx;
                float d2 = a2 + b2;
                float rr = o.Record == sel.Record ? req + o.REq : roth + o.ROth;
                float rr2 = rr * rr;
                if (d2 < rr2) { ok = false; break; }
            }
            if (!ok) { it++; continue; }

            budgetA -= rec.CostA;
            budgetB -= rec.CostB;
            vec.Add(new GrowthInstance
            {
                Record = sel.Record, Geometry = sel.GeometryName, Generated = true, Material = mat,
                X = x, Z = z, Scale = s, Yaw = yaw, NormalScale = sel.NormalScale,
                R = r, REq = req, ROth = roth, CellI = cellI, CellJ = cellJ,
            });
            it += 2;   // a placement costs two iterations (0x760c37)
        }
        return (budgetA, budgetB, stop, Math.Min(it, nIter));
    }

    /// <summary>The material under (x, z) (<c>GrowthManager::vf8</c> 0x75e1e0): truncate x*side/world, wrap by the
    /// side as an UNSIGNED division, row = z, column = x, low four bits.</summary>
    public static int MaterialAt(byte[] map, int side, float sideOverWorld, float x, float z)
    {
        int xi = (int)(x * sideOverWorld), zi = (int)(z * sideOverWorld);
        uint ux = (uint)xi % (uint)side, uz = (uint)zi % (uint)side;
        long k = (long)uz * side + ux;
        return k < map.Length ? map[k] & 0x0F : 0;
    }

    /// <summary>Set y (the terrain height) and the tilted up axis of every generated instance.</summary>
    public static void Settle(IEnumerable<GrowthInstance> list, GrowthTerrain terrain)
    {
        foreach (var inst in list)
        {
            if (!inst.Generated) continue;
            var (h, nx, ny, nz) = terrain.HeightNormal(inst.X, inst.Z);
            inst.Y = (float)h;
            double ns = inst.NormalScale;
            double ux = nx * ns, uy = ny, uz = nz * ns;
            double l = Math.Sqrt(ux * ux + uy * uy + uz * uz);
            inst.Up = l > 0 ? new Vector3((float)(ux / l), (float)(uy / l), (float)(uz / l)) : Vector3.UnitY;
        }
    }
}

/// <summary>A whole map's overgrowth, generated the way the stock exe does it at level load (OverGrowthManager::vf7
/// 0x71f4c0): cells of <see cref="CellSize"/> on a <see cref="GridW"/> x <see cref="GridH"/> grid, statics listed in
/// <c>overGrowthGeometries.wst</c> taken over first, then every cell's loop.</summary>
public sealed class OvergrowthResult
{
    public float CellSize { get; init; }
    public int GridW { get; init; }
    public int GridH { get; init; }
    public float ViewDistance { get; init; }
    public float DensityScale { get; init; }
    public bool ImportSceneObjects { get; init; }
    public IReadOnlyList<GrowthRecord> Records { get; init; } = Array.Empty<GrowthRecord>();
    /// <summary>Each cell's instances (imported first, then generated), index j * GridW + i.</summary>
    public IReadOnlyList<List<GrowthInstance>> Cells { get; init; } = Array.Empty<List<GrowthInstance>>();
    public IReadOnlyList<GrowthStop> CellStops { get; init; } = Array.Empty<GrowthStop>();
    /// <summary>Statics in the geometry list that did not fit their cell's budget: they stay plain objects.</summary>
    public IReadOnlyList<GrowthInstance> Dropped { get; init; } = Array.Empty<GrowthInstance>();

    public IEnumerable<GrowthInstance> All => Cells.SelectMany(c => c);
    public IEnumerable<GrowthInstance> Generated => All.Where(i => i.Generated);
    public IEnumerable<GrowthInstance> Imported => All.Where(i => !i.Generated);

    /// <summary>How many cells ran out of budget (the usual limit on a forest's density).</summary>
    public int CellsStoppedOnBudget => CellStops.Count(s => s == GrowthStop.Budget);
}

public static class BfvGrowth
{
    /// <summary>The engine's cell layout for a layer. Overgrowth: <c>viewDistance/(N/2-1)</c> with N = 10 (or the
    /// Veg build's hard-coded seed), snapped so a whole number of cells fits the map (0x71dc90). Undergrowth: the
    /// same seed snapped to divide the 256 m terrain colour tile (0x71fda0).</summary>
    public static float CellSize(bool over, float viewDistance, float worldSize, VegetationBuild build = VegetationBuild.Stock)
    {
        float ps0 = GrowthBuilds.PatchSeedSize(build, over) ?? (float)(viewDistance / 4.0);
        if (ps0 <= 0f) ps0 = over ? 125f : 17.5f;
        if (over)
        {
            float w = worldSize;
            float q = w / ps0;
            float n = (float)Math.Floor(q);
            if (n < 1f) n = 1f;
            return w / n;
        }
        else
        {
            float q = 256f / ps0;
            float n = (float)Math.Floor(q);
            if (n < 1f) n = 1f;
            return 256f / n;
        }
    }

    /// <summary>Generate a map's overgrowth exactly as the game does.</summary>
    /// <param name="map">The raw <c>OverGrowthMap.raw</c>, row = z, column = x.</param>
    /// <param name="meshInfo">Finds a mesh by exact name (case-insensitively): its box and LOD0 material0 counts.</param>
    /// <param name="importable">The geometry list <c>overGrowthGeometries.wst</c> names (null = no import).</param>
    /// <param name="statics">The level's static objects in <c>StaticObjects.con</c> order.</param>
    public static OvergrowthResult GenerateOvergrowth(GrowthDefinition def, byte[] map, int mapSide, GrowthTerrain terrain,
                                                      float worldSize, Func<string, GrowthMeshInfo?> meshInfo,
                                                      IReadOnlyCollection<string>? importable = null,
                                                      IReadOnlyList<GrowthStatic>? statics = null,
                                                      VegetationBuild build = VegetationBuild.Stock)
    {
        int side = SideFor(def, map, mapSide);
        int world = (int)worldSize;
        float ps = CellSize(true, def.ViewDistance, world, build);
        double inv = 1.0 / ps;
        int W = Math.Max(1, (int)(world * inv + 0.5)), H = W;
        float mscale = (float)side / (float)world;

        var records = new GrowthRecords(meshInfo);
        foreach (var types in def.Materials)
            foreach (var t in types)
                t.Record = records.Resolve(t.GeometryName);

        int budgetA0 = (int)(def.DensityScale * 2048f), budgetB0 = 0x2000;
        var cells = new List<GrowthInstance>[W * H];
        var budA = new int[W * H];
        var budB = new int[W * H];
        for (int k = 0; k < cells.Length; k++) { cells[k] = new List<GrowthInstance>(); budA[k] = budgetA0; budB[k] = budgetB0; }

        // importSceneObjects (0x71ef40): statics whose geometry the list names go into their cell first, use its budget
        // and stand as spacing obstacles. Those that do not fit stay ordinary objects.
        var dropped = new List<GrowthInstance>();
        if (def.ImportSceneObjects && importable is { Count: > 0 } && statics is not null)
        {
            var set = importable as HashSet<string> ?? new HashSet<string>(importable, StringComparer.OrdinalIgnoreCase);
            foreach (var o in statics)
            {
                string g = string.IsNullOrEmpty(o.Geometry) ? o.Template : o.Geometry;
                if (!set.Contains(g)) continue;
                int ridx = records.Resolve(GrowthRecords.Twin(g, '2'));
                var m1 = records.Mesh(g);
                if (m1 is null || ridx < 0) continue;
                float R = m1.Value.CornerRadius(o.Scale.X, o.Scale.Y, o.Scale.Z);
                float r = R * o.Scale.Y;
                float qx = o.Position.X / ps, qz = o.Position.Z / ps;
                int ci = Mod((int)qx, W), cj = Mod((int)qz, H);
                cells[cj * W + ci].Add(new GrowthInstance
                {
                    Record = ridx, Geometry = g, Generated = false, X = o.Position.X, Y = o.Position.Y, Z = o.Position.Z,
                    Scale = o.Scale.Y, R = r, REq = r, ROth = r, CellI = ci, CellJ = cj,
                    StaticIndex = o.Index, Template = o.Template, StaticRotation = o.Rotation, StaticScale = o.Scale,
                });
            }
            for (int k = 0; k < cells.Length; k++)
            {
                var c = cells[k];
                int keep = 0;
                foreach (var inst in c)
                {
                    var rec = records.All[inst.Record];
                    int a = budA[k] - rec.CostA;
                    if (a < 0) break;
                    int b = budB[k] - rec.CostB;
                    if (b < 0) break;
                    budA[k] = a; budB[k] = b;
                    keep++;
                }
                if (keep < c.Count) { dropped.AddRange(c.Skip(keep)); c.RemoveRange(keep, c.Count - keep); }
            }
        }

        var stops = new GrowthStop[W * H];
        var mats = def.Materials;
        int MatAt(float x, float z) => GrowthLoop.MaterialAt(map, side, mscale, x, z);
        for (int j = 0; j < H; j++)
            for (int i = 0; i < W; i++)
            {
                int k = j * W + i;
                int seed = GrowthRandom.SeedFor(i, j);
                if (!GrowthBuilds.Keeps(build, seed)) { stops[k] = GrowthStop.Skipped; continue; }
                var rng = new GrowthRandom(seed);
                float x0 = (float)i * ps, z0 = (float)j * ps;
                var res = GrowthLoop.Run(ref rng, budA[k], budB[k], x0, z0, ps, MatAt, mats, records.All, cells[k], i, j);
                budA[k] = res.A; budB[k] = res.B; stops[k] = res.Stop;
            }

        foreach (var c in cells) GrowthLoop.Settle(c, terrain);
        return new OvergrowthResult
        {
            CellSize = ps, GridW = W, GridH = H, ViewDistance = def.ViewDistance, DensityScale = def.DensityScale,
            ImportSceneObjects = def.ImportSceneObjects, Records = records.All, Cells = cells, CellStops = stops, Dropped = dropped,
        };
    }

    /// <summary>The map side the engine reads: the <c>.wst</c>'s materialMapSideSize when the file holds that many
    /// cells (the engine loads that many from the raw), else the file's own square side.</summary>
    public static int SideFor(GrowthDefinition def, byte[] map, int fileSide)
    {
        int s = def.MaterialMapSideSize;
        if (s > 0 && (long)s * s <= map.Length) return s;
        return fileSide > 0 ? fileSide : Math.Max(1, (int)Math.Sqrt(map.Length));
    }

    private static int Mod(int a, int m) { int r = a % m; return r < 0 ? r + m : r; }
}

/// <summary>One undergrowth display patch, generated on demand (GrowthManager::vf11 0x75e140).</summary>
public sealed class GrowthPatch
{
    public int Px { get; init; }
    public int Pz { get; init; }
    public int Seed { get; init; }
    public float X0 { get; init; }
    public float Y0 { get; init; }
    public float Z0 { get; init; }
    public List<GrowthInstance> Instances { get; init; } = new();
    public GrowthStop Stop { get; init; }
    public int Iterations { get; init; }
    public int UsedVertices { get; init; }
    public int UsedIndices { get; init; }
}

/// <summary>A level's undergrowth: the patch grid, the generator per patch, and the rules deciding which patches
/// the game shows around a camera and how opaque each is. Patches are never stored by the game, but a patch that
/// comes back regenerates identically, so a cache keyed on (px, pz) is exact.</summary>
public sealed class UndergrowthField
{
    private readonly GrowthDefinition _def;
    private readonly byte[] _map;
    private readonly int _side;
    private readonly float _mscale;
    private readonly GrowthTerrain _terrain;
    private readonly GrowthRecords _records;
    private readonly VegetationBuild _build;
    private readonly Dictionary<(int, int), GrowthPatch> _cache = new();

    /// <summary>Display slots per side of the camera's ring (N; the Veg builds' 34 only widens a ring the view
    /// distance already limits).</summary>
    public const int RingSlots = 10;

    public float PatchSize { get; }
    public float ViewDistance => _def.ViewDistance;
    public float WorldSize { get; }
    public GrowthDefinition Definition => _def;
    public IReadOnlyList<GrowthRecord> Records => _records.All;
    /// <summary>The vertex budget per patch, 1024 x densityScale (the patch buffer's capacity).</summary>
    public int BudgetA { get; }
    public const int BudgetB = 0x1000;

    public UndergrowthField(GrowthDefinition def, byte[] map, int mapSide, GrowthTerrain terrain, float worldSize,
                            Func<string, GrowthMeshInfo?> meshInfo, VegetationBuild build = VegetationBuild.Stock)
    {
        _def = def; _map = map; _terrain = terrain; _build = build;
        _side = BfvGrowth.SideFor(def, map, mapSide);
        WorldSize = worldSize;
        _mscale = (float)_side / (float)(int)worldSize;
        PatchSize = BfvGrowth.CellSize(false, def.ViewDistance, worldSize, build);
        BudgetA = (int)(1024f * def.DensityScale);
        _records = new GrowthRecords(meshInfo);
        foreach (var types in def.Materials)
            foreach (var t in types)
                t.Record = _records.Resolve(t.GeometryName);
    }

    /// <summary>The patch origin, its y the terrain height at the patch centre.</summary>
    public (float X, float Y, float Z) Origin(int px, int pz)
    {
        float ps = PatchSize;
        float x0 = (float)px * ps, z0 = (float)pz * ps;
        float half = ps * 0.5f;
        float cx = half + x0, cz = half + z0;
        return (x0, (float)_terrain.Height(cx, cz), z0);
    }

    public GrowthPatch Patch(int px, int pz)
    {
        if (_cache.TryGetValue((px, pz), out var p)) return p;
        p = Generate(px, pz);
        if (_cache.Count > 20000) _cache.Clear();
        _cache[(px, pz)] = p;
        return p;
    }

    public void ClearCache() => _cache.Clear();

    private GrowthPatch Generate(int px, int pz)
    {
        var (x0, y0, z0) = Origin(px, pz);
        int seed = (int)(((long)pz * 4711 + (long)px * 13 + 23) & 0x7FFFFFFF);
        var vec = new List<GrowthInstance>();
        if (!GrowthBuilds.Keeps(_build, seed))
            return new GrowthPatch { Px = px, Pz = pz, Seed = seed, X0 = x0, Y0 = y0, Z0 = z0, Instances = vec, Stop = GrowthStop.Skipped };
        var rng = new GrowthRandom(seed);
        int MatAt(float x, float z) => GrowthLoop.MaterialAt(_map, _side, _mscale, x, z);
        var res = GrowthLoop.Run(ref rng, BudgetA, BudgetB, x0, z0, PatchSize, MatAt, _def.Materials, _records.All, vec, px, pz);
        GrowthLoop.Settle(vec, _terrain);
        return new GrowthPatch
        {
            Px = px, Pz = pz, Seed = seed, X0 = x0, Y0 = y0, Z0 = z0, Instances = vec, Stop = res.Stop,
            Iterations = res.Iterations, UsedVertices = BudgetA - res.A, UsedIndices = BudgetB - res.B,
        };
    }

    /// <summary>The ring of slots the game keeps around a camera (GrowthManager::vf9 0x75f070): patches cx-4..cx+5.</summary>
    public IEnumerable<(int Px, int Pz)> Ring(Vector3 cam)
    {
        double inv = 1.0 / PatchSize;
        int cx = (int)(cam.X * inv), cz = (int)(cam.Z * inv);
        const int N = RingSlots;
        for (int j = 0; j < N; j++)
            for (int i = 0; i < N; i++)
            {
                int px = N / 2 - FloorMod(N * 100 + i + cx, N) + cx;
                int pz = N / 2 - FloorMod(N * 100 + j + cz, N) + cz;
                yield return (px, pz);
            }
    }

    /// <summary>The game's patch distance (0x75e4c0): the nearest of the patch's four corners, at the origin's
    /// height, with the vertical difference weighted 0.2.</summary>
    public double PatchDistance(int px, int pz, Vector3 cam)
    {
        var (x0, y0, z0) = Origin(px, pz);
        float x1 = x0 + PatchSize, z1 = z0 + PatchSize;
        double dy = (y0 - cam.Y) * 0.2;
        double best = double.MaxValue;
        foreach (var (cx, cz) in new[] { (x0, z0), (x1, z0), (x1, z1), (x0, z1) })
        {
            double dx = cx - cam.X, dz = cz - cam.Z;
            double d2 = dx * dx + dy * dy + dz * dz;
            if (d2 < best) best = d2;
        }
        return Math.Sqrt(best);
    }

    /// <summary>How opaque the game draws a patch: fully to (1 - fadeRange) x viewDistance, then linearly to nothing
    /// at viewDistance (0x75f270..), times the close-to-ground factor that only bites with the eye under a metre
    /// above the terrain (0x75f428.., 0x75f684..).</summary>
    public float PatchAlpha(double distance, Vector3 cam, double centreDistance)
    {
        double r = distance / Math.Max(ViewDistance, 1e-3);
        double fr = _def.FadeRange;
        double a = r <= 1.0 - fr ? 1.0 : fr > 0 ? Math.Clamp((1.0 - r) / fr, 0.0, 1.0) : 0.0;
        double h = _terrain.Height(cam.X, cam.Z);
        double g = Math.Abs(cam.Y - h) / Math.Max(_def.CloseToGroundFadeHeight, 1e-3);
        g = Math.Clamp(g, _def.CloseToGroundFadeAlpha, 1.0);
        double f = Math.Clamp(centreDistance / Math.Max(_def.CloseToGroundFadeDist, 1e-3), g, 1.0);
        return (float)(a * f);
    }

    /// <summary>What the game draws for a camera here: every ring slot within the view distance, nearest first,
    /// with its alpha. Slots outside the map are left out - there is no ground for them.</summary>
    public List<(GrowthPatch Patch, double Distance, float Alpha)> Visible(Vector3 cam)
    {
        var res = new List<(GrowthPatch, double, float)>();
        foreach (var (px, pz) in Ring(cam))
        {
            if (!InsideMap(px, pz)) continue;
            double d = PatchDistance(px, pz, cam);
            if (d >= ViewDistance) continue;
            var p = Patch(px, pz);
            double half = PatchSize * 0.5;
            double cdx = p.X0 + half - cam.X, cdy = p.Y0 - cam.Y, cdz = p.Z0 + half - cam.Z;
            double cd = Math.Sqrt(cdx * cdx + cdy * cdy + cdz * cdz);
            res.Add((p, d, PatchAlpha(d, cam, cd)));
        }
        res.Sort((a, b) => a.Item2.CompareTo(b.Item2));
        return res;
    }

    /// <summary>Every patch whose nearest corner lies within <paramref name="radius"/> metres of (x, z), ignoring
    /// height - for looking at a stretch of undergrowth from above, which the game itself never draws.</summary>
    public List<GrowthPatch> Around(float x, float z, float radius)
    {
        var res = new List<GrowthPatch>();
        float ps = PatchSize;
        int r = (int)Math.Ceiling(radius / ps) + 1;
        int cx = (int)Math.Floor(x / ps), cz = (int)Math.Floor(z / ps);
        for (int pz = cz - r; pz <= cz + r; pz++)
            for (int px = cx - r; px <= cx + r; px++)
            {
                if (!InsideMap(px, pz)) continue;
                float x0 = px * ps, z0 = pz * ps;
                float nx = Math.Clamp(x, x0, x0 + ps), nz = Math.Clamp(z, z0, z0 + ps);
                if ((nx - x) * (nx - x) + (nz - z) * (nz - z) > radius * radius) continue;
                res.Add(Patch(px, pz));
            }
        return res;
    }

    /// <summary>A patch whose origin lies on the map. Patches tile the map exactly (the size divides 256 and maps are
    /// multiples of 256), so this is every patch with ground under it.</summary>
    public bool InsideMap(int px, int pz) =>
        px >= 0 && pz >= 0 && (float)px * PatchSize < WorldSize && (float)pz * PatchSize < WorldSize;

    private static int FloorMod(int a, int m) { int r = a % m; return r < 0 ? r + m : r; }
}
