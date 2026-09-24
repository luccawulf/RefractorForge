namespace RefractorForge.Formats.Menu;

/// <summary>Where a record sits: the field that holds it (an O slot, or a LIST at <see cref="Index"/>), on
/// <see cref="Parent"/> or on a tree node of it (<see cref="TreeNode"/>).</summary>
public sealed record MemeSlot(MemeRecord Parent, MemeField Field, int Index = -1, MemeTreeNode? TreeNode = null)
{
    public bool IsListItem => Index >= 0;

    public MemeRecord? Current => IsListItem ? Field.Items?[Index] : Field.Value as MemeRecord;
}

/// <summary>
/// The edits a menu editor makes, each keeping the file one the writer takes and the engine reads as shown.
/// <para>Two rules of the format drive them. Names are identity: the engine keeps the first definition of a name and
/// skips every later copy's payload, so a value edit belongs on every same-named copy (<see cref="SetEverywhere"/>).
/// And "Next node" is a continuation, not a child: a node's siblings hang off its Next node, so inserting or removing a
/// node splices a chain rather than replacing a subtree (<see cref="InsertAfter"/>, <see cref="Unlink"/>).</para>
/// </summary>
public static class MemeEdits
{
    /// <summary>Set <paramref name="label"/> on <paramref name="record"/> and on every other object of the same name in
    /// the file that has the field, so the change sticks whichever copy the engine meets first (docs/Meme_Format.md 5.2).
    /// A record value is deep-copied for each other copy, so the file stays a tree. Returns how many records changed.</summary>
    public static int SetEverywhere(MemeFile file, MemeRecord record, string label, object? value)
    {
        record.Set(label, value);
        int n = 1;
        if (record.Name.Length == 0) return n;
        var set = record.Field(label).Value;
        foreach (var copy in file.CopiesOf(record.Name))
        {
            if (copy == record || !copy.TryGetField(label, out var f) || f.Token != record.Field(label).Token) continue;
            f.Value = set switch
            {
                MemeRecord r => r.DeepClone(),
                List<MemeRecord> items => items.Select(x => x.DeepClone()).ToList(),
                _ => set,
            };
            n++;
        }
        return n;
    }

    /// <summary>Rename a record - and, with <paramref name="everyCopy"/>, every other object of its old name, which the
    /// engine treats as the same object. By-name references are left alone: they name menu FILES (every retail one
    /// does), not objects in this file. Returns how many records were renamed.</summary>
    public static int Rename(MemeFile file, MemeRecord record, string newName, bool everyCopy = true)
    {
        ArgumentNullException.ThrowIfNull(newName);
        if (MemeValues.NameProblem(newName, 0x80, "object name") is { } why) throw new ArgumentException(why, nameof(newName));
        var old = record.Name;
        var targets = everyCopy && old.Length > 0 ? file.CopiesOf(old).ToList() : new List<MemeRecord>();
        if (!targets.Contains(record)) targets.Add(record);
        foreach (var r in targets) r.Name = newName;
        return targets.Count;
    }

    /// <summary>Put an object, a reference or null (a null pointer) into an O slot; returns what was there.</summary>
    public static MemeRecord ReplaceSlot(MemeRecord parent, string label, MemeRecord? value)
    {
        var f = parent.Field(label);
        if (f.Token != MemeToken.O) throw new ArgumentException($"'{label}' is a {f.Token} field, not an O slot", nameof(label));
        var old = f.Value as MemeRecord ?? MemeRecord.Null();
        f.Set(value);
        return old;
    }

    /// <summary>Insert an item into a LIST (ActionListAction's Action list, DataListData's Data list). A null record
    /// cannot be an item - the reader ends the list there.</summary>
    public static void InsertListItem(MemeRecord parent, string label, int index, MemeRecord item)
    {
        var items = ListOf(parent, label);
        ArgumentNullException.ThrowIfNull(item);
        if (item.Kind == MemeRecordKind.Null)
            throw new ArgumentException("a null record cannot be a LIST item: the reader ends the list there", nameof(item));
        items.Insert(index, item);
    }

    /// <summary>Remove a LIST item; returns it.</summary>
    public static MemeRecord RemoveListItem(MemeRecord parent, string label, int index)
    {
        var items = ListOf(parent, label);
        var r = items[index];
        items.RemoveAt(index);
        return r;
    }

    private static List<MemeRecord> ListOf(MemeRecord parent, string label)
    {
        var f = parent.Field(label);
        if (f.Token != MemeToken.List) throw new ArgumentException($"'{label}' is a {f.Token} field, not a LIST", nameof(label));
        if (f.Value is not List<MemeRecord> items) f.Value = items = (f.Value as IEnumerable<MemeRecord>)?.ToList() ?? new List<MemeRecord>();
        return items;
    }

    /// <summary>Insert <paramref name="inserted"/> (a node, or a chain of nodes linked by Next node) into
    /// <paramref name="node"/>'s chain right after it: the inserted chain's last node continues with what followed
    /// <paramref name="node"/>. Drawn after <paramref name="node"/>, in the same parent rect.</summary>
    public static void InsertAfter(MemeRecord node, MemeRecord inserted)
    {
        var slot = node.NextField ?? throw new InvalidOperationException($"{node} has no Next node to insert after");
        var tail = ChainTail(inserted, node);
        tail.Next = slot.Value as MemeRecord ?? MemeRecord.Null();
        slot.Value = inserted;
    }

    /// <summary>Insert <paramref name="inserted"/> (a node or a chain) at the head of the chain in an O slot of
    /// <paramref name="parent"/> - e.g. a TransformNode's "Transformed node" or a SplitNode's "Split node" - with the
    /// chain that was there continuing after it.</summary>
    public static void InsertFirst(MemeRecord parent, string label, MemeRecord inserted)
    {
        var f = parent.Field(label);
        if (f.Token != MemeToken.O) throw new ArgumentException($"'{label}' is a {f.Token} field, not an O slot", nameof(label));
        var tail = ChainTail(inserted, parent);
        tail.Next = f.Value as MemeRecord ?? MemeRecord.Null();
        f.Value = inserted;
    }

    private static MemeRecord ChainTail(MemeRecord head, MemeRecord notIn)
    {
        var seen = new HashSet<MemeRecord>(ReferenceEqualityComparer.Instance);
        var t = head;
        while (true)
        {
            if (t == notIn || !seen.Add(t)) throw new InvalidOperationException($"inserting {head} there would make a cycle");
            if (t.NextField is null) throw new InvalidOperationException($"{t} has no Next node, so it cannot continue a chain");
            if (t.Next is { Kind: MemeRecordKind.Object } n) t = n;
            else return t;
        }
    }

    /// <summary>Take <paramref name="node"/> out of its chain, keeping the rest: whatever held it now holds its
    /// continuation (a LIST item with none is removed), and the node's own Next node is cleared. Returns false when the
    /// node is not in the file (or is the root).</summary>
    public static bool Unlink(MemeFile file, MemeRecord node)
    {
        if (FindSlot(file, node) is not { } slot) return false;
        var rest = node.NextField is not null ? node.Next ?? MemeRecord.Null() : MemeRecord.Null();
        if (slot.IsListItem)
        {
            var items = slot.Field.Items!;
            if (rest.Kind == MemeRecordKind.Null) items.RemoveAt(slot.Index);
            else items[slot.Index] = rest;
        }
        else slot.Field.Value = rest;
        if (node.NextField is not null) node.Next = MemeRecord.Null();
        return true;
    }

    /// <summary>A deep copy of a subtree, to paste elsewhere (<see cref="MemeRecord.DeepClone"/>).</summary>
    public static MemeRecord DeepCopy(MemeRecord record) => record.DeepClone();

    /// <summary>The slot that holds <paramref name="record"/> (by identity), or null when it is the root or not in the
    /// file. A record held by two slots gives the first in file order.</summary>
    public static MemeSlot? FindSlot(MemeFile file, MemeRecord record)
    {
        foreach (var r in file.Walk())
        {
            if (r.Fields is null) continue;
            foreach (var f in r.Fields)
            {
                switch (f.Token)
                {
                    case MemeToken.O when ReferenceEquals(f.Value, record):
                        return new MemeSlot(r, f);
                    case MemeToken.List when f.Value is List<MemeRecord> items:
                        for (int i = 0; i < items.Count; i++)
                            if (ReferenceEquals(items[i], record)) return new MemeSlot(r, f, i);
                        break;
                    case MemeToken.Tree when f.Value is MemeTreeNode t:
                        if (FindInTree(r, t, record) is { } s) return s;
                        break;
                }
            }
        }
        return null;
    }

    private static MemeSlot? FindInTree(MemeRecord owner, MemeTreeNode root, MemeRecord record)
    {
        var stack = new Stack<MemeTreeNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            foreach (var f in n.Fields)
                if (f.Token == MemeToken.O && ReferenceEquals(f.Value, record)) return new MemeSlot(owner, f, -1, n);
            if (n.Prev is not null) stack.Push(n.Prev);
            if (n.Next is not null) stack.Push(n.Next);
            if (n.Child is not null) stack.Push(n.Child);
        }
        return null;
    }
}
