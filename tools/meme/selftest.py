"""Tests of meme.py beyond the corpus round-trip:
  A. negative controls - a deliberately wrong layout must make instances opaque (the runner is sensitive)
  B. edits - value change, rename to a new string, new record inserted, subtree deleted, table overflow
  C. Tree / NavigateTree grammar - synthetic file (no corpus file has one), parsed and rewritten exactly
  D. old format - a converted file parses back to the same tree (also run per file by the corpus runner)
  E. writer guards - models the writer used to write wrongly are now written right or refused (ValueError)
  F. lint - a copy of a shared named object edited alone is reported; editing every copy is clean
"""
import copy, os, struct, sys, collections
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import meme

sys.setrecursionlimit(100000)
FAIL = []


def check(cond, what):
    print('  %-4s %s' % ('ok' if cond else 'FAIL', what))
    if not cond:
        FAIL.append(what)


def retail_files(group):
    g = [x for x in meme.corpus_groups() if x[0] == group][0]
    return [(p, g[1]) for p in meme.iter_files(g[2], group) if meme.is_meme(p)]


def _tree_same(a, b):
    if a is None or b is None:
        return a is None and b is None
    ra, rb = list(meme._tree_records(a)), list(meme._tree_records(b))
    shape = lambda n: None if n is None else tuple(shape(n.get(k)) for k in ('child', 'next', 'prev'))
    return len(ra) == len(rb) and shape(a) == shape(b) and all(same_tree(x, y) for x, y in zip(ra, rb))


def same_tree(a, b):
    """Structural equality of two Records (class, name, field tokens/labels/values); None == a null record."""
    a = meme.null() if a is None else a
    b = meme.null() if b is None else b
    if a.kind != b.kind or a.cls != b.cls or a.name != b.name:
        return False
    if a.kind == 'opaque':
        return a.raw == b.raw
    if a.fields is None:
        return (a.raw or b'') == (b.raw or b'')
    if len(a.fields) != len(b.fields):
        return False
    for x, y in zip(a.fields, b.fields):
        if (x.tok, x.label) != (y.tok, y.label):
            return False
        if x.tok == 'O':
            if not same_tree(x.value, y.value):
                return False
        elif x.tok == 'LIST':
            if len(x.value) != len(y.value) or not all(same_tree(p, q) for p, q in zip(x.value, y.value)):
                return False
            if (x.end is None) != (y.end is None) or (x.end is not None and not same_tree(x.end, y.end)):
                return False
        elif x.tok == 'TREE':
            if not _tree_same(x.value, y.value):
                return False
        elif x.value != y.value and not (x.value != x.value and y.value != y.value):
            return False
    return True


def negative_controls():
    print('A. negative controls (retail BF1942 + BFV, 186 files)')
    files = retail_files('BF1942') + retail_files('BFV')
    S = meme.schema()
    tests = [
        ('TransformNode without its last token', lambda s: s['TransformNode']['tokens'].pop()),
        ('ColorEffect Alpha read as bool', lambda s: s['ColorEffect']['tokens'].__setitem__(-1, ['bool', 'Alpha'])),
        ('PointerXData = [O Data] (the MDT header)', lambda s: s['PointerXData']['tokens'].append(['O', 'Data'])),
        ('BfButtonNode Width/Height swapped for one s8', lambda s: s['BfButtonNode']['tokens'].__setitem__(4, ['s8', 'Width'])),
        ('IntData value as u8', lambda s: s['IntData']['tokens'].__setitem__(0, ['u8', 'Value'])),
    ]
    for name, mut in tests:
        saved = copy.deepcopy(S)
        for g in ('BF1942', 'BFV'):
            mut(S[g])
        bad_files = opaque = 0
        for p, game in files:
            mf = meme.parse(open(p, 'rb').read(), game)
            if mf.stats['opaque']:
                bad_files += 1
                opaque += mf.stats['opaque']
        S.clear()
        S.update(saved)
        check(bad_files > 0, '%s -> %d of %d files get opaque records (%d records)' % (name, bad_files, len(files), opaque))


def edits():
    print('B. edits')
    p = os.path.join(meme.CORPUS, 'BFV', 'BfVietnam', 'menu', 'InGame')
    data = open(p, 'rb').read()
    mf = meme.parse(data, 'BFV')
    # B1 change one float of the first TransformNode
    tn = next(r for r in mf.walk() if r.short == 'TransformNode')
    old = tn['X']
    tn['X'] = old + 12.5
    out = meme.serialize(mf)
    check(len(out) == len(data) and sum(a != b for a, b in zip(out, data)) <= 4,
          'float edit: same length, <= 4 bytes differ (%d)' % sum(a != b for a, b in zip(out, data)))
    m2 = meme.parse(out, 'BFV')
    tn2 = next(r for r in m2.walk() if r.short == 'TransformNode')
    check(tn2['X'] == old + 12.5 and m2.stats['opaque'] == 0, 'float edit re-parses with the new value')
    tn['X'] = old
    check(meme.serialize(mf) == data, 'float edit undone -> byte-exact again')
    # B2 rename a named object to a new string -> auto rebuilds a first-use table (file was first-use)
    named = next(r for r in mf.walk() if r.kind == 'object' and r.name and r is not mf.root)
    oldname = named.name
    named.name = 'RDK/TestVariable'
    out = meme.serialize(mf)
    m2 = meme.parse(out, 'BFV')
    check(m2.table_rule == 'first-use', 'rename to a new string: auto writes a first-use table (%s)' % m2.table_rule)
    check(same_tree(mf.root, m2.root), 'rename: tree re-parses identically')
    out_keep = meme.serialize(mf, 'keep')
    mk = meme.parse(out_keep, 'BFV')
    check(mk.strings[-1] == 'RDK/TestVariable' and mk.strings[:-1] == mf.strings and same_tree(mf.root, mk.root),
          'rename with table=keep: new string appended at the end, old table untouched')
    named.name = oldname
    # B3 insert a new TransformNode wrapping nothing, as the sibling of the first TransformNode's child chain
    newt = meme.new('BFV', 'TransformNode', X=10.0, Y=20.0, Width=100.0, Height=50.0)
    newt['Next node'] = tn['Next node']
    tn['Next node'] = newt
    out = meme.serialize(mf)
    m2 = meme.parse(out, 'BFV')
    check(len(out) == len(data) + 8 + 16 + 8 and m2.stats['opaque'] == 0 and same_tree(mf.root, m2.root),
          'insert TransformNode (header 8 + 4 floats 16 + null child 8): +%d bytes, sizes recomputed, re-parses identically'
          % (len(out) - len(data)))
    tn['Next node'] = newt['Next node']
    check(meme.serialize(mf) == data, 'insert undone -> byte-exact again')
    # B4 delete a subtree: auto keeps the table (unused strings stay), rebuild drops them
    victim = next(f for r in mf.walk() for f in (r.fields or ()) if f.tok == 'O' and f.value.kind == 'object'
                  and f.value.short == 'CullNode')
    saved = victim.value
    victim.value = meme.null()
    a = meme.parse(meme.serialize(mf), 'BFV')
    b = meme.parse(meme.serialize(mf, 'rebuild'), 'BFV')
    check(a.strings == mf.strings and b.table_rule == 'first-use' and len(b.strings) <= len(a.strings),
          'delete subtree: auto keeps the table (%d), rebuild compacts it (%d)' % (len(a.strings), len(b.strings)))
    victim.value = saved
    # B5 table overflow
    mf2 = meme.parse(data, 'BFV')
    recs = [r for r in mf2.walk() if r.kind == 'object' and r is not mf2.root]
    for i, r in enumerate(recs[:600]):
        r.name = 'X%d' % i
    try:
        meme.serialize(mf2)
        check(False, 'table over 511 strings must be refused')
    except ValueError as e:
        check('511' in str(e), 'table over 511 strings refused: %s' % e)
    # B6 a brand-new file
    root = meme.new('BF1942', 'NameNode')
    t = meme.new('BF1942', 'TransformNode', X=390.0, Y=290.0, Width=20.0, Height=20.0)
    pic = meme.new('BF1942', 'PictureNode')
    root['Next node'] = meme.null()
    t['Transformed node'] = pic
    root.fields[-1].value = t if root.fields[-1].tok == 'O' else root.fields[-1].value
    nf = meme.MemeFile('BF1942', root)
    out = meme.serialize(nf)
    back = meme.parse(out, 'BF1942')
    check(back.table_rule == 'first-use' and back.stats['opaque'] == 0 and same_tree(root, back.root),
          'new file from scratch: first-use table, parses back (%d bytes)' % len(out))


def tree_grammar():
    print('C. Tree / NavigateTree (synthetic; no corpus file has one)')
    for game in ('BF1942', 'BFV'):
        strs = ['MemeFile 2.0', 'dice::meme::NameNode', 'dice::meme::NavigateAction', 'dice::meme::NavigateTree']
        null = struct.pack('<IHH', 8, 0, 0)
        node = null + null                       # NavigateTreeObject: Description, Action
        # root node with a child (no grandchildren), a next sibling, no prev
        tree = b'\1' + node + (b'\1' + node + b'\0' + b'\0' + b'\0') + (b'\1' + node + b'\0' + b'\0') + b'\0'
        navtree = struct.pack('<IHH', 8 + len(tree), 0, 3) + tree
        navact = struct.pack('<IHH', 8 + len(navtree), 0, 2) + navtree
        nn = meme.schema(game)['NameNode']['tokens']
        # NameNode = [Next node][Name?..] - build its payload from the schema: first O = the action holder
        body = b''
        for tok, lbl in nn:
            body += navact if tok == 'O' and not body else (null if tok == 'O' else b'')
        # NavigateAction's schema layout decides how its payload looks; only run when it is [O Tree]
        na = meme.schema(game)['NavigateAction']['tokens']
        if [t for t, l in na] != ['O']:
            print('  skip %s: NavigateAction is %s' % (game, na))
            continue
        D = b''.join(bytes([len(s)]) + s.encode() for s in strs) + b'\0' + struct.pack('<H', 1) + body
        mf = meme.parse(D, game)
        t = mf.root.fields[0].value.fields[0].value.fields[0].value
        ok = mf.stats['opaque'] == 0 and meme.serialize(mf) == D and t is not None and t['child'] is not None \
            and t['next'] is not None and t['prev'] is None
        check(ok, '%s NavigateTree: node + child + next sibling parsed and rewritten byte-exact' % game)


def old_format():
    print('D. old format')
    p = os.path.join(meme.CORPUS, 'BF1942', 'bf1942', 'menu', 'InGame')
    data = open(p, 'rb').read()
    mf = meme.parse(data, 'BF1942')
    old = meme.serialize(meme.to_old_format(mf))
    mo = meme.parse(old, 'BF1942')
    check(mo.format == 'old' and mo.stats['opaque'] == 0 and same_tree(mf.root, mo.root),
          'BF1942 InGame converted to the old format (%d -> %d bytes) parses to the same tree' % (len(data), len(old)))


def writer_guards():
    """E. models the writer used to write wrongly (silently, or by crashing / hanging) - found by the writer
    verification (work/verify-writer/vw_edge.py); each must now be written correctly or refused with ValueError."""
    print('E. writer guards')
    p = os.path.join(meme.CORPUS, 'BFV', 'BfVietnam', 'menu', 'InGame')
    data = open(p, 'rb').read()

    def fresh():
        return meme.parse(data, 'BFV')

    def refused(mf, why):
        try:
            meme.serialize(mf)
        except ValueError as e:
            check(True, '%s -> refused (%s)' % (why, str(e)[:70]))
            return
        check(False, '%s -> must be refused' % why)

    mf = fresh()
    tn = next(r for r in mf.walk() if r.short == 'TransformNode')
    tn['Transformed node'] = None
    m2 = meme.parse(meme.serialize(mf), 'BFV')
    check(same_tree(mf.root, m2.root) and m2.stats['opaque'] == 0, 'O slot set to None is written as a null pointer')
    mf = fresh()
    lf = next(f for r in mf.walk() if r.kind == 'object' for f in r.fields if f.tok == 'LIST' and len(f.value) >= 2)
    lf.value.insert(1, meme.null())
    refused(mf, 'null record inside a LIST (would end the list early)')
    mf = fresh()
    next(f for r in mf.walk() if r.kind == 'object' for f in r.fields if f.tok == 'u8').value = 256
    refused(mf, 'u8 = 256 (used to be written as 0)')
    mf = fresh()
    tn = next(r for r in mf.walk() if r.short == 'TransformNode')
    tn['Next node'] = tn
    refused(mf, 'a node that is its own Next node (used to hang)')
    mf = fresh()
    mf.root.cls = 'dice::meme::' + 'N' * 60
    refused(mf, 'root class name of 72 bytes (engine buffer 0x40)')
    mf = fresh()
    next(f for r in mf.walk() if r.kind == 'object' for f in r.fields if f.tok == 's8').value = 'menu/a\0b.tga'
    refused(mf, 'NUL inside an s8 handle (the engine would read menu/a)')
    mf = fresh()
    tn = next(r for r in mf.walk() if r.short == 'TransformNode')
    tn['Transformed node'] = meme.Record('dice::meme::PictureNode', 'x')
    refused(mf, 'Record(cls) without fields (used to be written as a header-only PictureNode)')
    mf = fresh()
    x = meme.new('BFV', 'PictureNode', 'x')
    x.cls = None
    next(r for r in mf.walk() if r.short == 'TransformNode')['Transformed node'] = x
    refused(mf, 'fields without a class (used to become a reference + dead bytes)')
    m42 = meme.parse(open(os.path.join(meme.CORPUS, 'BF1942', 'bf1942', 'menu', 'InGame'), 'rb').read(), 'BF1942')
    next(r for r in m42.walk() if r.short == 'TransformNode')['Transformed node'] = meme.new('BFV', 'BfPictureButtonNode')
    refused(m42, 'BFV-only class pasted into a BF1942 file (the engine would skip it)')
    mf = fresh()
    tn = next(r for r in mf.walk() if r.short == 'TransformNode')
    tn.fields[1] = meme.Field('s8', 'X', 'abc')
    refused(mf, 'field list that differs from the schema layout (used to be read back as other values)')
    # class strings: a bare class name resolves in the engine (second lookup pass), 'x::PictureNode' does not
    root = meme.new('BFV', 'NameNode')
    pic = meme.new('BFV', 'PictureNode', Picture='menu/a.tga')
    pic.cls = 'PictureNode'
    root['Next node'] = pic
    out = meme.serialize(meme.MemeFile('BFV', root))
    back = meme.parse(out, 'BFV')
    check(back.stats['opaque'] == 0 and back.root['Next node'].short == 'PictureNode', 'bare class name written and decoded')
    strs = [('x::PictureNode' if s == 'PictureNode' else s) for s in back.strings]
    body = out[sum(1 + len(s) for s in back.strings) + 1:]
    odd = b''.join(bytes([len(s)]) + s.encode('latin1') for s in strs) + b'\0' + body
    mo = meme.parse(odd, 'BFV')
    check(mo.root['Next node'].kind == 'opaque' and meme.serialize(mo) == odd,
          "'x::PictureNode' (unresolvable in the engine) is read as opaque and written back byte-exact")
    mf = fresh()
    v = meme.new('BFV', 'BfVerticalScrollExNode')
    check(v['Version <do not edit>'] == 1, 'new() gives BfVerticalScrollExNode Version 1 (the engine fails any other)')
    v['Version <do not edit>'] = 0
    next(r for r in mf.walk() if r.short == 'TransformNode')['Transformed node'] = v
    refused(mf, 'BfVerticalScrollExNode Version 0')
    for game in ('BF1942', 'BFV'):
        x = meme.new(game, 'NavigateTree')
        x.fields[0].value = {'flag': 0, 'fields': [meme.Field('O', 'Description', meme.null()),
                                                    meme.Field('O', 'Action', meme.null())]}
        nav = meme.new(game, 'NavigateAction')
        nav['Action tree'] = x
        b = meme.new(game, 'BfButtonNode')
        b['Action'] = nav
        root = meme.new(game, 'NameNode')
        root['Next node'] = b
        nf = meme.MemeFile(game, root)
        back = meme.parse(meme.serialize(nf), game)
        t = back.root['Next node']['Action']['Action tree'].fields[0].value
        check(back.stats['opaque'] == 0 and t is not None, '%s tree node with flag 0 is written as a node (flag 1)' % game)
        node = lambda **kw: dict({'flag': 1, 'fields': [meme.Field('O', 'Description', meme.null()),
                                                         meme.Field('O', 'Action', meme.null())]}, **kw)
        x.fields[0].value = node(next=node(prev=node()))
        try:
            meme.serialize(nf)
            check(False, '%s tree: a prev node under a next-node must be refused (it used to be dropped)' % game)
        except ValueError:
            check(True, '%s tree: a prev node under a next-node is refused (it used to be dropped silently)' % game)
    tp = [x for x in meme.corpus_groups() if x[0] == 'BF1942c'][0][2]
    testi = [os.path.join(dp, 'testi') for dp, dn, fn in os.walk(tp) if 'testi' in fn and 'FW1918' in dp]
    if testi:
        mo = meme.parse(open(testi[0], 'rb').read(), 'BF1942')
        try:
            meme.to_old_format(mo)
            check(False, 'old-format conversion of a file with an opaque record must be refused')
        except ValueError:
            check(True, 'old-format conversion of a file with an opaque record is refused')


def lint_checks():
    print('F. lint')
    p = os.path.join(meme.CORPUS, 'BFV', 'BfVietnam', 'menu', 'InGame')
    mf = meme.parse(open(p, 'rb').read(), 'BFV')
    check(meme.lint(mf) == [], 'retail InGame: no findings')
    copies = collections.defaultdict(list)
    for r in mf.walk():
        if r.kind == 'object' and r.name and r.short == 'IntData':
            copies[r.name].append(r)
    name, rs = next((k, v) for k, v in copies.items() if len(v) >= 2)
    rs[1]['Value <do not edit>'] = rs[0]['Value <do not edit>'] + 7
    kinds = [k for k, r, t in meme.lint(mf)]
    check(kinds == ['DUPLICATE_DIFFERS'], 'second copy of %r edited alone -> DUPLICATE_DIFFERS (%s)' % (name, kinds))
    for r in rs:
        r['Value <do not edit>'] = rs[0]['Value <do not edit>']
    check(meme.lint(mf) == [], 'all copies edited together -> clean')


if __name__ == '__main__':
    negative_controls()
    edits()
    tree_grammar()
    old_format()
    writer_guards()
    lint_checks()
    print('SELFTEST', 'FAILED: %s' % FAIL if FAIL else 'PASSED')
