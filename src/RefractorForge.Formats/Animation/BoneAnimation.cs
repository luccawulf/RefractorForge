using System;
using System.Collections.Generic;
using System.IO;

namespace RefractorForge.Formats.Animation;

/// <summary>One animated bone of a <c>.baf</c> clip: a name plus 7 quantized RLE keyframe channels
/// (0..3 = quaternion x,y,z,w; 4..6 = translation x,y,z), concatenated into <see cref="Blob"/> with each
/// channel's int16 start offset in <see cref="ChannelOffset"/>.</summary>
public sealed class AnimBone
{
    public required string Name { get; init; }
    /// <summary>All 7 channels' int16 streams, concatenated (length == streamCount).</summary>
    public required short[] Blob { get; init; }
    /// <summary>Start index (in int16 units) of each of the 7 channels within <see cref="Blob"/>.</summary>
    public required int[] ChannelOffset { get; init; }
}

/// <summary>
/// Clean-room parser for the Battlefield 1942 / Vietnam <c>.baf</c> bone-animation format, version 3
/// (engine class <c>dice::anim::BoneAnimation</c>). The data is quantized: each channel is a run-length
/// keyframe stream of signed int16 that dequantize as <c>value / ((1&lt;&lt;S)-1)</c> (S=15 → /32767).
/// </summary>
/// <remarks>
/// <para>Layout (little-endian; recovered from the <c>BoneAnimation</c> constructor at 0x0832d930 and the
/// sampler <c>CompressedAnim::GetValue</c> at 0x0832fd30; verified byte-exact + unit-quaternion against
/// 6 real clips incl. a 166-frame reload):</para>
/// <code>
/// u32 version (==3)
/// u16 numBones
/// per bone: u16 nameLen(incl NUL); char name[nameLen]
/// u32 frameCount        (only the low 16 bits matter)
/// u8  S                 (quantization shift; divisor = (1&lt;&lt;S)-1)
/// per bone:
///   u16 streamCount     (total int16 in this bone's blob = sum of the 7 channel lengths)
///   7 channels [qx,qy,qz,qw,tx,ty,tz], each: u16 L; int16[L]
/// </code>
/// </remarks>
public sealed class BoneAnimation
{
    public const int ChannelCount = 7;

    public int Version { get; }
    public int FrameCount { get; }
    public int QuantBits { get; }
    public float Divisor { get; }
    public IReadOnlyList<AnimBone> Bones { get; }

    private BoneAnimation(int version, int frameCount, int quantBits, List<AnimBone> bones)
    {
        Version = version;
        FrameCount = frameCount;
        QuantBits = quantBits;
        Divisor = (1 << quantBits) - 1;
        Bones = bones;
    }

    public static BoneAnimation Load(string path) => Load(File.ReadAllBytes(path));

    public static BoneAnimation Load(byte[] data)
    {
        using var ms = new MemoryStream(data, writable: false);
        using var br = new BinaryReader(ms);

        int version = br.ReadInt32();
        if (version != 3)
            throw new InvalidDataException($".baf version {version} not supported (expected 3).");
        int numBones = br.ReadUInt16();
        if (numBones <= 0 || numBones > 4096)
            throw new InvalidDataException($".baf implausible numBones {numBones}.");

        var names = new string[numBones];
        for (int i = 0; i < numBones; i++)
            names[i] = Skeleton.ReadSkeString(br); // identical string convention to .ske

        int frameCount = br.ReadInt32() & 0xFFFF; // engine keeps only the low 16 bits
        int s = br.ReadByte();
        if (s <= 0 || s > 24)
            throw new InvalidDataException($".baf implausible quant shift {s}.");

        var bones = new List<AnimBone>(numBones);
        for (int i = 0; i < numBones; i++)
        {
            int streamCount = br.ReadUInt16();
            var blob = new short[streamCount];
            var offset = new int[ChannelCount];
            int acc = 0;
            for (int c = 0; c < ChannelCount; c++)
            {
                int l = br.ReadUInt16();
                offset[c] = acc;
                for (int k = 0; k < l; k++)
                {
                    if (acc >= streamCount)
                        throw new InvalidDataException(".baf channel overruns streamCount.");
                    blob[acc++] = br.ReadInt16();
                }
            }
            if (acc != streamCount)
                throw new InvalidDataException($".baf channel lengths {acc} != streamCount {streamCount}.");
            bones.Add(new AnimBone { Name = names[i], Blob = blob, ChannelOffset = offset });
        }
        return new BoneAnimation(version, frameCount, s, bones);
    }

    /// <summary>
    /// Sample one channel of a bone at an integer frame. Ports <c>CompressedAnim::GetValue</c>: walk the
    /// channel's run-length keyframes (header int16: low byte ctrl — low 7 bits = span, high bit = constant
    /// vs per-frame; high byte = int16 stride to the next header) to the run covering <paramref name="frame"/>,
    /// then dequantize the int16 that sits one int16 after the header.
    /// </summary>
    public float Sample(AnimBone bone, int channel, int frame)
    {
        short[] blob = bone.Blob;
        int based = bone.ChannelOffset[channel];
        int idx = 0;          // int16 index of the current keyframe header (relative to channel base)
        int rem = frame;
        int hdr = (ushort)blob[based + idx];
        int span = hdr & 0x7F;
        while (rem > span - 1)
        {
            int step = (hdr >> 8) & 0xFF;
            rem -= span;
            idx += step;
            hdr = (ushort)blob[based + idx];
            span = hdr & 0x7F;
        }
        int valueIdx = (hdr & 0x80) != 0 ? idx : idx + rem; // constant run vs per-frame run
        short raw = blob[based + valueIdx + 1];             // value sits one int16 after the header
        return raw / Divisor;
    }

    public (float X, float Y, float Z, float W) GetQuat(AnimBone bone, int frame) =>
        (Sample(bone, 0, frame), Sample(bone, 1, frame), Sample(bone, 2, frame), Sample(bone, 3, frame));

    public (float X, float Y, float Z) GetTrans(AnimBone bone, int frame) =>
        (Sample(bone, 4, frame), Sample(bone, 5, frame), Sample(bone, 6, frame));
}
