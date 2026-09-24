using System;
using System.Numerics;

namespace RefractorForge.Formats.Animation;

/// <summary>
/// Refractor skeletal matrix math, matching the engine in memory: a matrix is a float[16] laid out exactly as the
/// engine's <c>BaseMatrix4&lt;float&gt;</c> - basis vectors at [0..2], [4..6], [8..10], translation at [12..14], [15] = 1 -
/// and a point transforms as <c>x' = x*m[0] + y*m[4] + z*m[8] + m[12]</c> (and likewise for y', z'), which is
/// <c>Skin::getBlendedVertices</c>' own arithmetic. Read as column-major with column vectors this is also what GLSL's
/// <c>mat4</c> expects, so a matrix uploads to a shader verbatim.
/// <para>Checked against the Linux server by running its own code (<c>BaseMatrix4::mult</c> 0x08062440,
/// <c>BaseQuaternion::toMat</c> 0x08226180) on test inputs: <see cref="Mul"/> and <see cref="FromQuatTrans"/> give the
/// engine's bytes, and <c>Skeleton::transform</c> composes <c>world = mult(local, parentWorld)</c>, i.e.
/// <c>Mul(parentWorld, local)</c> here.</para>
/// </summary>
public static class SkeletalMath
{
    /// <summary>Identity matrix.</summary>
    public static float[] Identity() =>
        new float[16] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

    /// <summary>
    /// The engine's <c>BaseMatrix4::mult(out, A, B)</c> with B = <paramref name="b"/> and A = <paramref name="a"/>: apply A,
    /// then B. With A = local and B = parentWorld this is the bone's world matrix.
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
    /// A rotation+translation matrix from a quaternion (x,y,z,w) and a translation - <c>BaseQuaternion::toMat</c>'s own
    /// result, byte for byte, including its s = 2/(x²+y²+z²+w²), which makes any non-zero length of quaternion give the
    /// same rotation.
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
        m[0] = 1f - (yy + zz); m[1] = xy - wz; m[2] = xz + wy; m[3] = 0f;
        m[4] = xy + wz; m[5] = 1f - (xx + zz); m[6] = yz - wx; m[7] = 0f;
        m[8] = xz - wy; m[9] = yz + wx; m[10] = 1f - (xx + yy); m[11] = 0f;
        m[12] = tx; m[13] = ty; m[14] = tz; m[15] = 1f;
        return m;
    }

    public static float[] FromQuatTrans(Quaternion q, Vector3 t) => FromQuatTrans(q.X, q.Y, q.Z, q.W, t.X, t.Y, t.Z);

    /// <summary>The unit quaternion <see cref="FromQuatTrans"/> turns back into this matrix's rotation (w kept non-negative).</summary>
    public static Quaternion ToQuat(float[] m)
    {
        // m holds R row-major: R00=m0 R01=m1 R02=m2 / R10=m4 R11=m5 R12=m6 / R20=m8 R21=m9 R22=m10.
        double r00 = m[0], r01 = m[1], r02 = m[2], r10 = m[4], r11 = m[5], r12 = m[6], r20 = m[8], r21 = m[9], r22 = m[10];
        double trace = r00 + r11 + r22, x, y, z, w;
        if (trace > 0)
        {
            double s = Math.Sqrt(trace + 1.0) * 2;
            w = 0.25 * s; x = (r21 - r12) / s; y = (r02 - r20) / s; z = (r10 - r01) / s;
        }
        else if (r00 > r11 && r00 > r22)
        {
            double s = Math.Sqrt(1.0 + r00 - r11 - r22) * 2;
            w = (r21 - r12) / s; x = 0.25 * s; y = (r01 + r10) / s; z = (r02 + r20) / s;
        }
        else if (r11 > r22)
        {
            double s = Math.Sqrt(1.0 + r11 - r00 - r22) * 2;
            w = (r02 - r20) / s; x = (r01 + r10) / s; y = 0.25 * s; z = (r12 + r21) / s;
        }
        else
        {
            double s = Math.Sqrt(1.0 + r22 - r00 - r11) * 2;
            w = (r10 - r01) / s; x = (r02 + r20) / s; y = (r12 + r21) / s; z = 0.25 * s;
        }
        var q = Quaternion.Normalize(new Quaternion((float)x, (float)y, (float)z, (float)w));
        return q.W < 0 ? -q : q;
    }

    /// <summary>The inverse of a rotation+translation matrix (its rotation part must be orthonormal).</summary>
    public static float[] InverseRigid(float[] m)
    {
        var o = new float[16];
        // The rotation's inverse is its transpose (in any layout); the translation is -R^-1 t.
        o[0] = m[0]; o[1] = m[4]; o[2] = m[8];
        o[4] = m[1]; o[5] = m[5]; o[6] = m[9];
        o[8] = m[2]; o[9] = m[6]; o[10] = m[10];
        float tx = m[12], ty = m[13], tz = m[14];
        o[12] = -(o[0] * tx + o[4] * ty + o[8] * tz);
        o[13] = -(o[1] * tx + o[5] * ty + o[9] * tz);
        o[14] = -(o[2] * tx + o[6] * ty + o[10] * tz);
        o[15] = 1f;
        return o;
    }

    /// <summary>The translation of a matrix.</summary>
    public static (float X, float Y, float Z) Translation(float[] m) => (m[12], m[13], m[14]);

    /// <summary>Transform a point by a matrix, as the engine's skinning does.</summary>
    public static (float X, float Y, float Z) TransformPoint(float[] m, float x, float y, float z)
        => (m[0] * x + m[4] * y + m[8] * z + m[12],
            m[1] * x + m[5] * y + m[9] * z + m[13],
            m[2] * x + m[6] * y + m[10] * z + m[14]);

    public static Vector3 TransformPoint(float[] m, Vector3 p)
    {
        var (x, y, z) = TransformPoint(m, p.X, p.Y, p.Z);
        return new Vector3(x, y, z);
    }

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
