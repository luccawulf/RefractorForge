namespace RefractorForge.Formats.Terrain;

/// <summary>
/// A river from a spline: carve the bed, work out the bank cells for material paint, and say where the water
/// line should sit. The carve itself is <see cref="TerrainShaper.CarveChannel"/>; this adds what a river needs
/// on top of a ditch - a bed that never climbs, and banks that get their own material.
/// </summary>
public static class RiverTool
{
    public sealed class Params
    {
        public float Width { get; init; } = 24f;          // metres, bank to bank
        public float Depth { get; init; } = 4f;           // below the surrounding ground
        public float BankWidth { get; init; } = 6f;       // metres of bank material each side
        public byte BankMaterial { get; init; } = 3;      // material index for the banks
        public byte BedMaterial { get; init; } = 4;       // material index for the bed
        public bool LevelBed { get; init; } = true;       // flatten the bed to the lowest point so water covers it
    }

    public sealed class Result
    {
        public (int X0, int Y0, int W, int H) TerrainRect { get; init; }
        /// <summary>Cells to paint, with the material for each (bed cells listed after bank cells so the bed wins).</summary>
        public List<(int Gx, int Gy, byte Material)> Paint { get; } = new();
        /// <summary>Suggested water level: a little below the lowest surrounding ground along the path.</summary>
        public float SuggestedWaterLevel { get; init; }
    }

    public static Result Build(Heightmap hm, TerrainConfig cfg, IReadOnlyList<(float X, float Z)> path, Params p)
    {
        if (path.Count < 2) return new Result { TerrainRect = (0, 0, 0, 0) };

        // Ground height along the path BEFORE carving, so the water line can be chosen from the original bank.
        float sp = cfg.HorizontalSpacing <= 0 ? 1f : cfg.HorizontalSpacing;
        float minGround = float.MaxValue;
        foreach (var (x, z) in path)
        {
            int gx = Math.Clamp((int)MathF.Round(x / sp), 0, hm.Width - 1);
            int gz = Math.Clamp((int)MathF.Round(z / sp), 0, hm.Height - 1);
            minGround = MathF.Min(minGround, cfg.HeightToMeters(hm[gx, gz]));
        }

        var rect = TerrainShaper.CarveChannel(hm, cfg, path, p.Width, p.Depth, skirt: 0.45f);

        if (p.LevelBed && rect.W > 0)
        {
            // A river bed must not run uphill: pull every bed cell down to the lowest point of the carve so the
            // water plane covers the whole length instead of leaving dry humps.
            float half = p.Width * 0.5f * 0.55f;
            float bedTarget = minGround - p.Depth;
            for (int y = rect.Y0; y < rect.Y0 + rect.H; y++)
                for (int x = rect.X0; x < rect.X0 + rect.W; x++)
                {
                    float wx = x * sp, wz = y * sp;
                    if (DistToPath(path, wx, wz) > half) continue;
                    float cur = cfg.HeightToMeters(hm[x, y]);
                    if (cur > bedTarget) hm[x, y] = cfg.MetersToRaw(bedTarget);
                }
        }

        var res = new Result { TerrainRect = rect, SuggestedWaterLevel = minGround - p.Depth * 0.35f };

        // Bank and bed cells by distance to the centreline.
        float bedR = p.Width * 0.5f, bankR = bedR + p.BankWidth;
        int cx0 = Math.Max(0, (int)((path.Min(q => q.X) - bankR) / sp) - 1);
        int cz0 = Math.Max(0, (int)((path.Min(q => q.Z) - bankR) / sp) - 1);
        int cx1 = Math.Min(hm.Width - 1, (int)((path.Max(q => q.X) + bankR) / sp) + 1);
        int cz1 = Math.Min(hm.Height - 1, (int)((path.Max(q => q.Z) + bankR) / sp) + 1);
        var bed = new List<(int, int, byte)>();
        for (int y = cz0; y <= cz1; y++)
            for (int x = cx0; x <= cx1; x++)
            {
                float d = DistToPath(path, x * sp, y * sp);
                if (d <= bedR) bed.Add((x, y, p.BedMaterial));
                else if (d <= bankR) res.Paint.Add((x, y, p.BankMaterial));
            }
        res.Paint.AddRange(bed);
        return res;
    }

    private static float DistToPath(IReadOnlyList<(float X, float Z)> path, float px, float pz)
    {
        float best = float.MaxValue;
        for (int i = 0; i + 1 < path.Count; i++)
        {
            var (ax, az) = path[i]; var (bx, bz) = path[i + 1];
            float vx = bx - ax, vz = bz - az;
            float l2 = vx * vx + vz * vz;
            float t = l2 < 1e-6f ? 0f : Math.Clamp(((px - ax) * vx + (pz - az) * vz) / l2, 0f, 1f);
            float qx = ax + vx * t, qz = az + vz * t;
            float dx = px - qx, dz = pz - qz;
            best = MathF.Min(best, dx * dx + dz * dz);
        }
        return MathF.Sqrt(best);
    }
}
