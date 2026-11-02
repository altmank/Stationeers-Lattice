using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.TestKit;
using Lattice.Core.Board;
using Lattice.Core.Catalogue;
using Lattice.Core.Grid;
using Lattice.Core.Ports;
using Lattice.TestKit.Catalogue;

namespace Lattice.BigBase;

/// <summary>
/// Generates a big base from a seed, laid out as a player builds one: pairs of room rows facing a corridor, the rows
/// dealt over several storeys; each room a floor and a ceiling of frames with walls (and some windows) round its sides,
/// a doorway two cells high in the wall run to the corridor and at times a partition wall with its own doorway; a pipe
/// trunk and a cable trunk down each corridor with a branch through every doorway, shafts carrying the pipe of each
/// corridor stack and the power grid up through the storeys; devices on the room floors whose gas and power ends are
/// piped and wired into those branches, chute devices joined by chutes in some rooms, and the on-pipe devices on
/// straight pipes. Every piece, kit and count comes from the catalogue and the <see cref="BaseMix"/>; each piece stands
/// on its grid's snap, in a turn its piece allows, and takes spots no other piece takes. The same catalogue, mix and
/// seed give the same base. A device whose ends cannot all be run into its networks is not placed. A family the mix or the catalogue lacks is left out, not an error, unless the mix targets it.
/// </summary>
internal sealed partial class BaseGenerator
{
    private const int Small = 5;
    private const int TurnCost = 4;
    private const int ClimbCost = 3;

    // A run to a device end may be this many cells long however little of the room's budget is left: a joined end
    // matters more than the count of the family.
    private const int Detour = 30;

    // The cells a room's branch runs on into the room from its doorway, the bus its devices are set beside.
    private const int Bus = 6;

    private readonly FakeCatalogue _catalogue;
    private readonly BaseMix _mix;
    private readonly BaseLayout _layout;
    private readonly Random _random;
    private readonly CubeGrid _grid = new();
    private readonly Shapes _shapes;
    private readonly Dictionary<string, NamedPiece> _named = new(StringComparer.Ordinal);
    private readonly List<BasePiece> _pieces = new(40000);
    private readonly List<int> _built = new(40000);
    private readonly Dictionary<int, Corridor> _firstOfStorey = new();
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly HashSet<SpotKey> _largeSpots = new();
    private readonly HashSet<Cell> _devices = new();
    private readonly Dictionary<Cell, RunCell> _runs = new();
    private readonly Dictionary<RunKind, int> _runCounts = new();
    private readonly List<Net> _nets = new();
    private readonly List<Room> _rooms = new();
    private readonly List<Corridor> _corridors = new();
    private readonly List<int> _storeyY = new();
    private readonly Dictionary<(string Piece, Direction Out), Turn> _faceTurns = new();
    private bool _chutesPossible;
    private int _faces;
    private int _windows;
    private int _frames;
    private int _dropped;
    private int _step;

    public BaseGenerator(FakeCatalogue catalogue, BaseMix mix, int seed, BaseLayout? layout = null)
    {
        _catalogue = catalogue;
        _mix = mix;
        _layout = layout ?? BaseLayout.Default;
        _random = new Random(seed);
        _shapes = new Shapes(catalogue, _grid);
        foreach (var piece in catalogue.Pieces)
            _named[piece.Name] = piece;
    }

    /// <summary>The base, or what in the catalogue or the mix stops it.</summary>
    public Result<GeneratedBase, string> Generate()
    {
        if (!Palette(PieceFamilies.Frame, out var frames) || !Palette(PieceFamilies.Wall, out var walls) || !Palette(PieceFamilies.Window, out var windows))
            return Result<GeneratedBase, string>.Error("the mix names no catalogue piece for frames, walls or windows");
        if (!Kits(PieceFamilies.Pipe, out var pipeKits) || !Kits(PieceFamilies.Cable, out var cableKits) || !Kits(PieceFamilies.Chute, out var chuteKits))
            return Result<GeneratedBase, string>.Error("the mix names no catalogue kit with a straight and a corner for pipes, cables or chutes");

        _chutesPossible = chuteKits.Count > 0;
        Structure(frames, walls, windows);
        Trunks(pipeKits, cableKits);
        var floorDevices = FloorDevices();
        if (floorDevices.Machines.Count == 0 && _mix.Target(PieceFamilies.Device) > 0)
            return Result<GeneratedBase, string>.Error("the catalogue holds no device that stands on a frame floor with a gas, power or chute end");
        Furnish(floorDevices, chuteKits);
        Mounted();
        OnPipe();
        EmitRuns();
        var built = InBuildOrder();
        return Result<GeneratedBase, string>.Ok(new GeneratedBase(
            ValueList.From(built), ValueMap.From(_counts), _rooms.Count, StoreysUsed(), NetworkCounts(), _dropped, StandSpot(built)));
    }

    // The pieces in the order a player builds them: storey by storey, corridor by corridor, the corridor's own pieces and
    // then each room's; within a step, frames, walls, windows, pipes, cables, chutes, devices. Ties keep emission order.
    private List<BasePiece> InBuildOrder()
    {
        var order = new int[_pieces.Count];
        for (var i = 0; i < order.Length; i++)
            order[i] = i;
        Array.Sort(order, (a, b) =>
        {
            var by = _built[a].CompareTo(_built[b]);
            return by != 0 ? by : a.CompareTo(b);
        });
        var built = new List<BasePiece>(order.Length);
        foreach (var index in order)
            built.Add(_pieces[index]);
        return built;
    }

    private static int Step(Corridor corridor, int slot) => (corridor.Storey * 1000 + corridor.Stack) * 1000 + slot;

    private static int StepOf(Room room) => Step(room.Corridor, 1 + room.Corridor.Rooms.IndexOf(room));

    private static int GroupRank(PieceFamily family) =>
        family == PieceFamilies.Frame ? 0 :
        family == PieceFamilies.Wall ? 1 :
        family == PieceFamilies.Window ? 2 :
        family == PieceFamilies.Pipe ? 3 :
        family == PieceFamilies.Cable ? 4 :
        family == PieceFamilies.Chute ? 5 :
        family == PieceFamilies.Device ? 6 : 7;

    // The step of a run cell: the room whose bounds hold it, else its corridor; the spines and shafts west of the rooms
    // go with the first corridor of their storey.
    private int StepAt(Cell cell)
    {
        var storey = 0;
        for (var s = 1; s < _storeyY.Count; s++)
            if (cell.Y >= 20 * _storeyY[s])
                storey = s;
        var first = _firstOfStorey.TryGetValue(storey, out var found) ? found : _corridors[0];
        if (cell.X < 20 * _layout.OriginX)
            return Step(first, 0);
        foreach (var corridor in _corridors)
        {
            if (corridor.Storey != storey)
                continue;
            foreach (var room in corridor.Rooms)
                if (cell.X >= 20 * room.X0 && cell.X <= 20 * (room.X0 + room.W) && cell.Z >= 20 * room.Z0 && cell.Z <= 20 * (room.Z0 + room.D))
                    return StepOf(room);
            if (cell.Z >= 20 * corridor.Zc && cell.Z <= 20 * (corridor.Zc + 2))
                return Step(corridor, 0);
        }

        return Step(first, 0);
    }

    private ValueMap<string, int> NetworkCounts()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var net in _nets)
            if (net.Cells > 0)
            {
                var family = Family(net.Kind).Value;
                counts[family] = (counts.TryGetValue(family, out var count) ? count : 0) + 1;
            }

        return ValueMap.From(counts);
    }

    private static PieceFamily Family(RunKind kind) => kind switch
    {
        RunKind.Pipe => PieceFamilies.Pipe,
        RunKind.Cable => PieceFamilies.Cable,
        _ => PieceFamilies.Chute,
    };

    // ---- the catalogue

    private bool Palette(PieceFamily family, out List<Weighted> palette)
    {
        palette = new List<Weighted>();
        foreach (var weighted in BaseMix.Of(_mix.Pieces, family))
            if (_named.TryGetValue(weighted.Name, out var piece) && piece.Facts.Grid == GridSize.Large && weighted.Weight > 0)
                palette.Add(weighted);
        return palette.Count > 0 || _mix.Target(family) == 0;
    }

    private bool Kits(PieceFamily family, out List<Kit> kits)
    {
        kits = new List<Kit>();
        foreach (var weighted in BaseMix.Of(_mix.Kits, family))
            foreach (var line in _catalogue.KitLines)
            {
                if (line.Name != weighted.Name || weighted.Weight <= 0)
                    continue;
                var options = new List<NamedPiece>();
                foreach (var option in line.Options)
                    if (_named.TryGetValue(option, out var piece) && piece.Facts.Grid == GridSize.Small && OneCell(piece.Facts))
                        options.Add(piece);
                var kit = new Kit(line.Name, weighted.Weight, options, _grid);
                if (kit.Fits(Bit(Direction.ZPlus) | Bit(Direction.ZMinus)) && kit.Fits(Bit(Direction.ZPlus) | Bit(Direction.XPlus)))
                    kits.Add(kit);
            }

        return kits.Count > 0 || _mix.Target(family) == 0;
    }

    // Devices that stand on a frame floor in an upright turn and have an end to pipe, wire or chute, in name order.
    private DevicePalette FloorDevices()
    {
        var machines = new List<NamedPiece>();
        var chutes = new List<NamedPiece>();
        var names = new List<string>(_named.Keys);
        names.Sort(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var piece = _named[name];
            var facts = piece.Facts;
            if (facts.Family != PieceFamilies.Device || facts.Grid != GridSize.Small || facts.Kits.Count == 0 || facts.Face.IsSome ||
                facts.Turning.Placement != PlacedBy.Grid || Uprights(facts).Count == 0 || !OnFloor(facts))
                continue;
            var (gas, power, chute) = (false, false, false);
            foreach (var end in facts.Ends)
            {
                gas |= Has<NetworkType.GasPipe>(end.Kinds);
                power |= Has<NetworkType.Power>(end.Kinds);
                chute |= Has<NetworkType.Chute>(end.Kinds);
            }

            if (chute)
                chutes.Add(piece);
            else if (gas || power)
                machines.Add(piece);
        }

        return new DevicePalette(machines, chutes);
    }

    private static bool OnFloor(PieceFacts facts)
    {
        if (facts.SupportGroup.TryGet(out var group) && group != SupportGroups.FrameBelow && group != SupportGroups.FrameOrGround)
            return false;
        foreach (var cell in facts.Footprint)
            if (Math.Abs(cell.At.X) > 10 || Math.Abs(cell.At.Z) > 10 || cell.At.Y < 0 || cell.At.Y > 15)
                return false;
        return true;
    }

    private static bool OneCell(PieceFacts facts)
    {
        foreach (var end in facts.Ends)
            if (end.At != default)
                return false;
        foreach (var cell in facts.Footprint)
            if (cell.At != default)
                return false;
        return facts.Ends.Count >= 2;
    }

    private List<Turn> Uprights(PieceFacts facts)
    {
        var turns = new List<Turn>(4);
        for (var i = 0; i < Turn.Count; i++)
        {
            var turn = TurnAt(i);
            if (facts.AllowedTurns.Contains(turn) && _grid.Apply(turn, Direction.YPlus) == Direction.YPlus)
                turns.Add(turn);
        }

        return turns;
    }

    private static Option<Turn> FirstTurn(PieceFacts facts, Cell at)
    {
        if (!facts.Snap.Holds(at))
            return Option<Turn>.None;
        for (var i = 0; i < Turn.Count; i++)
            if (facts.AllowedTurns.Contains(TurnAt(i)))
                return Option.Some(TurnAt(i));
        return Option<Turn>.None;
    }

    // The palettes and kit lists passed in are never empty: callers choose from the ones the mix gave pieces for.
    private NamedPiece Pick(List<Weighted> palette)
    {
        var total = 0;
        foreach (var weighted in palette)
            total += weighted.Weight;
        var roll = _random.Next(total);
        foreach (var weighted in palette)
        {
            if (roll < weighted.Weight)
                return _named[weighted.Name];
            roll -= weighted.Weight;
        }

        return _named[palette[palette.Count - 1].Name];
    }

    private Kit Pick(List<Kit> kits)
    {
        var total = 0;
        foreach (var kit in kits)
            total += kit.Weight;
        var roll = _random.Next(total);
        foreach (var kit in kits)
        {
            if (roll < kit.Weight)
                return kit;
            roll -= kit.Weight;
        }

        return kits[kits.Count - 1];
    }

    // A new network of the kind from a weighted kit, or none when the mix has no kit for the family.
    private Net? NewNet(RunKind kind, List<Kit> kits)
    {
        if (kits.Count == 0)
            return null;
        var net = new Net(kind, Pick(kits));
        _nets.Add(net);
        return net;
    }

    private void Emit(NamedPiece piece, Placement placement)
    {
        _pieces.Add(new BasePiece(piece.Name, placement.At, placement.Turn, Math.Max(0, piece.Facts.Steps.Count - 1)));
        _built.Add((_step << 4) | GroupRank(piece.Facts.Family));
        var family = piece.Facts.Family.Value;
        _counts[family] = (_counts.TryGetValue(family, out var count) ? count : 0) + 1;
    }

    // ---- grid helpers

    private static readonly Direction[] AllDirections =
        [Direction.XPlus, Direction.XMinus, Direction.ZPlus, Direction.ZMinus, Direction.YPlus, Direction.YMinus];

    private static int Centre(int index) => 20 * index + 10;

    private static int FloorTop(int floor) => 20 * floor + 20;

    private static CellOffset Scaled(Direction direction, int by) => direction.Unit().Times(by);

    internal static int Bit(Direction direction) => 1 << ((int)direction - 1);

    private static int Count(int mask)
    {
        var count = 0;
        for (var m = mask; m != 0; m &= m - 1)
            count++;
        return count;
    }

    private static Direction DirectionOf(int mask)
    {
        foreach (var direction in AllDirections)
            if ((mask & Bit(direction)) != 0)
                return direction;
        throw new InvalidOperationException("no direction in the mask");
    }

    private static bool IsStraight(int mask) =>
        mask == (Bit(Direction.XPlus) | Bit(Direction.XMinus)) ||
        mask == (Bit(Direction.YPlus) | Bit(Direction.YMinus)) ||
        mask == (Bit(Direction.ZPlus) | Bit(Direction.ZMinus));

    private static Turn TurnAt(int index) => Turn.Create(index).TryGet(out var turn) ? turn : Turn.None;

    private static bool Has<T>(ValueSet<NetworkType> kinds) where T : NetworkType
    {
        foreach (var kind in kinds)
            if (kind is T)
                return true;
        return false;
    }

    private readonly record struct State(Cell Cell, Direction Travel, bool Join);

    private readonly record struct Budget(int Pipe, int Cable, int Chute);

    private sealed record DevicePalette(List<NamedPiece> Machines, List<NamedPiece> Chutes);
}
