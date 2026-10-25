using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Grid;
using Lattice.Core.Ports;
using Lattice.Core.World;
using Lattice.Tests.Grid;
using Xunit;

namespace Lattice.Tests.Contract;

public sealed class TurnAxesTests
{
    private static readonly SignedAxis XPlus = SignedAxis.XPlus, XMinus = SignedAxis.XMinus;
    private static readonly SignedAxis YPlus = SignedAxis.YPlus, YMinus = SignedAxis.YMinus;
    private static readonly SignedAxis ZPlus = SignedAxis.ZPlus, ZMinus = SignedAxis.ZMinus;

    /// <summary>The contract's numbering: where turn i sends +X, +Y and +Z. Fixed for the life of the contract.</summary>
    private static readonly TurnAxes[] Numbering =
    [
        new(XPlus, YPlus, ZPlus), new(ZMinus, YPlus, XPlus), new(XMinus, YPlus, ZMinus), new(ZPlus, YPlus, XMinus),
        new(XPlus, YMinus, ZMinus), new(ZPlus, YMinus, XPlus), new(XMinus, YMinus, ZPlus), new(ZMinus, YMinus, XMinus),
        new(YMinus, XPlus, ZPlus), new(ZMinus, XPlus, YMinus), new(YPlus, XPlus, ZMinus), new(ZPlus, XPlus, YPlus),
        new(YPlus, XMinus, ZPlus), new(ZMinus, XMinus, YPlus), new(YMinus, XMinus, ZMinus), new(ZPlus, XMinus, YMinus),
        new(XPlus, ZPlus, YMinus), new(YPlus, ZPlus, XPlus), new(XMinus, ZPlus, YPlus), new(YMinus, ZPlus, XMinus),
        new(XPlus, ZMinus, YPlus), new(YMinus, ZMinus, XPlus), new(XMinus, ZMinus, YMinus), new(YPlus, ZMinus, XMinus),
    ];

    public static IEnumerable<object[]> Indices() => Enumerable.Range(0, Turn.Count).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(Indices))]
    public void Every_turn_sends_the_axes_where_the_contract_numbering_says(int index) =>
        Assert.Equal(Numbering[index], Turns.Of(index).Axes);

    [Fact]
    public void No_turn_leaves_every_axis_in_place() =>
        Assert.Equal(new TurnAxes(XPlus, YPlus, ZPlus), Turn.None.Axes);

    [Theory]
    [MemberData(nameof(Indices))]
    public void A_turn_is_found_again_from_where_it_sends_x_and_y(int index)
    {
        var axes = Turns.Of(index).Axes;
        Assert.Equal(Result<Turn, Reason>.Ok(Turns.Of(index)), Turn.Of(axes.X, axes.Y));
    }

    [Theory]
    [MemberData(nameof(Indices))]
    public void Every_turn_is_a_rotation_z_is_x_cross_y(int index)
    {
        var axes = Turns.Of(index).Axes;
        Assert.Equal(Unit(axes.Z), Cross(Unit(axes.X), Unit(axes.Y)));
    }

    [Fact]
    public void Axes_that_are_not_square_are_refused()
    {
        Assert.True(Turn.Of(XPlus, XMinus).TryGetError(out var refused));
        Assert.Equal(ReasonCodes.InvalidValue, refused.Code);
        Assert.Equal("+x,-x", refused.Facts["value"]);
        Assert.True(Turn.Of(YPlus, YPlus).IsError);
    }

    [Fact]
    public void A_default_signed_axis_is_refused() =>
        Assert.True(Turn.Of(default, YPlus).IsError);

    [Theory]
    [MemberData(nameof(Indices))]
    public void The_core_turn_table_agrees_with_the_contract(int index)
    {
        var rotation = new TurnTable().RotationOf(Turns.Of(index));
        var axes = Turns.Of(index).Axes;
        Assert.Equal(Unit(axes.X), Turns.Unit(rotation.XTo));
        Assert.Equal(Unit(axes.Y), Turns.Unit(rotation.YTo));
        Assert.Equal(Unit(axes.Z), Turns.Unit(rotation.ZTo));
    }

    /// <summary>
    /// The bridge's game rotation for each turn (a Unity quaternion, the form the game's ThingTransformRotation
    /// holds) sends +X, +Y and +Z where the contract says.
    /// </summary>
    [Theory]
    [MemberData(nameof(Indices))]
    public void The_game_rotation_of_every_turn_sends_the_axes_where_the_contract_says(int index)
    {
        var q = TurnReading.ToGame(Turns.Of(index));
        var axes = Turns.Of(index).Axes;
        Assert.Equal(Unit(axes.X), Rotate(q, new CellOffset(1, 0, 0)));
        Assert.Equal(Unit(axes.Y), Rotate(q, new CellOffset(0, 1, 0)));
        Assert.Equal(Unit(axes.Z), Rotate(q, new CellOffset(0, 0, 1)));
    }

    private static CellOffset Unit(SignedAxis axis)
    {
        var s = axis.Sign == AxisSign.Plus ? 1 : -1;
        return axis.Axis switch
        {
            Axis.X => new(s, 0, 0),
            Axis.Y => new(0, s, 0),
            _ => new(0, 0, s),
        };
    }

    private static CellOffset Cross(CellOffset a, CellOffset b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    /// <summary>v' = q v q*, rounded to the grid.</summary>
    private static CellOffset Rotate(GameRotation q, CellOffset v)
    {
        double x = q.X, y = q.Y, z = q.Z, w = q.W;
        double vx = v.X, vy = v.Y, vz = v.Z;
        var tx = 2 * (y * vz - z * vy);
        var ty = 2 * (z * vx - x * vz);
        var tz = 2 * (x * vy - y * vx);
        return new(
            (int)System.Math.Round(vx + w * tx + (y * tz - z * ty)),
            (int)System.Math.Round(vy + w * ty + (z * tx - x * tz)),
            (int)System.Math.Round(vz + w * tz + (x * ty - y * tx)));
    }
}
