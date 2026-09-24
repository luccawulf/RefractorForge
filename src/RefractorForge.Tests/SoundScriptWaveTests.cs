using RefractorForge.Formats.Rfa;
using RefractorForge.Formats.Sound;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// A sound script one wave at a time (<see cref="SoundScript.Waves"/>): a vehicle's has many - engine start, idle, rev,
/// stop - each with its own volume, distances and loop. Every retail sound script of both games reads and comes back
/// byte-exact; an edit of one wave touches that wave's lines only.
/// </summary>
public class SoundScriptWaveTests
{
    private const string Willy = "newPatch\r\n####################\r\n### Engine Start ###\r\n####################\r\nload @ROOT/Sound/@RTD/start1.wav\r\n"
        + "minDistance 1\r\ndopplerOff\r\npriority -2\r\n*** Distance Volume ***\r\nbeginEffect\r\n\tcontrolDestination Volume\r\n\tcontrolSource Distance\r\n"
        + "\tenvelope Ramp\r\n\tparam 50\r\n\tparam 100\r\n\tparam 1\r\n\tparam -1\t\r\nendEffect\r\n\r\n############\r\n### Main ###\r\n############\r\n\r\n"
        + "load @ROOT/Sound/@RTD/Willyengine3.wav\r\nloop\r\nminDistance 2\r\nrelativePosition 0/0/0\r\npriority 9\r\n*** Distance Volume ***\r\n"
        + "beginEffect\r\n\tcontrolDestination Volume\r\n\tcontrolSource Distance\r\n\tenvelope Ramp\r\n\tparam 220\r\n\tparam 300\r\n\tparam 1\r\n\tparam -1\t\r\nendEffect\r\n";

    [Fact]
    public void Each_wave_reads_and_edits_on_its_own()
    {
        var s = SoundScript.Parse(Willy);
        Assert.Equal(2, s.Waves.Count);
        var (start, main) = (s.Waves[0], s.Waves[1]);
        Assert.Equal(("load", "@ROOT/Sound/@RTD/start1.wav", 5, 1f, 100f, -2, false, true), (start.Mode, start.Wav, start.Line, start.MinDistance, start.MaxDistance, start.Priority, start.Loop, start.DopplerOff));
        Assert.Equal(("@ROOT/Sound/@RTD/Willyengine3.wav", 2f, 300f, 9, true, (float?)null), (main.Wav, main.MinDistance, main.MaxDistance, main.Priority, main.Loop, main.Volume));

        s.SetVolume(1, 0.8f);                       // absent: added right under its source line
        s.SetMaxDistance(1, 450f);
        s.SetLoop(0, true);
        s.SetPriority(0, 3);
        s.SetWav(0, "@ROOT/Sound/@RTD/start2.wav");
        var after = s.Waves;
        Assert.Equal((true, 3, 100f, "@ROOT/Sound/@RTD/start2.wav"), (after[0].Loop, after[0].Priority, after[0].MaxDistance, after[0].Wav));
        Assert.Equal((0.8f, 450f, 2f, true), (after[1].Volume, after[1].MaxDistance, after[1].MinDistance, after[1].Loop));
        var text = s.ToText();
        Assert.Contains("load @ROOT/Sound/@RTD/Willyengine3.wav\r\nvolume 0.8\r\nloop\r\n", text);
        Assert.Contains("\tparam 450\r\n", text);
        Assert.Contains("\tparam 100\r\n", text);                                              // the other wave's ramp as it was
        s.SetLoop(0, false);
        Assert.False(s.Waves[0].Loop);
        Assert.True(s.Waves[1].Loop);
    }

    [Fact]
    public void Tiers_and_includes_are_kept_apart()
    {
        var top = SoundScript.Parse("#templateLevel HIGH\r\n\r\n\t#include High/WillyEngine.ssc\r\n\r\n#templateLevel MEDIUM\r\n\r\n\t#include Mid/WillyEngine.ssc\r\n");
        Assert.Empty(top.Waves);
        Assert.Equal(new[] { "High/WillyEngine.ssc", "Mid/WillyEngine.ssc" }, top.Includes);
        var tiers = SoundScript.Parse("#templateLevel HIGH\nnewPatch\nload a.wav\nloop\n#templateLevel LOW\nnewPatch\nload b.wav\n");
        Assert.Equal(new[] { ("HIGH", "a.wav", true), ("LOW", "b.wav", false) }, tiers.Waves.Select(w => (w.Tier!, w.Wav, w.Loop)));
    }

    /// <summary>Every sound script of both retail games: its waves read, and the file comes back as it was.</summary>
    [InstallFact(Installs.Bf1942CleanArchives, Installs.BfvOriginalArchives)]
    public void Every_retail_sound_script_reads_and_round_trips()
    {
        int scripts = 0, waves = 0;
        foreach (var dir in new[] { Installs.Bf1942CleanArchives, Installs.BfvOriginalArchives }.Where(Directory.Exists))
            foreach (var name in new[] { "Objects.rfa", "objects.rfa" }.Select(n => Path.Combine(dir, n)).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var a = new RefractorFlatArchive(name);
                foreach (var e in a.Entries.Where(e => e.Name.EndsWith(".ssc", StringComparison.OrdinalIgnoreCase)))
                {
                    var bytes = a.Read(e);
                    var s = SoundScript.Parse(bytes);
                    waves += s.Waves.Count;
                    Assert.All(s.Waves, w => Assert.False(string.IsNullOrEmpty(w.Wav), e.Name));
                    Assert.Equal(bytes, s.ToBytes());
                    scripts++;
                }
            }
        Assert.True(scripts > 100, $"{scripts} scripts");
        Assert.True(waves > scripts, $"{waves} waves in {scripts} scripts");
    }
}
