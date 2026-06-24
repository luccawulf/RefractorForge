using RefractorForge.Formats.Sound;
using Xunit;

namespace RefractorForge.Tests;

public class SoundTests
{
    [Fact]
    public void SoundScript_parse_edit_roundtrip()
    {
        string ssc = "#templateLevel HIGH\r\n\r\nnewPatch\r\n\r\nstream @ROOT/Sound/@RTD/frogs_1.wav\r\nloop\r\nminDistance 10\r\nvolume 1\r\n\r\nbeginEffect\r\n\tcontrolDestination Volume\r\n\tcontrolSource Distance\r\nendEffect\r\n";
        var s = SoundScript.Parse(ssc);
        Assert.True(s.ToText() == ssc, "unedited .ssc round-trips byte-exact");
        Assert.True(s.Wav == "@ROOT/Sound/@RTD/frogs_1.wav" && s.SourceMode == "stream", "reads wav + source mode");
        Assert.True(Math.Abs(s.Volume - 1f) < 1e-4 && Math.Abs(s.MinDistance - 10f) < 1e-4, "reads volume + minDistance");
        Assert.True(s.Loop && !s.Stereo, "reads loop flag");

        s.SetVolume(0.5f); s.SetMinDistance(25f); s.SetLoop(false); s.SetStereo(true); s.SetWav("@ROOT/Sound/@RTD/frogs_2.wav");
        var s2 = SoundScript.Parse(s.ToText());
        Assert.True(Math.Abs(s2.Volume - 0.5f) < 1e-4, $"volume edit persists ({s2.Volume})");
        Assert.True(Math.Abs(s2.MinDistance - 25f) < 1e-4, $"minDistance edit persists ({s2.MinDistance})");
        Assert.True(!s2.Loop, "loop turned off");
        Assert.True(s2.Stereo, "stereo turned on");
        Assert.True(s2.Wav == "@ROOT/Sound/@RTD/frogs_2.wav", "wav swapped");
        Assert.True(s.ToText().Contains("beginEffect") && s.ToText().Contains("controlSource Distance"), "effect block preserved");

        var noVol = SoundScript.Parse("newPatch\r\nload sound.wav\r\nminDistance 5\r\n");
        Assert.True(Math.Abs(noVol.Volume - 1f) < 1e-4, "missing volume defaults to 1");
        noVol.SetVolume(0.3f);
        Assert.True(Math.Abs(SoundScript.Parse(noVol.ToText()).Volume - 0.3f) < 1e-4, "SetVolume inserts line when absent");
    }

    [Fact]
    public void SoundLibrary_folder_save_roundtrip()
    {
        string tmpLvl = Path.Combine(Path.GetTempPath(), "rf_soundedit_" + Guid.NewGuid().ToString("N")[..8]);
        string tmpSnd = Path.Combine(tmpLvl, "Sounds");
        Directory.CreateDirectory(tmpSnd);
        try
        {
            File.WriteAllText(Path.Combine(tmpSnd, "Frogs.con"),
                "ObjectTemplate.create SimpleObject Frogs\r\nObjectTemplate.saveInSeparateFile 1\r\nObjectTemplate.loadSoundScript Frogs.ssc\r\n");
            File.WriteAllText(Path.Combine(tmpSnd, "Frogs.ssc"),
                "newPatch\r\nstream @ROOT/Sound/@RTD/frogs_1.wav\r\nloop\r\nminDistance 10\r\nvolume 1\r\n");
            var lib0 = SoundLibrary.LoadFolder(tmpLvl);
            var fr = lib0.Get("Frogs");
            Assert.True(fr is not null && fr.SscPath is not null && fr.Script is not null, "folder load mapped Frogs");
            fr!.Script!.SetVolume(0.25f); fr.Script.SetMinDistance(42f); fr.Dirty = true;
            var wrote = lib0.SaveDirty();
            Assert.True(wrote.Count == 1 && !fr.Dirty, "SaveDirty wrote 1 file + cleared dirty");
            var lib1 = SoundLibrary.LoadFolder(tmpLvl);
            var fr1 = lib1.Get("Frogs");
            Assert.True(fr1?.Script is not null && Math.Abs(fr1.Script.Volume - 0.25f) < 1e-4 && Math.Abs(fr1.Script.MinDistance - 42f) < 1e-4,
                  "edits persisted to disk + reloaded");
        }
        finally { try { Directory.Delete(tmpLvl, true); } catch { } }
    }
}
