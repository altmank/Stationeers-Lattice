using System.Linq;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Catalogue;
using Lattice.Core.Grid;
using Lattice.Core.Ports;
using Lattice.Core.Runs;
using Lattice.TestKit;
using Lattice.TestKit.PlacementCheck;

namespace Lattice.Tests.Sync;

/// <summary>A client's run with a port end: sent as written, resolved on the host against the host's board, joined there.</summary>
public sealed class PortEndSyncTests
{
    [Fact]
    public void A_client_run_ending_at_a_ghosts_port_is_laid_on_the_host_and_joins_the_port()
    {
        var bench = new SyncBench();
        var host = bench.Host.T;
        Assert.True(bench.Host.Expanders.Register(new RunExpander(new RunPlanner(host.Catalogue, host.Grid, new FakePlacementCheck())).Descriptor).IsOk);
        var (alice, _) = bench.Join(connection: 1, player: 7);
        bench.Frame();
        var control = Assert.Single(Assert.IsType<Outcome.Applied>(bench.Host.Submit(new Command.Place(ValueList.Of(bench.Draft("StructureAreaPowerControl", new Cell(100, 100, 100)))))).Changes.Added);
        var input = TestKitIds.Must(PiecePorts.Over(host.Catalogue).At(((Intent.Build)control.Intent).Target))
            .Single(static p => p.Role == Option.Some(PortRole.Input));
        bench.Frames(2);
        var coil = host.Catalogue.Named("StructureCableStraight").Kits[0].Kit;
        var run = new Command.PlanRun(new RunRequest(coil, input.Joins.Plus(new CellOffset(0, 0, 15)), ValueList<Cell>.Empty, input.Joins, Option<PlanId>.None, Board.TestBoard.TestMaker)
        {
            ToPort = Option.Some<PortEnd>(new PortEnd.OnGhost(control.Id, input.Index)),
        });

        alice.Submit(SyncBench.Id(1), run, SyncBench.HostPlayer);
        bench.Frames(4);

        Assert.IsType<Outcome.Applied>(Assert.Single(bench.Answers).Outcome);
        var shapes = new Shapes(host.Catalogue, host.Grid);
        var end = bench.HostState().Ghosts.Single(g => g.Intent is Intent.Build build && build.Target.At == input.Joins);
        var ends = TestKitIds.Must(shapes.Of(((Intent.Build)end.Intent).Target)).Ends;
        Assert.Contains(ends, e => e.Cell == input.Joins && e.Facing.ToFace() == Opposite(input.Toward));
        Assert.Empty(bench.HostReports);
    }

    private static Face Opposite(Face face) => face switch
    {
        Face.PlusX => Face.MinusX,
        Face.MinusX => Face.PlusX,
        Face.PlusY => Face.MinusY,
        Face.MinusY => Face.PlusY,
        Face.PlusZ => Face.MinusZ,
        _ => Face.PlusZ,
    };
}
