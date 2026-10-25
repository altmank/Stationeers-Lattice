using System;
using Lattice.Api;
using Lattice.Testing.Stubs;

namespace Lattice.Consumer.Tests;

/// <summary>
/// Fakes written as a consumer writes them: inherit a stub, override only what the test reads. These classes are never
/// edited when a minor adds members to the interfaces they fake; they keep compiling because the stubs gain each member
/// in the same release.
/// </summary>
public sealed class StubbedFakeFlows
{
    private static readonly Actor Me = new Actor.Player(new ClientId(7));

    private static readonly Plan Hall = new(
        new PlanId(3), new Revision(1), PlanName.Create("Hall").Match(static n => n, static _ => default), Me,
        Maker.Create(ModId.Create("consumer").Match(static m => m, static _ => default), "tool").Match(static m => m, static _ => default),
        new Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty);

    /// <summary>The plans a planner UI lists: only <see cref="IPlans.All"/> is set up.</summary>
    private sealed class PlansFake : StubPlans
    {
        public override ValueList<Plan> All => ValueList.Of(Hall);
    }

    /// <summary>A board with one ghost in one cell: only <see cref="IGhostBoard.Revision"/> and <see cref="IGhostBoard.At"/> are set up.</summary>
    private sealed class BoardFake(Ghost ghost) : StubGhostBoard
    {
        public override Revision Revision => new(5);

        public override ValueList<Ghost> At(Cell cell) => cell == ghost.Intent switch
        {
            Intent.Build build => build.Target.At,
            _ => default,
        } ? ValueList.Of(ghost) : ValueList<Ghost>.Empty;
    }

    /// <summary>The consumer's own code under test: the line a planner shows for the cell under the cursor.</summary>
    private static string Describe(IPlans plans, IGhostBoard board, Cell cursor)
    {
        foreach (var ghost in board.At(cursor))
            foreach (var plan in plans.All)
                if (plan.Id == ghost.Plan)
                    return $"{plan.Name.Value} (rev {board.Revision.Value})";
        return "nothing planned here";
    }

    [Fact]
    public void A_fake_that_inherits_a_stub_answers_what_it_overrides()
    {
        var at = new Cell(10, 10, 10);
        var ghost = new Ghost(
            new GhostId(1), Hall.Id, new Revision(5), new Intent.Build(new Placement(new PrefabKey(2001), at, Turn.None), Option<Finish>.None),
            new GhostState.Planned(), Me, Hall.MadeBy, DateTimeOffset.UnixEpoch, new Visibility.Everyone(),
            Option<Label>.None, Option<Tint>.None, ValueSet<GroupName>.Empty, ValueMap<AttachmentKey, Attachment>.Empty);

        Assert.Equal("Hall (rev 5)", Describe(new PlansFake(), new BoardFake(ghost), at));
        Assert.Equal("nothing planned here", Describe(new PlansFake(), new BoardFake(ghost), new Cell(30, 10, 10)));
    }

    [Fact]
    public void A_member_the_fake_did_not_set_up_fails_loudly_naming_itself()
    {
        var failure = Assert.Throws<NotSupportedException>(() => new PlansFake().Find(Hall.Id));

        Assert.Equal("IPlans.Find is not set up in StubPlans: override it", failure.Message);
    }
}
