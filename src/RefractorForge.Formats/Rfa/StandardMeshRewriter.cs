using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RefractorForge.Formats.Rfa;

/// <summary>
/// Re-emits a parsed <see cref="StandardMesh"/>, changing only its lightmap UVs - and, where an unwrap needs a
/// seam, duplicating the vertices along it.
///
/// <para>This is NOT <see cref="StandardMeshWriter"/>. That one is an AUTHORING writer: it builds a mesh from an
/// <see cref="Mesh.ObjMesh"/>, which has no second UV channel, no per-material renderType or materialSettings, and
/// no idea what the 12 unknown header bytes or the portal chunk held. Pointing it at a retail mesh would quietly
/// discard all of that. This one starts from the original bytes and puts every field back where it found it.</para>
///
/// <para><b>The contract, and the gate:</b> with no plan, the output is BYTE-IDENTICAL to the input. Every mesh in
/// both retail corpora must satisfy that before this class is trusted with anything - the same standard
/// <see cref="StandardMeshWriter.WriteCollisionSection"/> already meets. It is what proves the undecoded collision
/// BSP tails, the shadow block and the portal chunk survive a rewrite without anyone having to understand them.</para>
/// </summary>
public static class StandardMeshRewriter
{
    /// <summary>
    /// Widen a material's vertex so it can hold a lightmap UV at all: 32 bytes (pos/normal/uv) to 40
    /// (+ the second UV), vertexFormat 1041 to 9233.
    ///
    /// <para>Only that one step is allowed, and it is not a guess: both games ship meshes at exactly this
    /// declaration carrying real unwraps - 19 of BfVietnam's own (O_Citadel_M1, O_HCMT*, ControlTower_M1) and all
    /// 144 of BF1942's unwrapped meshes - so the layout is one the engine demonstrably loads. Promotion to the
    /// 64-byte PLANAR form is refused: its trailing 24 bytes are a tangent frame this project has not decoded, and
    /// inventing one is a different and much larger risk.</para>
    /// </summary>
    public sealed record StridePromotion(uint NewVertexFormat, uint NewVertexByteSize)
    {
        /// <summary>The only promotion there is: 32-byte pos/normal/uv to 40-byte + lightmap UV.</summary>
        public static readonly StridePromotion To40 = new(9233u, 40u);
    }

    /// <summary>What to do with one material's vertices.</summary>
    /// <param name="Uv2">New lightmap UV per OUTPUT vertex (originals first, then the clones), or empty to leave
    /// the existing ones alone. A material whose vertices have no lightmap slot ignores this.</param>
    /// <param name="CloneSources">For each appended clone, the original vertex it copies. Position, normal,
    /// diffuse UV and the 64-byte layout's tangent frame are copied from the source VERBATIM - never recomputed -
    /// so a clone differs from its source in exactly the eight lightmap bytes.</param>
    /// <param name="Indices">The index buffer remapped onto the new vertex numbering, or null to keep the original.
    /// Must stay <see cref="SmMaterial.NumFaceValues"/> long: this is a renumbering, not a retopology, which is why
    /// it is correct for a triangle STRIP as well as a list.</param>
    /// <param name="Promote">Widen this material's vertex before writing, or null to keep its stride. Required
    /// for any 32-byte material: without it the lightmap UVs have nowhere to go.</param>
    public sealed record MaterialPlan(
        (float U, float V)[] Uv2,
        int[] CloneSources,
        ushort[]? Indices = null,
        StridePromotion? Promote = null);

    /// <param name="SetQFlag">Write the version-10 qflag. Measured across both retail corpora, qflag 1 means "this
    /// mesh has a real unwrap" (371 of 371 unwrapped meshes), so a rewrite that fills the channel should set it.
    /// Null keeps whatever the file had.</param>
    public sealed record Options(bool? SetQFlag = null);

    /// <summary>
    /// Re-emit <paramref name="original"/>. <paramref name="plan"/> is indexed [lod][material] and may be null, or
    /// hold nulls, for anything left untouched.
    /// </summary>
    public static byte[] Write(byte[] original, StandardMesh src,
                               IReadOnlyList<IReadOnlyList<MaterialPlan?>>? plan = null,
                               Options? opts = null)
    {
        if (original is null) throw new ArgumentNullException(nameof(original));
        if (src is null) throw new ArgumentNullException(nameof(src));
        opts ??= new Options();

        using var ms = new MemoryStream(original.Length + 4096);
        var w = new BinaryWriter(ms);

        w.Write(src.Version);
        w.Write(src.HeaderUnknown4);
        // The bounding box is copied as BYTES, not rewritten from the parsed floats: a float that round-trips
        // through a parse is normally bit-exact, but a NaN payload is not guaranteed to be, and some retail meshes
        // do carry NaNs. Copying removes the question entirely. It sits right after version + the 4 unknown bytes.
        w.Write(original, 8, 24);
        if (src.Version == 10) w.Write(opts.SetQFlag is { } q ? (byte)(q ? 1 : 0) : src.QFlag);

        w.Write((uint)src.NumCollisionMeshes);
        foreach (var sec in src.CollisionSections) { w.Write((uint)sec.Length); w.Write(sec); }

        w.Write((uint)src.NumLods);
        for (int l = 0; l < src.Lods.Count; l++)
        {
            var mats = src.Lods[l];
            w.Write((uint)mats.Count);

            // All headers first, then all geometry - the format's own order.
            for (int m = 0; m < mats.Count; m++)
            {
                var mat = mats[m];
                var mp = PlanFor(plan, l, m);
                Validate(mat, mp, opts);
                int extraVerts = mp?.CloneSources.Length ?? 0;
                int nv = mat.NumVertices + extraVerts;
                if (nv > 65535)
                    throw new InvalidDataException(
                        $"Material '{mat.Name}' would reach {nv} vertices after splitting (>65535 u16 limit).");
                var name = Encoding.Latin1.GetBytes(mat.Name);
                w.Write((uint)name.Length); w.Write(name);
                w.Write(mat.HeaderUnknown12);
                w.Write(mat.RenderType);
                w.Write(mp?.Promote?.NewVertexFormat ?? mat.VertexFormat);
                w.Write(mp?.Promote?.NewVertexByteSize ?? mat.VertexByteSize);
                w.Write((uint)nv);
                w.Write((uint)mat.NumFaceValues);
                w.Write(mat.MaterialSettings);
            }

            for (int m = 0; m < mats.Count; m++)
            {
                var mat = mats[m];
                WriteVertices(w, original, mat, PlanFor(plan, l, m));
                var idx = PlanFor(plan, l, m)?.Indices ?? mat.RawIndices;
                if (idx.Length != mat.NumFaceValues)
                    throw new InvalidDataException(
                        $"Material '{mat.Name}': index buffer is {idx.Length} values, the header declares {mat.NumFaceValues}.");
                foreach (var i in idx) w.Write(i);
            }
        }

        // The shadow block and the portal chunk, verbatim. They are only partly decoded, and nothing here needs
        // to decode them - a rewrite that changes vertex counts does not move anything inside them.
        w.Write(original, src.TrailerOffset, original.Length - src.TrailerOffset);

        w.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// Refuse the plans that would produce a mesh which parses cleanly and is WRONG - the failure mode that costs
    /// days, because nothing downstream can tell it from a good one.
    /// </summary>
    private static void Validate(SmMaterial mat, MaterialPlan? plan, Options opts)
    {
        if (plan is null) return;
        int nvOut = mat.NumVertices + plan.CloneSources.Length;

        foreach (var srcV in plan.CloneSources)
            if ((uint)srcV >= (uint)mat.NumVertices)
                throw new InvalidDataException(
                    $"Material '{mat.Name}': a clone names source vertex {srcV}, which does not exist ({mat.NumVertices} vertices).");

        // A plan that supplies some UVs but not all would leave the rest holding whatever their source vertex had,
        // which on a freshly widened vertex is zero - an object lit correctly in patches.
        if (plan.Uv2.Length != 0 && plan.Uv2.Length != nvOut)
            throw new InvalidDataException(
                $"Material '{mat.Name}': {plan.Uv2.Length} lightmap UVs for {nvOut} output vertices - it must be all of them or none.");

        int outStride = (int)(plan.Promote?.NewVertexByteSize ?? mat.VertexByteSize);
        if (plan.Promote is { } pr)
        {
            if (mat.VertexByteSize != 32 || pr.NewVertexByteSize != 40 || pr.NewVertexFormat != 9233)
                throw new InvalidDataException(
                    $"Material '{mat.Name}': the only supported promotion is 32-byte/1041 to 40-byte/9233, not " +
                    $"{mat.VertexByteSize}/{mat.VertexFormat} to {pr.NewVertexByteSize}/{pr.NewVertexFormat}. " +
                    "The 64-byte planar form carries an undecoded tangent frame and must never be synthesised.");
        }

        // THE SILENT ONE. Without this, a plan aimed at a 32-byte material writes the clones and the remapped
        // indices but drops every UV on the floor, because there is nowhere in a 32-byte vertex to put one. The
        // result is a bigger, renumbered, still-unlit mesh that round-trips perfectly and fails only in game.
        if (plan.Uv2.Length > 0 && outStride - 32 < 8)
            throw new InvalidDataException(
                $"Material '{mat.Name}' has a {outStride}-byte vertex with no room for a lightmap UV. " +
                "Supply StridePromotion.To40, or do not plan UVs for it.");

        // qflag 1 tells the engine this mesh carries a real unwrap. Letting it say so while a material cannot
        // hold one is the same lie, one level up.
        if (opts.SetQFlag == true && outStride - 32 < 8)
            throw new InvalidDataException(
                $"Material '{mat.Name}' cannot hold a lightmap UV, so qflag must not be set to 1 for this mesh.");
    }

    private static MaterialPlan? PlanFor(IReadOnlyList<IReadOnlyList<MaterialPlan?>>? plan, int lod, int mat)
        => plan is not null && lod < plan.Count && plan[lod] is { } row && mat < row.Count ? row[mat] : null;

    /// <summary>
    /// Emit one material's vertex region, honouring the two layouts the format uses.
    /// <list type="bullet">
    /// <item><b>Interleaved</b> (32B, and 40B on BF1942): each vertex is one contiguous record, and the lightmap
    /// UV - when the stride leaves room for it - is the eight bytes at +32.</item>
    /// <item><b>Planar</b> (64B on BfVietnam): <c>nv</c> contiguous 32-byte pos/normal/uv records, and only THEN a
    /// separate <c>nv * 32</c> block of extras, of which the lightmap UV is the first eight bytes and the rest is
    /// the tangent frame. Reading or writing that as an interleaved stride is what once scrambled these meshes.</item>
    /// </list>
    /// </summary>
    private static void WriteVertices(BinaryWriter w, byte[] original, SmMaterial mat, MaterialPlan? plan)
    {
        int nvIn = mat.NumVertices;
        int clones = plan?.CloneSources.Length ?? 0;
        int nvOut = nvIn + clones;
        int srcStride = (int)mat.VertexByteSize;
        int outStride = (int)(plan?.Promote?.NewVertexByteSize ?? mat.VertexByteSize);
        int extra = outStride - 32;
        bool planar = srcStride == 64;
        int origin = mat.VertexDataOffset;
        var uv2 = plan?.Uv2 ?? Array.Empty<(float U, float V)>();
        bool writeUv = uv2.Length > 0 && extra >= 8;

        int Source(int v) => v < nvIn ? v : plan!.CloneSources[v - nvIn];

        if (!planar)
        {
            // A widened vertex keeps its first 32 bytes verbatim - position, normal and diffuse UV are copied,
            // never recomputed - and everything past the lightmap UV is zeroed rather than left as stale bytes.
            var rec = new byte[outStride];
            int copy = Math.Min(srcStride, outStride);
            for (int v = 0; v < nvOut; v++)
            {
                Array.Clear(rec);
                Buffer.BlockCopy(original, origin + Source(v) * srcStride, rec, 0, copy);
                if (writeUv && v < uv2.Length) PutUv(rec, 32, uv2[v]);
                w.Write(rec);
            }
            return;
        }

        // Planar: the two blocks are written one after the other, each in output-vertex order.
        var pos = new byte[32];
        for (int v = 0; v < nvOut; v++)
        {
            Buffer.BlockCopy(original, origin + Source(v) * 32, pos, 0, 32);
            w.Write(pos);
        }
        int extraBase = origin + nvIn * 32;
        var ext = new byte[extra];
        for (int v = 0; v < nvOut; v++)
        {
            Buffer.BlockCopy(original, extraBase + Source(v) * extra, ext, 0, extra);
            if (writeUv && v < uv2.Length) PutUv(ext, 0, uv2[v]);
            w.Write(ext);
        }
    }

    private static void PutUv(byte[] dst, int at, (float U, float V) uv)
    {
        BinaryPrimitives.WriteSingleLittleEndian(dst.AsSpan(at, 4), uv.U);
        BinaryPrimitives.WriteSingleLittleEndian(dst.AsSpan(at + 4, 4), uv.V);
    }
}
