using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace RefractorForge.Formats.Menu;

/// <summary>The value rules the setter and the writer share, so a value <see cref="MemeField.Set"/> accepts is one the
/// writer writes, and one assigned raw is refused by the writer for the same reason.</summary>
internal static class MemeValues
{
    /// <summary>Any C# integer (and bool, as the reference writer's Python counts True as 1); floating-point values are
    /// not integers - 1.7 must not silently become 1.</summary>
    public static bool TryInteger(object? v, out long x)
    {
        switch (v)
        {
            case int i: x = i; return true;
            case byte b: x = b; return true;
            case sbyte sb: x = sb; return true;
            case short s: x = s; return true;
            case ushort us: x = us; return true;
            case uint ui: x = ui; return true;
            case long l: x = l; return true;
            case ulong ul when ul <= long.MaxValue: x = (long)ul; return true;
            case bool bo: x = bo ? 1 : 0; return true;
            default: x = 0; return false;
        }
    }

    public static bool TryByte(object? v, out byte b)
    {
        b = 0;
        if (!TryInteger(v, out var x) || x is < 0 or > 255) return false;
        b = (byte)x;
        return true;
    }

    /// <summary>-2^31..2^32-1: 2^31 and up are written as the same 32 bits and read back negative.</summary>
    public static bool TryI32(object? v, out int i)
    {
        i = 0;
        if (!TryInteger(v, out var x) || x < int.MinValue || x > uint.MaxValue) return false;
        i = unchecked((int)x);
        return true;
    }

    public static bool TryF32(object? v, out float f, out string? why)
    {
        why = null;
        switch (v)
        {
            case float x: f = x; return true;
            case double d:
                f = (float)d;
                if (float.IsInfinity(f) && double.IsFinite(d)) { why = $"{d} does not fit in an f32"; return false; }
                return true;
            case decimal m: f = (float)m; return true;
        }
        if (TryInteger(v, out var n)) { f = n; return true; }
        f = 0;
        why = $"{v ?? "null"} is not a number";
        return false;
    }

    /// <summary>Why a string cannot be written in a field of <paramref name="token"/>, or null. Header names, s8
    /// handles and str32 / wstr32 values reach the engine as C strings (str32 BFV 0x00878E80 and wstr32 0x00878F20
    /// allocate len+1 and keep only the pointer), so an embedded NUL would cut them short in game.</summary>
    public static string? StringProblem(MemeToken token, string s)
    {
        if (s.Contains('\0')) return "contains a NUL character; the engine would cut it off there";
        if (token is MemeToken.S8 or MemeToken.Str32)
        {
            foreach (var c in s)
                if (c > 0xFF) return $"'{Clip(s)}' is not latin1 (a byte per character)";
            if (token == MemeToken.S8 && s.Length > 255) return $"an s8 string holds at most 255 bytes, not {s.Length}";
        }
        return null;
    }

    public static string? NameProblem(string name, int max, string what)
    {
        foreach (var c in name)
            if (c > 0xFF) return $"{what} '{Clip(name)}' is not latin1 (a byte per character)";
        if (name.Length >= max)
            return $"{what} of {name.Length} bytes; the engine reads at most {max - 1}: '{Clip(name)}'";
        if (name.Contains('\0')) return $"{what} contains a NUL character; the engine would cut it off there: '{Clip(name)}'";
        return null;
    }

    public static string Clip(string s) => s.Length <= 40 ? s : s[..40] + "...";

    /// <summary>A lone surrogate (half of a pair) - kept as read, but a plausibility warning, and JSON cannot carry it
    /// as text.</summary>
    public static bool HasLoneSurrogate(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { i++; continue; }
            if (char.IsSurrogate(c)) return true;
        }
        return false;
    }
}

/// <summary>
/// Sibling chains nest one record per node, so reading, writing and walking a menu file recurse as deep as its longest
/// chain (retail and community files never pass 278 levels). Work runs on the caller's stack, and when that runs short
/// (<see cref="RuntimeHelpers.EnsureSufficientExecutionStack"/>) it is redone from the start on a thread with a large
/// stack reservation, the way the reference implementation raises its recursion limit.
/// </summary>
internal static class DeepStack
{
    private const int BigStack = 512 * 1024 * 1024;

    public static T Run<T>(Func<T> work)
    {
        try { return work(); }
        catch (InsufficientExecutionStackException) { }
        T result = default!;
        ExceptionDispatchInfo? error = null;
        var t = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception e) { error = ExceptionDispatchInfo.Capture(e); }
        }, BigStack) { Name = "Meme deep stack" };
        t.Start();
        t.Join();
        error?.Throw();
        return result;
    }

    public static void Run(Action work) => Run(() => { work(); return 0; });
}
