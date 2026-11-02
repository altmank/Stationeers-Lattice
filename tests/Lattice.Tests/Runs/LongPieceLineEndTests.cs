using System.Linq;
using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.Core.Runs;
using Xunit;
using static Lattice.Tests.Runs.RunBench;

namespace Lattice.Tests.Runs;

/// <summary>
/// A run that leaves the end cell of a multi-cell straight side-on, where that straight is a line's end, turns the cell
/// into a corner, as it does a single end piece (<see cref="MergeLineEndTests"/>): the straight's open outer face joins
/// nothing, so it is dropped rather than kept as the stub of a tee (cable, pipe) or as a way no chute piece has. The
/// long straights' ends and cells are the game's, as the catalogue reader exports them (the header of
/// hand-catalogue.lcat: cables pivot..-z, pipes pivot..+z, chutes pivot..-x); the corner as a line's end is the
/// 0.14.1 rule.
/// </summary>
public sealed class LongPieceLineEndTests
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

    public static TheoryData<string, string, string, string, string> Lines => new()
    {
        { "StructureCableStraight3", "StructureCableStraight", "StructureCableCorner", "x+", "z-" },
        { "StructurePipeStraight3", "StructurePipeStraight", "StructurePipeCorner", "x+", "z+" },
        { "StructureChuteStraight3", "StructureChuteStraight", "StructureChuteCorner", "z+", "x-" },
        { "ModConduitLong4", "ModConduitLine", "ModConduitBend", "x+", "y-" },
    };

    private static Direction Side(string way) => way switch
    {
        "x+" => Direction.XPlus,
        "x-" => Direction.XMinus,
        "y-" => Direction.YMinus,
        "z+" => Direction.ZPlus,
        "z-" => Direction.ZMinus,
        _ => throw new System.ArgumentException(way),
    };

    /// <summary>The long straight laid unturned at the origin; its pivot cell is the line's end with the open face.</summary>
    private static (RunBench Bench, StructureRef Long) Line(string longPiece) =>
        Line(new RunBench(ChuteCorner), longPiece);

    private static (RunBench Bench, StructureRef Long) Line(RunBench bench, string longPiece) =>
        (bench, bench.Real(longPiece, Origin));

    [Theory]
    [MemberData(nameof(Lines))]
    public void A_run_leaving_the_end_cell_of_a_long_straight_side_on_lays_a_corner_there(
        string longPiece, string straight, string corner, string away, string inward)
    {
        var (bench, line) = Line(longPiece);
        var to = Step(Origin, Side(away), 2);

        var plan = bench.Plan(bench.Run(bench.KitOf(straight), Origin, to));

        Assert.False(plan.FirstFailure.IsSome);
        Assert.Contains(plan.Changes.OfType<Intent.Remove>(), r => r.Target == line);
        var end = Assert.Single(plan.Pieces, p => bench.CellsOf(p).Contains(Origin));
        Assert.Equal(corner, bench.NameOf(end.Piece));
        Assert.Equal(Ways($"{away},{inward}"), bench.WaysOf(end, Origin));
    }

    [Theory]
    [InlineData("StructureCableStraight3", "StructureCableStraight", "x+", "z-")]
    [InlineData("StructurePipeStraight3", "StructurePipeStraight", "x+", "z+")]
    [InlineData("StructureChuteStraight3", "StructureChuteStraight", "z+", "x-")]
    [InlineData("ModConduitLong4", "ModConduitLine", "x+", "y-")]
    public void The_rest_of_the_long_straight_is_split_into_straights(
        string longPiece, string straight, string away, string inward)
    {
        var (bench, _) = Line(longPiece);

        var plan = bench.Plan(bench.Run(bench.KitOf(straight), Origin, Step(Origin, Side(away), 2)));

        var next = Step(Origin, Side(inward));
        var rest = Assert.Single(plan.Pieces, p => bench.CellsOf(p).Contains(next));
        Assert.Equal(straight, bench.NameOf(rest.Piece));
        Assert.Equal(Ways(inward + "," + Opposite(inward)), bench.WaysOf(rest, next));
    }

    private static string Opposite(string way) => way[0] + (way[1] == '+' ? "-" : "+");

    [Fact]
    public void An_outer_face_another_lines_end_points_into_is_kept_as_a_tee()
    {
        var bench = new RunBench();
        bench.Real("StructureCableStraight3", Origin);
        bench.RealPointingInto(Origin, Direction.ZPlus);

        var plan = bench.Plan(bench.Run(bench.Cable, Origin, Step(Origin, Direction.XPlus, 2)));

        var end = Assert.Single(plan.Pieces, p => bench.CellsOf(p).Contains(Origin));
        Assert.Equal(Ways("x+,z+,z-"), bench.WaysOf(end, Origin));
    }

    [Fact]
    public void A_run_leaving_mid_way_along_a_long_straight_still_lays_a_tee()
    {
        var bench = new RunBench();
        bench.Real("StructureCableStraight3", Origin);
        var middle = Step(Origin, Direction.ZMinus);

        var plan = bench.Plan(bench.Run(bench.Cable, middle, Step(middle, Direction.XPlus, 2)));

        var tee = Assert.Single(plan.Pieces, p => bench.CellsOf(p).Contains(middle));
        Assert.Equal(Ways("x+,z+,z-"), bench.WaysOf(tee, middle));
    }
}
