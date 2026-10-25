using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Grid;
using Lattice.Core.Ports;

namespace Lattice.Tests.Grid;

/// <summary>Shared values for the grid tests.</summary>
internal static class Turns
{
    public static readonly IGrid Cube = new CubeGrid();

    public static IEnumerable<Turn> All()
    {
        for (var i = 0; i < Turn.Count; i++)
            yield return Of(i);
    }

    public static Turn Of(int index) => Turn.Create(index).TryGet(out var turn) ? turn : throw new KeyNotFoundException($"turn {index}");

    public static readonly Direction[] Directions =
        [Direction.XPlus, Direction.XMinus, Direction.YPlus, Direction.YMinus, Direction.ZPlus, Direction.ZMinus];

    public static readonly Axis[] Axes = [Axis.X, Axis.Y, Axis.Z];

    public static CellOffset Unit(Direction direction) => direction switch
    {
        Direction.XPlus => new(1, 0, 0),
        Direction.XMinus => new(-1, 0, 0),
        Direction.YPlus => new(0, 1, 0),
        Direction.YMinus => new(0, -1, 0),
        Direction.ZPlus => new(0, 0, 1),
        Direction.ZMinus => new(0, 0, -1),
        _ => throw new KeyNotFoundException(direction.ToString()),
    };
}
