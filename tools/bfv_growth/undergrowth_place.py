"""Battlefield Vietnam (Refractor) UNDERGROWTH placement, reimplemented from BfVietnam.exe.

Read-only. Shares the generator with overgrowth (GrowthManager::vf14 0x760950, RNG, roulette,
budget, spacing; see overgrowth_place.py next to this file (docs/BFV_Undergrowth_RE.md for the write-up), whose helpers are imported), but
undergrowth is generated lazily per display patch around the camera:

  * patch size ps = 256 / floor(256 / (viewDistance/4))         UnderGrowthManager::vf3 0x71fda0
    (Irving underGrowth.wst viewdistance=60 -> 256/17 = 15.0588 m; default 70 -> 18.2857 m)
  * patch (px,pz) covers x in [px*ps,(px+1)*ps), z likewise; origin y = terrain height at its centre
  * seed = (pz*4711 + px*13 + 23) & 0x7fffffff                  GrowthManager::vf9 0x75f2f0
  * budget A = trunc(1024*densityScale) vertices, B = 4096 indices (the patch's
    RaStreamDataEditable(0x400,0x1000,0x1c) capacities), n_iter = A/8 = 128  GrowthManager::vf11 0x75e140
  * material from UnderGrowthMap.raw (1024^2 over 2048 m -> 2 m cells, row = z, column = x)
  * the game keeps a 10x10 ring of patch slots (indices cx-4..cx+5) and fills/draws a patch only
    when sqrt(min over its 4 corners of dx^2 + (0.2*dy)^2 + dz^2) < viewDistance;
    alpha = 1 up to (1-fadeRange)*viewDistance, then linear to 0 (fadeRange 0.3)

Usage (defaults: Operation_Irving, point = the US hangar):
  python undergrowth_place.py [--x 805 --z 502] [--y Y] [--mode game|radius] [--radius R]
                              [--json out.json] [--patches]
  --mode game   : emulate the game for a camera at (x, y, z) (y defaults to terrain + 1.7 m)
  --mode radius : every patch whose nearest corner (2D) is within --radius of (x, z)
"""
import argparse
import json
import math
import os
import sys
from collections import Counter

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import overgrowth_place as OG  # noqa: E402  (shared RNG, distributions, .wst/.sm readers, terrain)

F = OG.F
TWO_M24 = OG.TWO_M24


class UnderGrowth:
    def __init__(self, level, meshes):
        self.level = level
        self.meshes = OG.MeshDB(meshes)
        ter = OG.parse_terrain(level)
        self.terrain = OG.Terrain(level, ter)
        self.world = int(ter["worldSize"])
        wst = OG.parse_wst(os.path.join(level, "Growth", "underGrowth.wst"))
        ug = OG.find_node(wst, "underGrowth")
        a = {k.lower(): v for k, v in ug["attrs"].items()}   # property names are _stricmp-matched (0x783570)
        # defaults: GrowthManager ctor 0x7600e0 + UnderGrowthManager ctor 0x71fe20
        self.N = 10
        self.view = float(a.get("viewdistance", 70.0))
        self.side = int(a.get("materialmapsidesize", 512))
        self.density = min(1.0, max(0.0, float(a.get("densityscale", 1.0))))
        self.fadeRange = float(a.get("faderange", 0.3))
        self.ctgHeight = float(a.get("closetogroundfadeheight", 1.0))
        self.ctgAlpha = float(a.get("closetogroundfadealpha", 0.4))
        self.ctgDist = float(a.get("closetogroundfadedist", 12.0))
        # GrowthManager::vf3 0x75e110 then the 256 m snap of UnderGrowthManager::vf3 0x71fda0
        ps0 = F(self.view / (self.N // 2 - 1))
        self.ps = F(F(256.0) / F(math.floor(float(F(F(256.0) / ps0)))))
        self.mscale = F(self.side) / F(self.world)          # +0x88 (GrowthManager::vf1 0x760760)
        mm = np.fromfile(os.path.join(level, "Growth", "UnderGrowthMap.raw"), dtype=np.uint8)
        self.mat = mm.reshape(self.side, self.side) & 0x0F   # CompressedMap, 4 bits, [z][x]
        self.materials = [[] for _ in OG.MATERIAL_NAMES]
        mats = OG.find_node(ug, "materials")
        for mi, mnode in enumerate(mats["kids"] if mats else []):
            idx = next((i for i, n in enumerate(OG.MATERIAL_NAMES) if n.lower() == mnode["name"].lower()), mi)
            types = OG.find_node(mnode, "types")
            for t in (types["kids"] if types else []):
                ta = {k.lower(): v for k, v in t["attrs"].items()}
                self.materials[idx].append({
                    "geometryName": ta.get("geometryname", ""),
                    "probability": F(float(ta.get("probability", 1.0))),
                    "eq": F(float(ta.get("minradiusdisttoequals", 1.0))),
                    "oth": F(float(ta.get("minradiusdisttoothers", 1.0))),
                    "normalScale": F(float(ta.get("normalscale", 1.0))),
                    "scale": OG.parse_dist(ta.get("scale"), (0, 1.0, 0.0, False)),
                    "rotation": OG.parse_dist(ta.get("rotation"), (1, -180.0, 180.0, False)),
                    "idx": -1,
                })
        self.recs, self.rec_by_name = [], {}
        for types in self.materials:
            for t in types:
                t["idx"] = self.record(t["geometryName"])

    def record(self, name):
        """GrowthManager::sub_00760460 (case-sensitive names; '_m1' twin = name with last char '1')"""
        if name in self.rec_by_name:
            return self.rec_by_name[name]
        info = self.meshes.get(name)
        if info is None:
            return -1
        m1 = name[:-1] + "1" if len(name) > 2 and name[-2].lower() == "m" else name
        rec = {"name": name, "R": OG.corner_radius(info["bbox"]), "costA": info["nverts"], "costB": info["nidx"]}
        self.recs.append(rec)
        self.rec_by_name[name] = len(self.recs) - 1
        self.rec_by_name.setdefault(m1, len(self.recs) - 1)
        return len(self.recs) - 1

    def material_at(self, x, z):
        """GrowthManager::vf8 0x75e1e0 (unsigned div after _ftol2, like the exe)"""
        xi = (int(F(x) * self.mscale) & 0xFFFFFFFF) % self.side
        zi = (int(F(z) * self.mscale) & 0xFFFFFFFF) % self.side
        return int(self.mat[zi, xi])

    def origin(self, px, pz):
        ps = self.ps
        x0, z0 = F(F(px) * ps), F(F(pz) * ps)
        half = F(ps * F(0.5))
        y0, _ = self.terrain.height_normal(float(F(half + x0)), float(F(half + z0)))
        return x0, F(y0), z0

    def patch_distance(self, px, pz, cam):
        """geom_0075e4c0/0075e460: sqrt(min over 4 corners of dx^2 + (0.2*dy)^2 + dz^2)"""
        x0, y0, z0 = self.origin(px, pz)
        best = None
        for cx, cz in ((x0, z0), (x0 + self.ps, z0), (x0 + self.ps, z0 + self.ps), (x0, z0 + self.ps)):
            dx, dy, dz = float(cx) - cam[0], (float(y0) - cam[1]) * 0.2, float(cz) - cam[2]
            d2 = dx * dx + dy * dy + dz * dz
            best = d2 if best is None or d2 < best else best
        return math.sqrt(best)

    def grow_patch(self, px, pz):
        """GrowthManager::vf9 seed + vf11 budgets + vf14 loop (0x760950) + vf13 transform"""
        x0, y0, z0 = self.origin(px, pz)
        seed = (pz * 4711 + px * 13 + 23) & 0x7FFFFFFF
        rng = OG.Rng(seed)
        A = int(F(1024.0) * F(self.density))
        B = 0x1000
        n_iter = A // 8
        it, cont, vec = 0, True, []
        ps = self.ps
        stop = "iterations"
        while it < n_iter and cont:
            dx = F(F(rng.k()) * ps) * TWO_M24
            dz = F(F(rng.k()) * ps) * TWO_M24
            x = F(dx + x0)
            z = F(dz + z0)
            mat = self.material_at(x, z)
            u = F(rng.k()) * TWO_M24
            sel = None
            for t in self.materials[mat]:
                if u < t["probability"]:
                    if t["idx"] < 0:
                        continue
                    sel = t
                    break
                u = F(u - t["probability"])
            if sel is None:
                it += 1
                continue
            rec = self.recs[sel["idx"]]
            if rec["costA"] > A or rec["costB"] > B:
                cont = False
                stop = "budget"
                it += 1
                continue
            s = OG.sample_dist(sel["scale"], rng)
            R = rec["R"]
            cand = {"type": sel["idx"], "x": x, "z": z, "scale": s,
                    "rEq": F(F(R * sel["eq"]) * s), "rOth": F(F(R * sel["oth"]) * s),
                    "geometry": sel["geometryName"], "material": mat, "normalScale": sel["normalScale"]}
            cand["yaw"] = OG.sample_dist(sel["rotation"], rng)
            ok = True
            for o in vec:
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
            A -= rec["costA"]
            B -= rec["costB"]
            vec.append(cand)
            it += 2
        for inst in vec:
            h, n = self.terrain.height_normal(float(inst["x"]), float(inst["z"]))
            inst["y"] = F(h)
            ns = float(inst["normalScale"])
            up = (n[0] * ns, n[1], n[2] * ns)
            lu = math.sqrt(sum(v * v for v in up))
            inst["up"] = tuple(v / lu for v in up)
        used = (int(F(1024.0) * F(self.density)) - A, 0x1000 - B)
        return {"px": px, "pz": pz, "seed": seed, "origin": (x0, y0, z0), "inst": vec,
                "iterations": min(it, n_iter), "stop": stop, "used": used}

    def ring(self, cam):
        """GrowthManager::vf9 0x75f070: slots hold px = cx + N/2 - ((N*100 + i + cx) mod N)"""
        inv = 1.0 / float(self.ps)
        cx, cz = int(cam[0] * inv), int(cam[2] * inv)        # _ftol2 truncation
        N = self.N
        out = []
        for j in range(N):
            for i in range(N):
                px = N // 2 - ((N * 100 + i + cx) % N) + cx
                pz = N // 2 - ((N * 100 + j + cz) % N) + cz
                out.append((px, pz))
        return out

    def alpha(self, D, cam, centre_dist):
        """distance fade (vf9 0x75f270..) times close-to-ground factor (vf2 0x75f428.., 0x75f684..)"""
        r = D / self.view
        if r <= 1.0 - self.fadeRange:
            a = 1.0
        else:
            a = min(1.0, max(0.0, (1.0 - r) / self.fadeRange)) if self.fadeRange > 0 else 0.0
        h, _ = self.terrain.height_normal(cam[0], cam[2])
        g = abs(cam[1] - h) / self.ctgHeight
        g = min(1.0, max(self.ctgAlpha, g))
        f = min(1.0, max(g, centre_dist / self.ctgDist))
        return a * f


def rows(inst):
    t = math.radians(float(inst["yaw"]))
    fwd = (math.sin(t), 0.0, math.cos(t))
    up = inst["up"]
    right = (up[1] * fwd[2] - up[2] * fwd[1], up[2] * fwd[0] - up[0] * fwd[2], up[0] * fwd[1] - up[1] * fwd[0])
    fwd2 = (right[1] * up[2] - right[2] * up[1], right[2] * up[0] - right[0] * up[2], right[0] * up[1] - right[1] * up[0])
    return right, up, fwd2


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--level", default=OG.DEF_LEVEL)
    ap.add_argument("--meshes", default=OG.DEF_MESHES)
    ap.add_argument("--x", type=float, default=805.0)
    ap.add_argument("--z", type=float, default=502.0)
    ap.add_argument("--y", type=float, default=None, help="camera height (default terrain + 1.7)")
    ap.add_argument("--mode", choices=("game", "radius"), default="game")
    ap.add_argument("--radius", type=float, default=None, help="radius mode: metres (default viewDistance)")
    ap.add_argument("--json", default=None)
    ap.add_argument("--patches", action="store_true", help="print one line per patch")
    args = ap.parse_args()

    ug = UnderGrowth(args.level, args.meshes)
    if args.y is None:
        h, _ = ug.terrain.height_normal(args.x, args.z)
        args.y = h + 1.7
    cam = (args.x, args.y, args.z)
    print(f"level={os.path.basename(os.path.normpath(args.level))} viewDistance={ug.view} patch={float(ug.ps):.5f} m "
          f"({float(ug.ps) * ug.side / ug.world:.3f} map cells) mapSide={ug.side} densityScale={ug.density} "
          f"budget A={int(1024 * ug.density)} verts B=4096 idx n_iter={int(1024 * ug.density) // 8}")
    print("records (R = bbox corner radius, cost = LOD0 mat0 verts/indices):")
    for i, r in enumerate(ug.recs):
        print(f"  #{i}: {r['name']:20s} R={float(r['R']):6.3f} costA={r['costA']:4d} costB={r['costB']:4d}")

    if args.mode == "game":
        cand = ug.ring(cam)
        limit = ug.view
    else:
        limit = args.radius if args.radius is not None else ug.view
        r = int(math.ceil(limit / float(ug.ps))) + 1
        cx, cz = int(math.floor(args.x / float(ug.ps))), int(math.floor(args.z / float(ug.ps)))
        cand = [(px, pz) for pz in range(cz - r, cz + r + 1) for px in range(cx - r, cx + r + 1)]
    patches = []
    for px, pz in cand:
        if args.mode == "game":
            D = ug.patch_distance(px, pz, cam)
        else:
            D = ug.patch_distance(px, pz, (args.x, float(ug.origin(px, pz)[1]), args.z))
        if D >= limit:
            continue
        p = ug.grow_patch(px, pz)
        x0, y0, z0 = p["origin"]
        cd = math.dist((float(x0) + float(ug.ps) / 2, float(y0), float(z0) + float(ug.ps) / 2), cam)
        p["D"], p["alpha"] = D, ug.alpha(D, cam, cd)
        patches.append(p)

    allinst = [i for p in patches for i in p["inst"]]
    print(f"\ncamera=({cam[0]:g}, {cam[1]:.2f}, {cam[2]:g}) mode={args.mode} limit={limit:g} m: "
          f"{len(patches)} patches, {len(allinst)} instances")
    for k, v in sorted(Counter(i["geometry"] for i in allinst).items(), key=lambda kv: -kv[1]):
        print(f"  {k:20s} {v}")
    # density sanity vs budget
    if patches:
        cnt = [len(p["inst"]) for p in patches]
        va = [p["used"][0] for p in patches]
        ia = [p["used"][1] for p in patches]
        stops = Counter(p["stop"] for p in patches)
        print(f"per patch: instances min/mean/max {min(cnt)}/{sum(cnt) / len(cnt):.1f}/{max(cnt)} (cap 64 by "
              f"iterations); verts used max {max(va)}/1024; indices used max {max(ia)}/4096; "
              f"loop ended by {dict(stops)}")
        assert max(va) <= 1024 and max(ia) <= 4096 and max(cnt) <= 64
    if args.patches:
        print("\n   px   pz      seed   dist  alpha  inst verts  idx  iters stop")
        for p in sorted(patches, key=lambda p: p["D"]):
            print(f"  {p['px']:3d}  {p['pz']:3d} {p['seed']:9d} {p['D']:6.1f}  {p['alpha']:.2f}  {len(p['inst']):4d} "
                  f"{p['used'][0]:5d} {p['used'][1]:5d}  {p['iterations']:4d}  {p['stop']}")
    if args.json:
        out = []
        for p in patches:
            for i in p["inst"]:
                r0, r1, r2 = rows(i)
                out.append({"patch": [p["px"], p["pz"]], "type": i["geometry"], "material": i["material"],
                            "pos": [float(i["x"]), float(i["y"]), float(i["z"])], "scale": float(i["scale"]),
                            "yaw": float(i["yaw"]), "up": list(i["up"]), "rows": [list(r0), list(r1), list(r2)],
                            "patchAlpha": p["alpha"]})
        with open(args.json, "w") as fh:
            json.dump({"patchSize": float(ug.ps), "viewDistance": ug.view, "camera": list(cam),
                       "patches": [{"px": p["px"], "pz": p["pz"], "seed": p["seed"], "dist": p["D"],
                                    "alpha": p["alpha"], "count": len(p["inst"])} for p in patches],
                       "instances": out}, fh)
        print(f"\nwrote {len(out)} instances to {args.json}")


if __name__ == "__main__":
    main()
