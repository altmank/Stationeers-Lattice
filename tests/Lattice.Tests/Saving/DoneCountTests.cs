using System.Linq;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Saving;
using Lattice.TestKit;
using Lattice.Tests.Board;
using Lattice.Tests.FinishedPlans;
using Lattice.Tests.Plans;
using static Lattice.Tests.FinishedPlans.FinishedBench;

namespace Lattice.Tests.Saving;

/// <summary>A plan's count of done ghosts lives with the plan on the board and survives loads, catch-ups and reopens.</summary>
public sealed class DoneCountTests
{
    private static int DoneOf(FinishedBench b, PlanId plan) => b.Board.Index.Plans[plan].Done;

    /// <summary>A plan of two trusses built and one planned.</summary>
    private static PlanId TwoBuiltOneLeft(FinishedBench b)
    {
        var plan = b.NewPlan("Hall");
        b.Construct(b.Plan(plan, "ModTruss", Large(0)));
        b.Construct(b.Plan(plan, "ModTruss", Large(1)));
        b.Plan(plan, "ModTruss", Large(2));
        return plan;
    }

    [Fact]
    public void The_board_counts_each_plan_s_done_ghosts()
    {
        using var b = new FinishedBench();
        var plan = TwoBuiltOneLeft(b);
        var other = b.NewPlan("Store");

        Assert.Equal(2, DoneOf(b, plan));
        Assert.Equal(0, DoneOf(b, other));
    }

    [Fact]
    public void Done_counts_survive_a_load_of_a_save()
    {
        using var b = new FinishedBench();
        var plan = TwoBuiltOneLeft(b);

        b.Reload(Option.Some(b.Save()));

        Assert.Equal(2, DoneOf(b, plan));
        Assert.Single(b.Board.Index.Plans[plan].Ghosts);
    }

    [Fact]
    public void The_default_plan_s_done_count_survives_a_load()
    {
        using var b = new FinishedBench();
        b.Construct(b.Plan(GhostBoard.DefaultPlan, "ModTruss", Large(0)));

        b.Reload(Option.Some(b.Save()));

        Assert.Equal(1, DoneOf(b, GhostBoard.DefaultPlan));
    }

    [Fact]
    public void Done_counts_survive_a_load_with_no_save_record()
    {
        using var b = new FinishedBench();
        var plan = TwoBuiltOneLeft(b);

        b.Reload(Option<SaveKey>.None);

        Assert.Equal(2, DoneOf(b, plan));
    }

    [Fact]
    public void A_checkpoint_written_without_a_done_count_reads_its_built_count()
    {
        var state = PlanReplay.From(new PlanId(4), 10, Option<Plan>.None, false, Enumerable.Empty<Ghost>(), built: 3);

        Assert.Equal(3, state.Done);
    }

    [Fact]
    public void Catching_up_a_plan_takes_the_done_count_of_its_newest_line()
    {
        using var b = new FinishedBench();
        var plan = b.NewPlan("Hall");
        b.Construct(b.Plan(plan, "ModTruss", Large(0)));
        var save = b.Save();
        b.Construct(b.Plan(plan, "ModTruss", Large(1)));
        b.Reload(Option.Some(save));
        Assert.Equal(1, DoneOf(b, plan));

        TestBoard.Applied(b.Apply(new Command.CatchUp(plan)));

        Assert.Equal(2, DoneOf(b, plan));
    }

    [Fact]
    public void A_reopened_plan_keeps_its_done_count()
    {
        using var b = new FinishedBench();
        var plan = TwoBuiltOneLeft(b);
        b.Finish(plan);

        TestBoard.Applied(b.Apply(new Command.Reopen(plan)));

        Assert.Equal(2, DoneOf(b, plan));
    }

    [Fact]
    public void A_plan_built_to_the_end_before_a_save_loads_as_complete()
    {
        var seed = new BoardSeed(new Revision(5), new IdCounters(10, 10, 10), ValueList<Plan>.Empty, ValueList<Ghost>.Empty)
        {
            Done = ValueMap.Of(GhostBoard.DefaultPlan, 2),
        };
        var bench = new PlanBench(seed: seed);

        Assert.Equal(new Progress(2, 0, 0), bench.Facts().Progress);
        Assert.IsType<PlanState.Complete>(bench.State());
    }
}
