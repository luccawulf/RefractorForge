using System.Runtime.InteropServices;

namespace RefractorForge.Formats;

/// <summary>
/// <c>lexiconAll.dat</c>: a mod's text table, in the mod's root folder next to its <c>init.con</c>. Each row is a key
/// and its text in every language the game ships. A mod's file holds only its own keys - XPack1's has 126 rows, the
/// base game's 1,693 - so a key it lacks is found further down the mod chain. The key of a vehicle's name is its
/// template (BF1942 <c>Willy</c>, XPack2 <c>Greyhound</c> = "M8 Greyhound", BF Vietnam <c>uh1transport</c> = "Huey
/// Slick"); retail keys differ from their templates in case, so a lookup ignores it.
/// <para>The layout, which every retail file (both games, both expansions) and 99 of 121 community lexicons
/// round-trip byte-exact through: <c>u32</c> row count and <c>u32</c> column count; then the rows, cell after cell,
/// each a null-terminated UTF-16LE string - the first row the header (<c>LANGUAGE</c>, then the language names:
/// English, French, Italian, Spanish, German, Japanese, Korean, Chinese; BF Vietnam has Chinese before Korean, XPack2
/// a tenth Notes column); then a size table, one <c>u32</c> per column: the key column's characters, every other
/// column's bytes, terminators counted. Nineteen community files (FHR and DC_Final_Coop among them) carry a table a column short, so a
/// wrong table is read past (<see cref="SizeTableMatches"/>) and written right.</para>
/// </summary>
public sealed class Lexicon
{
    public const string FileName = "lexiconAll.dat";
    public const string KeyTitle = "LANGUAGE";

    /// <summary>The retail header of each game, after <see cref="KeyTitle"/>.</summary>
    public static readonly IReadOnlyList<string> Bf1942Languages = new[] { "English", "French", "Italian", "Spanish", "German", "Japanese", "Korean", "Chinese" };
    public static readonly IReadOnlyList<string> BfvLanguages = new[] { "English", "French", "Italian", "Spanish", "German", "Japanese", "Chinese", "Korean" };

    /// <summary>The header row: the key column's title, then each language, in the file's order.</summary>
    public List<string> Header { get; }

    /// <summary>Every row after the header: the key, then one text per language - as many cells as the header has.
    /// Order and duplicates are kept (retail repeats keys: BF1942 has 38 twice).</summary>
    public List<string[]> Rows { get; } = new();

    /// <summary>Whether the size table at the end of the file said what the rows hold.</summary>
    public bool SizeTableMatches { get; private set; } = true;

    public IEnumerable<string> Languages => Header.Skip(1);

    public Lexicon(IEnumerable<string> languages)
    {
        Header = new List<string> { KeyTitle };
        Header.AddRange(languages);
        if (Header.Count < 2) throw new ArgumentException("a lexicon needs at least one language", nameof(languages));
    }

    /// <summary>An empty lexicon with the header of the game's own.</summary>
    public static Lexicon For(bool isVietnam) => new(isVietnam ? BfvLanguages : Bf1942Languages);

    /// <summary>Read a <c>lexiconAll.dat</c>. Throws <see cref="InvalidDataException"/> when the counts are not a
    /// lexicon's or the rows end before the count says (two community files are cut short that way).</summary>
    public static Lexicon Read(byte[] bytes)
    {
        if (bytes.Length < 8) throw new InvalidDataException($"{bytes.Length} bytes: too short for a lexicon's counts");
        uint rows = BitConverter.ToUInt32(bytes, 0), cols = BitConverter.ToUInt32(bytes, 4);
        // Every cell is at least its two-byte terminator: counts the file cannot hold are not a lexicon's.
        if (rows == 0 || cols < 2 || cols > 64 || (ulong)rows * cols * 2 > (ulong)bytes.Length - 8)
            throw new InvalidDataException($"{rows} rows of {cols} columns cannot fit in {bytes.Length} bytes: not a lexicon");
        int off = 8;
        var sizes = new long[cols];
        string Cell(int row, int col)
        {
            int end = off;
            while (true)
            {
                if (end + 1 >= bytes.Length)
                    throw new InvalidDataException($"the file ends inside row {row} of {rows} (column {col}): it is cut short");
                if (bytes[end] == 0 && bytes[end + 1] == 0) break;
                end += 2;
            }
            // The code units as they are: a decoder would replace a stray surrogate and the file would not come back.
            var s = new string(MemoryMarshal.Cast<byte, char>(bytes.AsSpan(off, end - off)));
            sizes[col] += end + 2 - off;
            off = end + 2;
            return s;
        }
        var header = new string[cols];
        for (int c = 0; c < cols; c++) header[c] = Cell(0, c);
        var lex = new Lexicon(header.Skip(1));
        lex.Header[0] = header[0];
        for (int r = 1; r < rows; r++)
        {
            var row = new string[cols];
            for (int c = 0; c < cols; c++) row[c] = Cell(r, c);
            lex.Rows.Add(row);
        }
        sizes[0] /= 2;
        bool matches = bytes.Length - off == cols * 4;
        for (int c = 0; matches && c < cols; c++) matches = BitConverter.ToUInt32(bytes, off + c * 4) == sizes[c];
        lex.SizeTableMatches = matches;
        return lex;
    }

    public static Lexicon Read(string path) => Read(File.ReadAllBytes(path));

    /// <summary>The file, in the retail layout.</summary>
    public byte[] ToBytes()
    {
        int cols = Header.Count;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((uint)(Rows.Count + 1));
        w.Write((uint)cols);
        var sizes = new long[cols];
        void Cell(int col, string s)
        {
            var b = MemoryMarshal.AsBytes(s.AsSpan());
            w.Write(b);
            w.Write((ushort)0);
            sizes[col] += b.Length + 2;
        }
        for (int c = 0; c < cols; c++) Cell(c, Header[c]);
        foreach (var row in Rows)
        {
            if (row.Length != cols) throw new InvalidOperationException($"row '{(row.Length > 0 ? row[0] : "")}' has {row.Length} cells; the header has {cols}");
            for (int c = 0; c < cols; c++) Cell(c, row[c] ?? "");
        }
        sizes[0] /= 2;
        foreach (var s in sizes) w.Write((uint)s);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>The first row whose key is <paramref name="key"/>, case ignored; null when none is.</summary>
    public string[]? Row(string key) => Rows.FirstOrDefault(r => string.Equals(r[0], key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The column of <paramref name="language"/> (case ignored), or -1.</summary>
    public int Column(string language) => Header.FindIndex(1, h => string.Equals(h, language, StringComparison.OrdinalIgnoreCase));

    /// <summary>A key's text in <paramref name="language"/> (English by default); null when the key is not here.</summary>
    public string? Text(string key, string language = "English")
    {
        int c = Column(language);
        return c < 0 || Row(key) is not { } row ? null : row[c];
    }

    /// <summary>Give <paramref name="key"/> one text in every language, as a mod that is not translated does; the
    /// first row with the key is changed, or a row added at the end.</summary>
    public void Set(string key, string text)
    {
        NoNull(text);
        var row = Row(key) ?? Add(key);
        for (int c = 1; c < row.Length; c++) row[c] = text;
    }

    /// <summary>Give <paramref name="key"/> its text in one language; a new key gets it in every language.</summary>
    public void Set(string key, string language, string text)
    {
        int c = Column(language);
        if (c < 0) throw new ArgumentException($"'{language}' is not a column of this lexicon ({string.Join(", ", Languages)})", nameof(language));
        NoNull(text);
        if (Row(key) is { } row) row[c] = text;
        else Set(key, text);
    }

    /// <summary>A cell ends at its first null character, so a text cannot hold one.</summary>
    private static void NoNull(string s, string what = "text")
    {
        if (s.Contains('\0')) throw new ArgumentException($"a lexicon {what} cannot hold a null character");
    }

    /// <summary>Remove every row with <paramref name="key"/>; how many there were.</summary>
    public int Remove(string key) => Rows.RemoveAll(r => string.Equals(r[0], key, StringComparison.OrdinalIgnoreCase));

    private string[] Add(string key)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("a lexicon key cannot be empty", nameof(key));
        NoNull(key, "key");
        var row = new string[Header.Count];
        row[0] = key;
        for (int c = 1; c < row.Length; c++) row[c] = "";
        Rows.Add(row);
        return row;
    }
}
