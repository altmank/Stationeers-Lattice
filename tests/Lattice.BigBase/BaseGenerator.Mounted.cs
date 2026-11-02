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

/// <summary>Which catalogue pieces are the base's vents and controls; the generator and the tests read the same rule.</summary>
internal static class MountedKinds
{
    public const string Vent = "vent";
    public const string Control = "control";

    /// <summary>A face-mounted piece that opens its pipe network to the air and has a gas end.</summary>
    public static bool IsVent(PieceFacts facts) =>
        facts.Vents && Mounted(facts) && HasEnd<NetworkType.GasPipe>(facts);

    /// <summary>A face-mounted device with a control face and a power end, and no gas end.</summary>
    public static bool IsControl(PieceFacts facts) =>
        facts.Family == PieceFamilies.Device && facts.ControlFace.IsSome && Mounted(facts) &&
        HasEnd<NetworkType.Power>(facts) && !HasEnd<NetworkType.GasPipe>(facts);

    private static bool Mounted(PieceFacts facts) =>
        facts.Grid == GridSize.Small && facts.Kits.Count > 0 && facts.Turning.Placement == PlacedBy.FaceMount &&
        (!facts.SupportGroup.TryGet(out var group) || group == SupportGroups.WallMount);

    private static bool HasEnd<T>(PieceFacts facts) where T : NetworkType
    {
        foreach (var end in facts.Ends)
            foreach (var kind in end.Kinds)
                if (kind is T)
                    return true;
        return false;
    }
}

// Vents and controls: a vent at the end of a room's pipe branch with its gas end toward the bus, a control on the
// room's inside wall beside the doorway with its control face into the room and its power end wired to the cable branch.
// They are dealt over the rooms in build order, so every stretch of the file holds some.
internal sealed partial class BaseGenerator
{
    private void Mounted()
    {
        var vents = new List<NamedPiece>();
        var controls = new List<NamedPiece>();
        var names = new List<string>(_named.Keys);
        names.Sort(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (MountedKinds.IsVent(_named[name].Facts))
                vents.Add(_named[name]);
            else if (MountedKinds.IsControl(_named[name].Facts))
                controls.Add(_named[name]);
        }

        // A vent is a pipe-family piece and a control a device: a mix without the family has neither.
        var ventTarget = vents.Count > 0 && _mix.Target(PieceFamilies.Pipe) > 0 ? Target(MountedKinds.Vent) : 0;
        var controlTarget = controls.Count > 0 && _mix.Target(PieceFamilies.Device) > 0 ? Target(MountedKinds.Control) : 0;
        if (ventTarget + controlTarget == 0)
            return;
        var order = new int[_rooms.Count];
        for (var i = 0; i < order.Length; i++)
            order[i] = i;
        Array.Sort(order, (a, b) =>
        {
            var by = StepOf(_rooms[a]).CompareTo(StepOf(_rooms[b]));
            return by != 0 ? by : a.CompareTo(b);
        });
        var (ventsOwed, controlsOwed) = (0, 0);
        var reach = new Budget(Detour, Detour, 0);
        for (var rank = 0; rank < order.Length; rank++)
        {
            var room = _rooms[order[rank]];
            _step = StepOf(room);
            ventsOwed += Spread(ventTarget, rank, order.Length);
            controlsOwed += Spread(controlTarget, rank, order.Length);
            if (room.Corridor.Pipe is not null && ventsOwed > 0)
                ventsOwed -= Devices(room, vents, ventsOwed, reach, null, piece => VentSpot(room, piece), MountedKinds.Vent);
            if (room.Corridor.Cable is not null && controlsOwed > 0)
                controlsOwed -= Devices(room, controls, controlsOwed, reach, null, piece => ControlSpot(room, piece), MountedKinds.Control);
        }
    }

    private int Target(string group) => _mix.Groups.TryGetValue(group, out var count) ? count : 0;

    private static int Spread(int total, int rank, int of) => (int)((long)total * (rank + 1) / of - (long)total * rank / of);

    // In line past the branch's last cell, or beside one of its cells, the gas end toward the branch.
    private (Cell At, List<Turn> Turns) VentSpot(Room room, NamedPiece piece)
    {
        var step = room.Facing > 0 ? Small : -Small;
        var x = 20 * room.DoorX + (room.Facing > 0 ? 5 : 15);
        var y = FloorTop(room.Y0);
        var deep = BusEnd(room, x, y);
        Cell at;
        Direction toward;
        if (_random.Next(2) == 0)
        {
            at = new Cell(x, y, deep - step);
            toward = room.Facing > 0 ? Direction.ZPlus : Direction.ZMinus;
        }
        else
        {
            var side = _random.Next(2) == 0 ? Small : -Small;
            at = new Cell(x + side, y, deep + _random.Next(0, Bus) * step);
            toward = side > 0 ? Direction.XMinus : Direction.XPlus;
        }

        var turns = new List<Turn>(4);
        for (var i = 0; i < Turn.Count; i++)
        {
            var turn = TurnAt(i);
            if (!piece.Facts.AllowedTurns.Contains(turn))
                continue;
            foreach (var end in piece.Facts.Ends)
                if (Has<NetworkType.GasPipe>(end.Kinds))
                {
                    if (_shapes.Leaving(new Placement(piece.Facts.Key, at, turn), end).Facing == toward)
                        turns.Add(turn);
                    break;
                }
        }

        return (at, turns);
    }

    // On the wall the doorway is in, a few cells to either side of the doorway, at hand height.
    private (Cell At, List<Turn> Turns) ControlSpot(Room room, NamedPiece piece)
    {
        var reach = Small * (1 + _random.Next(4));
        var x = _random.Next(2) == 0 ? 20 * room.DoorX - reach : 20 * (room.DoorX + 1) + reach;
        var z = room.Facing > 0 ? 20 * room.Corridor.Zc - Small : 20 * (room.Corridor.Zc + 2) + Small;
        var at = new Cell(x, FloorTop(room.Y0) + Small * _random.Next(2, 4), z);
        var inward = room.Facing > 0 ? Direction.ZMinus : Direction.ZPlus;
        var turns = new List<Turn>(4);
        if (piece.Facts.ControlFace.TryGet(out var face))
            for (var i = 0; i < Turn.Count; i++)
            {
                var turn = TurnAt(i);
                if (piece.Facts.AllowedTurns.Contains(turn) && _grid.Apply(turn, face) == inward)
                    turns.Add(turn);
            }

        return (at, turns);
    }

    // The cell a body stands in: the first room in build order, its first floor frame with no piece standing over it
    // (the frames, walls and windows aside), at the middle of the cell, a metre above the frame.
    private Option<Cell> StandSpot(List<BasePiece> pieces)
    {
        Room? first = null;
        foreach (var room in _rooms)
            if (first is null || StepOf(room) < StepOf(first))
                first = room;
        if (first is null)
            return Option<Cell>.None;
        var top = FloorTop(first.Y0);
        var blocked = new HashSet<(int, int)>();
        foreach (var piece in pieces)
        {
            if (piece.At.Y < top || piece.At.Y >= top + 40 || !_named.TryGetValue(piece.Prefab, out var named))
                continue;
            var family = named.Facts.Family;
            if (family == PieceFamilies.Frame || family == PieceFamilies.Wall || family == PieceFamilies.Window)
                continue;
            foreach (var column in Columns(piece.At.X))
                foreach (var row in Columns(piece.At.Z))
                    blocked.Add((column, row));
        }

        for (var x = first.X0; x < first.X0 + first.W; x++)
            for (var z = first.Z0; z < first.Z0 + first.D; z++)
                if (!blocked.Contains((x, z)) && _largeSpots.Contains(new SpotKey(new Cell(Centre(x), Centre(first.Y0), Centre(z)), Slot.Centre)))
                    return Option.Some(new Cell(Centre(x), top + 10, Centre(z)));
        return Option<Cell>.None;
    }

    // The large-cell columns a coordinate touches: two where it lies on the plane between them.
    private static int[] Columns(int coordinate) =>
        coordinate % 20 == 0 ? [coordinate / 20 - 1, coordinate / 20] : [coordinate / 20];
}
