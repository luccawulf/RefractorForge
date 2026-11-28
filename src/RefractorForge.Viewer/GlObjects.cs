using System.Linq;
using System.Numerics;
using RefractorForge.Formats.Con;
using RefractorForge.Render;
using Silk.NET.OpenGL;

namespace RefractorForge.Viewer;

/// <summary>
/// Uploads each unique object template's StandardMesh to the GPU once (interleaved
/// position+normal+uv), uploads each unique DDS texture once, then draws every placement with its
/// world transform. Textured parts sample their bitmap (alpha-tested for foliage cut-outs); untextured
/// parts use the per-material shader colour. Smooth normals are accumulated per vertex.
/// </summary>
public sealed class GlObjects
{
    private struct Part { public int Offset; public int Count; public Vector3 Color; public uint Tex; public bool AlphaTest; public bool Blend; public float? AlphaRef; public bool Foliage; public float? Opacity; public bool DepthWrite;
                          public Vector3 MatDif; public Vector3 SelfIllum; }

    /// <summary>The shader's uAlphaTest mode for a part, plus its alpha-test threshold. Mode picks OUTPUT and
    /// LIGHTING; the threshold is independent and applies in every mode, because a blended material can carry an
    /// <c>alphaTestRef</c> too (headlight glows do). A blend wins the mode - it still discards below its ref.
    /// Otherwise vegetation is mode 1 (lit flat) and a hard surface is mode 3 (shaded solid); both use the material's
    /// own threshold, which for foliage is derived from the texture and falls back to the long-standing 0.33.</summary>
    private static (int Mode, float Ref) AlphaModeOf(Part p) =>
        p.Blend ? (2, p.AlphaRef ?? 0f)
        : p.AlphaTest ? (p.Foliage ? 1 : 3, p.AlphaRef ?? 0.33f)
        : (0, 0f);

    // uAlphaRef and uMinOpacity are resolved here rather than threaded through every Draw* signature (there are a
    // dozen call sites).
    private uint _refProg; private int _refLoc = -1, _minOpacityLoc = -1;
    private void ResolveLocs(GL gl, uint prog)
    {
        if (prog == _refProg) return;
        _refProg = prog;
        _refLoc = gl.GetUniformLocation(prog, "uAlphaRef");
        _minOpacityLoc = gl.GetUniformLocation(prog, "uMinOpacity");
    }

    /// <summary>How solid a blended pane stays when its texture alpha is nothing. The .rs calls this "minimum
    /// opacity" and BFVietnam glass authors 0.2 - but it pairs that with a <c>reflectivity</c> cubemap the viewport
    /// cannot draw, and the reflection is most of what makes a canopy read as glass rather than a hole. The
    /// shader makes up the difference with a fresnel sheen, so the authored floor can be honoured as written.</summary>
    private const float DefaultMinOpacity = 0.30f;      // what BF1942 material with no opacity line has always used

    private void SetAlpha(GL gl, uint prog, int uAlphaTest, Part part)
    {
        var (mode, aref) = AlphaModeOf(part);
        gl.Uniform1(uAlphaTest, mode);
        ResolveLocs(gl, prog);
        if (_refLoc >= 0) gl.Uniform1(_refLoc, aref);
        if (_minOpacityLoc >= 0) gl.Uniform1(_minOpacityLoc, part.Opacity ?? DefaultMinOpacity);
        // `depthWrite false` on 97 of BFVietnam's shaders, all of them blended glass. Objects are drawn unsorted
        // here, so a pane that stamped depth erased whatever was drawn after it - the aircraft behind its own canopy.
        gl.DepthMask(part.DepthWrite);
    }
    private sealed class Template { public uint Vao; public Part[] Parts = Array.Empty<Part>(); public Vector3 BbMin; public Vector3 BbMax;
                                    /// <summary>No tangent frame: the game draws it fixed-function (MeshLibrary.Mesh.VertexLit).</summary>
                                    public bool VertexLit; }

    private readonly Dictionary<string, Template> _templates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(Texture2D Bmp, bool AlphaTest), uint> _glTextures = new();   // one GL texture per (bitmap, alpha-tested): cutout foliage skips mipmaps to stop alpha erosion

    /// <summary>Every placed, mesh-resolvable object: its template, baked world matrix and object index.</summary>
    public readonly List<(string Tmpl, Matrix4x4 World, int ObjIndex)> Placements = new();

    /// <summary>Object indices in a hidden group: skipped by Draw and by Raycast, so a hidden thing is neither
    /// seen nor clickable. Set by the viewer when the groups change.</summary>
    public HashSet<int>? HiddenIndices;

    /// <summary>Object index -> the share of the sun and back light the game leaves an object with NO lightmap
    /// (EngineLight.StaticShare). Null, or no entry, is the whole light. Ignored for an instance drawn with a
    /// lightmap - the game does the same.</summary>
    public Dictionary<int, float>? LightShares;

    /// <summary>The radius the game samples the terrain with for this template (EngineLight.StaticRadius), or 0.</summary>
    public float StaticRadius(string tmpl)
        => _templates.TryGetValue(tmpl, out var t) ? RefractorForge.Render.EngineLight.StaticRadius(t.BbMin, t.BbMax) : 0f;

    /// <summary>Ephemeral overgrowth foliage instances (a VIEW of the .wst-generated trees; never saved), grouped
    /// by template so each VAO binds once. Independent of <see cref="Placements"/>, so Build/Rebuild don't clear it.
    /// Three layers, because the game draws three: overgrowth close up as real <c>_m1</c> objects, overgrowth far
    /// away batched as its <c>_m2</c> meshes, and undergrowth - which lives on a different clock (generated around
    /// the camera, so rebuilt as you move, while the trees only change when you change them).</summary>
    public enum GrowthLayer { OverNear = 0, OverFar = 1, Under = 2 }

    /// <summary>One growth instance to draw: which mesh, where, which overgrowth cell or undergrowth patch it
    /// belongs to (the game culls those whole), its radius, and how opaque (undergrowth fades by patch).</summary>
    public readonly record struct GrowthItem(string Mesh, Matrix4x4 World, int Group, float Radius, float Alpha);

    private readonly Dictionary<string, List<GrowthItem>>[] _growth =
    {
        new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase),
    };
    private readonly int[] _growthCount = new int[3];
    public int GrowthCount(GrowthLayer layer) => _growthCount[(int)layer];
    public int FoliageInstanceCount => Math.Max(_growthCount[0], _growthCount[1]);   // the same trees, near and far
    public int UndergrowthInstanceCount => _growthCount[2];

    // Per-instance baked object lightmaps: objIndex -> GL texture (the ObjectLightMaps/*.tga matched to that placement).
    // Sampled via the mesh's 2nd UV channel; bound per-instance in Draw when ShowLightmaps is on.
    private readonly Dictionary<int, uint> _instLightmap = new();
    private bool _haveLightmaps;
    public bool ShowLightmaps = true;
    /// <summary>Battlecraft's "View as Detail Mesh": draw every part in its flat material colour instead of its
    /// texture, which makes it far easier to read where objects sit relative to each other.</summary>
    public bool FlatShading = false;
    public int LightmapInstanceCount => _instLightmap.Count;

    /// <summary>A template's local-space bounding box (Battlecraft's "View as Bounding Box" overlay draws these).</summary>
    public bool TryGetTemplateBounds(string tmpl, out Vector3 min, out Vector3 max)
    {
        if (_templates.TryGetValue(tmpl, out var t)) { min = t.BbMin; max = t.BbMax; return true; }
        min = default; max = default; return false;
    }

    public int TemplateCount => _templates.Count;
    public int InstanceCount => Placements.Count;
    public int TextureCount => _glTextures.Count;

    /// <summary>Texture memory uploaded for objects, including mip chains — reported at load so a map whose art
    /// dwarfs everything else says so instead of quietly exhausting the GPU.</summary>
    public long TextureBytes { get; private set; }
    /// <summary>How much was saved by downscaling oversized art (0 when nothing needed it).</summary>
    public long TextureBytesSaved { get; private set; }

    /// <summary>Resolve each placed object to its baked ObjectLightMaps/*.tga (by template + world position) and record
    /// the matched lightmap as a per-instance GL texture (deduped by bitmap). Call after Build/Rebuild + a level load.</summary>
    public void SetObjectLightmaps(GL gl, RefractorForge.Render.ObjectLightmaps? lightmaps, StaticObjectsFile objects,
                                   RefractorForge.Render.MeshLibrary? meshLib = null)
    {
        _instLightmap.Clear();
        _haveLightmaps = false;
        if (lightmaps is null || lightmaps.Count == 0) return;
        var objs = objects.Objects;
        foreach (var (_, _, objIndex) in Placements)
        {
            if ((uint)objIndex >= (uint)objs.Count) continue;
            var o = objs[objIndex];
            var lm = lightmaps.Match(o.Template, o.Position.X, o.Position.Y, o.Position.Z);
            // The bake names a multi-part Bundle object's lightmap by its GEOMETRY mesh (landrep1_supply -> landrep1_m1),
            // not the placed template, so a template-name match misses. Fall back to the primary geometry name.
            if (lm is null && meshLib is not null)
            {
                var g = meshLib.PrimaryGeometryName(o.Template);
                if (!string.IsNullOrEmpty(g) && !g.Equals(o.Template, StringComparison.OrdinalIgnoreCase))
                    lm = lightmaps.Match(g, o.Position.X, o.Position.Y, o.Position.Z);
            }
            if (lm is null) continue;
            _instLightmap[objIndex] = GlTextureFor(gl, lm);   // GlTextureFor dedupes by Texture2D bitmap
        }
        _haveLightmaps = _instLightmap.Count > 0;
    }

    public void Sync(StaticObjectsFile objects)
    {
        var objs = objects.Objects;
        for (int i = 0; i < Placements.Count; i++)
        {
            var (tmpl, _, idx) = Placements[i];
            if ((uint)idx < (uint)objs.Count)
                Placements[i] = (tmpl, LevelScene.MeshWorld(objs[idx]), idx);
        }
    }

    /// <summary>Re-resolve the placement list against the current document IN PLACE, uploading only templates that
    /// aren't already on the GPU. Unlike <see cref="Build"/> (which re-uploads every template's geometry + textures —
    /// a multi-second stall at hundreds of objects), this reuses the cached GPU templates, so an add / delete / move
    /// is cheap. This is what keeps collaborative edits (and the local echo of them) from freezing the editor.</summary>
    public void Rebuild(GL gl, StaticObjectsFile objects, MeshLibrary lib)
    {
        Placements.Clear();
        var objs = objects.Objects;
        for (int i = 0; i < objs.Count; i++)
            if (lib.TryGetRenderMesh(objs[i].Template, out _))
                Placements.Add((objs[i].Template, LevelScene.MeshWorld(objs[i]), i));

        foreach (var name in Placements.Select(p => p.Tmpl).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_templates.ContainsKey(name)) continue;   // already on the GPU -> reuse (the expensive part is skipped)
            if (!lib.TryGetRenderMesh(name, out var mesh)) continue;
            UploadMesh(gl, name, mesh);                    // upload ONLY a genuinely new template (rare)
        }
    }

    public static GlObjects Build(GL gl, StaticObjectsFile objects, MeshLibrary lib)
    {
        var self = new GlObjects();
        var objs = objects.Objects;
        // A template renders if it assembles as a vehicle (full hull+turret+wheels / the whole car) OR resolves to a
        // single mesh. ASSEMBLED-FIRST (TryGetRenderMesh) so a placed vehicle shows its complete hierarchy and never a
        // low-detail single-mesh fallback — matching the model viewer (this is the Yamato/PrinceOW "placed = low LOD" fix).
        for (int i = 0; i < objs.Count; i++)
            if (lib.TryGetRenderMesh(objs[i].Template, out _))
                self.Placements.Add((objs[i].Template, LevelScene.MeshWorld(objs[i]), i));

        // One upload per unique template (the geometry + textures + lightmap UVs); UploadMesh is the single place the
        // interleaved vertex format (pos+normal+uv+lightmapUv) is built, shared with gameplay-body uploads.
        foreach (var name in self.Placements.Select(p => p.Tmpl).Distinct(StringComparer.OrdinalIgnoreCase))
            if (lib.TryGetRenderMesh(name, out var mesh)) self.UploadMesh(gl, name, mesh);
        return self;
    }

    private static void Bounds(Vector3[] pos, out Vector3 min, out Vector3 max)
    {
        min = new Vector3(float.MaxValue); max = new Vector3(float.MinValue);
        foreach (var p in pos) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        if (pos.Length == 0) { min = Vector3.Zero; max = Vector3.Zero; }
    }

    /// <summary>
    /// Ray-pick the nearest placed object by testing the ray against each placement's transformed
    /// bounding box. Returns the object index (into StaticObjectsFile.Objects) or -1. This selects on the
    /// object's actual geometry rather than a pivot point, so clicks land reliably.
    /// </summary>
    /// <summary>Ray-test one uploaded mesh at an arbitrary transform. Vehicle spawners are drawn from the gameplay
    /// layer rather than from <see cref="Placements"/>, so they need this to be clickable on the vehicle ITSELF
    /// instead of on a marker near its centre.</summary>
    public bool RaycastTemplate(string key, Matrix4x4 world, Vector3 rayOrigin, Vector3 rayDir, out float t)
    {
        t = float.MaxValue;
        if (!_templates.TryGetValue(key, out var tpl)) return false;
        if (!Matrix4x4.Invert(world, out var inv)) return false;
        var lo = Vector3.Transform(rayOrigin, inv);
        var ld = Vector3.TransformNormal(rayDir, inv);
        return RayAabb(lo, ld, tpl.BbMin, tpl.BbMax, out t, out _);
    }

    /// <summary>
    /// The object under a world ray, or -1. Each placement's bounding box is tested in its own local space
    /// (the ray parameter t survives the transform unchanged, so t is directly comparable in world units even
    /// when the placement is scaled).
    ///
    /// A box that ENCLOSES the ray's origin is treated as a last resort rather than an instant winner. The camera
    /// is routinely inside some large object's box — a hangar, a wall run, anything wide — and a slab test started
    /// at t = 0 hands that object every click in the level, including clicks on empty sky. Since the box is only a
    /// proxy for the mesh, being inside one says very little; anything the ray genuinely enters is the better
    /// answer, and among enclosing boxes the SMALLEST is the one you are most plausibly standing in.
    /// </summary>
    public int Raycast(Vector3 rayOrigin, Vector3 rayDir) => RaycastBoxes(PickBoxes(), rayOrigin, rayDir);

    /// <summary>One clickable box: a placement's local bounding box and the transform that puts it in the world.</summary>
    public readonly record struct PickBox(Matrix4x4 World, Vector3 BbMin, Vector3 BbMax, int Index);

    /// <summary>The visible, mesh-resolved placements as pick boxes. Separated from <see cref="RaycastBoxes"/> so
    /// the selection geometry can be tested without a GL context.</summary>
    private IEnumerable<PickBox> PickBoxes()
    {
        foreach (var (tmpl, world, objIndex) in Placements)
        {
            if (!_templates.TryGetValue(tmpl, out var t)) continue;
            if (HiddenIndices is not null && HiddenIndices.Contains(objIndex)) continue;   // hidden = not clickable
            yield return new PickBox(world, t.BbMin, t.BbMax, objIndex);
        }
    }

    public static int RaycastBoxes(IEnumerable<PickBox> boxes, Vector3 rayOrigin, Vector3 rayDir)
    {
        int best = -1; float bestT = float.MaxValue;
        int enclosing = -1; float enclosingVol = float.MaxValue;
        foreach (var b in boxes)
        {
            if (!Matrix4x4.Invert(b.World, out var inv)) continue;
            // Transform the ray into the box's local space and slab-test. The ray parameter t is unchanged by an
            // affine transform, so t stays in world units and stays comparable across differently-scaled placements.
            var lo = Vector3.Transform(rayOrigin, inv);
            var ld = Vector3.TransformNormal(rayDir, inv);
            if (!RayAabb(lo, ld, b.BbMin, b.BbMax, out float tHit, out bool inside)) continue;
            if (inside)
            {
                var s = b.BbMax - b.BbMin;
                float vol = MathF.Abs(s.X * s.Y * s.Z);
                if (vol < enclosingVol) { enclosingVol = vol; enclosing = b.Index; }
            }
            else if (tHit < bestT) { bestT = tHit; best = b.Index; }
        }
        return best >= 0 ? best : enclosing;
    }

    /// <param name="inside">The origin is within the box, so <paramref name="tHit"/> is 0 and means nothing.</param>
    private static bool RayAabb(Vector3 o, Vector3 d, Vector3 min, Vector3 max, out float tHit, out bool inside)
    {
        tHit = 0f;
        inside = true;
        float tmin = 0f, tmax = float.MaxValue;
        for (int a = 0; a < 3; a++)
        {
            float oa = a == 0 ? o.X : a == 1 ? o.Y : o.Z;
            float da = a == 0 ? d.X : a == 1 ? d.Y : d.Z;
            float lo = a == 0 ? min.X : a == 1 ? min.Y : min.Z;
            float hi = a == 0 ? max.X : a == 1 ? max.Y : max.Z;
            if (oa < lo || oa > hi) inside = false;
            if (MathF.Abs(da) < 1e-9f) { if (oa < lo || oa > hi) return false; }
            else
            {
                float inv = 1f / da;
                float t1 = (lo - oa) * inv, t2 = (hi - oa) * inv;
                if (t1 > t2) (t1, t2) = (t2, t1);
                tmin = MathF.Max(tmin, t1); tmax = MathF.Min(tmax, t2);
                if (tmin > tmax) return false;
            }
        }
        tHit = tmin;
        return true;
    }

    /// <summary>Render placed objects into the sun shadow-map (depth-only, position attribute only). Objects whose XZ is
    /// beyond <paramref name="cullRadius"/> of <paramref name="focusXZ"/> are skipped — they can't fall in the focused
    /// shadow frustum, and culling them keeps the depth pass fast on dense maps. Same VAOs as the colour pass; the depth
    /// program reads location 0 (position) and ignores the rest.</summary>
    public unsafe void DrawDepth(GL gl, uint depthProg, int uModelLoc, Vector2 focusXZ, float cullRadius)
    {
        float cull2 = cullRadius * cullRadius;
        foreach (var (tmpl, world, _) in Placements)
        {
            if (!_templates.TryGetValue(tmpl, out var t)) continue;
            var tr = world.Translation;
            float dx = tr.X - focusXZ.X, dz = tr.Z - focusXZ.Y;
            if (dx * dx + dz * dz > cull2) continue;
            Matrix4x4 m = world;
            gl.UniformMatrix4(uModelLoc, 1, false, (float*)&m);
            gl.BindVertexArray(t.Vao);
            foreach (var part in t.Parts)
                gl.DrawElements(PrimitiveType.Triangles, (uint)part.Count, DrawElementsType.UnsignedInt, (void*)((nint)part.Offset * sizeof(uint)));
        }
    }

    /// <summary>
    /// Draw every clickable placement painted a flat colour that encodes its object index — the pass behind
    /// <see cref="GlPick"/>. Whatever ends up in a pixel is, by construction, the object you can see there, so
    /// selection cannot disagree with the picture the way a separate ray test can.
    ///
    /// Alpha-tested parts discard the same texels they discard when drawn normally, so a fence, a railing or a
    /// palm frond is clickable on its bars and leaves and see-through everywhere else, exactly as it looks.
    /// Blended parts are left solid here: a canopy you can see is a canopy you should be able to click.
    /// </summary>
    public unsafe void DrawIds(GL gl, uint prog, int uMVP, int uId, int uUseTex, int uAlphaRef, Matrix4x4 viewProj)
    {
        gl.UseProgram(prog);
        gl.ActiveTexture(TextureUnit.Texture0);
        foreach (var (tmpl, world, objIndex) in Placements)
        {
            if (!_templates.TryGetValue(tmpl, out var t)) continue;
            if (HiddenIndices is not null && HiddenIndices.Contains(objIndex)) continue;   // hidden = not clickable
            Matrix4x4 mvpM = world * viewProj;
            gl.UniformMatrix4(uMVP, 1, false, (float*)&mvpM);
            // objIndex + 1, so a cleared (zero) pixel reads as "nothing here" rather than as object 0.
            int code = objIndex + 1;
            gl.Uniform4(uId, (code & 0xFF) / 255f, ((code >> 8) & 0xFF) / 255f, ((code >> 16) & 0xFF) / 255f, 1f);
            gl.BindVertexArray(t.Vao);
            foreach (var part in t.Parts)
            {
                bool cutout = part.AlphaTest && part.Tex != 0;
                gl.Uniform1(uUseTex, cutout ? 1 : 0);
                if (cutout)
                {
                    gl.Uniform1(uAlphaRef, part.AlphaRef ?? (part.Foliage ? 0.33f : 0.5f));
                    gl.BindTexture(TextureTarget.Texture2D, part.Tex);
                }
                gl.DrawElements(PrimitiveType.Triangles, (uint)part.Count, DrawElementsType.UnsignedInt, (void*)((nint)part.Offset * sizeof(uint)));
            }
        }
    }

    public unsafe void Draw(GL gl, uint prog, int uMVP, int uModel, int uColor, int uUseTex, int uAlphaTest, int uTint,
                            Matrix4x4 viewProj, IReadOnlySet<int> selectedSet, int primaryIndex, Vector3 highlightTint,
                            IReadOnlySet<int>? lockedSet = null)
    {
        var secondary = Vector3.Lerp(Vector3.One, highlightTint, 0.55f);   // dimmer tint for non-primary selection
        // Battlecraft tints locked objects red so you can see at a glance what will refuse to move. Selection still
        // shows through as a brighter red, otherwise you could not tell which of the locked ones you had picked.
        var lockedTint = new Vector3(1.6f, 0.42f, 0.38f);
        var lockedSelTint = new Vector3(2.1f, 0.72f, 0.62f);
        gl.UseProgram(prog);
        gl.ActiveTexture(TextureUnit.Texture0);
        // Per-object baked lightmaps go on texture unit 1; sampled only for instances that have one (uHasLightmap==1).
        int uHasLm = gl.GetUniformLocation(prog, "uHasLightmap");
        int uLm = gl.GetUniformLocation(prog, "uLightmap");
        int uVertexLit = gl.GetUniformLocation(prog, "uVertexLit");
        int uLightCut = gl.GetUniformLocation(prog, "uLightCut");
        int uMatDif = gl.GetUniformLocation(prog, "uMatDif"), uSelfIllum = gl.GetUniformLocation(prog, "uSelfIllum");
        if (uLm >= 0) gl.Uniform1(uLm, 1);
        bool lmOn = ShowLightmaps && _haveLightmaps && uHasLm >= 0;
        foreach (var (tmpl, world, objIndex) in Placements)
        {
            if (!_templates.TryGetValue(tmpl, out var t)) continue;
            if (HiddenIndices is not null && HiddenIndices.Contains(objIndex)) continue;   // in a hidden group
            // Upload the matrices straight from their memory (Matrix4x4 is 16 sequential floats in MVP order)
            // — no per-object float[] allocation, which at hundreds of objects was the GC-stutter source.
            Matrix4x4 mvpM = world * viewProj, modelM = world;
            gl.UniformMatrix4(uMVP, 1, false, (float*)&mvpM);
            gl.UniformMatrix4(uModel, 1, false, (float*)&modelM);
            bool isPrimary = objIndex == primaryIndex;
            bool isSel = isPrimary || (selectedSet is not null && selectedSet.Contains(objIndex));
            bool isLocked = lockedSet is not null && lockedSet.Contains(objIndex);
            Vector3 tint = isLocked ? (isSel ? lockedSelTint : lockedTint)
                         : isPrimary ? highlightTint : isSel ? secondary : Vector3.One;
            gl.Uniform3(uTint, tint.X, tint.Y, tint.Z);
            if (uHasLm >= 0)
            {
                if (lmOn && _instLightmap.TryGetValue(objIndex, out var lmTex))
                {
                    gl.ActiveTexture(TextureUnit.Texture1);
                    gl.BindTexture(TextureTarget.Texture2D, lmTex);
                    gl.ActiveTexture(TextureUnit.Texture0);
                    gl.Uniform1(uHasLm, 1);
                }
                else gl.Uniform1(uHasLm, 0);
            }
            if (uVertexLit >= 0) gl.Uniform1(uVertexLit, t.VertexLit ? 1 : 0);
            if (uLightCut >= 0)
                gl.Uniform1(uLightCut, LightShares is not null && !_instLightmap.ContainsKey(objIndex) && LightShares.TryGetValue(objIndex, out float share) ? 1f - share : 0f);
            gl.BindVertexArray(t.Vao);
            foreach (var part in t.Parts)
            {
                if (uMatDif >= 0) gl.Uniform3(uMatDif, part.MatDif.X, part.MatDif.Y, part.MatDif.Z);
                if (uSelfIllum >= 0) gl.Uniform3(uSelfIllum, part.SelfIllum.X, part.SelfIllum.Y, part.SelfIllum.Z);
                if (part.Tex != 0 && !FlatShading)
                {
                    gl.BindTexture(TextureTarget.Texture2D, part.Tex);
                    gl.Uniform1(uUseTex, 1);
                    SetAlpha(gl, prog, uAlphaTest, part);
                }
                else
                {
                    gl.Uniform1(uUseTex, 0);
                    gl.Uniform1(uAlphaTest, 0); gl.DepthMask(true);
                    gl.Uniform3(uColor, part.Color.X, part.Color.Y, part.Color.Z);
                }
                gl.DrawElements(PrimitiveType.Triangles, (uint)part.Count, DrawElementsType.UnsignedInt, (void*)((nint)part.Offset * sizeof(uint)));
            }
        }
            // Everything else that shares this program (growth, the model viewer, markers) is an ordinary material.
            if (uMatDif >= 0) gl.Uniform3(uMatDif, 1f, 1f, 1f);
            if (uSelfIllum >= 0) gl.Uniform3(uSelfIllum, 0f, 0f, 0f);
            if (uVertexLit >= 0) gl.Uniform1(uVertexLit, 0);   // growth, the model viewer and markers share this program
            if (uLightCut >= 0) gl.Uniform1(uLightCut, 0f);
            gl.DepthMask(true);   // a depthWrite-false material must not leak out of this pass
    }

    /// <summary>Replace one growth layer with these instances, uploading any mesh not already on the GPU and skipping
    /// ones that do not resolve. Reuses the shared template cache, so a tree mesh shared with a placed object is
    /// uploaded once. Rebuilding a layer uploads nothing per instance, so the undergrowth can follow the camera.</summary>
    public void SetGrowth(GL gl, GrowthLayer layer, IReadOnlyList<GrowthItem> items, MeshLibrary lib)
    {
        var bucket = _growth[(int)layer];
        bucket.Clear();
        int n = 0;
        foreach (var it in items)
        {
            if (!_templates.ContainsKey(it.Mesh))
            {
                if ((!lib.TryGet(it.Mesh, out var mesh) || mesh is null) && (!lib.TryGetAssembledMesh(it.Mesh, out mesh) || mesh is null)) continue;
                UploadMesh(gl, it.Mesh, mesh);
            }
            if (!bucket.TryGetValue(it.Mesh, out var lst)) { lst = new List<GrowthItem>(); bucket[it.Mesh] = lst; }
            lst.Add(it);
            n++;
        }
        _growthCount[(int)layer] = n;
    }

    public void ClearFoliage()
    {
        foreach (var b in _growth) b.Clear();
        Array.Clear(_growthCount);
    }

    /// <summary>How a growth layer is shaded. <see cref="GrowthShade.Lit"/> is the ordinary object shading. The
    /// game draws undergrowth UNLIT instead (RaShaderUndergrowth.fx, fixed function): colour =
    /// saturate(4 x atlas x the terrain colour under it), alpha = atlas alpha x the patch's fade, alpha-tested at
    /// min(fade, 100/255) and blended over what is behind.</summary>
    public enum GrowthShade { Lit = 0, Undergrowth = 1 }

    /// <summary>Draw a growth layer. An instance is drawn when its distance from the camera lies in
    /// [<paramref name="minDist"/> + its radius, <paramref name="maxDist"/> + its radius) and its group passes
    /// <paramref name="groupVisible"/> - the game culls overgrowth by whole cell. <paramref name="overrideTex"/>, when
    /// not 0, replaces every part's texture: the game draws all far overgrowth with the level's overGrowth.rs atlas and
    /// all undergrowth with its underGrowth.rs atlas, whatever the meshes' own shaders name.</summary>
    public unsafe void DrawGrowth(GL gl, GrowthLayer layer, uint prog, int uMVP, int uModel, int uColor, int uUseTex, int uAlphaTest, int uTint,
                                  Matrix4x4 viewProj, Vector3 camPos, float minDist, float maxDist, Func<int, bool>? groupVisible,
                                  uint overrideTex, GrowthShade shade)
    {
        var bucket = _growth[(int)layer];
        if (bucket.Count == 0) return;
        gl.UseProgram(prog);
        gl.ActiveTexture(TextureUnit.Texture0);
        { int u = gl.GetUniformLocation(prog, "uHasLightmap"); if (u >= 0) gl.Uniform1(u, 0); }   // foliage has no lightmap
        int uMode = gl.GetUniformLocation(prog, "uGrowthMode"), uAlpha = gl.GetUniformLocation(prog, "uGrowthAlpha");
        if (uMode >= 0) gl.Uniform1(uMode, (int)shade);
        gl.Uniform3(uTint, 1f, 1f, 1f);
        bool under = shade == GrowthShade.Undergrowth;
        if (under)
        {
            gl.Enable(EnableCap.Blend);
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        }
        foreach (var (tmpl, items) in bucket)
        {
            if (!_templates.TryGetValue(tmpl, out var t)) continue;
            gl.BindVertexArray(t.Vao);
            foreach (var it in items)
            {
                if (groupVisible is not null && !groupVisible(it.Group)) continue;
                var tr = it.World.Translation;
                float dx = tr.X - camPos.X, dy = tr.Y - camPos.Y, dz = tr.Z - camPos.Z;
                float d2 = dx * dx + dy * dy + dz * dz;
                float lo = minDist > 0f ? minDist + it.Radius : 0f, hi = maxDist + it.Radius;
                if (d2 < lo * lo || d2 >= hi * hi) continue;
                Matrix4x4 mvpM = it.World * viewProj, modelM = it.World;
                gl.UniformMatrix4(uMVP, 1, false, (float*)&mvpM);
                gl.UniformMatrix4(uModel, 1, false, (float*)&modelM);
                if (uAlpha >= 0) gl.Uniform1(uAlpha, it.Alpha);
                foreach (var part in t.Parts)
                {
                    uint tex = overrideTex != 0 ? overrideTex : part.Tex;
                    if (tex != 0)
                    {
                        gl.BindTexture(TextureTarget.Texture2D, tex);
                        gl.Uniform1(uUseTex, 1);
                        SetAlpha(gl, prog, uAlphaTest, part);
                        if (under) gl.DepthMask(true);   // undergrowth writes depth (ZWriteEnable defaults on)
                    }
                    else
                    {
                        gl.Uniform1(uUseTex, 0);
                        gl.Uniform1(uAlphaTest, 0); gl.DepthMask(true);
                        gl.Uniform3(uColor, part.Color.X, part.Color.Y, part.Color.Z);
                    }
                    gl.DrawElements(PrimitiveType.Triangles, (uint)part.Count, DrawElementsType.UnsignedInt, (void*)((nint)part.Offset * sizeof(uint)));
                }
            }
        }
        gl.DepthMask(true);   // a depthWrite-false material must not leak out of this pass
        if (uMode >= 0) gl.Uniform1(uMode, 0);
        if (under) gl.Disable(EnableCap.Blend);
    }

    /// <summary>Upload a mesh under a cache key (once), returning its template. Used for gameplay-spawn
    /// bodies (vehicles) that aren't part of the StaticObjects placement set.</summary>
    private Template UploadMesh(GL gl, string key, MeshLibrary.Mesh mesh)
    {
        if (_templates.TryGetValue(key, out var existing)) return existing;
        var pos = mesh.Positions;
        var nrm = new Vector3[pos.Length];
        foreach (var part in mesh.Parts)
            for (int t = 0; t + 2 < part.Indices.Length; t += 3)
            {
                int a = part.Indices[t], b = part.Indices[t + 1], c = part.Indices[t + 2];
                var fn = Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]);
                nrm[a] += fn; nrm[b] += fn; nrm[c] += fn;
            }
        // interleave position(3) + normal(3) + uv(2) + lightmap-uv(2). The 2nd UV is (0,0) when the mesh has none;
        // the shader only samples the lightmap when a per-instance lightmap texture is bound (uHasLightmap==1).
        var lm = mesh.LightmapUvs;
        var verts = new float[pos.Length * 10];
        for (int i = 0; i < pos.Length; i++)
        {
            var p = pos[i];
            var n = nrm[i].LengthSquared() > 1e-12f ? Vector3.Normalize(nrm[i]) : Vector3.UnitY;
            var uv = i < mesh.Uvs.Length ? mesh.Uvs[i] : Vector2.Zero;
            var l = (lm is not null && i < lm.Length) ? lm[i] : Vector2.Zero;
            int o = i * 10;
            verts[o] = p.X; verts[o + 1] = p.Y; verts[o + 2] = p.Z;
            verts[o + 3] = n.X; verts[o + 4] = n.Y; verts[o + 5] = n.Z;
            verts[o + 6] = uv.X; verts[o + 7] = uv.Y;
            verts[o + 8] = l.X; verts[o + 9] = l.Y;
        }
        var allIdx = new List<uint>();
        var parts = new List<Part>();
        foreach (var part in mesh.Parts)
        {
            int off = allIdx.Count;
            foreach (var ix in part.Indices) allIdx.Add((uint)ix);
            uint tex = part.Texture is { } bmp ? GlTextureFor(gl, bmp, part.AlphaTest) : 0u;
            parts.Add(new Part { Offset = off, Count = part.Indices.Length, Color = part.Color, Tex = tex, AlphaTest = part.AlphaTest, Blend = part.Blend, AlphaRef = part.AlphaRef, Foliage = part.Foliage, Opacity = part.Opacity, DepthWrite = part.DepthWrite, MatDif = part.Diffuse, SelfIllum = part.SelfIllum });
        }
        // Blended parts go LAST, whatever order the mesh listed them in. The assault Huey puts its canopy FIRST,
        // so the glass was composited against the sky and the opaque fuselage then drew straight over it - and,
        // before depthWrite was honoured, the fuselage was culled by the glass's own depth and never drew at all.
        // OrderBy is a stable sort, so everything else keeps its authored order.
        var ordered = parts.OrderBy(p => p.Blend ? 1 : 0).ToArray();
        Bounds(pos, out var bbMin, out var bbMax);
        var tpl = new Template { Vao = MakeMesh(gl, verts, allIdx.ToArray()), Parts = ordered, BbMin = bbMin, BbMax = bbMax, VertexLit = mesh.VertexLit };
        _templates[key] = tpl;
        return tpl;
    }

    /// <summary>Draw a single resolved mesh at a world matrix (e.g. a vehicle body at its spawn).
    /// Builds + caches the GPU template on first use under <paramref name="key"/>.</summary>
    public unsafe void DrawMesh(GL gl, uint prog, int uMVP, int uModel, int uColor, int uUseTex, int uAlphaTest, int uTint,
                                Matrix4x4 viewProj, string key, MeshLibrary.Mesh mesh, Matrix4x4 world, Vector3 tint, Vector3? solidColor = null)
    {
        var t = UploadMesh(gl, key, mesh);
        gl.UseProgram(prog);
        gl.ActiveTexture(TextureUnit.Texture0);
        { int u = gl.GetUniformLocation(prog, "uHasLightmap"); if (u >= 0) gl.Uniform1(u, 0); }   // model-viewer: dynamic light, no lightmap
        Matrix4x4 mvpM = world * viewProj, modelM = world;
        gl.UniformMatrix4(uMVP, 1, false, (float*)&mvpM);
        gl.UniformMatrix4(uModel, 1, false, (float*)&modelM);
        gl.Uniform3(uTint, tint.X, tint.Y, tint.Z);
        gl.BindVertexArray(t.Vao);
        foreach (var part in t.Parts)
        {
            if (solidColor is Vector3 sc)   // flat untextured colour for every part (e.g. a neutral white flag cloth)
            {
                gl.Uniform1(uUseTex, 0);
                gl.Uniform1(uAlphaTest, 0); gl.DepthMask(true);
                gl.Uniform3(uColor, sc.X, sc.Y, sc.Z);
            }
            else if (part.Tex != 0)
            {
                gl.BindTexture(TextureTarget.Texture2D, part.Tex);
                gl.Uniform1(uUseTex, 1);
                SetAlpha(gl, prog, uAlphaTest, part);
            }
            else
            {
                gl.Uniform1(uUseTex, 0);
                gl.Uniform1(uAlphaTest, 0); gl.DepthMask(true);
                gl.Uniform3(uColor, part.Color.X, part.Color.Y, part.Color.Z);
            }
            gl.DrawElements(PrimitiveType.Triangles, (uint)part.Count, DrawElementsType.UnsignedInt, (void*)((nint)part.Offset * sizeof(uint)));
        }
            gl.DepthMask(true);   // a depthWrite-false material must not leak out of this pass
    }

    /// <summary>Largest object texture uploaded, or 0 to use the map's own resolution (the default). Object
    /// textures are the biggest thing the editor hands the GPU - a couple of hundred distinct 2048-4096 textures
    /// runs to several GB with mip chains - so on a GPU sharing system memory this is the dial that decides whether
    /// a remastered map draws at all. Nothing on disk is touched; this only affects what is uploaded.</summary>
    public static int MaxObjectTexture { get; set; } = 0;

    /// <summary>Box-filter a texture down so its longest side is <paramref name="max"/>.</summary>
    private static Texture2D Downscale(Texture2D s, int max)
    {
        int w = s.Width, h = s.Height;
        float k = max / (float)Math.Max(w, h);
        int nw = Math.Max(1, (int)MathF.Round(w * k)), nh = Math.Max(1, (int)MathF.Round(h * k));
        var rgba = new byte[nw * nh * 4];
        for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++)
            {
                var c = s.SampleRGBA((x + 0.5f) / nw, (y + 0.5f) / nh);
                int o = (y * nw + x) * 4;
                rgba[o] = (byte)(c.X * 255f); rgba[o + 1] = (byte)(c.Y * 255f);
                rgba[o + 2] = (byte)(c.Z * 255f); rgba[o + 3] = (byte)(c.W * 255f);
            }
        return new Texture2D(nw, nh, rgba);
    }

    private unsafe uint GlTextureFor(GL gl, Texture2D t, bool alphaTest = false)
    {
        // Keyed on the ORIGINAL texture, so a downscale happens once per source rather than once per use.
        var key = (t, alphaTest);
        if (_glTextures.TryGetValue(key, out var id)) return id;

        long native = (long)t.Width * t.Height * 4;
        if (MaxObjectTexture > 0 && (t.Width > MaxObjectTexture || t.Height > MaxObjectTexture))
            t = Downscale(t, MaxObjectTexture);
        long used = (long)t.Width * t.Height * 4;
        // Mip chains add about a third on top; alpha-tested foliage skips them (see below).
        TextureBytes += alphaTest ? used : used * 4 / 3;
        TextureBytesSaved += alphaTest ? native - used : (native - used) * 4 / 3;

        id = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, id);
        fixed (byte* p = t.Rgba)
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)t.Width, (uint)t.Height, 0,
                          PixelFormat.Rgba, PixelType.UnsignedByte, p);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.Repeat);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.Repeat);
        // Alpha-tested foliage SKIPS mipmaps: box-filtered mips average the leaf-card alpha toward 0, so distant trees
        // lose their canopy under the shader's hard alpha cutoff (the main "bald trees at range" cause). Linear (no mips)
        // keeps coverage at distance — minor aliasing, the normal foliage trade-off. Opaque meshes keep trilinear mips.
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)(alphaTest ? GLEnum.Linear : GLEnum.LinearMipmapLinear));
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        if (!alphaTest) gl.GenerateMipmap(TextureTarget.Texture2D);
        _glTextures[key] = id;
        return id;
    }

    private static unsafe uint MakeMesh(GL gl, float[] verts, uint[] indices)
    {
        uint vao = gl.GenVertexArray(); gl.BindVertexArray(vao);
        uint vbo = gl.GenBuffer(); gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, verts, BufferUsageARB.StaticDraw);
        uint stride = 10 * (uint)sizeof(float);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
        gl.EnableVertexAttribArray(2);
        gl.VertexAttribPointer(3, 2, VertexAttribPointerType.Float, false, stride, (void*)(8 * sizeof(float)));   // lightmap uv
        gl.EnableVertexAttribArray(3);
        uint ebo = gl.GenBuffer(); gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, indices, BufferUsageARB.StaticDraw);
        return vao;
    }
}
