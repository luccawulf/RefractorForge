using System;
using System.Numerics;

namespace RefractorForge.Render;

/// <summary>
/// A level's outdoor light described the way it exists - a sun, a sky and the ground they light - rather than as
/// the four colours the engine draws with. Everything a realistic bake needs follows from it: how much light a point
/// receives (the ray tracer integrates this sun and this sky), what that light should look like on screen
/// (<see cref="ToDisplay"/>), and which Init.con colours let the engine reproduce it (<see cref="Fit"/>).
///
/// <para><b>Units.</b> Light here is LINEAR and relative: the sun delivers an irradiance of luma 1 onto a surface that
/// faces it squarely, and the sky and everything bounced are measured against that. The engine works on display
/// values instead - it multiplies gamma-encoded textures by gamma-encoded light and never converts - so a result is
/// taken across once, at the end: <c>display = (exposure x linear) ^ (1 / gamma)</c>. Doing the transport in linear
/// light is what keeps the proportions right. A shadow in clear weather receives about a fifth of the light of the
/// sunlit ground beside it; drawn a fifth as bright it looks like a hole, and drawn at <c>0.2 ^ (1/2.2) = 0.48</c>
/// it looks like the 0.45-0.78 every retail level paints its shadows at.</para>
/// </summary>
public sealed record Daylight
{
    /// <summary>Unit vector toward the sun.</summary>
    public Vector3 ToSun { get; init; } = Vector3.Normalize(new Vector3(0.5f, 0.7f, 0.5f));

    /// <summary>The sun's colour, linear. Only its hue matters: it is scaled to luma 1.</summary>
    public Vector3 SunColour { get; init; } = new(1f, 0.95f, 0.86f);

    /// <summary>The sun's angular diameter in degrees; 0.53 is the real one. It sets how fast a shadow edge softens
    /// with distance from what casts it.</summary>
    public float SunAngularDiameterDeg { get; init; } = 0.53f;

    /// <summary>The sky's colour straight up, linear. Only its hue matters; <see cref="SkyStrength"/> sets how bright.</summary>
    public Vector3 SkyZenith { get; init; } = new(0.34f, 0.52f, 0.92f);

    /// <summary>The sky's colour at the horizon, linear, relative to <see cref="SkyZenith"/>.</summary>
    public Vector3 SkyHorizon { get; init; } = new(0.76f, 0.84f, 0.96f);

    /// <summary>
    /// How much light the open sky delivers to level ground, as a fraction of what the sun delivers to a surface
    /// facing it. About 0.15-0.25 under a clear sky, 0.4-0.6 in haze, and above 1 when overcast - where the "sun" is
    /// only a brighter patch of cloud. This one number is most of the difference between a harsh noon and a soft day.
    /// </summary>
    public float SkyStrength { get; init; } = 0.2f;

    /// <summary>The ground's mean reflectance, linear - what the open ground returns toward walls and eaves.</summary>
    public Vector3 GroundAlbedo { get; init; } = new(0.16f, 0.15f, 0.11f);

    /// <summary>Scales every bounce. 1 is physical; 0 leaves only the sun and the sky.</summary>
    public float BounceStrength { get; init; } = 1f;

    /// <summary>
    /// The display level of an open surface that faces the sun squarely: 1 draws its texture at full brightness.
    /// This is the exposure. A little above 1 clips the brightest faces slightly, as the retail levels do; a night
    /// level sets it far lower.
    /// </summary>
    public float Peak { get; init; } = 1.05f;

    /// <summary>The display gamma light is encoded with on the way out.</summary>
    public float Gamma { get; init; } = 2.2f;

    /// <summary>
    /// How much of the open ground's bounce an enclosed, downward-facing surface is taken to receive when the
    /// engine's ambient floor is chosen - 1 for the underside of an open awning, toward 0 for a deep room. The engine
    /// shows the ambient alone on every ceiling and nothing can be drawn darker than it, so this is the level's
    /// interior brightness.
    /// </summary>
    public float InteriorFill { get; init; } = 0.3f;

    /// <summary>
    /// The most the ambient floor may be before the fit has any reason to raise it. Bright ground - sand, snow -
    /// bounces a great deal of light, and taken at its word would put the floor near half brightness: every interior
    /// on the map would then be drawn at least that bright, and none could read as indoors. Retail levels sit at
    /// 0.2-0.35.
    /// </summary>
    public float MaxInteriorFloor { get; init; } = 0.30f;

    /// <summary>Work out everything that follows from these settings, once. The result is immutable and safe to
    /// share between threads - a bake asks it for the sky's radiance hundreds of millions of times.</summary>
    public DaylightModel Build() => new(this);
}

/// <summary>
/// A <see cref="Daylight"/> with its derived quantities worked out: the sky scaled to its strength, the exposure,
/// and the functions the ray tracer, the bakes and the Init.con fit are written against.
/// </summary>
public sealed class DaylightModel
{
    /// <summary>The settings this was built from.</summary>
    public Daylight Settings { get; }

    public Vector3 ToSun { get; }
    /// <summary>The sun's irradiance on a surface facing it, linear: its colour at luma 1.</summary>
    public Vector3 SunIrradiance { get; }
    /// <summary>The sky's irradiance on level ground, linear RGB; its luma is the sky strength.</summary>
    public Vector3 SkyIrradianceUp { get; }
    /// <summary>What open, level ground receives: the sun at its height plus the whole sky.</summary>
    public Vector3 OpenGroundIrradiance { get; }
    /// <summary>The radiance open, level ground returns: its albedo times what the sun and the sky deliver to it.</summary>
    public Vector3 GroundRadiance { get; }
    /// <summary>The scale that puts an open surface facing the sun at the peak on screen.</summary>
    public float Exposure { get; }
    /// <summary>The sky's radiance straight up and at the horizon, linear - <see cref="SkyRadiance"/> blends these.</summary>
    public Vector3 SkyZenithRadiance => _zenith * _skyScale;
    public Vector3 SkyHorizonRadiance => _horizon * _skyScale;
    /// <summary>The display gamma <see cref="ToDisplay"/> encodes with.</summary>
    public float DisplayGamma => _gamma;

    private readonly Vector3 _horizon, _zenith;
    private readonly float _skyScale, _gamma, _invGamma;

    public DaylightModel(Daylight d)
    {
        Settings = d;
        ToSun = d.ToSun.LengthSquared() > 1e-12f ? Vector3.Normalize(d.ToSun) : Vector3.UnitY;
        float sl = EngineLight.Luma(d.SunColour);
        SunIrradiance = sl > 1e-6f ? d.SunColour / sl : Vector3.One;
        _horizon = d.SkyHorizon; _zenith = d.SkyZenith;
        _gamma = MathF.Max(d.Gamma, 1f); _invGamma = 1f / _gamma;

        // E = integral over the upper hemisphere of L(w) * cos(theta) dw = 2 pi * integral_0^1 L(y) * y dy.
        const int n = 256;
        var sum = Vector3.Zero;
        for (int i = 0; i < n; i++)
        {
            float y = (i + 0.5f) / n;
            sum += Vector3.Lerp(_horizon, _zenith, Gradient(y)) * y;
        }
        var e = sum * (2f * MathF.PI / n);
        float l = EngineLight.Luma(e);
        _skyScale = l > 1e-6f ? MathF.Max(d.SkyStrength, 0f) / l : 0f;
        SkyIrradianceUp = e * _skyScale;

        OpenGroundIrradiance = SunIrradiance * MathF.Max(0f, ToSun.Y) + SkyIrradianceUp;
        GroundRadiance = d.GroundAlbedo * OpenGroundIrradiance * (MathF.Max(d.BounceStrength, 0f) / MathF.PI);

        Exposure = 1f;
        float facing = EngineLight.Luma(OpenIrradiance(ToSun));
        Exposure = facing > 1e-6f ? MathF.Pow(MathF.Max(d.Peak, 1e-3f), _gamma) / facing : 1f;
    }

    // The gradient's shape: most of the change from horizon colour to zenith colour happens low in the sky.
    private static float Gradient(float y) => MathF.Pow(y, 0.6f);

    /// <summary>The sky's radiance in a direction above the horizon, linear. A two-colour gradient: horizon to
    /// zenith, scaled so the whole dome delivers the sky strength to level ground.</summary>
    public Vector3 SkyRadiance(Vector3 dir)
        => Vector3.Lerp(_horizon, _zenith, Gradient(Math.Clamp(dir.Y, 0f, 1f))) * _skyScale;

    /// <summary>What a ray that leaves the level sees: the sky above the horizon, and below it the open ground lit
    /// by this sun and sky - the world carries on past the edge of the map.</summary>
    public Vector3 Environment(Vector3 dir) => dir.Y > 0f ? SkyRadiance(dir) : GroundRadiance;

    // ---- an unoccluded surface ---------------------------------------------------------------------------------

    private static readonly Vector3[] Sphere = FibonacciSphere(1536);

    private static Vector3[] FibonacciSphere(int n)
    {
        var d = new Vector3[n];
        float golden = MathF.PI * (3f - MathF.Sqrt(5f));
        for (int i = 0; i < n; i++)
        {
            float y = 1f - 2f * (i + 0.5f) / n;
            float r = MathF.Sqrt(MathF.Max(0f, 1f - y * y));
            float a = i * golden;
            d[i] = new Vector3(MathF.Cos(a) * r, y, MathF.Sin(a) * r);
        }
        return d;
    }

    /// <summary>
    /// The light an UNOCCLUDED surface facing <paramref name="n"/> receives over open, level ground, linear: the sun
    /// on its facing, the part of the sky it can see, and the part of the ground it can see. This is what the ray
    /// tracer returns for a point with nothing around it, computed directly - the reference the engine's colours are
    /// fitted to.
    /// </summary>
    public Vector3 OpenIrradiance(Vector3 n)
    {
        var e = SunIrradiance * MathF.Max(0f, Vector3.Dot(n, ToSun));
        var ground = GroundRadiance;
        var sum = Vector3.Zero;
        foreach (var w in Sphere)
        {
            float c = Vector3.Dot(n, w);
            if (c <= 0f) continue;
            sum += (w.Y > 0f ? SkyRadiance(w) : ground) * c;
        }
        return e + sum * (4f * MathF.PI / Sphere.Length);
    }

    // ---- linear light to what the screen shows -----------------------------------------------------------------

    /// <summary>Linear light to the display multiplier the engine works in: what the texture should be multiplied by.
    /// Not clamped - the engine saturates on its own, and a bake needs to know by how much a value overshoots.</summary>
    public Vector3 ToDisplay(Vector3 linear)
    {
        float k = Exposure, g = _invGamma;
        return new Vector3(MathF.Pow(MathF.Max(linear.X * k, 0f), g),
                           MathF.Pow(MathF.Max(linear.Y * k, 0f), g),
                           MathF.Pow(MathF.Max(linear.Z * k, 0f), g));
    }

    /// <summary>The inverse of <see cref="ToDisplay"/>: a display multiplier back to linear light. A placed lamp is
    /// authored as "1 draws the texture at full brightness", and this is how it joins the linear sum.</summary>
    public Vector3 FromDisplay(Vector3 display)
    {
        float k = Exposure;
        if (k <= 1e-9f) return Vector3.Zero;
        return new Vector3(MathF.Pow(MathF.Max(display.X, 0f), _gamma),
                           MathF.Pow(MathF.Max(display.Y, 0f), _gamma),
                           MathF.Pow(MathF.Max(display.Z, 0f), _gamma)) / k;
    }

    /// <summary>What an unoccluded surface facing <paramref name="n"/> should show: <see cref="OpenIrradiance"/> on
    /// screen.</summary>
    public Vector3 OpenDisplay(Vector3 n) => ToDisplay(OpenIrradiance(n));

    // ---- the engine's four colours -----------------------------------------------------------------------------

    /// <summary>What <see cref="Fit"/> worked out, with the numbers that explain it.</summary>
    /// <param name="Colours">The four Init.con light colours.</param>
    /// <param name="ShadowAlpha">The alpha for <c>shadow.shadowColor</c>: how dark a soldier's or vehicle's moving
    /// shadow must be to match the shade baked into the ground.</param>
    /// <param name="OpenShade">The display level of open, level ground the sun does not reach, relative to the same
    /// ground in the sun - the shadow darkness this daylight produces.</param>
    public readonly record struct Fitted(EngineLight.Colours Colours, float ShadowAlpha, float OpenShade);

    /// <summary>
    /// The Init.con colours with which the engine comes closest to this daylight.
    ///
    /// <para>The engine has two directional lights and a constant, so it cannot draw a sky. What it can do is reach
    /// the right brightness on every surface that one of its lights touches, with the lightmap doing the rest, and
    /// each colour is chosen for its part in that:</para>
    /// <list type="number">
    /// <item><b>LMambientColor</b> is the floor - what a ceiling shows and the darkest anything lightmapped can be.
    /// It is the open ground's bounce onto a downward face, scaled by <see cref="Daylight.InteriorFill"/>.</item>
    /// <item><b>diffuseColor</b> and <b>SecondaryDiffuseColor</b> are searched together so that, with the lightmap
    /// at full, a surface in the open is AT LEAST as bright as it should be whichever way it faces - a lightmap can
    /// only take light away, so anything short of that can never be put right - and beyond that as little over as
    /// possible, because an object with no lightmap shows the two lights exactly as they are.</item>
    /// <item><b>ambientColor</b> is what is left for an object with no lightmap once its two lights are counted.</item>
    /// </list>
    /// </summary>
    /// <param name="minSun">A floor under <c>diffuseColor</c>. The lightmap can only scale the two lights DOWN, so
    /// a level lit by lamps at night has to keep its "sun" bright enough for a lamp-lit wall to reach full
    /// brightness, and write the moon low in the map instead.</param>
    /// <param name="maxSun">The most <c>diffuseColor</c> may be. A brighter sun lets a wall that the sun only grazes
    /// reach its real brightness, at the price of burning out more of every object that has no lightmap; retail
    /// levels stay at or under 1.</param>
    /// <param name="minBack">The same floor under <c>SecondaryDiffuseColor</c>: a wall that faces away from the moon
    /// is lit by the back light alone, so without this no lamp can show on it.</param>
    /// <param name="tint">The colour both lights take, instead of the sun's and the open shade's. A lightmap scales
    /// the engine's lights and cannot change their colour, so on a level lit by lamps every lamp-lit wall takes the
    /// colour of <c>diffuseColor</c>; left at the moon's blue, a sodium lamp would light a wall blue. The lamps'
    /// own colour is the right one there - the moonlit faces it also tints are dark, and the ambient keeps the
    /// night's colour for everything unlit.</param>
    public Fitted Fit(float minSun = 0f, float maxSun = DefaultMaxSun, float minBack = 0f, Vector3? tint = null)
    {
        float MaxSun = Math.Clamp(maxSun, 0.05f, SunLimit);
        var sun = ToSun;

        // 1. The floor: ground bounce onto a face that looks straight down, partly enclosed.
        var floorLinear = Settings.GroundAlbedo * OpenGroundIrradiance
                          * (MathF.Max(Settings.BounceStrength, 0f) * Math.Clamp(Settings.InteriorFill, 0f, 1f));
        var lm = Vector3.Clamp(ToDisplay(floorLinear), new Vector3(0.02f), new Vector3(0.6f));
        float lmLuma = EngineLight.Luma(lm), lmCap = Math.Clamp(Settings.MaxInteriorFloor, 0.02f, MaxFloor);
        if (lmLuma > lmCap) lm *= lmCap / lmLuma;

        // The surfaces the fit is judged on: every facing from straight up to a little below level. For each, how
        // strongly each light falls on it and how bright it should be.
        var facing = new System.Collections.Generic.List<(float A, float B, float Want)>();
        var backDir = EngineLight.BackLight(sun);
        for (int i = 0; i < Sphere.Length; i += 4)
        {
            var n = Sphere[i];
            if (n.Y < -0.1f) continue;
            facing.Add((MathF.Max(0f, Vector3.Dot(n, sun)), MathF.Max(0f, Vector3.Dot(n, backDir)),
                        MathF.Min(EngineLight.Luma(OpenDisplay(n)), 1f)));
        }

        // The HUE of each colour is read off the surface that colour exists for; only its STRENGTH is searched.
        // Fitting three channels separately lets one of them run into a limit the others do not, and the colour of
        // the light then drifts with the weather setting instead of with the light.
        float cosElev = MathF.Sqrt(MathF.Max(0f, sun.X * sun.X + sun.Z * sun.Z));
        var side = cosElev > 1e-3f ? Vector3.Normalize(new Vector3(-sun.Z, 0f, sun.X)) : Vector3.UnitX;   // level, square to the sun
        var away = cosElev > 1e-3f ? Vector3.Normalize(new Vector3(-sun.X, 0f, -sun.Z)) : Vector3.UnitZ;
        var keyHue = Hue(OpenDisplay(sun) - lm);          // what the sun adds to a face turned toward it
        var backHue = Hue(OpenDisplay(away) - lm);        // open shade on a wall: the sky and the ground
        if (tint is { } tc) keyHue = backHue = Hue(tc);
        var ambHue = Hue(OpenDisplay(side));              // a wall neither light touches: the same, with no floor taken off

        // 2. The two lights' strengths and the floor, searched together: a coarse grid, then a fine one around its
        // best cell. The floor starts where step 1 put it and may only RISE: under a clear sky the two lights reach
        // every target and it stays put, but the more of the light comes from the whole sky - haze, overcast - the
        // less two directional lights can stand in for it, and the constant has to carry what they cannot.
        float floorWant = EngineLight.Luma(lm);
        float lo = Math.Clamp(minSun, 0f, MaxSun);
        float lo2 = Math.Clamp(minBack, 0f, MaxBack);
        float Cost(float k, float k2, float floor)
        {
            double cost = 0;
            foreach (var (a, b, want) in facing)
            {
                float d = MathF.Min(1f, k * a + k2 * b + floor) - want;
                // Short of the target is a fault no lightmap can mend - a lightmap only takes light away - while
                // over it shows only on objects without one. So falling short costs more, but not without limit:
                // a sun bright enough to lift every grazing wall would burn out every soldier and vehicle.
                cost += d < 0f ? 3.0 * d * d : (double)d * d;
            }
            // Every step the floor rises is taken straight out of how dark an interior can be.
            float lift = floor - floorWant;
            cost += 2.0 * facing.Count * lift * lift;
            // Among equals, the dimmer pair: it leaves objects with no lightmap closest to the real light.
            return (float)cost + 1e-4f * (k + k2);
        }
        float bk = lo, bk2 = lo2, bf = floorWant, best = float.MaxValue;
        for (float f = floorWant; f <= MaxFloor + 1e-4f; f += 0.04f)
            for (float k = lo; k <= MaxSun + 1e-4f; k += 0.05f)
                for (float k2 = lo2; k2 <= MaxBack + 1e-4f; k2 += 0.05f)
                {
                    float c = Cost(k, k2, f);
                    if (c < best) { best = c; bk = k; bk2 = k2; bf = f; }
                }
        float ck = bk, ck2 = bk2, cf = bf;
        for (float f = MathF.Max(floorWant, cf - 0.04f); f <= MathF.Min(MaxFloor, cf + 0.04f) + 1e-4f; f += 0.01f)
            for (float k = MathF.Max(lo, ck - 0.05f); k <= MathF.Min(MaxSun, ck + 0.05f) + 1e-4f; k += 0.01f)
                for (float k2 = MathF.Max(lo2, ck2 - 0.05f); k2 <= MathF.Min(MaxBack, ck2 + 0.05f) + 1e-4f; k2 += 0.01f)
                {
                    float c = Cost(k, k2, f);
                    if (c < best) { best = c; bk = k; bk2 = k2; bf = f; }
                }
        // What the floor gained is sky and ground light on a wall, so it takes that light's colour.
        lm = Capped(lm + ambHue * MathF.Max(0f, bf - floorWant), MaxFloor);
        var keyV = Capped(keyHue * bk, MaxSun);
        var backV = Capped(backHue * bk2, MaxBack);

        // 3. The ambient for objects with no lightmap: whatever makes the two lights plus a constant sit closest to
        // the real light, over the same facings.
        float bestA = 0.04f; best = float.MaxValue;
        for (float a0 = 0.04f; a0 <= 0.6f + 1e-4f; a0 += 0.01f)
        {
            double cost = 0;
            foreach (var (a, b, want) in facing)
            {
                float d = MathF.Min(1f, bk * a + bk2 * b + a0) - want;
                cost += (double)d * d;
            }
            if (cost < best) { best = (float)cost; bestA = a0; }
        }
        var ambV = Capped(ambHue * bestA, 0.8f);

        // The moving shadow of a soldier or a vehicle is alpha-blended over ground that already carries the sun, so
        // matching baked shade means taking the sunlit level down to the shaded one.
        float lit = EngineLight.Luma(ToDisplay(OpenGroundIrradiance));
        float shade = EngineLight.Luma(ToDisplay(SkyIrradianceUp));
        float ratio = lit > 1e-6f ? Math.Clamp(shade / lit, 0f, 1f) : 1f;

        return new Fitted(new EngineLight.Colours(keyV, backV, ambV, lm), Math.Clamp(1f - ratio, 0f, 0.9f), ratio);
    }

    /// <summary>The most <c>renderer.diffuseColor</c> can usefully be. The engine halves it on the way to the shader,
    /// whose constants stop at 1, so 2 is the hard limit.</summary>
    public const float SunLimit = 1.5f;
    /// <summary>The most <see cref="Fit"/> sets <c>renderer.diffuseColor</c> to unless told otherwise.</summary>
    public const float DefaultMaxSun = 1.25f;
    /// <summary>The most <c>renderer.LMambientColor</c> is ever set to.</summary>
    public const float MaxFloor = 0.6f;
    /// <summary>The most <c>renderer.SecondaryDiffuseColor</c> is ever set to.</summary>
    public const float MaxBack = 1.0f;

    /// <summary>A colour scaled to luma 1 - its hue alone. Grey when there is nothing to take a hue from.</summary>
    private static Vector3 Hue(Vector3 c)
    {
        c = Vector3.Max(c, Vector3.Zero);
        float l = EngineLight.Luma(c);
        return l > 1e-4f ? c / l : Vector3.One;
    }

    /// <summary>Scale a colour down, keeping its hue, until no channel is over <paramref name="cap"/>.</summary>
    private static Vector3 Capped(Vector3 c, float cap)
    {
        float m = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        return m > cap ? c * (cap / m) : c;
    }
}
