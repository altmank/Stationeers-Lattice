using System.Collections.Generic;
using Lattice.Api;
using Lattice.TestKit;
using static Lattice.Tests.Board.TestBoard;

namespace Lattice.Tests.Board;

/// <summary>Moving build ghosts with their ids (<see cref="Command.Move"/>) and creating a plan with its ghosts.</summary>
public sealed class MoveAndCreateTests
{
    private static PlanDraft Draft(string name) =>
        new(TestKitIds.Must(PlanName.Create(name)), TestMaker, new Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty);

    private static Placement FrameAt(TestBoard t, int x) => new(t.Catalogue.Named("StructureFrame").Key, new Cell(x, 10, 10), default);

    private static Command.Move MoveOf(TestBoard t, params (GhostId Id, int X)[] moves)
    {
        var to = new Dictionary<GhostId, Placement>();
        foreach (var (id, x) in moves)
            to[id] = FrameAt(t, x);
        return new Command.Move(ValueMap<GhostId, Placement>.From(to), Option<Revision>.None);
    }

    private static Outcome Move(TestBoard t, params (GhostId Id, int X)[] moves) => t.Apply(MoveOf(t, moves));

    private static Cell At(TestBoard t, GhostId id) =>
        t.View.Find(id).TryGet(out var ghost) && ghost.Intent is Intent.Build build ? build.Target.At : default;

    [Fact]
    public void A_run_shifted_onto_itself_keeps_every_ghost_and_its_id()
    {
        var t = new TestBoard();
        var a = t.Placed(t.Draft("StructureFrame", new Cell(10, 10, 10))).Added[0].Id;
        var b = t.Placed(t.Draft("StructureFrame", new Cell(30, 10, 10))).Added[0].Id;

        var set = Applied(Move(t, (a, 30), (b, 50)));

        Assert.Empty(set.Removed);
        Assert.Equal(2, set.Changed.Count);
        Assert.Equal(new Cell(30, 10, 10), At(t, a));
        Assert.Equal(new Cell(50, 10, 10), At(t, b));
        Assert.Equal(2, t.View.At(new Cell(30, 10, 10)).Count + t.View.At(new Cell(50, 10, 10)).Count);
    }

    [Fact]
    public void A_ghost_that_stays_where_one_lands_is_replaced_by_it()
    {
        var t = new TestBoard();
        var moved = t.Placed(t.Draft("StructureFrame", new Cell(10, 10, 10))).Added[0].Id;
        var staying = t.Placed(t.Draft("StructureFrame", new Cell(30, 10, 10))).Added[0].Id;

        var set = Applied(Move(t, (moved, 30)));

        var removal = Assert.Single(set.Removed);
        Assert.Equal(staying, removal.Ghost.Id);
        Assert.Equal(new RemovalCause.Replaced(moved), removal.Cause);
    }

    [Fact]
    public void A_move_is_refused_for_no_ghosts_two_landing_in_one_spot_or_a_removal()
    {
        var t = new TestBoard();
        var a = t.Placed(t.Draft("StructureFrame", new Cell(10, 10, 10))).Added[0].Id;
        var b = t.Placed(t.Draft("StructureFrame", new Cell(30, 10, 10))).Added[0].Id;
        var structure = new StructureRef(new ReferenceId(9), t.Catalogue.Named("StructureFrame").Key, new Cell(90, 10, 10));
        var removal = t.Placed(TestBoard.Draft(new Intent.Remove(structure))).Added[0].Id;

        var none = Refused(Move(t));
        var same = Refused(Move(t, (a, 50), (b, 50)));
        var notBuild = Refused(Move(t, (removal, 50)));

        Assert.Equal(ReasonCodes.InvalidValue, none.Why.Code);
        Assert.Equal(ReasonCodes.InvalidValue, same.Why.Code);
        Assert.Equal(ReasonCodes.InvalidValue, notBuild.Why.Code);
        Assert.Equal(new Cell(10, 10, 10), At(t, a));
    }

    [Fact]
    public void A_move_naming_a_ghost_not_on_the_board_is_refused_not_found_naming_it_and_moves_nothing()
    {
        var t = new TestBoard();
        var a = t.Placed(t.Draft("StructureFrame", new Cell(10, 10, 10))).Added[0].Id;
        var gone = new GhostId(999);

        var refusal = Refused(Move(t, (a, 50), (gone, 70)));

        Assert.Equal(ReasonCodes.NotFound, refusal.Why.Code);
        Assert.Equal(ValueList.Of(gone), refusal.Ghosts);
        Assert.Equal(new Cell(10, 10, 10), At(t, a));
    }

    [Fact]
    public void A_move_is_refused_changed_since_when_a_moved_ghost_changed_after_the_revision_the_sender_saw()
    {
        var t = new TestBoard();
        var a = t.Placed(t.Draft("StructureFrame", new Cell(10, 10, 10))).Added[0].Id;
        var seen = t.Board.Revision;
        Applied(t.Apply(new Command.Change(Ids(a), new GhostEdit.SetTint(Option<Tint>.None), Option<Revision>.None)));

        var refusal = Refused(t.Apply(MoveOf(t, (a, 50)) with { UnchangedSince = Option.Some(seen) }));

        Assert.Equal(ReasonCodes.ChangedSince, refusal.Why.Code);
        Assert.Equal(ValueList.Of(a), refusal.Ghosts);
        Assert.Equal(new Cell(10, 10, 10), At(t, a));
    }

    [Fact]
    public void A_plan_created_with_ghosts_holds_them_even_after_another_plan_made_earlier_in_the_batch()
    {
        var t = new TestBoard();
        var frame = t.Draft("StructureFrame", new Cell(10, 10, 10));

        var set = Applied(t.Apply(new Command.Batch(ValueList.From(new Command[]
        {
            new Command.CreatePlan(Draft("First")),
            new Command.CreatePlan(Draft("Second")) { Ghosts = ValueList.Of(frame) },
        }))));

        Assert.Equal(2, set.PlansOpened.Count);
        Assert.Equal(set.PlansOpened[1].Id, Assert.Single(set.Added).Plan);
    }

    [Fact]
    public void Every_policy_question_of_a_plan_created_with_ghosts_names_that_command()
    {
        var t = new TestBoard();
        var asked = new List<Command>();
        Assert.True(t.Policies.Install(new AccessPolicy((_, _) => true, (_, _) => true, request =>
        {
            asked.Add(request.Command);
            return Result<Unit, Reason>.Ok(Unit.Value);
        })).IsOk);
        var create = new Command.CreatePlan(Draft("Hall")) { Ghosts = ValueList.Of(t.Draft("StructureFrame", new Cell(10, 10, 10))) };

        Applied(t.Apply(create));

        Assert.NotEmpty(asked);
        Assert.All(asked, command => Assert.Same(create, command));
    }
}
