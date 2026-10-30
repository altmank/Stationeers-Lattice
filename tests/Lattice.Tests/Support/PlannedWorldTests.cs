using Lattice.Api;
using Lattice.Core.Board;
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

    [Fact]
    public void With_shapes_a_planned_piece_stands_in_every_cell_it_fills_as_a_built_one_does()
    {
        var bench = new RunBench();
        bench.Ghost("ModConduitLong4", At(0, 0, 0));
        bench.Real("ModConduitLong4", At(4, 0, 0));
        var footprints = new PlannedWorld(bench.Boards.View, bench.Query, shapes: new Shapes(bench.Catalogue, bench.Grid));
        var ownCells = new PlannedWorld(bench.Boards.View, bench.Query);

        for (var y = 0; y >= -3; y--)
        {
            Assert.True(Assert.Single(footprints.At(At(0, y, 0))).Ref.Id.Value < 0);
            Assert.Single(footprints.At(At(4, y, 0)));
            Assert.Single(ownCells.At(At(4, y, 0)));
        }

        Assert.Single(ownCells.At(At(0, 0, 0)));
        Assert.Empty(ownCells.At(At(0, -2, 0)));
        Assert.Empty(footprints.At(At(0, 1, 0)));
    }
}
