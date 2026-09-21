namespace RefractorForge.Formats.Terrain;

/// <summary>
/// A look, and the four knobs that describe one: where the beach ends, where ground becomes cliff, how patchy the
/// in-between materials are, and how wet the low ground is. A style builds the <see cref="MaterialRule"/> list;
/// the rules stay editable afterwards for anyone who wants them, but nobody has to open them to get a good map.
///
/// Material indices are the editor's palette order. Index 7 never appears: it is deathMaterial, the engine's
/// out-of-bounds, not a surface (see <see cref="DeathMaterial"/>).
/// </summary>
public sealed class TexturingStyle
{
    public const int MatDefault = 0, MatDryGrass = 1, MatWetGrass = 2, MatDryDirt = 3, MatWetDirt = 4, MatMud = 5,
                     MatDrySand = 6, MatGravel = 8, MatRock = 9, MatFrozen = 14, MatWater = 15;

    public string Name { get; init; } = "";
    /// <summary>One line the editor shows under the picker, so the choice can be made without trying all of them.</summary>
    public string Description { get; init; } = "";

    /// <summary>How far inland the beach reaches, in metres.</summary>
    public float BeachMeters { get; set; } = 8f;
    /// <summary>Ground steeper than this is treated as a bare face (what it is painted with is per style).</summary>
    public float CliffDeg { get; set; } = 34f;
    /// <summary>0 = clean bands, 1 = the in-between materials break up into patches.</summary>
    public float Variation { get; set; } = 0.5f;
    /// <summary>0 = dry map, 1 = every gully and hollow turns to wet ground and mud.</summary>
    public float Wetness { get; set; } = 0.5f;

    private readonly Func<TexturingStyle, List<MaterialRule>> _build;

    private TexturingStyle(Func<TexturingStyle, List<MaterialRule>> build) => _build = build;

    public List<MaterialRule> Build() => _build(this);

    public TexturingStyle Clone() => new(_build)
    {
        Name = Name, Description = Description,
        BeachMeters = BeachMeters, CliffDeg = CliffDeg, Variation = Variation, Wetness = Wetness,
    };

    // ---- shared pieces every style wants ----

    private static MaterialRule Base(int mat) => new() { Name = "Ground", Material = mat };

    /// <summary>
    /// The ground UNDER the water line. It is painted like land, not like water: across 83 retail levels the "Water"
    /// material covers 0.00% of submerged ground (DICE uses sand, the level default, or sand road down there) - the
    /// water surface is drawn by the engine on top. Painting material 15 for a sea bed gives a lake nobody has seen
    /// in this game.
    /// </summary>
    private static MaterialRule SeaBed(int mat = MatDrySand) => new()
    { Name = "Under water", Material = mat, MaxAboveWater = 0f, EdgeSoftness = 0.2f };

    private static MaterialRule Beach(TexturingStyle s, int mat = MatDrySand) => new()
    {
        Name = "Beach", Material = mat, MinAboveWater = 0f, MaxShoreMeters = s.BeachMeters,
        MaxSlopeDeg = MathF.Max(s.CliffDeg - 4f, 10f), EdgeSoftness = 0.55f,
    };

    /// <summary>
    /// Steep faces. The material is per style on purpose: retail BFV paints steep ground as DIRT and leaves rock to
    /// placed cliff objects (material 9 is 0.01% of all retail terrain, in 4 of 83 levels), so only the styles that
    /// are meant to look bare reach for rock.
    /// </summary>
    private static MaterialRule Cliffs(TexturingStyle s, int mat = MatDryDirt) => new()
    { Name = "Steep faces", Material = mat, MinSlopeDeg = s.CliffDeg, EdgeSoftness = 0.35f };

    private static MaterialRule Scree(TexturingStyle s) => new()
    {
        Name = "Scree below cliffs", Material = MatGravel,
        MinSlopeDeg = MathF.Max(s.CliffDeg - 9f, 6f), MaxSlopeDeg = s.CliffDeg,
        EdgeSoftness = 0.6f, Coverage = 0.55f + 0.45f * s.Variation, PatchMeters = 40f,
    };

    /// <summary>Water collects in the gullies: the wetter the map, the earlier flow counts as wet ground.</summary>
    private static MaterialRule Gullies(TexturingStyle s, int mat, float fromFlow) => new()
    {
        Name = "Gullies", Material = mat, MinFlow = Lerp(fromFlow + 0.18f, fromFlow - 0.08f, s.Wetness),
        MaxSlopeDeg = 30f, MaxConvexity = 0.15f, EdgeSoftness = 0.5f,
    };

    private static float Lerp(float a, float b, float t) => a + (b - a) * Math.Clamp(t, 0f, 1f);

    // ---- the styles ----

    public static IReadOnlyList<TexturingStyle> All => new[] { TropicalIsland(), RiverDelta(), Highland(), Desert(), Temperate() };

    public static TexturingStyle ByName(string name)
        => All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) ?? TropicalIsland();

    /// <summary>Green, hilly, sand at the water: the Vietnam coast look.</summary>
    public static TexturingStyle TropicalIsland() => new(s => new List<MaterialRule>
    {
        Base(MatDryGrass),
        new() { Name = "Jungle floor", Material = MatWetGrass, MaxAltitude = Lerp(0.35f, 0.7f, s.Wetness),
                MaxSlopeDeg = 26f, Coverage = Lerp(0.45f, 0.95f, s.Wetness), PatchMeters = 90f, EdgeSoftness = 0.5f },
        new() { Name = "Worn earth", Material = MatDryDirt, MinAltitude = 0.3f, MaxSlopeDeg = s.CliffDeg,
                Coverage = 0.25f + 0.5f * s.Variation, PatchMeters = 70f, EdgeSoftness = 0.5f },
        Gullies(s, MatMud, 0.5f),
        Scree(s),
        Cliffs(s),
        Beach(s),
        SeaBed(),
    })
    { Name = "Tropical island", Description = "Green hills, sand at the water line, worn earth on the steep faces." };

    /// <summary>Flat, wet, muddy - paddies and river banks.</summary>
    public static TexturingStyle RiverDelta() => new(s => new List<MaterialRule>
    {
        Base(MatWetGrass),
        new() { Name = "Drier rises", Material = MatDryGrass, MinAltitude = Lerp(0.5f, 0.25f, s.Variation),
                Coverage = 0.6f, PatchMeters = 120f, EdgeSoftness = 0.6f },
        new() { Name = "Wet ground", Material = MatWetDirt, MaxAltitude = Lerp(0.2f, 0.5f, s.Wetness),
                MaxSlopeDeg = 18f, Coverage = Lerp(0.3f, 0.9f, s.Wetness), PatchMeters = 60f, EdgeSoftness = 0.5f },
        Gullies(s, MatMud, 0.4f),
        new() { Name = "Banks", Material = MatMud, MinAboveWater = 0f, MaxShoreMeters = MathF.Max(s.BeachMeters, 4f),
                MaxSlopeDeg = 22f, EdgeSoftness = 0.6f },
        Cliffs(s),
        SeaBed(MatMud),
    })
    { Name = "River delta", Description = "Flat and wet: paddies, mud along the banks, little bare rock.", Wetness = 0.75f, CliffDeg = 38f };

    /// <summary>Bare high ground: dirt and gravel with rock above, grass only in the valleys.</summary>
    public static TexturingStyle Highland() => new(s => new List<MaterialRule>
    {
        Base(MatDryDirt),
        new() { Name = "Valley grass", Material = MatDryGrass, MaxAltitude = Lerp(0.3f, 0.55f, s.Wetness),
                MaxSlopeDeg = 24f, Coverage = 0.8f, PatchMeters = 80f, EdgeSoftness = 0.5f },
        new() { Name = "Stony ground", Material = MatGravel, MinAltitude = 0.45f,
                Coverage = 0.4f + 0.5f * s.Variation, PatchMeters = 55f, EdgeSoftness = 0.5f },
        Gullies(s, MatWetDirt, 0.55f),
        Cliffs(s, MatRock),
        new() { Name = "Snow line", Material = MatFrozen, MinAltitude = 0.86f, MaxSlopeDeg = s.CliffDeg + 6f,
                Coverage = 0.85f, PatchMeters = 50f, EdgeSoftness = 0.45f },
        Beach(s, MatGravel),
        SeaBed(MatGravel),
    })
    { Name = "Highland", Description = "Dirt and gravel high ground, grass in the valleys, snow on the peaks.", CliffDeg = 30f, Wetness = 0.3f };

    /// <summary>Sand everywhere, rock where it is steep - the Al Nas end of the scale.</summary>
    public static TexturingStyle Desert() => new(s => new List<MaterialRule>
    {
        Base(MatDrySand),
        new() { Name = "Hard pan", Material = MatDryDirt, MinAltitude = 0.15f, MaxSlopeDeg = s.CliffDeg,
                Coverage = 0.35f + 0.5f * s.Variation, PatchMeters = 110f, EdgeSoftness = 0.6f },
        new() { Name = "Stony flats", Material = MatGravel, MinSlopeDeg = 8f, MaxSlopeDeg = s.CliffDeg,
                Coverage = 0.3f + 0.4f * s.Variation, PatchMeters = 60f, EdgeSoftness = 0.5f },
        Gullies(s, MatDryDirt, 0.6f),
        Cliffs(s, MatGravel),
        new() { Name = "Oasis green", Material = MatDryGrass, MinFlow = Lerp(0.85f, 0.6f, s.Wetness),
                MaxSlopeDeg = 20f, EdgeSoftness = 0.5f },
        SeaBed(),
    })
    { Name = "Desert", Description = "Sand, hard pan and stone; green only where water runs.", Wetness = 0.2f, BeachMeters = 4f };

    /// <summary>Mixed green country - the safe default on an unfamiliar map.</summary>
    public static TexturingStyle Temperate() => new(s => new List<MaterialRule>
    {
        Base(MatDryGrass),
        new() { Name = "Lush ground", Material = MatWetGrass, MaxAltitude = Lerp(0.4f, 0.75f, s.Wetness),
                MaxSlopeDeg = 28f, Coverage = Lerp(0.35f, 0.85f, s.Wetness), PatchMeters = 100f, EdgeSoftness = 0.55f },
        new() { Name = "Bare earth", Material = MatDryDirt, MinSlopeDeg = 14f, MaxSlopeDeg = s.CliffDeg,
                Coverage = 0.3f + 0.5f * s.Variation, PatchMeters = 65f, EdgeSoftness = 0.5f },
        Gullies(s, MatWetDirt, 0.55f),
        Scree(s),
        Cliffs(s),
        Beach(s),
        SeaBed(),
    })
    { Name = "Temperate", Description = "Mixed grass and earth with rocky faces - a good starting point anywhere." };
}
