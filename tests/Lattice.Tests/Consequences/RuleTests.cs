using System.Collections.Generic;
using Lattice.Api;
using FaceLoad = Lattice.Core.Consequences.FaceLoad;
using Lattice.Core.Consequences;
using static Lattice.Tests.Consequences.Models;

namespace Lattice.Tests.Consequences;

/// <summary>Network union and leave rules.</summary>
public sealed class NetworkRuleTests
{
    [Fact]
    public void A_network_of_only_the_new_pieces_goes_into_the_kept_one() =>
        Assert.Equal(NetworkUnion.IntoKept, NetworkUnionRule.For(theirsHoldsOnlyNewPieces: true));

    [Fact]
    public void A_network_with_pieces_that_stood_before_takes_the_kept_one_in() =>
        Assert.Equal(NetworkUnion.IntoTheirs, NetworkUnionRule.For(theirsHoldsOnlyNewPieces: false));

    [Fact]
    public void A_chute_is_never_taken_out_of_its_network_first() =>
        Assert.False(NetworkLeaveRule.LeavesFirst(NeighbourRebuild.Always, networkKeptWhole: true));

    [Fact]
    public void A_cable_or_pipe_of_a_network_kept_whole_leaves_it_first() =>
        Assert.True(NetworkLeaveRule.LeavesFirst(NeighbourRebuild.WhenItHadANetwork, networkKeptWhole: true));

    [Fact]
    public void A_cable_or_pipe_of_a_network_that_splits_stays_in_it_for_the_game_to_rebuild() =>
        Assert.False(NetworkLeaveRule.LeavesFirst(NeighbourRebuild.WhenItHadANetwork, networkKeptWhole: false));

    [Fact]
    public void A_chute_of_a_network_that_splits_stays_in_it_for_the_game_to_rebuild() =>
        Assert.False(NetworkLeaveRule.LeavesFirst(NeighbourRebuild.Always, networkKeptWhole: false));

    [Fact]
    public void A_chute_a_plain_removal_takes_stays_in_its_network() =>
        Assert.False(NetworkLeaveRule.SwapLeavesFirst(NeighbourRebuild.Always, hasReplacements: false));

    [Fact]
    public void A_chute_a_swap_replaces_hands_its_devices_over_first() =>
        Assert.True(NetworkLeaveRule.SwapLeavesFirst(NeighbourRebuild.Always, hasReplacements: true));

    [Fact]
    public void A_cable_or_pipe_a_plain_removal_takes_leaves_its_network_first() =>
        Assert.True(NetworkLeaveRule.SwapLeavesFirst(NeighbourRebuild.WhenItHadANetwork, hasReplacements: false));
}

/// <summary>Air, gravity and wall stress on a swap.</summary>
public sealed class AirRuleTests
{
    private static PieceBlocking Blocks(bool air, bool gravity) => new(air, gravity);

    [Fact]
    public void An_airtight_piece_for_an_airtight_piece_keeps()
    {
        Assert.Equal(AirChange.Keeps, AirRule.Judge(Blocks(true, true), Blocks(true, true)));
        Assert.Equal(AirChange.Keeps, AirRule.Judge(Blocks(false, false), Blocks(false, false)));
    }

    [Fact]
    public void A_leaky_piece_made_airtight_seals()
    {
        Assert.Equal(AirChange.Seals, AirRule.Judge(Blocks(false, true), Blocks(true, true)));
        Assert.Equal(AirChange.Seals, AirRule.Judge(Blocks(false, false), Blocks(true, true)));
    }

    [Fact]
    public void Letting_through_air_or_gravity_the_old_piece_blocked_would_open()
    {
        Assert.Equal(AirChange.WouldOpen, AirRule.Judge(Blocks(true, true), Blocks(false, true)));
        Assert.Equal(AirChange.WouldOpen, AirRule.Judge(Blocks(true, true), Blocks(true, false)));
        Assert.Equal(AirChange.WouldOpen, AirRule.Judge(Blocks(false, true), Blocks(true, false)));
    }

    [Fact]
    public void A_face_at_or_above_its_summed_rating_with_the_new_wall_is_overstressed()
    {
        Assert.Equal(StressVerdict.Overstressed, WallStress.Judge(new FaceLoad(150, 50, false), 100, 0.8));
        Assert.Equal(StressVerdict.Overstressed, WallStress.Judge(new FaceLoad(200, 0, false), 100, 0.8));
    }

    [Fact]
    public void Above_the_new_walls_stress_mark_is_a_warning_only()
    {
        Assert.Equal(StressVerdict.Stressed, WallStress.Judge(new FaceLoad(81, 0, false), 100, 0.8));
        Assert.Equal(StressVerdict.Ok, WallStress.Judge(new FaceLoad(80, 0, false), 100, 0.8));
    }

    [Fact]
    public void A_shielded_face_or_a_wall_without_a_rating_is_never_stressed()
    {
        Assert.Equal(StressVerdict.Ok, WallStress.Judge(new FaceLoad(1000, 0, true), 100, 0.8));
        Assert.Equal(StressVerdict.Ok, WallStress.Judge(new FaceLoad(1000, 0, false), -1, 0.8));
    }
}

/// <summary>Whether a face stays sealed.</summary>
public sealed class FaceSealTests
{
    private static readonly ReferenceId Plate = Id(1);
    private static readonly ReferenceId OtherPlate = Id(2);
    private static readonly ReferenceId Frame = Id(3);

    private static readonly Option<AirBlocker> Nothing = Option<AirBlocker>.None;

    [Fact]
    public void A_plate_on_a_finished_frames_face_opens_nothing() =>
        Assert.True(FaceSeal.Sealed([], Option.Some(new AirBlocker(Frame, true)), Nothing, [Plate]));

    [Fact]
    public void Two_plates_back_to_back_breach_only_when_both_go()
    {
        AirBlocker[] face = [new(OtherPlate, true)];

        Assert.True(FaceSeal.Sealed(face, Nothing, Nothing, [Plate]));
        Assert.False(FaceSeal.Sealed(face, Nothing, Nothing, [Plate, OtherPlate]));
    }

    [Fact]
    public void An_unfinished_frame_or_a_window_gap_seals_nothing() =>
        Assert.False(FaceSeal.Sealed([new AirBlocker(Id(9), false)], Option.Some(new AirBlocker(Frame, false)), Nothing, [Plate]));

    [Fact]
    public void A_frame_removed_with_the_plate_no_longer_seals() =>
        Assert.False(FaceSeal.Sealed([], Option.Some(new AirBlocker(Frame, true)), Nothing, [Plate, Frame]));
}

/// <summary>Proportional and by-volume shares.</summary>
public sealed class GasShareTests
{
    [Fact]
    public void Taken_in_proportion_to_what_each_cell_holds() =>
        Assert.Equal([5.0, 15.0, 0.0, 30.0], GasShares.Proportional([10.0, 30.0, 0.0, 60.0], Option.Some(50.0)));

    [Fact]
    public void All_of_it_without_an_amount() =>
        Assert.Equal([2.0, 3.0], GasShares.Proportional([2.0, 3.0], Option<double>.None));

    [Fact]
    public void Amount_capped_at_the_total() =>
        Assert.Equal([2.0, 3.0], GasShares.Proportional([2.0, 3.0], Option.Some(1000.0)));

    [Fact]
    public void Nothing_held_takes_nothing()
    {
        Assert.Equal([0.0, 0.0], GasShares.Proportional([0.0, -1.0], Option.Some(5.0)));
        Assert.Empty(GasShares.Proportional([], Option.Some(5.0)));
    }

    [Fact]
    public void Spread_by_volume() => Assert.Equal([0.5, 0.375, 0.125], GasShares.ByVolume([8000.0, 6000.0, 2000.0]));

    [Fact]
    public void No_volume_spreads_evenly()
    {
        Assert.Equal([0.5, 0.5], GasShares.ByVolume([0.0, 0.0]));
        Assert.Empty(GasShares.ByVolume([]));
    }
}

/// <summary>Pooling a room's air.</summary>
public sealed class RoomAirTests
{
    [Fact]
    public void Air_pools_like_the_game()
    {
        var air = new RoomAir(2);
        air.AddCell(1.0, 3.0, 5.0);
        air.AddVolume(8000.0);
        air.AddGas(0, 300.0, 300.0 * 21.1 * 290.0, 300.0 * 21.1, true);
        air.AddCell(3.0, 1.0, 7.0);
        air.AddVolume(8000.0);
        air.AddGas(0, 300.0, 300.0 * 21.1 * 300.0, 300.0 * 21.1, true);
        air.AddGas(1, 10.0, 10.0 * 72.0 * 295.0, 10.0 * 72.0, false);

        var expectedK = (300.0 * 21.1 * 590.0 + 10.0 * 72.0 * 295.0) / (600.0 * 21.1 + 720.0);
        Assert.Equal(2, air.CellCount);
        Assert.Equal(610.0, air.TotalMol);
        Assert.Equal(600.0, air.GasMol);
        Assert.Equal(expectedK, air.TemperatureK, 9);
        Assert.Equal(GasLaw.PressureKpa(600.0, expectedK, 16000.0), air.PressureKpa, 9);
        Assert.Equal((1.0, 1.0, 5.0), (air.MinX, air.MinY, air.MinZ));
        Assert.Equal((3.0, 3.0, 7.0), (air.MaxX, air.MaxY, air.MaxZ));
    }

    [Fact]
    public void An_empty_room_reads_zero_not_nan()
    {
        var air = new RoomAir(1);
        air.AddCell(0.0, 0.0, 0.0);

        Assert.Equal(0.0, air.TemperatureK);
        Assert.Equal(0.0, air.PressureKpa);
    }
}

/// <summary>The room check after a change.</summary>
public sealed class RoomDiffTests
{
    private static Cell[] Row(params int[] xs)
    {
        var cells = new Cell[xs.Length];
        for (var index = 0; index < xs.Length; index++)
            cells[index] = new Cell(xs[index], 10, 10);
        return cells;
    }

    private static RoomRecord Room(Cell[] cells, double mol, double energy) => new(new RoomRef(5), cells, mol, energy);

    private static Option<RoomRecord> After(Cell[] cells, double mol, double energy) => Option.Some(Room(cells, mol, energy));

    private static List<ReasonCode> Codes(RoomOutcome outcome)
    {
        var codes = new List<ReasonCode>();
        foreach (var problem in outcome.Problems)
            codes.Add(problem.Code);
        return codes;
    }

    [Fact]
    public void The_same_room_with_the_same_cells_and_air_has_no_problem()
    {
        var outcome = RoomDiff.Compare(Room(Row(10, 30, 50), 100, 1e6), After(Row(10, 30, 50), 100.4, 1e6 + 5000), RoomExpectation.None);

        Assert.Empty(outcome.Problems);
        Assert.Equal(3, outcome.ExpectedCells);
        Assert.Equal(1.0, outcome.Tolerance.Mol, 6);
        Assert.Equal(10000.0, outcome.Tolerance.EnergyJ, 6);
    }

    [Fact]
    public void A_room_that_no_longer_exists_is_gone()
    {
        var outcome = RoomDiff.Compare(Room(Row(10, 30), 10, 1000), Option<RoomRecord>.None, RoomExpectation.None);

        Assert.Equal([ReasonCodes.RoomGone], Codes(outcome));
        Assert.True(outcome.DeltaMol.IsNone);
    }

    [Fact]
    public void A_cell_a_sealing_frame_now_blocks_is_expected_to_leave()
    {
        var outcome = RoomDiff.Compare(Room(Row(10, 30, 50), 100, 1e6), After(Row(10, 30), 100, 1e6), new RoomExpectation(Row(50), 0, 0));

        Assert.Empty(outcome.Problems);
        Assert.Equal(2, outcome.ExpectedCells);
    }

    [Fact]
    public void An_unexpected_cell_lost_or_gained_changes_the_room()
    {
        var lost = RoomDiff.Compare(Room(Row(10, 30, 50), 100, 1e6), After(Row(10, 30), 100, 1e6), RoomExpectation.None);
        var gained = RoomDiff.Compare(Room(Row(10), 100, 1e6), After(Row(10, 30), 100, 1e6), RoomExpectation.None);

        Assert.Equal([ReasonCodes.RoomCellsChanged], Codes(lost));
        Assert.Equal(new RoomCellDiff(1, 0), lost.Cells);
        Assert.Equal(new RoomCellDiff(0, 1), gained.Cells);
    }

    [Fact]
    public void Air_outside_the_tolerance_changes_the_room()
    {
        Assert.Empty(RoomDiff.Compare(Room(Row(10), 10, 1e5), After(Row(10), 10.5, 1e5), RoomExpectation.None).Problems);
        Assert.Equal([ReasonCodes.RoomAirChanged], Codes(RoomDiff.Compare(Room(Row(10), 10, 1e5), After(Row(10), 10.6, 1e5), RoomExpectation.None)));
        Assert.Empty(RoomDiff.Compare(Room(Row(10), 1000, 1e8), After(Row(10), 990, 1e8), RoomExpectation.None).Problems);
        Assert.Equal([ReasonCodes.RoomAirChanged], Codes(RoomDiff.Compare(Room(Row(10), 1000, 1e8), After(Row(10), 1000, 1.02e8), RoomExpectation.None)));
    }

    [Fact]
    public void Gas_of_a_sealed_cell_may_move_within_its_allowance()
    {
        var divided = new RoomExpectation([], 3, 2000);

        Assert.Empty(RoomDiff.Compare(Room(Row(10), 10, 1e5), After(Row(10), 13.4, 1e5 + 2900), divided).Problems);
        Assert.Equal([ReasonCodes.RoomAirChanged], Codes(RoomDiff.Compare(Room(Row(10), 10, 1e5), After(Row(10), 13.6, 1e5), divided)));
    }
}
