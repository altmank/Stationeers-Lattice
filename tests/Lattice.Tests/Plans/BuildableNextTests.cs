using System;
using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.TestKit;
using Lattice.Tests.Board;
using Lattice.Tests.Validation;
using Xunit;
using static Lattice.Tests.Validation.ValidationBench;

namespace Lattice.Tests.Plans;

/// <summary>
/// <see cref="IPlans.BuildableNext"/>: every visible plan's buildable-next ghosts, or those a selector picks, read from
/// the plan facts the module keeps.
/// </summary>
public sealed class BuildableNextTests
{
    private static readonly Selector Everything = new Selector.Everything();

    private static PlanId NewPlan(PlanBench bench, string name, Visibility? visibility = null) =>
        Assert.Single(TestBoard.Applied(bench.V.Apply(new Command.CreatePlan(new PlanDraft(
            TestKitIds.Must(PlanName.Create(name)), TestBoard.TestMaker, visibility ?? new Visibility.Everyone(),
            ValueMap<AttachmentKey, Attachment>.Empty)))).PlansOpened).Id;

    private static ValueList<GhostId> PlaceIn(PlanBench bench, PlanId plan, params Cell[] cells)
    {
        var drafts = cells.Select(at => TestBoard.Draft(
            new Intent.Build(new Placement(Pieces.Named("StructureFrameIron").Key, at, default), Option<Finish>.None),
            Option.Some(plan))).ToArray();
        return ValueList.From(TestBoard.Applied(bench.V.Apply(new Command.Place(ValueList.From(drafts)))).Added.Select(g => g.Id));
    }

    /// <summary>Frames until validation has settled and every plan's facts are ready, as the viewer reads them.</summary>
    private static void Settle(PlanBench bench)
    {
        for (var i = 0; i < 256; i++)
        {
            bench.V.Frame();
            bench.Module.Frame();
            bench.V.Events.Flush();
            if (bench.V.Settled && bench.V.Board.Index.Plans.Keys.All(p =>
                    TestKitIds.Must(bench.Module.Facts(p)) is Answer<PlanFacts>.Ready))
                return;
        }

        throw new InvalidOperationException("Plan facts did not settle.");
    }

    private static ValueList<GhostId> Ready(IPlans plans, Selector which) =>
        Assert.IsType<Answer<ValueList<GhostId>>.Ready>(TestKitIds.Must(plans.BuildableNext(which))).Value;

    private static IEnumerable<GhostId> FactsOf(PlanBench bench, PlanId plan) =>
        Assert.IsType<Answer<PlanFacts>.Ready>(TestKitIds.Must(bench.Plans.Facts(plan))).Value.BuildableNext;

    [Fact]
    public void Every_plan_s_buildable_ghosts_come_in_plan_id_order_each_in_its_own_build_order()
    {
        var bench = new PlanBench();
        var plans = bench.Plans;
        var second = NewPlan(bench, "Second");
        var third = NewPlan(bench, "Third");
        PlaceIn(bench, third, Large(9, 0, 0), Large(12, 0, 0));
        PlaceIn(bench, bench.Default, Large(0, 0, 0));
        PlaceIn(bench, second, Large(3, 0, 0), Large(6, 0, 0));
        Settle(bench);

        var expected = FactsOf(bench, bench.Default).Concat(FactsOf(bench, second)).Concat(FactsOf(bench, third));

        Assert.Equal(expected, Ready(plans, Everything));
        Assert.Equal(5, Ready(plans, Everything).Count);
    }

    [Fact]
    public void A_selector_keeps_only_the_ghosts_it_picks()
    {
        var bench = new PlanBench();
        var plans = bench.Plans;
        var second = NewPlan(bench, "Second");
        var mine = PlaceIn(bench, bench.Default, Large(0, 0, 0), Large(3, 0, 0));
        var theirs = PlaceIn(bench, second, Large(6, 0, 0), Large(9, 0, 0));
        Settle(bench);

        Assert.Equal(theirs, Ready(plans, new Selector.InPlan(second)));
        Assert.Equal(new[] { mine[1], theirs[0] }, Ready(plans, new Selector.Ids(ValueList.From(new[] { theirs[0], mine[1] }))));
        Assert.Empty(Ready(plans, new Selector.Ids(ValueList<GhostId>.Empty)));
    }

    [Fact]
    public void A_selector_the_board_refuses_is_refused_with_its_reason()
    {
        var bench = new PlanBench();
        bench.V.Ghost("StructureFrameIron", Large(0, 0, 0));

        Assert.True(bench.Plans.BuildableNext(new Selector.Box(Large(0, 0, 0), Large(1, 1, 1))).TryGetError(out var why));

        Assert.Equal(ReasonCodes.ModuleMissing, why.Code);
    }

    [Fact]
    public void An_empty_board_gives_an_empty_ready_answer_at_once()
    {
        var bench = new PlanBench();
        NewPlan(bench, "Second");

        Assert.Empty(Ready(bench.Plans, Everything));
    }

    [Fact]
    public void Pending_while_any_plan_it_needs_is_pending_then_ready_with_answers_ready()
    {
        var bench = new PlanBench();
        var plans = bench.Plans;
        var ready = new List<Revision>();
        bench.V.Events.AnswersReady += ready.Add;
        var second = NewPlan(bench, "Second");
        PlaceIn(bench, bench.Default, Large(0, 0, 0));
        Settle(bench);
        var theirs = PlaceIn(bench, second, Large(6, 0, 0));
        ready.Clear();

        Assert.IsType<Answer<ValueList<GhostId>>.Pending>(TestKitIds.Must(plans.BuildableNext(Everything)));
        Assert.IsType<Answer<ValueList<GhostId>>.Pending>(TestKitIds.Must(plans.BuildableNext(new Selector.Ids(theirs))));
        Settle(bench);

        Assert.Contains(bench.V.Board.Revision, ready);
        Assert.Equal(theirs, Ready(plans, new Selector.Ids(theirs)));
        Assert.Equal(2, Ready(plans, Everything).Count);
    }

    [Fact]
    public void A_plan_or_ghost_the_viewer_may_not_see_is_left_out()
    {
        var bench = new PlanBench();
        var plans = new PlansView(bench.V.Board, TestBoard.Bob);
        var hiddenPlan = NewPlan(bench, "Hidden");
        var shown = PlaceIn(bench, bench.Default, Large(0, 0, 0), Large(3, 0, 0));
        PlaceIn(bench, hiddenPlan, Large(6, 0, 0));
        Assert.True(bench.V.Policies.Install(new AccessPolicy(
            (viewer, ghost) => viewer != TestBoard.Bob || ghost.Id != shown[1],
            (viewer, plan) => viewer != TestBoard.Bob || plan.Id != hiddenPlan,
            _ => Result<Unit, Reason>.Ok(Unit.Value))).IsOk);
        Settle(bench);

        Assert.Equal(shown, Assert.IsType<Answer<PlanFacts>.Ready>(TestKitIds.Must(bench.Module.Facts(bench.Default))).Value.BuildableNext);
        Assert.NotEmpty(Assert.IsType<Answer<PlanFacts>.Ready>(TestKitIds.Must(bench.Module.Facts(hiddenPlan))).Value.BuildableNext);
        Assert.Equal(shown.Concat(FactsOf(bench, hiddenPlan)), Ready(bench.Plans, Everything));
        Assert.Equal(new[] { shown[0] }, Ready(plans, Everything));
    }

    [Fact]
    public void A_selector_reads_through_the_viewer_s_view()
    {
        var bench = new PlanBench();
        var plans = new PlansView(bench.V.Board, TestBoard.Bob);
        var hiddenPlan = NewPlan(bench, "Hidden");
        var shown = PlaceIn(bench, bench.Default, Large(0, 0, 0), Large(3, 0, 0));
        var hidden = PlaceIn(bench, hiddenPlan, Large(6, 0, 0));
        Assert.True(bench.V.Policies.Install(new AccessPolicy(
            (viewer, ghost) => viewer != TestBoard.Bob || (ghost.Id != shown[1] && ghost.Plan != hiddenPlan),
            (viewer, plan) => viewer != TestBoard.Bob || plan.Id != hiddenPlan,
            _ => Result<Unit, Reason>.Ok(Unit.Value))).IsOk);
        Settle(bench);

        Assert.Equal(hidden, Assert.IsType<Answer<PlanFacts>.Ready>(TestKitIds.Must(bench.Module.Facts(hiddenPlan))).Value.BuildableNext);
        Assert.Equal(new[] { shown[0] }, Ready(plans, new Selector.Ids(ValueList.From(new[] { shown[0], shown[1], hidden[0] }))));
        Assert.Empty(Ready(plans, new Selector.InPlan(hiddenPlan)));
        Assert.Equal(hidden, Ready(bench.Plans, new Selector.InPlan(hiddenPlan)));
    }

    [Fact]
    public void Without_the_module_it_is_module_missing()
    {
        var bench = new ValidationBench().Start();

        Assert.True(new PlansView(bench.Board, TestBoard.Alice).BuildableNext(Everything).TryGetError(out var why));

        Assert.Equal(ReasonCodes.ModuleMissing, why.Code);
    }

    [Fact]
    public void A_repeat_read_of_an_unchanged_board_allocates_nothing_and_a_change_reads_afresh()
    {
        var bench = new PlanBench();
        var plans = bench.Plans;
        var second = NewPlan(bench, "Second");
        PlaceIn(bench, bench.Default, Large(0, 0, 0));
        var theirs = PlaceIn(bench, second, Large(6, 0, 0));
        Settle(bench);
        var inPlan = new Selector.InPlan(second);
        var first = Ready(plans, Everything);
        Ready(plans, inPlan);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            plans.BuildableNext(Everything);
            plans.BuildableNext(inPlan);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Same(first, Ready(plans, Everything));

        TestKitIds.Must(bench.V.Board.Complete(theirs[0], new Completion(Option<StructureRef>.None, DateTimeOffset.UnixEpoch)));
        Settle(bench);

        Assert.Single(Ready(plans, Everything));
        Assert.Empty(Ready(plans, inPlan));
    }
}
