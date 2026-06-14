using System.Globalization;
using System.Text;

namespace RefractorForge.Formats.Con;

/// <summary>
/// Generates a minimal but genuinely playable Conquest layer for a brand-new map: three control points
/// (US base / NVA base / neutral centre), a ring of soldier spawn points per base wired to its spawn group,
/// the spawn-manager + game-type files, and the Init.con block (capture flags, kits, team skins, pre-spawn
/// cameras) that lets players actually spawn. Every file mirrors retail Operation_Irving so it loads in both
/// this editor and the game. Positions are derived from the world size and sampled terrain height.
/// </summary>
public static class NewMapGameplay
{
    /// <summary>One generated base: a control point + its spawn group. Fx/Fz are fractions of the world size.</summary>
    public readonly record struct BaseDef(string Key, int Team, float Fx, float Fz, int Group, int ConversionTime, string Geometry);

    /// <summary>The default three-flag Conquest layout: US base (team 2), NVA base (team 1), neutral centre.</summary>
    public static IReadOnlyList<BaseDef> DefaultBases() => new[]
    {
        new BaseDef("us_base",  2, 0.25f, 0.25f, 1, 40, "USflagbase_m1"),
        new BaseDef("nva_base", 1, 0.75f, 0.75f, 2, 40, "NVAflagbase_m1"),
        new BaseDef("center",   0, 0.50f, 0.50f, 3, 15, "NVAflagbase_m1"),
    };

    private static string F(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);
    private static string Pos(float x, float y, float z) => $"{F(x)}/{F(y)}/{F(z)}";
    // A clean identifier for controlPointName (no spaces/punctuation).
    private static string Ident(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s) if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
        return sb.Length == 0 ? "map" : sb.ToString();
    }

    // The four spawn-point offsets (metres) ringing each base.
    private static readonly (float dx, float dz)[] Ring = { (6, 0), (-6, 0), (0, 6), (0, -6) };

    /// <summary>
    /// Build every <c>Conquest/*</c> file (+ <c>Conquest.con</c> and <c>GameTypes/Conquest.con</c>) for a map
    /// of the given world size. <paramref name="heightAt"/> returns the spawn-safe Y (metres) at a world XZ.
    /// Returns folder-relative path → file text (CRLF). The caller writes them under the level dir.
    /// </summary>
    public static Dictionary<string, string> BuildFiles(string mapName, int worldSize, Func<float, float, float> heightAt,
                                                        IReadOnlyList<BaseDef> bases)
    {
        string id = Ident(mapName);
        var cps = new StringBuilder();
        var cpTemplates = new StringBuilder();
        var spawns = new StringBuilder();
        var spawnTemplates = new StringBuilder();
        var manager = new StringBuilder();

        cpTemplates.AppendLine("NetworkableInfo.createNewInfo ControlPointInfo");
        cpTemplates.AppendLine("NetworkableInfo.setPredictionMode PMNone");
        cpTemplates.AppendLine("NetworkableInfo.setBasePriority c_NIGhostAlways");
        cpTemplates.AppendLine();

        for (int bi = 0; bi < bases.Count; bi++)
        {
            var b = bases[bi];
            float wx = b.Fx * worldSize, wz = b.Fz * worldSize, wy = heightAt(wx, wz);

            // Control-point instance + template.
            cps.AppendLine($"Object.create {b.Key}");
            cps.AppendLine($"Object.absolutePosition {Pos(wx, wy, wz)}");
            cps.AppendLine();

            cpTemplates.AppendLine($"ObjectTemplate.create ControlPoint {b.Key}");
            cpTemplates.AppendLine("ObjectTemplate.networkableInfo ControlPointInfo");
            cpTemplates.AppendLine($"ObjectTemplate.controlPointName {id}_base{bi + 1}");
            cpTemplates.AppendLine("ObjectTemplate.radius 30");
            cpTemplates.AppendLine($"ObjectTemplate.team {b.Team}");
            cpTemplates.AppendLine($"ObjectTemplate.spawnGroupId {b.Group}");
            cpTemplates.AppendLine($"ObjectTemplate.objectSpawnerId {b.Group}");
            cpTemplates.AppendLine("ObjectTemplate.areaValue 25");
            cpTemplates.AppendLine($"ObjectTemplate.conversionTime {b.ConversionTime}");
            cpTemplates.AppendLine($"ObjectTemplate.geometry {b.Geometry}");
            cpTemplates.AppendLine("ObjectTemplate.hasCollisionPhysics 1");
            cpTemplates.AppendLine("ObjectTemplate.addTemplate AnimatedFlag");
            cpTemplates.AppendLine("ObjectTemplate.setPosition 0/8.2/0");
            cpTemplates.AppendLine("ObjectTemplate.setTeamGeometry 0 o_Neutralflag_m1");
            cpTemplates.AppendLine("ObjectTemplate.setTeamGeometry 1 o_VCflag_m1");
            cpTemplates.AppendLine("ObjectTemplate.setTeamGeometry 2 o_USflag_m1");
            cpTemplates.AppendLine();

            // Four soldier spawn points ringing the base, wired to this base's spawn group.
            for (int si = 0; si < Ring.Length; si++)
            {
                string sp = $"{b.Key}_sp{si + 1}";
                float sx = wx + Ring[si].dx, sz = wz + Ring[si].dz, sy = heightAt(sx, sz);
                int spawnId = (bi + 1) * 10 + si + 1;

                spawns.AppendLine($"Object.create {sp}");
                spawns.AppendLine($"Object.absolutePosition {Pos(sx, sy, sz)}");
                spawns.AppendLine("Object.rotation 0/0/0");
                spawns.AppendLine();

                spawnTemplates.AppendLine($"ObjectTemplate.create SpawnPoint {sp}");
                spawnTemplates.AppendLine($"ObjectTemplate.setSpawnId {spawnId}");
                spawnTemplates.AppendLine($"ObjectTemplate.setGroup {b.Group}");
                spawnTemplates.AppendLine();
            }

            manager.AppendLine($"spawnPointManager.group {b.Group}");
            manager.AppendLine("spawnPointManager.groupTeam 0");
            manager.AppendLine("spawnPointManager.groupIcon test1.tga");
            manager.AppendLine();
        }

        var (objSpawnTemplates, objSpawns) = VehicleSpawns(worldSize, heightAt, bases);

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [@"Conquest\ControlPoints.con"] = cps.ToString() + "rem *** EndOfFile ***\r\n",
            [@"Conquest\ControlPointTemplates.con"] = cpTemplates.ToString(),
            [@"Conquest\SoldierSpawns.con"] = spawns.ToString() + "rem *** EndOfFile ***\r\n",
            [@"Conquest\SoldierSpawnTemplates.con"] = spawnTemplates.ToString() + "rem *** EndOfFile ***\r\n",
            [@"Conquest\spawnPointManagerSettings.con"] = manager.ToString() + "rem *** EndOfFile ***\r\n",
            [@"Conquest\ObjectSpawnTemplates.con"] = objSpawnTemplates,
            [@"Conquest\ObjectSpawns.con"] = objSpawns,
            [@"Conquest.con"] = GameTypeCon(),
            [@"GameTypes\Conquest.con"] = GameTypeCon(),
        };
        return files;
    }

    /// <summary>Default vehicle pool for a playable map: a tank + jeep at each home base (team-appropriate
    /// US/NVA variants) and a US attack huey at the US base. Spawner OSId == the base's objectSpawnerId, so
    /// the flag owns its vehicles. Template names are stock BFV (mirrors Operation_Irving's spawners).</summary>
    private static (string templates, string spawns) VehicleSpawns(int worldSize, Func<float, float, float> heightAt,
                                                                   IReadOnlyList<BaseDef> bases)
    {
        // Name, US (team 2) template, NVA (team 1) template (null => US-only), spawn delay, offset dx/dz, yaw, US-only.
        var pool = new (string Name, string Us, string? Nva, int Delay, float Dx, float Dz, float Yaw, bool UsOnly)[]
        {
            ("rf_TankSpawner", "Sheridan",   "t54",   30,  16f,  0f,  0f, false),
            ("rf_JeepSpawner", "Mutt",       "vespa", 20,  16f, 11f,  0f, false),
            ("rf_HeliSpawner", "uh1Assault", null,    40, -16f,  0f,180f, true),
        };

        var t = new StringBuilder();
        foreach (var v in pool)
        {
            t.AppendLine($"ObjectTemplate.create ObjectSpawner {v.Name}");
            t.AppendLine($"ObjectTemplate.setObjectTemplate 2 {v.Us}");
            if (v.Nva is not null) t.AppendLine($"ObjectTemplate.setObjectTemplate 1 {v.Nva}");
            t.AppendLine($"ObjectTemplate.SpawnDelay {v.Delay}");
            t.AppendLine("ObjectTemplate.SpawnDelayAtStart 0");
            t.AppendLine("ObjectTemplate.TimeToLive 120");
            t.AppendLine("ObjectTemplate.Distance 200");
            t.AppendLine();
        }

        var s = new StringBuilder();
        foreach (var b in bases)
        {
            if (b.Team == 0) continue;                    // only the two home bases get vehicles
            float bx = b.Fx * worldSize, bz = b.Fz * worldSize;
            s.AppendLine($"rem *** {b.Key} vehicles ***");
            foreach (var v in pool)
            {
                if (v.UsOnly && b.Team != 2) continue;    // US-only vehicles (the huey) skip the NVA base
                float vx = bx + v.Dx, vz = bz + v.Dz, vy = heightAt(vx, vz);
                s.AppendLine($"Object.create {v.Name}");
                s.AppendLine($"Object.absolutePosition {Pos(vx, vy, vz)}");
                s.AppendLine($"Object.rotation {F(v.Yaw)}/0/0");
                s.AppendLine($"Object.setOSId {b.Group}");
                s.AppendLine();
            }
        }
        return (t.ToString() + "rem *** EndOfFile ***\r\n", s.ToString() + "rem *** EndOfFile ***\r\n");
    }

    private static string GameTypeCon()
    {
        var sb = new StringBuilder();
        sb.AppendLine("if v_arg1 == host");
        sb.AppendLine("Game.setNumberOfTickets 2 150");
        sb.AppendLine("Game.setNumberOfTickets 1 150");
        sb.AppendLine("Game.setTicketLostPerMin 2 5");
        sb.AppendLine("Game.setTicketLostPerMin 1 5");
        sb.AppendLine("endIf");
        sb.AppendLine();
        sb.AppendLine("run Conquest/spawnPointManagerSettings");
        sb.AppendLine();
        sb.AppendLine("run Conquest/SoldierSpawnTemplates");
        sb.AppendLine("run Conquest/SoldierSpawns");
        sb.AppendLine();
        sb.AppendLine("run Conquest/ObjectSpawnTemplates");
        sb.AppendLine("run Conquest/ControlPointTemplates");
        sb.AppendLine();
        sb.AppendLine("if v_arg1 == host");
        sb.AppendLine("\trun Conquest/ObjectSpawns");
        sb.AppendLine("\trun Conquest/ControlPoints");
        sb.AppendLine("endIf");
        sb.AppendLine();
        sb.AppendLine("rem *** EndOfFile ***");
        return sb.ToString();
    }

    /// <summary>
    /// The Init.con gameplay block: capture-flag templates, team skins + kits (so players can spawn), team
    /// insignia, and the pre-spawn cameras parked over each team's base. Appended to a playable map's Init.con.
    /// </summary>
    public static IEnumerable<string> InitConBlock(int worldSize, Func<float, float, float> heightAt, IReadOnlyList<BaseDef> bases)
    {
        yield return "";
        yield return "rem *** Conquest gameplay: capture flags, kits, team skins, pre-spawn cameras ***";
        yield return "ObjectTemplate.create Flag BlueFlag";
        yield return "ObjectTemplate.team 1";
        yield return "ObjectTemplate.networkableInfo FlagBodyInfo";
        yield return "ObjectTemplate.radius 5";
        yield return "ObjectTemplate.TimeToReSpawn 10";
        yield return "ObjectTemplate.addTemplate AnimatedVCFlag";
        yield return "";
        yield return "ObjectTemplate.create Flag RedFlag";
        yield return "ObjectTemplate.team 2";
        yield return "ObjectTemplate.networkableInfo FlagBodyInfo";
        yield return "ObjectTemplate.radius 5";
        yield return "ObjectTemplate.TimeToReSpawn 10";
        yield return "ObjectTemplate.addTemplate AnimatedUSFlag";
        yield return "";
        // Team 1 = Vietcong (NVA), Team 2 = US Army — the stock BFV kits/skins (defined in the base game).
        yield return "game.setTeamSkin 1 1 VietcongA1";
        yield return "game.setTeamSkin 1 2 VietcongA2";
        yield return "game.setTeamSkin 1 3 VietcongB1";
        yield return "game.setTeamSkin 1 4 VietcongB2";
        yield return "game.setKit 1 0 Vietcong_Scout";
        yield return "game.setKit 1 1 Vietcong_Assault";
        yield return "game.setKit 1 2 Vietcong_HeavyAssault";
        yield return "game.setKit 1 3 Vietcong_Engineer";
        yield return "game.setKit 1 4 Vietcong_Scout_Alt";
        yield return "game.setKit 1 5 Vietcong_Assault_Alt";
        yield return "game.setKit 1 6 Vietcong_HeavyAssault_Alt";
        yield return "game.setKit 1 7 Vietcong_Engineer_Alt";
        yield return "game.setTeamSkin 2 1 USArmyA1";
        yield return "game.setTeamSkin 2 2 USArmyA2";
        yield return "game.setTeamSkin 2 3 USArmyB1";
        yield return "game.setTeamSkin 2 4 USArmyB2";
        yield return "game.setKit 2 0 USArmy_Recon";
        yield return "game.setKit 2 1 USArmy_Assault";
        yield return "game.setKit 2 2 USArmy_HeavyAssault";
        yield return "game.setKit 2 3 USArmy_Engineer";
        yield return "game.setKit 2 4 USArmy_Recon_Alt";
        yield return "game.setKit 2 5 USArmy_Assault_Alt";
        yield return "game.setKit 2 6 USArmy_HeavyAssault_Alt";
        yield return "game.setKit 2 7 USArmy_Engineer_Alt";
        yield return "game.setTeamInsignia 1 VCflag";
        yield return "game.setTeamInsignia 2 Cavalry";

        // Pre-spawn camera parked above each team's home base, looking at the map.
        foreach (var b in bases)
        {
            if (b.Team == 0) continue;
            float wx = b.Fx * worldSize, wz = b.Fz * worldSize, wy = heightAt(wx, wz) + 25f;
            yield return $"game.setBeforeSpawnCameraPosition {b.Team} {Pos(wx, wy, wz)}";
        }
    }

    /// <summary>
    /// Render an <c>AIpathFinding.con</c> with the standard 7 search maps (Tank/Infantry/Boat/LandingCraft/
    /// Car/Heli/Amphibius) and their stock passability params (slope/water/brush), seeded from the team bases.
    /// Mirrors retail Operation_Irving. Note: the navmaps themselves (Pathfinding/*.raw) are not generated yet —
    /// this configures AI so the engine can build/use them (it has createSearchMaps + loadMaps).
    /// </summary>
    public static IEnumerable<string> AiPathFindingCon(int worldSize, IReadOnlyList<BaseDef> bases)
    {
        // Seed points for the AI flood-fill: the team bases, in navmap grid coords (~1 cell per metre).
        var seeds = new List<string>();
        foreach (var b in bases)
            if (b.Team != 0) seeds.Add($"{(int)(b.Fx * worldSize)}/{(int)(b.Fz * worldSize)}");
        if (seeds.Count == 0) seeds.Add($"{worldSize / 2}/{worldSize / 2}");
        string sp = string.Join(",", seeds);

        // name, "<waterH waterDepth maxSlope brush lowClip hiClip considerAITypes 2ndLayer levels>", type, mapNum, minSearchLevel, smoothing
        var maps = new (string Name, string Parms, string Type, int MapNum, int MinLevel, int Smooth)[]
        {
            ("Tank0",         "0 0 30 3.0 0.3 2.5 0 0 1.0/3.0/7.0", "Tank",         0, 0, 20),
            ("Infantry1",     "0 1.5 40 1.0 0.4 2.0 1 0 1.0/3.0/7.0", "Infantry",   1, 0, 10),
            ("Boat2",         "1 1.4 30 4.0 0.3 2.5 0 0 0/0/0", "Boat",             2, 2, 20),
            ("LandingCraft3", "1 1.4 30 4.0 0.3 2.5 0 0 0/0/0", "LandingCraft",     3, 2, 20),
            ("Car4",          "0 0 35 3.0 0.3 2.5 0 0 0/0/0", "Car",                4, 0, 20),
            ("Heli5",         "0 0 20 4.0 2.0 4.5 0 0 0/0/0", "Heli",               5, 0, 20),
            ("Amphibius6",    "0 5000 30 3.0 0.3 2.5 0 0 2", "Amphibius",           6, 0, 20),
        };

        yield return "rem *** AI pathfinding (generated by RefractorForge) ***";
        foreach (var m in maps)
        {
            yield return $"ai.addSearchMap {m.Name} {m.Parms}";
            yield return $"ai.addSearchType {m.Type} {m.MapNum} {m.MinLevel}";
            yield return $"ai.setMapSpawnPoints {m.MapNum} {sp}";
            yield return $"ai.setSmoothing {m.MapNum} {m.Smooth}";
            yield return "";
        }
        yield return "ai.loadMaps";
    }
}
