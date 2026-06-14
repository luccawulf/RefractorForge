using System;

namespace RefractorForge.Formats.Animation;

/// <summary>
/// Applies a <see cref="BoneAnimation"/> clip to a <see cref="Skeleton"/> to produce posed local and world
/// matrices, reproducing the engine's <c>BoneAnimation::applyOnSkeleton</c> + <c>Skeleton::transform</c>:
/// per animated bone, sample the two bracketing keyframes, slerp the rotation and lerp the translation,
/// build the local matrix, and compose world = parentWorld * local down the hierarchy.
/// </summary>
public static class SkeletalPose
{
    /// <summary>Map each clip bone to a skeleton bone index (case-insensitive name match; -1 if absent).</summary>
    public static int[] BindClip(Skeleton skeleton, BoneAnimation clip)
    {
        var map = new int[clip.Bones.Count];
        for (int i = 0; i < clip.Bones.Count; i++)
            map[i] = skeleton.FindBone(clip.Bones[i].Name);
        return map;
    }

    /// <summary>
    /// Compute the local matrix for every skeleton bone at clip time <paramref name="time"/> (in clip loops;
    /// fractional part interpolated). Bones the clip does not animate keep their <c>.ske</c> rest local matrix.
    /// </summary>
    public static float[][] PoseLocals(Skeleton skeleton, BoneAnimation clip, float time, int[]? bind = null)
    {
        int n = skeleton.Bones.Count;
        var locals = new float[n][];
        for (int i = 0; i < n; i++) locals[i] = skeleton.Bones[i].Local; // default: rest pose (shared reference; not mutated)
        ApplyClip(skeleton, clip, time, locals, bind);
        return locals;
    }

    /// <summary>
    /// Overlay a clip's animated bones onto an existing set of local matrices (rest pose, or a prior layer).
    /// Used to layer the weapon-independent lower-body clip with an upper-body clip (they animate disjoint
    /// bone sets), matching how the engine drives a moving soldier.
    /// </summary>
    public static void ApplyClip(Skeleton skeleton, BoneAnimation clip, float time, float[][] locals, int[]? bind = null)
    {
        bind ??= BindClip(skeleton, clip);

        int frames = clip.FrameCount;
        int i0, i1;
        float t;
        if (frames <= 1)
        {
            i0 = i1 = 0;
            t = 0f;
        }
        else
        {
            float frac = time - MathF.Floor(time);     // wrap to [0,1)
            float pos = frac * frames;
            i0 = (int)MathF.Floor(pos) % frames;
            if (i0 < 0) i0 += frames;
            i1 = (i0 + 1) % frames;
            t = pos - MathF.Floor(pos);
        }

        for (int b = 0; b < clip.Bones.Count; b++)
        {
            int bone = bind[b];
            if (bone < 0) continue; // bone not in this skeleton -> keep what's there
            var ab = clip.Bones[b];

            var qa = clip.GetQuat(ab, i0);
            var pa = clip.GetTrans(ab, i0);
            float qx, qy, qz, qw, tx, ty, tz;
            if (i1 == i0)
            {
                qx = qa.X; qy = qa.Y; qz = qa.Z; qw = qa.W;
                tx = pa.X; ty = pa.Y; tz = pa.Z;
            }
            else
            {
                var qb = clip.GetQuat(ab, i1);
                var pb = clip.GetTrans(ab, i1);
                var q = SkeletalMath.NlerpQuat((qa.X, qa.Y, qa.Z, qa.W), (qb.X, qb.Y, qb.Z, qb.W), t);
                qx = q.X; qy = q.Y; qz = q.Z; qw = q.W;
                tx = pa.X + (pb.X - pa.X) * t;
                ty = pa.Y + (pb.Y - pa.Y) * t;
                tz = pa.Z + (pb.Z - pa.Z) * t;
            }
            locals[bone] = SkeletalMath.FromQuatTrans(qx, qy, qz, qw, tx, ty, tz);
        }
    }

    /// <summary>Posed world matrix per skeleton bone at clip time <paramref name="time"/>.</summary>
    public static float[][] PoseWorld(Skeleton skeleton, BoneAnimation clip, float time, int[]? bind = null)
        => skeleton.ComputeWorld(PoseLocals(skeleton, clip, time, bind));

    /// <summary>
    /// Pose the skeleton with several layered clips (e.g. a lower-body locomotion clip + an upper-body clip),
    /// each applied at its own time, then compose world matrices. Later layers override earlier ones on any
    /// shared bone.
    /// </summary>
    public static float[][] PoseWorldLayered(Skeleton skeleton, (BoneAnimation Clip, float Time, int[]? Bind)[] layers)
    {
        int n = skeleton.Bones.Count;
        var locals = new float[n][];
        for (int i = 0; i < n; i++) locals[i] = skeleton.Bones[i].Local;
        foreach (var (clip, time, bind) in layers)
            if (clip != null) ApplyClip(skeleton, clip, time, locals, bind);
        return skeleton.ComputeWorld(locals);
    }
}
