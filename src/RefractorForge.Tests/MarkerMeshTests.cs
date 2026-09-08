using System;
using System.Numerics;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The marker a sound area wears. An <c>AreaObject</c> has no geometry at all, so this shape IS the object as far
/// as the editor is concerned; a flipped face in it reads as a hole in the marker, and the only way to see that
/// otherwise is to launch the GPU. The winding bug these tests catch was real: the first cut had every side wall
/// facing inward, which the whole-mesh volume caught immediately (-0.32 instead of +0.95).
/// </summary>
public class MarkerMeshTests
{
    [Fact]
    public void The_music_note_winds_outward()
    {
        var (v, i) = MarkerMeshes.MusicNote();
        Assert.True(v.Length > 0 && i.Length % 3 == 0);
        // Divergence theorem: positive means every triangle faces out. A single inverted cap or wall flips the sign
        // or eats a visible chunk out of the total, so this is a real check and not just "is it non-empty".
        double vol6 = MarkerMeshes.SignedVolume6(v, i);
        Assert.True(vol6 > 0.9 && vol6 < 1.0, $"expected the note's volume x6 near 0.95, got {vol6:0.0000}");
    }

    [Fact]
    public void Every_face_agrees_with_the_normals_it_carries()
    {
        // The shader lights from the stored normals but culls on the geometric winding, so the two disagreeing is
        // the failure that renders as a lit surface you cannot see.
        var (v, idx) = MarkerMeshes.MusicNote();
        Vector3 P(uint k) => new(v[k * 8], v[k * 8 + 1], v[k * 8 + 2]);
        Vector3 N(uint k) => new(v[k * 8 + 3], v[k * 8 + 4], v[k * 8 + 5]);
        for (int t = 0; t + 2 < idx.Length; t += 3)
        {
            var geo = Vector3.Cross(P(idx[t + 1]) - P(idx[t]), P(idx[t + 2]) - P(idx[t]));
            if (geo.LengthSquared() < 1e-12f) continue;               // degenerate, nothing to compare
            var stored = N(idx[t]) + N(idx[t + 1]) + N(idx[t + 2]);
            Assert.True(Vector3.Dot(Vector3.Normalize(geo), Vector3.Normalize(stored)) > 0.5f,
                        $"triangle {t / 3} winds against its own normal");
        }
    }

    [Fact]
    public void The_note_is_upright_and_about_two_units_tall()
    {
        // The viewer scales this by distance and stands it on the sound's position, so its proportions decide how
        // the marker reads on screen: taller than it is wide, and thin enough to be a note rather than a slab.
        var (v, _) = MarkerMeshes.MusicNote();
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue,
              minZ = float.MaxValue, maxZ = float.MinValue;
        for (int k = 0; k < v.Length; k += 8)
        {
            minX = MathF.Min(minX, v[k]); maxX = MathF.Max(maxX, v[k]);
            minY = MathF.Min(minY, v[k + 1]); maxY = MathF.Max(maxY, v[k + 1]);
            minZ = MathF.Min(minZ, v[k + 2]); maxZ = MathF.Max(maxZ, v[k + 2]);
        }
        Assert.InRange(maxY - minY, 1.5f, 2.2f);
        Assert.True(maxY - minY > maxX - minX, "a note is taller than it is wide");
        Assert.InRange(maxZ - minZ, 0.1f, 0.4f);
    }
}
