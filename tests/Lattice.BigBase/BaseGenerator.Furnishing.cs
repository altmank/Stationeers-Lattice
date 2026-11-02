using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;
using Lattice.Core.Board;
using Lattice.Core.Catalogue;
using Lattice.Core.Grid;
using Lattice.Core.Ports;

namespace Lattice.BigBase;

// Furnishing: the devices on the floors with their gas, power and chute ends run into the networks, and the on-pipe
// devices along straight pipes.
internal sealed partial class BaseGenerator
{
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
            var chute = room.Factory ? NewNet(RunKind.Chute, chuteKits) : null;
            var want = Share(floorTarget - placed, left);
            var (pipe, cable) = (room.Corridor.Pipe, room.Corridor.Cable);
            var (pipeFrom, cableFrom) = (pipe?.Cells ?? 0, cable?.Cells ?? 0);
            _step = StepOf(room);
            placed += Devices(room, chute is not null ? devices.Chutes : devices.Machines, want, budget, chute);
            if (pipe is not null)
                Spare(room, pipe, pipeFrom + budget.Pipe);
            if (cable is not null)
                Spare(room, cable, cableFrom + budget.Cable);
            if (chute is not null)
                Spare(room, chute, budget.Chute);
        }
    }

    private int Devices(Room room, List<NamedPiece> palette, int want, Budget budget, Net? chute,
        Func<NamedPiece, (Cell At, List<Turn> Turns)>? mount = null, string? tally = null)
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
            var (at, turns) = mount is null ? (DeviceSpot(room), Uprights(piece.Facts)) : mount(piece);
            if (turns.Count == 0)
                continue;
            var turn = turns[_random.Next(turns.Count)];
            var placement = new Placement(piece.Facts.Key, at, turn);
            if (!piece.Facts.Snap.Holds(at) || !_shapes.Of(placement).TryGet(out var shape) || !Fits(room, shape, piece.Facts, chute))
                continue;
            foreach (var spot in shape.Spots)
                _devices.Add(spot.Cell);
            var joined = true;
            _journal = new();
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
                if (!Route(room, net, end.Cell.Plus(Scaled(end.Facing, Small)), end.Facing, allowed, seed: net.Kind == RunKind.Chute))
                {
                    joined = false;
                    break;
                }
            }

            if (joined)
            {
                _journal = null;
                Emit(piece, placement);
                if (tally is not null)
                    _counts[tally] = (_counts.TryGetValue(tally, out var had) ? had : 0) + 1;
                placed++;
                continue;
            }

            Rollback();
            _journal = null;
            foreach (var spot in shape.Spots)
                _devices.Remove(spot.Cell);
        }

        return placed;
    }

    // Mostly within a few cells of the doorway, where the branches enter, as a player sets machines near the supply;
    // otherwise anywhere on the doorway's side of the room's partition, if it has one.
    private Cell DeviceSpot(Room room)
    {
        var y = FloorTop(room.Y0);
        var (low, high) = (20 * room.Z0, 20 * (room.Z0 + room.D));
        if (room.Partition.TryGet(out var p))
        {
            var plane = 20 * (room.Z0 + p.At);
            if (room.Facing > 0)
                low = plane;
            else
                high = plane;
        }

        if (_random.Next(20) == 0)
            return new Cell(20 * room.X0 + Small * _random.Next(1, 4 * room.W), y, low + Small * _random.Next(1, (high - low) / Small));
        const int reach = 3;
        var x = Math.Clamp(20 * room.DoorX + 10 + Small * _random.Next(-reach, reach + 1), 20 * room.X0 + Small, 20 * (room.X0 + room.W) - Small);
        var back = Small * _random.Next(1, Math.Min(Bus, (high - low) / Small - 1) + 1);
        return new Cell(x, y, room.Facing > 0 ? high - back : low + back);
    }

    private bool Fits(Room room, PlacedShape shape, PieceFacts facts, Net? chute)
    {
        foreach (var spot in shape.Spots)
            if (!Inside(room, spot.Cell) || _devices.Contains(spot.Cell) || _runs.ContainsKey(spot.Cell))
                return false;
        foreach (var end in shape.Ends)
        {
            var next = end.Cell.Plus(Scaled(end.Facing, Small));
            if (_devices.Contains(next) || !Inside(room, next))
                return false;
            if (NetFor(room, end, facts, shape, chute) is { } net && _runs.TryGetValue(next, out var run) &&
                (run.Net != net || !net.Fits(run.Mask | Bit(end.Facing.Opposite()))))
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

    // The on-pipe devices along straight pipes, spread evenly over them in as many sweeps as it takes. A device stands
    // in the pipe's own turn, so it lies along the pipe; where the piece does not allow that turn it is not mounted there.
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
        var taken = new HashSet<Cell>();
        var done = 0;
        for (var sweep = 0; sweep < gap && done < want; sweep++)
            for (var i = (_random.Next(gap) + sweep) % gap; i < straights.Count && done < want; i += gap)
            {
                var (cell, run) = straights[i];
                var piece = pieces[_random.Next(pieces.Count)];
                if (taken.Contains(cell) || !run.Net.Match(run.Mask).TryGet(out var pipe) || !Mount(piece, pipe, cell).TryGet(out var placement))
                    continue;
                taken.Add(cell);
                _journal = null;
                _step = StepAt(cell);
                Emit(piece, placement);
                done++;
            }
    }

    private static Option<Placement> Mount(NamedPiece device, (NamedPiece Piece, Turn Turn) pipe, Cell at)
    {
        if ((device.Facts.Collision & pipe.Piece.Facts.Collision) != SmallCollision.None || device.Facts.Ends.Count > 0 || !device.Facts.Snap.Holds(at))
            return Option<Placement>.None;
        return device.Facts.AllowedTurns.Contains(pipe.Turn)
            ? Option.Some(new Placement(device.Facts.Key, at, pipe.Turn))
            : Option<Placement>.None;
    }

    private int OnPipeTarget()
    {
        var share = 0;
        foreach (var weighted in _mix.OnPipe)
            if (_named.ContainsKey(weighted.Name))
                share += weighted.Weight;
        return _mix.Target(PieceFamilies.Device) * Math.Min(share, 100) / 100;
    }

    private static int Share(int left, int parts) => parts <= 0 || left <= 0 ? 0 : (left + parts - 1) / parts;
}
