using System;
using System.Linq;
using Lattice.Api;
using Lattice.Tests.Board;
using static Lattice.Tests.Validation.ValidationBench;

namespace Lattice.Tests.Validation;

/// <summary>
/// The game is not asked about a ghost that left the board (removed or done) before its ask's turn: no judgement reads
/// that answer. A ghost back on the board is judged again and asked again. Two ghosts three large cells apart, so the
/// game is asked about each.
/// </summary>
public sealed class GoneGhostAskTests
{
    private static readonly Cell Stays = Large(0, 0, 0);
    private static readonly Cell Leaves = Large(6, 0, 0);

    // The run asks the game about both ghosts; the step before the checking slices answer them.
    private static (ValidationBench Bench, GhostId Leaving) AskedAboutBoth()
    {
        var bench = new ValidationBench().Start();
        bench.Ghost("StructureFrameIron", Stays);
        var leaving = bench.Ghost("StructureFrameIron", Leaves);
        bench.Events.Flush();
        bench.Service.Frame();
        bench.Worker.RunAll();
        bench.Service.Frame();
        Assert.Empty(bench.Placement.Calls);
        return (bench, leaving);
    }

    // The board change goes to the worker, and the checking slices answer the run's asks while it runs; then the
    // worker's runs are taken until validation settles.
    private static void AnswerWhileTheWorkerRuns(ValidationBench bench)
    {
        bench.Frame(runWorker: false);
        Assert.Equal(1, AskedAt(bench, Stays));
        bench.Pump();
    }

    private static int AskedAt(ValidationBench bench, Cell at) => bench.Placement.Calls.Count(p => p.At == at);

    [Fact]
    public void The_game_is_not_asked_about_a_ghost_removed_before_its_turn()
    {
        var (bench, leaving) = AskedAboutBoth();

        bench.Apply(new Command.Remove(TestBoard.Ids(leaving)));
        AnswerWhileTheWorkerRuns(bench);

        Assert.Equal(1, AskedAt(bench, Stays));
        Assert.Equal(0, AskedAt(bench, Leaves));
    }

    [Fact]
    public void The_game_is_not_asked_about_a_ghost_done_before_its_turn()
    {
        var (bench, leaving) = AskedAboutBoth();

        Assert.True(bench.Board.Complete(leaving, new Completion(Option<StructureRef>.None, DateTimeOffset.UnixEpoch)).IsOk);
        AnswerWhileTheWorkerRuns(bench);

        Assert.Equal(1, AskedAt(bench, Stays));
        Assert.Equal(0, AskedAt(bench, Leaves));
    }

    [Fact]
    public void A_ghost_back_on_the_board_after_its_ask_was_passed_over_is_asked_again_and_judged()
    {
        var (bench, leaving) = AskedAboutBoth();
        var removal = TestBoard.Applied(bench.Apply(new Command.Remove(TestBoard.Ids(leaving))));
        AnswerWhileTheWorkerRuns(bench);

        TestBoard.Applied(bench.Apply(new Command.Revert(removal.Id)));
        bench.Pump();

        Assert.Equal(1, AskedAt(bench, Leaves));
        Assert.IsType<Finding.Passes>(Verdict(bench.Report(), leaving));
    }

    [Fact]
    public void A_ghost_placed_again_where_one_left_is_asked_again_and_judged()
    {
        var (bench, leaving) = AskedAboutBoth();
        bench.Apply(new Command.Remove(TestBoard.Ids(leaving)));
        AnswerWhileTheWorkerRuns(bench);

        var again = bench.Ghost("StructureFrameIron", Leaves);
        bench.Pump();

        Assert.Equal(1, AskedAt(bench, Leaves));
        Assert.IsType<Finding.Passes>(Verdict(bench.Report(), again));
    }
}
