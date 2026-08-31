using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RefractorForge.Formats.Geometry;

namespace RefractorForge.Formats.Con;

/// <summary>
/// Compact full-state (de)serialization of the gameplay layer for collaboration. Gameplay is small (a few
/// dozen handles), so rather than wrestle index-addressed incremental ops into convergence, a gameplay edit
/// just ships the WHOLE layer; the receiver replaces its own. Relay ordering makes this last-writer-wins and
/// it can never desync. One line per handle ("CP/VS/SS &lt;name&gt; &lt;pos&gt; ..."); tokens are space-free.
/// </summary>
public static class GameplaySync
{
    private static string Tok(string s) => string.IsNullOrEmpty(s) ? "-" : s.Replace(' ', '_');
    private static string UnTok(string s) => s == "-" ? "" : s;
    private static string F(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    public static string Serialize(EditableGameplay gp)
    {
        // EVERY field goes on the wire. This is full-state sync that the relay echoes back to the sender, so a
        // field left out is not merely unseen by other people - it is erased on every copy INCLUDING the editor
        // that made the edit, and written out by the next save. Leaving fields off cost the per-team vehicle
        // templates, the spawner team, and the group id that ties a soldier spawn to its control point.
        // New fields are APPENDED, and Parse defaults anything absent, so an older peer's shorter line still loads.
        var sb = new StringBuilder();
        foreach (var c in gp.ControlPoints)
            sb.Append("CP ").Append(Tok(c.Name)).Append(' ').Append(c.Position).Append(' ').Append(F(c.Radius)).Append(' ').Append(c.SpawnGroupId)
              .Append(' ').Append(c.Team).Append(' ').Append(c.AreaValue).Append(' ').Append(c.ConversionTime).Append(' ').Append(Tok(c.ControlPointName))
              .Append(' ').Append(c.ObjectSpawnerId)
              .Append(' ').Append(Tok(c.PoleGeometry)).Append(' ').Append(Tok(c.FlagGeometry1)).Append(' ').Append(Tok(c.FlagGeometry2))
              .Append(' ').Append(F(c.FlagHeight))
              .Append(' ').Append(c.TimeToGetControl).Append(' ').Append(c.TimeToLoseControl)
              .Append(' ').Append(c.DisableIfEnemyInside).Append(' ').Append(c.DisableWhenLosing)
              .Append(' ').Append(c.LoseControlWhenEnemyClose).Append(' ').Append(c.LoseControlWhenNotClose)
              .Append(' ').Append(c.UnableToChangeTeam).Append(' ').Append(c.OnlyTakableByTeam)
              .Append(' ').Append(c.HasCollisionPhysics).Append('\n');
        foreach (var v in gp.VehicleSpawns)
            sb.Append("VS ").Append(Tok(v.Name)).Append(' ').Append(v.Position).Append(' ').Append(v.Rotation).Append(' ').Append(Tok(v.Vehicle)).Append(' ').Append(v.OsId)
              .Append(' ').Append(Tok(v.Vehicle1)).Append(' ').Append(Tok(v.Vehicle2)).Append(' ').Append(v.Team)
              .Append(' ').Append(v.MinSpawnDelay).Append(' ').Append(v.MaxSpawnDelay).Append(' ').Append(v.SpawnDelayAtStart)
              .Append(' ').Append(v.TimeToLive).Append(' ').Append(v.Distance).Append(' ').Append(v.DamageWhenLost)
              .Append(' ').Append(v.MaxNrOfObjectSpawned).Append('\n');
        foreach (var s in gp.SoldierSpawns)
            sb.Append("SS ").Append(Tok(s.Name)).Append(' ').Append(s.Position).Append(' ').Append(s.Rotation)
              .Append(' ').Append(s.Group).Append(' ').Append(s.SpawnId).Append(' ').Append(s.SpawnAsParaTrooper).Append('\n');
        return sb.ToString();
    }

    /// <summary>Parse the full-state text into fresh handle lists.</summary>
    public static (List<ControlPointDef> Cps, List<VehicleSpawnDef> Vss, List<SoldierSpawnDef> Sss) Parse(string text)
    {
        var cps = new List<ControlPointDef>();
        var vss = new List<VehicleSpawnDef>();
        var sss = new List<SoldierSpawnDef>();
        foreach (var raw in text.Split('\n'))
        {
            var p = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) continue;
            try
            {
                // Index-guarded reads throughout: a shorter line from an older peer takes the record's own
                // default rather than failing, which is what keeps mixed-version sessions working.
                int I(int i, int dflt) => p.Length > i ? int.Parse(p[i], CultureInfo.InvariantCulture) : dflt;
                float Fl(int i, float dflt) => p.Length > i ? float.Parse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture) : dflt;
                string St(int i, string dflt) => p.Length > i ? UnTok(p[i]) : dflt;

                switch (p[0])
                {
                    case "CP" when p.Length >= 5:
                        cps.Add(new ControlPointDef(UnTok(p[1]), Vec3.Parse(p[2]), float.Parse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture), int.Parse(p[4]),
                            I(5, 0),                       // team (back-compat: older 5-field lines)
                            I(6, 25),                      // areaValue
                            I(7, 40),                      // conversionTime
                            St(8, UnTok(p[1])),            // controlPointName (defaults to the object name)
                            I(9, 0),                       // objectSpawnerId - owns this flag's vehicle spawners
                            St(10, "flagbase_m1"), St(11, "flagge_m1"), St(12, "flaguk_m1"), Fl(13, 8.2f),
                            I(14, 40), I(15, 40),          // timeToGet / timeToLoseControl
                            I(16, 0), I(17, 0), I(18, 1), I(19, 0), I(20, 0), I(21, 0), I(22, 1))); break;
                    case "VS" when p.Length >= 6:
                        vss.Add(new VehicleSpawnDef(UnTok(p[1]), Vec3.Parse(p[2]), Vec3.Parse(p[3]), UnTok(p[4]), int.Parse(p[5]),
                            St(6, ""), St(7, ""),          // the per-team vehicle templates
                            I(8, 0),                       // team
                            I(9, 20), I(10, 20), I(11, 0), I(12, 120), I(13, 200), I(14, 10), I(15, 1))); break;
                    case "SS" when p.Length >= 4:
                        sss.Add(new SoldierSpawnDef(UnTok(p[1]), Vec3.Parse(p[2]), Vec3.Parse(p[3]),
                            I(4, 0),                       // group - matches the control point's spawnGroupId
                            I(5, 0), I(6, 0))); break;
                }
            }
            catch { /* skip a malformed line */ }
        }
        return (cps, vss, sss);
    }

    /// <summary>Replace <paramref name="gp"/>'s contents with the deserialized full state.</summary>
    public static void Apply(EditableGameplay gp, string text)
    {
        var (cps, vss, sss) = Parse(text);
        gp.ReplaceAll(cps, vss, sss);
    }
}
