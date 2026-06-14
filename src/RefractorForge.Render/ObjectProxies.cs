using System.Numerics;
using RefractorForge.Formats.Con;

namespace RefractorForge.Render;

/// <summary>
/// Turns placed objects into renderable instances. Today each object is a proxy box transformed by
/// its real position/rotation/scale; when StandardMesh loading lands, only the model arrays passed to
/// <see cref="SoftwareRenderer.DrawModels"/> change — the per-object transforms computed here are identical.
/// </summary>
public static class ObjectProxies
{
    public static List<ModelInstance> Build(IEnumerable<StaticObject> objects, float width = 8f, float height = 18f)
    {
        var list = new List<ModelInstance>();
        foreach (var o in objects)
        {
            float sc = o.Scale ?? 1f;
            var world = Matrix4x4.CreateScale(width * sc, height * sc, width * sc)
                      * Matrix4x4.CreateFromYawPitchRoll(Rad(o.Rotation.X), Rad(o.Rotation.Y), Rad(o.Rotation.Z))
                      * Matrix4x4.CreateTranslation(o.Position.X, o.Position.Y + height * sc * 0.5f, o.Position.Z);
            list.Add(new ModelInstance(world, ColorFor(o.Template)));
        }
        return list;
    }

    private static float Rad(float deg) => deg * MathF.PI / 180f;

    /// <summary>Stable per-template color (FNV-1a hash) so same object types share a color.</summary>
    public static Vector3 ColorFor(string template)
    {
        uint h = 2166136261u;
        foreach (char c in template) { h ^= c; h *= 16777619u; }
        return new Vector3(
            0.35f + 0.6f * ((h & 0xFF) / 255f),
            0.35f + 0.6f * (((h >> 8) & 0xFF) / 255f),
            0.35f + 0.6f * (((h >> 16) & 0xFF) / 255f));
    }
}
