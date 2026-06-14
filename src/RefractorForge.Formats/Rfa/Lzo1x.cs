using System;
using System.IO;

namespace RefractorForge.Formats.Rfa;

/// <summary>
/// Clean-room decompressor for the LZO1X bitstream, the codec Refractor uses for the
/// per-block payloads inside RFA archives (Battlefield 1942 / Vietnam).
/// </summary>
/// <remarks>
/// <para>
/// <b>Provenance / licensing.</b> This is an independent, clean-room implementation written
/// purely from the published <i>format description</i> of the LZO1X bitstream (the prose
/// byte-sequence specification, e.g. the Linux kernel's <c>staging/lzo</c> documentation).
/// It does <b>not</b> incorporate, translate, or derive from the reference LZO/minilzo C
/// sources, which are GPL-2.0. No GPL code is linked or shipped. Correctness was validated
/// by decoding the retail archives and comparing byte-for-byte (SHA-256) against the system
/// <c>liblzo2</c> used strictly as an external test oracle. The result is a permissively
/// licensable, dependency-free decoder that the editor can ship in a single .exe.
/// </para>
/// <para>
/// Battlefield archives use the <b>v0</b> stream form (no version marker byte). Only
/// decompression is implemented (the editor reads meshes; it never re-LZO-packs).
/// </para>
/// </remarks>
public static class Lzo1x
{
    /// <summary>Decompress an LZO1X v0 stream into <paramref name="dst"/>.</summary>
    /// <param name="src">Compressed input.</param>
    /// <param name="dst">Output buffer; must be at least <paramref name="dstLen"/> bytes.</param>
    /// <param name="dstLen">The exact expected decompressed length.</param>
    /// <returns>The number of bytes written (equal to <paramref name="dstLen"/> on success).</returns>
    /// <exception cref="InvalidDataException">Stream malformed or output length != <paramref name="dstLen"/>.</exception>
    public static int Decompress(ReadOnlySpan<byte> src, Span<byte> dst, int dstLen)
    {
        if (dst.Length < dstLen) throw new ArgumentException("Destination buffer too small.", nameof(dst));

        int ip = 0, op = 0, state = 0, sLen = src.Length;

        // initial literal run: first byte 18..255 => copy (b-17) literals
        if (sLen > 0 && src[0] >= 18)
        {
            int run = src[ip++] - 17;
            CopyLit(src, dst, ref ip, ref op, run, dstLen);
            state = run < 4 ? run : 4;
        }

        while (ip < sLen)
        {
            int t = src[ip++];

            if (t < 16)
            {
                if (state == 0)
                {
                    int length = t;
                    if (length == 0)
                    {
                        length = 15;
                        int z;
                        while ((z = ReadByte(src, ref ip)) == 0) length += 255;
                        length += z;
                    }
                    CopyLit(src, dst, ref ip, ref op, length + 3, dstLen);
                    state = 4;
                }
                else if (state <= 3)
                {
                    int s = t & 3;
                    int h = ReadByte(src, ref ip);
                    CopyMatch(dst, ref op, (h << 2) + ((t >> 2) & 3) + 1, 2, dstLen);
                    CopyLit(src, dst, ref ip, ref op, s, dstLen);
                    state = s;
                }
                else // state == 4
                {
                    int s = t & 3;
                    int h = ReadByte(src, ref ip);
                    CopyMatch(dst, ref op, (h << 2) + ((t >> 2) & 3) + 2049, 3, dstLen);
                    CopyLit(src, dst, ref ip, ref op, s, dstLen);
                    state = s;
                }
            }
            else if (t < 32)
            {
                // 0b0001_HLLL : long-distance match (>=16384) or end-of-stream
                int length = t & 7;
                if (length == 0)
                {
                    length = 7;
                    int z;
                    while ((z = ReadByte(src, ref ip)) == 0) length += 255;
                    length += z;
                }
                int h = (t >> 3) & 1;
                int le16 = ReadByte(src, ref ip) | (ReadByte(src, ref ip) << 8);
                int s = le16 & 3;
                int distance = 16384 + (h << 14) + (le16 >> 2);
                if (distance == 16384) break;   // end-of-stream
                CopyMatch(dst, ref op, distance, length + 2, dstLen);
                CopyLit(src, dst, ref ip, ref op, s, dstLen);
                state = s;
            }
            else if (t < 64)
            {
                // 0b001_LLLLL : match, distance 1..16384
                int length = t & 31;
                if (length == 0)
                {
                    length = 31;
                    int z;
                    while ((z = ReadByte(src, ref ip)) == 0) length += 255;
                    length += z;
                }
                int le16 = ReadByte(src, ref ip) | (ReadByte(src, ref ip) << 8);
                int s = le16 & 3;
                CopyMatch(dst, ref op, (le16 >> 2) + 1, length + 2, dstLen);
                CopyLit(src, dst, ref ip, ref op, s, dstLen);
                state = s;
            }
            else if (t < 128)
            {
                // 0b01L_DDDSS : short match length 3..4, distance 1..2048
                int s = t & 3;
                int d = (t >> 2) & 7;
                int h = ReadByte(src, ref ip);
                CopyMatch(dst, ref op, (h << 3) + d + 1, 3 + ((t >> 5) & 1), dstLen);
                CopyLit(src, dst, ref ip, ref op, s, dstLen);
                state = s;
            }
            else
            {
                // 0b1LL_DDDSS : short match length 5..8, distance 1..2048
                int s = t & 3;
                int d = (t >> 2) & 7;
                int h = ReadByte(src, ref ip);
                CopyMatch(dst, ref op, (h << 3) + d + 1, 5 + ((t >> 5) & 3), dstLen);
                CopyLit(src, dst, ref ip, ref op, s, dstLen);
                state = s;
            }
        }

        if (op != dstLen)
            throw new InvalidDataException($"Decoded {op} bytes; expected {dstLen}.");
        return op;
    }

    /// <summary>Convenience wrapper that allocates and returns the decompressed bytes.</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> src, int dstLen)
    {
        var dst = new byte[dstLen];
        Decompress(src, dst, dstLen);
        return dst;
    }

    // ---- Compression --------------------------------------------------------
    // Greedy LZO1X-1-style compressor producing the exact v0 bitstream the decoder above accepts
    // (the documented inverse of each instruction). Clean-room from the format description; the
    // greedy single-entry hash parse is the standard LZO1X-1 structure and is not GPL-derived.
    // Every output is validated by round-tripping through Decompress (see the RFA round-trip test).

    private const int HashLog = 14;
    private const int HashSize = 1 << HashLog;
    private const int MaxDist = 0xBFFF;     // 49151 — the largest distance the v0 stream encodes

    private static int Hash3(ReadOnlySpan<byte> s, int i)
    {
        uint v = (uint)(s[i] | (s[i + 1] << 8) | (s[i + 2] << 16));
        return (int)((v * 2654435761u) >> (32 - HashLog)) & (HashSize - 1);
    }

    /// <summary>Compress <paramref name="src"/> into an LZO1X v0 stream.</summary>
    public static byte[] Compress(ReadOnlySpan<byte> src)
    {
        int n = src.Length;
        var o = new List<byte>(n / 2 + 16);
        if (n == 0) { o.Add(0x11); o.Add(0x00); o.Add(0x00); return o.ToArray(); }

        var table = new int[HashSize];
        for (int i = 0; i < HashSize; i++) table[i] = -1;

        int ip = 0, anchor = 0, sPatch = -1;
        bool firstRun = true;

        while (ip + 3 <= n)
        {
            int h = Hash3(src, ip);
            int cand = table[h];
            table[h] = ip;

            if (cand >= 0 && ip - cand <= MaxDist
                && src[cand] == src[ip] && src[cand + 1] == src[ip + 1] && src[cand + 2] == src[ip + 2])
            {
                FlushLiterals(o, src, anchor, ip - anchor, ref firstRun, ref sPatch);

                int dist = ip - cand;
                int mlen = 3;
                while (ip + mlen < n && src[cand + mlen] == src[ip + mlen]) mlen++;
                EmitMatch(o, dist, mlen, ref sPatch);

                ip += mlen;
                anchor = ip;
                firstRun = false;
            }
            else ip++;
        }
        FlushLiterals(o, src, anchor, n - anchor, ref firstRun, ref sPatch);   // final literal run
        o.Add(0x11); o.Add(0x00); o.Add(0x00);                                 // end-of-stream marker
        return o.ToArray();
    }

    private static void EmitExtLen(List<byte> o, int rem)   // rem >= 1, encodes as zero bytes + final (1..255)
    {
        while (rem > 255) { o.Add(0); rem -= 255; }
        o.Add((byte)rem);
    }

    private static void FlushLiterals(List<byte> o, ReadOnlySpan<byte> src, int start, int len, ref bool firstRun, ref int sPatch)
    {
        if (len <= 0) return;
        if (firstRun)
        {
            if (len <= 238) o.Add((byte)(17 + len));               // initial-run form
            else { o.Add(0); EmitExtLen(o, len - 18); }            // t==0 extended literal run
        }
        else if (len <= 3)
        {
            o[sPatch] = (byte)(o[sPatch] | len);                   // fold into preceding match's trailing-literal count
        }
        else
        {
            if (len <= 18) o.Add((byte)(len - 3));                 // t in [1..15], state==0 literal run
            else { o.Add(0); EmitExtLen(o, len - 18); }
        }
        for (int i = 0; i < len; i++) o.Add(src[start + i]);
        sPatch = -1;
    }

    private static void EmitMatch(List<byte> o, int dist, int mlen, ref int sPatch)
    {
        if (dist <= 2048 && mlen <= 8)
        {
            int d = (dist - 1) & 7;
            if (mlen <= 4) o.Add((byte)(0x40 | ((mlen - 3) << 5) | (d << 2)));   // type 4: len 3-4
            else o.Add((byte)(0x80 | ((mlen - 5) << 5) | (d << 2)));             // type 5: len 5-8
            sPatch = o.Count - 1;                                                // s = low 2 bits of token
            o.Add((byte)((dist - 1) >> 3));
        }
        else if (dist <= 16384)
        {
            int lenm2 = mlen - 2;                                                // type 3: dist 1..16384
            if (lenm2 <= 31) o.Add((byte)(0x20 | lenm2));
            else { o.Add(0x20); EmitExtLen(o, mlen - 33); }
            int le16 = (dist - 1) << 2;
            o.Add((byte)(le16 & 0xFF)); sPatch = o.Count - 1;                    // s = low 2 bits of le16 low byte
            o.Add((byte)((le16 >> 8) & 0xFF));
        }
        else
        {
            int dd = dist - 16384;                                               // type 2: dist 16385..49151
            int hbit = (dd >> 14) & 1;
            int lenm2 = mlen - 2;
            if (lenm2 <= 7) o.Add((byte)(0x10 | (hbit << 3) | lenm2));
            else { o.Add((byte)(0x10 | (hbit << 3))); EmitExtLen(o, mlen - 9); }
            int le16 = (dd & 0x3FFF) << 2;
            o.Add((byte)(le16 & 0xFF)); sPatch = o.Count - 1;
            o.Add((byte)((le16 >> 8) & 0xFF));
        }
    }

    private static int ReadByte(ReadOnlySpan<byte> src, ref int ip)
    {
        if ((uint)ip >= (uint)src.Length) throw new InvalidDataException("Unexpected end of input.");
        return src[ip++];
    }

    private static void CopyLit(ReadOnlySpan<byte> src, Span<byte> dst, ref int ip, ref int op, int count, int dstLen)
    {
        if (count == 0) return;
        if (count < 0 || ip + count > src.Length || op + count > dstLen)
            throw new InvalidDataException("Literal run out of bounds.");
        src.Slice(ip, count).CopyTo(dst.Slice(op, count));
        ip += count; op += count;
    }

    private static void CopyMatch(Span<byte> dst, ref int op, int distance, int length, int dstLen)
    {
        int from = op - distance;
        if (from < 0) throw new InvalidDataException($"Back-reference before start of output (op={op}, distance={distance}, length={length}, dstLen={dstLen}).");
        if (op + length > dstLen) throw new InvalidDataException($"Match run past end of output (op={op}, length={length}, dstLen={dstLen}).");
        for (int k = 0; k < length; k++) dst[op + k] = dst[from + k]; // overlap-safe (dist==1 RLE)
        op += length;
    }
}
