using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.Core.Runs;
using Lattice.TestKit;
using Xunit;
using static Lattice.Tests.Runs.RunBench;

namespace Lattice.Tests.Runs;

public sealed class ChooserTests
{
    [Theory]
    [InlineData("z+,z-", "StructureCableStraight")]
    [InlineData("y+,y-", "StructureCableStraight")]
    [InlineData("x+,z+", "StructureCableCorner")]
    [InlineData("x-,y+", "StructureCableCorner")]
    [InlineData("x+,x-,z+", "StructureCableJunction")]
    [InlineData("x+,y+,z+", "StructureCableCorner3")]
    [InlineData("x+,x-,z+,z-", "StructureCableJunction4")]
    [InlineData("x+,x-,y+,z+", "StructureCableCorner4")]
    [InlineData("x+,x-,y+,y-,z+", "StructureCableJunction5")]
    [InlineData("x+,x-,y+,y-,z+,z-", "StructureCableJunction6")]
    public void Every_neighbour_pattern_picks_the_piece_whose_turned_ends_leave_those_ways(string pattern, string expected)
    {
        var bench = new RunBench();
        var ways = Ways(pattern);
        foreach (var side in Each(ways))
            bench.RealPointingInto(Origin, side);

        var choice = TestKitIds.Must(PieceChooser.Choose(bench.Shapes(bench.Cable), Origin, bench.Around()));

        Assert.Equal(expected, bench.NameOf(choice.Placement.Piece));
        Assert.Equal(Origin, choice.Placement.At);
        Assert.Equal(ways, bench.WaysOf(choice.Placement, Origin));
    }

    [Fact]
    public void A_lone_cell_takes_the_kits_straight()
    {
        var bench = new RunBench();

        var choice = TestKitIds.Must(PieceChooser.Choose(bench.Shapes(bench.Cable), Origin, bench.Around()));

        Assert.Equal("StructureCableStraight", bench.NameOf(choice.Placement.Piece));
    }

    [Fact]
    public void One_neighbour_gives_a_straight_towards_it()
    {
        var bench = new RunBench();
        bench.RealPointingInto(Origin, Direction.XPlus);

        var choice = TestKitIds.Must(PieceChooser.Choose(bench.Shapes(bench.Cable), Origin, bench.Around()));

        Assert.Equal(Ways("x+,x-"), bench.WaysOf(choice.Placement, Origin));
    }

    [Fact]
    public void Planned_and_built_neighbours_both_count()
    {
        var bench = new RunBench();
        bench.RealPointingInto(Origin, Direction.XPlus);
        bench.Ghost("StructureCableStraight", Step(Origin, Direction.ZPlus));

        var choice = TestKitIds.Must(PieceChooser.Choose(bench.Shapes(bench.Cable), Origin, bench.Around()));

        Assert.Equal(Ways("x+,z+"), bench.WaysOf(choice.Placement, Origin));
    }

    [Fact]
    public void A_neighbour_planned_for_removal_does_not_count()
    {
        var bench = new RunBench();
        var doomed = bench.RealPointingInto(Origin, Direction.XPlus);
        bench.RealPointingInto(Origin, Direction.ZPlus);
        bench.Boards.Placed(Lattice.Tests.Board.TestBoard.Draft(new Intent.Remove(doomed)));

        var choice = TestKitIds.Must(PieceChooser.Choose(bench.Shapes(bench.Cable), Origin, bench.Around()));

        Assert.Equal(Ways("z+,z-"), bench.WaysOf(choice.Placement, Origin));
    }

    [Fact]
    public void Neighbours_of_another_network_kind_or_not_pointing_in_do_not_count()
    {
        var bench = new RunBench();
        bench.Real("StructurePipeStraight", Step(Origin, Direction.XPlus), bench.Sending(Direction.ZPlus, Direction.XPlus));
        bench.Real("StructureCableStraight", Step(Origin, Direction.YPlus), bench.Sending(Direction.ZPlus, Direction.XPlus));
        bench.RealPointingInto(Origin, Direction.ZMinus);

        var choice = TestKitIds.Must(PieceChooser.Choose(bench.Shapes(bench.Cable), Origin, bench.Around()));

        Assert.Equal(Ways("z+,z-"), bench.WaysOf(choice.Placement, Origin));
    }

    [Theory]
    [InlineData("y+,y-", "ModConduitLine")]
    [InlineData("x-,z+", "ModConduitBend")]
    [InlineData("x+,x-,z+", "ModConduitTee")]
    [InlineData("y+,y-,z+,z-", "ModConduitPlus")]
    public void A_modded_kit_with_other_local_ends_picks_by_its_ends(string pattern, string expected)
    {
        var bench = new RunBench();
        var ways = Ways(pattern);
        foreach (var side in Each(ways))
            bench.ConduitPointingInto(Origin, side);

        var choice = TestKitIds.Must(PieceChooser.Choose(bench.Shapes(bench.Conduit), Origin, bench.Around()));

        Assert.Equal(expected, bench.NameOf(choice.Placement.Piece));
        Assert.Equal(ways, bench.WaysOf(choice.Placement, Origin));
    }

    [Fact]
    public void A_shape_the_kit_lacks_leaves_the_neighbours_open()
    {
        var bench = new RunBench();
        bench.ConduitPointingInto(Origin, Direction.YPlus);
        bench.ConduitPointingInto(Origin, Direction.ZPlus);

        var choice = TestKitIds.Must(PieceChooser.Choose(bench.Shapes(bench.Conduit), Origin, Ways("x+,x-"), bench.Around()));

        Assert.Equal(Ways("x+,x-"), bench.WaysOf(choice.Placement, Origin));
    }

    [Fact]
    public void A_needed_shape_the_kit_lacks_is_refused_with_no_piece()
    {
        var bench = new RunBench();

        var refused = PieceChooser.Choose(bench.Shapes(bench.Conduit), Origin, Ways("x+,y+,z+"), bench.Around());

        Assert.True(refused.TryGetError(out var why));
        Assert.Equal(ReasonCodes.NoPiece, why.Code);
        Assert.Equal("x+,y+,z+", why.Facts["shape"]);
    }

    [Fact]
    public void A_kit_with_no_run_pieces_is_refused()
    {
        var bench = new RunBench();

        var refused = new PieceChooser(bench.Catalogue, bench.Grid).Kit(bench.KitOf("StructureTankSmall"));

        Assert.True(refused.TryGetError(out var why));
        Assert.Equal(ReasonCodes.NoPiece, why.Code);
    }
}
