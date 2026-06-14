using System.Collections.Generic;
using System.Linq;
using RefractorForge.Formats.Geometry;

namespace RefractorForge.Formats.Con;

/// <summary>Which gameplay layer an editable handle belongs to.</summary>
public enum GpKind { ControlPoint, Vehicle, Soldier }

/// <summary>
/// A mutable editing view over the gameplay layer. The parsed <see cref="GameplayObjects"/> are
/// immutable value records; this wraps them in lists so the editor can move handles and tune capture
/// radii in place (each edit replaces the struct at its index). Edits go through the same undo stack
/// as object/terrain/material edits via <c>GameplayMoveCommand</c> / <c>GameplayRadiusCommand</c>.
/// </summary>
public sealed class EditableGameplay
{
    public List<ControlPointDef> ControlPoints { get; }
    public List<VehicleSpawnDef> VehicleSpawns { get; }
    public List<SoldierSpawnDef> SoldierSpawns { get; }

    public EditableGameplay(GameplayObjects g)
    {
        ControlPoints = g.ControlPoints.ToList();
        VehicleSpawns = g.VehicleSpawns.ToList();
        SoldierSpawns = g.SoldierSpawns.ToList();
    }

    public int Count => ControlPoints.Count + VehicleSpawns.Count + SoldierSpawns.Count;

    public int CountOf(GpKind k) => k switch
    {
        GpKind.ControlPoint => ControlPoints.Count,
        GpKind.Vehicle => VehicleSpawns.Count,
        _ => SoldierSpawns.Count,
    };

    public Vec3 GetPos(GpKind k, int i) => k switch
    {
        GpKind.ControlPoint => ControlPoints[i].Position,
        GpKind.Vehicle => VehicleSpawns[i].Position,
        _ => SoldierSpawns[i].Position,
    };

    public void SetPos(GpKind k, int i, Vec3 p)
    {
        switch (k)
        {
            case GpKind.ControlPoint: ControlPoints[i] = ControlPoints[i] with { Position = p }; break;
            case GpKind.Vehicle: VehicleSpawns[i] = VehicleSpawns[i] with { Position = p }; break;
            default: SoldierSpawns[i] = SoldierSpawns[i] with { Position = p }; break;
        }
    }

    public string GetName(GpKind k, int i) => k switch
    {
        GpKind.ControlPoint => ControlPoints[i].Name,
        GpKind.Vehicle => VehicleSpawns[i].Name,
        _ => SoldierSpawns[i].Name,
    };

    /// <summary>Extra detail for the inspector: the vehicle template for a spawner, else "".</summary>
    public string GetDetail(GpKind k, int i) => k == GpKind.Vehicle ? VehicleSpawns[i].Vehicle : "";

    public float GetRadius(int controlPointIndex) => ControlPoints[controlPointIndex].Radius;
    public void SetRadius(int controlPointIndex, float r)
        => ControlPoints[controlPointIndex] = ControlPoints[controlPointIndex] with { Radius = r };

    /// <summary>Facing rotation (Euler; X = yaw). Control points are radial, so they report zero.</summary>
    public Vec3 GetRotation(GpKind k, int i) => k switch
    {
        GpKind.Vehicle => VehicleSpawns[i].Rotation,
        GpKind.Soldier => SoldierSpawns[i].Rotation,
        _ => Vec3.Zero,
    };

    public void SetRotation(GpKind k, int i, Vec3 rot)
    {
        switch (k)
        {
            case GpKind.Vehicle: VehicleSpawns[i] = VehicleSpawns[i] with { Rotation = rot }; break;
            case GpKind.Soldier: SoldierSpawns[i] = SoldierSpawns[i] with { Rotation = rot }; break;
        }
    }

    public float GetYaw(GpKind k, int i) => GetRotation(k, i).X;
    public void SetYaw(GpKind k, int i, float yaw) { var r = GetRotation(k, i); SetRotation(k, i, new Vec3(yaw, r.Y, r.Z)); }

    /// <summary>The whole handle as a boxed struct — lets the editor capture/restore it for undo and
    /// rebuild a modified copy with C# <c>with</c> expressions (position, rotation, name, radius, vehicle).</summary>
    public object GetItem(GpKind k, int i) => k switch
    {
        GpKind.ControlPoint => ControlPoints[i],
        GpKind.Vehicle => VehicleSpawns[i],
        _ => SoldierSpawns[i],
    };

    public void SetItem(GpKind k, int i, object item)
    {
        switch (k)
        {
            case GpKind.ControlPoint: ControlPoints[i] = (ControlPointDef)item; break;
            case GpKind.Vehicle: VehicleSpawns[i] = (VehicleSpawnDef)item; break;
            default: SoldierSpawns[i] = (SoldierSpawnDef)item; break;
        }
    }

    private System.Collections.IList List(GpKind k) => k switch
    {
        GpKind.ControlPoint => ControlPoints,
        GpKind.Vehicle => VehicleSpawns,
        _ => SoldierSpawns,
    };

    /// <summary>Replace the whole layer in place (keeps this object's identity / references). Used by
    /// collaboration full-state sync.</summary>
    public void ReplaceAll(IEnumerable<ControlPointDef> cps, IEnumerable<VehicleSpawnDef> vss, IEnumerable<SoldierSpawnDef> sss)
    {
        ControlPoints.Clear(); ControlPoints.AddRange(cps);
        VehicleSpawns.Clear(); VehicleSpawns.AddRange(vss);
        SoldierSpawns.Clear(); SoldierSpawns.AddRange(sss);
    }

    /// <summary>Append a handle, returning its new index.</summary>
    public int Add(GpKind k, object item) { var l = List(k); l.Add(item); return l.Count - 1; }
    public void Insert(GpKind k, int i, object item) => List(k).Insert(System.Math.Min(i, List(k).Count), item);
    public void RemoveAt(GpKind k, int i) { var l = List(k); if (i >= 0 && i < l.Count) l.RemoveAt(i); }

    /// <summary>Defaults for newly placed handles (named generically; edit in the inspector afterwards).</summary>
    public static ControlPointDef NewControlPoint(Vec3 pos) => new("ControlPoint", pos, 30f, 0);
    public static VehicleSpawnDef NewVehicleSpawn(Vec3 pos) => new("Spawner", pos, Vec3.Zero, "", 1);
    public static SoldierSpawnDef NewSoldierSpawn(Vec3 pos) => new("SoldierSpawn", pos, Vec3.Zero);

    /// <summary>Snapshot back to an immutable <see cref="GameplayObjects"/> (e.g. for saving).</summary>
    public GameplayObjects ToImmutable() => new(ControlPoints.ToList(), VehicleSpawns.ToList(), SoldierSpawns.ToList());
}
