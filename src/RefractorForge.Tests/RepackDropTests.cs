using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RefractorForge.Formats.Rfa;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Retiring an entry an archive can never load. Saigon68 accumulated 15 named
/// <c>ObjectLightMaps/../standardMesh/city_dumpster1_&lt;pos&gt;.tga</c> — a path that normalises out of the folder the
/// engine reads, so the file was unreachable dead weight AND a magnet for the save's unique-leaf upsert.
///
/// The bar for a drop is high: it edits someone's level in place, so everything NOT dropped has to come through
/// byte-identical, and the result has to still validate as an archive.
/// </summary>
public class RepackDropTests
{
    private static string Make(string dir, params (string Name, string Body)[] entries)
    {
        Directory.CreateDirectory(dir);
        var rfa = Path.Combine(dir, "level.rfa");
        RefractorFlatArchive.WriteFile(rfa,
            entries.Select(e => (e.Name, (byte[])Encoding.UTF8.GetBytes(e.Body))).ToList(),
            compress: true, xPackId: XPackId.Default);
        return rfa;
    }

    [Fact]
    public void Dropping_a_traversal_entry_leaves_everything_else_byte_identical()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rf_drop_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var rfa = Make(dir,
                ("BfVietnam/levels/Saigon68/Init.con", "rem init\r\n"),
                ("BfVietnam/levels/Saigon68/ObjectLightMaps/O_Keep_m1_1-2-3.tga", "keep-me-exactly"),
                ("BfVietnam/levels/Saigon68/ObjectLightMaps/../standardMesh/city_dumpster1_449-10-173.tga", "dead"),
                ("BfVietnam/levels/Saigon68/StandardMesh/levelWater.rs", "subshader x {}\r\n"));

            var before = new RefractorFlatArchive(rfa);
            var kept = before.Entries
                .Where(e => !e.Name.Replace('\\', '/').Contains("/../"))
                .ToDictionary(e => e.Name, e => before.Read(e), StringComparer.OrdinalIgnoreCase);

            var outRfa = Path.Combine(dir, "out.rfa");
            RefractorFlatArchive.RepackToFile(outRfa, before, new Dictionary<string, byte[]>(),
                drop: n => n.Replace('\\', '/').Contains("/../"));

            Assert.Null(RefractorFlatArchive.Validate(outRfa));          // still a valid archive

            var after = new RefractorFlatArchive(outRfa);
            Assert.DoesNotContain(after.Entries, e => e.Name.Replace('\\', '/').Contains("/../"));
            Assert.Equal(before.Entries.Count - 1, after.Entries.Count);

            // Every surviving entry, byte for byte.
            foreach (var e in after.Entries)
            {
                Assert.True(kept.ContainsKey(e.Name), $"unexpected entry {e.Name}");
                Assert.Equal(kept[e.Name], after.Read(e));
            }
            Assert.Equal(kept.Count, after.Entries.Count);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>No predicate = the existing behaviour, untouched.</summary>
    [Fact]
    public void Without_a_drop_predicate_nothing_is_removed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rf_drop_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var rfa = Make(dir,
                ("BfVietnam/levels/M/Init.con", "a"),
                ("BfVietnam/levels/M/ObjectLightMaps/../standardMesh/x_1-2-3.tga", "b"));
            var before = new RefractorFlatArchive(rfa);
            var outRfa = Path.Combine(dir, "out.rfa");
            RefractorFlatArchive.RepackToFile(outRfa, before, new Dictionary<string, byte[]>());
            var after = new RefractorFlatArchive(outRfa);
            Assert.Equal(before.Entries.Count, after.Entries.Count);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
