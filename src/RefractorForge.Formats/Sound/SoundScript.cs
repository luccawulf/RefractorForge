using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RefractorForge.Formats.Sound;

/// <summary>One wave of a sound script as written: its place (the wave's index, the patch and detail tier it is in, its
/// 1-based source line), its source (<c>load</c>/<c>stream</c> and the path) and its properties - null where the script
/// leaves one to the engine; <see cref="MaxDistance"/> is its Distance -&gt; Volume ramp's far end.</summary>
public sealed record SoundWave(int Index, int Patch, string? Tier, string Mode, string Wav, int Line,
                               float? Volume, float? MinDistance, float? MaxDistance, int? Priority,
                               bool Loop, bool Stereo, bool DopplerOff, int Effects);

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

    /// <summary>
    /// Move where the sound reaches silence: the SECOND <c>param</c> of the Distance -&gt; Volume <c>Ramp</c>
    /// effect, the mirror of <see cref="MaxDistance"/>. Only that effect's params are touched, so a script with
    /// other effects keeps them; a script with no such ramp is left alone (there is no far distance to move, and
    /// inventing one would change how the sound falls off rather than how far it carries).
    /// </summary>
    public void SetMaxDistance(float d)
    {
        d = System.MathF.Max(1f, d);
        bool inEffect = false, toVolume = false, fromDistance = false;
        int start = -1;
        var paramLines = new List<int>();
        for (int i = 0; i < _lines.Count; i++)
        {
            var k = KeyOf(_lines[i]);
            if (k == "begineffect") { inEffect = true; toVolume = fromDistance = false; paramLines.Clear(); start = i; continue; }
            if (!inEffect) continue;
            if (k == "endeffect")
            {
                if (toVolume && fromDistance && paramLines.Count >= 2)
                {
                    int at = paramLines[1];
                    var indent = _lines[at][..(_lines[at].Length - _lines[at].TrimStart().Length)];
                    _lines[at] = indent + "param " + d.ToString("0.###", CultureInfo.InvariantCulture);
                    return;
                }
                inEffect = false; start = -1; continue;
            }
            var t = Tokens(_lines[i]);
            if (t.Length >= 2)
            {
                if (k == "controldestination") toVolume = t[1].Equals("Volume", System.StringComparison.OrdinalIgnoreCase);
                else if (k == "controlsource") fromDistance = t[1].Equals("Distance", System.StringComparison.OrdinalIgnoreCase);
            }
            if (k == "param") paramLines.Add(i);
        }
    }
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

    // ---- one wave at a time (a vehicle's script has many: engine start, idle, rev, stop...) ----

    /// <summary>Every wave of the script in file order: where its source line is, which patch and tier it is in, and
    /// its properties as written (null where the script leaves one to the engine). A wave runs from its source line to
    /// the next source line, <c>newPatch</c>, <c>#templateLevel</c> or <c>#include</c>.</summary>
    public IReadOnlyList<SoundWave> Waves
    {
        get
        {
            var waves = new List<SoundWave>();
            int patch = -1;
            string? tier = null;
            var blocks = WaveBlocks();
            int b = 0;
            for (int i = 0; i < _lines.Count && b < blocks.Count; i++)
            {
                var k = KeyOf(_lines[i]);
                if (k == "newpatch") patch++;
                else if (k == "#templatelevel") { var t = Tokens(_lines[i]); tier = t.Length >= 2 ? t[1] : null; }
                if (i != blocks[b].start) continue;
                var (start, end) = blocks[b];
                var src = Tokens(_lines[start]);
                float? Scalar(string keyLow)
                {
                    for (int j = start + 1; j < end; j++)
                        if (KeyOf(_lines[j]) == keyLow && Tokens(_lines[j]) is { Length: >= 2 } t
                            && float.TryParse(t[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
                    return null;
                }
                bool Flag(string keyLow)
                {
                    for (int j = start + 1; j < end; j++) if (KeyOf(_lines[j]) == keyLow && Tokens(_lines[j]).Length == 1) return true;
                    return false;
                }
                int effects = 0;
                for (int j = start + 1; j < end; j++) if (KeyOf(_lines[j]) == "begineffect") effects++;
                waves.Add(new SoundWave(b, System.Math.Max(0, patch), tier, src[0].ToLowerInvariant(), src.Length >= 2 ? src[1] : "", start + 1,
                                        Scalar("volume"), Scalar("mindistance"), DistanceRamp(start, end)?.Far, Scalar("priority") is { } p ? (int)p : null,
                                        Flag("loop"), Flag("stereo"), Flag("doppleroff"), effects));
                b++;
            }
            return waves;
        }
    }

    /// <summary>The scripts this one includes (<c>#include High/WillyEngine.ssc</c>), as written - a vehicle's top script
    /// includes one per detail tier.</summary>
    public IReadOnlyList<string> Includes
        => _lines.Where(l => KeyOf(l) == "#include" && Tokens(l).Length >= 2).Select(l => Tokens(l)[1]).ToList();

    public void SetVolume(int wave, float v) => SetWaveScalar(wave, "volume", v.ToString("0.######", CultureInfo.InvariantCulture));
    public void SetMinDistance(int wave, float d) => SetWaveScalar(wave, "minDistance", System.MathF.Max(0f, d).ToString("0.###", CultureInfo.InvariantCulture));
    public void SetPriority(int wave, int p) => SetWaveScalar(wave, "priority", p.ToString(CultureInfo.InvariantCulture));
    public void SetLoop(int wave, bool on) => SetWaveFlag(wave, "loop", on);

    /// <summary>Where one wave falls silent: the far distance of its own Distance -&gt; Volume ramp (see
    /// <see cref="SetMaxDistance(float)"/>); a wave with no such ramp is left alone.</summary>
    public void SetMaxDistance(int wave, float d)
    {
        var blocks = WaveBlocks();
        if (wave < 0 || wave >= blocks.Count || DistanceRamp(blocks[wave].start, blocks[wave].end) is not { } ramp) return;
        var indent = LeadingWs(_lines[ramp.FarLine]);
        _lines[ramp.FarLine] = indent + Tokens(_lines[ramp.FarLine])[0] + " " + System.MathF.Max(1f, d).ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>Point one wave at another sound file, keeping its <c>stream</c>/<c>load</c> keyword.</summary>
    public void SetWav(int wave, string path)
    {
        var blocks = WaveBlocks();
        if (wave < 0 || wave >= blocks.Count) return;
        int at = blocks[wave].start;
        _lines[at] = LeadingWs(_lines[at]) + Tokens(_lines[at])[0] + " " + path;
    }

    /// <summary>A copy of wave <paramref name="wave"/> - its source line and properties, effects included - put right after
    /// it; the new wave's index (<paramref name="wave"/> + 1), or -1 when there is no such wave.</summary>
    public int CopyWave(int wave)
    {
        var blocks = WaveBlocks();
        if (wave < 0 || wave >= blocks.Count) return -1;
        var (start, end) = OwnLines(blocks[wave]);
        var copy = _lines.GetRange(start, end - start);
        _lines.InsertRange(end, copy);
        return wave + 1;
    }

    /// <summary>Take wave <paramref name="wave"/> out: its source line, properties and effects. The comments and blank
    /// lines after it stay - they head the next wave.</summary>
    public bool RemoveWave(int wave)
    {
        var blocks = WaveBlocks();
        if (wave < 0 || wave >= blocks.Count) return false;
        var (start, end) = OwnLines(blocks[wave]);
        _lines.RemoveRange(start, end - start);
        return true;
    }

    /// <summary>A wave's own lines: its block without the blank and comment lines at its end, which head what follows
    /// (<c>### Main ###</c> between two waves belongs to the second).</summary>
    private (int start, int end) OwnLines((int start, int end) block)
    {
        int end = block.end;
        while (end - 1 > block.start && (_lines[end - 1].Trim() is var t && (t.Length == 0 || t.StartsWith('#') || t.StartsWith('*')))) end--;
        return (block.start, end);
    }

    private void SetWaveScalar(int wave, string key, string value)
    {
        var blocks = WaveBlocks();
        if (wave < 0 || wave >= blocks.Count) return;
        var (start, end) = blocks[wave];
        string keyLow = key.ToLowerInvariant();
        for (int j = start + 1; j < end; j++)
            if (KeyOf(_lines[j]) == keyLow) { _lines[j] = LeadingWs(_lines[j]) + Tokens(_lines[j])[0] + " " + value; return; }
        _lines.Insert(start + 1, LeadingWs(_lines[start]) + key + " " + value);
    }

    private void SetWaveFlag(int wave, string key, bool on)
    {
        var blocks = WaveBlocks();
        if (wave < 0 || wave >= blocks.Count) return;
        var (start, end) = blocks[wave];
        string keyLow = key.ToLowerInvariant();
        for (int j = end - 1; j > start; j--)
            if (KeyOf(_lines[j]) == keyLow && Tokens(_lines[j]).Length == 1)
            {
                if (on) return;
                _lines.RemoveAt(j);
                return;
            }
        if (on) _lines.Insert(start + 1, LeadingWs(_lines[start]) + key);
    }

    /// <summary>The Distance -&gt; Volume ramp inside [start, end): its far distance and the line that holds it.</summary>
    private (float Far, int FarLine)? DistanceRamp(int start, int end)
    {
        bool inEffect = false, toVolume = false, fromDistance = false;
        var paramLines = new List<int>();
        for (int i = start; i < end; i++)
        {
            var k = KeyOf(_lines[i]);
            if (k == "begineffect") { inEffect = true; toVolume = fromDistance = false; paramLines.Clear(); continue; }
            if (!inEffect) continue;
            if (k == "endeffect")
            {
                if (toVolume && fromDistance && paramLines.Count >= 2
                    && float.TryParse(Tokens(_lines[paramLines[1]]).ElementAtOrDefault(1), NumberStyles.Float, CultureInfo.InvariantCulture, out var far))
                    return (far, paramLines[1]);
                inEffect = false; continue;
            }
            var t = Tokens(_lines[i]);
            if (t.Length >= 2)
            {
                if (k == "controldestination") toVolume = t[1].Equals("Volume", System.StringComparison.OrdinalIgnoreCase);
                else if (k == "controlsource") fromDistance = t[1].Equals("Distance", System.StringComparison.OrdinalIgnoreCase);
            }
            if (k == "param") paramLines.Add(i);
        }
        return null;
    }

    /// <summary>Each wave's lines: its source line up to the next source line, <c>newPatch</c>, <c>#templateLevel</c> or
    /// <c>#include</c>.</summary>
    private List<(int start, int end)> WaveBlocks()
    {
        var blocks = new List<(int, int)>();
        int cur = -1;
        for (int i = 0; i < _lines.Count; i++)
        {
            var k = KeyOf(_lines[i]);
            bool source = k == "stream" || k == "load";
            if (source || k == "newpatch" || k == "#templatelevel" || k == "#include")
            {
                if (cur >= 0) blocks.Add((cur, i));
                cur = source ? i : -1;
            }
        }
        if (cur >= 0) blocks.Add((cur, _lines.Count));
        return blocks;
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
