using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Catalogue;
using Lattice.Core.Grid;
using Lattice.Core.Ports;
using Lattice.TestKit.Catalogue;

namespace Lattice.TestKit.BigBase;

/// <summary>
/// Where a generated base stands and how its rooms run: the large cell (indices, a cell's centre at 20 * i + 10 dm) of
/// its first corner, room columns per corridor, the room sizes in large cells and the room heights.
/// </summary>
internal sealed record BaseLayout(int OriginX, int OriginY, int OriginZ)
{
    /// <summary>A base 601 m east and north and 301 m up, inside the area a new world's base is spawned into.</summary>
    public static BaseLayout Default => new(300, 150, 300);

    public int Columns { get; init; } = 8;

    public int ColumnPitch { get; init; } = 10;

    public int RoomMin { get; init; } = 3;

    public int RoomMax { get; init; } = 8;

    public int HeightMin { get; init; } = 2;

    public int HeightMax { get; init; } = 5;
}

/// <summary>A generated base: its structures in file order, and the counts per catalogue family.</summary>
internal sealed record GeneratedBase(ValueList<BasePiece> Pieces, ValueMap<string, int> Counts, int Rooms);

/// <summary>
/// Generates a big base from a seed, laid out as a player builds one: pairs of room rows facing a corridor, each room
/// a floor and a ceiling of frames with walls (and some windows) round its sides, a door to the corridor and at times a
/// partition wall with its own door; a pipe trunk and a cable trunk down each corridor with a branch through every
/// door; devices on the room floors whose gas and power ends are piped and wired into those branches, chute devices
/// joined by chutes in some rooms, and the on-pipe devices on straight pipes. Every piece, kit and count comes from the
/// catalogue and the <see cref="BaseMix"/>; each piece stands on its grid's snap, in a turn its piece allows, and takes
/// spots no other piece takes. The same catalogue, mix and seed give the same base.
/// </summary>
internal sealed class BaseGenerator
{
    private const int Small = 5;
    private const int TurnCost = 4;
    private const int ClimbCost = 3;

    private readonly FakeCatalogue _catalogue;
    private readonly BaseMix _mix;
    private readonly BaseLayout _layout;
    private readonly Random _random;
    private readonly CubeGrid _grid = new();
    private readonly Shapes _shapes;
    private readonly Dictionary<string, NamedPiece> _named = new(StringComparer.Ordinal);
    private readonly List<BasePiece> _pieces = new(40000);
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly HashSet<SpotKey> _largeSpots = new();
    private readonly HashSet<Cell> _devices = new();
    private readonly Dictionary<Cell, RunCell> _runs = new();
    private readonly Dictionary<RunKind, int> _runCounts = new();
    private readonly List<Room> _rooms = new();
    private readonly List<Corridor> _corridors = new();
    private readonly Dictionary<(string Piece, Direction Out), Turn> _faceTurns = new();
    private int _faces;
    private int _windows;
    private int _frames;

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

        Structure(frames, walls, windows);
        Trunks(pipeKits, cableKits);
        var floorDevices = FloorDevices();
        if (floorDevices.Machines.Count == 0)
            return Result<GeneratedBase, string>.Error("the catalogue holds no device that stands on a frame floor with a gas, power or chute end");
        Furnish(floorDevices, chuteKits);
        OnPipe();
        EmitRuns();
        return Result<GeneratedBase, string>.Ok(new GeneratedBase(ValueList.From(_pieces), ValueMap.From(_counts), _rooms.Count));
    }

    // ---- frames, walls and windows

    private void Structure(List<Weighted> frames, List<Weighted> walls, List<Weighted> windows)
    {
        var frameTarget = _mix.Target(PieceFamilies.Frame);
        var faceTarget = _mix.Target(PieceFamilies.Wall) + _mix.Target(PieceFamilies.Window);
        var ratio = frameTarget == 0 ? 0.0 : (double)faceTarget / frameTarget;
        var windowShare = faceTarget == 0 ? 0.0 : (double)_mix.Target(PieceFamilies.Window) / faceTarget;
        var y0 = _layout.OriginY;
        var pairPitch = _layout.RoomMax * 2 + 4;
        for (var pair = 0; _frames < frameTarget; pair++)
        {
            var zc = _layout.OriginZ + pair * pairPitch + _layout.RoomMax;
            var x0 = _layout.OriginX;
            var x1 = _layout.OriginX + _layout.Columns * _layout.ColumnPitch;
            var corridor = new Corridor(zc, y0);
            var spineFrom = _corridors.Count == 0 ? zc : _corridors[_corridors.Count - 1].Zc + 2;
            _corridors.Add(corridor);
            for (var x = x0; x < x1; x++)
                for (var dz = 0; dz < 2; dz++)
                    Frame(frames, new Cell(Centre(x), Centre(y0), Centre(zc + dz)));
            for (var z = spineFrom; z < zc + 2; z++)
                for (var dx = 1; dx <= 2; dx++)
                    Frame(frames, new Cell(Centre(x0 - dx), Centre(y0), Centre(z)));

            for (var column = 0; column < _layout.Columns && _frames < frameTarget; column++)
                for (var side = 0; side < 2 && _frames < frameTarget; side++)
                {
                    var w = _random.Next(_layout.RoomMin, _layout.RoomMax + 1);
                    var d = _random.Next(_layout.RoomMin, _layout.RoomMax + 1);
                    var rx = x0 + column * _layout.ColumnPitch + _random.Next(0, _layout.ColumnPitch - w);
                    var facing = side == 0 ? 1 : -1;
                    var rz = side == 0 ? zc - d : zc + 2;
                    var (h, partition) = Shape(w, d, ratio);
                    var door = rx + _random.Next(0, w);
                    var room = new Room(rx, rz, w, d, h, y0, door, facing, partition, corridor, _random.Next(4) == 0);
                    _rooms.Add(room);
                    corridor.Rooms.Add(room);
                    Room(room, frames, walls, windows, windowShare);
                }
        }
    }

    // The room height and partition that keep walls per frame nearest the mix's.
    private (int Height, Option<Partition> Partition) Shape(int w, int d, double ratio)
    {
        var best = (Height: _layout.HeightMin, Partition: Option<Partition>.None);
        var bestError = double.MaxValue;
        for (var h = _layout.HeightMin; h <= _layout.HeightMax; h++)
            for (var split = 0; split < 2; split++)
            {
                if (split == 1 && d < 4)
                    continue;
                var faces = 2 * (w + d) * h - 2 + (split == 1 ? w * h - 2 : 0);
                var frames = 2 * w * d;
                var error = Math.Abs((double)(_faces + faces) / (_frames + frames) - ratio) + _random.NextDouble() * 0.01;
                if (error < bestError)
                {
                    bestError = error;
                    best = (h, split == 1 ? Option.Some(new Partition(_random.Next(2, d - 1), _random.Next(0, w))) : Option<Partition>.None);
                }
            }

        return best;
    }

    private void Room(Room room, List<Weighted> frames, List<Weighted> walls, List<Weighted> windows, double windowShare)
    {
        for (var x = room.X0; x < room.X0 + room.W; x++)
            for (var z = room.Z0; z < room.Z0 + room.D; z++)
            {
                Frame(frames, new Cell(Centre(x), Centre(room.Y0), Centre(z)));
                Frame(frames, new Cell(Centre(x), Centre(room.Y0 + room.H + 1), Centre(z)));
            }

        for (var j = 1; j <= room.H; j++)
        {
            var y = Centre(room.Y0 + j);
            var door = j <= 2;
            for (var x = room.X0; x < room.X0 + room.W; x++)
            {
                if (!(door && x == room.DoorX && room.Facing < 0))
                    Face(walls, windows, windowShare, new Cell(Centre(x), y, Centre(room.Z0)), Direction.ZMinus);
                if (!(door && x == room.DoorX && room.Facing > 0))
                    Face(walls, windows, windowShare, new Cell(Centre(x), y, Centre(room.Z0 + room.D - 1)), Direction.ZPlus);
                if (room.Partition.TryGet(out var p) && !(door && x == room.X0 + p.DoorAt))
                    Face(walls, windows, 0, new Cell(Centre(x), y, Centre(room.Z0 + p.At - 1)), Direction.ZPlus);
            }

            for (var z = room.Z0; z < room.Z0 + room.D; z++)
            {
                Face(walls, windows, windowShare, new Cell(Centre(room.X0), y, Centre(z)), Direction.XMinus);
                Face(walls, windows, windowShare, new Cell(Centre(room.X0 + room.W - 1), y, Centre(z)), Direction.XPlus);
            }
        }
    }

    private void Frame(List<Weighted> palette, Cell at)
    {
        var piece = Pick(palette);
        if (!FirstTurn(piece.Facts, at).TryGet(out var turn) || !_largeSpots.Add(new SpotKey(at, Slot.Centre)))
            return;
        Emit(piece, new Placement(piece.Facts.Key, at, turn));
        _frames++;
    }

    // A wall or window on the face of the large cell toward the outward side: the turn that lays it there, found once a
    // piece and side and kept.
    private void Face(List<Weighted> walls, List<Weighted> windows, double windowShare, Cell large, Direction outward)
    {
        var window = windowShare > 0 && _windows < windowShare * (_faces + 1) - 1 + _random.NextDouble() * 2;
        var piece = Pick(window ? windows : walls);
        var boundary = Faces.Between(large, outward, _grid);
        if (_largeSpots.Contains(boundary.Near) || _largeSpots.Contains(boundary.Far))
            return;
        var at = large.Plus(Scaled(outward, 10));
        if (!FaceTurn(piece, at, boundary, outward).TryGet(out var turn) ||
            !_shapes.Of(new Placement(piece.Facts.Key, at, turn)).TryGet(out var shape))
            return;
        foreach (var spot in shape.Spots)
            _largeSpots.Add(spot);
        Emit(piece, new Placement(piece.Facts.Key, at, turn));
        _faces++;
        if (window)
            _windows++;
    }

    private Option<Turn> FaceTurn(NamedPiece piece, Cell at, Boundary boundary, Direction outward)
    {
        if (!piece.Facts.Snap.Holds(at))
            return Option<Turn>.None;
        if (_faceTurns.TryGetValue((piece.Name, outward), out var known) && OnBoundary(piece, at, known, boundary))
            return Option.Some(known);
        foreach (var upright in new[] { true, false })
            for (var i = 0; i < Turn.Count; i++)
            {
                var turn = TurnAt(i);
                if (!piece.Facts.AllowedTurns.Contains(turn) || (_grid.Apply(turn, Direction.YPlus) == Direction.YPlus) != upright)
                    continue;
                if (!OnBoundary(piece, at, turn, boundary))
                    continue;
                _faceTurns[(piece.Name, outward)] = turn;
                return Option.Some(turn);
            }

        return Option<Turn>.None;
    }

    private bool OnBoundary(NamedPiece piece, Cell at, Turn turn, Boundary boundary) =>
        piece.Facts.AllowedTurns.Contains(turn) &&
        _shapes.Of(new Placement(piece.Facts.Key, at, turn)).TryGet(out var shape) &&
        shape.Spots.Count == 1 && boundary.Holds(shape.Spots[0]);

    // ---- networks

    // A gas network per corridor; one power grid for the base, its corridor trunks joined down the spine.
    private void Trunks(List<Kit> pipeKits, List<Kit> cableKits)
    {
        var grid = new Net(RunKind.Cable, Pick(cableKits));
        var spineX = Centre(_layout.OriginX - 1);
        var spine = new List<int>();
        foreach (var corridor in _corridors)
        {
            corridor.Pipe = new Net(RunKind.Pipe, Pick(pipeKits));
            corridor.Cable = grid;
            var floor = FloorTop(corridor.Y0);
            var trunkZ = 20 * corridor.Zc + 20;
            Trunk(corridor, corridor.Pipe, floor, trunkZ, static r => r.Facing > 0 ? 5 : 15, Option<int>.None);
            if (Trunk(corridor, grid, floor + Small, trunkZ, static r => r.Facing > 0 ? 15 : 5, Option.Some(spineX)))
                spine.Add(trunkZ);
        }

        if (spine.Count > 1)
        {
            var y = FloorTop(_layout.OriginY) + Small;
            for (var z = spine[0]; z < spine[spine.Count - 1]; z += Small)
                Connect(grid, new Cell(spineX, y, z), new Cell(spineX, y, z + Small));
        }
    }

    // A straight run down the corridor between its outermost branches, and a branch from it through every room's door
    // to the room's first cell inside.
    private bool Trunk(Corridor corridor, Net net, int y, int z, Func<Room, int> lane, Option<int> westTo)
    {
        if (corridor.Rooms.Count == 0)
            return false;
        var min = westTo.Reduce(int.MaxValue);
        var max = int.MinValue;
        foreach (var room in corridor.Rooms)
        {
            var x = 20 * room.DoorX + lane(room);
            min = Math.Min(min, x);
            max = Math.Max(max, x);
        }

        for (var x = min; x < max; x += Small)
            Connect(net, new Cell(x, y, z), new Cell(x + Small, y, z));
        foreach (var room in corridor.Rooms)
        {
            var x = 20 * room.DoorX + lane(room);
            var inside = room.Facing > 0 ? 20 * corridor.Zc - Small : 20 * (corridor.Zc + 2) + Small;
            var step = room.Facing > 0 ? Small : -Small;
            for (var at = inside; at != z; at += step)
                Connect(net, new Cell(x, y, at), new Cell(x, y, at + step));
        }

        return true;
    }

    private void Furnish(DevicePalette devices, List<Kit> chuteKits)
    {
        var pipeTarget = _mix.Target(PieceFamilies.Pipe);
        var cableTarget = _mix.Target(PieceFamilies.Cable);
        var chuteTarget = _mix.Target(PieceFamilies.Chute);
        var floorTarget = _mix.Target(PieceFamilies.Device) - OnPipeTarget();
        var placed = 0;
        var factories = 0;
        foreach (var room in _rooms)
            if (room.Factory)
                factories++;
        for (var i = 0; i < _rooms.Count; i++)
        {
            var room = _rooms[i];
            var left = _rooms.Count - i;
            var budget = new Budget(
                Share(pipeTarget - Count(RunKind.Pipe), left),
                Share(cableTarget - Count(RunKind.Cable), left),
                room.Factory ? Share(chuteTarget - Count(RunKind.Chute), factories--) : 0);
            var chute = room.Factory ? new Net(RunKind.Chute, Pick(chuteKits)) : null;
            var want = Share(floorTarget - placed, left);
            var (pipe, cable) = (room.Corridor.Pipe!, room.Corridor.Cable!);
            var (pipeFrom, cableFrom) = (pipe.Cells, cable.Cells);
            placed += Devices(room, room.Factory ? devices.Chutes : devices.Machines, want, budget, chute);
            Spare(room, pipe, pipeFrom + budget.Pipe);
            Spare(room, cable, cableFrom + budget.Cable);
            if (chute is not null)
                Spare(room, chute, budget.Chute);
        }
    }

    private int Devices(Room room, List<NamedPiece> palette, int want, Budget budget, Net? chute)
    {
        if (palette.Count == 0)
            return 0;
        var placed = 0;
        var pipeStart = Count(RunKind.Pipe);
        var cableStart = Count(RunKind.Cable);
        var chuteStart = Count(RunKind.Chute);
        for (var attempt = 0; attempt < 80 && placed < want; attempt++)
        {
            var piece = palette[_random.Next(palette.Count)];
            var uprights = Uprights(piece.Facts);
            if (uprights.Count == 0)
                continue;
            var turn = uprights[_random.Next(uprights.Count)];
            var at = new Cell(
                20 * room.X0 + Small * _random.Next(1, 4 * room.W),
                FloorTop(room.Y0),
                20 * room.Z0 + Small * _random.Next(1, 4 * room.D));
            var placement = new Placement(piece.Facts.Key, at, turn);
            if (!piece.Facts.Snap.Holds(at) || !_shapes.Of(placement).TryGet(out var shape) || !Fits(room, shape))
                continue;
            foreach (var spot in shape.Spots)
                _devices.Add(spot.Cell);
            Emit(piece, placement);
            placed++;
            foreach (var end in shape.Ends)
            {
                var net = NetFor(room, end, piece.Facts, shape, chute);
                if (net is null)
                    continue;
                var spent = net.Kind switch
                {
                    RunKind.Pipe => Count(RunKind.Pipe) - pipeStart,
                    RunKind.Cable => Count(RunKind.Cable) - cableStart,
                    _ => Count(RunKind.Chute) - chuteStart,
                };
                var allowed = net.Kind switch { RunKind.Pipe => budget.Pipe, RunKind.Cable => budget.Cable, _ => budget.Chute } - spent;
                if (allowed > 0)
                    Route(room, net, end.Cell.Plus(Scaled(end.Facing, Small)), end.Facing, allowed, seed: net.Kind == RunKind.Chute);
            }
        }

        return placed;
    }

    private bool Fits(Room room, PlacedShape shape)
    {
        foreach (var spot in shape.Spots)
            if (!Inside(room, spot.Cell) || _devices.Contains(spot.Cell) || _runs.ContainsKey(spot.Cell))
                return false;
        foreach (var end in shape.Ends)
        {
            var next = end.Cell.Plus(Scaled(end.Facing, Small));
            if (_devices.Contains(next))
                return false;
        }

        return true;
    }

    private Net? NetFor(Room room, WorldEnd end, PieceFacts piece, PlacedShape shape, Net? chute)
    {
        foreach (var e in piece.Ends)
        {
            if (_shapes.Leaving(shape.Placement, e) != (end.Cell, end.Facing))
                continue;
            if (Has<NetworkType.GasPipe>(e.Kinds))
                return room.Corridor.Pipe;
            if (Has<NetworkType.Power>(e.Kinds))
                return room.Corridor.Cable;
            if (Has<NetworkType.Chute>(e.Kinds))
                return chute;
        }

        return null;
    }

    // Spare runs once the room's devices are joined: a branch from beside a wall to the network, as a player lays a
    // run to come back to, while the room's budget lasts.
    private void Spare(Room room, Net net, int limit)
    {
        for (var attempt = 0; attempt < 40 && net.Cells < limit; attempt++)
        {
            var (start, inward) = BesideAWall(room, net);
            if (!Inside(room, start) || _devices.Contains(start) || _runs.ContainsKey(start))
                continue;
            Route(room, net, start, inward, limit - net.Cells, seed: false);
        }
    }

    private (Cell Start, Direction Inward) BesideAWall(Room room, Net net)
    {
        var y = FloorTop(room.Y0) + (net.Kind == RunKind.Cable ? Small : 0);
        var x = 20 * room.X0 + Small * _random.Next(1, 4 * room.W);
        var z = 20 * room.Z0 + Small * _random.Next(1, 4 * room.D);
        return _random.Next(4) switch
        {
            0 => (new Cell(20 * room.X0 + Small, y, z), Direction.XPlus),
            1 => (new Cell(20 * (room.X0 + room.W) - Small, y, z), Direction.XMinus),
            2 => (new Cell(x, y, 20 * room.Z0 + Small), Direction.ZPlus),
            _ => (new Cell(x, y, 20 * (room.Z0 + room.D) - Small), Direction.ZMinus),
        };
    }

    // The cheapest path (a step costs 1, a turn and a climb more) from the cell an end leaves into, travelling out of it,
    // to a cell of the same network whose piece can take one more end; laid when it is no longer than the budget. A
    // chute end with nothing to reach starts a network of its own (seed).
    private void Route(Room room, Net net, Cell start, Direction outward, int budget, bool seed)
    {
        var back = Bit(outward.Opposite());
        if (_runs.TryGetValue(start, out var here))
        {
            if (here.Net == net && net.Fits(here.Mask | back))
                here.Mask |= back;
            return;
        }

        if (!Inside(room, start) || _devices.Contains(start))
            return;
        var path = Search(room, net, start, outward, budget);
        if (path is null)
        {
            if (seed)
                AddRun(net, start, back);
            return;
        }

        var (cells, join) = path.Value;
        var seen = new HashSet<Cell>(cells);
        if (seen.Count != cells.Count || seen.Contains(join))
            return;
        AddRun(net, cells[0], back);
        for (var i = 1; i < cells.Count; i++)
            Connect(net, cells[i - 1], cells[i]);
        Connect(net, cells[cells.Count - 1], join);
    }

    private (List<Cell> Cells, Cell Join)? Search(Room room, Net net, Cell start, Direction outward, int budget)
    {
        var buckets = new List<List<State>>();
        var best = new Dictionary<(Cell, Direction), int>();
        var from = new Dictionary<(Cell, Direction), (Cell, Direction)>();
        var length = new Dictionary<(Cell, Direction), int>();
        Push(buckets, 0, new State(start, outward, false));
        best[(start, outward)] = 0;
        length[(start, outward)] = 1;
        for (var cost = 0; cost < buckets.Count; cost++)
        {
            var bucket = buckets[cost];
            for (var b = 0; b < bucket.Count; b++)
            {
                var state = bucket[b];
                var key = (state.Cell, state.Travel);
                if (state.Join)
                    return (Walk(from, key, start, outward, out var join), join);
                if (best[key] != cost)
                    continue;
                var inBit = Bit(state.Travel.Opposite());
                foreach (var next in AllDirections)
                {
                    if (next == state.Travel.Opposite() || !net.Fits(inBit | Bit(next)))
                        continue;
                    var cell = state.Cell.Plus(Scaled(next, Small));
                    if (!Inside(room, cell))
                        continue;
                    var nextKey = (cell, next);
                    var step = cost + 1 + (next != state.Travel ? TurnCost : 0) + (next is Direction.YPlus or Direction.YMinus ? ClimbCost : 0);
                    if (_runs.TryGetValue(cell, out var run))
                    {
                        if (run.Net != net || !net.Fits(run.Mask | Bit(next.Opposite())))
                            continue;
                        if (!best.TryGetValue(nextKey, out var had) || step < had)
                        {
                            best[nextKey] = step;
                            from[nextKey] = key;
                            Push(buckets, step, new State(cell, next, true));
                        }

                        continue;
                    }

                    if (_devices.Contains(cell) || length[key] + 1 > budget)
                        continue;
                    if (!best.TryGetValue(nextKey, out var known) || step < known)
                    {
                        best[nextKey] = step;
                        from[nextKey] = key;
                        length[nextKey] = length[key] + 1;
                        Push(buckets, step, new State(cell, next, false));
                    }
                }
            }
        }

        return null;
    }

    private static List<Cell> Walk(Dictionary<(Cell, Direction), (Cell, Direction)> from, (Cell, Direction) end, Cell start, Direction outward, out Cell join)
    {
        join = end.Item1;
        var cells = new List<Cell>();
        var at = from[end];
        while (true)
        {
            cells.Add(at.Item1);
            if (at == (start, outward))
                break;
            at = from[at];
        }

        cells.Reverse();
        return cells;
    }

    private static void Push(List<List<State>> buckets, int cost, State state)
    {
        while (buckets.Count <= cost)
            buckets.Add(new List<State>());
        buckets[cost].Add(state);
    }

    // The on-pipe devices along straight pipes, spread evenly over them.
    private void OnPipe()
    {
        var want = OnPipeTarget();
        if (want == 0)
            return;
        var straights = new List<(Cell Cell, RunCell Run)>();
        foreach (var entry in _runs)
            if (entry.Value.Net.Kind == RunKind.Pipe && IsStraight(entry.Value.Mask))
                straights.Add((entry.Key, entry.Value));
        if (straights.Count == 0)
            return;
        var pieces = new List<NamedPiece>();
        foreach (var weighted in _mix.OnPipe)
            if (_named.TryGetValue(weighted.Name, out var piece) && weighted.Weight > 0)
                pieces.Add(piece);
        if (pieces.Count == 0)
            return;
        var gap = Math.Max(1, straights.Count / want);
        var offset = _random.Next(gap);
        var done = 0;
        for (var i = offset; i < straights.Count && done < want; i += gap)
        {
            var piece = pieces[_random.Next(pieces.Count)];
            var (cell, run) = straights[i];
            if (!run.Net.Match(run.Mask).TryGet(out var pipe) || !Mount(piece, pipe, cell).TryGet(out var placement))
                continue;
            run.Mounted = true;
            Emit(piece, placement);
            done++;
        }
    }

    private Option<Placement> Mount(NamedPiece device, (NamedPiece Piece, Turn Turn) pipe, Cell at)
    {
        if ((device.Facts.Collision & pipe.Piece.Facts.Collision) != SmallCollision.None || device.Facts.Ends.Count > 0 || !device.Facts.Snap.Holds(at))
            return Option<Placement>.None;
        if (device.Facts.AllowedTurns.Contains(pipe.Turn))
            return Option.Some(new Placement(device.Facts.Key, at, pipe.Turn));
        return FirstTurn(device.Facts, at).Map(t => new Placement(device.Facts.Key, at, t));
    }

    private int OnPipeTarget()
    {
        var share = 0;
        foreach (var weighted in _mix.OnPipe)
            if (_named.ContainsKey(weighted.Name))
                share += weighted.Weight;
        return _mix.Target(PieceFamilies.Device) * Math.Min(share, 100) / 100;
    }

    private void EmitRuns()
    {
        foreach (var entry in _runs)
        {
            var mask = entry.Value.Mask;
            if (Count(mask) == 1)
                mask |= Bit(DirectionOf(mask).Opposite());
            if (entry.Value.Net.Match(mask).TryGet(out var match))
                Emit(match.Piece, new Placement(match.Piece.Facts.Key, entry.Key, match.Turn));
        }
    }

    private void Connect(Net net, Cell a, Cell b)
    {
        var along = DirectionMath.FromUnit(new CellOffset(Math.Sign(b.X - a.X), Math.Sign(b.Y - a.Y), Math.Sign(b.Z - a.Z)));
        AddRun(net, a, Bit(along));
        AddRun(net, b, Bit(along.Opposite()));
    }

    private void AddRun(Net net, Cell at, int bits)
    {
        if (_runs.TryGetValue(at, out var run))
        {
            run.Mask |= bits;
            return;
        }

        _runs[at] = new RunCell(net, bits);
        Tally(net.Kind);
        net.Cells++;
    }

    private void Tally(RunKind kind) => _runCounts[kind] = Count(kind) + 1;

    private int Count(RunKind kind) => _runCounts.TryGetValue(kind, out var count) ? count : 0;

    private static int Share(int left, int parts) => parts <= 0 || left <= 0 ? 0 : (left + parts - 1) / parts;

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

    private void Emit(NamedPiece piece, Placement placement)
    {
        _pieces.Add(new BasePiece(piece.Name, placement.At, placement.Turn, Math.Max(0, piece.Facts.Steps.Count - 1)));
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

    // Inside the room's walls, from the floor to under the ceiling, never on a partition's plane but at its door.
    private static bool Inside(Room room, Cell cell)
    {
        if (cell.X <= 20 * room.X0 || cell.X >= 20 * (room.X0 + room.W) || cell.Z <= 20 * room.Z0 || cell.Z >= 20 * (room.Z0 + room.D))
            return false;
        var floor = FloorTop(room.Y0);
        if (cell.Y < floor || cell.Y > floor + 20 * room.H - 2 * Small)
            return false;
        return !room.Partition.TryGet(out var p) || cell.Z != 20 * (room.Z0 + p.At) ||
               (cell.X > 20 * (room.X0 + p.DoorAt) && cell.X < 20 * (room.X0 + p.DoorAt + 1) && cell.Y < floor + 40);
    }

    private readonly record struct State(Cell Cell, Direction Travel, bool Join);

    private readonly record struct Budget(int Pipe, int Cable, int Chute);

    private sealed record DevicePalette(List<NamedPiece> Machines, List<NamedPiece> Chutes);
}

/// <summary>A partition wall across a room: the large-cell row it stands before, and the column of its door.</summary>
internal readonly record struct Partition(int At, int DoorAt);

/// <summary>
/// A room: its first large cell (x, z), size, height of air over its floor frames at large index <see cref="Y0"/>, the
/// column of its door to the corridor and the side it opens on (+1 toward +z, -1 toward -z).
/// </summary>
internal sealed record Room(int X0, int Z0, int W, int D, int H, int Y0, int DoorX, int Facing, Option<Partition> Partition, Corridor Corridor, bool Factory);

/// <summary>A corridor of floor frames two large cells deep at <see cref="Zc"/>, with the rooms either side and their networks.</summary>
internal sealed class Corridor(int zc, int y0)
{
    public int Zc { get; } = zc;

    public int Y0 { get; } = y0;

    public List<Room> Rooms { get; } = new();

    public Net? Pipe { get; set; }

    public Net? Cable { get; set; }
}

internal enum RunKind
{
    Pipe,
    Cable,
    Chute,
}

/// <summary>One network the generator lays: its kind and the kit its pieces come from.</summary>
internal sealed class Net(RunKind kind, Kit kit)
{
    public RunKind Kind { get; } = kind;

    public int Cells { get; set; }

    public bool Fits(int mask) => kit.Fits(mask);

    public Option<(NamedPiece Piece, Turn Turn)> Match(int mask) => kit.Match(mask);
}

/// <summary>A run cell: its network and the sides its piece joins (one bit per direction).</summary>
internal sealed class RunCell(Net net, int mask)
{
    public Net Net { get; } = net;

    public int Mask { get; set; } = mask;

    public bool Mounted { get; set; }
}

/// <summary>
/// A kit's one-cell run pieces and, for each set of sides, the first of them (in the kit's order) and the first turn
/// its piece allows that points its ends exactly to those sides; none when no piece of the kit can.
/// </summary>
internal sealed class Kit
{
    private readonly Option<(NamedPiece Piece, Turn Turn)>[] _matches = new Option<(NamedPiece, Turn)>[64];

    public Kit(string name, int weight, List<NamedPiece> pieces, IGrid grid)
    {
        Name = name;
        Weight = weight;
        for (var mask = 1; mask < 64; mask++)
            _matches[mask] = Find(pieces, mask, grid);
    }

    public string Name { get; }

    public int Weight { get; }

    public bool Fits(int mask) => mask is > 0 and < 64 && _matches[mask].IsSome;

    public Option<(NamedPiece Piece, Turn Turn)> Match(int mask) => mask is > 0 and < 64 ? _matches[mask] : Option<(NamedPiece, Turn)>.None;

    private static Option<(NamedPiece, Turn)> Find(List<NamedPiece> pieces, int mask, IGrid grid)
    {
        foreach (var piece in pieces)
        {
            if (piece.Facts.Ends.Count != Ones(mask))
                continue;
            for (var i = 0; i < Turn.Count; i++)
            {
                if (!Turn.Create(i).TryGet(out var turn) || !piece.Facts.AllowedTurns.Contains(turn))
                    continue;
                var sides = 0;
                foreach (var end in piece.Facts.Ends)
                    sides |= BaseGenerator.Bit(grid.Apply(turn, end.Facing));
                if (sides == mask)
                    return Option.Some((piece, turn));
            }
        }

        return Option<(NamedPiece, Turn)>.None;
    }

    private static int Ones(int mask)
    {
        var count = 0;
        for (var m = mask; m != 0; m &= m - 1)
            count++;
        return count;
    }
}
