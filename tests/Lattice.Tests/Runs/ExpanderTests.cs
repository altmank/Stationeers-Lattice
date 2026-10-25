using System;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Commands;
using Lattice.Core.Ports;
using Lattice.Core.Runs;
using Lattice.TestKit;
using Lattice.Tests.Board;
using Xunit;
using static Lattice.Tests.Runs.RunBench;

namespace Lattice.Tests.Runs;

public sealed class ExpanderTests
{
    private static Result<Command, Reason> Expand(RunBench bench, RunRequest request) =>
        new RunExpander(bench.Planner).Descriptor.Expand(new ExpansionInput<Command.PlanRun>(
            new Command.PlanRun(request), new CommandId(Guid.NewGuid()), TestBoard.Alice, bench.Boards.View, bench.Query));

    [Fact]
    public void The_run_command_expands_to_ghosts_the_board_places()
    {
        var bench = new RunBench();

        var command = TestKitIds.Must(Expand(bench, bench.Run(bench.Cable, Origin, At(4, 0, 0))));
        TestBoard.Applied(bench.Boards.Apply(command));

        for (var x = 0; x <= 4; x++)
            Assert.Single(bench.Boards.View.At(At(x, 0, 0)));
    }

    [Fact]
    public void A_run_through_a_planned_cable_replaces_its_ghost_with_the_merged_shape()
    {
        var bench = new RunBench();
        var crossing = At(2, 0, 0);
        bench.Ghost("StructureCableStraight", crossing);

        TestBoard.Applied(bench.Boards.Apply(TestKitIds.Must(Expand(bench, bench.Run(bench.Cable, Origin, At(4, 0, 0))))));

        var ghost = Assert.Single(bench.Boards.View.At(crossing));
        var build = Assert.IsType<Intent.Build>(ghost.Intent);
        Assert.Equal("StructureCableJunction4", bench.NameOf(build.Target.Piece));
    }

    [Fact]
    public void A_run_through_a_planned_swap_keeps_it_a_swap_of_the_same_structure()
    {
        var bench = new RunBench();
        var crossing = At(2, 0, 0);
        var old = bench.Real("StructureCableStraight", crossing);
        var corner = new Placement(bench.Catalogue.Named("StructureCableCorner").Key, crossing, default);
        bench.Boards.Placed(TestBoard.Draft(new Intent.Swap(old, corner, Option<Finish>.None)));

        TestBoard.Applied(bench.Boards.Apply(TestKitIds.Must(Expand(bench, bench.Run(bench.Cable, Origin, At(4, 0, 0))))));

        var swap = Assert.IsType<Intent.Swap>(Assert.Single(bench.Boards.View.On(old)).Intent);
        Assert.Equal(Ways("x+,x-,z+"), bench.WaysOf(swap.New, crossing));
    }

    [Fact]
    public void A_run_with_nothing_to_change_is_applied_as_an_empty_place()
    {
        var bench = new RunBench();
        bench.Real("StructureCableStraight3", Origin);

        var command = TestKitIds.Must(Expand(bench, bench.Run(bench.Cable, Step(Origin, Direction.ZMinus, 2), Origin)));

        Assert.Empty(Assert.IsType<Command.Place>(command).Ghosts);
        Assert.IsType<Outcome.Applied>(bench.Boards.Apply(command));
    }

    [Fact]
    public void A_run_with_a_failing_cell_still_places_its_ghosts()
    {
        var bench = new RunBench();
        bench.Check.Refuse(p => bench.CellsOf(p).Contains(At(1, 0, 0)), ReasonCodes.GameRefused, "Blocked");

        var command = Assert.IsType<Command.Place>(TestKitIds.Must(Expand(bench, bench.Run(bench.Cable, Origin, At(4, 0, 0)))));

        Assert.Equal(5, command.Ghosts.Sum(g => bench.CellsOf(((Intent.Build)g.Intent).Target).Count));
    }

    [Fact]
    public void A_run_that_cannot_be_planned_is_refused_with_the_reason()
    {
        var bench = new RunBench();

        var refused = Expand(bench, bench.Run(bench.Cable, Origin, At(2, 0, 2)));

        Assert.True(refused.TryGetError(out var why));
        Assert.Equal("not-straight", why.Facts["problem"]);
    }

    [Fact]
    public void The_expander_registers_for_the_run_command()
    {
        var bench = new RunBench();
        var expanders = new CommandExpanders();

        Assert.True(expanders.Register(new RunExpander(bench.Planner).Descriptor).IsOk);
        Assert.True(expanders.For(new Command.PlanRun(bench.Run(bench.Cable, Origin, Origin))).IsSome);
        Assert.Equal(RunExpander.Module, new RunExpander(bench.Planner).Descriptor.Module);
    }
}
