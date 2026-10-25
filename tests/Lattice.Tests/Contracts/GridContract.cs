using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Ports;

namespace Lattice.Tests.Contracts;

/// <summary>
/// What every <see cref="IGrid"/> promises: the 24 turns form a group acting on directions and offsets, neighbours
/// step back, reflections undo themselves, snapping a cell's centre gives the cell. Derive it and give the subject.
/// </summary>
public abstract class GridContract
{
    private static readonly Direction[] Directions =
        [Direction.XPlus, Direction.XMinus, Direction.YPlus, Direction.YMinus, Direction.ZPlus, Direction.ZMinus];

    internal abstract IGrid Subject { get; }

    [Fact]
    public void No_turn_changes_nothing()
    {
        foreach (var d in Directions)
            Assert.Equal(d, Subject.Apply(Turn.None, d));
    }

    [Fact]
    public void Every_turn_composed_with_its_inverse_is_no_turn()
    {
        foreach (var t in AllTurns())
        {
            Assert.Equal(Turn.None, Subject.Compose(t, Subject.Invert(t)));
            Assert.Equal(Turn.None, Subject.Compose(Subject.Invert(t), t));
        }
    }

    [Fact]
    public void A_composed_turn_applies_first_then_then()
    {
        foreach (var first in AllTurns())
            foreach (var then in AllTurns())
                foreach (var d in Directions)
                    Assert.Equal(Subject.Apply(then, Subject.Apply(first, d)), Subject.Apply(Subject.Compose(first, then), d));
    }

    [Fact]
    public void The_24_turns_are_distinct()
    {
        var seen = new HashSet<(Direction, Direction)>();
        foreach (var t in AllTurns())
            Assert.True(seen.Add((Subject.Apply(t, Direction.XPlus), Subject.Apply(t, Direction.YPlus))), $"Turn {t.Index} repeats another.");
    }

    [Fact]
    public void Turning_an_offset_keeps_its_length()
    {
        var offset = new CellOffset(5, 10, 20);
        foreach (var t in AllTurns())
        {
            var turned = Subject.Apply(t, offset);
            Assert.Equal(25 + 100 + 400, turned.X * turned.X + turned.Y * turned.Y + turned.Z * turned.Z);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void A_neighbour_steps_back(int gridSize)
    {
        var grid = (GridSize)gridSize;
        var cell = Subject.Snap(new WorldPosition(11.2f, 3.4f, -7.9f), grid);
        foreach (var d in Directions)
        {
            var there = Subject.Neighbour(cell, d, grid);
            Assert.NotEqual(cell, there);
            Assert.Equal(cell, Subject.Neighbour(there, Subject.Reflect(d, AxisOf(d)), grid));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Snapping_a_cell_centre_gives_the_cell(int gridSize)
    {
        var grid = (GridSize)gridSize;
        var cell = Subject.Snap(new WorldPosition(123.4f, 56.7f, -89.1f), grid);
        Assert.Equal(cell, Subject.Snap(Subject.CentreOf(cell, grid), grid));
    }

    [Fact]
    public void Reflecting_twice_changes_nothing()
    {
        foreach (var axis in new[] { Axis.X, Axis.Y, Axis.Z })
        {
            foreach (var d in Directions)
                Assert.Equal(d, Subject.Reflect(Subject.Reflect(d, axis), axis));
            var plane = new MirrorPlane(axis, 35);
            var cell = new Cell(10, 25, -40);
            Assert.Equal(cell, Subject.Reflect(Subject.Reflect(cell, plane), plane));
        }
    }

    private static Axis AxisOf(Direction d) => d switch
    {
        Direction.XPlus or Direction.XMinus => Axis.X,
        Direction.YPlus or Direction.YMinus => Axis.Y,
        _ => Axis.Z,
    };

    private static IEnumerable<Turn> AllTurns()
    {
        for (var i = 0; i < Turn.Count; i++)
            if (Turn.Create(i).TryGet(out var t))
                yield return t;
    }
}
