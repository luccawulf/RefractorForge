using System;
using System.IO;
using RefractorForge.Formats;
using RefractorForge.Formats.Editing;
using RefractorForge.Formats.Terrain;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The editor's own sidecars - placed lights (and the night amount), object groups, review notes - have to work for
/// a PACKED level as well as a folder one.
///
/// The bug these come from: picking a night preset set the rig's night amount, which made the rig non-empty, which
/// made the next save write it with <c>Directory.CreateDirectory(levelDir)</c> - and for a packed level `levelDir`
/// is the .rfa FILE. The IOException escaped the save and was swallowed, so the level simply never saved and said
/// nothing about it. A packed level now keeps its sidecars beside the archive.
/// </summary>
public class LevelSidecarTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "rf_sidecar_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void NightPresetOnAPackedLevelSavesInsteadOfThrowing()
    {
        var dir = TempDir();
        try
        {
            var rfa = Path.Combine(dir, "al_vietnas.rfa");
            File.WriteAllBytes(rfa, new byte[16]);

            // Exactly what ApplyTimeOfDay does for a night preset.
            var rig = new LightRig { NightAmount = 0.8f };
            rig.Save(rfa);                                  // this is what used to throw

            Assert.True(File.Exists(Path.Combine(dir, "al_vietnas.RefractorForgeLights.json")));
            Assert.True(File.Exists(rfa));                  // and the archive is untouched
            Assert.Equal(0.8f, LightRig.Load(rfa).NightAmount, 3);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void GroupsAndNotesRoundTripBesideAPackedLevel()
    {
        var dir = TempDir();
        try
        {
            var rfa = Path.Combine(dir, "Operation_Irving.rfa");
            File.WriteAllBytes(rfa, new byte[16]);

            var groups = new ObjectGroups();
            groups.Create("bunkers").Ids.Add("b12");
            groups.Save(rfa);

            var notes = new Annotations();
            notes.Notes.Add(new Annotation { Text = "this wall is see-through" });
            notes.Save(rfa);

            Assert.Single(ObjectGroups.Load(rfa).Groups);
            Assert.Single(Annotations.Load(rfa).Notes);
            Assert.Equal("bunkers", ObjectGroups.Load(rfa).Groups[0].Name);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AFolderLevelStillKeepsItsSidecarsInTheFolder()
    {
        var dir = TempDir();
        try
        {
            var levelDir = Path.Combine(dir, "al_vietnas");
            Directory.CreateDirectory(levelDir);

            new LightRig { NightAmount = 0.5f }.Save(levelDir);

            Assert.True(File.Exists(Path.Combine(levelDir, LightRig.FileName)));
            Assert.Equal(0.5f, LightRig.Load(levelDir).NightAmount, 3);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AnEmptyRigLeavesNoSidecarBehind()
    {
        var dir = TempDir();
        try
        {
            var rfa = Path.Combine(dir, "map.rfa");
            File.WriteAllBytes(rfa, new byte[16]);
            var p = LightRig.PathFor(rfa);

            new LightRig { NightAmount = 0.4f }.Save(rfa);
            Assert.True(File.Exists(p));

            new LightRig().Save(rfa);                       // back to daylight with no lights placed
            Assert.False(File.Exists(p));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void APackedLevelsSidecarsAreNeverPacked()
    {
        // Both spellings: the folder form, and the <level>.<name> form a packed level uses.
        Assert.True(LevelSaver.IsEditorOnlyFile(LightRig.FileName));
        Assert.True(LevelSaver.IsEditorOnlyFile("al_vietnas." + LightRig.FileName));
        Assert.True(LevelSaver.IsEditorOnlyFile("al_vietnas." + ObjectGroups.FileName));
        Assert.True(LevelSaver.IsEditorOnlyFile("al_vietnas." + Annotations.FileName));
        Assert.True(LevelSaver.IsEditorOnlyFile("Levels/al_vietnas/al_vietnas." + Annotations.FileName));

        // and nothing the engine reads is caught by the new name test
        Assert.False(LevelSaver.IsEditorOnlyFile("Init.con"));
        Assert.False(LevelSaver.IsEditorOnlyFile("al_vietnas.StaticObjects.con"));
    }

    [Fact]
    public void SidecarPathsFollowTheLevelKind()
    {
        var dir = TempDir();
        try
        {
            var rfa = Path.Combine(dir, "map.rfa");
            File.WriteAllBytes(rfa, new byte[4]);
            Assert.Equal(Path.Combine(dir, "map." + LightRig.FileName), LightRig.PathFor(rfa));

            var folder = Path.Combine(dir, "map_folder");
            Directory.CreateDirectory(folder);
            Assert.Equal(Path.Combine(folder, LightRig.FileName), LightRig.PathFor(folder));

            // A folder that does not exist yet is still a folder, not an archive - no extension.
            var future = Path.Combine(dir, "not_made_yet");
            Assert.Equal(Path.Combine(future, LightRig.FileName), LightRig.PathFor(future));
        }
        finally { Directory.Delete(dir, true); }
    }
}
