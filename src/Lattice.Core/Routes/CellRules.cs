using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Grid;
using Lattice.Core.Openings;
using Lattice.Core.Ports;
using Lattice.Core.Runs;
using Lattice.Core.Support;

namespace Lattice.Core.Routes;

/// <summary>Why a route may not enter a cell.</summary>
internal enum Refusal
{
    None = 0,

    /// <summary>The kit's straight there would collide with what stands (collision classes or ends, the game's small-grid rule).</summary>
    Collides = 1,

    /// <summary>On frames: the cell is not in or on a frame's cell, and not near an end.</summary>
    OffFrame = 2,

    /// <summary>Doors and windows clear: the cell is in an opening's keep-out.</summary>
    Opening = 3,

    /// <summary>Ports free: a device's port faces the cell and nothing joins it yet.</summary>
    Port = 4,

    /// <summary>No unsafe joins: the route would join a network that is neither end's.</summary>
    Join = 5,

    /// <summary>A leg of a route with via cells: an earlier leg's cell, or an end or via cell that is not this leg's.</summary>
    Crosses = 6,

    /// <summary>Keep off lines: a line piece stands in the cell, or a line's end points into it.</summary>
    AlongLine = 7,
}

/// <summary>The words a refusal is written with: a route's <c>stopped</c> causes and a drawn run's broken rules.</summary>
internal static class RefusalWords
{
    /// <summary>The refusal's word; null for <see cref="Refusal.None"/>.</summary>
    public static string? Word(this Refusal why) => why switch
    {
        Refusal.Collides => "collides",
        Refusal.OffFrame => "off-frame",
        Refusal.Opening => "opening",
        Refusal.Port => "port",
        Refusal.Join => "join",
        Refusal.Crosses => "crosses",
        Refusal.AlongLine => "along-line",
        _ => null,
    };
}

/// <summary>
/// The cells a route may enter, judged on the planned world (every ghost done, <see cref="PlannedWorld"/>, a planned
/// piece in every cell it fills as a built one is) from catalogue facts only; the game's own check runs later, on the
/// pieces the run planner lays.
/// <list type="bullet">
/// <item>Always: the kit's straight along the way the route travels must not collide with what stands in the cell
/// (<see cref="Shapes.Conflict"/>: collision classes, then ends).
/// The kit's own pieces do not block: the run merges with them as a drawn run does, unless keep off lines. Small-grid pieces
/// never see the large grid (same section), so frames and walls block nothing.</item>
/// <item>On frames: the cell lies in or on the surface of a large cell whose centre holds a piece that allows
/// mounting (a frame, built, being built or planned; not broken), or within <see cref="OffFrameReach"/> cells of an
/// end, so a port off the frames is still reached.</item>
/// <item>Doors and windows clear: no cell in an opening's keep-out (<see cref="OpeningKeepOuts"/>).</item>
/// <item>Always, for a leg of a route with via cells: none of the closed cells (earlier legs', the other ends and via
/// cells), so the route never crosses itself; and on frames, the leg's own two cells reach off the frames as ends do.</item>
/// <item>Ports free: no cell a device's port (an end with a role) faces while nothing joins that port, but for the ports
/// the request names as its ends, a ghost's or a built structure's.</item>
/// <item>No unsafe joins: no cell where the route would join a network that is neither end's: a piece of the kit
/// there, or a neighbour's end pointing in that joins the kit (as the piece chooser joins it). A branch's ends are the
/// whole request's: the main route's and every branch's, so branches never count as a bridge.</item>
/// <item>Keep off lines: no cell holding a line piece, or into which a line piece's end points. A line piece is a run
/// piece (two or more ends, none with a port role, no device cell) of any kit, built or planned, on any network, with
/// an end the kit joins (<see cref="KitShapes.Joins(ConnectionEnd)"/>); a device's ports are never lines. Judged after
/// no unsafe joins, so a foreign line reports the join.</item>
/// </list>
/// A drawn run's cells are judged with <see cref="Breaks"/>: the cell rules but collisions, frames and closed cells.
/// Each cell is judged once per search; the collision test once per cell and axis. For
/// <see cref="RouteToggles.HugFramesAndWalls"/>, a cell is in open air (<see cref="InOpenAir"/>) when no large cell that
/// holds it has a frame in its centre or a piece (a wall, a window, a floor) on one of its faces.
/// </summary>
internal sealed class CellRules : IRouteCells
{
    /// <summary>How many cells from an end an on-frames route may run off the frames: 4 small cells, 2 m.</summary>
    public const int OffFrameReach = 4;

    private const int Large = 20;
    private const int Half = 10;

    private readonly KitShapes _kit;
    private readonly IGrid _grid;
    private readonly Shapes _shapes;
    private readonly IWorldQuery _world;
    private readonly RouteToggles _toggles;
    private readonly Cell _from;
    private readonly Cell _to;
    private readonly OpeningKeepOuts _openings;
    private readonly ISupportFacts _support;
    private readonly RouteNetworks _networks;
    private readonly Dictionary<(Cell, Axis), bool> _collides = [];
    private readonly Dictionary<Cell, Refusal> _cells = [];
    private readonly Dictionary<Cell, bool> _framed = [];
    private readonly Dictionary<Cell, bool> _walled = [];
    private readonly HashSet<NetworkKey> _own = [];
    private readonly HashSet<NetworkKey> _here = [];
    private readonly Dictionary<ReferenceId, PlacedShape?> _placed = [];
    private readonly IReadOnlyList<PortTarget> _named;
    private readonly IReadOnlyList<Cell> _near;
    private readonly HashSet<Cell>? _closed;

    public CellRules(
        KitShapes kit, IGrid grid, Shapes shapes, IWorldQuery planned, RouteToggles toggles, Cell from, Cell to,
        OpeningKeepOuts openings, ISupportFacts support, RouteNetworks networks, IReadOnlyList<PortTarget>? named = null,
        IReadOnlyList<Cell>? sameRun = null, IReadOnlyList<Cell>? near = null, HashSet<Cell>? closed = null)
    {
        _named = named ?? System.Array.Empty<PortTarget>();
        _near = near ?? System.Array.Empty<Cell>();
        _closed = closed;
        _kit = kit;
        _grid = grid;
        _shapes = shapes;
        _world = planned;
        _toggles = toggles;
        _from = from;
        _to = to;
        _openings = openings;
        _support = support;
        _networks = networks;
        if (toggles.NoBridging)
        {
            Joined(from, _own);
            Joined(to, _own);
            if (sameRun is not null)
                foreach (var end in sameRun)
                    Joined(end, _own);
        }
    }

    public bool Enters(Cell cell, Direction heading) => Why(cell, heading) == Refusal.None;

    /// <summary>Why the route may not enter the cell travelling this way; <see cref="Refusal.None"/> when it may.</summary>
    public Refusal Why(Cell cell, Direction heading)
    {
        if (_closed is not null && _closed.Contains(cell))
            return Refusal.Crosses;
        if (Collides(cell, heading.AxisOf()))
            return Refusal.Collides;
        return Breaks(cell);
    }

    /// <summary>
    /// The rule the cell breaks, judged once and cached: doors and windows clear, ports free, no unsafe joins and keep
    /// off lines, as the toggles ask (and on frames, which a drawn run turns off); <see cref="Refusal.None"/> when it
    /// keeps them all. Never a collision or a closed cell.
    /// </summary>
    public Refusal Breaks(Cell cell)
    {
        if (!_cells.TryGetValue(cell, out var why))
            _cells[cell] = why = Judge(cell);
        return why;
    }

    private Refusal Judge(Cell cell)
    {
        if (_toggles.OnFrames && !NearEnd(cell) && !OnFrame(cell))
            return Refusal.OffFrame;
        if (_toggles.KeepDoorsAndWindowsClear && _openings.At(cell).IsSome)
            return Refusal.Opening;
        if (_toggles.KeepPortsFree && FreePortFaces(cell))
            return Refusal.Port;
        if (_toggles.NoBridging && JoinsAnother(cell))
            return Refusal.Join;
        if (_toggles.KeepOffLines && OnLine(cell))
            return Refusal.AlongLine;
        return Refusal.None;
    }

    private bool Collides(Cell cell, Axis axis)
    {
        if (_collides.TryGetValue((cell, axis), out var known))
            return known;
        var collides = false;
        if (_kit.For(Directions.Along(axis)).TryGet(out var straight)
            && _shapes.Of(new Placement(straight.Piece, cell, straight.Turn)).TryGet(out var mine))
            foreach (var info in _world.At(cell))
                if (ShapeOf(info) is { } theirs && !_kit.Places(theirs.Piece) && Shapes.Conflict(mine, theirs).IsSome)
                    collides = true;
        return _collides[(cell, axis)] = collides;
    }

    /// <summary>Whether no large cell whose closed box holds the cell has a frame in its centre or a piece on a face.</summary>
    public bool InOpenAir(Cell cell)
    {
        foreach (var x in Centres(cell.X))
            foreach (var y in Centres(cell.Y))
                foreach (var z in Centres(cell.Z))
                {
                    var large = new Cell(x, y, z);
                    if (Framed(large) || Walled(large))
                        return false;
                }

        return true;
    }

    private bool Walled(Cell large)
    {
        if (_walled.TryGetValue(large, out var known))
            return known;
        var walled = false;
        for (var toward = Direction.XPlus; toward <= Direction.ZMinus && !walled; toward++)
        {
            var face = Faces.Between(large, toward, _grid);
            walled = HoldsFace(face.Near) || HoldsFace(face.Far);
        }

        return _walled[large] = walled;
    }

    private bool HoldsFace(SpotKey face)
    {
        foreach (var info in _world.At(face.Cell))
            if (info.State is not BuildState.Broken && ShapeOf(info) is { } shape && Holds(shape, face))
                return true;
        return false;
    }

    private bool NearEnd(Cell cell)
    {
        if (Steps(cell, _from) <= OffFrameReach || Steps(cell, _to) <= OffFrameReach)
            return true;
        foreach (var end in _near)
            if (Steps(cell, end) <= OffFrameReach)
                return true;
        return false;
    }

    private int Steps(Cell a, Cell b) => (System.Math.Abs(a.X - b.X) + System.Math.Abs(a.Y - b.Y) + System.Math.Abs(a.Z - b.Z)) / _kit.Step;

    /// <summary>Whether a large cell whose closed box holds the cell has a frame in its centre.</summary>
    private bool OnFrame(Cell cell)
    {
        foreach (var x in Centres(cell.X))
            foreach (var y in Centres(cell.Y))
                foreach (var z in Centres(cell.Z))
                    if (Framed(new Cell(x, y, z)))
                        return true;
        return false;
    }

    /// <summary>The large-cell centres (odd metres) within half a large cell of the value: one inside a cell, two on a face plane.</summary>
    private static int[] Centres(int dm)
    {
        var offset = ((dm - Half) % Large + Large) % Large;
        return offset == Half ? [dm - Half, dm + Half] : offset < Half ? [dm - offset] : [dm - offset + Large];
    }

    private bool Framed(Cell large)
    {
        if (_framed.TryGetValue(large, out var known))
            return known;
        var framed = false;
        var centre = new SpotKey(large, Slot.Centre);
        foreach (var info in _world.At(large))
            if (info.State is not BuildState.Broken && ShapeOf(info) is { } shape && Holds(shape, centre)
                && _support.Of(info.Ref.Piece).TryGet(out var facts) && facts.AllowsMounting)
                framed = true;
        return _framed[large] = framed;
    }

    private static bool Holds(PlacedShape shape, SpotKey spot)
    {
        foreach (var taken in shape.Spots)
            if (taken == spot)
                return true;
        return false;
    }

    /// <summary>Whether a device's port faces the cell from a neighbour and no piece in the cell joins it.</summary>
    private bool FreePortFaces(Cell cell)
    {
        for (var direction = Direction.XPlus; direction <= Direction.ZMinus; direction++)
        {
            var next = _grid.Neighbour(cell, direction, _kit.Grid);
            var back = direction.Opposite();
            foreach (var info in _world.At(next))
            {
                if (ShapeOf(info) is not { } shape)
                    continue;
                for (var i = 0; i < shape.Ends.Count; i++)
                    if (shape.Piece.Ends[i].Role.IsSome && shape.Ends[i].Cell == next && shape.Ends[i].Facing == back
                        && !Named(next, back) && !Answered(cell, direction, shape.Piece.Ends[i]))
                        return true;
            }
        }

        return false;
    }

    /// <summary>Whether a port leaving this cell this way is one the request ends at.</summary>
    private bool Named(Cell from, Direction toward)
    {
        foreach (var port in _named)
            if (port.Port.From == from && port.Back.Opposite() == toward)
                return true;
        return false;
    }

    /// <summary>Whether a piece in the cell has an end facing the port (towards <paramref name="towards"/>) sharing its kind.</summary>
    private bool Answered(Cell cell, Direction towards, ConnectionEnd port)
    {
        foreach (var info in _world.At(cell))
            if (ShapeOf(info) is { } shape)
                for (var i = 0; i < shape.Ends.Count; i++)
                    if (shape.Ends[i].Cell == cell && shape.Ends[i].Facing == towards && port.SharesAKindWith(shape.Piece.Ends[i]))
                        return true;
        return false;
    }

    private bool JoinsAnother(Cell cell)
    {
        _here.Clear();
        Joined(cell, _here);
        foreach (var key in _here)
            if (!_own.Contains(key))
                return true;
        return false;
    }

    /// <summary>Whether a line piece stands in the cell, or a neighbour's line end points into it.</summary>
    private bool OnLine(Cell cell)
    {
        foreach (var info in _world.At(cell))
            if (ShapeOf(info) is { } shape && IsLine(shape.Piece))
                foreach (var end in shape.Piece.Ends)
                    if (_kit.Joins(end))
                        return true;
        for (var direction = Direction.XPlus; direction <= Direction.ZMinus; direction++)
        {
            var next = _grid.Neighbour(cell, direction, _kit.Grid);
            var back = direction.Opposite();
            foreach (var info in _world.At(next))
            {
                if (ShapeOf(info) is not { } shape || !IsLine(shape.Piece))
                    continue;
                for (var i = 0; i < shape.Ends.Count; i++)
                    if (shape.Ends[i].Cell == next && shape.Ends[i].Facing == back && _kit.Joins(shape.Piece.Ends[i]))
                        return true;
            }
        }

        return false;
    }

    /// <summary>A run piece: two or more ends, none with a port role, and no device cell.</summary>
    private static bool IsLine(PieceFacts piece)
    {
        if (piece.Ends.Count < 2)
            return false;
        foreach (var end in piece.Ends)
            if (end.Role.IsSome)
                return false;
        foreach (var cell in piece.Footprint)
            if (cell.Slot == Slot.Device)
                return false;
        return true;
    }

    /// <summary>The networks a run piece in the cell joins: the kit's pieces there and the neighbours' ends pointing in that join the kit.</summary>
    private void Joined(Cell cell, HashSet<NetworkKey> into)
    {
        foreach (var info in _world.At(cell))
            if (ShapeOf(info) is { } shape && _kit.Places(shape.Piece))
                into.Add(_networks.Of(info));
        for (var direction = Direction.XPlus; direction <= Direction.ZMinus; direction++)
        {
            var next = _grid.Neighbour(cell, direction, _kit.Grid);
            var back = direction.Opposite();
            foreach (var info in _world.At(next))
            {
                if (ShapeOf(info) is not { } shape)
                    continue;
                for (var i = 0; i < shape.Ends.Count; i++)
                    if (shape.Ends[i].Cell == next && shape.Ends[i].Facing == back && _kit.Joins(shape.Piece.Ends[i]))
                        into.Add(_networks.Of(info));
            }
        }
    }

    private PlacedShape? ShapeOf(StructureInfo info)
    {
        if (_placed.TryGetValue(info.Ref.Id, out var known))
            return known;
        var shape = _shapes.Of(new Placement(info.Ref.Piece, info.Ref.At, info.Turn)).TryGet(out var placed) ? placed : null;
        return _placed[info.Ref.Id] = shape;
    }
}
