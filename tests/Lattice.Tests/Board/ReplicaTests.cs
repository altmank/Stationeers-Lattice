using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Sync;
using Lattice.TestKit;
using static Lattice.Tests.Board.TestBoard;

namespace Lattice.Tests.Board;

/// <summary>
/// A client's board as sync drives it (<see cref="IReplicaBoard"/>): it takes the host's change sets as they are and
/// the host's ghosts at join, with no policy asked and no ids of its own.
/// </summary>
public sealed class ReplicaTests
{
    private static readonly Cell At = new(100, 100, 100);
    private static readonly Cell Next = new(120, 100, 100);
    private static readonly PlanName Store = TestKitIds.Must(PlanName.Create("North store"));

    private static Command CreatePlan() => new Command.CreatePlan(new PlanDraft(
        Store, TestMaker, new Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty));

    private static Dictionary<GhostId, Ghost> GhostsOf(GhostBoard board) =>
        board.Index.Ghosts.Values.ToDictionary(e => e.Id, e => e.Ghost);

    private static Dictionary<PlanId, Plan> PlansOf(GhostBoard board) =>
        board.Index.Plans.Values.ToDictionary(e => e.Plan.Id, e => e.Plan);

    private static void AssertSame(TestBoard host, TestBoard client)
    {
        Assert.Equal(host.Board.Revision, client.Board.Revision);
        Assert.Equal(GhostsOf(host.Board), GhostsOf(client.Board));
        Assert.Equal(PlansOf(host.Board), PlansOf(client.Board));
    }

    [Fact]
    public void A_client_that_mirrors_every_host_change_set_holds_what_the_host_holds()
    {
        var host = new TestBoard();
        var client = new TestBoard();
        IReplicaBoard replica = client.Board;
        var sets = new List<ChangeSet>();

        var plan = Applied(host.Apply(CreatePlan()));
        sets.Add(plan);
        var into = plan.PlansOpened[0].Id;
        var placed = host.Placed(host.Draft("StructureCableStraight", At, plan: Option.Some(into)), host.Draft("StructureCableStraight", Next));
        sets.Add(placed);
        sets.Add(Applied(host.Apply(new Command.Change(Ids(placed.Added[0].Id), new GhostEdit.SetLabel(Option.Some(TestKitIds.Must(Label.Create("east")))), Option<Revision>.None))));
        sets.Add(Applied(host.Apply(new Command.Remove(Ids(placed.Added[1].Id)))));
        sets.Add(TestKitIds.Must(host.Board.MarkStale(placed.Added[0].Id, Reason.Of(ReasonCodes.NotFound, LatticeIdentity.Board))));
        sets.Add(Applied(host.Apply(new Command.FinishPlan(into))));

        foreach (var set in sets)
            Assert.Equal(set, TestKitIds.Must(replica.Mirror(set)));

        AssertSame(host, client);
    }

    [Fact]
    public void A_mirrored_set_raises_changed_with_the_host_set_and_keeps_its_ids()
    {
        var host = new TestBoard();
        var client = new TestBoard();
        var heard = new List<ChangeSet>();
        client.Events.Changed += heard.Add;
        var set = host.Placed(host.Draft("StructureCableStraight", At));

        TestKitIds.Must(((IReplicaBoard)client.Board).Mirror(set));
        client.Events.Flush();

        Assert.Equal(set, Assert.Single(heard));
        Assert.Equal(Option.Some(set.Added[0]), ((IReplicaBoard)client.Board).Ghost(set.Added[0].Id));
    }

    [Fact]
    public void A_set_that_is_not_the_next_revision_is_refused_and_changes_nothing()
    {
        var host = new TestBoard();
        var client = new TestBoard();
        host.Placed(host.Draft("StructureCableStraight", At));
        var second = host.Placed(host.Draft("StructureCableStraight", Next));

        var mirrored = ((IReplicaBoard)client.Board).Mirror(second);

        Assert.True(mirrored.IsError);
        Assert.Equal(new Revision(0), client.Board.Revision);
        Assert.Empty(client.Board.Index.Ghosts);
        Assert.Equal(0, client.Events.Waiting);
    }

    [Fact]
    public void A_set_that_changes_a_ghost_the_client_lacks_is_refused_and_changes_nothing()
    {
        var host = new TestBoard();
        var client = new TestBoard();
        var placed = host.Placed(host.Draft("StructureCableStraight", At));
        var removed = Applied(host.Apply(new Command.Remove(Ids(placed.Added[0].Id))));
        var skipped = removed with { Revision = new Revision(1) };

        var mirrored = ((IReplicaBoard)client.Board).Mirror(skipped);

        Assert.True(mirrored.IsError);
        Assert.Equal(new Revision(0), client.Board.Revision);
    }

    [Fact]
    public void Mirroring_asks_no_access_policy()
    {
        var host = new TestBoard();
        var client = new TestBoard();
        Assert.True(client.Policies.Install(new AccessPolicy(
            static (_, _) => true, static (_, _) => true,
            static _ => Result<Unit, Reason>.Error(Reason.Of(ReasonCodes.NotAllowed, LatticeIdentity.Board)))).IsOk);
        var set = host.Placed(host.Draft("StructureCableStraight", At));

        Assert.True(((IReplicaBoard)client.Board).Mirror(set).IsOk);
        Assert.Single(client.Board.Index.Ghosts);
    }

    [Fact]
    public void Reset_replaces_everything_with_the_host_board_and_raises_board_reset()
    {
        var host = new TestBoard();
        var plan = Applied(host.Apply(CreatePlan())).PlansOpened[0].Id;
        host.Placed(host.Draft("StructureCableStraight", At, plan: Option.Some(plan)));
        var client = new TestBoard();
        client.Placed(client.Draft("StructureCableStraight", Next));
        var resets = 0;
        client.Events.BoardReset += () => resets++;

        ((IReplicaBoard)client.Board).Reset(new BoardSeed(
            host.Board.Revision, new IdCounters(10, 10, 10), ValueList.From(PlansOf(host.Board).Values), ValueList.From(GhostsOf(host.Board).Values)));
        client.Events.Flush();

        AssertSame(host, client);
        Assert.Equal(1, resets);
        Assert.Empty(client.Board.Index.InCell(Next));
    }

    [Fact]
    public void After_a_reset_the_client_mirrors_the_host_sets_that_follow()
    {
        var host = new TestBoard();
        host.Placed(host.Draft("StructureCableStraight", At));
        var client = new TestBoard();
        ((IReplicaBoard)client.Board).Reset(new BoardSeed(
            host.Board.Revision, new IdCounters(10, 10, 10), ValueList.From(PlansOf(host.Board).Values), ValueList.From(GhostsOf(host.Board).Values)));

        var next = host.Placed(host.Draft("StructureCableStraight", Next));

        Assert.True(((IReplicaBoard)client.Board).Mirror(next).IsOk);
        AssertSame(host, client);
    }
}
