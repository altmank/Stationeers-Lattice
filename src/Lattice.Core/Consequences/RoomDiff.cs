using System;
using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Core.Consequences;

/// <summary>A room as recorded: its ref, its cells and its air summed over them.</summary>
internal sealed record RoomRecord(RoomRef Ref, IReadOnlyCollection<Cell> Cells, double TotalMol, double EnergyJ);

/// <summary>
/// What may move in or out of a room without anything being wrong: cells a sealing frame now blocks leave it, and the
/// gas those cells held is divided among their open neighbours (<c>AtmosphericEventInstance</c>
/// <c>DivideWorldAtmosphere</c>), so up to that much may arrive or leave.
/// </summary>
internal sealed record RoomExpectation(IReadOnlyCollection<Cell> SealedCells, double AllowanceMol, double AllowanceEnergyJ)
{
    /// <summary>Nothing expected to move.</summary>
    public static RoomExpectation None { get; } = new(Array.Empty<Cell>(), 0.0, 0.0);
}

/// <summary>Cells the room was expected to keep but lost, and cells it has that it was not expected to.</summary>
internal readonly record struct RoomCellDiff(int Lost, int Gained);

/// <summary>How far a room's moles and energy may move.</summary>
internal readonly record struct RoomTolerance(double Mol, double EnergyJ);

/// <summary>One recorded room compared with the room of the same ref afterwards.</summary>
/// <param name="Before">The room as recorded.</param>
/// <param name="After">The room of the same ref afterwards; none when no room has it any more.</param>
/// <param name="ExpectedCells">How many cells it should hold.</param>
/// <param name="Cells">Cells lost and gained against the expectation.</param>
/// <param name="Tolerance">How far its air may move.</param>
/// <param name="Problems">room-gone, room-cells-changed and room-air-changed, each at most once.</param>
internal sealed record RoomOutcome(
    RoomRecord Before,
    Option<RoomRecord> After,
    int ExpectedCells,
    RoomCellDiff Cells,
    RoomTolerance Tolerance,
    IReadOnlyList<Reason> Problems)
{
    /// <summary>Moles after less moles before; none when the room is gone.</summary>
    public Option<double> DeltaMol => After.Map(after => after.TotalMol - Before.TotalMol);

    /// <summary>Energy after less energy before; none when the room is gone.</summary>
    public Option<double> DeltaEnergyJ => After.Map(after => after.EnergyJ - Before.EnergyJ);
}

/// <summary>
/// The room check after a change, once the game has re-evaluated its rooms. A room must still exist under the same id
/// (a refill of the same cells reuses the id of the first room it empties, <c>RoomEvaluator.ThreadedWork</c>), hold
/// exactly its cells less those a sealing frame now blocks, and hold the same air within tolerance: 1 % or 0.5 mol
/// (1 kJ for energy), whichever is larger, plus the sealed cells' allowance.
/// </summary>
internal static class RoomDiff
{
    /// <summary>The share of the air before that may move.</summary>
    public const double RelativeTolerance = 0.01;

    /// <summary>The least tolerance in moles.</summary>
    public const double MinimumMolTolerance = 0.5;

    /// <summary>The least tolerance in joules.</summary>
    public const double MinimumEnergyToleranceJ = 1000.0;

    /// <summary>Compares the recorded room with the room afterwards. Linear in the cells.</summary>
    public static RoomOutcome Compare(RoomRecord before, Option<RoomRecord> after, RoomExpectation expectation)
    {
        var expected = new HashSet<Cell>(before.Cells);
        expected.ExceptWith(expectation.SealedCells);
        var tolerance = new RoomTolerance(
            Tolerance(before.TotalMol, MinimumMolTolerance, expectation.AllowanceMol),
            Tolerance(before.EnergyJ, MinimumEnergyToleranceJ, expectation.AllowanceEnergyJ));
        var problems = new List<Reason>();
        if (!after.TryGet(out var now))
        {
            problems.Add(ConsequenceCodes.Of(ReasonCodes.RoomGone, Facts(before)));
            return new RoomOutcome(before, after, expected.Count, new RoomCellDiff(expected.Count, 0), tolerance, problems);
        }

        var cells = CellsAgainst(expected, now.Cells);
        if (cells.Lost > 0 || cells.Gained > 0)
            problems.Add(ConsequenceCodes.Of(ReasonCodes.RoomCellsChanged, Facts(before)
                .With("lost", cells.Lost.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .With("gained", cells.Gained.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        if (Math.Abs(now.TotalMol - before.TotalMol) > tolerance.Mol || Math.Abs(now.EnergyJ - before.EnergyJ) > tolerance.EnergyJ)
            problems.Add(ConsequenceCodes.Of(ReasonCodes.RoomAirChanged, Facts(before)));
        return new RoomOutcome(before, after, expected.Count, cells, tolerance, problems);
    }

    /// <summary>The larger of the share and the floor, plus the allowance (never negative).</summary>
    public static double Tolerance(double before, double floor, double allowance) =>
        Math.Max(RelativeTolerance * Math.Abs(before), floor) + Math.Max(0.0, allowance);

    private static ValueMap<string, string> Facts(RoomRecord room) =>
        ValueMap<string, string>.Of("room", room.Ref.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static RoomCellDiff CellsAgainst(HashSet<Cell> expected, IReadOnlyCollection<Cell> actual)
    {
        var have = new HashSet<Cell>(actual);
        var lost = 0;
        foreach (var cell in expected)
            if (!have.Contains(cell))
                lost++;
        var gained = 0;
        foreach (var cell in have)
            if (!expected.Contains(cell))
                gained++;
        return new RoomCellDiff(lost, gained);
    }
}
