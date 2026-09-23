using System.Globalization;
using System.Text;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Mesh;
using RefractorForge.Formats.Rfa;

namespace RefractorBridge.Mesh;

/// <summary>What converting one .tm produced.</summary>
public sealed record TmConversion(
    byte[] StandardMesh,
    string RsText,
    IReadOnlyList<string> MaterialNames,
    IReadOnlyList<string> Textures,
    int Vertices,
    int Triangles,
    int SpriteMaterialsDropped,
    int SpriteTrianglesDropped,
    bool CollisionCarried);

/// <summary>
/// BF1942 TreeMesh (<c>.tm</c>) -> Battlefield Vietnam StandardMesh (<c>.sm</c> + <c>.rs</c>).
///
/// THE single biggest structural difference between the two engines. BfVietnam.exe registers
/// <c>AnimatedMesh, ParticleSystemTemplate, PatchTerrain, RoamTerrain, SimpleGeom, SkeletonCollisionMesh,
/// StandardMesh</c> - and no TreeMesh - so every <c>.tm</c> in a BF1942 mod is an object that cannot load at
/// all. Censusing 20 mods found this is not a rounding error: Forgotten Hope declares 190 TreeMesh geometries,
/// FHSW 185, Pirates of the Reich and WWII Reality 112 each, Interstate 109.
///
/// The conversion is a REGROUPING, not a resample - same vertices, same triangles, reorganised into the
/// sections a StandardMesh wants:
/// <list type="bullet">
/// <item>A .tm holds exactly four material groups (0 leaf, 1 trunk, 2 sprite, 3 extra) indexing one shared
/// vertex and index buffer. Each becomes a StandardMesh material section.</item>
/// <item><b>Group 2 (sprites) is dropped.</b> Those are the billboard imposters BF1942 swaps in at distance,
/// and BFV has no shader for them - kept, they would draw as flat untextured quads.</item>
/// <item>The collision hull is carried across. Trees are solid in game, and the hull is a separate block from
/// the render geometry (it sinks below the visible box to anchor the trunk), so dropping it would turn every
/// converted tree into something you walk through.</item>
/// </list>
///
/// The material is modelled on BATTLEFIELD VIETNAM'S OWN vegetation rather than invented - C01F_Trees_M1.rs
/// and C01F_bush_M1.rs out of the retail standardMesh.rfa. Note what they actually do, because it is the
/// opposite of the obvious guess: <c>transparent false</c> WITH <c>alphatestref</c>, not <c>transparent
/// true</c>. Alpha-blending a leaf card is what makes ported foliage look like glass. <c>selfillum</c> keeps
/// leaves from going black in shadow. And every property line ends in a semicolon - a missing one loads fine on
/// a dedicated server (no renderer, never parses materials) and crashes the client partway through loading.
/// </summary>
public static class TmToSm
{
    /// <summary>Retail BFV uses 0.5 on trees and 0.4 on bushes; 0.5 is the safe default for a leaf cutout.</summary>
    public const float DefaultAlphaTestRef = 0.5f;

    /// <summary>Index of the sprite/billboard group inside a .tm's four material groups.</summary>
    private const int SpriteGroup = 2;

    /// <summary>
    /// True when a .tm has nothing but billboard imposters - no leaf, trunk or extra geometry at all. BF1942
    /// ships a handful of these (jungle_bush_M1, Jungle_tree15c_M1), and they are not convertible in any useful
    /// sense: with the sprite group dropped there is no geometry left. They need a BFV vegetation substitute or
    /// to be dropped, which is a content decision, not a conversion.
    /// </summary>
    public static bool IsBillboardOnly(byte[] tmBytes)
    {
        var tm = TreeMesh.Parse(tmBytes);
        for (int g = 0; g < tm.Groups.Length; g++)
        {
            if (g == SpriteGroup) continue;
            foreach (var m in tm.Groups[g]) if (m.Count > 0) return false;
        }
        return true;
    }

    public static TmConversion Convert(
        byte[] tmBytes,
        string meshName,
        float alphaTestRef = DefaultAlphaTestRef,
        bool reverseWinding = false)
    {
        var tm = TreeMesh.Parse(tmBytes);

        var subs = new List<ObjSubMesh>();
        var names = new List<string>();
        var textures = new List<string>();
        int spriteMats = 0, spriteTris = 0;

        for (int g = 0; g < tm.Groups.Length; g++)
        {
            foreach (var mat in tm.Groups[g])
            {
                if (mat.Count <= 0) continue;

                if (g == SpriteGroup)
                {
                    spriteMats++;
                    spriteTris += mat.Count;
                    continue;
                }

                var sub = BuildSection(tm, mat, $"{meshName}_Material{subs.Count}");
                if (sub.Faces.Count == 0) continue;

                subs.Add(sub);
                names.Add(sub.Material);
                textures.Add(CleanTextureName(mat.TexName));
            }
        }

        if (subs.Count == 0)
            throw new InvalidDataException(
                $"'{meshName}' has no drawable geometry outside the sprite group - there is nothing to convert.");

        var mesh = ObjMesh.FromSubMeshes(subs);
        mesh.BoundingBox[0] = tm.Min.X; mesh.BoundingBox[1] = tm.Min.Y; mesh.BoundingBox[2] = tm.Min.Z;
        mesh.BoundingBox[3] = tm.Max.X; mesh.BoundingBox[4] = tm.Max.Y; mesh.BoundingBox[5] = tm.Max.Z;
        if (reverseWinding) mesh.ReverseWinding();

        byte[]? collision = BuildCollision(tm);

        return new TmConversion(
            StandardMeshWriter.Write(mesh, collision),
            BuildRs(names, textures, alphaTestRef),
            names,
            textures,
            mesh.TotalVertices,
            mesh.TotalFaces,
            spriteMats,
            spriteTris,
            collision is { Length: > 0 });
    }

    /// <summary>
    /// One .tm material becomes one StandardMesh section. The .tm's materials all index ONE shared vertex
    /// buffer, while a StandardMesh section owns its vertices, so each section is remapped to just the vertices
    /// its own triangles touch.
    /// </summary>
    private static ObjSubMesh BuildSection(TreeMesh tm, TreeMesh.Material mat, string materialName)
    {
        var sub = new ObjSubMesh { Material = materialName, Object = materialName };
        var remap = new Dictionary<ushort, int>();

        int Local(ushort global)
        {
            if (remap.TryGetValue(global, out int local)) return local;

            var v = tm.Vertices[global];
            local = sub.Positions.Count;
            sub.Positions.Add(new Vec3(v.Px, v.Py, v.Pz));
            sub.Normals.Add(new Vec3(v.Nx, v.Ny, v.Nz));
            sub.Uvs.Add((v.U, v.V));
            remap[global] = local;
            return local;
        }

        // "Render each material group as a triangle list over index[start .. start + count*3)".
        int end = mat.Start + mat.Count * 3;
        for (int i = mat.Start; i + 2 < end && i + 2 < tm.Indices.Length; i += 3)
            sub.Faces.Add((Local(tm.Indices[i]), Local(tm.Indices[i + 1]), Local(tm.Indices[i + 2])));

        return sub;
    }

    private static byte[]? BuildCollision(TreeMesh tm)
    {
        if (tm.CollisionVertices.Length == 0 || tm.CollisionIndices.Length < 3) return null;

        var tris = new List<(int A, int B, int C)>(tm.CollisionIndices.Length / 3);
        for (int i = 0; i + 2 < tm.CollisionIndices.Length; i += 3)
            tris.Add((tm.CollisionIndices[i], tm.CollisionIndices[i + 1], tm.CollisionIndices[i + 2]));

        return tris.Count == 0 ? null : StandardMeshWriter.BuildCollisionSection(tm.CollisionVertices, tris);
    }

    /// <summary>
    /// The material script, shaped like Battlefield Vietnam's own foliage. A StandardMesh binds each section to
    /// a material BY NAME, so every name written into the .sm must appear here or that surface renders
    /// untextured - which is not an error the engine reports.
    /// </summary>
    private static string BuildRs(IReadOnlyList<string> names, IReadOnlyList<string> textures, float alphaTestRef)
    {
        var sb = new StringBuilder();
        string aref = alphaTestRef.ToString("0.###", CultureInfo.InvariantCulture);

        for (int i = 0; i < names.Count; i++)
        {
            sb.Append("subshader \"").Append(names[i]).Append("\" \"StandardMesh/Default\"\r\n");
            sb.Append("{\r\n");
            sb.Append("\tlighting true;\r\n");
            sb.Append("\tlightingSpecular true;\r\n");
            // transparent FALSE with an alpha test - retail BFV vegetation's own combination. Alpha-blending a
            // leaf card instead is what makes ported foliage look like glass.
            sb.Append("\ttransparent false;\r\n");
            sb.Append("\talphatestref ").Append(aref).Append(";\r\n");
            sb.Append("\tmaterialDiffuse .6 .6 .6;\r\n");
            sb.Append("\tmaterialAmbient .4 .4 .4;\r\n");
            sb.Append("\tselfillum .3 .3 .3;\r\n");
            if (textures[i].Length > 0)
                sb.Append("\ttexture \"texture/").Append(textures[i]).Append("\";\r\n");
            sb.Append("}\r\n\r\n");
        }

        return sb.ToString();
    }

    /// <summary>A .tm names its texture however the exporter felt; the .rs wants a bare archive-root name.</summary>
    private static string CleanTextureName(string raw)
    {
        string s = raw.Trim().Replace('\\', '/');
        int slash = s.LastIndexOf('/');
        if (slash >= 0) s = s[(slash + 1)..];
        int dot = s.LastIndexOf('.');
        if (dot > 0) s = s[..dot];
        return s;
    }
}
