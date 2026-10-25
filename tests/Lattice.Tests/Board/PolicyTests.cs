using System;
using System.Collections.Generic;
using Lattice.Api;
using static Lattice.Tests.Board.TestBoard;

namespace Lattice.Tests.Board;

public sealed class PolicyTests
{
    private static readonly Cell At = new(100, 100, 100);
    private static readonly ReasonCode OnlyTheOwner = Lattice.TestKit.TestKitIds.Must(ReasonCode.Create(Lattice.TestKit.TestKitIds.Mod, "only-the-owner"));
    private static readonly GroupName Group = Lattice.TestKit.TestKitIds.Must(GroupName.Create("Hydro"));

    private static AccessPolicy Policy(
        Func<Actor, Ghost, bool>? maySee = null,
        Func<AccessRequest, Result<Unit, Reason>>? mayChange = null) => new(
        maySee ?? ((_, _) => true),
        (_, _) => true,
        mayChange ?? (_ => Result<Unit, Reason>.Ok(Unit.Value)));

    private static Result<Unit, Reason> OwnerOnly(AccessRequest request) =>
        request.Ghost.TryGet(out var ghost) && ghost.PlacedBy != request.Actor
            ? Result<Unit, Reason>.Error(Reason.Of(OnlyTheOwner, RuleId.Contract))
            : Result<Unit, Reason>.Ok(Unit.Value);

    private static Command Relabel(GhostId id) =>
        new Command.Change(Ids(id), new GhostEdit.SetLabel(Option<Label>.None), Option<Revision>.None);

    [Fact]
    public void A_policy_that_refuses_a_players_edit_refuses_it_with_its_own_reason_and_changes_nothing()
    {
        var t = new TestBoard();
        var ghost = Assert.Single(t.Placed(t.Draft("StructureCableStraight", At)).Added);
        t.Events.Flush();
        Assert.True(t.Policies.Install(Policy(mayChange: OwnerOnly)).IsOk);
        var revision = t.Board.Revision;

        var refusal = Refused(t.Apply(Relabel(ghost.Id), Bob));

        Assert.Equal(OnlyTheOwner, refusal.Why.Code);
        Assert.Equal(ValueList.Of(ghost.Id), refusal.Ghosts);
        Assert.Equal(revision, t.Board.Revision);
        Assert.Equal(0, t.Events.Waiting);
        Assert.True(t.View.Find(ghost.Id).TryGet(out var unchanged) && unchanged == ghost);
    }

    [Fact]
    public void The_default_policy_allows_every_actor()
    {
        var t = new TestBoard();
        var ghost = Assert.Single(t.Placed(t.Draft("StructureCableStraight", At)).Added);

        Assert.IsType<Outcome.Applied>(t.Apply(Relabel(ghost.Id), Bob));
    }

    [Fact]
    public void A_policy_that_throws_leaves_the_board_unchanged()
    {
        var t = new TestBoard();
        var ghost = Assert.Single(t.Placed(t.Draft("StructureCableStraight", At)).Added);
        Assert.True(t.Policies.Install(Policy(mayChange: _ => throw new InvalidOperationException("policy bug"))).IsOk);
        var revision = t.Board.Revision;

        Assert.Throws<InvalidOperationException>(() => t.Apply(new Command.Remove(Ids(ghost.Id))));

        Assert.Equal(revision, t.Board.Revision);
        Assert.True(t.View.Find(ghost.Id).IsSome);
    }

    [Fact]
    public void Place_asks_once_per_target_plan_and_once_per_ghost_it_replaces()
    {
        var t = new TestBoard();
        var old = Assert.Single(t.Placed(t.Draft("StructureCableStraight", At)).Added);
        var asked = new List<AccessRequest>();
        t.Policies.Install(Policy(mayChange: request =>
        {
            asked.Add(request);
            return Result<Unit, Reason>.Ok(Unit.Value);
        }));

        t.Placed(t.Draft("StructureCableCorner", At), t.Draft("StructureCableCorner", new Cell(200, 100, 100)));

        Assert.Equal(2, asked.Count);
        Assert.True(asked[0].Ghost.IsNone);
        Assert.Equal(t.Board.ViewFor(Alice).Find(old.Id), Option<Ghost>.None);
        Assert.True(asked[1].Ghost.TryGet(out var replaced) && replaced.Id == old.Id);
    }

    [Fact]
    public void Reads_hide_what_the_policy_hides_from_a_viewer()
    {
        var t = new TestBoard();
        var ghost = Assert.Single(t.Placed(t.Draft("StructureCableStraight", At)).Added);
        t.Policies.Install(Policy(maySee: (viewer, g) => viewer == g.PlacedBy));

        var bob = t.View.As(Bob);

        Assert.True(bob.Find(ghost.Id).IsNone);
        Assert.Empty(bob.At(At));
        Assert.True(bob.Select(Ids(ghost.Id)).TryGet(out var selected) && selected.Count == 0);
        Assert.Single(t.View.At(At));
    }

    [Fact]
    public void A_command_selector_runs_on_the_actors_view_and_names_no_hidden_ghost()
    {
        var t = new TestBoard();
        var groups = ValueSet.Of(Group);
        var cable = t.Draft("StructureCableStraight", At) with { Groups = groups };
        t.Placed(cable);
        t.Policies.Install(Policy(maySee: (viewer, g) => viewer == g.PlacedBy));

        var refusal = Refused(t.Apply(new Command.Remove(new Selector.Group(Group)), Bob));

        Assert.Equal(ReasonCodes.NotFound, refusal.Why.Code);
        Assert.Empty(refusal.Ghosts);
        Assert.Single(t.View.At(At));
    }

    [Fact]
    public void A_policy_that_says_its_answers_changed_drops_the_cached_reads()
    {
        var t = new TestBoard();
        t.Placed(t.Draft("StructureCableStraight", At));
        var hidden = false;
        Assert.True(t.Policies.Install(Policy(maySee: (_, _) => !hidden)).TryGet(out var handle));
        Assert.Single(t.View.At(At));

        hidden = true;
        Assert.Single(t.View.At(At));
        handle.Changed();

        Assert.Empty(t.View.At(At));
    }

    [Fact]
    public void A_second_policy_is_refused_and_disposing_restores_allow_everything()
    {
        var t = new TestBoard();
        Assert.True(t.Policies.Install(Policy(maySee: (_, _) => false)).TryGet(out var handle));
        Assert.True(t.Policies.Install(Policy()).TryGetError(out var refused));
        Assert.Equal(ReasonCodes.AlreadyRegistered, refused.Code);

        handle.Dispose();

        Assert.True(t.Policies.AllowsEverything);
    }
}
