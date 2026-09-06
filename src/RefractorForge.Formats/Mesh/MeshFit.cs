using System;
using System.Collections.Generic;
using RefractorForge.Formats.Geometry;

namespace RefractorForge.Formats.Mesh;

/// <summary>Which way is up in the file being imported.</summary>
public enum UpAxis
{
    /// <summary>Already Refractor's convention — what Blender's OBJ exporter writes by default.</summary>
    Y,
    /// <summary>Blender's own world axes, which FBX / glTF / <c>.blend</c> imports arrive in. Rotated on import.</summary>
    Z,
}

/// <summary>How to size the import.</summary>
public enum FitMode
{
    /// <summary>Trust the file's units, optionally multiplied by <see cref="MeshFitOptions.Scale"/>.</summary>
    AsAuthored,
    /// <summary>Scale so the model's HEIGHT is <see cref="MeshFitOptions.TargetMeters"/> — how you size a
    /// building, a tree or a figure, where height is the number you actually know.</summary>
    Height,
    /// <summary>Scale so the LONGEST side is <see cref="MeshFitOptions.TargetMeters"/> — how you size a vehicle or
    /// a gun, which are defined by their length.</summary>
    LongestSide,
}

/// <summary>Where the model's origin ends up, which is the point it rotates about and the point you place.</summary>
public enum OriginMode
{
    /// <summary>Centred horizontally with its lowest point at y=0 — the format's convention for anything that
    /// stands on the ground, and what lets the editor drop it onto the terrain.</summary>
    Base,
    /// <summary>The centre of the bounding box: right for something that hangs, spins or flies.</summary>
    Center,
    /// <summary>Leave the authored origin alone — for a model whose pivot was placed deliberately.</summary>
    Keep,
}

public sealed class MeshFitOptions
{
    public UpAxis Up = UpAxis.Y;
    public FitMode Fit = FitMode.AsAuthored;
    /// <summary>Target size in metres for <see cref="FitMode.Height"/> / <see cref="FitMode.LongestSide"/>.</summary>
    public float TargetMeters = 4f;
    /// <summary>Extra uniform scale, applied in every mode. 1 = none.</summary>
    public float Scale = 1f;
    public OriginMode Origin = OriginMode.Base;
    /// <summary>Turn the texture space over: an OBJ's image origin is bottom-left (V grows up the picture), the
    /// engine's — and the editor's, which draws retail meshes correctly — is top-left. Without this every imported
    /// texture arrives upside down, in the editor and in the game alike. On by default; off only for a mesh that
    /// already speaks the engine's convention.</summary>
    public bool FlipV = true;
}

/// <summary>
/// Lands an imported mesh in Refractor's world: right way up, right size, origin where the engine expects it.
///
/// Getting this wrong is the single most common way an import "works" and is still useless — a Blender model
/// arrives on its side (Z-up), at whatever units the author happened to work in, pivoting around a point somewhere
/// off in space. The three corrections are separable and all three matter, so they are three explicit choices
/// rather than one guess.
/// </summary>
public static class MeshFit
{
    /// <summary>What the fit actually did, so the caller can report it — a scale that comes out at 0.01 or 100
    /// almost always means the source file's units were not what the user thought.</summary>
    public readonly record struct Result(float Scale, Vec3 Offset, float Width, float Height, float Depth);

    public static Result Apply(ObjMesh mesh, MeshFitOptions o)
    {
        if (mesh.TotalVertices == 0) return default;

        // 0. Texture space. See FlipV: the picture's origin moves from the bottom-left corner to the top-left.
        if (o.FlipV) FlipV(mesh);

        // 1. Axis. Blender is Z-up with -Y forward; Refractor is Y-up with -Z forward, so (x, y, z) -> (x, z, -y).
        //    That is a proper rotation (determinant +1), so the triangle winding still means what it did and only
        //    the positions and normals move.
        if (o.Up == UpAxis.Z)
            foreach (var s in mesh.SubMeshes)
            {
                for (int i = 0; i < s.Positions.Count; i++) s.Positions[i] = ZUpToYUp(s.Positions[i]);
                for (int i = 0; i < s.Normals.Count; i++) s.Normals[i] = ZUpToYUp(s.Normals[i]);
            }

        var b = Bounds(mesh);
        float w = b.maxX - b.minX, h = b.maxY - b.minY, d = b.maxZ - b.minZ;

        // 2. Scale. A degenerate axis (a flat sheet imported as a sign) must not divide by zero.
        float scale = o.Scale <= 0f ? 1f : o.Scale;
        float target = o.TargetMeters > 0f ? o.TargetMeters : 1f;
        if (o.Fit == FitMode.Height && h > 1e-6f) scale *= target / h;
        else if (o.Fit == FitMode.LongestSide)
        {
            float longest = MathF.Max(w, MathF.Max(h, d));
            if (longest > 1e-6f) scale *= target / longest;
        }

        // 3. Origin, computed on the SCALED box so it lands exactly on zero rather than near it.
        Vec3 offset = o.Origin switch
        {
            OriginMode.Base => new Vec3(-(b.minX + b.maxX) * 0.5f * scale, -b.minY * scale, -(b.minZ + b.maxZ) * 0.5f * scale),
            OriginMode.Center => new Vec3(-(b.minX + b.maxX) * 0.5f * scale, -(b.minY + b.maxY) * 0.5f * scale, -(b.minZ + b.maxZ) * 0.5f * scale),
            _ => Vec3.Zero,
        };

        if (scale != 1f || offset != Vec3.Zero) mesh.Transform(scale, offset);
        return new Result(scale, offset, w * scale, h * scale, d * scale);
    }

    /// <summary>V' = 1 - V on every vertex: the OBJ texture convention turned into the engine's. Its own inverse,
    /// so applying it twice is a no-op — and a mesh that goes out through the OBJ export is turned back.</summary>
    public static void FlipV(ObjMesh mesh)
    {
        foreach (var s in mesh.SubMeshes)
            for (int i = 0; i < s.Uvs.Count; i++) s.Uvs[i] = (s.Uvs[i].U, 1f - s.Uvs[i].V);
    }

    private static Vec3 ZUpToYUp(Vec3 p) => new(p.X, p.Z, -p.Y);

    private static (float minX, float minY, float minZ, float maxX, float maxY, float maxZ) Bounds(ObjMesh mesh)
    {
        float minx = float.MaxValue, miny = float.MaxValue, minz = float.MaxValue;
        float maxx = float.MinValue, maxy = float.MinValue, maxz = float.MinValue;
        foreach (var s in mesh.SubMeshes)
            foreach (var p in s.Positions)
            {
                minx = MathF.Min(minx, p.X); miny = MathF.Min(miny, p.Y); minz = MathF.Min(minz, p.Z);
                maxx = MathF.Max(maxx, p.X); maxy = MathF.Max(maxy, p.Y); maxz = MathF.Max(maxz, p.Z);
            }
        return (minx, miny, minz, maxx, maxy, maxz);
    }

    /// <summary>
    /// Split any material section over the <c>.sm</c> format's 65,535-vertex ceiling into several sections, so a
    /// dense import writes instead of throwing. The pieces keep the material name — the shader binds by name, and
    /// several sections may share one — and each gets its own vertex array, which is what the limit is really about.
    /// </summary>
    public static int SplitOversizedSections(ObjMesh mesh, int maxVerts = 65535)
    {
        int splits = 0;
        for (int si = 0; si < mesh.SubMeshes.Count; si++)
        {
            var s = mesh.SubMeshes[si];
            if (s.Positions.Count <= maxVerts) continue;

            // Walk the triangles, starting a fresh section whenever adding one would cross the ceiling. Vertices are
            // re-indexed per piece, so a vertex shared across the cut is simply duplicated.
            var pieces = new List<ObjSubMesh>();
            ObjSubMesh piece = null!;
            Dictionary<int, int> remap = null!;
            void Fresh() { piece = new ObjSubMesh { Material = s.Material }; remap = new Dictionary<int, int>(); pieces.Add(piece); }
            Fresh();

            foreach (var (a, bIdx, c) in s.Faces)
            {
                int need = (remap.ContainsKey(a) ? 0 : 1) + (remap.ContainsKey(bIdx) ? 0 : 1) + (remap.ContainsKey(c) ? 0 : 1);
                if (piece.Positions.Count + need > maxVerts && piece.Faces.Count > 0) Fresh();
                piece.Faces.Add((Map(a), Map(bIdx), Map(c)));
            }

            int Map(int src)
            {
                if (remap.TryGetValue(src, out var dst)) return dst;
                dst = piece.Positions.Count;
                piece.Positions.Add(s.Positions[src]);
                piece.Normals.Add(src < s.Normals.Count ? s.Normals[src] : new Vec3(0, 1, 0));
                piece.Uvs.Add(src < s.Uvs.Count ? s.Uvs[src] : (0f, 0f));
                remap[src] = dst;
                return dst;
            }

            mesh.SubMeshes.RemoveAt(si);
            mesh.SubMeshes.InsertRange(si, pieces);
            si += pieces.Count - 1;
            splits += pieces.Count - 1;
        }
        return splits;
    }
}
