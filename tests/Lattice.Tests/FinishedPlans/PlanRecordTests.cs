using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.TestKit;
using Lattice.Tests.Board;
using static Lattice.Tests.FinishedPlans.FinishedBench;

namespace Lattice.Tests.FinishedPlans;

/// <summary>
/// <see cref="AccessRequest.PlanRecord"/>: every ask carries the plan the change lands in as it stands before the change
/// (inside a batch, as the earlier steps leave it), or as it opens: a reopened plan as it was finished, a new plan as its
/// draft or expansion makes it. Lattice only passes it; the policy decides.
/// </summary>
public sealed class PlanRecordTests
{
    private static readonly ReasonCode OwnersOnly = TestKitIds.Must(ReasonCode.Create(TestKitIds.Mod, "owners-only"));

    private static List<AccessRequest> Recording(FinishedBench b)
    {
        var asked = new List<AccessRequest>();
        Assert.True(b.T.Policies.Install(new AccessPolicy(
            static (_, _) => true, static (_, _) => true,
            request =>
            {
                asked.Add(request);
                return Result<Unit, Reason>.Ok(Unit.Value);
            })).IsOk);
        return asked;
    }

    private static PlanDraft Draft(string name) =>
        new(TestKitIds.Must(PlanName.Create(name)), TestBoard.TestMaker, new Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty);

    private static void EveryAskCarriesItsPlan(IEnumerable<AccessRequest> asked)
    {
        Assert.NotEmpty(asked);
        foreach (var request in asked)
        {
            Assert.True(request.PlanRecord.TryGet(out var record), $"{request.Command.GetType().Name}: no plan record");
            Assert.Equal(request.Plan, record.Id);
        }
    }

    [Fact]
    public void A_reopen_is_asked_with_the_plan_as_it_was_finished_for_the_plan_and_every_returning_ghost()
    {
        using var b = new FinishedBench();
        var plan = b.NewPlan("Hall", new Visibility.OwnerOnly());
        b.Plan(plan, "ModTruss", Large(1));
        b.Plan(plan, "ModTruss", Large(2));
        b.Finish(plan);
        var asked = Recording(b);

        TestBoard.Applied(b.Apply(new Command.Reopen(plan)));

        var reopenAsks = asked.Where(static r => r.Command is Command.Reopen).ToList();
        Assert.Equal(3, reopenAsks.Count);
        foreach (var request in reopenAsks)
        {
            var record = request.PlanRecord.Reduce(() => throw new Xunit.Sdk.XunitException("no record"));
            Assert.Equal(plan, record.Id);
            Assert.Equal("Hall", record.Name.Value);
            Assert.Equal(TestBoard.Alice, record.Owner);
            Assert.IsType<Visibility.OwnerOnly>(record.Visibility);
        }
    }

    [Fact]
    public void A_policy_that_lets_only_the_records_owner_reopen_refuses_another_actor_and_changes_nothing()
    {
        using var b = new FinishedBench();
        var plan = b.NewPlan("Hall");
        b.Plan(plan, "ModTruss", Large(1));
        b.Finish(plan);
        Assert.True(b.T.Policies.Install(new AccessPolicy(
            static (_, _) => true, static (_, _) => true,
            static request => request.Command is Command.Reopen && !request.PlanRecord.Match(p => p.Owner == request.Actor, () => false)
                ? Result<Unit, Reason>.Error(Reason.Of(OwnersOnly, RuleId.Contract))
                : Result<Unit, Reason>.Ok(Unit.Value))).IsOk);
        var at = b.Board.Revision;

        var refused = TestBoard.Refused(b.Apply(new Command.Reopen(plan), TestBoard.Bob));
        Assert.Equal(OwnersOnly, refused.Why.Code);
        Assert.Equal(at, b.Board.Revision);

        TestBoard.Applied(b.Apply(new Command.Reopen(plan), TestBoard.Alice));
        Assert.True(new PlansView(b.Board, TestBoard.Alice).Find(plan).IsSome);
    }

    [Fact]
    public void A_new_plan_is_asked_as_its_draft_makes_it_and_so_is_every_ghost_placed_in_it()
    {
        using var b = new FinishedBench();
        var asked = Recording(b);
        var create = new Command.CreatePlan(Draft("Annex"))
        {
            Ghosts = ValueList.From(new[] { b.T.Draft("ModTruss", Large(3)), b.T.Draft("ModTruss", Large(4)) }),
        };

        var opened = TestBoard.Applied(b.Apply(create)).PlansOpened[0];

        Assert.NotEmpty(asked);
        Assert.All(asked, request => Assert.Equal(Option.Some(opened), request.PlanRecord));
    }

    [Fact]
    public void Repair_and_take_down_are_asked_with_the_plan_their_expansion_opens()
    {
        using var b = new FinishedBench();
        var plan = b.NewPlan("Hall");
        var gone = b.Construct(b.Plan(plan, "ModTruss", Large(1)));
        b.Construct(b.Plan(plan, "ModTruss", Large(2)));
        b.Finish(plan);
        b.TakeApart(gone);
        var asked = Recording(b);

        foreach (var found in new[] { b.Repair(plan).Found, b.TakeDown(plan).Found })
        {
            asked.Clear();
            var command = TestKitIds.Must(found);
            var set = TestBoard.Applied(b.Apply(command));

            EveryAskCarriesItsPlan(asked);
            Assert.All(asked, request => Assert.Equal(set.PlansOpened[0].Id, request.Plan));
        }
    }

    [Fact]
    public void Inside_a_batch_a_place_into_a_plan_renamed_by_an_earlier_step_carries_the_renamed_record()
    {
        using var b = new FinishedBench();
        var plan = b.NewPlan("Hall");
        var asked = Recording(b);
        var batch = new Command.Batch(ValueList.From(new Command[]
        {
            new Command.EditPlan(plan, new PlanEdit.Rename(TestKitIds.Must(PlanName.Create("Great hall"))), Option<Revision>.None),
            new Command.Place(ValueList.Of(b.T.Draft("ModTruss", Large(5), default, Option.Some(plan)))),
        }));

        TestBoard.Applied(b.Apply(batch));

        var edit = Assert.Single(asked, static r => r.Command is Command.EditPlan);
        var place = Assert.Single(asked, static r => r.Command is Command.Place && r.Ghost.IsNone);
        Assert.Equal("Hall", edit.PlanRecord.Reduce(() => throw new Xunit.Sdk.XunitException("none")).Name.Value);
        Assert.Equal("Great hall", place.PlanRecord.Reduce(() => throw new Xunit.Sdk.XunitException("none")).Name.Value);
    }

    [Fact]
    public void A_ghost_replaced_from_another_plan_is_asked_with_that_plans_record()
    {
        using var b = new FinishedBench();
        var first = b.NewPlan("First");
        var second = b.NewPlan("Second");
        var old = b.Plan(first, "ModTruss", Large(6));
        var asked = Recording(b);

        TestBoard.Applied(b.Apply(new Command.Place(ValueList.Of(b.T.Draft("ModTruss", Large(6), default, Option.Some(second))))));

        var leaving = Assert.Single(asked, r => r.Ghost.TryGet(out var g) && g.Id == old.Id);
        Assert.Equal(first, leaving.Plan);
        Assert.Equal("First", leaving.PlanRecord.Reduce(() => throw new Xunit.Sdk.XunitException("none")).Name.Value);
    }

    [Fact]
    public void Every_ask_over_a_world_of_changes_carries_its_plan()
    {
        using var b = new FinishedBench();
        var asked = Recording(b);
        var plan = b.NewPlan("Hall");
        var other = b.NewPlan("Yard");
        var ghost = b.Plan(plan, "ModTruss", Large(1));
        b.Plan(plan, "ModPanel", Large(2));
        var changed = TestBoard.Applied(b.Apply(new Command.Change(TestBoard.Ids(ghost.Id), new GhostEdit.SetLabel(Option<Label>.None), Option<Revision>.None)));
        b.Apply(new Command.Move(ValueMap.Of(ghost.Id, b.At("ModTruss", Large(3))), Option<Revision>.None));
        b.Apply(new Command.MoveToPlan(TestBoard.Ids(ghost.Id), other));
        b.Apply(new Command.EditPlan(other, new PlanEdit.ChangeVisibility(new Visibility.OwnerOnly()), Option<Revision>.None));
        b.Apply(new Command.Revert(changed.Id));
        b.Apply(new Command.Remove(TestBoard.Ids(ghost.Id)));
        b.Finish(plan);
        b.Apply(new Command.Reopen(plan));

        EveryAskCarriesItsPlan(asked);
    }

    [Fact]
    public void A_request_a_consumer_builds_with_the_four_values_has_no_record_and_compares_as_before()
    {
        var request = new AccessRequest(TestBoard.Alice, new Command.FinishPlan(new PlanId(3)), new PlanId(3), Option<Ghost>.None);
        var (actor, command, plan, ghost) = request;

        Assert.True(request.PlanRecord.IsNone);
        Assert.Equal(new AccessRequest(actor, command, plan, ghost), request);
    }
}
