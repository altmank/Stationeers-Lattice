using System;
using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.Core.Runs;
using Lattice.TestKit;
using Xunit;
using static Lattice.Tests.Runs.RunBench;

namespace Lattice.Tests.Runs;

public sealed class RunTests
{
    private static List<Cell> Covered(RunBench bench, RunPlan plan) => plan.Pieces.SelectMany(p => bench.CellsOf(p)).ToList();

    private static Placement PieceAt(RunBench bench, RunPlan plan, Cell cell) =>
        Assert.Single(plan.Pieces, p => bench.CellsOf(p).Contains(cell));

    [Fact]
    public void A_straight_run_lays_the_longest_pieces_first()
    {
        var bench = new RunBench();

        var plan = bench.Plan(bench.Run(bench.Cable, Origin, At(11, 0, 0)));

        Assert.Equal(12, plan.Cells.Count);
        Assert.Equal(
            new[] { "StructureCableStraight10", "StructureCableStraight", "StructureCableStraight" },
            plan.Pieces.Select(p => bench.NameOf(p.Piece)));
        Assert.Equal(plan.Cells.ToHashSet(), Covered(bench, plan).ToHashSet());
        Assert.Equal(12, Covered(bench, plan).Count);
        Assert.Equal(12, plan.Singles.Count);
        Assert.False(plan.FirstFailure.IsSome);
    }

    [Fact]
    public void A_run_bends_at_its_waypoints()
    {
        var bench = new RunBench();
        var bend = At(5, 0, 0);

        var plan = bench.Plan(bench.Run(bench.Cable, Origin, At(5, 0, 5), bend));

        Assert.Equal(11, plan.Cells.Count);
        var corner = PieceAt(bench, plan, bend);
        Assert.Equal("StructureCableCorner", bench.NameOf(corner.Piece));
        Assert.Equal(Ways("x-,z+"), bench.WaysOf(corner, bend));
        Assert.Equal(plan.Cells.ToHashSet(), Covered(bench, plan).ToHashSet());
    }

    [Fact]
    public void A_run_through_a_blocked_cell_reports_that_cell_and_the_games_reason()
    {
        var bench = new RunBench();
        var blocked = At(7, 0, 0);
        bench.Check.Refuse(p => bench.CellsOf(p).Contains(blocked), ReasonCodes.GameRefused, "Blocked by StructureWall");

        var plan = bench.Plan(bench.Run(bench.Cable, Origin, At(11, 0, 0)));

        Assert.True(plan.FirstFailure.TryGet(out var failure));
        Assert.Equal(blocked, failure.At);
        Assert.Equal(ReasonCodes.GameRefused, failure.Why.Code);
        Assert.Equal(Option.Some("Blocked by StructureWall"), failure.Why.GameMessage);
        Assert.Equal(plan.Cells.ToHashSet(), Covered(bench, plan).ToHashSet());
        Assert.Equal("StructureCableStraight5", bench.NameOf(PieceAt(bench, plan, Origin).Piece));
        Assert.Equal("StructureCableStraight", bench.NameOf(PieceAt(bench, plan, blocked).Piece));
    }

    [Fact]
    public void Crossing_a_real_cable_swaps_it_for_the_merged_shape()
    {
        var bench = new RunBench();
        var crossing = At(2, 0, 0);
        var old = bench.Real("StructureCableStraight", crossing);

        var plan = bench.Plan(bench.Run(bench.Cable, Origin, At(4, 0, 0)));

        var swap = Assert.Single(plan.Changes.OfType<Intent.Swap>());
        Assert.Equal(old, swap.Old);
        Assert.Equal("StructureCableJunction4", bench.NameOf(swap.New.Piece));
        Assert.Equal(Ways("x+,x-,z+,z-"), bench.WaysOf(swap.New, crossing));
    }

    [Fact]
    public void A_run_ending_on_a_real_cable_joins_it()
    {
        var bench = new RunBench();
        var end = At(3, 0, 0);
        bench.Real("StructureCableStraight", end);

        var plan = bench.Plan(bench.Run(bench.Cable, Origin, end));

        var swap = Assert.Single(plan.Changes.OfType<Intent.Swap>());
        Assert.Equal(Ways("x-,z+,z-"), bench.WaysOf(swap.New, end));
    }

    [Fact]
    public void Crossing_a_real_long_straight_removes_it_and_splits_it_with_a_cross()
    {
        var bench = new RunBench();
        var crossing = At(2, 0, 0);
        var old = bench.Real("StructureCableStraight10", Step(crossing, Direction.ZPlus, 4));

        var plan = bench.Plan(bench.Run(bench.Cable, Origin, At(4, 0, 0)));

        var removal = Assert.Single(plan.Changes.OfType<Intent.Remove>());
        Assert.Equal(old, removal.Target);
        var cross = PieceAt(bench, plan, crossing);
        Assert.Equal("StructureCableJunction4", bench.NameOf(cross.Piece));
        Assert.Equal(14, Covered(bench, plan).Distinct().Count());
        Assert.False(plan.FirstFailure.IsSome);
    }

    [Fact]
    public void A_run_along_a_real_long_straight_leaves_it_as_it_is()
    {
        var bench = new RunBench();
        bench.Real("StructureCableStraight3", Origin);

        var plan = bench.Plan(bench.Run(bench.Cable, Step(Origin, Direction.ZMinus, 2), Origin));

        Assert.Empty(plan.Changes);
        Assert.Equal(3, plan.Cells.Count);
    }

    [Fact]
    public void A_run_takes_a_side_neighbour_pointing_in()
    {
        var bench = new RunBench();
        var side = At(1, 0, 0);
        bench.RealPointingInto(side, Direction.ZPlus);

        var plan = bench.Plan(bench.Run(bench.Cable, Origin, At(4, 0, 0)));

        Assert.Equal(Ways("x+,x-,z+"), bench.WaysOf(PieceAt(bench, plan, side), side));
    }

    [Fact]
    public void A_self_crossing_run_lays_a_cross_where_it_crosses()
    {
        var bench = new RunBench();
        var crossing = At(2, 0, 0);

        var plan = bench.Plan(bench.Run(bench.Cable, Origin, At(2, 0, -2), At(4, 0, 0), At(4, 0, 2), At(2, 0, 2)));

        Assert.Equal("StructureCableJunction4", bench.NameOf(PieceAt(bench, plan, crossing).Piece));
        Assert.Equal(plan.Cells.Count, Covered(bench, plan).Count);
    }

    [Fact]
    public void A_modded_kit_lays_its_own_long_piece_first()
    {
        var bench = new RunBench();

        var plan = bench.Plan(bench.Run(bench.Conduit, Origin, At(0, 5, 0)));

        Assert.Equal(new[] { "ModConduitLong4", "ModConduitLine", "ModConduitLine" }, plan.Pieces.Select(p => bench.NameOf(p.Piece)));
        Assert.Equal(plan.Cells.ToHashSet(), Covered(bench, plan).ToHashSet());
    }

    [Fact]
    public void A_cell_the_kit_has_no_piece_for_fails_with_no_piece()
    {
        var bench = new RunBench();

        var plan = bench.Plan(bench.Run(bench.Conduit, At(0, -2, 0), At(-2, 0, 0), Origin, At(0, 0, 2), At(2, 0, 2), At(2, 0, 0)));

        Assert.True(plan.FirstFailure.TryGet(out var failure));
        Assert.Equal(Origin, failure.At);
        Assert.Equal(ReasonCodes.NoPiece, failure.Why.Code);
        Assert.Equal("x+,x-,y-,z+", failure.Why.Facts["shape"]);
        Assert.DoesNotContain(Origin, Covered(bench, plan));
    }

    [Fact]
    public void A_leg_off_one_axis_is_refused()
    {
        var bench = new RunBench();

        var why = bench.Refusal(bench.Run(bench.Cable, Origin, At(3, 0, 2)));

        Assert.Equal(ReasonCodes.InvalidValue, why.Code);
        Assert.Equal("not-straight", why.Facts["problem"]);
    }

    [Fact]
    public void A_point_off_the_small_grid_is_refused()
    {
        var bench = new RunBench();

        var why = bench.Refusal(bench.Run(bench.Cable, Origin, new Cell(Origin.X + 7, Origin.Y, Origin.Z)));

        Assert.Equal("off-grid", why.Facts["problem"]);
    }

    [Fact]
    public void An_unknown_kit_is_refused()
    {
        var bench = new RunBench();

        var why = bench.Refusal(bench.Run(new KitKey(424242), Origin, At(3, 0, 0)));

        Assert.NotEqual(ReasonCodes.NoPiece, why.Code);
    }

    [Fact]
    public void The_report_bills_the_pieces_and_the_same_cells_as_singles()
    {
        var bench = new RunBench();
        var plan = bench.Plan(bench.Run(bench.Cable, Origin, At(11, 0, 0)));
        static Bill Count(ValueList<Placement> pieces) => new(
            ValueList.Of(TestKitIds.Must(ItemQuantity.Create(new ItemKey(1), pieces.Count))),
            ValueList<ItemQuantity>.Empty, ValueList<ItemDelta>.Empty, ValueSet<ItemKey>.Empty);

        var report = plan.Report(Count);

        Assert.Equal(plan.Cells, report.Cells);
        Assert.Equal(3, report.Cost.Needed[0].Count);
        Assert.Equal(12, report.CostAsSingles.Needed[0].Count);
    }
}
