using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RefractorForge.Formats.Sound;

/// <summary>
/// Parses and surgically edits a Refractor sound script (<c>.ssc</c>) — the format the BFV Sound SSC Editor
/// produces. The file is a flat, line-oriented script: optional <c>#templateLevel HIGH|MEDIUM|LOW</c> tier
/// markers, <c>newPatch</c> blocks, and within each a series of WAVE entries. A wave is a <c>stream</c>/<c>load
/// &lt;path&gt;</c> source line followed by its properties (<c>loop</c>, <c>stereo</c>, <c>volume N</c>,
/// <c>minDistance N</c>, <c>priority N</c>, <c>relativePosition x/y/z</c>, ...) and <c>beginEffect</c>/
/// <c>endEffect</c> blocks.
/// </summary>
/// <remarks>
/// The raw lines are kept and values are edited IN PLACE, so an unedited file round-trips byte-exact and an
/// edit preserves the file's formatting, comments and effect envelopes (same discipline as the project's .con
/// and .wst handling). Scalar/flag setters apply to EVERY wave — correct for the single-wave-per-tier ambient
/// emitters placed in levels (HIGH+MEDIUM tiers mirror each other); multi-wave vehicle engine scripts aren't
/// level-placed emitters and aren't the target of this editor.
/// </remarks>
public sealed class SoundScript
{
    private readonly List<string> _lines;   // line CONTENT only (no terminators)
    private readonly string _nl;            // the file's newline ("\r\n" or "\n")
    private readonly bool _trailingNl;      // whether the file ended with a newline

    private SoundScript(List<string> lines, string nl, bool trailingNl)
    { _lines = lines; _nl = nl; _trailingNl = trailingNl; }

    public static SoundScript Parse(string text)
    {
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        bool trailing = text.EndsWith("\n");
        string body = !trailing ? text : text.Substring(0, text.Length - nl.Length);
        var lines = body.Length == 0 ? new List<string>() : new List<string>(body.Split(new[] { nl }, System.StringSplitOptions.None));
        return new SoundScript(lines, nl, trailing);
    }

    public static SoundScript Parse(byte[] bytes) => Parse(Encoding.Latin1.GetString(bytes));

    public string ToText()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < _lines.Count; i++)
        {
            sb.Append(_lines[i]);
            if (i < _lines.Count - 1 || _trailingNl) sb.Append(_nl);
        }
        return sb.ToString();
    }

    public byte[] ToBytes() => Encoding.Latin1.GetBytes(ToText());

    // ---- read accessors (first occurrence is representative for the placed ambient emitter) ----

    /// <summary>The wav path of the first source line (after the <c>stream</c>/<c>load</c> keyword), or null.</summary>
    public string? Wav
    {
        get { foreach (var ln in _lines) { var k = KeyOf(ln); if (k == "stream" || k == "load") { var t = Tokens(ln); return t.Length >= 2 ? t[1] : null; } } return null; }
    }

    /// <summary><c>stream</c> (streamed from disk) or <c>load</c> (held in memory). Defaults to "load".</summary>
    public string SourceMode
    {
        get { foreach (var ln in _lines) { var k = KeyOf(ln); if (k == "stream" || k == "load") return k; } return "load"; }
    }

    public float Volume => FirstScalar("volume", 1f);
    public float MinDistance => FirstScalar("mindistance", 0f);

    /// <summary>Where the sound reaches silence: the second distance of the <c>Distance</c> -&gt; <c>Volume</c>
    /// <c>Ramp</c> effect (<c>param &lt;near&gt; / param &lt;far&gt; / param 1 / param -1</c>), which is how every
    /// retail ambient shapes its falloff. Null when the script has no such effect, and the engine's own rolloff from
    /// <see cref="MinDistance"/> is all there is.</summary>
    public float? MaxDistance
    {
        get
        {
            bool inEffect = false, toVolume = false, fromDistance = false;
            var ps = new List<float>();
            foreach (var ln in _lines)
            {
                var k = KeyOf(ln);
                if (k == "begineffect") { inEffect = true; toVolume = fromDistance = false; ps.Clear(); continue; }
                if (!inEffect) continue;
                if (k == "endeffect")
                {
                    if (toVolume && fromDistance && ps.Count >= 2) return ps[1];
                    inEffect = false; continue;
                }
                var t = Tokens(ln);
                if (t.Length >= 2)
                {
                    if (k == "controldestination") toVolume = t[1].Equals("Volume", System.StringComparison.OrdinalIgnoreCase);
                    else if (k == "controlsource") fromDistance = t[1].Equals("Distance", System.StringComparison.OrdinalIgnoreCase);
                    else if (k == "param" && float.TryParse(t[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pv)) ps.Add(pv);
                }
            }
            return null;
        }
    }
    public bool Loop => HasFlag("loop");
    public bool Stereo => HasFlag("stereo");

    // ---- edits (applied to every wave) ----

    public void SetVolume(float v) => SetScalar("volume", v);
    public void SetMinDistance(float d) => SetScalar("minDistance", d);
    public void SetLoop(bool on) => SetFlag("loop", on);
    public void SetStereo(bool on) => SetFlag("stereo", on);

    /// <summary>Replace the wav path on every source line, keeping its <c>stream</c>/<c>load</c> keyword.</summary>
    public void SetWav(string path)
    {
        for (int i = 0; i < _lines.Count; i++)
        {
            var k = KeyOf(_lines[i]);
            if (k == "stream" || k == "load")
            {
                var t = Tokens(_lines[i]);
                _lines[i] = LeadingWs(_lines[i]) + t[0] + " " + path;
            }
        }
    }

    // ---- internals ----

    private float FirstScalar(string keyLow, float dflt)
    {
        foreach (var ln in _lines)
            if (KeyOf(ln) == keyLow)
            {
                var t = Tokens(ln);
                if (t.Length >= 2 && float.TryParse(t[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
            }
        return dflt;
    }

    private bool HasFlag(string keyLow)
    {
        foreach (var ln in _lines) if (KeyOf(ln) == keyLow && Tokens(ln).Length == 1) return true;
        return false;
    }

    // Set a "key value" scalar on every wave: replace the existing line where present, else insert it right
    // after the wave's source line. Preserves each replaced line's key spelling + leading whitespace.
    private void SetScalar(string key, float value)
    {
        string keyLow = key.ToLowerInvariant();
        string val = value.ToString("0.######", CultureInfo.InvariantCulture);
        var ranges = WaveRanges();
        if (ranges.Count == 0) return;
        var outL = new List<string>(_lines.Count + ranges.Count);
        int idx = 0;
        foreach (var (start, end) in ranges)
        {
            for (; idx < start; idx++) outL.Add(_lines[idx]);
            int srcPos = outL.Count;            // where the source line (first line of this wave) lands
            bool replaced = false;
            for (int j = start; j < end; j++)
            {
                var ln = _lines[j];
                if (!replaced && KeyOf(ln) == keyLow) { outL.Add(LeadingWs(ln) + Tokens(ln)[0] + " " + val); replaced = true; }
                else outL.Add(ln);
            }
            if (!replaced) outL.Insert(srcPos + 1, key + " " + val);
            idx = end;
        }
        for (; idx < _lines.Count; idx++) outL.Add(_lines[idx]);
        _lines.Clear(); _lines.AddRange(outL);
    }

    // Add or remove a bare flag line (loop / stereo) on every wave.
    private void SetFlag(string key, bool on)
    {
        string keyLow = key.ToLowerInvariant();
        var ranges = WaveRanges();
        if (ranges.Count == 0) return;
        var outL = new List<string>(_lines.Count + ranges.Count);
        int idx = 0;
        foreach (var (start, end) in ranges)
        {
            for (; idx < start; idx++) outL.Add(_lines[idx]);
            int srcPos = outL.Count;
            bool present = false;
            for (int j = start; j < end; j++)
            {
                var ln = _lines[j];
                bool isFlag = KeyOf(ln) == keyLow && Tokens(ln).Length == 1;
                if (isFlag) { present = true; if (on) outL.Add(ln); }   // keep if staying on; drop if turning off
                else outL.Add(ln);
            }
            if (on && !present) outL.Insert(srcPos + 1, key);
            idx = end;
        }
        for (; idx < _lines.Count; idx++) outL.Add(_lines[idx]);
        _lines.Clear(); _lines.AddRange(outL);
    }

    // Wave ranges: [sourceLineIndex, nextSourceOrEnd). Anything before the first source line is preamble.
    private List<(int start, int end)> WaveRanges()
    {
        var ranges = new List<(int, int)>();
        int cur = -1;
        for (int i = 0; i < _lines.Count; i++)
        {
            var k = KeyOf(_lines[i]);
            if (k == "stream" || k == "load") { if (cur >= 0) ranges.Add((cur, i)); cur = i; }
        }
        if (cur >= 0) ranges.Add((cur, _lines.Count));
        return ranges;
    }

    private static string KeyOf(string line)
    {
        int i = 0; while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
        int j = i; while (j < line.Length && line[j] != ' ' && line[j] != '\t') j++;
        return line.Substring(i, j - i).ToLowerInvariant();
    }

    private static string LeadingWs(string line)
    { int i = 0; while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++; return line.Substring(0, i); }

    private static string[] Tokens(string line) => line.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
}
