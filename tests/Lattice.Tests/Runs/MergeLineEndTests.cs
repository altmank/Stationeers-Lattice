using System.Linq;
using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.Core.Runs;
using Xunit;
using static Lattice.Tests.Runs.RunBench;

namespace Lattice.Tests.Runs;

/// <summary>
/// A run that leaves a line's end side-on turns the end piece into a corner: the end's open face joins nothing, so it is
/// dropped rather than kept as a stub of a tee. A face that joins something is always kept.
/// </summary>
public sealed class MergeLineEndTests
{
    private const string ChuteCorner = """
        lattice-catalogue 3
        piece StructureChuteCorner kit=ItemKitChute option=2 grid=small
          step material=ItemKitChute*1 exit=ItemWrench air=no
          end 0 0 0 x+ chute
          end 0 0 0 z+ chute
          cell 0 0 0 chute
          collision 23
        """;

    private static readonly Cell End = At(2, 0, 0);

    private static readonly Cell Away = At(2, 0, 3);

    /// <summary>A three-cell line along x from the origin to <see cref="End"/>, its open end facing x+.</summary>
    private static StructureRef[] RealLine(RunBench bench, string straight, Direction along) =>
        [.. Enumerable.Range(0, 3).Select(x => bench.Real(straight, At(x, 0, 0), bench.Sending(along, Direction.XPlus)))];

    private static Placement PieceAt(RunBench bench, RunPlan plan, Cell cell) =>
        Assert.Single(plan.Pieces, p => bench.CellsOf(p).Contains(cell));

    [Fact]
    public void A_chute_run_leaving_a_planned_lines_end_lays_a_corner_there()
    {
        var bench = new RunBench(ChuteCorner);
        var turn = bench.Sending(Direction.XPlus, Direction.XPlus);
        for (var x = 0; x < 3; x++)
            bench.Ghost("StructureChuteStraight", At(x, 0, 0), turn);

        var plan = bench.Plan(bench.Run(bench.KitOf("StructureChuteStraight"), End, Away));

        Assert.False(plan.FirstFailure.IsSome);
        var corner = PieceAt(bench, plan, End);
        Assert.Equal("StructureChuteCorner", bench.NameOf(corner.Piece));
        Assert.Equal(Ways("x-,z+"), bench.WaysOf(corner, End));
        Assert.DoesNotContain(plan.Pieces, p => bench.CellsOf(p).Contains(At(1, 0, 0)));
    }

    [Theory]
    [InlineData("StructureCableStraight", "StructureCableCorner")]
    [InlineData("StructurePipeStraight", "StructurePipeCorner")]
    public void A_run_leaving_a_built_lines_end_swaps_it_for_a_corner_not_a_tee(string straight, string corner)
    {
        var bench = new RunBench();
        var line = RealLine(bench, straight, Direction.ZPlus);

        var plan = bench.Plan(bench.Run(bench.KitOf(straight), End, Away));

        Assert.False(plan.FirstFailure.IsSome);
        var swap = Assert.Single(plan.Changes.OfType<Intent.Swap>());
        Assert.Equal(line[2], swap.Old);
        Assert.Equal(corner, bench.NameOf(swap.New.Piece));
        Assert.Equal(Ways("x-,z+"), bench.WaysOf(swap.New, End));
        Assert.DoesNotContain(plan.Changes, c => c is Intent.Remove);
        Assert.DoesNotContain(plan.Pieces, p => bench.CellsOf(p).Contains(At(1, 0, 0)));
    }

    [Fact]
    public void A_chute_run_leaving_a_built_lines_end_swaps_it_for_a_corner()
    {
        var bench = new RunBench(ChuteCorner);
        var line = RealLine(bench, "StructureChuteStraight", Direction.XPlus);

        var plan = bench.Plan(bench.Run(bench.KitOf("StructureChuteStraight"), End, Away));

        Assert.False(plan.FirstFailure.IsSome);
        var swap = Assert.Single(plan.Changes.OfType<Intent.Swap>());
        Assert.Equal(line[2], swap.Old);
        Assert.Equal("StructureChuteCorner", bench.NameOf(swap.New.Piece));
    }

    [Fact]
    public void A_cable_run_leaving_mid_line_still_lays_a_tee()
    {
        var bench = new RunBench();
        RealLine(bench, "StructureCableStraight", Direction.ZPlus);
        var middle = At(1, 0, 0);

        var plan = bench.Plan(bench.Run(bench.Cable, middle, At(1, 0, 3)));

        var swap = Assert.Single(plan.Changes.OfType<Intent.Swap>());
        Assert.Equal(Ways("x+,x-,z+"), bench.WaysOf(swap.New, middle));
    }

    [Fact]
    public void A_chute_run_leaving_mid_line_is_still_refused_no_piece()
    {
        var bench = new RunBench(ChuteCorner);
        RealLine(bench, "StructureChuteStraight", Direction.XPlus);
        var middle = At(1, 0, 0);

        var plan = bench.Plan(bench.Run(bench.KitOf("StructureChuteStraight"), middle, At(1, 0, 3)));

        Assert.True(plan.FirstFailure.TryGet(out var failure));
        Assert.Equal(middle, failure.At);
        Assert.Equal(ReasonCodes.NoPiece, failure.Why.Code);
    }

    [Fact]
    public void An_end_face_another_lines_end_points_into_is_kept()
    {
        var bench = new RunBench();
        RealLine(bench, "StructureCableStraight", Direction.ZPlus);
        bench.RealPointingInto(End, Direction.XPlus);

        var plan = bench.Plan(bench.Run(bench.Cable, End, Away));

        var swap = Assert.Single(plan.Changes.OfType<Intent.Swap>());
        Assert.Equal(Ways("x+,x-,z+"), bench.WaysOf(swap.New, End));
    }

    [Fact]
    public void A_run_starting_on_a_lines_end_into_the_line_leaves_it_as_it_is()
    {
        var bench = new RunBench();
        RealLine(bench, "StructureCableStraight", Direction.ZPlus);

        var plan = bench.Plan(bench.Run(bench.Cable, End, At(0, 0, 0)));

        Assert.Empty(plan.Changes);
    }

    [Fact]
    public void A_lone_piece_joining_nothing_merges_as_before()
    {
        var bench = new RunBench();
        var lone = At(2, 0, 0);
        bench.Real("StructureCableStraight", lone, bench.Sending(Direction.ZPlus, Direction.XPlus));

        var plan = bench.Plan(bench.Run(bench.Cable, lone, Away));

        var swap = Assert.Single(plan.Changes.OfType<Intent.Swap>());
        Assert.Equal(Ways("x+,x-,z+"), bench.WaysOf(swap.New, lone));
    }
}
