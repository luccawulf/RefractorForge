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
/// </summary>
public sealed class DifFont
{
    private readonly Dictionary<int, DifGlyph> _glyphs = new();
    private readonly Dictionary<(int, int), float> _kerning = new();

    public int Version { get; private set; }
    public string Name { get; private set; } = "";
    /// <summary>The atlas size the header states. Informational: the engine divides UVs by the real texture size
    /// (0x00884054), which is what a renderer must do too.</summary>
    public int Width { get; private set; }
    public int Height { get; private set; }
    public float LineHeight { get; private set; }
    public IReadOnlyDictionary<int, DifGlyph> Glyphs => _glyphs;
    public IReadOnlyDictionary<(int First, int Second), float> Kerning => _kerning;

    public DifGlyph? Glyph(int code) => _glyphs.GetValueOrDefault(code);

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
                    f.Name = w.RestOfLine(32).TrimEnd('\r', '\t', ' ');
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
                        f._glyphs.TryAdd(code, new DifGlyph(code, a, b, c, top, x0, y0, x1, y1));
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
