using System.Linq;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.Core.Ports;
using Lattice.Core.Routes;
using Lattice.TestKit;
using static Lattice.Tests.Runs.RunBench;

namespace Lattice.Tests.Routes;

/// <summary>
/// <see cref="RouteToggles.KeepOffLines"/>: a route joins existing lines only at its ends. It never steps into a cell
/// holding a line piece or one a line's end points into, other than its ends and via cells; a device's ports are not
/// lines.
/// </summary>
public sealed class KeepOffLinesTests
{
    private static readonly Cell Start = At(2, 2, 2);
    private static readonly Cell Far = At(18, 2, 2);

    private static RouteToggles Lines(bool on = true, bool noBridging = false, bool ports = false) =>
        RouteBench.Off with { KeepOffLines = on, NoBridging = noBridging, KeepPortsFree = ports };

    /// <summary>Real cable straights in the cells, each along the axis of <paramref name="along"/>.</summary>
    private static Cell[] Line(RouteBench bench, Direction along, params Cell[] cells)
    {
        foreach (var cell in cells)
            bench.Runs.Real("StructureCableStraight", cell, along is Direction.ZPlus or Direction.ZMinus ? default : bench.Sending(Direction.ZPlus, along));
        return cells;
    }

    private static Cell[] AlongZ(int x, int fromZ, int toZ) =>
        Enumerable.Range(fromZ, toZ - fromZ + 1).Select(z => At(x, 2, z)).ToArray();

    [Fact]
    public void A_route_from_a_line_end_to_a_cell_beside_the_lines_far_end_leaves_the_line_at_once()
    {
        var bench = new RouteBench();
        var line = Line(bench, Direction.XPlus, At(4, 2, 2), At(5, 2, 2), At(6, 2, 2), At(7, 2, 2), At(8, 2, 2));
        var a = line[0];
        var c = At(8, 2, 3);

        var on = bench.Plan(bench.Route(bench.Runs.Cable, a, c, Lines()));
        var off = bench.Plan(bench.Route(bench.Runs.Cable, a, c, Lines(on: false)));
        var today = bench.Plan(bench.Route(bench.Runs.Cable, a, c));

        Assert.Equal(new[] { a }, on.Run.Cells.Intersect(line).ToArray());
        Assert.Equal(c, on.Run.Cells[on.Run.Cells.Count - 1]);
        Assert.Equal(today.Run.Cells, off.Run.Cells);
        Assert.Equal(today.Run.Changes, off.Run.Changes);
        Assert.True(off.Run.Cells.Intersect(line).Count() > 1);
    }

    [Fact]
    public void A_line_across_the_way_is_gone_round_and_never_entered()
    {
        var bench = new RouteBench();
        var line = Line(bench, Direction.ZPlus, AlongZ(10, -2, 6));

        var on = bench.Plan(bench.Route(bench.Runs.Cable, Start, Far, Lines()));
        var off = bench.Plan(bench.Route(bench.Runs.Cable, Start, Far));

        Assert.Empty(on.Run.Cells.Intersect(line));
        Assert.True(on.Bends > 0);
        Assert.Contains(At(10, 2, 2), off.Run.Cells);
        Assert.Equal(0, off.Bends);
    }

    [Fact]
    public void The_cell_a_lines_open_end_points_into_is_kept_off_mid_route_and_entered_as_the_routes_end()
    {
        var bench = new RouteBench();
        Line(bench, Direction.ZPlus, AlongZ(10, 3, 6));
        var pointedInto = At(10, 2, 2);

        var through = bench.Plan(bench.Route(bench.Runs.Cable, Start, Far, Lines()));
        var ending = bench.Plan(bench.Route(bench.Runs.Cable, Start, pointedInto, Lines()));

        Assert.DoesNotContain(pointedInto, through.Run.Cells);
        Assert.Equal(pointedInto, ending.Run.Cells[ending.Run.Cells.Count - 1]);
        var last = ending.Run.Pieces.Single(piece => bench.Runs.CellsOf(piece).Contains(pointedInto));
        Assert.True(bench.Runs.WaysOf(last, pointedInto).Contains(Direction.ZPlus));
    }

    [Fact]
    public void A_named_port_whose_every_way_in_runs_through_a_line_finds_no_route_along_line()
    {
        var bench = new RouteBench(RouteLimits.Default with { Margin = 2 });
        var device = bench.Runs.Real("StructureAreaPowerControl", At(10, 2, 2));
        var port = TestKitIds.Must(PiecePorts.Over(bench.Catalogue).At(new Placement(device.Piece, device.At, default)))
            .Single(p => p.Role == Option.Some(PortRole.Input));
        var joins = port.Joins;
        Line(bench, Direction.ZPlus, Step(joins, Direction.XMinus), Step(joins, Direction.YPlus), Step(joins, Direction.YMinus));
        Line(bench, Direction.XPlus, Step(joins, Direction.ZPlus), Step(joins, Direction.ZMinus));
        var request = bench.Route(bench.Runs.Cable, Start, joins) with { ToPort = Option.Some<PortEnd>(new PortEnd.OnStructure(device, port.Index)) };

        var why = bench.Refusal(request with { Toggles = Lines() });
        var found = bench.Try(request);

        Assert.Equal(ReasonCodes.NoRoute, why.Code);
        Assert.Contains(why.Facts["stopped"].Split(','), side => side.EndsWith(":along-line", System.StringComparison.Ordinal));
        Assert.True(found.IsOk);
    }

    [Fact]
    public void A_via_cell_on_a_line_merges_there_and_the_next_leg_leaves_the_line()
    {
        var bench = new RouteBench();
        var line = Line(bench, Direction.ZPlus, AlongZ(10, -2, 6));
        var via = At(10, 2, 2);
        var request = bench.Route(bench.Runs.Cable, Start, At(10, 2, 9)) with { Via = ValueList.Of(via) };

        var on = bench.Plan(request with { Toggles = Lines() });
        var off = bench.Plan(request);

        Assert.Equal(new[] { via }, on.Run.Cells.Intersect(line).ToArray());
        Assert.Contains(At(10, 2, 4), off.Run.Cells);
    }

    [Fact]
    public void A_device_port_facing_a_cell_is_the_ports_toggles_not_a_line()
    {
        var bench = new RouteBench();
        var portCell = At(10, 2, 2);
        bench.Runs.Real("StructureAreaPowerControl", At(10, 2, 3), bench.Sending(Direction.XMinus, Direction.ZMinus));

        var lines = bench.Plan(bench.Route(bench.Runs.Cable, Start, Far, Lines()));
        var ports = bench.Plan(bench.Route(bench.Runs.Cable, Start, Far, Lines(ports: true)));

        Assert.Contains(portCell, lines.Run.Cells);
        Assert.DoesNotContain(portCell, ports.Run.Cells);
    }

    [Theory]
    [InlineData("cable", true)]
    [InlineData("device", false)]
    public void A_piece_with_roled_ends_is_a_line_when_its_family_is_a_networks_own_and_a_device_port_when_a_device(string family, bool line)
    {
        var bench = new RouteBench(extraPieces: $"""
            piece ModCableTap kit=ItemModCableTap option=0 grid=small family={family}
              step material=ItemModCableTap*1 exit=ItemWireCutters air=no
              end 0 0 0 x+ power role=input
              end 0 0 0 x- power role=input2
              end 0 0 0 z- power role=output
              cell 0 0 0 cable
              collision 2
            """);
        var faced = At(10, 2, 2);
        bench.Runs.Real("ModCableTap", At(10, 2, 3));

        var lines = bench.Plan(bench.Route(bench.Runs.Cable, Start, Far, Lines()));
        var ports = bench.Plan(bench.Route(bench.Runs.Cable, Start, Far, Lines(on: false, ports: true)));

        Assert.Equal(!line, lines.Run.Cells.Contains(faced));
        Assert.Equal(line, ports.Run.Cells.Contains(faced));
    }

    [Fact]
    public void A_heavy_cables_open_end_is_a_line_to_a_normal_cable_route()
    {
        var bench = new RouteBench();
        bench.Runs.Real("StructureCableStraightH", At(10, 2, 3));
        var pointedInto = At(10, 2, 2);

        var on = bench.Plan(bench.Route(bench.Runs.Cable, Start, Far, Lines()));
        var off = bench.Plan(bench.Route(bench.Runs.Cable, Start, Far));

        Assert.DoesNotContain(pointedInto, on.Run.Cells);
        Assert.Contains(pointedInto, off.Run.Cells);
    }

    [Fact]
    public void With_no_bridging_too_a_foreign_line_reports_the_join()
    {
        var bench = new RouteBench(RouteLimits.Default with { Margin = 0 });
        Line(bench, Direction.ZPlus, AlongZ(10, -2, 6));

        var both = bench.Refusal(bench.Route(bench.Runs.Cable, Start, Far, Lines(noBridging: true)));
        var lines = bench.Refusal(bench.Route(bench.Runs.Cable, Start, Far, Lines()));

        Assert.Contains("x+:join", both.Facts["stopped"].Split(','));
        Assert.Contains("x+:along-line", lines.Facts["stopped"].Split(','));
    }

    [Fact]
    public void With_no_bridging_too_the_ends_own_line_reports_along_line()
    {
        var bench = new RouteBench(RouteLimits.Default with { Margin = 0 });
        var line = Line(bench, Direction.XPlus, At(0, 2, 2), At(1, 2, 2), At(2, 2, 2));

        var why = bench.Refusal(bench.Route(bench.Runs.Cable, line[2], Far, Lines(noBridging: true)));

        Assert.Equal(ReasonCodes.NoRoute, why.Code);
        Assert.Contains("x+:along-line", why.Facts["stopped"].Split(','));
    }
}
