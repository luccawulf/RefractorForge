using System;
using System.Collections.Generic;
using System.IO;
using System.Buffers.Binary;

namespace RefractorForge.Formats.Terrain
{
    // Codec for the engine's packed terrain shadow file, Textures/LightmapShadowBits.lsb.
    //
    // Reverse-engineered from BfVietnam.exe (per-tile reader FUN_00764380 / writer FUN_007644a0,
    // confirmed byte-exact against five real maps). BF1942 (flags=0) and BFV (flags=1) share the
    // identical format. There is NO file header: the file is purely a sequence of tile blocks read
    // from offset 0 until EOF. The terrain's shadow grid is gridDim*gridDim tiles (16 or 64 in the
    // wild), each tile holding per-row run-length spans of cast-shadow visibility.
    //
    // Per-tile layout (all little-endian; the in-game reader pulls these sequentially):
    //   u32 count1                         // tile head-node count; if 0 the tile is empty (block ends here)
    //   u32 numRows                        // number of rows in this tile
    //   u32 rowCounts[numRows]             // span count per row
    //   for r in 0..numRows:
    //     u16 head                         // one head value per row
    //     u16 span[rowCounts[r]]           // each span: bits 0..12 = run length, bit 13 (0x2000) = lit flag
    //
    // We retain the exact parsed values so Encode() reproduces the original bytes verbatim — the
    // RFA/DDS "round-trip the real file byte-exact first" discipline. Pixel semantics (expanding the
    // bit-13 spans into a visibility raster) are intentionally not done here; that belongs to the
    // shadow-bake write-back step, which is a separate concern.
    public sealed class LightmapShadowBits
    {
        // Bit 13 (0x2000) of a span token is the "lit" flag; the low 13 bits are the run length. A row is a
        // run-length-encoded scanline of LIT_RUN_MAX-wide visibility, runs strictly alternating lit/shadow.
        public const int LitFlag = 0x2000;
        public const int RunMask = 0x1FFF;

        public sealed class Row
        {
            public ushort Head;
            public ushort[] Spans = Array.Empty<ushort>();

            /// <summary>All tokens of the row in order: the head value followed by the spans.</summary>
            public IEnumerable<ushort> Tokens
            {
                get { yield return Head; foreach (var s in Spans) yield return s; }
            }

            /// <summary>Scanline width in pixels = sum of every token's run length.</summary>
            public int PixelWidth
            {
                get { int w = Head & RunMask; foreach (var s in Spans) w += s & RunMask; return w; }
            }

            /// <summary>Expand this RLE row into per-pixel visibility (lit=255, shadow=0) at <paramref name="dst"/>[off..].</summary>
            public void ToPixels(byte[] dst, int off)
            {
                foreach (ushort t in Tokens)
                {
                    byte v = (t & LitFlag) != 0 ? (byte)255 : (byte)0;
                    int run = t & RunMask;
                    for (int i = 0; i < run; i++) dst[off++] = v;
                }
            }

            /// <summary>
            /// Canonical RLE of a visibility scanline (any non-zero pixel = lit): maximal alternating runs.
            /// This is the exact form the engine writes — re-encoding a real row reproduces its bytes.
            /// </summary>
            public static Row FromPixels(ReadOnlySpan<byte> line)
            {
                var toks = new List<ushort>();
                int i = 0, n = line.Length;
                while (i < n)
                {
                    int lit = line[i] != 0 ? 1 : 0;
                    int j = i;
                    while (j < n && (line[j] != 0 ? 1 : 0) == lit) j++;
                    int run = j - i;
                    // Runs wider than RunMask can't occur at the engine's 1024-wide tiles, but split safely if they ever do.
                    while (run > RunMask)
                    {
                        toks.Add((ushort)(RunMask | (lit << 13)));
                        run -= RunMask;
                    }
                    toks.Add((ushort)((run & RunMask) | (lit << 13)));
                    i = j;
                }
                if (toks.Count == 0) toks.Add(0);   // empty line -> a single zero token
                var row = new Row { Head = toks[0] };
                if (toks.Count > 1) row.Spans = toks.GetRange(1, toks.Count - 1).ToArray();
                return row;
            }
        }

        public sealed class Tile
        {
            // Raw count1 field as stored. Zero marks an empty tile (no rows follow on disk).
            public uint Count1;
            public Row[] Rows = Array.Empty<Row>();
            public bool IsEmpty => Count1 == 0;

            /// <summary>Pixel width of the tile (every row is the same width in practice); 0 if empty.</summary>
            public int Width => Rows.Length == 0 ? 0 : Rows[0].PixelWidth;
            public int Height => Rows.Length;

            /// <summary>Decode the tile to a row-major visibility raster (lit=255, shadow=0), Height*Width bytes.</summary>
            public byte[] ToRaster()
            {
                int w = Width, h = Height;
                var px = new byte[w * h];
                for (int y = 0; y < h; y++) Rows[y].ToPixels(px, y * w);
                return px;
            }

            /// <summary>Build a tile by canonically RLE-encoding a row-major visibility raster (non-zero = lit).</summary>
            public static Tile FromRaster(ReadOnlySpan<byte> raster, int width, int height)
            {
                var rows = new Row[height];
                for (int y = 0; y < height; y++)
                    rows[y] = Row.FromPixels(raster.Slice(y * width, width));
                // count1 mirrors numRows in every observed file.
                return new Tile { Count1 = (uint)height, Rows = rows };
            }
        }

        public List<Tile> Tiles { get; } = new List<Tile>();

        /// <summary>Tiles per side: every observed file is a square grid (8x8 or 4x4) of equal patches.</summary>
        public int GridDim
        {
            get { int g = (int)Math.Round(Math.Sqrt(Tiles.Count)); return g * g == Tiles.Count ? g : 0; }
        }

        /// <summary>Pixel side of one tile (1024 in every observed file); 0 if there are no non-empty tiles.</summary>
        public int TilePixels
        {
            get { foreach (var t in Tiles) if (!t.IsEmpty) return t.Width; return 0; }
        }

        // --- whole-world raster bridge (the bake write-back path) ---
        //
        // The .lsb is a GridDim x GridDim grid of TilePixels-square per-patch lightmaps; tile linear index
        // is y*GridDim + x (row-major, matching the engine's (y<<shift)+x with shift=log2(GridDim)). Stitched
        // together they form a (GridDim*TilePixels) square visibility image of the whole world. Column 0 is
        // world -X edge, row 0 is world -Z edge — the SAME orientation TerrainShadow.Bake/BakeAtlas already use
        // (texel (x,y) -> world (x/size*ws, y/size*ws)), so a bake slots straight in. NOTE: the absolute world
        // orientation (which corner the engine treats as (0,0), and row +Z vs -Z) is the one thing NOT yet
        // confirmed in-game; if a generated map's shadows come out mirrored/rotated, flip here, not in the codec.

        /// <summary>Stitch all tiles into one row-major visibility raster (lit=255, shadow=0); side = GridDim*TilePixels.</summary>
        public byte[] ToVisibility(out int side)
        {
            int g = GridDim, tp = TilePixels;
            side = g * tp;
            var full = new byte[(long)side * side <= int.MaxValue ? side * side : throw new InvalidOperationException("shadow raster too large")];
            for (int gy = 0; gy < g; gy++)
                for (int gx = 0; gx < g; gx++)
                {
                    var tile = Tiles[gy * g + gx];
                    if (tile.IsEmpty) continue;                 // empty tile = all shadow (0), already zero-filled
                    var raster = tile.ToRaster();               // tp*tp, row-major
                    for (int r = 0; r < tp; r++)
                        Array.Copy(raster, r * tp, full, (gy * tp + r) * side + gx * tp, tp);
                }
            return full;
        }

        /// <summary>
        /// Build a full .lsb from a whole-world visibility raster (non-zero = lit), slicing it into a
        /// gridDim x gridDim grid of tilePx-square tiles. Inverse of <see cref="ToVisibility"/>.
        /// </summary>
        public static LightmapShadowBits FromVisibility(ReadOnlySpan<byte> visibility, int side, int gridDim, int tilePx = 1024)
        {
            if (gridDim < 1) throw new ArgumentOutOfRangeException(nameof(gridDim));
            if (side != gridDim * tilePx) throw new ArgumentException($"side {side} must equal gridDim*tilePx ({gridDim * tilePx}).");
            var lsb = new LightmapShadowBits();
            var tileRaster = new byte[tilePx * tilePx];
            for (int gy = 0; gy < gridDim; gy++)
                for (int gx = 0; gx < gridDim; gx++)
                {
                    for (int r = 0; r < tilePx; r++)
                    {
                        int srcRow = (gy * tilePx + r) * side + gx * tilePx;
                        visibility.Slice(srcRow, tilePx).CopyTo(tileRaster.AsSpan(r * tilePx, tilePx));
                    }
                    lsb.Tiles.Add(Tile.FromRaster(tileRaster, tilePx, tilePx));
                }
            return lsb;
        }

        public static LightmapShadowBits Load(string path) => Decode(File.ReadAllBytes(path));

        /// <summary>Find and load <c>LightmapShadowBits.lsb</c> anywhere under a level folder; null if absent.</summary>
        public static LightmapShadowBits? TryLoadFolder(string levelDir)
        {
            foreach (var hit in Directory.EnumerateFiles(levelDir, "LightmapShadowBits.lsb", SearchOption.AllDirectories))
            {
                try { return Load(hit); } catch { return null; }
            }
            return null;
        }

        public static LightmapShadowBits Decode(byte[] data)
        {
            var lsb = new LightmapShadowBits();
            int p = 0;
            int n = data.Length;
            while (p < n)
            {
                uint count1 = ReadU32(data, ref p);
                var tile = new Tile { Count1 = count1 };
                if (count1 != 0)
                {
                    int numRows = (int)ReadU32(data, ref p);
                    var rowCounts = new int[numRows];
                    for (int r = 0; r < numRows; r++)
                        rowCounts[r] = (int)ReadU32(data, ref p);

                    var rows = new Row[numRows];
                    for (int r = 0; r < numRows; r++)
                    {
                        var row = new Row { Head = ReadU16(data, ref p) };
                        int sc = rowCounts[r];
                        var spans = new ushort[sc];
                        for (int k = 0; k < sc; k++)
                            spans[k] = ReadU16(data, ref p);
                        row.Spans = spans;
                        rows[r] = row;
                    }
                    tile.Rows = rows;
                }
                lsb.Tiles.Add(tile);
            }
            return lsb;
        }

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            Span<byte> buf = stackalloc byte[4];
            foreach (var tile in Tiles)
            {
                WriteU32(ms, buf, tile.Count1);
                if (tile.Count1 == 0) continue;

                int numRows = tile.Rows.Length;
                WriteU32(ms, buf, (uint)numRows);
                for (int r = 0; r < numRows; r++)
                    WriteU32(ms, buf, (uint)tile.Rows[r].Spans.Length);
                for (int r = 0; r < numRows; r++)
                {
                    var row = tile.Rows[r];
                    WriteU16(ms, buf, row.Head);
                    for (int k = 0; k < row.Spans.Length; k++)
                        WriteU16(ms, buf, row.Spans[k]);
                }
            }
            return ms.ToArray();
        }

        public void Save(string path) => File.WriteAllBytes(path, Encode());

        private static uint ReadU32(byte[] d, ref int p)
        {
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p, 4));
            p += 4;
            return v;
        }

        private static ushort ReadU16(byte[] d, ref int p)
        {
            ushort v = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p, 2));
            p += 2;
            return v;
        }

        private static void WriteU32(Stream s, Span<byte> buf, uint v)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buf, v);
            s.Write(buf.Slice(0, 4));
        }

        private static void WriteU16(Stream s, Span<byte> buf, ushort v)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buf, v);
            s.Write(buf.Slice(0, 2));
        }
    }
}
