using RefractorForge.Formats.Con;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;

namespace RefractorForge.Formats.Validation;

/// <summary>
/// The things the game will not tell you about until someone is standing on them.
///
/// Every check here is for a mistake the engine accepts silently: a crate floating a metre in the air, a spawn
/// under the ground, a control point no one can spawn at, a vehicle spawner that spawns nothing because it has
/// no team. None of them crash the map. They just ship. This is the counterpart, for level content, of the
/// archive validation that already runs after every save.
///
/// Anything that needs the renderer's knowledge - template bounds, whether a template exists - comes in as a
/// delegate, so the checks themselves stay headless and testable.
/// </summary>
public static class LevelValidator
{
    public sealed class Inputs
    {
        public StaticObjectsFile? Objects { get; init; }
        public EditableGameplay? Gameplay { get; init; }
        public Heightmap? Heightmap { get; init; }
        public TerrainConfig? Config { get; init; }
        public CombatArea? CombatArea { get; init; }

        /// <summary>Local-space bounds of a template, or null if unknown. Used for float/bury checks.</summary>
        public Func<string, (Vec3 Min, Vec3 Max)?>? Bounds { get; init; }
        /// <summary>Whether a template can be resolved at all (mesh library or game objects).</summary>
        public Func<string, bool>? TemplateExists { get; init; }

        /// <summary>How far above the ground an object's bottom may sit before it counts as floating.</summary>
        public float FloatTolerance { get; init; } = 0.75f;
        /// <summary>How far below the ground an object's bottom may sink before it counts as buried.</summary>
        public float BuryTolerance { get; init; } = 2.5f;
    }

    public static LevelReport Run(Inputs inp)
    {
        var r = new LevelReport("Map check");
        if (inp.Objects is not null) CheckObjects(inp, r);
        if (inp.Gameplay is not null) CheckGameplay(inp, r);
        return r;
    }

    private static float? GroundAt(Inputs inp, float wx, float wz)
    {
        if (inp.Heightmap is null || inp.Config is null) return null;
        var hm = inp.Heightmap; var cfg = inp.Config;
        float sp = cfg.HorizontalSpacing <= 0 ? 1f : cfg.HorizontalSpacing;
        int x = Math.Clamp((int)MathF.Round(wx / sp), 0, hm.Width - 1);
        int y = Math.Clamp((int)MathF.Round(wz / sp), 0, hm.Height - 1);
        return cfg.HeightToMeters(hm[x, y]);
    }

    private static void CheckObjects(Inputs inp, LevelReport r)
    {
        var objs = inp.Objects!.Objects;

        // Duplicate ids. Ids are what selection, locking and collaboration key on, so two objects sharing one
        // means edits land on the wrong thing with no error.
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in objs)
        {
            if (seen.TryGetValue(o.Id, out var n)) seen[o.Id] = n + 1; else seen[o.Id] = 1;
        }
        foreach (var kv in seen.Where(kv => kv.Value > 1))
            r.Add(IssueSeverity.Error, "Duplicate id", $"{kv.Value} objects share id '{kv.Key}'", objectId: kv.Key);

        // Exact duplicates: same template at the same place. Two identical objects on top of each other draw
        // twice, z-fight, and were almost always a double-paste.
        var placed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in objs)
        {
            string key = $"{o.Template}|{o.Position.X:0.##}|{o.Position.Y:0.##}|{o.Position.Z:0.##}";
            if (placed.TryGetValue(key, out var firstId))
                r.Add(IssueSeverity.Warning, "Duplicate object",
                    $"'{o.Template}' placed twice at the same spot", o.Position, o.Id);
            else placed[key] = o.Id;
        }

        foreach (var o in objs)
        {
            if (inp.TemplateExists is not null && !inp.TemplateExists(o.Template))
            {
                r.Add(IssueSeverity.Error, "Missing template",
                    $"'{o.Template}' is not in any loaded archive - it will not appear in game", o.Position, o.Id);
                continue;
            }

            if (inp.CombatArea is { } ca && !ca.Contains(o.Position.X, o.Position.Z))
                r.Add(IssueSeverity.Info, "Outside combat area",
                    $"'{o.Template}' is {ca.DistanceOutside(o.Position.X, o.Position.Z):0} m outside the combat area",
                    o.Position, o.Id);

            // Floating / buried. Uses the template's bottom, not its origin: a lamp post's origin is at its base
            // but a bridge span's is in its middle, and only the bottom says whether it touches anything.
            var g = GroundAt(inp, o.Position.X, o.Position.Z);
            if (g is null) continue;
            float bottom = o.Position.Y;
            if (inp.Bounds?.Invoke(o.Template) is { } b)
                bottom = o.Position.Y + b.Min.Y * (o.Scale ?? 1f);

            float gap = bottom - g.Value;
            if (gap > inp.FloatTolerance)
                r.Add(IssueSeverity.Warning, "Floating",
                    $"'{o.Template}' hangs {gap:0.0} m above the ground", o.Position, o.Id);
            else if (gap < -inp.BuryTolerance)
                r.Add(IssueSeverity.Warning, "Buried",
                    $"'{o.Template}' sits {-gap:0.0} m below the ground", o.Position, o.Id);
        }
    }

    private static void CheckGameplay(Inputs inp, LevelReport r)
    {
        var gp = inp.Gameplay!;
        var ca = inp.CombatArea;

        // Control points: each needs somewhere to spawn, or it is a flag nobody can hold.
        var groupsWithSpawns = new HashSet<int>(gp.SoldierSpawns.Select(s => s.Group));
        var cpGroups = new HashSet<int>();
        foreach (var cp in gp.ControlPoints)
        {
            cpGroups.Add(cp.SpawnGroupId);
            if (cp.SpawnGroupId != 0 && !groupsWithSpawns.Contains(cp.SpawnGroupId))
                r.Add(IssueSeverity.Error, "Control point",
                    $"'{cp.Name}' uses spawn group {cp.SpawnGroupId} but no soldier spawn belongs to it", cp.Position);
            if (cp.Radius <= 0f)
                r.Add(IssueSeverity.Error, "Control point", $"'{cp.Name}' has no capture radius", cp.Position);
            if (ca is { } a && !a.Contains(cp.Position.X, cp.Position.Z))
                r.Add(IssueSeverity.Error, "Control point",
                    $"'{cp.Name}' is outside the combat area - players are killed for going near it", cp.Position);
            CheckAboveGround(inp, r, "Control point", cp.Name, cp.Position);
        }

        // Soldier spawns that belong to no flag never activate.
        foreach (var s in gp.SoldierSpawns)
        {
            if (s.Group != 0 && !cpGroups.Contains(s.Group))
                r.Add(IssueSeverity.Warning, "Soldier spawn",
                    $"'{s.Name}' is in spawn group {s.Group}, which no control point uses", s.Position);
            if (ca is { } a && !a.Contains(s.Position.X, s.Position.Z))
                r.Add(IssueSeverity.Error, "Soldier spawn",
                    $"'{s.Name}' is outside the combat area - spawning there is a death", s.Position);
            CheckAboveGround(inp, r, "Soldier spawn", s.Name, s.Position);
        }

        // Vehicle spawners: nothing to spawn, or nobody to spawn it for.
        foreach (var v in gp.VehicleSpawns)
        {
            bool hasAny = !string.IsNullOrWhiteSpace(v.Vehicle) || !string.IsNullOrWhiteSpace(v.Vehicle1) || !string.IsNullOrWhiteSpace(v.Vehicle2);
            if (!hasAny)
                r.Add(IssueSeverity.Error, "Vehicle spawner", $"'{v.Name}' has no vehicle template at all", v.Position);
            else if (v.Team == 0 && string.IsNullOrWhiteSpace(v.Vehicle1) && string.IsNullOrWhiteSpace(v.Vehicle2))
                r.Add(IssueSeverity.Warning, "Vehicle spawner",
                    $"'{v.Name}' has no team and no per-team vehicle - it depends on the nearest flag", v.Position);
            if (ca is { } a && !a.Contains(v.Position.X, v.Position.Z))
                r.Add(IssueSeverity.Error, "Vehicle spawner",
                    $"'{v.Name}' is outside the combat area", v.Position);
            CheckAboveGround(inp, r, "Vehicle spawner", v.Name, v.Position);
        }

        if (gp.ControlPoints.Count == 0)
            r.Add(IssueSeverity.Error, "Gameplay", "The level has no control points");
        if (gp.SoldierSpawns.Count == 0)
            r.Add(IssueSeverity.Error, "Gameplay", "The level has no soldier spawns");

        // Both teams need somewhere to start. Team 0 flags are neutral; a team with no flag of its own starts
        // with nothing and the round is over before it begins.
        foreach (int team in new[] { 1, 2 })
            if (gp.ControlPoints.Count > 0 && !gp.ControlPoints.Any(c => c.Team == team))
                r.Add(IssueSeverity.Warning, "Gameplay", $"Team {team} owns no control point at the start");
    }

    private static void CheckAboveGround(Inputs inp, LevelReport r, string cat, string name, Vec3 pos)
    {
        var g = GroundAt(inp, pos.X, pos.Z);
        if (g is null) return;
        float gap = pos.Y - g.Value;
        if (gap < -1.0f)
            r.Add(IssueSeverity.Error, cat, $"'{name}' is {-gap:0.0} m under the ground", pos);
        else if (gap > 15f)
            r.Add(IssueSeverity.Warning, cat, $"'{name}' is {gap:0.0} m up in the air", pos);
    }
}
