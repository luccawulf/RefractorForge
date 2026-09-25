using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

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

    /// <summary>The int16 length of channel <paramref name="c"/>'s stream.</summary>
    public int ChannelLength(int c) => (c + 1 < BoneAnimation.ChannelCount ? ChannelOffset[c + 1] : Blob.Length) - ChannelOffset[c];
}

/// <summary>One bone's motion, a value per frame: rotations as quaternions (x,y,z,w) and translations in metres,
/// both in the bone's parent space - what <see cref="BoneAnimation.Create"/> encodes and <see cref="BoneAnimation.ToTracks"/>
/// decodes.</summary>
public sealed record BoneTrack(string Name, Quaternion[] Rotations, Vector3[] Translations);

/// <summary>
/// Clean-room parser and writer for the Battlefield 1942 / Vietnam <c>.baf</c> bone-animation format, version 3
/// (engine class <c>dice::anim::BoneAnimation</c>). Each channel is a run-length keyframe stream of int16.
/// </summary>
/// <remarks>
/// <para>Layout (little-endian; the <c>BoneAnimation</c> constructor at 0x0832d930 and <c>CompressedAnim::GetValue</c> at
/// 0x0832fd30 of the Linux server; every retail clip of both games parses to its last byte):</para>
/// <code>
/// u32 version (==3)
/// u16 numBones
/// per bone: u16 nameLen(incl NUL); char name[nameLen]
/// u32 frameCount        (only the low 16 bits matter)
/// u8  S                 (quantization shift)
/// per bone:
///   u16 streamCount     (total int16 in this bone's blob = sum of the 7 channel lengths)
///   7 channels [qx,qy,qz,qw,tx,ty,tz], each: u16 L; int16[L]
/// </code>
/// <para>A run header is one int16: low 7 bits the frames it covers, bit 7 set for a constant run (one value follows,
/// the next header is 2 int16 on) and clear for a literal run (one value per frame follows, the next header is
/// span + 1 on); the high byte is that stride.</para>
/// <para><b>Quantization.</b> <c>GetValue</c> divides every channel, quaternions included, by <c>(1&lt;&lt;S)-1</c>. DICE's
/// exporter wrote quaternions at 15 bits (x 32767) whatever S is and translations at S bits, choosing a smaller S for
/// clips that move a bone further than a metre (86 retail clips have S 9..14). The engine gets away with reading those
/// quaternions 2^(15-S) too long because <c>BaseQuaternion::toMat</c> divides by |q|^2. This class reads the rotation
/// channels at 15 bits, so a decoded quaternion is unit length in every retail clip, and writes the way DICE did.</para>
/// </remarks>
public sealed class BoneAnimation
{
    public const int ChannelCount = 7;
    /// <summary>The scale the rotation channels are stored at, whatever <see cref="QuantBits"/> says.</summary>
    public const float RotationScale = 32767f;
    /// <summary>Run limits of DICE's encoder, never exceeded by a retail clip: constant runs cover 6..126 frames,
    /// literal runs 1..125 (the header's span has 7 bits).</summary>
    public const int MinConstantRun = 6, MaxConstantRun = 126, MaxLiteralRun = 125;

    /// <summary>The most bones, and the longest bone name, a clip the games play can have: the loader keeps the count in
    /// a byte and reads a name into a 272-byte buffer.</summary>
    public const int MaxBones = 255, MaxNameLength = 271;

    public int Version { get; }
    public int FrameCount { get; }
    /// <summary>The frame-count field as stored (the engine keeps its low 16 bits; no retail clip sets the others).</summary>
    public uint RawFrameCount { get; }
    public int QuantBits { get; }
    /// <summary>The translation divisor, <c>(1&lt;&lt;S)-1</c>.</summary>
    public float Divisor { get; }
    public IReadOnlyList<AnimBone> Bones { get; }

    private BoneAnimation(int version, uint rawFrameCount, int quantBits, List<AnimBone> bones)
    {
        Version = version;
        RawFrameCount = rawFrameCount;
        FrameCount = (int)(rawFrameCount & 0xFFFF);
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

        uint rawFrames = br.ReadUInt32();
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
        return new BoneAnimation(version, rawFrames, s, bones);
    }

    /// <summary>The file this clip was read from, or the one a new clip makes - byte for byte what <see cref="Load"/> read.</summary>
    public byte[] Write()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Version);
        w.Write((ushort)Bones.Count);
        foreach (var b in Bones) Skeleton.WriteSkeString(w, b.Name);
        w.Write(RawFrameCount);
        w.Write((byte)QuantBits);
        foreach (var b in Bones)
        {
            if (b.Blob.Length > ushort.MaxValue) throw new InvalidDataException($"{b.Name}: {b.Blob.Length} values do not fit a .baf bone (65535 at most).");
            w.Write((ushort)b.Blob.Length);
            for (int c = 0; c < ChannelCount; c++)
            {
                int l = b.ChannelLength(c);
                w.Write((ushort)l);
                for (int k = 0; k < l; k++) w.Write(b.Blob[b.ChannelOffset[c] + k]);
            }
        }
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// Sample one channel of a bone at an integer frame. Ports <c>CompressedAnim::GetValue</c>: walk the run headers to
    /// the run covering <paramref name="frame"/> and dequantize its value - rotations at 15 bits, translations at S (see the
    /// class remarks).
    /// </summary>
    public float Sample(AnimBone bone, int channel, int frame)
        => Raw(bone, channel, frame) / (channel < 4 ? RotationScale : Divisor);

    /// <summary>The stored int16 of one channel at an integer frame (frames past the end read the last value).</summary>
    public short Raw(AnimBone bone, int channel, int frame)
    {
        short[] blob = bone.Blob;
        int based = bone.ChannelOffset[channel], len = bone.ChannelLength(channel);
        int idx = 0, rem = Math.Clamp(frame, 0, Math.Max(0, FrameCount - 1));
        int hdr = (ushort)blob[based];
        int span = hdr & 0x7F;
        while (rem > span - 1)
        {
            int step = (hdr >> 8) & 0xFF;
            if (step == 0 || idx + step >= len) break;   // a malformed stream: stay on the last run
            rem -= span;
            idx += step;
            hdr = (ushort)blob[based + idx];
            span = hdr & 0x7F;
        }
        int valueIdx = (hdr & 0x80) != 0 ? idx : idx + Math.Min(rem, Math.Max(0, span - 1));
        return blob[based + valueIdx + 1];
    }

    /// <summary>Every frame's stored int16 of one channel.</summary>
    public short[] Values(AnimBone bone, int channel)
        => DecodeChannel(bone.Blob, bone.ChannelOffset[channel], FrameCount);

    public (float X, float Y, float Z, float W) GetQuat(AnimBone bone, int frame) =>
        (Sample(bone, 0, frame), Sample(bone, 1, frame), Sample(bone, 2, frame), Sample(bone, 3, frame));

    public (float X, float Y, float Z) GetTrans(AnimBone bone, int frame) =>
        (Sample(bone, 4, frame), Sample(bone, 5, frame), Sample(bone, 6, frame));

    /// <summary>The bone's rotation at a frame, normalized.</summary>
    public Quaternion Rotation(AnimBone bone, int frame)
    {
        var (x, y, z, w) = GetQuat(bone, frame);
        var q = new Quaternion(x, y, z, w);
        return q.LengthSquared() > 1e-16f ? Quaternion.Normalize(q) : Quaternion.Identity;
    }

    public Vector3 Translation(AnimBone bone, int frame)
    {
        var (x, y, z) = GetTrans(bone, frame);
        return new Vector3(x, y, z);
    }

    /// <summary>Every bone's motion, a value per frame.</summary>
    public IReadOnlyList<BoneTrack> ToTracks()
    {
        var o = new List<BoneTrack>(Bones.Count);
        foreach (var b in Bones)
        {
            var r = new Quaternion[FrameCount]; var t = new Vector3[FrameCount];
            for (int f = 0; f < FrameCount; f++) { r[f] = Rotation(b, f); t[f] = Translation(b, f); }
            o.Add(new BoneTrack(b.Name, r, t));
        }
        return o;
    }

    /// <summary>
    /// A new clip of <paramref name="frameCount"/> frames from per-frame tracks, encoded the way DICE's exporter did:
    /// rotations at 15 bits, translations at the largest S that holds the furthest move (<see cref="ChooseQuantBits"/>),
    /// runs of 6..126 equal values stored once and the rest as literal runs of at most 125.
    /// </summary>
    public static BoneAnimation Create(IReadOnlyList<BoneTrack> tracks, int frameCount, int? quantBits = null)
    {
        // The engine's loader (BoneAnimation::BoneAnimation) keeps the count in a byte and reads each name into a 272-byte
        // buffer: a clip it can play has 1..255 bones named in 271 characters or fewer.
        if (tracks.Count is 0 or > MaxBones) throw new ArgumentException($"{tracks.Count} bones: the games play a clip of 1..{MaxBones}.");
        if (tracks.FirstOrDefault(t => t.Name.Length > MaxNameLength) is { } longName)
            throw new ArgumentException($"bone '{longName.Name[..20]}...' is named in {longName.Name.Length} characters: the games read {MaxNameLength} at most.");
        if (frameCount is < 1 or > 0xFFFF) throw new ArgumentOutOfRangeException(nameof(frameCount), frameCount, "a clip holds 1..65535 frames");
        float furthest = 0f;
        foreach (var t in tracks)
        {
            if (t.Rotations.Length < frameCount || t.Translations.Length < frameCount)
                throw new ArgumentException($"{t.Name}: {t.Rotations.Length} rotations and {t.Translations.Length} translations for {frameCount} frames.");
            for (int f = 0; f < frameCount; f++)
                furthest = MathF.Max(furthest, MathF.Max(MathF.Abs(t.Translations[f].X), MathF.Max(MathF.Abs(t.Translations[f].Y), MathF.Abs(t.Translations[f].Z))));
        }
        int s = quantBits ?? ChooseQuantBits(furthest);
        if (s is < 1 or > 15) throw new ArgumentOutOfRangeException(nameof(quantBits), s, "S is 1..15");
        float divisor = (1 << s) - 1;

        var bones = new List<AnimBone>(tracks.Count);
        var perFrame = new short[frameCount];
        foreach (var t in tracks)
        {
            var blob = new List<short>();
            var offsets = new int[ChannelCount];
            for (int c = 0; c < ChannelCount; c++)
            {
                for (int f = 0; f < frameCount; f++)
                {
                    var q = t.Rotations[f];   // the sign is kept as given: interpolation follows the author's path
                    float v = c switch
                    {
                        0 => q.X, 1 => q.Y, 2 => q.Z, 3 => q.W,
                        4 => t.Translations[f].X, 5 => t.Translations[f].Y, _ => t.Translations[f].Z,
                    };
                    perFrame[f] = Quantize(v, c < 4 ? RotationScale : divisor);
                }
                offsets[c] = blob.Count;
                blob.AddRange(EncodeChannel(perFrame));
            }
            if (blob.Count > ushort.MaxValue) throw new InvalidDataException($"{t.Name}: the clip is too long to store ({blob.Count} values, 65535 at most).");
            bones.Add(new AnimBone { Name = t.Name, Blob = blob.ToArray(), ChannelOffset = offsets });
        }
        return new BoneAnimation(3, (uint)frameCount, s, bones);
    }

    /// <summary>The largest S (1..15) whose translation range, 32767 / ((1&lt;&lt;S)-1) metres, holds <paramref name="furthest"/> -
    /// 15 for moves under a metre, as DICE's exporter chose.</summary>
    public static int ChooseQuantBits(float furthest)
    {
        int s = 15;
        while (s > 1 && furthest * ((1 << s) - 1) > short.MaxValue) s--;
        return s;
    }

    private static short Quantize(float v, float scale)
    {
        double q = Math.Round(v * (double)scale, MidpointRounding.AwayFromZero);
        return (short)Math.Clamp(q, -short.MaxValue, short.MaxValue);
    }

    /// <summary>One channel's stream for per-frame values: equal values in runs of 6 or more stored once (126 frames a run
    /// at most), everything else as literal runs of at most 125. Decodes to exactly <paramref name="perFrame"/>.</summary>
    public static short[] EncodeChannel(ReadOnlySpan<short> perFrame)
    {
        var o = new List<short>(perFrame.Length + 4);
        int litStart = 0, i = 0;
        void FlushLiteral(int end, ReadOnlySpan<short> v)
        {
            for (int s = litStart; s < end; s += MaxLiteralRun)
            {
                int k = Math.Min(MaxLiteralRun, end - s);
                o.Add((short)(((k + 1) << 8) | k));
                for (int j = 0; j < k; j++) o.Add(v[s + j]);
            }
        }
        while (i < perFrame.Length)
        {
            int j = i;
            while (j < perFrame.Length && perFrame[j] == perFrame[i]) j++;
            if (j - i >= MinConstantRun)
            {
                FlushLiteral(i, perFrame);
                int run = j - i;
                while (run > 0)
                {
                    int take = Math.Min(MaxConstantRun, run);
                    // Leave the last piece long enough to be a constant run too (126 + 124 + 6, not 126 + 126 + a literal 4).
                    if (run - take is > 0 and < MinConstantRun) take = run - MinConstantRun;
                    o.Add(unchecked((short)((2 << 8) | 0x80 | take)));
                    o.Add(perFrame[i]);
                    run -= take;
                }
                litStart = j;
            }
            i = j;
        }
        FlushLiteral(perFrame.Length, perFrame);
        return o.ToArray();
    }

    /// <summary>Every frame's int16 of one stream starting at <paramref name="start"/> in <paramref name="blob"/>.</summary>
    public static short[] DecodeChannel(short[] blob, int start, int frames)
    {
        var o = new short[frames];
        int idx = start, f = 0;
        while (f < frames && idx < blob.Length)
        {
            int hdr = (ushort)blob[idx], span = hdr & 0x7F, stride = (hdr >> 8) & 0xFF;
            bool constant = (hdr & 0x80) != 0;
            for (int k = 0; k < span && f < frames; k++) o[f++] = constant ? blob[idx + 1] : blob[idx + 1 + k];
            if (stride == 0) break;
            idx += stride;
        }
        for (; f < frames && f > 0; f++) o[f] = o[f - 1];
        return o;
    }
}
