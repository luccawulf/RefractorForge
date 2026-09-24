r"""dif.py - Refractor menu bitmap fonts (BF1942 and BF Vietnam): the .dif glyph table + its .tga atlas, and the
older '.font' format (BF1942.font / BfVietnam.font).  Reader, text layout and a CPU text renderer.

    import dif
    f = dif.parse_dif('font/standard6.dif')             # -> DifFont (version, name, width, height, line_height,
                                                        #    glyphs {code: Glyph}, kerning {(c1, c2): amount})
    rgba, w, h = dif.render_text('font/standard6.dif', 'font/standard6.tga', u'Quit 0123')

.dif FORMAT (text; whitespace-delimited except the name line) [engine: BFV DifFont ctor 0x0087FD10 and section
readers 0x0087EFC0 / 0x0087F570 / 0x0087F630; BF1942 0x0069A0E0 / 0x0069A470 / 0x0069A520, whose iostream imports
name the types (BFCPRT.dll operator>>(unsigned&) / (float&) / (int&) / (unsigned short&))]

    The loader reads words; each of the keywords 'header', 'kerning', 'glyphs' starts a section, anything else is skipped.
    header                              version (uint)        - every retail file: 2
                                        name (rest of the line, <= 32 chars, e.g. 'Trebuchet MS', 'standard 07_55')
                                        texture width (uint)
                                        texture height (uint) - only when version > 1; version 1 has a square texture
                                        line height (float)   - the pen's baseline is rect.y + line height
    kerning  N  { first(u16) second(u16) amount(float) } * N    - optional; the 32 Trebuchet-face files carry 112 pairs
                                        (111 distinct, one repeated; the first wins).  Loaded and exposed by the font
                                        renderer (BFV 0x00884180), but none of the 7 Style draw routines calls it.
    glyphs   N  { code(u16) A(float) B(float) C(float) top(int) x0 y0 x1 y1 (uint) } * N

    A, B, C are Windows-style ABC widths: A = space before the ink, B = ink width (the drawn quad's width), C = space
    after.  The pen advances A + B + C.  top = height of the ink's top above the baseline.  (x0, y0)-(x1, y1) is the
    atlas rectangle in pixels of the .tga (x1 / y1 exclusive); the engine divides by the real texture size, so the
    header's width / height are informational.  The drawn quad is (pen + A, baseline - top, B, y1 - y0).
    Codes are UTF-16 code units (u16); a string's character with no glyph ends the draw of that string [inferred from
    BFV DifFontRenderer 0x00884020 returning false and the Style loop 0x00876070 stopping on a failed draw].
    The .tga (same folder and stem) is an 8-bit grayscale coverage map (type 3); it is drawn as alpha.

.font FORMAT (BF1942.font / BfVietnam.font; BFV ref2::fx::NewRendFont reader 0x00A6A2F0: sscanf lines)
    Texture = <path> / TextureWidth = %d / TextureHeight = %d / BetweenWidth = %d / SpaceWidth = %d / Height = %d /
    AlphaTest = %d / AlphaBlend = %d / [ConstantWidth = %d] / ------- / then per glyph '%c %f %f %f' = char x0 y0 x1
    (cell = x0..x1 inclusive, y0..y0+Height).  A single-byte (latin1) table; used by the console / debug text and as
    the menu System's built-in font (BFV 0x0087C84E 'BFVIETNAM.font'), not by any Style handle in a retail file.

CLI
    python dif.py render <dif> <tga> "<text>" <out.png> [--scale N] [--color RRGGBB] [--bg RRGGBB]
    python dif.py check <folder>...          parse every .dif (and .font) under the folders, report invariants
    python dif.py atlas <dif> <tga> <out.png> [--scale N]   the atlas with every glyph rectangle outlined
"""
import os, re, struct, sys

__all__ = ['Glyph', 'DifFont', 'parse_dif', 'load_tga', 'layout', 'render_text', 'render_font', 'parse_font',
           'resolve_font', 'find_font_files', 'write_png', 'glyph_table']


class Glyph(object):
    __slots__ = ('code', 'a', 'b', 'c', 'top', 'x0', 'y0', 'x1', 'y1')

    def __init__(self, code, a, b, c, top, x0, y0, x1, y1):
        self.code, self.a, self.b, self.c, self.top = code, a, b, c, top
        self.x0, self.y0, self.x1, self.y1 = x0, y0, x1, y1

    @property
    def advance(self):
        return self.a + self.b + self.c

    @property
    def height(self):          # the engine's H: y1 - y0 (BFV glyph ctor 0x0087E86A)
        return self.y1 - self.y0

    def __repr__(self):
        return 'Glyph(%d %r A=%g B=%g C=%g top=%d rect=%d,%d-%d,%d)' % (
            self.code, chr(self.code) if self.code >= 32 else '', self.a, self.b, self.c, self.top,
            self.x0, self.y0, self.x1, self.y1)


class DifFont(object):
    def __init__(self):
        self.version = None
        self.name = ''
        self.width = self.height = None
        self.line_height = None
        self.glyphs = {}           # code -> Glyph (the first entry of a code wins, like the engine's std::map insert)
        self.kerning = {}          # (first, second) -> amount
        self.glyph_count = None    # the count the file declares
        self.kerning_count = 0     # the count the kerning section declares
        self.kerning_duplicates = []
        self.duplicates = []       # codes declared twice
        self.unknown_words = []    # words outside any section (the engine skips them)
        self.path = None

    def kern(self, c1, c2):
        return self.kerning.get((c1, c2), 0.0)


# ------------------------------------------------------------------------------------------------ .dif reader
class _Words(object):
    """A whitespace word reader over the text with a 'rest of line' read, like the engine's istream use."""

    def __init__(self, text):
        self.t = text
        self.i = 0

    def skip_ws(self):
        t, i = self.t, self.i
        while i < len(t) and t[i] in ' \t\r\n\v\f':
            i += 1
        self.i = i

    def word(self):
        self.skip_ws()
        t, i = self.t, self.i
        if i >= len(t):
            return None
        j = i
        while j < len(t) and t[j] not in ' \t\r\n\v\f':
            j += 1
        self.i = j
        return t[i:j]

    def line(self, limit=32):
        """getline(buf, 0x21): the rest of the line (after skipping leading whitespace), at most 32 chars."""
        self.skip_ws()
        t, i = self.t, self.i
        j = t.find('\n', i)
        if j < 0:
            j = len(t)
        self.i = j + 1
        return t[i:j][:limit]

    def uint(self):
        w = self.word()
        if w is None or not re.match(r'^\+?\d+$', w):
            raise ValueError('expected an unsigned int, got %r' % w)
        return int(w)

    def int(self):
        w = self.word()
        if w is None or not re.match(r'^[+-]?\d+$', w):
            raise ValueError('expected an int, got %r' % w)
        return int(w)

    def float(self):
        w = self.word()
        try:
            return float(w)
        except (TypeError, ValueError):
            raise ValueError('expected a float, got %r' % w)


def parse_dif(src):
    """Parse a .dif (path, bytes or str).  Raises ValueError on a malformed section."""
    if isinstance(src, (bytes, bytearray)):
        data, path = bytes(src), None
    elif isinstance(src, str) and '\n' in src:
        data, path = src.encode('latin1'), None
    else:
        path = src
        with open(src, 'rb') as fh:
            data = fh.read()
    text = data.decode('latin1')
    f = DifFont()
    f.path = path
    w = _Words(text)
    while True:
        k = w.word()
        if k is None:
            break
        if k == 'header':
            f.version = w.uint()
            name = w.line().rstrip('\r\t ')
            f.name = name
            f.width = w.uint()
            f.height = w.uint() if f.version > 1 else f.width
            f.line_height = w.float()
        elif k == 'kerning':
            n = w.uint()
            f.kerning_count = n
            for _ in range(n):
                c1, c2 = w.uint(), w.uint()
                amount = w.float()
                if (c1, c2) in f.kerning:
                    # std::map insert (BFV 0x0087F130) keeps the first pair; the retail Trebuchet files repeat one
                    f.kerning_duplicates.append((c1, c2, amount))
                    continue
                f.kerning[(c1, c2)] = amount
        elif k == 'glyphs':
            n = w.uint()
            f.glyph_count = n
            for _ in range(n):
                code = w.uint()
                if code > 0xFFFF:
                    raise ValueError('glyph code %d does not fit u16' % code)
                a, b, c = w.float(), w.float(), w.float()
                top = w.int()
                x0, y0, x1, y1 = w.uint(), w.uint(), w.uint(), w.uint()
                g = Glyph(code, a, b, c, top, x0, y0, x1, y1)
                if code in f.glyphs:
                    # the engine inserts into a std::map keyed by code (BFV 0x0087F070 = map::insert), which keeps the
                    # FIRST entry; no retail file has a duplicate.
                    f.duplicates.append(code)
                    continue
                f.glyphs[code] = g
        else:
            f.unknown_words.append(k)
    if f.version is None:
        raise ValueError('no header section')
    return f


def glyph_table(dif_path):
    """-> {'name', 'version', 'width', 'height', 'line_height', 'kerning': {(c1, c2): amount},
          'glyphs': {code: {'a', 'b', 'c', 'advance', 'top', 'x0', 'y0', 'x1', 'y1', 'height'}}}"""
    f = parse_dif(dif_path)
    return {'name': f.name, 'version': f.version, 'width': f.width, 'height': f.height, 'line_height': f.line_height,
            'kerning': dict(f.kerning),
            'glyphs': {c: {'a': g.a, 'b': g.b, 'c': g.c, 'advance': g.advance, 'top': g.top, 'x0': g.x0, 'y0': g.y0,
                           'x1': g.x1, 'y1': g.y1, 'height': g.height} for c, g in f.glyphs.items()}}


# ------------------------------------------------------------------------------------------------ .font reader
class FontFile(object):
    def __init__(self):
        self.keys = {}
        self.glyphs = {}      # char code -> (x0, y0, x1)
        self.path = None

    @property
    def cell_height(self):
        return int(self.keys.get('Height', 0))


def parse_font(src):
    """The older '.font' format (BF1942.font, BfVietnam.font)."""
    if isinstance(src, (bytes, bytearray)):
        data, path = bytes(src), None
    else:
        path = src
        with open(src, 'rb') as fh:
            data = fh.read()
    f = FontFile()
    f.path = path
    lines = data.decode('latin1').split('\n')
    i = 0
    while i < len(lines):
        ln = lines[i].rstrip('\r')
        i += 1
        if ln.startswith('--'):
            break
        m = re.match(r'^(\w+) = (.*)$', ln)
        if m:
            v = m.group(2).strip()
            f.keys[m.group(1)] = int(v) if re.match(r'^-?\d+$', v) else v
    for ln in lines[i:]:
        ln = ln.rstrip('\r')
        if not ln:
            continue
        ch = ln[0]
        parts = ln[1:].split()
        if len(parts) < 3:
            continue
        x0, y0, x1 = (float(p) for p in parts[:3])
        f.glyphs[ord(ch)] = (x0, y0, x1)
    return f


# ------------------------------------------------------------------------------------------------ TGA
def load_tga(path):
    """-> (width, height, bytes RGBA top-down).  An 8-bit grayscale TGA becomes white with alpha = gray (the menu
    draws the font atlas as coverage).  Uses Pillow when present, else a small uncompressed / RLE reader."""
    try:
        from PIL import Image
        im = Image.open(path)
        im.load()
        if im.mode in ('L', 'P') and im.mode == 'L':
            a = im.tobytes()
            rgba = bytearray(len(a) * 4)
            rgba[0::4] = b'\xff' * len(a)
            rgba[1::4] = b'\xff' * len(a)
            rgba[2::4] = b'\xff' * len(a)
            rgba[3::4] = a
            return im.size[0], im.size[1], bytes(rgba)
        im = im.convert('RGBA')
        return im.size[0], im.size[1], im.tobytes()
    except ImportError:
        return _load_tga_raw(path)


def _load_tga_raw(path):
    d = open(path, 'rb').read()
    idlen, cmtype, itype = d[0], d[1], d[2]
    w, h = struct.unpack_from('<HH', d, 12)
    bpp, desc = d[16], d[17]
    if cmtype:
        raise ValueError('colour-mapped TGA not supported')
    p = 18 + idlen
    bpx = bpp // 8
    n = w * h
    px = bytearray()
    if itype in (2, 3):
        px = d[p:p + n * bpx]
    elif itype in (10, 11):
        while len(px) < n * bpx:
            c = d[p]
            p += 1
            if c & 0x80:
                px += d[p:p + bpx] * ((c & 0x7F) + 1)
                p += bpx
            else:
                k = ((c & 0x7F) + 1) * bpx
                px += d[p:p + k]
                p += k
    else:
        raise ValueError('TGA type %d not supported' % itype)
    out = bytearray(n * 4)
    for i in range(n):
        if bpx == 1:
            v = px[i]
            out[i * 4:i * 4 + 4] = bytes((255, 255, 255, v))
        elif bpx == 3:
            b, g, r = px[i * 3:i * 3 + 3]
            out[i * 4:i * 4 + 4] = bytes((r, g, b, 255))
        else:
            b, g, r, a = px[i * 4:i * 4 + 4]
            out[i * 4:i * 4 + 4] = bytes((r, g, b, a))
    if not (desc & 0x20):   # bottom-up
        rows = [out[y * w * 4:(y + 1) * w * 4] for y in range(h)]
        out = bytearray(b''.join(reversed(rows)))
    return w, h, bytes(out)


# ------------------------------------------------------------------------------------------------ layout / render
def layout(font, text, x=0.0, y=0.0, wrap_width=None, kerning=False):
    """Glyph quads of `text` drawn by a Style at rect (x, y) [engine: BFV Style::drawText 0x00876070 and
    SingleLineStyle 0x00876480]: baseline = y + line height; per character: pen += A; quad (pen, baseline - top,
    B, y1 - y0); pen += B + C.  '\r' returns the pen to x, '\n' also moves the baseline down one line height.
    A character without a glyph ends the string.  wrap_width: the Style wraps when the pen passes x + width (the
    rect's width); None = no wrap.  kerning: add the pair amount between characters (the renderer exposes it,
    BFV 0x00884180, but no traced Style draw applies it, so the faithful default is False).
    -> list of (glyph, qx, qy, qw, qh)"""
    quads = []
    lh = font.line_height
    pen = x
    base = y + lh
    prev = None
    for ch in text:
        code = ord(ch)
        if code == 13:
            pen = x
            prev = None
            continue
        if code == 10:
            pen = x
            base += lh
            prev = None
            continue
        g = font.glyphs.get(code)
        if g is None:
            break
        if kerning and prev is not None:
            pen += font.kern(prev, code)
        pen += g.a
        quads.append((g, pen, base - g.top, g.b, g.height))
        pen += g.b + g.c
        prev = code
        if wrap_width is not None and pen >= x + wrap_width:
            pen = x
            base += lh
    return quads


def _blend(dst, dw, dh, sx, sy, src, sw, qx, qy, qw, qh, rx0, ry0, rw, rh, color):
    """Draw the atlas rect (rx0, ry0, rw, rh) of src into dst at (qx, qy) scaled to (qw, qh), nearest sampling,
    'over' compositing, tinted by color (r, g, b, a 0-255)."""
    cr, cg, cb, ca = color
    for j in range(qh):
        ty = qy + j
        if ty < 0 or ty >= dh:
            continue
        syy = ry0 + (j * rh) // qh if qh else ry0
        for i in range(qw):
            tx = qx + i
            if tx < 0 or tx >= dw:
                continue
            sxx = rx0 + (i * rw) // qw if qw else rx0
            s = (syy * sw + sxx) * 4
            a = src[s + 3] * ca // 255
            if not a:
                continue
            r = src[s] * cr // 255
            g = src[s + 1] * cg // 255
            b = src[s + 2] * cb // 255
            d = (ty * dw + tx) * 4
            da = dst[d + 3]
            oa = a + da * (255 - a) // 255
            if oa:
                dst[d] = (r * a + dst[d] * da * (255 - a) // 255) // oa
                dst[d + 1] = (g * a + dst[d + 1] * da * (255 - a) // 255) // oa
                dst[d + 2] = (b * a + dst[d + 2] * da * (255 - a) // 255) // oa
            dst[d + 3] = oa


_ATLAS_CACHE = {}


def _atlas(tga_path):
    k = os.path.abspath(tga_path)
    if k not in _ATLAS_CACHE:
        _ATLAS_CACHE[k] = load_tga(tga_path)
    return _ATLAS_CACHE[k]


def render_text(dif_path, tga_path, text, color=(255, 255, 255, 255), pad=1, kerning=False):
    """Render `text` (str; UTF-16 code units - a character above U+FFFF has no glyph) with the .dif + .tga.
    -> (RGBA bytes, width, height): a transparent canvas just large enough for the quads plus `pad` pixels, with the
    first line's top at pad and its baseline at pad + line height."""
    font = parse_dif(dif_path) if not isinstance(dif_path, DifFont) else dif_path
    aw, ah, atlas = _atlas(tga_path)
    quads = layout(font, text, kerning=kerning)
    if len(color) == 3:
        color = tuple(color) + (255,)
    lh = font.line_height
    # integer pixel positions (glyph metrics are integral in every retail .dif)
    iq = [(g, int(round(qx)), int(round(qy)), int(round(qw)), int(round(qh))) for g, qx, qy, qw, qh in quads]
    minx = min([0] + [q[1] for q in iq])
    miny = min([0] + [q[2] for q in iq])
    maxx = max([0] + [q[1] + q[3] for q in iq])
    maxy = max([int(round(lh))] + [q[2] + q[4] for q in iq])
    w = maxx - minx + 2 * pad
    h = maxy - miny + 2 * pad
    ox, oy = pad - minx, pad - miny
    dst = bytearray(w * h * 4)
    for g, qx, qy, qw, qh in iq:
        if g.code == 32:
            continue      # DifFontRenderer draws no quad for a space (0x0088404B)
        rw, rh = g.x1 - g.x0, g.y1 - g.y0
        if rw <= 0 or rh <= 0 or qw <= 0 or qh <= 0:
            continue
        _blend(dst, w, h, 0, 0, atlas, aw, qx + ox, qy + oy, qw, qh, g.x0, g.y0, rw, rh, color)
    return bytes(dst), w, h


def render_font(font_path, tga_path, text, color=(255, 255, 255, 255), pad=1):
    """Render with an old-style '.font' (cell x0..x1 inclusive, Height rows; BetweenWidth pixels between glyphs;
    SpaceWidth ignored for ' ', whose cell is empty)  [inferred from the data; the draw was not traced]."""
    f = parse_font(font_path)
    aw, ah, atlas = _atlas(tga_path)
    if len(color) == 3:
        color = tuple(color) + (255,)
    hgt = f.cell_height
    between = int(f.keys.get('BetweenWidth', 1))
    space = int(f.keys.get('SpaceWidth', 1))
    const = f.keys.get('ConstantWidth')
    pen = 0
    items = []
    for ch in text:
        c = ord(ch)
        if c > 255:
            continue
        cell = f.glyphs.get(c)
        if cell is None:
            continue
        x0, y0, x1 = (int(v) for v in cell)
        if c == 32:
            pen += int(const) if const else (space + between) * 3
            continue
        cw = x1 - x0 + 1
        items.append((pen, x0, y0, cw))
        pen += cw + between
    w = max(pen, 1) + 2 * pad
    h = hgt + 2 * pad
    dst = bytearray(w * h * 4)
    for px, x0, y0, cw in items:
        _blend(dst, w, h, 0, 0, atlas, aw, px + pad, pad, cw, hgt, x0, y0, cw, hgt, color)
    return bytes(dst), w, h


def write_png(path, rgba, w, h, scale=1, bg=None):
    """Write RGBA bytes as a PNG (Pillow if present, else zlib).  bg: composite onto an opaque (r, g, b)."""
    if bg is not None:
        out = bytearray(w * h * 4)
        for i in range(w * h):
            a = rgba[i * 4 + 3]
            for k in range(3):
                out[i * 4 + k] = (rgba[i * 4 + k] * a + bg[k] * (255 - a)) // 255
            out[i * 4 + 3] = 255
        rgba = bytes(out)
    if scale != 1:
        rows = []
        for y in range(h):
            row = rgba[y * w * 4:(y + 1) * w * 4]
            big = b''.join(row[x * 4:x * 4 + 4] * scale for x in range(w))
            rows.extend([big] * scale)
        rgba, w, h = b''.join(rows), w * scale, h * scale
    try:
        from PIL import Image
        Image.frombytes('RGBA', (w, h), bytes(rgba)).save(path)
        return
    except ImportError:
        pass
    import zlib
    raw = b''.join(b'\0' + rgba[y * w * 4:(y + 1) * w * 4] for y in range(h))

    def chunk(t, d):
        return struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d) & 0xFFFFFFFF)
    with open(path, 'wb') as fh:
        fh.write(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0)) +
                 chunk(b'IDAT', zlib.compress(raw, 9)) + chunk(b'IEND', b''))


def _ci_join(root, rel):
    """Case-insensitive path lookup below root (the archives are case-insensitive); None when absent."""
    cur = root
    for part in rel.replace('\\', '/').split('/'):
        if not part:
            continue
        try:
            names = os.listdir(cur)
        except OSError:
            return None
        hit = [n for n in names if n.lower() == part.lower()]
        if not hit:
            return None
        cur = os.path.join(cur, hit[0])
    return cur


def resolve_font(handle, font_root, language='English'):
    """A Style's 'Font handle' (e.g. 'Trebuchet MS14.dif') -> (dif_path, tga_path) the way the engine finds them
    [engine BFV font factory 0x00883280..0x008834CD, DifFontRenderer 0x008841F0, DifFont 0x0087FD10]:
    Font/<Language>/<handle> first, else Font/<handle> (Language = the registry LANGUAGE value: English, German,
    French, Italian, Spanish, Swedish, Japanese, Korean, Chinese - table BFV 0x00D30AA0; retail font.rfa has folders only
    for Chinese, Italian, Japanese, Korean, Spanish, so English / French / German / Swedish use the root).  A handle ending
    '.dif' gets the DifFontRenderer, '.font' the Ref2FontRenderer, anything else no font.  The atlas is the same folder +
    stem + '.tga' (loaded through the texture manager, so any texture extension it probes would do).
    font_root = the folder holding the extracted 'Font' / 'font' directory's CONTENTS."""
    stem, ext = os.path.splitext(handle)
    if ext.lower() not in ('.dif', '.font'):
        return None, None
    for rel in ([language + '/' + handle] if language else []) + [handle]:
        p = _ci_join(font_root, rel)
        if p:
            d = os.path.dirname(p)
            if ext.lower() == '.font':
                return p, _ci_join(d, stem + '.tga')
            return p, _ci_join(d, stem + '.tga')
    return None, None


def find_font_files(root):
    out = []
    for dp, dn, fns in os.walk(root):
        for fn in fns:
            if fn.lower().endswith(('.dif', '.font')):
                out.append(os.path.join(dp, fn))
    return sorted(out)


def tga_for(dif_path):
    """The atlas beside a .dif: same folder and stem, .tga (case-insensitive on disk)."""
    d, fn = os.path.split(dif_path)
    stem = os.path.splitext(fn)[0]
    for cand in os.listdir(d or '.'):
        if cand.lower() == (stem + '.tga').lower():
            return os.path.join(d, cand)
    return None


def _tga_size(path):
    d = open(path, 'rb').read(18)
    w, h = struct.unpack_from('<HH', d, 12)
    return w, h, d[2], d[16]


def check(roots):
    """Parse every .dif / .font under the roots and report invariants (the 'every .dif must parse' gate)."""
    ok = bad = 0
    for root in roots:
        for p in find_font_files(root):
            rel = os.path.relpath(p, root)
            if p.lower().endswith('.font'):
                try:
                    ff = parse_font(p)
                    print('OK   %-45s .font keys=%s glyphs=%d' % (rel, ','.join('%s=%s' % kv for kv in ff.keys.items()),
                                                                  len(ff.glyphs)))
                    ok += 1
                except Exception as e:
                    print('FAIL %-45s %s' % (rel, e))
                    bad += 1
                continue
            try:
                f = parse_dif(p)
            except Exception as e:
                print('FAIL %-45s %s' % (rel, e))
                bad += 1
                continue
            t = tga_for(p)
            notes = []
            if f.glyph_count != len(f.glyphs) + len(f.duplicates):
                notes.append('COUNT %s vs %d' % (f.glyph_count, len(f.glyphs)))
            if f.duplicates:
                notes.append('DUP %s' % f.duplicates[:5])
            if f.unknown_words:
                notes.append('UNKNOWN %r' % f.unknown_words[:5])
            if t:
                tw, th, tt, tb = _tga_size(t)
                if (tw, th) != (f.width, f.height):
                    notes.append('HDR %dx%d TGA %dx%d' % (f.width, f.height, tw, th))
                oob = [g.code for g in f.glyphs.values() if g.x1 > tw or g.y1 > th]
                if oob:
                    notes.append('OOB %d' % len(oob))
                tinfo = 'tga %dx%d type%d %dbpp' % (tw, th, tt, tb)
            else:
                notes.append('NO TGA')
                tinfo = ''
            bw = sum(1 for g in f.glyphs.values() if g.b != g.x1 - g.x0)
            codes = sorted(f.glyphs)
            print('OK   %-45s v%s %-22r %sx%s lh=%g glyphs=%d codes=%d..%d kern=%d/%d B!=rectw:%d %s %s' % (
                rel, f.version, f.name, f.width, f.height, f.line_height, len(f.glyphs), codes[0], codes[-1],
                len(f.kerning), f.kerning_count, bw, tinfo, ' '.join(notes)))
            ok += 1
    print('parsed %d, failed %d' % (ok, bad))
    return bad == 0


def _hex(s):
    s = s.lstrip('#')
    return tuple(int(s[i:i + 2], 16) for i in (0, 2, 4))


def main(argv):
    if len(argv) >= 2 and argv[1] == 'check':
        return 0 if check(argv[2:]) else 1
    if len(argv) >= 6 and argv[1] == 'render':
        dp, tp, text, out = argv[2:6]
        scale, color, bg = 1, (255, 255, 255), None
        rest = argv[6:]
        for i, a in enumerate(rest):
            if a == '--scale':
                scale = int(rest[i + 1])
            elif a == '--color':
                color = _hex(rest[i + 1])
            elif a == '--bg':
                bg = _hex(rest[i + 1])
        if dp.lower().endswith('.font'):
            rgba, w, h = render_font(dp, tp, text, color)
        else:
            rgba, w, h = render_text(dp, tp, text, color)
        write_png(out, rgba, w, h, scale, bg)
        print('%s: %dx%d' % (out, w, h))
        return 0
    if len(argv) >= 5 and argv[1] == 'atlas':
        dp, tp, out = argv[2:5]
        scale = int(argv[argv.index('--scale') + 1]) if '--scale' in argv else 1
        f = parse_dif(dp)
        aw, ah, atlas = load_tga(tp)
        img = bytearray(aw * ah * 4)
        for i in range(aw * ah):
            a = atlas[i * 4 + 3]
            img[i * 4:i * 4 + 4] = bytes((a, a, a, 255))
        for g in f.glyphs.values():
            for x in range(g.x0, min(g.x1, aw)):
                for y in (g.y0, g.y1 - 1):
                    if 0 <= y < ah:
                        img[(y * aw + x) * 4:(y * aw + x) * 4 + 3] = b'\xff\x40\x40'
        write_png(out, bytes(img), aw, ah, scale)
        return 0
    print(__doc__)
    return 2


if __name__ == '__main__':
    sys.exit(main(sys.argv))
