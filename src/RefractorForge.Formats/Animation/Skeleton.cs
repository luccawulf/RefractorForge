using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace RefractorForge.Formats.Animation;

/// <summary>
/// One bone of a Refractor <c>.ske</c> skeleton: a name, a parent ordinal, and a local-to-parent
/// bind matrix as 16 floats laid out exactly as the engine holds it in memory (see <see cref="SkeletalMath"/>):
/// the basis vectors at [0..2], [4..6], [8..10] and the translation at [12..14], [15] = 1.
/// </summary>
public sealed class SkeletonBone
{
    public required string Name { get; init; }
    /// <summary>Parent bone ordinal within this file, or -1 for a root bone (file sentinel 0xFFFF).</summary>
    public required int Parent { get; init; }
    /// <summary>Local-to-parent bind transform, float[16] in the engine's layout (see <see cref="SkeletalMath"/>).</summary>
    public required float[] Local { get; init; }
    /// <summary>For a version-2 file: the bone's stored quaternion (x,y,z,w) and position, kept so a rewrite reproduces them.</summary>
    public float[]? QuatPos { get; init; }
}

/// <summary>
/// Clean-room parser and writer for the Battlefield 1942 / Vietnam <c>.ske</c> skeleton (rest-pose bind) format
/// (engine class <c>dice::anim::Skeleton</c>).
/// </summary>
/// <remarks>
/// <para>Layout (little-endian; the Linux server's <c>Skeleton</c> constructor at 0x08340800; every retail skeleton of both
/// games is version 1 and parses to its last byte):</para>
/// <code>
/// u32 version
/// u32 boneCount
/// per bone:
///   u16 nameLen          (INCLUDING the trailing NUL)
///   char name[nameLen]   (NUL-terminated ASCII; the engine lowercases it)
///   u16 parentIndex      (0xFFFF == root)
///   version 1: f32 m[12]  the local matrix as three rows [R00 R01 R02 Tx][R10 R11 R12 Ty][R20 R21 R22 Tz] of a
///                         column-vector matrix; the engine stores it as [f0 f4 f8 0 | f1 f5 f9 0 | f2 f6 f10 0 | f3 f7 f11 1]
///   otherwise:  f32 q[4] (x,y,z,w) then f32 pos[3] - BaseQuaternion::toMat plus the translation
/// </code>
/// <para>World matrices compose parent-before-child (<c>Skeleton::transform</c>, <c>BaseMatrix4::mult</c>).</para>
/// </remarks>
public sealed class Skeleton
{
    public int Version { get; }
    public IReadOnlyList<SkeletonBone> Bones { get; }

    private Skeleton(int version, List<SkeletonBone> bones)
    {
        Version = version;
        Bones = bones;
    }

    /// <summary>A new version-1 skeleton. Every parent must come before its children, as the engine composes in file order.</summary>
    public static Skeleton Create(IEnumerable<SkeletonBone> bones)
    {
        var list = new List<SkeletonBone>(bones);
        for (int i = 0; i < list.Count; i++)
        {
            var b = list[i];
            if (b.Parent >= i) throw new ArgumentException($"bone {i} '{b.Name}': its parent {b.Parent} must come before it.");
            if (b.Local.Length != 16) throw new ArgumentException($"bone {i} '{b.Name}': a local matrix has 16 floats.");
            if (string.IsNullOrEmpty(b.Name) || b.Name.Length > 254) throw new ArgumentException($"bone {i}: a name of 1..254 characters.");
        }
        if (list.Count == 0) throw new ArgumentException("a skeleton needs a bone.");
        return new Skeleton(1, list);
    }

    public static Skeleton Load(string path) => Load(File.ReadAllBytes(path));

    public static Skeleton Load(byte[] data)
    {
        using var ms = new MemoryStream(data, writable: false);
        using var br = new BinaryReader(ms);

        int version = br.ReadInt32();
        if (version is < 1 or > 2)
            throw new InvalidDataException($".ske version {version} not supported (expected 1, or 2 for quaternions).");
        int boneCount = br.ReadInt32();
        if (boneCount < 0 || boneCount > 100000)
            throw new InvalidDataException($".ske implausible boneCount {boneCount}.");

        var bones = new List<SkeletonBone>(boneCount);
        Span<float> f = stackalloc float[12];
        for (int i = 0; i < boneCount; i++)
        {
            string name = ReadSkeString(br);
            int parentRaw = br.ReadUInt16();
            int parent = parentRaw == 0xFFFF ? -1 : parentRaw;

            if (version == 1)
            {
                for (int k = 0; k < 12; k++) f[k] = br.ReadSingle();
                bones.Add(new SkeletonBone { Name = name, Parent = parent, Local = FromRows(f) });
            }
            else
            {
                var qp = new float[7];
                for (int k = 0; k < 7; k++) qp[k] = br.ReadSingle();
                var m = SkeletalMath.FromQuatTrans(qp[0], qp[1], qp[2], qp[3], qp[4], qp[5], qp[6]);
                bones.Add(new SkeletonBone { Name = name, Parent = parent, Local = m, QuatPos = qp });
            }
        }
        return new Skeleton(version, bones);
    }

    /// <summary>The file - byte for byte what <see cref="Load"/> read; a new skeleton is written as version 1.</summary>
    public byte[] Write()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Version);
        w.Write(Bones.Count);
        foreach (var b in Bones)
        {
            WriteSkeString(w, b.Name);
            w.Write(b.Parent < 0 ? (ushort)0xFFFF : (ushort)b.Parent);
            if (Version == 1)
                foreach (var v in ToRows(b.Local)) w.Write(v);
            else
            {
                var qp = b.QuatPos ?? QuatPosOf(b.Local);
                foreach (var v in qp) w.Write(v);
            }
        }
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>The engine's matrix from the 12 stored floats: [f0 f4 f8 0 | f1 f5 f9 0 | f2 f6 f10 0 | f3 f7 f11 1].</summary>
    public static float[] FromRows(ReadOnlySpan<float> f) => new float[16]
    {
        f[0], f[4], f[8], 0f,
        f[1], f[5], f[9], 0f,
        f[2], f[6], f[10], 0f,
        f[3], f[7], f[11], 1f,
    };

    /// <summary>The 12 floats a version-1 file stores for a local matrix - the exact inverse of <see cref="FromRows"/>.</summary>
    public static float[] ToRows(float[] m) => new[]
    {
        m[0], m[4], m[8], m[12],
        m[1], m[5], m[9], m[13],
        m[2], m[6], m[10], m[14],
    };

    private static float[] QuatPosOf(float[] m)
    {
        var q = SkeletalMath.ToQuat(m);
        return new[] { q.X, q.Y, q.Z, q.W, m[12], m[13], m[14] };
    }

    /// <summary>A length-prefixed string: u16 length (incl. NUL), then that many NUL-terminated bytes.</summary>
    internal static string ReadSkeString(BinaryReader br)
    {
        int len = br.ReadUInt16();
        if (len == 0) return "";
        byte[] buf = br.ReadBytes(len);
        int n = buf.Length;
        if (n > 0 && buf[n - 1] == 0) n--; // drop trailing NUL
        return Encoding.Latin1.GetString(buf, 0, n);
    }

    /// <summary>The inverse of <see cref="ReadSkeString"/>: u16 length including the NUL, the characters, the NUL.</summary>
    internal static void WriteSkeString(BinaryWriter w, string s)
    {
        var b = Encoding.Latin1.GetBytes(s);
        w.Write((ushort)(b.Length + 1));
        w.Write(b);
        w.Write((byte)0);
    }

    /// <summary>Case-insensitive bone lookup (engine interns names lowercased with trailing spaces trimmed).</summary>
    public int FindBone(string name)
    {
        string key = Normalize(name);
        for (int i = 0; i < Bones.Count; i++)
            if (Normalize(Bones[i].Name) == key) return i;
        return -1;
    }

    internal static string Normalize(string s) => s.ToLowerInvariant().TrimEnd(' ');

    /// <summary>
    /// Compose world matrices for every bone from a set of local matrices (rest pose if
    /// <paramref name="locals"/> is null). Bones are parent-before-child in the file, so a single
    /// forward pass suffices. The bone's world position (joint location) is (world[12], world[13], world[14]).
    /// </summary>
    public float[][] ComputeWorld(float[][]? locals = null)
    {
        int n = Bones.Count;
        var world = new float[n][];
        for (int i = 0; i < n; i++)
        {
            float[] local = locals != null ? locals[i] : Bones[i].Local;
            int p = Bones[i].Parent;
            world[i] = (p >= 0 && p < i) ? SkeletalMath.Mul(world[p], local) : (float[])local.Clone();
        }
        return world;
    }
}
