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
        var sb = new StringBuilder();
        foreach (var c in gp.ControlPoints)
            sb.Append("CP ").Append(Tok(c.Name)).Append(' ').Append(c.Position).Append(' ').Append(F(c.Radius)).Append(' ').Append(c.SpawnGroupId)
              .Append(' ').Append(c.Team).Append(' ').Append(c.AreaValue).Append(' ').Append(c.ConversionTime).Append(' ').Append(Tok(c.ControlPointName)).Append('\n');
        foreach (var v in gp.VehicleSpawns)
            sb.Append("VS ").Append(Tok(v.Name)).Append(' ').Append(v.Position).Append(' ').Append(v.Rotation).Append(' ').Append(Tok(v.Vehicle)).Append(' ').Append(v.OsId).Append('\n');
        foreach (var s in gp.SoldierSpawns)
            sb.Append("SS ").Append(Tok(s.Name)).Append(' ').Append(s.Position).Append(' ').Append(s.Rotation).Append('\n');
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
                switch (p[0])
                {
                    case "CP" when p.Length >= 5:
                        cps.Add(new ControlPointDef(UnTok(p[1]), Vec3.Parse(p[2]), float.Parse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture), int.Parse(p[4]),
                            p.Length >= 6 ? int.Parse(p[5]) : 0,            // team (back-compat: older 5-field lines)
                            p.Length >= 7 ? int.Parse(p[6]) : 25,           // areaValue
                            p.Length >= 8 ? int.Parse(p[7]) : 40,           // conversionTime
                            p.Length >= 9 ? UnTok(p[8]) : UnTok(p[1]))); break;   // controlPointName (defaults to the object name)
                    case "VS" when p.Length >= 6:
                        vss.Add(new VehicleSpawnDef(UnTok(p[1]), Vec3.Parse(p[2]), Vec3.Parse(p[3]), UnTok(p[4]), int.Parse(p[5]))); break;
                    case "SS" when p.Length >= 4:
                        sss.Add(new SoldierSpawnDef(UnTok(p[1]), Vec3.Parse(p[2]), Vec3.Parse(p[3]))); break;
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
