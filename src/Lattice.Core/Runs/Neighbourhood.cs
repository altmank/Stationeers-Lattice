using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Grid;
using Lattice.Core.Ports;

namespace Lattice.Core.Runs;

/// <summary>What holds a piece in the planned world: a real structure, or a build or swap ghost.</summary>
internal abstract record Holder
{
    private Holder() { }

    /// <summary>A structure that stands and no ghost removes, swaps or displaces.</summary>
    public sealed record Real(StructureRef Structure) : Holder;

    /// <summary>A build or swap ghost's new piece; a swap ghost's structure still stands until the swap is built.</summary>
    public sealed record Planned(GhostId Ghost, Option<StructureRef> Swapping) : Holder;

    /// <summary>The real structure that stands in the piece's place now: the real one, or the one a swap ghost replaces.</summary>
    public Option<StructureRef> Standing => this switch
    {
        Real real => Option.Some(real.Structure),
        Planned planned => planned.Swapping,
        _ => Option<StructureRef>.None,
    };
}

/// <summary>A piece in the planned world, its turned shape, and the cells it fills.</summary>
internal sealed record Occupant(PlacedShape Shape, Holder Holder, ValueList<Cell> Cells);

/// <summary>
/// The planned world around a run, as one actor sees it: the real structures, less the ones a remove or swap ghost
/// targets and the ones a build ghost displaces from their slot, plus the new pieces of build and swap ghosts. Reads
/// are cached per cell for the life of the instance (one plan).
/// </summary>
internal sealed class Neighbourhood(Shapes shapes, IGrid grid, IGhostBoard board, IWorldQuery world)
{
    private readonly Dictionary<Cell, List<Occupant>> _at = [];

    /// <summary>The planned pieces that fill a cell.</summary>
    public IReadOnlyList<Occupant> At(Cell cell)
    {
        if (_at.TryGetValue(cell, out var known))
            return known;

        var here = new List<Occupant>(2);
        foreach (var ghost in board.At(cell))
            if (NewPiece(ghost.Intent) is { } placement && shapes.Of(placement).TryGet(out var shape))
                here.Add(new Occupant(shape, new Holder.Planned(ghost.Id, Swapped(ghost.Intent)), PieceCells.Of(shape, grid)));

        foreach (var structure in world.At(cell))
        {
            if (Taken(structure.Ref) || !shapes.Of(new Placement(structure.Ref.Piece, structure.Ref.At, structure.Turn)).TryGet(out var shape))
                continue;
            if (!Displaced(shape, cell, here))
                here.Add(new Occupant(shape, new Holder.Real(structure.Ref), PieceCells.Of(shape, grid)));
        }

        _at[cell] = here;
        return here;
    }

    /// <summary>The ways into a cell from its neighbours: each neighbour with an end leaving towards the cell that joins the kit.</summary>
    public Directions EndsInto(Cell cell, KitShapes kit)
    {
        var into = Directions.None;
        for (var direction = Direction.XPlus; direction <= Direction.ZMinus; direction++)
        {
            var next = grid.Neighbour(cell, direction, kit.Grid);
            var back = direction.Opposite();
            foreach (var occupant in At(next))
            {
                var ends = occupant.Shape.Ends;
                for (var i = 0; i < ends.Count; i++)
                    if (ends[i].Cell == next && ends[i].Facing == back && kit.Joins(occupant.Shape.Piece.Ends[i]))
                        into = into.With(direction);
            }
        }

        return into;
    }

    /// <summary>The kit's piece that fills a cell in the planned world, if any.</summary>
    public Occupant? OfKit(Cell cell, KitShapes kit)
    {
        foreach (var occupant in At(cell))
            if (kit.Places(occupant.Shape.Piece))
                return occupant;
        return null;
    }

    private static Placement? NewPiece(Intent intent) => intent switch
    {
        Intent.Build build => build.Target,
        Intent.Swap swap => swap.New,
        _ => null,
    };

    private static Option<StructureRef> Swapped(Intent intent) =>
        intent is Intent.Swap swap ? Option.Some(swap.Old) : Option<StructureRef>.None;

    private bool Taken(StructureRef structure)
    {
        foreach (var ghost in board.On(structure))
            if (ghost.Intent is Intent.Remove or Intent.Swap)
                return true;
        return false;
    }

    private static bool Displaced(PlacedShape real, Cell cell, List<Occupant> planned)
    {
        foreach (var spot in real.Spots)
        {
            if (spot.Cell != cell)
                continue;
            foreach (var occupant in planned)
                foreach (var theirs in occupant.Shape.Spots)
                    if (theirs == spot)
                        return true;
        }

        return false;
    }
}
