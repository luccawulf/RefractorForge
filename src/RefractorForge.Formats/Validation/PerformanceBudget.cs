using RefractorForge.Formats.Con;
using RefractorForge.Formats.Geometry;

namespace RefractorForge.Formats.Validation;

/// <summary>
/// Where the map will chug.
///
/// Refractor draws what the view distance admits, so what matters is not the level's total but its DENSITY:
/// the same ten thousand triangles are fine spread over a valley and a slideshow stacked in one courtyard. The
/// map is cut into cells and each cell is scored on what stands in it, so the report can point at the corner
/// of the map that needs thinning rather than at "the map".
/// </summary>
public static class PerformanceBudget
{
    /// <summary>What the renderer knows about a template: triangles in its LOD 0, and unique texture bytes.</summary>
    public readonly record struct TemplateCost(int Triangles, long TextureBytes, int Parts);

    public sealed class Options
    {
        public float CellMeters { get; init; } = 256f;
        /// <summary>Triangles in one cell that earns a warning / an error. Refractor-era numbers: a Battlefield
        /// 1942 city block runs 40-80k triangles on screen; past ~150k in one cell the frame rate is gone.</summary>
        public int WarnTriangles { get; init; } = 120_000;
        public int ErrorTriangles { get; init; } = 250_000;
        public int WarnObjects { get; init; } = 400;
        public long WarnTextureBytes { get; init; } = 96L * 1024 * 1024;
    }

    public sealed record CellStat(int Cx, int Cz, int Objects, long Triangles, long TextureBytes, int UniqueTemplates);

    public static (LevelReport Report, List<CellStat> Cells) Run(
        StaticObjectsFile objects, float worldSize, Func<string, TemplateCost?> cost, Options? opt = null)
    {
        opt ??= new Options();
        var r = new LevelReport("Performance budget");
        int n = Math.Max(1, (int)MathF.Ceiling(worldSize / opt.CellMeters));

        var objCount = new int[n * n];
        var tris = new long[n * n];
        var texSeen = new HashSet<string>[n * n];
        var texBytes = new long[n * n];
        var tmplSeen = new HashSet<string>[n * n];
        var costCache = new Dictionary<string, TemplateCost?>(StringComparer.OrdinalIgnoreCase);

        long totalTris = 0;
        var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var o in objects.Objects)
        {
            int cx = Math.Clamp((int)(o.Position.X / opt.CellMeters), 0, n - 1);
            int cz = Math.Clamp((int)(o.Position.Z / opt.CellMeters), 0, n - 1);
            int i = cz * n + cx;
            objCount[i]++;
            if (!costCache.TryGetValue(o.Template, out var c)) { c = cost(o.Template); costCache[o.Template] = c; }
            if (c is null) { unknown.Add(o.Template); continue; }
            tris[i] += c.Value.Triangles;
            totalTris += c.Value.Triangles;
            (tmplSeen[i] ??= new(StringComparer.OrdinalIgnoreCase)).Add(o.Template);
            // Texture memory is per unique template per cell: the same house twice loads its textures once.
            if ((texSeen[i] ??= new(StringComparer.OrdinalIgnoreCase)).Add(o.Template))
                texBytes[i] += c.Value.TextureBytes;
        }

        var cells = new List<CellStat>();
        for (int cz = 0; cz < n; cz++)
            for (int cx = 0; cx < n; cx++)
            {
                int i = cz * n + cx;
                if (objCount[i] == 0) continue;
                cells.Add(new CellStat(cx, cz, objCount[i], tris[i], texBytes[i], tmplSeen[i]?.Count ?? 0));
            }

        foreach (var c in cells.OrderByDescending(c => c.Triangles))
        {
            var centre = new Vec3((c.Cx + 0.5f) * opt.CellMeters, 0f, (c.Cz + 0.5f) * opt.CellMeters);
            string where = $"cell ({c.Cx},{c.Cz}) around ({centre.X:0}, {centre.Z:0})";
            if (c.Triangles >= opt.ErrorTriangles)
                r.Add(IssueSeverity.Error, "Triangles", $"{c.Triangles:N0} triangles in {where} - this will not hold a frame rate", centre);
            else if (c.Triangles >= opt.WarnTriangles)
                r.Add(IssueSeverity.Warning, "Triangles", $"{c.Triangles:N0} triangles in {where}", centre);
            if (c.Objects >= opt.WarnObjects)
                r.Add(IssueSeverity.Warning, "Objects", $"{c.Objects:N0} objects in {where} - each is a draw call", centre);
            if (c.TextureBytes >= opt.WarnTextureBytes)
                r.Add(IssueSeverity.Warning, "Textures", $"{c.TextureBytes / (1024.0 * 1024):0} MB of texture in {where}", centre);
        }

        r.Add(IssueSeverity.Info, "Total",
            $"{objects.Objects.Count:N0} objects, {totalTris:N0} triangles across {cells.Count} occupied cell(s) of {opt.CellMeters:0} m");
        if (unknown.Count > 0)
            r.Add(IssueSeverity.Info, "Unknown", $"{unknown.Count} template(s) could not be costed (no mesh loaded): " +
                string.Join(", ", unknown.Take(6)) + (unknown.Count > 6 ? ", ..." : ""));

        return (r, cells);
    }
}
