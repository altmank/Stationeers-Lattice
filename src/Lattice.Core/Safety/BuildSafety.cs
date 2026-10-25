using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Consequences;
using Lattice.Core.Openings;
using Lattice.Core.Ports;

namespace Lattice.Core.Safety;

/// <summary>
/// The build safety check: what a whole plan would do that nobody asked for, as warnings
/// that never block. Each warning names the ghosts behind it and the facts it rests on; an agent may treat one as a
/// refusal under its own rules, Lattice never does. Reads the built world for rooms, air and networks, and the planned
/// world (every ghost on the board done) for what stands where. A warning that needs a fact Lattice does not hold stays
/// silent. Main thread. Cost: linear in the plan's ghosts, plus the pieces of each power network they take from and up
/// to <see cref="Vents.MostFollowed"/> pieces behind each planned vent.
/// </summary>
internal sealed class BuildSafety(
    ICatalogue catalogue, IGrid grid, ISafetyFacts facts, IOpeningFacts openings, OpeningBands bands, SafetyLimits limits)
{
    /// <summary>The warnings for the plan's ghosts, on the board's planned world over the built world.</summary>
    public ValueList<SafetyWarning> Check(IReadOnlyList<Ghost> plan, IGhostBoard board, IWorldView world)
    {
        var run = Start(plan, board, world);
        run.Step(int.MaxValue, int.MaxValue);
        return run.Warnings();
    }

    /// <summary>
    /// The same check in parts (<see cref="SafetyRun.Step(int, int)"/>), for the main thread's slices: the warnings come out as
    /// <see cref="Check"/> gives them. The board and the world must not change between its steps.
    /// </summary>
    public SafetyRun Start(IReadOnlyList<Ghost> plan, IGhostBoard board, IWorldView world)
    {
        var scan = new SafetyScan(plan, board, world, catalogue, grid, facts, limits);
        return new SafetyRun(scan, new OpeningKeepOuts(scan.Planned, scan.Shapes, openings, bands), openings);
    }
}

/// <summary>
/// One build safety check of one plan, worked a few units at a time: each check in turn over every ghost of the plan
/// (and power cuts over every network the plan takes from), in the order <see cref="BuildSafety.Check"/> runs them. A
/// step also stops on a budget of cell reads, so a large piece's controls (every cell around each of its spots) and a
/// long walk behind a vent go on over several steps; every other unit counts one read. Main thread; the board and the
/// world must not change between steps.
/// </summary>
internal sealed class SafetyRun(SafetyScan scan, OpeningKeepOuts keepOuts, IOpeningFacts openings) : ISafetyRun
{
    /// <summary>The units one plan-state checking step asks for (a few ghosts through one check).</summary>
    public const int StepUnits = 4;

    /// <summary>
    /// The cell reads one step may make (a few more when a spot's cells straddle the end): on a large real base a large piece's
    /// controls read about one cell per microsecond, so a step stays near a tenth of a millisecond.
    /// </summary>
    public const int StepReads = 128;

    private const int Passes = 8;
    private const int PowerNetworks = 2;
    private const int Ventilation = 3;
    private const int ControlSides = 4;

    private int _pass;
    private int _next;
    private readonly Controls.Cursor _controls = new();
    private Vents.Follow? _vent;

    /// <summary>Whether every check is done.</summary>
    public bool Done => _pass == Passes;

    /// <summary>The check working now, in <see cref="BuildSafety.Check"/>'s order (0 faces to 7 terrain); 8 once done.</summary>
    public int Check => _pass;

    /// <summary>
    /// Works up to <paramref name="units"/> units (one ghost through one check, or one network through power cuts) and
    /// about <see cref="StepReads"/> cell reads; true once every check is done.
    /// </summary>
    public bool Step(int units) => Step(units, StepReads);

    /// <summary>Works up to <paramref name="units"/> units and about <paramref name="reads"/> cell reads; true once every check is done.</summary>
    public bool Step(int units, int reads)
    {
        while (units > 0 && reads > 0 && _pass < Passes)
        {
            if (_next < UnitsOf(_pass))
            {
                if (!Visit(_pass, _next, ref reads))
                    break;
                _next++;
                units--;
                continue;
            }

            _pass++;
            _next = 0;
        }

        return Done;
    }

    /// <summary>The warnings found so far; all of them once <see cref="Done"/>.</summary>
    public ValueList<SafetyWarning> Warnings() => scan.Warnings.ToList();

    private int UnitsOf(int pass) => pass == PowerNetworks ? scan.PowerOrder.Count : scan.Ghosts.Count;

    // True once the unit is done; the controls and vents units may stop part way when the reads run out.
    private bool Visit(int pass, int index, ref int reads)
    {
        if (pass == Ventilation)
            return Vents.Visit(scan, scan.Ghosts[index], ref _vent, ref reads);
        if (pass == ControlSides)
        {
            if (!Controls.Visit(scan, scan.Ghosts[index], _controls, ref reads))
                return false;
            _controls.Spot = -1;
            return true;
        }

        reads--;
        switch (pass)
        {
            case 0:
                FaceChecks.Visit(scan, scan.Ghosts[index]);
                break;
            case 1:
                PowerCuts.Gather(scan, scan.Ghosts[index]);
                break;
            case PowerNetworks:
                PowerCuts.Check(scan, scan.PowerOrder[index]);
                break;
            case 5:
                OpeningWarnings.Visit(scan, keepOuts, openings, scan.Ghosts[index]);
                break;
            case 6:
                Paint.Visit(scan, scan.Ghosts[index]);
                break;
            default:
                Terrain.Visit(scan, scan.Ghosts[index]);
                break;
        }

        return true;
    }
}

/// <summary>A build safety check worked a few units at a time (<see cref="SafetyRun"/>). Main thread.</summary>
internal interface ISafetyRun
{
    /// <summary>Works up to <paramref name="units"/> units; true once the check is done.</summary>
    bool Step(int units);

    /// <summary>The warnings found so far; all of them once a step has answered done.</summary>
    ValueList<SafetyWarning> Warnings();
}

/// <summary>The gases that feed a fire: the game's <c>GasMixture.TotalOxidiser</c> (Assets.Scripts.Atmospherics/GasMixture.cs).</summary>
internal static class Oxidisers
{
    /// <summary>The oxidiser gases as the world names them (<see cref="World.GameText.Token"/> of the game's gas types).</summary>
    public static HashSet<GasKind> Kinds
    {
        get
        {
            var kinds = new HashSet<GasKind>();
            foreach (var name in new[] { "Oxygen", "LiquidOxygen", "NitrousOxide", "LiquidNitrousOxide", "Ozone", "LiquidOzone" })
                if (GasKind.Create(World.GameText.Token(name)).TryGet(out var kind))
                    kinds.Add(kind);
            return kinds;
        }
    }

    /// <summary>The moles of oxidiser in the gases.</summary>
    public static double In(ValueList<GasHeld> gases, HashSet<GasKind> oxidisers)
    {
        var moles = 0.0;
        foreach (var gas in gases)
            if (oxidisers.Contains(gas.Gas))
                moles += gas.Amount.Value;
        return moles;
    }

    /// <summary>Every mole of the gases.</summary>
    public static double Total(ValueList<GasHeld> gases)
    {
        var moles = 0.0;
        foreach (var gas in gases)
            moles += gas.Amount.Value;
        return moles;
    }
}

/// <summary>
/// What taking out an air-blocking face piece (a wall, window or door) does to the rooms on its two sides. The game's
/// air passes a face once nothing on it blocks air (<c>Structure.CanAirPass</c>, <see cref="AirRule"/>), and a swap takes
/// the old piece out before the new one goes in, so a swap opens the face for a while whatever it puts back:
/// swap-on-air-room when a room with air is on either side. Removals and swaps both: oxidiser-outside when a room
/// holding oxidiser is on one side and the outside on the other; hot-gas when a hot room is on one side and anything
/// else on the other.
/// </summary>
internal static class FaceChecks
{
    public static void Visit(SafetyScan scan, Ghost ghost)
    {
        if (!SafetyScan.Taken(ghost).TryGet(out var target) || !scan.World.Structure(target.Id).TryGet(out var info)
            || !scan.Piece(info.Ref.Piece).TryGet(out var piece) || !WorldModels.AirBlockerOf(info, piece).BlocksAir
            || !scan.Shape(info).TryGet(out var shape))
            return;
        foreach (var spot in shape.Spots)
        {
            if (!SafetyScan.IsFace(spot.Slot))
                continue;
            var (negative, positive) = scan.SidesOf(spot);
            var a = scan.RoomAt(negative);
            var b = scan.RoomAt(positive);
            if (a.TryGet(out var ra) && b.TryGet(out var rb) && ra.Ref == rb.Ref)
                continue;
            Side(scan, ghost, info, a, b.IsNone, scan.Oxidisers);
            Side(scan, ghost, info, b, a.IsNone, scan.Oxidisers);
        }
    }

    private static void Side(SafetyScan scan, Ghost ghost, StructureInfo taken, Option<RoomInfo> side, bool otherIsOutside, HashSet<GasKind> oxidisers)
    {
        if (!side.TryGet(out var room))
            return;
        var air = room.Air.TryGet(out var gases) ? gases : ValueList<GasHeld>.Empty;
        var moles = Oxidisers.Total(air);
        var about = "room " + SafetyScan.Text(room.Ref.Value);
        if (ghost.Intent is Intent.Swap && moles > 0.0)
            scan.Warnings.Add(SafetyCodes.SwapOnAirRoom, about, new Dictionary<string, string>
            {
                ["room"] = SafetyScan.Text(room.Ref.Value),
                ["structure"] = SafetyScan.Text(taken.Ref.Id.Value),
                ["mol"] = SafetyScan.Text(moles),
            }, ghost.Id);
        var oxidiser = Oxidisers.In(air, oxidisers);
        if (otherIsOutside && oxidiser > 0.0)
            scan.Warnings.Add(SafetyCodes.OxidiserOutside, about, new Dictionary<string, string>
            {
                ["room"] = SafetyScan.Text(room.Ref.Value),
                ["structure"] = SafetyScan.Text(taken.Ref.Id.Value),
                ["oxidiser-mol"] = SafetyScan.Text(oxidiser),
            }, ghost.Id);
        if (scan.Facts.Temperature(room.Ref).TryGet(out var kelvin) && kelvin.Value >= scan.Limits.HotGas.Value)
            scan.Warnings.Add(SafetyCodes.HotGas, about, new Dictionary<string, string>
            {
                ["room"] = SafetyScan.Text(room.Ref.Value),
                ["structure"] = SafetyScan.Text(taken.Ref.Id.Value),
                ["kelvin"] = SafetyScan.Text(kelvin.Value),
            }, ghost.Id);
    }
}

/// <summary>
/// Painting indoors: each spray of a spray can adds pollutant to the air where it is used (<c>SprayCan.PollutionMixture</c>,
/// Assets.Scripts.Objects.Items/SprayCan.cs). A ghost whose finish asks for a colour on a piece in a room warns, one
/// warning per room. A face piece counts when a room is on either side.
/// </summary>
internal static class Paint
{
    public static void Visit(SafetyScan scan, Ghost ghost)
    {
        if (!ColourOf(ghost) || !Where(scan, ghost).TryGet(out var placement) || !scan.Shape(placement).TryGet(out var shape))
            return;
        var grid = shape.Piece.Grid;
        foreach (var spot in shape.Spots)
        {
            if (SafetyScan.IsFace(spot.Slot))
            {
                var (negative, positive) = scan.SidesOf(spot);
                Report(scan, ghost, scan.RoomAt(negative));
                Report(scan, ghost, scan.RoomAt(positive));
            }
            else
            {
                Report(scan, ghost, scan.RoomAt(scan.LargeOf(spot.Cell, grid)));
            }
        }
    }

    public static bool ColourOf(Ghost ghost) => ghost.Intent switch
    {
        Intent.Build build => build.Finish.TryGet(out var finish) && finish.Colour.IsSome,
        Intent.Swap swap => swap.Finish.TryGet(out var finish) && finish.Colour.IsSome,
        Intent.FinishOnly only => only.Finish.Colour.IsSome,
        _ => false,
    };

    public static Option<Placement> Where(SafetyScan scan, Ghost ghost)
    {
        if (SafetyScan.Put(ghost).TryGet(out var put))
            return Option.Some(put);
        return ghost.Intent is Intent.FinishOnly only && scan.World.Structure(only.Target.Id).TryGet(out var info)
            ? Option.Some(new Placement(info.Ref.Piece, info.Ref.At, info.Turn))
            : Option<Placement>.None;
    }

    private static void Report(SafetyScan scan, Ghost ghost, Option<RoomInfo> room)
    {
        if (room.TryGet(out var inside))
            scan.Warnings.Add(SafetyCodes.PaintIndoors, "room " + SafetyScan.Text(inside.Ref.Value),
                new Dictionary<string, string> { ["room"] = SafetyScan.Text(inside.Ref.Value) }, ghost.Id);
    }
}

/// <summary>
/// Terrain in the way (warn, never dig): a build or swap ghost whose piece takes a large cell that
/// terrain fills, one warning per ghost with the first such cell.
/// </summary>
internal static class Terrain
{
    public static void Visit(SafetyScan scan, Ghost ghost)
    {
        if (!SafetyScan.Put(ghost).TryGet(out var placement) || !scan.Shape(placement).TryGet(out var shape))
            return;
        foreach (var spot in shape.Spots)
        {
            var large = scan.LargeOf(spot.Cell, shape.Piece.Grid);
            if (!scan.Facts.Terrain(large))
                continue;
            scan.Warnings.Add(SafetyCodes.TerrainInTheWay, "ghost " + SafetyScan.Text(ghost.Id.Value),
                new Dictionary<string, string> { ["cell"] = SafetyScan.Text(large) }, ghost.Id);
            break;
        }
    }
}
