using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RefractorForge.Formats.Mesh;

/// <summary>
/// A model taken apart by the Battlefield toolkit's naming convention - the interchange format the whole
/// Refractor art pipeline actually used. There was never a file format for "a mesh with its collision and its
/// shadow": the 3ds Max exporter (Rexman's <c>bfTool_maxZipScript.mzp</c>) and DICE's standalone
/// <c>3dsToSm.exe</c> both read the roles off the OBJECT NAMES in the scene:
/// <code>
///   LOD01 .. LODnn   visible detail levels, highest first
///   COL01            simple collision (soldiers, vehicles)
///   COL02            complex collision (projectiles)
///   shadow           the real-time shadow mesh
///   bbox / bounds    an optional geometric bounding box
/// </code>
/// The exporter's rule ("parseSceneNames"): lower-case the name, look at its first three letters, and read the
/// digits that follow as the number. Objects that share a role and number are merged. A scene with one unnamed
/// object exports it as LOD01. That rule is followed here, with two forgiving differences: a role with no number
/// counts as 01 rather than being dropped, and objects the convention does not name are folded into LOD01 and
/// reported (a downloaded model is full of "Cube.003"s that are all part of the visible mesh, and losing them
/// silently is worse than a note). An abbreviation only counts when it is not the start of a longer word, so a
/// "shaft" is not a shadow and a "collar" is not collision.
/// </summary>
public sealed class MeshParts
{
    /// <summary>Visible detail levels, LOD01 first. Never empty when <see cref="Split"/> was given any geometry.</summary>
    public List<ObjMesh> Lods { get; } = new();
    /// <summary>COL01 - the simple collision mesh, or null.</summary>
    public ObjMesh? CollisionSimple { get; set; }
    /// <summary>COL02 - the complex collision mesh, or null.</summary>
    public ObjMesh? CollisionComplex { get; set; }
    /// <summary>The shadow mesh, or null.</summary>
    public ObjMesh? Shadow { get; set; }
    /// <summary>A geometric bounding-box object, or null (the box is then LOD01's).</summary>
    public ObjMesh? Bounds { get; set; }
    /// <summary>Object names the convention did not recognise, folded into LOD01.</summary>
    public List<string> Unclassified { get; } = new();
    /// <summary>True when at least one object name followed the convention.</summary>
    public bool UsedConvention { get; private set; }

    public IEnumerable<ObjMesh> CollisionMeshes
    {
        get { if (CollisionSimple is not null) yield return CollisionSimple; if (CollisionComplex is not null) yield return CollisionComplex; }
    }

    enum Role { None, Lod, Col, Shadow, Bounds }

    static readonly Regex NamePattern = new(@"^(?:(lod|col)(?![a-z])\s*_?(\d+)?|(sha)(?:dow)?(?![a-z])|(bbo|bou)(?:x|nds)?(?![a-z]))",
                                            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Which role and number an object name declares. Number is 1 when the role carries none.</summary>
    public static (bool Matched, string Role, int Number) Classify(string objectName)
    {
        var m = NamePattern.Match(objectName.Trim());
        if (!m.Success) return (false, "", 0);
        if (m.Groups[1].Success)
        {
            int n = m.Groups[2].Success && int.TryParse(m.Groups[2].Value, out var v) && v > 0 ? v : 1;
            return (true, m.Groups[1].Value.ToLowerInvariant() == "lod" ? "lod" : "col", n);
        }
        if (m.Groups[3].Success) return (true, "shadow", 1);
        return (true, "bounds", 1);
    }

    /// <summary>Sort a mesh's objects into their roles. Every part comes back merged to one piece per material,
    /// which is what the writer and the decimator work on; the object names have done their job by then.</summary>
    public static MeshParts Split(ObjMesh mesh)
    {
        var parts = new MeshParts();
        var lods = new SortedDictionary<int, List<ObjSubMesh>>();
        var cols = new SortedDictionary<int, List<ObjSubMesh>>();
        var shadow = new List<ObjSubMesh>();
        var bounds = new List<ObjSubMesh>();
        var loose = new List<ObjSubMesh>();
        var looseNames = new List<string>();

        foreach (var s in mesh.SubMeshes)
        {
            var (ok, role, n) = Classify(s.Object);
            if (!ok) { loose.Add(s); if (s.Object.Length > 0 && !looseNames.Contains(s.Object)) looseNames.Add(s.Object); continue; }
            parts.UsedConvention = true;
            switch (role)
            {
                case "lod": Bucket(lods, n).Add(s); break;
                case "col": Bucket(cols, n).Add(s); break;
                case "shadow": shadow.Add(s); break;
                default: bounds.Add(s); break;
            }
        }

        if (!parts.UsedConvention)
        {
            // No convention at all: the whole file is the visible model, exactly as before.
            var all = ObjMesh.FromSubMeshes(mesh.SubMeshes, mesh.MtlLibs);
            all.MergeByMaterial();
            parts.Lods.Add(all);
            return parts;
        }

        // Loose objects join LOD01 - and become it, when the scene named collision but never a LOD.
        if (loose.Count > 0)
        {
            Bucket(lods, lods.Count > 0 ? lods.Keys.First() : 1).AddRange(loose);
            parts.Unclassified.AddRange(looseNames);
        }
        // A scene that only named the extras (COL01 and a shadow, say) and left the model itself unnamed is
        // handled above; one that has no visible geometry at all gets its collision drawn, which at least shows.
        if (lods.Count == 0 && cols.Count > 0) Bucket(lods, 1).AddRange(cols.Values.First());

        foreach (var kv in lods) parts.Lods.Add(Part(kv.Value, mesh.MtlLibs));
        if (cols.TryGetValue(1, out var c1)) parts.CollisionSimple = Part(c1, null);
        if (cols.TryGetValue(2, out var c2)) parts.CollisionComplex = Part(c2, null);
        // A single collision object numbered oddly (COL03, "col") is still THE collision mesh.
        if (parts.CollisionSimple is null && parts.CollisionComplex is null && cols.Count > 0) parts.CollisionSimple = Part(cols.Values.First(), null);
        if (shadow.Count > 0) parts.Shadow = Part(shadow, null);
        if (bounds.Count > 0) parts.Bounds = Part(bounds, null);
        return parts;
    }

    static List<ObjSubMesh> Bucket(SortedDictionary<int, List<ObjSubMesh>> d, int n)
    {
        if (!d.TryGetValue(n, out var l)) { l = new List<ObjSubMesh>(); d[n] = l; }
        return l;
    }

    static ObjMesh Part(IEnumerable<ObjSubMesh> pieces, IEnumerable<string>? mtlLibs)
    {
        var m = ObjMesh.FromSubMeshes(pieces, mtlLibs);
        m.MergeByMaterial();
        return m;
    }

    /// <summary>One line for a dialog: what was found.</summary>
    public string Describe()
    {
        if (!UsedConvention) return "";
        var bits = new List<string>();
        for (int i = 0; i < Lods.Count; i++) bits.Add($"LOD{i + 1:00} {Lods[i].TotalFaces:n0}");
        if (CollisionSimple is not null) bits.Add($"COL01 {CollisionSimple.TotalFaces:n0}");
        if (CollisionComplex is not null) bits.Add($"COL02 {CollisionComplex.TotalFaces:n0}");
        if (Shadow is not null) bits.Add($"shadow {Shadow.TotalFaces:n0}");
        if (Bounds is not null) bits.Add("bbox");
        return string.Join(", ", bits);
    }
}
