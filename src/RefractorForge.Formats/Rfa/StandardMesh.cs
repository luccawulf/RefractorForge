using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RefractorForge.Formats.Geometry;

namespace RefractorForge.Formats.Rfa;

/// <summary>One material section of a <see cref="StandardMesh"/> LOD: a vertex array plus its index list.</summary>
public sealed class SmMaterial
{
    public string Name { get; init; } = "";
    /// <summary>4 = triangle list, 5 = triangle strip.</summary>
    public uint RenderType { get; init; }
    /// <summary>Vertex declaration id (1041 = pos/normal/uv, 9233 = + lightmap uv).</summary>
    public uint VertexFormat { get; init; }
    /// <summary>Bytes per vertex (32 = pos+normal+uv, 40 = + lightmap uv).</summary>
    public uint VertexByteSize { get; init; }
    public int NumVertices { get; init; }
    public int NumFaceValues { get; init; }
    public uint MaterialSettings { get; init; }

    /// <summary>Vertex positions in engine space (the file is Y-up; positions are used as-is).</summary>
    public Vec3[] Vertices { get; init; } = Array.Empty<Vec3>();
    /// <summary>Per-vertex normals in engine space (used as-is; no axis swap).</summary>
    public Vec3[] Normals { get; init; } = Array.Empty<Vec3>();
    /// <summary>Per-vertex (u, v) texture coordinates.</summary>
    public (float U, float V)[] Uvs { get; init; } = Array.Empty<(float, float)>();
    /// <summary>Per-vertex 2nd (u, v): the baked-object-lightmap UV (present on 40-byte / format-9233 vertices),
    /// or (0,0) when the mesh has no lightmap channel. Used to sample the level's ObjectLightMaps/*.tga.</summary>
    public (float U, float V)[] LightmapUvs { get; init; } = Array.Empty<(float, float)>();
    /// <summary>True when this material's vertices carry a lightmap UV channel (40-byte / format 9233).</summary>
    public bool HasLightmapUv { get; init; }
    /// <summary>For 64-byte PLANAR vertices: the trailing per-vertex extra block as floats (tangent frame + 2nd UV),
    /// (vbs-32)/4 floats per vertex. Captured raw so the 2nd-UV offset can be located. Null for non-planar meshes.</summary>
    public float[]? PlanarExtra { get; init; }
    /// <summary>Triangles as vertex-index triples (already wound for engine display).</summary>
    public (int A, int B, int C)[] Faces { get; init; } = Array.Empty<(int, int, int)>();
}

/// <summary>
/// Clean-room parser for Refractor "StandardMesh" (<c>.sm</c>) geometry — the static-prop and
/// kit-mesh format used by Battlefield 1942 / Vietnam.
/// </summary>
/// <remarks>
/// <para>The byte layout was derived from the community BF1942 Blender add-on
/// (<c>Ahrkylien/BF1942-Blender-add-on</c>, <c>standard_mesh.py</c>), used purely as a
/// <i>format reference</i>. The parser is independent code and ships under the project's own
/// permissive license. Output was validated against every <c>.sm</c> in the retail
/// <c>standardMesh.rfa</c> (1999 meshes) and <c>objects.rfa</c>, matching a Python reference
/// implementation field-for-field (version, LOD count, material/vertex/face counts, byte cursor).</para>
/// <para>Layout:</para>
/// <code>
/// u32 version                  (10 typical, 9 for some kits)
/// 4   bytes  unknown (=0)
/// 6   f32    boundingBox       (minX,minY,minZ, maxX,maxY,maxZ in file order)
/// if version==10: u8 qflag
/// u32 numCollisionMeshes,  per mesh { u32 sizeOfSection; skip sizeOfSection bytes }
/// u32 numLods, per LOD:
///     u32 numMaterials
///     numMaterials x materialHeader            (all headers first)
///     numMaterials x materialVertexAndFaceData (then all geometry)
///
/// materialHeader : u32 nameLen, char[nameLen] name, 12 bytes unknown(=0),
///                  u32 renderType, u32 vertexFormat, u32 vertexByteSize,
///                  u32 numVertices, u32 numFaceValues, u32 materialSettings
/// vertex          : 32B = 3 f32 pos, 3 f32 normal, 2 f32 uv. 40B = + 2 f32 lightmap uv (interleaved).
///                   64B = *planar*: nv contiguous 32B pos/normal/uv records, then nv*32B extra.
/// faces          : numFaceValues x u16 indices;
///                  renderType 5 -> sequential triangle strip, else triangle list (reversed winding).
/// </code>
/// <para>The format is Y-up with object origin at the base, matching the engine/terrain, so
/// positions are used directly (no axis swap — earlier Blender-derived code swapped Y/Z because
/// Blender is Z-up, which incorrectly laid tall props on their side).</para>
/// </remarks>
public sealed class StandardMesh
{
    public uint Version { get; private init; }
    /// <summary>Authored bounding box in file order: minX, minY, minZ, maxX, maxY, maxZ.</summary>
    public float[] BoundingBox { get; private init; } = new float[6];
    public int NumCollisionMeshes { get; private init; }
    /// <summary>The raw bytes of each collision mesh section (not yet decoded — kept for reverse-engineering /
    /// re-writing the collision format). One entry per <see cref="NumCollisionMeshes"/>.</summary>
    public IReadOnlyList<byte[]> CollisionSections { get; private init; } = Array.Empty<byte[]>();
    public int NumLods { get; private init; }
    /// <summary>Materials per LOD (LOD0 first). Each inner list is one level of detail.</summary>
    public IReadOnlyList<IReadOnlyList<SmMaterial>> Lods { get; private init; } = Array.Empty<IReadOnlyList<SmMaterial>>();
    /// <summary>Bytes consumed by the parser (should be &lt;= <see cref="Total"/>; trailing bytes are tolerated).</summary>
    public int Consumed { get; private init; }
    /// <summary>Total input length.</summary>
    public int Total { get; private init; }

    public static StandardMesh Parse(byte[] buf) => Parse((ReadOnlySpan<byte>)buf);

    public static StandardMesh Parse(ReadOnlySpan<byte> buf)
    {
        int p = 0;
        uint version = U32(buf, ref p);
        p += 4; // unknown (0,0,0,0)
        var bbox = new float[6];
        for (int i = 0; i < 6; i++) bbox[i] = F32(buf, ref p);

        if (version == 10) p += 1;            // qflag (u8)
        else if (version != 9) throw new InvalidDataException($"Unexpected .sm version {version}.");

        int numCol = (int)U32(buf, ref p);
        var col = new List<byte[]>(numCol);
        for (int i = 0; i < numCol; i++)
        {
            int size = (int)U32(buf, ref p);
            col.Add(size > 0 && p + size <= buf.Length ? buf.Slice(p, size).ToArray() : Array.Empty<byte>());
            p += size;                        // collision section captured (raw, not yet decoded) then skipped
        }

        int numLods = (int)U32(buf, ref p);
        var lods = new List<IReadOnlyList<SmMaterial>>(numLods);
        for (int l = 0; l < numLods; l++)
        {
            int numMat = (int)U32(buf, ref p);

            // Pass 1: all material headers.
            var hdrs = new (string name, uint rt, uint vf, uint vbs, int nv, int nfv, uint ms)[numMat];
            for (int m = 0; m < numMat; m++)
            {
                int nlen = (int)U32(buf, ref p);
                string name = Encoding.Latin1.GetString(buf.Slice(p, nlen)); p += nlen;
                p += 12;                                   // unknown (12 bytes, 0)
                uint rt  = U32(buf, ref p);
                uint vf  = U32(buf, ref p);
                uint vbs = U32(buf, ref p);
                int nv   = (int)U32(buf, ref p);
                int nfv  = (int)U32(buf, ref p);
                uint ms  = U32(buf, ref p);
                hdrs[m] = (name, rt, vf, vbs, nv, nfv, ms);
            }

            // Pass 2: vertex + face data for each material.
            var mats = new List<SmMaterial>(numMat);
            foreach (var h in hdrs)
            {
                // vertexByteSize is the authoritative stride for the whole vertex region (nv*vbs).
                // 32B and 40B vertices are *interleaved*: pos/normal/uv occupy the first 32 bytes,
                // and any remainder (lightmap uv at 40B) trails each vertex.
                // 64B vertices use a *planar* layout instead: nv contiguous 32-byte pos/normal/uv
                // records, followed by a separate nv*(vbs-32) block of extra channels (tangent frame
                // / second uv). Reading those as interleaved 64-byte strides samples every other
                // vertex and then runs into the extra block — which is what scrambled these meshes.
                int extra = (int)h.vbs - 32;
                if (extra < 0) throw new InvalidDataException($"Unexpected vertexByteSize {h.vbs} (<32) for material '{h.name}'.");
                bool planar = h.vbs == 64;
                var verts = new Vec3[h.nv];
                var norms = new Vec3[h.nv];
                var uvs   = new (float, float)[h.nv];
                var lmuvs = new (float, float)[h.nv];      // 2nd uv (object lightmap), (0,0) when absent
                // The lightmap (2nd) uv is the FIRST 8 bytes of the vertex's "extra": for 40B interleaved (BF1942,
                // format 9233) it trails the diffuse uv per vertex; for 64B PLANAR (BF Vietnam, vf 1041 vbs 64) it's the
                // first two floats of each vertex's trailing 32-byte extra block (the rest is the tangent frame). Both
                // confirmed against real meshes (French_Barn_Lrg_M1 40B, O_BurntHut01_M1 64B). Reading it is what makes
                // BFV object lightmaps work.
                bool hasLm = extra >= 8;
                for (int v = 0; v < h.nv; v++)
                {
                    float vx = F32(buf, ref p), vy = F32(buf, ref p), vz = F32(buf, ref p);
                    verts[v] = new Vec3(vx, vy, vz);       // file is already Y-up; no axis swap
                    float nx = F32(buf, ref p), ny = F32(buf, ref p), nz = F32(buf, ref p);
                    norms[v] = new Vec3(nx, ny, nz);
                    float u = F32(buf, ref p), w = F32(buf, ref p);
                    uvs[v] = (u, w);
                    if (!planar && extra > 0)
                    {
                        if (extra >= 8) { lmuvs[v] = (F32(buf, ref p), F32(buf, ref p)); if (extra > 8) p += extra - 8; }
                        else p += extra;
                    }
                }
                float[]? planarExtra = null;
                if (planar && extra > 0)
                {
                    int fpv = extra / 4;                   // floats per vertex in the trailing extra block
                    planarExtra = new float[h.nv * fpv];
                    for (int k = 0; k < planarExtra.Length; k++) planarExtra[k] = F32(buf, ref p);
                    if (fpv >= 2)                          // lightmap (2nd) uv = the first two floats of each vertex's extra
                        for (int v = 0; v < h.nv; v++) lmuvs[v] = (planarExtra[v * fpv], planarExtra[v * fpv + 1]);
                }

                var fv = new int[h.nfv];
                for (int i = 0; i < h.nfv; i++) fv[i] = U16(buf, ref p);

                List<(int, int, int)> faces;
                if (h.rt == 5)                              // triangle strip (naive expansion)
                {
                    faces = new List<(int, int, int)>(Math.Max(0, h.nfv - 2));
                    for (int i = 0; i < h.nfv - 2; i++)
                        faces.Add((fv[i], fv[i + 1], fv[i + 2]));
                }
                else                                        // triangle list, reversed winding
                {
                    faces = new List<(int, int, int)>(h.nfv / 3);
                    for (int i = 0; i + 2 < h.nfv; i += 3)
                        faces.Add((fv[i + 2], fv[i + 1], fv[i]));
                }

                mats.Add(new SmMaterial
                {
                    Name = h.name, RenderType = h.rt, VertexFormat = h.vf, VertexByteSize = h.vbs,
                    NumVertices = h.nv, NumFaceValues = h.nfv, MaterialSettings = h.ms,
                    Vertices = verts, Normals = norms, Uvs = uvs, Faces = faces.ToArray(),
                    LightmapUvs = lmuvs, HasLightmapUv = hasLm, PlanarExtra = planarExtra,
                });
            }
            lods.Add(mats);
        }

        return new StandardMesh
        {
            Version = version, BoundingBox = bbox, NumCollisionMeshes = numCol, CollisionSections = col,
            NumLods = numLods, Lods = lods, Consumed = p, Total = buf.Length,
        };
    }

    /// <summary>
    /// Parse without throwing. Returns <c>false</c> for malformed input so a single bad asset
    /// never takes down a load. (Two muzzle-flash meshes in the retail <c>standardMesh.rfa</c>
    /// are NUL-to-space corrupted in EA's shipped data and legitimately fail here; the LZO
    /// decode of those files is still byte-exact — the defect is in the source bytes, not the codec.)
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> buf, out StandardMesh? mesh)
    {
        try { mesh = Parse(buf); return true; }
        catch { mesh = null; return false; }
    }

    /// <summary>
    /// Decode a <see cref="CollisionSections"/> entry into renderable vertices + triangles, or <c>false</c> if it
    /// doesn't match the known layout (see <c>docs/SM_Collision_RE.md</c>). Layout:
    /// <code>
    /// u32 magic 0xEB97C2FA ; u32 version(=5) ; u32 numVerts
    /// vert[numVerts]  — 16 bytes each: f32 x, y, z, w   (w unused for the shape)
    /// u32 numTris
    /// tri[numTris]    — u16 i0, i1, i2, u16 separator   (8 bytes each)
    /// </code>
    /// The trailing BSP / named buffers ("DShape…") are ignored — verts + tris are enough to draw the collision.
    /// </summary>
    public static bool TryParseCollision(ReadOnlySpan<byte> sec, out Vec3[] verts, out int[] triIndices)
    {
        verts = Array.Empty<Vec3>(); triIndices = Array.Empty<int>();
        try
        {
            if (sec.Length < 16) return false;
            int p = 0;
            if (U32(sec, ref p) != 0xEB97C2FA) return false;
            _ = U32(sec, ref p);                                  // version (5)
            int nv = (int)U32(sec, ref p);
            if (nv < 0 || nv > 1_000_000 || (long)p + (long)nv * 16 > sec.Length) return false;
            var vs = new Vec3[nv];
            for (int i = 0; i < nv; i++)
            {
                float x = F32(sec, ref p), y = F32(sec, ref p), z = F32(sec, ref p);
                p += 4;                                           // w — not part of the shape
                vs[i] = new Vec3(x, y, z);
            }
            if (p + 4 > sec.Length) return false;
            int nt = (int)U32(sec, ref p);
            if (nt < 0 || nt > 5_000_000 || (long)p + (long)nt * 8 > sec.Length) return false;
            var idx = new int[nt * 3];
            for (int i = 0; i < nt; i++)
            {
                int a = U16(sec, ref p), b = U16(sec, ref p), c = U16(sec, ref p);
                p += 2;                                           // per-triangle separator (material/edge marker)
                if ((uint)a >= (uint)nv || (uint)b >= (uint)nv || (uint)c >= (uint)nv) return false;
                idx[i * 3] = a; idx[i * 3 + 1] = b; idx[i * 3 + 2] = c;
            }
            verts = vs; triIndices = idx;
            return true;
        }
        catch { return false; }
    }

    /// <summary>The full, loss-less contents of a collision section: header version, the vertex block (4 floats
    /// each: x,y,z,w), the triangle block (4 u16 each: i0,i1,i2,separator), and the still-undecoded BSP/DShape
    /// <see cref="Tail"/> bytes. Captures everything needed to re-emit the section byte-exact.</summary>
    public sealed class CollisionData
    {
        public uint Version;
        public float[] Verts = Array.Empty<float>();    // 4 per vertex
        public ushort[] Tris = Array.Empty<ushort>();   // 4 per triangle
        public byte[] Tail = Array.Empty<byte>();        // the BSP / serialized-DShape remainder (verbatim)
        public int VertexCount => Verts.Length / 4;
        public int TriangleCount => Tris.Length / 4;
    }

    /// <summary>Loss-less parse of a collision section (header + verts + tris + the raw BSP/DShape tail), for
    /// round-tripping and as the basis of a future writer. Returns false if the header/boundaries don't match.</summary>
    public static bool TryParseCollisionFull(ReadOnlySpan<byte> sec, out CollisionData data)
    {
        data = new CollisionData();
        try
        {
            if (sec.Length < 12) return false;
            int p = 0;
            if (U32(sec, ref p) != 0xEB97C2FA) return false;
            data.Version = U32(sec, ref p);
            int nv = (int)U32(sec, ref p);
            if (nv < 0 || (long)p + (long)nv * 16 > sec.Length) return false;
            var vf = new float[nv * 4];
            for (int i = 0; i < vf.Length; i++) vf[i] = F32(sec, ref p);
            data.Verts = vf;
            if (p + 4 > sec.Length) return false;
            int nt = (int)U32(sec, ref p);
            if (nt < 0 || (long)p + (long)nt * 8 > sec.Length) return false;
            var tu = new ushort[nt * 4];
            for (int i = 0; i < tu.Length; i++) tu[i] = (ushort)U16(sec, ref p);
            data.Tris = tu;
            data.Tail = sec.Slice(p).ToArray();
            return true;
        }
        catch { return false; }
    }

    /// <summary>(materialCount, totalVertices, totalFaces) for LOD0 — the highest detail level.</summary>
    public (int Materials, int Vertices, int Faces) Lod0Counts()
    {
        if (Lods.Count == 0) return (0, 0, 0);
        var mats = Lods[0];
        int nv = 0, nf = 0;
        foreach (var m in mats) { nv += m.NumVertices; nf += m.Faces.Length; }
        return (mats.Count, nv, nf);
    }

    private static uint U32(ReadOnlySpan<byte> b, ref int p)
    { uint v = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(p)); p += 4; return v; }

    private static int U16(ReadOnlySpan<byte> b, ref int p)
    { int v = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(p)); p += 2; return v; }

    private static float F32(ReadOnlySpan<byte> b, ref int p)
    { float v = BinaryPrimitives.ReadSingleLittleEndian(b.Slice(p)); p += 4; return v; }
}
