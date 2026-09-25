using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RefractorForge.Formats.Geometry;

namespace RefractorForge.Formats.Rfa;

/// <summary>
/// Battlefield 1942 TreeMesh (.tm) files - the overgrowth/foliage geometry inside treeMesh.rfa (trees, bushes: trunk + leaf
/// + sprite groups), read and written. Reverse-engineered from BfMeshView's modTreeMesh.bas; every field is kept, so all
/// 111 retail files write back byte for byte (gated by <c>TreeMeshWriteTests</c>).
///
/// Layout (all little-endian):
///   header   : u32 ver(=3), u32(0), u32(8)
///   bounds   : float3 min, max, min2, max2  (the geometry AABB, plus a second AABB)
///   meshes   : EXACTLY 4 groups (0 leaf, 1 trunk, 2 sprite, 3 extra). Per group: u32 matnum, then per material
///              u32 start (index offset), u32 count (triangle count), string texname (u32 len + latin1 bytes).
///   collision: u32 colflag (0, or retail's one tag); if != 0 the <see cref="TreeCollision"/> section.
///   geometry : u32 vertnum, vert[44B] (float3 pos, float3 normal, u32 extra, float2 uv0, float2 uv1),
///              u32 indexnum, u16 index[].
/// A trunk (and extra) material is a triangle list over index[start .. start + count*3). A leaf or sprite material holds
/// its count triangles once per view (the header's 8): copy k, at start + k*count*3, sorted back to front as seen from
/// <see cref="ViewDirection"/> k - the engine draws the copy for the side the camera is on (TreeMesh::realDraw,
/// getAngleIndex), so count is the triangles of ONE copy. Every retail leaf (99) and sprite (57) block is 8 such copies.
/// A sprite is 4 vertices at its centre (uv1 the corner's offset in view space, +-its size) that the engine turns to the
/// camera. Triangles run clockwise seen from their front ((b-a)x(c-a) along the normal), as Direct3D draws them; culling
/// is off for the leaves only. Retail's per-vertex extra is 0x80808080 on leaves and trunks and 0 on sprites.
/// </summary>
public sealed class TreeMesh
{
    public readonly record struct Vertex(float Px, float Py, float Pz, float Nx, float Ny, float Nz, float U, float V,
                                         uint Extra = TreeMesh.SolidExtra, float U1 = 0f, float V1 = 0f);
    public sealed class Material { public int Start; public int Count; public string TexName = ""; }

    /// <summary>The per-vertex word every retail leaf and trunk vertex carries (sprites carry 0).</summary>
    public const uint SolidExtra = 0x80808080u;
    /// <summary>The collision flag every retail tree with a collision hull carries.</summary>
    public const uint CollisionTag = 3952591610u;

    public uint Version = 3;
    public uint Reserved0;
    /// <summary>The header's third word: the views a leaf or sprite block is sorted for and a billboard is made of (8 in
    /// every file; <see cref="Views"/>).</summary>
    public uint Reserved8 = 8;
    /// <summary>The first box: round the leaf, trunk and extra vertices and each sprite's cube (see <see cref="RecomputeBounds"/>).
    /// The engine frames the billboard with it (<see cref="BillboardRadius"/>, <see cref="BillboardHalfHeight"/>).</summary>
    public Vec3 Min, Max;
    /// <summary>The second box: round the sprites' cubes and the origin, zeros for a tree without sprites (the sprite
    /// shader's scale).</summary>
    public Vec3 Min2, Max2;
    public List<Material>[] Groups = { new(), new(), new(), new() };   // 0 leaf, 1 trunk, 2 sprite, 3 extra
    public Vertex[] Vertices = Array.Empty<Vertex>();
    public ushort[] Indices = Array.Empty<ushort>();
    public bool HasCollision;
    /// <summary>The collision flag as read (0 when there is none).</summary>
    public uint CollisionFlag;
    /// <summary>The collision section, whole (null when there is none).</summary>
    public TreeCollision? Collision;

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

        tm.Version = U32(); tm.Reserved0 = U32(); tm.Reserved8 = U32();    // ver(3), 0, 8
        tm.Min = F3(); tm.Max = F3(); tm.Min2 = F3(); tm.Max2 = F3();

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

        tm.CollisionFlag = U32();
        tm.HasCollision = tm.CollisionFlag != 0;
        if (tm.HasCollision)
        {
            tm.Collision = TreeCollision.Read(b, ref p);
            tm.CollisionVertices = tm.Collision.Vertices.Select(v => v.Position).ToArray();
            tm.CollisionIndices = tm.Collision.Faces.SelectMany(f => new[] { f.A, f.B, f.C }).ToArray();
        }

        int vn = (int)U32();
        tm.Vertices = new Vertex[vn];
        for (int i = 0; i < vn; i++)
        {
            float px = F(), py = F(), pz = F(), nx = F(), ny = F(), nz = F();
            uint extra = U32();
            float u = F(), v = F();
            float u1 = F(), v1 = F();
            tm.Vertices[i] = new Vertex(px, py, pz, nx, ny, nz, u, v, extra, u1, v1);
        }
        int idn = (int)U32();
        tm.Indices = new ushort[idn];
        for (int i = 0; i < idn; i++) { tm.Indices[i] = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)); p += 2; }

        tm.Consumed = p;
        return tm;
    }

    public static bool TryParse(byte[] buf, out TreeMesh? mesh)
    {
        try { mesh = Parse(buf); return true; } catch { mesh = null; return false; }
    }

    /// <summary>The file: every field as held, so a tree read and written unchanged is the same bytes.</summary>
    public byte[] Write()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        void W3(Vec3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
        w.Write(Version); w.Write(Reserved0); w.Write(Reserved8);
        W3(Min); W3(Max); W3(Min2); W3(Max2);
        for (int g = 0; g < 4; g++)
        {
            w.Write((uint)Groups[g].Count);
            foreach (var m in Groups[g])
            {
                w.Write((uint)m.Start); w.Write((uint)m.Count);
                var name = Encoding.Latin1.GetBytes(m.TexName);
                w.Write((uint)name.Length); w.Write(name);
            }
        }
        if (Collision is { } c)
        {
            w.Write(CollisionFlag == 0 ? CollisionTag : CollisionFlag);
            c.Write(w);
        }
        else w.Write(0u);
        w.Write((uint)Vertices.Length);
        foreach (var v in Vertices)
        {
            w.Write(v.Px); w.Write(v.Py); w.Write(v.Pz); w.Write(v.Nx); w.Write(v.Ny); w.Write(v.Nz);
            w.Write(v.Extra); w.Write(v.U); w.Write(v.V); w.Write(v.U1); w.Write(v.V1);
        }
        w.Write((uint)Indices.Length);
        foreach (var i in Indices) w.Write(i);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>The views a leaf or sprite block is sorted for (the header's third word, 8 in every file).</summary>
    public int Views => Reserved8 is > 0 and <= 64 ? (int)Reserved8 : 8;

    /// <summary>
    /// Where the camera stands for view <paramref name="k"/> of <paramref name="views"/>: the direction from the tree,
    /// (sin a, 0, cos a) at a = 360/views x (k + 1/2) degrees - from +Z (north) round towards +X (east), view 0 half a step
    /// east of north. Copy k of a leaf or sprite block is sorted back to front as seen from there (every retail block agrees)
    /// and cell k of the tree's billboard is the tree seen from there (TreeRenderer::createBillboard: the camera 2 x the
    /// radius out along this, level with the first box's middle, looking at the tree's axis).
    /// </summary>
    public static Vec3 ViewDirection(int k, int views = 8)
    {
        double a = 2.0 * Math.PI * (k + 0.5) / views;
        return new Vec3((float)Math.Sin(a), 0f, (float)Math.Cos(a));
    }

    /// <summary>
    /// Triangles laid out as a leaf or sprite block is: once per view, copy k sorted back to front as seen from
    /// <see cref="ViewDirection"/> k (by each triangle's middle, the farthest first; ties keep their order). The material's
    /// count is the triangles of one copy.
    /// </summary>
    public static ushort[] ViewSorted(IReadOnlyList<(int A, int B, int C)> triangles, Func<int, Vec3> position, int views = 8)
    {
        var middles = triangles.Select(t =>
        {
            Vec3 a = position(t.A), b = position(t.B), c = position(t.C);
            return new Vec3((a.X + b.X + c.X) / 3f, (a.Y + b.Y + c.Y) / 3f, (a.Z + b.Z + c.Z) / 3f);
        }).ToArray();
        var list = new List<ushort>(triangles.Count * 3 * views);
        for (int k = 0; k < views; k++)
        {
            var d = ViewDirection(k, views);
            foreach (int i in Enumerable.Range(0, triangles.Count).OrderBy(i => middles[i].X * d.X + middles[i].Z * d.Z))
            {
                var t = triangles[i];
                list.Add((ushort)t.A); list.Add((ushort)t.B); list.Add((ushort)t.C);
            }
        }
        return list.ToArray();
    }

    /// <summary>Each material's index range: [Start, the next material's Start) in the order they lie in the buffer (all four
    /// groups together partition it), the last to the end.</summary>
    public IReadOnlyList<(int Group, Material Material, int End)> Blocks()
    {
        var all = new List<(int Group, Material Material)>();
        for (int g = 0; g < Groups.Length; g++) foreach (var m in Groups[g]) all.Add((g, m));
        all.Sort((x, y) => x.Material.Start.CompareTo(y.Material.Start));
        var list = new List<(int, Material, int)>(all.Count);
        for (int i = 0; i < all.Count; i++)
            list.Add((all[i].Group, all[i].Material, Math.Clamp(i + 1 < all.Count ? all[i + 1].Material.Start : Indices.Length, 0, Indices.Length)));
        return list;
    }

    /// <summary>The vertices the sprite group draws (each sprite four of them at its centre).</summary>
    public bool[] SpriteVertices()
    {
        var sprite = new bool[Vertices.Length];
        foreach (var (g, m, end) in Blocks())
            if (g == 2)
                for (int q = Math.Max(0, m.Start); q < end; q++)
                    if (Indices[q] < sprite.Length) sprite[Indices[q]] = true;
        return sprite;
    }

    /// <summary>A sprite vertex's size: half its square's side, the largest of its uv1 offsets.</summary>
    public static float SpriteSize(Vertex v) => Math.Max(Math.Abs(v.U1), Math.Abs(v.V1));

    /// <summary>
    /// The header's two boxes from the geometry, by the rule every retail file keeps (111 of 111, to 2e-6): the first round
    /// the leaf, trunk and extra vertices and every sprite's cube (its centre +- <see cref="SpriteSize"/>), the second round
    /// the sprites' cubes and the origin - all zeros for a tree without sprites.
    /// </summary>
    public void RecomputeBounds()
    {
        if (Vertices.Length == 0) return;
        var sprite = SpriteVertices();
        float x0 = float.MaxValue, y0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue, z1 = float.MinValue;
        float sx0 = 0, sy0 = 0, sz0 = 0, sx1 = 0, sy1 = 0, sz1 = 0;
        for (int i = 0; i < Vertices.Length; i++)
        {
            var v = Vertices[i];
            float r = sprite[i] ? SpriteSize(v) : 0f;
            x0 = Math.Min(x0, v.Px - r); y0 = Math.Min(y0, v.Py - r); z0 = Math.Min(z0, v.Pz - r);
            x1 = Math.Max(x1, v.Px + r); y1 = Math.Max(y1, v.Py + r); z1 = Math.Max(z1, v.Pz + r);
            if (!sprite[i]) continue;
            sx0 = Math.Min(sx0, v.Px - r); sy0 = Math.Min(sy0, v.Py - r); sz0 = Math.Min(sz0, v.Pz - r);
            sx1 = Math.Max(sx1, v.Px + r); sy1 = Math.Max(sy1, v.Py + r); sz1 = Math.Max(sz1, v.Pz + r);
        }
        Min = new Vec3(x0, y0, z0); Max = new Vec3(x1, y1, z1);
        Min2 = new Vec3(sx0, sy0, sz0); Max2 = new Vec3(sx1, sy1, sz1);
    }

    /// <summary>The billboard's half width, as the engine takes it from the first box: the farthest its X or Z reaches from
    /// the tree's axis (TreeMeshTemplate::load). A billboard cell is 2 x this across, about the axis.</summary>
    public float BillboardRadius => Math.Max(Math.Max(Math.Abs(Min.X), Math.Abs(Max.X)), Math.Max(Math.Abs(Min.Z), Math.Abs(Max.Z)));

    /// <summary>The billboard's half height: half the first box's height, about its middle.</summary>
    public float BillboardHalfHeight => (Max.Y - Min.Y) * 0.5f;

    /// <summary>The whole tree made bigger or smaller about its base: vertices (a sprite's size with it), both boxes and the
    /// collision hull.</summary>
    public void Scale(float s)
    {
        var sprite = SpriteVertices();
        for (int i = 0; i < Vertices.Length; i++)
        {
            var v = Vertices[i];
            Vertices[i] = sprite[i]
                ? v with { Px = v.Px * s, Py = v.Py * s, Pz = v.Pz * s, U1 = v.U1 * s, V1 = v.V1 * s }
                : v with { Px = v.Px * s, Py = v.Py * s, Pz = v.Pz * s };
        }
        Vec3 S(Vec3 v) => new(v.X * s, v.Y * s, v.Z * s);
        Min = S(Min); Max = S(Max); Min2 = S(Min2); Max2 = S(Max2);
        if (Collision is { } c)
        {
            c.Scale(s);
            CollisionVertices = c.Vertices.Select(v => v.Position).ToArray();
        }
    }
}

/// <summary>
/// A tree's collision section: <c>u32 tag(=5)</c>, vertices (float3 + a spare word), faces (three u16 + a material u16,
/// retail 80-82 and 165-166), two counts (the second is the BSP's node count less one; for a one-node tree the first is its
/// face count), a plane record per face (float3 normal, a float retail leaves 0, three u32 vertex indices, the material),
/// and a recursive BSP of boxes (min, max, u32 face count, u32 faces, then a byte for each child, 1 when it follows).
/// </summary>
public sealed class TreeCollision
{
    public readonly record struct CollisionVertex(Vec3 Position, uint Spare);
    public readonly record struct CollisionFace(ushort A, ushort B, ushort C, ushort Material);

    public sealed class Node
    {
        public Vec3 Min, Max;
        public uint[] Faces = Array.Empty<uint>();
        public Node? A, B;
    }

    public uint Tag = 5;
    public List<CollisionVertex> Vertices = new();
    public List<CollisionFace> Faces = new();
    public uint CountA, CountB;
    /// <summary>A 32-byte record per face, as read.</summary>
    public List<byte[]> Planes = new();
    public Node Root = new();

    public static TreeCollision Read(byte[] b, ref int p)
    {
        var c = new TreeCollision();
        uint U32(ref int q) { uint v = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(q)); q += 4; return v; }
        float F(ref int q) { float v = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(q)); q += 4; return v; }
        c.Tag = U32(ref p);
        int vn = (int)U32(ref p);
        for (int i = 0; i < vn; i++)
        {
            var pos = new Vec3(F(ref p), F(ref p), F(ref p));
            c.Vertices.Add(new CollisionVertex(pos, U32(ref p)));
        }
        int fn = (int)U32(ref p);
        for (int i = 0; i < fn; i++)
        {
            ushort U16(ref int q) { ushort v = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(q)); q += 2; return v; }
            c.Faces.Add(new CollisionFace(U16(ref p), U16(ref p), U16(ref p), U16(ref p)));
        }
        c.CountA = U32(ref p); c.CountB = U32(ref p);
        int hn = (int)U32(ref p);
        for (int i = 0; i < hn; i++) { c.Planes.Add(b.AsSpan(p, 32).ToArray()); p += 32; }
        c.Root = ReadNode(b, ref p);
        return c;
    }

    private static Node ReadNode(byte[] b, ref int p)
    {
        var n = new Node();
        float F(ref int q) { float v = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(q)); q += 4; return v; }
        n.Min = new Vec3(F(ref p), F(ref p), F(ref p));
        n.Max = new Vec3(F(ref p), F(ref p), F(ref p));
        int fn = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)); p += 4;
        n.Faces = new uint[fn];
        for (int i = 0; i < fn; i++) { n.Faces[i] = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)); p += 4; }
        if (b[p++] == 1) n.A = ReadNode(b, ref p);
        if (b[p++] == 1) n.B = ReadNode(b, ref p);
        return n;
    }

    public void Write(BinaryWriter w)
    {
        w.Write(Tag);
        w.Write((uint)Vertices.Count);
        foreach (var v in Vertices) { w.Write(v.Position.X); w.Write(v.Position.Y); w.Write(v.Position.Z); w.Write(v.Spare); }
        w.Write((uint)Faces.Count);
        foreach (var f in Faces) { w.Write(f.A); w.Write(f.B); w.Write(f.C); w.Write(f.Material); }
        w.Write(CountA); w.Write(CountB);
        w.Write((uint)Planes.Count);
        foreach (var pl in Planes) w.Write(pl);
        WriteNode(w, Root);
    }

    private static void WriteNode(BinaryWriter w, Node n)
    {
        w.Write(n.Min.X); w.Write(n.Min.Y); w.Write(n.Min.Z); w.Write(n.Max.X); w.Write(n.Max.Y); w.Write(n.Max.Z);
        w.Write((uint)n.Faces.Length);
        foreach (var f in n.Faces) w.Write(f);
        w.Write((byte)(n.A is null ? 0 : 1));
        if (n.A is not null) WriteNode(w, n.A);
        w.Write((byte)(n.B is null ? 0 : 1));
        if (n.B is not null) WriteNode(w, n.B);
    }

    /// <summary>Made bigger or smaller about the origin: the vertices, the planes' distance word, the BSP's boxes.</summary>
    public void Scale(float s)
    {
        Vec3 S(Vec3 v) => new(v.X * s, v.Y * s, v.Z * s);
        for (int i = 0; i < Vertices.Count; i++) Vertices[i] = Vertices[i] with { Position = S(Vertices[i].Position) };
        foreach (var pl in Planes)
        {
            float d = BinaryPrimitives.ReadSingleLittleEndian(pl.AsSpan(12));
            BinaryPrimitives.WriteSingleLittleEndian(pl.AsSpan(12), d * s);
        }
        void Walk(Node? n)
        {
            if (n is null) return;
            n.Min = S(n.Min); n.Max = S(n.Max);
            Walk(n.A); Walk(n.B);
        }
        Walk(Root);
    }

    /// <summary>
    /// A static mesh's collision section (<c>.sm</c>) read as a tree's: it IS one - the same SimpleCollisionMesh (class
    /// 0xEB97C2FA, the tree's collision flag), version, vertices, faces, plane records and BSP, every retail section read
    /// whole this way (BF1942's 747) - so a tree made of a static mesh can keep the mesh's own hull and its BSP. Null when
    /// the section is not one, or does not end where it should.
    /// </summary>
    public static TreeCollision? FromMeshSection(byte[] section)
    {
        if (section.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(section) != TreeMesh.CollisionTag) return null;
        try
        {
            int p = 4;
            var c = Read(section, ref p);
            return p == section.Length ? c : null;
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException or ArgumentException) { return null; }
    }

    /// <summary>
    /// A collision hull from triangles, laid out as retail's one-node bushes are: every face in the root box, the first
    /// count the face count, the second 0, a plane record per face (its normal, 0, its vertex indices, its material).
    /// The faces are wound as the game's collision faces are - (b-a)x(c-a) points INTO the solid, the reverse of a drawn
    /// triangle - and the plane's normal is its opposite, out of the solid: -normalize((b-a)x(c-a)), as every retail tree
    /// face (1306 of 1306) and every static mesh collision face stores it. The engine keeps the faces whose normal meets a
    /// movement head on and pushes back along it, so an inward normal lets everything through.
    /// </summary>
    public static TreeCollision Build(IReadOnlyList<Vec3> positions, IReadOnlyList<(int A, int B, int C)> faces, ushort material = 82)
    {
        if (positions.Count > ushort.MaxValue) throw new ArgumentException("a tree's collision holds at most 65535 vertices");
        var c = new TreeCollision();
        foreach (var p in positions) c.Vertices.Add(new CollisionVertex(p, 0));
        foreach (var (a, b, d) in faces)
        {
            c.Faces.Add(new CollisionFace((ushort)a, (ushort)b, (ushort)d, material));
            var pa = positions[a]; var pb = positions[b]; var pc = positions[d];
            float ux = pb.X - pa.X, uy = pb.Y - pa.Y, uz = pb.Z - pa.Z, vx = pc.X - pa.X, vy = pc.Y - pa.Y, vz = pc.Z - pa.Z;
            float nx = -(uy * vz - uz * vy), ny = -(uz * vx - ux * vz), nz = -(ux * vy - uy * vx);
            float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len > 1e-12f) { nx /= len; ny /= len; nz /= len; }
            var rec = new byte[32];
            BinaryPrimitives.WriteSingleLittleEndian(rec.AsSpan(0), nx);
            BinaryPrimitives.WriteSingleLittleEndian(rec.AsSpan(4), ny);
            BinaryPrimitives.WriteSingleLittleEndian(rec.AsSpan(8), nz);
            BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(16), (uint)a);
            BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(20), (uint)b);
            BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(24), (uint)d);
            BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(28), material);
            c.Planes.Add(rec);
        }
        c.CountA = (uint)faces.Count;
        c.CountB = 0;
        float x0 = float.MaxValue, y0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue, z1 = float.MinValue;
        foreach (var p in positions)
        {
            x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); z0 = Math.Min(z0, p.Z);
            x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); z1 = Math.Max(z1, p.Z);
        }
        c.Root = new Node
        {
            Min = positions.Count == 0 ? default : new Vec3(x0, y0, z0),
            Max = positions.Count == 0 ? default : new Vec3(x1, y1, z1),
            Faces = Enumerable.Range(0, faces.Count).Select(i => (uint)i).ToArray(),
        };
        return c;
    }
}
