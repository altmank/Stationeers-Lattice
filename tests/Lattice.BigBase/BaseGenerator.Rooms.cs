using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;
using Lattice.Core.Board;
using Lattice.Core.Grid;
using Lattice.Core.Ports;

namespace Lattice.BigBase;

// Rooms: the frames, walls and windows of corridors and rooms, storey by storey.
internal sealed partial class BaseGenerator
{
    private int StoreysUsed()
    {
        var used = 0;
        foreach (var corridor in _corridors)
            used = Math.Max(used, corridor.Storey + 1);
        return used;
    }

    private void Structure(List<Weighted> frames, List<Weighted> walls, List<Weighted> windows)
    {
        var frameTarget = _mix.Target(PieceFamilies.Frame);
        var faceTarget = _mix.Target(PieceFamilies.Wall) + _mix.Target(PieceFamilies.Window);
        var ratio = frameTarget == 0 ? 0.0 : (double)faceTarget / frameTarget;
        var windowShare = faceTarget == 0 ? 0.0 : (double)_mix.Target(PieceFamilies.Window) / faceTarget;
        var pairPitch = _layout.RoomMax * 2 + 4;
        var storeys = Math.Max(1, _layout.Storeys);
        var floor = _layout.OriginY;
        for (var s = 0; s < storeys; s++)
        {
            _storeyY.Add(floor);
            floor += _layout.HeightMax + 2 + _layout.StoreyGap + _random.Next(_layout.StoreyJitter + 1);
        }

        var lastZc = new Dictionary<int, int>();
        for (var pair = 0; _frames < frameTarget; pair++)
        {
            var (storey, stack) = (pair % storeys, pair / storeys);
            var y0 = _storeyY[storey];
            var zc = _layout.OriginZ + stack * pairPitch + _layout.RoomMax;
            var x0 = _layout.OriginX;
            var x1 = _layout.OriginX + _layout.Columns * _layout.ColumnPitch;
            var corridor = new Corridor(zc, y0, storey, stack);
            var spineFrom = lastZc.TryGetValue(storey, out var last) ? last + 2 : zc;
            lastZc[storey] = zc;
            _corridors.Add(corridor);
            _firstOfStorey.TryAdd(storey, corridor);
            _step = Step(corridor, 0);
            for (var x = x0; x < x1; x++)
                for (var dz = 0; dz < 2; dz++)
                    Frame(frames, new Cell(Centre(x), Centre(y0), Centre(zc + dz)));

            // The spine's west column is open where the shafts pass up through the floor above the lowest storey.
            _step = Step(_firstOfStorey[storey], 0);
            for (var z = spineFrom; z < zc + 2; z++)
                for (var dx = 1; dx <= 2; dx++)
                    if (!(storey > 0 && dx == 2 && z >= zc))
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
                    var doorway = rx + _random.Next(0, w);
                    var room = new Room(rx, rz, w, d, h, y0, doorway, facing, partition, corridor, _chutesPossible && _random.Next(4) == 0);
                    _rooms.Add(room);
                    corridor.Rooms.Add(room);
                    _step = StepOf(room);
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

    // A room's frames above and below, and its walls round the sides. A doorway is a gap two cells high in the wall run
    // to the corridor, and in a partition: no piece stands in it.
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
            var doorway = j <= 2;
            for (var x = room.X0; x < room.X0 + room.W; x++)
            {
                if (!(doorway && x == room.DoorX && room.Facing < 0))
                    Face(walls, windows, windowShare, new Cell(Centre(x), y, Centre(room.Z0)), Direction.ZMinus);
                if (!(doorway && x == room.DoorX && room.Facing > 0))
                    Face(walls, windows, windowShare, new Cell(Centre(x), y, Centre(room.Z0 + room.D - 1)), Direction.ZPlus);
                if (room.Partition.TryGet(out var p) && !(doorway && x == room.X0 + p.DoorAt))
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
        if (palette.Count == 0)
            return;
        var piece = Pick(palette);
        if (!FirstTurn(piece.Facts, at).TryGet(out var turn) || !_largeSpots.Add(new SpotKey(at, Slot.Centre)))
            return;
        Emit(piece, new Placement(piece.Facts.Key, at, turn));
        _frames++;
    }

    // A wall or window on the face of the large cell toward the outward side: the turn that lays it there, found once a
    // piece and side and kept. A family without pieces gives way to the other; with neither, the face stays open.
    private void Face(List<Weighted> walls, List<Weighted> windows, double windowShare, Cell large, Direction outward)
    {
        if (walls.Count == 0 && windows.Count == 0)
            return;
        var window = windows.Count > 0 && (walls.Count == 0 || (windowShare > 0 && _windows < windowShare * (_faces + 1) - 1 + _random.NextDouble() * 2));
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
        if (piece.Facts.Family == PieceFamilies.Wall)
            _wallFaces.Add(at);
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

    // Inside the room, or while a wall-mounted control is placed, on the doorway wall's plane in the first row above the
    // floor: a run lies along the wall there as cables do on a real wall.
    private bool Reaches(Room room, Cell cell) =>
        Inside(room, cell) ||
        (_wallPlane is { } plane && cell.Z == plane && cell.X > 20 * room.X0 && cell.X < 20 * (room.X0 + room.W) &&
         cell.Y >= FloorTop(room.Y0) && cell.Y < FloorTop(room.Y0) + 20);

    // Inside the room's walls, from the floor to under the ceiling, never on a partition's plane but at its doorway.
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
}
