using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Mesh;

namespace RefractorForge.Formats.Rfa;

/// <summary>
/// Writes a Refractor "StandardMesh" (<c>.sm</c>) — the inverse of <see cref="StandardMesh"/>. Emits a
/// version-10 mesh with one material section per <see cref="ObjSubMesh"/> per LOD: interleaved 32-byte vertices
/// (pos/normal/uv, vertexFormat 1041) and a u16 triangle-list index buffer, optional collision sections and an
/// optional shadow mesh. Designed so the output parses back through <see cref="StandardMesh.Parse"/> to identical
/// geometry (gated by <c>objsm</c>).
/// </summary>
/// <remarks>
/// The layout follows the Battlefield toolkit's own exporter (Rexman's MAXScript, in the Mod Development Toolkit)
/// byte for byte where it matters, because that exporter's output is what the game has been loading for twenty
/// years:
/// <list type="bullet">
/// <item>The reader un-reverses triangle-list winding (<c>renderType 4</c>): it emits
/// <c>(fv[i+2], fv[i+1], fv[i])</c>. So a triangle (a,b,c) is written as the index triple <c>c,b,a</c> — the
/// reader flips it back to (a,b,c). The engine reads (a,b,c) clockwise-from-outside; see
/// <see cref="MeshFitOptions.FrontFaces"/> for how an import gets there.</item>
/// <item>Collision is a "SimpleBSP" section, one node per face - see <see cref="BuildCollisionSection"/>.</item>
/// <item>The file ends with the shadow block (<c>u32 0</c> when there is none) and an empty portal chunk
/// (<c>u32 0</c>): <c>0, 0</c> is how 699 of BfVietnam's 1,997 meshes end.</item>
/// </list>
/// </remarks>
public static class StandardMeshWriter
{
    public static byte[] Write(ObjMesh mesh, byte[]? collisionSection = null)
        => Write(new[] { mesh }, collisionSection);

    public static byte[] Write(IReadOnlyList<ObjMesh> lods, byte[]? collisionSection = null)
        => Write(lods, collisionSection is { Length: > 0 } ? new[] { collisionSection } : Array.Empty<byte[]>(), null, null, null);

    /// <summary>
    /// Write a mesh with several levels of detail — <paramref name="lods"/> in order, coarsest last. The engine
    /// picks one by the <c>setLodDistance</c> ramp in the object's Geometries.con, so writing several here and
    /// pointing the ramp at them is what actually makes a distant object cheap; a single-LOD mesh with a full ramp
    /// (which is what a generated object used to be) draws every triangle right out to the cull distance.
    /// </summary>
    /// <param name="collisionSections">Zero, one (COL01) or two (COL01 simple, COL02 complex) sections from
    /// <see cref="BuildCollisionSection"/>.</param>
    /// <param name="shadow">The real-time shadow mesh, or null. Written the way the exporter writes it: as one
    /// unlit, untextured material section after the LODs, with its faces exploded (three vertices per triangle).</param>
    /// <param name="shadowName">The shadow section's material name; the exporter uses the mesh name plus "_".</param>
    /// <param name="bounds">Six floats (min xyz, max xyz) for the header's box; null takes LOD 0's own box. A
    /// decimated copy's box is slightly smaller, which would make the whole object cull early, so it is never a
    /// coarser LOD's.</param>
    public static byte[] Write(IReadOnlyList<ObjMesh> lods, IReadOnlyList<byte[]> collisionSections, ObjMesh? shadow, string? shadowName, float[]? bounds)
    {
        if (lods.Count == 0) throw new InvalidDataException("No LOD given to write.");
        var levels = new List<List<ObjSubMesh>>(lods.Count);
        foreach (var lod in lods)
        {
            var subs = lod.SubMeshes.Where(s => s.Faces.Count > 0).ToList();
            if (subs.Count == 0) throw new InvalidDataException("Mesh has no triangles to write.");
            foreach (var s in subs)
                if (s.Positions.Count > 65535)
                    throw new InvalidDataException($"Material '{s.Material}' has {s.Positions.Count} vertices (>65535 u16 limit; split the mesh).");
            levels.Add(subs);
        }

        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);   // BinaryWriter is little-endian, matching StandardMesh's LE reads

        w.Write((uint)10);                          // version
        w.Write(new byte[4]);                       // unknown (0)
        var box = bounds is { Length: 6 } ? bounds : lods[0].BoundingBox;
        for (int i = 0; i < 6; i++) w.Write(box[i]);   // bbox minX,minY,minZ, maxX,maxY,maxZ
        w.Write((byte)0);                           // qflag (version 10): 1 would declare a lightmap channel
        var cols = collisionSections.Where(c => c is { Length: > 0 }).ToList();
        w.Write((uint)cols.Count);                  // numCollisionMeshes + {u32 size; section bytes}
        foreach (var c in cols) { w.Write((uint)c.Length); w.Write(c); }
        w.Write((uint)levels.Count);                // numLods

        // Per LOD: all material headers first, then all geometry.
        foreach (var subs in levels)
        {
            w.Write((uint)subs.Count);
            foreach (var s in subs) WriteMaterialHeader(w, s.Material, s.Positions.Count, s.Faces.Count * 3);
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
        }

        // The shadow block, then the portal chunk. Grammar (from the exporter, and every DICE mesh agrees):
        //   u32 hasShadow; [u32 numLods=1; u32 numMaterials=1; material header; vertices; faces]; u32 portalSize; ...
        WriteShadow(w, shadow, shadowName ?? "shadow_");
        w.Write((uint)0);                           // portal mesh chunk: none

        w.Flush();
        return ms.ToArray();
    }

    // name, then the 12 bytes the reader skips (the name's NUL + u8 + u16 + u32 + u32, all zero), then the
    // render type / vertex format / stride / counts / material settings.
    static void WriteMaterialHeader(BinaryWriter w, string material, int numVertices, int numFaceValues)
    {
        var name = Encoding.Latin1.GetBytes(material);
        w.Write((uint)name.Length); w.Write(name);
        w.Write(new byte[12]);                      // NUL, u8 0, u16 0, u32 0, u32 0
        w.Write((uint)4);                           // renderType: triangle list
        w.Write((uint)1041);                        // vertexFormat: pos/normal/uv
        w.Write((uint)32);                          // vertexByteSize
        w.Write((uint)numVertices);
        w.Write((uint)numFaceValues);
        w.Write((uint)0);                           // materialSettings
    }

    /// <summary>
    /// The shadow mesh, exactly as the toolkit's exporter writes it (<c>exportSM_Shad</c>, confirmed against
    /// Saigon68's VSS_Crate): <c>u32 1, u32 1, u32 1</c>, a normal material header named <c>&lt;mesh&gt;_</c>, then
    /// 32-byte vertices carrying only a position (normal and uv zero), and the faces in the SAME order as the
    /// visible mesh's parsed order (the exporter does not reverse these). The exporter explodes the faces first -
    /// three vertices per triangle, none shared - and the engine has only ever seen it that way, so this does too.
    /// </summary>
    static void WriteShadow(BinaryWriter w, ObjMesh? shadow, string name)
    {
        var tris = new List<(Vec3 A, Vec3 B, Vec3 C)>();
        if (shadow is not null)
            foreach (var s in shadow.SubMeshes)
                foreach (var (a, b, c) in s.Faces)
                    tris.Add((s.Positions[a], s.Positions[b], s.Positions[c]));
        if (tris.Count == 0) { w.Write((uint)0); return; }
        if (tris.Count * 3 > 65535) throw new InvalidDataException($"Shadow mesh has {tris.Count} triangles; the exploded form needs at most 21,845.");

        w.Write((uint)1);                           // has shadow
        w.Write((uint)1);                           // one shadow LOD
        w.Write((uint)1);                           // one material
        WriteMaterialHeader(w, name, tris.Count * 3, tris.Count * 3);
        foreach (var (a, b, c) in tris)
            foreach (var p in new[] { a, b, c })
            {
                w.Write(p.X); w.Write(p.Y); w.Write(p.Z);
                w.Write(new byte[20]);              // normal + uv: the exporter writes five zero longs
            }
        for (int i = 0; i < tris.Count * 3; i++) w.Write((ushort)i);
    }

    public static void WriteFile(ObjMesh mesh, string path) => File.WriteAllBytes(path, Write(mesh));

    /// <summary>Re-emit a collision section from its full parsed form (<see cref="StandardMesh.CollisionData"/>):
    /// header + vertex block (4 f32 each) + triangle block (4 u16 each) + the BSP <c>Tail</c> verbatim.
    /// Round-trips a real section byte-exact (gated by <c>objsm</c> / the <c>smcol</c> survey).</summary>
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

    /// <summary>The material a collision mesh is made of when nothing better is known - the engine's numbered
    /// material table (<c>game/materialManagerdefine.con</c>), where this is the id retail collision sections use
    /// more than any other. See <see cref="Con.CollisionMaterials"/> for the named choices.</summary>
    public const int DefaultCollisionMaterial = 88;

    /// <summary>The 24-byte marker the toolkit's exporter leaves in every collision section it writes.</summary>
    public const string SimpleBspMarker = "SimpleBSP tree method  ";

    /// <summary>
    /// Build a collision section from raw geometry - the toolkit exporter's "SimpleBSP", byte for byte.
    /// <para>
    /// The section's BSP was the last undecoded piece of the format, and it turns out to be one node per face:
    /// the face's plane normal, a zero, its three vertex indices and its material. DICE's own <c>DShape</c>
    /// sections carry the same per-face nodes (a real tree only adds structure in the counts and the index list),
    /// and the trivial tree - node count = face count, index list 0..n-1, the marker string - is what Rexman's
    /// exporter writes and what Saigon68's Desert Combat props and three retail meshes load with. Decoded from
    /// <c>exportSM_Col</c> + <c>WriteSimpleBsp</c> in the toolkit's MAXScript and verified against those files.
    /// </para><para>
    /// Two conventions matter. The vertex's fourth float is its X with the low sixteen bits overwritten by the
    /// material id (the exporter seeks back two bytes and writes a short over it). And the node's normal is the
    /// LEFT-hand normal of the triangle - the engine reads a face clockwise-from-outside, so this is the outward
    /// one; DC_roadbarrier1's floor face (0,2,3) carries (0,-1,0). Faces are written in the mesh's own order,
    /// which after <see cref="MeshFit"/> is that clockwise order.
    /// </para>
    /// </summary>
    /// <param name="materialId">The engine material the surface is made of: what a bullet sounds like on it and
    /// what damage it takes. <see cref="Con.CollisionMaterials"/> lists them by name.</param>
    public static byte[] BuildCollisionSection(IReadOnlyList<Vec3> verts, IReadOnlyList<(int A, int B, int C)> tris, int materialId = DefaultCollisionMaterial)
    {
        if (verts.Count > 32767) throw new InvalidDataException($"Collision needs <= 32767 vertices (got {verts.Count}); simplify the mesh.");
        ushort mat = (ushort)Math.Clamp(materialId, 0, 65535);
        using var ms = new MemoryStream(58 + 16 * verts.Count + 44 * tris.Count);
        var w = new BinaryWriter(ms);
        w.Write(0xEB97C2FAu);                                   // u1
        w.Write((uint)5);                                       // u2
        w.Write((uint)verts.Count);
        foreach (var v in verts)
        {
            w.Write(v.X); w.Write(v.Y); w.Write(v.Z);
            uint xbits = (uint)BitConverter.SingleToInt32Bits(v.X);
            w.Write((xbits & 0xFFFF0000u) | mat);               // x again, with the material in its low half
        }
        w.Write((uint)tris.Count);
        foreach (var (a, b, c) in tris) { w.Write((ushort)a); w.Write((ushort)b); w.Write((ushort)c); w.Write(mat); }

        // The SimpleBSP: one node per face.
        w.Write((uint)tris.Count);                              // node count
        w.Write((uint)0);
        w.Write((uint)tris.Count);
        foreach (var (a, b, c) in tris)
        {
            var pa = verts[a]; var pb = verts[b]; var pc = verts[c];
            float ux = pb.X - pa.X, uy = pb.Y - pa.Y, uz = pb.Z - pa.Z;
            float wx = pc.X - pa.X, wy = pc.Y - pa.Y, wz = pc.Z - pa.Z;
            float nx = -(uy * wz - uz * wy), ny = -(uz * wx - ux * wz), nz = -(ux * wy - uy * wx);   // left-hand: outward for a clockwise face
            float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len > 1e-12f) { nx /= len; ny /= len; nz /= len; } else { nx = ny = nz = 0f; }
            w.Write(nx); w.Write(ny); w.Write(nz);
            w.Write((uint)0);
            w.Write((uint)a); w.Write((uint)b); w.Write((uint)c);
            w.Write((uint)mat);
        }
        var marker = Encoding.ASCII.GetBytes(SimpleBspMarker);
        w.Write(marker); w.Write(new byte[24 - marker.Length]); // 23 characters + NUL
        w.Write((uint)tris.Count);                              // index list: every face, in order
        for (int i = 0; i < tris.Count; i++) w.Write((uint)i);
        w.Write((ushort)0);
        return ms.ToArray();
    }

    /// <summary>Build a collision section from a mesh (all pieces flattened into one vertex pool). Null if empty
    /// or beyond the section's vertex limit.</summary>
    public static byte[]? BuildObjCollision(ObjMesh mesh, int materialId = DefaultCollisionMaterial)
    {
        var verts = new List<Vec3>();
        var tris = new List<(int, int, int)>();
        foreach (var s in mesh.SubMeshes)
        {
            int b = verts.Count;
            verts.AddRange(s.Positions);
            foreach (var (a, bb, c) in s.Faces) tris.Add((b + a, b + bb, b + c));
        }
        if (tris.Count == 0 || verts.Count > 32767) return null;
        return BuildCollisionSection(verts, tris, materialId);
    }
}
