using System.Linq;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.Core.Grid;
using Lattice.Core.Routes;
using Lattice.Core.Slices;
using Lattice.TestKit;
using Lattice.Tests.Routes;
using static Lattice.Tests.Runs.RunBench;

namespace Lattice.Tests.Runs;

/// <summary>
/// Run and route ends named by a built structure's port (<see cref="PortEnd.OnStructure"/>): found by id, else by piece
/// and cell; refused before anything is laid when they do not hold; resolved again when the run is laid, so a structure
/// gone by then refuses the command; and joined by the end piece, as an end in the port's joining cell is.
/// </summary>
public sealed class StructurePortEndTests
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
        """;

    private const string PowerValve = """
        piece ModValve kit=ItemModValve option=0 grid=small
          step material=ItemModValve*1 exit=ItemWrench air=no
          end 0 0 0 z- power reach=273 role=output
          end 0 0 0 z+ power reach=275 role=input
          cell 0 0 0 device
          collision 4
        """;

    private static readonly PortIndex Output = Index(0);
    private static readonly PortIndex Input = Index(1);
    private static readonly PortIndex Power = Index(2);

    private static PortIndex Index(int value) => TestKitIds.Must(PortIndex.Create(value));

    private static RunBench Bench() => new(Devices);

    private static Port PortOf(RunBench bench, StructureRef structure, PortIndex index, Turn turn = default) =>
        TestKitIds.Must(PiecePorts.Over(bench.Catalogue).At(new Placement(structure.Piece, structure.At, turn))).Single(p => p.Index == index);

    private static Option<PortEnd> On(StructureRef structure, PortIndex index) => Option.Some<PortEnd>(new PortEnd.OnStructure(structure, index));

    private static RunRequest ToPort(RunBench bench, KitKey kit, Cell from, StructureRef structure, PortIndex index) =>
        bench.Run(kit, from, PortOf(bench, structure, index).Joins) with { ToPort = On(structure, index) };

    private static KitKey Pipe(RunBench bench) => bench.KitOf("StructurePipeStraight");

    [Fact]
    public void A_run_to_a_built_devices_port_reports_the_end_piece_that_joins_it_and_leaves_into_the_port()
    {
        var bench = Bench();
        var valve = bench.Real("ModValve", At(0, 0, 0));
        var joins = PortOf(bench, valve, Input).Joins;

        var plan = bench.Plan(ToPort(bench, Pipe(bench), At(3, 0, 1), valve, Input));

        var end = plan.Pieces.Single(p => bench.CellsOf(p).Contains(joins));
        Assert.Equal(Ways("x+,z-"), bench.WaysOf(end, joins));
        var join = Assert.Single(plan.PortEnds);
        Assert.Equal(Option.Some(end), join.JoinedBy);
        Assert.Equal(Input, join.Port.Index);
    }

    [Fact]
    public void A_route_to_a_built_port_with_ports_kept_free_ends_joined_and_keeps_off_the_devices_other_port()
    {
        var bench = new RouteBench(extraPieces: PowerValve);
        var valve = bench.Runs.Real("ModValve", At(6, 2, 4));
        var joins = PortOf(bench.Runs, valve, Input).Joins;
        var other = PortOf(bench.Runs, valve, Output).Joins;
        var request = bench.Route(bench.Runs.Cable, At(6, 2, 0), joins, new RouteToggles(false, false, false, true, false)) with { ToPort = On(valve, Input) };

        var route = bench.Plan(request);

        Assert.Equal(joins, route.Run.Cells[route.Run.Cells.Count - 1]);
        Assert.DoesNotContain(other, route.Run.Cells);
        Assert.True(Assert.Single(route.Run.PortEnds).JoinedBy.IsSome);
    }

    [Fact]
    public void A_device_turned_since_the_port_was_read_is_changed_since()
    {
        var bench = Bench();
        var valve = bench.Real("ModValve", At(0, 0, 0));
        var read = PortOf(bench, valve, Input).Joins;
        var turned = TestKitIds.Must(Turn.Create(1));
        bench.World.Move(ValueList.Of(new StructureMove(valve, valve.At, turned)));

        var why = bench.Refusal(bench.Run(Pipe(bench), At(3, 0, 1), read) with { ToPort = On(valve, Input) });

        Assert.Equal(ReasonCodes.ChangedSince, why.Code);
        Assert.Equal(valve.Id.Value.ToString(), why.Facts["structure"]);
        Assert.Equal($"{read.X},{read.Y},{read.Z}", why.Facts["expected"]);
        var now = PortOf(bench, valve, Input, turned).Joins;
        Assert.Equal($"{now.X},{now.Y},{now.Z}", why.Facts["now"]);
    }

    [Fact]
    public void A_pipe_kit_to_a_power_port_is_a_port_mismatch_naming_the_structure()
    {
        var bench = Bench();
        var valve = bench.Real("ModValve", At(0, 0, 0));

        var why = bench.Refusal(ToPort(bench, Pipe(bench), At(-3, 3, 0), valve, Power));

        Assert.Equal(ReasonCodes.PortMismatch, why.Code);
        Assert.Equal(valve.Id.Value.ToString(), why.Facts["structure"]);
        Assert.Equal("2", why.Facts["port"]);
        Assert.Equal(Pipe(bench).Hash.ToString(), why.Facts["kit"]);
    }

    [Fact]
    public void An_index_the_structures_piece_lacks_is_not_found_as_a_port()
    {
        var bench = Bench();
        var valve = bench.Real("ModValve", At(0, 0, 0));

        var why = bench.Refusal(bench.Run(Pipe(bench), At(3, 0, 1), At(0, 0, 1)) with { ToPort = On(valve, Index(7)) });

        Assert.Equal(ReasonCodes.NotFound, why.Code);
        Assert.Equal("port", why.Facts["problem"]);
        Assert.Equal("7", why.Facts["port"]);
    }

    [Fact]
    public void A_structure_that_no_longer_stands_is_not_found()
    {
        var bench = Bench();
        var valve = bench.Real("ModValve", At(0, 0, 0));
        var joins = PortOf(bench, valve, Input).Joins;
        bench.World.Remove(valve.Id);

        var why = bench.Refusal(bench.Run(Pipe(bench), At(3, 0, 1), joins) with { ToPort = On(valve, Input) });

        Assert.Equal(ReasonCodes.NotFound, why.Code);
        Assert.Equal(valve.Id.Value.ToString(), why.Facts["structure"]);
    }

    [Fact]
    public void Both_ends_at_one_built_port_are_refused()
    {
        var bench = Bench();
        var valve = bench.Real("ModValve", At(0, 0, 0));
        var joins = PortOf(bench, valve, Input).Joins;

        var why = bench.Refusal(bench.Run(Pipe(bench), joins, joins) with { FromPort = On(valve, Input), ToPort = On(valve, Input) });

        Assert.Equal(ReasonCodes.InvalidValue, why.Code);
        Assert.Equal("same-port", why.Facts["problem"]);
    }

    [Fact]
    public void A_structure_whose_id_changed_is_found_by_its_piece_and_cell()
    {
        var bench = Bench();
        var valve = bench.Real("ModValve", At(0, 0, 0));
        var stale = valve with { Id = new ReferenceId(99999) };

        var plan = bench.Plan(ToPort(bench, Pipe(bench), At(3, 0, 1), stale, Input));

        Assert.True(Assert.Single(plan.PortEnds).JoinedBy.IsSome);
    }

    [Fact]
    public void A_structure_taken_down_while_the_route_is_searched_refuses_the_route_and_lays_nothing()
    {
        var bench = new RouteBench(RouteLimits.Default with { PerStep = 2 }, PowerValve);
        var valve = bench.Runs.Real("ModValve", At(6, 2, 4));
        var joins = PortOf(bench.Runs, valve, Input).Joins;
        var request = bench.Route(bench.Runs.Cable, At(0, 2, 0), joins) with { ToPort = On(valve, Input) };
        var job = TestKitIds.Must(bench.Planner.Start(request, bench.Runs.Boards.View, bench.Runs.Query));
        Assert.Equal(StepOutcome.More, job.Step());

        bench.Runs.World.Remove(valve.Id);
        var outcome = job.Run();

        Assert.True(outcome.TryGetError(out var why));
        Assert.Equal(ReasonCodes.NotFound, why.Code);
        Assert.Equal(valve.Id.Value.ToString(), why.Facts["structure"]);
        Assert.Empty(bench.Runs.Boards.Board.Index.Ghosts);
    }

    [Fact]
    public void A_run_ending_in_the_same_cell_without_a_port_end_lays_the_same_pieces_and_reports_no_port()
    {
        var bench = Bench();
        var valve = bench.Real("ModValve", At(0, 0, 0));

        var named = bench.Plan(ToPort(bench, Pipe(bench), At(3, 0, 1), valve, Input));
        var plain = bench.Plan(bench.Run(Pipe(bench), At(3, 0, 1), PortOf(bench, valve, Input).Joins));

        Assert.Empty(plain.PortEnds);
        Assert.Equal(named.Pieces, plain.Pieces);
    }
}
