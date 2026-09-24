r"""menu_render.py - reference renderer for Refractor "Meme 2.0" menu files (BF1942 and BF Vietnam).

Implements the render spec (RefractorDevelopmentKit docs/research/menu-render.md).  A static preview: the display list of one menu file walked the way the
engines' draw routines walk it, with every runtime value taken as the file's own default, onto an 800x600 canvas.

    import menu_render as mr
    arch = mr.archives_for('BFV', 'BfVietnam')                 # extracted menu.rfa / font.rfa / lexiconAll.dat
    img, stats = mr.render(open('MainMenu', 'rb').read(), 'BFV', arch, mode='defaults', outlines=False,
                           name='MainMenu')
    img.save('MainMenu.png')                                   # PIL RGBA 800x600
    png = mr.render_png(data, 'BFV', arch, mode='all')         # the same as PNG bytes

mode:     'defaults' - every CullNode evaluated at the file's default values (what the engine shows before game code
                       writes a variable);
          'all'      - CullNodes ignored (everything a file can show, overlapping);
          'dim'      - like 'all', but content a CullNode would hide at defaults is drawn at 30 % alpha.
outlines: overlay 1-px rectangles: cyan = TransformNode child rects, yellow = nodes that draw, orange = runtime
          text / pictures (named variables), magenta = a picture handle that does not resolve, red = (in 'all') a
          drawing node that 'defaults' would hide.
context:  render(..., context=True, name=...) draws the file inside the layer stack of the game's root menu file
          (BattleField: Path -> BattlefieldMenu -> the *Layer PathNodes), with the PathNodes pointed at this file (and
          at its parents) and the action list that navigates to it replayed; `mode` then applies inside the file only,
          everything around it is drawn at 'defaults'.  See context_overrides().
variables: {name: value} editor toggles replacing a named variable's file default (culls, colours, positions).

CLI:
    python menu_render.py corpus [--out DIR]            every retail file of both games, modes all + defaults
    python menu_render.py one <GAME> <mod> <name> <out.png> [--mode M] [--outlines] [--context]
"""
import math, os, posixpath, sys, collections, json, time, traceback

sys.setrecursionlimit(20000)
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)                                                # meme.py and dif.py sit beside this file
import meme                                                            # noqa: E402
import dif                                                             # noqa: E402

import numpy as np                                                     # noqa: E402
from PIL import Image                                                  # noqa: E402

W, H = 800, 600
# Research data, not in the repository: set these before use.
#   MEME_EXTRACT = a folder holding <GAME>/<mod>/menu/... and <GAME>/<mod>/font/... - the menu.rfa and font.rfa of
#                  each mod extracted (rdk archive extract <rfa> <folder>), GAME being BFV or BF1942;
#   MEME_CORPUS  = a folder holding <GAME>/<mod>/menu/<file> - the menu files alone (for the 'corpus' command).
#   MEME_BF1942 / MEME_BFV = the clean installs (for the lexicons).
EXTRACT = os.environ.get('MEME_EXTRACT', os.path.join(HERE, 'extract'))
CORPUS = os.environ.get('MEME_CORPUS', os.path.join(HERE, 'corpus'))
_BF42 = os.environ.get('MEME_BF1942', 'D:/Games/EA GAMES/Battlefield 1942 Clean/Battlefield 1942')
_BFV = os.environ.get('MEME_BFV', 'D:/Games/EA GAMES/Battlefield Vietnam Original Files')

# ---------------------------------------------------------------------------------------------------- archives
LEXICON = {
    'BfVietnam': _BFV + '/Mods/BfVietnam/lexiconAll.dat',
    'bf1942': _BF42 + '/Mods/bf1942/lexiconAll.dat',
    'XPack1': _BF42 + '/Mods/XPack1/lexiconAll.dat',
    'XPack2': _BF42 + '/Mods/XPack2/lexiconAll.dat',
}
BASE = {'BFV': 'BfVietnam', 'BF1942': 'bf1942'}


def archives_for(game, mod):
    """The layers a mod sees, mod first then the base mod [engine: the lexicon tables are push_front'ed as they load,
    base first then the mod (BF1942 0x0045EFF0 / 0x0045F052 -> 0x007D9A60 -> list insert-at-begin 0x007D9530; BFV
    0x0055CC8B), so the mod's table is searched first; a mod's menu.rfa overlays the base per file [inferred]]."""
    mods = [mod] if mod == BASE[game] else [mod, BASE[game]]
    menu, font, lex = [], [], []
    for m in mods:
        root = os.path.join(EXTRACT, game, m)
        for sub in os.listdir(root) if os.path.isdir(root) else []:
            p = os.path.join(root, sub)
            if os.path.isdir(p) and sub.lower() == 'menu':
                menu.append(p)
            if os.path.isdir(p) and sub.lower() == 'font':
                font.append(p)
        if LEXICON.get(m) and os.path.isfile(LEXICON[m]):
            lex.append(LEXICON[m])
    return {'game': game, 'mod': mod, 'menu': menu, 'font': font, 'lexicon': lex, 'language': 'English'}


class _CiIndex(object):
    """Case-insensitive file index of a folder tree: 'a/b/c.dds' (lower) -> real path."""

    def __init__(self, root):
        self.map = {}
        for dp, dn, fns in os.walk(root):
            for fn in fns:
                full = os.path.join(dp, fn)
                self.map[os.path.relpath(full, root).replace('\\', '/').lower()] = full

    def get(self, rel):
        return self.map.get(rel.lower())


class Lexicon(object):
    """lexiconAll.dat [data]: u32 rows (incl. the LANGUAGE header row), u32 columns, rows*columns null-terminated
    UTF-16LE cells row-major, then a size table.  Lookup: _stricmp, first table first (0x007D9280 / BFV 0x0055C490)."""

    def __init__(self, path):
        import struct
        d = open(path, 'rb').read()
        rows, cols = struct.unpack_from('<II', d, 0)
        p, cells = 8, []
        for _ in range(rows * cols):
            q = p
            while d[q:q + 2] != b'\0\0':
                q += 2
            cells.append(d[p:q].decode('utf-16-le', 'replace'))
            p = q + 2
        self.path, self.header, self.table = path, cells[:cols], {}
        for r in range(1, rows):
            row = cells[r * cols:(r + 1) * cols]
            self.table.setdefault(row[0].upper(), row)          # first row of a repeated key kept [inferred]

    def get(self, key, lang):
        row = self.table.get(key.upper())
        if row is None:
            return None
        hdr = [h.lower() for h in self.header]
        i = hdr.index(lang.lower()) if lang.lower() in hdr else 1
        return row[i] if i < len(row) else None


class Assets(object):
    _cache = {}

    def __init__(self, arch):
        self.arch = arch
        self.game = arch['game']
        self.menu = [_CiIndex(r) for r in arch['menu']]
        self.font_roots = arch['font']
        self.lex = [Lexicon(p) for p in arch['lexicon']]
        self.lang = arch.get('language', 'English')
        self.tex = {}
        self.fonts = {}
        self.files = {}

    @classmethod
    def get(cls, arch):
        k = (arch['game'], arch['mod'], tuple(arch['menu']), tuple(arch['font']), tuple(arch['lexicon']))
        if k not in cls._cache:
            cls._cache[k] = cls(arch)
        return cls._cache[k]

    # menu files (by-name references load 'menu/' + name: BFV 0x0087CC40, BF1942 0x00601E80)
    def menu_file(self, name):
        if name not in self.files:
            data = None
            for ix in self.menu:
                p = ix.get(name.replace('\\', '/'))
                if p and os.path.isfile(p):
                    data = open(p, 'rb').read()
                    break
            self.files[name] = data
        return self.files[name]

    # textures: 'Menu/Texture/' + handle, '\' -> '/', extension stripped, probed .dds/.tga/.bmp, case ignored
    def texture(self, handle):
        """-> (PIL RGBA image, path) or (None, None) when the handle does not resolve."""
        if handle in self.tex:
            return self.tex[handle]
        res = (None, None)
        rel = posixpath.normpath('texture/' + handle.replace('\\', '/'))
        if not rel.startswith('..'):
            stem = posixpath.splitext(rel)[0]
            for cand in [stem + e for e in ('.dds', '.tga', '.bmp')]:
                for ix in self.menu:
                    p = ix.get(cand)
                    if p:
                        try:
                            im = Image.open(p)
                            im.load()
                            res = (im.convert('RGBA'), p)
                        except Exception:
                            res = (None, p)
                        break
                if res[1]:
                    break
        self.tex[handle] = res
        return res

    def font(self, handle):
        """Font/<Language>/<handle>, else Font/<handle> (BFV 0x00883280; BF1942 0x006669E0); .dif only."""
        if handle in self.fonts:
            return self.fonts[handle]
        f = None
        if handle and handle.lower().endswith('.dif'):
            for root in self.font_roots:
                dp, tp = dif.resolve_font(handle, root, self.lang)
                if dp and tp:
                    font = dif.parse_dif(dp)
                    im = Image.open(tp)
                    im.load()
                    if im.mode == 'L':
                        a = np.asarray(im, dtype=np.float32) / 255.0
                    else:
                        a = np.asarray(im.convert('RGBA'), dtype=np.float32)[:, :, 3] / 255.0
                    font.atlas = a
                    font.dif_path, font.tga_path = dp, tp
                    f = font
                    break
        self.fonts[handle] = f
        return f

    def lexicon(self, key):
        for t in self.lex:
            v = t.get(key, self.lang)
            if v is not None:
                return v
        return None


# ---------------------------------------------------------------------------------------------------- data values
class Rt(object):
    """Per-render state: the global object registry (first definition of a name wins), lazily loaded menu files."""

    def __init__(self, game, assets, mode, variables=None):
        self.game, self.assets, self.mode = game, assets, mode
        self.vars = dict(variables or {})     # editor toggles: variable name -> value (overrides the file default)
        self.reg = {}
        self.loaded = {}
        self.stats = collections.Counter()
        self.notes = collections.Counter()

    def register_file(self, mf, fname):
        if fname and fname not in self.reg:
            self.reg[fname] = mf.root
        for r in mf.walk():
            if r.kind == 'object' and r.name and r.name not in self.reg:
                self.reg[r.name] = r

    def resolve(self, r, load=True):
        """A by-name reference -> the registered object, else the menu file of that name (loadFile), else None."""
        if r is None:
            return None
        if r.kind == 'ref':
            o = self.reg.get(r.name)
            if o is None and load and r.name not in self.loaded:
                data = self.assets.menu_file(r.name)
                self.loaded[r.name] = None
                if data is not None:
                    try:
                        mf = meme.parse(data, self.game)
                        self.loaded[r.name] = mf
                        self.register_file(mf, r.name)
                        self.stats['menu files loaded by reference'] += 1
                    except Exception as e:
                        self.notes['reference %s failed to parse: %s' % (r.name, e)] += 1
                o = self.reg.get(r.name)
            if o is None:
                self.notes['dangling reference %s' % r.name] += 1
            return o
        if r.kind != 'object':
            return None
        if r.name and self.reg.get(r.name) is not None and self.reg[r.name] is not r:
            return self.reg[r.name]                  # first definition of a name wins
        return r

    # getFloat (BFV Data +0x60, BF1942 +0x5C)
    def val(self, d, depth=0):
        d = self.resolve(d)
        if d is None or depth > 64 or d.fields is None:
            return 0.0
        s = d.short
        if d.name and d.name in self.vars:
            self.stats['variable toggled by the editor'] += 1
            return float(self.vars[d.name])
        g = lambda k: self.resolve(d[k])
        v = lambda k: self.val(d[k], depth + 1)
        if s == 'BoolData':
            return 1.0 if d['Value <do not edit>'] == 1 else 0.0
        if s in ('IntData', 'FloatData'):
            return float(d['Value <do not edit>'])
        if s in ('StringData', 'WstringData'):
            return _atof(d.fields[0].value)
        if s in ('EqualData', 'NotEqualData', 'LessData', 'LessEqualData', 'AndData', 'OrData', 'ExclusiveOrData',
                 'AddData', 'SubData', 'MulData', 'DivData'):
            if g('Data 1') is None or g('Data 2') is None:
                return 0.0                           # a null operand gives 0 (0x00ABF420.. / 0x007EE9C0..)
            a = v('Data 1')
            if s == 'AndData':                       # short-circuit
                return 1.0 if a != 0 and v('Data 2') != 0 else 0.0
            b = v('Data 2')
            if s == 'EqualData': return float(a == b)
            if s == 'NotEqualData': return float(a != b)
            if s == 'LessData': return float(a < b)
            if s == 'LessEqualData': return float(a <= b)
            if s == 'OrData': return float(a != 0 or b != 0)
            if s == 'ExclusiveOrData': return float((a != 0) != (b != 0))       # untraced, no corpus instance
            if s == 'AddData': return a + b
            if s == 'SubData': return a - b
            if s == 'MulData': return a * b
            if s == 'DivData':
                if b == 0:
                    return math.copysign(float('inf'), a) if a != 0 else float('nan')
                return a / b
        if s == 'NotData':
            return 0.0 if g('Data') is None else float(v('Data') == 0)
        if s == 'IndexDataData':
            item = self.list_item(g('Data'), self.val(d['Index'], depth + 1))
            return self.val(item, depth + 1) if item is not None else 0.0
        if s in ('DataListData', 'ConcatenateListData'):
            item = self.list_item(d, 0)
            return self.val(item, depth + 1) if item is not None else 0.0
        if s == 'BfLocaleStringData':
            return self.val(d['String Id'], depth + 1)
        if s in ('PointerXData', 'PointerYData'):
            return POINTER_OFFSCREEN                 # runtime: the static preview parks the pointer off-canvas
        return 0.0                                   # BfLocaleData, BfColorData, BfEditData, FloatRefData, ... = 0

    def list_item(self, lst, idx):
        if lst is None or lst.fields is None or lst.short not in ('DataListData', 'ConcatenateListData'):
            return None
        items = lst['Data list']
        try:
            i = int(idx)                             # _ftol2 truncates (0x007C2DD0)
        except (ValueError, OverflowError):
            return None
        return self.resolve(items[i]) if 0 <= i < len(items) else None

    def is_runtime(self, d):
        """A named Data (a variable the game or a menu action can write) - its file value is only a default."""
        d0 = d
        if d0 is not None and d0.kind == 'ref':
            return True
        return bool(d0 is not None and d0.kind == 'object' and d0.name)

    # getChar string (Data +0x68 / +0x64)
    def text(self, d, depth=0):
        """-> (text or None, source) ; source in literal / lexicon / lexicon-miss / runtime / none."""
        d0 = d
        d = self.resolve(d)
        if d is None or d.fields is None or depth > 32:
            return None, 'none'
        s = d.short
        rt = self.is_runtime(d0)
        if s == 'BfLocaleStringData':
            sid = self.resolve(d['String Id'])
            key, _ = self.text(d['String Id'], depth + 1)
            key = key or ''
            if not key:
                return '', 'runtime'
            t = self.assets.lexicon(key)
            if t is None:
                self.stats['lexicon key missing'] += 1
                self.notes['lexicon miss %r' % key] += 1
                return key, 'lexicon-miss'
            self.stats['lexicon key found'] += 1
            # '\' 'n' in the text becomes LF then CR (BFV 0x0055C728, BF1942 0x007D949D)
            return t.replace('\\n', '\n\r'), ('runtime' if self.is_runtime(d['String Id']) else 'lexicon')
        if s in ('StringData', 'WstringData'):
            return d.fields[0].value, ('runtime' if rt else 'literal')
        if s == 'IntData':
            return '%i' % d['Value <do not edit>'], ('runtime' if rt else 'literal')
        if s == 'FloatData':
            return _fmt9(d['Value <do not edit>']), ('runtime' if rt else 'literal')
        if s == 'BoolData':
            return ('true' if d['Value <do not edit>'] == 1 else 'false'), ('runtime' if rt else 'literal')
        if s == 'IndexDataData':
            item = self.list_item(self.resolve(d['Data']), self.val(d['Index']))
            t, _ = self.text(item, depth + 1) if item is not None else ('', None)
            return t, 'runtime'
        if s in ('DataListData', 'ConcatenateListData'):
            item = self.list_item(d, 0)
            t, _ = self.text(item, depth + 1) if item is not None else ('', None)
            return t, 'runtime'
        return '', 'runtime'                          # logical / operator / Bf*Data: no characters

    def string(self, d):
        """Data -> string for picture names (Data-to-string BFV 0x008746C0)."""
        t, src = self.text(d)
        return t or '', src


POINTER_OFFSCREEN = -10000.0


def _atof(s):
    import re
    m = re.match(r'\s*[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?', s or '')
    return float(m.group(0)) if m else 0.0


def _fmt9(f):
    """FloatData text: sign, truncated integer part, '.', 9 truncated decimals (BFV 0x008777C0, ecx = 9)."""
    if f != f or f in (float('inf'), float('-inf')):
        return str(f)
    neg = f < 0
    a = abs(f)
    ip = int(a)
    frac = int((a - ip) * 10 ** 9)
    return ('-' if neg else '') + '%d.%09d' % (ip, frac)


# ---------------------------------------------------------------------------------------------------- raster
class Canvas(object):
    def __init__(self, bg=(0, 0, 0)):
        self.fb = np.zeros((H, W, 3), dtype=np.float32)
        self.fb[:, :] = np.array(bg, dtype=np.float32) / 255.0
        self.rs_cache = {}

    def _box(self, x, y, w, h, clip):
        x0, y0 = int(math.floor(x + 0.5)), int(math.floor(y + 0.5))
        x1, y1 = int(math.floor(x + w + 0.5)), int(math.floor(y + h + 0.5))
        cx0, cy0, cx1, cy1 = 0, 0, W, H
        if clip is not None:
            cx0 = max(cx0, int(math.floor(clip[0] + 0.5)))
            cy0 = max(cy0, int(math.floor(clip[1] + 0.5)))
            cx1 = min(cx1, int(math.floor(clip[0] + clip[2] + 0.5)))
            cy1 = min(cy1, int(math.floor(clip[1] + clip[3] + 0.5)))
        return x0, y0, x1, y1, max(x0, cx0), max(y0, cy0), min(x1, cx1), min(y1, cy1)

    def _resized(self, key, im, w, h):
        k = (key, w, h)
        a = self.rs_cache.get(k)
        if a is None:
            if im.size != (w, h):
                im = im.resize((w, h), Image.BILINEAR)
            a = np.asarray(im, dtype=np.float32) / 255.0
            if len(self.rs_cache) > 4000:
                self.rs_cache.clear()
            self.rs_cache[k] = a
        return a

    def quad(self, x, y, w, h, im, col, clip=None, key=None, angle=0.0, pivot=None, hard=False):
        """One quad: texture RGBA (or None = untextured) x vertex colour col (r,g,b,a), SrcAlpha/InvSrcAlpha
        (hard=True: blending off + alpha test > 0, the Bf outline styles)."""
        if w <= 0 or h <= 0:
            return False
        if angle:
            return self._rotated(x, y, w, h, im, col, clip, key, angle, pivot, hard)
        x0, y0, x1, y1, a0, b0, a1, b1 = self._box(x, y, w, h, clip)
        if a1 <= a0 or b1 <= b0:
            return False
        c = np.array(col[:3], dtype=np.float32)
        if im is None:
            src = np.broadcast_to(c, (b1 - b0, a1 - a0, 3))
            al = np.full((b1 - b0, a1 - a0), col[3], dtype=np.float32)
        else:
            if isinstance(im, np.ndarray):          # a glyph: alpha coverage, white
                cov = im[b0 - y0:b1 - y0, a0 - x0:a1 - x0]
                src = np.broadcast_to(c, cov.shape + (3,))
                al = cov * col[3]
            else:
                a = self._resized(key or id(im), im, x1 - x0, y1 - y0)
                a = a[b0 - y0:b1 - y0, a0 - x0:a1 - x0]
                src = a[:, :, :3] * c
                al = a[:, :, 3] * col[3]
        self._blend(a0, b0, a1, b1, src, al, hard)
        return True

    def _blend(self, a0, b0, a1, b1, src, al, hard):
        dst = self.fb[b0:b1, a0:a1]
        if hard:
            m = al > 0
            dst[m] = np.clip(src, 0, 1)[m] if src.ndim == 3 else src
        else:
            al = np.clip(al, 0, 1)[:, :, None]
            dst[:] = np.clip(src, 0, 1) * al + dst * (1 - al)

    def _rotated(self, x, y, w, h, im, col, clip, key, angle, pivot, hard):
        wi, hi = max(1, int(round(w))), max(1, int(round(h)))
        if im is None:
            src = Image.new('RGBA', (wi, hi), (255, 255, 255, 255))
        elif isinstance(im, np.ndarray):
            src = Image.fromarray((np.clip(im, 0, 1) * 255).astype(np.uint8), 'L')
            src = Image.merge('RGBA', (Image.new('L', src.size, 255),) * 3 + (src,)).resize((wi, hi))
        else:
            src = im.resize((wi, hi), Image.BILINEAR)
        px, py = pivot if pivot is not None else (x + w / 2.0, y + h / 2.0)
        ca, sa = math.cos(angle), math.sin(angle)
        corners = [(x, y), (x + w, y), (x + w, y + h), (x, y + h)]
        rc = [(px + (cx - px) * ca - (cy - py) * sa, py + (cx - px) * sa + (cy - py) * ca) for cx, cy in corners]
        bx0, by0 = math.floor(min(p[0] for p in rc)), math.floor(min(p[1] for p in rc))
        bx1, by1 = math.ceil(max(p[0] for p in rc)), math.ceil(max(p[1] for p in rc))
        bw, bh = int(bx1 - bx0), int(by1 - by0)
        if bw <= 0 or bh <= 0:
            return False
        # inverse map: output (u,v) -> source pixel
        sx, sy = wi / float(w), hi / float(h)
        # canvas point q = (bx0+u, by0+v); unrotate about pivot; then source = ((qx'-x)*sx, (qy'-y)*sy)
        a_ = ca * sx; b_ = sa * sx
        c_ = (ca * (bx0 - px) + sa * (by0 - py) + px - x) * sx
        d_ = -sa * sy; e_ = ca * sy
        f_ = (-sa * (bx0 - px) + ca * (by0 - py) + py - y) * sy
        out = src.transform((bw, bh), Image.AFFINE, (a_, b_, c_, d_, e_, f_), Image.BILINEAR)
        arr = np.asarray(out, dtype=np.float32) / 255.0
        x0, y0, x1, y1, a0, b0, a1, b1 = self._box(bx0, by0, bw, bh, clip)
        if a1 <= a0 or b1 <= b0:
            return False
        arr = arr[b0 - y0:b1 - y0, a0 - x0:a1 - x0]
        c = np.array(col[:3], dtype=np.float32)
        self._blend(a0, b0, a1, b1, arr[:, :, :3] * c, arr[:, :, 3] * col[3], hard)
        return True

    def rect_outline(self, r, rgb, alpha=0.9):
        x, y, w, h = r
        x0, y0 = int(math.floor(x + 0.5)), int(math.floor(y + 0.5))
        x1, y1 = int(math.floor(x + w + 0.5)) - 1, int(math.floor(y + h + 0.5)) - 1
        c = np.array(rgb, dtype=np.float32) / 255.0
        def hl(yy, xa, xb):
            if 0 <= yy < H:
                xa, xb = max(0, xa), min(W - 1, xb)
                if xb >= xa:
                    self.fb[yy, xa:xb + 1] = c * alpha + self.fb[yy, xa:xb + 1] * (1 - alpha)
        def vl(xx, ya, yb):
            if 0 <= xx < W:
                ya, yb = max(0, ya), min(H - 1, yb)
                if yb >= ya:
                    self.fb[ya:yb + 1, xx] = c * alpha + self.fb[ya:yb + 1, xx] * (1 - alpha)
        if x1 < x0 or y1 < y0:
            return
        hl(y0, x0, x1); hl(y1, x0, x1); vl(x0, y0, y1); vl(x1, y0, y1)

    def image(self):
        a = (np.clip(self.fb, 0, 1) * 255 + 0.5).astype(np.uint8)
        rgba = np.dstack([a, np.full((H, W), 255, np.uint8)])
        return Image.fromarray(rgba, 'RGBA')


# ---------------------------------------------------------------------------------------------------- text
LINE_STEP = {'BfStyle': 1.5, 'BfCenterStyle': 1.5, 'BfOutlineStyle': 1.5, 'BfLeftOutlineStyle': 1.5,
             'BfStyle2': 1.25}
WORD_WRAP = {'BfStyle', 'BfStyle2', 'BfCenterStyle', 'BfLeftOutlineStyle'}
CENTRED = {'CenterAlignedStyle', 'BfCenterStyle', 'BfOutlineStyle'}
HARD = {'BfOutlineStyle', 'BfLeftOutlineStyle'}


def layout(style, font, text, rect):
    """Glyph quads [(glyph, x, y)] of `text` laid out by a Style class in rect (x, y, w, h).  Common rules
    [engine]: first baseline at y + line height; glyph quad (pen + A, baseline - top, B, y1 - y0); pen += A+B+C;
    a character without a glyph ends the string (Style 0x00876070: 0x008762CE); no kerning."""
    x, y, w, h = rect
    lh = font.line_height
    G = font.glyphs
    out = []
    if style in ('Style', 'SingleLineStyle', 'RowDistanceStyle', 'SizeStyle', 'SingleLineSizeStyle', None):
        # Style::draw 0x00876070: '\r' -> pen = x; '\n' -> baseline += lh (x NOT reset); per-character wrap when
        # the pen reaches x + w; stops when the baseline reaches y + h
        pen, base = x, y + lh
        for ch in text:
            c = ord(ch)
            if c == 0:
                break
            if c == 13:
                pen = x
                continue
            if c == 10:
                base += lh
                if not base < y + h:
                    break
                continue
            g = G.get(c)
            if g is None:
                break
            pen += g.a
            out.append((g, pen, base - g.top))
            pen += g.b + g.c
            if not pen < x + w:
                pen = x
                base += lh
                if not base < y + h:
                    break
        return out
    if style == 'EditLine':
        # BfEditNode / BfEditNodeInt (0x005676B0): one line from the scroll index (0 here) in the node's own Font;
        # glyphs past the right edge are not drawn [inferred]
        pen, base = x, y + lh
        for ch in text:
            g = G.get(ord(ch))
            if g is None or ord(ch) in (10, 13):
                break
            if pen + g.a + g.b > x + w:
                break
            out.append((g, pen + g.a, base - g.top))
            pen += g.a + g.b + g.c
        return out
    if style == 'RightAlignedStyle':
        # 0x00ABF040: characters walked backwards from x + w; '\n' (and running past x) moves DOWN, so the last
        # line of the string is drawn on top; stops at the bottom
        pen, base = x + w, y + lh
        for ch in reversed(text):
            c = ord(ch)
            if c == 13:
                continue
            if c == 10:
                pen = x + w
                base += lh
                if not base < y + h:
                    break
                continue
            g = G.get(c)
            if g is None:
                break
            pen -= g.a + g.b + g.c
            if pen < x:
                pen = x + w - (g.a + g.b + g.c)
                base += lh
                if not base < y + h:
                    break
            out.append((g, pen + g.a, base - g.top))
        return out
    step = lh * LINE_STEP.get(style, 1.0)
    lines = _split_lines(text, G)
    if style in WORD_WRAP:
        wrapped = []
        for ln in lines:
            wrapped.extend(_word_wrap(ln, G, w))
        lines = wrapped
    base = y + lh
    for ln in lines:
        width = sum(G[ord(c)].a + G[ord(c)].b + G[ord(c)].c for c in ln)
        pen = x + (w - width) * 0.5 if style in CENTRED else x
        for ch in ln:
            g = G[ord(ch)]
            pen += g.a
            out.append((g, pen, base - g.top))
            pen += g.b + g.c
        base += step
    return out


def _split_lines(text, G):
    """Lines split at '\n' ('\r' dropped); the string ends at the first character without a glyph."""
    lines, cur = [], []
    for ch in text:
        c = ord(ch)
        if c == 0:
            break
        if c == 13:
            continue
        if c == 10:
            lines.append(''.join(cur))
            cur = []
            continue
        if c not in G:
            break
        cur.append(ch)
    lines.append(''.join(cur))
    return lines


def _word_wrap(line, G, width):
    adv = lambda s: sum(G[ord(c)].a + G[ord(c)].b + G[ord(c)].c for c in s)
    words = line.split(' ')
    out, cur = [], ''
    for wd in words:
        cand = wd if not cur else cur + ' ' + wd
        if cur and adv(cand) > width:
            out.append(cur)
            cur = wd
        else:
            cur = cand
    out.append(cur)
    return out


# ---------------------------------------------------------------------------------------------------- the walk
TRANSFORMS = {'TransformNode', 'WindowNode', 'DragWindowNode', 'BfTransformNode', 'BfTransformNodeSize'}
EFFECT_NODES = {'EffectNode', 'VariableEffectNode', 'TransitionalEffectNode', 'TransitionalShowEffectNode',
                'TransitionalHideEffectNode', 'ShowEffectNode', 'FocusEffectNode', 'NormalToFocusEffectNode',
                'FocusToPressedEffectNode', 'NotFocusEffectNode', 'ExtendedNormalToFocusEffectNode',
                'ExtendedFocusToPressedEffectNode'}
PLAIN = {'Node', 'NameNode', 'PathNode', 'StackPathNode', 'DisableNode', 'ActionNode', 'CullEventActionNode',
         'CullVariableActionNode', 'CullVariableAndEventActionNode', 'TimeoutActionNode', 'CullActionNode',
         'BfVariableTimeoutActionNode', 'BfVariableTimeoutActionNode2', 'IfElseEventActionNode',
         'CullEventTimeoutActionNode', 'BfLocaleNode', 'BfCreditsNode', 'BfAddSubEffectNode',
         'BfAddSubNextEffectNode', 'BfListBoxNode'}
RUNTIME_ONLY = {'BfOccupiedVehicleNode', 'BfNewCreditsListNode', 'BfControlPointGauge', 'BfBinkNode',
                'BfVerticalScrollNode', 'BfVerticalScrollExNode', 'NavigateViewNode', 'BfAnimationNode',
                'BfGaugeNode', 'BfRectangle', 'NodeListNode'}
COL = {'transform': (0, 200, 255), 'draw': (255, 220, 0), 'runtime': (255, 140, 0), 'missing': (255, 0, 255),
       'hidden': (255, 40, 40)}


class Walker(object):
    def __init__(self, rt, canvas, outlines=False, overrides=None):
        self.rt, self.cv, self.outlines = rt, canvas, outlines
        self.boxes = []
        self.active = set()
        self.overrides = overrides or {}       # PathNode (file) name -> destination file name (context render)
        self.fonts_missing = collections.Counter()
        self.target = None                     # context render: the page file; outside it culls use 'defaults'
        self.in_target = True

    def mode(self):
        return self.rt.mode if self.in_target else 'defaults'

    # -- state: rect [x,y,w,h] (mutable, shared with the parent like the engine's rect*), clip, effects (outer ->
    #    inner), hidden (content 'defaults' would hide; only in 'all' / 'dim')
    def chain(self, node, rect, clip, effects, hidden, depth=0):
        rt = self.rt
        steps = 0
        while node is not None:
            steps += 1
            if steps > 100000 or depth > 400:
                rt.notes['walk depth limit'] += 1
                return
            if node.kind == 'ref':
                name = node.name
                node = rt.resolve(node)
                if node is None:
                    return
                if id(node) in self.active:
                    rt.notes['reference cycle at %s' % name] += 1
                    return
                self.active.add(id(node))
                prev = self.in_target
                if self.target is not None and name == self.target:
                    self.in_target = True
                try:
                    self.chain(node, rect, clip, effects, hidden, depth + 1)
                finally:
                    self.active.discard(id(node))
                    self.in_target = prev
                return
            node = rt.resolve(node)
            if node is None or node.fields is None:
                if node is not None:
                    rt.stats['opaque record skipped'] += 1
                return
            s = node.short
            try:
                nxt = node['Next node']
            except KeyError:
                nxt = None
            rt.stats['node ' + s] += 1
            if s in TRANSFORMS:
                if s == 'BfTransformNode':
                    X, Y = rt.val(node['X']), rt.val(node['Y'])
                    Wd, Hd = node['Width'], node['Height']
                elif s == 'BfTransformNodeSize':
                    X, Y = node['X'], node['Y']
                    Wd, Hd = rt.val(node['Width']), rt.val(node['Height'])
                else:
                    X, Y, Wd, Hd = node['X'], node['Y'], node['Width'], node['Height']
                child = [rect[0] + X, rect[1] + Y, Wd, Hd]
                if self.outlines:
                    self.boxes.append(('transform', tuple(child), hidden))
                self.chain(node['Transformed node'], child, clip, effects, hidden, depth + 1)
            elif s == 'TranslateNode':
                # 0x008724A0 / 0x007E96A0: its OWN continuation in (x + X, y + Y, w, h); null Data = 0
                rect = [rect[0] + rt.val(node['X']), rect[1] + rt.val(node['Y']), rect[2], rect[3]]
            elif s == 'SplitNode':
                self.chain(node['Split node'], rect, clip, effects, hidden, depth + 1)
            elif s == 'ClipNode':
                clip = rect                           # replaced, not intersected (0x00869C40)
            elif s == 'CullNode':
                d = node['Data']
                vis = rt.resolve(d) is None or rt.val(d) != 0
                rt.stats['cull visible' if vis else 'cull hidden'] += 1
                if not vis:
                    if self.mode() == 'defaults':
                        return
                    hidden = True
            elif s in EFFECT_NODES:
                e = rt.resolve(node['Effect'])
                if s.startswith('Transitional') or 'Focus' in s or s == 'ShowEffectNode':
                    e = None                          # pushes only while a transition / focus runs; static: none
                    rt.stats['effect node inactive in a static frame'] += 1
                if e is not None:
                    lvl = rt.val(node['Effect level']) if s == 'VariableEffectNode' else 1.0
                    effects = effects + ((e, lvl),)
            elif s == 'PathNode' or s == 'StackPathNode':
                pass
            elif s in PLAIN:
                pass
            else:
                effects2 = effects
                try:
                    handled = self.draw_self(node, s, rect, clip, effects2, hidden)
                except Exception as ex:
                    rt.notes['draw error %s: %s' % (s, ex)] += 1
                    handled = True
                if not handled:
                    rt.notes['class not drawn (unknown to the renderer): ' + s] += 1
            # PathNode override (context render): the file's layer shows another destination
            if s == 'PathNode' and self.overrides:
                for fname, dest in self.overrides.items():
                    if rt.reg.get(fname) is node:
                        nxt = meme.ref(dest)
                        break
            node = nxt

    # -- colour / geometry of the effect stack
    def colour(self, effects, dim, flat=False):
        """Vertex colour after the effect stack: innermost effect applied first, outermost last (state walk
        0x008755E0 / 0x00875580 -> Effect +0x4C / +0x48 continuing outward), so an outer SET colour wins."""
        rt = self.rt
        col = [1.0, 1.0, 1.0, 1.0]
        angle, pivot = 0.0, None
        for e, lvl in reversed(effects):
            e = rt.resolve(e)
            if e is None or e.fields is None:
                continue
            s = e.short
            if s == 'ColorEffect':
                col = [e['Red'], e['Green'], e['Blue'], e['Alpha']]
            elif s == 'VariableColorEffect':
                col = [rt.val(e[k]) if rt.resolve(e[k]) is not None else 1.0 for k in ('Red', 'Green', 'Blue', 'Alpha')]
                if col[3] == 0 and not flat and self.settle(e['Alpha']):
                    col[3] = 1.0
            elif s == 'MultiplyColorEffect':
                col = [col[i] * e[k] for i, k in enumerate(('Red', 'Green', 'Blue', 'Alpha'))]
            elif s == 'BfMultiplyColorEffect':
                col = [col[i] * (rt.val(e[k]) if rt.resolve(e[k]) is not None else 1.0)
                       for i, k in enumerate(('Red', 'Green', 'Blue', 'Alpha'))]
            elif s == 'BfMultiplyColorEffect2':
                a = rt.val(e['Alpha']) if rt.resolve(e['Alpha']) is not None else 1.0
                if a == 0 and self.settle(e['Alpha']):
                    a = 1.0
                col[3] *= a
            elif s == 'AlphaFadeEffect':
                col[3] *= lvl
            elif s == 'RotateEffect':
                angle += rt.val(e['Start angle']) + rt.val(e['Angle multiplyer']) * lvl
            elif s == 'RotateAroundCoordinateEffect':
                angle += rt.val(e['Start angle']) + rt.val(e['Angle multiplyer']) * lvl
                pivot = (rt.val(e['X']) + 400.0, rt.val(e['Y']) + 300.0)       # engine coords [inferred]
            else:
                rt.notes['effect not applied (untraced): ' + s] += 1
        if any(c > 1.0 or c < 0.0 for c in col):
            rt.stats['vertex colour outside 0..1 (clamped; the engine packs it unclamped)'] += 1
            col = [min(1.0, max(0.0, c)) for c in col]
        if dim:
            col[3] *= 0.3
        return col, angle, pivot

    def settle(self, d):
        """'all' / 'dim' only: a NAMED alpha variable that is 0 at load, on a textured quad or text, is a fade's start
        value (FocusEffect/*, Navigation/Level3/Fade/AlphaFadeN, animated by BfAddSub(Next)EffectNode or set by show
        actions); the preview shows it faded in (editor rule, not engine behaviour).  Excluded: a VariableColorEffect
        alpha on an untextured quad, where 0 is the resting state of a fade cover."""
        if self.mode() not in ('all', 'dim'):
            return False
        if d is not None and d.kind in ('object', 'ref') and d.name:
            self.rt.stats['named alpha 0 settled to 1 (all mode)'] += 1
            return True
        return False

    def box(self, kind, r, hidden):
        if self.outlines:
            self.boxes.append((kind, (r[0], r[1], r[2], r[3]), hidden))

    def picture(self, handle, r, clip, effects, hidden, kind_src='literal', node_cls='', null_draws=True):
        """null_draws: PictureNode's drawSelf (0x005600F0, shared by VariablePictureNode / BfVariablePictureNode)
        has no null check, so an empty handle draws an untextured quad in the vertex colour (0x00882850 null branch);
        the buttons check and draw nothing (0x0055D7BD, 0x0055D91E).  A runtime picture (name from a variable) that
        is empty or does not resolve is not drawn in the preview (editor rule; the engine would draw a white quad or
        its default texture)."""
        rt = self.rt
        if kind_src == 'runtime' and not handle:
            rt.stats['runtime picture unset (not drawn)'] += 1
            self.box('runtime', r, hidden)
            return
        if not handle and not null_draws:
            rt.stats['button with an empty picture (nothing drawn)'] += 1
            return
        if not handle:
            # an untextured quad whose VariableColorEffect SETS a variable alpha is a fade COVER (e.g. BF1942
            # IntroNavigation's black quad, VariableColorEffect 0,0,0,Navigation/Level2/Fade/CurrentAlpha4): its
            # alpha-0 default is the resting state, so that alpha is never 'settled' [editor].  A page fade
            # (BfMultiplyColorEffect2) over a flat colour box is still settled.
            col, ang, piv = self.colour(effects, hidden and self.mode() == 'dim', flat=True)
            self.cv.quad(r[0], r[1], r[2], r[3], None, col, clip, angle=ang, pivot=piv)
            rt.stats['flat quad (empty picture)'] += 1
            self.box('draw', r, hidden)
            return
        im, path = rt.assets.texture(handle)
        if im is None:
            rt.stats['picture missing'] += 1
            rt.notes['picture missing %s (%s)' % (handle, node_cls)] += 1
            if self.outlines or kind_src != 'runtime':
                self.box('missing', r, hidden)
            return
        col, ang, piv = self.colour(effects, hidden and self.mode() == 'dim')
        self.cv.quad(r[0], r[1], r[2], r[3], im, col, clip, key=path, angle=ang, pivot=piv)
        rt.stats['picture drawn'] += 1
        self.box('runtime' if kind_src == 'runtime' else 'draw', r, hidden)

    def draw_text(self, text, src, style_rec, r, clip, effects, hidden, outline=False, font_handle=None,
                  single_line=False):
        rt = self.rt
        style = rt.resolve(style_rec) if style_rec is not None else None
        scls = style.short if style is not None and style.fields is not None else None
        fh = font_handle if font_handle is not None else (style['Font handle'] if scls else None)
        if fh is None:
            rt.notes['text with a null Style: the System default style (BFVIETNAM.font) is not modelled'] += 1
            fh = 'standard6.dif'
        font = rt.assets.font(fh)
        if font is None:
            self.fonts_missing[fh] += 1
            rt.notes['font missing %r' % fh] += 1
            self.box('missing', r, hidden)
            return
        if single_line:
            scls = 'EditLine'
        quads = layout(scls, font, text or '', r)
        col, ang, piv = self.colour(effects, hidden and self.mode() == 'dim')
        hard = scls in HARD
        drawn = 0
        if outline:
            # BfTextNode Type != 0 (0x00556400): 8 black copies one device pixel away, in a private state that
            # outer effects do not reach; offsets (-1,0) (1,0) (1,-1) (1,1) x2 (-1,1) (-1,-1), no vertical ones.
            # Drawn here BEHIND the main text (z -0.55 vs -0.33; the projection's z sense is inferred).
            for dx, dy in ((-1, 0), (1, 0), (1, -1), (1, 1), (-1, 1), (-1, -1)):
                for g, qx, qy in quads:
                    if g.code == 32:
                        continue
                    self._glyph(font, g, qx + dx, qy + dy, (0.0, 0.0, 0.0, 1.0 if not (hidden and self.mode() == 'dim') else 0.3), clip, hard)
        for g, qx, qy in quads:
            if g.code == 32:
                continue                              # a space draws no quad (0x0088404B)
            if self._glyph(font, g, qx, qy, col, clip, hard, ang):
                drawn += 1
        rt.stats['glyphs drawn'] += drawn
        rt.stats['text ' + src] += 1
        self.box('runtime' if src == 'runtime' else 'draw', r, hidden)

    def _glyph(self, font, g, qx, qy, col, clip, hard, angle=0.0):
        rw, rh = g.x1 - g.x0, g.y1 - g.y0
        ah, aw = font.atlas.shape
        y1c, x1c = min(g.y1, ah), min(g.x1, aw)
        if rw <= 0 or rh <= 0 or x1c <= g.x0 or y1c <= g.y0:
            return False
        cov = font.atlas[g.y0:y1c, g.x0:x1c]
        if cov.shape != (rh, rw):                     # rect past the atlas edge (Trebuchet MS11): pad [inferred]
            pad = np.zeros((rh, rw), np.float32)
            pad[:cov.shape[0], :cov.shape[1]] = cov
            cov = pad
        bw = int(round(g.b))
        if bw != rw and bw > 0:                      # the quad is B wide: the glyph is stretched (data)
            idx = (np.arange(bw) * rw // bw)
            cov = cov[:, idx]
        x0, y0 = int(math.floor(qx + 0.5)), int(math.floor(qy + 0.5))
        return self.cv.quad(x0, y0, cov.shape[1], cov.shape[0], cov, col, clip, hard=hard)

    # -- the drawing classes (drawSelf, vtable +0x70 BFV / +0x68 BF1942)
    def draw_self(self, n, s, rect, clip, effects, hidden):
        rt = self.rt
        r = rect
        if s == 'PictureNode':
            self.picture(n['Picture'], r, clip, effects, hidden, node_cls=s)
        elif s == 'VariablePictureNode':
            name = ''.join(rt.string(n[k])[0] for k in ('First part', 'Middle part', 'Last part'))
            rtm = any(rt.is_runtime(n[k]) for k in ('First part', 'Middle part', 'Last part'))
            self.picture(name, r, clip, effects, hidden, 'runtime' if rtm else 'literal', s)
        elif s == 'BfVariablePictureNode':
            name, src = rt.string(n['picture str'])
            self.picture(name, r, clip, effects, hidden, src, s)
        elif s in ('BfButtonNode', 'BfSelectButtonNode'):
            # nothing before the first update; after it, Picture (pointer off the W x H hit box) [engine 0x0055CDB0]
            self.picture(n['Picture'], r, clip, effects, hidden, node_cls=s, null_draws=False)
        elif s == 'BfNavigationButtonNode':
            idx = n['Index']
            mob = n['MouseOver button']
            cm = rt.val(n['Current mouseover index'])
            cc = rt.val(n['Current clicked index'])
            state = 0
            if rt.resolve(n['MouseOver active']) is not None and _round(cm) == idx:
                state = 2 if mob else 1
            if _round(cc) == idx:
                state = 1 if mob else 3
            hover = False
            if mob:
                vis = (hover or state == 2) and state != 1
            else:
                vis = (not hover and state == 0) or state == 3
            pic = {0: n['Picture'], 2: n['Mouse over picture'], 3: n['Clicked picture']}.get(state, n['Picture'])
            if vis:
                self.picture(pic, r, clip, effects, hidden, node_cls=s, null_draws=False)
            else:
                rt.stats['navigation button idle-hidden (MouseOver variant)'] += 1
        elif s == 'BfLocalePictureButtonNode':
            # 0x0055E8C0: rect.x = CurrentX - screenW/2 written INTO THE CALLER'S RECT (and clip); texture
            # 'Menu/Texture/' + Picture Path + <language> + '/' + Picture Name (0x0055E740)
            cx = rt.val(n['Current X'])
            rect[0] = cx
            if clip is not None and clip is not rect and isinstance(clip, list):
                clip[0] = cx                          # the clip's x is written too (0x0055EA3F..0x0055EA5F)
            path = rt.string(n['Picture Path'])[0]
            pname = rt.string(n['Picture Name'])[0]
            handle = path + rt.assets.lang + '/' + pname
            self.picture(handle, rect, clip, effects, hidden, node_cls=s, null_draws=False)
        elif s == 'BfTriangleButtonsNode':
            team = rt.val(n['Selected Team'])
            self.picture(n['Bottom Triangle Selected'] if team == 1 else n['Top Triangle Selected'], r, clip,
                         effects, hidden, node_cls=s, null_draws=False)
        elif s in ('BfVariablePictureFillNode', 'BfVariablePictureFillNode2'):
            if s == 'BfVariablePictureFillNode':
                pic, fill, size = n['Picture'], n['Fill picture'], float(n['Size'])
            else:
                pic, fill = rt.string(n['Picture'])[0], rt.string(n['Fill picture'])[0]
                size = rt.val(n['Size'])
            mx = rt.val(n['Maximum value'])
            val = rt.val(n['Data'])
            if mx > 0 and size > 0:
                self.picture(pic, r, clip, effects, hidden, node_cls=s)
                f = size * val / mx
                if n['Horizontal align']:
                    fc = [r[0] if n['Fill order'] else r[0] + size - f, r[1], f, r[3]]
                else:
                    fc = [r[0], r[1] + size - f if n['Fill order'] else r[1] + r[3] - size, r[2], f]
                if f > 0:
                    self.picture(fill, r, fc, effects, hidden, node_cls=s)
            else:
                rt.stats['fill node: Max or Size <= 0, nothing drawn'] += 1
                self.box('runtime', r, hidden)
        elif s == 'BfScrollPictureNode':
            self.picture(n['Scroll picture'], r, clip, effects, hidden, node_cls=s)
            rt.stats['approximate: BfScrollPictureNode drawn as one quad over the rect'] += 1
        elif s == 'TextNode':
            t, src = rt.text(n['String'])
            if t is None:
                return True                           # a null String draws nothing (0x00872C10)
            self.draw_text(t, src, n['Style'], r, clip, effects, hidden)
        elif s == 'BfTextNode':
            t, src = rt.text(n['String'])
            if t is None:
                return True
            ty = rt.val(n['Type'])
            if ty == 0:
                rect[1] += 1                          # Type 0 adds 1 to the CALLER'S rect.y (0x00556400)
                self.draw_text(t, src, n['Style'], rect, clip, effects, hidden)
            else:
                self.draw_text(t, src, n['Style'], r, clip, effects, hidden, outline=True)
        elif s in ('BfEditNode', 'BfEditNodeInt', 'EditNode', 'BfEditNodeNew'):
            d = n['Int'] if s == 'BfEditNodeInt' else n['String']
            t, src = rt.text(d)
            self.draw_text(t or '', src if t else 'runtime', None, r, clip, effects, hidden, font_handle=n['Font'],
                           single_line=True)
        elif s == 'BfNewListBoxNode':
            col, ang, piv = self.colour(effects, hidden and self.mode() == 'dim')
            bg = (n['Background color red'], n['Background color green'], n['Background color blue'],
                  n['Background color alpha'] * col[3])
            if bg[3] > 0:
                self.cv.quad(r[0], r[1], r[2], r[3], None, bg, clip)
            if n['Border or not'] and n['Frame color alpha'] > 0:
                fc = tuple(min(1.0, c) for c in (n['Frame color red'], n['Frame color green'], n['Frame color blue'])) + (n['Frame color alpha'] * col[3],)
                for q in ((r[0], r[1], r[2], 1), (r[0], r[1] + r[3] - 1, r[2], 1), (r[0], r[1], 1, r[3]),
                          (r[0] + r[2] - 1, r[1], 1, r[3])):
                    self.cv.quad(q[0], q[1], q[2], q[3], None, fc, clip)
            rt.stats['list box: rows are runtime (not drawn)'] += 1
            self.box('runtime', r, hidden)
        elif s in ('BfSliderNode', 'BfFixedSliderNode', 'SliderNode'):
            mn, mx, vis = n['Minimum value'], n['Maximum value'], n['Number visible']
            rng = abs(mx - mn) or 1.0
            cw = r[2] * vis / rng
            v = rt.val(n['Data'])
            off = (v - mn) * (r[2] - cw) / (mx - mn) if mx != mn else 0.0
            off = max(0.0, min(r[2] - cw, off)) if r[2] >= cw else 0.0
            self.chain(n['Cursor node'], [r[0] + off, r[1], cw, r[3]], clip, effects, hidden)
            self.box('draw', r, hidden)
        elif s == 'BfCrosshairNode':
            self._crosshair(n, r, clip, effects, hidden)
        elif s == 'BfBinkNode':
            name, src = rt.string(n['Background picture'])
            if name:
                self.picture(name, r, clip, effects, hidden, 'runtime', s)
            else:
                self.box('runtime', r, hidden)
            rt.stats['runtime-only node ' + s] += 1
        elif s in RUNTIME_ONLY:
            rt.stats['runtime-only node ' + s] += 1
            self.box('runtime', r, hidden)
        else:
            return False
        return True

    def _crosshair(self, n, r, clip, effects, hidden):
        """8 untextured quads round the rect centre (0x00561680); arm geometry [inferred]."""
        rt = self.rt
        rad, th, oth, dev = (rt.val(n[k]) for k in ('Radius', 'Thickness', 'outline thickness', 'Deviation'))
        def bcol(k):
            c = rt.resolve(n[k])
            if c is None or c.short != 'BfColorData':
                return (1.0, 1.0, 1.0, 1.0)
            return (c['Red'], c['Green'], c['Blue'], c['Alpha'])
        cc, oc = bcol('color'), bcol('outline')
        cx, cy = r[0] + r[2] / 2.0, r[1] + r[3] / 2.0
        L = max(rad, 1.0)
        gap = dev
        for (dx, dy) in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            for colr, t in ((oc, th + 2 * oth), (cc, th)):
                if dx:
                    x0 = cx + dx * gap if dx > 0 else cx - gap - L
                    q = (x0 - (oth if colr is oc else 0), cy - t / 2.0, L + (2 * oth if colr is oc else 0), t)
                else:
                    y0 = cy + dy * gap if dy > 0 else cy - gap - L
                    q = (cx - t / 2.0, y0 - (oth if colr is oc else 0), t, L + (2 * oth if colr is oc else 0))
                self.cv.quad(q[0], q[1], q[2], q[3], None, colr, clip)
        rt.stats['approximate: crosshair arms'] += 1
        self.box('runtime', r, hidden)


def _round(v):
    try:
        return int(math.floor(v + 0.5)) if v >= 0 else -int(math.floor(-v + 0.5))
    except (ValueError, OverflowError):
        return None


# ---------------------------------------------------------------------------------------------------- context
def context_overrides(assets, game, target):
    """The game-root context of a page: (root_name, {layer: destination}, {variable: value}) or (None, {}, {}).

    1. The layer chain [data-driven]: a *Layer file (PathNode root) L can show F when its Next is F (the default) or a
       SetPathAction has (Path node L, Destination node F); L is drawn by the file whose chain references it by name
       (Next / Split / Transformed node).  Walk up from the page to BattleField, the root of the front end.
    2. The navigation replay [editor rule]: the action list that navigates to the page (an ActionListAction holding
       SetPathAction(*, page); the one with the most SetPathActions wins, e.g. Background's 'return to this page'
       lists or a *Navigation button) is replayed: its other SetPathActions point their layers (the navigation bars
       that belong to the page) and its SetVariableActions with a literal value become variable toggles (selected
       button index, navigation bar position).  Game-code functions (CallFunctionAction) are not run."""
    holders, where, groups = _layer_graph(assets, game)
    ov, chain = {}, []
    cur, seen = target, set()
    root = None
    for _ in range(16):
        if cur in seen:
            break
        seen.add(cur)
        if cur == 'BattleField':
            root = cur
            break
        lays = holders.get(cur)
        if not lays:
            parents = where.get(cur)
            if not parents:
                break
            cur = sorted(parents)[0]
            continue
        lay = sorted(lays)[0]
        ov[lay] = cur
        chain.append(lay)
        parents = where.get(lay)
        if not parents:
            break
        cur = sorted(parents)[0]
    if root is None:
        return None, {}, {}
    best = None
    for key, g in groups:
        if any(d == target for l, d in g['paths']):
            score = (len(g['paths']), -len(key))
            if best is None or score > best[0]:
                best = (score, g)
    vars_ = {}
    if best is not None:
        for l, d in best[1]['paths']:
            if l not in ov:
                ov[l] = d
        vars_ = dict(best[1]['vars'])
    return root, ov, vars_


_LG = {}


def _layer_graph(assets, game):
    k = (game, assets.arch['mod'])
    if k in _LG:
        return _LG[k]
    holders = collections.defaultdict(set)      # page -> layers that can show it
    where = collections.defaultdict(set)        # file -> files whose drawn chain references it by name
    groups = []                                 # (sort key, {'paths': [(layer, dest)], 'vars': [(name, value)]})
    names = set()
    for ix in assets.menu:
        for rel, p in ix.map.items():
            if '/' not in rel:
                names.add(os.path.basename(p))

    def flatten(a, out):
        if a is None or a.fields is None:
            return
        if a.short == 'ActionListAction':
            for x in a['Action list']:
                flatten(x, out)
        elif a.short == 'SplitAction':
            flatten(a['Action 1'], out)
            flatten(a['Action 2'], out)
        else:
            out.append(a)

    for name in sorted(names):
        data = assets.menu_file(name)
        if not data or data[1:13] != b'MemeFile 2.0':
            continue
        try:
            mf = meme.parse(data, game)
        except Exception:
            continue
        if mf.root.short == 'PathNode':
            nx = mf.root['Next node']
            if nx is not None and nx.kind == 'ref':
                holders[nx.name].add(name)
        for i, r in enumerate(mf.walk()):
            if r.short == 'SetPathAction':
                p, d = r['Path node'], r['Destination node']
                if p is not None and d is not None and p.kind == 'ref' and d.kind == 'ref':
                    holders[d.name].add(p.name)
            if r.short == 'ActionListAction':
                acts = []
                flatten(r, acts)
                g = {'paths': [], 'vars': []}
                for a in acts:
                    if a.short == 'SetPathAction':
                        p, d = a['Path node'], a['Destination node']
                        if p is not None and d is not None and p.kind == 'ref' and d.kind == 'ref':
                            g['paths'].append((p.name, d.name))
                    elif a.short == 'SetVariableAction':
                        v, d = a['Variable'], a['Data']
                        if v is not None and v.name and d is not None and d.kind == 'object' and not d.name and                                 d.short in ('BoolData', 'IntData', 'FloatData'):
                            g['vars'].append((v.name, float(d.fields[0].value)))
                if g['paths']:
                    groups.append(('%s:%06d' % (name, i), g))
            for f in r.fields or ():
                if f.tok == 'O' and f.value is not None and f.value.kind == 'ref' and                         f.label in ('Next node', 'Split node', 'Transformed node') and r.short != 'PathNode':
                    where[f.value.name].add(name)
    _LG[k] = (holders, where, groups)
    return _LG[k]


# ---------------------------------------------------------------------------------------------------- API
def render(data, game, archives, mode='defaults', outlines=False, bg=(0, 0, 0), name=None, context=False,
           variables=None):
    """-> (PIL RGBA 800x600, stats dict).  `data` = the menu file's bytes; `archives` = archives_for(...);
    `name` = the file's name (its root is registered under it); `variables` = {variable name: value} editor toggles
    that replace a named Data's file default wherever it is read (culls, colours, positions)."""
    assets = Assets.get(archives)
    rt = Rt(game, assets, mode, variables)
    mf = meme.parse(data, game)
    cv = Canvas(bg)
    overrides = {}
    root = mf.root
    if context and name:
        rootname, overrides, replay = context_overrides(assets, game, name)
        if replay:
            merged = dict(replay)
            merged.update(rt.vars)                   # explicit toggles win over the navigation replay
            rt.vars = merged
            rt.stats['navigation replay variables'] = len(replay)
        if rootname and rootname != name:
            rdata = assets.menu_file(rootname)
            rmf = meme.parse(rdata, game)
            # the page itself is registered first so that its own copies of shared variables win
            rt.register_file(mf, name)
            rt.register_file(rmf, rootname)
            root = rmf.root
            rt.stats['context root ' + rootname] += 1
        else:
            overrides = {}
            rt.register_file(mf, name)
    else:
        rt.register_file(mf, name or '')
    wk = Walker(rt, cv, outlines, overrides)
    if overrides:
        wk.target, wk.in_target = name, False
    wk.chain(root, [0.0, 0.0, float(W), float(H)], None, (), False)
    if outlines:
        for kind, r, hid in wk.boxes:
            cv.rect_outline(r, COL['hidden'] if hid and kind != 'transform' else COL[kind],
                            0.5 if kind == 'transform' else 0.9)
    stats = dict(rt.stats)
    stats['_notes'] = dict(rt.notes)
    stats['_overrides'] = overrides
    return cv.image(), stats


def render_png(data, game, archives, **kw):
    """render() -> the 800x600 RGBA PNG as bytes."""
    import io
    img, _ = render(data, game, archives, **kw)
    buf = io.BytesIO()
    img.save(buf, 'PNG')
    return buf.getvalue()


def corpus_files():
    out = []
    for f in sorted(os.listdir(os.path.join(CORPUS, 'BFV', 'BfVietnam', 'menu'))):
        out.append(('BFV', 'BfVietnam', f, os.path.join(CORPUS, 'BFV', 'BfVietnam', 'menu', f)))
    for mod in ('bf1942', 'XPack1', 'XPack2'):
        d = os.path.join(CORPUS, 'BF1942', mod, 'menu')
        for f in sorted(os.listdir(d)):
            out.append(('BF1942', mod, f, os.path.join(d, f)))
    return out


# The saved defaults the corpus context renders flip (editor toggles, listed in docs/research/menu-render.md of RefractorDevelopmentKit):
# * BF1942's ConfirmationMenu was saved with Join/Internet/ShowLegend = 1, so every context render would carry the
#   server-browser legend dialog; only ConfirmationMenu's CLOSE button (0) and the Internet/Local menus' legend
#   buttons (1) write it [data, writers.py].
# * FocusEffect/Init/ShowBackground is 0 at load and set to 1 by Background's own TimeoutActionNode (2.0 s) and by its
#   settle CullVariableActionNode [data], i.e. the menu background appears two seconds after start.
CONTEXT_TOGGLES = {'BF1942': {'Join/Internet/ShowLegend': 0, 'FocusEffect/Init/ShowBackground': 1},
                   'BFV': {'FocusEffect/Init/ShowBackground': 1}}


def run_corpus(outdir, modes=('all', 'defaults'), outlines_too=True, context_too=True):
    summary = {'files': 0, 'renders': 0, 'crashes': [], 'per_file': {}}
    agg = collections.Counter()
    notes = collections.Counter()
    t0 = time.time()
    for game, mod, name, path in corpus_files():
        arch = archives_for(game, mod)
        data = open(path, 'rb').read()
        label = name if mod in ('BfVietnam', 'bf1942') else '%s_%s' % (mod, name)
        od = os.path.join(outdir, game)
        os.makedirs(od, exist_ok=True)
        summary['files'] += 1
        for m in modes:
            try:
                img, st = render(data, game, arch, mode=m, name=name)
                img.save(os.path.join(od, '%s-%s.png' % (label, m)))
                summary['renders'] += 1
                for k, v in st.items():
                    if not k.startswith('_'):
                        agg['%s %s' % (m, k)] += v
                for k, v in st['_notes'].items():
                    notes['%s | %s' % (game, k)] += v
                summary['per_file']['%s/%s-%s' % (game, label, m)] = {
                    'glyphs': st.get('glyphs drawn', 0), 'pictures': st.get('picture drawn', 0),
                    'missing': st.get('picture missing', 0), 'hidden culls': st.get('cull hidden', 0)}
            except Exception:
                summary['crashes'].append(('%s/%s' % (game, label), m, traceback.format_exc()[-600:]))
        if outlines_too:
            try:
                img, st = render(data, game, arch, mode='all', outlines=True, name=name)
                img.save(os.path.join(od, '%s-all-outlines.png' % label))
                summary['renders'] += 1
            except Exception:
                summary['crashes'].append(('%s/%s' % (game, label), 'all-outlines', traceback.format_exc()[-600:]))
        if context_too:
            try:
                rootname, ov, _ = context_overrides(Assets.get(arch), game, name)
                if rootname and rootname != name:
                    img, st = render(data, game, arch, mode='all', name=name, context=True,
                                     variables=CONTEXT_TOGGLES.get(game))
                    img.save(os.path.join(od, '%s-context.png' % label))
                    summary['renders'] += 1
                    summary['context'] = summary.get('context', 0) + 1
            except Exception:
                summary['crashes'].append(('%s/%s' % (game, label), 'context', traceback.format_exc()[-600:]))
    summary['seconds'] = round(time.time() - t0, 1)
    summary['aggregate'] = dict(agg)
    summary['notes'] = dict(notes.most_common())
    json.dump(summary, open(os.path.join(outdir, 'corpus_summary.json'), 'w'), indent=1, sort_keys=True)
    return summary


def main(argv):
    if len(argv) >= 2 and argv[1] == 'corpus':
        out = argv[argv.index('--out') + 1] if '--out' in argv else os.path.join(HERE, 'png')
        s = run_corpus(out)
        print('files %d, renders %d, crashes %d, %.1fs' % (s['files'], s['renders'], len(s['crashes']), s['seconds']))
        for c in s['crashes']:
            print('CRASH', c[0], c[1], c[2])
        return 0 if not s['crashes'] else 1
    if len(argv) >= 6 and argv[1] == 'one':
        game, mod, name, out = argv[2:6]
        mode = argv[argv.index('--mode') + 1] if '--mode' in argv else 'defaults'
        p = os.path.join(CORPUS, game, mod, 'menu', name)
        img, st = render(open(p, 'rb').read(), game, archives_for(game, mod), mode=mode,
                         outlines='--outlines' in argv, name=name, context='--context' in argv)
        img.save(out)
        print(json.dumps(st, indent=1, sort_keys=True, default=str))
        return 0
    print(__doc__)
    return 2


if __name__ == '__main__':
    sys.exit(main(sys.argv))
