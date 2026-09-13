using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RefractorForge.Formats.Terrain;

/// <summary>One tree as the engine actually generated it: world position, yaw, and the uniform scale it drew at.</summary>
public readonly record struct CapturedTree(int Type, float X, float Y, float Z, float YawDeg, float Scale);

/// <summary>
/// Turning a level's procedural overgrowth into ordinary static objects.
///
/// WHY: the engine generates the forest from <c>OverGrowthMap.raw</c> + <c>overGrowth.wst</c> at load, and how much
/// it plants is decided by the EXECUTABLE, not the map - the stock BfVietnam.exe keeps a 10x10 patch window spaced
/// viewDistance/4 apart while the BfVietnam_Veg_* builds use 80x80 at 12.5 m. So a forest authored against one build
/// is a different forest on another, and asking players to install a patched .exe is not an option for a public
/// map. Baked to static objects the trees are just level content: every client loads the same ones, from the same
/// archive, on the stock game.
///
/// TWO SOURCES, both handled here:
///  - the editor's own statistical scatter (<see cref="OvergrowthFoliage.Scatter"/>), and
///  - a <c>tree_dump.bin</c> CAPTURE read out of the running game by the user's bfvVegetationCapture tool, which is
///    exact tree-for-tree because it reads the instances the engine already generated.
///
/// The dump was verified against Operation Flaming Dart (41,559 instances): 99.74% land on a tree-bearing material,
/// heights match the heightmap to a mean 0.028 m, and type->material is exact.
/// </summary>
public static class OvergrowthCapture
{
    // Record layout written by capture_overgrowth.py: a u32 count, then per instance an i32 type followed by the
    // 92-byte transform block (23 floats) copied straight out of the instance at +0x04. Floats 0..15 are a
    // row-major 4x4 - translation in 12/13/14 - and 16..18 are the uniform scale.
    private const int RecordBytes = 96;

    /// <summary>Read a <c>tree_dump.bin</c>. Throws with a plain message when the file is not one.</summary>
    public static List<CapturedTree> LoadDump(byte[] data)
    {
        if (data.Length < 4) throw new InvalidDataException("Not a tree dump: the file is empty.");
        int count = BitConverter.ToInt32(data, 0);
        long need = 4L + (long)count * RecordBytes;
        if (count < 0 || need > data.Length)
            throw new InvalidDataException($"Not a tree dump: it claims {count:N0} instances, which needs {need:N0} bytes but the file is {data.Length:N0}.");

        var list = new List<CapturedTree>(count);
        for (int i = 0; i < count; i++)
        {
            int o = 4 + i * RecordBytes;
            int type = BitConverter.ToInt32(data, o);
            float M(int f) => BitConverter.ToSingle(data, o + 4 + f * 4);
            // Yaw about Y from the rotation basis, exactly as the capture tool's finalize step does.
            float yaw = MathF.Atan2(M(2), M(0)) * 180f / MathF.PI;
            list.Add(new CapturedTree(type, M(12), M(13), M(14), yaw, M(16)));
        }
        return list;
    }

    /// <summary>
    /// The static-object template name for an overgrowth geometry: <c>c05f_trees_m2</c> -> <c>C05F_Trees_M1</c>.
    /// Overgrowth ships as <c>_m2</c> impostor geometry, while the same tree exists as an <c>_M1</c> object template
    /// that stock BfVietnam already defines (retail levels place these by hand), so a baked forest needs nothing
    /// declared. The rule: a <c>cNNf</c> part goes upper-case, an <c>m1</c>/<c>m2</c> part becomes <c>M1</c>, and
    /// everything else is capitalised.
    /// </summary>
    public static string StaticTemplateFor(string geometryName)
    {
        if (string.IsNullOrWhiteSpace(geometryName)) return geometryName;
        var parts = geometryName.Split('_');
        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p.Length == 0) continue;
            if (Regex.IsMatch(p, @"^[cC]\d\d[fF]$")) parts[i] = p.ToUpperInvariant();
            else if (p.Equals("m1", StringComparison.OrdinalIgnoreCase) || p.Equals("m2", StringComparison.OrdinalIgnoreCase)) parts[i] = "M1";
            else parts[i] = char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant();
        }
        return string.Join('_', parts);
    }

    /// <summary>
    /// Every name the static template for an overgrowth geometry could plausibly be, best first, for the caller to
    /// check against the templates the archives actually declare.
    ///
    /// Why a list and not one answer: a geometry name does not always carry the <c>_m1</c>/<c>_m2</c> part that
    /// <see cref="StaticTemplateFor"/> rewrites. The overgrowth "F_Fern06" is the template "F_Fern06_M1", and
    /// nothing in the name says so. Guessing wrong is not harmless - the name is written straight into
    /// StaticObjects.con as <c>object.create</c>, and a template the game does not know is an "unknown
    /// objectTemplate" error, two more parse errors for the position and rotation lines that follow it, and a tree
    /// that never appears. One map shipped 56 of them.
    /// </summary>
    public static IEnumerable<string> StaticTemplateCandidates(string geometryName)
    {
        if (string.IsNullOrWhiteSpace(geometryName)) yield break;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var primary = StaticTemplateFor(geometryName);
        var stem = Regex.Replace(primary, @"_[mMlL]\d+$", "");
        foreach (var c in new[] { primary, stem + "_M1", geometryName, stem })
            if (!string.IsNullOrEmpty(c) && seen.Add(c)) yield return c;
    }

    /// <summary>
    /// Empty every <c>&lt;types&gt;</c> block in a <c>.wst</c>, leaving the file otherwise byte-identical.
    ///
    /// This is the other half of baking: with the trees now placed as static objects, the engine must stop
    /// generating them or the map draws every tree twice. A TEXT transform on purpose - four retail <c>.wst</c>
    /// files are not well-formed XML and the game loads them anyway, so round-tripping through an XML writer would
    /// reformat (or reject) a file that works.
    /// </summary>
    public static string EmptyTypes(string wstXml)
        => Regex.Replace(wstXml, @"<types>.*?</types>", "<types>\r\n\t\t\t\t</types>",
                         RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>True when the palette still declares something to grow - i.e. the engine would draw it too. The
    /// negative lookahead matters: an EMPTIED block is still <c>&lt;types&gt;</c> followed by whitespace and a
    /// <c>&lt;</c>, so without it this reports every emptied file as still planting.</summary>
    public static bool HasAnyTypes(string wstXml)
        => Regex.IsMatch(wstXml, @"<types>\s*<(?!/\s*types)", RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>Format a baked forest as a standalone StaticObjects-style <c>.con</c>, for pasting into a level by
    /// hand or shipping beside it. The editor's own path adds them as real objects instead, but this keeps the
    /// capture tool's output format available.</summary>
    public static string ToCon(IEnumerable<(string Template, float X, float Y, float Z, float Yaw)> trees, string header)
    {
        var sb = new StringBuilder();
        sb.Append("rem ").Append(header).Append("\r\n");
        var inv = CultureInfo.InvariantCulture;
        foreach (var (t, x, y, z, yaw) in trees)
        {
            sb.Append("Object.create ").Append(t).Append("\r\n");
            sb.Append("Object.absolutePosition ")
              .Append(x.ToString("0.###", inv)).Append('/')
              .Append(y.ToString("0.###", inv)).Append('/')
              .Append(z.ToString("0.###", inv)).Append("\r\n");
            sb.Append("Object.rotation ").Append(yaw.ToString("0.##", inv)).Append("/0/0\r\n");
        }
        return sb.ToString();
    }
}
