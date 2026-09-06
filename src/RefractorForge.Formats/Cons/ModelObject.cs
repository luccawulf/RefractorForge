using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Mesh;
using RefractorForge.Formats.Rfa;

namespace RefractorForge.Formats.Con;

/// <summary>
/// Turns an imported 3D model into a level-local static object: a real <c>.sm</c> with its textures, registered so
/// it ships inside the map and needs nothing from the mod.
///
/// This is <see cref="DecalObject"/>'s recipe — the six files and two <c>.con</c> patches that are known to load —
/// generalised from one quad with one texture to an arbitrary mesh with as many material sections as it has:
///
///   Objects/&lt;Name&gt;/Objects.con      ObjectTemplate.create SimpleObject + geometry + collision flag
///   Objects/&lt;Name&gt;/Geometries.con   GeometryTemplate StandardMesh -> ../&lt;base&gt;/levels/&lt;Level&gt;/StandardMesh/&lt;Name&gt;
///   Objects/&lt;Name&gt;/&lt;Name&gt;.con        run Objects / run Geometries
///   Objects/objects.con              run &lt;Name&gt;/&lt;Name&gt;   (patched in via DecalObject.PatchObjectsCon)
///   StandardMesh/&lt;Name&gt;.sm + .rs      the geometry and one subshader per material
///   Texture/&lt;texture&gt;.dds            each texture, found via textureManager.alternativePath in Init.con
///
/// The two <c>.con</c> patches (<see cref="DecalObject.PatchObjectsCon"/>, <see cref="DecalObject.PatchInitCon"/>)
/// are shared verbatim — they are about the LEVEL, not about what kind of object you added.
/// </summary>
public static class ModelObject
{
    /// <summary>One texture to ship beside the mesh. <paramref name="Name"/> carries no extension or folder — it
    /// becomes <c>Texture/&lt;Name&gt;.dds</c> and the shader's <c>texture "texture/&lt;Name&gt;"</c>.</summary>
    public sealed record Texture(string Name, byte[] Dds);

    /// <summary>How one of the model's materials should be drawn, keyed by the material name the source file used
    /// (an OBJ <c>usemtl</c>). Anything the caller doesn't describe is drawn opaque and untextured.</summary>
    /// <param name="TextureName">A <see cref="Texture.Name"/> from the same build, or null for untextured.</param>
    /// <param name="AlphaTestRef">Set only for a cut-out — a fence, a leaf card. See
    /// <see cref="RsWriter.Material.AlphaTestRef"/>: putting one on a solid surface mislabels it.</param>
    public sealed record Material(string SourceName, string? TextureName, Vec3 Diffuse,
                                  bool Transparent = false, float? AlphaTestRef = null, bool TwoSided = false);

    /// <param name="Files">Archive-relative paths and bytes, ready for <c>LevelSaver.RepackToRfa(newEntries:)</c>
    /// or a folder write.</param>
    /// <param name="MaterialNames">The renamed materials in submesh order, so a caller can show what was bound.</param>
    public sealed record Built(string Template, List<(string RelPath, byte[] Bytes)> Files, string RunLine,
                               ObjMesh Mesh, List<string> MaterialNames, bool HasCollision);

    /// <param name="levelName">The level folder name, as it appears under &lt;baseSub&gt;/levels/.</param>
    /// <param name="name">Template name (letters, digits, underscore); sanitized.</param>
    /// <param name="mesh">The model. <b>Mutated</b>: its material names are rewritten to the
    /// <c>&lt;Name&gt;_MaterialN</c> convention and oversized sections are split. Pass a copy if that matters.</param>
    /// <param name="baseSub">The game's archive mount root: "bf1942" or "BfVietnam". The two games share no
    /// namespace, so a BF1942 path resolves to nothing in Vietnam and the object silently gets no mesh.</param>
    /// <param name="collision">Bake a collision section from the mesh so the object is solid. EXPERIMENTAL — the
    /// section's BSP tail is written empty because its node format is still unsolved, and whether the engine
    /// rebuilds it at load has to be confirmed in game (docs/SM_Collision_RE.md).</param>
    public static Built Build(string levelName, string name, ObjMesh mesh,
                              IEnumerable<Material>? materials = null,
                              IEnumerable<Texture>? textures = null,
                              bool collision = false,
                              string baseSub = "bf1942",
                              float maxDrawDistance = 0f)
    {
        name = DecalObject.Sanitize(name);
        if (mesh.SubMeshes.Count == 0) throw new InvalidOperationException("The model has no geometry to write.");

        var bySource = (materials ?? Enumerable.Empty<Material>())
            .GroupBy(m => m.SourceName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // A dense import can exceed the format's 65,535-vertex-per-section ceiling; split before naming so every
        // piece that ends up in the file is accounted for.
        MeshFit.SplitOversizedSections(mesh);

        // Rename to DICE's <Mesh>_MaterialN. The .sm binds a section to a shader by MATERIAL NAME through one
        // GLOBAL registry, so a model whose author called a material "wood" would otherwise fight every mod that
        // also has a "wood". Sections that shared a source material keep sharing one name (and one subshader).
        var indexOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var shaders = new List<RsWriter.Material>();
        var names = new List<string>();
        foreach (var s in mesh.SubMeshes)
        {
            string source = s.Material;
            if (!indexOf.TryGetValue(source, out int idx))
            {
                idx = indexOf.Count;
                indexOf[source] = idx;
                var m = bySource.TryGetValue(source, out var bind) ? bind : null;
                shaders.Add(new RsWriter.Material($"{name}_Material{idx}",
                                                  m?.TextureName,
                                                  m?.Diffuse ?? new Vec3(1, 1, 1),
                                                  Transparent: m?.Transparent ?? false,
                                                  AlphaTestRef: m?.AlphaTestRef,
                                                  TwoSided: m?.TwoSided ?? false));
            }
            s.Material = $"{name}_Material{idx}";
            names.Add(s.Material);
        }

        var files = new List<(string, byte[])>();
        var crlf = new UTF8Encoding(false);

        byte[]? col = collision ? StandardMeshWriter.BuildObjCollision(mesh) : null;
        files.Add(($"StandardMesh/{name}.sm", StandardMeshWriter.Write(mesh, col)));
        files.Add(($"StandardMesh/{name}.rs", crlf.GetBytes(RsWriter.Write(shaders))));

        foreach (var t in textures ?? Enumerable.Empty<Texture>())
        {
            var tn = DecalObject.Sanitize(t.Name);
            if (t.Dds is { Length: > 0 }) files.Add(($"Texture/{tn}.dds", t.Dds));
        }

        // The full 0..5 LOD ramp every shipped Geometries.con writes. All six distances point at the one mesh until
        // the importer generates decimated copies; a truncated ramp makes the object stop drawing early, so the
        // last number is really the cull distance.
        float far = maxDrawDistance > 0f ? maxDrawDistance : 1000f;
        string F1(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        var geom = new StringBuilder()
            .Append($"GeometryTemplate.create StandardMesh {name}\r\n")
            .Append($"GeometryTemplate.file ../{baseSub}/levels/{levelName}/StandardMesh/{name}\r\n");
        float[] ramp = { 0f, far * 0.1f, far * 0.2f, far * 0.4f, far * 0.6f, far };
        for (int i = 0; i < ramp.Length; i++) geom.Append($"GeometryTemplate.setLodDistance {i} {F1(ramp[i])}\r\n");
        geom.Append("\r\n");
        files.Add(($"Objects/{name}/Geometries.con", crlf.GetBytes(geom.ToString())));

        // Every collidable static object in the retail archives is exactly this — geometry plus the flag. There is
        // no .con-level collision primitive; the solidity lives in the .sm's col section, so the flag is necessary
        // and not sufficient (docs/SM_Collision_RE.md, "Path B — DEAD END").
        string obj =
            $"ObjectTemplate.create SimpleObject {name}\r\n" +
            $"ObjectTemplate.geometry {name}\r\n" +
            $"ObjectTemplate.HasCollisionPhysics {(col is not null ? 1 : 0)}\r\n" +
            (col is null && collision
                ? "rem Collision was requested but the mesh is past the 32767-vertex collision limit; decimate it.\r\n"
                : "") +
            "\r\n";
        files.Add(($"Objects/{name}/Objects.con", crlf.GetBytes(obj)));

        files.Add(($"Objects/{name}/{name}.con", crlf.GetBytes("run Objects\r\nrun Geometries\r\n")));

        return new Built(name, files, $"run {name}/{name}", mesh, names, col is not null);
    }
}
