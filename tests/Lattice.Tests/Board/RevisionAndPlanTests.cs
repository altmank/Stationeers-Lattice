using System;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.TestKit;
using static Lattice.Tests.Board.TestBoard;

namespace Lattice.Tests.Board;

public sealed class RevisionAndPlanTests
{
    private static readonly Cell At = new(100, 100, 100);
    private static readonly PlanName Store = TestKitIds.Must(PlanName.Create("North store"));
    private static readonly AttachmentKey Flag = TestKitIds.Must(AttachmentKey.Create(TestKitIds.Mod, "workflow"));
    private static readonly Attachment Approved = TestKitIds.Must(Attachment.Create("approved"));

    private static Command CreatePlan() => new Command.CreatePlan(new PlanDraft(
        Store, TestMaker, new Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty));

    private static Command Relabel(GhostId id, Option<Revision> since = default) =>
        new Command.Change(Ids(id), new GhostEdit.SetLabel(Option<Label>.None), since);

    [Fact]
    public void Revisions_advance_by_one_per_applied_command_and_not_on_a_refusal()
    {
        var t = new TestBoard();
        var first = t.Placed(t.Draft("StructureCableStraight", At));
        Assert.Equal(new Revision(1), first.Revision);
        Assert.Equal(new Revision(1), Assert.Single(first.Added).Revision);

        Refused(t.Place(t.Draft("StructureChuteStraight", At)));
        Assert.Equal(new Revision(1), t.Board.Revision);

        var second = Applied(t.Apply(Relabel(first.Added[0].Id)));
        Assert.Equal(new Revision(2), second.Revision);
        Assert.Equal(new Revision(2), Assert.Single(second.Changed).After.Revision);
    }

    [Fact]
    public void Ids_are_never_reused_across_remove_and_add()
    {
        var t = new TestBoard();
        var first = Assert.Single(t.Placed(t.Draft("StructureCableStraight", At)).Added);
        Applied(t.Apply(new Command.Remove(Ids(first.Id))));

        var second = Assert.Single(t.Placed(t.Draft("StructureCableStraight", At)).Added);

        Assert.True(second.Id.Value > first.Id.Value);
    }

    [Fact]
    public void A_seeded_board_hands_out_ids_above_every_branch_of_its_plan_files()
    {
        var older = new TestBoard();
        var loaded = Assert.Single(older.Placed(older.Draft("StructureCableStraight", At)).Added);
        var seed = new BoardSeed(new Revision(1), new IdCounters(NextGhost: 501, NextPlan: 40, NextChangeSet: 900),
            ValueList<Plan>.Empty, ValueList.Of(loaded));
        var t = new TestBoard(Option.Some(seed));

        var placed = t.Placed(t.Draft("StructureCableStraight", new Cell(200, 100, 100)));
        var plan = Applied(t.Apply(CreatePlan()));

        Assert.Equal(new GhostId(501), Assert.Single(placed.Added).Id);
        Assert.Equal(new ChangeSetId(900), placed.Id);
        Assert.Equal(new PlanId(40), Assert.Single(plan.PlansOpened).Id);
        Assert.True(t.View.Find(loaded.Id).IsSome);
    }

    [Fact]
    public void A_mark_from_the_world_is_not_a_change_since()
    {
        var t = new TestBoard();
        var ghost = Assert.Single(t.Placed(t.Draft("StructureCableStraight", At)).Added);
        var seen = t.Board.Revision;
        var partBuilt = new StructureRef(new ReferenceId(9), t.Catalogue.Named("StructureCableStraight").Key, At);
        Assert.True(t.Board.MarkStarted(ghost.Id, partBuilt).IsOk);

        Applied(t.Apply(Relabel(ghost.Id, Option.Some(seen))));

        var refusal = Refused(t.Apply(Relabel(ghost.Id, Option.Some(seen)), Bob));
        Assert.Equal(ReasonCodes.ChangedSince, refusal.Why.Code);
        Assert.Equal(ValueList.Of(ghost.Id), refusal.Ghosts);
    }

    [Fact]
    public void A_lifecycle_change_set_has_its_own_command_id_and_Lattice_as_actor()
    {
        var t = new TestBoard();
        var ghost = Assert.Single(t.Placed(t.Draft("StructureCableStraight", At)).Added);

        Assert.True(t.Board.MarkStale(ghost.Id, Reason.Of(ReasonCodes.Different, RuleId.Contract)).TryGet(out var marked));

        Assert.NotEqual(default, marked.Command);
        Assert.Equal(LatticeIdentity.Actor, marked.By);
        Assert.IsType<GhostState.Stale>(Assert.Single(marked.Changed).After.State);
    }

    [Fact]
    public void A_ghost_with_no_plan_goes_in_the_default_plan_and_an_unknown_plan_is_refused()
    {
        var t = new TestBoard();

        var loose = Assert.Single(t.Placed(t.Draft("StructureCableStraight", At)).Added);
        var refusal = Refused(t.Place(t.Draft("StructureCableStraight", new Cell(200, 100, 100), plan: Option.Some(new PlanId(77)))));

        Assert.Equal(GhostBoard.DefaultPlan, loose.Plan);
        Assert.Equal(ReasonCodes.NotFound, refusal.Why.Code);
        Assert.True(new PlansView(t.Board, Alice).Find(GhostBoard.DefaultPlan).IsSome);
    }

    [Fact]
    public void A_plan_is_owned_by_its_creator_and_keeps_higher_layer_data_it_never_acts_on()
    {
        var t = new TestBoard();
        var plan = Assert.Single(Applied(t.Apply(CreatePlan(), Bob)).PlansOpened);

        var edited = Applied(t.Apply(new Command.EditPlan(plan.Id, new PlanEdit.SetData(Flag, Approved), Option<Revision>.None)));

        Assert.Equal(Bob, plan.Owner);
        var after = Assert.Single(edited.PlansChanged).After;
        Assert.True(after.HigherLayerData.TryGetValue(Flag, out var data) && data == Approved);
        Assert.Equal(2, new PlansView(t.Board, Alice).All.Count);
    }

    [Fact]
    public void Moving_ghosts_to_a_plan_keeps_each_in_exactly_one_plan()
    {
        var t = new TestBoard();
        var ghost = Assert.Single(t.Placed(t.Draft("StructureCableStraight", At)).Added);
        var plan = Assert.Single(Applied(t.Apply(CreatePlan())).PlansOpened);

        Applied(t.Apply(new Command.MoveToPlan(Ids(ghost.Id), plan.Id)));

        Assert.True(t.View.Select(new Selector.InPlan(plan.Id)).TryGet(out var inPlan) && inPlan.Count == 1);
        Assert.True(t.View.Select(new Selector.InPlan(GhostBoard.DefaultPlan)).TryGet(out var loose) && loose.Count == 0);
    }

    [Fact]
    public void A_plan_edit_after_another_command_change_is_refused_changed_since()
    {
        var t = new TestBoard();
        var plan = Assert.Single(Applied(t.Apply(CreatePlan())).PlansOpened);
        var seen = t.Board.Revision;
        Applied(t.Apply(new Command.EditPlan(plan.Id, new PlanEdit.ChangeOwner(Bob), Option<Revision>.None)));

        var refusal = Refused(t.Apply(new Command.EditPlan(plan.Id, new PlanEdit.Rename(Store), Option.Some(seen))));

        Assert.Equal(ReasonCodes.ChangedSince, refusal.Why.Code);
    }

    [Fact]
    public void A_size_limit_set_by_a_higher_layer_refuses_a_plan_past_it()
    {
        var t = new TestBoard();
        Assert.True(t.Limit.Set(2).IsOk);

        var refusal = Refused(t.Place(
            t.Draft("StructureCableStraight", At),
            t.Draft("StructureCableStraight", new Cell(200, 100, 100)),
            t.Draft("StructureCableStraight", new Cell(300, 100, 100))));

        Assert.Equal(ReasonCodes.TooLarge, refusal.Why.Code);
        Assert.Equal(default, t.Board.Revision);
    }

    [Fact]
    public void Done_ghosts_leave_the_board_and_completion_follows_their_change_set()
    {
        var t = new TestBoard();
        var ghost = Assert.Single(t.Placed(t.Draft("StructureCableStraight", At)).Added);
        t.Events.Flush();
        var order = new System.Collections.Generic.List<string>();
        t.Board.Events.Changed += c => order.Add("changed " + Assert.Single(c.Removed).Cause.GetType().Name);
        t.Board.Events.Completed += (id, _) => order.Add("completed " + id.Value);

        Assert.True(t.Board.Complete(ghost.Id, new Completion(Option<StructureRef>.None, t.Time.Now)).IsOk);
        t.Events.Flush();

        Assert.True(t.View.Find(ghost.Id).IsNone);
        Assert.Equal(["changed Done", "completed " + ghost.Id.Value], order);
    }

    [Fact]
    public void A_ghost_whose_target_is_gone_is_dropped()
    {
        var t = new TestBoard();
        var structure = new StructureRef(new ReferenceId(5), t.Catalogue.Named("StructureFrameIron").Key, new Cell(10, 10, 10));
        var ghost = Assert.Single(t.Placed(Draft(new Intent.Remove(structure))).Added);

        Assert.True(t.Board.DropTargetGone(ghost.Id).TryGet(out var dropped));

        Assert.IsType<RemovalCause.TargetGone>(Assert.Single(dropped.Removed).Cause);
    }

    [Fact]
    public void Ghosts_on_a_structure_are_found_after_the_game_gives_it_a_new_id()
    {
        var t = new TestBoard();
        var frame = t.Catalogue.Named("StructureFrameIron").Key;
        var cell = new Cell(10, 10, 10);
        var ghost = Assert.Single(t.Placed(Draft(new Intent.Remove(new StructureRef(new ReferenceId(5), frame, cell)))).Added);

        Assert.Equal(ValueList.Of(ghost), t.View.On(new StructureRef(new ReferenceId(5), frame, cell)));
        Assert.Equal(ValueList.Of(ghost), t.View.On(new StructureRef(new ReferenceId(99), frame, cell)));
    }

    [Fact]
    public void A_started_ghost_is_found_on_its_part_built_structure()
    {
        var t = new TestBoard();
        var ghost = Assert.Single(t.Placed(t.Draft("StructureCableStraight", At)).Added);
        var partBuilt = new StructureRef(new ReferenceId(12), t.Catalogue.Named("StructureCableStraight").Key, At);

        Assert.True(t.Board.MarkStarted(ghost.Id, partBuilt).IsOk);

        Assert.Equal(ghost.Id, Assert.Single(t.View.On(partBuilt)).Id);
    }
}
