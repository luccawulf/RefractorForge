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
    /// Stable per-session identity for editing/selection/collaboration. NOT written to the
    /// .con file (the format has no id field) — assigned fresh on load, and shared between
    /// collaborators when a session's state is synced.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

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
    public float? Scale
    {
        get => _scale;
        set { _scale = value; ScaleSource = null; }
    }

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

    /// <summary>Deep copy, preserving Id and original source text (for collaboration state sync).</summary>
    public StaticObject Clone()
    {
        var c = new StaticObject(Template) { Id = Id, Layer = Layer };
        c.InitPosition(Position, PositionSource ?? Position.ToString());
        c.InitRotation(Rotation, RotationSource ?? Rotation.ToString());
        if (Scale is float s) c.InitScale(s, ScaleSource ?? s.ToString(System.Globalization.CultureInfo.InvariantCulture));
        c.ExtraLines.AddRange(ExtraLines);
        return c;
    }
}
