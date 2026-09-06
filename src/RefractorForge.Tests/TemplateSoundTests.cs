using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Which placed objects the editor draws as sound emitters. Two rounds of narrowing, both from the shipped data:
/// <list type="bullet">
/// <item>It used to look through a template's children eight deep for anything carrying a <c>loadSoundScript</c>,
/// on the theory that a Bundle keeps "the sounding part" in a child. In BFVietnam 301 object .con files load a
/// script and nearly all are EFFECTS, so any building with a fire or a smoke plume beneath it got sound rings.</item>
/// <item>Then, on a BF1942 map, ordinary scenery was still drawn as emitters. A sound emitter means an AMBIENT -
/// <c>autoPlaySound 1</c> - and BF1942 does not use that flag at ALL: every one of its 428 sound-carrying
/// templates is a gun, an engine, a projectile, an effect or a turret. BFVietnam has 36 real ambients (the
/// generators, speakers, radios and flags) against 377 gameplay sounds.</item>
/// </list>
/// The editor's own screens are the exception: a look-at video screen leaves <c>autoPlaySound</c> out on purpose,
/// and it is declared by the level itself, so the mapper who made it still sees it.
/// </summary>
public class TemplateSoundTests
{
    private static string NewLevelDir(string name)
    {
        var d = Path.Combine(Path.GetTempPath(), "rf_snd_" + Guid.NewGuid().ToString("N")[..8], name);
        Directory.CreateDirectory(d);
        return d;
    }

    // A mod's objects sit at objects/<Category>/<Template>/ with no "levels" above them - a base-game template.
    // This has to be a real .rfa: RefractorFlatArchive.FromFolder prefixes every entry with levels/<folder>/,
    // because a loose folder source IS an extracted level, so a directory can never stand in for a base archive.
    private static string NewModArchive(string entry, string con)
    {
        var root = Path.Combine(Path.GetTempPath(), "rf_snd_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var rfa = Path.Combine(root, "Objects.rfa");
        RefractorForge.Formats.Rfa.RefractorFlatArchive.WriteFile(
            rfa, new[] { (entry, System.Text.Encoding.Latin1.GetBytes(con)) },
            compress: true, xPackId: RefractorForge.Formats.Rfa.XPackId.Default);
        return rfa;
    }

    // ...a level's own objects sit under levels/<Map>/objects/<Template>/, which is what the editor writes.
    private static string NewLevelLocalDir()
    {
        var root = Path.Combine(Path.GetTempPath(), "rf_snd_" + Guid.NewGuid().ToString("N")[..8]);
        var d = Path.Combine(root, "bf1942", "levels", "MyMap");
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Write(string dir, string rel, string text)
    {
        var p = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    private static void Cleanup(string levelDir)
    {
        // walk back up to the temp root this test made, whatever depth the layout put the level at
        var d = new DirectoryInfo(levelDir);
        while (d.Parent is not null && !d.Name.StartsWith("rf_snd_")) d = d.Parent;
        try { Directory.Delete(d.FullName, true); } catch { }
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
        finally { Cleanup(dir); }
    }

    [Fact]
    public void A_template_that_loads_its_own_script_still_is_one()
    {
        // What the branch exists for: the game's radios and generators declare loadSoundScript on the placed
        // template itself, with autoPlaySound, and those are the emitters worth drawing.
        var dir = NewLevelDir("MyMap");
        try
        {
            Write(dir, "objects/Radio/Objects.con", """
ObjectTemplate.create SimpleObject UsaRadio
ObjectTemplate.autoPlaySound 1
ObjectTemplate.loadSoundScript usaradio.ssc
""");
            var lib = MeshLibrary.Open(dir);
            var snd = lib.PlacedSoundOf("UsaRadio");
            Assert.NotNull(snd);
            Assert.Equal("usaradio.ssc", snd!.Value.Script);
            Assert.True(snd.Value.AutoPlay);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void A_base_game_sound_that_only_plays_while_drawn_is_not_an_emitter()
    {
        // The BF1942 complaint, in miniature. euwindmill_m1 and its kind carry a script with no autoPlaySound -
        // the engine plays it while the object is drawn - and every one of them was getting a ring and a
        // "(while looked at)" label on the map. They are gameplay sounds, not placed ambients.
        var rfa = NewModArchive("objects/buildings/Windmill/Objects.con", """
ObjectTemplate.create Bundle euwindmill_m1
ObjectTemplate.loadSoundScript windmill.ssc
""");
        try
        {
            var lib = MeshLibrary.Open(rfa);
            var snd = lib.SoundOf("euwindmill_m1");
            Assert.NotNull(snd);                                   // it does carry one...
            Assert.False(snd!.Value.AutoPlay);
            Assert.False(snd.Value.LevelLocal);
            Assert.Null(lib.PlacedSoundOf("euwindmill_m1"));       // ...but it is not something the mapper placed to hear
        }
        finally { Cleanup(Path.GetDirectoryName(rfa)!); }
    }

    [Fact]
    public void The_levels_own_look_at_screen_is_still_shown()
    {
        // The editor's video screens: no autoPlaySound on purpose, but declared by the level itself, so the
        // mapper who built it should still see it on the map.
        var dir = NewLevelLocalDir();
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
            Assert.True(snd.Value.LevelLocal);
            Assert.NotNull(lib.PlacedSoundOf("video17"));
        }
        finally { Cleanup(dir); }
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
            Assert.Null(lib.PlacedSoundOf("HueWall"));
        }
        finally { Cleanup(dir); }
    }
}
