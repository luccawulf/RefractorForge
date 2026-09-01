using System.Text;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Mesh;
using RefractorForge.Formats.Rfa;

namespace RefractorForge.Formats.Con;

/// <summary>
/// A picture you can place: a flat mesh carrying a texture of your own, registered as a level-local object so
/// it ships inside the map and needs nothing from the mod.
///
/// Refractor has no decal primitive; posters, signs and scorch marks in retail maps are ordinary SimpleObjects
/// with a quad mesh. This builds one the way Easter Island's Sign_Credits is built - which is the shape the
/// engine is known to load:
///
///   Objects/&lt;Name&gt;/Objects.con      ObjectTemplate.create SimpleObject + geometry + no collision
///   Objects/&lt;Name&gt;/Geometries.con   GeometryTemplate StandardMesh -> ../bf1942/levels/&lt;Level&gt;/StandardMesh/&lt;Name&gt;
///   Objects/objects.con              run &lt;Name&gt;/&lt;Name&gt;   (one line per object; created if absent)
///   Objects/&lt;Name&gt;/&lt;Name&gt;.con        run Objects / run Geometries
///   StandardMesh/&lt;Name&gt;.sm + .rs      the quad and the shader binding its material to the texture
///   Texture/&lt;texture&gt;.dds            the picture, found via textureManager.alternativePath in Init.con
///
/// The quad stands upright facing -Z with its origin at the bottom centre, so it drops onto the ground like a
/// sign and rotates about its base.
/// </summary>
public static class DecalObject
{
    public sealed record Built(string Template, List<(string RelPath, byte[] Bytes)> Files, string RunLine, ObjMesh Mesh);

    /// <param name="levelName">The level folder name, as it appears under bf1942/levels/.</param>
    /// <param name="name">Template name (letters, digits, underscore).</param>
    /// <param name="widthMeters">Quad width.</param>
    /// <param name="heightMeters">Quad height.</param>
    /// <param name="textureName">Texture file name without extension, written to Texture/.</param>
    /// <param name="ddsBytes">The texture, already encoded as DDS.</param>
    /// <param name="flat">Lay it flat on the ground (a scorch mark) instead of standing it up (a poster).</param>
    /// <param name="doubleSided">Emit a second, reversed quad so it is visible from behind.</param>
    public static Built Build(string levelName, string name, float widthMeters, float heightMeters,
                              string textureName, byte[] ddsBytes, bool flat = false, bool doubleSided = true)
    {
        name = Sanitize(name);
        textureName = Sanitize(textureName);
        var mesh = Quad(widthMeters, heightMeters, textureName, flat, doubleSided);

        var files = new List<(string, byte[])>();
        var crlf = new UTF8Encoding(false);

        files.Add(($"StandardMesh/{name}.sm", StandardMeshWriter.Write(mesh)));

        // The .rs binds the material to the texture; alpha is honoured so a poster can have cut-out edges.
        string rs = $"subshader \"{textureName}\" \"StandardMesh/Default\"\r\n{{\r\n\ttexture \"{textureName}\"\r\n" +
                    "\tmaterialDiffuse 1 1 1\r\n\ttransparent\r\n\talphaTestRef 0.5\r\n\ttwosided\r\n}\r\n";
        files.Add(($"StandardMesh/{name}.rs", crlf.GetBytes(rs)));
        files.Add(($"Texture/{textureName}.dds", ddsBytes));

        string geom =
            $"GeometryTemplate.create StandardMesh {name}\r\n" +
            $"GeometryTemplate.file ../bf1942/levels/{levelName}/StandardMesh/{name}\r\n" +
            "GeometryTemplate.setLodDistance 0 0\r\n" +
            "GeometryTemplate.setLodDistance 1 200\r\n\r\n";
        files.Add(($"Objects/{name}/Geometries.con", crlf.GetBytes(geom)));

        string obj =
            $"ObjectTemplate.create SimpleObject {name}\r\n" +
            $"ObjectTemplate.geometry {name}\r\n" +
            "ObjectTemplate.setHasCollisionPhysics 0\r\n\r\n";
        files.Add(($"Objects/{name}/Objects.con", crlf.GetBytes(obj)));

        files.Add(($"Objects/{name}/{name}.con", crlf.GetBytes("run Objects\r\nrun Geometries\r\n")));

        return new Built(name, files, $"run {name}/{name}", mesh);
    }

    /// <summary>Add the object's run line to Objects/objects.con, creating the file if the level has none.</summary>
    public static string PatchObjectsCon(string? existing, string runLine)
    {
        var lines = (existing ?? "").Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Any(l => l.Trim().Equals(runLine, StringComparison.OrdinalIgnoreCase))) return string.Join("\r\n", lines);
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        lines.Add(runLine);
        return string.Join("\r\n", lines) + "\r\n";
    }

    /// <summary>
    /// Make sure Init.con runs the level's Objects folder and looks in its Texture folder. Both lines are what
    /// retail levels with local objects carry; adding them when absent is what makes a decal appear at all.
    /// </summary>
    public static string PatchInitCon(string existing, string levelName)
    {
        var lines = existing.Replace("\r\n", "\n").Split('\n').ToList();
        bool hasRun = lines.Any(l => l.Trim().Equals("run Objects/Objects", StringComparison.OrdinalIgnoreCase)
                                  || l.Trim().Equals("run Objects/objects", StringComparison.OrdinalIgnoreCase));
        string texLine = $"textureManager.alternativePath bf1942/levels/{levelName}/Texture";
        bool hasTex = lines.Any(l => l.Trim().Equals(texLine, StringComparison.OrdinalIgnoreCase));
        if (hasRun && hasTex) return existing;

        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        lines.Add("");
        lines.Add("rem RefractorForge level-local objects");
        if (!hasTex) lines.Add(texLine);
        if (!hasRun) lines.Add("run Objects/Objects");
        return string.Join("\r\n", lines) + "\r\n";
    }

    private static ObjMesh Quad(float w, float h, string material, bool flat, bool doubleSided)
    {
        var mesh = new ObjMesh();
        var sub = new ObjSubMesh { Material = material };
        float hw = w * 0.5f;

        Vec3[] pos; Vec3 n;
        if (flat)
        {
            // On the ground, facing up, origin at its centre.
            float hh = h * 0.5f;
            pos = new[] { new Vec3(-hw, 0.02f, -hh), new Vec3(hw, 0.02f, -hh), new Vec3(hw, 0.02f, hh), new Vec3(-hw, 0.02f, hh) };
            n = new Vec3(0, 1, 0);
        }
        else
        {
            // Upright, facing -Z, origin at bottom centre.
            pos = new[] { new Vec3(-hw, 0f, 0f), new Vec3(hw, 0f, 0f), new Vec3(hw, h, 0f), new Vec3(-hw, h, 0f) };
            n = new Vec3(0, 0, -1);
        }
        (float, float)[] uv = { (0f, 1f), (1f, 1f), (1f, 0f), (0f, 0f) };

        void AddQuad(bool reverse)
        {
            int b = sub.Positions.Count;
            for (int i = 0; i < 4; i++)
            {
                sub.Positions.Add(pos[i]);
                sub.Normals.Add(reverse ? new Vec3(-n.X, -n.Y, -n.Z) : n);
                sub.Uvs.Add(uv[i]);
            }
            if (!reverse) { sub.Faces.Add((b, b + 1, b + 2)); sub.Faces.Add((b, b + 2, b + 3)); }
            else { sub.Faces.Add((b, b + 2, b + 1)); sub.Faces.Add((b, b + 3, b + 2)); }
        }
        AddQuad(false);
        if (doubleSided) AddQuad(true);

        mesh.SubMeshes.Add(sub);
        float minX = pos.Min(p => p.X), maxX = pos.Max(p => p.X);
        float minY = pos.Min(p => p.Y), maxY = pos.Max(p => p.Y);
        float minZ = pos.Min(p => p.Z), maxZ = pos.Max(p => p.Z);
        mesh.BoundingBox[0] = minX; mesh.BoundingBox[1] = minY; mesh.BoundingBox[2] = minZ;
        mesh.BoundingBox[3] = maxX; mesh.BoundingBox[4] = maxY; mesh.BoundingBox[5] = maxZ;
        return mesh;
    }

    public static string Sanitize(string raw)
    {
        var sb = new StringBuilder();
        foreach (var ch in raw) sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        var s = sb.ToString().Trim('_');
        if (s.Length == 0) s = "decal";
        if (char.IsDigit(s[0])) s = "d_" + s;
        return s;
    }
}
