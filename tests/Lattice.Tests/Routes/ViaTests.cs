using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.Core.Routes;
using Lattice.TestKit;
using Lattice.Tests.Board;
using static Lattice.Tests.Runs.RunBench;

namespace Lattice.Tests.Routes;

/// <summary>
/// <see cref="RouteRequest.Via"/>: cells a route passes through in order, searched leg by leg, never crossing an earlier
/// leg, and laid as one run, so a via cell gets one piece and long straights run through it. Cable on small cells.
/// </summary>
public sealed class ViaTests
{
    private const string Valve = """
        piece ModValve kit=ItemModValve option=0 grid=small
          step material=ItemModValve*1 exit=ItemWrench air=no
          end 0 0 0 z- power reach=273 role=output
          end 0 0 0 z+ power reach=275 role=input
          cell 0 0 0 device
          collision 4
        """;

    private static readonly Cell From = At(0, 2, 2);
    private static readonly Cell To = At(12, 2, 2);

    private static RouteRequest Pinned(RouteBench bench, Cell from, Cell to, params Cell[] via) =>
        bench.Route(bench.Runs.Cable, from, to) with { Via = ValueList.From(via) };

    // The laid pieces, by the cells each covers.
    private static Dictionary<Cell, Placement> ByCell(RouteBench bench, RoutePlan plan)
    {
        var byCell = new Dictionary<Cell, Placement>();
        foreach (var piece in plan.Run.Pieces)
            foreach (var cell in bench.Runs.CellsOf(piece))
                byCell[cell] = piece;
        return byCell;
    }

    private static int TurnsIn(IReadOnlyList<Cell> cells)
    {
        var turns = 0;
        for (var i = 2; i < cells.Count; i++)
        {
            var (a, b, c) = (cells[i - 2], cells[i - 1], cells[i]);
            if (b.X - a.X != c.X - b.X || b.Y - a.Y != c.Y - b.Y || b.Z - a.Z != c.Z - b.Z)
                turns++;
        }

        return turns;
    }

    private static void PassesInOrder(IReadOnlyList<Cell> cells, params Cell[] points)
    {
        var at = -1;
        foreach (var point in points)
        {
            var next = cells.ToList().IndexOf(point);
            Assert.True(next > at, $"{point} is not passed after the cells before it");
            at = next;
        }
    }

    [Fact]
    public void A_pin_on_the_straight_line_is_passed_and_a_long_straight_runs_through_it()
    {
        var bench = new RouteBench();
        var pin = At(6, 2, 2);

        var plan = bench.Plan(Pinned(bench, From, To, pin));

        PassesInOrder(plan.Run.Cells, From, pin, To);
        Assert.Equal(plan.Run.Cells.Count, plan.Run.Cells.Distinct().Count());
        Assert.True(bench.Runs.CellsOf(ByCell(bench, plan)[pin]).Count > 1);
        Assert.Equal(0, plan.Bends);
    }

    [Fact]
    public void Where_the_legs_turn_at_a_pin_it_gets_a_corner()
    {
        var bench = new RouteBench();
        var pin = At(6, 2, 2);

        var plan = bench.Plan(Pinned(bench, From, At(6, 2, 8), pin));

        Assert.Equal("StructureCableCorner", bench.Runs.NameOf(ByCell(bench, plan)[pin].Piece));
        Assert.Equal(1, plan.Bends);
    }

    [Fact]
    public void A_pin_off_the_line_bends_the_route_to_reach_it_and_the_bends_are_the_whole_routes()
    {
        var bench = new RouteBench();
        var pin = At(6, 2, 6);

        var plan = bench.Plan(Pinned(bench, From, To, pin));

        PassesInOrder(plan.Run.Cells, From, pin, To);
        Assert.True(plan.Bends >= 2);
        Assert.Equal(TurnsIn(plan.Run.Cells), plan.Bends);
        Assert.Equal(plan.Bends, plan.AsRun.Via.Count);
    }

    [Fact]
    public void A_later_leg_goes_around_an_earlier_leg_rather_than_cross_it()
    {
        var bench = new RouteBench();
        var (a, b, to) = (At(6, 2, 2), At(3, 2, 0), At(3, 2, 6));

        var plan = bench.Plan(Pinned(bench, From, to, a, b));

        PassesInOrder(plan.Run.Cells, From, a, b, to);
        Assert.Equal(plan.Run.Cells.Count, plan.Run.Cells.Distinct().Count());
    }

    [Fact]
    public void A_leg_boxed_in_by_an_earlier_one_is_no_route_naming_the_leg()
    {
        var bench = new RouteBench(RouteLimits.Default with { Margin = 0 });

        var why = bench.Refusal(Pinned(bench, From, At(3, 2, 6), At(6, 2, 2), At(3, 2, 0)));

        Assert.Equal(ReasonCodes.NoRoute, why.Code);
        Assert.Equal("2", why.Facts["leg"]);
        var b = At(3, 2, 0);
        Assert.Equal($"{b.X},{b.Y},{b.Z}", why.Facts["from"]);
    }

    [Fact]
    public void More_than_sixteen_pins_are_too_large()
    {
        var bench = new RouteBench();
        var pins = Enumerable.Range(1, 17).Select(i => At(0, 2, 2 + i)).ToArray();

        var why = bench.Refusal(Pinned(bench, From, To, pins));

        Assert.Equal(ReasonCodes.TooLarge, why.Code);
        Assert.Equal("17", why.Facts["via"]);
        Assert.Equal("16", why.Facts["most"]);
    }

    [Fact]
    public void A_pin_off_the_kits_grid_is_refused_naming_its_index()
    {
        var bench = new RouteBench();

        var why = bench.Refusal(Pinned(bench, From, To, At(3, 2, 2), At(6, 2, 2) with { X = At(6, 2, 2).X + 1 }));

        Assert.Equal(ReasonCodes.InvalidValue, why.Code);
        Assert.Equal("off-grid", why.Facts["problem"]);
        Assert.Equal("1", why.Facts["via"]);
    }

    [Theory]
    [InlineData("to")]
    [InlineData("from")]
    [InlineData("pin")]
    public void A_pin_on_an_end_or_another_pin_repeats(string on)
    {
        var bench = new RouteBench();
        var repeated = on switch { "to" => To, "from" => From, _ => At(4, 2, 2) };

        var why = bench.Refusal(Pinned(bench, From, To, At(4, 2, 2), repeated));

        Assert.Equal(ReasonCodes.InvalidValue, why.Code);
        Assert.Equal("via-repeats", why.Facts["problem"]);
        Assert.Equal("1", why.Facts["via"]);
    }

    [Fact]
    public void The_bound_is_for_each_leg_not_the_whole_route()
    {
        var bench = new RouteBench();
        var request = Pinned(bench, From, To, At(6, 2, 2));
        var all = bench.Plan(request).Expanded;
        var bound = all - 1;

        var bounded = bench.Plan(request with { SearchBound = Option.Some(bound) });
        var over = bench.Refusal(request with { SearchBound = Option.Some(2) });

        Assert.Equal(all, bounded.Expanded);
        Assert.Equal(ReasonCodes.SearchLimit, over.Code);
        Assert.Equal("0", over.Facts["leg"]);
    }

    [Fact]
    public void Port_ends_join_at_the_first_and_last_legs_of_a_pinned_route()
    {
        var bench = new RouteBench(extraPieces: Valve);
        var a = bench.Runs.Boards.Placed(bench.Runs.Boards.Draft("ModValve", At(0, 2, 0))).Added[0].Id;
        var b = bench.Runs.Boards.Placed(bench.Runs.Boards.Draft("ModValve", At(12, 2, 8))).Added[0].Id;
        var ports = PiecePorts.Over(bench.Catalogue);
        var valve = bench.Catalogue.Named("ModValve").Key;
        var from = TestKitIds.Must(ports.At(new Placement(valve, At(0, 2, 0), default)))[1].Joins;
        var to = TestKitIds.Must(ports.At(new Placement(valve, At(12, 2, 8), default)))[0].Joins;
        var input = TestKitIds.Must(PortIndex.Create(1));
        var output = TestKitIds.Must(PortIndex.Create(0));
        var request = Pinned(bench, from, to, At(6, 2, 4)) with
        {
            FromPort = Option.Some<PortEnd>(new PortEnd.OnGhost(a, input)),
            ToPort = Option.Some<PortEnd>(new PortEnd.OnGhost(b, output)),
        };

        var plan = bench.Plan(request);

        Assert.Equal(from, plan.Run.Cells[0]);
        Assert.Equal(2, plan.Run.PortEnds.Count);
        Assert.All(plan.Run.PortEnds, join => Assert.True(join.JoinedBy.IsSome));
    }

    [Fact]
    public void A_branch_reaches_a_cell_on_a_middle_leg()
    {
        var bench = new RouteBench();
        var request = Pinned(bench, From, At(12, 2, 8), At(6, 2, 2), At(6, 2, 8)) with
        {
            Branches = ValueList.Of(new RouteBranch(At(8, 2, 5), Option<PortEnd>.None)),
        };

        var plan = bench.Plan(request);

        Assert.Equal("StructureCableJunction", bench.Runs.NameOf(ByCell(bench, plan)[At(6, 2, 5)].Piece));
    }

    [Fact]
    public void A_route_with_no_pins_names_no_leg_when_it_fails()
    {
        var bench = new RouteBench();

        var why = bench.Refusal(bench.Route(bench.Runs.Cable, From, To) with { SearchBound = Option.Some(2) });

        Assert.Equal(ReasonCodes.SearchLimit, why.Code);
        Assert.False(why.Facts.ContainsKey("leg"));
    }

    [Fact]
    public void A_pinned_route_is_laid_as_one_place_command()
    {
        var bench = new RouteBench();

        var plan = bench.Plan(Pinned(bench, From, To, At(4, 2, 4), At(8, 2, 0)));
        var placed = TestBoard.Applied(bench.Runs.Boards.Apply(plan.ToCommand()));

        Assert.Equal(plan.Run.Changes.Count, placed.Added.Count);
    }
}
