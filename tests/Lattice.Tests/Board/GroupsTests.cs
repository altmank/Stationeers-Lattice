using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.TestKit;
using Lattice.TestKit.Allocations;

namespace Lattice.Tests.Board;

/// <summary>
/// <see cref="IGhostBoard.Groups"/>: every group with a ghost the viewer may see and how many, ordered by name, read
/// from the board's group index; cached per revision, so an unchanged board answers with no allocation, and a change
/// shows as a new revision and a Changed event.
/// </summary>
public sealed class GroupsTests
{
    private static readonly GroupName Hydro = TestKitIds.Must(GroupName.Create("Hydro"));
    private static readonly GroupName Air = TestKitIds.Must(GroupName.Create("Air"));
    private static readonly GroupName Power = TestKitIds.Must(GroupName.Create("Power"));

    private static GhostDraft InGroups(TestBoard t, string piece, Cell at, params GroupName[] groups) =>
        TestBoard.Draft(t.Draft(piece, at).Intent, default, ValueSet.From(groups));

    [Fact]
    public void Groups_name_each_group_with_its_ghost_count_ordered_by_name()
    {
        var t = new TestBoard();
        t.Placed(
            InGroups(t, "StructureCableStraight", new Cell(5, 5, 5), Hydro),
            InGroups(t, "StructureCableStraight", new Cell(5, 5, 10), Hydro, Air),
            InGroups(t, "StructureCableStraight", new Cell(5, 5, 15)),
            InGroups(t, "StructureCableStraight", new Cell(5, 5, 20), Hydro));

        Assert.Equal(new[] { new GroupCount(Air, 1), new GroupCount(Hydro, 3) }, t.View.Groups.ToArray());
    }

    [Fact]
    public void An_empty_board_has_no_groups()
    {
        Assert.Empty(new TestBoard().View.Groups);
    }

    [Fact]
    public void Joining_and_leaving_a_group_changes_the_revision_raises_changed_and_the_counts()
    {
        var t = new TestBoard();
        var ghost = t.Placed(InGroups(t, "StructureCableStraight", new Cell(5, 5, 5), Hydro)).Added[0];
        var view = t.View;
        var before = view.Groups;
        var revision = view.Revision;
        t.Events.Flush();
        var changed = new List<ChangeSet>();
        t.Events.Changed += changed.Add;

        t.Apply(new Command.Change(TestBoard.Ids(ghost.Id), new GhostEdit.JoinGroup(Power), Option<Revision>.None));
        t.Events.Flush();

        Assert.NotEqual(revision, view.Revision);
        Assert.Equal(ghost.Id, Assert.Single(Assert.Single(changed).Changed).After.Id);
        Assert.Equal(new[] { new GroupCount(Hydro, 1), new GroupCount(Power, 1) }, view.Groups.ToArray());
        Assert.Equal(new[] { new GroupCount(Hydro, 1) }, before.ToArray());

        t.Apply(new Command.Change(TestBoard.Ids(ghost.Id), new GhostEdit.LeaveGroup(Hydro), Option<Revision>.None));

        Assert.Equal(new[] { new GroupCount(Power, 1) }, view.Groups.ToArray());
    }

    [Fact]
    public void Removing_a_groups_last_ghost_drops_the_group()
    {
        var t = new TestBoard();
        var ghost = t.Placed(InGroups(t, "StructureCableStraight", new Cell(5, 5, 5), Hydro)).Added[0];

        t.Apply(new Command.Remove(TestBoard.Ids(ghost.Id)));

        Assert.Empty(t.View.Groups);
    }

    [Fact]
    public void Groups_count_only_the_ghosts_the_viewer_may_see()
    {
        var t = new TestBoard();
        t.Placed(InGroups(t, "StructureCableStraight", new Cell(5, 5, 5), Hydro), InGroups(t, "StructureCableStraight", new Cell(5, 5, 10), Hydro, Air));
        t.Placed(InGroups(t, "StructureCableStraight", new Cell(5, 5, 15), Air));
        var firstPlaced = t.View.Select(new Selector.Group(Hydro)).Match(static g => g[0].Id, static _ => default);
        t.Policies.Install(new AccessPolicy((viewer, ghost) => viewer == TestBoard.Alice || ghost.Id == firstPlaced, (_, _) => true, _ => Result<Unit, Reason>.Ok(Unit.Value)));

        Assert.Equal(new[] { new GroupCount(Air, 2), new GroupCount(Hydro, 2) }, t.View.Groups.ToArray());
        Assert.Equal(new[] { new GroupCount(Hydro, 1) }, t.View.As(TestBoard.Bob).Groups.ToArray());
    }

    [Fact]
    public void Reading_groups_on_an_unchanged_board_allocates_nothing()
    {
        var t = new TestBoard();
        t.Placed(InGroups(t, "StructureCableStraight", new Cell(5, 5, 5), Hydro, Air));
        var view = t.View;

        Allocations.AssertNone(() => _ = view.Groups, "Groups");
        t.Policies.Install(new AccessPolicy((_, _) => true, (_, _) => true, _ => Result<Unit, Reason>.Ok(Unit.Value)));
        Allocations.AssertNone(() => _ = view.Groups, "Groups with a policy");
    }

    [Fact]
    public void Equal_group_counts_are_equal()
    {
        Assert.Equal(new GroupCount(Hydro, 2), new GroupCount(Hydro, 2));
        Assert.Equal(new GroupCount(Hydro, 2).GetHashCode(), new GroupCount(Hydro, 2).GetHashCode());
        Assert.NotEqual(new GroupCount(Hydro, 2), new GroupCount(Hydro, 3));
    }
}
