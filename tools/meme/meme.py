r"""meme.py - reference reader/writer for Refractor "Meme 2.0" menu files (the extensionless entries of
menu.rfa) for Battlefield 1942 (BF1942.exe) and Battlefield Vietnam (BfVietnam.exe).

    import meme
    mf = meme.parse(open('InGame', 'rb').read(), 'BFV')     # -> MemeFile (tree of Records)
    mf.root.cls, mf.root['Next node'] ...
    data = meme.serialize(mf)                                 # byte-exact when nothing was changed

The class layouts come from meme_schema.json (next to this file); the spec is docs/Meme_Format.md.

FILE
    string table   { u8 len; len bytes latin1 }* then a 0x00 length byte.  strs[0] == "MemeFile 2.0".
                   Header/payload indices are 1-based into this array (0 = none).  <= 511 strings after
                   the magic (ClassIStream holds table[512]; the fill loop has no bounds check).
    u16            root class index (NOT a record header: the root has no size / name).
    payload        the root class's positional payload, running to EOF.
RECORD (every nested object)
    u32 size (counted from the size field itself), u16 nameIdx, u16 classIdx, payload[size-8].
    classIdx == 0: nameIdx == 0 is a null pointer, nameIdx != 0 a by-name reference; 8 bytes.
OLD FORMAT (engine fallback when the magic is missing; no string table): the root class name and every
    record's object / class name are inline u8-length strings.  Engine-proven, no sample file exists.

Model: MemeFile(game, root, strings, ...) ; Record(cls, name, fields | raw) ; Field(tok, label, value).
    Record.kind: 'object' (decoded), 'opaque' (class unknown to this game, or its payload does not fit the
    layout - kept as raw payload bytes so nothing is lost), 'null', 'ref' (by-name reference).
    O -> Record ; LIST -> [Record] (the terminating null record is implicit, see Field.end) ;
    f32 -> float ; i32 -> int ; u8/bool -> int ; s8/str32 -> str (latin1) ; wstr32 -> str ;
    TREE -> nested dict {'fields', 'child', 'next', 'prev'} or None.

Command line:
    python meme.py corpus [--json out.json]     run every corpus group, report coverage / byte-exactness
    python meme.py dump <file> <BF1942|BFV>     print the tree
    python meme.py check <file> <BF1942|BFV>    parse + re-serialize, report
    python meme.py lint <file> <BF1942|BFV>     editor-level findings (see lint())
"""
import json, math, os, struct, sys, collections

HERE = os.path.dirname(os.path.abspath(__file__))
MAGIC = 'MemeFile 2.0'
MAX_STRINGS = 511                      # after the magic
NS = 'dice::meme::'

_SCHEMA = None


def schema(game=None):
    global _SCHEMA
    if _SCHEMA is None:
        _SCHEMA = json.load(open(os.path.join(HERE, 'meme_schema.json')))
    return _SCHEMA if game is None else _SCHEMA[game]


class ParseError(Exception):
    pass


# ------------------------------------------------------------------------------------------------ model
class Field(object):
    __slots__ = ('tok', 'label', 'value', 'end')

    def __init__(self, tok, label, value, end=None):
        self.tok, self.label, self.value = tok, label, value
        self.end = end                  # LIST only: a non-canonical terminator Record (None = plain null)

    def __repr__(self):
        return 'Field(%s %r=%r)' % (self.tok, self.label, self.value)


class Record(object):
    """One object record.  cls is the class name exactly as the file stores it (e.g.
    'dice::meme::BfButtonNode'); None for a null pointer / by-name reference."""
    __slots__ = ('cls', 'name', 'fields', 'raw', 'note', 'off', 'size', 'ni', 'ci')

    def __init__(self, cls=None, name='', fields=None, raw=None, note=None):
        self.cls, self.name, self.fields, self.raw, self.note = cls, name, fields, raw, note
        self.off = self.size = None     # where it was read from (informational)
        self.ni = self.ci = None        # string indices it was read with (keeps duplicate-table files exact)

    @property
    def short(self):
        return self.cls.split('::')[-1] if self.cls else None

    @property
    def kind(self):
        if self.cls is None:
            return 'ref' if self.name else 'null'
        return 'opaque' if self.fields is None else 'object'

    def field(self, label):
        for f in self.fields or ():
            if f.label == label:
                return f
        raise KeyError(label)

    def __getitem__(self, label):
        return self.field(label).value

    def __setitem__(self, label, value):
        self.field(label).value = value

    def children(self):
        """Nested records in file order (O slots, LIST items and their terminators, tree nodes).  An O slot or
        LIST item holding None (the writer's shorthand for a null pointer) is yielded as a fresh null record."""
        for f in self.fields or ():
            if f.tok == 'O':
                yield f.value if f.value is not None else null()
            elif f.tok == 'LIST':
                for r in f.value:
                    yield r if r is not None else null()
                if f.end is not None:
                    yield f.end
            elif f.tok == 'TREE':
                for r in _tree_records(f.value):
                    yield r

    def __repr__(self):
        if self.kind == 'null':
            return 'Record(null)'
        if self.kind == 'ref':
            return 'Record(ref %r)' % self.name
        return 'Record(%s %r%s)' % (self.short, self.name, ' OPAQUE' if self.fields is None else '')


def engine_class_name(cname):
    """True for a class string the engine's lookup can resolve (BFV 0x008753F0): the full registered name
    (dice::meme::X) or the bare class name X - a second pass compares against each registered name from the offset
    after its last ':' (set by the class-info ctor, BFV 0x008754B9).  'other::X' resolves to nothing (null, skipped).
    (BFV's runtime-only dice::ref2::menu::Ref2System, never written into a file, is not accepted here.)"""
    return cname.startswith(NS) and '::' not in cname[len(NS):] or ':' not in cname


def null():
    return Record()


def ref(name):
    return Record(None, name)


def new(game, cls, name='', **values):
    """A fresh object of a schema class with default values (null records, 0, '') - except a field the engine
    constrains, which gets its required value (BfVerticalScrollExNode Version = 1)."""
    short = cls.split('::')[-1]
    sch = schema(game)[short]
    metas = sch.get('fields') or [{}] * len(sch['tokens'])
    fields = []
    for (tok, label), meta in zip(sch['tokens'], metas):
        v = {'O': None, 'LIST': [], 'f32': 0.0, 'i32': 0, 'u8': 0, 'bool': 0, 's8': '', 'str32': '',
             'wstr32': '', 'TREE': None, 'u16': 0, 'u32as16': 0, 'sidx': ''}[tok]
        if tok == 'O':
            v = null()
        if 'eq' in (meta.get('constraint') or {}):
            v = meta['constraint']['eq']
        fields.append(Field(tok, label, v))
    r = Record(NS + cls if '::' not in cls else cls, name, fields)
    for k, v in values.items():
        r[k.replace('_', ' ')] = v
    return r


def _tree_records(node):
    if node is None:
        return
    for f in node['fields']:
        if f.tok == 'O':
            yield f.value if f.value is not None else null()
    for k in ('child', 'next', 'prev'):
        for r in _tree_records(node.get(k)):
            yield r


class MemeFile(object):
    def __init__(self, game, root, strings=None, fmt='meme2'):
        self.game = game
        self.root = root                # a Record with cls set, name '' (the root has no header)
        self.strings = strings          # the table as read, including the magic (None for a new file)
        self.format = fmt               # 'meme2' or 'old'
        self.trailing = b''             # bytes after the root payload (never seen; kept for exactness)
        self.warnings = []              # (kind, where, text) plausibility notes; never fatal
        self.stats = collections.Counter()
        self.table_rule = None          # how the table as read was ordered (see _table_rule)

    def walk(self):
        """Every Record, depth first in file order (root first).  A Record shared by several slots (a DAG) is
        visited once per slot, as the writer writes it; a Record that is its own descendant (a cycle, which no file
        can hold) raises ValueError instead of looping forever."""
        yield self.root
        path = {id(self.root)}
        stack = [(self.root, iter(list(self.root.children())))]
        while stack:
            r, it = stack[-1]
            c = next(it, None)
            if c is None:
                stack.pop()
                path.discard(id(r))
                continue
            if id(c) in path:
                raise ValueError('cycle: %r is its own descendant (via %r); a menu file is a tree' % (c, r))
            yield c
            path.add(id(c))
            stack.append((c, iter(list(c.children()))))


# ------------------------------------------------------------------------------------------------ reader
class _Reader(object):
    def __init__(self, data, game, sch, plausible):
        self.D = data
        self.game = game
        self.S = sch
        self.plausible = plausible
        self.warn = []
        self.stats = collections.Counter()
        self.old = False
        self.strs = None

    # -- strings
    def s(self, i):
        if i == 0:
            return ''
        if i >= len(self.strs):
            raise ParseError('string index %d beyond the table (%d strings)' % (i, len(self.strs)))
        return self.strs[i]

    def inline_name(self, q, limit):
        if q >= limit:
            raise ParseError('old-format name overruns at 0x%X' % q)
        n = self.D[q]
        if q + 1 + n > limit:
            raise ParseError('old-format name overruns at 0x%X' % q)
        return self.D[q + 1:q + 1 + n].decode('latin1'), q + 1 + n

    # -- records
    def record(self, pos, limit):
        D = self.D
        if self.old:
            if pos + 4 > limit:
                raise ParseError('record header overruns at 0x%X' % pos)
            size = struct.unpack_from('<I', D, pos)[0]
            name, q = self.inline_name(pos + 4, limit)
            cname, q = self.inline_name(q, limit)
            hdr = q - pos
            ni = ci = None
        else:
            if pos + 8 > limit:
                raise ParseError('record header at 0x%X overruns 0x%X' % (pos, limit))
            size, ni, ci = struct.unpack_from('<IHH', D, pos)
            hdr = 8
            name, cname = self.s(ni), self.s(ci)
        if size < hdr or pos + size > limit:
            raise ParseError('record at 0x%X: size %d outside 8..%d' % (pos, size, limit - pos))
        r = Record(cname or None, name)
        r.off, r.size, r.ni, r.ci = pos, size, ni, ci
        self.stats['records'] += 1
        if not cname:
            self.stats['refs' if name else 'nulls'] += 1
            if size != hdr:
                # the engine resolves the pointer from the header and seeks to pos+size: these bytes are
                # never read (dead).  Kept verbatim.
                r.raw = D[pos + hdr:pos + size]
                self.stats['dead_bytes'] += size - hdr
                self.warn.append(('CLASSLESS_SIZE', pos, '%s record of size %d: %d bytes the engine skips'
                                  % ('reference' if name else 'null', size, size - hdr)))
            return r, pos + size
        short = cname.split('::')[-1]
        sch = self.S.get(short) if engine_class_name(cname) else None
        if sch is None:
            r.raw = D[pos + hdr:pos + size]
            r.note = 'class %s is not registered in %s' % (cname, self.game)
            self.stats['opaque'] += 1
            self.stats['opaque_bytes'] += size - hdr
            return r, pos + size
        try:
            fields, q = self.payload(short, sch['tokens'], pos + hdr, pos + size)
            if q != pos + size:
                raise ParseError('%s at 0x%X consumed %d of %d payload bytes' % (short, pos, q - pos - hdr, size - hdr))
        except ParseError as e:
            r.raw = D[pos + hdr:pos + size]
            r.note = str(e)
            self.stats['opaque'] += 1
            self.stats['opaque_bytes'] += size - hdr
            return r, pos + size
        r.fields = fields
        self.stats['objects'] += 1
        self.stats['cls:' + short] += 1
        return r, pos + size

    def payload(self, cls, toks, q, limit):
        D = self.D
        fields = []
        for tok, lbl in toks:
            at = q
            if tok == 'O':
                v, q = self.record(q, limit)
                if self.plausible:
                    self.check_type(cls, lbl, v)
            elif tok == 'LIST':
                v, end = [], None
                while True:
                    k, q = self.record(q, limit)
                    if k.cls is None and not k.name:
                        if k.raw:
                            end = k
                        break
                    v.append(k)
                fields.append(Field(tok, lbl, v, end))
                continue
            elif tok == 'TREE':
                node_toks = self.tree_tokens(cls)
                v, q = self.tree(cls, node_toks, q, limit, 0)
            elif tok in ('u8', 'bool'):
                self.need(q, 1, limit, cls, lbl)
                v = D[q]
                q += 1
            elif tok == 'u16':
                self.need(q, 2, limit, cls, lbl)
                v = struct.unpack_from('<H', D, q)[0]
                q += 2
            elif tok == 'sidx':
                self.need(q, 2, limit, cls, lbl)
                v = self.s(struct.unpack_from('<H', D, q)[0])
                q += 2
            elif tok == 'i32':
                self.need(q, 4, limit, cls, lbl)
                v = struct.unpack_from('<i', D, q)[0]
                q += 4
            elif tok == 'u32as16':
                self.need(q, 4, limit, cls, lbl)
                v = struct.unpack_from('<I', D, q)[0]
                q += 4
            elif tok == 'f32':
                self.need(q, 4, limit, cls, lbl)
                v = struct.unpack_from('<f', D, q)[0]
                if v != v:                      # keep NaN payload bits exact
                    v = F32Bits(D[q:q + 4])
                q += 4
            elif tok == 's8':
                self.need(q, 1, limit, cls, lbl)
                n = D[q]
                self.need(q + 1, n, limit, cls, lbl)
                v = D[q + 1:q + 1 + n].decode('latin1')
                q += 1 + n
            elif tok == 'str32':
                self.need(q, 4, limit, cls, lbl)
                n = struct.unpack_from('<I', D, q)[0]
                self.need(q + 4, n, limit, cls, lbl)
                v = D[q + 4:q + 4 + n].decode('latin1')
                q += 4 + n
            elif tok == 'wstr32':
                self.need(q, 4, limit, cls, lbl)
                n = struct.unpack_from('<I', D, q)[0]
                self.need(q + 4, 2 * n, limit, cls, lbl)
                v = D[q + 4:q + 4 + 2 * n].decode('utf-16-le', 'surrogatepass')
                q += 4 + 2 * n
            else:
                raise ParseError('unknown token %r in schema for %s' % (tok, cls))
            if self.plausible and tok not in ('O', 'TREE'):
                self.check_value(cls, lbl, tok, v, at)
            fields.append(Field(tok, lbl, v))
        return fields, q

    def need(self, q, n, limit, cls, lbl):
        if q + n > limit:
            raise ParseError('%s.%s: %d bytes at 0x%X overrun the record end 0x%X' % (cls, lbl, n, q, limit))

    def tree_tokens(self, cls):
        f = self.S[cls]['fields'][0]
        return f.get('node_tokens', [])

    def tree(self, cls, toks, q, limit, kind):
        """kind 0 = TREE0 (child, next, prev), 1 = TREE1 (child, next), 2 = TREE2 (child, prev)."""
        self.need(q, 1, limit, cls, 'tree flag')
        flag = self.D[q]
        q += 1
        if flag == 0:
            return None, q
        fields, q = self.payload(cls + '<node>', toks, q, limit)
        node = {'flag': flag, 'fields': fields}
        node['child'], q = self.tree(cls, toks, q, limit, 0)
        if kind in (0, 1):
            node['next'], q = self.tree(cls, toks, q, limit, 1)
        if kind in (0, 2):
            node['prev'], q = self.tree(cls, toks, q, limit, 2)
        return node, q

    # -- plausibility (warnings only)
    def check_value(self, cls, lbl, tok, v, at):
        bad = None
        if tok == 'bool' and v not in (0, 1):
            bad = 'bool = %d' % v
        elif tok == 'f32':
            if isinstance(v, F32Bits) or math.isinf(v) or (v != 0 and not (1e-6 <= abs(v) <= 1e7)):
                bad = 'float = %r' % v
        elif tok in ('s8', 'str32'):
            if any(ord(c) < 0x20 and c not in '\t\r\n' for c in v):
                bad = 'control char in %r' % v[:40]
        elif tok == 'wstr32':
            if any(0xD800 <= ord(c) <= 0xDFFF for c in v):
                bad = 'lone surrogate'
        self.stats['values'] += 1
        if bad:
            self.warn.append(('IMPLAUSIBLE', at, '%s.%s %s: %s' % (cls, lbl, tok, bad)))

    def check_type(self, cls, lbl, rec):
        if rec.kind != 'object':
            return
        f = None
        sch = self.S.get(cls.split('<')[0])
        if sch:
            for (t, l), meta in zip(sch['tokens'], sch.get('fields', [])):
                if l == lbl and t == 'O':
                    f = meta
                    break
        want = f.get('expects') if f else None
        if not want or want == 'Object':
            return
        c = rec.short
        seen = 0
        while c and seen < 40:
            if c == want:
                return
            c = (self.S.get(c) or {}).get('base')
            seen += 1
        self.warn.append(('TYPE', rec.off, '%s.%s expects %s, holds %s' % (cls, lbl, want, rec.short)))


class F32Bits(float):
    """A NaN float kept with its exact 4 bytes."""
    def __new__(cls, raw):
        o = float.__new__(cls, float('nan'))
        o.raw = bytes(raw)
        return o


def _deep():
    if sys.getrecursionlimit() < 100000:
        sys.setrecursionlimit(100000)     # sibling chains nest thousands of records deep


def parse(data, game, plausible=True):
    """bytes -> MemeFile.  Never raises for a damaged record: it is kept as an opaque Record.  Raises
    ParseError only when the file framing itself is broken (string table, root class)."""
    _deep()
    sch = schema(game)
    R = _Reader(bytes(data), game, sch, plausible)
    D = R.D
    if len(D) >= 13 and D[0] == 12 and D[1:13] == MAGIC.encode():
        strs, p = [], 0
        while True:
            if p >= len(D):
                raise ParseError('string table runs past EOF')
            n = D[p]
            if n == 0:
                break
            if p + 1 + n > len(D):
                raise ParseError('string table entry overruns EOF')
            strs.append(D[p + 1:p + 1 + n].decode('latin1'))
            p += 1 + n
        R.strs = strs
        body = p + 1
        if body + 2 > len(D):
            raise ParseError('no root class index')
        rci = struct.unpack_from('<H', D, body)[0]
        rcls = R.s(rci)
        if not rcls:
            raise ParseError('root class index 0')
        q = body + 2
        fmt = 'meme2'
    else:
        R.old = True
        R.strs = []
        rcls, q = R.inline_name(0, len(D))
        if not rcls.startswith('dice::'):
            raise ParseError('neither a MemeFile 2.0 nor an old-format file (root class %r)' % rcls[:40])
        rci = None
        fmt = 'old'
    root = Record(rcls, '')
    root.off, root.ci = q, rci
    short = rcls.split('::')[-1]
    mf = MemeFile(game, root, R.strs if not R.old else None, fmt)
    R.stats['records'] += 1
    if short not in sch or not engine_class_name(rcls):
        root.raw, root.note = D[q:], 'root class %s is not registered in %s' % (rcls, game)
        R.stats['opaque'] += 1
        R.stats['opaque_bytes'] += len(D) - q
    else:
        try:
            fields, end = R.payload(short, sch[short]['tokens'], q, len(D))
            root.fields = fields
            mf.trailing = D[end:]
            R.stats['objects'] += 1
            R.stats['cls:' + short] += 1
            if mf.trailing:
                R.warn.append(('TRAILING', end, '%d bytes after the root payload' % len(mf.trailing)))
        except ParseError as e:
            root.raw, root.note = D[q:], str(e)
            R.stats['opaque'] += 1
            R.stats['opaque_bytes'] += len(D) - q
    root.size = len(D) - q
    mf.warnings = R.warn
    mf.stats = R.stats
    mf.table_rule = _table_rule(mf) if fmt == 'meme2' else None
    return mf


# ------------------------------------------------------------------------------------------------ writer
def first_use(mf):
    """Strings in the order a depth-first write first uses them: the root class, then per record header
    its object name and then its class name (pre-order, file order).  Opaque payloads are not looked into."""
    seen, order = set(), []

    def use(s):
        if s and s not in seen:
            seen.add(s)
            order.append(s)

    def walk_fields(fields):
        for f in fields or ():
            if f.tok == 'O':
                rec(f.value)
            elif f.tok == 'LIST':
                for r in f.value:
                    rec(r)
                if f.end is not None:
                    rec(f.end)
            elif f.tok == 'TREE':
                tree(f.value)
            elif f.tok == 'sidx':
                use(f.value)

    def tree(n):
        if n is None:
            return
        walk_fields(n['fields'])
        for k in ('child', 'next', 'prev'):
            if k in n:
                tree(n[k])

    def rec(r):
        if r is None:                   # None in an O slot = a null pointer
            return
        use(r.name)
        use(r.cls)
        walk_fields(r.fields)

    use(mf.root.cls)
    walk_fields(mf.root.fields)
    return order


def _has_opaque(mf):
    return any(r.kind == 'opaque' or (r.raw and r.kind in ('null', 'ref')) for r in mf.walk())


def _table_rule(mf):
    if mf.strings is None:
        return None
    if _has_opaque(mf):
        return 'unknown (opaque payloads)'
    fu = first_use(mf)
    t = mf.strings[1:]
    if t == fu:
        return 'first-use'
    if t[:len(fu)] == fu:
        return 'first-use + %d unused at the end' % (len(t) - len(fu))
    if set(fu) <= set(t):
        return 'other order' + (' (+%d unused)' % (len(t) - len(fu)) if len(t) > len(fu) else '')
    return 'invalid'


_CONS = {}


def _constraints(game):
    """{class short name: {field label: required value}} from the schema's engine-enforced constraints."""
    if game in _CONS:
        return _CONS[game]
    out = _CONS[game] = {}
    if not game or game not in schema():
        return out
    for cls, sch in schema(game).items():
        for (tok, label), meta in zip(sch['tokens'], sch.get('fields') or ()):
            eq = (meta.get('constraint') or {}).get('eq')
            if eq is not None:
                out.setdefault(cls, {})[label] = eq
    return out


def _cstring(s, what):
    """Header names, s8 handles and str32 / wstr32 values reach the engine as C strings (strcpy / NUL-terminated
    buffers): an embedded NUL would silently cut them short in game, so it is refused.  No corpus file has one."""
    if '\0' in s:
        raise ValueError('%s contains a NUL character; the engine would cut it off there: %r' % (what, s[:40]))


class _Writer(object):
    def __init__(self, table, old=False, game=None):
        self.old = old
        self.game = game
        self.table = list(table)
        self.index = {}
        self.cons = _constraints(game)
        self.sch = schema(game) if game in schema() else None
        for i, s in enumerate(self.table):
            if i and s not in self.index:
                self.index[s] = i

    def sid(self, s, hint=None):
        if not s:
            return 0
        if hint is not None and 0 < hint < len(self.table) and self.table[hint] == s:
            return hint
        i = self.index.get(s)
        if i is None:
            i = self.index[s] = len(self.table)
            self.table.append(s)
        return i

    def name(self, s, hint):
        if self.old:
            b = (s or '').encode('latin1')
            if len(b) > 255:
                raise ValueError('name longer than 255 bytes: %r' % s[:40])
            return bytes([len(b)]) + b
        return struct.pack('<H', self.sid(s, hint))

    def check(self, r):
        """Refuse a Record the engine would read differently from the model (see the per-rule comments)."""
        # the record reader strcpy's the names from the table into stack buffers of 0x80 / 0x40 bytes (BFV
        # 0x00874F6D/0x00874F8B via ClassIStream 0x0087A290, BF1942 0x007ED54D/0x007ED56B; the root class also
        # into 0x40, BFV 0x00874C4C); retail maxima 67 / 42
        if len((r.name or '').encode('latin1')) >= 0x80:
            raise ValueError('object name of %d bytes; the engine reads at most 127: %r' % (len(r.name), r.name[:40]))
        if len((r.cls or '').encode('latin1')) >= 0x40:
            raise ValueError('class name of %d bytes; the engine reads at most 63: %r' % (len(r.cls), r.cls[:40]))
        _cstring(r.name or '', 'object name')
        _cstring(r.cls or '', 'class name')
        if not r.cls and r.fields is not None:
            # it would go out as a null / by-name reference followed by bytes the engine skips
            raise ValueError('record %r has fields but no class' % (r.name,))
        if r.cls and r.fields is None and r.raw is None:
            # a Record(cls) built by hand: the header alone would promise a payload that is not there
            raise ValueError('record %s %r has neither fields nor a raw payload; build it with meme.new()'
                             % (r.cls, r.name))
        if r.cls and r.fields is not None and self.sch is not None:
            # a decoded object must be a class of this game, spelled so the engine resolves it (engine_class_name),
            # with exactly its schema layout: a class the exe does not register is read as a null pointer and
            # skipped (e.g. a BFV-only node pasted into a BF1942 file), and a hand-edited field list would be read
            # back as other values
            s = self.sch.get(r.short)
            if s is None or not engine_class_name(r.cls):
                raise ValueError('class %r is not a %s menu class; the engine would read a null pointer and skip it'
                                 % (r.cls, self.game))
            toks = s['tokens']
            if len(r.fields) != len(toks) or any(f.tok != t or f.label != l for f, (t, l) in zip(r.fields, toks)):
                raise ValueError('%s %r: its fields %s do not match the %s layout %s'
                                 % (r.short, r.name, [(f.tok, f.label) for f in r.fields][:8], self.game, toks[:8]))
        cons = self.cons.get(r.short) if r.cls and r.fields else None
        if cons:
            for f in r.fields:
                if f.label in cons and f.value != cons[f.label]:
                    raise ValueError('%s.%s = %r; the engine fails the read unless it is %r'
                                     % (r.short, f.label, f.value, cons[f.label]))

    def record(self, r, out):
        self.check(r)
        at = len(out)
        out += b'\0\0\0\0'
        out += self.name(r.name, r.ni)
        out += self.name(r.cls or '', r.ci)
        if r.fields is not None:
            self.fields(r.fields, out)
        elif r.raw:
            out += r.raw
        struct.pack_into('<I', out, at, len(out) - at)

    def fields(self, fields, out):
        for f in fields:
            t, v = f.tok, f.value
            if t == 'O':
                self.record(v if v is not None else null(), out)
            elif t == 'LIST':
                for r in v:
                    if r is None or r.kind == 'null':
                        # the reader (engine 0x0087A6F0 and meme.py) ends the list at the first null record, so a
                        # null item would cut the list short and shift every field after it
                        raise ValueError('%s: a null record cannot be a LIST item (it would end the list)' % f.label)
                    self.record(r, out)
                self.record(f.end if f.end is not None else null(), out)
            elif t in ('u8', 'bool'):
                if not isinstance(v, int) or not 0 <= v <= 255:      # int(1.7) would silently write 1
                    raise ValueError('%s: %s value %r is not an integer 0..255' % (f.label, t, v))
                out.append(int(v))
            elif t == 'u16':
                out += struct.pack('<H', v)
            elif t == 'sidx':
                out += struct.pack('<H', self.sid(v))
            elif t == 'i32':
                # signed; 2**31..2**32-1 is accepted as the same 32 bits (reads back as the negative value)
                if not isinstance(v, int) or not -0x80000000 <= v <= 0xFFFFFFFF:
                    raise ValueError('%s: i32 value %r is not an integer that fits in 32 bits' % (f.label, v))
                out += struct.pack('<i', v) if v < 0x80000000 else struct.pack('<I', v)
            elif t == 'u32as16':
                out += struct.pack('<I', v)
            elif t == 'f32':
                try:
                    out += v.raw if isinstance(v, F32Bits) else struct.pack('<f', v)
                except (OverflowError, struct.error) as e:
                    raise ValueError('%s: f32 value %r: %s' % (f.label, v, e))
            elif t == 's8':
                _cstring(v, '%s (s8)' % f.label)
                b = v.encode('latin1')
                if len(b) > 255:
                    raise ValueError('s8 string longer than 255 bytes: %r' % v[:40])
                out.append(len(b))
                out += b
            elif t == 'str32':
                _cstring(v, '%s (str32)' % f.label)
                b = v.encode('latin1')
                out += struct.pack('<I', len(b)) + b
            elif t == 'wstr32':
                _cstring(v, '%s (wstr32)' % f.label)
                b = v.encode('utf-16-le', 'surrogatepass')
                out += struct.pack('<I', len(b) // 2) + b
            elif t == 'TREE':
                self.tree(v, 0, out)
            else:
                raise ValueError('unknown token %r' % t)

    def tree(self, n, kind, out):
        if n is None:
            out.append(0)
            return
        # the grammar has no 'prev' under a next-node and no 'next' under a prev-node: refuse, never drop
        if kind == 1 and n.get('prev') is not None:
            raise ValueError('a TREE next-node cannot hold a prev node (the grammar has no slot for it)')
        if kind == 2 and n.get('next') is not None:
            raise ValueError('a TREE prev-node cannot hold a next node (the grammar has no slot for it)')
        out.append(n.get('flag') or 1)     # the flag byte says 'a node follows': 0 would end the tree here
        self.fields(n['fields'], out)
        self.tree(n.get('child'), 0, out)
        if kind in (0, 1):
            self.tree(n.get('next'), 1, out)
        if kind in (0, 2):
            self.tree(n.get('prev'), 2, out)


def serialize(mf, table='auto'):
    """MemeFile -> bytes.  Record sizes are always recomputed from the content.

    table='keep'    the table as read (order, unused entries) + any new strings appended at the end.
                    This is also what DICE's MDT MenuEditor 1.30 does ("unused names are left").
    table='rebuild' a fresh table in first-use order - the rule every retail file follows, so an unchanged
                    retail file comes back byte-exact.  Refused when the tree holds opaque payloads
                    (their string indices cannot be remapped).
    table='auto'    'keep' when every string the tree needs is already in the table (so an unchanged file
                    is byte-exact whatever its order); otherwise 'rebuild' if the file was first-use ordered
                    and fully decoded, else 'keep'.  A table over 511 strings is rebuilt if possible."""
    _deep()
    if table not in ('auto', 'keep', 'rebuild'):
        raise ValueError("table must be 'auto', 'keep' or 'rebuild', not %r" % (table,))
    opaque = _has_opaque(mf)                # walks the whole tree first: a cycle raises here, before any write
    if not mf.root.cls:
        raise ValueError('the root has no class')
    if mf.format == 'old':
        w = _Writer([], old=True, game=mf.game)
        w.check(mf.root)
        out = bytearray(w.name(mf.root.cls, None))
        if mf.root.fields is not None:
            w.fields(mf.root.fields, out)
        else:
            out += mf.root.raw or b''
        return bytes(out + mf.trailing)
    fu = first_use(mf)
    orig = mf.strings
    mode = table
    if mode == 'auto':
        was_first_use = str(mf.table_rule).startswith('first-use')    # as read, before any edit
        if orig and set(fu) <= set(orig[1:]):
            mode = 'keep'                   # unchanged strings: byte-exact whatever the order was
        elif orig and (opaque or not was_first_use):
            mode = 'keep'                   # a tool-written order: keep it, append (what MenuEditor does)
        else:
            mode = 'rebuild'                # new file, or a DICE-ordered one: write it the way DICE does
    if mode == 'rebuild' and opaque:
        raise ValueError('cannot rebuild the string table: the tree has opaque payloads')
    base = [MAGIC] + (fu if mode == 'rebuild' else [])
    if mode == 'keep':
        base = list(orig) if orig else [MAGIC]
    w = _Writer(base, game=mf.game)
    if mode == 'rebuild':
        w.sid = _no_hint(w.sid)
    w.check(mf.root)
    body = bytearray()
    body += w.name(mf.root.cls, mf.root.ci if mode == 'keep' else None)
    if mf.root.fields is not None:
        w.fields(mf.root.fields, body)
    else:
        body += mf.root.raw or b''
    body += mf.trailing
    if len(w.table) - 1 > MAX_STRINGS:
        if mode == 'keep' and not opaque and table == 'auto':
            return serialize(mf, 'rebuild')
        raise ValueError('%d strings after the magic; the engine holds at most %d' % (len(w.table) - 1, MAX_STRINGS))
    tab = bytearray()
    for s in w.table:
        b = s.encode('latin1')
        if not 0 < len(b) < 256:
            raise ValueError('string table entry of %d bytes: %r' % (len(b), s[:40]))
        tab.append(len(b))
        tab += b
    tab.append(0)
    return bytes(tab + body)


def _no_hint(sid):
    def f(s, hint=None):
        return sid(s, None)
    return f


def to_old_format(mf):
    """A copy of mf that serializes in the engine's old (inline-name) format - for tests only.  Refused when the
    tree holds opaque payloads: their nested headers carry u16 table indices, which the old format has no table for."""
    if mf.format != 'old' and _has_opaque(mf):
        raise ValueError('cannot convert to the old format: the tree has opaque payloads')
    o = MemeFile(mf.game, mf.root, None, 'old')
    o.trailing = mf.trailing
    return o


# ------------------------------------------------------------------------------------------------ lint
LINT_MAX_DEPTH = 1000


def lint(mf, max_depth=LINT_MAX_DEPTH):
    """Editor-level checks of a file the writer can write but the engine may read differently from what an editor
    shows.  Returns [(kind, record, text)]; nothing here is refused by serialize().
      DUPLICATE_DIFFERS  a named record whose content differs from the FIRST record of that name in file order: the
                         engine registers names globally, uses the first definition and skips every later payload
                         (findObject then seek, BFV 0x00874FBA / BF1942 0x007ED59A), so the edit is lost in game.
                         Edit all copies together.  (Retail: 0; community: 967.)
      REF_BEFORE_DEF     a by-name reference to a name this file only defines LATER: at that point findObject
                         fails and the engine tries loadFile(name) instead (BFV 0x0087508A).
      PATH_ALIAS         two names that differ only in / versus \\: the registry splits a name into folder and
                         leaf at its last / or \\ (BFV registerObject 0x00869020), so A/B and A\\B are one object in
                         game (how the folder part compares was not traced: deeper paths may be over-reported).
      BOOL_NOT_01        a bool byte other than 0/1: the engine reads any non-zero byte as true (BFV 0x00878E00).
      DEPTH              records nested deeper than max_depth: the engine reads nesting recursively (roughly 0x180
                         bytes of its 1 MB main stack per level, inferred) and retail never nests deeper than 278;
                         a long Next-node chain nests one level per node.
    """
    import hashlib
    out = []
    memo = {}

    def sig(r):
        if r is None:
            return b'null'
        k = id(r)
        if k in memo:
            return memo[k][0]
        h = hashlib.sha1()
        h.update(repr((r.cls, r.name, r.kind)).encode('latin1', 'replace'))
        if r.fields is None:
            h.update(r.raw or b'')
        else:
            for f in r.fields:
                h.update(f.tok.encode())
                if f.tok == 'O':
                    h.update(sig(f.value))
                elif f.tok == 'LIST':
                    for x in f.value:
                        h.update(sig(x))
                    h.update(b'|' + sig(f.end))
                elif f.tok == 'TREE':
                    for x in _tree_records(f.value):
                        h.update(sig(x))
                elif f.tok == 'f32':
                    h.update(f.value.raw if isinstance(f.value, F32Bits) else struct.pack('<f', f.value))
                else:
                    h.update(repr(f.value).encode('utf-8', 'surrogatepass'))
        d = h.digest()
        memo[k] = (d, r)                # keep r alive so its id is not reused
        return d

    order = list(mf.walk())             # raises on a cycle
    first, defined_at, alias = {}, {}, {}
    for i, r in enumerate(order):
        if r.kind in ('object', 'opaque') and r.name and r is not mf.root:
            defined_at.setdefault(r.name, i)
    for i, r in enumerate(order):
        if r.kind in ('object', 'opaque') and r.name and r is not mf.root:
            if r.name in first:
                f0 = first[r.name]
                if f0 is not r and (f0.cls != r.cls or sig(f0) != sig(r)):
                    out.append(('DUPLICATE_DIFFERS', r, '%r: this %s differs from the first definition (%s); the '
                                'engine uses the first' % (r.name, r.short, f0.short)))
            else:
                first[r.name] = r
            a = r.name.replace('\\', '/')
            if a in alias and alias[a] != r.name:
                out.append(('PATH_ALIAS', r, '%r and %r are the same registry path in game' % (alias[a], r.name)))
            alias.setdefault(a, r.name)
        elif r.kind == 'ref' and defined_at.get(r.name, -1) > i:
            out.append(('REF_BEFORE_DEF', r, 'reference to %r before its definition in this file: the engine looks '
                        'for a menu file of that name instead' % r.name))
        for f in r.fields or ():
            if f.tok == 'bool' and f.value not in (0, 1):
                out.append(('BOOL_NOT_01', r, '%s.%s = %r (the engine reads it as 1)' % (r.short, f.label, f.value)))
    # nesting depth (the root is level 0)
    stack = [(mf.root, 0)]
    deepest = (0, mf.root)
    while stack:
        r, d = stack.pop()
        if d > deepest[0]:
            deepest = (d, r)
        stack.extend((c, d + 1) for c in r.children())
    if deepest[0] > max_depth:
        out.append(('DEPTH', deepest[1], 'records nest %d levels deep (retail at most 278; limit here %d)'
                    % (deepest[0], max_depth)))
    return out


# ------------------------------------------------------------------------------------------------ dump
def dump(mf, out=sys.stdout, values=True):
    def line(depth, text):
        out.write('  ' * depth + text + '\n')

    def rec(r, depth, label):
        k = r.kind
        if k == 'null':
            line(depth, '%s: null' % label)
            return
        if k == 'ref':
            line(depth, '%s: -> %r' % (label, r.name))
            return
        head = '%s: %s%s' % (label, r.short, (' "%s"' % r.name) if r.name else '')
        if k == 'opaque':
            line(depth, head + '  [OPAQUE %d bytes: %s]' % (len(r.raw or b''), r.note))
            return
        line(depth, head)
        fields(r.fields, depth + 1)

    def fields(fs, depth):
        for f in fs:
            if f.tok == 'O':
                rec(f.value, depth, f.label)
            elif f.tok == 'LIST':
                line(depth, '%s: LIST[%d]' % (f.label, len(f.value)))
                for i, r in enumerate(f.value):
                    rec(r, depth + 1, '[%d]' % i)
            elif f.tok == 'TREE':
                line(depth, '%s: TREE %s' % (f.label, 'empty' if f.value is None else ''))
            elif values:
                line(depth, '%s = %r  (%s)' % (f.label, f.value, f.tok))

    rec(mf.root, 0, 'root')


# ------------------------------------------------------------------------------------------------ corpus
SCRATCH = os.path.normpath(os.path.join(HERE, '..', '..'))
# The extracted corpus (read-only copies of the menu.rfa entries; see README.md): MEME_CORPUS holds BF1942/ and BFV/
# (retail, laid out <mod>/menu/<file>), MEME_COMMUNITY holds BF1942c/ and BFVc/ (<mod>-<archive>/menu/<file>).
CORPUS = os.environ.get('MEME_CORPUS') or os.path.join(SCRATCH, 'corpus')
COMMUNITY = os.environ.get('MEME_COMMUNITY') or os.path.join(HERE, 'community')
MDT_TEMPLATES = 'D:/Games/EA GAMES/Battlefield Mod Development Toolkit/Menu Editor/templates'


def corpus_groups():
    """(group, game, root folder).  Retail comes from the shared corpus/.  The community groups come from
    ./community (extract_community.py: every menu*.rfa of every non-retail mod of both main installs, complete),
    because the shared corpus/BF1942c extraction was still running; every file both hold is byte-identical
    (xcheck_extract.py).  Without ./community the shared corpus/ folders are used."""
    g = [('BF1942', 'BF1942', os.path.join(CORPUS, 'BF1942')),
         ('BFV', 'BFV', os.path.join(CORPUS, 'BFV'))]
    for group, game in (('BF1942c', 'BF1942'), ('BFVc', 'BFV')):
        mine = os.path.join(COMMUNITY, group)
        g.append((group, game, mine if os.path.isdir(mine) else os.path.join(CORPUS, group)))
    g.append(('MDT', 'BF1942', MDT_TEMPLATES))
    return g


def iter_files(root, group):
    for dp, dn, fn in os.walk(root):
        dn.sort()
        if group == 'BFVc' and os.path.basename(dp) == '_retail_check':
            dn[:] = []
            continue
        if group == 'BFVc':
            dn[:] = [d for d in dn if d != '_retail_check']
        for f in sorted(fn):
            yield os.path.join(dp, f)


def is_meme(path):
    try:
        with open(path, 'rb') as fh:
            h = fh.read(13)
    except OSError:
        return False
    return len(h) == 13 and h[0] == 12 and h[1:13] == MAGIC.encode()


def check_file(data, game):
    """Parse + re-serialize one file; returns a dict of facts."""
    res = {}
    try:
        mf = parse(data, game)
    except ParseError as e:
        return {'fatal': str(e)}
    st = mf.stats
    res['records'] = st['records']
    res['objects'] = st['objects']
    res['nulls'] = st['nulls']
    res['refs'] = st['refs']
    res['opaque'] = st['opaque']
    res['opaque_bytes'] = st['opaque_bytes']
    res['dead_bytes'] = st['dead_bytes']
    res['decoded_or_dead'] = st['opaque'] == 0 and not mf.trailing
    res['full'] = res['decoded_or_dead'] and st['dead_bytes'] == 0
    res['warnings'] = mf.warnings
    res['rule'] = mf.table_rule
    res['nstrings'] = len(mf.strings) - 1 if mf.strings else 0
    out = serialize(mf, 'auto')
    res['exact_auto'] = out == data
    out_keep = serialize(mf, 'keep')
    res['exact_keep'] = out_keep == data
    if res['full']:
        rb = serialize(mf, 'rebuild')
        res['exact_rebuild'] = rb == data
        # the rebuilt file must parse back to the same tree
        m2 = parse(rb, game)
        res['rebuild_reparses'] = serialize(m2, 'rebuild') == rb and m2.stats['opaque'] == 0
        old = serialize(to_old_format(mf))
        mo = parse(old, game)
        was_old = mo.format == 'old'
        mo.format, mo.strings = 'meme2', None
        res['old_format_ok'] = was_old and serialize(mo, 'rebuild') == rb
    res['classes'] = collections.Counter({k[4:]: v for k, v in st.items() if k.startswith('cls:')})
    return res, mf


def run_corpus(json_out=None, verbose=True):
    sys.setrecursionlimit(100000)
    groups = corpus_groups()
    report = collections.OrderedDict()
    counts = {'BF1942': collections.defaultdict(dict), 'BFV': collections.defaultdict(dict)}
    for group, game, root in groups:
        c = collections.Counter()
        fails, warns, rules = [], collections.Counter(), collections.Counter()
        cls_counts = collections.Counter()
        warn_examples = []
        if not os.path.isdir(root):
            report[group] = {'root': root, 'missing': True}
            continue
        for p in iter_files(root, group):
            c['files seen'] += 1
            if not is_meme(p):
                c['not meme (skipped)'] += 1
                continue
            data = open(p, 'rb').read()
            c['meme files'] += 1
            r = check_file(data, game)
            if isinstance(r, dict):
                c['fatal'] += 1
                fails.append((os.path.relpath(p, root), r['fatal']))
                continue
            r, mf = r
            c['parsed'] += 1
            c['records'] += r['records']
            c['objects decoded'] += r['objects']
            c['null records'] += r['nulls']
            c['reference records'] += r['refs']
            c['opaque records'] += r['opaque']
            c['opaque bytes'] += r['opaque_bytes']
            c['bytes'] += len(data)
            c['full coverage'] += r['full']
            c['every byte decoded or engine-dead'] += r['decoded_or_dead']
            c['dead bytes (skipped by the engine)'] += r['dead_bytes']
            c['byte-exact (auto)'] += r['exact_auto']
            c['byte-exact (keep table)'] += r['exact_keep']
            if r['full']:
                c['byte-exact (rebuilt first-use table)'] += r.get('exact_rebuild', False)
                c['rebuilt file re-parses identically'] += r.get('rebuild_reparses', False)
                c['old-format conversion ok'] += r.get('old_format_ok', False)
            rules[r['rule']] += 1
            cls_counts.update(r['classes'])
            for w in r['warnings']:
                warns[w[0]] += 1
                if len(warn_examples) < 12:
                    warn_examples.append((os.path.relpath(p, root), w))
            if not r['full'] or not r['exact_auto']:
                fails.append((os.path.relpath(p, root),
                              [(x.short, x.note) for x in mf.walk() if x.kind == 'opaque'][:3]
                              + [w for w in mf.warnings if w[0] == 'CLASSLESS_SIZE'][:3]
                              + ([] if r['exact_auto'] else ['NOT BYTE-EXACT'])))
            for k, v in r['classes'].items():
                d = counts[game][k].setdefault(group, {'instances': 0, 'exact': 0, 'files': 0})
                d['instances'] += v
                d['exact'] += v
                d['files'] += 1
            for x in mf.walk():
                if x.kind == 'opaque' and x.short in schema(game):
                    d = counts[game][x.short].setdefault(group, {'instances': 0, 'exact': 0, 'files': 0})
                    d['instances'] += 1
                    d.setdefault('opaque', []).append('%s: %s' % (os.path.relpath(p, root), x.note))
        report[group] = {'root': root, 'game': game, 'counts': dict(c), 'table_rules': dict(rules),
                         'warnings': dict(warns), 'warning_examples': warn_examples, 'failures': fails,
                         'classes_seen': len(cls_counts)}
        if verbose:
            print('== %-8s (%s) %s' % (group, game, root))
            for k in ('files seen', 'not meme (skipped)', 'meme files', 'parsed', 'fatal', 'full coverage',
                      'every byte decoded or engine-dead', 'dead bytes (skipped by the engine)',
                      'byte-exact (auto)', 'byte-exact (keep table)', 'byte-exact (rebuilt first-use table)',
                      'rebuilt file re-parses identically', 'old-format conversion ok', 'records',
                      'objects decoded', 'null records', 'reference records', 'opaque records', 'opaque bytes'):
                print('   %-40s %d' % (k, c[k]))
            print('   classes seen: %d' % len(cls_counts))
            print('   string-table rule:', dict(rules))
            print('   warnings:', dict(warns))
            for f in fails[:10]:
                print('   !!', f)
    if json_out:
        json.dump({'report': report, 'counts': counts}, open(json_out, 'w'), indent=1, default=str)
        json.dump(counts, open(os.path.join(HERE, 'corpus_counts.json'), 'w'), indent=1)
    return report, counts


def main(argv):
    if not argv or argv[0] in ('-h', '--help'):
        print(__doc__)
        return 0
    cmd = argv[0]
    if cmd == 'corpus':
        out = None
        if '--json' in argv:
            out = argv[argv.index('--json') + 1]
        run_corpus(out or os.path.join(HERE, 'corpus_results.json'))
        return 0
    if cmd == 'lint':
        sys.setrecursionlimit(100000)
        mf = parse(open(argv[1], 'rb').read(), argv[2])
        issues = lint(mf)
        for kind, r, text in issues:
            print('%-18s %s' % (kind, text))
        print('%d lint findings' % len(issues))
        return 0
    if cmd in ('dump', 'check'):
        sys.setrecursionlimit(100000)
        data = open(argv[1], 'rb').read()
        mf = parse(data, argv[2])
        if cmd == 'dump':
            dump(mf)
        out = serialize(mf)
        print('records %d, opaque %d, warnings %d, table rule %s, byte-exact %s'
              % (mf.stats['records'], mf.stats['opaque'], len(mf.warnings), mf.table_rule, out == data))
        for w in mf.warnings[:20]:
            print('  warning', w)
        return 0
    print('unknown command', cmd)
    return 2


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
