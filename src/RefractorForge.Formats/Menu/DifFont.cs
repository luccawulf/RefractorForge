using System.Globalization;
using System.Text;

namespace RefractorForge.Formats.Menu;

/// <summary>One glyph of a <see cref="DifFont"/>: Windows-style ABC widths and its rectangle in the atlas.</summary>
/// <param name="A">Space before the ink.</param>
/// <param name="B">Ink width - the width of the drawn quad, which stretches the atlas rectangle when they differ
/// (Trebuchet MS8 has 11 such glyphs).</param>
/// <param name="C">Space after the ink. The pen advances A + B + C.</param>
/// <param name="Top">Height of the ink's top above the baseline.</param>
/// <param name="X0">Atlas rectangle in pixels of the <c>.tga</c>, <c>X1</c>/<c>Y1</c> exclusive.</param>
public sealed record DifGlyph(int Code, float A, float B, float C, int Top, int X0, int Y0, int X1, int Y1)
{
    public float Advance => A + B + C;
    /// <summary>The drawn quad's height, the engine's H (BFV glyph ctor 0x0087E86A).</summary>
    public int Height => Y1 - Y0;
}

/// <summary>
/// A menu bitmap font, the <c>.dif</c> half: the glyph table a Style's <c>Font handle</c> names
/// (<c>standard6.dif</c>, <c>Trebuchet MS14.dif</c>); the coverage atlas is the <c>.tga</c> of the same stem beside it.
/// The file is text, read as words the way the engine's istream reads it (BFV DifFont ctor 0x0087FD10, sections
/// 0x0087EFC0 / 0x0087F570 / 0x0087F630; BF1942 0x0069A0E0): each of the words <c>header</c>, <c>kerning</c>,
/// <c>glyphs</c> starts a section and any other word is skipped.
/// <list type="bullet">
/// <item><c>header</c>: version (2 in every retail file), the name (the rest of its line, at most 32 characters),
/// atlas width, atlas height (only when the version is above 1), line height (a float: the first baseline is
/// <c>rect.y + line height</c>).</item>
/// <item><c>kerning</c>: a count, then that many <c>first second amount</c>. Parsed, and never used: none of the seven
/// Style draw routines calls the kerning lookup (BFV 0x00884180).</item>
/// <item><c>glyphs</c>: a count, then that many <c>code A B C top x0 y0 x1 y1</c>; codes are UTF-16 code units.</item>
/// </list>
/// A repeated glyph code or kerning pair keeps its first entry (the engine inserts into a std::map). Every
/// <c>.dif</c> in both games' <c>font.rfa</c>, every language, reads.
/// <para>DICE's FontMakerEx 1.2 (the MDT's font tool) writes one layout, which <see cref="ToBytes"/> writes again byte
/// for byte: CRLF lines, the header's fields one to a line, then <c>glyphs</c>, its count and one tab-separated line per
/// glyph in code order, then <c>kerning</c>, its count and one tab-separated pair per line. The retail files in any
/// other layout are FontMakerEx files re-saved from a spreadsheet (header lines padded with tabs).</para>
/// </summary>
public sealed class DifFont
{
    /// <summary>The longest name the header holds: the engine reads it with getline into a 33-byte buffer.</summary>
    public const int MaxNameLength = 32;

    private readonly Dictionary<int, DifGlyph> _glyphs = new();
    private readonly List<DifGlyph> _order = new();
    private readonly Dictionary<(int, int), float> _kerning = new();
    // Every entry as the file lists it, repeats included, so a file writes back as it was (FontMakerEx wrote the pair
    // v . twice into every Trebuchet kerning table); the dictionaries keep the first, as the engine does.
    private readonly List<DifGlyph> _glyphLines = new();
    private readonly List<(int First, int Second, float Amount)> _kerningLines = new();

    public int Version { get; private set; }
    public string Name { get; private set; } = "";
    /// <summary>The atlas size the header states. Informational: the engine divides UVs by the real texture size
    /// (0x00884054), which is what a renderer must do too.</summary>
    public int Width { get; private set; }
    public int Height { get; private set; }
    public float LineHeight { get; private set; }
    public IReadOnlyDictionary<int, DifGlyph> Glyphs => _glyphs;
    /// <summary>The glyphs the engine keeps, in the order the file lists them.</summary>
    public IReadOnlyList<DifGlyph> GlyphList => _order;
    public IReadOnlyDictionary<(int First, int Second), float> Kerning => _kerning;

    public DifGlyph? Glyph(int code) => _glyphs.GetValueOrDefault(code);

    /// <summary>A new font: version 2, the glyphs in code order, the kerning pairs in the order given (a repeat keeps the
    /// first, as the engine does). Throws <see cref="ArgumentException"/> for a name the header cannot hold (empty,
    /// longer than <see cref="MaxNameLength"/>, starting with white space or holding a line break or a character
    /// outside Latin-1), a glyph code outside a UTF-16 unit, or an atlas size below 1.</summary>
    public static DifFont Create(string name, int width, int height, float lineHeight, IEnumerable<DifGlyph> glyphs,
                                 IEnumerable<(int First, int Second, float Amount)>? kerning = null)
    {
        CheckName(name);
        if (width < 1 || height < 1) throw new ArgumentException($"atlas size {width} x {height}");
        var f = new DifFont { Version = 2, Name = name, Width = width, Height = height, LineHeight = lineHeight };
        foreach (var g in glyphs.OrderBy(g => g.Code))
        {
            if (g.Code is < 0 or > 0xFFFF) throw new ArgumentException($"glyph code {g.Code} does not fit a UTF-16 unit");
            if (f._glyphs.TryAdd(g.Code, g)) { f._order.Add(g); f._glyphLines.Add(g); }
        }
        foreach (var (a, b, amount) in kerning ?? Array.Empty<(int, int, float)>())
            if (f._kerning.TryAdd((a, b), amount)) f._kerningLines.Add((a, b, amount));
        return f;
    }

    /// <summary>Why a name cannot go in a header, or null when it can.</summary>
    public static string? NameProblem(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "a font needs a name";
        if (name.Length > MaxNameLength) return $"the header holds at most {MaxNameLength} characters of the name";
        if (char.IsWhiteSpace(name[0])) return "the name cannot start with a space (the engine skips it and reads the next line)";
        foreach (var c in name)
            if (c is '\r' or '\n' || c > 0xFF || char.IsControl(c)) return $"the name cannot hold '{(c < 32 ? "\\x" + ((int)c).ToString("X2") : c.ToString())}'";
        return null;
    }

    private static void CheckName(string name)
    {
        if (NameProblem(name) is { } why) throw new ArgumentException(why, nameof(name));
    }

    /// <summary>The file in FontMakerEx's layout (see the class notes): what the engine reads back as this font. A read
    /// file's entries are written as it listed them, repeats included.</summary>
    public byte[] ToBytes()
    {
        CheckName(Name);
        var s = new StringBuilder();
        s.Append("header\r\n").Append(Version.ToString(CultureInfo.InvariantCulture)).Append("\r\n")
         .Append(Name).Append("\r\n")
         .Append(Width.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        if (Version > 1) s.Append(Height.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        s.Append(Num(LineHeight)).Append("\r\n");
        s.Append("glyphs\r\n").Append(_glyphLines.Count.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        foreach (var g in _glyphLines)
            s.Append(g.Code.ToString(CultureInfo.InvariantCulture)).Append('\t')
             .Append(Num(g.A)).Append('\t').Append(Num(g.B)).Append('\t').Append(Num(g.C)).Append('\t')
             .Append(g.Top.ToString(CultureInfo.InvariantCulture)).Append('\t')
             .Append(g.X0.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(g.Y0.ToString(CultureInfo.InvariantCulture)).Append('\t')
             .Append(g.X1.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(g.Y1.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        if (_kerningLines.Count > 0)
        {
            s.Append("kerning\r\n").Append(_kerningLines.Count.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            foreach (var (a, b, amount) in _kerningLines)
                s.Append(a.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(b.ToString(CultureInfo.InvariantCulture)).Append('\t')
                 .Append(Num(amount)).Append("\r\n");
        }
        return Encoding.Latin1.GetBytes(s.ToString());
    }

    /// <summary>A number as the tool writes it: whole numbers without a point, others in the shortest form that reads back.</summary>
    private static string Num(float v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Read a <c>.dif</c>. Throws <see cref="InvalidDataException"/> when it has no header or a section's
    /// numbers do not read.</summary>
    public static DifFont Parse(byte[] bytes)
    {
        var f = new DifFont();
        var w = new Words(Encoding.Latin1.GetString(bytes));
        bool header = false;
        for (var word = w.Next(); word is not null; word = w.Next())
        {
            switch (word)
            {
                case "header":
                    f.Version = w.UInt();
                    f.Name = w.RestOfLine(MaxNameLength).TrimEnd('\r', '\t', ' ');
                    f.Width = w.UInt();
                    f.Height = f.Version > 1 ? w.UInt() : f.Width;
                    f.LineHeight = w.Float();
                    header = true;
                    break;
                case "kerning":
                {
                    int n = w.UInt();
                    for (int i = 0; i < n; i++)
                    {
                        int a = w.UInt(), b = w.UInt();
                        float amount = w.Float();
                        f._kerning.TryAdd((a, b), amount);
                        f._kerningLines.Add((a, b, amount));
                    }
                    break;
                }
                case "glyphs":
                {
                    int n = w.UInt();
                    for (int i = 0; i < n; i++)
                    {
                        int code = w.UInt();
                        if (code > 0xFFFF) throw new InvalidDataException($"glyph code {code} does not fit a UTF-16 unit");
                        float a = w.Float(), b = w.Float(), c = w.Float();
                        int top = w.Int();
                        int x0 = w.UInt(), y0 = w.UInt(), x1 = w.UInt(), y1 = w.UInt();
                        var g = new DifGlyph(code, a, b, c, top, x0, y0, x1, y1);
                        if (f._glyphs.TryAdd(code, g)) f._order.Add(g);
                        f._glyphLines.Add(g);
                    }
                    break;
                }
            }
        }
        if (!header) throw new InvalidDataException("no header section: not a .dif font");
        return f;
    }

    /// <summary>A whitespace word reader with a rest-of-line read, like the engine's istream use.</summary>
    private sealed class Words
    {
        private readonly string _t;
        private int _i;
        public Words(string t) => _t = t;

        private static bool Ws(char c) => c is ' ' or '\t' or '\r' or '\n' or '\v' or '\f';

        private void SkipWs() { while (_i < _t.Length && Ws(_t[_i])) _i++; }

        public string? Next()
        {
            SkipWs();
            if (_i >= _t.Length) return null;
            int j = _i;
            while (j < _t.Length && !Ws(_t[j])) j++;
            var s = _t[_i..j];
            _i = j;
            return s;
        }

        /// <summary>getline into a 0x21-byte buffer: the rest of the line after leading whitespace, at most 32 characters.</summary>
        public string RestOfLine(int limit)
        {
            SkipWs();
            int j = _t.IndexOf('\n', _i);
            if (j < 0) j = _t.Length;
            var s = _t[_i..j];
            _i = Math.Min(_t.Length, j + 1);
            return s.Length > limit ? s[..limit] : s;
        }

        public int UInt()
        {
            var s = Next();
            if (s is null || !uint.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) || v > int.MaxValue)
                throw new InvalidDataException($"expected an unsigned number, got '{s}'");
            return (int)v;
        }

        public int Int()
        {
            var s = Next();
            if (s is null || !int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v))
                throw new InvalidDataException($"expected a number, got '{s}'");
            return v;
        }

        public float Float()
        {
            var s = Next();
            if (s is null || !float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                throw new InvalidDataException($"expected a decimal number, got '{s}'");
            return v;
        }
    }
}
