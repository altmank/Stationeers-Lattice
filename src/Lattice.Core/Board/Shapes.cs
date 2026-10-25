using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Grid;
using Lattice.Core.Ports;

namespace Lattice.Core.Board;

/// <summary>
/// One slot of one cell: the unit of "one ghost per spot". A face is keyed by the cell the piece lies in, as the game
/// keys it (<c>Cell.AddStructural</c>), so the two sides of one boundary are two spots; a reader that needs the
/// boundary looks at both (<see cref="Boundary"/>).
/// </summary>
internal readonly record struct SpotKey(Cell Cell, Slot Slot)
{
    public bool IsFace => Faces.DirectionOf(Slot) is not null;
}

/// <summary>The face between two neighbouring large cells, as each of them keys it.</summary>
/// <param name="Near">The face of the first cell toward the second.</param>
/// <param name="Far">The face of the second cell back toward the first.</param>
internal readonly record struct Boundary(SpotKey Near, SpotKey Far)
{
    public bool Holds(SpotKey spot) => spot == Near || spot == Far;
}

/// <summary>Large-cell faces: the slot of each direction, and the cells on either side of a face spot.</summary>
internal static class Faces
{
    public static Direction? DirectionOf(Slot slot) => slot switch
    {
        Slot.FaceXPlus => Direction.XPlus,
        Slot.FaceXMinus => Direction.XMinus,
        Slot.FaceYPlus => Direction.YPlus,
        Slot.FaceYMinus => Direction.YMinus,
        Slot.FaceZPlus => Direction.ZPlus,
        Slot.FaceZMinus => Direction.ZMinus,
        _ => null,
    };

    public static Slot SlotOf(Direction face) => face switch
    {
        Direction.XPlus => Slot.FaceXPlus,
        Direction.XMinus => Slot.FaceXMinus,
        Direction.YPlus => Slot.FaceYPlus,
        Direction.YMinus => Slot.FaceYMinus,
        Direction.ZPlus => Slot.FaceZPlus,
        Direction.ZMinus => Slot.FaceZMinus,
        _ => throw DirectionMath.NotADirection(face),
    };

    /// <summary>The two large cells a face spot lies between: the one on its negative side, then the one on its positive side.</summary>
    public static (Cell Negative, Cell Positive) Sides(SpotKey face, IGrid grid)
    {
        var toward = DirectionOf(face.Slot) ?? throw new System.ArgumentException("not a face: " + face.Slot, nameof(face));
        var beyond = grid.Neighbour(face.Cell, toward, GridSize.Large);
        return IsPositive(toward) ? (face.Cell, beyond) : (beyond, face.Cell);
    }

    /// <summary>The face between a large cell and its neighbour <paramref name="toward"/>, as each keys it.</summary>
    public static Boundary Between(Cell large, Direction toward, IGrid grid) =>
        new(new SpotKey(large, SlotOf(toward)), new SpotKey(grid.Neighbour(large, toward, GridSize.Large), SlotOf(toward.Opposite())));

    public static bool IsPositive(Direction direction) =>
        direction is Direction.XPlus or Direction.YPlus or Direction.ZPlus;
}

/// <summary>A point in millimetres in world cell coordinates (a cell key in decimetres times 100).</summary>
internal readonly record struct MillimetrePoint(long X, long Y, long Z)
{
    public long DistanceSquared(MillimetrePoint other)
    {
        var (dx, dy, dz) = (X - other.X, Y - other.Y, Z - other.Z);
        return dx * dx + dy * dy + dz * dz;
    }
}

/// <summary>A connection end in the world: the cell it leaves from, its facing, and its end point when the catalogue knows its reach.</summary>
internal readonly record struct WorldEnd(Cell Cell, Direction Facing, Option<MillimetrePoint> Point);

/// <summary>A piece placed in a cell and a turn, with the spots it takes and its ends, all from the catalogue.</summary>
internal sealed class PlacedShape(PieceFacts piece, Placement placement, SpotKey[] spots, WorldEnd[] ends)
{
    public PieceFacts Piece { get; } = piece;

    public Placement Placement { get; } = placement;

    public IReadOnlyList<SpotKey> Spots { get; } = spots;

    public IReadOnlyList<WorldEnd> Ends { get; } = ends;

    public bool IsSmall => Piece.Grid == GridSize.Small;

    public bool SharesACellWith(PlacedShape other)
    {
        for (var i = 0; i < Spots.Count; i++)
            for (var j = 0; j < other.Spots.Count; j++)
                if (Spots[i].Cell == other.Spots[j].Cell)
                    return true;
        return false;
    }
}

/// <summary>
/// Turns catalogue facts into spots: a placement takes every (cell, slot) of its piece's footprint, turned with the
/// placement; a piece the game places on a face takes the slots its points make (<see cref="FaceFacts"/>). Every
/// piece, base game or modded, is described by its catalogue facts alone.
/// </summary>
internal sealed class Shapes(ICatalogue catalogue, IGrid grid)
{
    /// <summary>A ghost within this distance of another's end collides with it: 0.1 m (<c>IsPipeEndCollision</c>).</summary>
    private const long EndCollisionMillimetres = 100;

    /// <summary>The grid the shapes are worked out on.</summary>
    public IGrid Grid => grid;

    /// <summary>The placement's shape; refused for a piece the catalogue does not hold. Linear in the piece's footprint and ends.</summary>
    public Result<PlacedShape, Reason> Of(Placement placement)
    {
        if (!catalogue.Piece(placement.Piece).TryGet(out var piece))
            return Result<PlacedShape, Reason>.Error(Reason.Of(
                ReasonCodes.UnknownPiece, LatticeIdentity.Board, ValueMap.Of("piece", placement.Piece.Hash.ToString())));

        var spots = piece.Face.TryGet(out var face) ? FaceSpots(placement, face) : FootprintSpots(placement, piece);

        var ends = new WorldEnd[piece.Ends.Count];
        for (var i = 0; i < piece.Ends.Count; i++)
            ends[i] = End(placement, piece.Ends[i]);
        return Result<PlacedShape, Reason>.Ok(new PlacedShape(piece, placement, spots, ends));
    }

    /// <summary>
    /// Why two small-grid pieces sharing a cell cannot both stand, or none: classes that share a bit cannot share a cell;
    /// otherwise two ends within 0.1 m collide (<c>SmallGrid._IsCollision</c>). An end with no known reach collides with an end leaving the same cell the same way.
    /// </summary>
    public static Option<ReasonCode> Conflict(PlacedShape mine, PlacedShape theirs)
    {
        if (!mine.IsSmall || !theirs.IsSmall || !mine.SharesACellWith(theirs))
            return Option<ReasonCode>.None;
        if ((mine.Piece.Collision & theirs.Piece.Collision) != SmallCollision.None)
            return Option.Some(ReasonCodes.CannotShare);
        for (var i = 0; i < mine.Ends.Count; i++)
            for (var j = 0; j < theirs.Ends.Count; j++)
                if (Collide(mine.Ends[i], theirs.Ends[j]))
                    return Option.Some(ReasonCodes.EndsCollide);
        return Option<ReasonCode>.None;
    }

    private static bool Collide(WorldEnd a, WorldEnd b) =>
        a.Point.TryGet(out var pa) && b.Point.TryGet(out var pb)
            ? pa.DistanceSquared(pb) < EndCollisionMillimetres * EndCollisionMillimetres
            : a.Cell == b.Cell && a.Facing == b.Facing;

    private SpotKey[] FootprintSpots(Placement placement, PieceFacts piece)
    {
        var footprint = piece.Footprint.Count > 0
            ? piece.Footprint
            : ValueList.Of(new FootprintCell(default, piece.Grid == GridSize.Large ? Slot.Centre : Slot.Other));
        var spots = new SpotKey[footprint.Count];
        for (var i = 0; i < footprint.Count; i++)
            spots[i] = Spot(placement, footprint[i]);
        return spots;
    }

    private SpotKey Spot(Placement placement, FootprintCell cell)
    {
        var at = placement.At.Plus(grid.Apply(placement.Turn, cell.At));
        return Faces.DirectionOf(cell.Slot) is { } face
            ? new SpotKey(at, Faces.SlotOf(grid.Apply(placement.Turn, face)))
            : new SpotKey(at, cell.Slot);
    }

    /// <summary>
    /// The game's registration of a face piece: each point turned and added to the position goes to the large cell
    /// whose centre is nearest it on the side of the piece's centre (<c>Grid3.GridCenter</c>, in single precision as
    /// the game works it), at the slot the point makes there; a point that makes no slot takes nothing
    /// (<c>StructuralArray.Get</c>). No points is the position alone (<c>Structure.OnAssignedReference</c>).
    /// </summary>
    private SpotKey[] FaceSpots(Placement placement, FaceFacts face)
    {
        var at = placement.At;
        var centre = Turned(placement.Turn.Axes, face.Centre);
        var originX = at.X * 0.1f + centre.X;
        var originY = at.Y * 0.1f + centre.Y;
        var originZ = at.Z * 0.1f + centre.Z;
        var count = face.Points.Count == 0 ? 1 : face.Points.Count;
        var spots = new List<SpotKey>(count);
        for (var i = 0; i < count; i++)
        {
            var point = face.Points.Count == 0 ? at : at.Plus(grid.Apply(placement.Turn, face.Points[i]));
            var cell = new Cell(CentreKey(point.X, originX), CentreKey(point.Y, originY), CentreKey(point.Z, originZ));
            if (SlotAt(point.X - cell.X, point.Y - cell.Y, point.Z - cell.Z) is { } slot)
                spots.Add(new SpotKey(cell, slot));
        }

        return spots.ToArray();
    }

    /// <summary>Half a large cell in decimetres: a face's offset from the cell's centre.</summary>
    private const int LargeHalf = 10;

    /// <summary>The key of the large-cell centre (odd metres) nearest a point's key on the origin's side (<c>Grid3.GridCenter</c>).</summary>
    private static int CentreKey(int key, float origin)
    {
        var metres = key / 10f;
        var half = (metres - 1f) / 2f;
        var centre = ((metres <= origin ? MathF.Ceiling(half) : MathF.Floor(half)) * 2f) + 1f;
        return (int)MathF.Round(centre * 10f);
    }

    /// <summary>The slot a point makes in a large cell, from its offset in decimetres (<c>StructuralArray.Get</c>); none off the centre and faces.</summary>
    private static Slot? SlotAt(int dx, int dy, int dz) => (dx, dy, dz) switch
    {
        (0, 0, 0) => Slot.Centre,
        (LargeHalf, 0, 0) => Slot.FaceXPlus,
        (-LargeHalf, 0, 0) => Slot.FaceXMinus,
        (0, LargeHalf, 0) => Slot.FaceYPlus,
        (0, -LargeHalf, 0) => Slot.FaceYMinus,
        (0, 0, LargeHalf) => Slot.FaceZPlus,
        (0, 0, -LargeHalf) => Slot.FaceZMinus,
        _ => null,
    };

    /// <summary>A local vector in metres turned as a placement's turn lays it, in single precision as the game works it.</summary>
    internal static (float X, float Y, float Z) Turned(TurnAxes axes, Lattice.Core.Catalogue.Vector local)
    {
        var (x1, y1, z1) = Image(axes.X, (float)local.X);
        var (x2, y2, z2) = Image(axes.Y, (float)local.Y);
        var (x3, y3, z3) = Image(axes.Z, (float)local.Z);
        return (x1 + x2 + x3, y1 + y2 + y3, z1 + z2 + z3);
    }

    private static (float X, float Y, float Z) Image(SignedAxis axis, float length)
    {
        var signed = axis.Sign == AxisSign.Minus ? -length : length;
        return axis.Axis switch
        {
            Axis.X => (signed, 0f, 0f),
            Axis.Y => (0f, signed, 0f),
            _ => (0f, 0f, signed),
        };
    }

    private WorldEnd End(Placement placement, ConnectionEnd end)
    {
        var cell = placement.At.Plus(grid.Apply(placement.Turn, end.At));
        var facing = grid.Apply(placement.Turn, end.Facing);
        var point = end.Reach.Map(reach =>
        {
            var unit = facing.Unit();
            return new MillimetrePoint(
                cell.X * 100L + unit.X * (long)reach.Value,
                cell.Y * 100L + unit.Y * (long)reach.Value,
                cell.Z * 100L + unit.Z * (long)reach.Value);
        });
        return new WorldEnd(cell, facing, point);
    }
}
