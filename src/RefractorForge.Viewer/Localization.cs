using System.Text.Json;

namespace RefractorForge.Viewer;

/// <summary>
/// UI language support. Translations are plain JSON files keyed by the ENGLISH source string, so:
///  * every untranslated string falls through unchanged (the editor is never half-broken), and
///  * the community can add or fix a language by editing a text file - no rebuild, no code.
///
/// Files live in <c>lang\&lt;code&gt;.json</c> beside the executable (e.g. <c>lang\ja.json</c>), with a user override
/// in <c>%APPDATA%\RefractorForge\lang\&lt;code&gt;.json</c> that wins, so a translator can iterate without touching
/// the install. The chosen language is remembered in <c>%APPDATA%\RefractorForge\ui.json</c>.
///
/// ImGui note: a widget's visible label doubles as its ID, so translating a label would change the ID and lose the
/// saved layout/state. Call sites therefore use <see cref="TL"/> for labelled widgets, which appends a stable ASCII
/// <c>##id</c> suffix taken from the ENGLISH text - the label is translated, the identity is not.
/// </summary>
public static class Loc
{
    public sealed record LangInfo(string Code, string DisplayName);

    /// <summary>Languages offered in the UI. English is built in; others load from lang\&lt;code&gt;.json.</summary>
    public static readonly LangInfo[] Available =
    {
        new("en", "English"),
        new("ja", "日本語 (Japanese)"),
    };

    private static Dictionary<string, string> _map = new(StringComparer.Ordinal);

    /// <summary>The active language code ("en" = pass-through).</summary>
    public static string Current { get; private set; } = "en";

    /// <summary>True once the user has picked a language at least once. Drives the FIRST-RUN prompt: a Japanese
    /// speaker should not have to find View ▸ Language in an English menu to discover the editor speaks Japanese.
    /// Backed by the settings file, so choosing English is remembered too and the prompt does not return.</summary>
    public static bool HasChosenLanguage => File.Exists(SettingsPath);

    /// <summary>True when a non-English language is active (so the UI needs a CJK-capable font).</summary>
    public static bool NeedsWideFont => !string.Equals(Current, "en", StringComparison.OrdinalIgnoreCase);

    private static string AppDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RefractorForge");
    private static string SettingsPath => Path.Combine(AppDataDir, "ui.json");

    private sealed record UiPrefs(string? Language);

    /// <summary>Load the remembered language and its dictionary. Call once at startup, BEFORE the ImGui controller
    /// is created (the font choice depends on the language).</summary>
    public static void Init()
    {
        try
        {
            if (File.Exists(SettingsPath) &&
                JsonSerializer.Deserialize<UiPrefs>(File.ReadAllText(SettingsPath)) is { Language: string l } && l.Length > 0)
                Current = l;
        }
        catch { }
        LoadDictionary(Current);
    }

    /// <summary>Switch language: remember it and load its dictionary. The caller restarts the editor so the font
    /// atlas is rebuilt for the new script.</summary>
    public static void SetLanguage(string code)
    {
        Current = string.IsNullOrWhiteSpace(code) ? "en" : code.Trim();
        try
        {
            Directory.CreateDirectory(AppDataDir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new UiPrefs(Current), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
        LoadDictionary(Current);
    }

    private static void LoadDictionary(string code)
    {
        _map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.Equals(code, "en", StringComparison.OrdinalIgnoreCase)) return;
        // Install file first, then the %APPDATA% override on top (so a translator's edits win).
        foreach (var path in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "lang", code + ".json"),
                     Path.Combine(AppDataDir, "lang", code + ".json"),
                 })
        {
            try
            {
                if (!File.Exists(path)) continue;
                if (JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) is { } d)
                    foreach (var kv in d)
                        if (!string.IsNullOrEmpty(kv.Value)) _map[kv.Key] = kv.Value;
            }
            catch { /* a malformed language file must never stop the editor starting */ }
        }
    }

    /// <summary>Every English string the UI has asked to translate this session. Lets "Export translation template"
    /// emit exactly the strings that actually appear, instead of a hand-maintained list that silently rots.</summary>
    private static readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    public static IEnumerable<string> Seen { get { lock (_seen) return _seen.ToArray(); } }

    private static void Note(string s) { lock (_seen) _seen.Add(s); }

    /// <summary>Translate a plain string (no ImGui id). Unknown strings pass through unchanged.</summary>
    public static string T(string english)
    {
        Note(english);
        return _map.Count != 0 && _map.TryGetValue(english, out var s) ? s : english;
    }

    /// <summary>Translate a WIDGET LABEL while keeping its ImGui identity stable: returns
    /// <c>"&lt;translated&gt;###&lt;english&gt;"</c>.
    ///
    /// The THREE hashes matter. With <c>"Label##id"</c> ImGui hides the text after the marker but still hashes the
    /// WHOLE string, so a translated label would produce a different ID and lose any saved window/widget state.
    /// With <c>"Label###id"</c> the ID is derived from <c>id</c> ALONE, so it stays pinned to the English text in
    /// every language. Pass-through (no suffix) when nothing is translated, so English users keep today's exact ids.
    /// </summary>
    public static string TL(string englishLabel)
    {
        Note(englishLabel);
        if (_map.Count == 0) return englishLabel;
        if (!_map.TryGetValue(englishLabel, out var s) || s == englishLabel) return englishLabel;
        // If the caller already supplied an explicit ##/### id, translate only the visible part and keep their id.
        int hash = englishLabel.IndexOf("##", StringComparison.Ordinal);
        return hash >= 0 ? s + englishLabel[hash..] : s + "###" + englishLabel;
    }

    /// <summary>Number of translated entries loaded (0 = English / nothing found).</summary>
    public static int EntryCount => _map.Count;

    /// <summary>Write a template language file containing every string the editor asked for this session, so a
    /// translator has an exact, complete starting point. Returns the path written.</summary>
    public static string WriteTemplate(string code, IEnumerable<string> englishStrings)
    {
        var dir = Path.Combine(AppDataDir, "lang");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, code + ".template.json");
        var dict = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in englishStrings) if (!string.IsNullOrWhiteSpace(s)) dict[s] = _map.TryGetValue(s, out var v) ? v : "";
        File.WriteAllText(path, JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    // ---- Font selection -------------------------------------------------------------------------------------

    /// <summary>A font file that can draw the active language, or null to keep ImGui's built-in font.
    /// Japanese/Chinese/Korean text needs a CJK-capable face; ImGui's default font is ASCII-only and would draw
    /// every Japanese character as a blank box. Probes the fonts Windows ships (a Japanese Windows always has at
    /// least one of these), so nothing has to be bundled or licensed.</summary>
    public static string? FindUiFont()
    {
        if (!NeedsWideFont) return null;
        var fonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
        foreach (var f in new[]
                 {
                     "YuGothR.ttc", "YuGothM.ttc",   // Yu Gothic - the modern Windows Japanese UI face
                     "meiryo.ttc",                    // Meiryo - very legible, common on Japanese installs
                     "msgothic.ttc",                  // MS Gothic - present on essentially every Windows
                     "YuGothB.ttc", "malgun.ttf", "simsun.ttc",
                 })
        {
            var p = Path.Combine(fonts, f);
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
