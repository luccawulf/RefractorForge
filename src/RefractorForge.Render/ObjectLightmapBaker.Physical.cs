using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using RefractorForge.Formats.Terrain;

namespace RefractorForge.Render;

public static partial class ObjectLightmapBaker
{
    /// <summary>
    /// What a traced bake works from: the level to trace in, the light to trace, and the engine colours the result
    /// is solved against.
    /// </summary>
    /// <param name="Scene">The whole level - terrain, objects, cut-outs (see <see cref="LightScene"/>). The object
    /// being baked is in it; it needs no separate copy of itself.</param>
    /// <param name="Day">The sun and the sky.</param>
    /// <param name="Colours">The level's Init.con light colours AS THEY WILL BE IN THE GAME. The map is the inverse
    /// of the engine's formula for exactly these values, so baking against one set and shipping another is the one
    /// way to get this wrong.</param>
    /// <param name="Budget">Rays per TEXEL. They are shared out over the texel's sub-samples.</param>
    /// <param name="DenoiseIterations">Passes of the edge-aware filter over the sky and bounce light. The sun's
    /// shadow is never filtered.</param>
    /// <param name="SurfaceOffset">How far off the surface a sample starts, in metres. A far LOD's surface does not
    /// lie exactly on the near LOD the scene holds, so its samples start further out.</param>
    /// <param name="SoftStride">The sky and bounce light is traced on every <c>SoftStride</c>-th texel each way and
    /// filled in between. It is smooth light - nothing in it is as fine as a texel - and it is nine tenths of the
    /// rays, so 2 makes a bake about three times faster for a difference that is hard to find. 1 traces every texel.</param>
    /// <param name="VertexLit">The mesh has no tangent frame, so the game clamps the two lights' sum to 1 before the
    /// lightmap scales it (<see cref="EngineLight.Material.VertexLit"/>); the map is solved against that.</param>
    public sealed record Physical(RayScene Scene, DaylightModel Day, EngineLight.Colours Colours, LightTracer.Budget Budget,
                                  int DenoiseIterations = 2, float SurfaceOffset = 0.01f, int SoftStride = 2, bool VertexLit = false)
    {
        /// <summary>Solve for BATTLEFIELD 1942. That game draws a lightmapped object as texture x 2 x lightmap, per
        /// channel, and adds no light of its own (<see cref="EngineLight.Bf1942"/>, measured), so the map is simply
        /// half of what the screen should show, in colour. <see cref="Colours"/> plays no part.</summary>
        public bool Bf1942 { get; init; }
    }

    /// <summary>A traced lightmap and how well the engine can show what was traced.</summary>
    /// <param name="Map">The lightmap: the same value in every channel, as the grey palette wants it.</param>
    /// <param name="Mean">The mean lightmap value over the covered texels, 0..1.</param>
    /// <param name="AtFloor">The share of texels whose light is darker than the engine's ambient floor - drawn at
    /// the floor, brighter than they should be.</param>
    /// <param name="AtCeiling">The share whose light is more than both engine lights at full can give - drawn
    /// darker than they should be. On a face neither light reaches (a ceiling) every texel above the floor is here.</param>
    /// <param name="MeanError">Mean distance, in display luma, between what was traced and what the engine will draw.</param>
    public sealed record PhysicalMap(Texture2D Map, float Mean, float AtFloor, float AtCeiling, float MeanError)
    {
        /// <summary>The map carries colour - it was solved per channel for a mesh the game lights per vertex - and
        /// must be written 24-bit, not through the grey palette.</summary>
        public bool Colour { get; init; }
    }

    /// <summary>
    /// Bake one object's lightmap by tracing the light that reaches it and solving for the value that makes the game
    /// draw that light.
    ///
    /// <para>The classic bake writes "can this texel see the sun". This one asks how much light arrives - sun, sky
    /// and what both bounce off the ground and the buildings around - turns that into what the screen should show,
    /// and inverts the engine's own formula (<see cref="EngineLight.SolvePrelight(Vector3, Vector3, Vector3, in EngineLight.Colours)"/>)
    /// to find the map value that gets it there. So a wall in open shade is written bright enough for the engine's
    /// back light to lift it to the brightness of shade, a room's far corner is written near zero, and a sunlit
    /// texel is written at whatever puts sun plus sky plus bounce on screen - not always 1.</para>
    ///
    /// <para>Light is averaged per texel in LINEAR units before the solve, so a texel half in shadow is half as lit,
    /// not the average of two solved values. The smooth part - sky and bounce - is denoised; the sun's visibility
    /// is kept apart and never filtered, because its edge is the one sharp thing in the picture.</para>
    /// </summary>
    /// <param name="samples">Sub-samples per texel axis.</param>
    /// <param name="rig">Placed lamps. Their light joins the daylight in linear units - direct on this object, and
    /// bounced off whatever they light around it - and is shadowed by the same scene.</param>
    /// <param name="lampSamples">Shadow rays per lamp per sub-sample.</param>
    public static PhysicalMap? BakePhysical(MeshLibrary.Mesh mesh, Matrix4x4 world, int size, int samples, Physical ph,
        LightRig? rig = null, int lampSamples = 1, CancellationToken cancel = default)
    {
        var lm = mesh.LightmapUvs;
        if (lm is null || lm.Length == 0 || size < 4) return null;
        samples = Math.Clamp(samples, 1, 4);
        var pos = mesh.Positions;
        int n2 = size * size, sub = samples * samples;
        var day = ph.Day;

        // Linear light per texel, in three parts kept apart until the end.
        var sunVis = new float[n2];                                  // fraction of the sun's disc, summed
        var softR = new float[n2]; var softG = new float[n2]; var softB = new float[n2];   // sky + bounce
        float[]? lampR = null, lampG = null, lampB = null;           // placed lights, already linear
        var cnt = new int[n2];
        var softCnt = new int[n2];                                   // how many of a texel's samples traced the soft part
        var owner = new int[n2]; Array.Fill(owner, -1);
        var texelPos = new Vector3[n2]; var texelNrm = new Vector3[n2];
        var texelPart = new int[n2];                                 // which material part owns the texel

        bool lamps = rig is not null && rig.Lights.Count > 0;
        if (lamps) { lampR = new float[n2]; lampG = new float[n2]; lampB = new float[n2]; }
        var bounceRig = lamps ? rig : null;

        // The ray budget is per texel; each sub-sample takes its share, with its own seed, so the texel as a whole
        // sees that many DIFFERENT directions.
        // The SUN is sampled by every sub-sample: its edge is what sub-samples are for. The hemisphere - sky and
        // bounce - is traced by ONE sample of a texel, with the texel's whole budget, and only on a lattice of
        // texels (see SoftStride): the rest are filled in from their neighbours on the same surface.
        int sunPer = Math.Max(1, (ph.Budget.SunSamples + sub - 1) / sub);
        var sunOnly = new LightTracer.Budget(sunPer, 0, ph.Budget.Bounces);
        var withSoft = new LightTracer.Budget(sunPer, Math.Max(1, ph.Budget.HemiSamples), ph.Budget.Bounces);
        int stride = Math.Clamp(ph.SoftStride, 1, 4);
        var chunk = new SampleChunk(16384);
        var skyRgb = chunk.SkyRgb;
        float offset = MathF.Max(ph.SurfaceOffset, 0f);

        void Flush()
        {
            if (chunk.Count == 0) return;
            if (cancel.IsCancellationRequested) { chunk.Clear(); return; }
            try
            {
                Parallel.For(0, chunk.Count, new ParallelOptions { CancellationToken = cancel }, i =>
                {
                    var p = chunk.Position[i] + chunk.Normal[i] * offset;
                    // TexelSeed carries the gatherer's mark: 1 = this sample traces the soft part for its texel.
                    var l = LightTracer.Gather(ph.Scene, day, p, chunk.Normal[i], chunk.TexelSeed[i] == 1u ? withSoft : sunOnly,
                                               chunk.SubSeed[i], bounceRig);
                    chunk.Sun[i] = l.Sun; skyRgb[i] = l.Sky; chunk.Bounce[i] = l.Indirect;
                    // A lamp is authored on the screen's scale; it joins the rest as linear light, here, so that a
                    // texel half in a lamp's shadow averages to half the LIGHT.
                    chunk.Lamp[i] = lamps
                        ? day.FromDisplay(LampLight.Display(ph.Scene, rig!, p, chunk.Normal[i], lampSamples, false, chunk.SubSeed[i]))
                        : Vector3.Zero;
                });
            }
            catch (OperationCanceledException) { }
            if (cancel.IsCancellationRequested) { chunk.Clear(); return; }
            for (int i = 0; i < chunk.Count; i++)
            {
                int o = chunk.Texel[i];
                sunVis[o] += chunk.Sun[i];
                if (chunk.TexelSeed[i] == 1u)
                {
                    var s = skyRgb[i] + chunk.Bounce[i];
                    softR[o] += s.X; softG[o] += s.Y; softB[o] += s.Z; softCnt[o]++;
                }
                if (lamps) { var lp = chunk.Lamp[i]; lampR![o] += lp.X; lampG![o] += lp.Y; lampB![o] += lp.Z; }
                cnt[o]++;
            }
            chunk.Clear();
        }

        int triId = 0, partId = -1;
        foreach (var part in mesh.Parts)
        {
            partId++;
            int thisPart = partId;
            var idx = part.Indices;
            for (int t = 0; t + 2 < idx.Length; t += 3, triId++)
            {
                if (cancel.IsCancellationRequested) return null;
                int a = idx[t], b = idx[t + 1], c = idx[t + 2];
                if ((uint)a >= (uint)pos.Length || (uint)b >= (uint)pos.Length || (uint)c >= (uint)pos.Length) continue;
                if (a >= lm.Length || b >= lm.Length || c >= lm.Length) continue;
                Vector3 wa = Vector3.Transform(pos[a], world), wb = Vector3.Transform(pos[b], world), wc = Vector3.Transform(pos[c], world);
                Vector3 fn = Vector3.Cross(wb - wa, wc - wa);
                if (fn.LengthSquared() < 1e-12f) continue;
                fn = -Vector3.Normalize(fn);                          // Refractor winds clockwise: outward is the negation
                Vector2 ta = lm[a] * size, tb = lm[b] * size, tc = lm[c] * size;
                if (!float.IsFinite(ta.X) || !float.IsFinite(ta.Y) || !float.IsFinite(tb.X) || !float.IsFinite(tb.Y) || !float.IsFinite(tc.X) || !float.IsFinite(tc.Y)) continue;
                int thisTri = triId;
                RasterTriangle(ta, tb, tc, size, samples, (px, py, w0, w1, w2, k) =>
                {
                    if (cancel.IsCancellationRequested) return;
                    int o = py * size + px;
                    bool first = owner[o] == -1;
                    if (first) { owner[o] = thisTri; texelNrm[o] = fn; texelPart[o] = thisPart; }
                    else if (owner[o] != thisTri) return;             // first triangle wins a texel
                    var wp = w0 * wa + w1 * wb + w2 * wc;
                    texelPos[o] = wp;
                    uint texelSeed = (uint)(px * 73856093 ^ py * 19349663);
                    // The texel's first sample traces its soft light, if the texel is on the lattice.
                    uint soft = first && px % stride == 0 && py % stride == 0 ? 1u : 0u;
                    chunk.Add(o, wp, fn, soft, SubSeed(texelSeed, k));
                    if (chunk.Full) Flush();
                });
            }
        }
        Flush();
        if (cancel.IsCancellationRequested) return null;

        var cover = new bool[n2];
        int covered = 0;
        for (int i = 0; i < n2; i++)
            if (cnt[i] > 0)
            {
                float inv = 1f / cnt[i];
                sunVis[i] *= inv;
                if (softCnt[i] > 0) { float si = 1f / softCnt[i]; softR[i] *= si; softG[i] *= si; softB[i] *= si; }
                if (lamps) { lampR![i] *= inv; lampG![i] *= inv; lampB![i] *= inv; }
                cover[i] = true; covered++;
            }
        // An unwrap that is all one point rasterises nothing: no map, and the caller decides what the object gets.
        if (covered < n2 / 2000) return null;

        // Give every texel off the lattice the soft light of its neighbours ON THE SAME SURFACE - near in the world
        // and facing the same way, which is what tells a neighbour apart from a different wall packed beside it.
        // Whatever finds no such neighbour (a sliver of a chart, an isolated texel) is traced after all.
        if (stride > 1)
        {
            var orphans = FillSoft(softR, softG, softB, softCnt, cover, texelPos, texelNrm, size);
            if (orphans.Count > 0 && !cancel.IsCancellationRequested)
            {
                try
                {
                    Parallel.For(0, orphans.Count, new ParallelOptions { CancellationToken = cancel }, j =>
                    {
                        int o = orphans[j];
                        var l = LightTracer.Gather(ph.Scene, day, texelPos[o] + texelNrm[o] * offset, texelNrm[o],
                                                   new LightTracer.Budget(0, withSoft.HemiSamples, withSoft.Bounces),
                                                   LightTracer.Mix((uint)o, 0x51F15EEDu), bounceRig);
                        var sv = l.Sky + l.Indirect;
                        softR[o] = sv.X; softG[o] = sv.Y; softB[o] = sv.Z;
                    });
                }
                catch (OperationCanceledException) { }
            }
            if (cancel.IsCancellationRequested) return null;
        }

        // Sky and bounce are smooth by nature and noisy by estimate, which is the case a denoiser is for. The
        // sensitivity is in linear light, where open sky is about a quarter and a contact shadow's edge a few
        // hundredths - small enough that the dark line under an eave survives.
        if (ph.DenoiseIterations > 0)
        {
            var dn = new Advanced(DenoiseIterations: ph.DenoiseIterations, DenoiseEdgeSensitivity: 0.05f);
            Denoise(softR, cover, texelPos, texelNrm, size, dn);
            Denoise(softG, cover, texelPos, texelNrm, size, dn);
            Denoise(softB, cover, texelPos, texelNrm, size, dn);
        }
        if (cancel.IsCancellationRequested) return null;

        // Linear light -> what the screen should show -> the map value that makes the engine show it.
        var inten = new float[n2];
        double sum = 0, err = 0; int atFloor = 0, atCeil = 0;
        var m = EngineLight.Material.Default with { VertexLit = ph.VertexLit };
        // A mesh the game lights per vertex takes its lightmap per CHANNEL over a vertex colour that is the two
        // lights x materialDiffuse + selfillum, clamped (measured). So each channel is solved on its own, exactly:
        // map = (wanted - LMambient) / vertex colour. On a lightmap-only copy (selfillum 1) the vertex colour is 1
        // and the map IS the light - on every face, whichever way it looks.
        float[]? outG = null, outB = null;
        if (ph.VertexLit || ph.Bf1942) { outG = new float[n2]; outB = new float[n2]; }
        for (int i = 0; i < n2; i++)
        {
            if (!cover[i]) continue;
            var nrm = texelNrm[i];
            var e = day.SunIrradiance * (MathF.Max(0f, Vector3.Dot(nrm, day.ToSun)) * sunVis[i])
                    + new Vector3(softR[i], softG[i], softB[i]);
            if (lamps) e += new Vector3(lampR![i], lampG![i], lampB![i]);
            var want = day.ToDisplay(e);
            if (ph.Bf1942)
            {
                // The map IS the light, halved: nothing to invert, no floor, and only one way to miss - a texel
                // brighter than the texture at full, which the game clips.
                var map = EngineLight.Bf1942.SolveLightmap(want);
                inten[i] = map.X; outG![i] = map.Y; outB![i] = map.Z;
                sum += EngineLight.Luma(map);
                if (MathF.Max(want.X, MathF.Max(want.Y, want.Z)) > 1.01f) atCeil++;
                err += MathF.Abs(MathF.Min(1f, EngineLight.Luma(want)) - EngineLight.Luma(EngineLight.Bf1942.Lightmapped(map)));
                continue;
            }
            if (ph.VertexLit)
            {
                var part = mesh.Parts[texelPart[i]];
                var pm = new EngineLight.Material(part.Diffuse, Vector3.One, part.SelfIllum) { VertexLit = true };
                var v = EngineLight.Lights(nrm, day.ToSun, ph.Colours, pm);
                var over = Vector3.Max(Vector3.Min(want, Vector3.One) - ph.Colours.LmAmbient, Vector3.Zero);
                var map = Vector3.Clamp(over / Vector3.Max(v, new Vector3(EngineLight.MinLights)), Vector3.Zero, Vector3.One);
                inten[i] = map.X; outG![i] = map.Y; outB![i] = map.Z;
                sum += EngineLight.Luma(map);
                var shown = EngineLight.Lightmapped(map, nrm, day.ToSun, ph.Colours, pm);
                float lw = EngineLight.Luma(Vector3.Min(want, Vector3.One)), ls = EngineLight.Luma(shown);
                if (lw < ls - 0.01f) atFloor++; else if (lw > ls + 0.01f) atCeil++;
                err += MathF.Abs(lw - ls);
                continue;
            }
            float p = EngineLight.SolvePrelight(want, nrm, day.ToSun, ph.Colours, m);
            inten[i] = p; sum += p;
            float reach = EngineLight.OutOfReach(want, nrm, day.ToSun, ph.Colours, m);
            if (reach < -0.01f) atFloor++; else if (reach > 0.01f) atCeil++;
            err += MathF.Abs(MathF.Min(1f, EngineLight.Luma(want)) - EngineLight.Luma(EngineLight.Lightmapped(p, nrm, day.ToSun, ph.Colours, m)));
        }

        if (outG is not null) { Dilate(outG, (bool[])cover.Clone(), size); Dilate(outB!, (bool[])cover.Clone(), size); }
        Dilate(inten, cover, size);
        var rgba = new byte[n2 * 4];
        for (int i = 0; i < n2; i++)
        {
            byte v = (byte)Math.Clamp((int)(inten[i] * 255f + 0.5f), 0, 255);
            rgba[i * 4] = v;
            rgba[i * 4 + 1] = outG is null ? v : (byte)Math.Clamp((int)(outG[i] * 255f + 0.5f), 0, 255);
            rgba[i * 4 + 2] = outB is null ? v : (byte)Math.Clamp((int)(outB![i] * 255f + 0.5f), 0, 255);
            rgba[i * 4 + 3] = 255;
        }
        return new PhysicalMap(new Texture2D(size, size, rgba), (float)(sum / covered),
                               atFloor / (float)covered, atCeil / (float)covered, (float)(err / covered)) { Colour = outG is not null };
    }

    /// <summary>
    /// Fill the soft light of every covered texel that did not trace its own from the ones that did, weighted by how
    /// close they are in the world and how alike they face. Returns the texels that found no such neighbour.
    /// </summary>
    private static List<int> FillSoft(float[] r, float[] g, float[] b, int[] softCnt, bool[] cover,
                                      Vector3[] pos, Vector3[] nrm, int size)
    {
        float reach = MathF.Max(TexelFootprint(cover, pos, size) * 3f, 1e-4f);
        var orphans = new List<int>();
        var gate = new object();
        // Values are written to copies so a filled texel never feeds another fill in the same pass.
        var fr = (float[])r.Clone(); var fg = (float[])g.Clone(); var fb = (float[])b.Clone();
        Parallel.For(0, size, y =>
        {
            List<int>? mine = null;
            for (int x = 0; x < size; x++)
            {
                int o = y * size + x;
                if (!cover[o] || softCnt[o] > 0) continue;
                var p0 = pos[o]; var n0 = nrm[o];
                float ar = 0f, ag = 0f, ab = 0f, wsum = 0f;
                for (int dy = -3; dy <= 3; dy++)
                    for (int dx = -3; dx <= 3; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if ((uint)nx >= (uint)size || (uint)ny >= (uint)size) continue;
                        int q = ny * size + nx;
                        if (!cover[q] || softCnt[q] == 0) continue;
                        float d = (pos[q] - p0).Length() / reach;
                        float nd = MathF.Max(0f, Vector3.Dot(n0, nrm[q]));
                        float w = MathF.Exp(-d * d) * nd * nd * nd * nd;
                        if (w <= 1e-5f) continue;
                        ar += r[q] * w; ag += g[q] * w; ab += b[q] * w; wsum += w;
                    }
                if (wsum > 1e-4f) { fr[o] = ar / wsum; fg[o] = ag / wsum; fb[o] = ab / wsum; }
                else (mine ??= new List<int>()).Add(o);
            }
            if (mine is not null) lock (gate) orphans.AddRange(mine);
        });
        Array.Copy(fr, r, r.Length); Array.Copy(fg, g, g.Length); Array.Copy(fb, b, b.Length);
        orphans.Sort();                                       // the order the threads finished in must not show
        return orphans;
    }

    /// <summary>
    /// The ONE lightmap value for a mesh whose lightmap channel carries no unwrap - every vertex reads the same
    /// texel, so the whole object takes a single value. This is the value that puts the object, on average, at the
    /// brightness the traced light says it has: a crate under a roof comes out dark, the same crate in the open
    /// bright, where a blank map leaves both at the ambient floor.
    /// </summary>
    /// <param name="maxSamples">Triangles sampled, spread over the mesh and weighted by area.</param>
    /// <param name="rig">Placed lamps, as in <see cref="BakePhysical"/>.</param>
    public static float AveragePhysical(MeshLibrary.Mesh mesh, Matrix4x4 world, Physical ph, int maxSamples = 96,
                                        LightRig? rig = null)
    {
        if (rig is { Lights.Count: 0 }) rig = null;
        var pos = mesh.Positions;
        if (pos is null || pos.Length == 0) return 1f;
        var day = ph.Day;
        int triCount = 0;
        foreach (var part in mesh.Parts) triCount += part.Indices.Length / 3;
        if (triCount == 0) return 1f;
        int stride = Math.Max(1, triCount / Math.Max(maxSamples, 1));
        var budget = new LightTracer.Budget(4, Math.Max(16, ph.Budget.HemiSamples / 4), ph.Budget.Bounces);

        // What the object should show and what the engine's lights give it, both averaged over its surface. The
        // one value that matches them is solved from the averages - solving each face and averaging the answers
        // lets a few faces the lights barely reach decide the whole object.
        double want = 0, lights = 0, area = 0;
        int ti = 0;
        foreach (var part in mesh.Parts)
        {
            var idx = part.Indices;
            for (int t = 0; t + 2 < idx.Length; t += 3, ti++)
            {
                if (ti % stride != 0) continue;
                int a = idx[t], b = idx[t + 1], c = idx[t + 2];
                if ((uint)a >= (uint)pos.Length || (uint)b >= (uint)pos.Length || (uint)c >= (uint)pos.Length) continue;
                Vector3 wa = Vector3.Transform(pos[a], world), wb = Vector3.Transform(pos[b], world), wc = Vector3.Transform(pos[c], world);
                var cr = Vector3.Cross(wb - wa, wc - wa);
                float ar = cr.Length() * 0.5f;
                if (ar < 1e-8f) continue;
                var fn = -cr / (ar * 2f);
                var p = (wa + wb + wc) / 3f + fn * MathF.Max(ph.SurfaceOffset, 0.02f);
                uint seed = (uint)(ti * 2654435761u);
                var l = LightTracer.Gather(ph.Scene, day, p, fn, budget, seed, rig);
                var e = l.Irradiance(day, fn);
                if (rig is not null) e += day.FromDisplay(LampLight.Display(ph.Scene, rig, p, fn, 2, false, seed));
                want += ar * MathF.Min(1f, EngineLight.Luma(day.ToDisplay(e)));
                lights += ar * EngineLight.Luma(EngineLight.Direct(fn, day.ToSun, ph.Colours));
                area += ar;
            }
        }
        if (area <= 0) return 1f;
        double over = want / area - EngineLight.Luma(ph.Colours.LmAmbient);
        if (over <= 0) return 0f;
        return (float)Math.Clamp(over / Math.Max(lights / area, EngineLight.MinLights), 0.0, 1.0);
    }
}
