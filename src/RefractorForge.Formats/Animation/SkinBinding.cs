using System;
using System.Collections.Generic;
using System.Numerics;

namespace RefractorForge.Formats.Animation;

/// <summary>
/// Which skin vertex each vertex of a skinned mesh follows - the engine's own matching, from
/// <c>AnimatedMeshTemplate::fillHwSkinnedBlock</c> (0x0837df00) and <c>VertexHashTable::findIndex</c> (0x08381140) of the
/// Linux server. The skin's positions go into 4096 buckets by <c>((int)(x*10) + (int)(y*10) + (int)(z*10)) &amp; 0xFFF</c>
/// (truncated); a mesh vertex looks in its own bucket, then the one below, then the one above, and takes the first skin
/// vertex closer than 1e-4 m. A mesh vertex that finds none is drawn rigid: weight one on the object's own matrix, where
/// the <c>.sm</c> has it. Every LOD and material of the mesh is matched the same way against the one skin.
/// </summary>
public sealed class SkinBinding
{
    /// <summary>The engine's match distance, in metres.</summary>
    public const float Tolerance = 1e-4f;
    private const int Buckets = 4096;

    private readonly List<(int Index, Vector3 P)>?[] _table = new List<(int, Vector3)>?[Buckets];

    public SkinBinding(IReadOnlyList<SkinVertex> skin)
    {
        for (int i = 0; i < skin.Count; i++)
        {
            var p = skin[i].Position;
            int b = Bucket(p);
            (_table[b] ??= new List<(int, Vector3)>()).Add((i, p));
        }
    }

    public SkinBinding(Skin skin) : this(skin.Vertices) { }

    /// <summary>The bucket of a position. The engine multiplies in x87 extended precision, where a float times ten is exact;
    /// a double holds that product exactly too, so the truncation lands on the same integer.</summary>
    public static int Bucket(Vector3 p) => ((int)(p.X * 10.0) + (int)(p.Y * 10.0) + (int)(p.Z * 10.0)) & (Buckets - 1);

    /// <summary>The skin vertex a mesh vertex at <paramref name="p"/> follows, or -1 (drawn rigid).</summary>
    public int Find(Vector3 p)
    {
        int b = Bucket(p);
        Span<int> order = stackalloc int[3] { b, (b - 1) & (Buckets - 1), (b + 1) & (Buckets - 1) };
        const double eps2 = (double)Tolerance * Tolerance;
        foreach (int c in order)
        {
            var list = _table[c];
            if (list is null) continue;
            foreach (var (index, q) in list)
            {
                // d² < eps², in the wider precision the engine's x87 code works in.
                double dx = (double)p.X - q.X, dy = (double)p.Y - q.Y, dz = (double)p.Z - q.Z;
                if (dx * dx + dy * dy + dz * dz < eps2) return index;
            }
        }
        return -1;
    }

    /// <summary>The skin vertex of every mesh vertex (-1 = rigid).</summary>
    public int[] Bind(IReadOnlyList<Vector3> meshPositions)
    {
        var o = new int[meshPositions.Count];
        for (int i = 0; i < o.Length; i++) o[i] = Find(meshPositions[i]);
        return o;
    }

    /// <summary>
    /// A skinned mesh as the engine draws it for a pose: each bound vertex where its skin vertex goes
    /// (<paramref name="posedSkin"/>, from <see cref="Skin.Pose"/>), each unbound one where the mesh has it.
    /// </summary>
    public static Vector3[] Apply(IReadOnlyList<Vector3> meshPositions, int[] binding, IReadOnlyList<Vector3> posedSkin)
    {
        var o = new Vector3[meshPositions.Count];
        for (int i = 0; i < o.Length; i++)
            o[i] = binding[i] >= 0 && binding[i] < posedSkin.Count ? posedSkin[binding[i]] : meshPositions[i];
        return o;
    }
}
