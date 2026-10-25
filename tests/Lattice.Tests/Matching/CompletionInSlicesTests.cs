using System;
using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Matching;
using Lattice.Core.Slices;
using Lattice.Tests.Board;

namespace Lattice.Tests.Matching;

/// <summary>
/// A plan landing on standing structures (never stall a frame): the Changed handler only
/// notes the plan's ghosts, and the checking slices judge them a bounded number a step, in id order, until every one
/// the world already holds is done.
/// </summary>
public sealed class CompletionInSlicesTests
{
    private const int Count = 100;

    [Fact]
    public void A_plan_over_standing_structures_is_judged_a_few_ghosts_a_step_after_it_lands()
    {
        var w = new TestWorld();
        for (var i = 0; i < Count; i++)
            w.World.Load(w.Structure("StructureFrame", At(i)));
        var scheduler = new FrameScheduler(new OneStepClock(), FrameBudgets.Default);
        w.Mirror.Start(w.World, w.Reads);
        var watcher = new CompletionWatcher(w.T.Board, w.Mirror, w.TurnFree, w.T.Time, scheduler);
        watcher.Load();

        var drafts = Enumerable.Range(0, Count)
            .Select(i => TestBoard.Draft(new Intent.Build(w.Placement("StructureFrame", At(i)), Option<Finish>.None)))
            .ToArray();
        var ghosts = w.T.Placed(drafts).Added.Select(g => g.Id).OrderBy(id => id.Value).ToList();
        w.T.Events.Flush();

        Assert.Equal(Count, watcher.Waiting);
        Assert.All(ghosts, id => Assert.True(w.Find(id).IsSome));

        scheduler.RunFrame();
        Assert.Equal(Count - CompletionWatcher.JudgedPerStep, watcher.Waiting);
        Assert.All(ghosts.Take(CompletionWatcher.JudgedPerStep), id => Assert.True(w.Find(id).IsNone));
        Assert.All(ghosts.Skip(CompletionWatcher.JudgedPerStep), id => Assert.True(w.Find(id).IsSome));

        for (var frame = 0; frame < Count && watcher.Waiting > 0; frame++)
            scheduler.RunFrame();
        Assert.All(ghosts, id => Assert.True(w.Find(id).IsNone));
    }

    [Fact]
    public void Structures_appearing_under_ghosts_are_judged_a_few_a_step_in_the_checking_slices_with_no_world_read()
    {
        var (w, scheduler, watcher, ghosts) = PlannedOverEmptyWorld();

        RegisterUnderEveryGhost(w);
        var reads = w.Reads.Reads;

        Assert.Equal(Count, watcher.Waiting);
        Assert.All(ghosts, id => Assert.True(w.Find(id).IsSome));

        scheduler.RunFrame();
        Assert.Equal(Count - CompletionWatcher.JudgedPerStep, watcher.Waiting);
        Assert.All(ghosts.Take(CompletionWatcher.JudgedPerStep), id => Assert.True(w.Find(id).IsNone));
        Assert.All(ghosts.Skip(CompletionWatcher.JudgedPerStep), id => Assert.True(w.Find(id).IsSome));

        for (var frame = 0; frame < Count && watcher.Waiting > 0; frame++)
            scheduler.RunFrame();
        Assert.All(ghosts, id => Assert.True(w.Find(id).IsNone));
        Assert.Equal(reads, w.Reads.Reads);
    }

    [Fact]
    public void A_ghost_touched_by_several_world_changes_before_its_slice_is_judged_once_against_the_world_as_it_then_stands()
    {
        var (w, scheduler, watcher, ghosts) = PlannedOverEmptyWorld();
        var info = w.Structure("StructureFrame", At(0), state: TestWorld.Step(0));

        w.World.Register(info);
        w.World.ChangeState(info.Ref.Id, new BuildState.Built());

        Assert.Equal(1, watcher.Waiting);
        scheduler.RunFrame();
        Assert.True(w.Find(ghosts[0]).IsNone);
    }

    [Fact]
    public void Catching_up_judges_every_waiting_ghost_at_once()
    {
        var (w, _, watcher, ghosts) = PlannedOverEmptyWorld();
        RegisterUnderEveryGhost(w);

        watcher.CatchUp();

        Assert.Equal(0, watcher.Waiting);
        Assert.All(ghosts, id => Assert.True(w.Find(id).IsNone));
    }

    [Fact]
    public void A_ghost_whose_own_structure_and_one_sharing_its_spot_both_arrive_before_its_slice_is_done()
    {
        var w = new TestWorld();
        var scheduler = new FrameScheduler(new OneStepClock(), FrameBudgets.Default);
        w.Mirror.Start(w.World, w.Reads);
        var watcher = new CompletionWatcher(w.T.Board, w.Mirror, w.TurnFree, w.T.Time, scheduler);
        watcher.Load();
        var ghost = w.Build("StructureCableStraight", TestWorld.Small);
        w.T.Events.Flush();
        scheduler.RunFrame();

        w.World.Register(w.Structure("StructureCableStraightH", TestWorld.Small));
        w.World.Register(w.Structure("StructureCableStraight", TestWorld.Small));
        scheduler.RunFrame();

        Assert.True(w.Find(ghost.Id).IsNone);
    }

    // Count ghosts planned over empty cells, judged planned, nothing waiting.
    private static (TestWorld W, FrameScheduler Scheduler, CompletionWatcher Watcher, List<GhostId> Ghosts) PlannedOverEmptyWorld()
    {
        var w = new TestWorld();
        var scheduler = new FrameScheduler(new OneStepClock(), FrameBudgets.Default);
        w.Mirror.Start(w.World, w.Reads);
        var watcher = new CompletionWatcher(w.T.Board, w.Mirror, w.TurnFree, w.T.Time, scheduler);
        watcher.Load();
        var drafts = Enumerable.Range(0, Count)
            .Select(i => TestBoard.Draft(new Intent.Build(w.Placement("StructureFrame", At(i)), Option<Finish>.None)))
            .ToArray();
        var ghosts = w.T.Placed(drafts).Added.Select(g => g.Id).OrderBy(id => id.Value).ToList();
        w.T.Events.Flush();
        for (var frame = 0; frame < Count && watcher.Waiting > 0; frame++)
            scheduler.RunFrame();
        Assert.Equal(0, watcher.Waiting);
        return (w, scheduler, watcher, ghosts);
    }

    private static void RegisterUnderEveryGhost(TestWorld w)
    {
        for (var i = 0; i < Count; i++)
            w.World.Register(w.Structure("StructureFrame", At(i)));
    }

    private static Cell At(int i) => new(10 + (20 * i), 10, 10);

    /// <summary>A clock that moves a whole second each read, so every slice runs exactly one step.</summary>
    private sealed class OneStepClock : IClock
    {
        private TimeSpan _now;

        public TimeSpan Now => _now += TimeSpan.FromSeconds(1);
    }
}
