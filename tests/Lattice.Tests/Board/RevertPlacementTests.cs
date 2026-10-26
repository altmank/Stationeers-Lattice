using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using static Lattice.Tests.Board.TestBoard;

namespace Lattice.Tests.Board;

/// <summary>
/// Reverting a change that moved ghosts or changed their pieces puts each back where and as it was, and meets what
/// stands there now as a placement does: the reverter's own ghost is replaced, another actor's refuses.
/// </summary>
public sealed class RevertPlacementTests
{
    private static Placement FrameAt(TestBoard t, int x) => new(t.Catalogue.Named("StructureFrame").Key, new Cell(x, 10, 10), default);

    private static Command.Move MoveOf(TestBoard t, params (GhostId Id, int X)[] moves)
    {
        var to = new Dictionary<GhostId, Placement>();
        foreach (var (id, x) in moves)
            to[id] = FrameAt(t, x);
        return new Command.Move(ValueMap<GhostId, Placement>.From(to), Option<Revision>.None);
    }

    private static GhostId Frame(TestBoard t, int x, Actor? by = null) =>
        Applied(t.Apply(new Command.Place(ValueList.Of(t.Draft("StructureFrame", new Cell(x, 10, 10)))), by)).Added[0].Id;

    private static List<GhostId> At(TestBoard t, int x) => t.View.At(new Cell(x, 10, 10)).Select(static g => g.Id).ToList();

    private static Placement ShapeOf(TestBoard t, GhostId id) =>
        t.Board.Index.Ghosts[id].Shape.Match(static s => s.Placement, () => throw new Xunit.Sdk.XunitException("no shape"));

    [Fact]
    public void Reverting_a_move_puts_the_ghost_and_its_shape_back()
    {
        var t = new TestBoard();
        var a = Frame(t, 10);
        var moved = Applied(t.Apply(MoveOf(t, (a, 30))));

        Applied(t.Apply(new Command.Revert(moved.Id)));

        Assert.Equal(FrameAt(t, 10), ShapeOf(t, a));
        Assert.Equal(new[] { a }, At(t, 10));
        Assert.Empty(At(t, 30));
    }

    [Fact]
    public void Reverting_a_move_replaces_the_reverters_own_ghost_placed_on_the_old_spot_since()
    {
        var t = new TestBoard();
        var a = Frame(t, 10);
        var moved = Applied(t.Apply(MoveOf(t, (a, 30))));
        var since = Frame(t, 10);

        var reverted = Applied(t.Apply(new Command.Revert(moved.Id)));

        var removal = Assert.Single(reverted.Removed);
        Assert.Equal(since, removal.Ghost.Id);
        Assert.Equal(new RemovalCause.Replaced(a), removal.Cause);
        Assert.Equal(new[] { a }, At(t, 10));
    }

    [Fact]
    public void Reverting_a_move_onto_a_spot_another_actor_took_since_is_refused_changed_since()
    {
        var t = new TestBoard();
        var a = Frame(t, 10);
        var moved = Applied(t.Apply(MoveOf(t, (a, 30))));
        var bobs = Frame(t, 10, Bob);

        var refused = Refused(t.Apply(new Command.Revert(moved.Id)));

        Assert.Equal(ReasonCodes.ChangedSince, refused.Why.Code);
        Assert.Equal(new[] { bobs }, refused.Ghosts);
        Assert.Equal(FrameAt(t, 30), ShapeOf(t, a));
        Assert.Equal(new[] { bobs }, At(t, 10));
    }

    [Fact]
    public void Reverting_two_ghosts_that_traded_places_puts_both_back_and_replaces_neither()
    {
        var t = new TestBoard();
        var a = Frame(t, 10);
        var b = Frame(t, 30);
        var traded = Applied(t.Apply(MoveOf(t, (a, 30), (b, 10))));

        var reverted = Applied(t.Apply(new Command.Revert(traded.Id)));

        Assert.Empty(reverted.Removed);
        Assert.Equal(new[] { a }, At(t, 10));
        Assert.Equal(new[] { b }, At(t, 30));
    }
}
