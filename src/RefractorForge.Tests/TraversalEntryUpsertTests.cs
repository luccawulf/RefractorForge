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
/// A save must never write into an archive entry whose stored path escapes its own folder.
///
/// Saigon68 carried 15 entries named <c>ObjectLightMaps/../standardMesh/city_dumpster1_&lt;pos&gt;.tga</c>, written by
/// a former bug that used <c>GeometryTemplate.file</c> (a PATH) as a file name. Once the naming was fixed the bake
/// produced the right name — and the save put it straight back at the wrong path, because the corrupt entry was the
/// only one holding that leaf and the unique-leaf upsert matched it. The corruption regenerated itself on every
/// save, so the fix appeared to do nothing.
/// </summary>
public class TraversalEntryUpsertTests
{
    private static string MakeArchive(string dir, params (string Name, string Body)[] entries)
    {
        Directory.CreateDirectory(dir);
        var rfa = Path.Combine(dir, "level.rfa");
        RefractorFlatArchive.WriteFile(rfa, entries
            .Select(e => (e.Name, (byte[])Encoding.UTF8.GetBytes(e.Body)))
            .ToList(), compress: true, xPackId: XPackId.Default);
        return rfa;
    }

    /// <summary>The corrected name must land at the corrected path, leaving the corrupt entry untouched.</summary>
    [Fact]
    public void A_corrected_name_is_not_redirected_onto_a_traversal_entry()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rf_trav_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // The archive as the old bug left it: the ONLY holder of this leaf is the escaped path.
            var rfa = MakeArchive(dir,
                ("BfVietnam/levels/Saigon68/Init.con", "rem init\r\n"),
                ("BfVietnam/levels/Saigon68/ObjectLightMaps/O_Good_m1_1-2-3.tga", "existing-good"),
                ("BfVietnam/levels/Saigon68/ObjectLightMaps/../standardMesh/city_dumpster1_449-10-173.tga", "corrupt"));

            var outRfa = Path.Combine(dir, "out.rfa");
            LevelSaver.RepackToRfa(rfa, outRfa, null, null, null, null, newEntries: new[]
            {
                ("ObjectLightMaps/city_dumpster1_449-10-173.tga", Encoding.UTF8.GetBytes("FRESH")),
            });

            var res = new RefractorFlatArchive(outRfa);
            string Norm(string n) => n.Replace('\\', '/');
            var names = res.Entries.Select(e => Norm(e.Name)).ToList();

            // It went to the RIGHT place...
            Assert.Contains("BfVietnam/levels/Saigon68/ObjectLightMaps/city_dumpster1_449-10-173.tga", names);
            var good = res.Entries.First(e => Norm(e.Name).EndsWith("/ObjectLightMaps/city_dumpster1_449-10-173.tga"));
            Assert.Equal("FRESH", Encoding.UTF8.GetString(res.Read(good)));

            // ...and the corrupt entry was NOT the one written into.
            var bad = res.Entries.FirstOrDefault(e => Norm(e.Name).Contains("/../"));
            if (bad is not null)
                Assert.NotEqual("FRESH", Encoding.UTF8.GetString(res.Read(bad)));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>The unique-leaf fallback still has to work for ordinary entries — that is what lets a re-baked
    /// lightmap land on a level whose folder case differs from ours.</summary>
    [Fact]
    public void An_ordinary_leaf_match_still_resolves()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rf_trav_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var rfa = MakeArchive(dir,
                ("BfVietnam/levels/Saigon68/Init.con", "rem init\r\n"),
                ("BfVietnam/levels/Saigon68/objectlightmaps/O_Hut_m1_5-6-7.tga", "old"));

            var outRfa = Path.Combine(dir, "out.rfa");
            // Different folder CASE from what the archive stores - the leaf fallback exists exactly for this.
            LevelSaver.RepackToRfa(rfa, outRfa, null, null, null, null, newEntries: new[]
            {
                ("ObjectLightMaps/O_Hut_m1_5-6-7.tga", Encoding.UTF8.GetBytes("REBAKED")),
            });

            var res = new RefractorFlatArchive(outRfa);
            var hits = res.Entries.Where(e => e.Name.Replace('\\', '/').EndsWith("/O_Hut_m1_5-6-7.tga")).ToList();
            Assert.Single(hits);                                                    // updated in place, not duplicated
            Assert.Equal("REBAKED", Encoding.UTF8.GetString(res.Read(hits[0])));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
