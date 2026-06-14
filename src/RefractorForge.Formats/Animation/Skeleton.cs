using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RefractorForge.Formats.Animation;

/// <summary>
/// One bone of a Refractor <c>.ske</c> skeleton: a name, a parent ordinal, and a local-to-parent
/// bind matrix stored COLUMN-MAJOR as 16 floats (element(row,col) = Local[col*4 + row]; the
/// translation column is Local[12..14], Local[15] = 1).
/// </summary>
public sealed class SkeletonBone
{
    public required string Name { get; init; }
    /// <summary>Parent bone ordinal within this file, or -1 for a root bone (file sentinel 0xFFFF).</summary>
    public required int Parent { get; init; }
    /// <summary>Local-to-parent bind transform, column-major float[16] (see <see cref="SkeletalMath"/>).</summary>
    public required float[] Local { get; init; }
}

/// <summary>
/// Clean-room parser for the Battlefield 1942 / Vietnam <c>.ske</c> skeleton (rest-pose bind) format
/// (engine class <c>dice::anim::Skeleton</c>).
/// </summary>
/// <remarks>
/// <para>Layout (little-endian; recovered from the unstripped Linux dedicated-server <c>Skeleton</c>
/// constructor at 0x08340800 and verified byte-exact against Binoculars/colt/UsSoldier/JapSoldier):</para>
/// <code>
/// u32 version (==1)
/// u32 boneCount
/// per bone:
///   u16 nameLen          (INCLUDING the trailing NUL)
///   char name[nameLen]   (NUL-terminated ASCII)
///   u16 parentIndex      (0xFFFF == root)
///   f32 m[12]            (the bone's local-to-parent matrix)
/// </code>
/// <para>The 12 on-disk floats are the engine matrix's three basis columns with the translation
/// interleaved as each column's 4th element: on disk = [R00,R10,R20,Tx, R01,R11,R21,Ty, R02,R12,R22,Tz],
/// i.e. rotation R[row][col] = m[col*4 + row] and translation = (m[3], m[7], m[11]). We expand that to a
/// 16-float column-major matrix. The engine composes world = parentWorld * local (Skeleton::transform).</para>
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

    public static Skeleton Load(string path) => Load(File.ReadAllBytes(path));

    public static Skeleton Load(byte[] data)
    {
        using var ms = new MemoryStream(data, writable: false);
        using var br = new BinaryReader(ms);

        int version = br.ReadInt32();
        if (version != 1)
            throw new InvalidDataException($".ske version {version} not supported (expected 1).");
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

            // 12 on-disk floats -> 16-float column-major matrix (element(row,col) = m[col*4+row]). The on-disk
            // groups f[0..2]/f[4..6]/f[8..10] are the rotation matrix ROWS, so to fill our column-major basis
            // columns we transpose: column c of the rotation = (f[c], f[c+4], f[c+8]). Translation is
            // (f[3], f[7], f[11]). This is the mapping that composes the soldier rest pose into a correct
            // (Z-up, 3ds-Max Biped) standing humanoid — verified by the `skeletal` Demo gate.
            for (int k = 0; k < 12; k++) f[k] = br.ReadSingle();
            var m = new float[16];
            m[0] = f[0]; m[1] = f[4]; m[2] = f[8]; m[3] = 0f;   // basis column X
            m[4] = f[1]; m[5] = f[5]; m[6] = f[9]; m[7] = 0f;   // basis column Y
            m[8] = f[2]; m[9] = f[6]; m[10] = f[10]; m[11] = 0f; // basis column Z
            m[12] = f[3]; m[13] = f[7]; m[14] = f[11]; m[15] = 1f; // translation column

            bones.Add(new SkeletonBone { Name = name, Parent = parent, Local = m });
        }
        return new Skeleton(version, bones);
    }

    /// <summary>A length-prefixed string: u16 length (incl. NUL), then that many NUL-terminated bytes.</summary>
    internal static string ReadSkeString(BinaryReader br)
    {
        int len = br.ReadUInt16();
        if (len == 0) return "";
        byte[] buf = br.ReadBytes(len);
        int n = buf.Length;
        if (n > 0 && buf[n - 1] == 0) n--; // drop trailing NUL
        return Encoding.ASCII.GetString(buf, 0, n);
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
    /// forward pass suffices. Each world matrix is column-major float[16]; the bone's world position
    /// (joint location) is (world[12], world[13], world[14]).
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
