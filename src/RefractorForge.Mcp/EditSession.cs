using System.Globalization;
using RefractorForge.Formats;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Editing;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;
using RefractorForge.Render;

namespace RefractorForge.Mcp;

/// <summary>
/// The MCP server's in-memory editing document: one loaded level plus an undo history, driven entirely through the
/// existing <see cref="IEditCommand"/> ops so it stays byte-compatible with the editor and (Phase 2) the collab
/// wire. The "headless core" the plan calls for — tools mutate this, then it saves via <see cref="LevelSaver"/>.
/// </summary>
public sealed class EditSession
{
    public string SourceRfa { get; }
    public string Name { get; }
    public LevelArchive.Loaded Level { get; }

    /// <summary>The object document being edited. Once attached to a running editor this is the EDITOR's live
    /// document (streamed over the collab relay), so listing, placing and generating all operate on what the user
    /// is actually looking at rather than on a stale copy from disk.</summary>
    public StaticObjectsFile So => _live?.Doc ?? Level.StaticObjects;

    /// <summary>The attached editor, when running in live mode.</summary>
    public LiveBridge? Live => _live;
    public bool IsLive => _live is not null;
    public TerrainConfig Cfg => Level.Config;
    public Heightmap Hm => Level.Heightmap;
    public MaterialMap? Material => Level.Material;

    private readonly EditHistory _history;
    private int _addCounter;
    private LiveBridge? _live;
    private MeshLibrary? _catalog;
    private bool _catalogTried;
    private readonly Dictionary<string, float> _footprint = new(StringComparer.OrdinalIgnoreCase);

    // Only write back what actually changed (a city edit shouldn't rewrite the heightmap or gameplay).
    public bool ObjectsDirty { get; private set; }
    public bool ConfigDirty { get; private set; }
    public bool HeightDirty { get; private set; }

    private EditSession(string rfa, LevelArchive.Loaded level)
    {
        SourceRfa = rfa;
        Level = level;
        Name = Path.GetFileNameWithoutExtension(rfa);
        _history = new EditHistory(level.StaticObjects);

        // Give every pre-existing object a stable id so it can be addressed by move/rotate/delete. Ids are not
        // written to StaticObjects.con, so this never changes the saved file — it only makes the doc editable.
        for (int i = 0; i < So.Objects.Count; i++)
            if (string.IsNullOrEmpty(So.Objects[i].Id)) So.Objects[i].Id = $"obj-{i}";
    }

    /// <summary>True when this session was opened from an extracted level FOLDER rather than a packed archive.
    /// Saving follows the form it was opened in, so a project keeps its loose files and an archive stays packed.</summary>
    public bool IsFolder => Directory.Exists(SourceRfa);

    /// <summary>Open a level from a packed <c>.rfa</c>, an extracted level folder, or a base plus patch archives.</summary>
    public static EditSession OpenRfa(params string[] rfaPaths)
        => new(rfaPaths[0], LevelArchive.FromRfa(rfaPaths));

    /// <summary>Attach to a running editor. From here on every edit is sent to it live instead of being applied to
    /// the local copy — but the level opened from disk is kept, because the terrain it carries is what scatter and
    /// city generation sample heights from (the relay streams objects, never the heightmap).</summary>
    public void AttachLive(LiveBridge bridge) { _live?.Dispose(); _live = bridge; }

    public void DetachLive() { _live?.Dispose(); _live = null; }

    /// <summary>How much of the live document is built from templates this level also has on disk. A low overlap
    /// almost always means the editor has a DIFFERENT level open, which would silently put generated objects at
    /// heights sampled from the wrong terrain — so the attach reports it rather than letting it pass.</summary>
    public double LiveTemplateOverlap()
    {
        if (_live is null) return 1.0;
        var mine = Level.StaticObjects.Objects.Select(o => o.Template).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var live = _live.Snapshot().Select(o => o.Template).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (live.Count == 0) return 1.0;
        return live.Count(t => mine.Contains(t)) / (double)live.Count;
    }

    /// <summary>Terrain height (metres) at world (x,z), matching the editor's nearest-sample convention.</summary>
    public float HeightAt(float x, float z)
    {
        float sp = Cfg.HorizontalSpacing <= 0 ? 1f : Cfg.HorizontalSpacing;
        int gx = Math.Clamp((int)(x / sp), 0, Hm.Width - 1);
        int gz = Math.Clamp((int)(z / sp), 0, Hm.Height - 1);
        return Cfg.HeightToMeters(Hm[gx, gz]);
    }

    // ---- Object edits (all reversible via the shared history) ----

    public string PlaceObject(string template, float x, float z, float? y, Vec3 rot, bool avoidOverlap = false, float clearance = 0f)
    {
        float yy = y ?? HeightAt(x, z);
        if (avoidOverlap && WouldOverlap(template, new Vec3(x, yy, z), clearance))
            throw new InvalidOperationException(
                $"{template} at {x:0}/{z:0} would sit inside something already there " +
                $"(its footprint is {FootprintRadius(template):0.#} m). Move it, or pass avoidOverlap false.");
        if (_live is not null) return _live.Add(template, new Vec3(x, yy, z), rot);
        string id = $"mcp-{++_addCounter}";
        _history.Do(new AddObject(id, template, new Vec3(x, yy, z), rot));
        ObjectsDirty = true;
        return id;
    }

    public bool Move(string id, Vec3 to) => _live is not null ? _live.Move(id, to) : Edit(id, () => _history.Do(new MoveObject(id, to)));
    public bool Rotate(string id, Vec3 to) => _live is not null ? _live.Rotate(id, to) : Edit(id, () => _history.Do(new RotateObject(id, to)));
    public bool ScaleObj(string id, float to) => _live is not null ? _live.Scale(id, to) : Edit(id, () => _history.Do(new ScaleObject(id, to)));
    public bool Delete(string id) => _live is not null ? _live.Delete(id) : Edit(id, () => _history.Do(new DeleteObject(id)));

    private bool Edit(string id, Action act)
    {
        if (So.FindById(id) is null) return false;
        act(); ObjectsDirty = true; return true;
    }

    /// <summary>Random scatter over the whole map (vegetation, props…). Area-targeted density is what
    /// <see cref="GenerateCity"/> is for; an area-bounded scatter overload can follow if needed.</summary>
    public int Scatter(IReadOnlyList<string> templates, int count, float minSlope, float maxSlope,
        bool avoidWater, float waterClearance, float spacing, int seed, float edgeMargin, float minScale, float maxScale,
        bool avoidOverlap = true, float clearance = 0f)
    {
        var placed = ObjectScatter.Scatter(templates, Cfg, HeightAt, count, minSlope, maxSlope,
            avoidWater, waterClearance, spacing, seed, edgeMargin, minScale, maxScale);
        if (avoidOverlap) placed = FilterOverlaps(placed, clearance); else LastSkippedOverlaps = 0;
        ApplyBatch(placed);
        return placed.Count;
    }

    /// <summary>Procedurally build a grid city in the given world-space area; returns the layout (buildings already
    /// applied as objects, plus the street centerlines the Render layer can later texture).</summary>
    public CityLayout GenerateCity(float minX, float minZ, float maxX, float maxZ,
        IReadOnlyList<string> palette, int seed, float blockSize, float roadWidth, float setback,
        float lotWidth, float spacing, float maxSlope, bool avoidWater, float waterClearance, float minScale, float maxScale,
        bool avoidOverlap = true, float clearance = 0f)
    {
        var layout = CityGenerator.Generate(minX, minZ, maxX, maxZ, Cfg, HeightAt, palette, seed,
            blockSize, roadWidth, setback, lotWidth, spacing, maxSlope, avoidWater, waterClearance, minScale, maxScale);
        if (avoidOverlap)
        {
            var kept = FilterOverlaps(layout.Buildings, clearance);
            layout.Buildings.Clear();
            layout.Buildings.AddRange(kept);
        }
        else LastSkippedOverlaps = 0;
        ApplyBatch(layout.Buildings);
        return layout;
    }

    /// <summary>Apply a whole generated batch as ONE history entry. A city is hundreds of placements, and pushing
    /// them individually meant "undo" walked back a building at a time - the user asked for a city, so a city is the
    /// unit they get to take back. <see cref="CompositeCommand"/> already applies and reverses in the right order,
    /// and collab sees a single grouped op rather than a storm of them.</summary>
    /// <summary>How many placements the last scatter/city dropped because they would have overlapped.</summary>
    public int LastSkippedOverlaps { get; private set; }

    /// <summary>Drop placements that would land inside something already placed, or inside each other.</summary>
    private List<ScatterPlacement> FilterOverlaps(IReadOnlyList<ScatterPlacement> placements, float clearance)
    {
        var kept = new List<ScatterPlacement>(placements.Count);
        var pending = new List<(Vec3 Pos, float R)>(placements.Count);
        foreach (var p in placements)
        {
            if (WouldOverlap(p.Template, p.Position, clearance, pending)) continue;
            kept.Add(p);
            pending.Add((p.Position, FootprintRadius(p.Template)));
        }
        LastSkippedOverlaps = placements.Count - kept.Count;
        return kept;
    }

    private void ApplyBatch(IReadOnlyList<ScatterPlacement> placements)
    {
        if (placements.Count == 0) return;
        if (_live is not null)
        {
            _live.AddMany(placements.Select(p => (p.Template, p.Position, new Vec3(p.Yaw, 0f, 0f), p.Scale)));
            return;
        }
        var cmds = new List<IEditCommand>(placements.Count);
        foreach (var p in placements)
        {
            string id = $"mcp-{++_addCounter}";
            cmds.Add(new AddObject(id, p.Template, p.Position, new Vec3(p.Yaw, 0f, 0f)));
            if (MathF.Abs(p.Scale - 1f) > 1e-4f) cmds.Add(new ScaleObject(id, p.Scale));
        }
        _history.Do(new CompositeCommand(cmds));
        ObjectsDirty = true;
    }

    /// <summary>Raise a mountain into the terrain. When attached to a running editor the changed heightmap rect is
    /// shipped as a collab TERRAIN op, so the ground rises in the viewport straight away; otherwise it is written
    /// into the local heightmap and saved with the level.</summary>
    public (float Peak, int Cells) RaiseMountain(float cx, float cz, float radius, float peak, int seed,
                                                 float roughness, int ridges)
    {
        var (x0, y0, w, h) = MountainGenerator.Raise(Hm, Cfg, cx, cz, radius, peak, seed, roughness, ridges);
        if (w == 0 || h == 0) throw new ArgumentException("the mountain lands entirely outside the map");

        float top = MountainGenerator.PeakHeight(Hm, Cfg, x0, y0, w, h);
        ShipTerrain(x0, y0, w, h);
        return (top, w * h);
    }

    /// <summary>Every template the mounted mod can actually place, not just the ones already in the level.
    /// Discovered from the level's own path: a level lives at &lt;mod&gt;/Archives/&lt;game&gt;/levels/x.rfa, so the mod
    /// is walkable from there, and <see cref="ModChain"/> resolves its inherited mounts (FHSW -> FH -> bf1942)
    /// the same way the editor does. Null when the level did not come from inside a mod tree.</summary>
    public MeshLibrary? Catalog
    {
        get
        {
            if (_catalogTried) return _catalog;
            _catalogTried = true;
            try
            {
                var root = ModChain.FindGameRoot(SourceRfa);
                if (root is null) return null;
                // .../Mods/<mod>/Archives/<game>/levels/<x>.rfa - walk up to the mod folder.
                var dir = new DirectoryInfo(Directory.Exists(SourceRfa) ? SourceRfa : Path.GetDirectoryName(SourceRfa)!);
                DirectoryInfo? mod = null;
                for (var d = dir; d is not null; d = d.Parent)
                    if (d.Parent is not null && d.Parent.Name.Equals("Mods", StringComparison.OrdinalIgnoreCase)) { mod = d; break; }
                if (mod is null) return null;

                var chain = ModChain.Resolve(root, mod.FullName);
                var (mesh, _) = ModChain.CollectArchives(chain);
                if (mesh.Length == 0) return null;
                _catalog = MeshLibrary.Open(mesh);
            }
            catch { _catalog = null; }
            return _catalog;
        }
    }

    /// <summary>How much ground a template occupies, as an XZ radius in metres, measured from its actual mesh.
    /// Placement without this is what puts a house inside another house: a grid spacing that suits a hut is far too
    /// tight for a hangar, and nothing in the level tells you which is which. Unknown templates (no resolvable mesh)
    /// get a small default rather than zero, so they still keep some distance.</summary>
    public float FootprintRadius(string template)
    {
        if (_footprint.TryGetValue(template, out var r)) return r;
        r = 3f;
        try
        {
            if (Catalog is { } lib && lib.TryGetRenderMesh(template, out var mesh) && mesh.Positions.Length > 0)
            {
                float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
                foreach (var p in mesh.Positions)
                {
                    if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
                    if (p.Z < minZ) minZ = p.Z; if (p.Z > maxZ) maxZ = p.Z;
                }
                // Half the larger horizontal extent: a circle that covers the footprint whatever its yaw.
                r = MathF.Max(MathF.Max(maxX - minX, maxZ - minZ) * 0.5f, 0.5f);
            }
        }
        catch { }
        _footprint[template] = r;
        return r;
    }

    /// <summary>Would an object of this template at this spot sit inside something already there?</summary>
    public bool WouldOverlap(string template, Vec3 pos, float clearance = 0f,
                             IReadOnlyList<(Vec3 Pos, float R)>? alsoAvoid = null)
    {
        float r = FootprintRadius(template) + clearance;
        foreach (var o in So.Objects)
        {
            float rr = r + FootprintRadius(o.Template);
            float dx = o.Position.X - pos.X, dz = o.Position.Z - pos.Z;
            if (dx * dx + dz * dz < rr * rr) return true;
        }
        if (alsoAvoid is not null)
            foreach (var (p, orr) in alsoAvoid)
            {
                float rr = r + orr;
                float dx = p.X - pos.X, dz = p.Z - pos.Z;
                if (dx * dx + dz * dz < rr * rr) return true;
            }
        return false;
    }

    /// <summary>Pairs of placed objects whose footprints intersect — what is already wrong, rather than what would
    /// be. Ordered worst-first by how deeply they interpenetrate.</summary>
    public List<(StaticObject A, StaticObject B, float Overlap)> FindOverlaps(float clearance = 0f, int max = 50)
    {
        var objs = So.Objects;
        var hits = new List<(StaticObject, StaticObject, float)>();
        for (int i = 0; i < objs.Count; i++)
            for (int j = i + 1; j < objs.Count; j++)
            {
                float rr = FootprintRadius(objs[i].Template) + FootprintRadius(objs[j].Template) + clearance;
                float dx = objs[i].Position.X - objs[j].Position.X, dz = objs[i].Position.Z - objs[j].Position.Z;
                float d2 = dx * dx + dz * dz;
                if (d2 < rr * rr) hits.Add((objs[i], objs[j], rr - MathF.Sqrt(d2)));
            }
        return hits.OrderByDescending(h => h.Item3).Take(max).ToList();
    }

    /// <summary>What the ground is doing at a world position.</summary>
    public TerrainProbe Probe(float x, float z) => SiteFinder.Probe(Hm, Cfg, x, z, Material);

    /// <summary>Patches of ground flat and dry enough to build on, best first.</summary>
    public List<BuildSite> FindSites(float radius, float maxSlope, float maxSpread, bool avoidWater,
                                     float waterClearance, int max, bool clearOfObjects,
                                     float minX, float minZ, float maxX, float maxZ, float maxSteepFraction)
        => SiteFinder.Find(Hm, Cfg, radius, maxSlope, maxSpread, avoidWater, waterClearance, max,
                           clearOfObjects ? So.Objects.Select(o => (o.Position.X, o.Position.Z)) : null,
                           clearOfObjects ? radius : 0f, minX, minZ, maxX, maxZ, maxSteepFraction);

    /// <summary>A top-down PNG of the level - ground, relief, object dots and a coordinate grid.</summary>
    public byte[] RenderMap(int size, IEnumerable<Vec3>? highlight = null, bool grid = true)
        => PngWriter.Encode(MapView.Render(size, Hm, Cfg, Level.Terrain, Material, So.Objects, highlight, grid));

    /// <summary>Paint a road along a centreline into the terrain's ground texture. The curve is the editor's own
    /// centripetal Catmull-Rom, so it matches what the Road tool would draw. When attached the patch goes out as an
    /// ATLAS op and appears live; headlessly there is no atlas to paint, so it reports that instead of pretending.
    /// </summary>
    public (int Samples, float Length, int PatchW, int PatchH) PaintRoad(
        IReadOnlyList<(float X, float Z)> points, float width, (byte R, byte G, byte B) colour, int seed)
    {
        if (points.Count < 2) throw new ArgumentException("a road needs at least two points");
        float half = MathF.Max(width, 0.5f) * 0.5f;

        var ctrl = points.Select(p => (p.X, HeightAt(p.X, p.Z), p.Z, half)).ToList();
        var samples = RoadSpline.Resample(ctrl, MathF.Max(half * 0.5f, 1f));
        if (samples.Count == 0) throw new InvalidOperationException("the road curve came out empty");

        var patch = RoadRaster.Paint(samples, colour, pixelsPerMetre: 2f, seed: seed, worldSize: Cfg.WorldSize);
        if (_live is null)
            throw new InvalidOperationException(
                "painting the ground needs a running editor - attach_editor first. The atlas is built by the " +
                "editor from the level's terrain tiles, so there is nothing to paint into headlessly.");

        _live.SendWorldOp(RoadRaster.ToWire(patch));
        float len = samples[^1].ArcLen;
        return (samples.Count, len, patch.Width, patch.Height);
    }

    /// <summary>Send a changed heightmap rect to the editor, or mark it for saving when running headlessly. Every
    /// terrain edit goes through here so they all reach other people in the session the same way - which is the
    /// whole point of routing through the collab op rather than writing the heightmap and hoping.</summary>
    private void ShipTerrain(int x0, int y0, int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        if (_live is not null) _live.SendWorldOp($"TERRAIN {x0} {y0} {w} {h} {MountainGenerator.EncodeRect(Hm, x0, y0, w, h)}");
        else HeightDirty = true;
    }

    /// <summary>Level a patch of ground, easing back into the terrain at its edge.</summary>
    public (float Height, int Cells) FlattenArea(float cx, float cz, float radius, float? target, float skirt)
    {
        var (x0, y0, w, h) = TerrainShaper.Flatten(Hm, Cfg, cx, cz, radius, target, skirt);
        if (w == 0 || h == 0) throw new ArgumentException("that area is empty or off the map");
        ShipTerrain(x0, y0, w, h);
        return (SiteFinder.HeightAt(Hm, Cfg, cx, cz), w * h);
    }

    /// <summary>Take the lumps out of a patch of ground.</summary>
    public int SmoothArea(float cx, float cz, float radius, int passes, float strength)
    {
        var (x0, y0, w, h) = TerrainShaper.Smooth(Hm, Cfg, cx, cz, radius, passes, strength);
        if (w == 0 || h == 0) throw new ArgumentException("that area is empty or off the map");
        ShipTerrain(x0, y0, w, h);
        return w * h;
    }

    /// <summary>Cut a channel along a path - a pass for a road through a ridge, or a wadi.</summary>
    public int CarveChannel(IReadOnlyList<(float X, float Z)> path, float width, float depth, float skirt)
    {
        var (x0, y0, w, h) = TerrainShaper.CarveChannel(Hm, Cfg, path, width, depth, skirt);
        if (w == 0 || h == 0) throw new ArgumentException("that path is empty or off the map");
        ShipTerrain(x0, y0, w, h);
        return w * h;
    }

    // ---- Gameplay (control points, vehicle spawners, soldier spawns) ----
    //
    // The protocol syncs gameplay as FULL STATE: whatever is sent REPLACES the layer on every peer. So every edit
    // here is a read-modify-write against the newest layer the editor has sent us, never against a copy we built
    // ourselves - building our own would delete whatever the human had done. The read is taken as late as possible
    // to keep the window small; a human editing gameplay in the same instant can still lose that one edit, which
    // is inherent to a last-writer-wins full-state channel rather than something this can paper over.

    /// <summary>The gameplay layer as the editor last reported it. Live mode only.</summary>
    private EditableGameplay LiveGameplay()
    {
        if (_live is null)
            throw new InvalidOperationException("editing gameplay needs a running editor - attach_editor first");
        var gp = new EditableGameplay(GameplayObjects.Empty);
        var text = _live.GameplayText;
        if (text is null)
            throw new InvalidOperationException(
                "the editor has not sent its gameplay layer yet. It is replayed on connect, so re-run attach_editor; " +
                "if it still does not arrive the level may genuinely have no gameplay objects.");
        GameplaySync.Apply(gp, text);
        return gp;
    }

    private void ShipGameplay(EditableGameplay gp)
    {
        var text = GameplaySync.Serialize(gp);
        _live!.SendWorldOp("GAMEPLAY " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>What is in the gameplay layer right now.</summary>
    public (List<ControlPointDef> Cps, List<VehicleSpawnDef> Vss, List<SoldierSpawnDef> Sss) Gameplay()
    {
        var gp = LiveGameplay();
        return (gp.ControlPoints.ToList(), gp.VehicleSpawns.ToList(), gp.SoldierSpawns.ToList());
    }

    public int AddControlPoint(string name, float x, float z, float radius, int team, int spawnGroupId,
                               int objectSpawnerId, string controlPointName)
    {
        var gp = LiveGameplay();
        int i = gp.Add(GpKind.ControlPoint, new ControlPointDef(
            name, new Vec3(x, HeightAt(x, z), z), radius, spawnGroupId,
            Team: team, AreaValue: 25, ConversionTime: 40,
            ControlPointName: controlPointName.Length > 0 ? controlPointName : name,
            ObjectSpawnerId: objectSpawnerId));
        ShipGameplay(gp);
        return i;
    }

    public int AddVehicleSpawn(string name, float x, float z, float yaw, string vehicle, int team, int osId)
    {
        var gp = LiveGameplay();
        int i = gp.Add(GpKind.Vehicle, new VehicleSpawnDef(
            name, new Vec3(x, HeightAt(x, z), z), new Vec3(yaw, 0f, 0f), vehicle, osId,
            Vehicle1: vehicle, Vehicle2: vehicle, Team: team));
        ShipGameplay(gp);
        return i;
    }

    public int AddSoldierSpawn(string name, float x, float z, float yaw, int group)
    {
        var gp = LiveGameplay();
        int i = gp.Add(GpKind.Soldier, new SoldierSpawnDef(
            name, new Vec3(x, HeightAt(x, z), z), new Vec3(yaw, 0f, 0f), Group: group));
        ShipGameplay(gp);
        return i;
    }

    public void MoveGameplay(GpKind kind, int index, float x, float z, float? y)
    {
        var gp = LiveGameplay();
        if (index < 0 || index >= gp.CountOf(kind)) throw new ArgumentException($"no {kind} at index {index}");
        gp.SetPos(kind, index, new Vec3(x, y ?? HeightAt(x, z), z));
        ShipGameplay(gp);
    }

    public void RotateGameplay(GpKind kind, int index, float yaw)
    {
        var gp = LiveGameplay();
        if (index < 0 || index >= gp.CountOf(kind)) throw new ArgumentException($"no {kind} at index {index}");
        gp.SetRotation(kind, index, new Vec3(yaw, 0f, 0f));
        ShipGameplay(gp);
    }

    public void DeleteGameplay(GpKind kind, int index)
    {
        var gp = LiveGameplay();
        if (index < 0 || index >= gp.CountOf(kind)) throw new ArgumentException($"no {kind} at index {index}");
        var cps = gp.ControlPoints.ToList(); var vss = gp.VehicleSpawns.ToList(); var sss = gp.SoldierSpawns.ToList();
        switch (kind)
        {
            case GpKind.ControlPoint: cps.RemoveAt(index); break;
            case GpKind.Vehicle: vss.RemoveAt(index); break;
            default: sss.RemoveAt(index); break;
        }
        gp.ReplaceAll(cps, vss, sss);
        ShipGameplay(gp);
    }

    public void SetWaterLevel(float meters)
    {
        Cfg.WaterLevel = meters;
        // The relay carries water level as a world op, so a live change moves the editor's water plane too.
        if (_live is not null) { _live.SendWorldOp("WATER " + meters.ToString("0.######", CultureInfo.InvariantCulture)); return; }
        ConfigDirty = true;
    }

    public bool Undo() { if (_live is not null) return _live.Undo() >= 0; bool ok = _history.Undo(); if (ok) ObjectsDirty = true; return ok; }
    public bool Redo() { if (_live is not null) return _live.Redo() >= 0; bool ok = _history.Redo(); if (ok) ObjectsDirty = true; return ok; }
    public int UndoDepth => _live?.UndoDepth ?? _history.UndoDepth;
    public int RedoDepth => _live?.RedoDepth ?? _history.RedoDepth;

    public IReadOnlyList<string> PlacedTemplates()
        => So.Objects.Select(o => o.Template).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Save the edits, writing back only what actually changed (objects / terrain scalars). A level opened
    /// from a folder is written IN PLACE as loose files - that is what the editor and the game both read from a
    /// project - while an archive is repacked to <paramref name="outPath"/> leaving the base untouched. Passing a
    /// path for a folder session is an error rather than a silent no-op, because the two are not interchangeable.</summary>
    public List<string> Save(string? outPath)
    {
        // In live mode the editor owns the document and the file on disk; saving from here would race it and write
        // a copy that the editor would then overwrite. Ctrl+S in the editor is the save.
        if (_live is not null)
            throw new InvalidOperationException("attached to a running editor - save from the editor (Ctrl+S); it owns the file");

        if (IsFolder)
            return LevelSaver.SaveFolder(SourceRfa,
                ObjectsDirty ? So : null, null, HeightDirty ? Hm : null, null, null,
                terrainConfig: ConfigDirty ? Cfg : null);

        if (string.IsNullOrWhiteSpace(outPath))
            throw new ArgumentException("saving an .rfa needs an output path (the base archive is never overwritten)");
        return LevelSaver.RepackToRfa(SourceRfa, outPath,
            ObjectsDirty ? So : null, HeightDirty ? Hm : null, null, null,
            terrainConfig: ConfigDirty ? Cfg : null);
    }
}
