using RefractorForge.Formats.Rfa;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Trees are BF1942 <c>.tm</c> meshes, not <c>.sm</c>, and their collision section used to be walked past and
/// thrown away — so the editor's collision overlay drew nothing for every tree and bush on a map even though the
/// geometry was in the file. These gates decode it from the real treeMesh archives.
/// </summary>
public class TreeCollisionTests
{
    private static string? FindTreeArchive()
    {
        foreach (var root in new[] { @"D:\Games\EA GAMES\Battlefield 1942", @"D:\Games\EA GAMES\Battlefield Vietnam" })
        {
            if (!Directory.Exists(root)) continue;
            string? hit;
            try { hit = Directory.EnumerateFiles(root, "treeMesh*.rfa", SearchOption.AllDirectories).FirstOrDefault(); }
            catch { continue; }
            if (hit is not null) return hit;
        }
        return null;
    }

    [Fact]
    public void Tree_collision_decodes_from_real_archives()
    {
        var rfa = FindTreeArchive();
        if (rfa is null) return;   // no install on this machine

        var arch = new RefractorFlatArchive(rfa);
        int parsed = 0, withCollision = 0, trianglesTotal = 0;

        foreach (var e in arch.Entries.Where(x => x.Name.EndsWith(".tm", StringComparison.OrdinalIgnoreCase)).Take(120))
        {
            byte[] bytes;
            try { bytes = arch.Read(e); } catch { continue; }
            if (!TreeMesh.TryParse(bytes, out var tm) || tm is null) continue;
            parsed++;

            // The whole file must still parse cleanly - capturing the collision section must not desync the
            // cursor for the geometry that follows it.
            Assert.Equal(bytes.Length, tm.Consumed);

            if (!tm.HasCollision) { Assert.Empty(tm.CollisionVertices); Assert.Empty(tm.CollisionIndices); continue; }
            if (tm.CollisionIndices.Length == 0) continue;
            withCollision++;

            Assert.NotEmpty(tm.CollisionVertices);
            Assert.True(tm.CollisionIndices.Length % 3 == 0, $"{e.Name}: indices are not whole triangles");
            foreach (var i in tm.CollisionIndices)
                Assert.True(i < tm.CollisionVertices.Length,
                    $"{e.Name}: index {i} outside {tm.CollisionVertices.Length} collision vertices");
            trianglesTotal += tm.CollisionIndices.Length / 3;

            // A hull is a real solid: it must have some extent rather than collapsing to a point.
            var xs = tm.CollisionVertices.Select(v => v.X).ToList();
            var ys = tm.CollisionVertices.Select(v => v.Y).ToList();
            Assert.True(xs.Max() - xs.Min() > 0.01f || ys.Max() - ys.Min() > 0.01f, $"{e.Name}: degenerate hull");
        }

        Assert.True(parsed > 0, "no .tm entries parsed");
        Assert.True(withCollision > 0, "no tree in the archive yielded collision geometry");
        Assert.True(trianglesTotal > 0);
    }

    [Fact]
    public void Trees_without_collision_report_empty_rather_than_throwing()
    {
        var rfa = FindTreeArchive();
        if (rfa is null) return;

        var arch = new RefractorFlatArchive(rfa);
        foreach (var e in arch.Entries.Where(x => x.Name.EndsWith(".tm", StringComparison.OrdinalIgnoreCase)).Take(120))
        {
            byte[] bytes;
            try { bytes = arch.Read(e); } catch { continue; }
            if (!TreeMesh.TryParse(bytes, out var tm) || tm is null || tm.HasCollision) continue;
            Assert.Empty(tm.CollisionVertices);
            Assert.Empty(tm.CollisionIndices);
            Assert.NotEmpty(tm.Vertices);      // the visible mesh still decodes
        }
    }
}
