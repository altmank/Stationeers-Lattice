using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.TestKit;
using TestBoard = Lattice.Tests.Board.TestBoard;
using static Lattice.Tests.Board.TestBoard;

namespace Lattice.Tests.Commands;

public sealed class BatchTests
{
    private static readonly Cell At = new(100, 100, 100);

    private static Command Place(TestBoard t, Cell at) => new Command.Place(ValueList.Of(t.Draft("StructureCableStraight", at)));

    private static Command Relabel(GhostId id, string label) => new Command.Change(
        Ids(id), new GhostEdit.SetLabel(Option.Some(TestKitIds.Must(Label.Create(label)))), Option<Revision>.None);

    private static Command Batch(params Command[] steps) => new Command.Batch(ValueList.From(steps));

    [Fact]
    public void A_batch_whose_third_step_is_refused_changes_nothing()
    {
        var t = new TestBoard();
        var kept = Assert.Single(t.Placed(t.Draft("StructureCableStraight", new Cell(0, 0, 0))).Added);
        t.Events.Flush();
        var revision = t.Board.Revision;
        var seen = new List<ChangeSet>();
        t.Events.Changed += seen.Add;

        var refusal = Refused(t.Apply(Batch(
            Place(t, At),
            new Command.Remove(Ids(kept.Id)),
            new Command.Remove(Ids(new GhostId(999))))));

        Assert.Equal(ReasonCodes.NotFound, refusal.Why.Code);
        Assert.True(refusal.Why.Facts.TryGetValue("step", out var step) && step == "2");
        Assert.Equal(revision, t.Board.Revision);
        Assert.True(t.View.Find(kept.Id).IsSome);
        Assert.Empty(t.View.At(At));
        t.Events.Flush();
        Assert.Empty(seen);
        var next = Assert.Single(t.Placed(t.Draft("StructureCableStraight", At)).Added);
        Assert.Equal(kept.Id.Value + 1, next.Id.Value);
    }

    [Fact]
    public void A_batch_is_one_change_set_at_one_revision_with_its_net_effect()
    {
        var t = new TestBoard();
        var old = Assert.Single(t.Placed(t.Draft("StructureCableStraight", new Cell(0, 0, 0))).Added);
        var gone = Assert.Single(t.Placed(t.Draft("StructureCableStraight", new Cell(0, 0, 50))).Added);
        var revision = t.Board.Revision;
        var placedId = new GhostId(gone.Id.Value + 1);
        var droppedId = new GhostId(gone.Id.Value + 2);

        var changes = Applied(t.Apply(Batch(
            Place(t, At),
            Relabel(placedId, "first"),
            Relabel(old.Id, "edited"),
            Place(t, new Cell(200, 100, 100)),
            new Command.Remove(Ids(droppedId, gone.Id)))));

        Assert.Equal(revision.Value + 1, changes.Revision.Value);
        Assert.Equal(changes.Revision, t.Board.Revision);
        var added = Assert.Single(changes.Added);
        Assert.Equal(placedId, added.Id);
        Assert.Equal("first", added.Label.Map(static l => l.Value).Reduce(""));
        Assert.Equal(changes.Revision, added.Revision);
        var changed = Assert.Single(changes.Changed);
        Assert.Equal(old, changed.Before);
        Assert.Equal("edited", changed.After.Label.Map(static l => l.Value).Reduce(""));
        var removed = Assert.Single(changes.Removed);
        Assert.Equal(gone, removed.Ghost);
        Assert.True(t.View.Find(droppedId).IsNone);
    }

    [Fact]
    public void A_nested_batch_applies_with_its_parent()
    {
        var t = new TestBoard();

        var changes = Applied(t.Apply(Batch(Place(t, At), Batch(Place(t, new Cell(200, 100, 100)), Place(t, new Cell(300, 100, 100))))));

        Assert.Equal(3, changes.Added.Count);
        Assert.Equal(new Revision(1), t.Board.Revision);
    }

    [Fact]
    public void The_policy_is_asked_about_every_step_on_the_unchanged_board_and_its_refusal_changes_nothing()
    {
        var t = new TestBoard();
        var ghost = Assert.Single(t.Placed(t.Draft("StructureCableStraight", new Cell(0, 0, 0))).Added);
        var revision = t.Board.Revision;
        var asked = new List<(Command Command, int GhostsOnBoard)>();
        var no = TestKitIds.Must(ReasonCode.Create(TestKitIds.Mod, "no-removals"));
        Assert.True(t.Policies.Install(new AccessPolicy(
            (_, _) => true,
            (_, _) => true,
            request =>
            {
                asked.Add((request.Command, t.View.Select(new Selector.InPlan(GhostBoard.DefaultPlan)).Match(static g => g.Count, static _ => -1)));
                return request.Command is Command.Remove
                    ? Result<Unit, Reason>.Error(Reason.Of(no, RuleId.Contract))
                    : Result<Unit, Reason>.Ok(Unit.Value);
            })).IsOk);

        var refusal = Refused(t.Apply(Batch(Place(t, At), new Command.Remove(Ids(ghost.Id)))));

        Assert.Equal(no, refusal.Why.Code);
        Assert.Equal([ghost.Id], refusal.Ghosts);
        Assert.Equal(2, asked.Count);
        Assert.All(asked, static a => Assert.Equal(1, a.GhostsOnBoard));
        Assert.Equal(revision, t.Board.Revision);
        Assert.Empty(t.View.At(At));
    }

    [Fact]
    public void An_empty_batch_applies_an_empty_change_set()
    {
        var t = new TestBoard();

        var changes = Applied(t.Apply(Batch()));

        Assert.Empty(changes.Added);
        Assert.Empty(changes.Changed);
        Assert.Empty(changes.Removed);
    }

    [Fact]
    public void A_change_in_a_batch_is_not_refused_as_changed_since_for_what_an_earlier_step_did()
    {
        var t = new TestBoard();
        var ghost = Assert.Single(t.Placed(t.Draft("StructureCableStraight", At)).Added);
        var seen = t.Board.Revision;

        var changes = Applied(t.Apply(Batch(
            Relabel(ghost.Id, "one"),
            new Command.Change(Ids(ghost.Id), new GhostEdit.SetTint(Option<Tint>.None), Option.Some(seen)))));

        Assert.Equal("one", Assert.Single(changes.Changed).After.Label.Map(static l => l.Value).Reduce(""));
    }

    [Fact]
    public void A_default_id_inside_a_batch_is_refused()
    {
        var t = new TestBoard();

        var refusal = Refused(t.Apply(Batch(Place(t, At), new Command.Remove(Ids(default(GhostId))))));

        Assert.Equal(ReasonCodes.DefaultValue, refusal.Why.Code);
        Assert.Empty(t.View.At(At));
    }

    [Fact]
    public void A_plan_created_in_a_batch_takes_ghosts_placed_later_in_it()
    {
        var t = new TestBoard();
        var name = TestKitIds.Must(PlanName.Create("Hydro"));
        var draft = new PlanDraft(name, TestMaker, new Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty);
        var plan = new PlanId(2);

        var changes = Applied(t.Apply(Batch(
            new Command.CreatePlan(draft),
            new Command.Place(ValueList.Of(t.Draft("StructureCableStraight", At, plan: Option.Some(plan)))))));

        Assert.Equal(plan, Assert.Single(changes.PlansOpened).Id);
        Assert.Equal(plan, Assert.Single(changes.Added).Plan);
        Assert.Contains(changes.Added[0].Id, t.Board.Index.Plans[plan].Ghosts);
    }

    [Fact]
    public void A_refused_batch_that_created_a_plan_leaves_no_plan()
    {
        var t = new TestBoard();
        var draft = new PlanDraft(TestKitIds.Must(PlanName.Create("Hydro")), TestMaker, new Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty);

        Refused(t.Apply(Batch(
            new Command.CreatePlan(draft),
            new Command.Place(ValueList.Of(t.Draft("StructureCableStraight", At, plan: Option.Some(new PlanId(2))))),
            new Command.Remove(Ids(new GhostId(999))))));

        Assert.False(t.Board.Index.Plans.ContainsKey(new PlanId(2)));
        var created = Applied(t.Apply(new Command.CreatePlan(draft)));
        Assert.Equal(new PlanId(2), Assert.Single(created.PlansOpened).Id);
    }

    [Fact]
    public void Steps_refused_by_kind_say_which_step()
    {
        var t = new TestBoard();

        var refusal = Refused(t.Apply(Batch(Place(t, At), new Command.Repair(new PlanId(1), TestMaker))));

        Assert.Equal(ReasonCodes.ModuleMissing, refusal.Why.Code);
        Assert.True(refusal.Why.Facts.TryGetValue("step", out var step) && step == "1");
    }

    [Fact]
    public void A_visibility_check_that_throws_while_steps_are_staged_leaves_the_board_unchanged()
    {
        var t = new TestBoard();
        var kept = Assert.Single(t.Placed(t.Draft("StructureCableStraight", new Cell(0, 0, 0))).Added);
        var throwing = false;
        Assert.True(t.Policies.Install(new AccessPolicy(
            (_, _) => throwing ? throw new InvalidOperationException("policy bug") : true,
            (_, _) => true,
            _ => Result<Unit, Reason>.Ok(Unit.Value))).IsOk);
        var revision = t.Board.Revision;
        throwing = true;

        Assert.Throws<InvalidOperationException>(() => t.Apply(Batch(Place(t, At), new Command.Remove(Ids(kept.Id)))));
        throwing = false;

        Assert.Equal(revision, t.Board.Revision);
        Assert.Equal([kept.Id], [.. t.Board.Index.Ghosts.Keys]);
        Assert.Empty(t.View.At(At));
        Assert.Equal(kept.Id.Value + 1, Assert.Single(t.Placed(t.Draft("StructureCableStraight", At)).Added).Id.Value);
    }
}
