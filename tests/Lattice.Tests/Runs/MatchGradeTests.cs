using System.Linq;
using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.Tests.Routes;
using static Lattice.Tests.Runs.RunBench;

namespace Lattice.Tests.Runs;

/// <summary>
/// <see cref="LayOptions.MatchGrade"/>: a run or route laid with the kit of the run it joins when that kit lays the same
/// network kinds; the from end decides before the to end. The heavy cable kit gets a corner and a T here (the hand
/// catalogue has its straight only), and a liquid pipe kit; the modded conduit lays power and data as cable does.
/// </summary>
public sealed class MatchGradeTests
{
    public const string Grades = """
        piece TestHeavyCableCorner kit=ItemCableCoilHeavy option=1 grid=small
          step material=ItemCableCoilHeavy*1 exit=ItemWireCutters air=no
          end 0 0 0 x- power,data
          end 0 0 0 z+ power,data
          cell 0 0 0 cable
          collision 2

        piece TestHeavyCableJunction kit=ItemCableCoilHeavy option=2 grid=small
          step material=ItemCableCoilHeavy*2 exit=ItemWireCutters air=no
          end 0 0 0 x- power,data
          end 0 0 0 z+ power,data
          end 0 0 0 z- power,data
          cell 0 0 0 cable
          collision 2

        piece TestLiquidPipeStraight kit=ItemKitTestLiquidPipe option=0 grid=small pipe=liquid
          step material=ItemKitTestLiquidPipe*1 exit=ItemWrench air=no
          end 0 0 0 z+ liquid
          end 0 0 0 z- liquid
          cell 0 0 0 pipe
          collision 1

        piece TestLiquidPipeJunction kit=ItemKitTestLiquidPipe option=1 grid=small pipe=liquid
          step material=ItemKitTestLiquidPipe*1 exit=ItemWrench air=no
          end 0 0 0 x- liquid
          end 0 0 0 z+ liquid
          end 0 0 0 z- liquid
          cell 0 0 0 pipe
          collision 1
        """;

    private static readonly LayOptions Match = new(SinglePiecesOnly: false, MatchGrade: true);
    private static readonly Cell Start = At(0, 0, 0);
    private static readonly Cell End = At(5, 0, 0);

    private static readonly Bill NoBill = new(ValueList<ItemQuantity>.Empty, ValueList<ItemQuantity>.Empty, ValueList<ItemDelta>.Empty, ValueSet<ItemKey>.Empty);

    private static RunBench Bench() => new("lattice-catalogue 3\n" + Grades);

    private static KitKey Heavy(RunBench bench) => bench.KitOf("StructureCableStraightH");

    private static KitKey KitLaid(RunBench bench, Placement piece) => bench.Catalogue.Named(bench.NameOf(piece.Piece)).Kits[0].Kit;

    [Fact]
    public void A_cable_run_off_a_heavy_main_lays_heavy_cable_and_says_so()
    {
        var bench = Bench();
        bench.Real("StructureCableStraightH", Start);

        var plan = bench.Plan(bench.Run(bench.Cable, Start, End) with { Lay = Match });

        Assert.All(plan.Pieces, p => Assert.Equal(Heavy(bench), KitLaid(bench, p)));
        Assert.Equal(Option.Some(Heavy(bench)), plan.LaidWith);
        Assert.Equal(Option.Some(Heavy(bench)), plan.Report(_ => NoBill).LaidWith);
    }

    [Fact]
    public void A_cable_run_off_a_heavy_main_lays_the_requests_kit_without_matching()
    {
        var bench = Bench();
        bench.Real("StructureCableStraightH", Start);

        var plan = bench.Plan(bench.Run(bench.Cable, Start, End));

        Assert.All(plan.Pieces.Where(p => p.At != Start), p => Assert.Equal(bench.Cable, KitLaid(bench, p)));
        Assert.True(plan.LaidWith.IsNone);
    }

    [Fact]
    public void A_route_between_a_heavy_and_a_normal_run_takes_the_from_ends_grade()
    {
        var bench = new RouteBench(extraPieces: Grades);
        bench.Runs.Real("StructureCableStraightH", Start);
        bench.Runs.Real("StructureCableStraight", End);

        var plan = bench.Plan(bench.Route(bench.Runs.Cable, Start, End) with { Lay = Match });

        var heavy = Heavy(bench.Runs);
        Assert.Equal(Option.Some(heavy), plan.Run.LaidWith);
        Assert.All(plan.Run.Pieces.Where(p => p.At != End), p => Assert.Equal(heavy, KitLaid(bench.Runs, p)));
    }

    [Fact]
    public void A_gas_pipe_run_off_a_liquid_pipe_stays_gas()
    {
        var bench = Bench();
        bench.Real("TestLiquidPipeStraight", Start);
        var gas = bench.KitOf("StructurePipeStraight");

        var plan = bench.Plan(bench.Run(gas, Start, End) with { Lay = Match });

        Assert.True(plan.LaidWith.IsNone);
        Assert.All(plan.Pieces, p => Assert.Equal(gas, KitLaid(bench, p)));
    }

    [Fact]
    public void A_cable_run_off_a_modded_conduit_with_the_same_ends_lays_conduit()
    {
        var bench = Bench();
        bench.ConduitPointingInto(Start, Direction.XMinus);

        var plan = bench.Plan(bench.Run(bench.Cable, Start, End) with { Lay = Match });

        Assert.Equal(Option.Some(bench.Conduit), plan.LaidWith);
        Assert.All(plan.Pieces, p => Assert.Equal(bench.Conduit, KitLaid(bench, p)));
    }

    [Fact]
    public void A_planned_heavy_main_is_matched_as_a_built_one_is()
    {
        var bench = Bench();
        bench.Ghost("StructureCableStraightH", Start);

        var plan = bench.Plan(bench.Run(bench.Cable, Start, End) with { Lay = Match });

        Assert.Equal(Option.Some(Heavy(bench)), plan.LaidWith);
    }
}
