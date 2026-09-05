using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Which placed objects the editor treats as sound emitters. This used to look through a template's children eight
/// deep for anything carrying a <c>loadSoundScript</c>, on the theory that a Bundle keeps "the sounding part" in a
/// child. In BFVietnam's own data 301 object .con files load a sound script and nearly all of them are EFFECTS - so
/// any ordinary building with a fire, a smoke plume or a generator effect anywhere beneath it was drawn with sound
/// rings and a volume readout, which buried the handful of objects that really do emit.
/// </summary>
public class TemplateSoundTests
{
    private static string NewLevelDir(string name)
    {
        var d = Path.Combine(Path.GetTempPath(), "rf_snd_" + Guid.NewGuid().ToString("N")[..8], name);
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Write(string dir, string rel, string text)
    {
        var p = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    [Fact]
    public void A_building_with_a_sounding_effect_child_is_not_a_sound_emitter()
    {
        var dir = NewLevelDir("MyMap");
        try
        {
            Write(dir, "objects/Hut/Objects.con", """
ObjectTemplate.create Bundle Hut
ObjectTemplate.addTemplate HutFire
ObjectTemplate.create Effect HutFire
ObjectTemplate.loadSoundScript fire.ssc
ObjectTemplate.autoPlaySound 1
""");
            var lib = MeshLibrary.Open(dir);
            Assert.Null(lib.SoundOf("Hut"));
            Assert.NotNull(lib.SoundOf("HutFire"));    // the effect itself still carries one
        }
        finally { Directory.Delete(Path.GetDirectoryName(dir)!, true); }
    }

    [Fact]
    public void A_template_that_loads_its_own_script_still_is_one()
    {
        // What the branch exists for: the editor's video screens, and the game's radios and generators, declare
        // loadSoundScript on the placed template itself.
        var dir = NewLevelDir("MyMap");
        try
        {
            Write(dir, "objects/Radio/Objects.con", """
ObjectTemplate.create SimpleObject UsaRadio
ObjectTemplate.autoPlaySound 1
ObjectTemplate.loadSoundScript usaradio.ssc
""");
            var lib = MeshLibrary.Open(dir);
            var snd = lib.SoundOf("UsaRadio");
            Assert.NotNull(snd);
            Assert.Equal("usaradio.ssc", snd!.Value.Script);
            Assert.True(snd.Value.AutoPlay);
        }
        finally { Directory.Delete(Path.GetDirectoryName(dir)!, true); }
    }

    [Fact]
    public void A_screen_whose_sound_only_plays_while_it_is_drawn_reads_as_not_autoplay()
    {
        var dir = NewLevelDir("MyMap");
        try
        {
            Write(dir, "objects/Screen/Objects.con", """
ObjectTemplate.create SimpleObject video17
ObjectTemplate.loadSoundScript video17.ssc
""");
            var lib = MeshLibrary.Open(dir);
            var snd = lib.SoundOf("video17");
            Assert.NotNull(snd);
            Assert.False(snd!.Value.AutoPlay);
        }
        finally { Directory.Delete(Path.GetDirectoryName(dir)!, true); }
    }

    [Fact]
    public void An_object_with_no_sound_anywhere_is_not_one()
    {
        var dir = NewLevelDir("MyMap");
        try
        {
            Write(dir, "objects/Wall/Objects.con", "ObjectTemplate.create Bundle HueWall");
            var lib = MeshLibrary.Open(dir);
            Assert.Null(lib.SoundOf("HueWall"));
            Assert.Null(lib.SoundOf("NoSuchTemplate"));
        }
        finally { Directory.Delete(Path.GetDirectoryName(dir)!, true); }
    }
}
