using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using RefractorForge.Formats;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Rfa;
using RefractorForge.Formats.Terrain;

namespace RefractorForge.Render;

/// <summary>
/// A level whose one screenshot says what the game's lighting formula really is.
///
/// <para>Everything the editor knows about how BfVietnam lights an object was read out of the exe and its shader
/// files (<see cref="EngineLight"/>, docs/Realistic_Lighting.md). That is a reading, not a measurement. This builds
/// the measurement: a copy of a retail level - so it is certain to load - emptied of its buildings and given a row
/// of test boards, with the level's four light colours set to pure red (the sun), pure green (the back light) and
/// blue (the two ambients). Every board is a black plate carrying fifty white patches, and each patch isolates one
/// term: its vertex NORMAL is authored (the triangles all face the camera, the normals point round the whole
/// sphere), its lightmap value is one of five known levels, and so its colour on screen is the formula's answer
/// for that normal and that value, channel by channel.</para>
///
/// <para>The same board is written in both vertex layouts the engine draws lit meshes from - with a tangent frame
/// (the per-pixel shaders most retail buildings take) and without (the fixed-function ones every imported model
/// and every mesh the editor widens for a lightmap takes) - and placed with a lightmap, without one, with a
/// coloured one and with a flat one on a mesh that has no unwrap. <see cref="FindBlackQuads"/>,
/// <see cref="SampleBoard"/> and <see cref="SolveLight"/> then read a screenshot back into numbers.</para>
/// </summary>
public static class LightCalibration
{
    // ---------------------------------------------------------------------------------------------------------
    // The board
    // ---------------------------------------------------------------------------------------------------------

    public const int Columns = 10, Rows = 5;
    /// <summary>Centre-to-centre spacing of the patches, the side of one patch, and the plate's rim, in metres.</summary>
    public const float Pitch = 0.6f, PatchSize = 0.46f, Border = 0.3f;
    public const float BoardWidth = Columns * Pitch + 2f * Border;
    public const float BoardHeight = Rows * Pitch + 2f * Border;

    /// <summary>The five lightmap values a patch can carry.</summary>
    public static readonly byte[] Levels = { 0, 64, 128, 191, 255 };

    /// <summary>One patch: where it is, which way its normal points, and what its lightmap texel holds.</summary>
    /// <param name="Level">Index into <see cref="Levels"/>.</param>
    /// <param name="HalfMaterial">Drawn with <c>materialDiffuse 0.5</c> instead of 1.</param>
    /// <param name="Empty">No patch here - the plate shows through. The top-left cell, so a reading can tell which
    /// way up and which way round it found the board.</param>
    public readonly record struct Cell(int Column, int Row, string Label, Vector3 Normal, int Level, bool HalfMaterial = false, bool Empty = false)
    {
        public float Prelight => Levels[Level] / 255f;
    }

    /// <summary>The back light the exe's upload routine builds: the sun mirrored in azimuth, at its own height.</summary>
    public static Vector3 PredictedBack(Vector3 toSun) => EngineLight.BackLight(toSun);

    /// <summary>A unit vector at a compass bearing (0 = north = +Z, 90 = east = +X) and a height above the horizon.</summary>
    public static Vector3 Direction(float azimuthDeg, float elevationDeg)
    {
        float a = azimuthDeg * MathF.PI / 180f, e = elevationDeg * MathF.PI / 180f;
        return new Vector3(MathF.Sin(a) * MathF.Cos(e), MathF.Sin(e), MathF.Cos(a) * MathF.Cos(e));
    }

    /// <summary>
    /// The fifty cells, row 0 at the top. Rows 0-2 are a fan of normals at three heights and eight bearings at
    /// half lightmap strength (so nothing clips whichever formula is true); row 3 runs the lightmap from 0 to 1
    /// on the patch that faces the sun and on the one that faces the predicted back light; row 4 does the same
    /// for faces looking straight down and up, then the half-diffuse material, then two more directions.
    /// </summary>
    public static Cell[] Cells(Vector3 toSun)
    {
        var L = Vector3.Normalize(toSun);
        var B = PredictedBack(L);
        var up = Vector3.UnitY;
        // Level with the ground and square to both lights: neither reaches it, so it shows the ambient alone.
        var side = Vector3.Normalize(new Vector3(L.Z, 0f, -L.X));
        var list = new List<Cell>(Columns * Rows);
        void Add(int c, int r, string label, Vector3 n, int level, bool half = false) => list.Add(new Cell(c, r, label, Vector3.Normalize(n), level, half));

        list.Add(new Cell(0, 0, "mark", up, 4, Empty: true));
        for (int i = 0; i < 8; i++) Add(1 + i, 0, $"up45 az{i * 45:000}", Direction(i * 45f, 45f), 2);
        Add(9, 0, "up", up, 2);

        for (int i = 0; i < 8; i++) Add(i, 1, $"level az{i * 45:000}", Direction(i * 45f, 0f), 2);
        Add(8, 1, "to sun", L, 2);
        Add(9, 1, "to back", B, 2);

        for (int i = 0; i < 8; i++) Add(i, 2, $"down45 az{i * 45:000}", Direction(i * 45f, -45f), 2);
        Add(8, 2, "down", -up, 2);
        Add(9, 2, "from sun", -L, 2);

        for (int k = 0; k < 5; k++) Add(k, 3, $"to sun P{k}", L, k);
        for (int k = 0; k < 5; k++) Add(5 + k, 3, $"to back P{k}", B, k);

        Add(0, 4, "down P0", -up, 0);
        Add(1, 4, "down P2", -up, 2);
        Add(2, 4, "down P4", -up, 4);
        Add(3, 4, "up P0", up, 0);
        Add(4, 4, "up P4", up, 4);
        Add(5, 4, "half to sun P4", L, 4, half: true);
        Add(6, 4, "half to back P4", B, 4, half: true);
        Add(7, 4, "half down P4", -up, 4, half: true);
        Add(8, 4, "from back", -B, 2);
        Add(9, 4, "side", side, 2);
        return list.OrderBy(c => c.Row).ThenBy(c => c.Column).ToArray();
    }

    /// <summary>A cell's centre on the plate, as fractions of its width and height from the top-left corner.</summary>
    public static Vector2 CellCentre(int column, int row)
        => new((Border + (column + 0.5f) * Pitch) / BoardWidth, (Border + (row + 0.5f) * Pitch) / BoardHeight);

    // The lightmap is an 8 x 8 grid of flat cells, mirrored left-right and top-bottom, so the value a patch reads
    // does not depend on which corner the engine calls the origin. A patch's four vertices all carry the CENTRE of
    // its cell, so filtering and mip selection cannot bleed a neighbour in.
    private const int LmSide = 128, LmCells = 8;

    private static (float U, float V) LevelUv(int level)
        => ((level % 4 + 0.5f) / LmCells, (level / 4 + 0.5f) / LmCells);

    private static int LmSlot(int px, int py)
    {
        int c = px * LmCells / LmSide, r = py * LmCells / LmSide;
        return Math.Min(c, LmCells - 1 - c) + 4 * Math.Min(r, LmCells - 1 - r);
    }

    /// <summary>The grey value of every lightmap texel, top row first.</summary>
    public static byte[] LightmapValues()
    {
        var v = new byte[LmSide * LmSide];
        for (int y = 0; y < LmSide; y++)
            for (int x = 0; x < LmSide; x++)
            {
                int slot = LmSlot(x, y);
                v[y * LmSide + x] = slot < Levels.Length ? Levels[slot] : (byte)128;
            }
        return v;
    }

    /// <summary>What a board's lightmap file is.</summary>
    public enum LightmapKind
    {
        /// <summary>No file at all: the engine lights the object without one.</summary>
        None,
        /// <summary>The retail form: 8-bit colour-mapped, grey ramp palette.</summary>
        Grey,
        /// <summary>24-bit colour, each level written as (v, v/2, 3v/4): which channel, or all three?</summary>
        Rgb24,
        /// <summary>The retail 8-bit form with the PALETTE recoloured the same way.</summary>
        ColourPalette,
        /// <summary>One value (128) over the whole map: what a mesh with no unwrap is given.</summary>
        Uniform,
    }

    /// <summary>The channel weights the two coloured lightmap kinds write a level with.</summary>
    public static readonly Vector3 ColourWeights = new(1f, 0.5f, 0.75f);

    public static byte[]? EncodeLightmap(LightmapKind kind)
    {
        if (kind == LightmapKind.None) return null;
        var grey = LightmapValues();
        if (kind == LightmapKind.Uniform) Array.Fill(grey, (byte)128);
        var rgba = new byte[LmSide * LmSide * 4];
        for (int i = 0; i < grey.Length; i++)
        {
            byte g = grey[i];
            if (kind == LightmapKind.Rgb24)
            {
                rgba[i * 4] = g; rgba[i * 4 + 1] = (byte)(g / 2); rgba[i * 4 + 2] = (byte)(g * 3 / 4);
            }
            else { rgba[i * 4] = g; rgba[i * 4 + 1] = g; rgba[i * 4 + 2] = g; }
            rgba[i * 4 + 3] = 255;
        }
        var tex = new Texture2D(LmSide, LmSide, rgba);
        if (kind == LightmapKind.Rgb24) return TgaTexture.EncodeRgb24(tex);
        var file = TgaTexture.EncodeGrayColormapped(tex);
        if (kind == LightmapKind.ColourPalette)
            for (int i = 0; i < 256; i++)
            {
                int p = 18 + i * 3;                                   // palette entries are B, G, R
                file[p] = (byte)(i * 3 / 4); file[p + 1] = (byte)(i / 2); file[p + 2] = (byte)i;
            }
        return file;
    }

    /// <summary>The lightmap value a patch at <paramref name="level"/> reads from a file of this kind, per channel.</summary>
    public static Vector3 LightmapRgb(LightmapKind kind, int level)
    {
        float v = Levels[level] / 255f;
        return kind switch
        {
            LightmapKind.None => Vector3.One,
            LightmapKind.Uniform => new Vector3(128f / 255f),
            LightmapKind.Rgb24 or LightmapKind.ColourPalette => new Vector3(Levels[level] / 255f, (Levels[level] / 2) / 255f, (Levels[level] * 3 / 4) / 255f),
            _ => new Vector3(v),
        };
    }

    /// <summary>
    /// The board as a mesh: a black plate facing -Z with its origin at the bottom centre, the patches two
    /// centimetres proud of it. Every face is written twice, once each way round, so the board shows whichever
    /// way the engine culls - the normals are authored either way.
    /// </summary>
    /// <param name="withUnwrap">False leaves the lightmap channel empty (every vertex at 0,0) and the header's
    /// qflag clear - the state most retail meshes are in.</param>
    public static byte[] BuildMesh(string meshName, Vector3 toSun, StandardMeshWriter.LitLayout layout, bool withUnwrap)
    {
        var full = NewSection(); var half = NewSection(); var plate = NewSection();
        float hw = BoardWidth * 0.5f;

        void Quad((List<Vec3> P, List<Vec3> N, List<(float, float)> Uv, List<(float, float)> Lm, List<(int, int, int)> F) s,
                  float x0, float y0, float x1, float y1, float z, Vector3 n, (float U, float V) lm)
        {
            foreach (bool back in new[] { false, true })
            {
                int b = s.P.Count;
                float zz = back ? -z : z;
                s.P.Add(new Vec3(x0, y0, zz)); s.P.Add(new Vec3(x1, y0, zz)); s.P.Add(new Vec3(x1, y1, zz)); s.P.Add(new Vec3(x0, y1, zz));
                for (int i = 0; i < 4; i++)
                {
                    s.N.Add(new Vec3(n.X, n.Y, n.Z));
                    s.Uv.Add((0.5f, 0.5f));
                    s.Lm.Add(withUnwrap ? lm : (0f, 0f));
                }
                // The winding DecalObject's upright quad uses for its -Z face, and the same one reversed.
                if (!back) { s.F.Add((b, b + 1, b + 2)); s.F.Add((b, b + 2, b + 3)); }
                else { s.F.Add((b, b + 2, b + 1)); s.F.Add((b, b + 3, b + 2)); }
            }
        }

        Quad(plate, -hw, 0f, hw, BoardHeight, 0f, new Vector3(0, 0, -1), LevelUv(4));
        foreach (var c in Cells(toSun))
        {
            if (c.Empty) continue;
            float cx = -hw + Border + (c.Column + 0.5f) * Pitch;
            float cy = BoardHeight - Border - (c.Row + 0.5f) * Pitch;
            float h = PatchSize * 0.5f;
            Quad(c.HalfMaterial ? half : full, cx - h, cy - h, cx + h, cy + h, -0.02f, c.Normal, LevelUv(c.Level));
        }

        var sections = new[]
        {
            new StandardMeshWriter.LitSection(meshName + "_Material0", full.P, full.N, full.Uv, full.Lm, full.F),
            new StandardMeshWriter.LitSection(meshName + "_Material1", half.P, half.N, half.Uv, half.Lm, half.F),
            new StandardMeshWriter.LitSection(meshName + "_Material2", plate.P, plate.N, plate.Uv, plate.Lm, plate.F),
        };
        var bounds = new[] { -hw, 0f, -0.05f, hw, BoardHeight, 0.05f };
        return StandardMeshWriter.WriteLit(sections, layout, withUnwrap && layout != StandardMeshWriter.LitLayout.Plain32, bounds);

        static (List<Vec3> P, List<Vec3> N, List<(float, float)> Uv, List<(float, float)> Lm, List<(int, int, int)> F) NewSection()
            => (new(), new(), new(), new(), new());
    }

    /// <summary>The mesh's shader file: white at full and at half diffuse, and the black plate. A normal map is
    /// named only for the tangent-frame layout, and it is flat, so the light is decided by the vertex normal.</summary>
    public static string ShaderText(string meshName, bool normalMap)
    {
        var sb = new StringBuilder();
        void Sub(int i, string diffuse, string texture)
        {
            sb.Append($"subshader \"{meshName}_Material{i}\" \"StandardMesh/Default\"\r\n{{\r\n");
            sb.Append("\tlighting true;\r\n\tlightingSpecular false;\r\n");
            sb.Append($"\tmaterialDiffuse {diffuse};\r\n");
            sb.Append($"\ttexture \"texture/{texture}\";\r\n");
            if (normalMap) sb.Append($"\tnormalmap \"texture/{FlatNormalTexture}\";\r\n");
            sb.Append("}\r\n\r\n");
        }
        Sub(0, "1 1 1", WhiteTexture);
        Sub(1, "0.5 0.5 0.5", WhiteTexture);
        Sub(2, "1 1 1", BlackTexture);
        return sb.ToString();
    }

    public const string WhiteTexture = "rfcal_white", BlackTexture = "rfcal_black", FlatNormalTexture = "rfcal_flat_n";

    private static byte[] FlatTexture(byte r, byte g, byte b, byte a)
    {
        const int side = 16;
        var px = new byte[side * side * 4];
        for (int i = 0; i < side * side; i++) { px[i * 4] = r; px[i * 4 + 1] = g; px[i * 4 + 2] = b; px[i * 4 + 3] = a; }
        // Uncompressed, with a mip chain: exact colours (a DXT block would round 128 to 132), in the layout the
        // retail first-person arm normal maps already ship in.
        return DdsTexture.EncodeUncompressedMipped(new Texture2D(side, side, px));
    }

    // ---------------------------------------------------------------------------------------------------------
    // The level
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Where the boards stand: they face south, in a row centred on <paramref name="CentreX"/> at
    /// <paramref name="BoardsZ"/>; the players spawn at <paramref name="ViewerZ"/>, south of them, facing north.</summary>
    public sealed record Site(int CentreX, int BoardsZ, int ViewerZ)
    {
        /// <summary>The widest flat dry ground in retail Hue: the street grid west of the palace.</summary>
        public static readonly Site Hue = new(376, 536, 512);
        /// <summary>The same ground, ten metres further back, for the six-column second level.</summary>
        public static readonly Site HueUnlit = new(376, 536, 502);
    }

    /// <param name="BaseRfa">The retail level to copy (its numbered patches beside it are merged in).</param>
    /// <param name="Name">The new level's name.</param>
    /// <param name="OutputDir">Where <c>&lt;Name&gt;.rfa</c> is written. An existing file of that name is refused.</param>
    /// <param name="Full">Also write the tangent-frame boards and the 24-bit coloured lightmap. Both are formats this
    /// editor has never shipped to the game; without them the level uses only forms that are known to load.</param>
    public sealed record Options(string BaseRfa, string Name, string OutputDir, bool Full, Site Site)
    {
        public Vector3 Sun { get; init; } = new(1.25f, 0f, 0f);
        public Vector3 Back { get; init; } = new(0f, 1f, 0f);
        public Vector3 Ambient { get; init; } = new(0f, 0f, 0.5f);
        public Vector3 LmAmbient { get; init; } = new(0f, 0f, 0.25f);
        public string BaseSub { get; init; } = "BfVietnam";
        /// <summary>Build the SECOND level instead: what the terrain shadow map does to an object with no lightmap.
        /// Six columns - four such boards over ground painted black, dark grey, mid grey and white, a mesh with a
        /// stray lightmap file, and a lightmapped control - in two rows: the front row stands where the shadow
        /// map's flag is SET (in shadow), the back row where it is clear. <c>Terrain.ShadowAmbient</c> is given a
        /// different value in each channel. (The first level's shadow map was written with the flag set everywhere
        /// - by mistake, under a codec whose names said "lit" - which is why its unlightmapped boards came out
        /// dim; from this build on it is clear everywhere.)</summary>
        public bool Unlit { get; init; }
        public Vector3 ShadowAmbient { get; init; } = new(60f, 120f, 200f);
    }

    public sealed record Board(string Id, string Mesh, string Layout, string Lightmap, bool Unwrapped, int X, int Y, int Z, string What)
    {
        /// <summary>The terrain tile colour painted under the board (0..1), where the level paints one.</summary>
        public float[]? Ground { get; init; }
        /// <summary>What the level's terrain shadow map holds under the board: "flag set" (in terrain shadow) or
        /// "flag clear" (not).</summary>
        public string? ShadowMap { get; init; }
    }

    public sealed record Swatch(float[] Tile, float[] Expected);

    /// <summary>Everything a reading needs to know about the level it is reading. Saved beside the archive.</summary>
    public sealed record Manifest
    {
        public string Level { get; init; } = "";
        public string BaseLevel { get; init; } = "";
        public float[] ToSun { get; init; } = Array.Empty<float>();
        public float[] Sun { get; init; } = Array.Empty<float>();
        public float[] Back { get; init; } = Array.Empty<float>();
        public float[] Ambient { get; init; } = Array.Empty<float>();
        public float[] LmAmbient { get; init; } = Array.Empty<float>();
        public float[] Detail { get; init; } = Array.Empty<float>();
        /// <summary>The five lightmap levels as the game holds them when they come from an 8-bit map: looked up in
        /// the level's own Palette.pal and kept in a 5:6:5 texture (<see cref="LightmapPalette"/>). Fifteen numbers,
        /// r g b per level. Empty in a manifest written before that was known.</summary>
        public float[] LevelsShown { get; init; } = Array.Empty<float>();
        public List<Board> Boards { get; init; } = new();
        public List<Swatch> Swatches { get; init; } = new();
        public float[] Viewer { get; init; } = Array.Empty<float>();
        public int CentreX { get; init; }
        public int BoardsZ { get; init; }
        public int ViewerZ { get; init; }
        public List<string> Notes { get; init; } = new();

        public Site SiteOf => new(CentreX, BoardsZ, ViewerZ);

        public Vector3 ToSunVector => new(ToSun[0], ToSun[1], ToSun[2]);
        public EngineLight.Colours Colours => new(V(Sun), V(Back), V(Ambient), V(LmAmbient));
        private static Vector3 V(float[] a) => new(a[0], a[1], a[2]);
    }

    /// <summary>The ground swatches' tile colours, west to east.</summary>
    public static readonly (byte R, byte G, byte B)[] GroundSwatches =
    {
        (64, 64, 64), (128, 128, 128), (191, 191, 191), (255, 255, 255), (200, 100, 50),
    };

    /// <summary>
    /// Build the level. Nothing outside <see cref="Options.OutputDir"/> is touched; the base level is only read.
    /// </summary>
    public static Manifest BuildLevel(Options o)
    {
        var copy = LevelCopy.CopyArchive(o.BaseRfa, o.Name, o.OutputDir);
        // A site the layout cannot use is only found out once the copy can be read; do not leave the copy behind.
        try { return BuildInto(o, copy); }
        catch { try { File.Delete(copy.Destination); } catch { } throw; }
    }

    private static Manifest BuildInto(Options o, LevelCopy.Result copy)
    {
        var arc = new RefractorFlatArchive(copy.Destination);
        string prefix = LevelSaver.ArchivePrefix(arc);
        var repl = new Dictionary<string, byte[]>(RefractorFlatArchive.NameComparer);
        var notes = new List<string>(copy.Notes);
        var latin = Encoding.Latin1;

        byte[]? Read(string rel) => arc.TryGetEntry(prefix + rel, out var e) ? arc.Read(e) : null;
        byte[] Need(string rel) => Read(rel) ?? throw new InvalidDataException($"{Path.GetFileName(o.BaseRfa)} has no {rel}.");
        string Text(string rel) => latin.GetString(Need(rel));
        string[] Lines(string rel) => Text(rel).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        void Put(string rel, byte[] bytes) => repl[prefix + rel] = bytes;
        void PutText(string rel, IEnumerable<string> lines) => Put(rel, latin.GetBytes(string.Join("\r\n", lines) + "\r\n"));

        // ---- what the level is ----
        var cfg = TerrainConfig.Parse(Lines("Init/Terrain.con"));
        var hm = Heightmap.FromBytes(Need("Heightmap.raw"), cfg.MaterialSize, cfg.MaterialSize);
        var env = EnvironmentSettings.Parse(Lines("Init/SkyAndSun.con"), Lines("Init/Terrain.con"), Lines("Init.con"));
        var toSun = Vector3.Normalize(new Vector3(env.SunDirection.X, env.SunDirection.Y, env.SunDirection.Z));
        float spacing = cfg.HorizontalSpacing;
        float Ground(float wx, float wz)
        {
            float fx = Math.Clamp(wx / spacing, 0, hm.Width - 1.001f), fz = Math.Clamp(wz / spacing, 0, hm.Height - 1.001f);
            int ix = (int)fx, iz = (int)fz; float tx = fx - ix, tz = fz - iz;
            float h00 = cfg.HeightToMeters(hm[ix, iz]), h10 = cfg.HeightToMeters(hm[ix + 1, iz]);
            float h01 = cfg.HeightToMeters(hm[ix, iz + 1]), h11 = cfg.HeightToMeters(hm[ix + 1, iz + 1]);
            return (h00 * (1 - tx) + h10 * tx) * (1 - tz) + (h01 * (1 - tx) + h11 * tx) * tz;
        }

        // ---- the site must be what the layout assumes: dry, in bounds and near enough level ----
        var site = o.Site;
        const int boardPitch = 8;
        int[] boardX = o.Unlit
            ? new[] { site.CentreX - 20, site.CentreX - 12, site.CentreX - 4, site.CentreX + 4, site.CentreX + 12, site.CentreX + 20 }
            : new[] { site.CentreX - 12, site.CentreX - 4, site.CentreX + 4, site.CentreX + 12 };
        int backZ = site.BoardsZ + (o.Unlit ? 5 : 0);         // the second level's upper row stands five metres behind
        int north = o.Unlit ? 13 : 6;                         // how far past the boards the level reaches
        if (boardX[0] - 8 < 0 || boardX[^1] + 8 > cfg.WorldSize || site.ViewerZ - 12 < 0 || site.BoardsZ + north > cfg.WorldSize || site.ViewerZ >= site.BoardsZ)
            throw new InvalidDataException($"The site does not fit the map ({cfg.WorldSize} m square), or its boards are not north of its viewer.");
        float lo = float.MaxValue, hi = float.MinValue;
        var matMap = Read("MaterialMap.raw");
        for (float z = site.ViewerZ - 8; z <= site.BoardsZ + north - 2; z += 1f)
            for (float x = boardX[0] - 5; x <= boardX[^1] + 5; x += 1f)
            {
                float g = Ground(x, z); lo = Math.Min(lo, g); hi = Math.Max(hi, g);
                if (matMap is not null && matMap.Length == cfg.MaterialSize * cfg.MaterialSize
                    && matMap[(int)(z / spacing) * cfg.MaterialSize + (int)(x / spacing)] == DeathMaterial.Index)
                    throw new InvalidDataException($"The site crosses the level's out-of-bounds material at {x:0}, {z:0}.");
            }
        if (lo < cfg.WaterLevel + 0.5f) throw new InvalidDataException($"The site is under water ({lo:0.0} m, water at {cfg.WaterLevel:0.0}).");
        if (hi - lo > 2.5f) throw new InvalidDataException($"The site is not level: {lo:0.0} to {hi:0.0} m.");
        int bottomY = (int)MathF.Ceiling(hi + 1.2f);          // whole metres: a lightmap is keyed by the truncated position
        // The second level's upper row stands further back, where a row looks lower: lift it clear of the front row.
        int topY = bottomY + (o.Unlit ? 5 : 4);

        // ---- the boards ----
        var meshes = new List<(string Name, StandardMeshWriter.LitLayout Layout, bool Unwrap, bool NormalMap)>
        {
            ("rfcal_pv_m1", StandardMeshWriter.LitLayout.Lightmap40, true, false),
            ("rfcal_p32_m1", StandardMeshWriter.LitLayout.Plain32, false, false),
        };
        var boards = new List<Board>
        {
            new("A", "rfcal_pv_m1", "40-byte, no tangent frame", nameof(LightmapKind.Grey), true, boardX[0], bottomY, site.BoardsZ, "lightmapped, the fixed-function path"),
            new("B", "rfcal_pv_m1", "40-byte, no tangent frame", nameof(LightmapKind.None), true, boardX[1], bottomY, site.BoardsZ, "the same mesh with no lightmap file"),
            new("C", "rfcal_pv_m1", "40-byte, no tangent frame", o.Full ? nameof(LightmapKind.Rgb24) : nameof(LightmapKind.ColourPalette), true, boardX[2], bottomY, site.BoardsZ, "a coloured lightmap"),
            new("D", "rfcal_p32_m1", "32-byte, no lightmap channel", nameof(LightmapKind.Uniform), false, boardX[3], bottomY, site.BoardsZ, "no unwrap, one flat lightmap value (128)"),
        };
        // The second level: twelve boards, the same six in a row twice. Columns: four boards with no lightmap over
        // ground painted black, dark grey, mid grey and white; the mesh with no unwrap and a stray lightmap file,
        // and a lightmapped board as the control, both over mid grey. Front row: shadow-map flag set (in terrain
        // shadow). Back row: clear.
        byte[] groundLevels = { 0, 64, 128, 255, 128, 128 };
        if (o.Unlit)
        {
            boards.Clear();
            for (int row = 0; row < 2; row++)
                for (int c = 0; c < boardX.Length; c++)
                {
                    string id = ((char)('A' + row * boardX.Length + c)).ToString();
                    float g = groundLevels[c] / 255f;
                    string flag = row == 0 ? "flag set (in shadow)" : "flag clear (sunlit)";
                    int y = row == 0 ? bottomY : topY, z = row == 0 ? site.BoardsZ : backZ;
                    boards.Add(c switch
                    {
                        4 => new Board(id, "rfcal_p32_m1", "32-byte, no lightmap channel", nameof(LightmapKind.Uniform), false, boardX[c], y, z,
                                       $"no unwrap + a flat lightmap file; ground {groundLevels[c]}; shadow map {flag}") { Ground = new[] { g, g, g }, ShadowMap = flag },
                        5 => new Board(id, "rfcal_pv_m1", "40-byte, no tangent frame", nameof(LightmapKind.Grey), true, boardX[c], y, z,
                                       $"lightmapped (the control); ground {groundLevels[c]}; shadow map {flag}") { Ground = new[] { g, g, g }, ShadowMap = flag },
                        _ => new Board(id, "rfcal_pv_m1", "40-byte, no tangent frame", nameof(LightmapKind.None), true, boardX[c], y, z,
                                       $"no lightmap; ground {groundLevels[c]}; shadow map {flag}") { Ground = new[] { g, g, g }, ShadowMap = flag },
                    });
                }
        }
        else if (o.Full)
        {
            meshes.Add(("rfcal_pp_m1", StandardMeshWriter.LitLayout.Tangent64, true, true));
            meshes.Add(("rfcal_ppq0_m1", StandardMeshWriter.LitLayout.Tangent64, false, true));
            boards.Add(new("E", "rfcal_pp_m1", "64-byte, tangent frame", nameof(LightmapKind.Grey), true, boardX[0], topY, site.BoardsZ, "lightmapped, the per-pixel path"));
            boards.Add(new("F", "rfcal_pp_m1", "64-byte, tangent frame", nameof(LightmapKind.None), true, boardX[1], topY, site.BoardsZ, "the same mesh with no lightmap file"));
            boards.Add(new("G", "rfcal_pp_m1", "64-byte, tangent frame", nameof(LightmapKind.Rgb24), true, boardX[2], topY, site.BoardsZ, "a coloured lightmap"));
            boards.Add(new("H", "rfcal_ppq0_m1", "64-byte, empty lightmap channel", nameof(LightmapKind.Uniform), false, boardX[3], topY, site.BoardsZ, "no unwrap, one flat lightmap value (128)"));
        }

        var objectsCon = new StringBuilder();
        foreach (var (name, layout, unwrap, normalMap) in meshes)
        {
            Put($"StandardMesh/{name}.sm", BuildMesh(name, toSun, layout, unwrap));
            Put($"StandardMesh/{name}.rs", latin.GetBytes(ShaderText(name, normalMap)));
            var geom = new StringBuilder()
                .Append($"GeometryTemplate.create StandardMesh {name}\r\n")
                .Append($"GeometryTemplate.file ../{o.BaseSub}/levels/{o.Name}/StandardMesh/{name}\r\n");
            float[] ramp = { 0f, 50f, 100f, 200f, 400f, 800f };
            for (int i = 0; i < ramp.Length; i++) geom.Append($"GeometryTemplate.setLodDistance {i} {ramp[i].ToString("0.###", CultureInfo.InvariantCulture)}\r\n");
            geom.Append("\r\n");
            Put($"Objects/{name}/Geometries.con", latin.GetBytes(geom.ToString()));
            Put($"Objects/{name}/Objects.con", latin.GetBytes(
                $"ObjectTemplate.create SimpleObject {name}\r\nObjectTemplate.geometry {name}\r\nObjectTemplate.HasCollisionPhysics 0\r\n\r\n"));
            Put($"Objects/{name}/{name}.con", latin.GetBytes("run Objects\r\nrun Geometries\r\n"));
            objectsCon.Append($"run {name}/{name}\r\n");
        }
        if (Read("Objects/objects.con") is not null)
            throw new InvalidDataException("The base level already has its own Objects/objects.con - pick a base without level-local objects.");
        Put("Objects/objects.con", latin.GetBytes(objectsCon.ToString()));
        Put($"Texture/{WhiteTexture}.dds", FlatTexture(255, 255, 255, 255));
        Put($"Texture/{BlackTexture}.dds", FlatTexture(0, 0, 0, 255));
        if (o.Full && !o.Unlit) Put($"Texture/{FlatNormalTexture}.dds", FlatTexture(128, 128, 255, 0));   // alpha is the gloss: none

        // The lightmap folder keeps the spelling the level already uses.
        string lmFolder = arc.Entries.Select(e => e.Name.Replace('\\', '/'))
            .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(n => n[prefix.Length..].Split('/')[0])
            .FirstOrDefault(f => f.Equals("ObjectLightmaps", StringComparison.OrdinalIgnoreCase)) ?? "ObjectLightmaps";
        var levelPalette = LightmapPalette.Parse(Read($"{lmFolder}/Palette.pal")) ?? LightmapPalette.Linear;
        var levelsShown = Levels.SelectMany(v => { var (r, g, b) = levelPalette.Shown(v); return new[] { r / 255f, g / 255f, b / 255f }; }).ToArray();
        var so = new StaticObjectsFile();
        foreach (var b in boards)
        {
            so.Objects.Add(new StaticObject(b.Mesh) { Position = new Vec3(b.X, b.Y, b.Z), Rotation = new Vec3(0, 0, 0) });
            if (EncodeLightmap(Enum.Parse<LightmapKind>(b.Lightmap)) is { } tga)
                Put($"{lmFolder}/{ObjectLightmaps.FileBase(b.Mesh)}_{b.X}-{b.Y}-{b.Z}.tga", tga);
        }
        Put("StaticObjects.con", LevelSaver.SerializeStaticObjects(so));
        notes.Add("Every retail static object was taken out so nothing stands between the spawn and the boards.");

        // ---- the light: one channel per term, fog pushed past the boards ----
        var init = Lines("Init.con").ToList();
        SetCon(init, "renderer.diffuseColor", Triple(o.Sun));
        SetCon(init, "renderer.ambientColor", Triple(o.Ambient));
        SetCon(init, "renderer.SecondaryDiffuseColor", Triple(o.Back));
        SetCon(init, "renderer.LMambientColor", Triple(o.LmAmbient));
        float view = env.ViewDistance > 60f ? env.ViewDistance : 300f;
        SetCon(init, "renderer.fogstart", (view - 15f).ToString("0.###", CultureInfo.InvariantCulture));
        SetCon(init, "renderer.fogend", view.ToString("0.###", CultureInfo.InvariantCulture));
        var initText = DecalObject.PatchInitCon(string.Join("\r\n", init), o.Name, o.BaseSub);
        Put("Init.con", latin.GetBytes(initText));

        if (o.Unlit)
        {
            var terrain = Lines("Init/Terrain.con").ToList();
            SetCon(terrain, "Terrain.ShadowAmbient", $"{F(o.ShadowAmbient.X)}/{F(o.ShadowAmbient.Y)}/{F(o.ShadowAmbient.Z)}");
            PutText("Init/Terrain.con", terrain);
        }

        // ---- both teams spawn in front of the boards, facing them ----
        float[] viewer = { site.CentreX, Ground(site.CentreX, site.ViewerZ), site.ViewerZ };
        foreach (var mode in new[] { "Conquest" })
        {
            if (Read($"{mode}/SoldierSpawns.con") is null) continue;
            var groups = MainBaseGroups(Lines($"{mode}/ControlPointTemplates.con"));
            var names = SpawnNames(Lines($"{mode}/SoldierSpawnTemplates.con"));
            var spawns = Lines($"{mode}/SoldierSpawns.con").ToList();
            int moved = 0;
            foreach (var (team, group) in groups)
            {
                var mine = names.Where(kv => kv.Value == group).Select(kv => kv.Key).ToList();
                for (int i = 0; i < mine.Count; i++)
                {
                    float x = site.CentreX + (i - (mine.Count - 1) * 0.5f) * 2.5f;
                    float z = site.ViewerZ - (team == 1 ? 3f : 0f);
                    string pos = $"{F(x)}/{F(Ground(x, z) + 0.1f)}/{F(z)}";
                    if (MoveObject(spawns, mine[i], pos, "0/0/0")) moved++;
                }
            }
            PutText($"{mode}/SoldierSpawns.con", spawns);
            notes.Add($"{mode}: moved {moved} main-base spawn points to the viewing line.");
        }

        // ---- ground: swatches on a black mat, a flat detail texture, no grass, nothing in terrain shadow ----
        var swatches = new List<Swatch>();
        var detail = DdsTexture.Decode(DxtEncoder.EncodeDxt1Mipped(Flat(512, 128, 128, 128)));
        var detailRgb = new Vector3(detail.Rgba[0], detail.Rgba[1], detail.Rgba[2]) / 255f;
        string? detailEntry = arc.Entries.Select(e => e.Name).FirstOrDefault(n =>
            n.Replace('\\', '/').Equals(prefix + "Textures/detail.dds", StringComparison.OrdinalIgnoreCase));
        if (detailEntry is not null) repl[detailEntry] = DxtEncoder.EncodeDxt1Mipped(Flat(512, 128, 128, 128));
        else notes.Add("No Textures/detail.dds to replace: the ground swatches are multiplied by whatever detail the level has.");

        var tiles = arc.Entries
            .Where(e => { var n = e.Name.Replace('\\', '/'); return n.StartsWith(prefix + "Textures/tx", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) && n.IndexOf('/', (prefix + "Textures/").Length) < 0; })
            .Select(e => (e.Name, arc.Read(e))).ToList();
        var tt = TerrainTexture.FromTileBytes(tiles, cfg.WorldSize);
        if (tt is not null)
        {
            const int atlasSide = 4096;
            var atlas = tt.BakeAtlas(atlasSide);
            float pxPerM = atlasSide / (float)cfg.WorldSize;
            var (mx0, mx1, mz0, mz1) = GroundMat(site);
            void Fill(float x0, float z0, float x1, float z1, byte r, byte g, byte b)
            {
                int ax0 = (int)MathF.Round(x0 * pxPerM), ax1 = (int)MathF.Round(x1 * pxPerM);
                int ay0 = (int)MathF.Round(z0 * pxPerM), ay1 = (int)MathF.Round(z1 * pxPerM);
                for (int y = ay0; y < ay1; y++)
                    for (int x = ax0; x < ax1; x++)
                    {
                        int i = (y * atlasSide + x) * 4;
                        atlas.Rgba[i] = r; atlas.Rgba[i + 1] = g; atlas.Rgba[i + 2] = b; atlas.Rgba[i + 3] = 255;
                    }
            }
            if (o.Unlit)
            {
                // A flat colour under each column of boards, wide and deep enough that whatever point or patch of
                // ground the engine looks at under a board is all one colour.
                for (int c = 0; c < boardX.Length; c++)
                    Fill(boardX[c] - 3.8f, site.BoardsZ - 6f, boardX[c] + 3.8f, backZ + 6f, groundLevels[c], groundLevels[c], groundLevels[c]);
                (mx0, mx1, mz0, mz1) = (boardX[0] - 3.8f, boardX[^1] + 3.8f, site.BoardsZ - 6f, backZ + 6f);
            }
            else Fill(mx0, mz0, mx1, mz1, 0, 0, 0);
            for (int i = 0; i < (o.Unlit ? 0 : GroundSwatches.Length); i++)
            {
                var (sx0, sx1, sz0, sz1) = GroundSwatchRect(site, i);
                var c = GroundSwatches[i];
                Fill(sx0, sz0, sx1, sz1, c.R, c.G, c.B);
                var tile = new Vector3(c.R, c.G, c.B) / 255f;
                var want = Vector3.Min(Vector3.One, tile * detailRgb * 2f);
                swatches.Add(new Swatch(new[] { tile.X, tile.Y, tile.Z }, new[] { want.X, want.Y, want.Z }));
            }
            int rx = (int)(mx0 * pxPerM), ry = (int)(mz0 * pxPerM), rw = (int)((mx1 - mx0) * pxPerM) + 1, rh = (int)((mz1 - mz0) * pxPerM) + 1;
            var touched = tt.TilesIn(rx, ry, rw, rh, atlasSide, atlasSide).ToHashSet();
            foreach (var (fileName, tile) in tt.SplitToTiles(atlas, (c, r) => touched.Contains((c, r))))
                repl[fileName] = DxtEncoder.EncodeDxt1Mipped(tile);
            notes.Add(o.Unlit ? $"Painted a flat colour under each column of boards ({touched.Count} terrain tile(s))." : $"Painted the ground chart into {touched.Count} terrain tile(s).");
        }
        else notes.Add("The level's terrain tiles could not be read: no ground chart.");

        // Grass would stand in front of the ground chart. The growth map's index is a terrain material id, so
        // give the site an index the palette grows nothing on.
        if (Read("growth/UnderGrowthMap.raw") is { } under && Read("growth/underGrowth.wst") is { } wst)
        {
            int side = (int)Math.Round(Math.Sqrt(under.Length));
            if (side * side == under.Length)
            {
                var palette = FoliagePalette.Parse(latin.GetString(wst).TrimStart());
                int bare = -1;
                foreach (int cand in new[] { 15, 7, 0, 14, 13, 12 })
                    if ((palette.SlotForIndex(cand)?.Types.Count ?? 0) == 0) { bare = cand; break; }
                if (bare >= 0)
                {
                    var copyMap = (byte[])under.Clone();
                    float perM = side / (float)cfg.WorldSize;
                    for (int z = (int)((site.ViewerZ - 12) * perM); z <= (int)((site.BoardsZ + north) * perM); z++)
                        for (int x = (int)((boardX[0] - 8) * perM); x <= (int)((boardX[^1] + 8) * perM); x++)
                            if (x >= 0 && z >= 0 && x < side && z < side) copyMap[z * side + x] = (byte)bare;
                    Put("growth/UnderGrowthMap.raw", copyMap);
                    notes.Add($"Undergrowth cleared round the site (index {bare}).");
                }
                else notes.Add("Every undergrowth index grows something here: the grass was left alone.");
            }
        }

        // A board with no lightmap is lit as a moving object is, and those are dimmed where the terrain shadow
        // map's flag is set. The first level wants that out of the measurement: nothing in shadow. The second level
        // is about it: in shadow everywhere except under the back row.
        string? lsbEntry = arc.Entries.Select(e => e.Name).FirstOrDefault(n => n.Replace('\\', '/').EndsWith("/LightmapShadowBits.lsb", StringComparison.OrdinalIgnoreCase));
        if (lsbEntry is not null)
        {
            var lsb = LightmapShadowBits.Decode(arc.Read(arc.Entries.First(e => e.Name == lsbEntry)));
            var mask = lsb.ToShadowMask(out int side);               // 255 = in shadow
            if (o.Unlit)
            {
                Array.Fill(mask, (byte)255);
                // Clear under the back row. The rectangle is cleared in all eight ways a square can be turned and
                // mirrored, so the test did not depend on the raster's orientation (since settled: row 0 south,
                // column 0 west). The front row is in none of the eight copies.
                float perM = side / (float)cfg.WorldSize;
                int x0 = (int)((boardX[0] - 8) * perM), x1 = (int)((boardX[^1] + 8) * perM);
                int z0 = (int)((site.BoardsZ + 2.5f) * perM), z1 = (int)((backZ + 7) * perM);
                for (int z = z0; z <= z1; z++)
                    for (int x = x0; x <= x1; x++)
                        foreach (var (px, pz) in new[] { (x, z), (side - 1 - x, z), (x, side - 1 - z), (side - 1 - x, side - 1 - z),
                                                         (z, x), (side - 1 - z, x), (z, side - 1 - x), (side - 1 - z, side - 1 - x) })
                            if (px >= 0 && pz >= 0 && px < side && pz < side) mask[pz * side + px] = 0;
                notes.Add("Terrain shadow map: in shadow everywhere except under the back row.");
            }
            else
            {
                Array.Clear(mask);
                notes.Add("Terrain shadow map: nothing in shadow.");
            }
            repl[lsbEntry] = LightmapShadowBits.FromShadowMask(mask, side, lsb.GridDim, lsb.TilePixels).Encode();
        }

        RefractorFlatArchive.RepackToFile(copy.Destination, arc, repl, (Func<string, bool>?)null);

        return new Manifest
        {
            Level = o.Name,
            BaseLevel = Path.GetFileNameWithoutExtension(o.BaseRfa),
            ToSun = new[] { toSun.X, toSun.Y, toSun.Z },
            Sun = A(o.Sun), Back = A(o.Back), Ambient = A(o.Ambient), LmAmbient = A(o.LmAmbient),
            Detail = A(detailRgb),
            LevelsShown = levelsShown,
            Boards = boards,
            Swatches = swatches,
            Viewer = viewer,
            CentreX = site.CentreX, BoardsZ = site.BoardsZ, ViewerZ = site.ViewerZ,
            Notes = notes,
        };

        static float[] A(Vector3 v) => new[] { v.X, v.Y, v.Z };
        static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        static string Triple(Vector3 v) => $"{F(v.X)}/{F(v.Y)}/{F(v.Z)}";
        static Texture2D Flat(int side, byte r, byte g, byte b)
        {
            var px = new byte[side * side * 4];
            for (int i = 0; i < side * side; i++) { px[i * 4] = r; px[i * 4 + 1] = g; px[i * 4 + 2] = b; px[i * 4 + 3] = 255; }
            return new Texture2D(side, side, px);
        }
    }

    /// <summary>The black mat the ground swatches lie on: west, east, south, north, in metres.</summary>
    public static (float X0, float X1, float Z0, float Z1) GroundMat(Site s) => (s.CentreX - GroundMatWidth * 0.5f, s.CentreX + GroundMatWidth * 0.5f, s.ViewerZ + 10f, s.ViewerZ + 21f);

    // Five swatches 1.8 m wide, half a metre apart and half a metre in from the mat's ends.
    private const float GroundMatWidth = 12f, SwatchWidth = 1.8f, SwatchStep = 2.3f, SwatchInset = 0.5f;

    public static (float X0, float X1, float Z0, float Z1) GroundSwatchRect(Site s, int i)
    {
        var (x0, _, z0, z1) = GroundMat(s);
        float sx = x0 + SwatchInset + i * SwatchStep;
        return (sx, sx + SwatchWidth, z0 + 1.5f, z1 - 1.5f);
    }

    /// <summary>A ground swatch's centre on the mat, as fractions from its north-west corner (the far left one in
    /// a screenshot taken from the spawn).</summary>
    public static Vector2 GroundSwatchCentre(int i) => new((SwatchInset + i * SwatchStep + SwatchWidth * 0.5f) / GroundMatWidth, 0.5f);

    /// <summary>The middle of the black strip between swatch <paramref name="i"/> and the next (0..3), the same way:
    /// a reading checks these are black to tell the mat from any other dark shape on the ground.</summary>
    public static Vector2 GroundGapCentre(int i) => new((SwatchInset + i * SwatchStep + (SwatchWidth + SwatchStep) * 0.5f) / GroundMatWidth, 0.5f);

    // Set "key value" in a .con, in place where the key is already there, else after the last renderer line.
    private static void SetCon(List<string> lines, string key, string value)
    {
        int last = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            var t = lines[i].TrimStart();
            if (t.StartsWith("renderer.", StringComparison.OrdinalIgnoreCase)) last = i;
            if (t.Length > key.Length && t.StartsWith(key, StringComparison.OrdinalIgnoreCase) && char.IsWhiteSpace(t[key.Length]))
            {
                lines[i] = $"{key} {value}";
                return;
            }
        }
        lines.Insert(last + 1, $"{key} {value}");
    }

    // team -> the spawn group of the first control point that team owns from the start.
    private static List<(int Team, int Group)> MainBaseGroups(string[] lines)
    {
        var found = new List<(int, int)>();
        int team = -1, group = -1;
        void Flush()
        {
            if (team is 1 or 2 && group >= 0 && found.All(f => f.Item1 != team)) found.Add((team, group));
            team = -1; group = -1;
        }
        foreach (var raw in lines)
        {
            var w = raw.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (w.Length < 2) continue;
            if (w[0].Equals("ObjectTemplate.create", StringComparison.OrdinalIgnoreCase)) Flush();
            else if (w[0].Equals("ObjectTemplate.team", StringComparison.OrdinalIgnoreCase) && int.TryParse(w[1], out int t)) team = t;
            else if (w[0].Equals("ObjectTemplate.spawnGroupId", StringComparison.OrdinalIgnoreCase) && int.TryParse(w[1], out int g)) group = g;
        }
        Flush();
        return found;
    }

    private static Dictionary<string, int> SpawnNames(string[] lines)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string? cur = null;
        foreach (var raw in lines)
        {
            var w = raw.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (w.Length < 2) continue;
            if (w[0].Equals("ObjectTemplate.create", StringComparison.OrdinalIgnoreCase)) cur = w.Length >= 3 ? w[2] : null;
            else if (cur is not null && w[0].Equals("ObjectTemplate.setGroup", StringComparison.OrdinalIgnoreCase) && int.TryParse(w[1], out int g)) map[cur] = g;
        }
        return map;
    }

    // Re-place one "Object.create <name>" block: its position and rotation lines are rewritten (or added).
    private static bool MoveObject(List<string> lines, string name, string position, string rotation)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            var w = lines[i].Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (w.Length < 2 || !w[0].Equals("Object.create", StringComparison.OrdinalIgnoreCase) || !w[1].Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            int posAt = -1; bool rot = false;
            for (int end = i + 1; end < lines.Count; end++)
            {
                var t = lines[end].TrimStart();
                if (t.StartsWith("Object.create", StringComparison.OrdinalIgnoreCase)) break;
                if (t.StartsWith("Object.absolutePosition", StringComparison.OrdinalIgnoreCase)) { lines[end] = $"Object.absolutePosition {position}"; posAt = end; }
                else if (t.StartsWith("Object.rotation", StringComparison.OrdinalIgnoreCase)) { lines[end] = $"Object.rotation {rotation}"; rot = true; }
            }
            if (posAt < 0) { lines.Insert(i + 1, $"Object.absolutePosition {position}"); posAt = i + 1; }
            if (!rot) lines.Insert(posAt + 1, $"Object.rotation {rotation}");
            return true;
        }
        return false;
    }

    // ---------------------------------------------------------------------------------------------------------
    // What the editor expects to see
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The colour the editor's model of the engine (<see cref="EngineLight"/>) gives one patch of one board. This
    /// is the belief the level exists to test: one formula for every lit mesh, the lightmap's blue channel as the
    /// sun's visibility, the lightmapped ambient where there is a lightmap and the plain one where there is not.
    /// </summary>
    public static Vector3 Predict(Manifest m, Board board, Cell cell)
    {
        if (cell.Empty) return Vector3.Zero;
        var kind = Enum.Parse<LightmapKind>(board.Lightmap);
        float d = cell.HalfMaterial ? 0.5f : 1f;
        // The mesh's layout picks the shader family: only the tangent-frame boards are lit per pixel.
        var mat = new EngineLight.Material(new Vector3(d), Vector3.One, Vector3.Zero) { VertexLit = !board.Mesh.Contains("_pp", StringComparison.OrdinalIgnoreCase) };
        var c = m.Colours;
        // No file, or a file on a mesh with no unwrap (which the game ignores): lit as an object without a lightmap.
        if (kind == LightmapKind.None || !board.Unwrapped) return EngineLight.Dynamic(cell.Normal, m.ToSunVector, c, mat);
        // A 24-bit map is read as written; an 8-bit one through the level's palette, whatever colour map it carries.
        var lm = kind == LightmapKind.Rgb24 ? LightmapRgb(kind, cell.Level)
               : m.LevelsShown.Length >= 15 ? new Vector3(m.LevelsShown[cell.Level * 3], m.LevelsShown[cell.Level * 3 + 1], m.LevelsShown[cell.Level * 3 + 2])
               : LightmapRgb(LightmapKind.Grey, cell.Level);
        return EngineLight.Lightmapped(lm, cell.Normal, m.ToSunVector, c, mat);
    }

    /// <summary>
    /// Draw the level as a player standing at the spawn would see it, with each patch in the colour
    /// <paramref name="colour"/> gives it (default: <see cref="Predict"/>). A plain pinhole picture of the plates
    /// and the ground chart - there to be compared with the real screenshot, and to test the reader against.
    /// </summary>
    public static byte[] RenderExpected(Manifest m, int width, int height, float fovYDeg = 55f, float pitchUpDeg = 6f,
                                        Func<Board, Cell, Vector3>? colour = null, float eyeHeight = 1.7f, float back = 0f)
    {
        colour ??= (b, c) => Predict(m, b, c);
        var cells = Cells(m.ToSunVector);
        var rgba = new byte[width * height * 4];
        var eye = new Vector3(m.Viewer[0], m.Viewer[1] + eyeHeight, m.Viewer[2] - back);
        float pitch = pitchUpDeg * MathF.PI / 180f;
        var fwd = new Vector3(0f, MathF.Sin(pitch), MathF.Cos(pitch));
        var right = Vector3.UnitX;
        var up = Vector3.Cross(fwd, right);                    // +Z x +X = +Y in the engine's left-handed frame
        float ty = MathF.Tan(fovYDeg * MathF.PI / 360f), tx = ty * width / height;
        var site = m.SiteOf;
        var (mx0, mx1, mz0, mz1) = GroundMat(site);
        float groundY = m.Viewer[1];
        var boardColours = m.Boards.Select(b => cells.Select(c => colour(b, c)).ToArray()).ToArray();

        for (int py = 0; py < height; py++)
            for (int px = 0; px < width; px++)
            {
                var dir = Vector3.Normalize(fwd + right * ((2f * (px + 0.5f) / width - 1f) * tx) + up * ((1f - 2f * (py + 0.5f) / height) * ty));
                var col = new Vector3(0.55f, 0.62f, 0.70f);    // sky
                float best = float.MaxValue;
                if (dir.Y < -1e-5f)
                {
                    float t = (groundY - eye.Y) / dir.Y;
                    var p = eye + dir * t;
                    best = t;
                    col = new Vector3(0.45f, 0.42f, 0.36f);
                    foreach (var b in m.Boards)
                        if (b.Ground is { } gc && MathF.Abs(p.X - b.X) < 3.8f && p.Z >= m.BoardsZ - 6f && p.Z < m.BoardsZ + 11f)
                            col = new Vector3(gc[0], gc[1], gc[2]);
                    if (m.Swatches.Count > 0 && p.X >= mx0 && p.X < mx1 && p.Z >= mz0 && p.Z < mz1)
                    {
                        col = Vector3.Zero;
                        for (int i = 0; i < m.Swatches.Count; i++)
                        {
                            var (sx0, sx1, sz0, sz1) = GroundSwatchRect(site, i);
                            if (p.X >= sx0 && p.X < sx1 && p.Z >= sz0 && p.Z < sz1)
                                col = new Vector3(m.Swatches[i].Expected[0], m.Swatches[i].Expected[1], m.Swatches[i].Expected[2]);
                        }
                    }
                }
                if (dir.Z > 1e-5f)
                    for (int bi = 0; bi < m.Boards.Count; bi++)
                    {
                        var b = m.Boards[bi];
                        float t = (b.Z - eye.Z) / dir.Z;
                        if (t >= best) continue;
                        var p = eye + dir * t;
                        float u = (p.X - (b.X - BoardWidth * 0.5f)) / BoardWidth, v = (b.Y + BoardHeight - p.Y) / BoardHeight;
                        if (u < 0f || u >= 1f || v < 0f || v >= 1f) continue;
                        best = t;
                        col = Vector3.Zero;
                        float cu = (u * BoardWidth - Border) / Pitch, cv = (v * BoardHeight - Border) / Pitch;
                        int cc = (int)MathF.Floor(cu), cr = (int)MathF.Floor(cv);
                        if (cc >= 0 && cr >= 0 && cc < Columns && cr < Rows
                            && MathF.Abs(cu - cc - 0.5f) * Pitch <= PatchSize * 0.5f && MathF.Abs(cv - cr - 0.5f) * Pitch <= PatchSize * 0.5f)
                            col = boardColours[bi][cr * Columns + cc];
                    }
                int o = (py * width + px) * 4;
                rgba[o] = (byte)Math.Clamp((int)(col.X * 255f + 0.5f), 0, 255);
                rgba[o + 1] = (byte)Math.Clamp((int)(col.Y * 255f + 0.5f), 0, 255);
                rgba[o + 2] = (byte)Math.Clamp((int)(col.Z * 255f + 0.5f), 0, 255);
                rgba[o + 3] = 255;
            }
        return rgba;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Reading a screenshot
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>A black plate found in a picture: its corners (top-left, top-right, bottom-right, bottom-left, in
    /// pixels) and how many black pixels it has.</summary>
    public sealed record Quad(Vector2[] Corners, int Area)
    {
        public Vector2 Centre => (Corners[0] + Corners[1] + Corners[2] + Corners[3]) * 0.25f;
    }

    /// <summary>
    /// Find the black plates. A plate's texture is black, and black times any light is black, so its outline does
    /// not depend on the thing being measured. Returns the convex outline of each connected black region big enough
    /// to be a board, reduced to four corners.
    /// </summary>
    public static List<Quad> FindBlackQuads(ReadOnlySpan<byte> rgba, int width, int height, int threshold = 28, int minArea = 0)
    {
        if (minArea <= 0) minArea = Math.Max(400, width * height / 1500);
        var label = new int[width * height];
        var stack = new Stack<int>();
        var quads = new List<Quad>();
        bool Black(ReadOnlySpan<byte> px, int i) => px[i * 4] <= threshold && px[i * 4 + 1] <= threshold && px[i * 4 + 2] <= threshold;

        int next = 0;
        var rowMin = new int[height]; var rowMax = new int[height];
        for (int start = 0; start < width * height; start++)
        {
            if (label[start] != 0 || !Black(rgba, start)) continue;
            next++;
            Array.Fill(rowMin, int.MaxValue); Array.Fill(rowMax, int.MinValue);
            int area = 0, y0 = int.MaxValue, y1 = int.MinValue;
            label[start] = next; stack.Push(start);
            while (stack.Count > 0)
            {
                int p = stack.Pop(); int x = p % width, y = p / width;
                area++;
                if (x < rowMin[y]) rowMin[y] = x;
                if (x > rowMax[y]) rowMax[y] = x;
                if (y < y0) y0 = y;
                if (y > y1) y1 = y;
                if (x > 0 && label[p - 1] == 0 && Black(rgba, p - 1)) { label[p - 1] = next; stack.Push(p - 1); }
                if (x < width - 1 && label[p + 1] == 0 && Black(rgba, p + 1)) { label[p + 1] = next; stack.Push(p + 1); }
                if (y > 0 && label[p - width] == 0 && Black(rgba, p - width)) { label[p - width] = next; stack.Push(p - width); }
                if (y < height - 1 && label[p + width] == 0 && Black(rgba, p + width)) { label[p + width] = next; stack.Push(p + width); }
            }
            if (area < minArea) continue;
            var pts = new List<Vector2>((y1 - y0 + 1) * 4);
            for (int y = y0; y <= y1; y++)
            {
                if (rowMin[y] == int.MaxValue) continue;
                pts.Add(new Vector2(rowMin[y], y)); pts.Add(new Vector2(rowMin[y], y + 1));
                pts.Add(new Vector2(rowMax[y] + 1, y)); pts.Add(new Vector2(rowMax[y] + 1, y + 1));
            }
            var hull = ConvexHull(pts);
            if (hull.Count < 4) continue;
            var corners = ReduceToFour(hull);
            // A plate fills its outline; a stray black shape (a HUD frame, a shadowed doorway) mostly does not.
            float outline = MathF.Abs(PolygonArea(corners));
            if (outline < 1f || area < outline * 0.25f) continue;
            quads.Add(new Quad(OrderCorners(corners), area));
        }
        return quads;
    }

    private static List<Vector2> ConvexHull(List<Vector2> pts)
    {
        pts.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var h = new List<Vector2>();
        foreach (var p in pts)
        {
            while (h.Count >= 2 && Cross(h[^2], h[^1], p) <= 0) h.RemoveAt(h.Count - 1);
            h.Add(p);
        }
        int lower = h.Count + 1;
        for (int i = pts.Count - 2; i >= 0; i--)
        {
            var p = pts[i];
            while (h.Count >= lower && Cross(h[^2], h[^1], p) <= 0) h.RemoveAt(h.Count - 1);
            h.Add(p);
        }
        h.RemoveAt(h.Count - 1);
        return h;
    }

    private static float PolygonArea(IReadOnlyList<Vector2> p)
    {
        float a = 0;
        for (int i = 0; i < p.Count; i++) { var q = p[(i + 1) % p.Count]; a += p[i].X * q.Y - q.X * p[i].Y; }
        return a * 0.5f;
    }

    // Drop the hull vertex whose removal loses the least area until four are left: on a clean quad those are its
    // corners, because every other vertex sits on (nearly) a straight edge.
    private static Vector2[] ReduceToFour(List<Vector2> hull)
    {
        var h = new List<Vector2>(hull);
        while (h.Count > 4)
        {
            int best = 0; float bestArea = float.MaxValue;
            for (int i = 0; i < h.Count; i++)
            {
                var a = h[(i + h.Count - 1) % h.Count]; var b = h[i]; var c = h[(i + 1) % h.Count];
                float tri = MathF.Abs((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X));
                if (tri < bestArea) { bestArea = tri; best = i; }
            }
            h.RemoveAt(best);
        }
        return h.ToArray();
    }

    // Top-left, top-right, bottom-right, bottom-left: the two highest corners are the top edge.
    private static Vector2[] OrderCorners(Vector2[] c)
    {
        var byY = c.OrderBy(p => p.Y).ToArray();
        var top = byY.Take(2).OrderBy(p => p.X).ToArray();
        var bottom = byY.Skip(2).OrderBy(p => p.X).ToArray();
        return new[] { top[0], top[1], bottom[1], bottom[0] };
    }

    /// <summary>The picture position of a point on a plate, given as fractions of its width and height from the
    /// top-left corner. Exact for a flat plate seen in perspective.</summary>
    public static Vector2 Project(Vector2[] corners, float u, float v)
    {
        // The unit square to a quadrilateral (Heckbert): corners are top-left, top-right, bottom-right, bottom-left.
        float x0 = corners[0].X, y0 = corners[0].Y, x1 = corners[1].X, y1 = corners[1].Y;
        float x2 = corners[2].X, y2 = corners[2].Y, x3 = corners[3].X, y3 = corners[3].Y;
        float dx1 = x1 - x2, dx2 = x3 - x2, sx = x0 - x1 + x2 - x3;
        float dy1 = y1 - y2, dy2 = y3 - y2, sy = y0 - y1 + y2 - y3;
        float den = dx1 * dy2 - dy1 * dx2;
        float g = MathF.Abs(den) < 1e-9f ? 0f : (sx * dy2 - sy * dx2) / den;
        float h = MathF.Abs(den) < 1e-9f ? 0f : (dx1 * sy - dy1 * sx) / den;
        float a = x1 - x0 + g * x1, b = x3 - x0 + h * x3, c = x0;
        float d = y1 - y0 + g * y1, e = y3 - y0 + h * y3, f = y0;
        float w = g * u + h * v + 1f;
        return new Vector2((a * u + b * v + c) / w, (d * u + e * v + f) / w);
    }

    /// <summary>The median colour (0..1 per channel) of the picture round a point on a plate. The window is a
    /// fraction of the plate, so it scales with how large the plate is in the picture; the median keeps a
    /// crosshair or a line of chat text out of it.</summary>
    public static Vector3 SampleAt(ReadOnlySpan<byte> rgba, int width, int height, Vector2[] corners, Vector2 at, float halfU, float halfV)
    {
        var r = new List<byte>(); var g = new List<byte>(); var b = new List<byte>();
        var p00 = Project(corners, at.X - halfU, at.Y - halfV); var p11 = Project(corners, at.X + halfU, at.Y + halfV);
        var p10 = Project(corners, at.X + halfU, at.Y - halfV); var p01 = Project(corners, at.X - halfU, at.Y + halfV);
        int steps = (int)Math.Clamp(MathF.Max(MathF.Max(Vector2.Distance(p00, p10), Vector2.Distance(p01, p11)),
                                               MathF.Max(Vector2.Distance(p00, p01), Vector2.Distance(p10, p11))) * 1.5f, 3f, 64f);
        for (int j = 0; j <= steps; j++)
            for (int i = 0; i <= steps; i++)
            {
                var p = Project(corners, at.X + halfU * (2f * i / steps - 1f), at.Y + halfV * (2f * j / steps - 1f));
                int x = (int)MathF.Floor(p.X), y = (int)MathF.Floor(p.Y);
                if (x < 0 || y < 0 || x >= width || y >= height) continue;
                int o = (y * width + x) * 4;
                r.Add(rgba[o]); g.Add(rgba[o + 1]); b.Add(rgba[o + 2]);
            }
        if (r.Count == 0) return new Vector3(float.NaN);
        r.Sort(); g.Sort(); b.Sort();
        return new Vector3(r[r.Count / 2], g[g.Count / 2], b[b.Count / 2]) / 255f;
    }

    /// <summary>The fifty cells' colours, in <see cref="Cells"/> order. <paramref name="mirrored"/> reads the plate
    /// as seen from behind.</summary>
    public static Vector3[] SampleBoard(ReadOnlySpan<byte> rgba, int width, int height, Vector2[] corners, bool mirrored = false)
    {
        var res = new Vector3[Columns * Rows];
        float hu = PatchSize * 0.3f / BoardWidth, hv = PatchSize * 0.3f / BoardHeight;
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Columns; c++)
            {
                var at = CellCentre(c, r);
                if (mirrored) at.X = 1f - at.X;
                res[r * Columns + c] = SampleAt(rgba, width, height, corners, at, hu, hv);
            }
        return res;
    }

    /// <summary>
    /// Is this black shape one of the boards? A board has lit patches on a grid and black between them: the points
    /// where four patches' corners meet are all plate, and so is the rim. A HUD panel or a gun in the foreground
    /// can be black and board-shaped, but it does not have that grid.
    /// </summary>
    public static bool IsBoard(ReadOnlySpan<byte> rgba, int width, int height, Vector2[] corners, bool mirrored = false)
    {
        static float Peak(Vector3 v) => float.IsNaN(v.X) ? 1f : MathF.Max(v.X, MathF.Max(v.Y, v.Z));
        int lit = 0;
        foreach (var v in SampleBoard(rgba, width, height, corners, mirrored)) if (!float.IsNaN(v.X) && Peak(v) > 0.12f) lit++;
        if (lit < 12) return false;
        float hu = (Pitch - PatchSize) * 0.25f / BoardWidth, hv = (Pitch - PatchSize) * 0.25f / BoardHeight;
        int dark = 0, total = 0;
        for (int r = 0; r <= Rows; r++)
            for (int c = 0; c <= Columns; c++)
            {
                var at = new Vector2((Border + c * Pitch) / BoardWidth, (Border + r * Pitch) / BoardHeight);
                if (mirrored) at.X = 1f - at.X;
                total++;
                if (Peak(SampleAt(rgba, width, height, corners, at, hu, hv)) < 0.12f) dark++;
            }
        return dark >= total * 0.9f;
    }

    /// <summary>
    /// The light a set of patches saw, with nothing assumed about where it is: each patch gives its normal and how
    /// bright one channel was (with the unlit level already taken off), and a directional light makes that
    /// <c>max(0, N . v)</c> for one vector <c>v</c> - its direction times its strength. Solved by least squares
    /// over the patches it reaches, re-choosing them until they stop changing.
    /// </summary>
    /// <returns>The direction TOWARD the light, its strength, and the fit's RMS error over every patch.</returns>
    public static (Vector3 Direction, float Strength, float Rms) SolveLight(IReadOnlyList<(Vector3 Normal, float Value)> samples)
    {
        if (samples.Count < 4) return (Vector3.Zero, 0f, 0f);
        float peak = samples.Max(s => s.Value);
        if (peak <= 1e-4f) return (Vector3.Zero, 0f, 0f);
        var active = samples.Select(s => s.Value > peak * 0.08f).ToArray();
        Vector3 v = Vector3.Zero;
        for (int iter = 0; iter < 12; iter++)
        {
            // Normal equations of sum (N.v - value)^2 over the active patches.
            double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0, bx = 0, by = 0, bz = 0; int n = 0;
            for (int i = 0; i < samples.Count; i++)
            {
                if (!active[i]) continue;
                var (N, val) = samples[i]; n++;
                xx += N.X * N.X; xy += N.X * N.Y; xz += N.X * N.Z; yy += N.Y * N.Y; yz += N.Y * N.Z; zz += N.Z * N.Z;
                bx += N.X * val; by += N.Y * val; bz += N.Z * val;
            }
            if (n < 3) break;
            double det = xx * (yy * zz - yz * yz) - xy * (xy * zz - yz * xz) + xz * (xy * yz - yy * xz);
            if (Math.Abs(det) < 1e-12) break;
            var nv = new Vector3(
                (float)((bx * (yy * zz - yz * yz) - xy * (by * zz - yz * bz) + xz * (by * yz - yy * bz)) / det),
                (float)((xx * (by * zz - yz * bz) - bx * (xy * zz - yz * xz) + xz * (xy * bz - by * xz)) / det),
                (float)((xx * (yy * bz - by * yz) - xy * (xy * bz - by * xz) + bx * (xy * yz - yy * xz)) / det));
            var nextActive = samples.Select(s => Vector3.Dot(s.Normal, nv) > 0f && s.Value > peak * 0.02f).ToArray();
            bool same = nextActive.SequenceEqual(active) && Vector3.Distance(nv, v) < 1e-5f;
            v = nv; active = nextActive;
            if (same) break;
        }
        float len = v.Length();
        if (len < 1e-6f) return (Vector3.Zero, 0f, 0f);
        double err = 0;
        foreach (var (N, val) in samples) { float d = MathF.Max(0f, Vector3.Dot(N, v)) - val; err += d * d; }
        return (v / len, len, (float)Math.Sqrt(err / samples.Count));
    }
}
