using RefractorForge.Formats.Con;
using RefractorForge.Formats.Terrain;

namespace RefractorForge.Formats;

/// <summary>
/// The in-memory representation of a map. Deliberately the opposite of Battlecraft's
/// monolithic fixed-size struct: each part is an independent object with no embedded
/// caps. WorldSize is a plain int, the object list is unbounded, terrain resolution is
/// whatever the heightmap says.
/// </summary>
public sealed class Level
{
    public string Name { get; set; } = "untitled";

    /// <summary>World size in meters. No enforced ceiling (no 4096 wall).</summary>
    public int WorldSize { get; set; } = 4096;

    /// <summary>Authoritative terrain parameters (from Terrain.con), when known.</summary>
    public TerrainConfig? Terrain { get; set; }

    public Heightmap? Heightmap { get; set; }

    public StaticObjectsFile StaticObjects { get; } = new();
}
