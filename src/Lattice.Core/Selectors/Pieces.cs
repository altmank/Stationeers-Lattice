using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.BuiltWorld;
using Lattice.Core.Ports;

namespace Lattice.Core.Selectors;

/// <summary>
/// The board's ghosts through its cell index. A ghost's own cell is its new piece's cell (build, swap) or its target's
/// (remove, finish); only build and swap ghosts have a shape.
/// </summary>
internal sealed class GhostPieces(BoardIndex index) : IHeldPieces<GhostId>
{
    public void At(Cell cell, List<Held<GhostId>> into)
    {
        foreach (var id in index.InCell(cell))
            into.Add(Of(index.Ghosts[id]));
    }

    public static Held<GhostId> Of(GhostEntry entry) =>
        new(entry.Id, BoardIndex.OwnCell(entry), entry.Shape);
}

/// <summary>The mirror's structures through its cell index, each shaped by the catalogue (none for a piece it does not hold).</summary>
internal sealed class StructurePieces(WorldMirror mirror, Shapes shapes) : IHeldPieces<ReferenceId>
{
    public void At(Cell cell, List<Held<ReferenceId>> into)
    {
        foreach (var info in mirror.At(cell))
            into.Add(Of(info));
    }

    public Held<ReferenceId> Of(StructureInfo info) =>
        new(info.Ref.Id, info.Ref.At, ShapeOf(info));

    public Option<PlacedShape> ShapeOf(StructureInfo info) =>
        shapes.Of(new Placement(info.Ref.Piece, info.Ref.At, info.Turn))
            .Match(static shape => Option.Some(shape), static _ => Option<PlacedShape>.None);
}

/// <summary>Room selections, on ghosts or on structures alike.</summary>
internal static class Picks
{
    /// <summary>
    /// The holders in or bounding a room: anything with a spot or its own cell inside a room cell or on its surface,
    /// and a large piece in a cell beside the room that fills that cell or takes the face the room lies across. Probes
    /// the room's keys and the cells beside it; never looks past them. Linear in the room's cells and what they hold.
    /// </summary>
    public static HashSet<TId> Room<TId>(IHeldPieces<TId> pieces, IReadOnlyList<Cell> cells, GridSpace space) where TId : notnull
    {
        var inside = new HashSet<Cell>();
        var beside = new HashSet<Cell>();
        space.AroundLargeCells(cells, inside, beside);
        var room = new HashSet<Cell>(cells);
        var picked = new HashSet<TId>();
        var found = new List<Held<TId>>();
        foreach (var key in inside)
            pieces.At(key, found);
        foreach (var held in found)
            picked.Add(held.Id);

        foreach (var cell in beside)
        {
            found.Clear();
            pieces.At(cell, found);
            foreach (var held in found)
                if (held.Shape.TryGet(out var shape) && !shape.IsSmall && space.Bounds(shape, cell, room))
                    picked.Add(held.Id);
        }

        return picked;
    }
}
