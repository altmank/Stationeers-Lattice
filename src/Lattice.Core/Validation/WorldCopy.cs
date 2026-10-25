using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.BuiltWorld;

namespace Lattice.Core.Validation;

/// <summary>
/// The worker's copy of the built world's structures: the world mirror's snapshot taken once at start, plus every
/// change the mirror reported since, so no snapshot is copied per change. Cells mean what they mean in the mirror:
/// a structure is in its own cell and in every cell its catalogue spots lie in. Worker only; not thread-safe.
/// </summary>
internal sealed class WorldCopy(MirrorSnapshot start, Shapes shapes)
{
    private readonly Dictionary<ReferenceId, StructureInfo?> _changed = [];
    private readonly Dictionary<ReferenceId, List<Cell>> _cellsOf = [];
    private readonly Dictionary<Cell, List<StructureInfo>> _added = [];

    public Option<StructureInfo> Structure(ReferenceId id)
    {
        if (_changed.TryGetValue(id, out var now))
            return now is null ? Option<StructureInfo>.None : Option.Some(now);
        return start.Structure(id);
    }

    /// <summary>The structures in a cell, in id order. Allocates nothing where no change touched the cell.</summary>
    public ValueList<StructureInfo> At(Cell cell)
    {
        var base_ = start.At(cell);
        _added.TryGetValue(cell, out var added);
        var touched = added is { Count: > 0 };
        if (!touched)
            foreach (var info in base_)
                if (_changed.ContainsKey(info.Ref.Id))
                {
                    touched = true;
                    break;
                }

        if (!touched)
            return base_;
        var here = new List<StructureInfo>(base_.Count + (added?.Count ?? 0));
        foreach (var info in base_)
            if (!_changed.ContainsKey(info.Ref.Id))
                here.Add(info);
        if (added is not null)
            here.AddRange(added);
        here.Sort(static (a, b) => a.Ref.Id.Value.CompareTo(b.Ref.Id.Value));
        return ValueList.From(here);
    }

    /// <summary>
    /// A copy that later changes do not touch, for reads on another thread (worker analyzers). Linear in the changes
    /// taken in since the start snapshot, which is shared (it never changes).
    /// </summary>
    public WorldCopy Freeze()
    {
        var copy = new WorldCopy(start, shapes);
        foreach (var changed in _changed)
            copy._changed.Add(changed.Key, changed.Value);
        foreach (var added in _added)
            copy._added.Add(added.Key, new List<StructureInfo>(added.Value));
        return copy;
    }

    /// <summary>The cells a structure is listed in.</summary>
    public List<Cell> CellsOf(ReferenceId id) =>
        _cellsOf.TryGetValue(id, out var cells) ? cells : Structure(id).TryGet(out var info) ? Cells(info) : [];

    /// <summary>Takes in one change the mirror reported; gives the cells whose contents changed.</summary>
    public List<Cell> Apply(MirrorChange change)
    {
        var touched = new List<Cell>(4);
        switch (change)
        {
            case MirrorChange.Appeared appeared:
                Put(appeared.Now, touched);
                break;
            case MirrorChange.Altered altered:
                Drop(altered.Before, touched);
                Put(altered.Now, touched);
                break;
            case MirrorChange.Gone gone:
                Drop(gone.Before, touched);
                _changed[gone.Before.Ref.Id] = null;
                _cellsOf.Remove(gone.Before.Ref.Id);
                break;
            default:
                throw new InvalidOperationException("Unknown mirror change " + change.GetType().Name);
        }

        return touched;
    }

    private void Put(StructureInfo info, List<Cell> touched)
    {
        var id = info.Ref.Id;
        var cells = Cells(info);
        _changed[id] = info;
        _cellsOf[id] = cells;
        foreach (var cell in cells)
        {
            if (!_added.TryGetValue(cell, out var here))
                _added.Add(cell, here = new List<StructureInfo>(2));
            here.Add(info);
            if (!touched.Contains(cell))
                touched.Add(cell);
        }
    }

    private void Drop(StructureInfo before, List<Cell> touched)
    {
        var id = before.Ref.Id;
        var cells = _cellsOf.TryGetValue(id, out var known) ? known : Cells(before);
        foreach (var cell in cells)
        {
            if (_added.TryGetValue(cell, out var here))
            {
                here.RemoveAll(info => info.Ref.Id == id);
                if (here.Count == 0)
                    _added.Remove(cell);
            }

            if (!touched.Contains(cell))
                touched.Add(cell);
        }

        _changed[id] = null;
    }

    private List<Cell> Cells(StructureInfo info)
    {
        var cells = new List<Cell>(4) { info.Ref.At };
        if (shapes.Of(new Placement(info.Ref.Piece, info.Ref.At, info.Turn)).TryGet(out var shape))
            foreach (var spot in shape.Spots)
                if (!cells.Contains(spot.Cell))
                    cells.Add(spot.Cell);
        return cells;
    }
}
