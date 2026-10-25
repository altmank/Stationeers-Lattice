using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.TestKit;
using static Lattice.Tests.Board.TestBoard;
using TestBoard = Lattice.Tests.Board.TestBoard;

namespace Lattice.Tests.Commands;

public sealed class RevertTests
{
    private static readonly Cell At = new(100, 100, 100);
    private static readonly Cell Elsewhere = new(200, 100, 100);

    private static Command Relabel(GhostId id, string label) => new Command.Change(
        Ids(id), new GhostEdit.SetLabel(Option.Some(TestKitIds.Must(Label.Create(label)))), Option<Revision>.None);

    private static Command Revert(ChangeSet changes) => new Command.Revert(changes.Id);

    private static string LabelOf(Ghost ghost) => ghost.Label.Map(static l => l.Value).Reduce("");

    [Fact]
    public void Reverting_a_placement_removes_its_ghosts()
    {
        var t = new TestBoard();
        var placed = t.Placed(t.Draft("StructureCableStraight", At), t.Draft("StructureCableStraight", Elsewhere));

        var undone = Applied(t.Apply(Revert(placed)));

        Assert.Equal(2, undone.Removed.Count);
        Assert.All(undone.Removed, static r => Assert.IsType<RemovalCause.Removed>(r.Cause));
        Assert.Empty(t.View.At(At));
        Assert.Empty(t.View.At(Elsewhere));
    }

    [Fact]
    public void Reverting_a_removal_restores_the_ghosts_with_their_ids()
    {
        var t = new TestBoard();
        var ghost = Assert.Single(t.Placed(t.Draft("StructureCableStraight", At)).Added);
        var removed = Applied(t.Apply(new Command.Remove(Ids(ghost.Id))));

        var undone = Applied(t.Apply(Revert(removed)));

        var restored = Assert.Single(undone.Added);
        Assert.Equal(ghost.Id, restored.Id);
        Assert.Equal(ghost with { Revision = undone.Revision }, restored);
        Assert.Equal(ghost.Id, Assert.Single(t.View.At(At)).Id);
    }

    [Fact]
    public void Reverting_a_placement_that_replaced_a_ghost_brings_the_old_one_back()
    {
        var t = new TestBoard();
        var old = Assert.Single(t.Placed(t.Draft("StructureCableStraight", At)).Added);
        var replacing = t.Placed(t.Draft("StructureCableCorner", At));
        Assert.Equal(old.Id, Assert.Single(replacing.Removed).Ghost.Id);

        var undone = Applied(t.Apply(Revert(replacing)));

        Assert.Equal(old.Id, Assert.Single(undone.Added).Id);
        Assert.Equal(replacing.Added[0].Id, Assert.Single(undone.Removed).Ghost.Id);
        Assert.Equal(old.Id, Assert.Single(t.View.At(At)).Id);
    }

    [Fact]
    public void Reverting_an_edit_sets_the_ghost_back_and_keeps_its_lifecycle_state()
    {
        var t = new TestBoard();
        var ghost = Assert.Single(t.Placed(t.Draft("StructureCableStraight", At)).Added);
        var edit = Applied(t.Apply(Relabel(ghost.Id, "after")));
        var partBuilt = new StructureRef(new ReferenceId(77), ghost.Intent is Intent.Build b ? b.Target.Piece : default, At);
        Assert.True(t.Board.MarkStarted(ghost.Id, partBuilt).IsOk);

        var undone = Applied(t.Apply(Revert(edit)));

        var change = Assert.Single(undone.Changed);
        Assert.Equal("", LabelOf(change.After));
        Assert.IsType<GhostState.Started>(change.After.State);
        Assert.Equal(undone.Revision, change.After.Revision);
    }

    [Fact]
    public void Reverting_a_change_set_someone_else_edited_since_is_refused_naming_the_ghost_and_the_actor()
    {
        var t = new TestBoard();
        var placed = t.Placed(t.Draft("StructureCableStraight", At), t.Draft("StructureCableStraight", Elsewhere));
        var touched = placed.Added[1].Id;
        Applied(t.Apply(Relabel(touched, "bob was here"), Bob));
        var revision = t.Board.Revision;

        var refusal = Refused(t.Apply(Revert(placed)));

        Assert.Equal(ReasonCodes.ChangedSince, refusal.Why.Code);
        Assert.Equal([touched], refusal.Ghosts);
        Assert.True(refusal.Why.Facts.TryGetValue("actor", out var actor) && actor == "player:2");
        Assert.Equal(revision, t.Board.Revision);
        Assert.Equal(2, t.Board.Index.Ghosts.Count);
    }

    [Fact]
    public void Reverting_after_someone_else_removed_one_of_the_ghosts_is_refused()
    {
        var t = new TestBoard();
        var placed = t.Placed(t.Draft("StructureCableStraight", At));
        Applied(t.Apply(new Command.Remove(Ids(placed.Added[0].Id)), Bob));

        var refusal = Refused(t.Apply(Revert(placed)));

        Assert.Equal(ReasonCodes.ChangedSince, refusal.Why.Code);
        Assert.Equal([placed.Added[0].Id], refusal.Ghosts);
    }

    [Fact]
    public void Reverting_after_my_own_later_edit_is_allowed()
    {
        var t = new TestBoard();
        var placed = t.Placed(t.Draft("StructureCableStraight", At));
        Applied(t.Apply(Relabel(placed.Added[0].Id, "mine")));

        var undone = Applied(t.Apply(Revert(placed)));

        Assert.Single(undone.Removed);
    }

    [Fact]
    public void Restoring_into_a_spot_someone_else_has_since_filled_is_refused()
    {
        var t = new TestBoard();
        var ghost = Assert.Single(t.Placed(t.Draft("StructureCableStraight", At)).Added);
        var removed = Applied(t.Apply(new Command.Remove(Ids(ghost.Id))));
        var bobs = Assert.Single(Applied(t.Apply(new Command.Place(ValueList.Of(t.Draft("StructureCableCorner", At))), Bob)).Added);

        var refusal = Refused(t.Apply(Revert(removed)));

        Assert.Equal(ReasonCodes.ChangedSince, refusal.Why.Code);
        Assert.Equal([bobs.Id], refusal.Ghosts);
    }

    [Fact]
    public void Reverting_another_actors_change_set_is_refused()
    {
        var t = new TestBoard();
        var placed = t.Placed(t.Draft("StructureCableStraight", At));

        var refusal = Refused(t.Apply(Revert(placed), Bob));

        Assert.Equal(ReasonCodes.NotAllowed, refusal.Why.Code);
        Assert.Single(t.View.At(At));
    }

    [Fact]
    public void Reverting_an_unknown_change_set_is_refused_not_found()
    {
        var t = new TestBoard();

        Assert.Equal(ReasonCodes.NotFound, Refused(t.Apply(new Command.Revert(new ChangeSetId(42)))).Why.Code);
    }

    [Fact]
    public void A_default_change_set_id_is_refused()
    {
        var t = new TestBoard();

        Assert.Equal(ReasonCodes.DefaultValue, Refused(t.Apply(new Command.Revert(default))).Why.Code);
    }

    [Fact]
    public void Reverting_a_revert_redoes_the_change()
    {
        var t = new TestBoard();
        var placed = t.Placed(t.Draft("StructureCableStraight", At));
        var undone = Applied(t.Apply(Revert(placed)));

        var redone = Applied(t.Apply(Revert(undone)));

        Assert.Equal(placed.Added[0].Id, Assert.Single(redone.Added).Id);
        Assert.Single(t.View.At(At));
    }

    [Fact]
    public void Built_work_is_never_reverted_a_completed_ghost_is_skipped()
    {
        var t = new TestBoard();
        var placed = t.Placed(t.Draft("StructureCableStraight", At), t.Draft("StructureCableStraight", Elsewhere));
        Assert.True(t.Board.Complete(placed.Added[0].Id, new Completion(Option<StructureRef>.None, t.Time.Now)).IsOk);

        var undone = Applied(t.Apply(Revert(placed)));

        Assert.Equal(placed.Added[1].Id, Assert.Single(undone.Removed).Ghost.Id);
        Assert.Empty(t.Board.Index.Ghosts);
    }

    [Fact]
    public void Reverting_a_plan_edit_sets_the_plan_back()
    {
        var t = new TestBoard();
        var before = t.Board.Index.Plans[GhostBoard.DefaultPlan].Plan;
        var renamed = Applied(t.Apply(new Command.EditPlan(GhostBoard.DefaultPlan, new PlanEdit.Rename(TestKitIds.Must(PlanName.Create("Mine"))), Option<Revision>.None)));

        var undone = Applied(t.Apply(Revert(renamed)));

        var change = Assert.Single(undone.PlansChanged);
        Assert.Equal(before.Name, change.After.Name);
        Assert.Equal(before.Name, t.Board.Index.Plans[GhostBoard.DefaultPlan].Plan.Name);
    }

    [Fact]
    public void A_plan_edit_someone_else_made_since_refuses_the_revert_naming_the_plan()
    {
        var t = new TestBoard();
        var renamed = Applied(t.Apply(new Command.EditPlan(GhostBoard.DefaultPlan, new PlanEdit.Rename(TestKitIds.Must(PlanName.Create("Mine"))), Option<Revision>.None)));
        Applied(t.Apply(new Command.EditPlan(GhostBoard.DefaultPlan, new PlanEdit.Rename(TestKitIds.Must(PlanName.Create("Bobs"))), Option<Revision>.None), Bob));

        var refusal = Refused(t.Apply(Revert(renamed)));

        Assert.Equal(ReasonCodes.ChangedSince, refusal.Why.Code);
        Assert.True(refusal.Why.Facts.TryGetValue("plan", out var plan) && plan == "1");
    }

    [Fact]
    public void Reverting_a_change_set_that_opened_a_plan_is_refused()
    {
        var t = new TestBoard();
        var draft = new PlanDraft(TestKitIds.Must(PlanName.Create("Hydro")), TestMaker, new Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty);
        var created = Applied(t.Apply(new Command.CreatePlan(draft)));

        var refusal = Refused(t.Apply(Revert(created)));

        Assert.Equal(ReasonCodes.NotAllowed, refusal.Why.Code);
        Assert.True(refusal.Why.Facts.ContainsKey("plans-opened"));
    }

    [Fact]
    public void A_revert_is_asked_of_the_policy_per_ghost()
    {
        var t = new TestBoard();
        var placed = t.Placed(t.Draft("StructureCableStraight", At));
        var asked = new List<AccessRequest>();
        Assert.True(t.Policies.Install(new AccessPolicy((_, _) => true, (_, _) => true, r =>
        {
            asked.Add(r);
            return Result<Unit, Reason>.Ok(Unit.Value);
        })).IsOk);

        Applied(t.Apply(Revert(placed)));

        var request = Assert.Single(asked);
        Assert.IsType<Command.Revert>(request.Command);
        Assert.Equal(placed.Added[0].Id, request.Ghost.Map(static g => g.Id).Reduce(default(GhostId)));
    }

    [Fact]
    public void A_revert_inside_a_batch_undoes_with_the_batch()
    {
        var t = new TestBoard();
        var placed = t.Placed(t.Draft("StructureCableStraight", At));

        var changes = Applied(t.Apply(new Command.Batch(ValueList.From<Command>([
            Revert(placed),
            new Command.Place(ValueList.Of(t.Draft("StructureCableCorner", Elsewhere))),
        ]))));

        Assert.Single(changes.Removed);
        Assert.Single(changes.Added);
    }
}
