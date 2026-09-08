using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The terrain sun-shadow bake has to include the PLACED OBJECTS, not just the heightmap.
///
/// Saigon68 is the case that found this: a real bake of a built-up, near-flat city map came out 5.8% shadowed and
/// looked in game exactly like a bake that had done nothing - because the only question being asked was whether the
/// ground shadows itself, and flat ground does not. Every shadow a player sees on that map is cast by a building,
/// and the buildings were not in the calculation at all.
/// </summary>
public class TerrainShadowObjectCastTests
{
    // Dead flat ground: nothing here can shadow itself, so any shadow in the result came from an object.
    private static (Heightmap, TerrainConfig) FlatWorld(int side = 64, int world = 256)
        => (new Heightmap(side, side), new TerrainConfig { MaterialSize = side, WorldSize = world, YScale = 1f });

    /// <summary>An axis-aligned box as 12 world-space triangles.</summary>
    private static List<(Vector3, Vector3, Vector3)> Box(Vector3 min, Vector3 max)
    {
        var c = new[]
        {
            new Vector3(min.X, min.Y, min.Z), new Vector3(max.X, min.Y, min.Z),
            new Vector3(max.X, min.Y, max.Z), new Vector3(min.X, min.Y, max.Z),
            new Vector3(min.X, max.Y, min.Z), new Vector3(max.X, max.Y, min.Z),
            new Vector3(max.X, max.Y, max.Z), new Vector3(min.X, max.Y, max.Z),
        };
        int[,] f = { {0,1,2},{0,2,3}, {4,6,5},{4,7,6}, {0,4,5},{0,5,1},
                     {1,5,6},{1,6,2}, {2,6,7},{2,7,3}, {3,7,4},{3,4,0} };
        var tris = new List<(Vector3, Vector3, Vector3)>();
        for (int i = 0; i < f.GetLength(0); i++) tris.Add((c[f[i, 0]], c[f[i, 1]], c[f[i, 2]]));
        return tris;
    }

    private static int ShadowedTexels(Texture2D t)
    {
        int n = 0;
        for (int i = 0; i < t.Rgba.Length; i += 4) if (t.Rgba[i] < 128) n++;
        return n;
    }

    [Fact]
    public void Flat_ground_with_no_objects_casts_no_shadow()
    {
        var (hm, cfg) = FlatWorld();
        var sun = new Vec3(0.6f, 0.5f, -0.6f);
        var bake = TerrainShadow.Bake(128, hm, cfg, sun, blurRadius: 0);
        // This is the Saigon68 symptom in one line: nothing to self-shadow means nothing to see.
        Assert.Equal(0, ShadowedTexels(bake));
    }

    [Fact]
    public void A_building_on_flat_ground_casts_a_shadow()
    {
        var (hm, cfg) = FlatWorld();
        var sun = new Vec3(0.6f, 0.5f, -0.6f);
        // A 20 m cube sitting on the ground in the middle of a 256 m world.
        var occ = MeshOccluder.Build(Box(new Vector3(118f, 0f, 118f), new Vector3(138f, 20f, 138f)));
        Assert.NotNull(occ);

        var without = TerrainShadow.Bake(128, hm, cfg, sun, blurRadius: 0);
        var with = TerrainShadow.Bake(128, hm, cfg, sun, blurRadius: 0, objects: occ);

        Assert.Equal(0, ShadowedTexels(without));
        Assert.True(ShadowedTexels(with) > 100,
            $"a 20 m building should darken a real patch of ground, got {ShadowedTexels(with)} texel(s)");
    }

    [Fact]
    public void The_shadow_falls_away_from_the_sun()
    {
        var (hm, cfg) = FlatWorld();
        var occ = MeshOccluder.Build(Box(new Vector3(118f, 0f, 118f), new Vector3(138f, 20f, 138f)))!;
        // Sun toward +X: the shadow must land on the -X side of the box, never the +X side.
        var bake = TerrainShadow.Bake(256, hm, cfg, new Vec3(1f, 0.45f, 0f), blurRadius: 0, objects: occ);
        int px = 256; float ws = cfg.WorldSize;
        int Sample(float wx, float wz)
        {
            int x = Math.Clamp((int)(wx / ws * px), 0, px - 1);
            int y = Math.Clamp((int)(wz / ws * px), 0, px - 1);
            return bake.Rgba[(y * px + x) * 4];
        }
        Assert.True(Sample(108f, 128f) < 128, "ground on the far side from the sun should be in shadow");
        Assert.True(Sample(150f, 128f) >= 128, "ground on the sun's side should stay lit");
    }

    [Fact]
    public void The_packed_lsb_carries_the_object_shadow_too()
    {
        var (hm, cfg) = FlatWorld();
        var sun = new Vec3(0.6f, 0.5f, -0.6f);
        var occ = MeshOccluder.Build(Box(new Vector3(60f, 0f, 60f), new Vector3(196f, 40f, 196f)))!;

        var plain = TerrainShadow.BakeToLsb(hm, cfg, sun, gridDim: 1, tilePx: 256, bakeSize: 256);
        var withObj = TerrainShadow.BakeToLsb(hm, cfg, sun, gridDim: 1, tilePx: 256, bakeSize: 256, objects: occ);

        // The .lsb stores the OPPOSITE sense of "lit", so a flagged texel is a shadowed one.
        int Flagged(LightmapShadowBits l)
        {
            var vis = l.ToVisibility(out _);
            int n = 0; foreach (var v in vis) if (v != 0) n++;
            return n;
        }
        Assert.Equal(0, Flagged(plain));
        Assert.True(Flagged(withObj) > 1000, $"the building should be in the packed .lsb, got {Flagged(withObj)}");
        // And it still round-trips byte-exact, which is what the game reads.
        Assert.Equal(withObj.Encode(), LightmapShadowBits.Decode(withObj.Encode()).Encode());
    }

    [Fact]
    public void The_bake_reports_progress_and_can_be_cancelled()
    {
        var (hm, cfg) = FlatWorld();
        int rows = 0;
        TerrainShadow.Bake(64, hm, cfg, new Vec3(0.6f, 0.5f, -0.6f), blurRadius: 0,
                           onRow: () => Interlocked.Increment(ref rows));
        Assert.Equal(64, rows);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(
            () => TerrainShadow.Bake(64, hm, cfg, new Vec3(0.6f, 0.5f, -0.6f), cancel: cts.Token));
    }

    /// <summary>One occluder, many threads: the bake shares a single grid and gives each thread its own cursor.
    /// Sharing the cursor instead would corrupt the "already tested this ray" stamps and drop hits at random.</summary>
    [Fact]
    public void The_occluder_is_safe_to_share_across_threads()
    {
        var occ = MeshOccluder.Build(Box(new Vector3(-5f, -5f, -5f), new Vector3(5f, 5f, 5f)))!;
        var dir = Vector3.Normalize(new Vector3(0f, 1f, 0f));
        var hits = new int[8];
        System.Threading.Tasks.Parallel.For(0, 8, t =>
        {
            var cur = occ.NewCursor();
            int n = 0;
            for (int i = 0; i < 500; i++) if (occ.Occluded(new Vector3(0f, -20f, 0f), dir, cur)) n++;
            hits[t] = n;
        });
        Assert.All(hits, h => Assert.Equal(500, h));
    }
}
