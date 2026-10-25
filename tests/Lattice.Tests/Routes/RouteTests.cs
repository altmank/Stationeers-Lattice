using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Grid;
using Lattice.Core.Ports;
using Lattice.Core.Routes;
using Lattice.Core.Slices;
using Lattice.TestKit;
using Lattice.Tests.Board;
using Xunit;
using Xunit.Abstractions;
using static Lattice.Tests.Routes.RouteBench;
using static Lattice.Tests.Runs.RunBench;

namespace Lattice.Tests.Routes;

[Collection(nameof(Timing))]
public sealed class RouteTests(ITestOutputHelper output)
{
    private static readonly Cell Start = At(2, 2, 2);
    private static readonly Cell Far = At(18, 2, 2);

    private static RouteToggles Only(
        bool onFrames = false, bool fewestBends = false, bool clear = false, bool ports = false, bool joins = false) =>
        new(onFrames, fewestBends, clear, ports, joins);

    private static bool Straight(RoutePlan plan) => plan.Bends == 0 && plan.AsRun.Via.Count == 0;

    [Fact]
    public void A_clear_line_is_one_straight_run()
    {
        var bench = new RouteBench();

        var plan = bench.Plan(bench.Route(bench.Runs.Cable, Start, Far));

        Assert.True(Straight(plan));
        Assert.Equal(17, plan.Run.Cells.Count);
        Assert.True(plan.Run.FirstFailure.IsNone);
    }

    [Fact]
    public void Ends_on_two_axes_take_one_bend_and_the_bend_is_the_runs_waypoint()
    {
        var bench = new RouteBench();

        var plan = bench.Plan(bench.Route(bench.Runs.Cable, Start, At(8, 2, 8)));

        Assert.Equal(1, plan.Bends);
        Assert.Single(plan.AsRun.Via);
        Assert.Equal(13, plan.Run.Cells.Count);
    }

    [Fact]
    public void The_same_request_gives_the_same_route()
    {
        var first = new RouteBench();
        var second = new RouteBench();

        var a = first.Plan(first.Route(first.Runs.Cable, Start, At(9, 5, 7)));
        var b = second.Plan(second.Route(second.Runs.Cable, Start, At(9, 5, 7)));

        Assert.Equal(a.Run.Cells, b.Run.Cells);
        Assert.Equal(a.AsRun.Via, b.AsRun.Via);
    }

    /// <summary>Open cells only along a staircase (short, many bends) and a wide loop (long, two bends).</summary>
    private sealed class TwoWays : IRouteCells
    {
        public static readonly Cell From = At(0, 0, 0);
        public static readonly Cell To = At(4, 0, 4);

        private readonly HashSet<Cell> _open = [];

        public TwoWays()
        {
            int[][] stairs = [[0, 0], [1, 0], [1, 1], [2, 1], [2, 2], [3, 2], [3, 3], [4, 3], [4, 4]];
            int[][] loop = [[-1, 0], [-2, 0], [-2, 1], [-2, 2], [-2, 3], [-2, 4], [-1, 4], [0, 4], [1, 4], [2, 4], [3, 4]];
            foreach (var cell in stairs.Concat(loop))
                _open.Add(At(cell[0], 0, cell[1]));
        }

        public bool Enters(Cell cell, Direction heading) => _open.Contains(cell);
    }

    [Theory]
    [InlineData(false, 9, 6)]
    [InlineData(true, 13, 2)]
    public void Fewest_bends_takes_a_longer_route_with_fewer_turns(bool fewestBends, int cells, int bends)
    {
        var search = new RouteSearch(new CubeGrid(), GridSize.Small, TwoWays.From, TwoWays.To, new TwoWays(), fewestBends, RouteLimits.Default);

        var found = Assert.IsType<RouteOutcome.Found>(search.Find());

        Assert.Equal(cells, found.Cells.Count);
        Assert.Equal(bends, found.Bends);
    }

    [Fact]
    public void On_frames_keeps_to_framed_cells_built_or_planned_away_from_the_ends()
    {
        var bench = new RouteBench();
        bench.Runs.Real("StructureFrame", Large(0, 0, 0));
        bench.Runs.Real("StructureFrame", Large(6, 0, 0));
        for (var i = 0; i <= 6; i++)
            if (i == 3)
                bench.Runs.Ghost("ModScaffold", Large(i, 0, 1));
            else
                bench.Runs.Real("StructureFrame", Large(i, 0, 1));
        var (from, to) = (Large(0, 0, 0), Large(6, 0, 0));

        var off = bench.Plan(bench.Route(bench.Runs.Cable, from, to));
        var on = bench.Plan(bench.Route(bench.Runs.Cable, from, to, Only(onFrames: true)));

        Assert.True(Straight(off));
        Assert.True(on.Bends >= 2);
        foreach (var cell in on.Run.Cells)
            if (Steps(cell, from) > 4 && Steps(cell, to) > 4)
                Assert.True(cell.Z >= 120, $"{cell} is off the frames");
    }

    [Fact]
    public void On_frames_with_no_frames_finds_no_route()
    {
        var bench = new RouteBench(RouteLimits.Default with { Margin = 4 });

        var why = bench.Refusal(bench.Route(bench.Runs.Cable, Large(0, 0, 0), Large(6, 0, 0), Only(onFrames: true)));

        Assert.Equal(ReasonCodes.NoRoute, why.Code);
    }

    private static int Steps(Cell a, Cell b) => (Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) + Math.Abs(a.Z - b.Z)) / 5;

    [Fact]
    public void Doors_clear_goes_round_a_doors_keep_out()
    {
        var bench = new RouteBench();
        bench.Runs.Real("ModHatch", Large(2, 0, 0));
        var (from, to) = (Large(0, 0, 0), Large(6, 0, 0));

        var off = bench.Plan(bench.Route(bench.Runs.Cable, from, to));
        var on = bench.Plan(bench.Route(bench.Runs.Cable, from, to, Only(clear: true)));

        Assert.True(Straight(off));
        Assert.False(Straight(on));
        foreach (var cell in on.Run.Cells)
            Assert.False(InDoorKeepOut(cell) && !(cell.X == 160 && cell.Y == 110 && cell.Z is >= 110 and <= 120), $"{cell} is in front of the door");
    }

    // The hatch at large cell (2, 0, 0) takes its x+ face: the plane x = 160, y and z 100 to 120, 1 m each side.
    private static bool InDoorKeepOut(Cell cell) =>
        Math.Abs(cell.X - 160) <= 10 && cell.Y is >= 100 and <= 120 && cell.Z is >= 100 and <= 120;

    [Fact]
    public void A_route_to_a_doors_port_reaches_it_through_the_keep_out()
    {
        var bench = new RouteBench();
        bench.Runs.Real("ModHatch", Large(2, 0, 0));

        var plan = bench.Plan(bench.Route(bench.Runs.Cable, new Cell(160, 110, 150), new Cell(160, 110, 110), Only(clear: true)));

        Assert.True(Straight(plan));
    }

    [Fact]
    public void Windows_clear_keeps_off_a_windows_plane_but_not_off_a_floor()
    {
        var bench = new RouteBench();
        bench.Runs.Real("StructureCompositeWindowIron", Large(2, 0, 0));
        bench.Runs.Real("StructureCompositeWindowIron", Large(2, 0, 4), bench.Sending(Direction.ZMinus, Direction.YMinus));
        var (from, to) = (new Cell(110, 110, 100), new Cell(230, 110, 100));
        var (floorFrom, floorTo) = (new Cell(110, 100, 190), new Cell(230, 100, 190));

        var off = bench.Plan(bench.Route(bench.Runs.Cable, from, to));
        var on = bench.Plan(bench.Route(bench.Runs.Cable, from, to, Only(clear: true)));
        var floor = bench.Plan(bench.Route(bench.Runs.Cable, floorFrom, floorTo, Only(clear: true)));

        Assert.True(Straight(off));
        Assert.False(Straight(on));
        foreach (var cell in on.Run.Cells)
            Assert.False(Math.Abs(cell.Z - 100) <= 5 && cell.X is >= 140 and <= 160 && cell.Y is >= 100 and <= 120, $"{cell} is on the window");
        Assert.True(Straight(floor));
    }

    [Fact]
    public void Ports_free_keeps_off_the_cell_a_device_port_faces()
    {
        var bench = new RouteBench();
        var portCell = At(10, 2, 2);
        bench.Runs.Real("StructureAreaPowerControl", At(10, 2, 3), bench.Sending(Direction.XMinus, Direction.ZMinus));

        var off = bench.Plan(bench.Route(bench.Runs.Cable, Start, Far));
        var on = bench.Plan(bench.Route(bench.Runs.Cable, Start, Far, Only(ports: true)));

        Assert.Contains(portCell, off.Run.Cells);
        Assert.DoesNotContain(portCell, on.Run.Cells);
    }

    [Fact]
    public void No_unsafe_joins_keeps_away_from_other_real_and_planned_networks()
    {
        var bench = new RouteBench();
        bench.Runs.Real("StructureCableStraight", At(10, 2, 3));
        bench.Runs.Ghost("StructureCableStraight", At(13, 2, 1));

        var off = bench.Plan(bench.Route(bench.Runs.Cable, Start, Far));
        var on = bench.Plan(bench.Route(bench.Runs.Cable, Start, Far, Only(joins: true)));

        Assert.True(Straight(off));
        Assert.DoesNotContain(At(10, 2, 2), on.Run.Cells);
        Assert.DoesNotContain(At(13, 2, 2), on.Run.Cells);
    }

    [Fact]
    public void No_unsafe_joins_runs_along_the_ends_own_network()
    {
        var bench = new RouteBench();
        bench.Runs.Real("StructureCableStraight", At(10, 2, 3));

        var plan = bench.Plan(bench.Route(bench.Runs.Cable, At(10, 2, 4), At(10, 2, -4), Only(joins: true)));

        Assert.True(Straight(plan));
    }

    [Fact]
    public void A_planned_removal_frees_the_network_it_takes()
    {
        var bench = new RouteBench();
        var beside = bench.Runs.Real("StructureCableStraight", At(10, 2, 3));
        bench.Runs.Boards.Placed(TestBoard.Draft(new Intent.Remove(beside)));

        var plan = bench.Plan(bench.Route(bench.Runs.Cable, Start, Far, Only(joins: true)));

        Assert.True(Straight(plan));
    }

    [Fact]
    public void A_device_on_the_line_blocks_it_whatever_the_toggles()
    {
        var bench = new RouteBench();
        bench.Runs.Real("StructureAreaPowerControl", At(10, 2, 2));

        var plan = bench.Plan(bench.Route(bench.Runs.Cable, Start, Far));

        Assert.DoesNotContain(At(10, 2, 2), plan.Run.Cells);
    }

    [Fact]
    public void A_planned_device_on_the_line_blocks_it()
    {
        var bench = new RouteBench();
        bench.Runs.Ghost("StructureAreaPowerControl", At(10, 2, 2));

        var plan = bench.Plan(bench.Route(bench.Runs.Cable, Start, Far));

        Assert.DoesNotContain(At(10, 2, 2), plan.Run.Cells);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void A_cable_crosses_a_pipe_but_cannot_run_alongside_one_in_a_cell(bool pipeAlongTheLine, bool straight)
    {
        var bench = new RouteBench();
        bench.Runs.Real("StructurePipeStraight", At(10, 2, 2), pipeAlongTheLine ? bench.Sending(Direction.ZPlus, Direction.XPlus) : default);

        var plan = bench.Plan(bench.Route(bench.Runs.Cable, Start, Far));

        Assert.Equal(straight, Straight(plan));
    }

    [Fact]
    public void A_route_along_the_kits_own_piece_merges_with_it()
    {
        var bench = new RouteBench();
        var standing = At(10, 2, 2);
        bench.Runs.Real("StructureCableStraight", standing, bench.Sending(Direction.ZPlus, Direction.XPlus));

        var plan = bench.Plan(bench.Route(bench.Runs.Cable, Start, Far));

        Assert.True(Straight(plan));
        Assert.DoesNotContain(plan.Run.Pieces, piece => bench.Runs.CellsOf(piece).Contains(standing));
    }

    [Fact]
    public void A_failing_cell_reads_as_the_same_drawn_runs()
    {
        var bench = new RouteBench();
        bench.Runs.Check.Refuse(p => bench.Runs.CellsOf(p).Contains(At(10, 2, 2)), ReasonCodes.GameRefused, "something in the way");

        var plan = bench.Plan(bench.Route(bench.Runs.Cable, Start, Far));
        var drawn = bench.Runs.Plan(plan.AsRun);

        Assert.True(plan.Run.FirstFailure.TryGet(out var first));
        Assert.Equal(At(10, 2, 2), first.At);
        Assert.Equal(drawn.FirstFailure, plan.Run.FirstFailure);
        Assert.Equal(drawn.Pieces, plan.Run.Pieces);
    }

    [Fact]
    public void A_modded_kit_routes_round_a_modded_door()
    {
        var bench = new RouteBench();
        bench.Runs.Real("ModHatch", Large(2, 0, 0));
        var (from, to) = (Large(0, 0, 0), Large(6, 0, 0));

        var off = bench.Plan(bench.Route(bench.Runs.Conduit, from, to));
        var on = bench.Plan(bench.Route(bench.Runs.Conduit, from, to, Only(clear: true)));

        Assert.True(Straight(off));
        Assert.All(off.Run.Pieces, piece => Assert.StartsWith("ModConduit", bench.Runs.NameOf(piece.Piece)));
        Assert.False(Straight(on));
        Assert.DoesNotContain(new Cell(150, 110, 110), on.Run.Cells);
    }

    private static RouteBench WalledIn(Cell goal, RouteLimits? limits = null)
    {
        var bench = new RouteBench(limits);
        for (var direction = Direction.XPlus; direction <= Direction.ZMinus; direction++)
            bench.Runs.Real("StructureAreaPowerControl", Step(goal, direction));
        return bench;
    }

    [Fact]
    public void No_route_gives_the_reason_and_the_nearest_reach()
    {
        var goal = At(10, 2, 2);
        var bench = WalledIn(goal, RouteLimits.Default with { Margin = 2 });

        var why = bench.Refusal(bench.Route(bench.Runs.Cable, Start, goal));

        Assert.Equal(ReasonCodes.NoRoute, why.Code);
        Assert.Equal("2", why.Facts["cells-left"]);
        var nearest = why.Facts["nearest"].Split(',').Select(int.Parse).ToArray();
        Assert.Equal(2, Steps(new Cell(nearest[0], nearest[1], nearest[2]), goal));
    }

    [Fact]
    [Trait(Perf.Trait, Perf.Value)]
    public void No_route_is_found_out_within_a_second()
    {
        var goal = At(10, 2, 2);
        var bench = WalledIn(goal, RouteLimits.Default with { Margin = 2 });
        Perf.Collect();

        var watch = Stopwatch.StartNew();
        var why = bench.Refusal(bench.Route(bench.Runs.Cable, Start, goal));
        watch.Stop();
        output.WriteLine($"no route in {watch.Elapsed.TotalMilliseconds:F1} ms, facts {why.Facts}");

        Assert.Equal(ReasonCodes.NoRoute, why.Code);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"took {watch.Elapsed}");
    }

    [Fact]
    public void A_search_past_its_bound_gives_search_limit()
    {
        var bench = new RouteBench(RouteLimits.Default with { Expansions = Option.Some(5) });

        var why = bench.Refusal(bench.Route(bench.Runs.Cable, Start, Far));

        Assert.Equal(ReasonCodes.SearchLimit, why.Code);
        Assert.Equal("5", why.Facts["bound"]);
        Assert.Equal("5", why.Facts["expanded"]);
    }

    [Fact]
    public void A_bound_on_the_request_takes_the_place_of_the_planner_limit()
    {
        var bench = new RouteBench();

        var why = bench.Refusal(bench.Route(bench.Runs.Cable, Start, Far) with { SearchBound = Option.Some(5) });

        Assert.Equal(ReasonCodes.SearchLimit, why.Code);
        Assert.Equal("5", why.Facts["bound"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void A_request_bound_below_one_is_refused_as_an_invalid_value(int bound)
    {
        var bench = new RouteBench();

        var why = bench.Refusal(bench.Route(bench.Runs.Cable, Start, Far) with { SearchBound = Option.Some(bound) });

        Assert.Equal(ReasonCodes.InvalidValue, why.Code);
        Assert.Equal("search-bound", why.Facts["problem"]);
    }

    [Fact]
    public void The_bound_grows_with_the_distance_between_the_ends()
    {
        Assert.True(RouteLimits.Default.ExpansionsFor(500) > RouteLimits.Default.ExpansionsFor(10));
        Assert.Equal(7, (RouteLimits.Default with { Expansions = Option.Some(7) }).ExpansionsFor(500));
    }

    [Fact]
    [Trait(Perf.Trait, Perf.Value)]
    public void Each_step_of_a_long_search_fits_the_checking_slice()
    {
        var goal = At(30, 2, 2);
        var budget = FrameBudgets.Default.Checking.Length;
        List<TimeSpan>? fastest = null;
        var total = TimeSpan.Zero;
        for (var run = 0; run < 3; run++)
        {
            var bench = WalledIn(goal);
            var job = TestKitIds.Must(bench.Planner.Start(bench.Route(bench.Runs.Cable, Start, goal), bench.Runs.Boards.View, bench.Runs.Query));
            var steps = new List<TimeSpan>();
            var watch = new Stopwatch();
            Perf.Collect();
            var outcome = StepOutcome.More;
            while (outcome == StepOutcome.More)
            {
                watch.Restart();
                outcome = job.Step();
                watch.Stop();
                steps.Add(watch.Elapsed);
            }

            total = steps.Aggregate(TimeSpan.Zero, (a, b) => a + b);
            Assert.True(job.Outcome.TryGet(out var result) && result.TryGetError(out var why) && why.Code == ReasonCodes.SearchLimit);
            fastest = fastest is null ? steps : fastest.Zip(steps, (a, b) => a < b ? a : b).ToList();
        }

        output.WriteLine($"{fastest!.Count} steps, slowest {fastest.Max().TotalMilliseconds:F3} ms, last run {total.TotalMilliseconds:F1} ms");
        Assert.True(fastest.Count > 10);
        Assert.All(fastest, step => Assert.True(step < budget, $"a step took {step.TotalMilliseconds} ms"));
    }

    [Fact]
    public void Stepping_the_job_gives_the_same_route_as_planning_at_once()
    {
        var bench = new RouteBench(RouteLimits.Default with { PerStep = 4 });
        bench.Runs.Real("StructureAreaPowerControl", At(10, 2, 2));
        var request = bench.Route(bench.Runs.Cable, Start, At(14, 6, 9));

        var job = TestKitIds.Must(bench.Planner.Start(request, bench.Runs.Boards.View, bench.Runs.Query));
        var steps = 0;
        while (job.Step() == StepOutcome.More)
            steps++;
        Assert.True(job.Outcome.TryGet(out var stepped));

        Assert.True(steps > 1);
        Assert.Equal(bench.Plan(request).Run.Cells, TestKitIds.Must(stepped).Run.Cells);
    }

    [Fact]
    public void Ends_off_the_grid_and_unknown_kits_are_refused_at_once()
    {
        var bench = new RouteBench();

        var offGrid = bench.Refusal(bench.Route(bench.Runs.Cable, Start, new Cell(101, 100, 100)));
        var unknown = bench.Planner.Start(bench.Route(new KitKey(12345), Start, Far), bench.Runs.Boards.View, bench.Runs.Query);

        Assert.Equal(ReasonCodes.InvalidValue, offGrid.Code);
        Assert.Equal("off-grid", offGrid.Facts["problem"]);
        Assert.False(unknown.IsOk);
    }

    [Fact]
    public void The_expansion_places_the_routes_ghosts()
    {
        var bench = new RouteBench();
        var request = bench.Route(bench.Runs.Cable, Start, At(6, 2, 6));
        var input = new ExpansionInput<Command.PlanRoute>(
            new Command.PlanRoute(request), new CommandId(Guid.NewGuid()), TestBoard.Alice, bench.Runs.Boards.View, bench.Runs.Query);

        var expected = bench.Plan(request).Run.Pieces;
        var expansion = new RouteExpander(bench.Planner).Start(input);
        while (expansion.Step() == StepOutcome.More)
        {
        }

        TestBoard.Applied(bench.Runs.Boards.Apply(TestKitIds.Must(expansion.Found)));
        Assert.NotEmpty(expected);
        foreach (var piece in expected)
            Assert.Contains(bench.Runs.Boards.View.At(piece.At), ghost => ghost.Intent is Intent.Build build && build.Target == piece);
    }

    [Fact]
    public void A_request_that_cannot_be_searched_finishes_its_expansion_at_once_with_the_refusal()
    {
        var bench = new RouteBench();
        var input = new ExpansionInput<Command.PlanRoute>(
            new Command.PlanRoute(bench.Route(new KitKey(12345), Start, Far)), new CommandId(Guid.NewGuid()), TestBoard.Alice,
            bench.Runs.Boards.View, bench.Runs.Query);

        var expansion = new RouteExpander(bench.Planner).Start(input);

        Assert.Equal(StepOutcome.Done, expansion.Step());
        Assert.False(expansion.Found.IsOk);
    }
}
