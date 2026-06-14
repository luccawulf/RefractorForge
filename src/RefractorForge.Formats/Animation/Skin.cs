using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RefractorForge.Formats.Animation;

/// <summary>One bone influence on a skinned vertex: a local bone index (into <see cref="Skin.BoneNames"/>),
/// a blend weight, and the vertex position expressed in that bone's local space.</summary>
public readonly record struct SkinInfluence(int LocalBoneIndex, float Weight, float BindX, float BindY, float BindZ);

/// <summary>A skinned vertex: bind-pose position plus up to 4 bone influences.</summary>
public sealed class SkinVertex
{
    public float X, Y, Z;
    public required SkinInfluence[] Influences { get; init; }
}

/// <summary>
/// Clean-room parser for the Battlefield 1942 / Vietnam <c>.skn</c> skinned-mesh format (engine class
/// <c>dice::anim::Skin</c>). The file carries vertex bind positions + per-vertex bone weights + a local
/// bone-name table; it has NO triangle list, UVs, or materials — topology comes from the companion <c>.sm</c>.
/// </summary>
/// <remarks>
/// <para>Layout (little-endian; recovered from the <c>Skin</c> constructor at 0x08342ee0; verified byte-exact
/// against 12 skins incl. 1–4 influences and weights summing to 1.0):</para>
/// <code>
/// u32 version (==1; the loader also accepts 2)
/// u32 vertexCount
/// per vertex:
///   f32 pos[3]
///   u8  influenceCount   (N, observed 1..4)
///   per influence: u16 localBoneIdx; f32 weight; f32 bindPosLocal[3]   (18 bytes)
/// u16 boneNameCount
/// per name: u16 len(incl NUL); char name[len]
/// </code>
/// </remarks>
public sealed class Skin
{
    public int Version { get; }
    public IReadOnlyList<SkinVertex> Vertices { get; }
    public IReadOnlyList<string> BoneNames { get; }

    private Skin(int version, List<SkinVertex> verts, List<string> boneNames)
    {
        Version = version;
        Vertices = verts;
        BoneNames = boneNames;
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
            boneNames.Add(ReadName(br));

        return new Skin(version, verts, boneNames);
    }

    private static string ReadName(BinaryReader br)
    {
        int len = br.ReadUInt16();
        if (len == 0) return "";
        byte[] buf = br.ReadBytes(len);
        int n = buf.Length;
        if (n > 0 && buf[n - 1] == 0) n--;
        return Encoding.ASCII.GetString(buf, 0, n);
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
}
