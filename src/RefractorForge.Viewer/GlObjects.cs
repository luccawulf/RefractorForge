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
sealed class GlObjects
{
    private struct Part { public int Offset; public int Count; public Vector3 Color; public uint Tex; public bool AlphaTest; public bool Blend; }
    private sealed class Template { public uint Vao; public Part[] Parts = Array.Empty<Part>(); public Vector3 BbMin; public Vector3 BbMax; }

    private readonly Dictionary<string, Template> _templates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(Texture2D Bmp, bool AlphaTest), uint> _glTextures = new();   // one GL texture per (bitmap, alpha-tested): cutout foliage skips mipmaps to stop alpha erosion

    /// <summary>Every placed, mesh-resolvable object: its template, baked world matrix and object index.</summary>
    public readonly List<(string Tmpl, Matrix4x4 World, int ObjIndex)> Placements = new();

    /// <summary>Ephemeral overgrowth foliage instances (a VIEW of the .wst-generated trees; never saved), grouped
    /// by template so each VAO binds once. Independent of <see cref="Placements"/>, so Build/Rebuild don't clear it.</summary>
    private readonly Dictionary<string, List<Matrix4x4>> _foliage = new(StringComparer.OrdinalIgnoreCase);
    public int FoliageInstanceCount { get; private set; }

    // Per-instance baked object lightmaps: objIndex -> GL texture (the ObjectLightMaps/*.tga matched to that placement).
    // Sampled via the mesh's 2nd UV channel; bound per-instance in Draw when ShowLightmaps is on.
    private readonly Dictionary<int, uint> _instLightmap = new();
    private bool _haveLightmaps;
    public bool ShowLightmaps = true;
    public int LightmapInstanceCount => _instLightmap.Count;

    public int TemplateCount => _templates.Count;
    public int InstanceCount => Placements.Count;
    public int TextureCount => _glTextures.Count;

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
    public int Raycast(Vector3 rayOrigin, Vector3 rayDir)
    {
        int best = -1; float bestT = float.MaxValue;
        foreach (var (tmpl, world, objIndex) in Placements)
        {
            if (!_templates.TryGetValue(tmpl, out var t)) continue;
            if (!Matrix4x4.Invert(world, out var inv)) continue;
            // Transform the ray into the object's local space and slab-test against the local AABB.
            var lo = Vector3.Transform(rayOrigin, inv);
            var ld = Vector3.TransformNormal(rayDir, inv);
            if (RayAabb(lo, ld, t.BbMin, t.BbMax, out float tHit) && tHit < bestT) { bestT = tHit; best = objIndex; }
        }
        return best;
    }

    private static bool RayAabb(Vector3 o, Vector3 d, Vector3 min, Vector3 max, out float tHit)
    {
        tHit = 0f;
        float tmin = 0f, tmax = float.MaxValue;
        for (int a = 0; a < 3; a++)
        {
            float oa = a == 0 ? o.X : a == 1 ? o.Y : o.Z;
            float da = a == 0 ? d.X : a == 1 ? d.Y : d.Z;
            float lo = a == 0 ? min.X : a == 1 ? min.Y : min.Z;
            float hi = a == 0 ? max.X : a == 1 ? max.Y : max.Z;
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

    public unsafe void Draw(GL gl, uint prog, int uMVP, int uModel, int uColor, int uUseTex, int uAlphaTest, int uTint,
                            Matrix4x4 viewProj, IReadOnlySet<int> selectedSet, int primaryIndex, Vector3 highlightTint)
    {
        var secondary = Vector3.Lerp(Vector3.One, highlightTint, 0.55f);   // dimmer tint for non-primary selection
        gl.UseProgram(prog);
        gl.ActiveTexture(TextureUnit.Texture0);
        // Per-object baked lightmaps go on texture unit 1; sampled only for instances that have one (uHasLightmap==1).
        int uHasLm = gl.GetUniformLocation(prog, "uHasLightmap");
        int uLm = gl.GetUniformLocation(prog, "uLightmap");
        if (uLm >= 0) gl.Uniform1(uLm, 1);
        bool lmOn = ShowLightmaps && _haveLightmaps && uHasLm >= 0;
        foreach (var (tmpl, world, objIndex) in Placements)
        {
            if (!_templates.TryGetValue(tmpl, out var t)) continue;
            // Upload the matrices straight from their memory (Matrix4x4 is 16 sequential floats in MVP order)
            // — no per-object float[] allocation, which at hundreds of objects was the GC-stutter source.
            Matrix4x4 mvpM = world * viewProj, modelM = world;
            gl.UniformMatrix4(uMVP, 1, false, (float*)&mvpM);
            gl.UniformMatrix4(uModel, 1, false, (float*)&modelM);
            bool isPrimary = objIndex == primaryIndex;
            bool isSel = isPrimary || (selectedSet is not null && selectedSet.Contains(objIndex));
            Vector3 tint = isPrimary ? highlightTint : isSel ? secondary : Vector3.One;
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
            gl.BindVertexArray(t.Vao);
            foreach (var part in t.Parts)
            {
                if (part.Tex != 0)
                {
                    gl.BindTexture(TextureTarget.Texture2D, part.Tex);
                    gl.Uniform1(uUseTex, 1);
                    gl.Uniform1(uAlphaTest, part.Blend ? 2 : (part.AlphaTest ? 1 : 0));   // 2 = soft-blend glass, 1 = cutout
                }
                else
                {
                    gl.Uniform1(uUseTex, 0);
                    gl.Uniform1(uAlphaTest, 0);
                    gl.Uniform3(uColor, part.Color.X, part.Color.Y, part.Color.Z);
                }
                gl.DrawElements(PrimitiveType.Triangles, (uint)part.Count, DrawElementsType.UnsignedInt, (void*)((nint)part.Offset * sizeof(uint)));
            }
        }
    }

    /// <summary>Replace the overgrowth-foliage overlay with these (template, world) instances, uploading any
    /// template not already on the GPU and skipping ones that don't resolve. Reuses the shared template cache, so
    /// a tree mesh shared with a placed object is uploaded once.</summary>
    public void SetFoliage(GL gl, IReadOnlyList<(string Tmpl, Matrix4x4 World)> instances, MeshLibrary lib)
    {
        _foliage.Clear();
        FoliageInstanceCount = 0;
        foreach (var (tmpl, world) in instances)
        {
            if (!_templates.ContainsKey(tmpl))
            {
                if ((!lib.TryGet(tmpl, out var mesh) || mesh is null) && (!lib.TryGetAssembledMesh(tmpl, out mesh) || mesh is null)) continue;
                UploadMesh(gl, tmpl, mesh);
            }
            if (!_foliage.TryGetValue(tmpl, out var lst)) { lst = new List<Matrix4x4>(); _foliage[tmpl] = lst; }
            lst.Add(world);
            FoliageInstanceCount++;
        }
    }

    public void ClearFoliage() { _foliage.Clear(); FoliageInstanceCount = 0; }

    /// <summary>Draw the foliage overlay with a hard distance cull (instances farther than <paramref name="cullDist"/>
    /// from the camera are skipped) so a dense map stays interactive. No selection/tint; per-template VAO binds once.
    /// (Phase 1: distance-culled per-instance draw — GPU instancing is a future optimisation.)</summary>
    public unsafe void DrawFoliage(GL gl, uint prog, int uMVP, int uModel, int uColor, int uUseTex, int uAlphaTest, int uTint,
                                   Matrix4x4 viewProj, Vector3 camPos, float cullDist)
    {
        if (_foliage.Count == 0) return;
        float cull2 = cullDist * cullDist;
        gl.UseProgram(prog);
        gl.ActiveTexture(TextureUnit.Texture0);
        { int u = gl.GetUniformLocation(prog, "uHasLightmap"); if (u >= 0) gl.Uniform1(u, 0); }   // foliage has no lightmap
        gl.Uniform3(uTint, 1f, 1f, 1f);
        foreach (var (tmpl, worlds) in _foliage)
        {
            if (!_templates.TryGetValue(tmpl, out var t)) continue;
            gl.BindVertexArray(t.Vao);
            foreach (var world in worlds)
            {
                var tr = world.Translation;
                float dx = tr.X - camPos.X, dy = tr.Y - camPos.Y, dz = tr.Z - camPos.Z;
                if (dx * dx + dy * dy + dz * dz > cull2) continue;
                Matrix4x4 mvpM = world * viewProj, modelM = world;
                gl.UniformMatrix4(uMVP, 1, false, (float*)&mvpM);
                gl.UniformMatrix4(uModel, 1, false, (float*)&modelM);
                foreach (var part in t.Parts)
                {
                    if (part.Tex != 0)
                    {
                        gl.BindTexture(TextureTarget.Texture2D, part.Tex);
                        gl.Uniform1(uUseTex, 1);
                        gl.Uniform1(uAlphaTest, part.Blend ? 2 : (part.AlphaTest ? 1 : 0));   // 2 = soft-blend glass, 1 = cutout
                    }
                    else
                    {
                        gl.Uniform1(uUseTex, 0);
                        gl.Uniform1(uAlphaTest, 0);
                        gl.Uniform3(uColor, part.Color.X, part.Color.Y, part.Color.Z);
                    }
                    gl.DrawElements(PrimitiveType.Triangles, (uint)part.Count, DrawElementsType.UnsignedInt, (void*)((nint)part.Offset * sizeof(uint)));
                }
            }
        }
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
            parts.Add(new Part { Offset = off, Count = part.Indices.Length, Color = part.Color, Tex = tex, AlphaTest = part.AlphaTest, Blend = part.Blend });
        }
        Bounds(pos, out var bbMin, out var bbMax);
        var tpl = new Template { Vao = MakeMesh(gl, verts, allIdx.ToArray()), Parts = parts.ToArray(), BbMin = bbMin, BbMax = bbMax };
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
                gl.Uniform1(uAlphaTest, 0);
                gl.Uniform3(uColor, sc.X, sc.Y, sc.Z);
            }
            else if (part.Tex != 0)
            {
                gl.BindTexture(TextureTarget.Texture2D, part.Tex);
                gl.Uniform1(uUseTex, 1);
                gl.Uniform1(uAlphaTest, part.Blend ? 2 : (part.AlphaTest ? 1 : 0));   // 2 = soft-blend glass, 1 = cutout
            }
            else
            {
                gl.Uniform1(uUseTex, 0);
                gl.Uniform1(uAlphaTest, 0);
                gl.Uniform3(uColor, part.Color.X, part.Color.Y, part.Color.Z);
            }
            gl.DrawElements(PrimitiveType.Triangles, (uint)part.Count, DrawElementsType.UnsignedInt, (void*)((nint)part.Offset * sizeof(uint)));
        }
    }

    private unsafe uint GlTextureFor(GL gl, Texture2D t, bool alphaTest = false)
    {
        var key = (t, alphaTest);
        if (_glTextures.TryGetValue(key, out var id)) return id;
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
