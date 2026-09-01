using RefractorForge.Formats.Con;
using RefractorForge.Formats.Geometry;

namespace RefractorForge.Formats.Editing;

/// <summary>
/// Whole-selection arrangements: distribute, mirror, align to the ground.
///
/// Each returns the NEW transforms rather than applying them, so the editor can wrap the result in one
/// composite undo step and one collaboration broadcast - the same path a hand-made move takes, so nothing here
/// can desync a peer or escape Ctrl+Z.
/// </summary>
public static class SelectionOps
{
    public readonly record struct Placement(string Id, Vec3 Position, Vec3 Rotation);

    /// <summary>
    /// Space the selection evenly along the line from the first object to the last, in selection order.
    /// The end objects stay put; everything between slides to an even spacing.
    /// </summary>
    public static List<Placement> DistributeEvenly(IReadOnlyList<StaticObject> sel)
    {
        var outp = new List<Placement>();
        if (sel.Count < 3) return outp;
        var a = sel[0].Position; var b = sel[^1].Position;
        int n = sel.Count - 1;
        for (int i = 1; i < n; i++)
        {
            float t = i / (float)n;
            var p = new Vec3(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
            outp.Add(new Placement(sel[i].Id, p, sel[i].Rotation));
        }
        return outp;
    }

    public enum MirrorAxis { X, Z }

    /// <summary>
    /// Mirror the selection across a vertical plane through its centroid. Positions reflect; yaw flips so a
    /// building that faced the road still faces it. Mirroring geometry itself is not possible - a mesh has no
    /// flipped twin - so what mirrors is the arrangement.
    /// </summary>
    public static List<Placement> Mirror(IReadOnlyList<StaticObject> sel, MirrorAxis axis)
    {
        var outp = new List<Placement>();
        if (sel.Count == 0) return outp;
        float cx = sel.Average(o => o.Position.X), cz = sel.Average(o => o.Position.Z);
        foreach (var o in sel)
        {
            var p = o.Position; var r = o.Rotation;
            Vec3 np, nr;
            if (axis == MirrorAxis.X)
            {
                np = new Vec3(2f * cx - p.X, p.Y, p.Z);
                nr = new Vec3(Wrap(-r.X), r.Y, r.Z);            // yaw flips across the YZ plane
            }
            else
            {
                np = new Vec3(p.X, p.Y, 2f * cz - p.Z);
                nr = new Vec3(Wrap(180f - r.X), r.Y, r.Z);      // yaw flips across the XY plane
            }
            outp.Add(new Placement(o.Id, np, nr));
        }
        return outp;
    }

    /// <summary>
    /// Tilt each object to lie on the ground under it. Pitch and roll come from the terrain normal; yaw is kept,
    /// because which way a thing faces is a choice and which way the ground slopes is not.
    /// Refractor's rotation is (yaw, pitch, roll) in degrees.
    /// </summary>
    public static List<Placement> AlignToGround(IReadOnlyList<StaticObject> sel, Func<float, float, Vec3> normalAt, bool dropToGround, Func<float, float, float>? heightAt)
    {
        var outp = new List<Placement>();
        foreach (var o in sel)
        {
            var n = normalAt(o.Position.X, o.Position.Z);
            float len = MathF.Sqrt(n.X * n.X + n.Y * n.Y + n.Z * n.Z);
            if (len < 1e-6f) continue;
            n = new Vec3(n.X / len, n.Y / len, n.Z / len);

            // Express the normal in the object's own frame (undo its yaw), then read pitch and roll off it.
            float yaw = o.Rotation.X * MathF.PI / 180f;
            float c = MathF.Cos(-yaw), s = MathF.Sin(-yaw);
            float lx = n.X * c - n.Z * s;      // sideways component -> roll
            float lz = n.X * s + n.Z * c;      // forward component  -> pitch
            float pitch = MathF.Atan2(lz, n.Y) * 180f / MathF.PI;
            float roll = -MathF.Atan2(lx, n.Y) * 180f / MathF.PI;

            var pos = o.Position;
            if (dropToGround && heightAt is not null) pos = new Vec3(pos.X, heightAt(pos.X, pos.Z), pos.Z);
            outp.Add(new Placement(o.Id, pos, new Vec3(o.Rotation.X, pitch, roll)));
        }
        return outp;
    }

    /// <summary>Every object sharing a template with any of the selection.</summary>
    public static List<string> SelectAllOfTemplate(StaticObjectsFile file, IEnumerable<string> templates)
    {
        var want = new HashSet<string>(templates, StringComparer.OrdinalIgnoreCase);
        return file.Objects.Where(o => want.Contains(o.Template)).Select(o => o.Id).ToList();
    }

    private static float Wrap(float deg)
    {
        deg %= 360f;
        if (deg > 180f) deg -= 360f;
        if (deg <= -180f) deg += 360f;
        return deg;
    }
}
