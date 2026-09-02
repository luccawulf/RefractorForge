using System.Security.Cryptography;

namespace RefractorForge.Formats.Rfa;

/// <summary>
/// What changed between two archives - typically a level and its patch, or two versions of a mod's
/// <c>objects.rfa</c>. Entries are matched by name, case-insensitively, the way the engine's own file system
/// resolves them; "changed" means the bytes differ, not merely the size, since a re-saved file with the same
/// length is still a different file.
/// </summary>
public static class ArchiveDiff
{
    public enum Kind { OnlyInA, OnlyInB, Changed, Same }

    public sealed record Line(string Name, Kind Kind, int SizeA, int SizeB);

    public sealed class Result
    {
        public string PathA { get; init; } = "";
        public string PathB { get; init; } = "";
        public List<Line> Lines { get; } = new();
        public int OnlyInA => Lines.Count(l => l.Kind == Kind.OnlyInA);
        public int OnlyInB => Lines.Count(l => l.Kind == Kind.OnlyInB);
        public int Changed => Lines.Count(l => l.Kind == Kind.Changed);
        public int Same => Lines.Count(l => l.Kind == Kind.Same);
        public bool Identical => OnlyInA == 0 && OnlyInB == 0 && Changed == 0;

        /// <summary>A plain-text report, the shape people paste into a bug thread.</summary>
        public string ToReport()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"A: {PathA}");
            sb.AppendLine($"B: {PathB}");
            sb.AppendLine($"{OnlyInA} only in A, {OnlyInB} only in B, {Changed} changed, {Same} identical");
            sb.AppendLine();
            foreach (var l in Lines.Where(l => l.Kind != Kind.Same))
                sb.AppendLine(l.Kind switch
                {
                    Kind.OnlyInA => $"-  {l.Name}  ({l.SizeA:N0} B)",
                    Kind.OnlyInB => $"+  {l.Name}  ({l.SizeB:N0} B)",
                    _ => $"~  {l.Name}  ({l.SizeA:N0} -> {l.SizeB:N0} B)",
                });
            return sb.ToString();
        }
    }

    public static Result Compare(string pathA, string pathB, bool compareBytes = true)
    {
        var a = new RefractorFlatArchive(pathA);
        var b = new RefractorFlatArchive(pathB);
        return Compare(a, b, pathA, pathB, compareBytes);
    }

    public static Result Compare(RefractorFlatArchive a, RefractorFlatArchive b, string labelA = "A", string labelB = "B",
                                 bool compareBytes = true)
    {
        var result = new Result { PathA = labelA, PathB = labelB };
        var byB = new Dictionary<string, RefractorFlatArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in b.Entries) byB[Norm(e.Name)] = e;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ea in a.Entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            var key = Norm(ea.Name);
            seen.Add(key);
            if (!byB.TryGetValue(key, out var eb))
            { result.Lines.Add(new Line(ea.Name, Kind.OnlyInA, ea.UncompressedSize, 0)); continue; }

            bool same = ea.UncompressedSize == eb.UncompressedSize;
            if (same && compareBytes)
            {
                // Sizes match; decide by content. Hash rather than hold two big entries side by side.
                same = Hash(a.Read(ea)).AsSpan().SequenceEqual(Hash(b.Read(eb)));
            }
            result.Lines.Add(new Line(ea.Name, same ? Kind.Same : Kind.Changed, ea.UncompressedSize, eb.UncompressedSize));
        }
        foreach (var eb in b.Entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            if (!seen.Contains(Norm(eb.Name)))
                result.Lines.Add(new Line(eb.Name, Kind.OnlyInB, 0, eb.UncompressedSize));
        return result;
    }

    private static string Norm(string n) => n.Replace('\\', '/');
    private static byte[] Hash(byte[] data) => SHA256.HashData(data);
}
