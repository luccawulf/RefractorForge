using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Mesh;
using RefractorForge.Formats.Rfa;

namespace RefractorForge.Render;

/// <summary>
/// Decides, per object TYPE on a map, whether it can be made lightmap-ready and what that costs, and builds the
/// patched meshes for the ones chosen.
///
/// <para>Measured on al_vietnas, which is why the rows look the way they do: 2,006 placed objects, 129 lit. Of the
/// rest, 294 objects (49 meshes) already have BfVietnam's 64-byte lightmap vertex with the UV slot left EMPTY - the
/// interior clutter, cots, sandbags, ammo boxes, signs - and are filled in place without changing the format;
/// 1,578 (64 meshes) have no slot and are widened to 40 bytes, and about 1,090 of those are foliage. Foliage and
/// effect quads start unticked: BfVietnam is a 32-bit game, every placed object gets its own map, retail levels
/// carry about 120, and a tree's leaf cards need a 1024 px atlas.</para>
///
/// <para>Pure given the mesh bytes: <see cref="FilesNeeded"/> names what to read, the caller reads it from the
/// (not thread-safe) library, and <see cref="Plan"/> / <see cref="BuildPatches"/> can then run on a worker.</para>
/// </summary>
public static class LightmapReadyPlanner
{
    public enum Kind { Structure, Foliage, Effect }

    /// <summary>One mesh file as read from the library: its bytes and its shader text. Nothing else is needed, so
    /// planning never touches the library's caches and can run off the render thread.</summary>
    public sealed record MeshSource(byte[] Sm, string? Rs);

    /// <param name="State">full / partial / slot-zeroed / no-slot / mixed</param>
    /// <param name="Widen">A section has to grow from 32 to 40 bytes to hold the UVs.</param>
    /// <param name="Problem">Why this mesh cannot be patched, or null.</param>
    public sealed record GeometryPlan(string Geometry, string File, string State, bool Widen, string? Problem,
                                      LightmapUnwrapper.UnwrapDiagnostics? Diag, int Tris, int MapPx)
    {
        public bool Patch => Problem is null && State != "full";
    }

    /// <param name="DoneAs">Already pointed at this lightmap-ready copy.</param>
    /// <param name="Problem">Why the type cannot be made lightmap-ready, or null.</param>
    public sealed record Row(string Template, int Placed, Kind Kind, IReadOnlyList<GeometryPlan> Geometries,
                             string? DoneAs, string? Problem)
    {
        public bool Selectable => DoneAs is null && Problem is null && Geometries.Any(g => g.Patch);
        public bool DefaultOn => Selectable && Kind == Kind.Structure;
        public bool Widens => Geometries.Any(g => g.Patch && g.Widen);
        /// <summary>New lightmaps one placement adds - one per patched LOD mesh.</summary>
        public int MapsPerObject => Geometries.Count(g => g.Patch);
        /// <summary>What those maps cost on disk (8-bit colour-mapped TGA: pixels + palette + header).</summary>
        public long BytesPerObject => Geometries.Where(g => g.Patch).Sum(g => (long)g.MapPx * g.MapPx + 786);
    }

    /// <summary>The mesh files a plan over these templates will read, including an earlier run's originals.</summary>
    public static List<string> FilesNeeded(IEnumerable<string> templates, TemplateScripts ts, LightmapReady.Manifest manifest)
    {
        var files = new List<string>();
        foreach (var t in templates.Concat(manifest.Placed.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var g in LightmapReady.LodGeometries(ts, manifest.OriginalOf(t) ?? t))
            {
                var f = LightmapReady.GeometryFile(ts, g);
                if (!files.Contains(f, StringComparer.OrdinalIgnoreCase)) files.Add(f);
            }
        return files;
    }

    /// <summary>
    /// One row per placed template whose meshes do not all carry a lightmap unwrap - plus rows for types an earlier
    /// run already made lightmap-ready. Fully lit types and templates no script defines are left out; the second
    /// are counted in <paramref name="undefined"/>.
    /// </summary>
    /// <param name="mapSize">The lightmap size the bake would give a mesh of this largest extent (metres), given the
    /// floor its unwrap was packed for.</param>
    public static List<Row> Plan(IReadOnlyList<(string Template, int Count)> placed, TemplateScripts ts,
                                 LightmapReady.Manifest manifest, IReadOnlyDictionary<string, MeshSource?> meshes,
                                 Func<float, int, int> mapSize, out int undefined,
                                 CancellationToken cancel = default)
    {
        undefined = 0;
        var rows = new List<Row>();
        var geomCache = new Dictionary<string, GeometryPlan>(StringComparer.OrdinalIgnoreCase);

        foreach (var (template, count) in placed)
        {
            cancel.ThrowIfCancellationRequested();
            string? original = manifest.OriginalOf(template);
            string t = original ?? template;
            var def = ts.Object(t);
            if (def is null) { undefined += count; continue; }

            var geoms = LightmapReady.LodGeometries(ts, t)
                .Select(g => geomCache.TryGetValue(g, out var gp) ? gp : geomCache[g] = PlanGeometry(ts, g, meshes, mapSize))
                .ToList();
            if (original is null && geoms.All(g => g.State == "full")) continue;     // already lit: nothing to offer

            MeshSource? first = geoms.Count > 0 && meshes.TryGetValue(geoms[0].File, out var src) ? src : null;
            string? problem = def.ConflictingCreates ? "defined twice with different contents"
                            : geoms.Count == 0 ? "draws no mesh of its own"
                            : !geoms.Any(g => g.Patch) && original is null ? geoms.First(g => g.Problem is not null).Problem
                            : null;
            rows.Add(new Row(t, count, Classify(first), geoms, original is null ? null : template, problem));
        }
        return rows.OrderBy(r => r.DoneAs is null ? 0 : 1).ThenBy(r => r.Kind).ThenByDescending(r => r.Placed)
                   .ThenBy(r => r.Template, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static GeometryPlan PlanGeometry(TemplateScripts ts, string geometry, IReadOnlyDictionary<string, MeshSource?> meshes,
                                             Func<float, int, int> mapSize)
    {
        string file = LightmapReady.GeometryFile(ts, geometry);
        if (!meshes.TryGetValue(file, out var src) || src is null)
            return new GeometryPlan(geometry, file, "missing", false, "mesh file not found", null, 0, 0);
        if (!StandardMesh.TryParse(src.Sm, out var sm) || sm is null || sm.Lods.Count == 0)
            return new GeometryPlan(geometry, file, "unreadable", false, "mesh file could not be read", null, 0, 0);

        bool anyReal = false, anyNonReal = false, anySlot = false, anyNoSlot = false, widen = false;
        int tris = 0;
        foreach (var m in sm.Lods[0])
        {
            tris += m.Faces.Length;
            bool real = m.HasLightmapUv && m.LightmapUvs.Any(uv => MathF.Abs(uv.U) > 1e-6f || MathF.Abs(uv.V) > 1e-6f);
            if (real) anyReal = true; else anyNonReal = true;
            if (m.HasLightmapUv) anySlot = true; else anyNoSlot = true;
            if (m.VertexByteSize == 32) widen = true;
        }
        string state = anyReal && !anyNonReal ? "full" : anyReal ? "partial"
                     : !anySlot ? "no-slot" : !anyNoSlot ? "slot-zeroed" : "mixed";
        if (state == "full") return new GeometryPlan(geometry, file, state, false, null, null, tris, 0);

        // The game client has no guard for a mesh whose shader is missing - it dies - so a copy without one is
        // never written. (The dedicated server does not read shaders, which is why only this check catches it.)
        if (src.Rs is not { Length: > 0 })
            return new GeometryPlan(geometry, file, state, widen, "no shader (.rs) found for the mesh", null, tris, 0);

        var r = LightmapUnwrapper.Unwrap(sm);
        if (r.Status != LightmapUnwrapper.UnwrapStatus.Ok)
            return new GeometryPlan(geometry, file, state, widen, $"cannot unwrap: {r.Status} ({r.Diagnostics.Reason})", null, tris, 0);
        int px = mapSize(Extent(sm), r.Diagnostics.MinBakeSize);
        return new GeometryPlan(geometry, file, state, widen, null, r.Diagnostics, tris, px);
    }

    /// <summary>The mesh's largest bounding-box side over LOD0 - what the bake sizes a lightmap from.</summary>
    public static float Extent(StandardMesh sm)
    {
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        foreach (var m in sm.Lods[0])
            foreach (var v in m.Vertices)
            {
                if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z)) continue;
                minX = MathF.Min(minX, v.X); minY = MathF.Min(minY, v.Y); minZ = MathF.Min(minZ, v.Z);
                maxX = MathF.Max(maxX, v.X); maxY = MathF.Max(maxY, v.Y); maxZ = MathF.Max(maxZ, v.Z);
            }
        return minX > maxX ? 0f : MathF.Max(maxX - minX, MathF.Max(maxY - minY, maxZ - minZ));
    }

    /// <summary>
    /// Foliage is read from the SHADER, not guessed from names or from textures. Every BfVietnam vegetation material
    /// - trees, palms, bushes, ferns, grass, rice - carries both <c>alphatestref</c> and <c>selfillum</c>; barbed
    /// wire carries <c>alphatestref</c> and <c>twosided</c> but no <c>selfillum</c>, and solid props carry neither.
    /// (The editor's own foliage flag is no use here: an <c>alphatestref</c> makes it treat a leaf card as a hard
    /// cutout, and without textures loaded it sees nothing at all.) Weighted by triangles, so a building with a
    /// potted plant is still a building. An effect or decal is a quad or two, or nothing but blended glass.
    /// </summary>
    private static Kind Classify(MeshSource? src)
    {
        if (src is null || !StandardMesh.TryParse(src.Sm, out var sm) || sm is null || sm.Lods.Count == 0) return Kind.Structure;
        var veg = VegetationMaterials(src.Rs);
        var blended = BlendedMaterials(src.Rs);
        int total = 0, leaf = 0, glass = 0;
        foreach (var m in sm.Lods[0])
        {
            int n = m.Faces.Length;
            total += n;
            if (veg.Contains(m.Name)) leaf += n;
            if (blended.Contains(m.Name)) glass += n;
        }
        if (total <= 4 || (total > 0 && glass == total)) return Kind.Effect;
        if (total > 0 && leaf * 2 >= total) return Kind.Foliage;
        return Kind.Structure;
    }

    private static IEnumerable<(string Name, string Body)> Subshaders(string? rs)
    {
        if (string.IsNullOrEmpty(rs)) yield break;
        int i = 0;
        while ((i = rs.IndexOf("subshader", i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int q1 = rs.IndexOf('"', i), q2 = q1 < 0 ? -1 : rs.IndexOf('"', q1 + 1);
            int open = q2 < 0 ? -1 : rs.IndexOf('{', q2), close = open < 0 ? -1 : rs.IndexOf('}', open);
            if (close < 0) yield break;
            yield return (rs[(q1 + 1)..q2], rs[(open + 1)..close]);
            i = close;
        }
    }

    private static HashSet<string> VegetationMaterials(string? rs)
        => Subshaders(rs).Where(s => s.Body.Contains("alphatestref", StringComparison.OrdinalIgnoreCase)
                                     && s.Body.Contains("selfillum", StringComparison.OrdinalIgnoreCase))
                         .Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> BlendedMaterials(string? rs)
        => Subshaders(rs).Where(s => System.Text.RegularExpressions.Regex.IsMatch(s.Body, @"(?i)\btransparent\s+true"))
                         .Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Unwrap and rewrite every mesh the chosen types need, once each however many types share it. Includes an
    /// earlier run's meshes: the copies' scripts are written whole each time, so they are rebuilt - identically, the
    /// unwrap is deterministic - rather than left out.
    /// </summary>
    public static Dictionary<string, LightmapReady.PatchedMesh> BuildPatches(
        IEnumerable<string> templates, TemplateScripts ts, LightmapReady.Manifest manifest,
        IReadOnlyDictionary<string, MeshSource?> meshes, out List<string> failures, CancellationToken cancel = default)
    {
        failures = new List<string>();
        var result = new Dictionary<string, LightmapReady.PatchedMesh>(StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<string>(manifest.Geometries.Values, StringComparer.OrdinalIgnoreCase);
        foreach (var t in templates.Concat(manifest.Placed.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var g in LightmapReady.LodGeometries(ts, t))
            {
                cancel.ThrowIfCancellationRequested();
                if (result.ContainsKey(g)) continue;
                string file = LightmapReady.GeometryFile(ts, g);
                if (!meshes.TryGetValue(file, out var src) || src is null || src.Rs is not { Length: > 0 }) continue;
                if (!StandardMesh.TryParse(src.Sm, out var sm) || sm is null) continue;
                bool full = sm.Lods.Count > 0 && sm.Lods[0].All(m => m.HasLightmapUv
                            && m.LightmapUvs.Any(uv => MathF.Abs(uv.U) > 1e-6f || MathF.Abs(uv.V) > 1e-6f));
                if (full) continue;
                var r = LightmapUnwrapper.Unwrap(sm);
                if (r.Status != LightmapUnwrapper.UnwrapStatus.Ok || r.Lod0Plan is null) { failures.Add($"{g}: {r.Status}"); continue; }
                byte[] bytes;
                try
                {
                    bytes = StandardMeshRewriter.Write(src.Sm, sm, new[] { r.Lod0Plan }, new StandardMeshRewriter.Options(SetQFlag: true));
                }
                catch (Exception ex) { failures.Add($"{g}: {ex.Message}"); continue; }
                string clone = LightmapReady.GeometryCopyName(ts, g, manifest, taken);
                result[g] = new LightmapReady.PatchedMesh(g, clone, bytes, src.Rs);
            }
        return result;
    }
}
