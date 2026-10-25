using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Ports;

namespace Lattice.Core.Selectors;

/// <summary>A box between two corners, inclusive, corners in any order.</summary>
internal readonly record struct CellBox(Cell Min, Cell Max)
{
    public static CellBox Between(Cell corner, Cell opposite) => new(
        new Cell(System.Math.Min(corner.X, opposite.X), System.Math.Min(corner.Y, opposite.Y), System.Math.Min(corner.Z, opposite.Z)),
        new Cell(System.Math.Max(corner.X, opposite.X), System.Math.Max(corner.Y, opposite.Y), System.Math.Max(corner.Z, opposite.Z)));

    public bool Contains(Cell cell) =>
        cell.X >= Min.X && cell.X <= Max.X && cell.Y >= Min.Y && cell.Y <= Max.Y && cell.Z >= Min.Z && cell.Z <= Max.Z;
}

/// <summary>
/// The cell keys selectors probe. Every cell key, large or small, lies on the small grid's lattice: small cells centre
/// on every half metre and large cells on odd metres, both multiples of the small step (<see cref="Grid.CubeGrid"/>).
/// Steps are read from the grid, not assumed.
/// </summary>
internal sealed class GridSpace
{
    public GridSpace(IGrid grid)
    {
        Grid = grid;
        SmallStep = grid.Neighbour(default, Direction.XPlus, GridSize.Small).X;
        LargeStep = grid.Neighbour(default, Direction.XPlus, GridSize.Large).X;
    }

    public IGrid Grid { get; }

    /// <summary>The small grid's step in cell keys (decimetres).</summary>
    public int SmallStep { get; }

    /// <summary>The large grid's step in cell keys (decimetres).</summary>
    public int LargeStep { get; }

    /// <summary>Adds every lattice key in the box. Linear in the keys it holds.</summary>
    public void Keys(CellBox box, List<Cell> into)
    {
        for (var x = Up(box.Min.X); x <= box.Max.X; x += SmallStep)
            for (var y = Up(box.Min.Y); y <= box.Max.Y; y += SmallStep)
                for (var z = Up(box.Min.Z); z <= box.Max.Z; z += SmallStep)
                    into.Add(new Cell(x, y, z));
    }

    /// <summary>
    /// The keys inside or on the surface of a set of large cells (<paramref name="inside"/>), and the large cells next
    /// to them across a face that are not in the set (<paramref name="beside"/>). Linear in the cells.
    /// </summary>
    public void AroundLargeCells(IReadOnlyList<Cell> cells, HashSet<Cell> inside, HashSet<Cell> beside)
    {
        var half = LargeStep / 2;
        var set = new HashSet<Cell>(cells);
        foreach (var cell in cells)
        {
            Keys(new CellBox(
                new Cell(cell.X - half, cell.Y - half, cell.Z - half),
                new Cell(cell.X + half, cell.Y + half, cell.Z + half)), Buffer);
            foreach (var key in Buffer)
                inside.Add(key);
            Buffer.Clear();
            for (var direction = Direction.XPlus; direction <= Direction.ZMinus; direction++)
            {
                var next = Grid.Neighbour(cell, direction, GridSize.Large);
                if (!set.Contains(next))
                    beside.Add(next);
            }
        }
    }

    /// <summary>
    /// Whether a large piece in a cell next to a room bounds it: it fills that cell's centre, or it takes the face
    /// between that cell and the room, from either side.
    /// </summary>
    public bool Bounds(PlacedShape shape, Cell beside, HashSet<Cell> room)
    {
        foreach (var spot in shape.Spots)
        {
            if (spot.Slot == Slot.Centre && spot.Cell == beside)
                return true;
            if (!spot.IsFace)
                continue;
            var (negative, positive) = Faces.Sides(spot, Grid);
            if ((negative == beside && room.Contains(positive)) || (positive == beside && room.Contains(negative)))
                return true;
        }

        return false;
    }

    private List<Cell> Buffer { get; } = new(125);

    private int Up(int value)
    {
        var rest = ((value % SmallStep) + SmallStep) % SmallStep;
        return rest == 0 ? value : value + SmallStep - rest;
    }
}
