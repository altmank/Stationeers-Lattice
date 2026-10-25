using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Core.BuiltWorld;

/// <summary>
/// The world mirror's structures at one mirror revision, never changed after it is made: the worker (support,
/// validation) reads it off the main thread while the mirror moves on. Reads are constant cost and allocate nothing.
/// </summary>
internal sealed class MirrorSnapshot
{
    private readonly Dictionary<ReferenceId, StructureInfo> _byId;
    private readonly Dictionary<Cell, ValueList<StructureInfo>> _byCell;

    internal MirrorSnapshot(long revision, Dictionary<ReferenceId, StructureInfo> byId, Dictionary<Cell, ValueList<StructureInfo>> byCell)
    {
        Revision = revision;
        _byId = byId;
        _byCell = byCell;
    }

    /// <summary>The mirror revision it was taken at.</summary>
    public long Revision { get; }

    /// <summary>How many structures it holds.</summary>
    public int Count => _byId.Count;

    /// <summary>A structure by id.</summary>
    public Option<StructureInfo> Structure(ReferenceId id) =>
        _byId.TryGetValue(id, out var info) ? Option.Some(info) : Option<StructureInfo>.None;

    /// <summary>The structures any of whose spots lie in a cell, in id order.</summary>
    public ValueList<StructureInfo> At(Cell cell) =>
        _byCell.TryGetValue(cell, out var here) ? here : ValueList<StructureInfo>.Empty;
}
