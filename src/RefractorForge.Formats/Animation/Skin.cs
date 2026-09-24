using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace RefractorForge.Formats.Animation;

/// <summary>One bone influence on a skinned vertex: a local bone index (into <see cref="Skin.BoneNames"/>),
/// a blend weight, and the vertex position expressed in that bone's world frame at bind time.</summary>
public readonly record struct SkinInfluence(int LocalBoneIndex, float Weight, float BindX, float BindY, float BindZ)
{
    public Vector3 Bind => new(BindX, BindY, BindZ);
}

/// <summary>A skinned vertex: its position (what the mesh's vertices are matched against) plus its bone influences.</summary>
public sealed class SkinVertex
{
    public float X, Y, Z;
    public required SkinInfluence[] Influences { get; init; }
    public Vector3 Position => new(X, Y, Z);
}

/// <summary>A mesh vertex's bones for <see cref="Skin.Build"/>: skeleton bone indices and weights.</summary>
public readonly record struct BoneWeights(int[] Bones, float[] Weights);

/// <summary>
/// Clean-room parser and writer for the Battlefield 1942 / Vietnam <c>.skn</c> skin (engine class
/// <c>dice::anim::Skin</c>). The file carries vertex positions + per-vertex bone influences + a local bone-name table;
/// it has NO triangle list, UVs, or materials - the mesh is the companion <c>.sm</c>, bound to it by position.
/// </summary>
/// <remarks>
/// <para>Layout (little-endian; the Linux server's <c>Skin</c> constructor at 0x08342ee0; every retail skin of both games
/// parses to its last byte):</para>
/// <code>
/// u32 version (1; the loader also accepts 2)
/// u32 vertexCount
/// per vertex:
///   f32 pos[3]
///   u8  influenceCount   (1..6 in retail)
///   per influence: u16 localBoneIdx; f32 weight; f32 bindPos[3]   (18 bytes)
/// u16 boneNameCount
/// per name: u16 len(incl NUL); char name[len]
/// </code>
/// <para><b>How the engine uses it.</b> Every <c>.sm</c> vertex (each LOD, each material) takes the skin vertex within
/// 1e-4 m of it (<see cref="SkinBinding"/>); a mesh vertex no skin vertex is near is drawn rigid, where the .sm has it.
/// Of a vertex's influences the engine keeps at most two (<see cref="EngineInfluences"/>), and draws
/// <c>sum weight * (BoneWorld * bindPos)</c> - there is no inverse-bind matrix: a bind position is the vertex in the
/// bone's world frame at bind time (<see cref="Build"/>).</para>
/// </remarks>
public sealed class Skin
{
    /// <summary>Influences lighter than this are left out when a vertex has heavier ones (0x86c08a4).</summary>
    public const float WeightThreshold = 0.01f;
    /// <summary>The influences a vertex keeps once loaded.</summary>
    public const int MaxInfluences = 2;

    public int Version { get; }
    public IReadOnlyList<SkinVertex> Vertices { get; }
    public IReadOnlyList<string> BoneNames { get; }

    private Skin(int version, List<SkinVertex> verts, List<string> boneNames)
    {
        Version = version;
        Vertices = verts;
        BoneNames = boneNames;
    }

    /// <summary>A skin from its parts; influence bone indices point into <paramref name="boneNames"/>.</summary>
    public static Skin Create(IEnumerable<SkinVertex> vertices, IEnumerable<string> boneNames, int version = 1)
    {
        var v = vertices.ToList(); var n = boneNames.ToList();
        if (n.Count > ushort.MaxValue) throw new ArgumentException($"{n.Count} bones: a skin names 65535 at most.");
        foreach (var x in v)
        {
            if (x.Influences.Length is 0 or > 255) throw new ArgumentException($"a vertex has {x.Influences.Length} influences (1..255).");
            foreach (var i in x.Influences)
                if (i.LocalBoneIndex < 0 || i.LocalBoneIndex >= n.Count) throw new ArgumentException($"influence bone {i.LocalBoneIndex} is not one of the {n.Count} names.");
        }
        return new Skin(version, v, n);
    }

    public static Skin Load(string path) => Load(File.ReadAllBytes(path));

    public static Skin Load(byte[] data)
    {
        using var ms = new MemoryStream(data, writable: false);
        using var br = new BinaryReader(ms);

        int version = br.ReadInt32();
        if ((uint)(version - 1) > 1) // engine guard: accepts 1 or 2, rejects >=3
            throw new InvalidDataException($".skn version {version} not supported (expected 1 or 2).");
        int vertexCount = br.ReadInt32();
        if (vertexCount < 0 || vertexCount > 5_000_000)
            throw new InvalidDataException($".skn implausible vertexCount {vertexCount}.");

        var verts = new List<SkinVertex>(vertexCount);
        for (int i = 0; i < vertexCount; i++)
        {
            float x = br.ReadSingle(), y = br.ReadSingle(), z = br.ReadSingle();
            int n = br.ReadByte();
            var infl = new SkinInfluence[n];
            for (int k = 0; k < n; k++)
            {
                int bone = br.ReadUInt16();
                float w = br.ReadSingle();
                float bx = br.ReadSingle(), by = br.ReadSingle(), bz = br.ReadSingle();
                infl[k] = new SkinInfluence(bone, w, bx, by, bz);
            }
            verts.Add(new SkinVertex { X = x, Y = y, Z = z, Influences = infl });
        }

        int boneNameCount = br.ReadUInt16();
        var boneNames = new List<string>(boneNameCount);
        for (int j = 0; j < boneNameCount; j++)
            boneNames.Add(Skeleton.ReadSkeString(br));

        return new Skin(version, verts, boneNames);
    }

    /// <summary>The file - byte for byte what <see cref="Load"/> read.</summary>
    public byte[] Write()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Version);
        w.Write(Vertices.Count);
        foreach (var v in Vertices)
        {
            w.Write(v.X); w.Write(v.Y); w.Write(v.Z);
            w.Write((byte)v.Influences.Length);
            foreach (var i in v.Influences)
            {
                w.Write((ushort)i.LocalBoneIndex);
                w.Write(i.Weight);
                w.Write(i.BindX); w.Write(i.BindY); w.Write(i.BindZ);
            }
        }
        w.Write((ushort)BoneNames.Count);
        foreach (var n in BoneNames) Skeleton.WriteSkeString(w, n);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// The influences the engine keeps of a vertex's stored ones (the <c>Skin</c> constructor): those of weight 0.01 or
    /// more, heaviest first (ties in file order), at most four accepted - or, when none is that heavy, the first four
    /// with equal weights 1/n; then only the first two, the first reweighted to one minus the second.
    /// </summary>
    public static SkinInfluence[] EngineInfluences(IReadOnlyList<SkinInfluence> stored)
    {
        var kept = new List<SkinInfluence>(4);
        var light = new List<SkinInfluence>();
        foreach (var inf in stored)
        {
            if (kept.Count > 3) break;                  // "full": the rest of the vertex is read past
            if (!(inf.Weight < WeightThreshold))        // NaN counts as heavy, as the x87 compare does
            {
                int at = kept.FindIndex(k => k.Weight < inf.Weight);
                if (at < 0) kept.Add(inf); else kept.Insert(at, inf);
            }
            else light.Add(inf);
        }
        if (kept.Count == 0 && stored.Count > 0)
        {
            float each = 1f / stored.Count;
            foreach (var inf in light)
            {
                kept.Add(inf with { Weight = each });
                if (kept.Count > 3) break;
            }
        }
        if (kept.Count > MaxInfluences)
        {
            kept.RemoveRange(MaxInfluences, kept.Count - MaxInfluences);
            kept[0] = kept[0] with { Weight = 1f - kept[1].Weight };
        }
        return kept.ToArray();
    }

    /// <summary>
    /// Resolve each local bone name to a skeleton bone index (case-insensitive), so a per-vertex
    /// <see cref="SkinInfluence.LocalBoneIndex"/> can be mapped to <c>skeleton.Bones[result[idx]]</c>.
    /// Entries are -1 where the skeleton lacks the named bone.
    /// </summary>
    public int[] MapToSkeleton(Skeleton skeleton)
    {
        var map = new int[BoneNames.Count];
        for (int i = 0; i < BoneNames.Count; i++)
            map[i] = skeleton.FindBone(BoneNames[i]);
        return map;
    }

    /// <summary>
    /// Where the engine draws every skin vertex for the given bone world matrices (<see cref="Skeleton.ComputeWorld"/> of
    /// a pose): <c>sum weight * (BoneWorld * bindPos)</c> over <see cref="EngineInfluences"/>. A vertex whose bones the
    /// skeleton lacks stays where the skin has it.
    /// </summary>
    public Vector3[] Pose(Skeleton skeleton, float[][] world)
    {
        var map = MapToSkeleton(skeleton);
        var o = new Vector3[Vertices.Count];
        for (int i = 0; i < Vertices.Count; i++)
        {
            var v = Vertices[i];
            var acc = Vector3.Zero; bool any = false;
            foreach (var inf in EngineInfluences(v.Influences))
            {
                int b = inf.LocalBoneIndex < map.Length ? map[inf.LocalBoneIndex] : -1;
                if (b < 0 || b >= world.Length) continue;
                acc += inf.Weight * SkeletalMath.TransformPoint(world[b], inf.Bind);
                any = true;
            }
            o[i] = any ? acc : v.Position;
        }
        return o;
    }

    /// <summary>
    /// A skin for a mesh: <paramref name="positions"/> are its vertices where the skeleton's rest pose
    /// (<paramref name="skeleton"/>) holds them, <paramref name="weights"/> each vertex's skeleton bones and weights.
    /// Vertices at the same position (the seams a mesh splits for UVs and normals) become one skin vertex - the engine
    /// binds every mesh vertex to it by position; they must agree on their bones. Each vertex keeps its two heaviest
    /// influences (all the engine uses), renormalized, and gets bind positions that make the engine draw it exactly
    /// where it is at rest. The bone-name table holds the bones used, in skeleton order.
    /// </summary>
    public static Skin Build(IReadOnlyList<Vector3> positions, IReadOnlyList<BoneWeights> weights, Skeleton skeleton)
    {
        if (positions.Count != weights.Count) throw new ArgumentException($"{positions.Count} positions and {weights.Count} weight sets.");
        var world = skeleton.ComputeWorld();
        var inverse = world.Select(SkeletalMath.InverseRigid).ToArray();

        var byPosition = new Dictionary<(float, float, float), int>();
        var unique = new List<(Vector3 P, (int Bone, float W)[] Infl)>();
        for (int i = 0; i < positions.Count; i++)
        {
            var p = positions[i];
            var infl = Strongest(weights[i], skeleton.Bones.Count, i);
            // A seam copy: the first one's bones win (a mesh gives both copies the same weights in practice).
            if (byPosition.ContainsKey((p.X, p.Y, p.Z))) continue;
            byPosition[(p.X, p.Y, p.Z)] = unique.Count;
            unique.Add((p, infl));
        }

        var used = unique.SelectMany(u => u.Infl.Select(x => x.Bone)).Distinct().OrderBy(b => b).ToList();
        var local = used.Select((b, i) => (b, i)).ToDictionary(t => t.b, t => t.i);
        var verts = new List<SkinVertex>(unique.Count);
        foreach (var (p, infl) in unique)
        {
            var list = new SkinInfluence[infl.Length];
            for (int k = 0; k < infl.Length; k++)
            {
                var b = SkeletalMath.TransformPoint(inverse[infl[k].Bone], p);
                list[k] = new SkinInfluence(local[infl[k].Bone], infl[k].W, b.X, b.Y, b.Z);
            }
            verts.Add(new SkinVertex { X = p.X, Y = p.Y, Z = p.Z, Influences = list });
        }
        return Create(verts, used.Select(b => skeleton.Bones[b].Name), 1);
    }

    /// <summary>A vertex's two heaviest bones, renormalized to add up to one; a second bone lighter than the engine's
    /// threshold is dropped rather than stored for it to reweight.</summary>
    private static (int Bone, float W)[] Strongest(BoneWeights w, int boneCount, int vertex)
    {
        var pairs = new List<(int Bone, float W)>();
        for (int k = 0; k < Math.Min(w.Bones.Length, w.Weights.Length); k++)
        {
            if (w.Bones[k] < 0 || w.Bones[k] >= boneCount) throw new ArgumentException($"vertex {vertex}: bone {w.Bones[k]} is not in the skeleton.");
            if (w.Weights[k] > 0f && float.IsFinite(w.Weights[k]))
            {
                int dup = pairs.FindIndex(x => x.Bone == w.Bones[k]);
                if (dup >= 0) pairs[dup] = (w.Bones[k], pairs[dup].W + w.Weights[k]); else pairs.Add((w.Bones[k], w.Weights[k]));
            }
        }
        if (pairs.Count == 0) throw new ArgumentException($"vertex {vertex} has no bone.");
        pairs.Sort((a, b) => b.W.CompareTo(a.W));
        if (pairs.Count > MaxInfluences) pairs.RemoveRange(MaxInfluences, pairs.Count - MaxInfluences);
        float sum = pairs.Sum(x => x.W);
        pairs = pairs.Select(x => (x.Bone, x.W / sum)).ToList();
        if (pairs.Count == 2 && pairs[1].W < WeightThreshold) pairs = new List<(int, float)> { (pairs[0].Bone, 1f) };
        if (pairs.Count == 2) pairs[0] = (pairs[0].Bone, 1f - pairs[1].W);   // exactly what the engine's pair adds up to
        return pairs.ToArray();
    }
}
