using System;
using System.Collections.Generic;
using System.Text;
using RefractorForge.Formats.Mesh;
using RefractorForge.Formats.Rfa;

namespace RefractorForge.Formats.Con;

/// <summary>
/// Ships a lightmap-unwrapped copy of a retail mesh INSIDE THE LEVEL, and gives it an object to hang on.
///
/// <para>Two thirds of BfVietnam's meshes and ninety percent of BF1942's carry no lightmap channel at all, so the
/// unwrapper has to widen the vertex and write a new <c>.sm</c>. That file cannot simply replace the one in
/// <c>standardMesh.rfa</c> - the base archives are mounted first and win, the editor's own mesh library is
/// documented first-wins, and redefining a template the mod already declares is behaviour nobody here has
/// established. The route that IS established is the one decals and imported models already take: put the mesh in
/// the level's own archive and reference it BY PATH
/// (<c>GeometryTemplate.file ../&lt;base&gt;/levels/&lt;Level&gt;/StandardMesh/&lt;Name&gt;</c>). Precedence never
/// enters into it and nothing outside this one map changes.</para>
///
/// <para><b>The name matters more than it looks.</b> The patched mesh is <c>foo_lm_m1</c>, never
/// <c>foo_m1_lm</c>: the editor strips a trailing <c>_M1</c>/<c>_M2</c> when it matches a baked lightmap to a
/// placement (<c>ObjectLightmaps.NormTemplate</c>), so a suffix after the LOD tag would key the patched mesh's
/// lightmaps differently from every other mesh in the level and the viewport match would go quietly wrong.</para>
/// </summary>
public static class LightmapMeshPatch
{
    /// <summary>Everything the level needs, ready for the save path's <c>newEntries</c>.</summary>
    /// <param name="Template">The new object template to point placements at.</param>
    /// <param name="MeshName">The new mesh name - also the stem of its baked lightmap files.</param>
    public sealed record Built(string Template, string MeshName,
                               List<(string RelPath, byte[] Bytes)> Files, string RunLine,
                               LightmapUnwrapper.UnwrapDiagnostics Diagnostics);

    /// <summary>
    /// <c>foo_m1</c> becomes <c>foo_lm_m1</c> - the marker goes BEFORE the LOD tag, so the name keeps the shape
    /// the lightmap matcher expects. A name with no LOD tag just gets the suffix.
    /// </summary>
    public static string PatchedName(string meshName, string suffix = "_lm")
    {
        var n = (meshName ?? "").Trim();
        int slash = n.LastIndexOfAny(new[] { '/', '\\' });
        if (slash >= 0) n = n[(slash + 1)..];
        if (n.EndsWith(".sm", StringComparison.OrdinalIgnoreCase)) n = n[..^3];
        if (n.Length > 3 && (n.EndsWith("_M1", StringComparison.OrdinalIgnoreCase)
                          || n.EndsWith("_M2", StringComparison.OrdinalIgnoreCase)
                          || n.EndsWith("_M3", StringComparison.OrdinalIgnoreCase)))
            return n[..^3] + suffix + n[^3..];
        return n + suffix;
    }

    /// <summary>
    /// Unwrap <paramref name="originalSm"/> and produce the level-local files for it.
    ///
    /// <para>Returns null with a status when the mesh cannot be unwrapped - a triangle strip, geometry that will
    /// not pack, nothing usable. NOTHING is emitted in that case: a half-patched object is worse than an
    /// unpatched one, and a mesh whose qflag claims an unwrap it does not carry is worse still.</para>
    /// </summary>
    /// <param name="rsText">The original mesh's <c>.rs</c>, copied verbatim so the patched mesh keeps exactly the
    /// textures and render states the original had. Without it the object draws untextured.</param>
    public static Built? Build(string levelName, string meshName, byte[] originalSm, string? rsText,
                               string baseSub, out LightmapUnwrapper.UnwrapStatus status,
                               LightmapUnwrapper.UnwrapOptions? options = null,
                               float maxDrawDistance = 0f)
    {
        status = LightmapUnwrapper.UnwrapStatus.NoUsableGeometry;
        if (!StandardMesh.TryParse(originalSm, out var sm) || sm is null) return null;

        var result = LightmapUnwrapper.Unwrap(sm, options);
        status = result.Status;
        if (result.Status != LightmapUnwrapper.UnwrapStatus.Ok || result.Lod0Plan is null) return null;

        byte[] patched;
        try
        {
            // qflag 1 says "this mesh carries a real unwrap" - measured true of 371 of 371 unwrapped retail
            // meshes, and the rewriter refuses to set it on a mesh whose vertices could not hold one.
            patched = StandardMeshRewriter.Write(originalSm, sm, new[] { result.Lod0Plan },
                                                 new StandardMeshRewriter.Options(SetQFlag: true));
        }
        catch (Exception) { status = LightmapUnwrapper.UnwrapStatus.IndexCeiling; return null; }

        string name = PatchedName(meshName);
        var crlf = new UTF8Encoding(false);
        var files = new List<(string, byte[])>
        {
            ($"StandardMesh/{name}.sm", patched),
        };

        // The shader, verbatim but renamed to sit beside the new mesh. Its MATERIAL names are left exactly as
        // they were: the engine's own log shows shader keys of the form StandardMesh/<file>/<material>, so they
        // are namespaced by the mesh path rather than global - and if that turns out to be wrong, the symptom is
        // a material collision, not silent corruption. Renaming them instead would force every patched mesh
        // through a full rewrite of its material headers for no proven gain.
        if (rsText is { Length: > 0 })
            files.Add(($"StandardMesh/{name}.rs", crlf.GetBytes(rsText.Replace("\r\n", "\n").Replace("\n", "\r\n"))));

        float far = maxDrawDistance > 0f ? maxDrawDistance : 1000f;
        string F1(float v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        var geom = new StringBuilder()
            .Append($"GeometryTemplate.create StandardMesh {name}\r\n")
            .Append($"GeometryTemplate.file ../{baseSub}/levels/{levelName}/StandardMesh/{name}\r\n")
            .Append("GeometryTemplate.setLodDistance 0 0\r\n")
            .Append($"GeometryTemplate.setLodDistance 1 {F1(far * 0.1f)}\r\n")
            .Append($"GeometryTemplate.setLodDistance 2 {F1(far * 0.2f)}\r\n")
            .Append($"GeometryTemplate.setLodDistance 3 {F1(far * 0.4f)}\r\n")
            .Append($"GeometryTemplate.setLodDistance 4 {F1(far * 0.6f)}\r\n")
            .Append($"GeometryTemplate.setLodDistance 5 {F1(far)}\r\n\r\n")
            .ToString();
        files.Add(($"Objects/{name}/Geometries.con", crlf.GetBytes(geom)));

        // A SimpleObject that collides, which is what a static prop is. This deliberately does NOT try to
        // reproduce a Bundle/LodObject tree: cloning one faithfully needs the original's whole .con set, and for
        // the thing this exists to prove - does a widened, unwrapped mesh light in game - a single object is the
        // cleaner experiment. Anything more elaborate can be built on top once that answer is known.
        string obj =
            $"ObjectTemplate.create SimpleObject {name}\r\n" +
            $"ObjectTemplate.geometry {name}\r\n" +
            "ObjectTemplate.hasCollisionPhysics 1\r\n\r\n";      // not "setHas...": that is no command, and was skipped
        files.Add(($"Objects/{name}/Objects.con", crlf.GetBytes(obj)));
        files.Add(($"Objects/{name}/{name}.con", crlf.GetBytes("run Objects\r\nrun Geometries\r\n")));

        return new Built(name, name, files, $"run {name}/{name}", result.Diagnostics);
    }

    /// <summary>
    /// Has this level already been given a patched copy of this mesh? Derived from the ORIGINAL name only, so
    /// running the tool twice is a no-op rather than a way to produce <c>foo_lm_lm_m1</c>.
    /// </summary>
    public static bool AlreadyPatched(string meshName, IEnumerable<string> existingLevelPaths)
    {
        string name = PatchedName(meshName);
        string want = $"Objects/{name}/Geometries.con";
        foreach (var p in existingLevelPaths)
            if (p.Replace('\\', '/').EndsWith(want, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
