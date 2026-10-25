using System;
using Lattice.Api;
using Lattice.Core.Ports;

namespace Lattice.TestKit.Grid;

/// <summary>
/// <see cref="IGrid"/> on the test kit's turn table. Cells are the game's Grid3 keys in decimetres
/// (round(position * 10)); large cells are 2 m with centres at odd metres, small cells 0.5 m with centres on every
/// multiple of 0.5 m (GridCenter, SmallGridSize).
/// </summary>
internal sealed class FakeGrid : IGrid
{
    public static FakeGrid Instance { get; } = new();

    /// <summary>The distance between neighbouring cell centres, in decimetres.</summary>
    public static int Step(GridSize grid) => grid == GridSize.Large ? 20 : 5;

    public Cell Snap(WorldPosition position, GridSize grid) =>
        new(Key(SnapAxis(position.X, grid)), Key(SnapAxis(position.Y, grid)), Key(SnapAxis(position.Z, grid)));

    public WorldPosition CentreOf(Cell cell, GridSize grid) => new(cell.X / 10f, cell.Y / 10f, cell.Z / 10f);

    public Cell Neighbour(Cell cell, Direction direction, GridSize grid)
    {
        var step = Vector3i.Of(direction).Times(Step(grid));
        return cell.Plus(new CellOffset(step.X, step.Y, step.Z));
    }

    public Turn Compose(Turn first, Turn then) => TurnTable.Of(TurnTable.Of(then).After(TurnTable.Of(first)));

    public Turn Invert(Turn turn) => TurnTable.Of(TurnTable.Of(turn).Transposed());

    public Direction Apply(Turn turn, Direction direction) => TurnTable.Of(turn).Apply(Vector3i.Of(direction)).ToDirection();

    public CellOffset Apply(Turn turn, CellOffset offset)
    {
        var v = TurnTable.Of(turn).Apply(new Vector3i(offset.X, offset.Y, offset.Z));
        return new CellOffset(v.X, v.Y, v.Z);
    }

    public Direction Reflect(Direction direction, Axis normal)
    {
        var v = Vector3i.Of(direction);
        return (normal switch
        {
            Axis.X => v with { X = -v.X },
            Axis.Y => v with { Y = -v.Y },
            Axis.Z => v with { Z = -v.Z },
            _ => throw new ArgumentOutOfRangeException(nameof(normal), normal, null),
        }).ToDirection();
    }

    public Cell Reflect(Cell cell, MirrorPlane plane) => plane.Normal switch
    {
        Axis.X => cell with { X = 2 * plane.At - cell.X },
        Axis.Y => cell with { Y = 2 * plane.At - cell.Y },
        Axis.Z => cell with { Z = 2 * plane.At - cell.Z },
        _ => throw new ArgumentOutOfRangeException(nameof(plane), plane, null),
    };

    private static double SnapAxis(double metres, GridSize grid) => grid == GridSize.Large
        ? Math.Floor(metres / 2) * 2 + 1
        : Math.Round((metres - 0.5) / 0.5) * 0.5 + 0.5;

    private static int Key(double metres) => (int)Math.Round(metres * 10);
}
