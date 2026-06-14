using System.Numerics;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Terrain;

namespace RefractorForge.Render;

/// <summary>
/// A loaded level ready to render and edit: terrain mesh + placed objects, plus the camera/lighting
/// and proxy-box logic. The interactive GUI and the headless preview both drive this, so the render
/// path lives in exactly one place. When real StandardMesh geometry lands, only the model arrays in
/// <see cref="Render"/> change — nothing else here moves.
/// </summary>
public sealed class LevelScene
{
    public TerrainConfig Config { get; }
    public Heightmap Heightmap { get; }
    public StaticObjectsFile Objects { get; }
    public TerrainMesh Mesh { get; private set; }
    public float MinHeight { get; private set; }
    public float MaxHeight { get; private set; }
    public float MidHeight => (MinHeight + MaxHeight) * 0.5f;
    public float WorldSize => Config.WorldSize;

    /// <summary>Terrain sculpting for this level. Mutating it then calling <see cref="RebuildTerrain"/> updates the render mesh.</summary>
    public TerrainEditor Terrain { get; }

    /// <summary>The terrain material (texture) map, if the level has one. Null when MaterialMap.raw is absent.</summary>
    public MaterialMap? MaterialMap { get; }

    /// <summary>Texture painter over <see cref="MaterialMap"/>, or null if the level has no material map.</summary>
    public MaterialPainter? Materials { get; }

    /// <summary>The level's baked terrain texture (assembled from its txCxR.dds tiles), or null if absent.</summary>
    public TerrainTexture? TerrainTex { get; private set; }

    /// <summary>Attach (or detach with null) a baked terrain texture so <see cref="Render"/> textures the ground.</summary>
    public void AttachTerrainTexture(TerrainTexture? tex) => TerrainTex = tex;

    private int _stride;

    /// <summary>Optional real-geometry source. When attached, <see cref="Render"/> draws actual
    /// StandardMesh props instead of proxy boxes (boxes remain the fallback for mesh-less templates).</summary>
    public MeshLibrary? Meshes { get; private set; }

    /// <summary>Attach (or detach with null) a mesh library so objects render as real geometry.</summary>
    public void AttachMeshes(MeshLibrary? lib) => Meshes = lib;

    /// <summary>Proxy-box footprint/height in metres (matches <see cref="ObjectProxies"/> defaults).</summary>
    public const float ProxyWidth = 8f, ProxyHeight = 18f;

    private LevelScene(TerrainConfig cfg, Heightmap hm, StaticObjectsFile objs, TerrainMesh mesh, float minH, float maxH, int stride, MaterialMap? mat)
    {
        Config = cfg; Heightmap = hm; Objects = objs; Mesh = mesh; MinHeight = minH; MaxHeight = maxH; _stride = stride;
        Terrain = new TerrainEditor(hm, cfg);
        MaterialMap = mat;
        Materials = mat is null ? null : new MaterialPainter(mat, cfg);
    }

    public static LevelScene Load(string levelDir, int stride = 1)
    {
        string Find(string name) => Directory.EnumerateFiles(levelDir, name, SearchOption.AllDirectories).First();
        string? TryFind(string name) => Directory.EnumerateFiles(levelDir, name, SearchOption.AllDirectories).FirstOrDefault();
        var cfg = TerrainConfig.Load(Find("Terrain.con"));
        var hm = Heightmap.LoadForMaterialSize(Find("Heightmap.raw"), cfg.MaterialSize);
        var objs = StaticObjectsFile.Load(Find("StaticObjects.con"));
        var mesh = TerrainMesh.FromHeightmap(hm, cfg, stride);
        var (minH, maxH) = HeightExtent(mesh);
        MaterialMap? mat = null;
        var matPath = TryFind("MaterialMap.raw");
        if (matPath is not null)
            try { mat = MaterialMap.LoadForMaterialSize(matPath, cfg.MaterialSize); } catch { mat = null; }
        var scene = new LevelScene(cfg, hm, objs, mesh, minH, maxH, stride, mat);
        // Baked terrain texture: the level's Textures/ folder of txCxR.dds tiles.
        try
        {
            var texDir = Directory.EnumerateDirectories(levelDir, "Textures", SearchOption.AllDirectories).FirstOrDefault();
            if (texDir is not null) scene.TerrainTex = TerrainTexture.Load(texDir, cfg.WorldSize);
        }
        catch { /* texturing is optional; fall back to the height ramp */ }
        return scene;
    }

    /// <summary>
    /// Build a scene from a level already loaded out of one or more packed <c>.rfa</c> archives
    /// (<see cref="LevelArchive.FromRfa"/>). This is the .rfa counterpart to <see cref="Load"/> — same render
    /// data (terrain mesh + material map + baked terrain texture), so the .rfa and folder paths render identically.
    /// </summary>
    public static LevelScene FromLoaded(LevelArchive.Loaded loaded, int stride = 1)
    {
        var mesh = TerrainMesh.FromHeightmap(loaded.Heightmap, loaded.Config, stride);
        var (minH, maxH) = HeightExtent(mesh);
        var scene = new LevelScene(loaded.Config, loaded.Heightmap, loaded.StaticObjects, mesh, minH, maxH, stride, loaded.Material);
        scene.TerrainTex = loaded.Terrain;
        return scene;
    }

    /// <summary>Rebuild the render mesh from the (possibly sculpted) heightmap and refresh the height range.</summary>
    public void RebuildTerrain()
    {
        Mesh = TerrainMesh.FromHeightmap(Heightmap, Config, _stride);
        (MinHeight, MaxHeight) = HeightExtent(Mesh);
    }

    private static (float, float) HeightExtent(TerrainMesh mesh)
    {
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (var p in mesh.Positions) { if (p.Y < lo) lo = p.Y; if (p.Y > hi) hi = p.Y; }
        return (lo, hi);
    }

    /// <summary>Bilinearly-interpolated terrain height (metres) at a world XZ position.</summary>
    public float HeightAtWorld(float worldX, float worldZ)
    {
        float sp = Config.HorizontalSpacing; if (sp <= 0f) sp = 1f;
        float gx = Math.Clamp(worldX / sp, 0f, Heightmap.Width - 1.0001f);
        float gz = Math.Clamp(worldZ / sp, 0f, Heightmap.Height - 1.0001f);
        int x0 = (int)gx, z0 = (int)gz; int x1 = Math.Min(x0 + 1, Heightmap.Width - 1), z1 = Math.Min(z0 + 1, Heightmap.Height - 1);
        float fx = gx - x0, fz = gz - z0;
        float h00 = Config.HeightToMeters(Heightmap[x0, z0]);
        float h10 = Config.HeightToMeters(Heightmap[x1, z0]);
        float h01 = Config.HeightToMeters(Heightmap[x0, z1]);
        float h11 = Config.HeightToMeters(Heightmap[x1, z1]);
        return (h00 * (1 - fx) + h10 * fx) * (1 - fz) + (h01 * (1 - fx) + h11 * fx) * fz;
    }

    /// <summary>March a ray against the heightfield; returns the first hit point in world space, or null.</summary>
    public Vector3? RaycastTerrain(Ray ray)
    {
        float sp = Config.HorizontalSpacing; if (sp <= 0f) sp = 1f;
        float step = sp * 0.5f;
        float tMax = WorldSize * 3f + 2000f;
        float prevT = 0f;
        float prevDiff = ray.Origin.Y - HeightAtWorld(ray.Origin.X, ray.Origin.Z);  // >0 = above ground
        for (float t = step; t <= tMax; t += step)
        {
            var p = ray.Origin + ray.Dir * t;
            float diff = p.Y - HeightAtWorld(p.X, p.Z);
            if (prevDiff > 0f && diff <= 0f)        // crossed from above to below: bisect
            {
                float lo = prevT, hi = t;
                for (int i = 0; i < 24; i++)
                {
                    float mid = (lo + hi) * 0.5f;
                    var pm = ray.Origin + ray.Dir * mid;
                    if (pm.Y - HeightAtWorld(pm.X, pm.Z) > 0f) lo = mid; else hi = mid;
                }
                return ray.Origin + ray.Dir * hi;
            }
            prevT = t; prevDiff = diff;
        }
        return null;
    }

    public Camera CreateAerialCamera(float aspect) => Camera.FrameAerial(WorldSize, MidHeight, aspect);

    /// <summary>Pick-points = proxy-box centres, in the same order as <see cref="StaticObjectsFile.Objects"/>.</summary>
    public List<Vector3> PickPoints()
    {
        var pts = new List<Vector3>(Objects.Objects.Count);
        foreach (var o in Objects.Objects)
        {
            float sc = o.Scale ?? 1f;
            pts.Add(new Vector3(o.Position.X, o.Position.Y + ProxyHeight * sc * 0.5f, o.Position.Z));
        }
        return pts;
    }

    /// <summary>A generous selection radius so boxes are easy to click.</summary>
    public float PickRadius => ProxyWidth;

    /// <summary>Render terrain + objects into a fresh framebuffer. When a <see cref="MeshLibrary"/> is
    /// attached, resolvable objects draw as real StandardMesh geometry (grouped by template for
    /// instancing); mesh-less templates fall back to proxy boxes. Highlights the selected object.</summary>
    /// <summary>Render the scene. When <paramref name="fast"/> is true, terrain uses the height ramp
    /// and objects use flat shader colors (skipping per-pixel texture sampling) for smooth interaction;
    /// the editor uses this while the camera is moving and the full textured pass when it settles.</summary>
    public ImageBuffer Render(Camera cam, int width, int height, int selectedIndex = -1, bool fast = false)
    {
        var img = new ImageBuffer(Math.Max(1, width), Math.Max(1, height));
        img.Clear(new Vector3(0.55f, 0.68f, 0.85f));   // sky
        var light = Vector3.Normalize(new Vector3(-0.5f, 0.8f, -0.35f));
        if (TerrainTex is not null && !fast)
            SoftwareRenderer.DrawTerrainTextured(img, Mesh, cam, light, TerrainTex, Config.WaterLevel);
        else
            SoftwareRenderer.DrawTerrain(img, Mesh, cam, light, Config.WaterLevel, MinHeight, MaxHeight);

        var objs = Objects.Objects;
        var highlight = new Vector3(1f, 0.95f, 0.2f);

        if (Meshes is null)
        {
            // Legacy path: every object is a proxy box.
            var boxes = ObjectProxies.Build(objs);
            if ((uint)selectedIndex < (uint)boxes.Count)
                boxes[selectedIndex] = boxes[selectedIndex] with { Color = highlight };
            SoftwareRenderer.DrawModels(img, cam, light, SoftwareRenderer.CubePositions, SoftwareRenderer.CubeIndices, boxes);
            return img;
        }

        // Real-geometry path: group resolvable objects by template; box-fallback the rest.
        var groups = new Dictionary<string, (MeshLibrary.Mesh mesh, List<(Matrix4x4 world, bool sel)> placed)>();
        var boxInstances = new List<ModelInstance>();
        for (int i = 0; i < objs.Count; i++)
        {
            var o = objs[i];
            bool sel = i == selectedIndex;
            if (Meshes.TryGet(o.Template, out var mesh))
            {
                if (!groups.TryGetValue(o.Template, out var g))
                    groups[o.Template] = g = (mesh, new List<(Matrix4x4, bool)>());
                g.placed.Add((MeshWorld(o), sel));
            }
            else
            {
                // Mesh-less templates (sound/effect emitters, supply zones) are invisible in-game;
                // show them as a small ground marker rather than a full proxy tower, so real geometry
                // dominates the view. They stay selectable/visible for editing.
                boxInstances.Add(new ModelInstance(MarkerWorld(o), sel ? highlight : ObjectProxies.ColorFor(o.Template)));
            }
        }

        // Each material part is drawn once per template with the placed transforms. Parts with a real
        // texture sample it via the mesh UVs (white tint, or highlight if selected); untextured parts
        // use the shader-derived flat color.
        var white = Vector3.One;
        var instBuf = new List<ModelInstance>();
        foreach (var (mesh, placed) in groups.Values)
            foreach (var part in mesh.Parts)
            {
                instBuf.Clear();
                bool textured = part.Texture is not null && !fast;
                foreach (var (world, sel) in placed)
                    instBuf.Add(new ModelInstance(world, sel ? highlight : (textured ? white : part.Color)));
                if (textured)
                    SoftwareRenderer.DrawModelsTextured(img, cam, light, mesh.Positions, mesh.Uvs, part.Indices, part.Texture!, part.AlphaTest, instBuf);
                else
                    SoftwareRenderer.DrawModels(img, cam, light, mesh.Positions, part.Indices, instBuf);
            }
        if (boxInstances.Count > 0)
            SoftwareRenderer.DrawModels(img, cam, light, SoftwareRenderer.CubePositions, SoftwareRenderer.CubeIndices, boxInstances);
        return img;
    }

    /// <summary>World transform for a real mesh: object-local metres positioned/rotated/scaled in place
    /// (no proxy-box footprint scale or half-height lift — the mesh already has its true size/origin).</summary>
    public static Matrix4x4 MeshWorld(StaticObject o)
    {
        float sc = o.Scale ?? 1f;
        return Matrix4x4.CreateScale(sc)
             * Matrix4x4.CreateFromYawPitchRoll(Rad(o.Rotation.X), Rad(o.Rotation.Y), Rad(o.Rotation.Z))
             * Matrix4x4.CreateTranslation(o.Position.X, o.Position.Y, o.Position.Z);
    }

    /// <summary>Small ground marker (3 m cube) for mesh-less / invisible gameplay objects.</summary>
    private static Matrix4x4 MarkerWorld(StaticObject o)
    {
        const float s = 3f;
        return Matrix4x4.CreateScale(s)
             * Matrix4x4.CreateTranslation(o.Position.X, o.Position.Y + s * 0.5f, o.Position.Z);
    }

    private static float Rad(float deg) => deg * MathF.PI / 180f;
}
