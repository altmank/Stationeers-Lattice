using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Grid;
using Lattice.Core.Ports;
using static Lattice.Tests.Grid.Turns;

namespace Lattice.Tests.Grid;

/// <summary>
/// Mirroring a cell, a direction and a piece's ends; a piece is mirrorable when its reflected ends equal its ends
/// under one of the 24 turns.
/// </summary>
public sealed class MirrorTests
{
    private static readonly Mirror Mirror = new(Cube);

    private static readonly string[] RunKinds = ["power,data", "gas", "chute"];

    [Fact]
    public void A_direction_along_the_normal_flips_and_any_other_stays()
    {
        Assert.Equal(Direction.XMinus, Cube.Reflect(Direction.XPlus, Axis.X));
        Assert.Equal(Direction.YPlus, Cube.Reflect(Direction.YMinus, Axis.Y));
        Assert.Equal(Direction.ZPlus, Cube.Reflect(Direction.ZPlus, Axis.X));
        Assert.Equal(Direction.XMinus, Cube.Reflect(Direction.XMinus, Axis.Z));
    }

    [Fact]
    public void A_cell_reflects_across_the_plane_coordinate()
    {
        Assert.Equal(new Cell(30, 10, 10), Cube.Reflect(new Cell(10, 10, 10), new MirrorPlane(Axis.X, 20)));
        Assert.Equal(new Cell(10, -5, 10), Cube.Reflect(new Cell(10, 15, 10), new MirrorPlane(Axis.Y, 5)));
    }

    [Fact]
    public void Reflecting_twice_gives_back_the_cell_and_the_direction()
    {
        var cell = new Cell(1015, 25, -370);
        foreach (var axis in Axes)
        {
            var plane = new MirrorPlane(axis, 40);
            Assert.Equal(cell, Cube.Reflect(Cube.Reflect(cell, plane), plane));
            Assert.All(Directions, d => Assert.Equal(d, Cube.Reflect(Cube.Reflect(d, axis), axis)));
        }
    }

    [Theory]
    [InlineData((int)GridSize.Large, 20)]
    [InlineData((int)GridSize.Large, 10)]
    [InlineData((int)GridSize.Small, 5)]
    [InlineData((int)GridSize.Small, 20)]
    public void A_plane_that_maps_cell_centres_to_cell_centres_fits_the_grid(int gridSize, int at) =>
        Assert.True(Mirror.Fit(new MirrorPlane(Axis.X, at), (GridSize)gridSize).IsOk);

    [Theory]
    [InlineData((int)GridSize.Large, 15)]
    [InlineData((int)GridSize.Large, 5)]
    [InlineData((int)GridSize.Small, 3)]
    public void A_plane_that_moves_cells_off_the_grid_is_refused_with_a_reason(int gridSize, int at)
    {
        var grid = (GridSize)gridSize;
        Assert.True(Mirror.Fit(new MirrorPlane(Axis.Z, at), (GridSize)gridSize).TryGetError(out var reason));
        Assert.Equal(ReasonCodes.InvalidValue, reason.Code);
    }

    [Fact]
    public void A_fitting_plane_reflects_a_large_cell_onto_a_large_cell_centre()
    {
        var plane = new MirrorPlane(Axis.X, 10);

        Assert.True(Mirror.Fit(plane, GridSize.Large).IsOk);
        Assert.Equal(new Cell(-30, 10, 10), Cube.Reflect(new Cell(50, 10, 10), plane));
    }

    [Fact]
    public void Reflecting_ends_flips_their_offsets_and_facings_along_the_normal()
    {
        var ends = new[] { new ConnectionEnd(new CellOffset(5, 0, 0), Direction.XPlus, EndKinds.GasPipe) };

        var reflected = Mirror.Reflect(ends, Axis.X);

        Assert.Equal(ValueList.Of(new ConnectionEnd(new CellOffset(-5, 0, 0), Direction.XMinus, EndKinds.GasPipe)), reflected);
    }

    public static IEnumerable<object[]> EveryRunShape()
    {
        foreach (var kind in RunKinds)
            for (var mask = 1; mask < 64; mask++)
                yield return [kind, mask];
    }

    [Theory]
    [MemberData(nameof(EveryRunShape))]
    public void Every_cable_pipe_and_chute_shape_is_mirrorable(string endKind, int mask)
    {
        var ends = new List<ConnectionEnd>();
        for (var bit = 0; bit < Directions.Length; bit++)
            if ((mask & (1 << bit)) != 0)
                ends.Add(new ConnectionEnd(new CellOffset(0, 0, 0), Directions[bit], Kinds(endKind)));

        Assert.True(Mirror.IsMirrorable(Piece(ends.ToArray())));
    }

    [Fact]
    public void A_long_straight_is_mirrorable()
    {
        var straight = Piece(
            new ConnectionEnd(new CellOffset(0, 0, 0), Direction.ZMinus, EndKinds.Cable),
            new ConnectionEnd(new CellOffset(0, 0, 45), Direction.ZPlus, EndKinds.Cable));

        Assert.True(Mirror.IsMirrorable(straight));
    }

    [Fact]
    public void A_tank_is_mirrorable()
    {
        var tank = Piece(
            new ConnectionEnd(new CellOffset(0, -5, 0), Direction.YMinus, EndKinds.GasPipe),
            new ConnectionEnd(new CellOffset(0, 5, 0), Direction.YPlus, EndKinds.Power) { Role = Option.Some(ConnectionRole.Input) });

        Assert.True(Mirror.IsMirrorable(tank));
    }

    [Fact]
    public void A_device_with_uneven_ports_is_not_mirrorable() =>
        Assert.False(Mirror.IsMirrorable(UnevenDevice));

    [Fact]
    public void A_device_with_uneven_ports_has_no_mirror_turn_on_any_axis() =>
        Assert.All(Axes, axis => Assert.True(Mirror.MirrorTurn(UnevenDevice, axis).IsNone));

    [Fact]
    public void A_piece_without_ends_is_mirrorable() => Assert.True(Mirror.IsMirrorable(Piece()));

    [Fact]
    public void The_mirror_turn_puts_the_ends_on_their_reflection()
    {
        var corner = Piece(
            new ConnectionEnd(new CellOffset(0, 0, 0), Direction.XPlus, EndKinds.GasPipe),
            new ConnectionEnd(new CellOffset(0, 0, 0), Direction.ZPlus, EndKinds.GasPipe),
            new ConnectionEnd(new CellOffset(0, 5, 0), Direction.YPlus, EndKinds.Power) { Role = Option.Some(ConnectionRole.Input) });
        foreach (var axis in Axes)
        {
            Assert.True(Mirror.MirrorTurn(corner, axis).TryGet(out var turn));
            Assert.Equal(Sorted(Mirror.Reflect(corner.Ends, axis)), Sorted(Turned(corner.Ends, turn)));
        }
    }

    [Fact]
    public void A_mirrored_placement_puts_the_ends_where_the_reflection_of_the_placed_ends_is()
    {
        var tee = Piece(
            new ConnectionEnd(new CellOffset(0, 0, 0), Direction.XPlus, EndKinds.Cable),
            new ConnectionEnd(new CellOffset(0, 0, 0), Direction.XMinus, EndKinds.Cable),
            new ConnectionEnd(new CellOffset(0, 0, 0), Direction.ZPlus, EndKinds.Cable),
            new ConnectionEnd(new CellOffset(0, 15, 0), Direction.YPlus, EndKinds.Cable));
        foreach (var axis in Axes)
            foreach (var placed in All())
            {
                Assert.True(Mirror.MirrorPlacement(tee, placed, axis).TryGet(out var mirrored));
                Assert.Equal(Sorted(Mirror.Reflect(Turned(tee.Ends, placed).ToList(), axis)), Sorted(Turned(tee.Ends, mirrored)));
            }
    }

    [Fact]
    public void A_placement_of_an_uneven_device_has_no_mirror() =>
        Assert.True(Mirror.MirrorPlacement(UnevenDevice, Of(5), Axis.Y).IsNone);

    [Fact]
    public void A_reflected_turn_is_the_turn_seen_in_the_mirror()
    {
        foreach (var axis in Axes)
            foreach (var turn in All())
                foreach (var d in Directions)
                    Assert.Equal(
                        Cube.Reflect(Cube.Apply(turn, Cube.Reflect(d, axis)), axis),
                        Cube.Apply(Mirror.Reflect(turn, axis), d));
    }

    private static PieceFacts UnevenDevice => Piece(
        new ConnectionEnd(new CellOffset(0, 0, 0), Direction.XPlus, EndKinds.Cable),
        new ConnectionEnd(new CellOffset(0, 0, 0), Direction.YPlus, EndKinds.Power) { Role = Option.Some(ConnectionRole.Input) },
        new ConnectionEnd(new CellOffset(0, 0, 0), Direction.ZPlus, EndKinds.GasPipe));

    private static PieceFacts Piece(params ConnectionEnd[] ends) => new(
        new PrefabKey(1),
        ValueList.Of(new KitPlace(new KitKey(2), default)),
        GridSize.Small,
        ValueList<BuildStep>.Empty,
        ValueList.From(ends),
        new Ratings(Option<Watts>.None, Option<Kilopascals>.None, Option<Kilopascals>.None),
        Option<RuleId>.None);

    private static ValueSet<NetworkType> Kinds(string text) =>
        Lattice.Core.Catalogue.CatalogueText.ParseKinds(text).Reduce(ValueSet<NetworkType>.Empty);

    private static IEnumerable<ConnectionEnd> Turned(IEnumerable<ConnectionEnd> ends, Turn turn) =>
        ends.Select(e => e with { At = Cube.Apply(turn, e.At), Facing = Cube.Apply(turn, e.Facing) });

    private static List<string> Sorted(IEnumerable<ConnectionEnd> ends) =>
        ends.Select(e => e.ToString()).OrderBy(s => s).ToList();
}
