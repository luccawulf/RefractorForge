using System.Collections.Concurrent;
using System.Numerics;
using RefractorForge.Formats.Animation;
using RefractorForge.Formats.Rfa;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The skeletal-animation files written as well as read: <c>.ske</c>, <c>.skn</c> and <c>.baf</c> round-trip byte for byte,
/// new clips and skins come out the way the engine reads them, and a skin binds to its mesh by the engine's own rule.
/// </summary>
public class AnimationFormatsTests
{
    private static float[] Rot(Vector3 axis, float degrees, Vector3 t)
        => SkeletalMath.FromQuatTrans(Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), degrees * MathF.PI / 180f), t);

    /// <summary>A three-bone chain with real rotations: root, a bone a metre up the root's Y, and one off to its side.</summary>
    private static Skeleton Chain() => Skeleton.Create(new[]
    {
        new SkeletonBone { Name = "Root", Parent = -1, Local = Rot(Vector3.UnitY, 30, new Vector3(0.2f, 0.1f, -0.3f)) },
        new SkeletonBone { Name = "Mid", Parent = 0, Local = Rot(Vector3.UnitX, 40, new Vector3(0f, 1f, 0f)) },
        new SkeletonBone { Name = "Tip", Parent = 1, Local = Rot(new Vector3(1, 1, 0), -25, new Vector3(0.3f, 0.5f, 0.1f)) },
    });

    [Fact]
    public void A_new_clip_stores_every_frame_within_its_runs_limits()
    {
        const int frames = 300;
        var rng = new Random(5);
        var tracks = new List<BoneTrack>();
        for (int b = 0; b < 3; b++)
        {
            var r = new Quaternion[frames]; var t = new Vector3[frames];
            for (int f = 0; f < frames; f++)
            {
                // long still stretches (constant runs, split at 126), short ones (literal) and motion
                bool still = f < 140 || (f >= 200 && f < 204);
                float a = still ? 0.3f * b : 0.01f * f + (float)rng.NextDouble() * 0.2f;
                r[f] = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1, b, 0.5f)), a);
                t[f] = still ? new Vector3(0.1f * b, 0, 0) : new Vector3(3f * MathF.Sin(f * 0.05f), 0.5f, -1f);
            }
            tracks.Add(new BoneTrack("Bone" + b, r, t));
        }
        var clip = BoneAnimation.Create(tracks, frames);
        Assert.Equal(13, clip.QuantBits);                         // 3 m needs S 13 (range 4 m)

        var back = BoneAnimation.Load(clip.Write());
        Assert.Equal(clip.Write(), back.Write());
        for (int b = 0; b < 3; b++)
        {
            var bone = back.Bones[b];
            for (int f = 0; f < frames; f++)
            {
                var q = back.Rotation(bone, f); var want = Quaternion.Normalize(tracks[b].Rotations[f]);
                Assert.True(MathF.Abs(Quaternion.Dot(q, want)) > 0.99999f, $"bone {b} frame {f}: {q} vs {want}");
                Assert.True(Vector3.Distance(back.Translation(bone, f), tracks[b].Translations[f]) < 1f / 8191f, $"bone {b} frame {f}");
            }
            for (int c = 0; c < BoneAnimation.ChannelCount; c++)
            {
                int idx = bone.ChannelOffset[c], end = idx + bone.ChannelLength(c), covered = 0;
                while (idx < end)
                {
                    int hdr = (ushort)bone.Blob[idx], span = hdr & 0x7F, stride = hdr >> 8;
                    if ((hdr & 0x80) != 0) { Assert.InRange(span, 6, 126); Assert.Equal(2, stride); }
                    else { Assert.InRange(span, 1, 125); Assert.Equal(span + 1, stride); }
                    covered += span; idx += stride;
                }
                Assert.Equal(frames, covered);
                Assert.Equal(end, idx);
            }
        }
    }

    [Fact]
    public void Rotations_are_read_at_fifteen_bits_whatever_the_clip_s_translations_need()
    {
        var r = Enumerable.Range(0, 10).Select(f => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, f * 0.1f)).ToArray();
        var t = Enumerable.Range(0, 10).Select(f => new Vector3(f * 3f, 0, 0)).ToArray();   // 27 m: S 10
        var clip = BoneAnimation.Load(BoneAnimation.Create(new[] { new BoneTrack("B", r, t) }, 10).Write());
        Assert.Equal(10, clip.QuantBits);
        for (int f = 0; f < 10; f++)
        {
            var (x, y, z, w) = clip.GetQuat(clip.Bones[0], f);
            Assert.InRange(MathF.Sqrt(x * x + y * y + z * z + w * w), 0.9999f, 1.0001f);
            Assert.InRange(clip.Translation(clip.Bones[0], f).X, f * 3f - 0.001f, f * 3f + 0.001f);
        }
        Assert.Equal(15, BoneAnimation.ChooseQuantBits(0.99f));
        Assert.Equal(14, BoneAnimation.ChooseQuantBits(1.5f));
        Assert.Equal(9, BoneAnimation.ChooseQuantBits(60f));
    }

    [Fact]
    public void The_engine_keeps_the_two_heaviest_influences_and_reweights_the_first()
    {
        SkinInfluence I(int bone, float w) => new(bone, w, bone, 0, 0);
        // heaviest first; a light one left out; the first reweighted to 1 - second
        var kept = Skin.EngineInfluences(new[] { I(0, 0.2f), I(1, 0.005f), I(2, 0.5f), I(3, 0.3f) });
        Assert.Equal(new[] { 2, 3 }, kept.Select(k => k.LocalBoneIndex));
        Assert.Equal(0.7f, kept[0].Weight, 5);
        Assert.Equal(0.3f, kept[1].Weight, 5);
        // two kept as they are, even when they do not add up to one
        kept = Skin.EngineInfluences(new[] { I(4, 0.6f), I(5, 0.3f) });
        Assert.Equal(new[] { 0.6f, 0.3f }, kept.Select(k => k.Weight));
        // nothing heavy enough: shared equally, then two kept
        kept = Skin.EngineInfluences(new[] { I(6, 0.001f), I(7, 0.002f), I(8, 0.003f) });
        Assert.Equal(new[] { 6, 7 }, kept.Select(k => k.LocalBoneIndex));
        Assert.Equal(1f - 1f / 3f, kept[0].Weight, 5);
        // ties keep file order
        kept = Skin.EngineInfluences(new[] { I(1, 0.5f), I(2, 0.5f) });
        Assert.Equal(new[] { 1, 2 }, kept.Select(k => k.LocalBoneIndex));
    }

    [Fact]
    public void A_mesh_vertex_follows_the_skin_vertex_within_a_tenth_of_a_millimetre_even_across_a_bucket_edge()
    {
        SkinVertex V(float x, float y, float z) => new() { X = x, Y = y, Z = z, Influences = new[] { new SkinInfluence(0, 1, 0, 0, 0) } };
        var skin = new[] { V(0.1f, 0.2f, 0.3f), V(1f, 1f, 1f), V(-0.35f, 2f, 0f) };
        var bind = new SkinBinding(skin);
        Assert.Equal(0, bind.Find(new Vector3(0.1f, 0.2f, 0.3f)));
        // 0.09999 truncates to 0 tenths where 0.1 gives 1: the neighbouring bucket is searched too
        Assert.Equal(0, bind.Find(new Vector3(0.09999f, 0.2f, 0.3f)));
        Assert.Equal(1, bind.Find(new Vector3(1.00005f, 1f, 1f)));
        Assert.Equal(-1, bind.Find(new Vector3(1.0002f, 1f, 1f)));        // 0.2 mm away: drawn rigid
        Assert.Equal(2, bind.Find(new Vector3(-0.35f, 2f, 0.00001f)));
        Assert.Equal(new[] { 0, 1, -1 }, bind.Bind(new[] { new Vector3(0.1f, 0.2f, 0.3f), new Vector3(1, 1, 1), new Vector3(5, 5, 5) }));
    }

    [Fact]
    public void A_built_skin_draws_its_mesh_where_it_is_at_rest_and_moves_it_with_its_bones()
    {
        var skel = Chain();
        var world = skel.ComputeWorld();
        // Two vertices on each bone, one blended between Mid and Tip, and a seam copy of the first.
        var positions = new List<Vector3>
        {
            new(0.1f, 0.2f, 0.3f), new(-0.2f, 0.4f, 0f),
            new(0.4f, 1.1f, -0.2f), new(0.2f, 1.3f, 0.2f),
            new(0.5f, 1.8f, 0.1f), new(0.7f, 1.6f, 0.3f),
            new(0.3f, 1.5f, 0f),
            new(0.1f, 0.2f, 0.3f),
        };
        var weights = new List<BoneWeights>
        {
            new(new[] { 0 }, new[] { 1f }), new(new[] { 0 }, new[] { 1f }),
            new(new[] { 1 }, new[] { 1f }), new(new[] { 1 }, new[] { 1f }),
            new(new[] { 2 }, new[] { 1f }), new(new[] { 2 }, new[] { 1f }),
            new(new[] { 1, 2, 0 }, new[] { 0.6f, 0.3f, 0.1f }),        // three given: two kept, renormalized
            new(new[] { 0 }, new[] { 1f }),
        };
        var skin = Skin.Build(positions, weights, skel);
        Assert.Equal(7, skin.Vertices.Count);                                // the seam copy welded
        Assert.Equal(new[] { "Root", "Mid", "Tip" }, skin.BoneNames);
        Assert.All(skin.Vertices, v => Assert.InRange(v.Influences.Length, 1, 2));
        var blended = skin.Vertices[6].Influences;
        Assert.Equal(1f, blended.Sum(i => i.Weight), 5);
        Assert.Equal(0.6f / 0.9f, blended[0].Weight, 4);

        // At rest the engine's skinning puts every mesh vertex back where it is.
        var rest = skin.Pose(skel, world);
        var binding = new SkinBinding(skin).Bind(positions);
        Assert.DoesNotContain(-1, binding);
        var drawn = SkinBinding.Apply(positions, binding, rest);
        for (int i = 0; i < positions.Count; i++)
            Assert.True(Vector3.Distance(drawn[i], positions[i]) < 1e-5f, $"vertex {i}: {drawn[i]} vs {positions[i]}");

        // Bending Mid moves Mid's and Tip's vertices rigidly about Mid's joint, leaves Root's alone.
        var locals = skel.Bones.Select(b => b.Local).ToArray();
        locals[1] = SkeletalMath.Mul(locals[1], Rot(Vector3.UnitZ, 35, Vector3.Zero));
        var bent = skin.Pose(skel, skel.ComputeWorld(locals));
        Assert.True(Vector3.Distance(bent[0], positions[0]) < 1e-5f);
        Assert.True(Vector3.Distance(bent[2], positions[2]) > 0.05f);
        Assert.Equal(Vector3.Distance(rest[2], rest[3]), Vector3.Distance(bent[2], bent[3]), 4);   // rigid on its bone

        // And the file round-trips.
        var back = Skin.Load(skin.Write());
        Assert.Equal(skin.Write(), back.Write());
    }

    [Fact]
    public void A_skeleton_round_trips_and_a_version_two_file_reads_its_quaternions()
    {
        var skel = Chain();
        var bytes = skel.Write();
        var back = Skeleton.Load(bytes);
        Assert.Equal(bytes, back.Write());
        for (int i = 0; i < 3; i++) Assert.Equal(skel.Bones[i].Local, back.Bones[i].Local);

        // Version 2: quaternion (x,y,z,w) + position per bone - read through the engine's toMat.
        var q = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1, 2, 3)), 0.7f);
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.Latin1, leaveOpen: true))
        {
            w.Write(2); w.Write(1);
            w.Write((ushort)5); w.Write("Bone"u8.ToArray()); w.Write((byte)0);
            w.Write((ushort)0xFFFF);
            foreach (var f in new[] { q.X, q.Y, q.Z, q.W, 1f, 2f, 3f }) w.Write(f);
        }
        var v2 = Skeleton.Load(ms.ToArray());
        Assert.Equal(SkeletalMath.FromQuatTrans(q, new Vector3(1, 2, 3)), v2.Bones[0].Local);
        Assert.Equal(ms.ToArray(), v2.Write());

        // ToQuat inverts FromQuatTrans.
        var back2 = SkeletalMath.ToQuat(SkeletalMath.FromQuatTrans(q, Vector3.Zero));
        Assert.True(MathF.Abs(Quaternion.Dot(back2, q)) > 0.99999f);
        Assert.Throws<ArgumentException>(() => Skeleton.Create(new[] { new SkeletonBone { Name = "A", Parent = 0, Local = SkeletalMath.Identity() } }));
    }

    // ---- the retail corpus

    private static IEnumerable<(string Archive, RefractorFlatArchive Rfa)> Archives()
    {
        foreach (var dir in new[]
                 {
                     Installs.Bf1942CleanArchives,
                     Installs.Bf1942Clean + @"\Mods\XPack1\Archives",
                     Installs.Bf1942Clean + @"\Mods\XPack2\Archives",
                     Installs.BfvOriginalArchives,
                 })
            if (Directory.Exists(dir))
                foreach (var f in Directory.EnumerateFiles(dir, "*.rfa"))
                    yield return (f, new RefractorFlatArchive(f));
    }

    [InstallFact(Installs.Bf1942CleanArchives, Installs.BfvOriginalArchives)]
    public void Every_retail_skeleton_skin_and_clip_is_written_back_byte_for_byte()
    {
        var failures = new ConcurrentBag<string>();
        int ske = 0, skn = 0, baf = 0;
        double worstRotation = 0;
        Parallel.ForEach(Archives().ToList(), a =>
        {
            var (path, rfa) = a;
            foreach (var e in rfa.Entries)
            {
                string n = e.Name.ToLowerInvariant();
                if (!n.EndsWith(".ske") && !n.EndsWith(".skn") && !n.EndsWith(".baf")) continue;
                var bytes = rfa.Read(e);
                try
                {
                    byte[] again;
                    if (n.EndsWith(".ske")) { again = Skeleton.Load(bytes).Write(); Interlocked.Increment(ref ske); }
                    else if (n.EndsWith(".skn")) { again = Skin.Load(bytes).Write(); Interlocked.Increment(ref skn); }
                    else
                    {
                        var clip = BoneAnimation.Load(bytes);
                        again = clip.Write();
                        Interlocked.Increment(ref baf);
                        double worst = 0;
                        foreach (var b in clip.Bones)
                            for (int f = 0; f < clip.FrameCount; f++)
                            {
                                var (x, y, z, w) = clip.GetQuat(b, f);
                                worst = Math.Max(worst, Math.Abs(Math.Sqrt(x * x + y * y + z * z + w * w) - 1));
                            }
                        lock (failures) worstRotation = Math.Max(worstRotation, worst);
                    }
                    if (!again.AsSpan().SequenceEqual(bytes)) failures.Add($"{Path.GetFileName(path)}|{e.Name}: rewritten differently");
                }
                catch (Exception ex) { failures.Add($"{Path.GetFileName(path)}|{e.Name}: {ex.GetType().Name} {ex.Message}"); }
            }
        });
        Assert.True(failures.IsEmpty, string.Join("\n", failures.Take(20)));
        Assert.True(ske > 150 && skn > 200 && baf > 4000, $"{ske} skeletons, {skn} skins, {baf} clips");
        // every rotation of every frame of every clip is a unit quaternion read at 15 bits - S < 15 clips included
        Assert.True(worstRotation < 1e-3, $"worst |q| - 1 = {worstRotation}");
    }

    [InstallFact(Installs.Bf1942CleanArchives, Installs.BfvOriginalArchives)]
    public void Retail_clips_re_encode_to_streams_that_decode_to_the_same_frames()
    {
        int channels = 0, clips = 0;
        foreach (var (_, rfa) in Archives())
            foreach (var e in rfa.Entries.Where(x => x.Name.EndsWith(".baf", StringComparison.OrdinalIgnoreCase)))
            {
                var clip = BoneAnimation.Load(rfa.Read(e));
                clips++;
                foreach (var b in clip.Bones)
                    for (int c = 0; c < BoneAnimation.ChannelCount; c++)
                    {
                        var frames = clip.Values(b, c);
                        var stream = BoneAnimation.EncodeChannel(frames);
                        Assert.Equal(frames, BoneAnimation.DecodeChannel(stream, 0, frames.Length));
                        Assert.True(stream.Length <= b.ChannelLength(c) + 2, $"{e.Name} {b.Name}:{c} grew from {b.ChannelLength(c)} to {stream.Length}");
                        channels++;
                    }
            }
        Assert.True(clips > 4000 && channels > 1_000_000, $"{clips} clips, {channels} channels");
    }

    [InstallFact(Installs.Bf1942CleanArchives)]
    public void The_engines_binding_reproduces_retail_coverage_soldiers_whole_and_the_sherman_track_in_part()
    {
        var anim = new RefractorFlatArchive(Installs.Bf1942CleanArchives + @"\animations.rfa");
        var meshes = new RefractorFlatArchive(Installs.Bf1942CleanArchives + @"\standardMesh.rfa");
        (int Bound, int Total) Coverage(string skin, string mesh)
        {
            var s = Skin.Load(anim.Read(anim.Entries.First(x => x.Name.EndsWith(skin, StringComparison.OrdinalIgnoreCase))));
            var m = StandardMesh.Parse(meshes.Read(meshes.Entries.First(x => x.Name.EndsWith(mesh, StringComparison.OrdinalIgnoreCase))));
            var positions = m.Lods[0].SelectMany(mat => mat.Vertices).Select(v => new Vector3(v.X, v.Y, v.Z)).ToList();
            var b = new SkinBinding(s).Bind(positions);
            return (b.Count(i => i >= 0), b.Length);
        }
        Assert.Equal((740, 740), Coverage("/GerBody.skn", "/GerBody.sm"));
        Assert.Equal((414, 414), Coverage("/Ger2Face.skn", "/Ger2Face.sm"));
        Assert.Equal((120, 289), Coverage("/sherman_TrackL_M1.skn", "/Sherman_TrackL_M1.sm"));
    }
}
