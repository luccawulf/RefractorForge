using System;
using System.IO;
using RefractorForge.Formats;
using RefractorForge.Formats.Terrain;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The copy of the ground kept before the first bake - what "Revert ground to original" restores.
///
/// A light-pool bake multiplies into the terrain tiles and SATURATES where the pool is brightest, so dividing the
/// same pool back out cannot return the texel it clipped; after a save there was nothing left to divide out at all.
/// A copy is the only exact way back, which puts two things under test: that the copy round-trips without losing a
/// single texel, and that it never reaches the game.
/// </summary>
public class GroundOriginalTests
{
    private static Texture2D Noise(int size)
    {
        // Deterministic, and deliberately full-range: a lossy store would show up first at the extremes.
        var px = new byte[size * size * 4];
        for (int i = 0; i < size * size; i++)
        {
            px[i * 4 + 0] = (byte)(i * 7 & 0xFF);
            px[i * 4 + 1] = (byte)(i * 13 & 0xFF);
            px[i * 4 + 2] = (byte)(i * 29 & 0xFF);
            px[i * 4 + 3] = 255;
        }
        return new Texture2D(size, size, px);
    }

    [Fact]
    public void The_kept_ground_round_trips_texel_for_texel()
    {
        // The whole promise of Revert is "exactly as it was", so anything lossy here - a DXT store, a resize - would
        // hand back a ground that is merely close.
        var ground = Noise(64);
        var path = Path.Combine(Path.GetTempPath(), "rf_ground_" + Guid.NewGuid().ToString("N") + ".dds");
        try
        {
            DdsTexture.Save(ground, path);
            var back = DdsTexture.Load(path);

            Assert.Equal(ground.Width, back.Width);
            Assert.Equal(ground.Height, back.Height);
            for (int i = 0; i < ground.Width * ground.Height; i++)
            {
                Assert.Equal(ground.Rgba[i * 4 + 0], back.Rgba[i * 4 + 0]);
                Assert.Equal(ground.Rgba[i * 4 + 1], back.Rgba[i * 4 + 1]);
                Assert.Equal(ground.Rgba[i * 4 + 2], back.Rgba[i * 4 + 2]);
            }
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void A_packed_level_keeps_the_copy_beside_the_archive_never_inside_it()
    {
        // You cannot write a file into a .rfa, and treating the archive as a folder is how an earlier sidecar
        // managed to call CreateDirectory on a FILE and stop a packed level saving at all.
        var rfa = Path.Combine(Path.GetTempPath(), "rf_levels", "al_vietnas.rfa");
        var p = GroundOriginal.PathFor(rfa);

        Assert.Equal(Path.GetDirectoryName(rfa), Path.GetDirectoryName(p));
        Assert.Equal("al_vietnas." + GroundOriginal.FileName, Path.GetFileName(p));
        Assert.DoesNotContain(rfa + Path.DirectorySeparatorChar, p);
    }

    [Fact]
    public void A_folder_level_keeps_the_copy_in_the_folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rf_levels", "Al_Vietnas");
        Assert.Equal(Path.Combine(dir, GroundOriginal.FileName), GroundOriginal.PathFor(dir));
    }

    [Theory]
    [InlineData("RF_GroundOriginal.dds")]                 // a folder level's copy
    [InlineData("al_vietnas.RF_GroundOriginal.dds")]      // a packed level's, beside the archive
    [InlineData("Textures/RF_GroundOriginal.dds")]
    public void The_kept_ground_is_never_packed(string rel)
    {
        // It is a whole second copy of the terrain art. Packed, it would bloat the level and put a file the engine
        // never asked for inside the level path.
        Assert.True(LevelSaver.IsEditorOnlyFile(rel), rel + " would be packed into the level");
    }

    [Fact]
    public void The_levels_own_textures_are_still_packed()
    {
        // The guard above must not be so broad that it swallows the ground tiles themselves.
        Assert.False(LevelSaver.IsEditorOnlyFile("Textures/tx00x00.dds"));
        Assert.False(LevelSaver.IsEditorOnlyFile("Textures/RF_GroundShadowMerge.tga"));   // the merge record IS a level file
    }
}
