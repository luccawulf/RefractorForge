"""Battlefield Vietnam (Refractor) OVERGROWTH placement, reimplemented from BfVietnam.exe.

Read-only. Reads a level folder (Growth/overGrowth.wst, Growth/OverGrowthMap.raw,
Heightmap.raw, Init/Terrain.con, StaticObjects.con), the shared overgrowth geometry list
(standardMesh/overGrowthGeometries.wst), the .sm meshes (bbox + LOD0 counts) and the object
templates (objects/**/Objects.con), then reproduces the game's instance list.

Every step cites the code it mirrors (addresses are BfVietnam.exe VAs, ImageBase 0x400000).
See docs/BFV_Overgrowth_RE.md (and docs/BFV_Growth_Placement.md) for the full write-up.

Usage (defaults are Operation_Irving and the extracted archives):
  python overgrowth_place.py [--level DIR] [--meshes DIR] [--objects DIR]
                             [--cx 805 --cz 502 --radius 120] [--json out.json]
                             [--fpu single|double] [--no-import]
"""
import argparse
import json
import math
import os
import re
import struct
import sys

import numpy as np

ROOT = r"C:\Users\lucas\Downloads\BF1942 Server Recorder"
DEF_LEVEL = os.path.join(ROOT, r"bfv-levels\BfVietnam\levels\Operation_Irving")
DEF_MESHES = os.path.join(ROOT, r"extracted-archives-bfv\standardMesh")
DEF_OBJECTS = os.path.join(ROOT, r"extracted-archives-bfv\objects")

MATERIAL_NAMES = ["default", "water", "dryGrass", "juicyGrass", "dryDirt", "wetDirt", "mud",
                  "deathMaterial", "gravel", "muddyWater", "drySand", "wetSand", "rock",
                  "sandRoad", "dirtRoad", "pavelRoad", "dummy"]          # GrowthManager ctor 0x7600e0
CRD = {"crdnone": 0, "crduniform": 1, "crdexponential": 2, "crdnormal": 3}  # table 0xd0d558

# ---------------------------------------------------------------- float emulation
# x87 results are stored to float32 at every fstp. With --fpu single (D3D's default FPU
# control word, 24-bit mantissa) every intermediate is also float32, which numpy float32
# arithmetic reproduces. --fpu double rounds only at the stores.
FPU_SINGLE = True
F = np.float32


def fop(x):
    """round a store to float32"""
    return F(x)


# ---------------------------------------------------------------- RNG (Random::next 0x4e0990)
M31 = 0x7FFFFFFF


class Rng:
    """Lehmer generator, modulus 2^31-1. 0x4e0990 does two Schrage/Carta steps with
    multipliers 0x5e30 (24112) and 0x661f (26143); combined multiplier 630360016."""

    def __init__(self, seed):
        self.s = seed

    def next(self):
        self.s = (self.s * 24112) % M31
        self.s = (self.s * 26143) % M31
        return self.s

    def k(self):
        """((r | 0x80) >> 7) + 1 : the 24-bit integer every caller turns into (0,1] by *2^-24"""
        return ((self.next() | 0x80) >> 7) + 1


TWO_M24 = F(5.9604645e-08)   # 0xb5d414 = 2^-24


def sample_dist(dist, rng):
    """RandomDistribution::sample 0x5eff00 -> Random::sampleDistribution 0x5efea0"""
    typ, a, b, sym = dist
    sign = F(1.0)
    if sym:                                   # 4th field: random sign, one extra draw first
        u = F(rng.k()) * TWO_M24
        sign = F(1.0) if u > 0.5 else F(-1.0)
    if typ == 0:                              # CRDNone: constant a
        v = F(a)
    elif typ == 1:                            # CRDUniform: Random::uniform 0x4e0a40
        k = F(rng.k())
        if FPU_SINGLE:
            v = (F(F(b) - F(a)) * k) * TWO_M24 + F(a)
        else:
            v = F((float(F(b)) - float(F(a))) * float(k) * float(TWO_M24) + float(F(a)))
    else:
        raise NotImplementedError("CRDExponential/CRDNormal not used by any retail BFV level")
    return F(v * sign)


# ---------------------------------------------------------------- file parsing
def read_text(p):
    return open(p, "rb").read().decode("latin-1")


def parse_dist(s, default):
    if s is None:
        return default
    p = s.strip().split("/")
    typ = CRD.get(p[0].strip().lower(), 0)
    a = float(p[1]) if len(p) > 1 and p[1].strip() else default[1]
    b = float(p[2]) if len(p) > 2 and p[2].strip() else default[2]
    sym = len(p) > 3 and p[3].strip().lower() in ("true", "1")
    return (typ, a, b, sym)


TAG_RE = re.compile(r"<\s*(/?)\s*([A-Za-z0-9_\-\.]+)((?:\s+[A-Za-z0-9_]+\s*=\s*\"[^\"]*\")*)\s*(/?)\s*>", re.S)
ATTR_RE = re.compile(r"([A-Za-z0-9_]+)\s*=\s*\"([^\"]*)\"")


def parse_wst(path):
    """tolerant WRAPPER_TREE reader (element names may start with digits, so no XML lib)"""
    txt = read_text(path).lstrip()            # overGrowth.wst has a stray space before <?xml
    txt = re.sub(r"<\?.*?\?>", "", txt, flags=re.S)
    root = {"name": "#root", "attrs": {}, "kids": []}
    stack = [root]
    for m in TAG_RE.finditer(txt):
        close, name, attrs, selfclose = m.group(1), m.group(2), m.group(3), m.group(4)
        if close:
            if len(stack) > 1:
                stack.pop()
            continue
        node = {"name": name, "attrs": dict(ATTR_RE.findall(attrs or "")), "kids": []}
        stack[-1]["kids"].append(node)
        if not selfclose:
            stack.append(node)
    return root


def find_node(node, name):
    for k in node["kids"]:
        if k["name"].lower() == name.lower():
            return k
        r = find_node(k, name)
        if r:
            return r
    return None


def parse_terrain(level):
    t = read_text(os.path.join(level, "Init", "Terrain.con"))
    def g(key, d):
        m = re.search(r"GeometryTemplate\." + key + r"\s+([-\d\.e]+)", t, re.I)
        return float(m.group(1)) if m else d
    return {"worldSize": g("worldSize", 2048), "yScale": g("yScale", 1.0),
            "materialSize": int(g("materialSize", 512))}


def sm_info(path):
    """.sm header: u32 version, u32, bbox min/max (6 f32), [v>=10: u8], collision section,
    u32 lodCount, LOD0: u32 matCount, mat0: str name, 12 bytes, u32, u32 fmt, u32 stride,
    u32 nVerts, u32 nIdx (StandardMeshTemplate vf38 0x708200 / vf40 0x70ba90 / vf41 0x709140)"""
    b = open(path, "rb").read()
    ver = struct.unpack_from("<I", b, 0)[0]
    bb = struct.unpack_from("<6f", b, 8)
    info = {"bbox": bb, "nverts": None, "nidx": None}
    o = 0x20 + (1 if ver >= 10 else 0)
    ncoll = struct.unpack_from("<I", b, o)[0]
    if ncoll == 0:
        o += 4
        lod_off = o
    else:                                     # skip the collision meshes by locating LOD0's header
        lod_off = None
        for m in re.finditer(rb"[\x20-\x7e]{4,80}", b):
            s, name = m.start(), m.group()
            if b"material" not in name.lower() or s < 12:
                continue
            if struct.unpack_from("<I", b, s - 4)[0] != len(name):
                continue
            nmat, nlod = struct.unpack_from("<I", b, s - 8)[0], struct.unpack_from("<I", b, s - 12)[0]
            if 1 <= nmat <= 32 and 1 <= nlod <= 10:
                lod_off = s - 12
                break
    if lod_off is not None:
        o = lod_off + 8
        nl = struct.unpack_from("<I", b, o)[0]
        o += 4 + nl + 12 + 4
        fmt, stride, nv, ni = struct.unpack_from("<4I", b, o)
        info.update(nverts=nv, nidx=ni, stride=stride)
    return info


def corner_radius(bb, scale=(1.0, 1.0, 1.0)):
    """Object_006852d0: sqrt(max over the 8 bbox corners of |corner|^2), about the mesh origin.
    StandardMesh setScale (0x748860) scales min/max per axis before recomputing it."""
    mn = [bb[i] * scale[i] for i in range(3)]
    mx = [bb[3 + i] * scale[i] for i in range(3)]
    best = 0.0
    for c in range(8):
        x = mx[0] if c & 1 else mn[0]
        y = mx[1] if c & 2 else mn[1]
        z = mx[2] if c & 4 else mn[2]
        best = max(best, float(F(F(x) * F(x) + F(y) * F(y) + F(z) * F(z))))
    return F(math.sqrt(best))


class MeshDB:
    def __init__(self, d):
        self.d = d
        self.files = {f.lower(): os.path.join(d, f) for f in os.listdir(d)}
        self.cache = {}

    def get(self, name):
        key = name.lower()
        if key not in self.cache:
            p = self.files.get(key + ".sm")
            self.cache[key] = sm_info(p) if p else None
        return self.cache[key]


def object_geometries(objdir):
    """ObjectTemplate.create <type> <name> ... ObjectTemplate.geometry <geom>  (lowercased key)"""
    out = {}
    for dp, dn, fn in os.walk(objdir):
        for f in fn:
            if f.lower() != "objects.con":
                continue
            cur = None
            for line in read_text(os.path.join(dp, f)).splitlines():
                p = line.strip().split()
                if len(p) >= 3 and p[0].lower() == "objecttemplate.create":
                    cur = p[2]
                elif len(p) >= 2 and p[0].lower() == "objecttemplate.geometry" and cur:
                    out.setdefault(cur.lower(), p[1])
    return out


def static_objects(level):
    objs, cur = [], None
    for line in read_text(os.path.join(level, "StaticObjects.con")).splitlines():
        p = line.strip().split()
        if not p:
            continue
        k = p[0].lower()
        if k == "object.create" and len(p) > 1:
            cur = {"template": p[1], "pos": None, "rot": (0.0, 0.0, 0.0), "scale": (1.0, 1.0, 1.0)}
            objs.append(cur)
        elif cur and k == "object.absoluteposition":
            cur["pos"] = tuple(float(v) for v in p[1].split("/"))
        elif cur and k == "object.rotation":
            cur["rot"] = tuple(float(v) for v in p[1].split("/"))
        elif cur and k == "object.geometry.scale":
            cur["scale"] = tuple(float(v) for v in p[1].split("/"))
    return objs


# ---------------------------------------------------------------- terrain
class Terrain:
    """PatchTerrain::vf23 0x722c10 (height + face normal), HeightMap::getSample 0x745930"""

    def __init__(self, level, ter):
        side = ter["materialSize"]
        raw = np.fromfile(os.path.join(level, "Heightmap.raw"), dtype="<u2")
        self.side = int(round(math.sqrt(raw.size)))
        self.h = raw.reshape(self.side, self.side)       # [z][x]
        self.cell = ter["worldSize"] / self.side           # +0x320
        self.inv = 1.0 / self.cell                         # +0x324
        self.sc = ter["yScale"] / 256.0                    # +0x31c = yScale * 0.00390625
        self.mask = self.side - 1

    def raw(self, x, z):
        return float(self.h[z & self.mask, x & self.mask])

    def height_normal(self, x, z):
        px, pz = x * self.inv, z * self.inv
        ix, iz = int(px), int(pz)                          # _ftol2 truncation
        fx, fz = px - ix, pz - iz
        H = self.raw
        if (ix + iz) & 1:                                  # anti-diagonal (ix+1,iz)-(ix,iz+1)
            if 1.0 - fx > fz:
                b = H(ix, iz); ax = H(ix + 1, iz) - b; az = H(ix, iz + 1) - b
                h = b + fx * ax + fz * az
            else:
                b = H(ix + 1, iz + 1); dx = H(ix, iz + 1) - b; dz = H(ix + 1, iz) - b
                h = b + (1 - fx) * dx + (1 - fz) * dz
                ax, az = -dx, -dz
        else:                                              # main diagonal (ix,iz)-(ix+1,iz+1)
            if fx <= fz:
                b = H(ix, iz + 1); dz = H(ix, iz) - b; dx = H(ix + 1, iz + 1) - b
                h = b + (1 - fz) * dz + fx * dx
                ax, az = dx, -dz
            else:
                b = H(ix + 1, iz); dx = H(ix, iz) - b; dz = H(ix + 1, iz + 1) - b
                h = b + (1 - fx) * dx + fz * dz
                ax, az = -dx, dz
        # plane slope in metres per metre -> upward normal (-dh/dx, 1, -dh/dz)
        gx, gz = ax * self.sc / self.cell, az * self.sc / self.cell
        n = (-gx, 1.0, -gz)
        ln = math.sqrt(n[0] ** 2 + 1.0 + n[2] ** 2)
        return h * self.sc, (n[0] / ln, n[1] / ln, n[2] / ln)


# ---------------------------------------------------------------- the manager
class OverGrowth:
    def __init__(self, level, meshes, objdir, do_import=True):
        self.level = level
        self.meshes = MeshDB(meshes)
        ter = parse_terrain(level)
        self.terrain = Terrain(level, ter)
        wst = parse_wst(os.path.join(level, "Growth", "overGrowth.wst"))
        og = find_node(wst, "overGrowth")
        a = {k.lower(): v for k, v in og["attrs"].items()}
        # defaults: OverGrowthManager ctor 0x71e850 / GrowthManager ctor 0x7600e0
        self.N = 10                                          # +0x10 patches per side (ring)
        self.view = float(a.get("viewdistance", 500.0))      # +0x24
        self.side = int(a.get("materialmapsidesize", 512))   # +0x84
        self.density = min(1.0, max(0.0, float(a.get("densityscale", 1.0))))  # +0x28, clamp 0x75e1a0
        self.do_import = do_import and a.get("importsceneobjects", "false").strip().lower() in ("true", "1")
        self.world = int(ter["worldSize"])                   # +0x60/+0x64 = PatchTerrain +0x314
        # GrowthManager::vf3 0x75e110 + OverGrowthManager::vf3 0x71dc90
        ps = F(self.view / (self.N // 2 - 1))
        wsz = F(min(self.world, self.world))
        ps = F(wsz / F(math.floor(float(F(wsz / ps)))))
        self.ps = ps
        inv = 1.0 / float(ps)
        self.W = int(self.world * inv + 0.5)                 # +0xb04
        self.H = int(self.world * inv + 0.5)                 # +0xb08
        self.mscale = F(self.side) / F(self.world)          # +0x88 (GrowthManager::vf1 0x760760)
        mm = np.fromfile(os.path.join(level, "Growth", "OverGrowthMap.raw"), dtype=np.uint8)
        self.mat = (mm.reshape(self.side, self.side) & 0x0F)  # CompressedMap, 4 bits/cell, [z][x]
        # materials / types in .wst order
        self.materials = [[] for _ in MATERIAL_NAMES]
        mats = find_node(og, "materials")
        for mi, mnode in enumerate(mats["kids"] if mats else []):
            idx = next((i for i, n in enumerate(MATERIAL_NAMES) if n.lower() == mnode["name"].lower()), mi)
            types = find_node(mnode, "types")
            for t in (types["kids"] if types else []):
                ta = {k.lower(): v for k, v in t["attrs"].items()}
                self.materials[idx].append({
                    "key": t["name"],
                    "geometryName": ta.get("geometryname", ""),
                    "probability": F(float(ta.get("probability", 1.0))),
                    "eq": F(float(ta.get("minradiusdisttoequals", 1.0))),
                    "oth": F(float(ta.get("minradiusdisttoothers", 1.0))),
                    "normalScale": F(float(ta.get("normalscale", 1.0))),
                    "scale": parse_dist(ta.get("scale"), (0, 1.0, 0.0, False)),
                    "rotation": parse_dist(ta.get("rotation"), (1, -180.0, 180.0, False)),
                    "idx": -1,
                })
        # per-geometry records (GrowthManager::sub_00760460), created in material/type order
        self.recs, self.rec_by_name = [], {}
        for types in self.materials:
            for t in types:
                t["idx"] = self.record(t["geometryName"])
        # the shared list of importable geometries (OverGrowthManager::vf1 0x71c450 loads it into
        # the 'dummy' material; vf7_helper_0071ef40 puts its names into a _stricmp set)
        gl = parse_wst(os.path.join(meshes, "overGrowthGeometries.wst"))
        self.importable = set()
        def walk(n):
            for k in n["kids"]:
                g = {kk.lower(): vv for kk, vv in k["attrs"].items()}.get("geometryname")
                if g:
                    self.importable.add(g.lower())
                walk(k)
        walk(gl)
        self.objdir = objdir

    def record(self, name):
        """sub_00760460: exact (case-sensitive) match against each record's m2 name or its
        '..._m1' twin; otherwise build one. -1 when the m2 geometry does not resolve."""
        if name in self.rec_by_name:
            return self.rec_by_name[name]
        m1 = name[:-1] + "1" if len(name) > 2 and name[-2].lower() == "m" else name
        info = self.meshes.get(name)
        info1 = self.meshes.get(m1)
        if info is None:                      # rec+0x1c == 0 and manager+0x90 == 0 -> -1
            return -1
        rec = {"name": name, "m1": m1, "R": corner_radius(info["bbox"]),
               "costA": info["nverts"], "costB": info["nidx"],
               "R1": corner_radius(info1["bbox"]) if info1 else None}
        i = len(self.recs)
        self.recs.append(rec)
        self.rec_by_name[name] = i
        self.rec_by_name.setdefault(m1, i)
        return i

    def material_at(self, x, z):
        """GrowthManager::vf8 0x75e1e0: ftol(x*side/world) % side (unsigned), map[z][x]"""
        xi = int(F(x) * self.mscale) % self.side
        zi = int(F(z) * self.mscale) % self.side
        return int(self.mat[zi, xi])

    # ------------------------------------------------------------ import (0x71ef40)
    def import_scene_objects(self, cells):
        geo = object_geometries(self.objdir)
        imported = []
        for o in static_objects(self.level):
            if o["pos"] is None:
                continue
            gname = geo.get(o["template"].lower(), o["template"])
            if gname.lower() not in self.importable:
                continue
            name2 = gname[:-1] + "2" if len(gname) > 2 and gname[-2].lower() == "m" else gname
            ridx = self.record(name2)
            m1 = self.meshes.get(gname)
            if m1 is None or ridx < 0:
                continue
            sx, sy, sz = o["scale"]
            R = corner_radius(m1["bbox"], (sx, sy, sz))      # geometry radius after setScale
            r = F(R * F(sy))                                   # +0x50/+0x54/+0x58 = R * scale.y
            x, y, z = o["pos"]
            ci = int(float(F(F(x) / self.ps))) % self.W
            cj = int(float(F(F(z) / self.ps))) % self.H
            inst = {"type": ridx, "x": F(x), "y": F(y), "z": F(z), "scale": F(sy), "r": r,
                    "rEq": r, "rOth": r, "generated": False, "template": o["template"],
                    "geometry": gname, "rot": o["rot"], "scale3": o["scale"]}
            cells[(ci, cj)]["inst"].append(inst)
            imported.append(inst)
        # second pass: charge budgets in vector order, drop what does not fit (0x71f306..)
        for j in range(self.H):
            for i in range(self.W):
                c = cells[(i, j)]
                keep = 0
                for inst in c["inst"]:
                    rec = self.recs[inst["type"]]
                    a = c["A"] - rec["costA"]
                    if a < 0:
                        break
                    b = c["B"] - rec["costB"]
                    if b < 0:
                        break
                    c["A"], c["B"] = a, b
                    keep += 1
                c["dropped"] = c["inst"][keep:]
                c["inst"] = c["inst"][:keep]
                c["nImported"] = keep
        return imported

    # ------------------------------------------------------------ one cell (GrowthManager::vf14 0x760950)
    def grow_cell(self, c, seed):
        rng = Rng(seed)                                       # manager+0x18
        budgetA, budgetB = c["A"], c["B"]
        n_iter = int(budgetA / 8) if budgetA >= 0 else -((-budgetA) // 8)
        it, cont = 0, True
        vec = c["inst"]
        ps = self.ps
        while it < n_iter and cont:
            # vf14_helper 0x75e7f0: x then z, U in (0,1]
            dx = F(F(rng.k()) * ps) * TWO_M24
            dz = F(F(rng.k()) * ps) * TWO_M24
            x = F(dx + c["x"])
            z = F(dz + c["z"])
            mat = self.material_at(x, z)
            u = F(rng.k()) * TWO_M24                          # third draw, always taken
            sel = None
            for t in self.materials[mat]:
                if u < t["probability"]:
                    if t["idx"] < 0:
                        continue                              # unresolved: next type, u unchanged
                    sel = t
                    break
                u = F(u - t["probability"])
            if sel is None:
                it += 1
                continue
            rec = self.recs[sel["idx"]]
            if rec["costA"] > budgetA or rec["costB"] > budgetB:
                cont = False                                  # 0x760c61: stop this cell
                it += 1
                continue
            s = sample_dist(sel["scale"], rng)                # scale draw
            R = rec["R"] if rec["R"] is not None else rec["R1"]
            cand = {"type": sel["idx"], "x": x, "z": z, "scale": s,
                    "r": F(R * s), "rEq": F(F(R * sel["eq"]) * s), "rOth": F(F(R * sel["oth"]) * s),
                    "generated": True, "geometry": sel["geometryName"], "material": mat,
                    "normalScale": sel["normalScale"]}
            # GrowthManager::vf13 0x75e750: yaw draw, then terrain normal tilt, translation
            cand["yaw"] = sample_dist(sel["rotation"], rng)
            ok = True
            for o in vec:                                     # 0x760b77: same-cell test only
                ddx = F(x - o["x"])
                ddz = F(z - o["z"])
                d2 = F(F(ddz * ddz) + F(ddx * ddx))
                rr = F(cand["rEq"] + o["rEq"]) if o["type"] == cand["type"] else F(cand["rOth"] + o["rOth"])
                if d2 < F(rr * rr):
                    ok = False
                    break
            if not ok:
                it += 1
                continue
            budgetA -= rec["costA"]
            budgetB -= rec["costB"]
            vec.append(cand)
            it += 2                                           # 0x760c37: a placement costs 2
        c["budgetLeft"] = (budgetA, budgetB)

    def run(self):
        cells = {}
        for j in range(self.H):                               # OverGrowthManager::vf7 0x71f4c0
            for i in range(self.W):
                cells[(i, j)] = {"i": i, "j": j, "x": F(F(i) * self.ps), "z": F(F(j) * self.ps),
                                 "A": int(F(self.density) * F(2048.0)), "B": 0x2000,
                                 "inst": [], "nImported": 0, "dropped": []}
        self.imported = self.import_scene_objects(cells) if self.do_import else []
        for j in range(self.H):                               # vf7_helper_0071cc40
            for i in range(self.W):
                seed = (23 + 4711 * j + 13 * i) & 0x7FFFFFFF
                self.grow_cell(cells[(i, j)], seed)
        # final transforms (vf13): height at (x,z), up = normalize(nx*ns, ny, nz*ns)
        for c in cells.values():
            for inst in c["inst"]:
                if not inst["generated"]:
                    continue
                h, n = self.terrain.height_normal(float(inst["x"]), float(inst["z"]))
                inst["y"] = F(h)
                ns = float(inst["normalScale"])
                up = (n[0] * ns, n[1], n[2] * ns)
                lu = math.sqrt(sum(v * v for v in up))
                inst["up"] = tuple(v / lu for v in up)
        self.cells = cells
        return cells


def rows(inst):
    """matrix rows the engine builds (BFSoldier_0065a400 rotY, QuadTreeCuller_006a0940)"""
    t = math.radians(float(inst["yaw"]))
    fwd = (math.sin(t), 0.0, math.cos(t))
    up = inst["up"]
    right = (up[1] * fwd[2] - up[2] * fwd[1], up[2] * fwd[0] - up[0] * fwd[2], up[0] * fwd[1] - up[1] * fwd[0])
    fwd2 = (right[1] * up[2] - right[2] * up[1], right[2] * up[0] - right[0] * up[2], right[0] * up[1] - right[1] * up[0])
    return right, up, fwd2


def main():
    global FPU_SINGLE
    ap = argparse.ArgumentParser()
    ap.add_argument("--level", default=DEF_LEVEL)
    ap.add_argument("--meshes", default=DEF_MESHES)
    ap.add_argument("--objects", default=DEF_OBJECTS)
    ap.add_argument("--cx", type=float, default=805.0)
    ap.add_argument("--cz", type=float, default=502.0)
    ap.add_argument("--radius", type=float, default=120.0)
    ap.add_argument("--json", default=None)
    ap.add_argument("--fpu", choices=("single", "double"), default="single")
    ap.add_argument("--no-import", action="store_true")
    args = ap.parse_args()
    FPU_SINGLE = args.fpu == "single"

    og = OverGrowth(args.level, args.meshes, args.objects, do_import=not args.no_import)
    cells = og.run()
    print(f"level={os.path.basename(os.path.normpath(args.level))} world={og.world} viewDistance={og.view} "
          f"patch/cell size={float(og.ps):.5f} m grid={og.W}x{og.H} densityScale={og.density} "
          f"importSceneObjects={og.do_import}")
    print("records (per-geometry, costA=LOD0 mat0 vertices, costB=indices, R=bbox corner radius):")
    for i, r in enumerate(og.recs):
        print(f"  #{i}: {r['name']:16s} R={float(r['R']):7.3f} costA={r['costA']} costB={r['costB']}")
    gen = [x for c in cells.values() for x in c["inst"] if x["generated"]]
    imp = [x for c in cells.values() for x in c["inst"] if not x["generated"]]
    dropped = [x for c in cells.values() for x in c["dropped"]]
    from collections import Counter
    print(f"\ngenerated instances: {len(gen)}")
    for k, v in sorted(Counter(x["geometry"] for x in gen).items(), key=lambda kv: -kv[1]):
        print(f"  {k:16s} {v}")
    print(f"imported static objects kept as overgrowth: {len(imp)} (left as plain objects: {len(dropped)})")
    for k, v in sorted(Counter(x["geometry"] for x in imp).items(), key=lambda kv: -kv[1]):
        print(f"  {k:16s} {v}")

    cx, cz, R = args.cx, args.cz, args.radius
    near = []
    for c in cells.values():
        for x in c["inst"] + [dict(d, dropped=True) for d in c["dropped"]]:
            d = math.hypot(float(x["x"]) - cx, float(x["z"]) - cz)
            if d <= R:
                near.append((d, c, x))
    near.sort(key=lambda t: t[0])
    print(f"\ninstances within {R:g} m of ({cx:g}, {cz:g}): {len(near)}")
    print("   dist  cell   kind      geometry          x         y        z      yaw    scale")
    for d, c, x in near:
        kind = "gen" if x["generated"] else ("static" if x.get("dropped") else "import")
        yaw = f"{float(x['yaw']):7.1f}" if x["generated"] else "   (obj)"
        print(f"  {d:6.1f}  {c['i']:2d},{c['j']:<2d} {kind:7s}  {x['geometry']:16s} {float(x['x']):8.2f} "
              f"{float(x['y']):8.2f} {float(x['z']):8.2f} {yaw}  {float(x['scale']):.3f}")

    if args.json:
        out = []
        for c in cells.values():
            for x in c["inst"]:
                e = {"cell": [c["i"], c["j"]], "generated": x["generated"], "geometry": x["geometry"],
                     "pos": [float(x["x"]), float(x["y"]), float(x["z"])], "scale": float(x["scale"])}
                if x["generated"]:
                    r0, r1, r2 = rows(x)
                    e.update(yaw=float(x["yaw"]), up=list(x["up"]), rows=[list(r0), list(r1), list(r2)])
                else:
                    e.update(template=x["template"], rotation=list(x["rot"]), scale3=list(x["scale3"]))
                out.append(e)
        with open(args.json, "w") as fh:
            json.dump({"cellSize": float(og.ps), "grid": [og.W, og.H], "instances": out}, fh)
        print(f"\nwrote {len(out)} instances to {args.json}")


if __name__ == "__main__":
    main()
