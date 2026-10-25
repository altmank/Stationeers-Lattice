using System.Linq;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.Core.Grid;
using Lattice.Core.Openings;
using Lattice.Core.Ports;
using Lattice.Core.Routes;
using Lattice.Core.Runs;
using Lattice.TestKit;
using Lattice.Tests.Board;
using Lattice.Tests.Routes;
using static Lattice.Tests.Runs.RunBench;

namespace Lattice.Tests.Runs;

/// <summary>
/// Run and route ends named by a ghost's port (<see cref="RunRequest.ToPort"/>): resolved against the board as the
/// sender sees it, refused before anything is laid when they do not hold, and joined by the end piece, which leaves
/// back into the port whether or not the neighbourhood shows the ghost's end.
/// </summary>
public sealed class PortEndTests
{
    private const string Devices = """
        lattice-catalogue 3
        piece ModValve kit=ItemModValve option=0 grid=small
          step material=ItemModValve*1 exit=ItemWrench air=no
          end 0 0 0 z- gas reach=273 role=output
          end 0 0 0 z+ gas reach=275 role=input
          end 0 0 0 x- power,data reach=257
          cell 0 0 0 device
          collision 4

        piece ModTallNode kit=ItemModTallNode option=0 grid=small
          step material=ItemModTallNode*1 exit=ItemWrench air=no
          end 0 5 0 z+ power reach=300 role=input
          collision 4

        piece ModFluxNode kit=ItemModFluxNode option=0 grid=small
          step material=ItemModFluxNode*1 exit=ItemWrench air=no
          end 0 0 0 z+ mod:ModFlux:512 reach=300 role=input
          cell 0 0 0 device
          collision 4

        piece ModFluxLine kit=ItemModFluxLine option=0 grid=small
          step material=ItemModFluxLine*1 exit=ItemWrench air=no
          end 0 0 0 z+ mod:ModFlux:512 reach=400
          end 0 0 0 z- mod:ModFlux:512 reach=400
          cell 0 0 0 other

        piece ModLiquidLine kit=ItemModLiquidLine option=0 grid=small pipe=liquid
          step material=ItemModLiquidLine*1 exit=ItemWrench air=no
          end 0 0 0 z+ liquid reach=370
          end 0 0 0 z- liquid reach=370
          cell 0 0 0 pipe
          collision 1

        piece ModRodLine kit=ItemModRod option=0 grid=small
          step material=ItemModRod*1 exit=ItemWrench air=no
          end 0 0 0 z+ power reach=400
          end 0 0 0 z- power reach=400
          cell 0 0 0 cable
          collision 2
        """;

    private static readonly PortIndex Output = Index(0);
    private static readonly PortIndex Input = Index(1);
    private static readonly PortIndex Power = Index(2);

    private static PortIndex Index(int value) => TestKitIds.Must(PortIndex.Create(value));

    private static RunBench Bench() => new(Devices);

    private static GhostId Place(RunBench bench, string piece, Cell at, Turn turn = default) =>
        bench.Boards.Placed(bench.Boards.Draft(piece, at, turn)).Added[0].Id;

    private static Port PortOf(RunBench bench, GhostId ghost, PortIndex index)
    {
        var target = ((Intent.Build)bench.Boards.View.Find(ghost).Reduce(() => throw new System.InvalidOperationException("no ghost")).Intent).Target;
        return TestKitIds.Must(PiecePorts.Over(bench.Catalogue).At(target)).Single(p => p.Index == index);
    }

    private static RunRequest ToPort(RunBench bench, KitKey kit, Cell from, GhostId ghost, PortIndex index) =>
        bench.Run(kit, from, PortOf(bench, ghost, index).Joins) with { ToPort = Option.Some<PortEnd>(new PortEnd.OnGhost(ghost, index)) };

    private static KitKey Pipe(RunBench bench) => bench.KitOf("StructurePipeStraight");

    [Fact]
    public void A_run_arriving_square_to_a_port_lays_a_corner_into_it_and_reports_the_piece()
    {
        var bench = Bench();
        var valve = Place(bench, "ModValve", At(0, 0, 0));
        var joins = PortOf(bench, valve, Input).Joins;

        var plan = bench.Plan(ToPort(bench, Pipe(bench), At(3, 0, 1), valve, Input));

        var end = plan.Pieces.Single(p => bench.CellsOf(p).Contains(joins));
        Assert.Equal(Ways("x+,z-"), bench.WaysOf(end, joins));
        var join = Assert.Single(plan.PortEnds);
        Assert.Equal(Option.Some(end), join.JoinedBy);
        Assert.Equal(Input, join.Port.Index);
    }

    [Fact]
    public void A_run_starting_at_a_port_joins_it_from_its_first_piece()
    {
        var bench = Bench();
        var valve = Place(bench, "ModValve", At(0, 0, 0));
        var joins = PortOf(bench, valve, Output).Joins;
        var request = bench.Run(Pipe(bench), joins, At(3, 0, -1)) with { FromPort = Option.Some<PortEnd>(new PortEnd.OnGhost(valve, Output)) };

        var plan = bench.Plan(request);

        Assert.True(bench.WaysOf(plan.Pieces[0], joins).Contains(Direction.ZPlus));
        Assert.True(Assert.Single(plan.PortEnds).JoinedBy.IsSome);
    }

    [Fact]
    public void A_port_whose_cell_lies_outside_the_ghosts_spots_still_joins()
    {
        var bench = Bench();
        var node = Place(bench, "ModTallNode", At(0, 0, 0));
        var port = PortOf(bench, node, Index(0));
        Assert.Equal(At(0, 1, 0), port.From);

        var plan = bench.Plan(ToPort(bench, bench.Cable, At(3, 1, 1), node, Index(0)));

        var end = plan.Pieces.Single(p => bench.CellsOf(p).Contains(port.Joins));
        Assert.True(bench.WaysOf(end, port.Joins).Contains(Direction.ZMinus));
    }

    [Fact]
    public void A_port_of_a_swap_ghosts_new_piece_is_an_end()
    {
        var bench = Bench();
        var old = bench.Real("ModFluxNode", At(0, 0, 0));
        var swap = bench.Boards.Placed(TestBoard.Draft(new Intent.Swap(old, new Placement(bench.Catalogue.Named("ModValve").Key, At(0, 0, 0), default), Option<Finish>.None))).Added[0].Id;
        var joins = TestKitIds.Must(PiecePorts.Over(bench.Catalogue).At(new Placement(bench.Catalogue.Named("ModValve").Key, At(0, 0, 0), default)))[1].Joins;

        var plan = bench.Plan(bench.Run(Pipe(bench), At(3, 0, 1), joins) with { ToPort = Option.Some<PortEnd>(new PortEnd.OnGhost(swap, Input)) });

        Assert.True(Assert.Single(plan.PortEnds).JoinedBy.IsSome);
    }

    [Fact]
    public void An_unknown_ghost_is_not_found()
    {
        var bench = Bench();

        var why = bench.Refusal(bench.Run(Pipe(bench), At(3, 0, 1), At(0, 0, 1)) with { ToPort = Option.Some<PortEnd>(new PortEnd.OnGhost(new GhostId(999), Input)) });

        Assert.Equal(ReasonCodes.NotFound, why.Code);
        Assert.Equal("999", why.Facts["ghost"]);
        Assert.Equal("1", why.Facts["port"]);
    }

    [Fact]
    public void A_ghost_the_sender_may_not_see_answers_as_an_unknown_ghost_does()
    {
        var bench = Bench();
        Assert.True(bench.Boards.Policies.Install(new AccessPolicy(
            static (actor, ghost) => ghost.PlacedBy == actor, static (_, _) => true, static _ => Result<Unit, Reason>.Ok(Unit.Value))).IsOk);
        var hidden = TestBoard.Applied(bench.Boards.Apply(
            new Command.Place(ValueList.Of(bench.Boards.Draft("ModValve", At(0, 0, 0)))), TestBoard.Bob)).Added[0].Id;
        var joins = TestKitIds.Must(PiecePorts.Over(bench.Catalogue).At(new Placement(bench.Catalogue.Named("ModValve").Key, At(0, 0, 0), default)))[1].Joins;
        var unknown = new GhostId(hidden.Value + 1000);

        var seen = bench.Refusal(bench.Run(Pipe(bench), At(3, 0, 1), joins) with { ToPort = Option.Some<PortEnd>(new PortEnd.OnGhost(hidden, Input)) });
        var none = bench.Refusal(bench.Run(Pipe(bench), At(3, 0, 1), joins) with { ToPort = Option.Some<PortEnd>(new PortEnd.OnGhost(unknown, Input)) });

        Assert.Equal(none.Code, seen.Code);
        Assert.Equal(none.Facts.Count, seen.Facts.Count);
        Assert.Equal(ReasonCodes.NotFound, seen.Code);
    }

    [Fact]
    public void An_index_the_piece_lacks_is_not_found_as_a_port()
    {
        var bench = Bench();
        var valve = Place(bench, "ModValve", At(0, 0, 0));

        var why = bench.Refusal(bench.Run(Pipe(bench), At(3, 0, 1), At(0, 0, 1)) with { ToPort = Option.Some<PortEnd>(new PortEnd.OnGhost(valve, Index(7))) });

        Assert.Equal(ReasonCodes.NotFound, why.Code);
        Assert.Equal("port", why.Facts["problem"]);
        Assert.Equal("7", why.Facts["port"]);
    }

    [Fact]
    public void A_removal_ghost_has_no_port_of_its_own()
    {
        var bench = Bench();
        var standing = bench.Real("ModValve", At(0, 0, 0));
        var removal = bench.Boards.Placed(TestBoard.Draft(new Intent.Remove(standing))).Added[0].Id;

        var why = bench.Refusal(bench.Run(Pipe(bench), At(3, 0, 1), At(0, 0, 1)) with { ToPort = Option.Some<PortEnd>(new PortEnd.OnGhost(removal, Input)) });

        Assert.Equal(ReasonCodes.InvalidValue, why.Code);
        Assert.Equal("no-new-piece", why.Facts["problem"]);
    }

    [Fact]
    public void A_cell_that_is_not_the_ports_joining_cell_now_is_changed_since()
    {
        var bench = Bench();
        var valve = Place(bench, "ModValve", At(0, 0, 0));
        var read = PortOf(bench, valve, Input).Joins;
        TestBoard.Applied(bench.Boards.Apply(new Command.Move(
            ValueMap.Of(valve, new Placement(bench.Catalogue.Named("ModValve").Key, At(0, 0, 4), default)), Option<Revision>.None)));

        var why = bench.Refusal(bench.Run(Pipe(bench), At(3, 0, 1), read) with { ToPort = Option.Some<PortEnd>(new PortEnd.OnGhost(valve, Input)) });

        Assert.Equal(ReasonCodes.ChangedSince, why.Code);
        Assert.Equal($"{read.X},{read.Y},{read.Z}", why.Facts["expected"]);
        var now = PortOf(bench, valve, Input).Joins;
        Assert.Equal($"{now.X},{now.Y},{now.Z}", why.Facts["now"]);
    }

    [Fact]
    public void Both_ends_at_one_port_are_refused()
    {
        var bench = Bench();
        var valve = Place(bench, "ModValve", At(0, 0, 0));
        var port = new PortEnd.OnGhost(valve, Input);
        var joins = PortOf(bench, valve, Input).Joins;

        var why = bench.Refusal(bench.Run(Pipe(bench), joins, joins) with { FromPort = Option.Some<PortEnd>(port), ToPort = Option.Some<PortEnd>(port) });

        Assert.Equal(ReasonCodes.InvalidValue, why.Code);
        Assert.Equal("same-port", why.Facts["problem"]);
    }

    [Theory]
    [InlineData("cable", "input", false)]
    [InlineData("cable", "power", true)]
    [InlineData("liquid", "input", false)]
    [InlineData("gas", "input", true)]
    public void A_kit_joins_only_a_port_it_shares_a_kind_with(string kitName, string portName, bool joins)
    {
        var bench = Bench();
        var valve = Place(bench, "ModValve", At(0, 0, 0));
        var kit = kitName switch
        {
            "cable" => bench.Cable,
            "liquid" => bench.KitOf("ModLiquidLine"),
            _ => Pipe(bench),
        };
        var index = portName == "power" ? Power : Input;
        var from = Step(PortOf(bench, valve, index).Joins, Direction.YPlus, 3);

        var planned = bench.Planner.Plan(ToPort(bench, kit, from, valve, index), bench.Boards.View, bench.Query);

        if (joins)
            Assert.True(Assert.Single(TestKitIds.Must(planned).PortEnds).JoinedBy.IsSome);
        else
        {
            var why = planned.TryGetError(out var refused) ? refused : throw new System.InvalidOperationException("planned");
            Assert.Equal(ReasonCodes.PortMismatch, why.Code);
            Assert.Equal(kit.Hash.ToString(), why.Facts["kit"]);
        }
    }

    [Fact]
    public void A_modded_network_kind_joins_only_a_kit_that_carries_it()
    {
        var bench = Bench();
        var node = Place(bench, "ModFluxNode", At(0, 0, 0));
        var from = At(0, 0, 4);

        var flux = bench.Plan(ToPort(bench, bench.KitOf("ModFluxLine"), from, node, Index(0)));
        var cable = bench.Refusal(ToPort(bench, bench.Cable, from, node, Index(0)));

        Assert.True(Assert.Single(flux.PortEnds).JoinedBy.IsSome);
        Assert.Equal(ReasonCodes.PortMismatch, cable.Code);
    }

    [Fact]
    public void A_kit_with_no_piece_for_the_end_cells_ways_fails_there_and_the_run_is_still_laid()
    {
        var bench = Bench();
        var valve = Place(bench, "ModValve", At(0, 0, 0));
        var joins = PortOf(bench, valve, Power).Joins;

        var plan = bench.Plan(ToPort(bench, bench.KitOf("ModRodLine"), Step(joins, Direction.ZPlus, 3), valve, Power));

        Assert.True(plan.FirstFailure.TryGet(out var failure));
        Assert.Equal(joins, failure.At);
        Assert.Equal(ReasonCodes.NoPiece, failure.Why.Code);
        Assert.True(Assert.Single(plan.PortEnds).JoinedBy.IsNone);
        Assert.NotEmpty(plan.Changes);
    }

    [Fact]
    public void A_request_without_port_ends_reports_none_and_lays_as_before()
    {
        var bench = Bench();
        Place(bench, "ModValve", At(0, 0, 0));

        var plan = bench.Plan(bench.Run(Pipe(bench), At(3, 0, 1), At(0, 0, 1)));

        Assert.Empty(plan.PortEnds);
        Assert.Empty(plan.Report(static _ => new Bill(ValueList<ItemQuantity>.Empty, ValueList<ItemQuantity>.Empty, ValueList<ItemDelta>.Empty, ValueSet<ItemKey>.Empty)).PortEnds);
    }

    [Fact]
    public void A_route_between_two_ports_with_ports_kept_free_ends_on_both_and_keeps_off_a_third_devices_port()
    {
        var bench = Bench();
        var a = Place(bench, "ModValve", At(0, 0, 0));
        var b = Place(bench, "ModValve", At(8, 0, 0));
        Place(bench, "ModValve", At(4, 0, 2));
        var third = At(4, 0, 1);
        var (from, to) = (PortOf(bench, a, Input).Joins, PortOf(bench, b, Input).Joins);
        var planner = new RoutePlanner(bench.Catalogue, bench.Grid, bench.Check, new TestOpeningFacts(), new TestFrames(), OpeningBands.Default, RouteLimits.Default);
        var request = new RouteRequest(Pipe(bench), from, to, new RouteToggles(false, false, false, true, false), Option<PlanId>.None, TestBoard.TestMaker)
        {
            FromPort = Option.Some<PortEnd>(new PortEnd.OnGhost(a, Input)),
            ToPort = Option.Some<PortEnd>(new PortEnd.OnGhost(b, Input)),
        };

        var route = TestKitIds.Must(planner.Plan(request, bench.Boards.View, bench.Query));

        Assert.Equal(from, route.Run.Cells[0]);
        Assert.Equal(to, route.Run.Cells[route.Run.Cells.Count - 1]);
        Assert.DoesNotContain(third, route.Run.Cells);
        Assert.Equal(2, route.Run.PortEnds.Count);
        Assert.All(route.Run.PortEnds, join => Assert.True(join.JoinedBy.IsSome));
    }

    [Fact]
    public void A_route_refuses_a_port_end_before_it_searches()
    {
        var bench = Bench();
        var planner = new RoutePlanner(bench.Catalogue, bench.Grid, bench.Check, new TestOpeningFacts(), new TestFrames(), OpeningBands.Default, RouteLimits.Default);
        var request = new RouteRequest(Pipe(bench), At(0, 0, 1), At(8, 0, 1), new RouteToggles(false, false, false, false, false), Option<PlanId>.None, TestBoard.TestMaker)
        {
            ToPort = Option.Some<PortEnd>(new PortEnd.OnGhost(new GhostId(404), Input)),
        };

        var started = planner.Start(request, bench.Boards.View, bench.Query);

        Assert.True(started.TryGetError(out var why));
        Assert.Equal(ReasonCodes.NotFound, why.Code);
    }

    [Fact]
    public void A_request_with_no_port_ends_compares_and_deconstructs_as_before()
    {
        var bench = Bench();
        var request = bench.Run(Pipe(bench), At(0, 0, 0), At(3, 0, 0));
        var (kit, from, via, to, into, made) = request;

        Assert.Equal(new RunRequest(kit, from, via, to, into, made), request);
        Assert.NotEqual(request with { ToPort = Option.Some<PortEnd>(new PortEnd.OnGhost(new GhostId(1), Input)) }, request);
    }
}
