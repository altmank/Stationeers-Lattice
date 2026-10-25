using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Ports;
using static Lattice.Tests.Grid.Turns;

namespace Lattice.Tests.Grid;

/// <summary>The 24 cube turns form a group and each one is a rotation of the cube.</summary>
public sealed class TurnGroupTests
{
    [Fact]
    public void Turn_none_is_the_identity()
    {
        foreach (var turn in All())
        {
            Assert.Equal(turn, Cube.Compose(Turn.None, turn));
            Assert.Equal(turn, Cube.Compose(turn, Turn.None));
        }

        Assert.All(Directions, d => Assert.Equal(d, Cube.Apply(Turn.None, d)));
    }

    [Fact]
    public void Every_turn_has_an_inverse_on_both_sides()
    {
        foreach (var turn in All())
        {
            Assert.Equal(Turn.None, Cube.Compose(turn, Cube.Invert(turn)));
            Assert.Equal(Turn.None, Cube.Compose(Cube.Invert(turn), turn));
        }
    }

    [Fact]
    public void Composing_is_associative()
    {
        foreach (var a in All())
            foreach (var b in All())
                foreach (var c in All())
                    Assert.Equal(Cube.Compose(Cube.Compose(a, b), c), Cube.Compose(a, Cube.Compose(b, c)));
    }

    [Fact]
    public void Composing_means_first_then_second()
    {
        foreach (var first in All())
            foreach (var then in All())
                foreach (var d in Directions)
                    Assert.Equal(Cube.Apply(then, Cube.Apply(first, d)), Cube.Apply(Cube.Compose(first, then), d));
    }

    [Fact]
    public void The_24_turns_move_the_axes_24_different_ways()
    {
        var images = All().Select(t => (Cube.Apply(t, Direction.XPlus), Cube.Apply(t, Direction.YPlus))).ToHashSet();

        Assert.Equal(Turn.Count, images.Count);
    }

    [Fact]
    public void Every_turn_is_a_rotation_never_a_mirror()
    {
        foreach (var turn in All())
        {
            var x = Unit(Cube.Apply(turn, Direction.XPlus));
            var y = Unit(Cube.Apply(turn, Direction.YPlus));
            var z = Unit(Cube.Apply(turn, Direction.ZPlus));

            Assert.Equal(new CellOffset(x.Y * y.Z - x.Z * y.Y, x.Z * y.X - x.X * y.Z, x.X * y.Y - x.Y * y.X), z);
        }
    }

    [Fact]
    public void A_turn_keeps_opposite_directions_opposite()
    {
        foreach (var turn in All())
            foreach (var d in Directions)
            {
                var image = Unit(Cube.Apply(turn, d));
                var opposite = Unit(Cube.Apply(turn, Directions.Single(o => Unit(o) == new CellOffset(-Unit(d).X, -Unit(d).Y, -Unit(d).Z))));
                Assert.Equal(new CellOffset(-image.X, -image.Y, -image.Z), opposite);
            }
    }

    [Fact]
    public void Turning_an_offset_agrees_with_turning_the_axes()
    {
        var offset = new CellOffset(5, -15, 20);
        foreach (var turn in All())
        {
            var x = Unit(Cube.Apply(turn, Direction.XPlus));
            var y = Unit(Cube.Apply(turn, Direction.YPlus));
            var z = Unit(Cube.Apply(turn, Direction.ZPlus));
            var expected = new CellOffset(
                5 * x.X - 15 * y.X + 20 * z.X,
                5 * x.Y - 15 * y.Y + 20 * z.Y,
                5 * x.Z - 15 * y.Z + 20 * z.Z);

            Assert.Equal(expected, Cube.Apply(turn, offset));
        }
    }

    [Fact]
    public void Turns_0_to_3_are_quarter_yaws_about_up_that_take_plus_z_towards_plus_x()
    {
        var expected = new List<Direction> { Direction.ZPlus, Direction.XPlus, Direction.ZMinus, Direction.XMinus };

        Assert.Equal(expected, Enumerable.Range(0, 4).Select(i => Cube.Apply(Of(i), Direction.ZPlus)).ToList());
        Assert.All(Enumerable.Range(0, 4), i => Assert.Equal(Direction.YPlus, Cube.Apply(Of(i), Direction.YPlus)));
    }

    [Fact]
    public void Each_block_of_four_turns_sends_up_one_way()
    {
        Direction[] ups = [Direction.YPlus, Direction.YMinus, Direction.XPlus, Direction.XMinus, Direction.ZPlus, Direction.ZMinus];

        for (var i = 0; i < Turn.Count; i++)
            Assert.Equal(ups[i / 4], Cube.Apply(Of(i), Direction.YPlus));
    }
}
