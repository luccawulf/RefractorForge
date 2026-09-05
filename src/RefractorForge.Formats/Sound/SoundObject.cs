using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RefractorForge.Formats.Sound;

/// <summary>
/// Builds a level-local ambient sound: the wav, the <c>.ssc</c> script that plays it and the <c>.con</c> that makes
/// it a placeable object. Modelled line for line on a working retail point sound - al_vietnas's <c>rivermid</c>:
///
/// <code>
/// Sounds/rivermid.con   ObjectTemplate.create SimpleObject rivermid
///                       ObjectTemplate.loadSoundScript rivermid.ssc
///                       (its ObjectTemplate.triggerRadius line the engine rejects - see below)
/// Sounds/rivermid.ssc   load @ROOT/Sound/@RTD/alnas_river.wav ; loop ; minDistance 50 ; volume .4
///                       + a Distance->Volume Ramp effect (param 70 / 150 / 1 / -1)
/// Sound/22khz/*.wav     the audio itself
/// </code>
///
/// <c>@RTD</c> is the sample-rate folder the engine picks from the player's sound-quality setting, so the wav is
/// written under both <c>22khz</c> and <c>44kHz</c>; <c>minDistance</c> is the radius that stays at full volume and
/// the Ramp carries it down to silence at <c>maxDistance</c> - the "loud up close, quiet far away" pair.
/// The placed instance is an ordinary <c>Object.create &lt;name&gt;</c> line in StaticObjects.con.
///
/// WHERE THE SCRIPT GOES MATTERS: the engine resolves <c>loadSoundScript</c> relative to the folder of the .con that
/// declared the template, not from some sound path. A template declared in <c>Sounds/&lt;n&gt;.con</c> finds
/// <c>Sounds/&lt;n&gt;.ssc</c> (what <see cref="Build"/> writes); a sound put on an object declared in
/// <c>Objects/&lt;n&gt;/Objects.con</c> needs the script at <c>Objects/&lt;n&gt;/&lt;n&gt;.ssc</c> - see
/// <see cref="ScriptPathFor"/>.
/// </summary>
public static class SoundObject
{
    /// <param name="Template">The object name to place (also the .con/.ssc stem).</param>
    /// <param name="Files">Level-relative paths and bytes, ready for the level folder or the .rfa.</param>
    /// <param name="RunLine">The line that must appear in <c>Sounds/Environment.con</c> for the engine to read it.</param>
    public sealed record Built(string Template, IReadOnlyList<(string RelPath, byte[] Bytes)> Files, string RunLine);

    /// <summary>Everything a placed ambient sound needs. <paramref name="minDistance"/> is the full-volume radius,
    /// <paramref name="maxDistance"/> where it fades to nothing.</summary>
    /// <param name="stereo">Declare the sample as stereo. The format supports it and 276 of the game's own scripts
    /// use it alongside distance settings, but a stereo sample is the wrong choice for something you should be able to
    /// walk around - mono is what places a sound in the world.</param>
    /// <summary>Clips bigger than this are streamed from disk rather than loaded whole, as the game does with its
    /// own long loops. The largest clip any retail script loads is 0.8 MB.</summary>
    public const int StreamAbove = 1024 * 1024;

    /// <summary>
    /// A sound at a PLACE, the way every retail level does it: an <c>AreaObject</c> with a triggerRadius and an
    /// <c>addLinePoint</c> polygon, run from <c>Sounds/Environment.con</c> and placed in StaticObjects.con like any
    /// other object (Hue places river1 / river3 / island1 exactly so).
    ///
    /// This exists because the other shape - the sound hung on a visible SimpleObject with <c>autoPlaySound</c> -
    /// ties the sound to the object being DRAWN: it is heard only while the thing is on screen and stops at the
    /// draw distance, whatever the script's distances say. An AreaObject has no geometry, so nothing can cull it and
    /// the player's POSITION is all that matters.
    /// </summary>
    /// <param name="areaRadius">Half-width of the square area, in metres. Inside it the sound is at full volume.</param>
    public static Built BuildArea(string name, byte[] wavBytes, float volume = 0.6f, float minDistance = 20f,
                                  float maxDistance = 120f, bool loop = true, bool stereo = false,
                                  byte[]? wav44kBytes = null, float? areaRadius = null)
    {
        if (wavBytes is null || wavBytes.Length == 0) throw new ArgumentException("no wav data", nameof(wavBytes));
        string tpl = Sanitize(name);
        float minD = MathF.Max(1f, minDistance);
        float maxD = MathF.Max(minD + 1f, maxDistance);
        float rad = MathF.Max(1f, areaRadius ?? minD);
        // MDT: triggerRadius is the activation distance, 10..275, most commonly 50.
        float trig = Math.Clamp(maxD, 10f, 275f);

        var con = new StringBuilder();
        con.Append($"rem *** {tpl} ***\r\n");
        con.Append($"ObjectTemplate.create AreaObject {tpl}\r\n");
        con.Append("ObjectTemplate.saveInSeparateFile 1\r\n");
        con.Append($"ObjectTemplate.triggerRadius {F(trig)}\r\n");
        con.Append($"ObjectTemplate.loadSoundScript {tpl}.ssc\r\n");
        // A closed square around the object's own origin, traced out and back as the retail areas are.
        var pts = new (float X, float Z)[] { (rad, rad), (rad, -rad), (-rad, -rad), (-rad, rad), (rad, rad) };
        foreach (var p in pts) con.Append($"ObjectTemplate.addLinePoint {F(p.X)}/{F(p.Z)}\r\n");

        var files = new List<(string, byte[])>
        {
            ($"Sound/22khz/{tpl}.wav", wavBytes),
            ($"Sound/44kHz/{tpl}.wav", wav44kBytes ?? wavBytes),
            ($"Sounds/{tpl}.ssc", Encoding.Latin1.GetBytes(ScriptText(tpl, wavBytes, volume, minD, maxD, loop, stereo))),
            ($"Sounds/{tpl}.con", Encoding.Latin1.GetBytes(con.ToString())),
        };
        return new Built(tpl, files, $"run {tpl}.con");
    }

    public static Built Build(string name, byte[] wavBytes, float volume = 0.6f, float minDistance = 20f,
                              float maxDistance = 120f, bool loop = true, float? triggerRadius = null,
                              bool stereo = false, byte[]? wav44kBytes = null)
    {
        if (wavBytes is null || wavBytes.Length == 0) throw new ArgumentException("no wav data", nameof(wavBytes));
        string tpl = Sanitize(name);
        float minD = MathF.Max(1f, minDistance);
        float maxD = MathF.Max(minD + 1f, maxDistance);
        float trig = triggerRadius is { } tr && tr > 0f ? tr : maxD;

        // Shaped like the game's own ambient scripts (the US/NVA radios, the Hue speakers, o_gen_sound), because the
        // first shape did not work in game - its settings were ignored:
        //  - no "#templateLevel HIGH": in the 361 retail scripts that use it, that line OPENS a per-quality block,
        //    and all but two carry a block for each of HIGH / MEDIUM / LOW. A script with only a HIGH block gives a
        //    player on any other sound-quality setting nothing of ours to apply. The ambients carry no such line at
        //    all, so they apply at every setting - as this one now does.
        //  - no "rem": not one of the 1226 retail scripts uses it. A label in the "*** ***" form they do use.
        //  - "stream", not "load", for a long clip: retail never loads anything over 0.8 MB and streams its radio
        //    and propaganda loops (8-10 MB); a video's audio is minutes long. Streams take the distance ramp too
        //    (26 retail scripts combine them).
        //  - priority 11 for a looping ambient, as the radios and speakers have; a one-shot keeps a low one.
        bool stream = wavBytes.Length > StreamAbove;
        var ssc = new StringBuilder(ScriptText(tpl, wavBytes, volume, minD, maxD, loop, stereo));
        var con = new StringBuilder();
        con.Append($"rem *** {tpl} ***\r\n");
        con.Append($"ObjectTemplate.create SimpleObject {tpl}\r\n");
        con.Append("ObjectTemplate.saveInSeparateFile 1\r\n");
        con.Append($"ObjectTemplate.loadSoundScript {tpl}.ssc\r\n");
        // No triggerRadius: it is an AreaObject property, and on a SimpleObject the engine rejects it as an unknown
        // function (retail maps that carry the line get the same warning). The audible range is the script's
        // minDistance plus its Distance->Volume ramp.

        var files = new List<(string, byte[])>
        {
            // Both quality folders: the engine substitutes @RTD from the player's sound setting, and a level that
            // ships only one is silent for everyone on the other.
            // @RTD: the game reads the folder matching its sound-quality setting. With a 44.1 kHz file supplied the
            // high tier gets it and the low tier keeps the 22 kHz one; without, both folders get the same file.
            ($"Sound/22khz/{tpl}.wav", wavBytes),
            ($"Sound/44kHz/{tpl}.wav", wav44kBytes ?? wavBytes),
            ($"Sounds/{tpl}.ssc", Encoding.Latin1.GetBytes(ssc.ToString())),
            ($"Sounds/{tpl}.con", Encoding.Latin1.GetBytes(con.ToString())),
        };
        return new Built(tpl, files, $"run {tpl}.con");
    }


    /// <summary>The .ssc both shapes share, in the form the game's own ambients use.</summary>
    private static string ScriptText(string tpl, byte[] wavBytes, float volume, float minD, float maxD, bool loop, bool stereo)
    {
        // Shaped like the game's own ambient scripts (the US/NVA radios, the Hue speakers, o_gen_sound):
        //  - no "#templateLevel HIGH": that OPENS a per-quality block, and 359 of the 361 retail scripts using it
        //    carry HIGH, MEDIUM and LOW. A script with only a HIGH block gives a player on any other sound setting
        //    nothing of ours to apply. The object ambients carry no such line at all, so they apply everywhere.
        //  - no "rem": not one of the 1226 retail scripts uses it; the label form is "*** text ***".
        //  - "stream" for a long clip: retail never loads anything over 0.8 MB and streams its 8-10 MB radio loops.
        //    Streams take the distance ramp too (26 retail scripts combine them).
        //  - priority 11 for a looping ambient, as the radios and speakers have.
        bool stream = wavBytes.Length > StreamAbove;
        var ssc = new StringBuilder();
        ssc.Append("newPatch\r\n");
        ssc.Append($"*** {tpl} (RefractorForge) ***\r\n");
        ssc.Append($"{(stream ? "stream" : "load")} @ROOT/Sound/@RTD/{tpl}.wav\r\n");
        if (loop) ssc.Append("loop\r\n");
        if (stereo) ssc.Append("stereo\r\n");
        ssc.Append($"minDistance {F(minD)}\r\n");
        ssc.Append($"volume {F(volume)}\r\n");
        ssc.Append(loop ? "priority 11\r\n" : "priority -7\r\n");
        ssc.Append("*** Distance Volume ***\r\nbeginEffect\r\n\tcontrolDestination Volume\r\n\tcontrolSource Distance\r\n");
        ssc.Append($"\tenvelope Ramp\r\n\tparam {F(minD)}\r\n\tparam {F(maxD)}\r\n\tparam 1\r\n\tparam -1\r\nendEffect\r\n");
        return ssc.ToString();
    }

    /// <summary>Add the emitter's <c>run</c> line to the level's <c>Sounds/Environment.con</c> (creating the file's
    /// content when the level has none). Idempotent - a level re-saved twice keeps one line.</summary>
    public static string PatchEnvironmentCon(string? existing, string runLine)
    {
        string text = existing ?? DefaultEnvironmentCon;
        foreach (var ln in text.Split('\n'))
            if (ln.Trim().Equals(runLine, StringComparison.OrdinalIgnoreCase)) return text;
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        return text.TrimEnd('\r', '\n') + nl + runLine + nl;
    }

    /// <summary>What a level with no sound layer at all needs before its first emitter (the retail preamble).</summary>
    public const string DefaultEnvironmentCon =
        "Sound.3d.occludeByDistanceFactor 0.9\r\nSound.3d.occludeByDistanceMax 1000\r\nSound.3d.occludeByDistanceMin 80\r\n" +
        "Sound.3d.occludeByObjectDistance 30\r\nSound.3d.occludeByObjectFactor 0.8\r\nsound.distanceDelay.enableAsDefault 1\r\n\r\n";

    /// <summary>Where a sound script has to sit for an object whose template is declared in its own
    /// <c>Objects/&lt;folder&gt;/</c> - beside that .con, because that is where the engine looks.</summary>
    public static string ScriptPathFor(string objectFolder, string template) => $"Objects/{objectFolder}/{template}.ssc";

    /// <summary>A template name the engine (and a .con parser) can take: letters, digits and underscore.</summary>
    public static string Sanitize(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in (name ?? "").Trim())
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        var s = sb.ToString().Trim('_');
        if (s.Length == 0) s = "sound";
        if (char.IsDigit(s[0])) s = "s" + s;                 // a template may not start with a digit
        return s.Length > 40 ? s[..40] : s;
    }

    private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
