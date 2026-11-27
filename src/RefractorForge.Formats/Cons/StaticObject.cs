using RefractorForge.Formats.Geometry;

namespace RefractorForge.Formats.Con;

/// <summary>
/// One placed object from a StaticObjects.con block. Models the fields an editor
/// manipulates; any other <c>object.*</c> line is preserved verbatim in
/// <see cref="ExtraLines"/>.
///
/// Coordinates keep their ORIGINAL source text until the editor changes them, so
/// opening and re-saving a map does not rewrite every number with a different
/// float representation (the map-mangling behavior modders dislike in Battlecraft).
/// Changing a value via the property setter clears the cached source automatically.
/// </summary>
public sealed class StaticObject
{
    /// <summary>
    /// Identity for editing/selection/collaboration. The .con format has no id field, so a level that has never
    /// been synced gets one on load (<see cref="StaticObjectsFile.AssignStableIds"/> makes it the same on every
    /// machine for the same file). A synced level carries it as a <c>rem rfid:&lt;id&gt;</c> line - a comment
    /// to the game - which is what lets two people's copies of one map say "the same object" across saves.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>True when <see cref="Id"/> came from a <c>rem rfid:</c> line in the file rather than being made
    /// up on load - an id a sync can rely on.</summary>
    public bool IdFromFile { get; set; }

    public string Template { get; set; }

    private Vec3 _position = Vec3.Zero;
    public Vec3 Position
    {
        get => _position;
        set { _position = value; PositionSource = null; }
    }

    private Vec3 _rotation = Vec3.Zero;
    public Vec3 Rotation
    {
        get => _rotation;
        set { _rotation = value; RotationSource = null; }
    }

    private float? _scale;
    /// <summary>A uniform <c>object.geometry.scale</c>. Setting it replaces a per-axis scale, so the editor's
    /// scale tool still has one value to work on.</summary>
    public float? Scale
    {
        get => _scale;
        set { _scale = value; ScaleSource = null; _scaleAxes = null; }
    }

    private Vec3? _scaleAxes;
    /// <summary>A per-axis <c>object.geometry.scale x/y/z</c>, the form retail BF Vietnam levels write for most of
    /// their vegetation (Operation Irving's clusters are 0.86/0.98/0.90 and the like). Kept as its own value - and
    /// its text written back unchanged - so a save does not reset those objects to scale 1.</summary>
    public Vec3? ScaleAxes
    {
        get => _scaleAxes;
        set { _scaleAxes = value; ScaleSource = null; if (value is not null) _scale = null; }
    }

    /// <summary>The scale the object is drawn with on each axis: the per-axis value, else the uniform one, else 1.</summary>
    public Vec3 EffectiveScale => _scaleAxes ?? (_scale is float s ? new Vec3(s, s, s) : new Vec3(1f, 1f, 1f));

    /// <summary><c>object.layer</c> if present.</summary>
    public int? Layer { get; set; }

    /// <summary>Any unmodeled <c>object.*</c> / comment line, preserved for lossless round-trip.</summary>
    public List<string> ExtraLines { get; } = new();

    // Original textual forms; non-null while the value is unchanged since parsing.
    public string? PositionSource { get; private set; }
    public string? RotationSource { get; private set; }
    public string? ScaleSource { get; private set; }

    public StaticObject(string template) => Template = template;

    // Parser entry points: set value AND remember the exact source text.
    internal void InitPosition(Vec3 v, string src) { _position = v; PositionSource = src; }
    internal void InitRotation(Vec3 v, string src) { _rotation = v; RotationSource = src; }
    internal void InitScale(float v, string src)    { _scale = v;    ScaleSource = src; }
    internal void InitScaleAxes(Vec3 v, string src) { _scaleAxes = v; _scale = null; ScaleSource = src; }

    /// <summary>Deep copy, preserving Id and original source text (for collaboration state sync).</summary>
    public StaticObject Clone()
    {
        var c = new StaticObject(Template) { Id = Id, IdFromFile = IdFromFile, Layer = Layer };
        c.InitPosition(Position, PositionSource ?? Position.ToString());
        c.InitRotation(Rotation, RotationSource ?? Rotation.ToString());
        if (Scale is float s) c.InitScale(s, ScaleSource ?? s.ToString(System.Globalization.CultureInfo.InvariantCulture));
        else if (ScaleAxes is Vec3 sa) c.InitScaleAxes(sa, ScaleSource ?? sa.ToString());
        c.ExtraLines.AddRange(ExtraLines);
        return c;
    }
}
