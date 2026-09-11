using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RefractorForge.Formats.Con;

/// <summary>
/// Makes placed objects lightmap-able by giving the level its own COPY of each one, on a mesh that carries a
/// lightmap unwrap.
///
/// <para><b>A copy, not a rebuild.</b> An object's definition is whatever its scripts say, and a static prop is
/// often more than a mesh: BfVietnam's <c>USAmmobox</c> is a Bundle carrying <c>AmmoboxSupplyDepot</c> and
/// <c>AmmoboxVehicleSupplyDepot</c>, the medic box carries <c>mediclockerRepairpoint</c>, the rope bridge is a
/// Bundle with a LOD selector and an AI template. Rebuilding those as a plain SimpleObject - which is what the
/// first single-object tool did - silently switches off resupply and healing. So every template on the path from a
/// placed object down to a patched mesh is copied LINE FOR LINE under a new name, and the only lines that change
/// are the ones naming a mesh or a copied child. Everything else - children at an offset, gameplay children,
/// collision, cull radius, AI - is referenced or carried over untouched.</para>
///
/// <para><b>Why not redirect the mesh instead.</b> <c>GeometryTemplate.active X</c> + <c>.file</c> would be one line
/// per mesh, but nothing in 551 installed BFV archives does it, and if the engine keeps templates between maps a
/// redirect made by one level could break the next one in a server rotation. New names are referenced only by this
/// level's own placements, so they cannot leak.</para>
///
/// <para><b>Measured.</b> BfVietnam 1.21's dedicated server loads a level carrying such copies - a 64-byte mesh with
/// its slot filled in place, a 32-byte mesh widened to 40, and the ammo Bundle - and dies within two seconds when a
/// copy's mesh file is missing, so the pass is real.</para>
/// </summary>
public static class LightmapReady
{
    public const string Folder = "RF_LightmapReady";
    public const string RunLine = "run " + Folder + "/" + Folder;
    private const string Tag = "rem rf-lightmap-ready";

    /// <summary>One patched mesh: the original geometry template, the copy's name, and its files.</summary>
    public sealed record PatchedMesh(string Geometry, string Copy, byte[] Sm, string Rs);

    public sealed record Output(
        List<(string RelPath, byte[] Bytes)> Files,
        IReadOnlyDictionary<string, string> Placed,          // placed template -> the copy to point it at
        IReadOnlyList<string> Skipped);                      // "template: why"

    /// <summary>What an earlier run left in the level. <see cref="Placed"/> is what placements were pointed at;
    /// <see cref="Copies"/> is EVERY copied template, including the LodObjects and children inside a copy, so a
    /// second run reuses their names instead of mistaking its own earlier work for somebody else's template.</summary>
    public sealed record Manifest(IReadOnlyDictionary<string, string> Placed, IReadOnlyDictionary<string, string> Copies,
                                  IReadOnlyDictionary<string, string> Geometries)
    {
        public static readonly Manifest Empty = new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                                                     new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                                                     new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        /// <summary>The original a placed copy was made from, when <paramref name="template"/> is one of ours.</summary>
        public string? OriginalOf(string template)
            => Placed.FirstOrDefault(kv => kv.Value.Equals(template, StringComparison.OrdinalIgnoreCase)).Key;
        public bool IsOurs(string template)
            => Copies.Values.Contains(template, StringComparer.OrdinalIgnoreCase)
               || Geometries.Values.Contains(template, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The manifest lines at the top of the copies' <c>Objects.con</c>. Missing or foreign text reads as empty.</summary>
    public static Manifest ReadManifest(string? objectsCon)
    {
        var placed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var copies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var geoms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (objectsCon ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(Tag, StringComparison.OrdinalIgnoreCase)) continue;
            var t = line[Tag.Length..].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length != 3) continue;
            if (t[0].Equals("placed", StringComparison.OrdinalIgnoreCase)) placed[t[1]] = t[2];
            else if (t[0].Equals("copy", StringComparison.OrdinalIgnoreCase)) copies[t[1]] = t[2];
            else if (t[0].Equals("geometry", StringComparison.OrdinalIgnoreCase)) geoms[t[1]] = t[2];
        }
        foreach (var kv in placed) copies.TryAdd(kv.Key, kv.Value);
        return new Manifest(placed, copies, geoms);
    }

    /// <summary>The name a patched copy of <paramref name="geometry"/> gets: the earlier run's name if there was one,
    /// else <c>foo_lm_m1</c>, numbered past any geometry the mod already defines under that name.</summary>
    public static string GeometryCopyName(TemplateScripts ts, string geometry, Manifest? previous = null,
                                          ISet<string>? taken = null)
    {
        if (previous is not null && previous.Geometries.TryGetValue(geometry, out var had)) return had;
        // The number goes with the marker, BEFORE the LOD tag: "crate_lm2_m1", never "crate_lm_m12", which the
        // lightmap matcher would read as LOD twelve.
        string cand = LightmapMeshPatch.PatchedName(geometry);
        for (int i = 2; (taken?.Contains(cand) ?? false) || (ts.Geometry(cand) is not null && !(previous?.IsOurs(cand) ?? false)); i++)
            cand = LightmapMeshPatch.PatchedName(geometry, "_lm" + i.ToString(CultureInfo.InvariantCulture));
        taken?.Add(cand);
        return cand;
    }

    /// <summary>A geometry template's mesh file as written in its script (a bare name or a path), or the name itself
    /// when no script defines it.</summary>
    public static string GeometryFile(TemplateScripts ts, string geometry)
    {
        var d = ts.Geometry(geometry);
        if (d is not null)
            foreach (var l in d.Lines)
            {
                var (cmd, arg) = TemplateScripts.Command(l);
                if (cmd.Equals("file", StringComparison.OrdinalIgnoreCase) && arg.Length > 0) return arg;
            }
        return geometry;
    }

    private sealed record Child(string Name, int LineIndex, bool AtOrigin);

    /// <summary>A template's <c>addTemplate</c> children with where each sits. A child followed by a non-zero
    /// <c>setPosition</c>/<c>setRotation</c> is a part placed at an offset, not an alternative of its parent.</summary>
    private static List<Child> Children(TemplateScripts.Def d)
    {
        var list = new List<Child>();
        for (int i = 0; i < d.Lines.Count; i++)
        {
            var (cmd, arg) = TemplateScripts.Command(d.Lines[i]);
            if (cmd.Equals("addTemplate", StringComparison.OrdinalIgnoreCase))
                list.Add(new Child(TemplateScripts.FirstToken(arg), i, true));
            else if (list.Count > 0 && (cmd.Equals("setPosition", StringComparison.OrdinalIgnoreCase)
                                        || cmd.Equals("setRotation", StringComparison.OrdinalIgnoreCase)) && !IsZero(arg))
                list[^1] = list[^1] with { AtOrigin = false };
        }
        return list;
    }

    private static bool IsZero(string vec)
    {
        foreach (var part in vec.Split('/'))
            if (!float.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) || MathF.Abs(f) > 1e-6f)
                return false;
        return true;
    }

    private static IEnumerable<string> GeometriesOf(TemplateScripts.Def d)
    {
        foreach (var l in d.Lines)
        {
            var (cmd, arg) = TemplateScripts.Command(l);
            if (cmd.Equals("geometry", StringComparison.OrdinalIgnoreCase) && arg.Length > 0) yield return TemplateScripts.FirstToken(arg);
        }
    }

    /// <summary>
    /// Every mesh a placed template is DRAWN with, by geometry template name - the same set the bake lights and the
    /// game files lightmaps under: the template's own geometry, every alternative of a LodObject, and children that
    /// sit at the origin. Parts placed at an offset are the engine's to name by their own position; they are left
    /// alone here exactly as the bake leaves them.
    /// </summary>
    public static List<string> LodGeometries(TemplateScripts ts, string template)
    {
        var res = new List<string>();
        void Walk(string t, HashSet<string> seen, int depth)
        {
            if (depth > 24 || !seen.Add(t)) return;
            var d = ts.Object(t);
            if (d is null) return;
            foreach (var g in GeometriesOf(d))
                if (!res.Contains(g, StringComparer.OrdinalIgnoreCase)) res.Add(g);
            bool lod = d.Type.Equals("LodObject", StringComparison.OrdinalIgnoreCase);
            foreach (var c in Children(d))
                if (lod || c.AtOrigin) Walk(c.Name, seen, depth + 1);
        }
        Walk(template, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0);
        return res;
    }

    /// <summary>
    /// The level-local files that give each of <paramref name="placedTemplates"/> a copy on its patched meshes.
    /// Templates that cannot be copied faithfully are returned in <see cref="Output.Skipped"/> with the reason,
    /// and nothing is written for them.
    /// </summary>
    /// <param name="patches">Patched meshes, keyed by the ORIGINAL geometry template name.</param>
    /// <param name="previous">An earlier run's manifest, so copies keep their names across runs.</param>
    public static Output Emit(TemplateScripts ts, IEnumerable<string> placedTemplates,
                              IReadOnlyDictionary<string, PatchedMesh> patches, string levelName, string baseSub,
                              Manifest? previous = null)
    {
        previous ??= Manifest.Empty;
        var patch = new Dictionary<string, PatchedMesh>(patches, StringComparer.OrdinalIgnoreCase);
        var skipped = new List<string>();
        var need = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // Does this template (or anything drawn as part of it) use a patched mesh? A template that is defined more
        // than once with different bodies is not copied: which body the engine keeps is not established.
        bool Needs(string t, int depth)
        {
            if (need.TryGetValue(t, out var known)) return known;
            need[t] = false;                                   // cycle guard
            var d = ts.Object(t);
            if (d is null || depth > 24) return false;
            bool n = GeometriesOf(d).Any(g => patch.ContainsKey(g));
            bool lod = d.Type.Equals("LodObject", StringComparison.OrdinalIgnoreCase);
            foreach (var c in Children(d))
                if ((lod || c.AtOrigin) && Needs(c.Name, depth + 1)) n = true;
            if (n && (d.ConflictingCreates || d.Type.Length == 0)) n = false;
            need[t] = n;
            return n;
        }

        var placed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);     // template -> copy
        foreach (var kv in previous.Copies) names[kv.Key] = kv.Value;
        var taken = new HashSet<string>(names.Values, StringComparer.OrdinalIgnoreCase);

        string CopyName(string t)
        {
            if (names.TryGetValue(t, out var n)) return n;
            string cand = LightmapMeshPatch.PatchedName(t);
            for (int i = 2; taken.Contains(cand) || (ts.Object(cand) is not null && !previous.IsOurs(cand)); i++)
                cand = LightmapMeshPatch.PatchedName(t, "_lm" + i.ToString(CultureInfo.InvariantCulture));
            taken.Add(cand);
            return names[t] = cand;
        }

        foreach (var t in placedTemplates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var d = ts.Object(t);
            if (d is null) { skipped.Add($"{t}: no definition found in the level or its mod"); continue; }
            if (d.ConflictingCreates) { skipped.Add($"{t}: defined more than once with different contents"); continue; }
            if (!Needs(t, 0)) { skipped.Add($"{t}: none of its meshes was patched"); continue; }
            placed[t] = CopyName(t);
        }

        // Every template reachable from a placed one that needs copying, children first so the order reads naturally.
        var order = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Collect(string t, int depth)
        {
            if (depth > 24 || !visited.Add(t) || !Needs(t, 0)) return;
            var d = ts.Object(t)!;
            bool lod = d.Type.Equals("LodObject", StringComparison.OrdinalIgnoreCase);
            foreach (var c in Children(d)) if (lod || c.AtOrigin) Collect(c.Name, depth + 1);
            order.Add(t);
        }
        foreach (var t in placed.Keys) Collect(t, 0);

        var crlf = "\r\n";
        var objs = new StringBuilder();
        objs.Append("rem RefractorForge: lightmap-ready copies of objects whose meshes had no lightmap unwrap.").Append(crlf);
        objs.Append("rem Each is its original definition line for line; only the names of patched meshes and copied children differ.").Append(crlf);
        foreach (var kv in placed.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            objs.Append($"{Tag} placed {kv.Key} {kv.Value}").Append(crlf);
        foreach (var t in order.Where(t => !placed.ContainsKey(t)).OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
            objs.Append($"{Tag} copy {t} {CopyName(t)}").Append(crlf);
        foreach (var p in patch.Values.OrderBy(p => p.Geometry, StringComparer.OrdinalIgnoreCase))
            objs.Append($"{Tag} geometry {p.Geometry} {p.Copy}").Append(crlf);
        objs.Append(crlf);

        foreach (var t in order)
        {
            var d = ts.Object(t)!;
            bool lod = d.Type.Equals("LodObject", StringComparison.OrdinalIgnoreCase);
            var copyChild = new HashSet<int>();
            foreach (var c in Children(d))
                if ((lod || c.AtOrigin) && need.TryGetValue(c.Name, out var cn) && cn) copyChild.Add(c.LineIndex);

            objs.Append($"ObjectTemplate.create {d.Type} {CopyName(t)}").Append(crlf);
            for (int i = 0; i < d.Lines.Count; i++)
            {
                string line = d.Lines[i];
                var (cmd, arg) = TemplateScripts.Command(line);
                string prefix = line[..line.IndexOf('.')];          // keep the file's own spelling of the family
                if (cmd.Equals("geometry", StringComparison.OrdinalIgnoreCase) && patch.TryGetValue(TemplateScripts.FirstToken(arg), out var pm))
                    line = $"{prefix}.geometry {pm.Copy}";
                else if (copyChild.Contains(i))
                    line = $"{prefix}.addTemplate {CopyName(TemplateScripts.FirstToken(arg))}";
                objs.Append(line).Append(crlf);
            }
            objs.Append(crlf);
        }

        var geo = new StringBuilder();
        foreach (var p in patch.Values.OrderBy(p => p.Geometry, StringComparer.OrdinalIgnoreCase))
        {
            var d = ts.Geometry(p.Geometry);
            geo.Append($"GeometryTemplate.create {(d is { Type.Length: > 0 } ? d.Type : "StandardMesh")} {p.Copy}").Append(crlf);
            geo.Append($"GeometryTemplate.file ../{baseSub}/levels/{levelName}/StandardMesh/{p.Copy}").Append(crlf);
            if (d is not null)
                foreach (var l in d.Lines)
                    if (!TemplateScripts.Command(l).Cmd.Equals("file", StringComparison.OrdinalIgnoreCase))
                        geo.Append(l).Append(crlf);
            geo.Append(crlf);
        }

        var enc = Encoding.Latin1;
        var files = new List<(string, byte[])>();
        foreach (var p in patch.Values)
        {
            files.Add(($"StandardMesh/{p.Copy}.sm", p.Sm));
            files.Add(($"StandardMesh/{p.Copy}.rs", enc.GetBytes(p.Rs.Replace("\r\n", "\n").Replace("\n", crlf))));
        }
        // Geometries first, as the level's own object folders do (al_vietnas: "run geometries / run objects").
        files.Add(($"Objects/{Folder}/{Folder}.con", enc.GetBytes($"run Geometries{crlf}run Objects{crlf}")));
        files.Add(($"Objects/{Folder}/Geometries.con", enc.GetBytes(geo.ToString())));
        files.Add(($"Objects/{Folder}/Objects.con", enc.GetBytes(objs.ToString())));
        return new Output(files, placed, skipped);
    }

    /// <summary>Make sure the level's <c>Objects/Objects.con</c> runs the copies. Idempotent.</summary>
    public static string PatchObjectsCon(string? existing)
        => DecalObject.PatchObjectsCon(existing, RunLine);

    /// <summary>
    /// Make sure <c>Init.con</c> runs the level's Objects folder. Only that line: unlike a decal, a lightmap-ready copy
    /// needs no texture path. Appended at the end, which is before the engine creates the static objects - it loads
    /// <c>StaticObjects.con</c> itself once Init.con has finished.
    /// </summary>
    public static string PatchInitCon(string existing)
    {
        var lines = existing.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Any(l => l.Trim().Equals("run Objects/Objects", StringComparison.OrdinalIgnoreCase))) return existing;
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        lines.Add("run Objects/Objects");
        return string.Join("\r\n", lines) + "\r\n";
    }

    /// <summary>
    /// Point every underground map at the template that is actually PLACED. The engine binds a map to one template by
    /// name - <c>mapManager.addObjectMap o_sewers_A_M1 SewersAMap ...</c> - and making a tunnel lightmap-ready points its
    /// placements at a copy (<c>o_sewers_a_lm_m1</c>, whose lights child was patched). The map was then bound to a
    /// template nothing in the level used, and the underground map never came up (al_vietnas, 2026-09-11). Pointing
    /// the copies back at their originals needs the reverse. A map whose template is placed, or that no pairing in
    /// the manifest explains, is left exactly as it was.
    /// </summary>
    public static List<Terrain.EnvironmentSettings.ObjectMap> RebindObjectMaps(
        IEnumerable<Terrain.EnvironmentSettings.ObjectMap> maps, ISet<string> placedTemplates, Manifest manifest, out bool changed)
    {
        changed = false;
        var result = new List<Terrain.EnvironmentSettings.ObjectMap>();
        foreach (var m in maps)
        {
            string t = m.Template;
            if (!placedTemplates.Contains(t))
            {
                if (manifest.Placed.TryGetValue(t, out var copy) && placedTemplates.Contains(copy)) t = copy;
                else if (manifest.OriginalOf(t) is { } orig && placedTemplates.Contains(orig)) t = orig;
            }
            // One map per template: a level that bound both the original and its copy keeps the first.
            if (result.Any(r => r.Template.Equals(t, StringComparison.OrdinalIgnoreCase))) { changed = true; continue; }
            if (!t.Equals(m.Template, StringComparison.Ordinal)) { changed = true; result.Add(m with { Template = t }); }
            else result.Add(m);
        }
        return result;
    }
}
