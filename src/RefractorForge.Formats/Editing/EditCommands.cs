using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;

namespace RefractorForge.Formats.Editing;

/// <summary>
/// A single, reversible, serializable edit. Commands are addressed by object Id and encode
/// to a compact wire string — which is exactly what makes real-time collaboration tractable:
/// a relay can broadcast these and every client replays them onto the same id-addressed model.
/// </summary>
public interface IEditCommand
{
    void Apply(StaticObjectsFile file);
    void Undo(StaticObjectsFile file);
    string ToWire();
}

public sealed class MoveObject : IEditCommand
{
    public string Id; public Vec3 To;
    private Vec3 _from; private bool _captured;
    public MoveObject(string id, Vec3 to) { Id = id; To = to; }
    public void Apply(StaticObjectsFile f) { var o = f.FindById(Id); if (o is null) return; if (!_captured) { _from = o.Position; _captured = true; } o.Position = To; }
    public void Undo(StaticObjectsFile f) { var o = f.FindById(Id); if (o is not null) o.Position = _from; }
    public string ToWire() => $"MOVE {Id} {To}";
}

public sealed class RotateObject : IEditCommand
{
    public string Id; public Vec3 To;
    private Vec3 _from; private bool _captured;
    public RotateObject(string id, Vec3 to) { Id = id; To = to; }
    public void Apply(StaticObjectsFile f) { var o = f.FindById(Id); if (o is null) return; if (!_captured) { _from = o.Rotation; _captured = true; } o.Rotation = To; }
    public void Undo(StaticObjectsFile f) { var o = f.FindById(Id); if (o is not null) o.Rotation = _from; }
    public string ToWire() => $"ROT {Id} {To}";
}

public sealed class ScaleObject : IEditCommand
{
    public string Id; public float To;
    private float? _from; private bool _captured;
    public ScaleObject(string id, float to) { Id = id; To = to; }
    public void Apply(StaticObjectsFile f) { var o = f.FindById(Id); if (o is null) return; if (!_captured) { _from = o.Scale; _captured = true; } o.Scale = To; }
    public void Undo(StaticObjectsFile f) { var o = f.FindById(Id); if (o is not null) o.Scale = _from; }
    public string ToWire() => $"SCALE {Id} {To.ToString("0.######", CultureInfo.InvariantCulture)}";
}

public sealed class AddObject : IEditCommand
{
    public string Id; public string Template; public Vec3 Pos; public Vec3 Rot;
    public AddObject(string id, string template, Vec3 pos, Vec3 rot) { Id = id; Template = template; Pos = pos; Rot = rot; }
    public void Apply(StaticObjectsFile f)
    {
        if (f.FindById(Id) is not null) return;
        f.Objects.Add(new StaticObject(Template) { Id = Id, Position = Pos, Rotation = Rot });
    }
    public void Undo(StaticObjectsFile f) { var o = f.FindById(Id); if (o is not null) f.Objects.Remove(o); }
    public string ToWire() => $"ADD {Id} {Template} {Pos} {Rot}";
}

public sealed class DeleteObject : IEditCommand
{
    public string Id;
    private StaticObject? _snapshot; private int _index;
    public DeleteObject(string id) { Id = id; }
    public void Apply(StaticObjectsFile f)
    {
        var o = f.FindById(Id); if (o is null) return;
        _snapshot = o; _index = f.Objects.IndexOf(o); f.Objects.RemoveAt(_index);
    }
    public void Undo(StaticObjectsFile f) { if (_snapshot is not null) f.Objects.Insert(Math.Min(_index, f.Objects.Count), _snapshot); }
    public string ToWire() => $"DEL {Id}";
}

/// <summary>Groups several edits into one reversible unit — a multi-select move/rotate/delete becomes a
/// single undo step. Apply runs them in order; Undo reverses in the opposite order.</summary>
public sealed class CompositeCommand : IEditCommand
{
    private readonly List<IEditCommand> _cmds;
    public CompositeCommand(IEnumerable<IEditCommand> cmds) { _cmds = cmds.ToList(); }
    public int Count => _cmds.Count;
    public void Apply(StaticObjectsFile f) { foreach (var c in _cmds) c.Apply(f); }
    public void Undo(StaticObjectsFile f) { for (int i = _cmds.Count - 1; i >= 0; i--) _cmds[i].Undo(f); }
    public string ToWire() => string.Join(" ; ", _cmds.Select(c => c.ToWire()));
}

/// <summary>Adapts a coalesced terrain-sculpt <see cref="TerrainEdit"/> into the object <see cref="EditHistory"/>,
/// so a height stroke undoes/redoes on the same Z/Y stack as object edits (the <see cref="StaticObjectsFile"/>
/// argument is ignored). <paramref name="onChanged"/> lets the viewer re-upload the terrain mesh.</summary>
public sealed class TerrainStrokeCommand : IEditCommand
{
    private readonly TerrainEdit _edit;
    private readonly Heightmap _hm;
    private readonly System.Action? _onChanged;

    public TerrainStrokeCommand(TerrainEdit edit, Heightmap hm, System.Action? onChanged)
    { _edit = edit; _hm = hm; _onChanged = onChanged; }

    public void Apply(StaticObjectsFile _) { _edit.Redo(_hm); _onChanged?.Invoke(); }
    public void Undo(StaticObjectsFile _) { _edit.Undo(_hm); _onChanged?.Invoke(); }
    public string ToWire() => string.Create(CultureInfo.InvariantCulture, $"TERRAIN {_edit.X0} {_edit.Y0} {_edit.W} {_edit.H}");
}

/// <summary>A material-map paint stroke as an undoable command, riding the same history as object/terrain edits.</summary>
public sealed class MaterialStrokeCommand : IEditCommand
{
    private readonly MaterialEdit _edit;
    private readonly MaterialMap _map;
    private readonly System.Action? _onChanged;

    public MaterialStrokeCommand(MaterialEdit edit, MaterialMap map, System.Action? onChanged)
    { _edit = edit; _map = map; _onChanged = onChanged; }

    public void Apply(StaticObjectsFile _) { _edit.Redo(_map); _onChanged?.Invoke(); }
    public void Undo(StaticObjectsFile _) { _edit.Undo(_map); _onChanged?.Invoke(); }
    public string ToWire() => string.Create(CultureInfo.InvariantCulture, $"MATERIAL {_edit.X0} {_edit.Y0} {_edit.W} {_edit.H}");
}

/// <summary>Move a gameplay handle (control point / vehicle spawn / soldier spawn). Captures the
/// original position on first apply so undo restores it; rides the shared object undo stack.</summary>
public sealed class GameplayMoveCommand : IEditCommand
{
    private readonly EditableGameplay _gp;
    private readonly GpKind _kind;
    private readonly int _index;
    private readonly Vec3 _to;
    private Vec3 _from; private bool _captured;
    private readonly System.Action? _onChanged;

    public GameplayMoveCommand(EditableGameplay gp, GpKind kind, int index, Vec3 to, System.Action? onChanged)
    { _gp = gp; _kind = kind; _index = index; _to = to; _onChanged = onChanged; }

    public void Apply(StaticObjectsFile _)
    {
        if (!_captured) { _from = _gp.GetPos(_kind, _index); _captured = true; }
        _gp.SetPos(_kind, _index, _to); _onChanged?.Invoke();
    }
    public void Undo(StaticObjectsFile _) { _gp.SetPos(_kind, _index, _from); _onChanged?.Invoke(); }
    public string ToWire() => string.Create(CultureInfo.InvariantCulture, $"GPMOVE {(int)_kind} {_index} {_to.X} {_to.Y} {_to.Z}");
}

/// <summary>Change a control point's capture radius (metres).</summary>
public sealed class GameplayRadiusCommand : IEditCommand
{
    private readonly EditableGameplay _gp;
    private readonly int _index;
    private readonly float _to;
    private float _from; private bool _captured;
    private readonly System.Action? _onChanged;

    public GameplayRadiusCommand(EditableGameplay gp, int index, float to, System.Action? onChanged)
    { _gp = gp; _index = index; _to = to; _onChanged = onChanged; }

    public void Apply(StaticObjectsFile _)
    {
        if (!_captured) { _from = _gp.GetRadius(_index); _captured = true; }
        _gp.SetRadius(_index, _to); _onChanged?.Invoke();
    }
    public void Undo(StaticObjectsFile _) { _gp.SetRadius(_index, _from); _onChanged?.Invoke(); }
    public string ToWire() => string.Create(CultureInfo.InvariantCulture, $"GPRAD {_index} {_to}");
}

/// <summary>Rotate a vehicle/soldier spawn (Euler; X = yaw). Captures the original on first apply.</summary>
public sealed class GameplayRotateCommand : IEditCommand
{
    private readonly EditableGameplay _gp;
    private readonly GpKind _kind;
    private readonly int _index;
    private readonly Vec3 _to;
    private Vec3 _from; private bool _captured;
    private readonly System.Action? _onChanged;

    public GameplayRotateCommand(EditableGameplay gp, GpKind kind, int index, Vec3 to, System.Action? onChanged)
    { _gp = gp; _kind = kind; _index = index; _to = to; _onChanged = onChanged; }

    public void Apply(StaticObjectsFile _)
    {
        if (!_captured) { _from = _gp.GetRotation(_kind, _index); _captured = true; }
        _gp.SetRotation(_kind, _index, _to); _onChanged?.Invoke();
    }
    public void Undo(StaticObjectsFile _) { _gp.SetRotation(_kind, _index, _from); _onChanged?.Invoke(); }
    public string ToWire() => string.Create(CultureInfo.InvariantCulture, $"GPROT {(int)_kind} {_index} {_to.X} {_to.Y} {_to.Z}");
}

/// <summary>Replace a gameplay handle with a modified copy (rotation, name, vehicle template, etc.).
/// Captures the prior struct so undo restores it exactly.</summary>
public sealed class GameplaySetItemCommand : IEditCommand
{
    private readonly EditableGameplay _gp;
    private readonly GpKind _kind;
    private readonly int _index;
    private readonly object _to;
    private object? _from; private bool _captured;
    private readonly System.Action? _onChanged;

    public GameplaySetItemCommand(EditableGameplay gp, GpKind kind, int index, object to, System.Action? onChanged)
    { _gp = gp; _kind = kind; _index = index; _to = to; _onChanged = onChanged; }

    public void Apply(StaticObjectsFile _)
    {
        if (!_captured) { _from = _gp.GetItem(_kind, _index); _captured = true; }
        _gp.SetItem(_kind, _index, _to); _onChanged?.Invoke();
    }
    public void Undo(StaticObjectsFile _) { if (_from is not null) _gp.SetItem(_kind, _index, _from); _onChanged?.Invoke(); }
    public string ToWire() => $"GPSET {(int)_kind} {_index}";
}

/// <summary>Place a new gameplay handle; undo removes it.</summary>
public sealed class GameplayAddCommand : IEditCommand
{
    private readonly EditableGameplay _gp;
    private readonly GpKind _kind;
    private readonly object _item;
    private int _index = -1;
    private readonly System.Action? _onChanged;

    public GameplayAddCommand(EditableGameplay gp, GpKind kind, object item, System.Action? onChanged)
    { _gp = gp; _kind = kind; _item = item; _onChanged = onChanged; }

    /// <summary>Index the handle landed at (for selecting it after placement).</summary>
    public int Index => _index;

    public void Apply(StaticObjectsFile _)
    {
        if (_index < 0) _index = _gp.Add(_kind, _item);
        else _gp.Insert(_kind, _index, _item);
        _onChanged?.Invoke();
    }
    public void Undo(StaticObjectsFile _) { _gp.RemoveAt(_kind, _index); _onChanged?.Invoke(); }
    public string ToWire() => $"GPADD {(int)_kind}";
}

/// <summary>Delete a gameplay handle; undo re-inserts it at the same index.</summary>
public sealed class GameplayDeleteCommand : IEditCommand
{
    private readonly EditableGameplay _gp;
    private readonly GpKind _kind;
    private readonly int _index;
    private object? _snapshot;
    private readonly System.Action? _onChanged;

    public GameplayDeleteCommand(EditableGameplay gp, GpKind kind, int index, System.Action? onChanged)
    { _gp = gp; _kind = kind; _index = index; _onChanged = onChanged; }

    public void Apply(StaticObjectsFile _)
    {
        _snapshot ??= _gp.GetItem(_kind, _index);
        _gp.RemoveAt(_kind, _index); _onChanged?.Invoke();
    }
    public void Undo(StaticObjectsFile _) { if (_snapshot is not null) _gp.Insert(_kind, _index, _snapshot); _onChanged?.Invoke(); }
    public string ToWire() => $"GPDEL {(int)_kind} {_index}";
}

/// <summary>Encodes/decodes commands to the wire form used for persistence and collaboration.</summary>
public static class EditWire
{
    public static IEditCommand Parse(string line)
    {
        var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return p[0] switch
        {
            "MOVE"  => new MoveObject(p[1], Vec3.Parse(p[2])),
            "ROT"   => new RotateObject(p[1], Vec3.Parse(p[2])),
            "SCALE" => new ScaleObject(p[1], float.Parse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture)),
            "ADD"   => new AddObject(p[1], p[2], Vec3.Parse(p[3]), Vec3.Parse(p[4])),
            "DEL"   => new DeleteObject(p[1]),
            _ => throw new FormatException($"Unknown command '{p[0]}'"),
        };
    }

    /// <summary>
    /// Whether a wire line is an OBJECT edit — the only kind <see cref="Parse"/> understands. The same session also
    /// carries world ops (TERRAIN, MATERIAL, GAMEPLAY, WATER, OVERGROWTH, OBJMESH, ATLAS) which belong to the world
    /// state rather than the object document, so anything reading a live stream must ask this before parsing.
    /// Getting it wrong is not a parse error you can shrug off: it throws out of the socket read loop and takes the
    /// whole connection down with it.
    /// </summary>
    public static bool IsObjectOp(string line)
    {
        if (string.IsNullOrEmpty(line)) return false;
        int sp = line.IndexOf(' ');
        var verb = sp < 0 ? line : line[..sp];
        return verb is "MOVE" or "ROT" or "SCALE" or "ADD" or "DEL";
    }
}
