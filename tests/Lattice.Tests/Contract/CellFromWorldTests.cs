using System;
using Lattice.Api;
using Lattice.Core.Ports;
using static Lattice.Tests.Grid.Turns;

namespace Lattice.Tests.Contract;

/// <summary>
/// The contract's world-to-cell snap and face neighbour. Keys are the game's: each axis
/// to its cell centre with <c>GridCenter</c> (large 2 m offset 0, small 0.5 m offset 0.25; <c>Mathf.Round</c>, a tie to
/// the even multiple), then <c>Grid3.Round</c> of the centre in decimetres (half away from zero). Decompile
/// <c>Assets.Scripts.Util/ExtensionMethods.cs</c> <c>GridCenter</c>, <c>Assets.Scripts.Objects/SmallGrid.cs</c>
/// <c>SmallGridSize</c>/<c>SmallGridOffset</c>, <c>Assets.Scripts.GridSystem/Grid3.cs</c> <c>Round</c>.
/// </summary>
public sealed class CellFromWorldTests
{
    [Theory]
    [InlineData(0f, 10)]
    [InlineData(0.99f, 10)]
    [InlineData(1.99f, 10)]
    [InlineData(2f, 10)]
    [InlineData(2.01f, 30)]
    [InlineData(4f, 50)]
    [InlineData(5.5f, 50)]
    [InlineData(-0.01f, -10)]
    [InlineData(-2f, -30)]
    [InlineData(-4f, -30)]
    [InlineData(123.4f, 1230)]
    [InlineData(-123.4f, -1230)]
    public void A_point_takes_the_large_cell_the_game_snaps_it_to(float metres, int key)
    {
        Assert.Equal(new Cell(key, key, key), Cell.FromWorld(new WorldPoint(metres, metres, metres), PieceGrid.Large));
    }

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(0.24f, 0)]
    [InlineData(0.25f, 5)]
    [InlineData(0.74f, 5)]
    [InlineData(0.75f, 5)]
    [InlineData(0.76f, 10)]
    [InlineData(1.25f, 15)]
    [InlineData(1.75f, 15)]
    [InlineData(-0.25f, -5)]
    [InlineData(-0.75f, -5)]
    [InlineData(12.3f, 125)]
    [InlineData(-12.3f, -125)]
    public void A_point_takes_the_small_cell_the_game_snaps_it_to(float metres, int key)
    {
        Assert.Equal(new Cell(key, key, key), Cell.FromWorld(new WorldPoint(metres, metres, metres), PieceGrid.Small));
    }

    [Fact]
    public void Each_axis_snaps_on_its_own()
    {
        Assert.Equal(new Cell(10, 30, -10), Cell.FromWorld(new WorldPoint(0.5f, 2.5f, -0.5f), PieceGrid.Large));
        Assert.Equal(new Cell(5, 25, -5), Cell.FromWorld(new WorldPoint(0.5f, 2.5f, -0.5f), PieceGrid.Small));
    }

    [Fact]
    public void Lattice_placements_snap_with_the_contract_on_every_point()
    {
        for (var i = -2_000; i <= 2_000; i++)
        {
            var metres = i * 0.0625f + 0.01f * (i % 7);
            var point = new WorldPoint(metres, -metres, metres * 0.5f);
            var position = new WorldPosition(point.X, point.Y, point.Z);
            Assert.Equal(Cell.FromWorld(point, PieceGrid.Large), Cube.Snap(position, GridSize.Large));
            Assert.Equal(Cell.FromWorld(point, PieceGrid.Small), Cube.Snap(position, GridSize.Small));
        }
    }

    [Theory]
    [InlineData(Face.PlusX, 20, 0, 0)]
    [InlineData(Face.MinusX, -20, 0, 0)]
    [InlineData(Face.PlusY, 0, 20, 0)]
    [InlineData(Face.MinusY, 0, -20, 0)]
    [InlineData(Face.PlusZ, 0, 0, 20)]
    [InlineData(Face.MinusZ, 0, 0, -20)]
    public void A_face_neighbour_is_one_cell_along_the_face_normal(Face face, int x, int y, int z)
    {
        var large = new Cell(10, 30, -10);
        var small = new Cell(5, 25, -5);

        Assert.Equal(large.Plus(new CellOffset(x, y, z)), large.Neighbour(face, PieceGrid.Large));
        Assert.Equal(small.Plus(new CellOffset(x / 4, y / 4, z / 4)), small.Neighbour(face, PieceGrid.Small));
    }

    [Fact]
    public void A_neighbour_of_a_snapped_cell_is_the_cell_the_game_snaps_one_cell_further()
    {
        var at = new WorldPoint(3.1f, 0.4f, -7.9f);

        Assert.Equal(Cell.FromWorld(at with { X = at.X + 2f }, PieceGrid.Large), Cell.FromWorld(at, PieceGrid.Large).Neighbour(Face.PlusX, PieceGrid.Large));
        Assert.Equal(Cell.FromWorld(at with { Z = at.Z - 0.5f }, PieceGrid.Small), Cell.FromWorld(at, PieceGrid.Small).Neighbour(Face.MinusZ, PieceGrid.Small));
    }

    [Fact]
    public void A_value_outside_the_grids_or_faces_is_a_bug()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Cell.FromWorld(default, (PieceGrid)0));
        Assert.Throws<ArgumentOutOfRangeException>(() => default(Cell).Neighbour((Face)0, PieceGrid.Large));
        Assert.Throws<ArgumentOutOfRangeException>(() => default(Cell).Neighbour(Face.PlusX, (PieceGrid)7));
    }
}
