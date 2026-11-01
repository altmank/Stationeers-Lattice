using System.Linq;
using Lattice.Api;
using Lattice.Core.Executors;
using Lattice.Core.Runs;
using Lattice.Core.Selectors;
using Lattice.TestKit;
using Lattice.Tests.Board;
using Lattice.Tests.Preview;
using static Lattice.Tests.Runs.RunBench;

namespace Lattice.Tests.Executors;

/// <summary>
/// A batched ghost the board replaces is followed into the batch by the ghosts that replace it, in the same change
/// set: a split long straight by its singles, a planned line a run merges into by the pieces the run lays over it. A
/// split replaces the long ghost (<see cref="RemovalCause.Replaced"/>, by its first single), and one revert brings it
/// back. The hand catalogue's cable 10-long.
/// </summary>
public sealed class BatchReplacementTests
{
    private static readonly Maker Splitter = TestKitIds.Must(Maker.Create(TestKitIds.Mod, "splitter"));
    private static int _ids;

    private sealed class Bench
    {
        public Bench()
        {
            Preview = new PreviewBench();
            Assert.True(Preview.Expanders.Register(new SplitExpander(Preview.Runs.Catalogue, Preview.Runs.Grid).Descriptor).IsOk);
            Preview.Board.InstallSelectors(new GhostSelectors(Preview.Shapes, Preview.Runs.Grid, Preview.Mirror, Preview.Runs.World));
            Executors = new WorldExecutors(Preview.Board, new ManualGameClock(), WorldSeat.Host);
            Dock = TestKitIds.Must(Executors.Register(ExecutorsBench.Info("dock")));
        }

        public PreviewBench Preview { get; }

        public WorldExecutors Executors { get; }

        public IExecutorHandle Dock { get; }

        public ChangeSet Submit(Command command) => TestBoard.Applied(Assert.IsType<Submission.Answered>(
            Preview.Commands.Submit(new CommandId(new System.Guid(++_ids, 5, 5, new byte[8])), command, TestBoard.Alice)).Outcome);

        // A ghost 10-long cable along z from At(0, 0, 9) down to At(0, 0, 0), batched.
        public Ghost BatchedLong()
        {
            var draft = TestBoard.Draft(new Intent.Build(
                new Placement(Preview.Runs.Catalogue.Named("StructureCableStraight10").Key, At(0, 0, 9), default), Option<Finish>.None));
            var ghost = Preview.Runs.Boards.Placed(draft).Added[0];
            TestKitIds.Must(Dock.SetBatch(ValueSet.Of(ghost.Id)));
            return ghost;
        }

        public int BatchLeft => Executors.Find(Builders.Id("dock")).Reduce(() => throw new Xunit.Sdk.XunitException("no dock")).BatchLeft;
    }

    [Fact]
    public void A_split_long_ghost_leaves_as_replaced_and_its_singles_join_the_batch_in_the_same_change_set()
    {
        var bench = new Bench();
        var longGhost = bench.BatchedLong();
        var leftInside = -1;
        bench.Preview.Board.Committed += _ => leftInside = bench.BatchLeft;

        var split = bench.Submit(new Command.PlanSplit(new SplitTargets.Ghosts(TestBoard.Ids(longGhost.Id)), Option<PlanId>.None, Splitter));

        var removal = Assert.Single(split.Removed);
        Assert.Equal(longGhost.Id, removal.Ghost.Id);
        Assert.Equal(split.Added[0].Id, Assert.IsType<RemovalCause.Replaced>(removal.Cause).By);
        Assert.Equal(ValueSet.From(split.Added.Select(g => g.Id)), bench.Dock.Batch);
        Assert.Equal(10, leftInside);
    }

    [Fact]
    public void Undoing_a_split_restores_the_long_ghost()
    {
        var bench = new Bench();
        var longGhost = bench.BatchedLong();
        var split = bench.Submit(new Command.PlanSplit(new SplitTargets.Ghosts(TestBoard.Ids(longGhost.Id)), Option<PlanId>.None, Splitter));

        var undone = bench.Submit(new Command.Revert(split.Id));

        var back = Assert.Single(bench.Preview.Board.Index.Ghosts.Values).Ghost;
        Assert.Equal(longGhost.Id, back.Id);
        Assert.Equal(longGhost.Intent, back.Intent);
        Assert.Equal(10, undone.Removed.Count);
    }

    [Fact]
    public void A_run_merging_into_a_planned_line_puts_what_it_lays_over_the_line_into_the_batch()
    {
        var bench = new Bench();
        var longGhost = bench.BatchedLong();
        var lineCells = Enumerable.Range(0, 10).Select(z => At(0, 0, z)).ToHashSet();

        var run = bench.Submit(new Command.PlanRun(bench.Preview.Runs.Run(bench.Preview.Runs.Cable, At(-2, 0, 5), At(2, 0, 5))));

        var removal = Assert.Single(run.Removed, r => r.Ghost.Id == longGhost.Id);
        Assert.IsType<RemovalCause.Replaced>(removal.Cause);
        var batch = bench.Dock.Batch;
        Assert.DoesNotContain(longGhost.Id, batch);
        Assert.Equal(10, batch.Count);
        Assert.All(batch, id =>
        {
            var ghost = bench.Preview.Board.Index.Ghosts[id].Ghost;
            Assert.Contains(((Intent.Build)ghost.Intent).Target.At, lineCells);
        });
    }
}
