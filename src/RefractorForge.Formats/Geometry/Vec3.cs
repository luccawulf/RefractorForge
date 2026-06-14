using System.Globalization;

namespace RefractorForge.Formats.Geometry;

/// <summary>
/// A 3-component vector matching Refractor's "x/y/z" textual form.
/// Parsing/formatting always uses invariant culture so a '.' is the decimal
/// separator regardless of the user's locale (a classic source of corrupted .con files).
/// </summary>
public readonly record struct Vec3(float X, float Y, float Z)
{
    public static readonly Vec3 Zero = new(0, 0, 0);

    /// <summary>Parse "x/y/z" (the form used throughout .con files).</summary>
    public static Vec3 Parse(string s)
    {
        var parts = s.Split('/');
        if (parts.Length != 3)
            throw new FormatException($"Expected 'x/y/z', got '{s}'.");
        return new Vec3(ParseComponent(parts[0]), ParseComponent(parts[1]), ParseComponent(parts[2]));
    }

    public static bool TryParse(string s, out Vec3 value)
    {
        try { value = Parse(s); return true; }
        catch { value = Zero; return false; }
    }

    private static float ParseComponent(string s) =>
        float.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>Format as "x/y/z" with trimmed trailing zeros, invariant culture.</summary>
    public override string ToString() => $"{Fmt(X)}/{Fmt(Y)}/{Fmt(Z)}";

    private static string Fmt(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}
