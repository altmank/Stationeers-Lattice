using System;
using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Slices;
using Lattice.Core.Validation;
using Lattice.Tests.Board;
using Lattice.Tests.Slices;
using static Lattice.Tests.Validation.ValidationBench;

namespace Lattice.Tests.Validation;

/// <summary>
/// Trials' asks are answered ahead of the rest of the checking work, for at most a share of a frame: a preview never
/// waits behind a large plan's sliced work, each of its asks is answered once whichever job takes it, and a stream of
/// previews leaves the board's own asks their time.
/// </summary>
public sealed class TrialAskWorkTests
{
    /// <summary>What answering one ask costs on the bench's clock.</summary>
    private static readonly TimeSpan AskCost = TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond / 10);

    private static ValidationTrial Try(ValidationBench bench, IEnumerable<Cell> cells)
    {
        var drafts = cells.Select(at => TestBoard.Draft(new Intent.Build(new Placement(Pieces.Named("StructureFrameIron").Key, at, default), Option<Finish>.None))).ToArray();
        var tried = bench.Board.Try(new Command.Place(ValueList.From(drafts)), TestBoard.Alice)
            .Match(ok => ok, why => throw new Xunit.Sdk.XunitException($"Refused: {why}"));
        return bench.Service.Trial(tried.Changes);
    }

    private static Cell[] Apart(int count, int z) => Enumerable.Range(0, count).Select(i => Large(4 * i, 0, z)).ToArray();

    private static int Checked(ValidationBench bench, IReadOnlyCollection<Cell> cells) =>
        bench.Placement.Calls.Count(p => cells.Contains(p.At));

    [Fact]
    public void A_trials_asks_are_answered_while_a_long_checking_job_queued_before_them_fills_every_frame()
    {
        var bench = new ValidationBench().Start();
        bench.Pump();
        var hog = new CountingJob(bench.Clock, int.MaxValue, TimeSpan.FromMilliseconds(1));
        bench.Scheduler.Schedule(WorkKind.Checking, hog, Priority.Normal);
        bench.Frame();

        var trial = Try(bench, Apart(3, 40));
        for (var i = 0; i < 8 && !trial.IsReady; i++)
            bench.Frame();

        Assert.True(trial.IsReady, "the trial waited behind the long job");
        Assert.True(hog.Done > 0);
    }

    [Fact]
    public void Each_trial_ask_is_answered_once_when_both_checking_jobs_take_trial_asks()
    {
        var cells = Apart(6, 40);
        var shared = new ValidationBench { TrialAskSlice = TimeSpan.Zero }.Start();
        var alone = new ValidationBench().Start();
        shared.Pump();
        alone.Pump();

        var trial = Try(shared, cells);
        var most = 0;
        for (var i = 0; i < 16 && !trial.IsReady; i++)
        {
            var before = Checked(shared, cells);
            shared.Frame();
            most = Math.Max(most, Checked(shared, cells) - before);
        }

        var reference = Try(alone, cells);
        for (var i = 0; i < 16 && !reference.IsReady; i++)
            alone.Frame();

        Assert.True(trial.IsReady);
        Assert.True(most > 1, "only the trial job answered: the board job never took a trial ask");
        Assert.All(cells, at => Assert.Equal(1, shared.Placement.Calls.Count(p => p.At == at)));
        Assert.Equal(reference.Answer, trial.Answer);
    }

    [Fact]
    public void A_new_trial_every_frame_still_leaves_the_boards_asks_their_time()
    {
        var bench = new ValidationBench().Start();
        bench.Placement.Rule(_ =>
        {
            bench.Clock.Advance(AskCost);
            return Option<Reason>.None;
        });
        var board = Apart(60, 0);
        bench.Ghosts(board.Select(at => ("StructureFrameIron", at)).ToArray());

        ValidationTrial? previous = null;
        var frames = 0;
        for (; frames < 64 && Checked(bench, board) < board.Length; frames++)
        {
            if (previous is not null)
                bench.Service.Drop(previous);
            previous = Try(bench, Apart(30, 40 + 2 * frames));
            bench.Frame();
        }

        Assert.Equal(board.Length, Checked(bench, board));
        Assert.True(frames < 32, $"the board's asks took {frames} frames under a stream of trials");
    }
}
