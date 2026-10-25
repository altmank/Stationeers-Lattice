using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Openings;
using Lattice.Core.Ports;
using Lattice.Core.Support;
using Lattice.Tests.Routes;
using Xunit;
using static Lattice.Tests.Routes.RouteBench;

namespace Lattice.Tests.Openings;

public sealed class KeepOutTests
{
    private static OpeningKeepOuts KeepOuts(RouteBench bench) =>
        new(new PlannedWorld(bench.Runs.Boards.View, bench.Runs.Query), new Shapes(bench.Catalogue, bench.Runs.Grid), bench.Openings, OpeningBands.Default);

    [Theory]
    [InlineData(160, 110, 110, true)]
    [InlineData(150, 105, 100, true)]
    [InlineData(170, 120, 120, true)]
    [InlineData(175, 110, 105, false)]
    [InlineData(160, 125, 110, false)]
    public void A_doors_keep_out_is_its_face_rectangle_one_metre_each_side(int x, int y, int z, bool held)
    {
        var bench = new RouteBench();
        bench.Runs.Real("ModHatch", Large(2, 0, 0));
        var keepOuts = KeepOuts(bench);

        var hit = keepOuts.At(new Cell(x, y, z));

        Assert.Equal(held && !(x == 160 && y == 110 && z >= 110), hit.IsSome);
        if (hit.TryGet(out var found))
            Assert.Equal(OpeningKind.Door, found.Kind);
    }

    [Fact]
    public void A_doors_port_and_the_cells_in_front_of_it_are_released()
    {
        var bench = new RouteBench();
        bench.Runs.Real("ModHatch", Large(2, 0, 0));
        var keepOuts = KeepOuts(bench);

        Assert.True(keepOuts.At(new Cell(160, 110, 110)).IsNone);
        Assert.True(keepOuts.At(new Cell(160, 110, 120)).IsNone);
        Assert.True(keepOuts.At(new Cell(160, 110, 105)).IsSome);
    }

    [Fact]
    public void A_window_counts_on_a_wall_half_a_metre_each_side_and_not_on_a_floor()
    {
        var bench = new RouteBench();
        bench.Runs.Real("StructureCompositeWindowIron", Large(2, 0, 0));
        bench.Runs.Real("StructureCompositeWindowIron", Large(2, 0, 4), bench.Sending(Direction.ZMinus, Direction.YMinus));
        var keepOuts = KeepOuts(bench);

        Assert.Equal(OpeningKind.Window, keepOuts.At(new Cell(150, 110, 105)).Match(h => h.Kind, () => default));
        Assert.True(keepOuts.At(new Cell(150, 110, 90)).IsNone);
        Assert.True(keepOuts.At(new Cell(150, 100, 190)).IsNone);
    }

    [Fact]
    public void A_planned_door_keeps_its_space_and_a_removed_one_does_not()
    {
        var bench = new RouteBench();
        bench.Runs.Ghost("ModHatch", Large(2, 0, 0));
        var removed = bench.Runs.Real("ModHatch", Large(2, 0, 3));
        bench.Runs.Boards.Placed(Lattice.Tests.Board.TestBoard.Draft(new Intent.Remove(removed)));
        var keepOuts = KeepOuts(bench);

        Assert.True(keepOuts.At(new Cell(160, 110, 105)).IsSome);
        Assert.True(keepOuts.At(new Cell(160, 110, 165)).IsNone);
    }
}
