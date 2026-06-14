using System;

namespace RefractorForge.Formats.Animation;

/// <summary>
/// Refractor skeletal matrix math, ported byte-faithfully from the engine (BaseMatrix4::mult 0x08062440,
/// BaseQuaternion::toMat 0x08226180). Matrices are COLUMN-MAJOR float[16] with column-vector convention:
/// element(row,col) = m[col*4 + row], the translation column is m[12..14], m[15] = 1, and world = parent * local.
/// This is the same layout GLSL's <c>mat4</c> expects, so a matrix can be uploaded to a shader verbatim.
/// </summary>
public static class SkeletalMath
{
    /// <summary>Identity matrix (column-major float[16]).</summary>
    public static float[] Identity() =>
        new float[16] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

    /// <summary>
    /// Returns B * A (B on the left), reproducing the engine's <c>BaseMatrix4::mult(out, A, B)</c>.
    /// With A = local and B = parentWorld this yields world = parentWorld * local.
    /// </summary>
    public static float[] Mul(float[] b, float[] a)
    {
        var o = new float[16];
        for (int col = 0; col < 4; col++)
        {
            for (int row = 0; row < 3; row++)
            {
                float v = b[0 * 4 + row] * a[col * 4 + 0]
                        + b[1 * 4 + row] * a[col * 4 + 1]
                        + b[2 * 4 + row] * a[col * 4 + 2];
                if (col == 3) v += b[3 * 4 + row]; // add B's translation only for the translation column
                o[col * 4 + row] = v;
            }
            o[col * 4 + 3] = col == 3 ? 1f : 0f;
        }
        return o;
    }

    /// <summary>
    /// Build a column-major rotation+translation matrix from a quaternion (x,y,z,w) and a translation.
    /// Uses BaseQuaternion::toMat's s = 2/(x²+y²+z²+w²) (tolerant of slightly non-unit quats), but stores the
    /// TRANSPOSE of the engine's rotation so the animated local matrices share the same basis as the
    /// transposed <c>.ske</c> rest matrices (see <see cref="Skeleton.Load"/>) — i.e. both sit in this code's
    /// column-major / Mul convention. The two must match for layered clip bones to compose with rest bones.
    /// </summary>
    public static float[] FromQuatTrans(float qx, float qy, float qz, float qw, float tx, float ty, float tz)
    {
        float n = qx * qx + qy * qy + qz * qz + qw * qw;
        float s = n > 0f ? 2f / n : 0f;
        float xs = qx * s, ys = qy * s, zs = qz * s;
        float wx = qw * xs, wy = qw * ys, wz = qw * zs;
        float xx = qx * xs, xy = qx * ys, xz = qx * zs;
        float yy = qy * ys, yz = qy * zs, zz = qz * zs;
        var m = new float[16];
        m[0] = 1f - (yy + zz); m[1] = xy - wz; m[2] = xz + wy; m[3] = 0f; // col0 (transposed)
        m[4] = xy + wz; m[5] = 1f - (xx + zz); m[6] = yz - wx; m[7] = 0f; // col1
        m[8] = xz - wy; m[9] = yz + wx; m[10] = 1f - (xx + yy); m[11] = 0f; // col2
        m[12] = tx; m[13] = ty; m[14] = tz; m[15] = 1f; // translation
        return m;
    }

    /// <summary>The translation (column 3) of a column-major matrix.</summary>
    public static (float X, float Y, float Z) Translation(float[] m) => (m[12], m[13], m[14]);

    /// <summary>Transform a point by a column-major matrix (m * p, with implicit w = 1).</summary>
    public static (float X, float Y, float Z) TransformPoint(float[] m, float x, float y, float z)
        => (m[0] * x + m[4] * y + m[8] * z + m[12],
            m[1] * x + m[5] * y + m[9] * z + m[13],
            m[2] * x + m[6] * y + m[10] * z + m[14]);

    /// <summary>Normalized linear blend (nlerp) of two quaternions — a cheap stand-in for slerp; both inputs (x,y,z,w).</summary>
    public static (float X, float Y, float Z, float W) NlerpQuat(
        (float X, float Y, float Z, float W) a, (float X, float Y, float Z, float W) b, float t)
    {
        // Shortest path: flip b if the dot is negative.
        float dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
        if (dot < 0f) { b = (-b.X, -b.Y, -b.Z, -b.W); }
        float x = a.X + (b.X - a.X) * t;
        float y = a.Y + (b.Y - a.Y) * t;
        float z = a.Z + (b.Z - a.Z) * t;
        float w = a.W + (b.W - a.W) * t;
        float len = MathF.Sqrt(x * x + y * y + z * z + w * w);
        if (len < 1e-8f) return a;
        float inv = 1f / len;
        return (x * inv, y * inv, z * inv, w * inv);
    }
}
