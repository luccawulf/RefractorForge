using System.Numerics;
using NAudio.Wave;
using RefractorForge.Formats.Sound;

// In-editor preview of a level's placed AMBIENT sounds. Deliberately uses NAudio's HIGHEST-LEVEL, most battle-tested
// path and nothing custom: each placed emitter's wav bytes are written to a temp file once, then played per-voice via
// `AudioFileReader` (which decodes 8/16-bit PCM, normalizes to [-1,1], and exposes a simple .Volume) through its own
// `WaveOutEvent` (the OS mixes the handful of voices). An earlier hand-rolled mixer/sample-provider pipeline corrupted
// the audio into raw 0-255 byte values; this removes all of that. Volume tracks 3D distance to the ring; the nearest
// couple play; each plays through ONCE (no looping) and stops the instant you leave the ring.
sealed class SoundPlayback : System.IDisposable
{
    const int MaxVoices = 2;
    const int MaxStartsPerFrame = 1;
    const int MaxWavBytes = 30_000_000;

    private readonly System.Func<SoundEmitter, byte[]?> _resolve;
    private readonly Dictionary<string, string?> _tempPath = new(System.StringComparer.OrdinalIgnoreCase);   // template -> temp wav file (null = unresolvable, never retry)
    private readonly Dictionary<string, Voice> _voices = new(System.StringComparer.OrdinalIgnoreCase);
    private double _clock, _lastSummary = -10;
    private bool _enabled;

    public float MasterVolume = 0.85f;

    public const string BuildTag = "SND-15";
    public bool Diagnostics = false;   // flip to true to tee voice events to sound_debug.log when troubleshooting
    private static readonly string LogPath = System.IO.Path.Combine(System.AppContext.BaseDirectory, "sound_debug.log");
    private void Log(string m)
    {
        if (!Diagnostics) return;
        var line = $"[snd {BuildTag} t={_clock:0.0}] {m}";
        System.Console.WriteLine(line);
        try { System.IO.File.AppendAllText(LogPath, line + System.Environment.NewLine); } catch { }
    }

    private sealed class Voice
    {
        public string Template = "";
        public WaveOutEvent? Out;
        public AudioFileReader? Reader;
    }

    public SoundPlayback(System.Func<SoundEmitter, byte[]?> resolve) => _resolve = resolve;

    public bool Enabled => _enabled;

    public void SetEnabled(bool on)
    {
        if (on == _enabled) return;
        _enabled = on;
        if (on) { try { System.IO.File.WriteAllText(LogPath, $"=== sound session start, build {BuildTag} ==={System.Environment.NewLine}"); } catch { } Log("ENABLED"); }
        else { Log("DISABLED"); StopAll(); }
    }

    public void Update(Vector3 camPos, System.Collections.Generic.IEnumerable<(SoundEmitter Em, Vector3 Pos, float Radius)> placed, double dt)
    {
        if (!_enabled) return;
        try { UpdateCore(camPos, placed, dt); }
        catch (System.Exception ex) { Log($"update error (disabling): {ex.GetType().Name} {ex.Message}"); StopAll(); _enabled = false; }
    }

    private void UpdateCore(Vector3 camPos, System.Collections.Generic.IEnumerable<(SoundEmitter Em, Vector3 Pos, float Radius)> placed, double dt)
    {
        _clock += dt;

        // nearest placement (3D) per template within its ring
        var nearest = new Dictionary<string, (float D, float Radius, SoundEmitter Em)>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var (em, pos, radius) in placed)
        {
            if (em.Script is null || !em.Script.Loop || radius <= 0f) continue;
            float d = Vector3.Distance(camPos, pos);
            if (d > radius) continue;
            if (!nearest.TryGetValue(em.Template, out var cur) || d < cur.D) nearest[em.Template] = (d, radius, em);
        }

        // choose up to MaxVoices, retaining already-playing in-range templates first (hysteresis -> no churn)
        var chosen = new List<string>();
        foreach (var kv in _voices)
            if (kv.Value.Out is not null && nearest.ContainsKey(kv.Key) && chosen.Count < MaxVoices) chosen.Add(kv.Key);
        foreach (var t in nearest.Keys.OrderBy(k => nearest[k].D))
            if (chosen.Count < MaxVoices && !chosen.Contains(t)) chosen.Add(t);

        int started = 0;
        bool dueSummary = _clock - _lastSummary >= 1.0;
        var summary = dueSummary ? new System.Text.StringBuilder($"inRange(templates)={nearest.Count} chosen={chosen.Count} voices={_voices.Count} ") : null;
        foreach (var t in chosen)
        {
            var (d, radius, em) = nearest[t];
            float fade = System.Math.Clamp(1f - d / radius, 0f, 1f);
            float vol = (em.Script!.Volume <= 0f ? 1f : em.Script.Volume) * fade * MasterVolume;

            if (_voices.TryGetValue(t, out var v))
            {
                if (v.Reader is not null) { try { v.Reader.Volume = vol; } catch { } }
                summary?.Append($"| {t} d={d:0.0}/{radius:0} vol={vol:0.00} state={(v.Out?.PlaybackState.ToString() ?? "-")} ");
            }
            else if (started < MaxStartsPerFrame)
            {
                StartVoice(t, em, vol);
                started++;
            }
        }
        if (dueSummary) { _lastSummary = _clock; if (nearest.Count > 0) Log(summary!.ToString()); }

        foreach (var key in _voices.Keys.ToList())
            if (!chosen.Contains(key)) DropVoice(key);
    }

    private void StartVoice(string template, SoundEmitter em, float vol)
    {
        string? path = GetTempPath(em);
        if (path is null) { _voices[template] = new Voice { Template = template }; return; }   // unresolvable -> placeholder, never retry
        try
        {
            var reader = new AudioFileReader(path) { Volume = vol };   // decodes 8/16-bit PCM, normalizes, handles rate
            var outDev = new WaveOutEvent { DesiredLatency = 150 };
            outDev.Init(reader);
            outDev.Play();
            _voices[template] = new Voice { Template = template, Out = outDev, Reader = reader };
            Log($"START {template} vol={vol:0.00} ({reader.WaveFormat.SampleRate}Hz {reader.WaveFormat.Channels}ch {reader.TotalTime.TotalSeconds:0.0}s) (voices={_voices.Count})");
        }
        catch (System.Exception ex) { Log($"START FAILED {template}: {ex.GetType().Name} {ex.Message}"); _voices[template] = new Voice { Template = template }; }
    }

    private void DropVoice(string key)
    {
        if (!_voices.TryGetValue(key, out var v)) return;
        try { v.Out?.Stop(); v.Out?.Dispose(); } catch { }
        try { v.Reader?.Dispose(); } catch { }
        _voices.Remove(key);
        if (v.Out is not null) Log($"DROP {key} (left ring)");
    }

    // Resolve the emitter's wav bytes and stash them in a temp file AudioFileReader can open. Cached per template.
    private string? GetTempPath(SoundEmitter em)
    {
        if (_tempPath.TryGetValue(em.Template, out var cached)) return cached;
        string? path = null;
        try
        {
            byte[]? bytes = _resolve(em);
            if (bytes is not null && bytes.Length >= 64 && bytes.Length <= MaxWavBytes)
            {
                string safe = string.Concat(em.Template.Split(System.IO.Path.GetInvalidFileNameChars()));
                path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rf_snd_{safe}.wav");
                System.IO.File.WriteAllBytes(path, bytes);
            }
        }
        catch (System.Exception ex) { Log($"resolve/write failed {em.Template}: {ex.GetType().Name} {ex.Message}"); path = null; }
        _tempPath[em.Template] = path;
        Log(path is null ? $"resolve MISS {em.Template}" : $"resolved {em.Template} -> {path}");
        return path;
    }

    private void StopAll()
    {
        foreach (var k in _voices.Keys.ToList()) DropVoice(k);
        _voices.Clear();
    }

    public void Dispose() => StopAll();
}
