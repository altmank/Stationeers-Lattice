using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Ports;

namespace Lattice.Core.Swaps;

/// <summary>Who gives the swap planner's reasons.</summary>
internal static class SwapCodes
{
    /// <summary>The rule id of the swap planner's reasons and of its face check.</summary>
    public static RuleId Swaps => RuleId.Known("lattice.swaps");
}

/// <summary>
/// What a built structure can become in place (<see cref="SwapShapes"/>: same spots, same ends, same network kinds),
/// and the piece a <see cref="SwapTo"/> picks for it. Catalogue facts only, so a modded piece swaps like its kind; the
/// face's pressure difference is read from the world view as it is now. A face load over its rating is damage each
/// tick (<c>Atmosphere.ReactWithStructures</c>).
/// Main thread.
/// </summary>
internal sealed class SwapPlanner(ICatalogue catalogue, Shapes shapes, IWorldView world) : ISwaps, IPieceSwaps
{
    private readonly SwapShapes _fits = new(catalogue, shapes);

    /// <summary>
    /// Every piece a kit places that the structure can become in place, in kit order then option order, each with its
    /// face load; refused <see cref="ReasonCodes.UnknownPiece"/> for a structure whose piece the catalogue does not hold.
    /// The first call reads the whole catalogue once; after that linear in the pieces of the structure's size.
    /// </summary>
    public Result<ValueList<SwapOption>, Reason> Options(StructureInfo structure)
    {
        if (catalogue.Piece(structure.Ref.Piece).IsError)
            return Result<ValueList<SwapOption>, Reason>.Error(UnknownPiece(structure.Ref.Piece));

        var fits = _fits.FitsFor(structure.Ref.Piece, structure.Turn);
        var options = new SwapOption[fits.Count];
        for (var i = 0; i < fits.Count; i++)
        {
            var placement = new Placement(fits[i].Piece, structure.Ref.At, fits[i].Turn);
            options[i] = new SwapOption(placement, FaceLoadOf(placement));
        }

        return Result<ValueList<SwapOption>, Reason>.Ok(Lists.Own(options));
    }

    /// <summary>
    /// <see cref="Options"/> for the structure the world view holds under the reference's id; refused
    /// <see cref="ReasonCodes.NotFound"/> (fact <c>structure</c>) when it is gone. One world view read, then as
    /// <see cref="Options"/>.
    /// </summary>
    public Result<ValueList<SwapOption>, Reason> OptionsFor(StructureRef structure) =>
        world.Structure(structure.Id).TryGet(out var info)
            ? Options(info)
            : Result<ValueList<SwapOption>, Reason>.Error(Reason.Of(
                ReasonCodes.NotFound, SwapCodes.Swaps, ValueMap.Of("structure", structure.Id.Value.ToString())));

    /// <summary>
    /// What the structure becomes under <paramref name="to"/>. <see cref="SwapTo.Exactly"/>: that piece, if it fits.
    /// <see cref="SwapTo.SameShapeFrom"/>: the structure's own piece when the kit places it, else the kit's first piece
    /// that fits. Refused <see cref="ReasonCodes.NoPiece"/> (facts <c>piece</c>, and <c>with</c> or <c>kit</c>) when
    /// nothing fits, <see cref="ReasonCodes.UnknownPiece"/> for a piece the catalogue does not hold, the catalogue's
    /// reason for an unknown kit, <see cref="ReasonCodes.UnknownKind"/> for a kind of swap this version does not know.
    /// </summary>
    public Result<Placement, Reason> Target(StructureInfo structure, SwapTo to) =>
        Target(new Placement(structure.Ref.Piece, structure.Ref.At, structure.Turn), to);

    /// <summary>
    /// What a planned piece becomes under <paramref name="to"/>, as <see cref="Target(StructureInfo, SwapTo)"/> for a
    /// structure of that piece in that turn. For a swap ghost, <paramref name="old"/> is the structure it swaps: a
    /// result that is that structure as it stands (same piece in the same turn, or the same piece once it is gone) is
    /// refused <see cref="ReasonCodes.InvalidValue"/> (<c>problem=own-structure</c>). One world read for a swap ghost,
    /// else catalogue facts and cached fits.
    /// </summary>
    public Result<Placement, Reason> Target(Placement placement, Option<StructureRef> old, SwapTo to)
    {
        var target = Target(placement, to);
        if (old.TryGet(out var structure) && target.TryGet(out var found) && IsOwn(structure, found))
            return Result<Placement, Reason>.Error(Reason.Of(
                ReasonCodes.InvalidValue, SwapCodes.Swaps, ValueMap.Of("piece", found.Piece.Hash.ToString())
                    .With("structure", structure.Id.Value.ToString()).With("problem", "own-structure")));
        return target;
    }

    private Result<Placement, Reason> Target(Placement placement, SwapTo to)
    {
        if (catalogue.Piece(placement.Piece).IsError)
            return Result<Placement, Reason>.Error(UnknownPiece(placement.Piece));

        return to switch
        {
            SwapTo.Exactly exactly => Exactly(placement, exactly.Piece),
            SwapTo.SameShapeFrom same => SameShape(placement, same.Kit),
            _ => Result<Placement, Reason>.Error(UnknownKind(to)),
        };
    }

    public Option<Reason> Unknown(SwapTo to) =>
        to is SwapTo.Exactly or SwapTo.SameShapeFrom ? Option<Reason>.None : Option.Some(UnknownKind(to));

    private static Reason UnknownKind(SwapTo to) =>
        Reason.Of(ReasonCodes.UnknownKind, SwapCodes.Swaps, ValueMap.Of("kind", to.GetType().Name));

    private bool IsOwn(StructureRef structure, Placement placement) =>
        placement.Piece == structure.Piece && placement.At == structure.At &&
        (!world.Structure(structure.Id).TryGet(out var standing) || standing.Turn == placement.Turn);

    /// <summary>The face load of a placement whose piece closes a face and has a face rating; none otherwise. Linear in its spots.</summary>
    public Option<FaceLoad> FaceLoadOf(Placement placement)
    {
        if (!shapes.Of(placement).TryGet(out var shape) || !shape.Piece.Ratings.Face.TryGet(out var rating))
            return Option<FaceLoad>.None;
        foreach (var spot in shape.Spots)
            if (AxisOf(spot.Slot) is { } face)
            {
                var (negative, _) = Faces.Sides(spot, shapes.Grid);
                return Option.Some(new FaceLoad(
                    negative, face, rating, new Kilopascals(Math.Abs(world.PressureDifference(negative, Positive(face)).Value))));
            }

        return Option<FaceLoad>.None;
    }

    private Result<Placement, Reason> Exactly(Placement old, PrefabKey piece)
    {
        if (piece == old.Piece)
            return Result<Placement, Reason>.Ok(old);
        if (catalogue.Piece(piece).IsError)
            return Result<Placement, Reason>.Error(UnknownPiece(piece));
        return _fits.FitOf(old.Piece, old.Turn, piece).TryGet(out var turn)
            ? Result<Placement, Reason>.Ok(old with { Piece = piece, Turn = turn })
            : Result<Placement, Reason>.Error(NoPiece(old.Piece, "with", piece.Hash.ToString()));
    }

    private Result<Placement, Reason> SameShape(Placement old, KitKey kit)
    {
        var facts = catalogue.Kit(kit);
        return facts.TryGet(out var found) ? SameShapeIn(old, found) : Result<Placement, Reason>.Error(facts.ErrorOf());
    }

    private Result<Placement, Reason> SameShapeIn(Placement old, KitFacts facts)
    {
        foreach (var piece in facts.Pieces)
            if (piece == old.Piece)
                return Result<Placement, Reason>.Ok(old);
        foreach (var piece in facts.Pieces)
            if (_fits.FitOf(old.Piece, old.Turn, piece).TryGet(out var turn))
                return Result<Placement, Reason>.Ok(old with { Piece = piece, Turn = turn });
        return Result<Placement, Reason>.Error(NoPiece(old.Piece, "kit", facts.Key.Hash.ToString()));
    }

    private static Reason NoPiece(PrefabKey old, string key, string value) => Reason.Of(
        ReasonCodes.NoPiece, SwapCodes.Swaps, ValueMap.Of("piece", old.Hash.ToString()).With(key, value));

    private static Reason UnknownPiece(PrefabKey piece) => Reason.Of(
        ReasonCodes.UnknownPiece, SwapCodes.Swaps, ValueMap.Of("piece", piece.Hash.ToString()));

    /// <summary>The axis a face slot lies across, from either side; none for a slot that is not a face.</summary>
    private static Axis? AxisOf(Slot slot) => slot switch
    {
        Slot.FaceXPlus or Slot.FaceXMinus => Axis.X,
        Slot.FaceYPlus or Slot.FaceYMinus => Axis.Y,
        Slot.FaceZPlus or Slot.FaceZMinus => Axis.Z,
        _ => null,
    };

    /// <summary>The positive direction along an axis.</summary>
    public static Direction Positive(Axis axis) => axis switch
    {
        Axis.X => Direction.XPlus,
        Axis.Y => Direction.YPlus,
        _ => Direction.ZPlus,
    };
}
