using System.Numerics;
using RefractorForge.Formats.Rfa;

namespace RefractorForge.Render;

/// <summary>
/// Parses Battlefield 1942 particle EFFECTS (the <c>FX/&lt;name&gt;/Effects.con</c> particle systems + any global ones in
/// the object archives) so the editor can show a placed effect — a waterfall, lava fall, oil fire, smoke, steam, snow —
/// as an animated particle preview instead of an invisible marker.
///
/// <para>An effect is a 3-level template tree:
/// <c>EffectBundle</c> (the placeable name) <c>addTemplate</c>s one or more <c>Emitter</c>s at local offsets; each Emitter
/// names a <c>SpriteParticle</c> via <c>ObjectTemplate.template</c> and carries the spawn rate (<c>intensity</c>),
/// initial velocity (<c>positionalSpeedInUp/Right/Dof</c>) and emitter lifetime; the SpriteParticle carries the texture,
/// particle size, lifetime, gravity, size-over-time and blend mode. Some placed objects (e.g. <c>aux_oilfire_m1</c>) are a
/// regular mesh ObjectTemplate that ALSO addTemplates an effect — those render their mesh (via the mesh library) AND the
/// effect (here).</para>
///
/// <para>Numbers use Refractor's CRD (random distribution) form <c>CRD_NONE/x/y/z</c> or <c>CRD_NORMAL/mean/dev/..</c>;
/// for a preview we take the central value (the 2nd token).</para>
/// </summary>
public sealed class EffectsLibrary
{
    /// <summary>One resolved emitter ready to simulate: a textured particle stream at a local offset from the placed object.</summary>
    public sealed record EmitterDef(
        Vector3 LocalPos, string Texture, float Rate, float ParticleTtl,
        Vector3 Velocity, Vector3 Spread, float Gravity, float Size, float SizeEnd, bool Additive, bool Spin, float LodDistance);

    // Raw parsed templates, keyed by name (case-insensitive).
    private sealed class Bundle { public readonly List<(string Emitter, Vector3 Pos)> Children = new(); }
    private sealed class Emitter { public string? Particle; public float Rate = 10; public float Ttl = -1; public Vector3 Vel; public Vector3 Spread; public float Lod = 250; }
    private sealed class Sprite { public string? Texture; public float Ttl = 2; public float Size = 1; public float SizeEnd = 1; public float Gravity; public bool Additive; public bool Spin; }

    private readonly Dictionary<string, Bundle> _bundles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Emitter> _emitters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Sprite> _sprites = new(StringComparer.OrdinalIgnoreCase);
    // ObjectTemplate (mesh object) -> the effect templates it addTemplates (so a mesh+effect object shows both).
    private readonly Dictionary<string, List<(string Child, Vector3 Pos)>> _objEffects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EmitterDef[]?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public int BundleCount => _bundles.Count;

    /// <summary>Load every Effects.con (and object .con that attaches effects) from the given RFA archives.</summary>
    public static EffectsLibrary FromArchives(IEnumerable<RfaArchive> archives)
    {
        var lib = new EffectsLibrary();
        foreach (var a in archives)
            foreach (var e in a.Entries)
            {
                if (!e.Name.EndsWith(".con", StringComparison.OrdinalIgnoreCase)) continue;
                // Only parse cons that could define an effect or attach one (cheap pre-filter on the decoded text).
                string text;
                try { text = System.Text.Encoding.Latin1.GetString(a.Read(e)); } catch { continue; }
                // Parse effect-definition cons AND any con that addTemplates (a mesh object may attach an effect with no
                // effect keyword of its own, e.g. aux_oilfire_m1's Objects.con: "create Obstacle ... addtemplate e_OilFire").
                if (text.IndexOf("EffectBundle", StringComparison.OrdinalIgnoreCase) < 0
                    && text.IndexOf("Emitter", StringComparison.OrdinalIgnoreCase) < 0
                    && text.IndexOf("SpriteParticle", StringComparison.OrdinalIgnoreCase) < 0
                    && text.IndexOf("addTemplate", StringComparison.OrdinalIgnoreCase) < 0) continue;
                lib.ParseCon(text);
            }
        return lib;
    }

    public static EffectsLibrary FromRfaPaths(IEnumerable<string> paths)
    {
        var arcs = new List<RfaArchive>();
        foreach (var p in paths)
        {
            if (!File.Exists(p) || Path.GetFileName(p).StartsWith("~")) continue;
            try { arcs.Add(RfaArchive.Open(p)); } catch { }
        }
        return FromArchives(arcs);
    }

    private static float Crd(string arg)
    {
        // "CRD_NORMAL/45/4/0" -> 45 (mean) ; "CRD_NONE/-20/0/0" -> -20 ; a bare number -> itself.
        var p = arg.Split('/');
        int idx = (p.Length > 0 && p[0].StartsWith("CRD", StringComparison.OrdinalIgnoreCase)) ? 1 : 0;
        return idx < p.Length && float.TryParse(p[idx], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0f;
    }

    private static float CrdDev(string arg)
    {
        // the DEVIATION/spread term (token after the mean): "CRD_NORMAL/45/4/0" -> 4. Used to fan particles out naturally
        // like the engine, instead of an arbitrary random spread.
        var p = arg.Split('/');
        int idx = (p.Length > 0 && p[0].StartsWith("CRD", StringComparison.OrdinalIgnoreCase)) ? 2 : 1;
        return idx < p.Length && float.TryParse(p[idx], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? Math.Abs(v) : 0f;
    }

    private void ParseCon(string text)
    {
        string type = "", name = "";          // current ObjectTemplate.create <type> <name>
        Bundle? curBundle = null; Emitter? curEmitter = null; Sprite? curSprite = null;
        string? curObj = null;                // current non-effect ObjectTemplate (for mesh+effect objects)
        int pendingChild = -1;                // index into curBundle.Children / _objEffects[curObj] awaiting setPosition

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("rem", StringComparison.OrdinalIgnoreCase)) continue;
            if (!line.StartsWith("ObjectTemplate.", StringComparison.OrdinalIgnoreCase)) continue;
            var rest = line.Substring("ObjectTemplate.".Length);
            int sp = rest.IndexOf(' ');
            string cmd = (sp < 0 ? rest : rest[..sp]).ToLowerInvariant();
            string arg = sp < 0 ? "" : rest[(sp + 1)..].Trim();

            if (cmd == "create")
            {
                var t = arg.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                type = t.Length > 0 ? t[0] : ""; name = t.Length > 1 ? t[1] : "";
                curBundle = null; curEmitter = null; curSprite = null; curObj = null; pendingChild = -1;
                if (type.Equals("EffectBundle", StringComparison.OrdinalIgnoreCase)) { curBundle = new Bundle(); _bundles[name] = curBundle; }
                else if (type.Equals("Emitter", StringComparison.OrdinalIgnoreCase)) { curEmitter = new Emitter(); _emitters[name] = curEmitter; }
                else if (type.Equals("SpriteParticle", StringComparison.OrdinalIgnoreCase)) { curSprite = new Sprite(); _sprites[name] = curSprite; }
                else { curObj = name; }   // some other ObjectTemplate (e.g. a mesh object that may attach an effect)
                continue;
            }
            if (cmd == "addtemplate")
            {
                var child = arg.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? arg;
                if (curBundle is not null) { curBundle.Children.Add((child, Vector3.Zero)); pendingChild = curBundle.Children.Count - 1; }
                else if (curObj is not null) { if (!_objEffects.TryGetValue(curObj, out var l)) _objEffects[curObj] = l = new(); l.Add((child, Vector3.Zero)); pendingChild = l.Count - 1; }
                continue;
            }
            if (cmd == "setposition" && pendingChild >= 0)
            {
                var pos = ParseVec(arg);
                if (curBundle is not null && pendingChild < curBundle.Children.Count) curBundle.Children[pendingChild] = (curBundle.Children[pendingChild].Emitter, pos);
                else if (curObj is not null && _objEffects.TryGetValue(curObj, out var l) && pendingChild < l.Count) l[pendingChild] = (l[pendingChild].Child, pos);
                continue;
            }
            if (curEmitter is not null)
            {
                switch (cmd)
                {
                    case "template": curEmitter.Particle = arg.Trim(); break;
                    case "intensity": curEmitter.Rate = Math.Max(0.5f, Crd(arg)); break;
                    case "timetolive": curEmitter.Ttl = Crd(arg); break;
                    case "loddistance": if (float.TryParse(arg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ld)) curEmitter.Lod = ld; break;
                    case "positionalspeedindof": curEmitter.Vel.Z = Crd(arg); curEmitter.Spread.Z = CrdDev(arg); break;
                    case "positionalspeedinup": curEmitter.Vel.Y = Crd(arg); curEmitter.Spread.Y = CrdDev(arg); break;
                    case "positionalspeedinright": curEmitter.Vel.X = Crd(arg); curEmitter.Spread.X = CrdDev(arg); break;
                }
                continue;
            }
            if (curSprite is not null)
            {
                switch (cmd)
                {
                    case "texture": curSprite.Texture = arg.Trim(); break;
                    case "timetolive": curSprite.Ttl = Math.Max(0.1f, Crd(arg)); break;
                    case "size": curSprite.Size = Math.Max(0.05f, Crd(arg)); break;
                    case "gravitymodifier": curSprite.Gravity = Crd(arg); break;
                    case "destblendmode":
                        // BMOne / BMSourceAlpha = additive glow (fire/lava/explosions); BMInvSourceAlpha = normal alpha.
                        var b = arg.Trim();
                        curSprite.Additive = b.Equals("BMOne", StringComparison.OrdinalIgnoreCase) || b.Equals("BMSourceAlpha", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "sizeovertime":
                        // "0/0.6|10/1|...|100/1.8" — take the last keyframe's multiplier as the end-of-life size scale.
                        var last = arg.Split('|').LastOrDefault();
                        var kv = last?.Split('/');
                        if (kv is { Length: >= 2 } && float.TryParse(kv[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var so)) curSprite.SizeEnd = so;
                        break;
                    case "initrotation":
                        // present (e.g. "CRD_UNIFORM/1/360/1") -> the sprite spins; give each particle a random angle.
                        curSprite.Spin = true;
                        break;
                }
                continue;
            }
        }
    }

    private static Vector3 ParseVec(string s)
    {
        var p = s.Split('/');
        float F(int i) => i < p.Length && float.TryParse(p[i].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 0f;
        return new Vector3(F(0), F(1), F(2));
    }

    /// <summary>Resolve a placed template name to its flattened emitter list (the effect bundle's emitters, or the effect
    /// templates a mesh object attaches), or false if the template has no effect. Cached.</summary>
    public bool TryResolve(string template, out EmitterDef[] emitters)
    {
        emitters = Array.Empty<EmitterDef>();
        if (string.IsNullOrWhiteSpace(template)) return false;
        if (!_cache.TryGetValue(template, out var cached))
        {
            var acc = new List<EmitterDef>();
            CollectBundle(template, Vector3.Zero, acc, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            // A mesh+effect object (aux_oilfire_m1): also pull the effects it addTemplates.
            if (_objEffects.TryGetValue(template, out var attached))
                foreach (var (child, pos) in attached) CollectBundle(child, pos, acc, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            cached = acc.Count > 0 ? acc.ToArray() : null;
            _cache[template] = cached;
        }
        emitters = cached ?? Array.Empty<EmitterDef>();
        return cached is not null;
    }

    // Walk a name: if it's a bundle, add its emitters (at parent+local); if it's an emitter, resolve its sprite + add.
    private void CollectBundle(string name, Vector3 parentPos, List<EmitterDef> acc, int depth, HashSet<string> visiting)
    {
        if (depth > 8 || !visiting.Add(name)) return;
        if (_bundles.TryGetValue(name, out var b))
            foreach (var (child, pos) in b.Children) CollectBundle(child, parentPos + pos, acc, depth + 1, visiting);
        else if (_emitters.TryGetValue(name, out var em))
        {
            var sprite = em.Particle is not null && _sprites.TryGetValue(em.Particle, out var s) ? s : null;
            if (sprite?.Texture is { Length: > 0 } tex)
                acc.Add(new EmitterDef(parentPos, tex, em.Rate, sprite.Ttl, em.Vel, em.Spread, sprite.Gravity * 9.81f,
                                       sprite.Size, sprite.Size * sprite.SizeEnd, sprite.Additive, sprite.Spin, em.Lod));
        }
        visiting.Remove(name);
    }
}
