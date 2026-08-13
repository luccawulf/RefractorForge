using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using RefractorForge.Formats.Geometry;

namespace RefractorForge.Formats.Rfa;

/// <summary>
/// Parser for Battlefield 1942 TreeMesh (.tm) files - the overgrowth/foliage geometry inside treeMesh.rfa
/// (trees, bushes: trunk + leaf + sprite groups). Reverse-engineered from BfMeshView's modTreeMesh.bas.
///
/// Layout (all little-endian):
///   header   : u32 ver(=3), u32(0), u32(8)
///   bounds   : float3 min, max, min2, max2  (the geometry AABB, plus a second AABB)
///   meshes   : EXACTLY 4 groups (0 leaf, 1 trunk, 2 sprite, 3 extra). Per group: u32 matnum, then per material
///              u32 start (index offset), u32 count (triangle count), string texname (u32 len + latin1 bytes).
///   collision: u32 colflag; if != 0: u32(=5), u32 colvertnum + colvert[16B], u32 colfacenum + colface[8B],
///              u32 h_u1, u32 h_u2, u32 hnum + hdata[32B], then a recursive AABB BSP (read past, not kept).
///   geometry : u32 vertnum, vert[44B] (float3 pos, float3 normal, 4 bytes, float2 uv0, float2 uv1),
///              u32 indexnum, u16 index[].
/// Render each material group as a triangle list over index[start .. start + count*3).
/// </summary>
public sealed class TreeMesh
{
    public readonly record struct Vertex(float Px, float Py, float Pz, float Nx, float Ny, float Nz, float U, float V);
    public sealed class Material { public int Start; public int Count; public string TexName = ""; }

    public uint Version;
    public Vec3 Min, Max;
    public List<Material>[] Groups = { new(), new(), new(), new() };   // 0 leaf, 1 trunk, 2 sprite, 3 extra
    public Vertex[] Vertices = Array.Empty<Vertex>();
    public ushort[] Indices = Array.Empty<ushort>();
    public bool HasCollision;

    /// <summary>The tree's COLLISION hull, decoded from the section the parser used to skip. Trees are solid
    /// in-game, so without this the editor's collision overlay silently showed nothing for every tree and bush.
    /// Note the hull is not bound by <see cref="Min"/>/<see cref="Max"/> (those describe the visible mesh): real
    /// trunks sink several metres below the render box to anchor them, and some conifer hulls run taller.</summary>
    public Vec3[] CollisionVertices = Array.Empty<Vec3>();

    /// <summary>Triangle indices into <see cref="CollisionVertices"/> (3 per face).</summary>
    public ushort[] CollisionIndices = Array.Empty<ushort>();

    public int Consumed;   // bytes parsed; == file length for a clean parse

    public static TreeMesh Parse(byte[] b)
    {
        var tm = new TreeMesh();
        int p = 0;
        uint U32() { uint v = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)); p += 4; return v; }
        float F() { float v = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(p)); p += 4; return v; }
        Vec3 F3() { float x = F(), y = F(), z = F(); return new Vec3(x, y, z); }
        string Str() { int n = (int)U32(); if (n <= 0) return ""; var s = Encoding.Latin1.GetString(b, p, n); p += n; return s.TrimEnd('\0'); }

        tm.Version = U32(); U32(); U32();                 // ver(3), 0, 8
        tm.Min = F3(); tm.Max = F3(); F3(); F3();         // min, max, min2, max2

        for (int i = 0; i < 4; i++)
        {
            int matnum = (int)U32();
            for (int j = 0; j < matnum; j++)
            {
                var m = new Material { Start = (int)U32(), Count = (int)U32() };
                m.TexName = Str();
                tm.Groups[i].Add(m);
            }
        }

        int colflag = (int)U32();
        tm.HasCollision = colflag != 0;
        if (colflag != 0)
        {
            U32();                                        // colu1 (=5)
            // Collider vertices: float3 position + 4 spare bytes. Collider faces: 3 x u16 index + 1 x u16 spare.
            int cvn = (int)U32();
            tm.CollisionVertices = new Vec3[cvn];
            for (int i = 0; i < cvn; i++) { tm.CollisionVertices[i] = F3(); p += 4; }
            int cfn = (int)U32();
            tm.CollisionIndices = new ushort[cfn * 3];
            for (int i = 0; i < cfn; i++)
            {
                for (int k = 0; k < 3; k++)
                {
                    tm.CollisionIndices[i * 3 + k] = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p));
                    p += 2;
                }
                p += 2;                                   // spare u16
            }
            U32(); U32();                                 // h_u1, h_u2
            int hn = (int)U32(); p += hn * 32;            // hdata (32B each)
            SkipBspNode(b, ref p);                        // recursive AABB BSP
        }

        int vn = (int)U32();
        tm.Vertices = new Vertex[vn];
        for (int i = 0; i < vn; i++)
        {
            float px = F(), py = F(), pz = F(), nx = F(), ny = F(), nz = F();
            p += 4;                                       // u1..u4 bytes
            float u = F(), v = F();                       // uv0
            p += 8;                                       // uv1 (unused)
            tm.Vertices[i] = new Vertex(px, py, pz, nx, ny, nz, u, v);
        }
        int idn = (int)U32();
        tm.Indices = new ushort[idn];
        for (int i = 0; i < idn; i++) { tm.Indices[i] = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)); p += 2; }

        tm.Consumed = p;
        return tm;
    }

    // Recursive collision-BSP node: min float3, max float3, u32 facenum, u32 face[facenum], byte childA(1=recurse),
    // byte childB(1=recurse). We don't keep collision geometry, only advance the cursor to reach the geometry.
    static void SkipBspNode(byte[] b, ref int p)
    {
        p += 24;                                          // min + max float3
        int facenum = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)); p += 4;
        p += facenum * 4;                                 // face indices (u32 each)
        if (b[p++] == 1) SkipBspNode(b, ref p);           // child A
        if (b[p++] == 1) SkipBspNode(b, ref p);           // child B
    }

    public static bool TryParse(byte[] buf, out TreeMesh? mesh)
    {
        try { mesh = Parse(buf); return true; } catch { mesh = null; return false; }
    }
}
