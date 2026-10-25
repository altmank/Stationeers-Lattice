using System;
using Lattice.Api;
using Lattice.Core.Ports;

namespace Lattice.Core.Grid;

/// <summary>
/// The game's two grids as pure math. Cells are keyed by their centre in decimetres, as the game's <c>Grid3</c>:
/// large cells (2 m) centre on odd metres, small cells (0.5 m) on every half metre.
/// Every call is constant cost.
/// </summary>
internal sealed class CubeGrid : IGrid
{
    private readonly TurnTable _turns = new();

    /// <summary>
    /// The cell holding the position, as the game snaps: <c>GridCenter</c> with size 2 m offset 0 (large) or 0.5 m
    /// offset 0.25 m (small), then <c>Grid3</c>'s key (decompile <c>Assets.Scripts.Util/ExtensionMethods.cs</c>
    /// <c>GridCenter</c>, <c>Assets.Scripts.Objects/SmallGrid.cs</c> <c>SmallGridSize</c>/<c>SmallGridOffset</c>,
    /// <c>Assets.Scripts.GridSystem/Grid3.cs</c>): the contract's <see cref="Cell.FromWorld"/>, so a consumer's aim
    /// and Lattice's placements snap alike.
    /// </summary>
    public Cell Snap(WorldPosition position, GridSize grid) =>
        Cell.FromWorld(new WorldPoint(position.X, position.Y, position.Z), PieceGridOf(grid));

    /// <summary>The cell's centre in metres: its key times 0.1, as <c>GridController.LocalToWorld</c>.</summary>
    public WorldPosition CentreOf(Cell cell, GridSize grid) => new(cell.X * 0.1f, cell.Y * 0.1f, cell.Z * 0.1f);

    /// <summary>The next cell along the direction: 20 dm on the large grid, 5 dm on the small (<see cref="Cell.Neighbour"/>).</summary>
    public Cell Neighbour(Cell cell, Direction direction, GridSize grid) => cell.Neighbour(direction.ToFace(), PieceGridOf(grid));

    /// <inheritdoc cref="TurnTable.Compose"/>
    public Turn Compose(Turn first, Turn then) => _turns.Compose(first, then);

    /// <inheritdoc cref="TurnTable.Invert"/>
    public Turn Invert(Turn turn) => _turns.Invert(turn);

    /// <summary>The direction after the turn.</summary>
    public Direction Apply(Turn turn, Direction direction) => _turns.RotationOf(turn).Apply(direction);

    /// <summary>The offset after the turn, about the offset's origin.</summary>
    public CellOffset Apply(Turn turn, CellOffset offset) => _turns.RotationOf(turn).Apply(offset);

    /// <summary>The direction seen in a mirror square to <paramref name="normal"/>: flipped when it runs along it.</summary>
    public Direction Reflect(Direction direction, Axis normal) =>
        direction.AxisOf() == normal ? direction.Opposite() : direction;

    /// <summary>
    /// The cell seen in the mirror: the coordinate along the plane's normal becomes <c>2 * At - c</c>. The result is a
    /// cell centre of the grid only when the plane fits it (<see cref="Mirror.Fit"/>).
    /// </summary>
    public Cell Reflect(Cell cell, MirrorPlane plane) => plane.Normal switch
    {
        Axis.X => cell with { X = 2 * plane.At - cell.X },
        Axis.Y => cell with { Y = 2 * plane.At - cell.Y },
        Axis.Z => cell with { Z = 2 * plane.At - cell.Z },
        _ => throw DirectionMath.NotAnAxis(plane.Normal),
    };

    /// <summary>One cell in decimetres: 20 on the large grid, 5 on the small.</summary>
    internal static int Step(GridSize grid) => grid switch
    {
        GridSize.Large => 20,
        GridSize.Small => 5,
        _ => throw NotAGrid(grid),
    };

    private static PieceGrid PieceGridOf(GridSize grid) => grid switch
    {
        GridSize.Large => PieceGrid.Large,
        GridSize.Small => PieceGrid.Small,
        _ => throw NotAGrid(grid),
    };

    private static ArgumentOutOfRangeException NotAGrid(GridSize grid) => new(nameof(grid), grid, "not a grid");
}
