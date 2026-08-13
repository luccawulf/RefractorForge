using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Source-level guards for the translated UI. These caught a real bug: Dear ImGui's <c>ImHashStr</c> RESETS the
/// running hash when it meets "###", so a modal begun as <c>"共同編集###Collaborate"</c> hashes as
/// <c>"###Collaborate"</c> and no longer matched an <c>OpenPopup("Collaborate")</c>. The dialogs opened fine in
/// English (where the translation passes through unchanged) and silently never appeared in Japanese.
/// </summary>
public class LocalizationTests
{
    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "RefractorForge.sln"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }

    private static string ViewerDir() => Path.Combine(RepoRoot(), "src", "RefractorForge.Viewer");
    private static string ProgramCs() => File.ReadAllText(Path.Combine(ViewerDir(), "Program.cs"));

    // ---- Dear ImGui's ImHashStr, so the ### semantics are asserted, not assumed ---------------------------------
    private static readonly uint[] Lut = BuildLut();

    private static uint[] BuildLut()
    {
        var lut = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            lut[n] = c;
        }
        return lut;
    }

    private static uint ImHashStr(string s, uint seed = 0)
    {
        var data = Encoding.UTF8.GetBytes(s);
        seed = ~seed;
        uint crc = seed;
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] == '#' && i + 2 < data.Length && data[i + 1] == '#' && data[i + 2] == '#') crc = seed;
            crc = (crc >> 8) ^ Lut[(crc & 0xFF) ^ data[i]];
        }
        return ~crc;
    }

    [Fact]
    public void Imgui_hash_discards_everything_before_a_triple_hash()
    {
        // This is WHY a translated modal title breaks the id: only the text from ### onwards is hashed.
        Assert.Equal(ImHashStr("###Collaborate"), ImHashStr("共同編集###Collaborate"));
        Assert.Equal(ImHashStr("###Collaborate"), ImHashStr("Collaborate###Collaborate"));
        Assert.NotEqual(ImHashStr("Collaborate"), ImHashStr("共同編集###Collaborate"));
    }

    [Fact]
    public void Every_modal_is_opened_with_the_same_id_expression_it_is_begun_with()
    {
        var src = ProgramCs();
        var opens = Regex.Matches(src, @"ImGui\.OpenPopup\(\s*([^;]+?)\s*\)\s*;")
                         .Select(m => m.Groups[1].Value.Trim()).ToList();
        var begins = Regex.Matches(src, @"ImGui\.BeginPopupModal\(\s*((?:Loc\.\w+\([^()]*\))|(?:""[^""]*""))")
                          .Select(m => m.Groups[1].Value.Trim()).ToList();

        Assert.NotEmpty(opens);
        Assert.NotEmpty(begins);

        // Every popup that is opened must be begun with a byte-identical id expression. Anything else means the
        // two sides can hash differently in some language, and the dialog silently never shows.
        var unmatched = opens.Where(o => !begins.Contains(o)).ToList();
        Assert.True(unmatched.Count == 0,
            "OpenPopup id(s) with no identical BeginPopupModal id - these dialogs will not open in a translated " +
            "UI:\n  " + string.Join("\n  ", unmatched));
    }

    private static string Unescape(string s)
        => s.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", "\n");

    /// <summary>Every string literal inside a <c>Loc.T(...)</c>/<c>Loc.TL(...)</c> argument list, scanned over the
    /// BALANCED paren region. A leading-literal regex would miss <c>Loc.T(cond ? "a" : "b")</c> — a real pattern in
    /// this codebase, and one that silently left 10 strings untranslated until this scan replaced the regex.</summary>
    private static List<string> LocCallLiterals(string src)
    {
        var found = new List<string>();
        foreach (Match m in Regex.Matches(src, @"Loc\.TL?\("))
        {
            int i = m.Index + m.Length, depth = 1;
            while (i < src.Length && depth > 0)
            {
                char c = src[i];
                if (c == '"')
                {
                    int j = i + 1;
                    var sb = new StringBuilder();
                    while (j < src.Length && src[j] != '"')
                    {
                        if (src[j] == '\\' && j + 1 < src.Length) { sb.Append(src[j]).Append(src[j + 1]); j += 2; }
                        else sb.Append(src[j++]);
                    }
                    found.Add(sb.ToString());
                    i = j + 1;
                    continue;
                }
                if (c == '(') depth++;
                else if (c == ')') depth--;
                i++;
            }
        }
        return found;
    }

    [Fact]
    public void Japanese_dictionary_covers_every_string_the_ui_asks_to_translate()
    {
        var ja = JsonSerializer.Deserialize<Dictionary<string, string>>(
                     File.ReadAllText(Path.Combine(ViewerDir(), "Assets", "lang", "ja.json")))!;
        Assert.NotEmpty(ja);
        Assert.All(ja, kv => Assert.False(string.IsNullOrWhiteSpace(kv.Value), "empty translation for: " + kv.Key));

        // Literals handed to Loc, plus literals handed to the helpers that translate their own text argument
        // (Picker titles, tool/mapper tooltips, slider labels) - those never appear next to a Loc call.
        var patterns = new[]
        {
            @"Picker\.(?:File|Folder|Files)\(\s*""((?:[^""\\]|\\.)*)""",
            @"MapperButton\(\s*\d+\s*,\s*""((?:[^""\\]|\\.)*)""",
            @"IconTool\(\s*\d+\s*,\s*\w+\s*,\s*""((?:[^""\\]|\\.)*)""",
            @"SliderInput\(\s*""((?:[^""\\]|\\.)*)""",
        };

        var asked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(ViewerDir(), "*.cs"))
        {
            var src = File.ReadAllText(file);
            foreach (var lit in LocCallLiterals(src)) asked.Add(Unescape(lit));
            foreach (var p in patterns)
                foreach (Match m in Regex.Matches(src, p))
                    asked.Add(Unescape(m.Groups[1].Value));
        }

        // The product name deliberately stays English in every language.
        asked.Remove("RefractorForge");

        var missing = asked.Where(k => !ja.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0,
            $"{missing.Count} UI string(s) have no Japanese translation - add them to Assets/lang/ja.json:\n  " +
            string.Join("\n  ", missing.Take(40)));
    }

    [Fact]
    public void Translated_widget_labels_use_three_hashes_not_two()
    {
        // "Label##id" still hashes the VISIBLE text, so translating it would change the widget id and lose state.
        // Loc.TL must therefore emit "###", which pins the id to the English text in every language.
        var loc = File.ReadAllText(Path.Combine(ViewerDir(), "Localization.cs"));
        Assert.Contains("\"###\" + englishLabel", loc);
        Assert.Equal(ImHashStr("Save###Save"), ImHashStr("保存###Save"));
    }
}
