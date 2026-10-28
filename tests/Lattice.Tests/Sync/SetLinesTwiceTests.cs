using System.Linq;
using Lattice.Api;
using Lattice.Core.Sync;

namespace Lattice.Tests.Sync;

public sealed class SetLinesTwiceTests
{
    [Fact]
    public void A_set_that_removes_a_ghost_and_adds_it_again_reads_both()
    {
        var bench = new SyncBench();
        var (_, client) = bench.Join(connection: 1, player: 7);
        bench.Host.Submit(new Command.Place(ValueList.Of(bench.Draft("StructureCableStraight", new Lattice.Api.Cell(5, 5, 5)))));
        bench.Frames(3);
        var ghost = Assert.Single(client.Ghosts);
        var next = new Revision(client.Revision.Value + 1);
        var back = ghost with { Revision = next, Label = Label.Create("again").ToOption() };
        var set = client.Mirrored.Last() with
        {
            Revision = next,
            Added = ValueList.Of(back),
            Changed = ValueList<GhostChange>.Empty,
            Removed = ValueList.Of(new GhostRemoval(ghost, new RemovalCause.Removed())),
            PlansOpened = ValueList<Plan>.Empty,
            PlansChanged = ValueList<PlanChange>.Empty,
            PlansFinished = ValueList<PlanId>.Empty,
        };

        var read = SetLines.Read(SetLines.Write(set), client);

        Assert.True(read.TryGet(out var got), read.TryGetError(out var why) ? why.ToString() : "");
        Assert.Equal(back, Assert.Single(got.Added));
        Assert.Equal(ghost, Assert.Single(got.Removed).Ghost);
        Assert.Empty(got.Changed);
    }
}
