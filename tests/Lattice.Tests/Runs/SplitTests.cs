using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.Core.Runs;
using Lattice.TestKit;
using Xunit;
using static Lattice.Tests.Runs.RunBench;

namespace Lattice.Tests.Runs;

public sealed class SplitTests
{
    /// <summary>A real 10-long straight with its pivot at the origin, running along -z (unturned) through ten cells.</summary>
    private static (RunBench Bench, Occupant Long) TenLong()
    {
        var bench = new RunBench();
        bench.Real("StructureCableStraight10", Origin);
        var occupant = bench.Around().OfKit(Step(Origin, Direction.ZMinus, 5), bench.Shapes(bench.Cable))!;
        return (bench, occupant);
    }

    private static ValueList<Choice> Split(RunBench bench, Occupant piece, Cell at, string join) =>
        TestKitIds.Must(SplitPlanner.Split(
            bench.Shapes(bench.Cable), piece, new Dictionary<Cell, Directions> { [at] = Ways(join) }, bench.Grid));

    [Fact]
    public void A_join_in_the_middle_of_a_10_long_straight_splits_it_with_the_T_at_the_join()
    {
        var (bench, piece) = TenLong();
        var join = Step(Origin, Direction.ZMinus, 5);

        var pieces = Split(bench, piece, join, "x+");

        Assert.Equal(10, pieces.Count);
        Assert.Equal(piece.Cells.ToHashSet(), pieces.Select(p => p.Placement.At).ToHashSet());
        var tee = Assert.Single(pieces, p => p.Placement.At == join);
        Assert.Equal("StructureCableJunction", bench.NameOf(tee.Placement.Piece));
        Assert.Equal(Ways("x+,z+,z-"), bench.WaysOf(tee.Placement, join));
        Assert.All(pieces.Where(p => p.Placement.At != join), p =>
        {
            Assert.Equal("StructureCableStraight", bench.NameOf(p.Placement.Piece));
            Assert.Equal(Ways("z+,z-"), bench.WaysOf(p.Placement, p.Placement.At));
        });
    }

    [Fact]
    public void Joins_from_both_sides_give_a_cross()
    {
        var (bench, piece) = TenLong();
        var join = Step(Origin, Direction.ZMinus, 3);

        var cross = Assert.Single(Split(bench, piece, join, "x+,x-"), p => p.Placement.At == join);

        Assert.Equal("StructureCableJunction4", bench.NameOf(cross.Placement.Piece));
    }

    [Fact]
    public void A_join_at_an_end_cell_keeps_the_open_end()
    {
        var (bench, piece) = TenLong();

        var tee = Assert.Single(Split(bench, piece, Origin, "y+"), p => p.Placement.At == Origin);

        Assert.Equal(Ways("y+,z+,z-"), bench.WaysOf(tee.Placement, Origin));
    }

    [Fact]
    public void A_modded_long_piece_splits_by_its_own_ends()
    {
        var bench = new RunBench();
        bench.Real("ModConduitLong4", Origin);
        var kit = bench.Shapes(bench.Conduit);
        var piece = bench.Around().OfKit(Step(Origin, Direction.YMinus, 2), kit)!;
        var join = Step(Origin, Direction.YMinus, 2);

        var pieces = TestKitIds.Must(SplitPlanner.Split(kit, piece, new Dictionary<Cell, Directions> { [join] = Ways("x+") }, bench.Grid));

        Assert.Equal(4, pieces.Count);
        var tee = Assert.Single(pieces, p => p.Placement.At == join);
        Assert.Equal("ModConduitTee", bench.NameOf(tee.Placement.Piece));
        Assert.Equal(Ways("x+,y+,y-"), bench.WaysOf(tee.Placement, join));
    }

    [Fact]
    public void A_join_the_kit_has_no_piece_for_is_refused()
    {
        var bench = new RunBench();
        bench.Real("ModConduitLong4", Origin);
        var kit = bench.Shapes(bench.Conduit);
        var piece = bench.Around().OfKit(Origin, kit)!;

        var refused = SplitPlanner.Split(kit, piece, new Dictionary<Cell, Directions> { [Origin] = Ways("x+,z+") }, bench.Grid);

        Assert.True(refused.TryGetError(out var why));
        Assert.Equal(ReasonCodes.NoPiece, why.Code);
    }
}
