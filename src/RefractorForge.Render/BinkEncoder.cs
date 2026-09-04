using System;
using System.Diagnostics;
using System.IO;

namespace RefractorForge.Render;

/// <summary>
/// Writes the Bink 1 <c>.bik</c> videos Refractor plays. FFmpeg can DECODE Bink but cannot encode it, so the only
/// thing that can produce one is RAD's own compressor (the free RAD Video Tools): FFmpeg prepares an AVI the
/// compressor can read - RAD takes AVI/QuickTime/image sequences, not mp4 - and <c>radvideo64.exe Binkc</c> makes
/// the .bik, carrying the sound across with it.
///
/// Waiting for it right is the whole difficulty. The compressor writes to <c>&lt;output&gt;.tmp</c> and only renames
/// it at the very end, so the destination does not exist for the entire run; and it never exits on its own, because
/// its progress window stays up when the work is finished. Waiting on a fixed clock (the first attempt) killed real
/// videos part-way through and left the .tmp behind. So this watches two independent signs of life - the file
/// growing and the process burning CPU - and stops only when the .tmp is gone, the real file is there, and both have
/// gone quiet.
/// </summary>
public static class BinkEncoder
{
    /// <summary>Where RAD Video Tools installs. Null when it is not on this machine.</summary>
    public static string? FindRadVideo()
    {
        foreach (var c in new[]
                 {
                     @"C:\Program Files (x86)\RADVideo\radvideo64.exe", @"C:\Program Files\RADVideo\radvideo64.exe",
                     @"C:\Program Files (x86)\RADVideo\radvideo32.exe", @"C:\Program Files\RADVideo\radvideo32.exe",
                 })
            if (File.Exists(c)) return c;
        return null;
    }

    /// <summary>How the wait ended - the part worth asserting on.</summary>
    public enum Result { Ok, NoTools, SourceUnreadable, CompressorStalled, Failed }

    /// <param name="progress">Called with the bytes written so far, a few times a second. Never on the caller's thread.</param>
    /// <param name="maxWidth">Scale the video down to at most this wide, keeping its shape (0 = leave it alone).
    /// A decal is a texture on a wall, and Bink is not a small format: the game's own <c>background.bik</c> is
    /// 320x240 and 5 MB a minute, while an untouched 720p minute comes out at nearly 80 MB - too big to ship inside
    /// a map. 512 keeps a screen sharp at the distance anyone reads one from.</param>
    /// <summary>The FFmpeg command that makes RAD's intermediate: MJPEG video, PCM audio at the rate and channel
    /// count asked for. Its own function so the choice can be checked without running anything.</summary>
    public static string FfmpegArgs(string src, string avi, int maxWidth = 512, int audioRate = 22050, int audioChannels = 1)
    {
        // -2 on the height keeps the aspect and lands on an even number, which the yuv420p intermediate needs.
        string scale = maxWidth > 0 ? $"-vf \"scale='min(iw,{maxWidth})':-2\" " : "";
        return $"-hide_banner -y -i \"{src}\" {scale}-c:v mjpeg -q:v 3 -pix_fmt yuvj420p -c:a pcm_s16le -ar {audioRate} -ac {Math.Clamp(audioChannels, 1, 2)} \"{avi}\"";
    }

    /// <param name="audioRate">Sample rate of the .bik's audio track: 22050 (the game's 22khz tier) or 44100.</param>
    /// <param name="audioChannels">1 for mono, 2 for stereo.</param>
    public static Result Convert(string ffmpegExe, string radExe, string src, string dstBik,
                                 Action<long>? progress, out string error, int maxMinutes = 30, int maxWidth = 512,
                                 int audioRate = 22050, int audioChannels = 1)
    {
        error = "";
        if (!File.Exists(ffmpegExe) || !File.Exists(radExe)) { error = "FFmpeg or RAD Video Tools is missing."; return Result.NoTools; }
        if (!File.Exists(src)) { error = "The source video is gone."; return Result.SourceUnreadable; }

        var avi = Path.Combine(Path.GetTempPath(), "rf_bik_" + Guid.NewGuid().ToString("N") + ".avi");
        var tmp = dstBik + ".tmp";
        try
        {
            // MJPEG + PCM in an AVI: RAD reads it, it is faithful enough as an intermediate, and the audio survives
            // into the .bik at the rate and channel count chosen.
            RunQuiet(ffmpegExe, FfmpegArgs(src, avi, maxWidth, audioRate, audioChannels), 30 * 60 * 1000);
            if (!File.Exists(avi) || new FileInfo(avi).Length < 1024) { error = "FFmpeg could not read that video."; return Result.SourceUnreadable; }

            Del(dstBik); Del(tmp);
            var psi = new ProcessStartInfo(radExe, $"Binkc \"{avi}\" \"{dstBik}\"") { UseShellExecute = false };
            using var proc = Process.Start(psi)!;

            long lastSeen = -2; double lastCpu = -1; int stall = 0, done = 0;
            var work = Stopwatch.StartNew();
            while (!proc.HasExited && work.Elapsed.TotalMinutes < maxMinutes)
            {
                System.Threading.Thread.Sleep(250);
                long outSize = Size(dstBik), tmpSize = Size(tmp);
                long seen = outSize >= 0 ? outSize : tmpSize;
                progress?.Invoke(Math.Max(seen, 0));
                double cpu = lastCpu;
                try { proc.Refresh(); cpu = proc.TotalProcessorTime.TotalSeconds; } catch { }
                // 0.25 s of CPU in a 250 ms tick is real work; the idle window ticks over at a hundredth of that.
                bool working = seen != lastSeen || cpu > lastCpu + 0.25;
                if (working) { stall = 0; lastSeen = seen; lastCpu = cpu; } else stall++;
                if (outSize > 0 && tmpSize < 0 && stall >= 4) { if (++done >= 2) break; } else done = 0;
                if (stall > 480) break;      // two minutes with neither the file nor the CPU moving
            }
            bool stalled = !proc.HasExited && Size(dstBik) < 64;
            try { if (!proc.HasExited) proc.Kill(); } catch { }
            Del(tmp);                        // never leave a half-written file behind

            long final = Size(dstBik);
            if (final < 64)
            {
                error = stalled
                    ? $"The Bink compressor stopped responding after {work.Elapsed.TotalSeconds:0} s."
                    : "The Bink compressor produced nothing.";
                return Result.CompressorStalled;
            }
            progress?.Invoke(final);
            return Result.Ok;
        }
        catch (Exception ex) { error = ex.Message; return Result.Failed; }
        finally { Del(avi); }
    }

    private static long Size(string f) { try { return File.Exists(f) ? new FileInfo(f).Length : -1; } catch { return -1; } }
    private static void Del(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }

    private static void RunQuiet(string exe, string args, int timeoutMs)
    {
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        p.StandardError.ReadToEnd();
        p.WaitForExit(timeoutMs);
    }
}
