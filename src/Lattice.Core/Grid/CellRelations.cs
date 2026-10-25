using System;
using Lattice.Api;
using Lattice.Core.Ports;

namespace Lattice.Core.Grid;

/// <summary>How two cells of one grid touch. Directions point from the first cell to the second, in X, Y, Z order.</summary>
internal abstract record Contact
{
    private Contact()
    {
    }

    /// <summary>The cells do not touch.</summary>
    public sealed record Apart : Contact;

    /// <summary>The same cell.</summary>
    public sealed record Same : Contact;

    /// <summary>The cells share a face.</summary>
    public sealed record Face(Direction Towards) : Contact;

    /// <summary>The cells share an edge.</summary>
    public sealed record Edge(Direction First, Direction Second) : Contact;

    /// <summary>The cells share a corner.</summary>
    public sealed record Corner(Direction X, Direction Y, Direction Z) : Contact;
}

/// <summary>
/// Where a small cell sits in a large cell. A small cell on a face plane, edge or corner sits there in every large cell
/// that shares it. Directions point from the large cell's centre, in X, Y, Z order.
/// </summary>
internal abstract record SmallPlace
{
    private SmallPlace()
    {
    }

    /// <summary>Not in the large cell or on its boundary.</summary>
    public sealed record Outside : SmallPlace;

    /// <summary>Strictly inside: one of the 27 interior small cells.</summary>
    public sealed record Inside : SmallPlace;

    /// <summary>On one face plane.</summary>
    public sealed record OnFace(Direction Side) : SmallPlace;

    /// <summary>On the edge where two face planes meet.</summary>
    public sealed record OnEdge(Direction First, Direction Second) : SmallPlace;

    /// <summary>On a corner.</summary>
    public sealed record OnCorner(Direction X, Direction Y, Direction Z) : SmallPlace;
}

/// <summary>
/// Face, edge and corner relations between cells. A large cell's faces sit 10 dm from its centre, on the small cells
/// it shares with its neighbours. Constant cost.
/// </summary>
internal static class CellRelations
{
    private const int LargeHalf = 10;

    /// <summary>How <paramref name="to"/> touches <paramref name="from"/> on the grid.</summary>
    public static Contact Between(Cell from, Cell to, GridSize grid)
    {
        var step = CubeGrid.Step(grid);
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var dz = to.Z - from.Z;
        if (!IsWithinStep(dx, step) || !IsWithinStep(dy, step) || !IsWithinStep(dz, step))
            return new Contact.Apart();

        Span<Direction> towards = stackalloc Direction[3];
        var count = Collect(dx, dy, dz, towards);
        return count switch
        {
            0 => new Contact.Same(),
            1 => new Contact.Face(towards[0]),
            2 => new Contact.Edge(towards[0], towards[1]),
            3 => new Contact.Corner(towards[0], towards[1], towards[2]),
            _ => throw new InvalidOperationException("a cell has three axes"),
        };
    }

    /// <summary>The key of a large cell's face point: 10 dm from its centre along the direction, as the game's <c>Grid3.Face</c>.</summary>
    public static Cell FaceOf(Cell large, Direction side) => large.Plus(side.Unit().Times(LargeHalf));

    /// <summary>Where the small cell sits in the large cell.</summary>
    public static SmallPlace PlaceOf(Cell small, Cell large)
    {
        var dx = small.X - large.X;
        var dy = small.Y - large.Y;
        var dz = small.Z - large.Z;
        if (Math.Abs(dx) > LargeHalf || Math.Abs(dy) > LargeHalf || Math.Abs(dz) > LargeHalf)
            return new SmallPlace.Outside();

        Span<Direction> sides = stackalloc Direction[3];
        var count = Collect(OnBoundary(dx), OnBoundary(dy), OnBoundary(dz), sides);
        return count switch
        {
            0 => new SmallPlace.Inside(),
            1 => new SmallPlace.OnFace(sides[0]),
            2 => new SmallPlace.OnEdge(sides[0], sides[1]),
            3 => new SmallPlace.OnCorner(sides[0], sides[1], sides[2]),
            _ => throw new InvalidOperationException("a cell has three axes"),
        };
    }

    private static bool IsWithinStep(int delta, int step) => delta == 0 || Math.Abs(delta) == step;

    private static int OnBoundary(int delta) => Math.Abs(delta) == LargeHalf ? delta : 0;

    private static int Collect(int x, int y, int z, Span<Direction> into)
    {
        var count = 0;
        if (x != 0)
            into[count++] = DirectionMath.Along(Axis.X, x);
        if (y != 0)
            into[count++] = DirectionMath.Along(Axis.Y, y);
        if (z != 0)
            into[count++] = DirectionMath.Along(Axis.Z, z);
        return count;
    }
}
