using System.Numerics;
using RefractorForge.Formats.Rfa;

namespace RefractorForge.Render;

/// <summary>
/// Resolves StaticObjects.con object templates to real StandardMesh geometry pulled from the game's
/// RFA archives (objects.rfa / standardMesh.rfa), and caches the flattened result per template.
///
/// <para>Object templates name a logical object (e.g. <c>C01F_Trees_M1</c>, <c>O_NVAmedicbox</c>);
/// the geometry lives in a <c>.sm</c> file whose name usually matches the template, sometimes with an
/// <c>_m1</c>/<c>_m2</c> LOD suffix. Several templates are deliberately mesh-less (sound/effect
/// emitters like <c>Flies1</c>, logical supply/repair points); for those <see cref="TryGet"/> returns
/// <c>false</c> and the caller falls back to a proxy box.</para>
/// </summary>
public sealed class MeshLibrary
{
    private readonly List<RfaArchive> _archives = new();
    private readonly Dictionary<string, RfaEntry> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RfaEntry> _treeByName = new(StringComparer.OrdinalIgnoreCase);   // BF1942 .tm tree meshes (basename, no ext)
    private readonly Dictionary<string, RfaEntry> _rsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _rsOverrideFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Mesh?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RfaEntry> _vehicleCons = new();                  // .con files under .../Vehicles/
    private readonly List<RfaEntry> _conEntries = new();                   // ALL .con files (for object-template geometry)
    private Dictionary<string, string>? _objGeom;                          // ObjectTemplate name -> geometry name (lazy)
    private Dictionary<string, string>? _geomFile;                         // GeometryTemplate alias -> .sm file (lazy)
    private Dictionary<string, ConTemplate>? _allTemplates;               // ALL ObjectTemplates by name, across archives (lazy)
    private Dictionary<string, string>? _pcoFolder;                       // PlayerControlObject name -> its vehicle folder (lazy, mod-first)
    private readonly Dictionary<string, VehiclePart[]?> _vehCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _categoryOf = new(StringComparer.OrdinalIgnoreCase);  // object stem -> category label
    private TextureLibrary? _textures;

    /// <summary>One material's triangles, its shader-derived color (fallback/tint), and its decoded
    /// texture if the texture archive is available.</summary>
    public sealed record MaterialPart(int[] Indices, Vector3 Color, Texture2D? Texture, bool AlphaTest, bool Blend = false);

    /// <summary>A flattened, render-ready mesh: engine-space positions, per-vertex UVs, and per-material parts.</summary>
    public sealed record Mesh(Vector3[] Positions, System.Numerics.Vector2[] Uvs, MaterialPart[] Parts)
    {
        /// <summary>Per-vertex 2nd UV (baked-object-lightmap UV), parallel to <see cref="Positions"/>; null when the
        /// mesh carries no lightmap channel. The editor samples the level's ObjectLightMaps/*.tga with these.</summary>
        public System.Numerics.Vector2[]? LightmapUvs { get; init; }
        public int Triangles { get { int n = 0; foreach (var p in Parts) n += p.Indices.Length / 3; return n; } }
    }

    /// <summary>Attach a texture library so materials resolve to real bitmaps (sampled via the mesh UVs).</summary>
    public void AttachTextures(TextureLibrary? textures) => _textures = textures;

    /// <summary>The attached object-texture library (for resolving extra textures like skybox cubemap faces).</summary>
    public TextureLibrary? Textures => _textures;

    /// <summary>Inject a runtime-built mesh under a template name (e.g. an imported .obj) so <see cref="TryGet"/>
    /// resolves it exactly like an archive mesh — the editor can then render + place it without repacking.</summary>
    public void AddMesh(string template, Mesh mesh) => _cache[template] = mesh;

    /// <summary>Flatten an imported OBJ into a render-ready <see cref="Mesh"/>: one combined vertex array plus one
    /// material part per OBJ material. <paramref name="resolve"/> supplies each material's colour + optional texture
    /// (from its .mtl); without it, parts get a neutral tint so the mesh still reads against the terrain.</summary>
    public static Mesh MeshFromObj(RefractorForge.Formats.Mesh.ObjMesh obj, System.Func<string, (Vector3 Color, Texture2D? Tex)>? resolve = null)
    {
        var pos = new List<Vector3>();
        var uvs = new List<System.Numerics.Vector2>();
        var parts = new List<MaterialPart>();
        foreach (var s in obj.SubMeshes)
        {
            int b = pos.Count;
            for (int i = 0; i < s.Positions.Count; i++)
            {
                pos.Add(new Vector3(s.Positions[i].X, s.Positions[i].Y, s.Positions[i].Z));
                uvs.Add(new System.Numerics.Vector2(s.Uvs[i].U, s.Uvs[i].V));
            }
            var idx = new int[s.Faces.Count * 3];
            int k = 0;
            foreach (var (fa, fb, fc) in s.Faces) { idx[k++] = b + fa; idx[k++] = b + fb; idx[k++] = b + fc; }
            var (color, tex) = resolve?.Invoke(s.Material) ?? (new Vector3(0.72f, 0.74f, 0.78f), (Texture2D?)null);
            parts.Add(new MaterialPart(idx, color, tex, false));
        }
        return new Mesh(pos.ToArray(), uvs.ToArray(), parts.ToArray());
    }

    /// <summary>Flatten a Battlefield 1942 TreeMesh (.tm, from treeMesh.rfa) into a render-ready <see cref="Mesh"/>:
    /// one combined vertex array + one material part per group/material. Trunk (group 1) is opaque; leaf/sprite/extra
    /// groups render alpha-tested (cutout). <paramref name="resolve"/> supplies each material's colour + texture
    /// (its texname into texture.rfa); without it, parts get a foliage-green tint so the tree still reads.</summary>
    public static Mesh MeshFromTreeMesh(RefractorForge.Formats.Rfa.TreeMesh tm, System.Func<string, (Vector3 Color, Texture2D? Tex)>? resolve = null)
    {
        var pos = new List<Vector3>(tm.Vertices.Length);
        var uvs = new List<System.Numerics.Vector2>(tm.Vertices.Length);
        foreach (var v in tm.Vertices)
        {
            pos.Add(new Vector3(v.Px, v.Py, v.Pz));
            uvs.Add(new System.Numerics.Vector2(v.U, v.V));
        }
        var parts = new List<MaterialPart>();
        // Partition the index buffer by ASCENDING material Start. A BF1942 .tm material's true triangle range is
        // [Start, nextMaterialStart) (the last runs to Indices.Length). The per-material Count is correct ONLY for the
        // opaque TRUNK groups and badly UNDER-reports the leaf/sprite groups, so the old Count*3 range dropped most of
        // the canopy and trees rendered as bare trunks. Materials across all 4 groups are stored ascending and together
        // partition the whole buffer, so this draws every leaf/trunk triangle exactly once. (THE bald-tree fix.)
        var mats = new List<(int Group, RefractorForge.Formats.Rfa.TreeMesh.Material M)>();
        for (int g = 0; g < tm.Groups.Length; g++) foreach (var m in tm.Groups[g]) mats.Add((g, m));
        mats.Sort((x, y) => x.M.Start.CompareTo(y.M.Start));
        // SPRITE billboard half-size: the .tm stores sprites as zero-size points (no size field), so derive a plausible
        // leaf-cluster size from the tree's footprint.
        float spriteH = Math.Clamp(MathF.Max(tm.Max.X - tm.Min.X, tm.Max.Z - tm.Min.Z) * 0.12f, 1.5f, 4f);
        for (int i = 0; i < mats.Count; i++)
        {
            var m = mats[i].M;
            int s = m.Start;
            int e = (i + 1 < mats.Count) ? mats[i + 1].M.Start : tm.Indices.Length;
            if (s < 0 || e > tm.Indices.Length || e <= s) continue;
            var (color, tex) = resolve?.Invoke(m.TexName) ?? (new Vector3(0.36f, 0.55f, 0.30f), (Texture2D?)null);
            if (mats[i].Group == 2)
            {
                // SPRITE group: BF1942 stores each leaf-cluster billboard as a collapsed quad (4 coincident verts with
                // corner UVs) that the engine expands camera-facing at runtime — the editor saw zero-area triangles and
                // drew nothing (bald crown). Bake a static 3-quad "cross" (XY+ZY+XZ planes) of the cluster texture at each
                // sprite centre so the canopy reads full from any angle (not camera-facing, but a cheap static preview).
                var sidx = new List<int>();
                for (int q = s; q + 5 < e; q += 6)   // one sprite per quad (2 collapsed triangles)
                {
                    int vi = tm.Indices[q];
                    if ((uint)vi >= (uint)pos.Count) continue;
                    AppendSpriteCross(pos, uvs, sidx, pos[vi], spriteH);
                }
                if (sidx.Count > 0) parts.Add(new MaterialPart(sidx.ToArray(), color, tex, AlphaTest: true));
                continue;
            }
            int cnt = (e - s) - ((e - s) % 3);   // whole triangles only
            if (cnt <= 0) continue;
            var idx = new int[cnt];
            for (int k = 0; k < cnt; k++) idx[k] = tm.Indices[s + k];
            // Cutout PER-MATERIAL by texture (name + real alpha), not by group index (.tm groups aren't class-pure):
            // a foliage name OR a texture that actually carries transparency renders alpha-tested; opaque bark stays solid.
            bool cutout = IsCutout(m.TexName) || HasTransparency(tex);
            parts.Add(new MaterialPart(idx, color, tex, AlphaTest: cutout));
        }
        return new Mesh(pos.ToArray(), uvs.ToArray(), parts.ToArray());
    }

    // Append a VERTICAL billboard "cross" — two perpendicular UPRIGHT quads (XY + ZY planes) — of a leaf-cluster sprite,
    // centred at c with half-size h, textured 0..1. A static stand-in for BF1942's runtime camera-facing tree sprites:
    // upright like the in-game billboards and visible from any horizontal/angled view (no flat horizontal quad, which
    // read wrong from above). The quads sit on the sprite centre and extend +/-h up and down so the cluster straddles it.
    private static void AppendSpriteCross(List<Vector3> pos, List<System.Numerics.Vector2> uvs, List<int> idx, Vector3 c, float h)
    {
        void Quad(Vector3 a, Vector3 b, Vector3 d, Vector3 e2)
        {
            int o = pos.Count;
            pos.Add(a); pos.Add(b); pos.Add(d); pos.Add(e2);
            uvs.Add(new(0, 0)); uvs.Add(new(1, 0)); uvs.Add(new(1, 1)); uvs.Add(new(0, 1));
            idx.Add(o); idx.Add(o + 1); idx.Add(o + 2);
            idx.Add(o); idx.Add(o + 2); idx.Add(o + 3);
        }
        Quad(c + new Vector3(-h, -h, 0), c + new Vector3(h, -h, 0), c + new Vector3(h, h, 0), c + new Vector3(-h, h, 0)); // vertical, faces Z
        Quad(c + new Vector3(0, -h, -h), c + new Vector3(0, -h, h), c + new Vector3(0, h, h), c + new Vector3(0, h, -h)); // vertical, faces X
    }

    /// <summary>A decoded collision mesh — engine-space vertices + triangle indices, for wireframe display.</summary>
    public sealed record CollisionMesh(Vector3[] Positions, int[] Indices);
    private readonly Dictionary<string, CollisionMesh?> _colCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolve a template to its first decodable collision mesh (verts + triangles), or <c>false</c> if the
    /// mesh has no collision / it doesn't decode. Cached; uses the same name resolution as <see cref="TryGet"/>.</summary>
    public bool TryGetCollision(string template, out CollisionMesh mesh)
    {
        if (!_colCache.TryGetValue(template, out var cached)) { cached = BuildCollision(template); _colCache[template] = cached; }
        mesh = cached!;
        return cached is not null;
    }

    private CollisionMesh? BuildCollision(string template)
    {
        var entry = Resolve(template) ?? (LodStem(template) is { } stem ? Resolve(stem) : null);
        return entry is null ? null : BuildCollisionFromEntry(entry);
    }

    // Decode a resolved .sm entry's first usable collision section. Cached per entry so a vehicle that reuses the same
    // wheel/tread .sm across parts decodes it once.
    private readonly Dictionary<RfaEntry, CollisionMesh?> _colEntryCache = new();
    private CollisionMesh? BuildCollisionFromEntry(RfaEntry entry)
    {
        if (_colEntryCache.TryGetValue(entry, out var hit)) return hit;
        CollisionMesh? result = null;
        try
        {
            byte[] bytes = OwningArchive(entry).Read(entry);
            if (StandardMesh.TryParse(bytes, out var sm) && sm is not null)
                foreach (var sec in sm.CollisionSections)
                    if (StandardMesh.TryParseCollision(sec, out var verts, out var idx) && idx.Length > 0)
                    {
                        var pos = new Vector3[verts.Length];
                        for (int i = 0; i < verts.Length; i++) pos[i] = new Vector3(verts[i].X, verts[i].Y, verts[i].Z);
                        result = new CollisionMesh(pos, idx);
                        break;
                    }
        }
        catch { result = null; }
        _colEntryCache[entry] = result;
        return result;
    }

    /// <summary>Open the given RFA archives (missing paths are skipped). Later archives do not override earlier ones.</summary>
    public static MeshLibrary Open(params string[] archivePaths)
    {
        var lib = new MeshLibrary();
        foreach (var path in archivePaths)
        {
            if (!File.Exists(path)) continue;
            if (Path.GetFileName(path).StartsWith("~")) continue;   // ~$… temp/lock leftovers (e.g. a stray ~$andardMesh.rfa)
            RfaArchive arc;
            // A corrupt / partial / non-RFA file must NOT crash the whole editor (it's loaded straight-line, outside
            // the level-load try/catch) — skip it and keep going.
            try { arc = RfaArchive.Open(path); }
            catch (Exception ex) { System.Console.WriteLine($"MeshLibrary: skipping unreadable archive '{Path.GetFileName(path)}' ({ex.GetType().Name})"); continue; }
            lib._archives.Add(arc);
            foreach (var e in arc.Entries)
            {
                lib.IndexCategory(e.Name);   // derive object->category from the archive folder structure (any mod)
                var baseName = e.Name.Replace('\\', '/');
                baseName = baseName[(baseName.LastIndexOf('/') + 1)..];   // strip path
                if (baseName.EndsWith(".sm", StringComparison.OrdinalIgnoreCase)) _ = lib._byName.TryAdd(baseName, e);
                else if (baseName.EndsWith(".tm", StringComparison.OrdinalIgnoreCase)) _ = lib._treeByName.TryAdd(baseName[..^3], e);   // BF1942 tree mesh (treeMesh.rfa)
                else if (baseName.EndsWith(".rs", StringComparison.OrdinalIgnoreCase)) _ = lib._rsByName.TryAdd(baseName, e);
                else if (baseName.EndsWith(".con", StringComparison.OrdinalIgnoreCase))
                {
                    lib._conEntries.Add(e);   // any .con may define ObjectTemplate.geometry for a static object
                    // Multi-part assemblies (vehicles, stationary/hand weapons) live in per-template folders
                    // with an Objects.con hierarchy + sibling Geometries.con; index them all for assembly.
                    var pth = e.Name.Replace('\\', '/');
                    // Match "vehicles" ANYWHERE in the path (not just "/Vehicles/") so mod folder variants like
                    // Op_Remembrance's "Objects/E_Vehicles/..." (Enhanced Vehicles) are recognized as assemblies too.
                    if (baseName.Equals("Objects.con", StringComparison.OrdinalIgnoreCase)
                        && (pth.Contains("vehicles", StringComparison.OrdinalIgnoreCase)
                            || pth.Contains("/Stationary_Weapons/", StringComparison.OrdinalIgnoreCase)
                            || pth.Contains("/HandWeapons/", StringComparison.OrdinalIgnoreCase)))
                        lib._vehicleCons.Add(e);
                }
            }
        }
        return lib;
    }

    /// <summary>Layer in a directory of <c>.rs</c> files (e.g. a level's Standardmesh/ folder) that take
    /// precedence over the archive shaders, so per-level material overrides apply.</summary>
    public void AttachShaderOverrides(string? dir)
    {
        if (dir is null || !Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, "*.rs"))
            _rsOverrideFiles[Path.GetFileName(f)] = f;
    }

    /// <summary>Number of distinct .sm files visible across the opened archives.</summary>
    public int MeshCount => _byName.Count;

    /// <summary>The .sm basenames visible across the opened archives (e.g. "sheridan_m1.sm").</summary>
    public IEnumerable<string> MeshBaseNames => _byName.Keys;

    /// <summary>Template names that resolve by multi-part assembly — the folder names under
    /// /Vehicles/, /Stationary_Weapons/, /HandWeapons/ (e.g. "Sheridan", "Stationary_M60"). These don't
    /// appear as a single .sm, so a catalog built from mesh names alone would miss them.</summary>
    public IEnumerable<string> AssembledTemplateNames
    {
        get
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in _vehicleCons)
            {
                var segs = e.Name.Replace('\\', '/').Split('/');
                if (segs.Length < 2) continue;
                var folder = segs[^2];
                // Skip Objects.con that sit in a generic sub-folder (e.g. .../Stationary_M60/Ai/Objects.con) — that
                // folder name ("Ai") is not a real template, just an AI-definition holder.
                if (_genericFolders.Contains(folder)) continue;
                if (seen.Add(folder)) yield return folder;
            }
        }
    }

    /// <summary>Object name (LOD-stripped, lower-cased) -> a friendly category label, derived purely from the
    /// archive folder structure (e.g. <c>objects/Vehicles/Land/M113/...</c> -> "Land Vehicles"). Works for any
    /// BF1942/BFV mod without a hand-maintained catalog; unknown folders become their own category.</summary>
    public IReadOnlyDictionary<string, string> CategoryOf => _categoryOf;

    private static readonly HashSet<string> _genericFolders = new(StringComparer.OrdinalIgnoreCase)
    { "art", "meshes", "mesh", "textures", "texture", "tex", "common", "sounds", "sound", "ai", "animations", "anim", "lods", "lod" };

    private static string StemName(string n) =>
        System.Text.RegularExpressions.Regex.Replace(n, @"_(?:m\d+|l\d+|lod\d+)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Map an archive folder (the segment under <c>objects/</c>, plus a Vehicles subtype) to a display
    /// category. Unknown folders pass through title-cased so a mod's own folders still group sensibly.</summary>
    public static string CategoryLabel(string folder) => folder.ToLowerInvariant() switch
    {
        "vehicles/land" => "Land Vehicles",
        "vehicles/air" => "Air Vehicles",
        "vehicles/sea" => "Water Vehicles",
        "vehicles" => "Vehicles",
        "stationary_weapons" => "Stationary Weapons",
        "handweapons" => "Hand Weapons",
        "vegetation" => "Vegetation",
        "overgrowth" => "Overgrowth",
        "undergrowth" => "Undergrowth",
        "effects" => "Effects",
        "soldiers" => "Soldiers",
        "c99_meshes" => "Destructibles",
        "items" => "Pickups",
        "buildings" => "Structures",
        "common" => "Misc",
        "move_files" => "Misc",
        var f when f.StartsWith("objects_") => "Structures",
        _ => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(folder.Replace('_', ' ').Replace('/', ' ')),
    };

    // Parse one entry path; if it sits under an "objects" root, record its object folder -> category.
    private void IndexCategory(string entryName)
    {
        var segs = entryName.Replace('\\', '/').Split('/');
        int oi = -1;
        for (int i = 0; i < segs.Length; i++) if (segs[i].Equals("objects", StringComparison.OrdinalIgnoreCase)) { oi = i; break; }
        if (oi < 0 || oi + 2 >= segs.Length) return;

        string folder = segs[oi + 1];
        int objIdx = oi + 2;
        // Vehicles split into Land/Air/Sea one level deeper.
        if (folder.Equals("Vehicles", StringComparison.OrdinalIgnoreCase) && oi + 3 < segs.Length)
        { folder = "Vehicles/" + segs[oi + 2]; objIdx = oi + 3; }
        // Step past generic sub-folders (Common, Art, ...) to reach the actual object folder.
        while (objIdx < segs.Length - 1 && _genericFolders.Contains(segs[objIdx])) objIdx++;
        if (objIdx >= segs.Length) return;

        string objName = segs[objIdx];
        if (objName.Length == 0 || _genericFolders.Contains(objName) || objName.Contains('.')) return;   // skip files / generic
        var key = StemName(objName).ToLowerInvariant();
        if (key.Length > 0 && !_categoryOf.ContainsKey(key)) _categoryOf[key] = CategoryLabel(folder);
    }

    /// <summary>
    /// Resolve a template to flattened LOD0 geometry, or <c>false</c> if no mesh matches.
    /// Results (including misses) are cached, so repeated lookups for the same template are free.
    /// </summary>
    public bool TryGet(string template, out Mesh mesh)
    {
        if (!_cache.TryGetValue(template, out var cached))
        {
            cached = Build(template);
            _cache[template] = cached;
        }
        mesh = cached!;
        return cached is not null;
    }

    /// <summary>Resolve a template's DISPLAY mesh consistently everywhere: the full assembled vehicle hierarchy
    /// FIRST (whole hull+turret+wheels / the entire car), else a single StandardMesh. Placed objects (GlObjects),
    /// the model viewer and previews all call this, so a placed vehicle never falls back to a low-detail single mesh
    /// while the model viewer shows the full assembly — the Yamato/PrinceOW "placed = low LOD" mismatch came from
    /// the placement path trying TryGet (a single .sm) FIRST and short-circuiting the assembly.</summary>
    public bool TryGetRenderMesh(string template, out Mesh mesh)
    {
        if (TryGetAssembledMesh(template, out var a) && a is not null) { mesh = a; return true; }
        // Generic multi-part STATIC objects (a Bundle/LodObject with child templates) BEFORE the single-mesh path. A
        // Bundle root that ALSO carries its own ObjectTemplate.geometry — e.g. the galleon rGalleonAnchored_Hull = hull
        // + ~30 addTemplate masts/sails/rigging/railings, or Bocage's landrep1_supply repair depot — would otherwise
        // short-circuit to TryGet (the root's hull-only .sm) and DROP every child part. TryGetStaticAssembled self-guards
        // (returns false for a plain geometry-only object with no children -> falls through to the cheap TryGet path),
        // and AssembleTemplate includes the root's own geometry plus each LodObject's first alternative, so plain
        // buildings and LOD objects are unchanged; only multi-part Bundles gain their missing pieces.
        if (TryGetStaticAssembled(template, out var c) && c is not null) { mesh = c; return true; }
        if (TryGet(template, out var b) && b is not null) { mesh = b; return true; }
        mesh = null!;
        return false;
    }

    /// <summary>One geometry of an assembled vehicle: a resolved mesh and its transform relative to the
    /// vehicle origin (parts are positioned/rotated by the vehicle's Objects.con hierarchy).</summary>
    public sealed record VehiclePart(Mesh Mesh, Matrix4x4 Local);

    /// <summary>
    /// Assemble a complete vehicle from its <c>Objects.con</c> part hierarchy: walk the
    /// <c>create</c>/<c>geometry</c>/<c>addTemplate</c>/<c>setPosition</c>/<c>setRotation</c> tree,
    /// accumulating transforms, and collect every part's mesh at its place on the vehicle (hull, turret,
    /// barrel, wheels, treads…). Returns false if the vehicle's con isn't found or yields no geometry.
    /// LOD alternatives (<c>LodObject</c>: Complex/Simple/Wreck) take the FIRST (highest-detail) child only.
    /// </summary>
    public bool TryAssembleVehicle(string vehicle, out VehiclePart[] parts)
    {
        parts = Array.Empty<VehiclePart>();
        if (string.IsNullOrWhiteSpace(vehicle)) return false;
        if (_vehCache.TryGetValue(vehicle, out var cached)) { parts = cached ?? Array.Empty<VehiclePart>(); return cached is { Length: > 0 }; }

        VehiclePart[]? result = null;
        if (TryLoadVehicleHierarchy(vehicle, out var templates, out var geoFiles, out var rootName))
        {
            try
            {
                var acc = new List<VehiclePart>();
                AssembleTemplate(templates, geoFiles, rootName, Matrix4x4.Identity, acc, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0);
                if (acc.Count > 0) result = acc.ToArray();
            }
            catch { result = null; }
        }
        _vehCache[vehicle] = result;
        parts = result ?? Array.Empty<VehiclePart>();
        return result is { Length: > 0 };
    }

    /// <summary>Locate a vehicle/weapon's part hierarchy: find its folder's Objects.con, parse the cons DIRECTLY in
    /// that folder (Objects/Geometries/Weapons.con; sub-dir cons like Ai/, Sounds/ are EXCLUDED so an Ai/Objects.con
    /// can neither pollute the template list nor — sorting first in the archive — hijack the root pick), and choose the
    /// root template (folder-named, else the main Objects.con's first create, else templates[0]). Shared by the render
    /// (<see cref="TryAssembleVehicle"/>) and collision (<see cref="TryAssembleVehicleCollision"/>) walkers.</summary>
    private bool TryLoadVehicleHierarchy(string vehicle, out List<ConTemplate> templates, out Dictionary<string, string> geoFiles, out string rootName)
    {
        templates = new(); geoFiles = new(StringComparer.OrdinalIgnoreCase); rootName = vehicle;
        RfaEntry? conEntry = null;
        string vlow = vehicle.ToLowerInvariant();
        foreach (var e in _vehicleCons)
        {
            var segs = e.Name.Replace('\\', '/').Split('/');
            if (segs.Length >= 2 && segs[^2].Equals(vehicle, StringComparison.OrdinalIgnoreCase)) { conEntry = e; break; }
        }
        if (conEntry is null)
            foreach (var e in _vehicleCons)
                if (e.Name.Replace('\\', '/').ToLowerInvariant().Contains("/" + vlow + "/")) { conEntry = e; break; }
        // (3) A con that DEFINES a PlayerControlObject named `vehicle` even though its FOLDER has a different name:
        // interstate places stratosvigi/stratosking, both defined INSIDE the single stratos/ folder (no folder is
        // named after them), and the base mustang plane lives in the Air/Mustang folder. Without this, those placed
        // templates assemble to nothing -> a proxy box ("custom vehicles not loading"). First-wins (mod archives
        // first) so a mod's redefinition shadows the base.
        if (conEntry is null)
        {
            EnsurePcoFolders();
            if (_pcoFolder!.TryGetValue(vehicle, out var fdir))
                conEntry = _vehicleCons.FirstOrDefault(e =>
                {
                    var p = e.Name.Replace('\\', '/'); int s = p.LastIndexOf('/');
                    return s >= 0 && p[..s].Equals(fdir, StringComparison.OrdinalIgnoreCase);
                });
        }
        if (conEntry is null) return false;
        try
        {
            string dir = conEntry.Name.Replace('\\', '/');
            dir = dir[..dir.LastIndexOf('/')];                  // the vehicle folder
            var sb = new System.Text.StringBuilder();
            foreach (var arc in _archives)
                foreach (var e in arc.Entries)
                {
                    var en = e.Name.Replace('\\', '/');
                    if (!en.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase) || !en.EndsWith(".con", StringComparison.OrdinalIgnoreCase)) continue;
                    if (en.IndexOf('/', dir.Length + 1) >= 0) continue;   // only cons directly in the folder, not sub-dirs
                    sb.Append(System.Text.Encoding.Latin1.GetString(arc.Read(e))).Append('\n');
                }
            var text = sb.ToString();
            templates = ParseConTemplates(text);
            geoFiles = ParseGeometryFiles(text);
            // Root template: the exact template named `vehicle` (so a placed stratosvigi assembles AS stratosvigi);
            // else the folder's main spawnable (its first PlayerControlObject) — NOT the first `create`, which is
            // often a tiny helper SimpleObject (interstate's stratos folder leads with a 64-tri headlight glow, which
            // is exactly why the model viewer showed a blob); else the first template.
            rootName = templates.FirstOrDefault(t => t.Name.Equals(vehicle, StringComparison.OrdinalIgnoreCase))?.Name
                    ?? templates.FirstOrDefault(t => t.Type.Equals("PlayerControlObject", StringComparison.OrdinalIgnoreCase))?.Name
                    ?? FirstTemplateName(conEntry)
                    ?? (templates.Count > 0 ? templates[0].Name : vehicle);
            return templates.Count > 0;
        }
        catch { return false; }
    }

    private readonly Dictionary<string, (CollisionMesh Col, Matrix4x4 Local)[]?> _vehColCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Assemble a vehicle's per-part COLLISION meshes (decoded .sm collision sections) with each part's local
    /// transform, mirroring <see cref="TryAssembleVehicle"/>'s hierarchy walk. Parts whose .sm has no collision section
    /// are skipped. Returns false if the vehicle won't assemble or no part carries collision. Cached per vehicle.</summary>
    public bool TryAssembleVehicleCollision(string vehicle, out (CollisionMesh Col, Matrix4x4 Local)[] parts)
    {
        parts = Array.Empty<(CollisionMesh, Matrix4x4)>();
        if (string.IsNullOrWhiteSpace(vehicle)) return false;
        if (_vehColCache.TryGetValue(vehicle, out var cached))
        { parts = cached ?? Array.Empty<(CollisionMesh, Matrix4x4)>(); return cached is { Length: > 0 }; }

        (CollisionMesh, Matrix4x4)[]? result = null;
        if (TryLoadVehicleHierarchy(vehicle, out var templates, out var geoFiles, out var rootName))
        {
            try
            {
                var acc = new List<(CollisionMesh, Matrix4x4)>();
                AssembleCollision(templates, geoFiles, rootName, Matrix4x4.Identity, acc, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0);
                if (acc.Count > 0) result = acc.ToArray();
            }
            catch { result = null; }
        }
        _vehColCache[vehicle] = result;
        parts = result ?? Array.Empty<(CollisionMesh, Matrix4x4)>();
        return result is { Length: > 0 };
    }

    /// <summary>Collision counterpart of <see cref="AssembleTemplate"/>: walks the same part tree but accumulates each
    /// part's decoded COLLISION mesh (resolving geometry the SAME way the render walker does) + its world-local transform.</summary>
    private void AssembleCollision(List<ConTemplate> all, Dictionary<string, string> geoFiles, string name,
        Matrix4x4 parent, List<(CollisionMesh, Matrix4x4)> acc, HashSet<string> visiting, int depth)
    {
        if (depth > 24 || !visiting.Add(name)) return;
        var tpl = FindTemplate(all, name);
        if (tpl is not null)
        {
            if (tpl.Geometry is { Length: > 0 } g)
            {
                string meshName = geoFiles.TryGetValue(g, out var file) ? file : g;
                var entry = Resolve(meshName) ?? (meshName != g ? Resolve(g) : null);
                if (entry is not null && BuildCollisionFromEntry(entry) is { } cm) acc.Add((cm, parent));
            }
            var children = tpl.Children;
            if (tpl.IsLod && children.Count > 0) children = new() { children[0] };   // first LOD only — match render
            foreach (var (child, pos, rot) in children)
            {
                var local = Matrix4x4.CreateFromYawPitchRoll(Rad(rot.X), Rad(rot.Y), Rad(rot.Z))
                          * Matrix4x4.CreateTranslation(pos.X, pos.Y, pos.Z) * parent;
                AssembleCollision(all, geoFiles, child, local, acc, visiting, depth + 1);
            }
        }
        visiting.Remove(name);
    }

    private readonly Dictionary<string, (CollisionMesh Col, Matrix4x4 Local)[]?> _staticColCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Collision counterpart of <see cref="TryAssembleStatic"/>: assemble a generic multi-part Bundle/LodObject's
    /// per-part collision from the GLOBAL registry (works for vehicles/objects defined OUTSIDE the /Vehicles/ folders —
    /// e.g. a custom map's <c>objects/&lt;name&gt;/</c> vehicle). Returns false for a single-geometry template (handled by
    /// <see cref="TryGetCollision"/>). Cached.</summary>
    public bool TryAssembleStaticCollision(string template, out (CollisionMesh Col, Matrix4x4 Local)[] parts)
    {
        parts = Array.Empty<(CollisionMesh, Matrix4x4)>();
        if (string.IsNullOrWhiteSpace(template)) return false;
        if (_staticColCache.TryGetValue(template, out var cached)) { parts = cached ?? Array.Empty<(CollisionMesh, Matrix4x4)>(); return cached is { Length: > 0 }; }
        EnsureAllTemplates();
        EnsureObjectGeometry();
        (CollisionMesh, Matrix4x4)[]? result = null;
        if (_allTemplates is not null && _allTemplates.TryGetValue(template, out var root) && (root.IsLod || root.Children.Count > 0))
        {
            try
            {
                var all = _allTemplates.Values.ToList();
                var acc = new List<(CollisionMesh, Matrix4x4)>();
                AssembleCollision(all, _geomFile!, template, Matrix4x4.Identity, acc, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0);
                if (acc.Count > 0) result = acc.ToArray();
            }
            catch { result = null; }
        }
        _staticColCache[template] = result;
        parts = result ?? Array.Empty<(CollisionMesh, Matrix4x4)>();
        return result is { Length: > 0 };
    }

    /// <summary>Resolve a placed/spawned template's COLLISION the SAME comprehensive way <see cref="TryGetRenderMesh"/>
    /// resolves its display mesh: assembled vehicle (stock vehicle/weapon folders) → generic Bundle/LodObject (custom
    /// map vehicles in any folder) → a single-mesh collision (identity transform). So the collision overlay covers
    /// exactly what renders, regardless of where the template is defined.</summary>
    public bool TryGetRenderCollision(string template, out (CollisionMesh Col, Matrix4x4 Local)[] parts)
    {
        if (TryAssembleVehicleCollision(template, out parts) && parts.Length > 0) return true;
        if (TryAssembleStaticCollision(template, out parts) && parts.Length > 0) return true;
        if (TryGetCollision(template, out var single) && single is not null) { parts = new[] { (single, Matrix4x4.Identity) }; return true; }
        parts = Array.Empty<(CollisionMesh, Matrix4x4)>();
        return false;
    }

    private readonly Dictionary<string, Mesh?> _vehMeshCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Assemble a vehicle and FLATTEN it into a single <see cref="Mesh"/> (each part's geometry baked into
    /// vehicle-local space by its hierarchy transform). Lets a vehicle render anywhere a normal object mesh
    /// can — e.g. a vehicle template dropped as a static object. Returns false if the vehicle won't assemble.
    /// </summary>
    public bool TryGetAssembledMesh(string vehicle, out Mesh mesh)
    {
        mesh = null!;
        if (string.IsNullOrWhiteSpace(vehicle)) return false;
        if (_vehMeshCache.TryGetValue(vehicle, out var cached)) { mesh = cached!; return cached is not null; }

        Mesh? flat = TryAssembleVehicle(vehicle, out var parts) ? FlattenParts(parts) : null;
        _vehMeshCache[vehicle] = flat;
        mesh = flat!;
        return flat is not null;
    }

    /// <summary>A continuously-spinning object part (a BF1942 RotationalBundle — windmill blades, watermill wheel, mod
    /// fans/rotors): its resolved mesh, the static placement transform from the template root to the part, the local
    /// pivot, and the per-axis rotation speed (deg/s). The editor rotates it about the pivot each frame; view-only.</summary>
    public sealed record AnimatedPart(Mesh Mesh, Matrix4x4 StaticLocal, Vector3 Pivot, Vector3 SpeedDeg);
    private readonly Dictionary<string, AnimatedPart[]> _animCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The continuously-rotating parts of a placed template, or false if none. The spinning part is kept
    /// SEPARATE from the flattened static mesh (which never contains it) so the editor can draw it with a time-varying
    /// transform. Data-driven (any setContinousRotationSpeed object), so it covers windmills, watermills and mods alike.</summary>
    public bool TryGetAnimatedParts(string template, out AnimatedPart[] parts)
    {
        parts = Array.Empty<AnimatedPart>();
        if (string.IsNullOrWhiteSpace(template)) return false;
        if (_animCache.TryGetValue(template, out var cached)) { parts = cached; return cached.Length > 0; }
        EnsureAllTemplates();
        EnsureObjectGeometry();
        var acc = new List<AnimatedPart>();
        if (_allTemplates is not null && _allTemplates.ContainsKey(template))
            try { CollectAnimated(template, Matrix4x4.Identity, acc, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0); } catch { }
        var arr = acc.ToArray();
        _animCache[template] = arr;
        parts = arr;
        return arr.Length > 0;
    }

    // Walk the template hierarchy; a child that is a RotationalBundle with a non-zero speed becomes an AnimatedPart at
    // its accumulated placement (and is NOT descended into for the static flatten). Everything else recurses normally.
    private void CollectAnimated(string name, Matrix4x4 parent, List<AnimatedPart> acc, HashSet<string> visiting, int depth)
    {
        if (depth > 24 || !visiting.Add(name)) return;
        if (_allTemplates!.TryGetValue(name, out var tpl))
        {
            var children = tpl.Children;
            if (tpl.IsLod && children.Count > 0) children = new() { children[0] };
            foreach (var (child, pos, rot) in children)
            {
                var local = Matrix4x4.CreateFromYawPitchRoll(Rad(rot.X), Rad(rot.Y), Rad(rot.Z))
                          * Matrix4x4.CreateTranslation(pos.X, pos.Y, pos.Z) * parent;
                if (_allTemplates.TryGetValue(child, out var ct) && ct.IsRotational
                    && (ct.RotSpeed.X != 0f || ct.RotSpeed.Y != 0f || ct.RotSpeed.Z != 0f))
                {
                    var m = ResolveTemplateMesh(ct);
                    if (m is not null) acc.Add(new AnimatedPart(m, local, ct.Pivot, ct.RotSpeed));
                }
                else CollectAnimated(child, local, acc, visiting, depth + 1);
            }
        }
        visiting.Remove(name);
    }

    // Resolve one template to a mesh: its assembled subtree if it has children/LODs, else its own geometry (alias -> .sm).
    private Mesh? ResolveTemplateMesh(ConTemplate ct)
    {
        if ((ct.IsLod || ct.Children.Count > 0) && TryAssembleStatic(ct.Name, out var sub) && sub.Length > 0)
            return FlattenParts(sub);
        if (!string.IsNullOrEmpty(ct.Geometry))
        {
            string g = ct.Geometry!; string file = _geomFile!.TryGetValue(g, out var f) && f.Length > 0 ? f : g;
            if (TryGet(file, out var m) && m is not null) return m;
            if (TryGet(g, out m) && m is not null) return m;
        }
        return null;
    }

    /// <summary>Bake an assembled part list (each part = a mesh + its accumulated local transform) into one flat
    /// render mesh: transform every part's vertices into object space and concatenate the material parts.</summary>
    private static Mesh? FlattenParts(VehiclePart[] parts)
    {
        var pos = new List<Vector3>();
        var uvs = new List<System.Numerics.Vector2>();
        var lmuvs = new List<System.Numerics.Vector2>();   // 2nd UV (object lightmap), carried per part
        bool anyLm = false;
        var mats = new List<MaterialPart>();
        foreach (var vp in parts)
        {
            int baseV = pos.Count;
            var P = vp.Mesh.Positions;
            var L = vp.Mesh.LightmapUvs;                    // each part's baked-lightmap UVs (null if the .sm has none)
            for (int i = 0; i < P.Length; i++)
            {
                pos.Add(Vector3.Transform(P[i], vp.Local));
                uvs.Add(i < vp.Mesh.Uvs.Length ? vp.Mesh.Uvs[i] : default);
                lmuvs.Add(L is not null && i < L.Length ? L[i] : default);
            }
            if (L is not null) anyLm = true;
            foreach (var mp in vp.Mesh.Parts)
            {
                var idx = new int[mp.Indices.Length];
                for (int k = 0; k < idx.Length; k++) idx[k] = mp.Indices[k] + baseV;
                mats.Add(new MaterialPart(idx, mp.Color, mp.Texture, mp.AlphaTest, mp.Blend));
            }
        }
        return mats.Count > 0
            ? new Mesh(pos.ToArray(), uvs.ToArray(), mats.ToArray()) { LightmapUvs = anyLm ? lmuvs.ToArray() : null }
            : null;
    }

    private readonly Dictionary<string, Mesh?> _staticAsmCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Assemble a generic multi-part STATIC object (a Bundle/LodObject hierarchy defined anywhere — not just a
    /// /Vehicles/ folder) from the GLOBAL template + geometry-file registries, then flatten it. Returns false for a
    /// plain single-geometry template (those go through the cheaper <see cref="TryGet"/>). Cached.</summary>
    public bool TryGetStaticAssembled(string template, out Mesh mesh)
    {
        mesh = null!;
        if (string.IsNullOrWhiteSpace(template)) return false;
        if (_staticAsmCache.TryGetValue(template, out var cached)) { mesh = cached!; return cached is not null; }
        Mesh? flat = TryAssembleStatic(template, out var parts) ? FlattenParts(parts) : null;
        _staticAsmCache[template] = flat;
        mesh = flat!;
        return flat is not null;
    }

    /// <summary>Walk a placed template's part hierarchy from the global registry (Bundle children, LodObject first
    /// alternative, addTemplate transforms) and collect each resolvable part's mesh — the same walker the vehicle
    /// path uses, but for templates that live outside the vehicle/weapon folders. Only fires for roots that are a
    /// LodObject or that have child templates (a plain geometry-only object is left to <see cref="TryGet"/>).</summary>
    public bool TryAssembleStatic(string template, out VehiclePart[] parts)
    {
        parts = Array.Empty<VehiclePart>();
        EnsureAllTemplates();
        EnsureObjectGeometry();
        if (_allTemplates is null || !_allTemplates.TryGetValue(template, out var root)) return false;
        if (!root.IsLod && root.Children.Count == 0) return false;   // single-geometry object -> let TryGet handle it
        try
        {
            var all = _allTemplates.Values.ToList();
            var acc = new List<VehiclePart>();
            AssembleTemplate(all, _geomFile!, template, Matrix4x4.Identity, acc, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0);
            if (acc.Count > 0) { parts = acc.ToArray(); return true; }
        }
        catch { }
        return false;
    }

    /// <summary>The primary geometry MESH NAME a placed template renders as — needed to match per-instance object
    /// lightmaps, which the bake names by GEOMETRY (Bocage's <c>landrep1_supply</c> repair depot is lit by
    /// <c>landrep1_m1_&lt;pos&gt;.tga</c>, not <c>landrep1_supply_&lt;pos&gt;.tga</c>). Returns the template's own
    /// <c>ObjectTemplate.geometry</c> .sm file; else the first geometry found walking a Bundle/LodObject hierarchy; else
    /// the template name itself (normal buildings, where the placed template == the geometry == the lightmap name).</summary>
    public string PrimaryGeometryName(string template)
    {
        if (string.IsNullOrWhiteSpace(template)) return template;
        EnsureObjectGeometry();
        EnsureAllTemplates();
        string GeomFile(string g) => _geomFile!.TryGetValue(g, out var f) && f.Length > 0 ? f : g;
        if (_objGeom!.TryGetValue(template, out var direct) && direct.Length > 0) return GeomFile(direct);
        if (_allTemplates!.TryGetValue(template, out var root))
        {
            var g = FirstGeometry(root, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0);
            if (g is not null) return GeomFile(g);
        }
        return template;
    }

    // Depth-first walk for the first ObjectTemplate.geometry in a Bundle/LodObject hierarchy (LodObject -> first child).
    private string? FirstGeometry(ConTemplate tpl, HashSet<string> visiting, int depth)
    {
        if (depth > 24) return null;
        if (tpl.Geometry is { Length: > 0 } g) return g;
        var children = tpl.Children;
        if (tpl.IsLod && children.Count > 0) children = new() { children[0] };
        foreach (var (child, _, _) in children)
        {
            if (!visiting.Add(child)) continue;
            if (_allTemplates!.TryGetValue(child, out var ct))
            {
                var r = FirstGeometry(ct, visiting, depth + 1);
                if (r is not null) return r;
            }
        }
        return null;
    }

    private sealed class ConTemplate
    {
        public string Name = "";
        public string Type = "";
        public string? Geometry;
        public bool IsLod;                                     // LodObject: children are alternatives (take first)
        public bool IsRotational;                              // RotationalBundle: spins continuously about Pivot
        public Vector3 RotSpeed;                               // setContinousRotationSpeed (deg/s, per axis)
        public Vector3 Pivot;                                  // setPivotPosition (local pivot for the spin)
        public readonly List<(string Child, Vector3 Pos, Vector3 Rot)> Children = new();
    }

    /// <summary>The first ObjectTemplate.create name in a single .con entry (the main Objects.con's root), or null.</summary>
    private string? FirstTemplateName(RfaEntry conEntry)
    {
        try
        {
            var text = System.Text.Encoding.Latin1.GetString(OwningArchive(conEntry).Read(conEntry));
            var t = ParseConTemplates(text);
            return t.Count > 0 ? t[0].Name : null;
        }
        catch { return null; }
    }

    /// <summary>Parse an Objects.con into a name→template map of the part hierarchy.</summary>
    private static List<ConTemplate> ParseConTemplates(string text)
    {
        var list = new List<ConTemplate>();
        ConTemplate? cur = null;
        Vector3 pendingPos = Vector3.Zero, pendingRot = Vector3.Zero;
        bool havePending = false;
        int pendingChildIdx = -1;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("rem", StringComparison.OrdinalIgnoreCase)) continue;
            if (!line.StartsWith("ObjectTemplate.", StringComparison.OrdinalIgnoreCase)) continue;
            var rest = line.Substring("ObjectTemplate.".Length);
            var sp = rest.IndexOf(' ');
            string cmd = sp < 0 ? rest : rest.Substring(0, sp);
            string arg = sp < 0 ? "" : rest.Substring(sp + 1).Trim();

            if (cmd.Equals("create", StringComparison.OrdinalIgnoreCase))
            {
                var t = arg.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                cur = new ConTemplate { Type = t.Length > 0 ? t[0] : "", Name = t.Length > 1 ? t[1] : "" };
                cur.IsLod = cur.Type.Equals("LodObject", StringComparison.OrdinalIgnoreCase);
                cur.IsRotational = cur.Type.Equals("RotationalBundle", StringComparison.OrdinalIgnoreCase);
                list.Add(cur);
                pendingChildIdx = -1;
            }
            else if (cur is null) continue;
            else if (cmd.Equals("geometry", StringComparison.OrdinalIgnoreCase)) cur.Geometry = arg.Trim();
            else if (cmd.Equals("addTemplate", StringComparison.OrdinalIgnoreCase))
            {
                cur.Children.Add((arg.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? arg, Vector3.Zero, Vector3.Zero));
                pendingChildIdx = cur.Children.Count - 1;
            }
            else if (cmd.Equals("setPosition", StringComparison.OrdinalIgnoreCase) && pendingChildIdx >= 0)
            {
                var c = cur.Children[pendingChildIdx]; cur.Children[pendingChildIdx] = (c.Child, ParseVec(arg), c.Rot);
            }
            else if (cmd.Equals("setRotation", StringComparison.OrdinalIgnoreCase) && pendingChildIdx >= 0)
            {
                var c = cur.Children[pendingChildIdx]; cur.Children[pendingChildIdx] = (c.Child, c.Pos, ParseVec(arg));
            }
            // Continuous object rotation (RotationalBundle: windmill blades, watermill wheel, mod fans/rotors). These
            // apply to the template itself (not a child), so no pendingChildIdx guard. Note the engine's spelling
            // "Continous". View-only: surfaced via TryGetAnimatedParts and spun per-frame in the editor.
            else if (cmd.Equals("setContinousRotationSpeed", StringComparison.OrdinalIgnoreCase)) cur.RotSpeed = ParseVec(arg);
            else if (cmd.Equals("setPivotPosition", StringComparison.OrdinalIgnoreCase)) cur.Pivot = ParseVec(arg);
            else if (cmd.Equals("setObjectTemplate", StringComparison.OrdinalIgnoreCase))
            {
                // An ObjectSpawner spawns this template at runtime; for the editor we surface the SPAWNED object's mesh
                // so it appears where the spawner sits (Dystopia_City's Mario BrickBlock/QuestionBlock spawners spawn at
                // map start). Format: "setObjectTemplate <index> <templateName>"; take the trailing name, skip NULLs.
                var sp2 = arg.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                var spawned = sp2.Length > 0 ? sp2[^1] : null;
                if (!string.IsNullOrEmpty(spawned) && !spawned.Equals("NULL_OBJECT", StringComparison.OrdinalIgnoreCase)
                    && !int.TryParse(spawned, out _))
                {
                    cur.Children.Add((spawned, Vector3.Zero, Vector3.Zero));
                    pendingChildIdx = cur.Children.Count - 1;
                }
            }
        }
        _ = (pendingPos, pendingRot, havePending);
        return list;
    }

    /// <summary>Map each <c>GeometryTemplate.create StandardMesh &lt;alias&gt;</c> to its
    /// <c>GeometryTemplate.file &lt;mesh&gt;</c> (the actual .sm basename). ObjectTemplate.geometry names
    /// reference these aliases, which often differ from the mesh file (e.g. Ve_Mig17_Fus_M1 -> Ve_Mig17_Main_M1).</summary>
    private static Dictionary<string, string> ParseGeometryFiles(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? curAlias = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("GeometryTemplate.", StringComparison.OrdinalIgnoreCase)) continue;
            var rest = line.Substring("GeometryTemplate.".Length);
            var sp = rest.IndexOf(' ');
            string cmd = sp < 0 ? rest : rest.Substring(0, sp);
            string arg = sp < 0 ? "" : rest.Substring(sp + 1).Trim();
            if (cmd.Equals("create", StringComparison.OrdinalIgnoreCase))
            {
                var t = arg.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                curAlias = t.Length > 1 ? t[1] : null;            // create StandardMesh <alias>
            }
            else if (cmd.Equals("file", StringComparison.OrdinalIgnoreCase) && curAlias is not null)
            {
                if (!map.ContainsKey(curAlias)) map[curAlias] = arg.Trim();
            }
        }
        return map;
    }

    private static Vector3 ParseVec(string s)
    {
        var p = s.Split('/');
        float F(int i) => i < p.Length && float.TryParse(p[i].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 0f;
        return new Vector3(F(0), F(1), F(2));
    }

    /// <summary>Resolve a child template by name, mirroring the engine's name resolution:
    /// (1) the vehicle's own folder, (2) the GLOBAL registry of every ObjectTemplate — many vehicles share parts from a
    /// "Common" folder (Op_Remembrance's A4 loadout variants reuse A4_Common's lodA4Cockpit/…; the engine has one global
    /// namespace), and (3) the <c>setRandomGeometries N</c> pattern: <c>addTemplate X</c> + <c>setRandomGeometries N</c>
    /// means the real templates are <c>X1</c>…<c>XN</c> (the engine random-picks one) — take the first variant, the same
    /// way we take the first LOD alternative. (3) is why Op_Remembrance's Huey LOD bundles assembled to nothing.</summary>
    private ConTemplate? FindTemplate(List<ConTemplate> all, string name)
    {
        var tpl = all.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (tpl is null) { EnsureAllTemplates(); _allTemplates!.TryGetValue(name, out tpl); }
        if (tpl is null)
        {
            var v1 = name + "1";                              // setRandomGeometries: pick the first variant
            tpl = all.FirstOrDefault(t => t.Name.Equals(v1, StringComparison.OrdinalIgnoreCase));
            if (tpl is null) { EnsureAllTemplates(); _allTemplates!.TryGetValue(v1, out tpl); }
        }
        return tpl;
    }

    private void AssembleTemplate(List<ConTemplate> all, Dictionary<string, string> geoFiles, string name, Matrix4x4 parent, List<VehiclePart> acc, HashSet<string> visiting, int depth)
    {
        if (depth > 24 || !visiting.Add(name)) return;        // guard cycles / runaway depth
        var tpl = FindTemplate(all, name);
        if (tpl is not null)
        {
            if (tpl.Geometry is { Length: > 0 } g)
            {
                // Resolve the geometry: first as a GeometryTemplate alias -> .sm file, else by the name itself.
                string meshName = geoFiles.TryGetValue(g, out var file) ? file : g;
                if ((TryGet(meshName, out var m) || TryGet(g, out m)) && m is not null)
                    acc.Add(new VehiclePart(m, parent));
            }

            var children = tpl.Children;
            if (tpl.IsLod && children.Count > 0) children = new() { children[0] };   // first LOD alternative only
            foreach (var (child, pos, rot) in children)
            {
                // A continuously-rotating part (RotationalBundle, e.g. windmill blades) is drawn SEPARATELY as an animated
                // part (TryGetAnimatedParts), so skip it here — otherwise it'd be baked into the static mesh in place AND
                // drawn spinning, giving a doubled/ghosted result.
                // Use the SAME template lookup CollectAnimated/TryGetAnimatedParts uses: fall back to the GLOBAL registry
                // when the child isn't in the local `all` list. Otherwise a cross-folder rotating part (e.g. DCF mod
                // objects) is animated by TryGetAnimatedParts but NOT skipped here, so it ends up baked into the static
                // mesh AND drawn spinning — the "ghost duplicate".
                var ctc = FindTemplate(all, child);
                if (ctc is null && _allTemplates is not null && _allTemplates.TryGetValue(child, out var gctc)) ctc = gctc;
                if (ctc is not null && ctc.IsRotational && (ctc.RotSpeed.X != 0f || ctc.RotSpeed.Y != 0f || ctc.RotSpeed.Z != 0f)) continue;
                var local = Matrix4x4.CreateFromYawPitchRoll(Rad(rot.X), Rad(rot.Y), Rad(rot.Z))
                          * Matrix4x4.CreateTranslation(pos.X, pos.Y, pos.Z)
                          * parent;
                AssembleTemplate(all, geoFiles, child, local, acc, visiting, depth + 1);
            }
        }
        visiting.Remove(name);
    }

    private static float Rad(float deg) => deg * MathF.PI / 180f;

    /// <summary>
    /// Resolve a vehicle's main BODY mesh from its spawn name (e.g. "sheridan", "t54", "f4phantom").
    /// BFV vehicles are multi-part assemblies (Ve_&lt;name&gt;_Main/_body/_Fus + turret, wheels, gun, …);
    /// the parts are part-local so a full build needs the vehicle's .con hierarchy. As a useful
    /// approximation we render just the hull/fuselage (authored roughly in place), which gives the
    /// right silhouette, footprint and facing. Returns false if no body-like part is found.
    /// </summary>
    public bool TryGetVehicleBody(string vehicle, out Mesh mesh)
    {
        mesh = null!;
        if (string.IsNullOrWhiteSpace(vehicle)) return false;
        string key = "vb::" + vehicle;                       // cache under a distinct key
        if (_cache.TryGetValue(key, out var cached)) { mesh = cached!; return cached is not null; }

        // Body part naming, most-specific first: tanks/ground use _Main or _body, aircraft/boats _Fus.
        // Prefer the high-detail _m1 over _L1 (low) so the editor shows the better silhouette.
        string[] suffixes = { "_main_m1", "_body_m1", "_fus_m1", "_main", "_body", "_fus", "_hull_m1", "_hull" };
        string vlow = vehicle.ToLowerInvariant();
        RfaEntry? bestEntry = null; int bestRank = int.MaxValue;
        foreach (var kv in _byName)
        {
            string n = kv.Key.ToLowerInvariant();
            if (!n.EndsWith(".sm")) continue;
            string stem = n[..^3];                            // drop ".sm"
            // must be a part of THIS vehicle: "ve_<vehicle>..." (the Ve_ prefix is universal for vehicles)
            if (!stem.StartsWith("ve_" + vlow)) continue;
            for (int r = 0; r < suffixes.Length; r++)
                if (stem.EndsWith(suffixes[r]) && r < bestRank) { bestRank = r; bestEntry = kv.Value; break; }
        }

        Mesh? built = bestEntry is null ? null : BuildFromEntry(bestEntry);
        _cache[key] = built;
        mesh = built!;
        return built is not null;
    }

    /// <summary>True if a <c>.sm</c> file matching this template exists in the loaded archives (whether or not it
    /// parses). Lets the editor tell "missing asset — load the right .rfa" apart from "mesh found but unsupported".</summary>
    public bool HasMeshEntry(string template) => Resolve(template) is not null || ResolveTree(template) is not null;

    /// <summary>Explain WHY a template does or doesn't resolve to display geometry. Walks the same steps as
    /// <see cref="TryGetRenderMesh"/> and reports the first one that breaks, so the <c>objaudit</c> gate (and a
    /// future missing-asset tooltip) can say "the .con chain is broken HERE" instead of just "not found".</summary>
    public string Diagnose(string template)
    {
        if (TryGetAssembledMesh(template, out var am) && am is not null) return $"OK assembled ({am.Triangles} tris)";
        if (TryGet(template, out var m) && m is not null) return $"OK mesh ({m.Triangles} tris)";

        // Mirror Build()'s steps and report the first break.
        var byName = ResolveByName(template);
        if (byName is not null) return $"PARSE_FAIL: entry '{byName.Name}' found but yields no LOD0 geometry";
        EnsureObjectGeometry();
        EnsureAllTemplates();
        if (_objGeom!.TryGetValue(template, out var geom))
        {
            string file = _geomFile!.TryGetValue(geom, out var f) ? f : geom;
            var e = ResolveByName(file) ?? (!file.Equals(geom, StringComparison.OrdinalIgnoreCase) ? ResolveByName(geom) : null);
            if (e is not null) return $"PARSE_FAIL: geometry '{geom}' -> entry '{e.Name}' found but yields no geometry";
            if (ResolveTree(template) is not null) return $"PARSE_FAIL: .tm for '{template}' found but yields no geometry";
            return _geomFile.ContainsKey(geom)
                ? $"SM_MISSING: geometry '{geom}' -> file '{file}', but no matching .sm/.tm in any opened archive"
                : $"GEOM_UNMAPPED: geometry '{geom}' has no GeometryTemplate.file and no '{geom}' mesh in any opened archive";
        }
        if (_allTemplates!.TryGetValue(template, out var tpl))
        {
            if (tpl.Children.Count > 0) return $"NO_PARTS: '{tpl.Type}' with {tpl.Children.Count} child template(s), none produced geometry";
            return $"NO_GEOMETRY: '{tpl.Type}' template defines no geometry (mesh-less by design?)";
        }
        return "NO_TEMPLATE: no ObjectTemplate.create in any opened archive and no mesh matches the name";
    }

    private RfaEntry? Resolve(string template)
    {
        var e = ResolveByName(template);
        if (e is not null) return e;

        // Object templates whose name differs from the mesh (helipads, medic lockers, many gameplay-ish static
        // objects): follow ObjectTemplate.geometry -> the geometry's .sm, parsed from the archive .con files.
        EnsureObjectGeometry();
        if (_objGeom!.TryGetValue(template, out var geom))
        {
            string mesh = _geomFile!.TryGetValue(geom, out var f) ? f : geom;
            var r = ResolveByName(mesh) ?? (mesh != geom ? ResolveByName(geom) : null);
            if (r is not null) return r;
        }
        // The name may ITSELF be a GeometryTemplate alias whose .sm file is named differently (e.g. a vehicle part
        // geometry "Stryker_Hull_M1" -> file "STRYKER_hull"). This is the case when a mod overrides a vehicle's
        // Objects.con but its Geometries.con lives in a DIFFERENT archive/folder (so the vehicle-LOCAL alias map
        // misses it, and parts silently drop). _geomFile scans EVERY .con, so it maps the alias wherever it lives.
        if (_geomFile!.TryGetValue(template, out var direct) && !direct.Equals(template, StringComparison.OrdinalIgnoreCase))
            return ResolveByName(direct);
        return null;
    }

    /// <summary>Resolve a NAME directly to a .sm entry: exact + LOD-suffixed + "_off" variants + a shortest-prefix
    /// fallback. (The object-template indirection lives in <see cref="Resolve"/>.)</summary>
    private RfaEntry? ResolveByName(string t)
    {
        // GeometryTemplate.file values are often PATHS (e.g. "\DesertCombat\STRYKER\STRYKER_Hull"); the .sm index is
        // keyed by bare basename, so strip any folder prefix first or the lookup + LOD/prefix fallbacks all miss.
        t = t.Replace('\\', '/'); int sl = t.LastIndexOf('/'); if (sl >= 0) t = t[(sl + 1)..];
        foreach (var cand in new[] { t + ".sm", t + "_m1.sm", t + "_m2.sm" })
            if (_byName.TryGetValue(cand, out var e)) return e;

        if (t.Contains("_off", StringComparison.OrdinalIgnoreCase))
        {
            string stripped = t.Replace("_off", "", StringComparison.OrdinalIgnoreCase);
            foreach (var cand in new[] { stripped + ".sm", stripped + "_m1.sm", stripped + "_m2.sm" })
                if (_byName.TryGetValue(cand, out var e)) return e;
        }

        RfaEntry? best = null; int bestLen = int.MaxValue;
        foreach (var kv in _byName)
            if (kv.Key.StartsWith(t, StringComparison.OrdinalIgnoreCase) && kv.Key.Length < bestLen)
            { best = kv.Value; bestLen = kv.Key.Length; }
        return best;
    }

    /// <summary>Build the ObjectTemplate-name -> geometry and GeometryTemplate-alias -> .sm-file maps once, by
    /// scanning every archive .con. Lazy: only paid when a static object doesn't resolve by name.</summary>
    private void EnsureObjectGeometry()
    {
        if (_objGeom is not null) return;
        _objGeom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _geomFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _conEntries)
        {
            string text;
            try { text = System.Text.Encoding.Latin1.GetString(OwningArchive(e).Read(e)); } catch { continue; }
            if (text.IndexOf("ObjectTemplate.geometry", StringComparison.OrdinalIgnoreCase) < 0
                && text.IndexOf("GeometryTemplate.file", StringComparison.OrdinalIgnoreCase) < 0) continue;
            foreach (var t in ParseConTemplates(text))
                if (t.Geometry is { Length: > 0 } g) _objGeom.TryAdd(t.Name, g);
            foreach (var kv in ParseGeometryFiles(text))
                _geomFile.TryAdd(kv.Key, kv.Value);
        }
    }

    // Build (once, lazily) a registry of EVERY ObjectTemplate across all archives, so a vehicle's addTemplate can
    // resolve part templates defined in another folder (shared "Common" folders). First-wins (mod archives first).
    private void EnsureAllTemplates()
    {
        if (_allTemplates is not null) return;
        _allTemplates = new Dictionary<string, ConTemplate>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _conEntries)
        {
            string text;
            try { text = System.Text.Encoding.Latin1.GetString(OwningArchive(e).Read(e)); } catch { continue; }
            if (text.IndexOf("ObjectTemplate.create", StringComparison.OrdinalIgnoreCase) < 0) continue;
            foreach (var t in ParseConTemplates(text))
                if (t.Name.Length > 0) _allTemplates.TryAdd(t.Name, t);
        }
    }

    /// <summary>Index every PlayerControlObject (the spawnable vehicle/emplacement root) to the folder whose con
    /// DEFINES it, so a template placed in a level resolves even when its name differs from its folder name —
    /// interstate's stratos/ folder defines stratosvigi/stratosking, the base Air/Mustang folder defines mustang.
    /// First-wins (archives are mod-first) so a mod's redefinition shadows the base. Cons in AI/ or Sounds/ sub-folders
    /// are skipped so the hierarchy load reads the real vehicle folder, not a sub-dir.</summary>
    private void EnsurePcoFolders()
    {
        if (_pcoFolder is not null) return;
        _pcoFolder = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _vehicleCons)
        {
            string path = e.Name.Replace('\\', '/');
            int sl = path.LastIndexOf('/'); if (sl < 0) continue;
            string dir = path[..sl];
            string leaf = dir[(dir.LastIndexOf('/') + 1)..];
            if (leaf.Equals("AI", StringComparison.OrdinalIgnoreCase) || leaf.Equals("Sounds", StringComparison.OrdinalIgnoreCase)) continue;
            string text;
            try { text = System.Text.Encoding.Latin1.GetString(OwningArchive(e).Read(e)); } catch { continue; }
            if (text.IndexOf("PlayerControlObject", StringComparison.OrdinalIgnoreCase) < 0) continue;
            foreach (var t in ParseConTemplates(text))
                if (t.Type.Equals("PlayerControlObject", StringComparison.OrdinalIgnoreCase) && t.Name.Length > 0)
                    _pcoFolder.TryAdd(t.Name, dir);
        }
    }

    private Mesh? Build(string template)
    {
        var entry = Resolve(template);
        if (entry is not null) return BuildFromEntry(entry);
        // No .sm: try a BF1942 TreeMesh (.tm) — trees/bushes use a separate format the .sm resolver never finds.
        var tree = ResolveTree(template);
        if (tree is not null) return BuildFromEntry(tree);
        // BF1942 BAKED OVERGROWTH: the overgrowth bake writes static objects whose template name is the source
        // tree/mesh with a "bak" prefix (bakeu_birtch2_m1 -> eu_birtch2_m1 = EU_Birtch2_M1.tm). Those baked names
        // aren't defined templates, so without this they'd be amber diamonds — a whole forest of them. Strip the
        // prefix and resolve the underlying mesh/tree. Only fires when the "bak*" name has no mesh of its own.
        if (template.StartsWith("bak", StringComparison.OrdinalIgnoreCase) && template.Length > 3)
        {
            string s = template[3..];
            var e2 = Resolve(s);
            if (e2 is not null) return BuildFromEntry(e2);
            var t2 = ResolveTree(s);
            if (t2 is not null) return BuildFromEntry(t2);
        }
        // LOD-SUFFIXED PLACEMENTS: many maps place "<stem>_m1" while the template AND mesh are plain "<stem>"
        // (interstate's 2005 places sidewalkplacebig_m1 / city_building2_m1; the embedded Objects.con creates
        // sidewalkplacebig and the mesh is city_building2.sm). ResolveByName only ADDS suffixes, never strips
        // them, so these were invisible. Strip one trailing _mN/_lN/_lodN and re-run the whole chain — TryGet
        // shares the cache and can recurse for doubled suffixes. Only fires when nothing else matched, so a
        // formerly-invisible placement can only gain a mesh, never change one.
        if (LodStem(template) is { } stem && TryGet(stem, out var sm2) && sm2 is not null) return sm2;
        return null;
    }

    /// <summary>"sidewalkplacebig_m1" -> "sidewalkplacebig"; null when the name has no LOD suffix to strip.</summary>
    private static string? LodStem(string t)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(
            t, @"_(?:m|l|lod)\d+$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return s.Length > 0 && !s.Equals(t, StringComparison.OrdinalIgnoreCase) ? s : null;
    }

    /// <summary>Resolve a template to a BF1942 .tm tree mesh: by its own name, or via ObjectTemplate.geometry ->
    /// GeometryTemplate.file -> the .tm basename (the same indirection the .sm path uses).</summary>
    private RfaEntry? ResolveTree(string template)
    {
        static string Norm(string s)
        {
            s = s.Replace('\\', '/'); int i = s.LastIndexOf('/'); if (i >= 0) s = s[(i + 1)..];
            if (s.EndsWith(".tm", StringComparison.OrdinalIgnoreCase)) s = s[..^3];
            return s;
        }
        if (_treeByName.TryGetValue(Norm(template), out var e)) return e;
        EnsureObjectGeometry();
        if (_objGeom!.TryGetValue(template, out var geom))
        {
            string file = _geomFile!.TryGetValue(geom, out var f) ? f : geom;
            if (_treeByName.TryGetValue(Norm(file), out var te)) return te;
            if (_treeByName.TryGetValue(Norm(geom), out var ge)) return ge;
        }
        return null;
    }

    // Tree material -> the foliage texture (from texture.rfa via the texture lib) + a foliage-green fallback colour.
    private (Vector3 Color, Texture2D? Tex) ResolveTreeMaterial(string texName)
        => (new Vector3(0.36f, 0.55f, 0.30f), _textures?.Resolve(texName));

    private Mesh? BuildFromEntry(RfaEntry entry)
    {
        byte[] bytes;
        try { bytes = OwningArchive(entry).Read(entry); }
        catch { return null; }
        // BF1942 tree mesh (.tm): a different format from .sm — flatten it (trunk opaque, leaves alpha-tested).
        if (entry.Name.EndsWith(".tm", StringComparison.OrdinalIgnoreCase))
        {
            if (!RefractorForge.Formats.Rfa.TreeMesh.TryParse(bytes, out var tm) || tm is null || tm.Vertices.Length == 0)
                return null;
            return MeshFromTreeMesh(tm, ResolveTreeMaterial);
        }
        if (!StandardMesh.TryParse(bytes, out var sm) || sm is null || sm.Lods.Count == 0 || sm.Lods[0].Count == 0)
            return null;

        // Bind the matching .rs shader (level overrides win, else the archive shader next to the mesh).
        var shaders = LoadShaders(entry);

        var pos = new List<Vector3>();
        var uvs = new List<System.Numerics.Vector2>();
        var lmuvs = new List<System.Numerics.Vector2>();
        bool anyLm = false;
        var parts = new List<MaterialPart>();
        foreach (var m in sm.Lods[0])
        {
            int @base = pos.Count;
            int vcount = m.Vertices.Length;
            for (int i = 0; i < vcount; i++)
            {
                pos.Add(new Vector3(m.Vertices[i].X, m.Vertices[i].Y, m.Vertices[i].Z));
                var uv = i < m.Uvs.Length ? m.Uvs[i] : default;
                uvs.Add(new System.Numerics.Vector2(uv.U, uv.V));
                var lm = m.HasLightmapUv && i < m.LightmapUvs.Length ? m.LightmapUvs[i] : default;
                lmuvs.Add(new System.Numerics.Vector2(lm.U, lm.V));
                if (m.HasLightmapUv) anyLm = true;
            }
            var idx = new List<int>(m.Faces.Length * 3);
            foreach (var (a, b, c) in m.Faces)
            {
                if ((uint)a >= (uint)vcount || (uint)b >= (uint)vcount || (uint)c >= (uint)vcount) continue;
                if (a == b || b == c || a == c) continue;     // drop degenerate / strip-stitch triangles
                idx.Add(@base + a); idx.Add(@base + b); idx.Add(@base + c);
            }
            if (idx.Count == 0) continue;
            RsShaderSet.MaterialShader? sh = null;
            shaders?.Materials.TryGetValue(m.Name, out sh);
            var tex = _textures?.Resolve(sh?.Texture);
            // Foliage/cutout sheets and explicit fade materials are alpha-tested so the transparent
            // parts of the atlas don't render as opaque rectangles.
            // Glass/canopy (explicit `transparent`) blends softly with NORMAL lighting; foliage/fences/cutout sheets
            // (fade flag, foliage names, or genuine cutout alpha) are HARD alpha-tested with flat foliage lighting.
            bool blend = sh?.Transparent == true;
            bool cutout = !blend && (sh?.TextureFade == true || IsCutout(sh?.Texture) || HasTransparency(tex));
            parts.Add(new MaterialPart(idx.ToArray(), RsShaderSet.ColorFor(sh), tex, AlphaTest: cutout, Blend: blend));
        }
        if (parts.Count == 0) return null;
        return new Mesh(pos.ToArray(), uvs.ToArray(), parts.ToArray())
        {
            LightmapUvs = anyLm ? lmuvs.ToArray() : null,   // only carry a 2nd UV set when the mesh actually has one
        };
    }

    private static bool IsCutout(string? tex)
    {
        if (tex is null) return false;
        string t = tex.ToLowerInvariant();
        return t.Contains("leaf") || t.Contains("palm") || t.Contains("tree") || t.Contains("fern")
            || t.Contains("bush") || t.Contains("vine") || t.Contains("plant") || t.Contains("grass")
            || t.Contains("frond") || t.Contains("foliage") || t.Contains("portal")
            // cutout/transparent hard-surface names common on vehicles + structures
            || t.Contains("fence") || t.Contains("wire") || t.Contains("chain") || t.Contains("net")
            || t.Contains("grate") || t.Contains("window") || t.Contains("glass") || t.Contains("railing")
            || t.Contains("canopy") || t.Contains("cockpit") || t.Contains("windscreen") || t.Contains("windsh");
    }

    // True when a decoded texture actually contains transparency (a leaf/cutout atlas): a meaningful fraction of texels
    // are non-opaque. Lets the .tm foliage path mark leaf materials alpha-tested by their REAL alpha (DXT5 leaf atlases)
    // rather than a fragile group-index guess. Sampled (not full-scanned) for speed.
    private static bool HasTransparency(Texture2D? tex)
    {
        if (tex?.Rgba is not { Length: > 0 } d) return false;
        int n = d.Length / 4; if (n == 0) return false;
        // Real transparency (glass / cutout / fences / window holes) is BIMODAL: a meaningful transparent region AND
        // a meaningful solid region. We reject two false positives that hurt: (1) only a trickle of sub-opaque alpha,
        // which is usually a gloss/spec mask on metal panels -- not transparency; (2) a near-FULLY-transparent channel,
        // which is an unused/garbage alpha that would otherwise make the WHOLE mesh vanish (this was hiding vehicles).
        int step = Math.Max(1, n / 8192), sampled = 0, trans = 0, solid = 0;
        for (int i = 0; i < n; i += step) { int a = d[i * 4 + 3]; sampled++; if (a < 128) trans++; else if (a >= 240) solid++; }
        if (sampled == 0) return false;
        float tf = (float)trans / sampled, sf = (float)solid / sampled;
        return tf > 0.02f && tf < 0.85f && sf > 0.05f;   // some transparent + some solid -> genuine glass/cutout
    }

    /// <summary>Find and parse the .rs shader set for a mesh entry (basename match; level override first).</summary>
    private RsShaderSet? LoadShaders(RfaEntry smEntry)
    {
        string baseName = smEntry.Name.Replace('\\', '/');
        baseName = baseName[(baseName.LastIndexOf('/') + 1)..];
        string rsName = Path.ChangeExtension(baseName, ".rs");
        try
        {
            if (_rsOverrideFiles.TryGetValue(rsName, out var file))
                return RsShaderSet.Parse(File.ReadAllText(file));
            if (_rsByName.TryGetValue(rsName, out var e))
                return RsShaderSet.Parse(System.Text.Encoding.Latin1.GetString(OwningArchive(e).Read(e)));
        }
        catch { /* shader is optional; fall back to default coloring */ }
        return null;
    }

    private RfaArchive OwningArchive(RfaEntry e)
    {
        foreach (var arc in _archives)
            if (arc.Entries.Contains(e)) return arc;
        return _archives[0];
    }
}
