using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RefractorForge.Viewer;

/// <summary>
/// Captures everything written to <see cref="Console"/> into an in-memory ring buffer so the editor can show it in
/// an in-app "Log / Errors" window (like Battlecraft Vietnam's "Load Errors" box) instead of relying on the
/// background CMD window. Output is TEED — it still goes to the real console too (harmless; the console is hidden in
/// normal use). Thread-safe: background threads (collab, loaders) may write concurrently.
/// </summary>
internal static class ConsoleLog
{
    private static readonly object _gate = new();
    private static readonly List<string> _lines = new();
    private const int Cap = 4000;

    public static void Install()
    {
        try { Console.SetOut(new TeeWriter(Console.Out)); } catch { }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool AttachConsole(int dwProcessId);
    private const int ATTACH_PARENT_PROCESS = -1;

    /// <summary>For the headless --relay server (a WinExe has no console of its own): attach to the launching
    /// terminal's console so its output + admin-command input work. No-op if there's no parent console.</summary>
    public static void AttachParentConsole() { try { AttachConsole(ATTACH_PARENT_PROCESS); } catch { } }

    private static void Add(string line)
    {
        lock (_gate) { _lines.Add(line); if (_lines.Count > Cap) _lines.RemoveRange(0, _lines.Count - Cap); }
    }

    /// <summary>Current line count — used as a marker to find the lines produced by a level load.</summary>
    public static int Count { get { lock (_gate) return _lines.Count; } }

    public static List<string> Snapshot() { lock (_gate) return new List<string>(_lines); }

    /// <summary>The lines added since <paramref name="mark"/> (a previous <see cref="Count"/>).</summary>
    public static List<string> Since(int mark)
    {
        lock (_gate) { int m = Math.Clamp(mark, 0, _lines.Count); return _lines.GetRange(m, _lines.Count - m); }
    }

    public static void Clear() { lock (_gate) _lines.Clear(); }

    /// <summary>Heuristic: does a log line look like an error/warning (so the Log box auto-pops + highlights it)?</summary>
    public static bool LooksLikeError(string line)
    {
        var l = line.ToLowerInvariant();
        return l.Contains("error") || l.Contains("fail") || l.Contains("exception")
            || l.Contains("not found") || l.Contains("no resolvable") || l.Contains("no matching")
            || l.Contains("missing") || l.Contains("could not") || l.Contains("can't") || l.Contains("cannot");
    }

    // TextWriter that forwards every char to the real console AND accumulates whole lines into the buffer.
    // Overriding Write(char) is enough: TextWriter routes Write(string)/WriteLine(...) through it.
    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _inner;
        private readonly StringBuilder _cur = new();
        public TeeWriter(TextWriter inner) { _inner = inner; }
        public override Encoding Encoding => _inner.Encoding;
        public override void Write(char c)
        {
            try { _inner.Write(c); } catch { }
            if (c == '\n') { ConsoleLog.Add(_cur.ToString().TrimEnd('\r')); _cur.Clear(); }
            else if (c != '\r') _cur.Append(c);
        }
    }
}
