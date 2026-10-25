using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using static Lattice.Testing.Tests.Hosted;

namespace Lattice.Testing.Tests;

/// <summary>
/// Runs that end at a ghost's port, through the contract alone: a header with pump ghosts on it, the preview and the
/// placement agreeing, a ghost moved after its port was read, the run left as it is when the pump moves or goes, and
/// a started ghost as an end.
/// </summary>
public sealed class PortEndFlows
{
    private static readonly KitKey Pipe = TestCatalogue.Hand.Kit("ItemKitPipe").Reduce(default(KitKey));

    /// <summary>A modded pump on the small grid: a gas input facing -Z and a gas output facing +Z, built in two steps.</summary>
    private static readonly TestPiece Pump = Must(TestPiece.Create("ModHeaderPump", "ItemModHeaderPump", PieceGrid.Small))
        .WithPort(new CellOffset(0, 0, 0), SignedAxis.ZMinus, ValueSet.Of(TestNetwork.Gas), PortRole.Input)
        .WithPort(new CellOffset(0, 0, 0), SignedAxis.ZPlus, ValueSet.Of(TestNetwork.Gas), PortRole.Output)
        .WithCell(new CellOffset(0, 0, 0), TestSlot.Device)
        .WithCollision(4)
        .WithSteps(2);

    private static readonly PortIndex Input = Must(PortIndex.Create(0));

    private static TestWorld World() => Start(TestScenario.Empty.WithPiece(Pump));

    private static Ghost PlacePump(TestWorld world, Cell at) =>
        Assert.Single(Assert.IsType<Outcome.Applied>(Submit(world, new Command.Place(ValueList.Of(Draft(Pump.Key, at))))).Changes.Added);

    private static Port InputOf(TestWorld world, GhostId pump)
    {
        var ghost = world.System.Board.Find(pump).Reduce(() => throw new Xunit.Sdk.XunitException("no pump"));
        return Must(world.System.Catalogue.PortsAt(((Intent.Build)ghost.Intent).Target))[0];
    }

    private static Command.PlanRun RunTo(Cell from, Port port, GhostId pump) =>
        new(new RunRequest(Pipe, from, ValueList<Cell>.Empty, port.Joins, Option<PlanId>.None, Made)
        {
            ToPort = Option.Some<PortEnd>(new PortEnd.OnGhost(pump, port.Index)),
        });

    /// <summary>Whether a ghost in the port's joining cell leaves it back into the port.</summary>
    private static bool Joined(TestWorld world, Port port)
    {
        foreach (var ghost in world.System.Board.At(port.Joins))
            if (ghost.Intent is Intent.Build build && Must(world.System.Catalogue.PortsAt(build.Target)).Any(p => p.From == port.Joins && p.Joins == port.From))
                return true;
        return false;
    }

    [Fact]
    public void Four_runs_from_a_header_to_four_pump_ghosts_in_one_batch_leave_no_open_end_at_a_pump()
    {
        using var world = World();
        Assert.IsType<Outcome.Applied>(Submit(world, new Command.PlanRun(new RunRequest(Pipe, At(-2), ValueList<Cell>.Empty, At(14), Option<PlanId>.None, Made))));
        var pumps = new[] { 0, 4, 8, 12 }.Select(x => PlacePump(world, At(x, 0, 3)).Id).ToArray();
        var ports = pumps.Select(p => InputOf(world, p)).ToArray();

        var batch = new Command.Batch(ValueList.From(pumps.Select((pump, i) => (Command)RunTo(At(new[] { 0, 4, 8, 12 }[i]), ports[i], pump))));
        Assert.IsType<Outcome.Applied>(Submit(world, batch));

        Assert.All(ports, port => Assert.True(Joined(world, port), $"open end at {port.Joins}"));
    }

    [Fact]
    public void The_preview_of_a_run_to_a_port_lays_what_its_placement_lays_and_names_the_joining_piece()
    {
        using var world = World();
        var pump = PlacePump(world, At(0, 0, 3)).Id;
        var run = RunTo(At(3, 0, 2), InputOf(world, pump), pump);

        var preview = Ready(world, () => world.System.Preview.Of(run, world.Viewer))
            .Match(static p => p, static why => throw new Xunit.Sdk.XunitException(Describe(why[0].Why)));
        var report = preview.Run.Reduce(() => throw new Xunit.Sdk.XunitException("no run"));
        var laid = Assert.IsType<Outcome.Applied>(Submit(world, run)).Changes.Added;

        var join = Assert.Single(report.PortEnds);
        Assert.Equal(new PortEnd.OnGhost(pump, Input), join.End);
        Assert.True(join.JoinedBy.TryGet(out var by));
        Assert.Contains(laid, g => g.Intent is Intent.Build build && build.Target == by);
        Assert.Equal(report.Pieces.ToHashSet(), laid.Select(static g => ((Intent.Build)g.Intent).Target).ToHashSet());
    }

    [Fact]
    public void A_pump_moved_after_its_port_was_read_refuses_the_run_and_places_nothing()
    {
        using var world = World();
        var pump = PlacePump(world, At(0, 0, 3)).Id;
        var read = InputOf(world, pump);
        Assert.IsType<Outcome.Applied>(Submit(world, new Command.Move(ValueMap.Of(pump, new Placement(Pump.Key, At(0, 0, 6), default)), Option<Revision>.None)));
        var before = world.System.Board.Revision;

        var refused = Assert.IsType<Outcome.Refused>(Submit(world, RunTo(At(3, 0, 2), read, pump)));

        Assert.Equal(ReasonCodes.ChangedSince, refused.Why[0].Why.Code);
        Assert.Equal(before, world.System.Board.Revision);
    }

    [Fact]
    public void Moving_the_pump_alone_leaves_the_run_where_it_is_and_turning_both_keeps_them_joined()
    {
        using var world = World();
        var pump = PlacePump(world, At(0, 0, 3)).Id;
        var laid = Assert.IsType<Outcome.Applied>(Submit(world, RunTo(At(3, 0, 2), InputOf(world, pump), pump))).Changes.Added;
        var run = laid.Select(static g => g.Id).ToArray();
        var placed = Placements(world, run);

        Assert.IsType<Outcome.Applied>(Submit(world, new Command.Move(ValueMap.Of(pump, new Placement(Pump.Key, At(0, 0, 6), default)), Option<Revision>.None)));
        Assert.Equal(placed, Placements(world, run));
        Assert.False(Joined(world, InputOf(world, pump)));

        Assert.IsType<Outcome.Applied>(Submit(world, new Command.Move(ValueMap.Of(pump, new Placement(Pump.Key, At(0, 0, 3), default)), Option<Revision>.None)));
        var quarter = Enumerable.Range(0, Turn.Count).Select(static i => Must(Turn.Create(i)))
            .First(static t => t.Axes.X == SignedAxis.ZMinus && t.Axes.Y == SignedAxis.YPlus);
        var both = new Selector.Ids(ValueList.From(run.Append(pump)));
        Assert.IsType<Outcome.Applied>(Submit(world, new Command.Transform(both, new Transformation.Rotate(At(0, 0, 3), quarter), new TransformMode.Move())));

        Assert.True(Joined(world, InputOf(world, pump)));
    }

    [Fact]
    public void Removing_the_pump_leaves_the_run()
    {
        using var world = World();
        var pump = PlacePump(world, At(0, 0, 3)).Id;
        var run = Assert.IsType<Outcome.Applied>(Submit(world, RunTo(At(3, 0, 2), InputOf(world, pump), pump))).Changes.Added.Select(static g => g.Id).ToArray();

        Assert.IsType<Outcome.Applied>(Submit(world, new Command.Remove(new Selector.Ids(ValueList.Of(pump)))));

        Assert.All(run, id => Assert.True(world.System.Board.Find(id).IsSome));
    }

    [Fact]
    public void A_started_pump_ghost_is_an_end_like_a_planned_one()
    {
        using var world = World();
        var pump = PlacePump(world, At(0, 0, 3)).Id;
        Must(world.Build(Pump.Key, At(0, 0, 3), default));
        Must(world.Settle());
        Assert.IsType<GhostState.Started>(world.System.Board.Find(pump).Reduce(() => throw new Xunit.Sdk.XunitException("gone")).State);

        Assert.IsType<Outcome.Applied>(Submit(world, RunTo(At(3, 0, 2), InputOf(world, pump), pump)));

        Assert.True(Joined(world, InputOf(world, pump)));
    }

    private static List<Placement> Placements(TestWorld world, IEnumerable<GhostId> ids) =>
        ids.Select(id => ((Intent.Build)world.System.Board.Find(id).Reduce(() => throw new Xunit.Sdk.XunitException("gone")).Intent).Target).ToList();
}
