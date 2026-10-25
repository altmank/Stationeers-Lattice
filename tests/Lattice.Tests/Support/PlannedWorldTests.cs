using Lattice.Api;
using Lattice.Core.Support;
using Lattice.Tests.Board;
using Lattice.Tests.Runs;
using Xunit;
using static Lattice.Tests.Runs.RunBench;

namespace Lattice.Tests.Support;

public sealed class PlannedWorldTests
{
    [Fact]
    public void Every_ghost_reads_as_done_builds_and_swaps_stand_removals_are_gone()
    {
        var bench = new RunBench();
        var kept = bench.Real("StructureCableStraight", At(0, 0, 0));
        var removed = bench.Real("StructureCableStraight", At(1, 0, 0));
        var swapped = bench.Real("StructureCableStraight", At(2, 0, 0));
        var corner = new Placement(bench.Catalogue.Named("StructureCableCorner").Key, At(2, 0, 0), default);
        bench.Boards.Placed(TestBoard.Draft(new Intent.Remove(removed)));
        bench.Boards.Placed(TestBoard.Draft(new Intent.Swap(swapped, corner, Option<Finish>.None)));
        bench.Ghost("StructureCableStraight", At(3, 0, 0));
        var world = new PlannedWorld(bench.Boards.View, bench.Query);

        Assert.Equal(kept, Assert.Single(world.At(At(0, 0, 0))).Ref);
        Assert.Empty(world.At(At(1, 0, 0)));
        var swap = Assert.Single(world.At(At(2, 0, 0)));
        Assert.Equal(corner.Piece, swap.Ref.Piece);
        Assert.True(world.AssumedFrom(swap.Ref).IsSome);
        Assert.True(world.NetworkOf(swap.Ref.Id).IsNone);
        Assert.True(world.Structure(swapped.Id).IsNone);
        var built = Assert.Single(world.At(At(3, 0, 0)));
        Assert.True(built.Ref.Id.Value < 0);
        Assert.Equal(built, world.Structure(built.Ref.Id).Match(s => s, () => null!));
    }
}
