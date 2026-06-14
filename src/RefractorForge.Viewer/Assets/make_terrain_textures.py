#!/usr/bin/env python3
"""Generate placeholder tileable terrain textures for the bundled RefractorForge Texture Library.
These are STAND-INS so the library/browser isn't empty and the folder structure exists; the user
replaces them with a real texture pack (any .bmp/.dds/.tga/.png/.jpg dropped into these folders).
Writes 24-bit bottom-up BMPs (what Texture2D.LoadBmp reads) with seamless value-noise so they tile."""
import os, struct, math, random

ROOT = os.path.join(os.path.dirname(__file__), "TerrainTextures")

def value_noise_tile(size, period, seed):
    rnd = random.Random(seed)
    lat = [[rnd.random() for _ in range(period)] for _ in range(period)]
    def smooth(t): return t * t * (3 - 2 * t)
    out = [[0.0] * size for _ in range(size)]
    for y in range(size):
        for x in range(size):
            fx = x / size * period; fy = y / size * period
            x0 = int(fx) % period; y0 = int(fy) % period
            x1 = (x0 + 1) % period; y1 = (y0 + 1) % period
            tx = smooth(fx - math.floor(fx)); ty = smooth(fy - math.floor(fy))
            top = lat[y0][x0] * (1 - tx) + lat[y0][x1] * tx
            bot = lat[y1][x0] * (1 - tx) + lat[y1][x1] * tx
            out[y][x] = top * (1 - ty) + bot * ty
    return out

def write_bmp(path, size, base, seed):
    # multi-octave seamless noise modulating a base colour -> a believable tileable ground texture
    n = [[0.0] * size for _ in range(size)]
    amp = 1.0; tot = 0.0
    for period, a in ((4, 0.6), (8, 0.3), (16, 0.15)):
        lay = value_noise_tile(size, period, seed + period)
        for y in range(size):
            for x in range(size):
                n[y][x] += lay[y][x] * a
        tot += a
    rows = bytearray()
    pad = (4 - (size * 3) % 4) % 4
    # BMP is bottom-up
    for y in range(size - 1, -1, -1):
        for x in range(size):
            v = n[y][x] / tot           # 0..1
            shade = 0.72 + 0.42 * (v - 0.5)
            r = max(0, min(255, int(base[0] * shade)))
            g = max(0, min(255, int(base[1] * shade)))
            b = max(0, min(255, int(base[2] * shade)))
            rows += bytes((b, g, r))    # BGR
        rows += b"\x00" * pad
    img_size = len(rows)
    fh = b"BM" + struct.pack("<IHHI", 14 + 40 + img_size, 0, 0, 54)
    dib = struct.pack("<IiiHHIIiiII", 40, size, size, 1, 24, 0, img_size, 2835, 2835, 0, 0)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(fh); f.write(dib); f.write(rows)
    print("wrote", path)

PLACEHOLDERS = [
    ("Grass/placeholder_grass.bmp", (96, 134, 66), 11),
    ("Rock/placeholder_rock.bmp",   (122, 120, 116), 22),
    ("Sand/placeholder_sand.bmp",   (196, 178, 132), 33),
    ("Dirt/placeholder_dirt.bmp",   (124, 96, 66), 44),
    ("Road/placeholder_road.bmp",   (92, 90, 92), 55),
]
for rel, base, seed in PLACEHOLDERS:
    write_bmp(os.path.join(ROOT, rel.replace("/", os.sep)), 64, base, seed)

README = """RefractorForge - Texture Library
================================

Drop your own tileable terrain textures into this folder (or its subfolders) and they
appear in the editor's Texture Library browser (Surface mapper -> "Texture Library...").

- Supported formats: .bmp  .dds  .tga  .png  .jpg
- Make them SEAMLESS / tileable (they repeat across the ground). 64-512 px square is ideal.
- Subfolders become categories in the browser (e.g. Grass, Rock, Sand, Dirt, Road).
- The "placeholder_*.bmp" files are stand-ins - delete them and add your own pack.

Use a texture as a brush (paint it onto the terrain), Fill the whole terrain with it, or
combine two textures by height/slope with the Layer Tool (Editor42-style noise blend).
"""
with open(os.path.join(ROOT, "README.txt"), "w", encoding="utf-8") as f:
    f.write(README)
print("wrote README.txt")
