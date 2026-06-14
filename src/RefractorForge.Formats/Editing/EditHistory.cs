using RefractorForge.Formats.Con;

namespace RefractorForge.Formats.Editing;

/// <summary>Undo/redo stack. Local edits go through <see cref="Do"/>; remote (collaboration)
/// edits should be applied directly so they don't pollute the local undo stack.</summary>
public sealed class EditHistory
{
    private readonly StaticObjectsFile _file;
    private readonly Stack<IEditCommand> _undo = new();
    private readonly Stack<IEditCommand> _redo = new();

    public EditHistory(StaticObjectsFile file) => _file = file;

    public int UndoDepth => _undo.Count;
    public int RedoDepth => _redo.Count;

    /// <summary>Fired after each locally-committed edit (via <see cref="Do"/> only — NOT undo/redo, and NOT
    /// remote edits applied directly to the file). Collaboration uses this to broadcast the local edit.</summary>
    public Action<IEditCommand>? OnDo;

    /// <summary>Fired after a local <see cref="Undo"/> or <see cref="Redo"/>, with the command that was reversed
    /// or re-applied. Collaboration uses this to broadcast the resulting (now-current) state so peers converge —
    /// without this, an undo would only happen locally. Not fired for remote edits.</summary>
    public Action<IEditCommand>? OnUndoRedo;

    public void Do(IEditCommand cmd)
    {
        cmd.Apply(_file);
        _undo.Push(cmd);
        _redo.Clear();
        OnDo?.Invoke(cmd);
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var c = _undo.Pop(); c.Undo(_file); _redo.Push(c);
        OnUndoRedo?.Invoke(c);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var c = _redo.Pop(); c.Apply(_file); _undo.Push(c);
        OnUndoRedo?.Invoke(c);
        return true;
    }
}
