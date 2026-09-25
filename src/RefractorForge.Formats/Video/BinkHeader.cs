using System;
using System.Buffers.Binary;
using System.IO;

namespace RefractorForge.Formats.Video;

/// <summary>
/// The first 44 bytes of a Bink video (<c>.bik</c>): its signature, frame count, size, frame rate, flags and sound tracks.
///
/// Bink 1 files start "BIK" and a revision letter, Bink 2 files "KB2" and one. The player both games ship
/// (<c>binkw32.dll</c> 1.5J, 2002 - the same file in BF1942 and BF Vietnam) compares the first four bytes against
/// BIKf, BIKg, BIKh and BIKi and refuses anything else, so a video RAD's current tools write as Bink 2, or as a later
/// Bink 1 revision, shows in no menu and on no screen. Every retail and community video is BIKi.
/// </summary>
public sealed record BinkHeader(string Signature, int Frames, int LargestFrame, int Width, int Height,
                                int FpsNumerator, int FpsDenominator, uint Flags, int AudioTracks, long FileSize)
{
    /// <summary>Bink 1's grayscale video (no colour planes): Battlegroup42's menu background is one.</summary>
    public const uint GrayscaleFlag = 0x20000;
    /// <summary>An alpha plane before the colour planes.</summary>
    public const uint AlphaFlag = 0x100000;

    /// <summary>The four signatures the games' player opens, in the order it tests them.</summary>
    public static readonly string[] GameSignatures = { "BIKf", "BIKg", "BIKh", "BIKi" };

    public bool IsBink1 => Signature.StartsWith("BIK", StringComparison.Ordinal);
    public bool IsBink2 => Signature.StartsWith("KB2", StringComparison.Ordinal);
    public char Revision => Signature[3];
    /// <summary>Whether binkw32.dll 1.5J - the player both games ship - opens it.</summary>
    public bool GamePlays => Array.IndexOf(GameSignatures, Signature) >= 0;
    public double Fps => FpsDenominator > 0 ? (double)FpsNumerator / FpsDenominator : 0;
    public double Seconds => Fps > 0 ? Frames / Fps : 0;
    public bool Grayscale => (Flags & GrayscaleFlag) != 0;
    public bool HasAlpha => (Flags & AlphaFlag) != 0;

    /// <summary>What the header says, in words: "BIKi 320 x 180, 25 fps, 1892 frames (75.7 s), no sound".</summary>
    public string Describe()
    {
        string sound = AudioTracks switch { 0 => "no sound", 1 => "1 sound track", _ => $"{AudioTracks} sound tracks" };
        return $"{Signature} {Width} x {Height}, {Fps:0.##} fps, {Frames} frames ({Seconds:0.#} s), {sound}"
               + (Grayscale ? ", grayscale" : "") + (HasAlpha ? ", alpha" : "");
    }

    /// <summary>Read a header from the start of a file; null when it is not a Bink video or too short to be one.</summary>
    public static BinkHeader? Read(ReadOnlySpan<byte> head, long fileSize)
    {
        if (head.Length < 44) return null;
        bool bink1 = head[0] == 'B' && head[1] == 'I' && head[2] == 'K';
        bool bink2 = head[0] == 'K' && head[1] == 'B' && head[2] == '2';
        if (!bink1 && !bink2) return null;
        char rev = (char)head[3];
        if (rev < 'a' || rev > 'z') return null;
        return new BinkHeader(bink1 ? "BIK" + rev : "KB2" + rev,
            Frames: U(head, 8), LargestFrame: U(head, 12), Width: U(head, 20), Height: U(head, 24),
            FpsNumerator: U(head, 28), FpsDenominator: U(head, 32), Flags: BinaryPrimitives.ReadUInt32LittleEndian(head[36..]),
            AudioTracks: U(head, 40), FileSize: fileSize);
    }

    private static int U(ReadOnlySpan<byte> b, int at) => (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(b[at..]), int.MaxValue);

    /// <summary>Read the header of a file on disk; null when it is not a Bink video (or cannot be read).</summary>
    public static BinkHeader? Read(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var head = new byte[44];
            int n = 0;
            while (n < head.Length) { int r = f.Read(head, n, head.Length - n); if (r <= 0) break; n += r; }
            return n < head.Length ? null : Read(head, f.Length);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
