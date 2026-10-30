using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Grid;
using Lattice.Core.Ports;

namespace Lattice.Core.Catalogue;

/// <summary>
/// The axes the game's builder turns a piece about: its <c>Structure.RotationAxis</c>, values as the game's
/// (decompile <c>Assets.Scripts.Objects/RotationAxis.cs</c>).
/// </summary>
[Flags]
internal enum RotationAxes
{
    None = 0,
    X = 1,
    Y = 2,
    Z = 4,
    All = 7,
}

/// <summary>
/// The faces the game's builder lets a piece's cursor start on: its <c>Structure.AllowedRotations</c>, values as the
/// game's (decompile <c>Assets.Scripts.Objects/AllowedRotations.cs</c>).
/// </summary>
[Flags]
internal enum StartFaces
{
    None = 0,
    Wall = 1,
    Ceiling = 2,
    Floor = 4,
    All = 7,
}

/// <summary>How the game's builder places a piece: its <c>Structure.PlacementType</c> (decompile <c>Assets.Scripts.Objects/PlacementSnap.cs</c>).</summary>
internal enum PlacedBy
{
    Grid = 0,
    Face = 1,
    FaceMount = 2,
}

/// <summary>
/// What limits the turns the game's builder gives a piece, as its prefab holds it: how it is placed, the axes the
/// rotate keys turn it about, the faces its cursor starts on, whether it is a mounted piece (<c>IMounted</c>), whose up
/// and down keys turn it a half turn, and for a piece the builder smart-rotates (<c>ISmartRotatable</c>) the rotations
/// of its connection type's list (<see cref="Smart"/>).
/// </summary>
internal readonly record struct TurnLimits(PlacedBy Placement, RotationAxes Axes, StartFaces Faces, bool Mounted)
{
    /// <summary>
    /// The rotations the builder's smart rotate steps a piece through, as turns before the piece's axes bend them
    /// (decompile <c>Assets.Scripts.Util/SmartRotate.cs</c> <c>RotationsList</c>, read from the game at load): none for
    /// a piece it does not smart-rotate.
    /// </summary>
    public TurnSet Smart { get; init; }

    /// <summary>The game's defaults (<c>Structure.cs</c>) for a piece placed in the grid: every axis, every face.</summary>
    public static TurnLimits Free => new(PlacedBy.Grid, RotationAxes.All, StartFaces.All, false);

    /// <summary>Whether the builder gives every turn: the default axes and faces, however the piece is placed.</summary>
    public bool IsFree => Axes == RotationAxes.All && Faces == StartFaces.All;

    /// <summary>The turns the game's builder reaches (<see cref="BuilderTurns"/>), worked out on each ask: keep it.</summary>
    public TurnSet Turns => BuilderTurns.Of(this);
}

/// <summary>
/// The turns the game's builder reaches for a piece, worked out once for every <see cref="TurnLimits"/>: the cursor's
/// start turn and every turn the rotate keys take it to (decompile <c>Assets.Scripts.Inventory/InventoryManager.cs</c>
/// <c>UpdatePlacement(Structure)</c> for the start, <c>PlacementMode</c> for the keys). A quarter turn of +90 degrees
/// about an axis <c>a</c> sends a direction <c>v</c> to <c>a × v</c>, as Unity turns.
/// <list type="bullet">
/// <item>Smart rotate: holding the build key and scrolling steps a smart-rotated piece through its connection type's
/// rotations, each applied about the world (<c>Structure.Rotate</c>): as it is for a piece with two or more axes, bent
/// so its Z becomes the piece's one axis for a piece with one (<c>RotateZtoX</c>, <c>RotateZtoY</c>; a piece with two
/// or more axes that may only start on the floor is bent as one with Y), not at all for a piece with none.</item>
/// <item>Start: no turn, unless the piece may not start on a wall: then tilted onto the ceiling (forward down) when it
/// may start there, else onto the floor.</item>
/// <item>Grid: quarter turns about world Y, X and Z for each axis it has; about X a half turn for a mounted piece.</item>
/// <item>Face: with X, a quarter tilt about up × forward from a side face onto the floor or ceiling, and from there back
/// to the side it last faced; with Y, quarter turns about up on a side face; with Z, quarter turns about up on the
/// floor or ceiling and quarter rolls about the piece's forward anywhere.</item>
/// <item>Face mount: every turn. Its face is the one it is mounted on, whichever way that faces, and it rolls about
/// its forward whatever its axes.</item>
/// <item>Shared cursor: choosing another piece with the same placement, axes and start faces keeps the cursor's turn
/// (<c>UpdatePlacement(Structure)</c>), so those pieces share one set: every turn any of them reaches, turned on by
/// any of their moves (<see cref="Shared"/>).</item>
/// </list>
/// </summary>
internal static class BuilderTurns
{
    private static readonly TurnTable Table = new();

    /// <summary>The turns for one piece alone, as if no other piece shared its cursor. Any thread; allocates its walk.</summary>
    public static TurnSet Of(TurnLimits limits) => limits.IsFree ? TurnSet.All : Reach([limits]);

    /// <summary>
    /// The turns of each piece among <paramref name="pieces"/> (every piece the builder offers), each sharing its
    /// cursor with the others of the same placement, axes and start faces. One walk per such group; linear in the
    /// pieces.
    /// </summary>
    public static Func<TurnLimits, TurnSet> Shared(IEnumerable<TurnLimits> pieces)
    {
        var groups = new Dictionary<(PlacedBy, RotationAxes, StartFaces), List<TurnLimits>>();
        foreach (var limits in pieces)
        {
            if (limits.IsFree)
                continue;
            var key = (limits.Placement, limits.Axes, limits.Faces);
            if (!groups.TryGetValue(key, out var group))
                groups.Add(key, group = new List<TurnLimits>());
            if (!group.Contains(limits))
                group.Add(limits);
        }

        var sets = new Dictionary<(PlacedBy, RotationAxes, StartFaces), TurnSet>(groups.Count);
        foreach (var pair in groups)
            sets.Add(pair.Key, Reach(pair.Value));
        return limits => limits.IsFree
            ? TurnSet.All
            : sets.TryGetValue((limits.Placement, limits.Axes, limits.Faces), out var set) ? set : Of(limits);
    }

    /// <summary>The turns a cursor shared by pieces of one placement, axes and start faces reaches, by any of their moves.</summary>
    private static TurnSet Reach(List<TurnLimits> group)
    {
        var limits = group[0];
        if (limits.Placement == PlacedBy.FaceMount)
            return TurnSet.All;
        var start = Rotation.Identity;
        if ((limits.Faces & StartFaces.Wall) == 0)
            start = Quarter(Direction.XPlus, (limits.Faces & StartFaces.Ceiling) != 0);

        var smart = new List<Rotation>();
        var quarterX = false;
        var halfX = false;
        foreach (var each in group)
        {
            smart.AddRange(SmartMoves(each));
            quarterX |= !each.Mounted;
            halfX |= each.Mounted;
        }

        // A state is the turn and, for a face piece, the forward of the side face it last faced.
        var seen = new HashSet<(Rotation Turn, Direction Last)>();
        var queue = new Queue<(Rotation Turn, Direction Last)>();
        Visit((start, Direction.ZPlus));
        var set = TurnSet.None;
        while (queue.Count > 0)
        {
            var (turn, last) = queue.Dequeue();
            set = set.With(Table.TurnOf(turn));
            if (limits.Placement == PlacedBy.Grid)
                GridMoves(turn, limits.Axes);
            else
                FaceMoves(turn, last, limits.Axes);
            foreach (var move in smart)
            {
                var moved = turn.Then(move);
                var forward = moved.Apply(Direction.ZPlus);
                Visit((moved, limits.Placement == PlacedBy.Face && forward.AxisOf() == Axis.Y ? last : forward));
            }
        }

        return set;

        void Visit((Rotation, Direction) state)
        {
            if (seen.Add(state))
                queue.Enqueue(state);
        }

        void GridMoves(Rotation turn, RotationAxes axes)
        {
            if ((axes & RotationAxes.Y) != 0)
                World(turn, Direction.YPlus, half: false);
            if ((axes & RotationAxes.X) != 0 && quarterX)
                World(turn, Direction.XPlus, half: false);
            if ((axes & RotationAxes.X) != 0 && halfX)
                World(turn, Direction.XPlus, half: true);
            if ((axes & RotationAxes.Z) != 0)
                World(turn, Direction.ZPlus, half: false);
        }

        void World(Rotation turn, Direction axis, bool half)
        {
            if (half)
            {
                Visit((turn.Then(Quarter(axis, true)).Then(Quarter(axis, true)), Direction.ZPlus));
                return;
            }

            Visit((turn.Then(Quarter(axis, true)), Direction.ZPlus));
            Visit((turn.Then(Quarter(axis, false)), Direction.ZPlus));
        }

        void FaceMoves(Rotation turn, Direction last, RotationAxes axes)
        {
            var forward = turn.Apply(Direction.ZPlus);
            var side = forward.AxisOf() != Axis.Y;
            if ((axes & RotationAxes.X) != 0)
            {
                if (side)
                {
                    var tilt = DirectionMath.FromUnit(Direction.YPlus.Unit().Cross(forward.Unit()));
                    Visit((turn.Then(Quarter(tilt, true)), forward));
                    Visit((turn.Then(Quarter(tilt, false)), forward));
                }
                else
                {
                    var back = DirectionMath.FromUnit(Direction.YPlus.Unit().Cross(last.Unit()));
                    Visit((turn.Then(Quarter(back, forward == Direction.YPlus)), last));
                }
            }

            if ((axes & RotationAxes.Y) != 0 && side)
            {
                Visit(Spun(turn.Then(Quarter(Direction.YPlus, true))));
                Visit(Spun(turn.Then(Quarter(Direction.YPlus, false))));
            }

            if ((axes & RotationAxes.Z) != 0)
            {
                if (!side)
                {
                    Visit((turn.Then(Quarter(Direction.YPlus, true)), last));
                    Visit((turn.Then(Quarter(Direction.YPlus, false)), last));
                }

                Visit((Quarter(Direction.ZPlus, true).Then(turn), side ? forward : last));
                Visit((Quarter(Direction.ZPlus, false).Then(turn), side ? forward : last));
            }
        }

        static (Rotation, Direction) Spun(Rotation turn) => (turn, turn.Apply(Direction.ZPlus));
    }

    /// <summary>The smart-rotate steps as world rotations, each and its inverse, bent by the piece's axes as <c>Structure.Rotate</c> bends them.</summary>
    private static List<Rotation> SmartMoves(TurnLimits limits)
    {
        var moves = new List<Rotation>();
        if (limits.Smart.Count == 0 || limits.Axes == RotationAxes.None)
            return moves;
        var bend = Rotation.Identity;
        var single = limits.Axes is RotationAxes.X or RotationAxes.Y or RotationAxes.Z;
        if (limits.Axes == RotationAxes.X)
            bend = Quarter(Direction.YPlus, true);
        else if (limits.Axes == RotationAxes.Y || (!single && limits.Faces == StartFaces.Floor))
            bend = Quarter(Direction.XPlus, false);
        var unbend = Table.RotationOf(Table.Invert(Table.TurnOf(bend)));
        foreach (var step in limits.Smart.ToList())
        {
            var bent = unbend.Then(Table.RotationOf(step)).Then(bend);
            moves.Add(bent);
            moves.Add(Table.RotationOf(Table.Invert(Table.TurnOf(bent))));
        }

        return moves;
    }

    /// <summary>A quarter turn about a world axis: +90 degrees when <paramref name="plus"/>, sending v to axis × v.</summary>
    private static Rotation Quarter(Direction axis, bool plus) => new(Turned(Direction.XPlus, axis, plus), Turned(Direction.YPlus, axis, plus));

    private static Direction Turned(Direction v, Direction axis, bool plus)
    {
        if (v.AxisOf() == axis.AxisOf())
            return v;
        var turned = DirectionMath.FromUnit(axis.Unit().Cross(v.Unit()));
        return plus ? turned : turned.Opposite();
    }
}
