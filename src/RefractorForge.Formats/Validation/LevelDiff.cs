using RefractorForge.Formats.Con;
using RefractorForge.Formats.Geometry;

namespace RefractorForge.Formats.Validation;

/// <summary>
/// What changed between two versions of a level.
///
/// Objects have no stable identity across saves - ids are assigned on load - so two files cannot be matched by
/// id. They are matched by (template, position) instead, with a small tolerance: an object that moved a little
/// is still "the same object, moved", not "one deleted and one added". Anything left unmatched on either side
/// after that is a genuine add or delete.
/// </summary>
public static class LevelDiff
{
    public enum Kind { Added, Removed, Moved, Rotated, Rescaled }
    public sealed record Change(Kind Kind, string Template, Vec3 Position, string Detail, string? NewId = null);

    public sealed class Result
    {
        public List<Change> Changes { get; } = new();
        public int Added => Changes.Count(c => c.Kind == Kind.Added);
        public int Removed => Changes.Count(c => c.Kind == Kind.Removed);
        public int Moved => Changes.Count(c => c.Kind == Kind.Moved);
        public int Rotated => Changes.Count(c => c.Kind == Kind.Rotated);
        public int Rescaled => Changes.Count(c => c.Kind == Kind.Rescaled);
        public int Unchanged { get; set; }
    }

    /// <param name="before">The older version.</param>
    /// <param name="after">The newer version (the one open in the editor, usually).</param>
    /// <param name="matchRadius">How far an object may have moved and still count as the same object.</param>
    public static Result Compare(StaticObjectsFile before, StaticObjectsFile after, float matchRadius = 25f)
    {
        var res = new Result();
        var oldByTemplate = before.Objects
            .GroupBy(o => o.Template, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var usedOld = new HashSet<StaticObject>();

        foreach (var n in after.Objects)
        {
            StaticObject? best = null;
            float bestD = float.MaxValue;
            if (oldByTemplate.TryGetValue(n.Template, out var cands))
                foreach (var o in cands)
                {
                    if (usedOld.Contains(o)) continue;
                    float d = Dist(o.Position, n.Position);
                    if (d < bestD) { bestD = d; best = o; }
                }

            if (best is null || bestD > matchRadius)
            {
                res.Changes.Add(new Change(Kind.Added, n.Template, n.Position, "new", n.Id));
                continue;
            }
            usedOld.Add(best);

            bool any = false;
            if (bestD > 0.05f)
            {
                res.Changes.Add(new Change(Kind.Moved, n.Template, n.Position, $"moved {bestD:0.0} m", n.Id));
                any = true;
            }
            if (Dist(best.Rotation, n.Rotation) > 0.5f)
            {
                res.Changes.Add(new Change(Kind.Rotated, n.Template, n.Position,
                    $"rotated ({best.Rotation.X:0},{best.Rotation.Y:0},{best.Rotation.Z:0}) -> ({n.Rotation.X:0},{n.Rotation.Y:0},{n.Rotation.Z:0})", n.Id));
                any = true;
            }
            float so = best.Scale ?? 1f, sn = n.Scale ?? 1f;
            if (MathF.Abs(so - sn) > 0.01f)
            {
                res.Changes.Add(new Change(Kind.Rescaled, n.Template, n.Position, $"scale {so:0.##} -> {sn:0.##}", n.Id));
                any = true;
            }
            if (!any) res.Unchanged++;
        }

        foreach (var o in before.Objects)
            if (!usedOld.Contains(o))
                res.Changes.Add(new Change(Kind.Removed, o.Template, o.Position, "deleted"));

        return res;
    }

    public static LevelReport ToReport(Result d, string beforeLabel, string afterLabel)
    {
        var r = new LevelReport($"Diff: {beforeLabel} -> {afterLabel}");
        r.Add(IssueSeverity.Info, "Summary",
            $"{d.Added} added, {d.Removed} removed, {d.Moved} moved, {d.Rotated} rotated, {d.Rescaled} rescaled, {d.Unchanged} unchanged");
        foreach (var c in d.Changes)
        {
            var sev = c.Kind is Kind.Added or Kind.Removed ? IssueSeverity.Warning : IssueSeverity.Info;
            r.Add(sev, c.Kind.ToString(), $"'{c.Template}' {c.Detail}", c.Position, c.NewId);
        }
        return r;
    }

    private static float Dist(Vec3 a, Vec3 b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
