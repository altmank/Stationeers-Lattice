using System.Linq;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Saving;
using Lattice.Core.Sync;
using Lattice.Core.Written;
using Lattice.TestKit;
using Lattice.Tests.Board;
using Lattice.Tests.FinishedPlans;
using static Lattice.Tests.FinishedPlans.FinishedBench;

namespace Lattice.Tests.Saving;

/// <summary>
/// A builder's built count is saved with the completion (the done line's <c>by</c>), so it survives a load, a
/// catch-up, a reopen and a checkpoint, and reaches a joining client beside the plan's done count.
/// </summary>
public sealed class DoneByTests
{
    private static readonly ExecutorId Dock = TestKitIds.Must(ExecutorId.Create(TestKitIds.Mod, "dock"));

    private static int BuiltBy(FinishedBench b, PlanId plan) => b.Board.Index.Plans[plan].DoneBy.Of(Dock);

    /// <summary>A plan of two trusses the dock built and one a player built.</summary>
    private static PlanId TwoByTheDockOneByHand(FinishedBench b)
    {
        var plan = b.NewPlan("Hall");
        b.Board.ClaimHolder = _ => Option.Some(Dock);
        b.Construct(b.Plan(plan, "ModTruss", Large(0)));
        b.Construct(b.Plan(plan, "ModTruss", Large(1)));
        b.Board.ClaimHolder = null;
        b.Construct(b.Plan(plan, "ModTruss", Large(2)));
        return plan;
    }

    [Fact]
    public void Built_counts_per_builder_survive_a_load()
    {
        using var b = new FinishedBench();
        var plan = TwoByTheDockOneByHand(b);

        b.Reload(Option.Some(b.Save()));

        Assert.Equal(2, BuiltBy(b, plan));
        Assert.Equal(3, b.Board.Index.Plans[plan].Done);
    }

    [Fact]
    public void Catching_up_a_plan_takes_the_built_counts_of_its_newest_line()
    {
        using var b = new FinishedBench();
        var plan = b.NewPlan("Hall");
        b.Board.ClaimHolder = _ => Option.Some(Dock);
        b.Construct(b.Plan(plan, "ModTruss", Large(0)));
        var save = b.Save();
        b.Construct(b.Plan(plan, "ModTruss", Large(1)));
        b.Reload(Option.Some(save));
        Assert.Equal(1, BuiltBy(b, plan));

        TestBoard.Applied(b.Apply(new Command.CatchUp(plan)));

        Assert.Equal(2, BuiltBy(b, plan));
    }

    [Fact]
    public void A_reopened_plan_keeps_its_built_counts()
    {
        using var b = new FinishedBench();
        var plan = TwoByTheDockOneByHand(b);
        b.Finish(plan);

        TestBoard.Applied(b.Apply(new Command.Reopen(plan)));

        Assert.Equal(2, BuiltBy(b, plan));
    }

    [Fact]
    public void A_checkpoint_keeps_the_built_counts()
    {
        var state = PlanReplay.From(new PlanId(4), 10, Option<Plan>.None, false, Enumerable.Empty<Ghost>(), built: 3, done: 5,
            doneBy: ValueMap.Of(Dock, 4));
        using var b = new FinishedBench();
        b.Folder.WriteCheckpoint(state);

        var read = b.Folder.ReadCheckpoint(new PlanId(4), 10).Reduce(() => throw new Xunit.Sdk.XunitException("no checkpoint"));

        Assert.Equal(5, read.Done);
        Assert.Equal(ValueMap.Of(Dock, 4), read.DoneBy);
    }

    [Fact]
    public void A_done_line_carries_the_claim_holder_and_reads_it_back()
    {
        var removal = new ChangeOp.RemoveGhost(new GhostId(3), new PlanId(2),
            new RemovalCause.Done(new Completion(Option<StructureRef>.None, new System.DateTimeOffset(2026, 1, 1, 0, 0, 0, System.TimeSpan.Zero)) { By = Option.Some(Dock) }));
        var line = new ChangeLine(new LineNumber(1), Option<LineNumber>.None, new CommandId(System.Guid.NewGuid()), TestBoard.Alice,
            new System.DateTimeOffset(2026, 1, 1, 0, 0, 0, System.TimeSpan.Zero), new ChangeSetId(1), new Revision(1), ValueList.Of<ChangeOp>(removal));

        var read = ChangeLineForm.Read(ChangeLineForm.Write(line)).Match(static l => l, static why => throw new Xunit.Sdk.XunitException(why.ToString()));

        var cause = Assert.IsType<RemovalCause.Done>(Assert.IsType<ChangeOp.RemoveGhost>(Assert.Single(read.Ops)).Cause);
        Assert.Equal(Option.Some(Dock), cause.How.By);
    }

    [Fact]
    public void A_join_carries_each_plan_s_built_counts_beside_its_done_count()
    {
        var plan = new Plan(new PlanId(2), new Revision(1), TestKitIds.Must(PlanName.Create("Hall")), TestBoard.Alice, TestBoard.TestMaker,
            new Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty);
        var text = JoinParts.Write(new Revision(3), new[] { plan }, new System.Collections.Generic.Dictionary<PlanId, int> { [plan.Id] = 5 },
            new System.Collections.Generic.Dictionary<PlanId, ValueMap<ExecutorId, int>> { [plan.Id] = ValueMap.Of(Dock, 4) }, System.Array.Empty<Ghost>(), 0, 100);

        var part = TestKitIds.Must(JoinParts.Read(text));

        Assert.Equal(5, part.Done[plan.Id]);
        Assert.Equal(ValueMap.Of(Dock, 4), part.DoneBy[plan.Id]);
    }

    [Fact]
    public void A_plan_s_colour_and_builders_are_written_with_it_and_read_back()
    {
        var plan = new Plan(new PlanId(2), new Revision(1), TestKitIds.Must(PlanName.Create("Hall")), TestBoard.Alice, TestBoard.TestMaker,
            new Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty)
        {
            Colour = Option.Some(new Tint(10, 20, 30, 255)),
            BuildBy = ValueSet.From(new[] { Dock, TestKitIds.Must(ExecutorId.Create(TestKitIds.Mod, "drone")) }),
        };

        var read = Assert.IsType<PlanRecord.Known>(PlanForm.Read(PlanForm.Write(plan, ValueList<Field>.Empty, LineContext.None), LineContext.None));

        Assert.Equal(plan, read.Plan);
        Assert.Equal(plan.Colour, read.Plan.Colour);
        Assert.Equal(plan.BuildBy, read.Plan.BuildBy);
        Assert.Empty(read.Extra);
    }
}
