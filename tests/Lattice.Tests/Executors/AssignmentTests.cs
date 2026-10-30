using System.Linq;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Tests.Board;
using Lattice.TestKit;

namespace Lattice.Tests.Executors;

/// <summary>
/// An assignment is the plan's; the answer is the builder's. Assigning is a plan edit under the access policy; a
/// plan holds a set of builders (at most 64), each added or dropped on its own. It does not gate claims, reads Absent
/// while its builder is not registered, and survives the builder going. The answer is live, from the handle only.
/// </summary>
public sealed class AssignmentTests
{
    private static readonly Reason Off = Reason.Of(TestKitIds.Must(ReasonCode.Create(TestKitIds.Mod, "dock-off")), Builders.Dock);

    [Fact]
    public void A_plan_holds_many_builders_each_asked_until_it_answers()
    {
        var b = new ExecutorsBench();
        var plan = Builders.NewPlan(b);
        var a = b.Register("dock-a");
        b.Register("dock-c");

        Builders.Assign(b, plan, Builders.Id("dock-a"));
        Builders.Assign(b, plan, Builders.Id("dock-c"));
        TestKitIds.Must(a.Answer(plan, new AssignmentAnswer.Refused(Off)));

        Assert.Equal(new Assignment[] { new Assignment.Refused(Builders.Id("dock-a"), Off), new Assignment.Asked(Builders.Id("dock-c")) }, b.Executors.AssignedTo(plan));
        Assert.Equal(new[] { plan }, b.Executors.PlansOf(Builders.Id("dock-c")));

        TestKitIds.Must(a.Answer(plan, new AssignmentAnswer.Accepted()));
        Assert.Equal(new Assignment.Accepted(Builders.Id("dock-a")), b.Executors.AssignedTo(plan)[0]);
    }

    [Fact]
    public void Dropping_one_builder_leaves_the_others_and_their_answers()
    {
        var b = new ExecutorsBench();
        var plan = Builders.NewPlan(b);
        var a = b.Register("dock-a");
        var c = b.Register("dock-c");
        Builders.Assign(b, plan, Builders.Id("dock-a"));
        Builders.Assign(b, plan, Builders.Id("dock-c"));
        TestKitIds.Must(a.Answer(plan, new AssignmentAnswer.Accepted()));
        TestKitIds.Must(c.Answer(plan, new AssignmentAnswer.Accepted()));

        TestBoard.Applied(Builders.Edit(b, plan, new PlanEdit.Unassign(Builders.Id("dock-a"))));
        b.Flush();

        Assert.Equal(new Assignment[] { new Assignment.Accepted(Builders.Id("dock-c")) }, b.Executors.AssignedTo(plan));
        Assert.Empty(b.Executors.PlansOf(Builders.Id("dock-a")));
        Assert.True(a.Answer(plan, new AssignmentAnswer.Accepted()).TryGetError(out var why));
        Assert.Equal(ReasonCodes.NotFound, why.Code);
    }

    [Fact]
    public void Assigning_a_builder_not_registered_on_the_host_is_refused_not_found()
    {
        var b = new ExecutorsBench();
        var plan = Builders.NewPlan(b);

        var refusal = TestBoard.Refused(Builders.Edit(b, plan, new PlanEdit.Assign(Builders.Id("nobody"))));

        Assert.Equal(ReasonCodes.NotFound, refusal.Why.Code);
        Assert.Equal("executor", refusal.Why.Facts["problem"]);
    }

    [Fact]
    public void Assigning_a_builder_already_in_the_set_changes_nothing()
    {
        var b = new ExecutorsBench();
        var plan = Builders.NewPlan(b);
        b.Register("dock");
        Builders.Assign(b, plan, Builders.Id("dock"));

        var again = TestBoard.Applied(Builders.Edit(b, plan, new PlanEdit.Assign(Builders.Id("dock"))));

        Assert.Empty(again.PlansChanged);
        Assert.Single(b.T.Board.Index.Plans[plan].Plan.BuildBy);
    }

    [Fact]
    public void A_plan_takes_64_builders_and_refuses_the_next_too_large()
    {
        var b = new ExecutorsBench();
        var plan = Builders.NewPlan(b);
        for (var i = 0; i < Edits.MostBuilders; i++)
        {
            b.Register("dock-" + i);
            Builders.Assign(b, plan, Builders.Id("dock-" + i));
        }

        b.Register("one-more");
        var refusal = TestBoard.Refused(Builders.Edit(b, plan, new PlanEdit.Assign(Builders.Id("one-more"))));

        Assert.Equal(ReasonCodes.TooLarge, refusal.Why.Code);
        Assert.Equal(Edits.MostBuilders, b.T.Board.Index.Plans[plan].Plan.BuildBy.Count);
    }

    [Fact]
    public void A_builder_that_goes_stays_assigned_and_reads_absent_until_it_registers_again()
    {
        var b = new ExecutorsBench();
        var plan = Builders.NewPlan(b);
        var dock = b.Register("dock");
        Builders.Assign(b, plan, Builders.Id("dock"));
        TestKitIds.Must(dock.Answer(plan, new AssignmentAnswer.Accepted()));

        dock.Dispose();
        Assert.Equal(new Assignment[] { new Assignment.Absent(Builders.Id("dock")) }, b.Executors.AssignedTo(plan));
        Assert.Contains(Builders.Id("dock"), b.T.Board.Index.Plans[plan].Plan.BuildBy);

        b.Register("dock");
        Assert.Equal(new Assignment[] { new Assignment.Asked(Builders.Id("dock")) }, b.Executors.AssignedTo(plan));
    }

    [Fact]
    public void An_assignment_does_not_gate_claims_a_helper_may_build_the_plan()
    {
        var b = new ExecutorsBench();
        var plan = Builders.NewPlan(b);
        b.Register("dock");
        var helper = b.Register("helper");
        Builders.Assign(b, plan, Builders.Id("dock"));
        var ghost = Builders.GhostIn(b, plan, 0);

        Assert.True(helper.Claim(ghost, ExecutorsBench.Seconds(30)).IsOk);
        Assert.Equal(new BuilderProgress(0, 1, 0), b.Executors.ProgressOf(plan, Builders.Id("helper")));
    }

    [Fact]
    public void An_assign_edit_goes_through_the_access_policy()
    {
        var b = new ExecutorsBench();
        var plan = Builders.NewPlan(b);
        b.Register("dock");
        using var policy = TestKitIds.Must(b.T.Policies.Install(new AccessPolicy(
            static (_, _) => true,
            static (_, _) => true,
            static request => request.Command is Command.EditPlan { Edit: PlanEdit.Assign }
                ? Result<Unit, Reason>.Error(Reason.Of(ReasonCodes.NotAllowed, Builders.Dock))
                : Result<Unit, Reason>.Ok(Unit.Value))));

        var refusal = TestBoard.Refused(Builders.Edit(b, plan, new PlanEdit.Assign(Builders.Id("dock"))));

        Assert.Equal(ReasonCodes.NotAllowed, refusal.Why.Code);
    }
}

/// <summary>A plan's colour is plan data Lattice keeps as written and never acts on.</summary>
public sealed class PlanColourTests
{
    private static readonly Tint Teal = new(0, 128, 128, 255);

    [Fact]
    public void A_plan_takes_its_colour_from_its_draft_and_recolour_sets_or_clears_it()
    {
        var b = new ExecutorsBench();
        var plan = Builders.NewPlan(b, colour: Option.Some(Teal));
        Assert.Equal(Option.Some(Teal), b.T.Board.Index.Plans[plan].Plan.Colour);

        TestBoard.Applied(Builders.Edit(b, plan, new PlanEdit.Recolour(Option.Some(Teal with { A = 10 }))));
        Assert.Equal(Option.Some(Teal with { A = 10 }), b.T.Board.Index.Plans[plan].Plan.Colour);

        TestBoard.Applied(Builders.Edit(b, plan, new PlanEdit.Recolour(Option<Tint>.None)));
        Assert.True(b.T.Board.Index.Plans[plan].Plan.Colour.IsNone);
    }

    [Fact]
    public void A_plan_made_before_colours_reads_none()
    {
        var b = new ExecutorsBench();

        Assert.True(b.T.Board.Index.Plans[GhostBoard.DefaultPlan].Plan.Colour.IsNone);
        Assert.Empty(b.T.Board.Index.Plans[GhostBoard.DefaultPlan].Plan.BuildBy);
    }
}

/// <summary>
/// A builder's progress is counted from claims and completions, not reported. Built is the plan's completions whose
/// claim holder was the builder; working and waiting are its live claims by their last report; the plan's progress is
/// the sum over every executor, and what is left unclaimed is the plan's ghosts no claim holds.
/// </summary>
public sealed class BuilderProgressTests
{
    [Fact]
    public void Built_working_and_waiting_come_from_completions_and_claims()
    {
        var b = new ExecutorsBench();
        var plan = Builders.NewPlan(b);
        var dock = b.Register("dock");
        var helper = b.Register("helper");
        var ghosts = Enumerable.Range(0, 5).Select(i => Builders.GhostIn(b, plan, i)).ToArray();
        foreach (var ghost in ghosts.Take(3))
            dock.Claim(ghost, ExecutorsBench.Seconds(30));
        helper.Claim(ghosts[3], ExecutorsBench.Seconds(30));
        TestKitIds.Must(dock.Report(ghosts[1], new ExecutorStatus.Waiting(Reason.NoPower(Builders.Dock))));

        Builders.Complete(b, ghosts[0]);

        Assert.Equal(new BuilderProgress(1, 1, 1), b.Executors.ProgressOf(plan, Builders.Id("dock")));
        Assert.Equal(new BuilderProgress(0, 1, 0), b.Executors.ProgressOf(plan, Builders.Id("helper")));
        Assert.Equal(new BuilderProgress(1, 2, 1), b.Executors.ProgressOf(plan));
        Assert.Equal(1, b.Executors.UnclaimedIn(plan));
    }

    [Fact]
    public void A_player_finishing_a_claimed_ghost_counts_for_the_claim_holder_and_an_unclaimed_one_for_no_builder()
    {
        var b = new ExecutorsBench();
        var plan = Builders.NewPlan(b);
        var dock = b.Register("dock");
        var claimed = Builders.GhostIn(b, plan, 0);
        var free = Builders.GhostIn(b, plan, 1);
        dock.Claim(claimed, ExecutorsBench.Seconds(30));

        Builders.Complete(b, claimed);
        Builders.Complete(b, free);

        Assert.Equal(new BuilderProgress(1, 0, 0), b.Executors.ProgressOf(plan, Builders.Id("dock")));
        Assert.Equal(new BuilderProgress(1, 0, 0), b.Executors.ProgressOf(plan));
        Assert.Equal(2, b.T.Board.Index.Plans[plan].Done);
    }

    [Fact]
    public void A_builder_with_nothing_in_a_plan_reads_zeros()
    {
        var b = new ExecutorsBench();
        var plan = Builders.NewPlan(b);

        Assert.Equal(default, b.Executors.ProgressOf(plan, Builders.Id("nobody")));
        Assert.Equal(default, b.Executors.ProgressOf(new PlanId(999)));
        Assert.Equal(0, b.Executors.UnclaimedIn(new PlanId(999)));
    }
}
