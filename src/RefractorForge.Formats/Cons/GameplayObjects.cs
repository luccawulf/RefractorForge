using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RefractorForge.Formats.Geometry;

namespace RefractorForge.Formats.Con;

/// <summary>A capture flag: world position, capture <see cref="Radius"/> (metres) and the gameplay fields from
/// its template (team, area value, conversion time, the in-game control-point name). <see cref="SpawnGroupId"/>
/// owns the soldier spawn points whose template <c>setGroup</c> matches; <see cref="ObjectSpawnerId"/> owns the
/// vehicle spawners whose instance <c>setOSId</c> matches. The optional fields carry defaults so existing
/// shorter construction keeps working.</summary>
public readonly record struct ControlPointDef(string Name, Vec3 Position, float Radius, int SpawnGroupId,
    int Team = 0, int AreaValue = 0, int ConversionTime = 40, string ControlPointName = "", int ObjectSpawnerId = 0,
    string PoleGeometry = "flagbase_m1", string FlagGeometry1 = "flagge_m1", string FlagGeometry2 = "flaguk_m1", float FlagHeight = 8.2f,
    // BF1942 control-point template fields (Battlecraft "Edit Control Point" dialog). ConversionTime above stays as the
    // BFV single conversion time; BF1942 uses the separate get/lose pair below.
    int TimeToGetControl = 40, int TimeToLoseControl = 40, int DisableIfEnemyInside = 0, int DisableWhenLosing = 0,
    int LoseControlWhenEnemyClose = 1, int LoseControlWhenNotClose = 0, int UnableToChangeTeam = 0, int OnlyTakableByTeam = 0,
    int HasCollisionPhysics = 1);

/// <summary>A vehicle spawner: where it sits, which vehicle template it spawns, its OS id (<c>setOSId</c>) and the
/// <c>setTeam</c> the spawner belongs to — all preserved for round-tripping. <see cref="Vehicle"/> is the display
/// fallback (team-2 preferred); <see cref="Vehicle1"/>/<see cref="Vehicle2"/> are the per-team templates from
/// <c>setObjectTemplate 1/2</c> so the editor can show each spawner's team-appropriate vehicle.</summary>
public readonly record struct VehicleSpawnDef(string Name, Vec3 Position, Vec3 Rotation, string Vehicle, int OsId,
    string Vehicle1 = "", string Vehicle2 = "", int Team = 0);

/// <summary>A soldier (infantry) spawn point. <see cref="Group"/> is its template <c>setGroup</c> id, which ties it to
/// the control point whose <c>spawnGroupId</c> matches; <see cref="SpawnId"/> is <c>setSpawnId</c> and
/// <see cref="SpawnAsParaTrooper"/> is the (optional) paratrooper flag — both Battlecraft "Edit Soldier Spawn" fields.</summary>
public readonly record struct SoldierSpawnDef(string Name, Vec3 Position, Vec3 Rotation, int Group = 0,
    int SpawnId = 0, int SpawnAsParaTrooper = 0);

/// <summary>
/// The gameplay layer of a Battlefield Vietnam level — control points, vehicle spawners and soldier
/// spawns — which live in the <c>Conquest/</c> .con files alongside (not inside) StaticObjects.con.
/// Parsing is pure and engine-agnostic so it is unit-tested headlessly and reused by the viewer.
/// </summary>
public sealed record GameplayObjects(
    IReadOnlyList<ControlPointDef> ControlPoints,
    IReadOnlyList<VehicleSpawnDef> VehicleSpawns,
    IReadOnlyList<SoldierSpawnDef> SoldierSpawns)
{
    public static GameplayObjects Empty { get; } = new(new List<ControlPointDef>(), new List<VehicleSpawnDef>(), new List<SoldierSpawnDef>());

    public int Count => ControlPoints.Count + VehicleSpawns.Count + SoldierSpawns.Count;

    /// <summary>One placed object parsed from a *.con: its create-name and the properties on it.</summary>
    private sealed class Block
    {
        public string Name = "";
        public Vec3 Position;
        public Vec3 Rotation;
        public int OsId = 1;
        public int Team = 0;
    }

    /// <summary>Walk Object.create blocks, honouring rem line-comments and beginrem/endrem block-comments.</summary>
    private static List<Block> ParseObjectBlocks(IEnumerable<string> lines)
    {
        var blocks = new List<Block>();
        Block? cur = null;
        int remDepth = 0;
        foreach (var raw in lines)
        {
            var line = raw.Replace("\r", "").Trim();
            if (line.Length == 0) continue;
            var low = line.ToLowerInvariant();

            if (low == "beginrem") { remDepth++; continue; }
            if (low == "endrem") { if (remDepth > 0) remDepth--; continue; }
            if (remDepth > 0) continue;
            if (low.StartsWith("rem")) continue;

            var sp = line.Split(new[] { ' ', '\t' }, 2, System.StringSplitOptions.RemoveEmptyEntries);
            if (sp.Length < 2) continue;
            var key = sp[0].ToLowerInvariant();
            var val = sp[1].Trim();

            switch (key)
            {
                case "object.create":
                    cur = new Block { Name = val.Split(new[] { ' ', '\t' })[0] };
                    blocks.Add(cur);
                    break;
                case "object.absoluteposition":
                case "object.position":
                    if (cur is not null && TryVec(val, out var p)) cur.Position = p;
                    break;
                case "object.rotation":
                    if (cur is not null && TryVec(val, out var r)) cur.Rotation = r;
                    break;
                case "object.setosid":
                    if (cur is not null && int.TryParse(val.Split(new[] { ' ', '\t' })[0], out var os)) cur.OsId = os;
                    break;
                case "object.setteam":
                    if (cur is not null && int.TryParse(val.Split(new[] { ' ', '\t' })[0], out var tm)) cur.Team = tm;
                    break;
            }
        }
        return blocks;
    }

    /// <summary>Parse "x/y/z" (Refractor) — also tolerates space- or comma-separated triples.</summary>
    private static bool TryVec(string s, out Vec3 v)
    {
        v = Vec3.Zero;
        var parts = s.Split(new[] { '/', ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return false;
        if (float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
        { v = new Vec3(x, y, z); return true; }
        return false;
    }

    /// <summary>All editable fields of one ControlPoint template (the Battlecraft "Edit Control Point" dialog).</summary>
    private sealed class CpTemplate
    {
        public float Radius; public int Sg, Team, Area, Conv = 40, Osid; public string CpName = "";
        public string Pole = "", Flag1 = "", Flag2 = ""; public float FlagY;
        public int TimeGet = 40, TimeLose = 40, DisEnemy, DisLosing, LoseEnemyClose = 1, LoseNotClose, UnableChange, OnlyTakable, HasCollision = 1;
    }

    /// <summary>From ControlPointTemplates.con: name -> the template's editable fields (radius, team, the BF1942
    /// capture/disable timings, geometry + per-team flag cloth, etc.). spawnGroupId owns matching soldier spawn
    /// groups; objectSpawnerId owns matching vehicle spawner OSIds.</summary>
    private static Dictionary<string, CpTemplate> ParseControlPointTemplates(IEnumerable<string> lines)
    {
        var map = new Dictionary<string, CpTemplate>(System.StringComparer.OrdinalIgnoreCase);
        CpTemplate? cur = null; string? curName = null; int remDepth = 0;
        void Flush() { if (curName is not null && cur is not null) map[curName] = cur; }
        foreach (var raw in lines)
        {
            var line = raw.Replace("\r", "").Trim(); if (line.Length == 0) continue;
            var low = line.ToLowerInvariant();
            if (low == "beginrem") { remDepth++; continue; }
            if (low == "endrem") { if (remDepth > 0) remDepth--; continue; }
            if (remDepth > 0 || low.StartsWith("rem")) continue;
            var sp = line.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (sp.Length < 2) continue;
            var key = sp[0].ToLowerInvariant();
            if (key == "objecttemplate.create") { Flush(); curName = sp.Length >= 3 ? sp[2] : sp[1]; cur = new CpTemplate(); }
            else if (cur is null) continue;
            else if (key == "objecttemplate.radius" && float.TryParse(sp[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var rr)) cur.Radius = rr;
            else if (key == "objecttemplate.spawngroupid" && int.TryParse(sp[1], out var gg)) cur.Sg = gg;
            else if (key == "objecttemplate.objectspawnerid" && int.TryParse(sp[1], out var oo)) cur.Osid = oo;
            else if (key == "objecttemplate.team" && int.TryParse(sp[1], out var tt)) cur.Team = tt;
            else if (key == "objecttemplate.areavalue" && int.TryParse(sp[1], out var av)) cur.Area = av;
            else if (key == "objecttemplate.conversiontime" && int.TryParse(sp[1], out var ct)) cur.Conv = ct;
            else if (key == "objecttemplate.timetogetcontrol" && int.TryParse(sp[1], out var tg)) cur.TimeGet = tg;
            else if (key == "objecttemplate.timetolosecontrol" && int.TryParse(sp[1], out var tlc)) cur.TimeLose = tlc;
            else if (key == "objecttemplate.disableifenemyinsideradius" && int.TryParse(sp[1], out var di)) cur.DisEnemy = di;
            else if (key == "objecttemplate.disablewhenlosingcontrol" && int.TryParse(sp[1], out var dw)) cur.DisLosing = dw;
            else if (key == "objecttemplate.losecontrolwhenenemyclose" && int.TryParse(sp[1], out var le)) cur.LoseEnemyClose = le;
            else if (key == "objecttemplate.losecontrolwhennotclose" && int.TryParse(sp[1], out var ln)) cur.LoseNotClose = ln;
            else if (key == "objecttemplate.unabletochangeteam" && int.TryParse(sp[1], out var uc)) cur.UnableChange = uc;
            else if (key == "objecttemplate.onlytakablebyteam" && int.TryParse(sp[1], out var ob)) cur.OnlyTakable = ob;
            else if (key == "objecttemplate.hascollisionphysics" && int.TryParse(sp[1], out var hc)) cur.HasCollision = hc;
            else if (key == "objecttemplate.controlpointname") cur.CpName = sp[1];
            // Geometry for the 3D flag pole + per-team flag cloth: geometry = pole+base, setTeamGeometry <team> = flag
            // cloth, setPosition Y = flag mount height up the pole.
            else if (key == "objecttemplate.geometry" && cur.Pole.Length == 0) cur.Pole = sp[1];
            else if (key == "objecttemplate.setteamgeometry" && sp.Length >= 3)
            {
                if (sp[1] == "1") cur.Flag1 = sp[2];
                else if (sp[1] == "2") cur.Flag2 = sp[2];
            }
            else if (key == "objecttemplate.setposition")
            {
                var pv = sp[1].Split('/');
                if (pv.Length >= 2 && float.TryParse(pv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var py) && py > 0) cur.FlagY = py;
            }
        }
        Flush();
        return map;
    }

    /// <summary>From SoldierSpawnTemplates.con: spawnPointName -> (spawn group id <c>setGroup</c>, <c>setSpawnId</c>,
    /// the optional <c>spawnAsParaTrooper</c> flag). The group ties each soldier spawn to the control point whose
    /// <c>spawnGroupId</c> matches; spawnId + paratrooper are surfaced in the Battlecraft "Edit Soldier Spawn" dialog.</summary>
    private static Dictionary<string, (int group, int spawnId, int para)> ParseSpawnPointInfo(IEnumerable<string> lines)
    {
        var map = new Dictionary<string, (int, int, int)>(System.StringComparer.OrdinalIgnoreCase);
        string? cur = null; int g = 0, sid = 0, para = 0; int remDepth = 0;
        void Flush() { if (cur is not null) map[cur] = (g, sid, para); }
        foreach (var raw in lines)
        {
            var line = raw.Replace("\r", "").Trim(); if (line.Length == 0) continue;
            var low = line.ToLowerInvariant();
            if (low == "beginrem") { remDepth++; continue; }
            if (low == "endrem") { if (remDepth > 0) remDepth--; continue; }
            if (remDepth > 0 || low.StartsWith("rem")) continue;
            var sp = line.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (sp.Length < 2) continue;
            var key = sp[0].ToLowerInvariant();
            if (key == "objecttemplate.create") { Flush(); cur = sp.Length >= 3 ? sp[2] : sp[1]; g = 0; sid = 0; para = 0; }   // "create SpawnPoint <name>"
            else if (cur is null) continue;
            else if (key == "objecttemplate.setgroup" && int.TryParse(sp[1], out var gg)) g = gg;
            else if (key == "objecttemplate.setspawnid" && int.TryParse(sp[1], out var ss)) sid = ss;
            else if (key == "objecttemplate.spawnasparatrooper" && int.TryParse(sp[1], out var pp)) para = pp;
        }
        Flush();
        return map;
    }

    /// <summary>From ObjectSpawnTemplates.con: spawnerName -> (team-1 vehicle, team-2 vehicle) from setObjectTemplate 1/2.</summary>
    private static Dictionary<string, (string t1, string t2)> ParseSpawnerVehicles(IEnumerable<string> lines)
    {
        var team1 = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        var team2 = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        string? cur = null; int remDepth = 0;
        foreach (var raw in lines)
        {
            var line = raw.Replace("\r", "").Trim(); if (line.Length == 0) continue;
            var low = line.ToLowerInvariant();
            if (low == "beginrem") { remDepth++; continue; }
            if (low == "endrem") { if (remDepth > 0) remDepth--; continue; }
            if (remDepth > 0 || low.StartsWith("rem")) continue;
            var sp = line.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (sp.Length < 2) continue;
            var key = sp[0].ToLowerInvariant();
            if (key == "objecttemplate.create") cur = sp.Length >= 3 ? sp[2] : sp[1];   // "create ObjectSpawner <name>"
            else if (key == "objecttemplate.setobjecttemplate" && cur is not null && sp.Length >= 3)
            {
                if (sp[1] == "1") team1[cur] = sp[2];
                else if (sp[1] == "2") team2[cur] = sp[2];
            }
        }
        var result = new Dictionary<string, (string, string)>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var k in team1.Keys.Concat(team2.Keys).Distinct(System.StringComparer.OrdinalIgnoreCase))
            result[k] = (team1.TryGetValue(k, out var v1) ? v1 : "", team2.TryGetValue(k, out var v2) ? v2 : "");
        return result;
    }

    public static IReadOnlyList<ControlPointDef> ParseControlPoints(IEnumerable<string> pointLines, IEnumerable<string> templateLines)
    {
        var tmpl = ParseControlPointTemplates(templateLines);
        var list = new List<ControlPointDef>();
        foreach (var b in ParseObjectBlocks(pointLines))
        {
            if (!tmpl.TryGetValue(b.Name, out var t) || t is null)
            { list.Add(new ControlPointDef(b.Name, b.Position, 20f, 0, ControlPointName: b.Name)); continue; }
            list.Add(new ControlPointDef(b.Name, b.Position, t.Radius > 0 ? t.Radius : 20f, t.Sg,
                                         t.Team, t.Area, t.Conv, string.IsNullOrEmpty(t.CpName) ? b.Name : t.CpName, t.Osid,
                                         string.IsNullOrEmpty(t.Pole) ? "flagbase_m1" : t.Pole,
                                         string.IsNullOrEmpty(t.Flag1) ? "flagge_m1" : t.Flag1,
                                         string.IsNullOrEmpty(t.Flag2) ? "flaguk_m1" : t.Flag2,
                                         t.FlagY > 0 ? t.FlagY : 8.2f,
                                         t.TimeGet, t.TimeLose, t.DisEnemy, t.DisLosing, t.LoseEnemyClose, t.LoseNotClose, t.UnableChange, t.OnlyTakable, t.HasCollision));
        }
        return list;
    }

    public static IReadOnlyList<VehicleSpawnDef> ParseVehicleSpawns(IEnumerable<string> spawnLines, IEnumerable<string> templateLines)
    {
        var veh = ParseSpawnerVehicles(templateLines);
        var list = new List<VehicleSpawnDef>();
        foreach (var b in ParseObjectBlocks(spawnLines))
        {
            veh.TryGetValue(b.Name, out var vt);   // (t1, t2); default ("",  "") when the spawner has no template file entry
            string t1 = vt.t1 ?? "", t2 = vt.t2 ?? "";
            string disp = t2.Length > 0 ? t2 : (t1.Length > 0 ? t1 : b.Name);   // display fallback: team-2 preferred (unchanged behaviour)
            list.Add(new VehicleSpawnDef(b.Name, b.Position, b.Rotation, disp, b.OsId, t1, t2, b.Team));
        }
        return list;
    }

    public static IReadOnlyList<SoldierSpawnDef> ParseSoldierSpawns(IEnumerable<string> spawnLines, IEnumerable<string>? templateLines = null)
    {
        var info = templateLines is null ? new Dictionary<string, (int, int, int)>() : ParseSpawnPointInfo(templateLines);
        return ParseObjectBlocks(spawnLines)
            .Select(b => { info.TryGetValue(b.Name, out var t); return new SoldierSpawnDef(b.Name, b.Position, b.Rotation, t.Item1, t.Item2, t.Item3); }).ToList();
    }

    /// <summary>Assemble from already-read line sets (null = that file absent).</summary>
    public static GameplayObjects Parse(
        IEnumerable<string>? controlPoints, IEnumerable<string>? controlPointTemplates,
        IEnumerable<string>? objectSpawns, IEnumerable<string>? objectSpawnTemplates,
        IEnumerable<string>? soldierSpawns, IEnumerable<string>? soldierSpawnTemplates = null)
    {
        var cps = controlPoints is null ? new List<ControlPointDef>() : (List<ControlPointDef>)ParseControlPoints(controlPoints, controlPointTemplates ?? Enumerable.Empty<string>());
        var veh = objectSpawns is null ? new List<VehicleSpawnDef>() : (List<VehicleSpawnDef>)ParseVehicleSpawns(objectSpawns, objectSpawnTemplates ?? Enumerable.Empty<string>());
        var sol = soldierSpawns is null ? new List<SoldierSpawnDef>() : (List<SoldierSpawnDef>)ParseSoldierSpawns(soldierSpawns, soldierSpawnTemplates);
        return new GameplayObjects(cps, veh, sol);
    }

    /// <summary>Index of the control point that owns a spawn, or -1 if there are none. Vehicle spawners match by
    /// <c>objectSpawnerId == OSId</c>; soldier spawn points by <c>spawnGroupId == group</c>. Anything unmatched
    /// (id 0, editor-created, or an id no flag claims) falls back to the nearest control point on the ground
    /// plane, so the link is always meaningful.</summary>
    public static int OwningControlPointIndex(IReadOnlyList<ControlPointDef> cps, Vec3 spawnPos, int matchId, bool byObjectSpawner)
    {
        if (cps.Count == 0) return -1;
        if (matchId != 0)
            for (int i = 0; i < cps.Count; i++)
                if ((byObjectSpawner ? cps[i].ObjectSpawnerId : cps[i].SpawnGroupId) == matchId) return i;
        int best = 0; float bestD = float.MaxValue;
        for (int i = 0; i < cps.Count; i++)
        {
            float dx = cps[i].Position.X - spawnPos.X, dz = cps[i].Position.Z - spawnPos.Z, d = dx * dx + dz * dz;
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    /// <summary>Load the Conquest gameplay files from an extracted level folder (empty layers if absent).</summary>
    public static GameplayObjects LoadFolder(string levelDir)
    {
        // Prefer the multiplayer Conquest folder; fall back to the level root.
        string dir = System.IO.Directory.Exists(System.IO.Path.Combine(levelDir, "Conquest"))
            ? System.IO.Path.Combine(levelDir, "Conquest") : levelDir;
        IEnumerable<string>? Read(string name)
        {
            var p = System.IO.Path.Combine(dir, name);
            return System.IO.File.Exists(p) ? System.IO.File.ReadAllLines(p) : null;
        }
        return Parse(Read("ControlPoints.con"), Read("ControlPointTemplates.con"),
                     Read("ObjectSpawns.con"), Read("ObjectSpawnTemplates.con"),
                     Read("SoldierSpawns.con"), Read("SoldierSpawnTemplates.con"));
    }
}
