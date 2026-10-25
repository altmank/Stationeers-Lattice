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
/// <see cref="RouteRequest.Branches"/>: extra starts, each routed in order to the nearest cell of the run so far and
/// joined there with a T; laid as one change with the main route. Cable on small cells, the main route along x.
/// </summary>
public sealed class BranchTests
{
    private const string Valve = """
        piece ModValve kit=ItemModValve option=0 grid=small
          step material=ItemModValve*1 exit=ItemWrench air=no
          end 0 0 0 z- power reach=273 role=output
          end 0 0 0 z+ power reach=275 role=input
          cell 0 0 0 device
          collision 4
        """;

    private static readonly Cell MainFrom = At(0, 2, 2);
    private static readonly Cell MainTo = At(12, 2, 2);

    private static RouteRequest MainRoute(RouteBench bench, params RouteBranch[] branches) =>
        bench.Route(bench.Runs.Cable, MainFrom, MainTo) with { Branches = ValueList.From(branches) };

    private static RouteBranch From(Cell cell) => new(cell, Option<PortEnd>.None);

    // The board once the route's command is applied: each cell's one ghost, by cell.
    private static Dictionary<Cell, Ghost> Laid(RouteBench bench, RoutePlan plan)
    {
        TestBoard.Applied(bench.Runs.Boards.Apply(plan.ToCommand()));
        var ghosts = bench.Runs.Boards.Board.Index.Ghosts.Values.Select(e => e.Ghost).ToList();
        var byCell = new Dictionary<Cell, Ghost>();
        foreach (var ghost in ghosts)
            foreach (var cell in bench.Runs.CellsOf(((Intent.Build)ghost.Intent).Target))
                byCell.Add(cell, ghost);
        return byCell;
    }

    private static string Piece(RouteBench bench, Ghost ghost) => bench.Runs.NameOf(((Intent.Build)ghost.Intent).Target.Piece);

    [Fact]
    public void Three_starts_beside_a_main_route_join_it_with_tees_at_the_nearest_cells_and_make_no_loop()
    {
        var bench = new RouteBench();
        var plan = bench.Plan(MainRoute(bench, From(At(3, 2, 6)), From(At(6, 2, -2)), From(At(9, 2, 5))));

        var laid = Laid(bench, plan);

        foreach (var join in new[] { At(3, 2, 2), At(6, 2, 2), At(9, 2, 2) })
            Assert.Equal("StructureCableJunction", Piece(bench, laid[join]));
        Assert.Equal(13 + 4 + 4 + 3, laid.Count);
        Assert.Equal(laid.Count, plan.Run.Cells.Distinct().Count());
        Assert.DoesNotContain(laid.Values, g => Piece(bench, g) is "StructureCableJunction4" or "StructureCableCorner3");
    }

    [Fact]
    public void A_branch_nearer_an_earlier_branch_than_the_main_route_joins_that_branch()
    {
        var bench = new RouteBench();
        var plan = bench.Plan(MainRoute(bench, From(At(6, 2, 8)), From(At(9, 2, 7))));

        var laid = Laid(bench, plan);

        Assert.Equal("StructureCableJunction", Piece(bench, laid[At(6, 2, 2)]));
        Assert.Equal("StructureCableJunction", Piece(bench, laid[At(6, 2, 7)]));
        Assert.False(laid.ContainsKey(At(9, 2, 3)));
    }

    [Fact]
    public void A_branch_from_a_valves_port_joins_the_port_and_the_main_route()
    {
        var bench = new RouteBench(extraPieces: Valve);
        var valve = bench.Runs.Boards.Placed(bench.Runs.Boards.Draft("ModValve", At(6, 2, 7))).Added[0].Id;
        var output = TestKitIds.Must(PortIndex.Create(0));
        var target = ((Intent.Build)bench.Runs.Boards.View.Find(valve).Reduce(() => throw new System.InvalidOperationException()).Intent).Target;
        var port = TestKitIds.Must(PiecePorts.Over(bench.Catalogue).At(target)).Single(p => p.Index == output);
        var request = MainRoute(bench, new RouteBranch(port.Joins, Option.Some<PortEnd>(new PortEnd.OnGhost(valve, output))))
            with { Toggles = RouteBench.Off with { KeepPortsFree = true } };

        var plan = bench.Plan(request);

        var join = Assert.Single(plan.Run.PortEnds);
        Assert.Equal(port, join.Port);
        Assert.True(join.JoinedBy.IsSome);
        Assert.Contains(plan.Run.Cells, c => c == At(6, 2, 2));
        Assert.Equal("StructureCableJunction", Piece(bench, Laid(bench, plan)[At(6, 2, 2)]));
    }

    [Fact]
    public void A_branch_with_no_route_refuses_the_whole_command_naming_the_branch()
    {
        var bench = new RouteBench(RouteLimits.Default with { Margin = 4 });
        var request = bench.Route(bench.Runs.Cable, At(0, 2, 2), At(8, 2, 2), RouteBench.Off with { OnFrames = true })
            with { Branches = ValueList.From(new[] { From(At(4, 2, 5)), From(At(4, 2, 16)) }) };

        var why = bench.Refusal(request);

        Assert.Equal(ReasonCodes.NoRoute, why.Code);
        Assert.Equal("1", why.Facts["branch"]);
        Assert.True(why.Facts.ContainsKey("nearest"));
    }

    [Fact]
    public void More_branches_than_the_limit_are_refused_as_too_large()
    {
        var bench = new RouteBench();
        var branches = Enumerable.Range(0, RouteLimits.MaxBranches + 1).Select(i => From(At(i, 2, 6))).ToArray();

        var why = bench.Refusal(MainRoute(bench, branches));

        Assert.Equal(ReasonCodes.TooLarge, why.Code);
        Assert.Equal((RouteLimits.MaxBranches + 1).ToString(), why.Facts["branches"]);
    }

    [Fact]
    public void The_report_holds_the_cells_and_pieces_of_the_main_route_and_every_branch()
    {
        var bench = new RouteBench();
        var plan = bench.Plan(MainRoute(bench, From(At(3, 2, 6)), From(At(9, 2, -3))));

        var cells = plan.Run.Cells;

        Assert.Contains(MainTo, cells);
        Assert.Contains(At(3, 2, 6), cells);
        Assert.Contains(At(9, 2, -3), cells);
        Assert.Equal(cells.Count, plan.Run.Pieces.Sum(p => bench.Runs.CellsOf(p).Count));
    }

    [Fact]
    public void Under_no_bridging_a_branchs_own_network_is_the_routes_and_never_a_bridge()
    {
        var bench = new RouteBench();
        var crossing = At(4, 2, 2);
        bench.Runs.Real("StructureCableStraight", crossing);
        var noBridge = RouteBench.Off with { NoBridging = true };
        var main = bench.Route(bench.Runs.Cable, At(0, 2, 2), At(8, 2, 2), noBridge);

        var alone = bench.Plan(main);
        var branched = bench.Plan(main with { Branches = ValueList.Of(From(crossing)) });

        Assert.DoesNotContain(crossing, alone.Run.Cells);
        Assert.Equal(0, branched.Bends);
        Assert.Contains(crossing, branched.Run.Cells);
    }

    [Fact]
    public void A_route_without_branches_lays_as_before()
    {
        var bench = new RouteBench();

        var plan = bench.Plan(MainRoute(bench));
        var drawn = bench.Runs.Plan(plan.AsRun);

        Assert.Equal(drawn.Pieces, plan.Run.Pieces);
        Assert.Equal(drawn.Cells, plan.Run.Cells);
    }
}
