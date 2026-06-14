using System.Numerics;

namespace RefractorForge.Render;

/// <summary>
/// A fly camera. Produces standard view/projection matrices (System.Numerics, right-handed).
/// Used by both the headless software rasterizer and the GPU viewer so the projection math
/// is identical and verified once.
/// </summary>
public sealed class Camera
{
    public Vector3 Position;
    public float Yaw;    // radians; 0 looks toward +Z
    public float Pitch;  // radians; + looks up
    public float FovY = MathF.PI / 3f;   // 60°
    public float Near = 1f;
    public float Far = 60000f;
    public float Aspect = 1f;

    public Vector3 Forward => new(
        MathF.Cos(Pitch) * MathF.Sin(Yaw),
        MathF.Sin(Pitch),
        MathF.Cos(Pitch) * MathF.Cos(Yaw));

    public Vector3 Right => Vector3.Normalize(Vector3.Cross(Forward, Vector3.UnitY));

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Position, Position + Forward, Vector3.UnitY);
    public Matrix4x4 Projection => Matrix4x4.CreatePerspectiveFieldOfView(FovY, Aspect, Near, Far);

    /// <summary>Reflect the rendered scene left-right (negate clip X). The Battlefield level data is
    /// stored in a coordinate frame whose X reads mirrored relative to how the game/Battlecraft present
    /// the map; enabling this makes the editor's view match the game. It is purely a view transform —
    /// world data, saved files, picking and gizmos all stay in the native coordinates because they go
    /// through this same matrix (and its inverse).</summary>
    public bool MirrorX = false;
    public Matrix4x4 ViewProjection =>
        MirrorX ? View * Projection * Matrix4x4.CreateScale(-1f, 1f, 1f) : View * Projection;

    /// <summary>WASD-style planar move (XZ) plus vertical; speed already scaled by dt.</summary>
    public void Move(float fwd, float strafe, float up, float amount)
    {
        var f = Forward; f.Y = 0; if (f != Vector3.Zero) f = Vector3.Normalize(f);
        Position += (f * fwd + Right * strafe) * amount + Vector3.UnitY * (up * amount);
    }

    public void Look(float dYaw, float dPitch)
    {
        Yaw += dYaw;
        Pitch = Math.Clamp(Pitch + dPitch, -1.55f, 1.55f);
    }

    /// <summary>Move along the true (un-flattened) view direction — zoom toward what you're looking at.</summary>
    public void Dolly(float amount) => Position += Forward * amount;

    /// <summary>Point the camera at a world target from its current position.</summary>
    public void LookAt(Vector3 target)
    {
        var dir = target - Position;
        if (dir.LengthSquared() < 1e-6f) return;
        dir = Vector3.Normalize(dir);
        Pitch = MathF.Asin(Math.Clamp(dir.Y, -1f, 1f));
        Yaw = MathF.Atan2(dir.X, dir.Z);
    }

    /// <summary>Frame an entire map in an oblique aerial view.</summary>
    public static Camera FrameAerial(float worldSize, float midHeight, float aspect)
    {
        var target = new Vector3(worldSize / 2f, midHeight, worldSize / 2f);
        float dist = worldSize * 0.95f;
        float elev = MathF.PI * 0.22f;   // ~40° above horizon
        float azim = MathF.PI * 0.75f;
        var offset = new Vector3(
            MathF.Cos(elev) * MathF.Sin(azim),
            MathF.Sin(elev),
            MathF.Cos(elev) * MathF.Cos(azim)) * dist;
        var pos = target + offset;
        var dir = Vector3.Normalize(target - pos);
        return new Camera
        {
            Position = pos,
            Aspect = aspect,
            Far = worldSize * 4f,
            Pitch = MathF.Asin(dir.Y),
            Yaw = MathF.Atan2(dir.X, dir.Z),
        };
    }
}
