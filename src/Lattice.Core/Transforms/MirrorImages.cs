using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Catalogue;
using Lattice.Core.Grid;
using Lattice.Core.Ports;

namespace Lattice.Core.Transforms;

/// <summary>
/// The turn that lays a piece on its own mirror image, per piece and mirror axis, from catalogue facts alone, so a
/// modded piece mirrors by its own ends. A piece has a
/// mirror image when one of the 24 turns lays its ends (ports and roles included) and its footprint (cells and face
/// slots) on their reflection through the piece's origin; the footprint matters for pieces without ends, such as a
/// wall, whose face decides. A piece a mod registered a mirror partner for (<see cref="IMirrorPartners"/>, asked on
/// every call, so a registration that ends stops at once) mirrors into the partner instead: laid in the turn that puts
/// the partner's ends and footprint on the piece's reflected ones, or in no turn when none does (the mod's pair is
/// taken as declared). Worked out once per piece, image and axis (24 turns, quadratic in ends and footprint), then a
/// lookup. A piece the game places on a face has no footprint and stands on a face point, where its spots depend on
/// where it stands and how it is turned (<see cref="Shapes"/>): its image turn is the one that lays the image's spots, at
/// the reflected position, on the original's spots reflected, and among those the one whose centre lies nearest the
/// original's centre reflected (a wall turned edgewise can take the same spot, as the game breaks ties toward plus);
/// worked out once per piece, image, axis, placed turn and position within its large cell (24 shapes), then a lookup.
/// Main thread.
/// </summary>
internal sealed class MirrorImages(ICatalogue catalogue, IGrid grid, IMirrorPartners partners)
{
    private readonly Mirror _mirror = new(grid);
    private readonly Shapes _shapes = new(catalogue, grid);
    private readonly Dictionary<(PrefabKey Piece, PrefabKey Image, Axis Axis), Option<Turn>> _turns = new();
    private readonly Dictionary<FaceKey, Option<Turn>> _faceTurns = new();
    private readonly Dictionary<Axis, Turn[]> _preferred = new();

    /// <summary>
    /// The piece a placement of <paramref name="piece"/> becomes in a mirror across <paramref name="normal"/>, and the
    /// turn that lays it there before the placement's own turn as seen in the mirror; none when it has no mirror image.
    /// </summary>
    public Option<MirrorImage> ImageOf(PieceFacts piece, Axis normal)
    {
        if (partners.PartnerOf(piece.Key).TryGet(out var partner) && catalogue.Piece(partner).TryGet(out var image))
            return Option.Some(new MirrorImage(partner, TurnOf(piece, image, normal).Reduce(Turn.None)));
        return TurnOf(piece, normal).TryGet(out var own) ? Option.Some(new MirrorImage(piece.Key, own)) : Option<MirrorImage>.None;
    }

    /// <summary>
    /// The image of a placement of a face piece turned <paramref name="placed"/>, in a mirror across
    /// <paramref name="normal"/> that takes its position to <paramref name="at"/> and its spots to
    /// <paramref name="target"/>: the piece (the original, or its registered partner) and the turn that lays it there
    /// before the placed turn as seen in the mirror; none when the original has no turn that does. A partner no turn
    /// lays there is taken as declared, unturned. The turn that undoes itself in the mirror is preferred, so mirroring
    /// twice gives back the same turn.
    /// </summary>
    public Option<MirrorImage> FaceImageOf(PieceFacts piece, Turn placed, Cell at, Axis normal, IReadOnlyList<SpotKey> target)
    {
        if (partners.PartnerOf(piece.Key).TryGet(out var partner) && catalogue.Piece(partner).TryGet(out var image))
            return Option.Some(new MirrorImage(partner, FaceTurnOf(piece, image, placed, at, normal, target).Reduce(Turn.None)));
        return FaceTurnOf(piece, piece, placed, at, normal, target).TryGet(out var own)
            ? Option.Some(new MirrorImage(piece.Key, own))
            : Option<MirrorImage>.None;
    }

    /// <summary>
    /// The mirror turn of a piece the catalogue holds; none when it has no mirror image. Among several, the turn
    /// <c>M</c> whose reflection undoes it (<c>M</c>, then <c>M</c> seen in the mirror, is no turn) is preferred: then
    /// mirroring a placement twice gives back the same turn, not only the same shape.
    /// </summary>
    public Option<Turn> TurnOf(PieceFacts piece, Axis normal) => TurnOf(piece, piece, normal);

    /// <summary>The mirror turn of a piece by key; refused <see cref="TransformProblem.UnknownPiece"/> when the catalogue lacks it.</summary>
    public Result<Option<Turn>, TransformProblem> TurnOf(PrefabKey piece, Axis normal) =>
        catalogue.Piece(piece).TryGet(out var facts)
            ? Result<Option<Turn>, TransformProblem>.Ok(TurnOf(facts, normal))
            : Result<Option<Turn>, TransformProblem>.Error(TransformProblem.UnknownPiece);

    /// <summary>The turn a placement shows in the mirror: reflect, turn, reflect back.</summary>
    public Turn Seen(Turn turn, Axis normal) => _mirror.Reflect(turn, normal);

    private Option<Turn> TurnOf(PieceFacts piece, PieceFacts image, Axis normal)
    {
        if (!_turns.TryGetValue((piece.Key, image.Key, normal), out var turn))
            _turns[(piece.Key, image.Key, normal)] = turn = Find(piece, image, normal);
        return turn;
    }

    /// <summary>
    /// The turn that, before <paramref name="placed"/> seen in the mirror, lays <paramref name="image"/> at
    /// <paramref name="at"/> on <paramref name="target"/>, its centre nearest the original's reflected; the earlier in
    /// <see cref="Preferred"/> among equals. A move by whole large cells moves a face piece's spots with it, so the
    /// answer holds for every position the same within its large cell.
    /// </summary>
    private Option<Turn> FaceTurnOf(PieceFacts piece, PieceFacts image, Turn placed, Cell at, Axis normal, IReadOnlyList<SpotKey> target)
    {
        var key = new FaceKey(piece.Key, image.Key, normal, placed, Within(at.X), Within(at.Y), Within(at.Z));
        if (_faceTurns.TryGetValue(key, out var found))
            return found;
        found = Option<Turn>.None;
        if (piece.Face.TryGet(out var face) && image.Face.TryGet(out var imageFace))
        {
            var reflected = Reflect(Shapes.Turned(placed.Axes, face.Centre), normal);
            var seen = Seen(placed, normal);
            var nearest = float.MaxValue;
            foreach (var turn in Preferred(normal))
            {
                var laid = grid.Compose(turn, seen);
                var distance = DistanceSquared(Shapes.Turned(laid.Axes, imageFace.Centre), reflected);
                if (distance < nearest && _shapes.Of(new Placement(image.Key, at, laid)).TryGet(out var shape) && SpotMaps.Same(shape.Spots, target))
                    (found, nearest) = (Option.Some(turn), distance);
            }
        }

        _faceTurns[key] = found;
        return found;
    }

    private static (float X, float Y, float Z) Reflect((float X, float Y, float Z) point, Axis normal) => normal switch
    {
        Axis.X => (-point.X, point.Y, point.Z),
        Axis.Y => (point.X, -point.Y, point.Z),
        Axis.Z => (point.X, point.Y, -point.Z),
        _ => throw DirectionMath.NotAnAxis(normal),
    };

    private static float DistanceSquared((float X, float Y, float Z) a, (float X, float Y, float Z) b)
    {
        var (dx, dy, dz) = (a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        return dx * dx + dy * dy + dz * dz;
    }

    /// <summary>The 24 turns, those that undo themselves in a mirror across <paramref name="normal"/> first, each group by index.</summary>
    private Turn[] Preferred(Axis normal)
    {
        if (_preferred.TryGetValue(normal, out var turns))
            return turns;
        var undoing = new List<Turn>(Turn.Count);
        var rest = new List<Turn>(Turn.Count);
        for (var index = 0; index < Turn.Count; index++)
            if (Turn.Create(index).TryGet(out var turn))
                (grid.Compose(turn, _mirror.Reflect(turn, normal)) == Turn.None ? undoing : rest).Add(turn);
        undoing.AddRange(rest);
        return _preferred[normal] = undoing.ToArray();
    }

    /// <summary>A key's place within its large cell, 0 to 19.</summary>
    private static int Within(int key) => ((key % 20) + 20) % 20;

    /// <summary>The turn that lays <paramref name="image"/> on <paramref name="piece"/> reflected; the one that undoes itself in the mirror first.</summary>
    private Option<Turn> Find(PieceFacts piece, PieceFacts image, Axis normal)
    {
        var ends = _mirror.Reflect(piece.Ends, normal);
        var footprint = Footprint(piece);
        var imageFootprint = Footprint(image);
        var reflected = new FootprintCell[footprint.Count];
        for (var i = 0; i < reflected.Length; i++)
            reflected[i] = new FootprintCell(Reflect(footprint[i].At, normal), Reflect(footprint[i].Slot, normal));

        var first = Option<Turn>.None;
        for (var index = 0; index < Turn.Count; index++)
        {
            if (!Turn.Create(index).TryGet(out var turn) || image.Ends.Count != ends.Count || imageFootprint.Count != reflected.Length ||
                !SameEnds(image.Ends, turn, ends) || !SameCells(imageFootprint, turn, reflected))
                continue;
            if (grid.Compose(turn, _mirror.Reflect(turn, normal)) == Turn.None)
                return Option.Some(turn);
            if (!first.IsSome)
                first = Option.Some(turn);
        }

        return first;
    }

    private static IReadOnlyList<FootprintCell> Footprint(PieceFacts piece) =>
        piece.Footprint.Count > 0
            ? piece.Footprint
            : [new FootprintCell(default, piece.Grid == GridSize.Large ? Slot.Centre : Slot.Other)];

    private bool SameEnds(IReadOnlyList<ConnectionEnd> ends, Turn turn, ValueList<ConnectionEnd> target)
    {
        var matched = new bool[target.Count];
        for (var i = 0; i < ends.Count; i++)
        {
            var turned = ends[i] with { At = grid.Apply(turn, ends[i].At), Facing = grid.Apply(turn, ends[i].Facing) };
            if (!Match(turned, target, matched))
                return false;
        }

        return true;
    }

    private bool SameCells(IReadOnlyList<FootprintCell> cells, Turn turn, FootprintCell[] target)
    {
        var matched = new bool[target.Length];
        for (var i = 0; i < cells.Count; i++)
        {
            var turned = new FootprintCell(grid.Apply(turn, cells[i].At), TurnSlot(cells[i].Slot, turn));
            var found = false;
            for (var j = 0; j < target.Length && !found; j++)
                if (!matched[j] && target[j] == turned)
                    found = matched[j] = true;
            if (!found)
                return false;
        }

        return true;
    }

    private static bool Match(ConnectionEnd end, ValueList<ConnectionEnd> target, bool[] matched)
    {
        for (var j = 0; j < target.Count; j++)
            if (!matched[j] && target[j] == end)
                return matched[j] = true;
        return false;
    }

    private Slot TurnSlot(Slot slot, Turn turn) =>
        Faces.DirectionOf(slot) is { } face ? Faces.SlotOf(grid.Apply(turn, face)) : slot;

    private Slot Reflect(Slot slot, Axis normal) =>
        Faces.DirectionOf(slot) is { } face ? Faces.SlotOf(grid.Reflect(face, normal)) : slot;

    private static CellOffset Reflect(CellOffset offset, Axis normal) => normal switch
    {
        Axis.X => offset with { X = -offset.X },
        Axis.Y => offset with { Y = -offset.Y },
        Axis.Z => offset with { Z = -offset.Z },
        _ => throw DirectionMath.NotAnAxis(normal),
    };
}

/// <summary>What a face piece's image turn depends on: the pieces, the mirror axis, the placed turn and the position within its large cell.</summary>
internal readonly record struct FaceKey(PrefabKey Piece, PrefabKey Image, Axis Axis, Turn Placed, int X, int Y, int Z);

/// <summary>A mirror image: the piece it is (the original, or its registered partner) and the turn that lays it on the reflection.</summary>
internal readonly record struct MirrorImage(PrefabKey Piece, Turn Turn);
