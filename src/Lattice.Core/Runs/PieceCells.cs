using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Grid;
using Lattice.Core.Ports;

namespace Lattice.Core.Runs;

/// <summary>
/// The cells a placed piece fills and the ways it leaves each. Cells: its footprint, the cells its ends leave from,
/// and for a piece with two opposite ends on one line every cell between them (the game registers a long straight in
/// each cell it spans). A cell's ways: its ends leaving there, and the links to
/// the piece's own neighbouring cells (a long straight's middle cell runs both ways along it).
/// </summary>
internal static class PieceCells
{
    public static ValueList<Cell> Of(PlacedShape shape, IGrid grid)
    {
        var cells = new List<Cell>(shape.Spots.Count + shape.Ends.Count);
        foreach (var spot in shape.Spots)
            AddOnce(cells, spot.Cell);
        foreach (var end in shape.Ends)
            AddOnce(cells, end.Cell);

        if (shape.Ends.Count == 2 && shape.Ends[0].Facing == shape.Ends[1].Facing.Opposite() && shape.Ends[0].Cell != shape.Ends[1].Cell)
        {
            var size = shape.Piece.Grid;
            var (from, to) = (shape.Ends[0].Cell, shape.Ends[1].Cell);
            var along = shape.Ends[1].Facing;
            var step = grid.Neighbour(from, along, size);
            var unit = new CellOffset(step.X - from.X, step.Y - from.Y, step.Z - from.Z);
            var delta = new CellOffset(to.X - from.X, to.Y - from.Y, to.Z - from.Z);
            var axis = along.AxisOf();
            var steps = unit.On(axis) == 0 ? 0 : delta.On(axis) / unit.On(axis);
            if (steps > 0 && unit.Times(steps) == delta)
                for (var (cell, walked) = (from, 1); walked < steps; walked++)
                    AddOnce(cells, cell = cell.Plus(unit));
        }

        return ValueList.From(cells);
    }

    public static Directions At(Occupant occupant, Cell cell, IGrid grid)
    {
        var ways = Directions.None;
        foreach (var end in occupant.Shape.Ends)
            if (end.Cell == cell)
                ways = ways.With(end.Facing);
        for (var direction = Direction.XPlus; direction <= Direction.ZMinus; direction++)
            if (Holds(occupant.Cells, grid.Neighbour(cell, direction, occupant.Shape.Piece.Grid)))
                ways = ways.With(direction);
        return ways;
    }

    private static bool Holds(ValueList<Cell> cells, Cell cell)
    {
        foreach (var held in cells)
            if (held == cell)
                return true;
        return false;
    }

    private static void AddOnce(List<Cell> cells, Cell cell)
    {
        if (!cells.Contains(cell))
            cells.Add(cell);
    }
}
