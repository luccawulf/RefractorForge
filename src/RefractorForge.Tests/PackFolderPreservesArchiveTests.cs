using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RefractorForge.Formats;
using RefractorForge.Formats.Rfa;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Packing a level folder must never DELETE content that lives only in the archive it overwrites.
///
/// A level opened from a .rfa keeps most of its content in that archive; per-object lightmaps in particular are
/// routinely archive-only. "Test This Level" packs the working folder straight over
/// <c>Mods/&lt;mod&gt;/Archives/&lt;game&gt;/levels/&lt;Map&gt;.rfa</c>, and that took one real map from 539 object
/// lightmaps to ZERO - every object rendered wrong until they were restored from a backup.
/// </summary>
public class PackFolderPreservesArchiveTests
{
    [Fact]
    public void Packing_a_folder_keeps_archive_only_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rf_pack_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // An archive with more in it than the folder will provide - lightmaps only it holds.
            var rfa = Path.Combine(dir, "Map.rfa");
            Directory.CreateDirectory(dir);
            RefractorFlatArchive.WriteFile(rfa, new List<(string, byte[])>
            {
                ("bf1942/levels/Map/Init.con",                                   Encoding.UTF8.GetBytes("old init")),
                ("bf1942/levels/Map/ObjectLightMaps/O_House_m1_1-2-3.tga",        Encoding.UTF8.GetBytes("lightmap A")),
                ("bf1942/levels/Map/ObjectLightMaps/O_House_m1_4-5-6.tga",        Encoding.UTF8.GetBytes("lightmap B")),
                ("bf1942/levels/Map/Textures/tx00x00.dds",                        Encoding.UTF8.GetBytes("tile")),
            }, compress: true, xPackId: XPackId.Default);

            // The working folder holds only the .con the user edited - no lightmaps at all.
            var folder = Path.Combine(dir, "work");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Init.con"), "NEW init");

            LevelSaver.PackFolder(folder, rfa, "bf1942/levels/Map/");

            var after = new RefractorFlatArchive(rfa);
            string Norm(string n) => n.Replace('\\', '/');
            var names = after.Entries.Select(e => Norm(e.Name)).ToList();

            // The folder's edit won...
            var init = after.Entries.First(e => Norm(e.Name).EndsWith("/Init.con"));
            Assert.Equal("NEW init", Encoding.UTF8.GetString(after.Read(init)));

            // ...and NOTHING archive-only was lost.
            Assert.Contains("bf1942/levels/Map/ObjectLightMaps/O_House_m1_1-2-3.tga", names);
            Assert.Contains("bf1942/levels/Map/ObjectLightMaps/O_House_m1_4-5-6.tga", names);
            Assert.Contains("bf1942/levels/Map/Textures/tx00x00.dds", names);

            var lm = after.Entries.First(e => Norm(e.Name).EndsWith("O_House_m1_1-2-3.tga"));
            Assert.Equal("lightmap A", Encoding.UTF8.GetString(after.Read(lm)));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>Packing to a path that does not exist yet is unchanged - just the folder.</summary>
    [Fact]
    public void Packing_to_a_new_path_writes_only_the_folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rf_pack_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var folder = Path.Combine(dir, "work");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Init.con"), "init");
            File.WriteAllText(Path.Combine(folder, "StaticObjects.con"), "objects");

            var rfa = Path.Combine(dir, "Fresh.rfa");
            int n = LevelSaver.PackFolder(folder, rfa, "bf1942/levels/Map/");

            Assert.Equal(2, n);
            Assert.Equal(2, new RefractorFlatArchive(rfa).Entries.Count);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
