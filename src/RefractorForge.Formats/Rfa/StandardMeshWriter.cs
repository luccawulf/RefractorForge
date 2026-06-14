using System;
using System.IO;
using System.Linq;
using System.Text;
using RefractorForge.Formats.Mesh;

namespace RefractorForge.Formats.Rfa;

/// <summary>
/// Writes a Refractor "StandardMesh" (<c>.sm</c>) — the inverse of <see cref="StandardMesh"/>. Emits a
/// version-10 single-LOD mesh with one material section per <see cref="ObjSubMesh"/>: interleaved 32-byte
/// vertices (pos/normal/uv, vertexFormat 1041) and a u16 triangle-list index buffer. Designed so the output
/// parses back through <see cref="StandardMesh.Parse"/> to identical geometry (gated by <c>objsm</c>).
/// </summary>
/// <remarks>
/// Two layout details mirror the reader exactly:
/// <list type="bullet">
/// <item>The reader un-reverses triangle-list winding (<c>renderType 4</c>): it emits
/// <c>(fv[i+2], fv[i+1], fv[i])</c>. So a triangle (a,b,c) is written as the index triple <c>c,b,a</c> — the
/// reader flips it back to (a,b,c).</item>
/// <item>No collision meshes and a zeroed qflag/material-settings: enough for a placeable visual mesh. (In-game
/// collision + textured materials are a later pass; an imported mesh renders, you just walk through it.)</item>
/// </list>
/// </remarks>
public static class StandardMeshWriter
{
    public static byte[] Write(ObjMesh mesh, byte[]? collisionSection = null)
    {
        var subs = mesh.SubMeshes.Where(s => s.Faces.Count > 0).ToList();
        if (subs.Count == 0) throw new InvalidDataException("Mesh has no triangles to write.");
        foreach (var s in subs)
            if (s.Positions.Count > 65535)
                throw new InvalidDataException($"Material '{s.Material}' has {s.Positions.Count} vertices (>65535 u16 limit; split the mesh).");

        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);   // BinaryWriter is little-endian, matching StandardMesh's LE reads

        w.Write((uint)10);                          // version
        w.Write(new byte[4]);                       // unknown (0)
        for (int i = 0; i < 6; i++) w.Write(mesh.BoundingBox[i]);   // bbox minX,minY,minZ, maxX,maxY,maxZ
        w.Write((byte)0);                           // qflag (version 10)
        if (collisionSection is { Length: > 0 })    // numCollisionMeshes + {u32 size; section bytes}
        { w.Write((uint)1); w.Write((uint)collisionSection.Length); w.Write(collisionSection); }
        else w.Write((uint)0);
        w.Write((uint)1);                           // numLods

        // LOD 0: all material headers first, then all geometry.
        w.Write((uint)subs.Count);
        foreach (var s in subs)
        {
            var name = Encoding.Latin1.GetBytes(s.Material);
            w.Write((uint)name.Length); w.Write(name);
            w.Write(new byte[12]);                  // unknown (0)
            w.Write((uint)4);                       // renderType: triangle list
            w.Write((uint)1041);                    // vertexFormat: pos/normal/uv
            w.Write((uint)32);                      // vertexByteSize
            w.Write((uint)s.Positions.Count);       // numVertices
            w.Write((uint)(s.Faces.Count * 3));     // numFaceValues
            w.Write((uint)0);                       // materialSettings
        }
        foreach (var s in subs)
        {
            for (int i = 0; i < s.Positions.Count; i++)
            {
                var p = s.Positions[i]; var n = s.Normals[i]; var uv = s.Uvs[i];
                w.Write(p.X); w.Write(p.Y); w.Write(p.Z);
                w.Write(n.X); w.Write(n.Y); w.Write(n.Z);
                w.Write(uv.U); w.Write(uv.V);
            }
            foreach (var (a, b, c) in s.Faces) { w.Write((ushort)c); w.Write((ushort)b); w.Write((ushort)a); }   // reversed winding
        }

        w.Flush();
        return ms.ToArray();
    }

    public static void WriteFile(ObjMesh mesh, string path) => File.WriteAllBytes(path, Write(mesh));

    /// <summary>Re-emit a collision section from its full parsed form (<see cref="StandardMesh.CollisionData"/>):
    /// header + vertex block (4 f32 each) + triangle block (4 u16 each) + the BSP/DShape <c>Tail</c> verbatim.
    /// Round-trips a real section byte-exact (gated by <c>objsm</c> / the <c>smcol</c> survey). Generating a tail
    /// for brand-new geometry is still open RE (see <c>docs/SM_Collision_RE.md</c>), so this needs a real tail.</summary>
    public static byte[] WriteCollisionSection(StandardMesh.CollisionData d)
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(0xEB97C2FAu);
        w.Write(d.Version);
        w.Write((uint)d.VertexCount);
        foreach (var f in d.Verts) w.Write(f);
        w.Write((uint)d.TriangleCount);
        foreach (var u in d.Tris) w.Write(u);
        w.Write(d.Tail);
        return ms.ToArray();
    }

    /// <summary>
    /// Build a StandardMesh collision section ("col") from raw geometry — full structure decoded from BfMeshView's
    /// source (see <c>docs/SM_Collision_RE.md</c>): u1/u2 header, 16-byte verts (float3 + w), 8-byte faces
    /// (3×i16 + matid + flags), then an **EMPTY BSP** (qnum/znum = 0). The header + vert + face blocks are exact;
    /// the BSP/index tail is left empty because its node semantics are still unsolved (BfMeshView skips it too).
    /// <b>EXPERIMENTAL</b>: whether BFV rebuilds the BSP from the faces at load, or requires a real one, must be
    /// confirmed in-game. Parses back through <see cref="StandardMesh.TryParseCollision"/> (self-consistent).
    /// </summary>
    public static byte[] BuildCollisionSection(IReadOnlyList<RefractorForge.Formats.Geometry.Vec3> verts, IReadOnlyList<(int A, int B, int C)> tris)
    {
        if (verts.Count > 32767) throw new InvalidDataException($"Collision needs <= 32767 vertices (got {verts.Count}); simplify the mesh.");
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(0xEB97C2FAu);                                   // u1
        w.Write((uint)5);                                       // u2
        w.Write((uint)verts.Count);
        foreach (var v in verts) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); w.Write(v.X); }   // colvert: float3 + w (mirrors x)
        w.Write((uint)tris.Count);
        foreach (var (a, b, c) in tris) { w.Write((ushort)a); w.Write((ushort)b); w.Write((ushort)c); w.Write((byte)0); w.Write((byte)0); }  // colface
        w.Write((uint)0);                                       // qnum (empty BSP)
        w.Write((uint)1);                                       // u3
        w.Write((uint)0);                                       // flags
        w.Write(new byte[24]);                                  // ustr (empty)
        w.Write((uint)0);                                       // znum (empty index list)
        w.Write((ushort)0);                                     // u4
        return ms.ToArray();
    }

    /// <summary>Build an experimental collision section from an imported OBJ (all submeshes flattened into one
    /// vertex pool). Null if empty or beyond the 16-bit collision-index limit.</summary>
    public static byte[]? BuildObjCollision(ObjMesh mesh)
    {
        var verts = new List<RefractorForge.Formats.Geometry.Vec3>();
        var tris = new List<(int, int, int)>();
        foreach (var s in mesh.SubMeshes)
        {
            int b = verts.Count;
            verts.AddRange(s.Positions);
            foreach (var (a, bb, c) in s.Faces) tris.Add((b + a, b + bb, b + c));
        }
        if (tris.Count == 0 || verts.Count > 32767) return null;
        return BuildCollisionSection(verts, tris);
    }
}
