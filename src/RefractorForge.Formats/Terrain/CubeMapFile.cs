using System;

namespace RefractorForge.Formats.Terrain;

/// <summary>
/// A Refractor <c>.rcm</c> cube map: a six-line INI naming the <c>.dds</c> that goes on each face. It is what the
/// water's <c>cubemap</c> line points at, so it decides what the water MIRRORS.
/// <para>
/// The game ships exactly two, one letter apart - <c>texture/default_env.rcm</c> in the base archive and
/// <c>texture/env_default.rcm</c> that every retail level uses - and both name the same generic sky. A map with its
/// own skybox therefore reflected somebody else's clouds, because nothing ever pointed the water at the level's
/// own faces.
/// </para>
/// Face order is the one BF skyboxes are numbered in, verified against both shipped files and against the editor's
/// own cube-map upload (which has always looked right in the viewport):
/// <c>_01</c>=+Z, <c>_02</c>=+X, <c>_03</c>=-Z, <c>_04</c>=-X, <c>_05</c>=+Y (up), <c>_06</c>=-Y (down).
/// </summary>
public static class CubeMapFile
{
    /// <summary>The .rcm the base game ships; the water shader in the base archive points here.</summary>
    public const string BaseCubemap = "texture/default_env.rcm";

    /// <summary>The .rcm every retail level's water points at. Same generic sky, different file.</summary>
    public const string LevelCubemap = "texture/env_default.rcm";

    /// <summary>The face names <see cref="LevelCubemap"/> lists. A level changes what its water reflects by
    /// shipping its OWN copies of these SIX names - not by introducing a cube map of its own.
    /// <para>
    /// This is the whole trick, and it is the only thing that works: the .rcm is read from the base archive, but
    /// the faces it names go through the ordinary texture resolver, which prefers a level's own copy. Shipping a
    /// new .rcm inside a level crashes the map on its first drawn frame however correctly it is written - the
    /// cube-map loader does not search a level. Operation_Flaming_Dart does it this way and works.
    /// </para></summary>
    public const string StockFaceBase = "env_default";

    /// <summary>Where a level's override of one stock cube face goes, level-relative.</summary>
    public static string StockFaceRelPath(int face1To6) => $"Texture/{StockFaceBase}_0{face1To6}.dds";

    /// <summary>Where a level-local cube map for <paramref name="skyBaseName"/> lives, as the engine refers to it
    /// (forward slashes, no extension games). Shipping the file at <c>Texture/&lt;leaf&gt;</c> inside the level makes
    /// the level's copy win, the same way a map overrides a hi-res skybox face.
    /// <para>
    /// The SAME whole-path rule as <see cref="FaceFolder"/> applies to the .rcm itself, not just to the faces
    /// inside it: a shader referring to a level-local cube map as <c>texture/x.rcm</c> looks in the base archive,
    /// finds nothing, and the water simply never gets a cube map. Shaders write forward slashes (the .rcm's own
    /// face lines use backslashes - both are the game's own convention, copied as found).
    /// </para></summary>
    public static string RefFor(string skyBaseName, string? modRoot = null, string? levelName = null) =>
        string.IsNullOrWhiteSpace(modRoot) || string.IsNullOrWhiteSpace(levelName)
            ? "texture/" + Leaf(skyBaseName)
            : $"{modRoot}/levels/{levelName}/Texture/{Leaf(skyBaseName)}";

    /// <summary>The level-relative path to write it at, matching <see cref="RefFor"/>.</summary>
    public static string RelPathFor(string skyBaseName) => "Texture/" + Leaf(skyBaseName);

    private static string Leaf(string skyBaseName) => Sanitise(skyBaseName) + "_env.rcm";

    /// <summary>Where one generated face is written, level-relative. Used when the level's own skybox faces are
    /// not all one size - which is normal, BFVietnam's down face is often 32px against 512 for the rest - because a
    /// cube texture with mismatched faces is INCOMPLETE and samples black. Equal-sized faces are referenced where
    /// they already live instead, and nothing is copied.</summary>
    public static string FaceRelPath(string faceBaseName, int face1To6) =>
        $"Texture/{Sanitise(faceBaseName)}_0{face1To6}.dds";

    /// <summary>The folder a face lives in, as the engine names it. A texture shipped INSIDE a level is referred to
    /// by its whole path from the mod root - the level's own sky shader writes
    /// <c>bfvietnam/levels/Saigon68/Texture/Sky_Stalingrad_05</c> - NOT as <c>texture/...</c>, which resolves to the
    /// base archive. Naming level-local faces <c>texture\x.dds</c> pointed the cube map at six files that do not
    /// exist, and an incomplete cube map kills the map on its first drawn frame.</summary>
    public static string FaceFolder(string modRoot, string levelName) =>
        $"{modRoot}\\levels\\{levelName}\\Texture";

    /// <summary>The folder the game's own faces live in, for a cube map that names stock textures.</summary>
    public const string StockFaceFolder = "texture";

    /// <summary>The file body: the six faces of <paramref name="skyBaseName"/>, in the engine's own order, inside
    /// <paramref name="faceFolder"/> (see <see cref="FaceFolder"/> - level-local faces need the full path).</summary>
    public static string Text(string skyBaseName, string? faceFolder = null)
    {
        var dir = string.IsNullOrWhiteSpace(faceFolder) ? StockFaceFolder : faceFolder!.Replace('/', '\\').TrimEnd('\\');
        var b = Sanitise(skyBaseName);
        if (b.Length == 0) throw new ArgumentException("a cube map needs a skybox base name", nameof(skyBaseName));
        // Backslashes and CRLF, as both shipped files are written - the parser is the game's own INI reader and
        // there is no reason to hand it anything it has not already been reading since 2004.
        return "[CubeMap]\r\n"
             + $"PositiveX = {dir}\\{b}_02.dds\r\n"
             + $"NegativeX = {dir}\\{b}_04.dds\r\n"
             + $"PositiveY = {dir}\\{b}_05.dds\r\n"
             + $"NegativeY = {dir}\\{b}_06.dds\r\n"
             + $"PositiveZ = {dir}\\{b}_01.dds\r\n"
             + $"NegativeZ = {dir}\\{b}_03.dds\r\n";
    }

    /// <summary>Is this sky just the stock one? Then the level's water can keep pointing at the shipped .rcm rather
    /// than carrying a copy that says the same thing.</summary>
    public static bool IsStockSky(string? skyBaseName) =>
        string.IsNullOrWhiteSpace(skyBaseName)
        || Sanitise(skyBaseName).Equals("env_default", StringComparison.OrdinalIgnoreCase)
        || Sanitise(skyBaseName).Equals("default_env", StringComparison.OrdinalIgnoreCase);

    // The name reaches us from a mesh name (Sky_OI_m1 -> Sky_OI); keep it to what a filename may hold.
    private static string Sanitise(string name)
    {
        var s = (name ?? "").Trim().Replace('\\', '/');
        int slash = s.LastIndexOf('/');
        if (slash >= 0) s = s[(slash + 1)..];
        var keep = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-') keep.Append(c);
        return keep.ToString();
    }
}
