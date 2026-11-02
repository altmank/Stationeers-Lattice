using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;
using Lattice.Core.Grid;
using Lattice.Core.Ports;

namespace Lattice.BigBase;

// Routing: the trunks and shafts, the cheapest-path runs from device ends, and the run cells turned into kit pieces.
internal sealed partial class BaseGenerator
{
    // A gas network per corridor stack (the corridors one above another, joined by a shaft); one power grid for the base,
    // each storey's corridor trunks joined down that storey's spine and the storeys joined by shafts.
    private void Trunks(List<Kit> pipeKits, List<Kit> cableKits)
    {
        var grid = NewNet(RunKind.Cable, cableKits);
        var spineX = Centre(_layout.OriginX - 1);
        var pipeShaftX = 20 * (_layout.OriginX - 2) + Small;
        var cableShaftX = 20 * (_layout.OriginX - 2) + 3 * Small;
        var shafts = StoreysUsed() > 1;
        var pipes = new Dictionary<int, Net?>();
        var spines = new Dictionary<int, List<int>>();
        var stacks = new Dictionary<int, List<Corridor>>();
        foreach (var corridor in _corridors)
        {
            if (corridor.Rooms.Count == 0)
                continue;
            if (!pipes.TryGetValue(corridor.Stack, out var pipe))
                pipes[corridor.Stack] = pipe = NewNet(RunKind.Pipe, pipeKits);
            corridor.Pipe = pipe;
            corridor.Cable = grid;
            if (!stacks.TryGetValue(corridor.Stack, out var stack))
                stacks[corridor.Stack] = stack = new List<Corridor>();
            stack.Add(corridor);
            var floor = FloorTop(corridor.Y0);
            var trunkZ = 20 * corridor.Zc + 20;
            if (pipe is not null)
                Trunk(corridor, pipe, floor, trunkZ, static r => r.Facing > 0 ? 5 : 15, shafts ? Option.Some(pipeShaftX) : Option<int>.None);
            if (grid is null)
                continue;
            Trunk(corridor, grid, floor + Small, trunkZ, static r => r.Facing > 0 ? 15 : 5, Option.Some(spineX));
            if (!spines.TryGetValue(corridor.Storey, out var spine))
                spines[corridor.Storey] = spine = new List<int>();
            spine.Add(trunkZ);
        }

        if (grid is null)
            return;
        foreach (var (storey, spine) in spines)
        {
            var y = FloorTop(_storeyY[storey]) + Small;
            for (var z = spine[0]; z < spine[spine.Count - 1]; z += Small)
                Connect(grid, new Cell(spineX, y, z), new Cell(spineX, y, z + Small));
        }

        if (!shafts)
            return;
        foreach (var stack in stacks.Values)
            for (var i = 0; i < stack.Count; i++)
            {
                var (lower, z) = (stack[i], 20 * stack[i].Zc + 20);
                var y = FloorTop(lower.Y0) + Small;
                if (lower.Pipe is { } pipe && i > 0)
                    Rise(pipe, pipeShaftX, z, FloorTop(stack[i - 1].Y0), FloorTop(lower.Y0));
                if (i > 0)
                    Rise(grid, cableShaftX, z + Small, FloorTop(stack[i - 1].Y0) + Small, y);
                for (var x = cableShaftX; x < spineX; x += Small)
                    Connect(grid, new Cell(x, y, z + Small), new Cell(x + Small, y, z + Small));
                Connect(grid, new Cell(spineX, y, z + Small), new Cell(spineX, y, z));
            }
    }

    // A shaft: a vertical run from one floor to the one above, a cell at a time.
    private void Rise(Net net, int x, int z, int from, int to)
    {
        for (var y = from; y < to; y += Small)
            Connect(net, new Cell(x, y, z), new Cell(x, y + Small, z));
    }

    // A straight run down the corridor between its outermost branches, and a branch from it through every room's doorway
    // to the room's first cell inside.
    private void Trunk(Corridor corridor, Net net, int y, int z, Func<Room, int> lane, Option<int> westTo)
    {
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
            var step = room.Facing > 0 ? Small : -Small;
            var deep = room.Facing > 0 ? 20 * corridor.Zc - Small : 20 * (corridor.Zc + 2) + Small;
            for (var k = 0; k < Bus && Inside(room, new Cell(x, y, deep - step)); k++)
                deep -= step;
            for (var at = deep; at != z; at += step)
                Connect(net, new Cell(x, y, at), new Cell(x, y, at + step));
        }
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
    // to a cell of the same network whose piece can take one more end; laid when it is no longer than the budget, or
    // else than the longest detour. A chute end with nothing to reach starts its network (seed), when the network has no
    // cell yet. False when no run was laid and the end joins nothing.
    private bool Route(Room room, Net net, Cell start, Direction outward, int budget, bool seed)
    {
        var back = Bit(outward.Opposite());
        if (_runs.TryGetValue(start, out var here))
        {
            if (here.Net != net || !net.Fits(here.Mask | back))
                return false;
            Remember(start, here);
            here.Mask |= back;
            return true;
        }

        if (!Inside(room, start) || _devices.Contains(start))
            return false;
        var path = Search(room, net, start, outward, budget) ?? (budget < Detour ? Search(room, net, start, outward, Detour) : null);
        if (path is null)
        {
            if (!seed || net.Cells > 0)
                return false;
            AddRun(net, start, back);
            return true;
        }

        var (cells, join) = path.Value;
        var seen = new HashSet<Cell>(cells);
        if (seen.Count != cells.Count || seen.Contains(join))
            return false;
        AddRun(net, cells[0], back);
        for (var i = 1; i < cells.Count; i++)
            Connect(net, cells[i - 1], cells[i]);
        Connect(net, cells[cells.Count - 1], join);
        return true;
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

    // Each run cell as the kit piece, in the turn, that points its ends to exactly the sides it joins; a cell with one
    // side gets the straight piece. A cell no piece of the kit fits is counted, not placed.
    private void EmitRuns()
    {
        foreach (var entry in _runs)
        {
            var mask = entry.Value.Mask;
            if (Count(mask) == 1)
                mask |= Bit(DirectionOf(mask).Opposite());
            if (entry.Value.Net.Match(mask).TryGet(out var match))
                Emit(match.Piece, new Placement(match.Piece.Facts.Key, entry.Key, match.Turn));
            else
                _dropped++;
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
            Remember(at, run);
            run.Mask |= bits;
            return;
        }

        _journal?.Add((at, null, net, 0));
        _runs[at] = new RunCell(net, bits);
        Tally(net.Kind);
        net.Cells++;
    }

    // The runs laid while a device is tried are written down, so that a device whose ends cannot all be joined leaves no
    // stub behind.
    private List<(Cell At, RunCell? Run, Net Net, int Mask)>? _journal;

    private void Remember(Cell at, RunCell run) => _journal?.Add((at, run, run.Net, run.Mask));

    private void Rollback()
    {
        for (var i = _journal!.Count - 1; i >= 0; i--)
        {
            var (at, run, net, mask) = _journal[i];
            if (run is not null)
                run.Mask = mask;
            else
            {
                _runs.Remove(at);
                _runCounts[net.Kind]--;
                net.Cells--;
            }
        }

        _journal.Clear();
    }

    private void Tally(RunKind kind) => _runCounts[kind] = Count(kind) + 1;

    private int Count(RunKind kind) => _runCounts.TryGetValue(kind, out var count) ? count : 0;
}
