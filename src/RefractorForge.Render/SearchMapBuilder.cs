using RefractorForge.Formats.Con;
using RefractorForge.Formats.Terrain;

namespace RefractorForge.Render;

/// <summary>
/// Bridges the object library to the AI navmap generator. Turns placed <see cref="StaticObject"/>s into
/// <see cref="ObjectFootprint"/>s (a blocking disc + height, from the resolved mesh's bounds), so
/// <see cref="SearchMapGenerator"/> can carve buildings/props out of the ground-vehicle navmaps.
/// </summary>
public static class SearchMapBuilder
{
    /// <summary>Resolve each object to a world-space footprint; objects whose mesh can't be resolved are skipped.</summary>
    public static List<ObjectFootprint> Footprints(IEnumerable<StaticObject> objects, MeshLibrary lib)
    {
        var list = new List<ObjectFootprint>();
        foreach (var o in objects)
        {
            if (!lib.TryGet(o.Template, out var m) && !lib.TryGetAssembledMesh(o.Template, out m)) continue;
            if (m.Positions.Length == 0) continue;
            float minx = float.MaxValue, maxx = float.MinValue, miny = float.MaxValue, maxy = float.MinValue, minz = float.MaxValue, maxz = float.MinValue;
            foreach (var p in m.Positions)
            {
                if (p.X < minx) minx = p.X; if (p.X > maxx) maxx = p.X;
                if (p.Y < miny) miny = p.Y; if (p.Y > maxy) maxy = p.Y;
                if (p.Z < minz) minz = p.Z; if (p.Z > maxz) maxz = p.Z;
            }
            float scale = o.Scale ?? 1f;
            float rad = MathF.Max(maxx - minx, maxz - minz) * 0.5f * scale;
            float hgt = (maxy - miny) * scale;
            list.Add(new ObjectFootprint(o.Position.X, o.Position.Z, rad, hgt));
        }
        return list;
    }

    /// <summary>Generate + write all navmaps for a level, including object footprints from the library.</summary>
    public static int WriteFolder(string levelDir, TerrainConfig cfg, Heightmap hm,
                                  IEnumerable<StaticObject> objects, MeshLibrary lib)
        => SearchMapGenerator.WriteFolder(levelDir, cfg, hm, Footprints(objects, lib));
}
