namespace RefractorForge.Formats.Terrain;

/// <summary>
/// Where the editor keeps the ground texture as it was BEFORE any bake burned light into it.
///
/// The terrain tiles are the only copy of the ground art, and a light-pool bake multiplies into them
/// destructively - the pool saturates where it is brightest, so dividing the same pool back out cannot
/// return the texel it clipped. The undo stack covers it inside one session and nothing covers it after a
/// save, which is how a bake became a one-way door.
///
/// So the atlas is copied once, uncompressed, the first time a bake is about to touch it, and "revert to
/// original" restores those exact bytes however many bakes and sessions later. It is deliberately a SIDECAR
/// rather than a level file: a packed level keeps it beside the archive, a folder level keeps it in the
/// folder but <see cref="LevelSaver.IsEditorOnlyFile"/> keeps it out of every pack, so the game never sees
/// a second copy of the ground.
/// </summary>
public static class GroundOriginal
{
    public const string FileName = "RF_GroundOriginal.dds";

    /// <summary>The snapshot's path for a level that is either a folder or a packed archive.</summary>
    public static string PathFor(string levelDir) => LevelSidecar.PathFor(levelDir, FileName);
}
